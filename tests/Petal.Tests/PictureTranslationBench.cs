using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using TextAlignment = System.Windows.TextAlignment;
using Petal.Recipes.Translation;
using Xunit.Abstractions;

namespace Petal.Tests;

/// <summary>
/// The real thing on pictures made for it: PETAL_PICTURES is an empty folder the results go to, to be looked at. Each picture
/// is drawn here, read back by Windows, translated by the real model and written beside its original, with a log of what was
/// read against what was drawn. PETAL_PICTURES_IN adds a folder of pictures of the tester's own.
/// </summary>
public sealed class PictureTranslationBench(ITestOutputHelper output)
{
    private sealed record Sample(string Name, string From, string Into, Action<DrawingContext, int, int> Draw, string[] Said, int Width = 1200, int Height = 800);

    private static void Say(DrawingContext dc, string text, double x, double y, double size, Color ink, bool rtl = false, bool bold = false, TextAlignment align = TextAlignment.Left, double width = 0, string font = "Segoe UI")
    {
        var brush = new SolidColorBrush(ink);
        var typeface = new Typeface(new FontFamily(font), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, typeface, size, brush, 1.0);
        if (width > 0)
            formatted.MaxTextWidth = width;
        formatted.TextAlignment = align;
        dc.DrawText(formatted, new Point(x, y));
    }

    /// <summary>A real photograph that comes with Windows; the one in testdata is a test pattern.</summary>
    private static readonly string Landscape = Directory.EnumerateFiles(@"C:\Windows\Web\Wallpaper", "*.jpg", SearchOption.AllDirectories).OrderBy(f => f).FirstOrDefault()
        ?? Path.Combine(FindRepo(), "testdata", "landscape.jpg");

