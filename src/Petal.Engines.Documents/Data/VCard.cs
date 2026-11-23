using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Petal.Engines.Documents.Data;

/// <summary>
/// Contacts from vCard files (2.1, 3.0 and 4.0, as phones and address books export them): names, phone numbers,
/// e-mail addresses, organisation, title, addresses, birthday, notes and websites. Lines folded over several lines
/// are joined; quoted-printable text in any character set (old Android exports write Arabic names that way) is
/// decoded. Photos and other binary parts are left out.
/// </summary>
internal static class VCard
{
    static VCard() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public sealed class Contact
    {
        public string Name { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public List<(string Value, string Type)> Phones { get; } = [];
        public List<(string Value, string Type)> Emails { get; } = [];
        public string Organization { get; set; } = "";
        public string Title { get; set; } = "";
        public List<string> Addresses { get; } = [];
        public string Birthday { get; set; } = "";
        public string Note { get; set; } = "";
        public List<string> Websites { get; } = [];
    }

    public static List<Contact> Read(byte[] bytes)
    {
        // The file's own encoding: UTF-8 (with or without BOM) or UTF-16; quoted-printable parts say theirs.
        var text = bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF)
            ? Encoding.Unicode.GetString(bytes).TrimStart('﻿')
            : new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        var contacts = new List<Contact>();
        Contact? current = null;
        foreach (var line in Unfold(text))
        {
            var colon = Colon(line);
            if (colon < 0)
                continue;
            var head = line[..colon];
            var value = line[(colon + 1)..];
            var parts = head.Split(';');
            var name = parts[0];
            int dot = name.LastIndexOf('.');
            if (dot >= 0)
                name = name[(dot + 1)..]; // "item1.TEL": the group goes.
            name = name.ToUpperInvariant();
            if (name == "BEGIN" && value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                current = new Contact();
                continue;
            }
            if (name == "END" && value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                {
                    if (current.Name.Length == 0)
                        current.Name = string.Join(' ', new[] { current.FirstName, current.LastName }.Where(s => s.Length > 0));
                    contacts.Add(current);
                }
                current = null;
                continue;
            }
            if (current is null)
                continue;
            var parameters = Parameters(parts.Skip(1));
            if (parameters.TryGetValue("ENCODING", out var encoding) && encoding.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
                value = QuotedPrintable(value, parameters.GetValueOrDefault("CHARSET") ?? "UTF-8");
            else if (parameters.ContainsKey("ENCODING"))
                continue; // base64 photos, sounds, keys
            var type = Type(parameters);
            switch (name)
            {
                case "FN":
                    current.Name = Unescape(value).Trim();
                    break;
                case "N":
                {
                    var n = SplitEscaped(value, ';');
                    current.LastName = n.ElementAtOrDefault(0)?.Trim() ?? "";
                    current.FirstName = n.ElementAtOrDefault(1)?.Trim() ?? "";
                    break;
                }
                case "TEL":
                    if (Clean(value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? value[4..] : value) is { Length: > 0 } tel)
                        current.Phones.Add((tel, type));
                    break;
                case "EMAIL":
                    if (Unescape(value).Trim() is { Length: > 0 } email)
                        current.Emails.Add((email, type));
                    break;
                case "ORG":
                    current.Organization = string.Join(", ", SplitEscaped(value, ';').Select(s => s.Trim()).Where(s => s.Length > 0));
                    break;
                case "TITLE":
                    current.Title = Unescape(value).Trim();
                    break;
                case "ADR":
                {
                    var address = string.Join(", ", SplitEscaped(value, ';').Select(s => s.Trim()).Where(s => s.Length > 0));
                    if (address.Length > 0)
                        current.Addresses.Add(address);
                    break;
                }
                case "BDAY":
                    current.Birthday = Birthday(Unescape(value).Trim());
                    break;
                case "NOTE":
                    current.Note = Unescape(value).Trim();
                    break;
                case "URL":
                    if (Unescape(value).Trim() is { Length: > 0 } url)
                        current.Websites.Add(url);
                    break;
            }
        }
        if (contacts.Count == 0)
            throw new DataShapeException("This file holds no contacts.");
        return contacts;
    }

    /// <summary>
    /// Joins folded lines: a line starting with a space or tab goes on the one before (2.1, 3.0, 4.0), and a
    /// quoted-printable line ending with "=" goes on with the next (2.1).
    /// </summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var current = new StringBuilder();
        bool open = false;
        foreach (var raw in lines)
        {
            if (open && (raw.StartsWith(' ') || raw.StartsWith('\t')))
            {
                // A quoted-printable soft break ("=") folded the usual way too: the "=" goes.
                if (current.Length > 0 && current[^1] == '=' && IsQuotedPrintable(current.ToString()))
                    current.Length--;
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (open && current.Length > 0 && current[^1] == '=' && IsQuotedPrintable(current.ToString()))
            {
                current.Length--;
                current.Append(raw.TrimStart());
                continue;
            }
            if (open)
                yield return current.ToString();
            current.Clear().Append(raw);
            open = true;
        }
        if (open && current.Length > 0)
            yield return current.ToString();
    }

    private static bool IsQuotedPrintable(string line)
    {
        var colon = Colon(line);
        return colon > 0 && line[..colon].Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the value starts: the first colon not inside a quoted parameter.</summary>
    private static int Colon(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }

    private static Dictionary<string, string> Parameters(IEnumerable<string> parts)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var types = new List<string>();
        foreach (var part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq < 0)
            {
                // vCard 2.1: "TEL;CELL;PREF:" and "NOTE;QUOTED-PRINTABLE:".
                if (part.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase) || part.Equals("BASE64", StringComparison.OrdinalIgnoreCase))
                    map["ENCODING"] = part;
                else
                    types.Add(part);
                continue;
            }
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim('"');
            if (key.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
                types.AddRange(value.Split(','));
            else
                map[key] = value;
        }
        if (types.Count > 0)
            map["TYPE"] = string.Join(',', types);
        return map;
    }

