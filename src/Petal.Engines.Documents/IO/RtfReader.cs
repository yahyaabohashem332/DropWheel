using System.Globalization;
using System.Text;
using Petal.Engines.Documents.Model;

namespace Petal.Engines.Documents.IO;

/// <summary>
/// A tolerant RTF reader: character formatting, style-sheet headings and outline levels,
/// Word list tables (and old \pn lists), tables, hyperlink fields and PNG/JPEG pictures.
/// Everything it does not understand is skipped rather than shown as text.
/// </summary>
internal sealed class RtfReader
{
    private enum Dest
    {
        Normal,
        Skip,
        FontTable,
        StyleSheet,
        Info,
        InfoField,
        Picture,
        FieldInstruction,
        ListTable,
        ListOverrideTable,
    }

    private sealed class State
    {
        public Dest Dest = Dest.Normal;
        public TextStyle Style;
        public int Font = -1;
        public int Uc = 1;
        public string? Link;
        public string? InfoKey;
        public bool InFieldResult;

        public State Clone() => (State)MemberwiseClone();
    }

    private sealed class ParagraphState
    {
        public int StyleIndex;
        public int? OutlineLevel;
        public int ListOverride;
        public int ListLevel;
        public bool InTable;
        public Alignment Alignment;
        public bool PageBreakBefore;
        public bool LegacyBullet, LegacyNumber;
        public int LegacyLevel;
    }

    private readonly string _rtf;
    private int _pos;
    private State _state = new();
    private readonly Stack<State> _stack = new();
    private ParagraphState _para = new();

    private readonly DocModel _doc = new();
    private readonly Dictionary<int, (string Name, int CodePage)> _fonts = [];
    private readonly Dictionary<int, string> _styleNames = [];
    private readonly Dictionary<int, int> _styleOutline = [];
    private int _defaultCodePage = 1252;

    // List tables: list id -> per level "ordered", override index -> list id.
    private readonly Dictionary<int, List<bool>> _lists = [];
    private readonly Dictionary<int, int> _overrides = [];
    private int _currentListId;
    private List<bool>? _currentListLevels;
    private int _currentOverrideList;

    // Text being collected.
    private List<Inline> _inlines = [];
    private readonly StringBuilder _text = new();
    private readonly List<byte> _bytes = [];
    private TextStyle _textStyle;
    private string? _textLink;
    private int _textCodePage = 1252;

    // Destination buffers.
    private readonly StringBuilder _destText = new();
    private readonly StringBuilder _pictHex = new();
    private string _pictType = "";
    private int _pictWidthGoal, _pictHeightGoal, _pictScaleX = 100, _pictScaleY = 100;
    private int _styleDefIndex;
    private bool _styleDefIsParagraph;
    private int? _styleDefOutline;
    private int _fontDefIndex;
    private int _fontDefCharset;
    private string? _fieldLink;

    // Body assembly.
    private readonly List<Block> _body = [];
    private readonly List<ListEntry> _pendingList = [];
    private Model.Table? _table;
    private Model.TableRow? _row;
    private Model.TableCell? _cell;
    private readonly List<bool> _rowMerges = [];
    private bool _rowIsHeader;
    private readonly List<(int Column, Model.TableCell Cell)> _openVerticalMerges = [];
    private int _cellIndex;

    private RtfReader(string rtf) => _rtf = rtf;

    public static DocModel Read(string path)
    {
        // RTF is 7-bit; Latin-1 maps every byte to one char so \'hh and \bin data survive.
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return Parse(text);
    }

    public static DocModel Parse(string rtf)
    {
        Text.TextFile.EnsureCodePages();
        var reader = new RtfReader(rtf);
        reader.Run();
        return reader._doc;
    }

    private void Run()
    {
        while (_pos < _rtf.Length)
        {
            var c = _rtf[_pos++];
            switch (c)
            {
                case '{':
                    FlushText();
                    _stack.Push(_state);
                    _state = _state.Clone();
                    break;
                case '}':
                    EndGroup();
                    break;
                case '\\':
                    ReadControl();
                    break;
                case '\r' or '\n':
                    break;
                default:
                    AddChar(c);
                    break;
            }
        }
        FlushText();
        if (_inlines.Count > 0)
            EndParagraph();
        CloseTable();
        FlushList();
        _doc.Blocks.AddRange(_body);
    }

