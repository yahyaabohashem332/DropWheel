using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Petal.App.Overlay;
using Petal.App.Visuals;
using Petal.Core.Jobs;
using Petal.Engines.Imaging;

namespace Petal.App;

/// <summary>
/// "Petal.exe --snapshots &lt;folder&gt; &lt;background image&gt; &lt;files…&gt;" renders the wheel in every
/// mode, light and dark, to PNG files without touching the screen. Used for design reviews.
/// </summary>
internal static class DesignSnapshots
{
    /// <summary>"Petal.exe --snapshot-tools &lt;folder&gt; &lt;test data folder&gt;" renders each tool window with sample files.</summary>
    public static async Task RunToolsAsync(string[] args)
    {
        var output = args[1];
        var data = args[2];
        Directory.CreateDirectory(output);
        _snapshotFolder = output;
        var converters = EngineSetup.CreateConverters();
        var jobs = new Jobs.JobRunner(converters);
        var at = new Input.ScreenPoint(-4000, -4000);
        string F(string name) => Path.Combine(data, name);
        var cases = new List<(string Name, Func<Window> Create)>
        {
            ("annotate", () => new Tools.Windows.AnnotateWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.AnnotatePhoto), F("landscape.jpg"), jobs, at)),
            ("addbg", () => new Tools.Windows.AddBackgroundWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.AddBackground), F("figures.png"), jobs, at)),
            ("collage", () => new Tools.Windows.CollageWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.CreateCollage), [F("landscape.jpg"), F("figures.png"), F("landscape.jpg")], jobs, at)),
            ("metadata", () => new Tools.Windows.MetadataWindow(Petal.Core.Tools.ToolRegistry.Get(Petal.Core.Tools.ToolId.Metadata), [F("landscape.jpg")], jobs, at)),
        };
        foreach (var extra in ExtraToolCases(F, jobs, at))
            cases.Add(extra);
        var floating = FloatingCases().ToList();
        var only = args.Length > 3 ? args[3] : null;
        foreach (var dark in new[] { false, true })
        {
            Theme.Configure(dark ? ThemePreference.Dark : ThemePreference.Light, false);
            foreach (var (name, create) in cases)
            {
                if (only is not null && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var window = create();
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -6000;
                    window.Top = -6000;
                    window.ShowActivated = false;
                    window.Show();
                    await Task.Delay(2500);
                    var cut = new List<string>();
                    Save(RenderWindow(window, inspect: dark ? null : frame => cut.AddRange(CutTexts(frame))), Path.Combine(output, $"{(dark ? "dark" : "light")}-{name}.png"));
                    if (cut.Count > 0)
                        File.AppendAllLines(Path.Combine(output, "cut.txt"), cut.Select(c => $"{name}: {c}"));
                    window.Close();
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Path.Combine(output, "error.txt"), $"{name}: {ex}{Environment.NewLine}");
                }
            }
            foreach (var (name, create) in floating)
            {
                if (only is not null && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var window = create();
                    await Task.Delay(600);
                    if (!dark && window.Content is FrameworkElement content)
                        File.AppendAllLines(Path.Combine(output, "cut.txt"), CutTexts(content).Select(c => $"{name}: {c}"));
                    Save(RenderFloating(window, dark), Path.Combine(output, $"{(dark ? "dark" : "light")}-{name}.png"));
                    window.Close();
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Path.Combine(output, "error.txt"), $"{name}: {ex}{Environment.NewLine}");
                }
            }
        }
    }

    /// <summary>
    /// "Petal.exe --snapshot-circle &lt;folder&gt; &lt;test data folder&gt;": Circle Anything over a sample page, in each state
    /// (the pointer over a paragraph, reading, the wheel, the cards, a translation laid over the words), light and dark.
    /// Today's rates come from a file written here, so nothing goes online.
    /// </summary>
    public static async Task RunCircleAsync(string[] args)
    {
        var output = args[1];
        var data = args[2];
        Directory.CreateDirectory(output);
        var rates = Path.Combine(output, "rates.json");
        var feed = new { result = "success", time_last_update_unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            rates = new Dictionary<string, double> { ["USD"] = 1, ["SAR"] = 3.75, ["EUR"] = 0.92, ["GBP"] = 0.79, ["AED"] = 3.6725, ["EGP"] = 48.6, ["TRY"] = 41.8 } };
        File.WriteAllText(rates, System.Text.Json.JsonSerializer.Serialize(feed));
        Petal.Recipes.ExchangeRateService.CacheFile = rates;
        var still = CircleSample(Path.Combine(data, "landscape.jpg"));
        Save(still, Path.Combine(output, "sample.png"));
        File.WriteAllText(Path.Combine(output, "ocr.txt"), "Windows reads: " + string.Join(", ", Petal.Engines.Documents.DocumentEngine.OcrLanguages));
        var controller = new PetalController(EngineSetup.CreateConverters(), Application.Current.Dispatcher);
        var wheel = controller.CreateWheelForSnapshots();
        static List<Point> Loop(double cx, double cy, double rx, double ry) =>
            Enumerable.Range(0, 49).Select(i => new Point(cx + rx * Math.Cos(2 * Math.PI * i / 48), cy + ry * Math.Sin(2 * Math.PI * i / 48))).ToList();
        foreach (var dark in new[] { false, true })
        {
            Theme.Configure(dark ? ThemePreference.Dark : ThemePreference.Light, false);
            string Name(string name) => Path.Combine(output, $"{(dark ? "dark" : "light")}-circle-{name}.png");
            var window = Screen.CircleWindow.ForSnapshot(still);
            window.Show();
            try
            {
                await window.SnapshotReadyAsync();
                window.SnapshotHover(new Point(320, 222));
                Save(window.SnapshotImage(), Name("hover"));

                // A loop round the line with a price and a distance: reading, then the wheel on it, then its cards.
                var loop = Loop(420, 323, 370, 30);
                var reading = window.SnapshotStrokeAsync(loop);
                Save(window.SnapshotImage(), Name("reading"));
                await reading;
                var shot = window.SnapshotImage();
                Save(shot, Name("selected"));
                Save(WheelOver(wheel, shot, window.SnapshotPage(), loop[^1]), Name("wheel-money"));
                await window.SnapshotRunAsync("money");
                Save(window.SnapshotImage(), Name("money"));
                await window.SnapshotRunAsync("convert");
                Save(window.SnapshotImage(), Name("convert"));

                // A click on the paragraph, translated where it is.
                await window.SnapshotStrokeAsync([new Point(320, 222)]);
                window.SnapshotTranslation(["على بُعد ساعتين من المدينة، في الميناء القديم مقاهٍ ورحلات بالقوارب وسوق كل صباح سبت. الممر حول البحيرة سهل ومظلّل، ومناظر الغروب تستحق المشي."]);
                Save(window.SnapshotImage(), Name("translate"));
                window.SnapshotAnswer("sparkles", "Explanation", "A short travel note: a small lake town two hours from the city, with an old harbour, cafés, boat trips and a Saturday market. The walk round the lake is easy and shady, best at sunset.", ask: false);
                Save(window.SnapshotImage(), Name("explain"));

                // The date with its zone: the wheel, and the time where the reader is.
                var date = Loop(420, 365, 370, 30);
                await window.SnapshotStrokeAsync(date);
                shot = window.SnapshotImage();
                Save(WheelOver(wheel, shot, window.SnapshotPage(), date[^1]), Name("wheel-date"));
                await window.SnapshotRunAsync("time");
                Save(window.SnapshotImage(), Name("time"));

                // A colour code, and the Arabic paragraph.
                await window.SnapshotStrokeAsync(Loop(240, 407, 190, 26));
                await window.SnapshotRunAsync("colour");
                Save(window.SnapshotImage(), Name("colour"));
                await window.SnapshotStrokeAsync([new Point(600, 484)]);
                shot = window.SnapshotImage();
                Save(shot, Name("arabic"));
                File.WriteAllText(Path.Combine(output, "arabic-words.txt"), window.SnapshotWords() + Environment.NewLine + "screen:" + Environment.NewLine + window.SnapshotScreenWords(460, 510));
                if (window.HasContent)
                    Save(WheelOver(wheel, shot, window.SnapshotPage(), new Point(600, 484)), Name("wheel-arabic"));
                window.SnapshotAnswer("ask", "Ask About It", "", ask: true);
                Save(window.SnapshotImage(), Name("ask"));

                // A QR code: what it says comes first.
                await window.SnapshotStrokeAsync(Loop(180, 655, 120, 115));
                shot = window.SnapshotImage();
                if (window.HasContent)
                    Save(WheelOver(wheel, shot, window.SnapshotPage(), new Point(180, 655)), Name("wheel-qr"));

                // A click on the photo: the picture's petals, and its colours.
                await window.SnapshotStrokeAsync([new Point(1250, 210)]);
                shot = window.SnapshotImage();
                Save(shot, Name("picture"));
                if (window.HasContent)
                {
                    Save(WheelOver(wheel, shot, window.SnapshotPage(), new Point(1250, 210)), Name("wheel-picture"));
                    await window.SnapshotRunAsync("colours");
                    Save(window.SnapshotImage(), Name("colours"));
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>The wheel drawn over the part of a snapshot round <paramref name="at"/>, as it opens there.</summary>
    private static BitmapSource WheelOver(WheelWindow wheel, BitmapSource shot, RingPage page, Point at)
    {
        int side = (int)WheelWindow.Side;
        int x = Math.Clamp((int)at.X - side / 2, 0, shot.PixelWidth - side), y = Math.Clamp((int)at.Y - side / 2, 0, shot.PixelHeight - side);
        var behind = new CroppedBitmap(shot, new Int32Rect(x, y, side, side));
        behind.Freeze();
        return wheel.Snapshot([], WheelMode.Conversions, null, behind, pages: [page], page: 0, keyboard: true);
    }

    /// <summary>A page as a browser shows it: a heading, a paragraph, a price and a distance, a date with its zone, a colour, Arabic, and a photo.</summary>
    private static BitmapSource CircleSample(string photo)
    {
        const int width = 1600, height = 900;
        var visual = new DrawingVisual();
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        using (var dc = visual.RenderOpen())
        {
            void Write(string text, double x, double y, double size, Color colour, bool bold = false, bool rtl = false, double maxWidth = 0)
            {
                var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var formatted = new FormattedText(text, rtl ? System.Globalization.CultureInfo.GetCultureInfo("ar") : culture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, face, size, new SolidColorBrush(colour), 1);
                if (maxWidth > 0)
                    formatted.MaxTextWidth = maxWidth;
                dc.DrawText(formatted, new Point(x, y));
            }
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF9)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)), null, new Rect(0, 0, width, 58));
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(120, 13, 900, 32), 16, 16);
            Write("https://dropwheel.app/stories/lake-town", 142, 19, 15, Color.FromRgb(0x55, 0x55, 0x60));
            Write("A quiet lake town for the weekend", 80, 108, 36, Color.FromRgb(0x14, 0x14, 0x1A), bold: true);
            string[] paragraph =
            [
                "Two hours from the city, the old harbour has cafés, boat trips and a market",
                "every Saturday morning. The trail around the lake is easy and shaded, and",
                "the views at sunset are worth the walk.",
            ];
            for (int i = 0; i < paragraph.Length; i++)
                Write(paragraph[i], 80, 185 + i * 32, 21, Color.FromRgb(0x33, 0x33, 0x3C));
            Write("Rooms from $1,299 a week · the hotel is 3.5 km from the station", 80, 310, 21, Color.FromRgb(0x1A, 0x1A, 0x22));
            Write("Live Q&A on Nov 5, 2026 at 3:00 PM PST · call +1 415 555 0134", 80, 352, 21, Color.FromRgb(0x1A, 0x1A, 0x22));
            Write("Theme colour: #7C5CFC", 80, 394, 21, Color.FromRgb(0x7C, 0x5C, 0xFC));
            Write("يقع الفندق على ضفاف البحيرة، وتبدأ الرحلات اليومية من الساعة التاسعة صباحًا، وتنتهي قبل الغروب بقليل.", 80, 470, 22, Color.FromRgb(0x1A, 0x1A, 0x22), rtl: true, maxWidth: 820);
            // A QR code, as pages print them.
            var modules = Petal.Engines.Imaging.QrGenerator.Encode("https://dropwheel.app");
            int n = modules.GetLength(0);
            dc.DrawRectangle(Brushes.White, null, new Rect(80, 560, (n + 8) * 5, (n + 8) * 5));
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (modules[y, x])
                        dc.DrawRectangle(Brushes.Black, null, new Rect(80 + (x + 4) * 5, 560 + (y + 4) * 5, 5, 5));
            Write("Scan for the app", 80 + (n + 8) * 5 + 16, 560 + (n + 8) * 5 / 2 - 12, 18, Color.FromRgb(0x44, 0x44, 0x4C));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(photo));
            image.EndInit();
            dc.DrawRoundedRectangle(new ImageBrush(image) { Stretch = Stretch.UniformToFill }, null, new Rect(980, 108, 540, 340), 10, 10);
            Write("The harbour at dusk", 980, 458, 15, Color.FromRgb(0x70, 0x70, 0x78));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    /// <summary>Tool windows added later register here so the snapshot run covers them.</summary>
    private static string _snapshotFolder = "";

    /// <summary>A form in the snapshots' folder, with one field of each kind and a few lines of its own text: a visa application, say.</summary>
    private static string SamplePdfForm()
    {
        var path = Path.Combine(_snapshotFolder, "sample-form.pdf");
        Petal.Engines.Documents.Pdf.PdfSampleForm.Write(path);
        return path;
    }

    private static void SceneForm(Tools.Windows.PdfEditorWindow window)
    {
        foreach (var (name, value) in new[] { ("name", "Yahya Abo Hashem"), ("native", "يحيى أبو هاشم"), ("address", "Kadikoy, Istanbul"), ("country", "TR") })
            if (window.ControlsOf(name) is [System.Windows.Controls.TextBox box, ..])
                box.Text = value;
            else if (window.ControlsOf(name) is [System.Windows.Controls.ComboBox combo, ..])
                combo.SelectedIndex = 0;
        var agree = window.Session!.Fields.First(f => f.Name == "agree");
        window.Toggle(agree, agree.Widgets[0].OnState!);
        window.ZoomForSnapshot(0.95);
    }

    private static void SceneMarks(Tools.Windows.PdfEditorWindow window)
    {
        window.SelectTool(Tools.Windows.PdfTool.Select);
        foreach (var item in new Petal.Engines.Documents.Pdf.PdfPageItem[]
        {
            new Petal.Engines.Documents.Pdf.PdfTextItem(0, new Petal.Engines.Documents.Pdf.PdfArea(330, 180, 220, 18), "Written anywhere", 13, 0xE02020, true),
            new Petal.Engines.Documents.Pdf.PdfTextItem(0, new Petal.Engines.Documents.Pdf.PdfArea(330, 205, 220, 18), "نص مكتوب في أي مكان", 13, 0x1D2B6B),
            new Petal.Engines.Documents.Pdf.PdfTextItem(0, new Petal.Engines.Documents.Pdf.PdfArea(330, 230, 120, 16), Tools.Windows.PdfEditorWindow.TodayText(), 12, 0),
            new Petal.Engines.Documents.Pdf.PdfMarkItem(0, new Petal.Engines.Documents.Pdf.PdfArea(330, 255, 16, 16), Petal.Engines.Documents.Pdf.PdfMark.Check, 0x1D2B6B),
            new Petal.Engines.Documents.Pdf.PdfMarkItem(0, new Petal.Engines.Documents.Pdf.PdfArea(355, 255, 16, 16), Petal.Engines.Documents.Pdf.PdfMark.Cross, 0xE02020),
            new Petal.Engines.Documents.Pdf.PdfMarkItem(0, new Petal.Engines.Documents.Pdf.PdfArea(380, 255, 16, 16), Petal.Engines.Documents.Pdf.PdfMark.Dot, 0x1D2B6B),
            new Petal.Engines.Documents.Pdf.PdfMarkupItem(0, Petal.Engines.Documents.Pdf.PdfMarkup.Highlight, [new Petal.Engines.Documents.Pdf.PdfArea(72, 68, 150, 16)], 0xFFD23F),
            new Petal.Engines.Documents.Pdf.PdfMarkupItem(0, Petal.Engines.Documents.Pdf.PdfMarkup.Underline, [new Petal.Engines.Documents.Pdf.PdfArea(240, 68, 110, 16)], 0xE02020),
            new Petal.Engines.Documents.Pdf.PdfNoteItem(0, new Petal.Engines.Documents.Pdf.PdfArea(500, 60, 22, 22), "Check this", 0xFFD23F),
            new Petal.Engines.Documents.Pdf.PdfLineItem(0, 330, 290, 460, 330, true, 2, 0xE02020),
            new Petal.Engines.Documents.Pdf.PdfShapeItem(0, new Petal.Engines.Documents.Pdf.PdfArea(330, 345, 70, 40), false, 2, 0xE02020),
            new Petal.Engines.Documents.Pdf.PdfShapeItem(0, new Petal.Engines.Documents.Pdf.PdfArea(410, 345, 70, 40), true, 2, 0xE02020),
            new Petal.Engines.Documents.Pdf.PdfInkItem(0, [[(330, 410), (360, 395), (390, 420), (420, 395), (450, 410)]], 2.5, 0x1D2B6B),
        })
            window.AddForSnapshot(item);
        window.SelectForSnapshot(window.Items[0]);
        window.ZoomForSnapshot(0.95);
    }

    private static void SceneRedact(Tools.Windows.PdfEditorWindow window)
    {
        window.SelectTool(Tools.Windows.PdfTool.Redact);
        window.ZoomForSnapshot(0.95);
        _ = window.FindPrivateAsync();
    }

    private static IEnumerable<(string, Func<Window>)> ExtraToolCases(Func<string, string> file, Jobs.JobRunner jobs, Input.ScreenPoint at)
    {
        Petal.Core.Tools.ToolInfo T(Petal.Core.Tools.ToolId id) => Petal.Core.Tools.ToolRegistry.Get(id);
        var video = new[] { file("weekend.mov") };
        Window Shorts(Action<Tools.Windows.MakeShortsWindow> then)
        {
            var window = new Tools.Windows.MakeShortsWindow(T(Petal.Core.Tools.ToolId.MakeShorts), video, jobs, at);
            window.Loaded += (_, _) => then(window);
            return window;
        }
        Window Dub(Action<Tools.Windows.DubWindow> then)
        {
            var window = new Tools.Windows.DubWindow(T(Petal.Core.Tools.ToolId.Dub), video, jobs, at);
            window.Loaded += (_, _) => then(window);
            return window;
        }
        Window EditByText(Action<Tools.Windows.EditByTextWindow> then)
        {
            var window = new Tools.Windows.EditByTextWindow(T(Petal.Core.Tools.ToolId.EditByText), video, jobs, at);
            window.Loaded += (_, _) => then(window);
            return window;
        }
        Window Studio(Action<Screen.Studio.StudioWindow> then, bool tall = false) =>
            Screen.Studio.StudioWindow.ForSnapshot(SampleStudio(file("weekend.mp4"), tall), SampleScreen(), SamplePeaks(), then);
        Window Ask(Action<Tools.Windows.AskFileWindow> then)
        {
            var window = new Tools.Windows.AskFileWindow(T(Petal.Core.Tools.ToolId.AskFile), file("album.pdf"), jobs, at);
            window.Loaded += (_, _) => then(window);
            return window;
        }
        var audio = new[] { file("mixtape.mp3") };
        // The sample folder itself, dropped as a folder.
        var folder = Petal.Core.Jobs.FolderContents.Read([Path.GetDirectoryName(file("landscape.jpg"))!]);
        return
        [
            ("organizefolder", () => new Tools.Windows.OrganizeFolderWindow(T(Petal.Core.Tools.ToolId.OrganizeFolder), folder, jobs, at)),
            ("folderpdf", () => new Tools.Windows.FolderPdfWindow(T(Petal.Core.Tools.ToolId.FolderPdf), folder, jobs, at)),
            ("contactsheet", () => new Tools.Windows.ContactSheetWindow(T(Petal.Core.Tools.ToolId.ContactSheet), folder, jobs, at)),
            ("folderreport", () => new Tools.Windows.FolderReportWindow(T(Petal.Core.Tools.ToolId.FolderReport), folder, jobs, at)),
            ("gather", () => new Tools.Windows.GatherFilesWindow(T(Petal.Core.Tools.ToolId.GatherFiles), folder, jobs, at)),
            ("compress", () => new Tools.Windows.CompressWindow(T(Petal.Core.Tools.ToolId.Compress), video, jobs, at)),
            ("trimvideo", () => new Tools.Windows.TrimVideoWindow(T(Petal.Core.Tools.ToolId.TrimVideo), video, jobs, at)),
            ("cropvideo", () => new Tools.Windows.CropVideoWindow(T(Petal.Core.Tools.ToolId.CropVideo), video, jobs, at)),
            ("speed", () => new Tools.Windows.ChangeSpeedWindow(T(Petal.Core.Tools.ToolId.ChangeSpeed), video, jobs, at)),
            ("snapshots", () => new Tools.Windows.VideoSnapshotsWindow(T(Petal.Core.Tools.ToolId.VideoSnapshots), video, jobs, at)),
            ("split", () => new Tools.Windows.SplitVideoWindow(T(Petal.Core.Tools.ToolId.SplitVideo), video, jobs, at)),
            ("redactvideo", () => new Tools.Windows.RedactVideoWindow(T(Petal.Core.Tools.ToolId.RedactVideo), video, jobs, at)),
            ("normalize", () => new Tools.Windows.NormalizeWindow(T(Petal.Core.Tools.ToolId.NormalizeAudio), audio, jobs, at)),
            ("trimaudio", () => new Tools.Windows.TrimAudioWindow(T(Petal.Core.Tools.ToolId.TrimAudio), audio, jobs, at)),
            ("channels", () => new Tools.Windows.AudioChannelsWindow(T(Petal.Core.Tools.ToolId.AudioChannels), audio, jobs, at)),
            ("visualizer", () => new Tools.Windows.VisualizerWindow(T(Petal.Core.Tools.ToolId.AudioVisualizer), audio, jobs, at)),
            ("bleep", () => new Tools.Windows.BleepWindow(T(Petal.Core.Tools.ToolId.BleepAudio), audio, jobs, at)),
            ("editphoto", () => new Tools.Windows.EditPhotoWindow(T(Petal.Core.Tools.ToolId.EditPhoto), file("landscape.jpg"), jobs, at)),
            ("cropimage", () => new Tools.Windows.CropImageWindow(T(Petal.Core.Tools.ToolId.CropImage), [file("figures.png")], jobs, at)),
            ("redactphoto", () => new Tools.Windows.RedactPhotoWindow(T(Petal.Core.Tools.ToolId.RedactPhoto), file("landscape.jpg"), jobs, at)),
            ("organizepdf", () => new Tools.Windows.OrganizePdfWindow(T(Petal.Core.Tools.ToolId.OrganizePdf), file("album.pdf"), jobs, at)),
            ("resize", () => new Tools.Windows.ResizeImageWindow(T(Petal.Core.Tools.ToolId.ResizeImage), [file("landscape.jpg")], jobs, at)),
            ("resizebatch", () => new Tools.Windows.ResizeImageWindow(T(Petal.Core.Tools.ToolId.ResizeImage), [file("landscape.jpg"), file("figures.png")], jobs, at)),
            ("watermark", () => new Tools.Windows.WatermarkWindow(T(Petal.Core.Tools.ToolId.Watermark), [file("landscape.jpg")], jobs, at)),
            ("watermarkpdf", () => new Tools.Windows.WatermarkWindow(T(Petal.Core.Tools.ToolId.Watermark), [file("album.pdf")], jobs, at)),
            ("password", () => new Tools.Windows.PdfPasswordWindow(T(Petal.Core.Tools.ToolId.ProtectPdf), [file("album.pdf")], jobs, at)),
            ("pagenumbers", () => new Tools.Windows.PageNumbersWindow(T(Petal.Core.Tools.ToolId.NumberPages), [file("album.pdf")], jobs, at)),
            ("signpdf", () => new Tools.Windows.SignPdfWindow(T(Petal.Core.Tools.ToolId.SignPdf), file("album.pdf"), jobs, at)),
            ("editpdf", () => Tools.Windows.PdfEditorWindow.ForSnapshot(T(Petal.Core.Tools.ToolId.EditPdf), SamplePdfForm(), jobs, at, SceneForm)),
            ("editpdfmarks", () => Tools.Windows.PdfEditorWindow.ForSnapshot(T(Petal.Core.Tools.ToolId.EditPdf), SamplePdfForm(), jobs, at, SceneMarks)),
            ("editpdfredact", () => Tools.Windows.PdfEditorWindow.ForSnapshot(T(Petal.Core.Tools.ToolId.EditPdf), SamplePdfForm(), jobs, at, SceneRedact)),
            ("zip", () => new Tools.Windows.ZipWindow([file("landscape.jpg"), file("album.pdf")], jobs, at)),
            ("rename", () => new Tools.Windows.RenameWindow([file("landscape.jpg"), file("figures.png"), file("weekend.mov")], at)),
            ("qr", () => (Window)typeof(Tools.Windows.QrWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null)),
            ("removebg", () => new Tools.Windows.RemoveBackgroundWindow(T(Petal.Core.Tools.ToolId.RemoveBackground), [file("figures.png")], jobs, at)),
            ("transcribe", () => new Tools.Windows.TranscribeWindow(T(Petal.Core.Tools.ToolId.Transcribe), video, jobs, at)),
            ("history", () => { SampleHistory(file); return Private<Tools.Windows.HistoryWindow>(); }),
            ("favourite", () => new Settings.FavouriteWindow(new Favourite { Format = "webp", LongestSide = 1600, Quality = 80 }, _ => { }, _ => { })),
            ("automation", () => new Settings.AutomationWindow(new WatchRule { Folder = Path.GetDirectoryName(file("landscape.jpg"))!, Kind = WatchedKind.Pictures, Target = "jpg" }, _ => { }, _ => { })),
            ("automationsort", () => new Settings.AutomationWindow(new WatchRule { Folder = Path.GetDirectoryName(file("landscape.jpg"))!, Kind = WatchedKind.Any, Action = WatchAction.Organize }, _ => { }, _ => { })),
            ("recipe", () => new Settings.RecipeWindow(SampleRecipe(), _ => { }, _ => { })),
            ("scan", () => Actions.ScanWindow.ForSnapshot()),
            ("denoise", () => new Tools.Windows.RemoveNoiseWindow(T(Petal.Core.Tools.ToolId.RemoveNoise), audio, jobs, at)),
            ("separate", () => new Tools.Windows.SeparateVocalsWindow(T(Petal.Core.Tools.ToolId.SeparateVocals), audio, jobs, at)),
            ("erase", () => new Tools.Windows.EraseObjectWindow(T(Petal.Core.Tools.ToolId.EraseObject), file("landscape.jpg"), jobs, at)),
            ("translate", () => new Tools.Windows.TranslateSubtitlesWindow(T(Petal.Core.Tools.ToolId.TranslateSubtitles), [file("weekend.srt")], jobs, at)),
            ("translatedocument", () => new Tools.Windows.TranslateDocumentWindow(T(Petal.Core.Tools.ToolId.TranslateDocument), [file("album.pdf")], jobs, at)),
            ("exportlayers", () => new Tools.Windows.ExportLayersWindow(T(Petal.Core.Tools.ToolId.ExportLayers), [file("layers.psd")], jobs, at)),
            ("fileinfo", () => new Tools.Windows.FileDetailsWindow(T(Petal.Core.Tools.ToolId.FileDetails), file("layers.psd"), jobs, at)),
            ("fileinfoidml", () => new Tools.Windows.FileDetailsWindow(T(Petal.Core.Tools.ToolId.FileDetails), file("leaflet.idml"), jobs, at)),
            ("collect", () => new Tools.Windows.CollectFilesWindow(T(Petal.Core.Tools.ToolId.CollectFiles), file("leaflet.idml"), jobs, at)),
            ("palette", () => new Tools.Windows.PaletteWindow(T(Petal.Core.Tools.ToolId.Palette), file("brand.ase"), jobs, at)),
            ("colours", () => new Tools.Windows.ExtractColoursWindow(T(Petal.Core.Tools.ToolId.ExtractColours), file("landscape.jpg"), jobs, at)),
            ("readaloud", () => new Tools.Windows.ReadAloudWindow(T(Petal.Core.Tools.ToolId.ReadAloud), file("album.pdf"), jobs, at)),
            ("petalconfirm", () => Actions.CustomPetalRunner.ConfirmForSnapshot(file("album.pdf"))),
            ("search", () => Search.SearchWindow.ForSnapshot([file("landscape.jpg"), file("figures.png")], [file("album.pdf")])),
            ("ask", () => Tools.Windows.AskWindow.ForSnapshot([file("landscape.jpg"), file("figures.png")], jobs)),
            ("duplicates", () => Tools.Windows.DuplicatesWindow.ForSnapshot([file("landscape.jpg"), file("figures.png"), file("landscape.jpg"), file("figures.png")])),
            ("phone", () => Actions.PhoneWindow.ForSnapshot([file("landscape.jpg"), file("weekend.mov"), file("album.pdf")])),
            ("update", () => (Window)typeof(Shell.UpdateWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(Petal.Core.Util.UpdateManifest)])!
                .Invoke([new Petal.Core.Util.UpdateManifest { Version = "1.1.0", Url = "DropWheel-Setup-1.1.0.exe", Size = 148_000_000, Notes = Localization.L.T("Remove Background") + " · " + Localization.L.T("Transcribe") + " · " + Localization.L.T("Send to Phone") }])),
            ("practice", Private<Onboarding.PracticeWindow>),
            ("stats", () => { SampleStats(); return Private<Stats.StatsWindow>(); }),
            ("wheeleditor", () => new Settings.WheelEditorWindow(Settings.SettingsWindow.Clone(Wheels.WheelTemplates.Apps()[0]))),
            ("wheeleditorquick", () => new Settings.WheelEditorWindow(Wheels.WheelTemplates.Quick())),
            ("entrykeys", () => new Settings.EntryEditorWindow(new Wheels.WheelEntry { Kind = Wheels.EntryKind.Keystroke, Label = "NEW TAB", Name = "New Tab", Icon = "plus", Keys = "Ctrl+T", Tint = "#0090FF" })),
            ("entryscript", () => new Settings.EntryEditorWindow(SampleScript())),
            ("entryfolder", () => new Settings.EntryEditorWindow(new Wheels.WheelEntry { Kind = Wheels.EntryKind.Folder, Path = Path.GetDirectoryName(file("landscape.jpg")) })),
            ("entrygroup", () => new Settings.EntryEditorWindow(Wheels.WheelTemplates.Quick().Items[^1])),
            ("petalpicker", Private<Settings.PetalPickerWindow>),
            ("petalcolours", () => new Settings.PetalColoursWindow()),
            ("openwith", () => (Window)typeof(Actions.OpenWithWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(IReadOnlyList<string>)])!
                .Invoke([new[] { file("weekend.srt") }])),
            ("confirmscript", () => (Window)typeof(Wheels.ConfirmScriptWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(Wheels.WheelEntry), typeof(TaskCompletionSource<bool>)])!
                .Invoke([SampleScript(), new TaskCompletionSource<bool>()])),
            ("trialended", () => (Window)typeof(Shell.TrialEndedWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!
                .Invoke([])),
            ("screentext", () => Screen.ScreenTextWindow.ForSnapshot(SamplePicture(file("figures.png")))),
            ("colourcard", () => new Screen.ColourCard(Color.FromRgb(0x30, 0xA4, 0x6C), Colors.White)),
            ("clipboard", () => { SampleClips(); return Clips.ClipboardWindow.ForSnapshot(); }),
            ("rewrite", () => Words.TextResultWindow.ForSnapshot(Words.TextJob.Rewrite, "Hi team, the report is late again because the numbers came in late, sorry, we will send it tomorrow morning hopefully.")),
            ("translatetext", () => Words.TextResultWindow.ForSnapshot(Words.TextJob.Translate, "The meeting moves to Thursday at ten.")),
            ("dictationsetup", Private<Words.DictationSetupWindow>),
            ("meetingnotes", () => Words.MeetingNotesWindow.ForSnapshot(SampleMeeting())),
            ("focus", () => Screen.FocusWindow.ForSnapshot(["chrome", "Discord", "Spotify", "WhatsApp", "WINWORD"])),
            ("saveworkspace", () => Screen.SaveWorkspaceWindow.ForSnapshot(
            [
                new Screen.WorkspaceWindow { Program = "Code", Title = "petal-site — Visual Studio Code" },
                new Screen.WorkspaceWindow { Program = "chrome", Title = Localization.L.IsArabic ? "لوحة المشروع - Google Chrome" : "Project board - Google Chrome" },
                new Screen.WorkspaceWindow { Program = "Figma", Title = Localization.L.IsArabic ? "الصفحة الرئيسية – Figma" : "Landing page – Figma" },
            ])),
            ("snippets", () =>
            {
                Settings.SettingsStore.Current.Snippets =
                [
                    .. Petal.Core.Text.Snippets.Starters(),
                    new() { Abbreviation = ";addr", Text = Localization.L.IsArabic ? "شارع الملك فهد، الرياض 12271" : "14 Harbour Road, Bristol BS1 5TT" },
                    new() { Abbreviation = ";thx", Text = Localization.L.IsArabic ? "شكرًا جزيلًا {cursor}، مع التحية." : "Thanks so much, {cursor}. Best wishes." },
                ];
                return new Settings.SnippetsWindow();
            }),
            ("gallery", () => new Settings.WheelGalleryWindow(() => { })),
            ("importwheel", () => new Settings.ImportWheelWindow(() => { })),
            ("sendtopc", () => Actions.SendToPcWindow.ForSnapshot([file("landscape.jpg"), file("album.pdf")],
            [
                new Petal.Engines.Files.Sharing.DropPeer(Localization.L.IsArabic ? "حاسوب المكتب" : "Office PC", "a", System.Net.IPAddress.Loopback, 1),
                new Petal.Engines.Files.Sharing.DropPeer(Localization.L.IsArabic ? "لابتوب سارة" : "Sara's laptop", "b", System.Net.IPAddress.Loopback, 1),
            ])),
            ("receive", () => Actions.ReceiveWindow.ForSnapshot(new Petal.Engines.Files.Sharing.DropOffer(Localization.L.IsArabic ? "لابتوب سارة" : "Sara's laptop",
                [new("trip.jpg", 3_400_000), new("report.pdf", 820_000), new(Path.Combine("slides", "deck.pptx"), 12_600_000)]))),
            // 1.8
            ("removepauses", () => new Tools.Windows.RemovePausesWindow(T(Petal.Core.Tools.ToolId.RemovePauses), video, jobs, at)),
            ("animatedcaptions", () => new Tools.Windows.AnimatedCaptionsWindow(T(Petal.Core.Tools.ToolId.AnimatedCaptions), video, jobs, at)),
            ("animatedcaptionsenglish", () => new Tools.Windows.AnimatedCaptionsWindow(T(Petal.Core.Tools.ToolId.AnimatedCaptions), video, jobs, at) { WordsIn = "en" }),
            ("vertical", () => new Tools.Windows.ReframeWindow(T(Petal.Core.Tools.ToolId.Reframe), video, jobs, at)),
            ("describe", () => new Tools.Windows.DescribePictureWindow(T(Petal.Core.Tools.ToolId.DescribePicture), file("landscape.jpg"))),
            // 1.11
            ("shorts", () => new Tools.Windows.MakeShortsWindow(T(Petal.Core.Tools.ToolId.MakeShorts), video, jobs, at)),
            ("shortslistening", () => Shorts(window => window.ShowListeningForSnapshot(0.42, 2735))),
            ("shortsreview", () => Shorts(window => window.ShowForSnapshot(SampleMoments(),
                new Petal.Recipes.ShortsSource(video[0], Localization.L.IsArabic ? "ar" : "en", [], new Petal.Engines.Media.SoundLevels([]))))),
            ("dub", () => new Tools.Windows.DubWindow(T(Petal.Core.Tools.ToolId.Dub), video, jobs, at)),
            ("main0", () => Settings.SettingsWindow.ForPage(0)),
            ("main1", () => Settings.SettingsWindow.ForPage(1)),
            ("main2", () => Settings.SettingsWindow.ForPage(2)),
            ("main3", () => Settings.SettingsWindow.ForPage(3)),
            ("main4", () => Settings.SettingsWindow.ForPage(4)),
            ("main5", () => Settings.SettingsWindow.ForPage(5)),
            ("main6", () => Settings.SettingsWindow.ForPage(6)),
            ("main7", () => Settings.SettingsWindow.ForPage(7)),
            ("main8", () => Settings.SettingsWindow.ForPage(8)),
            ("main9", () => Settings.SettingsWindow.ForPage(9)),
            ("mainsmall", () => Settings.SettingsWindow.ForPage(1, 960, 700)),
            ("mainmid", () => Settings.SettingsWindow.ForPage(5, 1320, 880)),
            ("dubbest", () => Dub(window => window.FinestForSnapshot())),
            ("dubbestmissing", () => Dub(window => window.FinestMissingForSnapshot(false))),
            ("dubbestkey", () => Dub(window => window.FinestMissingForSnapshot(true))),
            ("modelchoice", () => Words.ModelDownloadWindow.ForSnapshot(false)),
            ("modelchoicekey", () => Words.ModelDownloadWindow.ForSnapshot(true)),
            ("dubpreparing", () => Dub(window => window.ShowPreparingForSnapshot(0.31))),
            ("dubreview", () => Dub(window => window.ShowForSnapshot(SampleDub(video[0])))),
            // 1.15
            ("studio", () => Studio(window => window.SeekForSnapshot(3.6))),
            ("studiozoom", () => Studio(window =>
            {
                window.SelectForSnapshot(1, 0);
                window.SeekForSnapshot(9.4);
            })),
            ("studiotall", () => Studio(window =>
            {
                window.SelectForSnapshot(2, 0);
                window.SeekForSnapshot(4.2);
            }, tall: true)),
            ("studioexport", () => Studio(window =>
            {
                window.SeekForSnapshot(9.4);
                window.ExportingForSnapshot(0.42);
            })),
            // 1.14
            ("askfile", () => new Tools.Windows.AskFileWindow(T(Petal.Core.Tools.ToolId.AskFile), file("album.pdf"), jobs, at)),
            ("askfilereading", () => Ask(window => window.ShowReadingForSnapshot(0.46))),
            ("askfileanswers", () => Ask(window => window.ShowForSnapshot(SampleAnswers(), Localization.L.P(70, "{0} page", "{0} pages"), file("album.pdf")))),
            // 1.13
            ("editbytext", () => new Tools.Windows.EditByTextWindow(T(Petal.Core.Tools.ToolId.EditByText), video, jobs, at)),
            ("editbytextlistening", () => EditByText(window => window.ShowListeningForSnapshot(0.58, 1934))),
            ("editbytextwords", () => EditByText(window =>
            {
                var words = SampleWords();
                // The clean-up done and one long pause shortened, as it would look after two clicks and a Delete.
                window.ShowForSnapshot(words, Petal.Engines.Media.TextCut.Fillers(words).Append(words.FindIndex(w => w.Kind == Petal.Engines.Media.EditTokenKind.Pause)),
                    Localization.L.IsArabic ? "ar" : "en");
            })),
            ("health", () => Settings.HealthWindow.ForSnapshot(
            [
                new Settings.HealthCheck(Localization.L.T("Keys and mouse"), Settings.HealthState.Good, Localization.L.T("DropWheel sees drags and key taps")),
                new Settings.HealthCheck(Localization.L.T("Shortcuts"), Settings.HealthState.Problem, Localization.L.F("Another program has {0}", "Ctrl+Win+M"),
                    Localization.L.T("Choose another shortcut in Settings, or close the program that uses it."), Localization.L.T("Open Settings"), () => { }),
                new Settings.HealthCheck(Localization.L.T("AI models"), Settings.HealthState.Note, Localization.L.F("{0} of {1} on this PC · {2}", 3, 9, "1.4 GB")),
                new Settings.HealthCheck(Localization.L.T("Disk space"), Settings.HealthState.Good, Localization.L.F("{0} free on {1}", "212 GB", "C:")),
            ])),
            ("stepguide", () => Screen.StepGuideWindow.ForSnapshot(
            [
                new Screen.GuideStep { Kind = Screen.StepKind.Click, Text = Screen.StepWords.For(Screen.StepKind.Click, "File", "menu item", "Notepad"), Picture = file("landscape.jpg") },
                new Screen.GuideStep { Kind = Screen.StepKind.Click, Text = Screen.StepWords.For(Screen.StepKind.Click, "Save as", "menu item", "Notepad"), Picture = file("figures.png") },
                new Screen.GuideStep { Kind = Screen.StepKind.Type, Text = Screen.StepWords.For(Screen.StepKind.Type, null, null, null, "notes.txt"), Picture = file("landscape.jpg") },
            ])),
            ("quicklook", () => QuickLook(file("landscape.jpg"))),
            ("quicklookpdf", () => QuickLook(file("album.pdf"))),
            ("quicklooktext", () => QuickLook(file("weekend.srt"))),
            ("guide", () => Guide.GuideWindow.ForSnapshot("do:draw.timer", null)),
            // A request in the language Petal speaks: the word for a countdown in it.
            ("guidesearch", () => Guide.GuideWindow.ForSnapshot(null, Localization.L.IsArabic ? "أريد أداة تقوم بمهمة العداد" : Guide.GuideBook.Name("do:draw.timer"))),
        ];
    }

    private static Window QuickLook(string path)
    {
        var window = new Shell.QuickLookWindow(IntPtr.Zero, [path], null);
        window.Loaded += async (_, _) => await window.ShowFirstAsync();
        return window;
    }

    /// <summary>Made-up moments of a long interview, as Make Shorts would find them.</summary>
    private static IReadOnlyList<Petal.Engines.Media.Moment> SampleMoments()
    {
        bool ar = Localization.L.IsArabic;
        (string Text, double Start, double Length, int Score, Petal.Engines.Media.MomentReason[] Why)[] samples = ar
            ?
            [
                ("هل يمكنك أن تأخذني إلى الفضاء؟ أريد أن أطفو وأقلب في الهواء.", 751, 44, 92, [Petal.Engines.Media.MomentReason.Question, Petal.Engines.Media.MomentReason.Feeling]),
                ("الحقيقة مدهشة فعلًا. حين نصل إلى الفضاء نشعر جميعًا بشيء غريب.", 1442, 38, 88, [Petal.Engines.Media.MomentReason.Hook, Petal.Engines.Media.MomentReason.Story]),
                ("ندور حول الأرض كل تسعين دقيقة. رأيت كل شيء من هنا.", 465, 46, 81, [Petal.Engines.Media.MomentReason.Numbers, Petal.Engines.Media.MomentReason.Complete]),
                ("نصيحتي لكل من يحلم بالطيران: ابدأ اليوم ولا تنتظر.", 1870, 42, 74, [Petal.Engines.Media.MomentReason.Advice]),
                ("أول مرة رأيت فيها الأرض من النافذة لم أستطع الكلام.", 2465, 35, 66, [Petal.Engines.Media.MomentReason.Story, Petal.Engines.Media.MomentReason.Feeling]),
            ]
            :
            [
                ("Can you take me to space? I want to float around and do flips.", 751, 44, 92, [Petal.Engines.Media.MomentReason.Question, Petal.Engines.Media.MomentReason.Feeling]),
                ("The truth is actually pretty amazing. When we get into space, we all feel something.", 1442, 38, 88, [Petal.Engines.Media.MomentReason.Hook, Petal.Engines.Media.MomentReason.Story]),
                ("We go around the Earth every 90 minutes. I've seen it all.", 465, 46, 81, [Petal.Engines.Media.MomentReason.Numbers, Petal.Engines.Media.MomentReason.Complete]),
                ("My advice to anyone who dreams of flying: start today.", 1870, 42, 74, [Petal.Engines.Media.MomentReason.Advice]),
                ("The first time I saw the Earth through the window, I couldn't speak.", 2465, 35, 66, [Petal.Engines.Media.MomentReason.Story, Petal.Engines.Media.MomentReason.Feeling]),
            ];
        return samples.Select(s =>
        {
            var parts = s.Text.Split(' ');
            var words = parts.Select((w, i) => new Petal.Engines.Media.SpokenWord(w, s.Start + 0.4 * i, s.Start + 0.4 * i + 0.35)).ToList();
            return new Petal.Engines.Media.Moment(s.Start, s.Start + s.Length, s.Score, s.Why, words);
        }).ToList();
    }

    /// <summary>Made-up lines of an interview, as Dub would prepare them: English into Arabic, or Arabic into English.</summary>
    private static Petal.Recipes.DubSource SampleDub(string video)
    {
        bool ar = Localization.L.IsArabic;
        (double Start, double End, string English, string Arabic)[] lines =
        [
            (0.0, 1.6, "I'm a very lucky person.", "أنا شخص محظوظ جدًا."),
            (1.6, 9.0, "I'm grateful every day, and I want more people to come up here to space.", "أنا ممتن كل يوم، وأريد أن يأتي المزيد من الناس إلى الفضاء."),
            (9.0, 11.0, "We need some good wings up here.", "نحتاج إلى أجنحة جيدة هنا."),
            (11.1, 13.6, "I would love to go. Can you take me to space?", "أحب أن أذهب. هل يمكنك أن تأخذني إلى الفضاء؟"),
            (27.4, 31.8, "I'd look down and say: I'm over Europe, and in twenty seconds I'll be on the West Coast.", "سأنظر إلى الأسفل وأقول: أنا فوق أوروبا، وبعد عشرين ثانية سأكون على الساحل الغربي."),
        ];
        // An Arabic reader sees English dubbed into Arabic; anyone else, Arabic dubbed into English.
        var dub = lines.Select(l => ar ? new Petal.Engines.Media.DubLine(l.Start, l.End, l.English, l.Arabic) : new Petal.Engines.Media.DubLine(l.Start, l.End, l.Arabic, l.English)).ToList();
        return new Petal.Recipes.DubSource(video, ar ? "en" : "ar", ar ? "ar" : "en", "", "", dub, 152);
    }

    /// <summary>Made-up words of an interview, as Edit by Text shows them: hesitations, words said twice, a sound and pauses ("|" a pause, "~" a sound).</summary>
    private static List<Petal.Engines.Media.EditToken> SampleWords()
    {
        var text = Localization.L.IsArabic
            ? "إذن، إمم، أعتقد أن أن أجمل ما في العيش هنا هو المنظر. | كل تسعين دقيقة نرى شروق الشمس. آآ، ولا نملّ منه أبدًا. ~ نصيحتي لكل من يحلم بالطيران: ابدأ اليوم، و و لا تنتظر. | الحقيقة مدهشة فعلًا. حين نصل إلى الفضاء نشعر جميعًا بشيء غريب، شيء يصعب وصفه بالكلمات."
            : "So, um, I think the the best part of living up here is the view. | Every ninety minutes we see a sunrise. Uh, it never gets old. ~ My advice to anyone who dreams of flying: start today, and and don't wait. | The truth is actually pretty amazing. When we get into space, we all feel something strange, something hard to put into words.";
        var words = new List<Petal.Engines.Media.EditToken>();
        double t = 0.4;
        foreach (var part in text.Split(' '))
        {
            var (kind, length) = part switch
            {
                "|" => (Petal.Engines.Media.EditTokenKind.Pause, 1.8),
                "~" => (Petal.Engines.Media.EditTokenKind.Sound, 0.6),
                _ => (Petal.Engines.Media.EditTokenKind.Word, 0.3),
            };
            words.Add(new Petal.Engines.Media.EditToken(kind, kind == Petal.Engines.Media.EditTokenKind.Word ? part : "", t, t + length));
            t += length + 0.08;
        }
        return words;
    }

    /// <summary>
    /// A made-up recording of a notes app: the pointer going to the sidebar, the document and the Share button,
    /// a run of typing, a shortcut, and the zooms DropWheel would plan, with a stretch sped up and one cut out.
    /// </summary>
    private static Petal.Core.Recording.StudioProject SampleStudio(string video, bool tall)
    {
        var stops = new (double T, double X, double Y)[] { (0, 640, 520), (2.4, 150, 214), (3, 150, 214), (5.2, 520, 300), (6.6, 520, 300), (11.5, 540, 340), (12.6, 1150, 76), (14, 1150, 76), (17, 900, 600), (24, 900, 600) };
        var pointer = new List<Petal.Core.Recording.PointerSample>();
        for (int i = 0; i <= 24 * 60; i++)
        {
            double t = i / 60.0;
            int k = 0;
            while (k + 1 < stops.Length && stops[k + 1].T <= t)
                k++;
            var a = stops[k];
            var b = stops[Math.Min(k + 1, stops.Length - 1)];
            double f = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 0;
            f = f * f * (3 - 2 * f);
            var kind = t is > 6.6 and < 11.5 ? Petal.Core.Recording.PointerKind.Text : t is > 12.5 and < 14.2 ? Petal.Core.Recording.PointerKind.Hand : Petal.Core.Recording.PointerKind.Arrow;
            pointer.Add(new(t, a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, true, kind));
        }
        var typing = Enumerable.Range(0, 18).Select(i => new Petal.Core.Recording.TypingSample(7 + i * 0.22, 560 + i * 9, 330, true)).ToList();
        var project = new Petal.Core.Recording.StudioProject
        {
            Folder = Path.Combine(Path.GetTempPath(), "DropWheel studio snapshot"),
            Video = Path.GetFullPath(video),
            Width = 1280,
            Height = 720,
            Fps = 30,
            Duration = 24,
            HasPointer = true,
            Pointer = pointer,
            Clicks = [new(3, 150, 214), new(6.6, 520, 300), new(14, 1150, 76)],
            Typing = typing,
            Keys = [new(11.2, "Ctrl + S")],
        };
        var look = new Petal.Core.Recording.StudioLook { Shape = tall ? "9:16" : "recorded" };
        project.Edit = project.Automatic(look, new Petal.Core.Recording.StudioOutput(), 1.8) with
        {
            Edits = [new(15, 17.5, Petal.Core.Recording.EditKind.Speed, 4), new(20, 21.5, Petal.Core.Recording.EditKind.Cut)],
        };
        return project;
    }

    /// <summary>A notes app as a screen recording would show it.</summary>
    private static BitmapSource SampleScreen()
    {
        var visual = new DrawingVisual();
        var face = new Typeface(Visuals.UiFonts.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(Visuals.UiFonts.Text, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        using (var dc = visual.RenderOpen())
        {
            void Write(string text, double x, double y, double size, Color colour, bool strong = false) =>
                dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, strong ? bold : face, size, new SolidColorBrush(colour), 1), new Point(x, y));
            Brush Fill(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
            dc.DrawRectangle(Fill(0xF4, 0xF5, 0xF7), null, new Rect(0, 0, 1280, 720));
            dc.DrawRectangle(Fill(0xFF, 0xFF, 0xFF), null, new Rect(0, 0, 1280, 44));
            Write("Notes — Quarterly plan", 18, 12, 14, Color.FromRgb(0x33, 0x33, 0x3A));
            foreach (var (x, glyph) in new[] { (1150.0, "—"), (1196.0, "☐"), (1242.0, "✕") })
                Write(glyph, x, 11, 15, Color.FromRgb(0x55, 0x55, 0x5E));
            dc.DrawRectangle(Fill(0xFF, 0xFF, 0xFF), null, new Rect(0, 44, 250, 676));
            dc.DrawRectangle(Fill(0xE6, 0xE7, 0xEA), null, new Rect(250, 44, 1, 676));
            string[] items = ["Inbox", "Projects", "Quarterly plan", "Ideas", "Meeting notes", "Archive"];
            for (int i = 0; i < items.Length; i++)
            {
                double y = 78 + i * 46;
                if (i == 2)
                    dc.DrawRoundedRectangle(Fill(0xEC, 0xE8, 0xFF), null, new Rect(12, y - 10, 226, 40), 8, 8);
                dc.DrawRoundedRectangle(i == 2 ? Fill(0x7C, 0x5C, 0xFC) : Fill(0xC9, 0xCC, 0xD3), null, new Rect(28, y + 2, 16, 16), 4, 4);
                Write(items[i], 56, y, 15, i == 2 ? Color.FromRgb(0x3B, 0x2A, 0xA8) : Color.FromRgb(0x33, 0x33, 0x3A), i == 2);
            }
            dc.DrawRoundedRectangle(Fill(0x7C, 0x5C, 0xFC), null, new Rect(1100, 60, 104, 34), 8, 8);
            Write("Share", 1130, 66, 15, Colors.White, true);
            dc.DrawRoundedRectangle(Fill(0xFF, 0xFF, 0xFF), new Pen(Fill(0xE6, 0xE7, 0xEA), 1), new Rect(290, 112, 950, 580), 12, 12);
            Write("Quarterly plan", 330, 140, 30, Color.FromRgb(0x1A, 0x1A, 0x22), true);
            Write("Launch the new reading lamp in March, with the shops and the website ready the same week.", 330, 200, 16, Color.FromRgb(0x44, 0x44, 0x4C));
            string[] tasks = ["Photos for the website", "Price list for the shops", "Two short videos for social media", "Answers for the support team"];
            for (int i = 0; i < tasks.Length; i++)
            {
                double y = 252 + i * 40;
                dc.DrawRoundedRectangle(i < 2 ? Fill(0x22, 0xC5, 0x5E) : null, new Pen(i < 2 ? Fill(0x22, 0xC5, 0x5E) : Fill(0xB8, 0xBB, 0xC4), 1.6), new Rect(332, y + 2, 18, 18), 5, 5);
                Write(tasks[i], 364, y, 16, i < 2 ? Color.FromRgb(0x8A, 0x8D, 0x96) : Color.FromRgb(0x2A, 0x2A, 0x33));
            }
            Write("Budget", 330, 432, 20, Color.FromRgb(0x1A, 0x1A, 0x22), true);
            double[] bars = [0.42, 0.7, 0.55, 0.9];
            for (int i = 0; i < bars.Length; i++)
                dc.DrawRoundedRectangle(Fill(0x7C, 0x5C, 0xFC), null, new Rect(334 + i * 90, 640 - 150 * bars[i], 56, 150 * bars[i]), 6, 6);
            Write("The marketing budget is 250,000 euros, half of it for online adverts.", 720, 480, 16, Color.FromRgb(0x44, 0x44, 0x4C));
            Write("Next review: Thursday at 10:00.", 720, 516, 16, Color.FromRgb(0x44, 0x44, 0x4C));
        }
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A voice's waveform, made up.</summary>
    private static float[] SamplePeaks()
    {
        var random = new Random(5);
        return Enumerable.Range(0, 1600).Select(i =>
        {
            double t = i / 1600.0 * 24;
            double speaking = Math.Sin(t * 1.7) > -0.3 ? 1 : 0.15;
            return (float)Math.Min(1, speaking * (0.25 + random.NextDouble() * 0.7));
        }).ToArray();
    }

    /// <summary>Questions about a declaration of rights, as Ask a File answers them, each with the page it comes from.</summary>
    private static IReadOnlyList<(string, string, IReadOnlyList<Petal.Recipes.Questions.Passage>)> SampleAnswers()
    {
        static Petal.Recipes.Questions.Passage On(int page, string text) =>
            new(page, new Petal.Engines.Documents.PlacedText(Petal.Engines.Documents.PlaceKind.Page, page, text), text);
        return Localization.L.IsArabic
            ?
            [
                ("هل التعليم الابتدائي إلزامي؟", "نعم. التعليم الابتدائي إلزامي، ويجب أن يكون مجانيًا في مراحله الأولى والأساسية على الأقل.",
                    [On(1, "لكل شخص الحق في التعلم. ويجب أن يكون التعليم في مراحله الأولى والأساسية على الأقل بالمجان، وأن يكون التعليم الأولي إلزاميًا.")]),
                ("متى اعتُمد هذا الإعلان؟", "اعتمدته الجمعية العامة للأمم المتحدة في باريس في 10 ديسمبر 1948.",
                    [On(2, "اعتمدت الجمعية العامة للأمم المتحدة الإعلان العالمي لحقوق الإنسان في باريس في 10 كانون الأول/ديسمبر 1948."), On(4, "في 10 كانون الأول/ديسمبر 1948 اعتمدت الجمعية العامة هذا الإعلان.")]),
            ]
            :
            [
                ("Is elementary education compulsory?", "Yes. Elementary education is compulsory, and it must be free, at least in the elementary and fundamental stages.",
                    [On(1, "Everyone has the right to education. Education shall be free, at least in the elementary and fundamental stages. Elementary education shall be compulsory.")]),
                ("When was this declaration adopted?", "It was adopted by the United Nations General Assembly in Paris on 10 December 1948.",
                    [On(2, "The United Nations General Assembly adopted the Universal Declaration of Human Rights in Paris on 10 December 1948."), On(4, "On 10 December 1948 the General Assembly adopted this declaration.")]),
            ];
    }

    /// <summary>Made-up meeting notes in a scratch file.</summary>
    private static string SampleMeeting()
    {
        var path = Path.Combine(Path.GetTempPath(), "petal-snapshot-meeting.md");
        bool ar = Localization.L.IsArabic;
        var summary = ar
            ? "## الملخص\nناقش الفريق موعد إطلاق الموقع الجديد واتفق على تأجيله أسبوعًا لإنهاء الترجمة.\n\n## القرارات\n- الإطلاق يوم 12 مايو\n- تُراجع سارة النصوص العربية\n\n## المهام\n- سارة: مراجعة الترجمة قبل الخميس\n- أحمد: تجهيز صور المتجر"
            : "## Summary\nThe team discussed the launch of the new site and agreed to move it a week to finish the translation.\n\n## Decisions\n- Launch on 12 May\n- Sara reviews the Arabic text\n\n## Action items\n- Sara: review the translation by Thursday\n- Omar: prepare the store pictures";
        var transcript = ar
            ? "[00:00] صباح الخير، نبدأ بموعد الإطلاق.\n[00:06] الترجمة لم تنتهِ بعد."
            : "[00:00] Morning all, let's start with the launch date.\n[00:06] The translation isn't finished yet.";
        File.WriteAllText(path, Petal.Recipes.MeetingNotes.Markdown(new DateTime(2026, 5, 3, 10, 0, 0), TimeSpan.FromMinutes(32), summary, transcript, ar));
        return path;
    }

    /// <summary>The floating windows of the screen tools, each rendered over a plain backdrop.</summary>
    private static IEnumerable<(string Name, Func<Window> Create)> FloatingCases() =>
    [
        ("drawbar", () => new Screen.DrawToolbar(Screen.DrawOverlay.ForSnapshot())),
        ("colourpanel", () => Tools.Parts.ColourWindow.ForSnapshot(Color.FromRgb(0x2E, 0x8B, 0xC4), [Colors.Black, Colors.White, Color.FromRgb(0xEB, 0x4D, 0x47), Color.FromRgb(0xFF, 0xD2, 0x3F), Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0x3B, 0x82, 0xF6)])),
        ("progresscard", () => Hud.HudCard.ProgressForSnapshot("Adding captions", "lesson.mp4 · 37%", 0.37)),
        ("savedshot", () => Hud.HudCard.SavedForSnapshot("Screenshot saved and copied", Path.Combine(Wheels.Screenshots.Folder, "Screenshot 2026-10-23 09.41.12.png"))),
        ("guidecard", () => Guide.GuideCard.ForSnapshot("tool:Compress")),
        ("savedrecording", () => Hud.HudCard.SavedForSnapshot("Recording saved", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Recording 2026-10-23 09.42.30.mp4"), Localization.L.T("The camera couldn't be added; the recording is saved without it."))),
        ("timer", Private<Screen.TimerWindow>),
        ("timerrunning", () => Screen.TimerWindow.ForSnapshot(TimeSpan.FromMinutes(5))),
        ("recordbar", () => new Screen.RecordBar((Screen.RecordingSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Screen.RecordingSession)))),
        ("dictation", () => new Words.DictationPill(IntPtr.Zero)),
        ("palette", () => Commands.CommandPalette.ForSnapshot("", [])),
        ("palettequery", () => Commands.CommandPalette.ForSnapshot(Localization.L.IsArabic ? "تسجيل" : "rec", [])),
        ("captions", () =>
        {
            var captions = new Words.CaptionWindow();
            if (Localization.L.IsArabic)
                captions.Show("We'll move the launch to next Thursday.", "سننقل الإطلاق إلى الخميس القادم.", "وسنخبر الفريق");
            else
                captions.Show("سننقل الإطلاق إلى الخميس القادم.", "We'll move the launch to next Thursday.", "And we'll tell the team");
            return captions;
        }),
        ("focuspill", () => new Screen.FocusPill()),
        ("meetingpill", () => new Words.MeetingPill()),
        ("note", () => new Notes.NoteWindow(new Notes.StickyNote
        {
            Text = Localization.L.IsArabic
                ? "اتصل بسارة قبل الخامسة" + Environment.NewLine + "أرسل العرض المحدّث"
                : "Call Sara before five" + Environment.NewLine + "Send the updated slides",
            Colour = "yellow",
        })),
    ];

    /// <summary>A window that sizes to its content, rendered on a backdrop the colour of a desktop.</summary>
    private static BitmapSource RenderFloating(Window window, bool dark)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var frame = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x30, 0x3A) : Color.FromRgb(0xC9, 0xD3, 0xDF)),
            Padding = new Thickness(24),
            Child = content,
            FlowDirection = window.FlowDirection,
        };
        // Out of its window the content loses what the window gave it, such as Western digits in Arabic.
        Localization.L.Apply(frame, mirror: false);
        if (!double.IsNaN(window.Width) && window.SizeToContent != SizeToContent.WidthAndHeight)
        {
            content.Width = window.Width;
            content.Height = window.Height;
        }
        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = frame.DesiredSize;
        frame.Arrange(new Rect(size));
        var host = new System.Windows.Controls.Border { Child = frame };
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A picture from the test data, as the screen tools are given one.</summary>
    private static BitmapSource SamplePicture(string path)
    {
        var image = Load(path);
        int w = Math.Min(image.PixelWidth, 520), h = Math.Min(image.PixelHeight, 220);
        var crop = new CroppedBitmap(image, new Int32Rect(0, 0, w, h));
        crop.Freeze();
        return crop;
    }

    /// <summary>A few made-up clips, so the clipboard snapshots never show what the user copied.</summary>
    private static void SampleClips()
    {
        Clips.ClipboardHistory.Clear(keepPinned: false);
        var now = DateTime.Now;
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.CommonPictures);
        Clips.ClipboardHistory.Add(new Clips.ClipItem { Kind = Clips.ClipKind.Text, Text = "https://example.com/report-2026.pdf", Copied = now.AddMinutes(-42) });
        Clips.ClipboardHistory.Add(new Clips.ClipItem { Kind = Clips.ClipKind.Text, Text = Localization.L.IsArabic ? "العنوان: شارع الملك فهد، الرياض" : "Address: 14 Harbour Road, Bristol", Copied = now.AddMinutes(-18), Pinned = true });
        Clips.ClipboardHistory.Add(new Clips.ClipItem { Kind = Clips.ClipKind.Files, Files = [Path.Combine(pictures, "trip.jpg"), Path.Combine(pictures, "beach.jpg")], Copied = now.AddMinutes(-6) });
        Clips.ClipboardHistory.Add(new Clips.ClipItem { Kind = Clips.ClipKind.Text, Text = Localization.L.IsArabic ? "شكرًا على وقتك، سأرسل الملف اليوم." : "Thanks for your time, I'll send the file today.", Copied = now.AddSeconds(-20) });
    }

    private static Wheels.WheelEntry SampleScript() => new()
    {
        Kind = Wheels.EntryKind.Script,
        Name = Localization.L.T("Capitals"),
        Input = Wheels.ScriptInput.SelectedText,
        Script = "$text = $env:PETAL_INPUT.ToUpper()\nSet-Clipboard $text\nWrite-Output $text",
    };

    /// <summary>Made-up counts in a scratch file, so the statistics snapshot never shows the user's own.</summary>
    private static void SampleStats()
    {
        Stats.Statistics.FilePath = Path.Combine(Path.GetTempPath(), "petal-snapshot-statistics.json");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var data = new Stats.StatsData { Since = today.AddDays(-80), Uses = 412, Files = 1240, BytesIn = 9_400_000_000, BytesOut = 6_100_000_000, Saved = 3_300_000_000 };
        var random = new Random(4);
        for (int back = 0; back < 84; back++)
            if (back < 9 || random.NextDouble() < 0.55)
                data.Days[Stats.Statistics.Key(today.AddDays(-back))] = new Stats.DayCount { Uses = random.Next(1, 14), Files = random.Next(0, 40) };
        foreach (var (key, name, count) in new[] { ("format:webp", "Convert to WEBP", 96), ("tool:Compress", "Compress", 71), ("tool:RemoveBackground", "Remove Background", 38), ("action:Share", "Share", 22), ("entry:x", "Dark Mode", 17) })
        {
            data.Petals[key] = count;
            data.Names[key] = name;
        }
        File.WriteAllText(Stats.Statistics.FilePath, System.Text.Json.JsonSerializer.Serialize(data));
        Stats.Statistics.Reload();
    }

    private static T Private<T>() where T : Window =>
        (T)typeof(T).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    /// <summary>A few made-up history entries in a scratch file, so snapshots never show the user's own.</summary>
    private static void SampleHistory(Func<string, string> file)
    {
        Jobs.History.FilePath = Path.Combine(Path.GetTempPath(), "petal-snapshot-history.json");
        Jobs.History.Reload();
        Jobs.History.Clear();
        Jobs.History.Add(Localization.L.P(3, "Renamed {0} item", "Renamed {0} items"), [], [], [new Jobs.RenamedPath(file("a.jpg"), file("landscape.jpg"))]);
        Jobs.History.Add(Localization.L.F("Converted to {0}", "PDF"), [file("landscape.jpg")], [file("album.pdf")]);
        Jobs.History.Add(Localization.L.T("Background removed"), [file("landscape.jpg")], [file("figures.png")]);
        // The oldest one shown as undone, written directly: snapshots never move real files.
        var entries = Jobs.History.Entries.ToList();
        entries[^1] = entries[^1] with { Undone = true };
        File.WriteAllText(Jobs.History.FilePath, System.Text.Json.JsonSerializer.Serialize(entries));
        Jobs.History.Reload();
    }

    /// <summary>Favourites and automations to show in snapshots of Settings and the wheel (never saved).</summary>
    private static void SampleSettings(string folder)
    {
        var s = Settings.SettingsStore.Current;
        s.Favourites =
        [
            new Favourite { Format = "webp", LongestSide = 1600, Quality = 80 },
            new Favourite { Format = "jpg", LongestSide = 1024, Name = Localization.L.T("Email") },
            new Favourite { Format = "mp4", LongestSide = 1280, Quality = 24 },
        ];
        s.Automations = [new WatchRule { Folder = folder, Kind = WatchedKind.Pictures, Target = "jpg" }];
        s.Recipes = [SampleRecipe()];
        s.Wheels = [];
        s.WheelsVersion = 0;
        Wheels.WheelTemplates.Upgrade(s);
        s.Destinations = [new Wheels.FolderDestination { Folder = folder, Name = Localization.L.T("Projects") }];
        s.Workspaces =
        [
            new Screen.Workspace { Name = Localization.L.T("Work") },
            new Screen.Workspace { Name = Localization.L.IsArabic ? "الدراسة" : "Study" },
        ];
        s.PetalColours = new() { ["tool:Compress"] = "#30A46C", ["tool:Watermark"] = "#0090FF", ["action:Share"] = "#D6409F" };
    }

    private static Petal.Recipes.Recipe SampleRecipe() => new()
    {
        Name = Localization.L.T("Shop"),
        Steps =
        [
            new() { Kind = Petal.Recipes.RecipeStepKind.RemoveBackground, Background = "#FFFFFF" },
            new() { Kind = Petal.Recipes.RecipeStepKind.Resize, LongestSide = 1080 },
            new() { Kind = Petal.Recipes.RecipeStepKind.Convert, Format = "webp", Quality = 80 },
        ],
    };

    public static void Run(string[] args)
    {
        var output = args[1];
        var background = Load(args[2]);
        var files = args.Skip(3).ToList();
        bool everyPetal = files.Remove("--every-petal");
        Directory.CreateDirectory(output);

        var converters = EngineSetup.CreateConverters();
        SampleSettings(Path.GetDirectoryName(Path.GetFullPath(files.FirstOrDefault() ?? args[2]))!);
        var controller = new PetalController(converters, Application.Current.Dispatcher);
        var wheel = controller.CreateWheelForSnapshots();

        // The dark wheel once per petal, so every centre name and every label can be measured.
        if (everyPetal)
        {
            Theme.Configure(ThemePreference.Dark, false);
            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            foreach (var mode in Enum.GetValues<WheelMode>())
            {
                var name = mode.ToString().ToLowerInvariant();
                Save(wheel.Snapshot(files, mode, null, background), Path.Combine(output, $"{name}.png"));
                File.WriteAllLines(Path.Combine(output, $"{name}.txt"), wheel.PetalLabels);
                File.WriteAllLines(Path.Combine(output, $"{name}.layout"), wheel.PetalLayouts.Select(l =>
                    FormattableString.Invariant($"{l.Size:0.00} {l.Lines.Length} r={Math.Sqrt(l.Centre.X * l.Centre.X + l.Centre.Y * l.Centre.Y):0.0} clear={l.Clearance:0.0} ") + string.Join(" / ", l.Lines)));
                for (int i = 0; i < wheel.PetalLabels.Count; i++)
                    Save(wheel.Snapshot(files, mode, i, background), Path.Combine(output, $"{name}-{i:00}.png"));
            }
            return;
        }

        foreach (var dark in new[] { false, true })
        {
            Theme.Configure(dark ? ThemePreference.Dark : ThemePreference.Light, false);
            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            foreach (var (name, window) in new (string, Window)[]
            {
                ("settings", CreateSettings()),
                ("welcome", CreateWelcome()),
            })
            {
                Save(RenderWindow(window), Path.Combine(output, $"{(dark ? "dark" : "light")}-{name}.png"));
                window.Close();
            }
            // Every page of the main window, for reviewing what is further down.
            for (int page = 0; page < Settings.SettingsWindow.PageCount; page++)
            {
                var full = Settings.SettingsWindow.ForPage(page);
                Save(RenderWindow(full), Path.Combine(output, $"{(dark ? "dark" : "light")}-settings-page{page}.png"));
                full.Close();
            }
            foreach (var mode in Enum.GetValues<WheelMode>())
            {
                foreach (int? highlight in new int?[] { null, 1 })
                {
                    var image = wheel.Snapshot(files, mode, highlight, background);
                    var name = $"{(dark ? "dark" : "light")}-{mode.ToString().ToLowerInvariant()}{(highlight is null ? "" : "-hover")}.png";
                    Save(image, Path.Combine(output, name));
                }
            }
            RingSnapshots(wheel, files, background, output, dark ? "dark" : "light");
        }
    }

    /// <summary>
    /// The wheels without files (quick actions with their numbers, a switch under the pointer, More opened,
    /// media, an app's wheel) and the file wheels in each look: solid, monochrome, icons only, a larger zone.
    /// </summary>
    private static void RingSnapshots(WheelWindow wheel, IReadOnlyList<string> files, BitmapSource background, string output, string theme)
    {
        var s = Settings.SettingsStore.Current;
        var quick = Wheels.Rings.Page(s.Wheels.First(w => w.Kind == Wheels.WheelKind.Quick));
        var media = Wheels.Rings.Page(s.Wheels.First(w => w.Kind == Wheels.WheelKind.Media));
        var browser = Wheels.Rings.Page(s.Wheels.First(w => w.Template == "browser"));
        var explorer = Wheels.Rings.Page(s.Wheels.First(w => w.Template == "explorer"));
        RingPage[] pages = [browser, quick, media];
        void Shot(string name, Func<BitmapSource> take) => Save(take(), Path.Combine(output, $"{theme}-{name}.png"));
        Shot("ring-quick", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: pages, page: 1, keyboard: true));
        Shot("ring-quick-hover", () => wheel.Snapshot([], WheelMode.Conversions, 0, background, pages: pages, page: 1, keyboard: true));
        Shot("ring-quick-more", () => wheel.Snapshot([], WheelMode.Conversions, 2, background, pages: pages, page: 1, openGroup: quick.Content().Items.Count - 1));
        // The orbit on its way out: just out of the petal, half way, overshooting, at rest, and with the keyboard.
        int more = quick.Content().Items.Count - 1;
        foreach (var (name, progress) in new[] { ("15", 0.15), ("45", 0.45), ("108", 1.08), ("100", 1.0) })
            Shot($"orbit-{name}", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: pages, page: 1, openGroup: more, orbit: progress));
        Shot("orbit-keys", () => wheel.Snapshot([], WheelMode.Conversions, 3, background, pages: pages, page: 1, keyboard: true, openGroup: more));
        Shot("ring-media", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: pages, page: 2));
        Shot("ring-browser", () => wheel.Snapshot([], WheelMode.Conversions, 4, background, pages: pages, page: 0, keyboard: true));
        Shot("ring-explorer", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: [explorer, quick, media], page: 0));
        Shot("actions-more", () => wheel.Snapshot(files, WheelMode.Actions, 2, background, openGroup: Petal.Core.Actions.ActionWheel.Default.Count + s.Destinations.Count));
        // The wheels of 1.6: the screen, drawing and presenting, text (with Case out), windows and clips; and the centre as a calculator.
        RingPage Of(string template) => Wheels.Rings.Page(s.Wheels.First(w => w.Template == template));
        RingPage[] newer = [Of("screen"), Of("present"), Of("text"), Of("windows"), Of("clipboard")];
        Shot("ring-screen", () => wheel.Snapshot([], WheelMode.Conversions, 1, background, pages: newer, page: 0, keyboard: true));
        Shot("ring-present", () => wheel.Snapshot([], WheelMode.Conversions, 0, background, pages: newer, page: 1));
        Shot("ring-text", () => wheel.Snapshot([], WheelMode.Conversions, 1, background, pages: newer, page: 2, openGroup: 3));
        Shot("ring-windows", () => wheel.Snapshot([], WheelMode.Conversions, 2, background, pages: newer, page: 3, keyboard: true));
        Shot("ring-windows-more", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: newer, page: 3, openGroup: 4));
        SampleClips();
        Shot("ring-clipboard", () => wheel.Snapshot([], WheelMode.Conversions, 1, background, pages: newer, page: 4));
        RingPage[] work = [Of("work"), Of("clipboard")];
        Shot("ring-work", () => wheel.Snapshot([], WheelMode.Conversions, 0, background, pages: work, page: 0, keyboard: true));
        Shot("ring-work-spaces", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: work, page: 0,
            openGroup: s.Wheels.First(w => w.Template == "work").Items.FindIndex(e => e.Kind == Wheels.EntryKind.Workspaces)));
        Shot("hub-calc", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: pages, page: 1, keyboard: true, calc: "12% of 340"));
        Shot("hub-units", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: pages, page: 1, keyboard: true, calc: "5 km in mi"));
        var saved = (s.WheelStyle, s.ShowPetalNames, s.CancelZone, s.WheelMotion);
        try
        {
            s.WheelStyle = Overlay.WheelStyle.Solid;
            Shot("style-solid", () => wheel.Snapshot(files, WheelMode.Conversions, 2, background));
            s.WheelStyle = Overlay.WheelStyle.Mono;
            Shot("style-mono", () => wheel.Snapshot(files, WheelMode.Tools, 2, background));
            s.WheelStyle = Overlay.WheelStyle.Glass;
            s.ShowPetalNames = false;
            Shot("icons-only", () => wheel.Snapshot(files, WheelMode.Tools, 1, background));
            Shot("icons-only-quick", () => wheel.Snapshot([], WheelMode.Conversions, null, background, pages: pages, page: 1));
        }
        finally
        {
            (s.WheelStyle, s.ShowPetalNames, s.CancelZone, s.WheelMotion) = saved;
        }
    }

    /// <summary>
    /// Shows Settings on screen with the add-favourite dialog over it ("modal"), or the dialog on its own
    /// the way the wheel opens tool windows ("near"), then quits after a few seconds: for checking how
    /// windows first appear on the real desktop, which an off-screen render can't show.
    /// </summary>
    public static void ShowOnScreen(string[] args)
    {
        double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 6;
        if (args.Length > 3)
            Theme.Configure(args[3] == "light" ? ThemePreference.Light : ThemePreference.Dark, false);
        var quit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        quit.Tick += (_, _) => Application.Current.Shutdown();
        quit.Start();
        // "folder:OrganizeFolder" runs that tool on the folder given last the way a drop on the tools wheel
        // does, and "folder:webp" converts it like the conversion wheel; both with a history of their own.
        if (args[1].StartsWith("folder:", StringComparison.Ordinal) && args.Length > 4)
        {
            Jobs.History.FilePath = Path.Combine(Path.GetTempPath(), "petal-dev-history.json");
            Jobs.History.Reload();
            var jobs = new Jobs.JobRunner(EngineSetup.CreateConverters());
            Jobs.FolderLease.Install();
            var contents = Petal.Core.Jobs.FolderContents.Read([args[4]]);
            var name = args[1]["folder:".Length..];
            var at = new Input.ScreenPoint(900, 500);
            if (Enum.TryParse<Petal.Core.Tools.ToolId>(name, out var id))
                _ = Jobs.FolderDrop.RunToolAsync(new Tools.ToolLauncher(jobs), Petal.Core.Tools.ToolRegistry.Get(id), contents, at);
            else
                _ = Jobs.FolderDrop.ConvertAsync(jobs, contents, Petal.Core.Formats.Formats.FromId(name) ?? throw new ArgumentException(name), at);
            return;
        }
        var dialog = new Settings.FavouriteWindow(null, _ => { });
        if (args[1] == "near")
        {
            dialog.ShowNear(new Input.ScreenPoint(900, 500));
            return;
        }
        var settings = CreateSettings();
        settings.Show();
        settings.Dispatcher.BeginInvoke(() =>
        {
            dialog.Owner = settings;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ShowDialog();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static Window CreateSettings() =>
        (Window)typeof(Settings.SettingsWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    private static Window CreateWelcome() =>
        (Window)typeof(Onboarding.WelcomeWindow).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    /// <summary>Lays out a window's content at its width and renders it over the window background.</summary>
    public static BitmapSource RenderWindow(Window window, double height = double.NaN, Action<FrameworkElement>? inspect = null)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var frame = new System.Windows.Controls.Border
        {
            Width = window.Width,
            Background = Palette.Brush(Theme.Current.WindowBackground),
            Child = content,
            BorderBrush = Palette.Brush(Theme.Current.Divider),
            BorderThickness = new Thickness(1),
            FlowDirection = window.FlowDirection,
        };
        Localization.L.Apply(frame, mirror: false);
        if (!double.IsNaN(height)) frame.Height = height;
        else if (window.SizeToContent == SizeToContent.Manual) frame.Height = window.Height;
        frame.Measure(new Size(window.Width, double.PositiveInfinity));
        var size = new Size(window.Width, double.IsNaN(frame.Height) ? frame.DesiredSize.Height : frame.Height);
        frame.Arrange(new Rect(size));
        // A left-to-right parent, so a right-to-left frame is mirrored as it is in a real window.
        var host = new System.Windows.Controls.Border { Child = frame };
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        inspect?.Invoke(frame);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path));
        image.EndInit();
        image.Freeze();
        // Normalise DPI so pixel sizes match layout units.
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        var normal = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        normal.Freeze();
        return normal;
    }

    private const string Trimmed = "trimmed", Cut = "CUT";

    // Mixed-script words measure a pixel or two apart from how the text block lays them out.
    private const double Slack = 2.5;

    /// <summary>
    /// Texts a rendered window cuts: wider than their own box, or pushed past an ancestor's edge.
    /// Trimmed texts (with "…") are listed apart, since a file name may be trimmed on purpose.
    /// </summary>
    public static IEnumerable<string> CutTexts(FrameworkElement root)
    {
        var all = new List<System.Windows.Controls.TextBlock>();
        void Walk(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is System.Windows.Controls.TextBlock block)
                    all.Add(block);
                Walk(child);
            }
        }
        Walk(root);
        // (IsVisible is false all through a tree laid out away from any window, as snapshots are.)
        bool Shown(DependencyObject node)
        {
            for (; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is UIElement { Visibility: not Visibility.Visible })
                    return false;
                if (ReferenceEquals(node, root))
                    return true;
            }
            return true;
        }
        foreach (var block in all)
        {
            if (string.IsNullOrWhiteSpace(block.Text) || block.ActualWidth < 1 || !Shown(block))
                continue;
            bool trimmed = block.TextTrimming != TextTrimming.None;
            // (A report for the design review, in English.)
            string kind = trimmed ? Trimmed : Cut;
            if (block.TextWrapping == TextWrapping.NoWrap)
            {
                var text = new FormattedText(block.Text, Localization.L.Culture, block.FlowDirection,
                    new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
                if (text.WidthIncludingTrailingWhitespace > block.ActualWidth - block.Padding.Left - block.Padding.Right + Slack)
                {
                    yield return $"{kind} by its box ({text.WidthIncludingTrailingWhitespace:0} > {block.ActualWidth:0}): {block.Text}";
                    continue;
                }
            }
            var bounds = new Rect(0, 0, block.ActualWidth, block.ActualHeight);
            // The block's own clip first (set when it is arranged narrower than its words), then its ancestors'.
            for (FrameworkElement? parent = block; parent is not null;
                 parent = ReferenceEquals(parent, root) ? null : VisualTreeHelper.GetParent(parent) as FrameworkElement)
            {
                // Scrolled content is meant to run past the view.
                if (parent is System.Windows.Controls.ScrollContentPresenter)
                    break;
                var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(parent)?.Bounds
                    ?? (parent.ClipToBounds ? new Rect(0, 0, parent.ActualWidth, parent.ActualHeight) : (Rect?)null);
                if (clip is not { } edge)
                    continue;
                var inParent = ReferenceEquals(parent, block) ? bounds : block.TransformToAncestor(parent).TransformBounds(bounds);
                if (inParent.Left < edge.Left - Slack || inParent.Right > edge.Right + Slack)
                {
                    yield return $"{kind} by {parent.GetType().Name} ({inParent.Width:0} in {edge.Width:0}): {block.Text}";
                    break;
                }
            }
        }
    }

    /// <summary>
    /// "Petal.exe --captions-test &lt;folder&gt; &lt;sound.wav&gt; [models folder] [both|arabic|original|english]" plays the
    /// sound into the live captions (off screen) and writes what they show every quarter second, with pictures.
    /// </summary>
    public static async Task RunCaptionsAsync(string[] args)
    {
        var output = args[1];
        Directory.CreateDirectory(output);
        if (args.Length >= 4)
            Petal.Core.Models.ModelStore.Folder = args[3];
        var settings = Settings.SettingsStore.Load();
        settings.CaptionsShow = args.Length >= 5 ? args[4] : "both";
        Words.LiveCaptions.SoundFile = args[2];
        double length;
        using (var reader = new NAudio.Wave.WaveFileReader(args[2]))
            length = reader.TotalTime.TotalSeconds;
        Words.LiveCaptions.Toggle();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var log = new System.Text.StringBuilder();
        string last = "";
        int picture = 0;
        while (clock.Elapsed.TotalSeconds < length + 5 && Words.LiveCaptions.Window is { } window)
        {
            await Task.Delay(250);
            if (window.IsLoaded && window.Left > -10000)
                window.Left = -20000;
            var (small, main, live) = window.Shown;
            var line = small + " || " + main + " || " + live;
            if (line != last)
                log.AppendLine($"{clock.Elapsed.TotalSeconds:0.00}\t{small}\t{main}\t{live}");
            last = line;
            if (clock.Elapsed.TotalSeconds > (picture + 1) * 8 && window.Content is FrameworkElement content && content.ActualWidth > 0)
            {
                picture++;
                // Drawn on its own, the card misses the window's right-to-left mirroring: it is put back here.
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    if (window.FlowDirection == FlowDirection.RightToLeft)
                        dc.PushTransform(new ScaleTransform(-1, 1, content.ActualWidth / 2, 0));
                    dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 2), (int)Math.Ceiling(content.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                Save(bitmap, Path.Combine(output, $"captions-{picture:00}.png"));
            }
        }
        File.WriteAllText(Path.Combine(output, "captions.txt"), log.ToString());
        Words.LiveCaptions.Stop();
        await Task.Delay(1500);
    }

    private static void Save(BitmapSource image, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }
}