    private static string FindRepo()
    {
        var dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "testdata")))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    private static IEnumerable<Sample> Samples()
    {
        string[] menu = ["Our Menu", "Lentil soup", "Grilled chicken with rice", "Fresh orange juice", "Chocolate cake", "Open every day until midnight"];
        yield return new Sample("menu-en-ar", "auto", "ar", (dc, w, h) =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(250, 245, 232)), null, new Rect(0, 0, w, h));
            Say(dc, menu[0], 0, 60, 72, Color.FromRgb(120, 40, 20), bold: true, align: TextAlignment.Center, width: w);
            string[] prices = ["$4", "$12", "$3", "$5"];
            for (int i = 1; i <= 4; i++)
            {
                Say(dc, menu[i], 120, 200 + (i - 1) * 90, 42, Colors.Black);
                Say(dc, prices[i - 1], 980, 200 + (i - 1) * 90, 42, Colors.Black, bold: true);
            }
            Say(dc, menu[5], 0, 660, 34, Color.FromRgb(90, 90, 90), align: TextAlignment.Center, width: w);
        }, menu);

        string[] sign = ["تحذير", "ممنوع الدخول إلا للموظفين", "الرجاء إغلاق الباب بعد الدخول"];
        yield return new Sample("sign-ar-en", "auto", "en", (dc, w, h) =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(180, 20, 30)), null, new Rect(0, 0, w, h));
            Say(dc, sign[0], 0, 110, 110, Colors.White, rtl: true, bold: true, align: TextAlignment.Center, width: w);
            Say(dc, sign[1], 0, 350, 64, Colors.White, rtl: true, align: TextAlignment.Center, width: w);
            Say(dc, sign[2], 0, 520, 48, Color.FromRgb(255, 225, 150), rtl: true, align: TextAlignment.Center, width: w);
        }, sign);

        string[] slide = ["Birleşik Kelimeler", "Birleşik kelimeler iki ayrı sözcüğün bir araya gelerek yeni bir anlam oluşturmasıyla meydana gelir. Bu kelimeler bazen bitişik, bazen ayrı yazılır.", "Örnek: kaynana, hanımeli, başöğretmen"];
        yield return new Sample("slide-tr-ar", "auto", "ar", (dc, w, h) =>
        {
            var gradient = new LinearGradientBrush(Color.FromRgb(20, 50, 90), Color.FromRgb(40, 110, 150), 0);
            dc.DrawRectangle(gradient, null, new Rect(0, 0, w, h));
            Say(dc, slide[0], 90, 70, 66, Colors.White, bold: true);
            Say(dc, slide[1], 90, 220, 40, Color.FromRgb(230, 240, 250), width: 1020);
            Say(dc, slide[2], 90, 560, 40, Color.FromRgb(255, 210, 110));
        }, slide);

        string[] poster = ["Grand Opening", "Saturday at 10 in the morning", "Free coffee for the first hundred guests"];
        yield return new Sample("poster-en-tr", "auto", "tr", (dc, w, h) =>
        {
            using var image = new MagickImage(Landscape);
            image.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
            image.Format = MagickFormat.Bmp;
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.StreamSource = new MemoryStream(image.ToByteArray());
            picture.EndInit();
            dc.DrawImage(picture, new Rect(0, 0, w, h));
            Say(dc, poster[0], 0, 120, 96, Colors.White, bold: true, align: TextAlignment.Center, width: w);
            Say(dc, poster[1], 0, 300, 52, Colors.White, align: TextAlignment.Center, width: w);
            Say(dc, poster[2], 0, 620, 44, Color.FromRgb(255, 240, 120), bold: true, align: TextAlignment.Center, width: w);
        }, poster);

        string[] screen = ["Paramètres", "Langue de l'interface", "Enregistrer les modifications", "Annuler"];
        yield return new Sample("screen-fr-ar", "auto", "ar", (dc, w, h) =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 33, 38)), null, new Rect(0, 0, w, h));
            Say(dc, screen[0], 60, 50, 44, Colors.White, bold: true);
            Say(dc, screen[1], 60, 170, 28, Color.FromRgb(210, 210, 215));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(45, 110, 230)), null, new Rect(60, 300, 480, 70), 10, 10);
            Say(dc, screen[2], 60, 316, 28, Colors.White, align: TextAlignment.Center, width: 480);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(70, 72, 80)), null, new Rect(570, 300, 200, 70), 10, 10);
            Say(dc, screen[3], 570, 316, 28, Colors.White, align: TextAlignment.Center, width: 200);
        }, screen);

        string[] letter = ["Estimado cliente:", "Le informamos de que su pedido ha sido enviado esta mañana y llegará a su domicilio en un plazo de tres días laborables. Si tiene alguna pregunta, no dude en ponerse en contacto con nosotros.", "Muchas gracias por su compra."];
        yield return new Sample("letter-es-ur", "auto", "ur", (dc, w, h) =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            Say(dc, letter[0], 100, 90, 34, Colors.Black, font: "Times New Roman");
            Say(dc, letter[1], 100, 180, 34, Colors.Black, width: 1000, font: "Times New Roman");
            Say(dc, letter[2], 100, 480, 34, Colors.Black, font: "Times New Roman");
        }, letter);
    }

    private static byte[] Render(Sample sample)
    {
        byte[]? pixels = null;
        var thread = new Thread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                sample.Draw(dc, sample.Width, sample.Height);
            var target = new RenderTargetBitmap(sample.Width, sample.Height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            pixels = new byte[sample.Width * sample.Height * 4];
            target.CopyPixels(pixels, sample.Width * 4, 0);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return pixels!;
    }

    private static void Write(byte[] bgra, int width, int height, string path)
    {
        using var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        image.Write(path);
    }

    [Fact]
    public async Task Pictures_for_looking_at()
    {
        if (Environment.GetEnvironmentVariable("PETAL_PICTURES") is not { Length: > 0 } folder)
            return;
        Directory.CreateDirectory(folder);
        bool translate = Environment.GetEnvironmentVariable("PETAL_PICTURES_READ_ONLY") != "1";
        var log = new StringBuilder();
        foreach (var sample in Samples())
        {
            var pixels = Render(sample);
            Write(pixels, sample.Width, sample.Height, Path.Combine(folder, sample.Name + ".before.png"));
            var clock = Stopwatch.StartNew();
            var work = await PictureTranslation.ReadAsync(pixels, sample.Width, sample.Height);
            log.AppendLine($"=== {sample.Name}: read in {clock.Elapsed.TotalSeconds:0.0}s, {work.Blocks.Count} blocks");
            log.AppendLine("drawn: " + string.Join(" | ", sample.Said));
            foreach (var b in work.Blocks)
                log.AppendLine($"  read: {b.Source}  [{(int)b.Box.Left},{(int)b.Box.Top} {(int)b.Box.Width}x{(int)b.Box.Height} {b.Align} plain={b.Plain} bold={b.Bold} ink={b.Ink} paper={b.Paper}]");
            if (translate)
            {
                clock.Restart();
                await PictureTranslation.TranslateAsync(work, sample.From, sample.Into);
                double said = clock.Elapsed.TotalSeconds;
                clock.Restart();
                await PictureTranslation.EraseAsync(work);
                double erased = clock.Elapsed.TotalSeconds;
                clock.Restart();
                var done = PictureTranslation.Compose(work, sample.Into);
                log.AppendLine($"translate {said:0.0}s, erase {erased:0.0}s, draw {clock.Elapsed.TotalSeconds:0.00}s");
                foreach (var b in work.Blocks)
                    log.AppendLine($"  -> {(b.Keep ? "(kept) " : "")}{(b.Tight ? "(tight) " : "")}{b.Translation}");
                Write(done, sample.Width, sample.Height, Path.Combine(folder, sample.Name + ".after.png"));
            }
        }
        // The tester's own pictures, saved as the app saves them.
        if (translate && Environment.GetEnvironmentVariable("PETAL_PICTURES_IN") is { Length: > 0 } own)
            foreach (var file in Directory.EnumerateFiles(own))
            {
                var into = Environment.GetEnvironmentVariable("PETAL_PICTURES_INTO") ?? "ar";
                var result = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "." + into + Path.GetExtension(file));
                var clock = Stopwatch.StartNew();
                int pieces = await PictureTranslation.TranslateFileAsync(file, result, "auto", into);
                log.AppendLine($"=== {Path.GetFileName(file)}: {pieces} pieces in {clock.Elapsed.TotalSeconds:0.0}s");
            }
        await File.WriteAllTextAsync(Path.Combine(folder, "log.txt"), log.ToString());
        output.WriteLine(log.ToString());
    }
}