    private void EndGroup()
    {
        FlushText();
        var ending = _state;
        switch (ending.Dest)
        {
            case Dest.Picture when _stack.Count > 0 && _stack.Peek().Dest != Dest.Picture:
                FinishPicture();
                break;
            case Dest.FieldInstruction when _stack.Count > 0 && _stack.Peek().Dest != Dest.FieldInstruction:
                _fieldLink = ParseHyperlink(_destText.ToString());
                _destText.Clear();
                break;
            case Dest.InfoField when ending.InfoKey is { } key:
                var value = _destText.ToString().Trim();
                _destText.Clear();
                if (value.Length > 0)
                {
                    switch (key)
                    {
                        case "title": _doc.Metadata.Title = value; break;
                        case "author": _doc.Metadata.Author = value; break;
                        case "subject": _doc.Metadata.Subject = value; break;
                        case "keywords": _doc.Metadata.Keywords = value; break;
                    }
                }
                break;
            case Dest.StyleSheet when _stack.Count > 0 && _stack.Peek().Dest == Dest.StyleSheet:
                var name = _destText.ToString().Trim().TrimEnd(';').Trim();
                _destText.Clear();
                if (_styleDefIsParagraph)
                {
                    _styleNames[_styleDefIndex] = name;
                    if (_styleDefOutline is { } ol)
                        _styleOutline[_styleDefIndex] = ol;
                }
                _styleDefIndex = 0;
                _styleDefIsParagraph = true;
                _styleDefOutline = null;
                break;
            case Dest.FontTable when _stack.Count > 0 && _stack.Peek().Dest == Dest.FontTable:
                var fontName = _destText.ToString().Trim().TrimEnd(';').Trim();
                _destText.Clear();
                _fonts[_fontDefIndex] = (fontName, CodePageForCharset(_fontDefCharset));
                _fontDefCharset = 0;
                break;
        }
        if (ending.InFieldResult && (_stack.Count == 0 || !_stack.Peek().InFieldResult))
            _fieldLink = null;
        _state = _stack.Count > 0 ? _stack.Pop() : new State();
    }

    private void ReadControl()
    {
        if (_pos >= _rtf.Length)
            return;
        var c = _rtf[_pos];
        if (!char.IsAsciiLetter(c))
        {
            _pos++;
            switch (c)
            {
                case '\\' or '{' or '}': AddChar(c); break;
                case '~': AddChar(' '); break;
                case '_': AddChar('‑'); break;
                case '-': break;
                case '*':
                    // Ignorable destination: skip unless we know it.
                    if (!PeekKnownDestination())
                        _state.Dest = Dest.Skip;
                    break;
                case '\'':
                    if (_pos + 2 <= _rtf.Length && byte.TryParse(_rtf.AsSpan(_pos, 2), NumberStyles.HexNumber, null, out var b))
                    {
                        _pos += 2;
                        AddByte(b);
                    }
                    break;
                case '\r' or '\n':
                    AddParagraphMark();
                    break;
            }
            return;
        }

        var start = _pos;
        while (_pos < _rtf.Length && char.IsAsciiLetter(_rtf[_pos]))
            _pos++;
        var word = _rtf[start.._pos];
        int? param = null;
        if (_pos < _rtf.Length && (_rtf[_pos] == '-' || char.IsAsciiDigit(_rtf[_pos])))
        {
            var ps = _pos;
            _pos++;
            while (_pos < _rtf.Length && char.IsAsciiDigit(_rtf[_pos]))
                _pos++;
            if (int.TryParse(_rtf.AsSpan(ps, _pos - ps), out var p))
                param = p;
        }
        if (_pos < _rtf.Length && _rtf[_pos] == ' ')
            _pos++;
        Control(word, param);
    }

    private bool PeekKnownDestination()
    {
        var p = _pos;
        while (p < _rtf.Length && (_rtf[p] is ' ' or '\r' or '\n')) p++;
        if (p >= _rtf.Length || _rtf[p] != '\\')
            return false;
        p++;
        var s = p;
        while (p < _rtf.Length && char.IsAsciiLetter(_rtf[p])) p++;
        var word = _rtf[s..p];
        return word is "shppict" or "fldinst" or "listtable" or "listoverridetable" or "cs" or "pn";
    }

