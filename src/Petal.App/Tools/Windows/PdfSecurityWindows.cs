using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Petal.App.Input;
using Petal.App.Jobs;
using Petal.App.Localization;
using Petal.App.Settings;
using Petal.App.Tools.Parts;
using Petal.App.Visuals;
using Petal.Core.Jobs;
using Petal.Core.Tools;
using Petal.Engines.Documents.Pdf;
using Petal.Recipes;

namespace Petal.App.Tools.Windows;

// ---------------------------------------------------------------------------------------------
// Password — protects a PDF with AES-256, or removes the password from one that has it.
// ---------------------------------------------------------------------------------------------

public sealed class PdfPasswordWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly TextBlock _heading = new() { Text = L.T("Checking…") };
    private readonly StackPanel _form = new() { Visibility = Visibility.Collapsed };
    private readonly PasswordField _password = new();
    private readonly PasswordField _repeat = new();
    private readonly CheckBox _print = new() { Content = L.T("Allow printing"), IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _copy = new() { Content = L.T("Allow copying text and pictures"), IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _edit = new() { Content = L.T("Allow changes and comments"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private bool _unlocking;
    private IReadOnlyList<string> _targets = [];

    public PdfPasswordWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 420)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        _heading.Style = (Style)FindResource("Petal.Section");
        Body.Children.Add(_heading);
        Body.Children.Add(_form);
        _problem.Style = (Style)FindResource("Petal.Caption");
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "Petal.Danger");
        _password.Changed += Validate;
        _repeat.Changed += Validate;
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Click += async (_, _) => await ApplyAsync();
        FooterNote = FileLabel(files);
        Loaded += async (_, _) => await DetectAsync();
    }

    private async Task DetectAsync()
    {
        var locked = await Task.Run(() => _files.Select(PdfSecurityTools.NeedsPassword).ToList());
        // A mixed selection is handled in the direction of the first file.
        _unlocking = locked[0];
        _targets = _files.Where((_, i) => locked[i] == _unlocking).ToList();
        int skipped = _files.Count - _targets.Count;

        _heading.Text = L.T(_unlocking ? "Remove the password" : "Protect with a password");
        _form.Children.Add(Row("Password", _password, 110, 12));
        if (_unlocking)
        {
            _form.Children.Add(Text("Saves a copy that opens without a password. The original keeps its password.", "Petal.Caption").WithMarginT(8));
            PrimaryButton.Content = L.T("Remove Password");
        }
        else
        {
            _form.Children.Add(Row("Repeat", _repeat, 110, 8));
            _form.Children.Add(_print);
            _form.Children.Add(_copy);
            _form.Children.Add(_edit);
            _form.Children.Add(Text("Encrypted with AES-256: without the password the PDF can't be opened. Keep the password safe — it can't be recovered.", "Petal.Caption").WithMarginT(10));
            PrimaryButton.Content = L.T("Protect");
        }
        if (skipped > 0)
            _form.Children.Add(Text(L.P(skipped, _unlocking ? "{0} file has no password and is skipped." : "{0} file already has a password and is skipped.",
                _unlocking ? "{0} files have no password and are skipped." : "{0} files already have a password and are skipped."), "Petal.Caption").WithMarginT(8));
        _form.Children.Add(_problem);
        _form.Visibility = Visibility.Visible;
        FooterNote = FileLabel(_targets.Count == 1 ? [Output(_targets[0])] : _targets);
        _password.FocusInput();
        Validate();
    }

    private string Output(string file) => OutputNaming.ForTool(file, _tool, _unlocking ? "Unlocked" : null);

    private void Validate()
    {
        string? problem = null;
        var password = _password.Password;
        if (!_unlocking && password.Length > 0)
        {
            if (!PdfSecurityTools.IsUsablePassword(password))
                problem = "A password with Arabic letters must start and end with one, and can't also contain Latin letters.";
            else if (_repeat.Password.Length > 0 && _repeat.Password != password)
                problem = "The two passwords don't match.";
        }
        Show(problem);
        PrimaryButton.IsEnabled = password.Length > 0 && problem is null && (_unlocking || _repeat.Password == password);
    }

    private void Show(string? problem)
    {
        _problem.Text = problem is null ? "" : L.T(problem);
        _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ApplyAsync()
    {
        var password = _password.Password;
        var targets = _targets;
        if (_unlocking)
        {
            PrimaryButton.IsEnabled = false;
            var wrong = await Task.Run(() => targets.FirstOrDefault(f => !PdfSecurityTools.CanOpen(f, password)));
            if (wrong is not null)
            {
                Show(targets.Count == 1 ? "That password doesn't open this PDF." : L.F("That password doesn't open “{0}”.", Path.GetFileName(wrong)));
                PrimaryButton.IsEnabled = true;
                return;
            }
        }
        var options = new PdfProtectOptions(password, _print.IsChecked == true, _copy.IsChecked == true, _edit.IsChecked == true);
        bool unlocking = _unlocking;
        Close();
        var work = targets.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = Output(file);
            if (unlocking)
                await PdfSecurityTools.UnlockAsync(file, output, password, p, ct);
            else
                await PdfSecurityTools.ProtectAsync(file, output, options, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = unlocking
            ? _jobs.RunAsync("Removing password", "Password removed", work, _at)
            : _jobs.RunAsync("Protecting PDF", "PDF protected", work, _at);
    }
}

// ---------------------------------------------------------------------------------------------
// Add Page Numbers — real text at the top or bottom of every page.
// ---------------------------------------------------------------------------------------------

public sealed class PageNumbersWindow : ToolWindow
{
    private readonly ToolInfo _tool;
    private readonly IReadOnlyList<string> _files;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Segmented<bool> _top;
    private readonly Segmented<int> _side;
    private readonly Segmented<PageNumberStyle> _style;
    private readonly LabeledSlider _size;
    private readonly TextBox _start = new() { Text = "1", Width = 70, FlowDirection = FlowDirection.LeftToRight };
    private readonly CheckBox _skipFirst = new() { Content = L.T("Leave the first page unnumbered (a cover)"), Margin = new Thickness(0, 12, 0, 0) };
    private readonly Image _page = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform };
    private readonly Image _overlay = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private PdfPageFrame? _frame;
    private int _pageCount = 1;

    public PageNumbersWindow(ToolInfo tool, IReadOnlyList<string> files, JobRunner jobs, ScreenPoint at) : base(tool.Name, 430)
    {
        _tool = tool;
        _files = files;
        _jobs = jobs;
        _at = at;
        var stage = new Grid { Margin = new Thickness(10) };
        stage.Children.Add(_page);
        stage.Children.Add(_overlay);
        Body.Children.Add(PreviewPanel(stage, 240));

        _top = Segments([(false, "Bottom"), (true, "Top")], false);
        _side = Segments([(0, "Left"), (1, "Centre"), (2, "Right")], 1);
        Body.Children.Add(Row("Position", _top, 100, 14));
        Body.Children.Add(Row("Align", _side, 100, 8));
        _style = Segments([(PageNumberStyle.Number, "1"), (PageNumberStyle.NumberOfTotal, "\u200E1 / 12\u200E"), (PageNumberStyle.Dashes, "- 1 -")], PageNumberStyle.Number);
        Body.Children.Add(Row("Style", _style, 100, 10));
        _size = Slider(7, 24, 10, v => v.ToString("0", CultureInfo.CurrentCulture) + " pt", 1);
        Body.Children.Add(Row("Size", _size, 100, 10));
        Body.Children.Add(Row("Start at", _start, 100, 10));
        Body.Children.Add(_skipFirst);

        _top.Changed += _ => Draw();
        _side.Changed += _ => Draw();
        _style.Changed += _ => Draw();
        _size.Changed += _ => Draw();
        _start.TextChanged += (_, _) => Draw();
        _skipFirst.Checked += async (_, _) => await LoadPageAsync();
        _skipFirst.Unchecked += async (_, _) => await LoadPageAsync();

        PrimaryButton.Content = L.T("Add Numbers");
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = FileLabel(files.Count == 1 ? [OutputNaming.ForTool(files[0], tool)] : files);
        Loaded += async (_, _) => await LoadPageAsync();
    }

    private PageNumberPlace Place() => (_top.Value, _side.Value) switch
    {
        (true, 0) => PageNumberPlace.TopLeft,
        (true, 1) => PageNumberPlace.TopCenter,
        (true, _) => PageNumberPlace.TopRight,
        (false, 0) => PageNumberPlace.BottomLeft,
        (false, 1) => PageNumberPlace.BottomCenter,
        _ => PageNumberPlace.BottomRight,
    };

    private int Start => int.TryParse(_start.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var s) ? s : 1;

    private async Task LoadPageAsync()
    {
        try
        {
            var file = _files[0];
            var frames = await Task.Run(() => PdfStamper.Pages(file));
            _pageCount = frames.Count;
            int index = _skipFirst.IsChecked == true && frames.Count > 1 ? 1 : 0;
            _frame = frames[index];
            var png = await Task.Run(() => PdfInfo.RenderPagePng(file, index, 600));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            _page.Source = image;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Page numbers preview");
            FooterNote = ex switch
            {
                PdfOpenException { NeedsPassword: true } => "Remove the PDF's password first.",
                PdfOpenException { LockedForChanges: true } => "Its author has locked this PDF against changes.",
                _ => "Couldn't open this PDF.",
            };
            PrimaryButton.IsEnabled = false;
        }
        Draw();
    }

    /// <summary>The number drawn where the engine will put it, at the preview's scale.</summary>
    private void Draw()
    {
        if (_page.Source is not BitmapSource page || _frame is not { } frame)
            return;
        double k = page.PixelWidth / frame.Width;
        int first = _skipFirst.IsChecked == true ? 1 : 0;
        int n = Start, total = Start + _pageCount - first - 1;
        var text = _style.Value switch
        {
            PageNumberStyle.NumberOfTotal => $"{n} / {total}",
            PageNumberStyle.Dashes => $"- {n} -",
            _ => n.ToString(CultureInfo.InvariantCulture),
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), _size.Value * k, new SolidColorBrush(Color.FromRgb(60, 60, 64)), 1.0);
            double margin = Math.Clamp(Math.Min(frame.Width, frame.Height) * 0.07, 18, 42) * k;
            double x = _side.Value switch { 0 => margin, 1 => (page.PixelWidth - ft.Width) / 2, _ => page.PixelWidth - margin - ft.Width };
            double y = (_top.Value ? margin : page.PixelHeight - margin) - ft.Height / 2;
            dc.DrawText(ft, new Point(x, y));
        }
        var bitmap = new RenderTargetBitmap(page.PixelWidth, page.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        _overlay.Source = bitmap;
    }

    private void Apply()
    {
        var options = new PageNumberOptions(Place(), _style.Value, _size.Value, Start, _skipFirst.IsChecked == true);
        Close();
        var work = _files.Select(file => new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            var output = OutputNaming.ForTool(file, _tool);
            await PdfStamper.NumberPagesAsync(file, output, options, p, ct);
            return (IReadOnlyList<string>)[output];
        })).ToList();
        _ = _jobs.RunAsync("Numbering pages", "Pages numbered", work, _at);
    }
}

