using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Petal.App.Screen;

namespace Petal.Tests;

/// <summary>
/// The floating screen tools take their clicks: a press on a chip, a button or the text box is theirs, not a
/// drag of the window (which used to swallow the timer's presets and the Record button), and choosing a
/// preset or typing a time starts the timer.
/// </summary>
public sealed class ScreenWindowClickTests
{
    private static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        yield return root;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            foreach (var d in All(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    /// <summary>Shown far off the screen (the window places itself when loaded; it is moved away again).</summary>
    private static void ShowAway(Window window)
    {
        window.ShowActivated = false;
        window.Loaded += (_, _) => { window.Left = -20000; window.Top = -20000; };
        window.Show();
        window.UpdateLayout();
    }

    private static void Click(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    private static TimerWindow NewTimer() =>
        (TimerWindow)Activator.CreateInstance(typeof(TimerWindow), nonPublic: true)!;

    private static string Shown(Window window) =>
        string.Join(" ", All(window).OfType<TextBlock>().Select(t => t.Text));

    [Fact]
    public void Timer_presets_and_the_time_box_are_clicks_not_drags()
    {
        OnSta(() =>
        {
            var timer = NewTimer();
            ShowAway(timer);
            var panel = (UIElement)timer.Content;
            var chips = All(timer).OfType<Border>().Where(b => b.Cursor == Cursors.Hand).ToList();
            Assert.True(chips.Count >= 7, $"{chips.Count} chips");
            Assert.All(chips, chip => Assert.True(ScreenUi.IsClickable(chip.Child ?? chip, panel)));
            var box = All(timer).OfType<TextBox>().Single();
            Assert.True(ScreenUi.IsClickable(box, panel));
            // The title is where the window is dragged from.
            var title = All(timer).OfType<TextBlock>().First(t => t.Text == "Timer");
            Assert.False(ScreenUi.IsClickable(title, panel));
            timer.Close();
        });
    }

    [Fact]
    public void A_preset_starts_the_timer()
    {
        OnSta(() =>
        {
            var timer = NewTimer();
            ShowAway(timer);
            var chip = All(timer).OfType<Border>().First(b => b.Child is TextBlock { Text: "25 min" });
            Click(chip);
            timer.UpdateLayout();
            Assert.Contains("25:00", Shown(timer));
            timer.Close();
        });
    }

    [Fact]
    public void A_typed_time_starts_the_timer()
    {
        OnSta(() =>
        {
            var timer = NewTimer();
            ShowAway(timer);
            var box = All(timer).OfType<TextBox>().Single();
            box.Text = "7";
            var source = PresentationSource.FromVisual(box)!;
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Return) { RoutedEvent = Keyboard.KeyDownEvent });
            timer.UpdateLayout();
            Assert.Contains("07:00", Shown(timer));
            timer.Close();
        });
    }

    [Fact]
    public void The_record_button_and_switches_are_clicks_not_drags()
    {
        OnSta(() =>
        {
            var session = (RecordingSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RecordingSession));
            var bar = new RecordBar(session);
            ShowAway(bar);
            var row = All(bar).OfType<StackPanel>().First(p => p.Orientation == Orientation.Horizontal && p.Children.Count >= 5);
            var record = All(bar).OfType<Border>().First(b => b.Cursor == Cursors.Hand && b is not StripButton);
            Assert.True(ScreenUi.IsClickable(record.Child, row));
            var switches = All(bar).OfType<StripButton>().ToList();
            Assert.True(switches.Count >= 6, $"{switches.Count} switches");
            Assert.All(switches, s => Assert.True(ScreenUi.IsClickable(s, row)));
            bar.Close();
        });
    }
}