    private void Control(string word, int? param)
    {
        var dest = _state.Dest;
        if (dest == Dest.Skip)
        {
            // Binary data must be stepped over even in skipped groups.
            if (word == "bin" && param is > 0)
                _pos = Math.Min(_rtf.Length, _pos + param.Value);
            return;
        }

        // Destinations.
        switch (word)
        {
            case "fonttbl": _state.Dest = Dest.FontTable; return;
            case "stylesheet": _state.Dest = Dest.StyleSheet; _styleDefIsParagraph = true; return;
            case "info": _state.Dest = Dest.Info; return;
            case "title" or "author" or "subject" or "keywords" when dest == Dest.Info:
                _state.Dest = Dest.InfoField;
                _state.InfoKey = word;
                return;
            case "colortbl" or "header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf"
                or "footnote" or "annotation" or "object" or "xmlnstbl" or "rsidtbl" or "generator" or "themedata" or "colorschememapping"
                or "latentstyles" or "datastore" or "mmathPr" or "nonshppict" or "pntext" or "listtext" or "bkmkstart" or "bkmkend"
                or "comment" or "operator" or "creatim" or "revtim" or "printim" or "buptim" or "doccomm" or "company" or "manager"
                or "category" or "hlinkbase" or "userprops" or "template" or "txe" or "fldtype" or "atnid" or "atnauthor" or "pgdsctbl" or "wgrffmtfilter"
                or "pntxtb" or "pntxta" or "fchars" or "lchars" or "revtbl" or "protusertbl" or "ftnsep" or "ftnsepc" or "ftncn" or "aftnsep" or "aftnsepc":
                _state.Dest = Dest.Skip;
                return;
            case "pict":
                _state.Dest = Dest.Picture;
                _pictHex.Clear();
                _pictType = "";
                _pictWidthGoal = _pictHeightGoal = 0;
                _pictScaleX = _pictScaleY = 100;
                return;
            case "shppict" or "shp" or "shpinst" or "sp" or "sn" or "sv" or "shptxt":
                if (word is "sn" or "sv") _state.Dest = Dest.Skip;
                return;
            case "fldinst":
                _state.Dest = Dest.FieldInstruction;
                _destText.Clear();
                return;
            case "fldrslt":
                _state.InFieldResult = true;
                _state.Link = _fieldLink;
                return;
            case "listtable": _state.Dest = Dest.ListTable; return;
            case "listoverridetable": _state.Dest = Dest.ListOverrideTable; return;
        }

        switch (dest)
        {
            case Dest.FontTable:
                if (word == "f" && param is { } fi) _fontDefIndex = fi;
                else if (word == "fcharset" && param is { } cs) _fontDefCharset = cs;
                else if (word is "panose" or "falt") _state.Dest = Dest.Skip;
                return;
            case Dest.StyleSheet:
                if (word == "s" && param is { } si) { _styleDefIndex = si; _styleDefIsParagraph = true; }
                else if (word is "cs" or "ds" or "ts" or "tsrowd") _styleDefIsParagraph = false;
                else if (word == "outlinelevel" && param is { } sol) _styleDefOutline = sol;
                return;
            case Dest.Picture:
                switch (word)
                {
                    case "pngblip": _pictType = "png"; break;
                    case "jpegblip": _pictType = "jpeg"; break;
                    case "picwgoal": _pictWidthGoal = param ?? 0; break;
                    case "pichgoal": _pictHeightGoal = param ?? 0; break;
                    case "picscalex": _pictScaleX = param ?? 100; break;
                    case "picscaley": _pictScaleY = param ?? 100; break;
                    case "bin" when param is > 0:
                        var n = Math.Min(param.Value, _rtf.Length - _pos);
                        foreach (var ch in _rtf.AsSpan(_pos, n))
                            _pictHex.Append(((byte)ch).ToString("x2"));
                        _pos += n;
                        break;
                }
                return;
            case Dest.ListTable:
                switch (word)
                {
                    case "list": _currentListLevels = []; break;
                    case "listlevel": _currentListLevels?.Add(true); break;
                    case "levelnfc" when _currentListLevels is { Count: > 0 } levels:
                        levels[^1] = param is not (23 or 255);
                        break;
                    case "listid" when _currentListLevels is not null:
                        _currentListId = param ?? 0;
                        _lists[_currentListId] = _currentListLevels;
                        break;
                    case "leveltext" or "levelnumbers" or "listname":
                        _state.Dest = Dest.Skip;
                        break;
                }
                return;
            case Dest.ListOverrideTable:
                if (word == "listid") _currentOverrideList = param ?? 0;
                else if (word == "ls" && param is { } ls) _overrides[ls] = _currentOverrideList;
                return;
            case Dest.Info or Dest.InfoField or Dest.FieldInstruction:
                return;
        }

        // Body text: character formatting.
        switch (word)
        {
            case "b": SetStyle(TextStyle.Bold, param != 0); return;
            case "i": SetStyle(TextStyle.Italic, param != 0); return;
            case "ul" or "uld" or "uldb" or "ulw" or "uldash" or "ulth" or "ulwave": SetStyle(TextStyle.Underline, param != 0); return;
            case "ulnone": SetStyle(TextStyle.Underline, false); return;
            case "strike" or "striked": SetStyle(TextStyle.Strike, param != 0); return;
            case "super": SetStyle(TextStyle.Superscript, true); SetStyle(TextStyle.Subscript, false); return;
            case "sub": SetStyle(TextStyle.Subscript, true); SetStyle(TextStyle.Superscript, false); return;
            case "nosupersub": SetStyle(TextStyle.Superscript | TextStyle.Subscript, false); return;
            case "plain":
                FlushText();
                _state.Style = TextStyle.None;
                _state.Font = -1;
                return;
            case "f":
                FlushText();
                _state.Font = param ?? -1;
                var mono = _fonts.TryGetValue(_state.Font, out var font) && IsMonospace(font.Name);
                SetStyle(TextStyle.Code, mono);
                return;
            case "uc": _state.Uc = Math.Max(0, param ?? 1); return;
            case "u" when param is { } u:
                AddChar((char)(u < 0 ? u + 65536 : u));
                SkipFallback(_state.Uc);
                return;
            case "ansicpg": _defaultCodePage = param ?? 1252; return;
            case "pc": _defaultCodePage = 437; return;
            case "mac": _defaultCodePage = 10000; return;
        }

        // Special characters.
        switch (word)
        {
            case "tab": AddChar('\t'); return;
            case "line": FlushText(); _inlines.Add(new LineBreak()); return;
            case "emdash": AddChar('—'); return;
            case "endash": AddChar('–'); return;
            case "bullet": AddChar('•'); return;
            case "lquote": AddChar('‘'); return;
            case "rquote": AddChar('’'); return;
            case "ldblquote": AddChar('“'); return;
            case "rdblquote": AddChar('”'); return;
            case "emspace" or "enspace" or "qmspace": AddChar(' '); return;
            case "par" or "sect": AddParagraphMark(); return;
            case "page":
                AddParagraphMark();
                if (!_para.InTable) { CloseTable(); FlushList(); _body.Add(new PageBreak()); }
                return;
            case "cell": EndCell(); return;
            case "nestcell": AddChar('\t'); return;
            case "nestrow": AddParagraphMark(); return;
            case "row": EndRow(); return;
        }

        // Paragraph formatting.
        switch (word)
        {
            case "pard":
                FlushText();
                _para = new ParagraphState();
                return;
            case "s": _para.StyleIndex = param ?? 0; return;
            case "outlinelevel": _para.OutlineLevel = param; return;
            case "ls": _para.ListOverride = param ?? 0; return;
            case "ilvl": _para.ListLevel = param ?? 0; return;
            case "intbl": _para.InTable = true; return;
            case "ql": _para.Alignment = Alignment.Start; return;
            case "qc": _para.Alignment = Alignment.Center; return;
            case "qr": _para.Alignment = Alignment.End; return;
            case "qj" or "qd": _para.Alignment = Alignment.Justify; return;
            case "pagebb": _para.PageBreakBefore = true; return;
            case "pnlvlblt": _para.LegacyBullet = true; return;
            case "pnlvlbody": _para.LegacyNumber = true; return;
            case "pnlvl": _para.LegacyNumber = true; _para.LegacyLevel = Math.Max(0, (param ?? 1) - 1); return;
            case "pn": return;
        }

        // Table structure.
        switch (word)
        {
            case "trowd":
                // Writers repeat the row definition before \row, so start counting afresh each time.
                _rowMerges.Clear();
                _cellDefs = 0;
                _rowIsHeader = false;
                return;
            case "trhdr": _rowIsHeader = true; return;
            case "clvmrg": _rowMerges.Add(true); return;
            case "clvmgf": return;
            case "cellx":
                // Each \cellx closes a cell definition; record "not merged" when \clvmrg was absent.
                while (_rowMerges.Count < CellDefinitions() + 1) _rowMerges.Add(false);
                _cellDefs++;
                return;
        }
    }