    /// <summary>The kind of number or address, in plain words: mobile, home, work, fax, other.</summary>
    private static string Type(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("TYPE", out var types))
            return "";
        var words = types.Split(',').Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
        if (words.Contains("fax")) return "fax";
        if (words.Contains("cell") || words.Contains("mobile") || words.Contains("iphone")) return "mobile";
        if (words.Contains("work")) return "work";
        if (words.Contains("home")) return "home";
        return words.Contains("other") ? "other" : "";
    }

    private static string QuotedPrintable(string value, string charset)
    {
        var bytes = new List<byte>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }
        return encoding.GetString(bytes.ToArray());
    }

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\:", ":").Replace("\\\\", "\\");

    /// <summary>Splits on <paramref name="separator"/> except where it is escaped, and unescapes each part.</summary>
    private static List<string> SplitEscaped(string value, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                current.Append(value[i]).Append(value[i + 1]);
                i++;
            }
            else if (value[i] == separator)
            {
                parts.Add(Unescape(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }
        parts.Add(Unescape(current.ToString()));
        return parts;
    }

    private static string Clean(string phone) => Unescape(phone).Trim();

    /// <summary>A birthday as 1990-05-17 when it is a full date ("19900517", "1990-05-17T00:00:00Z"); otherwise as written.</summary>
    private static string Birthday(string text)
    {
        foreach (var format in new[] { "yyyyMMdd", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss" })
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date))
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return text;
    }

    // ---------- as records ----------

    public static JsonArray ToJson(IReadOnlyList<Contact> contacts)
    {
        var array = new JsonArray();
        foreach (var c in contacts)
        {
            var obj = new JsonObject { ["name"] = c.Name };
            if (c.FirstName.Length > 0) obj["firstName"] = c.FirstName;
            if (c.LastName.Length > 0) obj["lastName"] = c.LastName;
            if (c.Phones.Count > 0) obj["phones"] = new JsonArray(c.Phones.Select(p => (JsonNode)new JsonObject { ["number"] = p.Value, ["type"] = p.Type.Length > 0 ? p.Type : null }).ToArray());
            if (c.Emails.Count > 0) obj["emails"] = new JsonArray(c.Emails.Select(e => (JsonNode)new JsonObject { ["address"] = e.Value, ["type"] = e.Type.Length > 0 ? e.Type : null }).ToArray());
            if (c.Organization.Length > 0) obj["organization"] = c.Organization;
            if (c.Title.Length > 0) obj["title"] = c.Title;
            if (c.Addresses.Count > 0) obj["addresses"] = new JsonArray(c.Addresses.Select(a => (JsonNode)JsonValue.Create(a)).ToArray());
            if (c.Birthday.Length > 0) obj["birthday"] = c.Birthday;
            if (c.Note.Length > 0) obj["note"] = c.Note;
            if (c.Websites.Count > 0) obj["websites"] = new JsonArray(c.Websites.Select(w => (JsonNode)JsonValue.Create(w)).ToArray());
            array.Add(obj);
        }
        return array;
    }

    /// <summary>
    /// Contacts as a table, one a row: Name, First name, Last name, as many Phone and E-mail columns as the contact
    /// with the most has (each phone's kind in its own column), then Organization, Title, Address, Birthday, Note
    /// and Website. Columns no contact uses are left out.
    /// </summary>
    public static Sheets.WorkbookModel ToWorkbook(IReadOnlyList<Contact> contacts, string title)
    {
        int phones = contacts.Max(c => c.Phones.Count), emails = contacts.Max(c => c.Emails.Count);
        var columns = new List<(string Header, Func<Contact, string> Value)>
        {
            ("Name", c => c.Name),
            ("First name", c => c.FirstName),
            ("Last name", c => c.LastName),
        };
        for (int i = 0; i < phones; i++)
        {
            int n = i;
            var suffix = i == 0 ? "" : $" {i + 1}";
            columns.Add(($"Phone{suffix}", c => n < c.Phones.Count ? c.Phones[n].Value : ""));
            columns.Add(($"Phone{suffix} type", c => n < c.Phones.Count ? c.Phones[n].Type : ""));
        }
        for (int i = 0; i < emails; i++)
        {
            int n = i;
            columns.Add(($"E-mail{(i == 0 ? "" : $" {i + 1}")}", c => n < c.Emails.Count ? c.Emails[n].Value : ""));
        }
        columns.Add(("Organization", c => c.Organization));
        columns.Add(("Title", c => c.Title));
        columns.Add(("Address", c => string.Join(" | ", c.Addresses)));
        columns.Add(("Birthday", c => c.Birthday));
        columns.Add(("Note", c => c.Note));
        columns.Add(("Website", c => string.Join(" ", c.Websites)));
        columns = columns.Where(col => contacts.Any(c => col.Value(c).Length > 0)).ToList();

        var sheet = new Sheets.SheetModel("Contacts");
        sheet.Rows.Add(columns.Select(c => new Sheets.SheetCell(c.Header)).ToList());
        // Every value stays text: a phone number like 0555… must keep its leading zero.
        foreach (var contact in contacts)
            sheet.Rows.Add(columns.Select(c => new Sheets.SheetCell(c.Value(contact) is { Length: > 0 } v ? v : null)).ToList());
        var workbook = new Sheets.WorkbookModel { Title = title };
        workbook.Sheets.Add(sheet);
        return workbook;
    }
}