// ---------------------------------------------------------------------------------------------
// Sign PDF — a drawn, typed or scanned signature placed on a page.
// ---------------------------------------------------------------------------------------------

public enum SignaturePages
{
    Last,
    First,
    Every,
}

public sealed class SignPdfWindow : ToolWindow
{
    private static readonly Color Ink = Color.FromRgb(0x1D, 0x2B, 0x6B);
    private readonly ToolInfo _tool;
    private readonly string _file;
    private readonly JobRunner _jobs;
    private readonly ScreenPoint _at;
    private readonly Segmented<int> _source;
    private readonly InkCanvas _ink = new() { Height = 130, FlowDirection = FlowDirection.LeftToRight, Background = Brushes.White, Cursor = System.Windows.Input.Cursors.Pen };
    private readonly TextBox _name = new();
    private readonly Button _choose = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _drawPanel = new(), _typePanel, _picturePanel;
    private readonly Segmented<SignaturePages> _pages;
    private readonly PlacePicker _place = new(Place9.BottomRight);
    private readonly LabeledSlider _size;
    private readonly Image _page = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform };
    private readonly Image _overlay = new() { FlowDirection = FlowDirection.LeftToRight, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly DispatcherTimer _redraw;
    private IReadOnlyList<PdfPageFrame> _frames = [];
    private int _previewIndex = -1;
    private BitmapSource? _picture;
    private string? _picturePath;

    public SignPdfWindow(ToolInfo tool, string file, JobRunner jobs, ScreenPoint at) : base(tool.Name, 460)
    {
        _tool = tool;
        _file = file;
        _jobs = jobs;
        _at = at;

        var stage = new Grid { Margin = new Thickness(10) };
        stage.Children.Add(_page);
        stage.Children.Add(_overlay);
        Body.Children.Add(PreviewPanel(stage, 210));

        _source = Segments([(0, "Draw"), (1, "Type"), (2, "Picture")], 0);
        _source.Changed += _ => Sync();
        Body.Children.Add(Row("Signature", _source, 100, 14));

        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = Ink, Width = 2.6, Height = 2.6, FitToCurve = true };
        _ink.StrokeCollected += (_, _) => Sync();
        var inkFrame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = _ink, ClipToBounds = true };
        inkFrame.SetResourceReference(Border.BorderBrushProperty, "Petal.ControlBorder");
        // Below the pad, not on it: the pad stays white in the dark theme.
        var clear = new Button { Content = L.T("Clear"), Style = (Style)FindResource("Petal.SubtleButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, -8, 0) };
        clear.Click += (_, _) => { _ink.Strokes.Clear(); Sync(); };
        _drawPanel.Margin = new Thickness(0, 10, 0, 0);
        _drawPanel.RowDefinitions.Add(new RowDefinition());
        _drawPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(clear, 1);
        _drawPanel.Children.Add(inkFrame);
        _drawPanel.Children.Add(clear);
        Body.Children.Add(_drawPanel);

        _name.TextChanged += (_, _) => Sync();
        _typePanel = Row("Your name", _name, 100, 10);
        Body.Children.Add(_typePanel);
        _choose.Click += (_, _) => ChoosePicture();
        _picturePanel = Row("Picture", _choose, 100, 10);
        Body.Children.Add(_picturePanel);

        _pages = Segments([(SignaturePages.Last, "Last page"), (SignaturePages.First, "First page"), (SignaturePages.Every, "Every page")], SignaturePages.Last);
        _pages.Changed += async _ => await LoadPreviewPageAsync();
        Body.Children.Add(Row("Pages", _pages, 100, 14));
        _place.Changed += _ => Sync();
        Body.Children.Add(Row("Position", _place, 100, 10));
        _size = Slider(10, 60, 28, v => v.ToString("0", CultureInfo.CurrentCulture) + "%", 1);
        _size.Changed += _ => Sync();
        Body.Children.Add(Row("Size", _size, 100, 10));
        Body.Children.Add(Text("This places your signature on the page as a picture. It isn't a certificate-based digital signature.", "Petal.Caption").WithMarginT(10));

        _redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => DrawPreview(), Dispatcher);
        PrimaryButton.Content = L.T("Sign");
        PrimaryButton.Click += (_, _) => Apply();
        FooterNote = Path.GetFileName(OutputNaming.ForTool(file, tool));
        Loaded += async (_, _) =>
        {
            try
            {
                _frames = await Task.Run(() => PdfStamper.Pages(file));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Opening PDF to sign");
                FooterNote = ex switch
                {
                    PdfOpenException { NeedsPassword: true } => "Remove the PDF's password first.",
                    PdfOpenException { LockedForChanges: true } => "Its author has locked this PDF against changes.",
                    _ => "Couldn't open this PDF.",
                };
            }
            await LoadPreviewPageAsync();
        };
        Sync();
    }

    private void Sync()
    {
        _drawPanel.Visibility = _source.Value == 0 ? Visibility.Visible : Visibility.Collapsed;
        _typePanel.Visibility = _source.Value == 1 ? Visibility.Visible : Visibility.Collapsed;
        _picturePanel.Visibility = _source.Value == 2 ? Visibility.Visible : Visibility.Collapsed;
        _choose.Content = _picturePath is null ? L.T("Choose picture…") : Path.GetFileName(_picturePath);
        PrimaryButton.IsEnabled = _frames.Count > 0 && Signature() is not null;
        _redraw.Stop();
        _redraw.Start();
    }

    private async Task LoadPreviewPageAsync()
    {
        if (_frames.Count == 0)
            return;
        int index = _pages.Value == SignaturePages.First ? 0 : _frames.Count - 1;
        if (index == _previewIndex)
            return;
        _previewIndex = index;
        try
        {
            var png = await Task.Run(() => PdfInfo.RenderPagePng(_file, index, 600));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            _page.Source = image;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sign preview");
        }
        Sync();
    }

    private StampSpec Spec(BitmapSource signature) => new()
    {
        Picture = signature,
        Place = _place.Value,
        Size = _size.Value / 100,
        Opacity = 1,
    };

    private void DrawPreview()
    {
        _redraw.Stop();
        if (_page.Source is not BitmapSource page)
            return;
        _overlay.Source = Signature() is { } signature ? StampRenderer.Render(page.PixelWidth, page.PixelHeight, Spec(signature)) : null;
    }

    /// <summary>The signature as a tightly cropped transparent picture, or null when there is none yet.</summary>
    private BitmapSource? Signature() => _source.Value switch
    {
        0 when _ink.Strokes.Count > 0 => RenderInk(),
        1 when _name.Text.Trim().Length > 0 => RenderName(_name.Text.Trim()),
        2 => _picture,
        _ => null,
    };

    private BitmapSource RenderInk()
    {
        var bounds = _ink.Strokes.GetBounds();
        bounds.Inflate(4, 4);
        const double scale = 3;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            _ink.Strokes.Draw(dc);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource RenderName(string name)
    {
        // Handwriting-style fonts that come with Windows, in order of preference.
        var family = new[] { "Segoe Script", "Ink Free", "Segoe Print" }.FirstOrDefault(f => Fonts.SystemFontFamilies.Any(s => s.Source == f)) ?? "Segoe UI";
        bool rtl = name.Any(c => c is >= '\u0590' and <= '\u08FF');
        var text = new FormattedText(name, CultureInfo.CurrentUICulture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            new Typeface(new FontFamily(rtl ? "Segoe UI" : family), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal), 120, new SolidColorBrush(Ink), 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var b = geometry.Bounds;
        b.Inflate(6, 6);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-b.X, -b.Y));
            dc.DrawGeometry(new SolidColorBrush(Ink), null, geometry);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(b.Width), (int)Math.Ceiling(b.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void ChoosePicture()
    {
        var dialog = new OpenFileDialog { Filter = L.T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) != true)
            return;
        _ = Load(dialog.FileName);

        async Task Load(string path)
        {
            try
            {
                _picture = await Parts.ImageLoader.LoadAsync(path, 1600);
                _picturePath = path;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Loading signature picture");
                FooterNote = "Couldn't open this image.";
            }
            Sync();
        }
    }

    private void Apply()
    {
        if (Signature() is not { } signature)
            return;
        var spec = Spec(signature);
        var which = _pages.Value;
        int last = _frames.Count - 1;
        var file = _file;
        var output = OutputNaming.ForTool(file, _tool);
        Close();
        _ = _jobs.RunAsync("Signing PDF", "PDF signed", [new WorkItem(file, Path.GetFileName(file), async (p, ct) =>
        {
            await PdfStamper.StampAsync(file, output, f =>
                which == SignaturePages.Every || (which == SignaturePages.First && f.Index == 0) || (which == SignaturePages.Last && f.Index == last)
                    ? StampRenderer.RenderPng((int)(f.Width * 2), (int)(f.Height * 2), spec)
                    : null, null, p, ct);
            return (IReadOnlyList<string>)[output];
        })], _at);
    }
}