    private int _cellDefs;

    private int CellDefinitions() => _cellDefs;

    private void SetStyle(TextStyle flag, bool on)
    {
        FlushText();
        _state.Style = on ? _state.Style | flag : _state.Style & ~flag;
    }

    private void SkipFallback(int count)
    {
        // Skips the ANSI fallback after \uN: plain characters or \'hh escapes count as one each.
        while (count > 0 && _pos < _rtf.Length)
        {
            var c = _rtf[_pos];
            if (c == '\\' && _pos + 1 < _rtf.Length && _rtf[_pos + 1] == '\'')
                _pos += 4;
            else if (c is '{' or '}' or '\\')
                break;
            else if (c is '\r' or '\n')
            {
                _pos++;
                continue;
            }
            else
                _pos++;
            count--;
        }
    }

    private static bool IsMonospace(string font) =>
        font.Contains("Courier", StringComparison.OrdinalIgnoreCase) || font.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
        font.Contains("Mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Lucida Console", StringComparison.OrdinalIgnoreCase);

    private static int CodePageForCharset(int charset) => charset switch
    {
        128 => 932, 129 => 949, 134 => 936, 136 => 950, 161 => 1253, 162 => 1254, 163 => 1258,
        177 => 1255, 178 => 1256, 186 => 1257, 204 => 1251, 222 => 874, 238 => 1250, 77 => 10000,
        _ => 0,
    };

