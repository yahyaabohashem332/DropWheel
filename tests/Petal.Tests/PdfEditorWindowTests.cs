using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Petal.App.Tools.Windows;
using Petal.Core.Tools;
using Petal.Engines.Documents.Pdf;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Petal.Tests;

/// <summary>
/// The Edit PDF window, in this process and on no screen: no real mouse, the same handlers the mouse calls. A press, a drag and
/// a release on a page do what they say, what they make comes out in the saved file where it was put, and the keys do theirs.
/// </summary>
[Collection(OneApplication)]
public sealed class PdfEditorWindowTests
{
    /// <summary>A process has a single WPF Application, and tests that use its styles take turns.</summary>
    public const string OneApplication = "One WPF Application";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "petal-editor-" + Guid.NewGuid().ToString("N"));

    /// <summary>One interface thread for all of these, as the app has: WPF's theme and icon stay with the thread that made them.</summary>
    private static readonly Lazy<Dispatcher> Interface = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Petal;component/Styles/Controls.xaml") });
            Petal.App.Visuals.ThemeResources.Install(app);
            // Awaits come back to this thread, as they do in the app.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    /// <summary>For other window tests: on the same interface thread as these.</summary>
    internal static void RunOnInterface(Func<Task> test) => OnSta(test);

    private static void OnSta(Func<Task> test)
    {
        var running = Interface.Value.InvokeAsync(test).Task.Unwrap();
        if (!running.Wait(TimeSpan.FromMinutes(3)))
            throw new TimeoutException("The window test didn't finish.");
        running.GetAwaiter().GetResult();
    }

    private string SampleForm()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"form {Guid.NewGuid():N}.pdf");
        PdfSampleForm.Write(path);
        return path;
    }

    private string Output() => Path.Combine(_dir, $"out {Guid.NewGuid():N}.pdf");

    private static async Task<PdfEditorWindow> Open(string file)
    {
        var window = new PdfEditorWindow(ToolRegistry.Get(ToolId.EditPdf), file, new Petal.App.Jobs.JobRunner(null!), new Petal.App.Input.ScreenPoint(-20000, -20000))
        {
            ConfirmClose = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
        };
        window.Show();
        await window.Opened;
        await Until(() => window.Session is null || window.PageView(0).IsBuilt);
        return window;
    }

    /// <summary>Lets the interface thread work until something is true.</summary>
    private static async Task Until(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "It didn't happen in time.");
            await Task.Delay(20);
        }
    }

    private static string Words(string pdf, int page = 0)
    {
        using var read = PigDocument.Open(pdf);
        return string.Join(' ', read.GetPage(page + 1).GetWords().Select(w => w.Text));
    }

    private static void Click(PdfEditorWindow window, int page, double x, double y)
    {
        window.Press(page, new Point(x, y));
        window.Release(new Point(x, y));
    }

    private static void DragOn(PdfEditorWindow window, int page, double x1, double y1, double x2, double y2)
    {
        window.Press(page, new Point(x1, y1));
        window.Drag(new Point((x1 + x2) / 2, (y1 + y2) / 2));
        window.Release(new Point(x2, y2));
    }

    private static void Type(PdfEditorWindow window, string text)
    {
        Assert.NotNull(window.Editor);
        window.Editor!.Text = text;
        window.CommitEditor();
    }

    [Fact]
    public void Form_fields_are_on_the_page_ready_and_tab_goes_through_them_and_they_save()
    {
        OnSta(async () =>
        {
            var file = SampleForm();
            var window = await Open(file);
            var names = window.Session!.Fields.Select(f => f.Name).ToList();
            Assert.Equal(["name", "native", "address", "country", "passport", "purpose", "agree"], names);

            var name = (TextBox)window.ControlsOf("name")[0];
            // On its place on the page, at the page's zoom.
            var place = window.Session.Fields[0].Widgets[0].Area;
            Assert.Equal(place.Left * window.Zoom, Canvas.GetLeft(name), 3);
            Assert.Equal(place.Top * window.Zoom, Canvas.GetTop(name), 3);
            name.Text = "Yahya";
            var native = (TextBox)window.ControlsOf("native")[0];
            native.Text = "يحيى أبو هاشم";
            Assert.Equal(FlowDirection.RightToLeft, native.FlowDirection);
            var combo = (ComboBox)window.ControlsOf("country")[0];
            combo.SelectedIndex = 1;
            var list = (ListBox)window.ControlsOf("purpose")[0];
            list.SelectedIndex = 2;
            var agree = window.Session.Fields.First(f => f.Name == "agree");
            window.Toggle(agree, "Yes");
            Assert.Equal("Yes", window.Values["agree"]);

            // Tab goes on in the form's order, and Shift+Tab back.
            Assert.True(window.HandleKey(Key.Tab, ModifierKeys.None, name));
            Assert.Same(native, FocusManager.GetFocusedElement(window));
            Assert.True(window.HandleKey(Key.Tab, ModifierKeys.None, native));
            Assert.Same(window.ControlsOf("address")[0], FocusManager.GetFocusedElement(window));
            Assert.True(window.HandleKey(Key.Tab, ModifierKeys.Shift, window.ControlsOf("address")[0]));
            Assert.Same(native, FocusManager.GetFocusedElement(window));

            var output = Output();
            await window.SaveToAsync(output);
            using var saved = PdfEditSession.Open(output);
            var values = saved.Fields.ToDictionary(f => f.Name, f => f.Value);
            Assert.Equal("Yahya", values["name"]);
            Assert.Equal("يحيى أبو هاشم", values["native"]);
            Assert.Equal("SY", values["country"]);
            Assert.Equal("Family", values["purpose"]);
            Assert.Equal("Yes", values["agree"]);
            window.Close();
        });
    }

    [Fact]
    public void Text_is_placed_by_a_click_moved_by_a_drag_resized_by_a_corner_and_edited_by_a_click()
    {
        OnSta(async () =>
        {
            var window = await Open(SampleForm());
            window.SelectTool(PdfTool.Text);
            Click(window, 0, 340, 200);
            Type(window, "Hello there");
            var item = Assert.IsType<PdfTextItem>(window.Items.Single());
            Assert.Equal(340, item.Area.Left, 1);
            Assert.Equal(12, item.Size);

            // Moved.
            window.SelectTool(PdfTool.Select);
            DragOn(window, 0, item.Area.Left + 40, item.Area.Top + 7, item.Area.Left + 60, item.Area.Top + 57);
            item = Assert.IsType<PdfTextItem>(window.Items.Single());
            Assert.Equal(360, item.Area.Left, 1);
            Assert.Same(item, window.Selected);

            // The bottom-right corner, dragged.
            DragOn(window, 0, item.Area.Right, item.Area.Bottom, item.Area.Right - 40, item.Area.Bottom + 30);
            var resized = Assert.IsType<PdfTextItem>(window.Items.Single());
            Assert.Equal(item.Area.Width - 40, resized.Area.Width, 1);
            Assert.Equal(item.Area.Height + 30, resized.Area.Height, 1);
            Assert.Equal(item.Area.Left, resized.Area.Left, 1);

            // A click on it opens it for typing.
            Click(window, 0, resized.Area.Left + 40, resized.Area.Top + 7);
            Assert.Equal("Hello there", window.Editor?.Text);
            Type(window, "Goodbye");
            Assert.Equal("Goodbye", Assert.IsType<PdfTextItem>(window.Items.Single()).Text);

            var output = Output();
            await window.SaveToAsync(output);
            Assert.Contains("Goodbye", Words(output));
            using var read = PigDocument.Open(output);
            var page = read.GetPage(1);
            var word = page.GetWords().First(w => w.Text == "Goodbye");
            Assert.InRange(word.BoundingBox.Left, 358, 364);
            window.Close();
        });
    }

    [Fact]
    public void Marks_and_the_date_drop_with_a_click_and_undo_redo_and_delete_work_from_the_keys()
    {
        OnSta(async () =>
        {
            var window = await Open(SampleForm());
            window.SelectTool(PdfTool.Check);
            Click(window, 0, 400, 500);
            var mark = Assert.IsType<PdfMarkItem>(window.Items.Single());
            Assert.Equal(PdfMark.Check, mark.Mark);
            Assert.Equal(400, mark.Area.Left + mark.Area.Width / 2, 1);
            window.SelectTool(PdfTool.Date);
            Click(window, 0, 400, 450);
            var date = Assert.IsType<PdfTextItem>(window.Items[1]);
            Assert.Equal(PdfEditorWindow.TodayText(), date.Text);
            Assert.Equal(DateTime.Now.ToString("d", Petal.App.Localization.L.Culture), date.Text);

            Assert.True(window.HandleKey(Key.Z, ModifierKeys.Control, null));
            Assert.Single(window.Items);
            Assert.True(window.HandleKey(Key.Z, ModifierKeys.Control, null));
            Assert.Empty(window.Items);
            Assert.True(window.HandleKey(Key.Y, ModifierKeys.Control, null));
            Assert.True(window.HandleKey(Key.Y, ModifierKeys.Control, null));
            Assert.Equal(2, window.Items.Count);

            window.SelectTool(PdfTool.Select);
            Click(window, 0, 400, 500);
            Assert.IsType<PdfMarkItem>(window.Selected);
            Assert.True(window.HandleKey(Key.Delete, ModifierKeys.None, null));
            Assert.IsType<PdfTextItem>(window.Items.Single());
            Assert.True(window.HandleKey(Key.Z, ModifierKeys.Control, null));
            Assert.Equal(2, window.Items.Count);

            // Esc goes back to the selection tool first, and only then closes.
            window.SelectTool(PdfTool.Pen);
            Assert.True(window.Escape());
            Assert.Equal(PdfTool.Select, window.ToolNow);
            Assert.False(window.Escape());

            // Undo takes back a value typed in a field too, the whole run of typing at once.
            var name = (TextBox)window.ControlsOf("name")[0];
            name.Text = "A";
            name.Text = "AB";
            Assert.Equal("AB", window.Values["name"]);
            window.Undo();
            Assert.False(window.Values.ContainsKey("name"));
            Assert.Equal("", name.Text);
            window.Redo();
            Assert.Equal("AB", name.Text);
            window.Close();
        });
    }

    [Fact]
    public void A_drag_across_words_marks_those_words_and_the_drawing_tools_make_their_annotations()
    {
        OnSta(async () =>
        {
            var window = await Open(SampleForm());
            var line = window.Session!.Lines(0).First(l => l.Text.StartsWith("Please", StringComparison.Ordinal));
            var first = line.Words[0].Area;
            var third = line.Words[2].Area;
            window.SelectTool(PdfTool.Highlight);
            DragOn(window, 0, first.Left + 2, first.Top + 6, third.Right - 2, third.Top + 6);
            var marked = Assert.IsType<PdfMarkupItem>(window.Items.Single());
            Assert.Equal(PdfMarkup.Highlight, marked.Kind);
            Assert.Equal(first.Left, Assert.Single(marked.Areas).Left, 1);
            Assert.Equal(third.Right, marked.Areas[0].Right, 1);

            window.SelectTool(PdfTool.Underline);
            DragOn(window, 0, first.Left + 2, first.Top + 6, first.Right - 2, first.Top + 6);
            window.SelectTool(PdfTool.StrikeOut);
            DragOn(window, 0, third.Left + 2, third.Top + 6, third.Right - 2, third.Top + 6);
            window.SelectTool(PdfTool.Note);
            Click(window, 0, 500, 100);
            Type(window, "See this");
            window.SelectTool(PdfTool.Pen);
            window.Press(0, new Point(350, 300));
            window.Drag(new Point(370, 320));
            window.Drag(new Point(390, 300));
            window.Release(new Point(410, 320));
            window.SelectTool(PdfTool.Line);
            DragOn(window, 0, 350, 340, 450, 340);
            window.SelectTool(PdfTool.Arrow);
            DragOn(window, 0, 350, 360, 450, 380);
            window.SelectTool(PdfTool.Rectangle);
            DragOn(window, 0, 350, 400, 420, 440);
            window.SelectTool(PdfTool.Ellipse);
            DragOn(window, 0, 430, 400, 500, 440);
            window.SelectTool(PdfTool.Cover);
            DragOn(window, 0, 350, 460, 420, 480);
            // A tiny slip of a drag makes nothing.
            window.SelectTool(PdfTool.Rectangle);
            DragOn(window, 0, 350, 500, 351, 501);
            Assert.Equal(10, window.Items.Count);

            // A picture or a signature goes where it was clicked and can be moved at once.
            window.PlacePicture(0, new Point(450, 600), BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, new byte[32], 16));
            var picture = Assert.IsType<PdfPictureItem>(window.Selected);
            Assert.Equal(450, picture.Area.Left + picture.Area.Width / 2, 1);
            Assert.Equal(picture.Area.Width / 2, picture.Area.Height, 1);
            Assert.Equal(PdfTool.Select, window.ToolNow);

            var output = Output();
            window.SetFlatten(false);
            await window.SaveToAsync(output);
            using var document = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            var kinds = document.Pages[0].Elements.GetArray("/Annots")!.Elements
                .Select(a => ((PdfSharp.Pdf.PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)a).Value).Elements.GetName("/Subtype"))
                .Where(k => k != "/Widget").ToList();
            Assert.Equal(["/Highlight", "/Underline", "/StrikeOut", "/Text", "/Ink", "/Line", "/Line", "/Square", "/Circle"], kinds);
            window.Close();
        });
    }

    [Fact]
    public void Clicking_a_line_retypes_it_and_the_old_words_leave_the_file()
    {
        OnSta(async () =>
        {
            var window = await Open(SampleForm());
            var line = window.Session!.Lines(0).First(l => l.Text.StartsWith("Office", StringComparison.Ordinal));
            window.SelectTool(PdfTool.Retype);
            Click(window, 0, line.Area.Left + 10, line.Area.Top + 5);
            Assert.Equal(line.Text, window.Editor?.Text);
            Type(window, "Changed by hand.");
            var retyped = Assert.IsType<PdfReplaceItem>(window.Items.Single());
            Assert.Equal(line.Size, retyped.Size, 1);

            // Clicked again, it opens with what was typed, and doesn't make a second item.
            Click(window, 0, line.Area.Left + 10, line.Area.Top + 5);
            Assert.Equal("Changed by hand.", window.Editor?.Text);
            Type(window, "Changed twice.");
            Assert.Equal("Changed twice.", Assert.IsType<PdfReplaceItem>(window.Items.Single()).Text);

            var output = Output();
            var result = await window.SaveToAsync(output);
            Assert.Equal(0, result.CoveredLines);
            var words = Words(output);
            Assert.DoesNotContain("Office", words);
            Assert.DoesNotContain("2026-418", words);
            Assert.Contains("Changedtwice.", words.Replace(" ", ""));
            Assert.Contains("Visa application form", words);
            window.Close();
        });
    }

    [Fact]
    public void Redaction_by_box_by_words_by_search_and_by_private_details_saves_without_them()
    {
        OnSta(async () =>
        {
            var window = await Open(SampleForm());
            var session = window.Session!;
            window.SelectTool(PdfTool.Redact);

            // A box drawn by hand.
            var title = session.Lines(0).First(l => l.Text.StartsWith("Visa", StringComparison.Ordinal)).Area;
            DragOn(window, 0, title.Left - 1, title.Top - 1, title.Left + 30, title.Bottom + 1);
            Assert.IsType<PdfRedactItem>(window.Items.Single());

            // A word found on all pages, and private details.
            await window.FindAsync("capital");
            Assert.Equal(2, window.Items.Count);
            // The e-mail address, the phone and the IBAN; looking again marks nothing twice.
            await window.FindPrivateAsync();
            Assert.Equal(5, window.Items.Count);
            await window.FindPrivateAsync();
            Assert.Equal(5, window.Items.Count);

            var output = Output();
            await window.SaveToAsync(output);
            var words = Words(output);
            foreach (var gone in new[] { "Visa", "capital", "visa.office", "532", "TR33" })
                Assert.DoesNotContain(gone, words);
            Assert.Contains("Please fill in", words);
            Assert.Contains("Contact:", words);
            Assert.Contains("Office use only", words);
            window.Close();
        });
    }

    [Fact]
    public void A_box_can_be_taken_back_and_then_nothing_goes()
    {
        OnSta(async () =>
        {
            var window = await Open(SampleForm());
            window.SelectTool(PdfTool.Redact);
            await window.FindAsync("capital");
            var box = Assert.IsType<PdfRedactItem>(window.Items.Single());
            window.SelectTool(PdfTool.Select);
            Click(window, 0, box.Area.Left + 2, box.Area.Top + 2);
            window.DeleteSelected();
            var output = Output();
            await window.SaveToAsync(output);
            Assert.Contains("capital", Words(output));
            window.Close();
        });
    }

    [Fact]
    public void A_protected_pdf_asks_for_its_password_in_the_window()
    {
        OnSta(async () =>
        {
            var plain = SampleForm();
            var locked = Output();
            await PdfSecurityTools.ProtectAsync(plain, locked, new PdfProtectOptions("sesame", AllowEditing: true));
            var window = await Open(locked);
            Assert.Null(window.Session);
            Assert.True(window.NeedsPassword);
            await window.OpenWithPasswordAsync("wrong");
            Assert.Null(window.Session);
            Assert.True(window.NeedsPassword);
            await window.OpenWithPasswordAsync("sesame");
            Assert.NotNull(window.Session);
            Assert.False(window.NeedsPassword);
            await Until(() => window.PageView(0).IsBuilt);
            window.Close();
        });
    }

    [Fact]
    public void A_long_pdf_draws_only_what_is_in_view_and_pages_stay_left_to_right()
    {
        OnSta(async () =>
        {
            Directory.CreateDirectory(_dir);
            var path = Output();
            PdfFonts.EnsureInitialized();
            using (var document = new PdfSharp.Pdf.PdfDocument())
            {
                for (int i = 0; i < 60; i++)
                {
                    var page = document.AddPage();
                    using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                    gfx.DrawString("Page number " + i, new PdfSharp.Drawing.XFont("Arial", 12), PdfSharp.Drawing.XBrushes.Black, 72, 72);
                }
                document.Save(path);
            }
            var window = await Open(path);
            await Until(() => window.PageView(0).Picture?.Source is not null);
            Assert.Equal(FlowDirection.LeftToRight, window.PageView(0).FlowDirection);
            Assert.False(window.PageView(59).IsBuilt);
            Assert.True(Enumerable.Range(0, 60).Count(i => window.PageView(i).IsBuilt) < 12);
            window.Close();
        });
    }

    [Fact]
    public void A_form_locked_for_filling_offers_filling_and_no_more()
    {
        OnSta(async () =>
        {
            var plain = SampleForm();
            var locked = Output();
            using (var document = PdfSharp.Pdf.IO.PdfReader.Open(plain, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
            {
                document.SecurityHandler.SetEncryptionToV5(true);
                document.SecuritySettings.OwnerPassword = "owner-only";
                document.SecuritySettings.PermitModifyDocument = false;
                document.SecuritySettings.PermitAnnotations = false;
                document.SecuritySettings.PermitFormsFill = true;
                document.Save(locked);
            }
            var window = await Open(locked);
            Assert.Equal(PdfEditRights.Fill, window.Session!.Rights);
            Assert.Contains("filling", window.FooterText);
            ((TextBox)window.ControlsOf("name")[0]).Text = "Allowed";
            var output = Output();
            await window.SaveToAsync(output);
            using var saved = PdfEditSession.Open(output);
            Assert.Equal("Allowed", saved.Fields.First(f => f.Name == "name").Value);
            window.Close();
        });
    }
}