    private int CurrentCodePage()
    {
        if (_fonts.TryGetValue(_state.Font, out var f) && f.CodePage != 0)
            return f.CodePage;
        return _defaultCodePage;
    }

    private void AddByte(byte b)
    {
        if (_state.Dest == Dest.Picture)
            return;
        var cp = CurrentCodePage();
        if (_bytes.Count > 0 && cp != _textCodePage)
            FlushBytes();
        _textCodePage = cp;
        _bytes.Add(b);
    }

    private void AddChar(char c)
    {
        switch (_state.Dest)
        {
            case Dest.Skip or Dest.ListTable or Dest.ListOverrideTable or Dest.Info:
                return;
            case Dest.Picture:
                if (Uri.IsHexDigit(c)) _pictHex.Append(c);
                return;
            case Dest.FontTable or Dest.StyleSheet or Dest.InfoField or Dest.FieldInstruction:
                FlushBytes();
                _destText.Append(c);
                return;
        }
        FlushBytes();
        if (_text.Length > 0 && (_textStyle != _state.Style || _textLink != CurrentLink()))
            FlushText();
        _textStyle = _state.Style;
        _textLink = CurrentLink();
        _text.Append(c);
    }

    private string? CurrentLink() => _state.InFieldResult ? _state.Link ?? _fieldLink : null;

    private void FlushBytes()
    {
        if (_bytes.Count == 0)
            return;
        string decoded;
        try
        {
            decoded = Encoding.GetEncoding(_textCodePage).GetString(_bytes.ToArray());
        }
        catch (ArgumentException)
        {
            decoded = Encoding.Latin1.GetString(_bytes.ToArray());
        }
        _bytes.Clear();
        foreach (var ch in decoded)
            AddChar(ch);
    }

    private void FlushText()
    {
        FlushBytes();
        if (_text.Length == 0)
            return;
        _inlines.Add(new TextRun(_text.ToString(), _textStyle, _textLink));
        _text.Clear();
    }

    private void AddParagraphMark()
    {
        if (_state.Dest != Dest.Normal)
            return;
        FlushText();
        EndParagraph();
    }

    private void EndParagraph()
    {
        var inlines = _inlines.Coalesce();
        _inlines = [];
        var hasContent = inlines.Any(i => i is TextRun r && r.Text.Trim().Length > 0 || i is InlineImage);

        if (_para.InTable)
        {
            _cell ??= new Model.TableCell();
            if (hasContent)
                _cell.Blocks.Add(MakeBlock(inlines));
            return;
        }

        CloseTable();
        if (_para.PageBreakBefore && _body.Count > 0)
        {
            FlushList();
            _body.Add(new PageBreak());
        }
        if (!hasContent)
        {
            FlushList();
            return;
        }

        var block = MakeBlock(inlines);
        if (block is Paragraph p && ListInfo() is { } list)
        {
            _pendingList.Add(new ListEntry(list.Level, list.Ordered, p));
            return;
        }
        FlushList();
        _body.Add(block);
    }

    private (int Level, bool Ordered)? ListInfo()
    {
        if (_para.ListOverride > 0)
        {
            var ordered = _overrides.TryGetValue(_para.ListOverride, out var listId) && _lists.TryGetValue(listId, out var levels) &&
                          _para.ListLevel < levels.Count && levels[_para.ListLevel];
            return (_para.ListLevel, ordered);
        }
        if (_para.LegacyBullet)
            return (0, false);
        if (_para.LegacyNumber)
            return (_para.LegacyLevel, true);
        return null;
    }

    private Block MakeBlock(List<Inline> inlines)
    {
        inlines.TrimInlines();
        var level = _para.OutlineLevel ?? (_styleOutline.TryGetValue(_para.StyleIndex, out var ol) ? ol : HeadingFromName());
        if (level is { } l and >= 0 and < 9)
        {
            var h = new Heading(l + 1);
            var runs = inlines.OfType<TextRun>().Where(r => r.Text.Trim().Length > 0).ToList();
            if (runs.Count > 0 && runs.All(r => r.Has(TextStyle.Bold)))
                foreach (var r in inlines.OfType<TextRun>())
                    r.Style &= ~TextStyle.Bold;
            h.Inlines.AddRange(inlines.Coalesce());
            return h;
        }
        if (inlines is [InlineImage only])
            return new ImageBlock(only.Image);
        var p = new Paragraph { Alignment = _para.Alignment };
        p.Inlines.AddRange(inlines);
        return p;
    }

    private int? HeadingFromName()
    {
        if (!_styleNames.TryGetValue(_para.StyleIndex, out var name))
            return null;
        if (name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(8), out var n))
            return n - 1;
        return name.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 0 : null;
    }

    private void FlushList()
    {
        if (_pendingList.Count == 0)
            return;
        _body.AddRange(ListGrouping.Build(_pendingList));
        _pendingList.Clear();
    }

    private void EndCell()
    {
        FlushText();
        if (_inlines.Count > 0)
        {
            var saved = _para.InTable;
            _para.InTable = true;
            EndParagraph();
            _para.InTable = saved;
        }
        if (_table is null)
        {
            FlushList();
            _table = new Model.Table();
        }
        _row ??= new Model.TableRow();
        var cell = _cell ?? new Model.TableCell();
        _cell = null;

        var merged = _cellIndex < _rowMerges.Count && _rowMerges[_cellIndex];
        if (merged)
        {
            // Vertically merged continuation: extend the cell above instead of adding one.
            var origin = _openVerticalMerges.LastOrDefault(m => m.Column == _cellIndex).Cell;
            if (origin is not null)
                origin.RowSpan++;
        }
        else
        {
            _row.Cells.Add(cell);
            _openVerticalMerges.RemoveAll(m => m.Column == _cellIndex);
            _openVerticalMerges.Add((_cellIndex, cell));
        }
        _cellIndex++;
    }

    private void EndRow()
    {
        FlushText();
        if (_table is null)
            return;
        if (_row is not null)
        {
            _row.IsHeader = _rowIsHeader && _table.Rows.All(r => r.IsHeader);
            _table.Rows.Add(_row);
        }
        _row = null;
        _cellIndex = 0;
        _cellDefs = 0;
    }

    private void CloseTable()
    {
        if (_table is null)
            return;
        if (_row is { Cells.Count: > 0 })
            _table.Rows.Add(_row);
        if (_table.Rows.Count > 0)
            _body.Add(_table);
        _table = null;
        _row = null;
        _cell = null;
        _cellIndex = 0;
        _cellDefs = 0;
        _openVerticalMerges.Clear();
    }

    private void FinishPicture()
    {
        var hex = _pictHex.ToString();
        _pictHex.Clear();
        if (_pictType.Length == 0 || hex.Length < 16)
            return;
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex.Length % 2 == 0 ? hex : hex[..^1]);
        }
        catch (FormatException)
        {
            return;
        }
        var image = ImageData.Create(bytes);
        if (image is null)
            return;
        if (_pictWidthGoal > 0 && _pictHeightGoal > 0)
        {
            image.WidthPt = _pictWidthGoal / 20.0 * _pictScaleX / 100.0;
            image.HeightPt = _pictHeightGoal / 20.0 * _pictScaleY / 100.0;
        }
        FlushText();
        _inlines.Add(new InlineImage(image));
    }

    private static string? ParseHyperlink(string instruction)
    {
        var text = instruction.Trim();
        if (!text.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase))
            return null;
        var start = text.IndexOf('"');
        var end = start >= 0 ? text.IndexOf('"', start + 1) : -1;
        if (start >= 0 && end > start)
        {
            var target = text[(start + 1)..end];
            return text.Contains("\\l", StringComparison.Ordinal) ? "#" + target : target;
        }
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : null;
    }
}
