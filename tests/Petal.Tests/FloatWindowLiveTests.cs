using System.IO;
using System.Windows;
using Petal.App.Actions.Float;

namespace Petal.Tests;

/// <summary>A real floating window, shown off the screen: what it shows, clicks passing through it, folding it.</summary>
[Collection(PdfEditorWindowTests.OneApplication)]
public sealed class FloatWindowLiveTests
{
    [Fact]
    public void A_floating_window_shows_its_file_lets_clicks_through_and_back_and_folds() => PdfEditorWindowTests.RunOnInterface(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "petal-float-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "notes.txt");
        File.WriteAllText(file, "first line\nsecond line\n");
        var window = FloatWindow.ForSnapshot(file, 520, 420);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.Show();
        try
        {
            for (int i = 0; i < 40 && window.View is not TextFloatView; i++)
                await Task.Delay(50);
            Assert.IsType<TextFloatView>(window.View);
            for (int i = 0; i < 40 && !window.View.Facts.Contains('2'); i++)
                await Task.Delay(50);
            Assert.Contains("2", window.View.Facts);

            // Clicks pass through, the window is seen through, and a button stays to stop it.
            Assert.False(window.PassesClicks);
            window.SetThrough(true);
            Assert.True(window.PassesClicks);
            Assert.True(window.Opacity < 0.8);
            Assert.NotNull(window.StopButton);
            Assert.True(window.StopButton!.IsVisible);
            window.SetThrough(false);
            Assert.False(window.PassesClicks);
            Assert.Equal(1, window.Opacity, 3);
            Assert.Null(window.StopButton);

            // Folded to its title bar and open again at the height it had.
            double height = window.Height;
            window.ToggleFold();
            Assert.True(window.Height < 70);
            window.ToggleFold();
            Assert.Equal(height, window.Height, 1);

            // Saved again in its own program: shown again.
            var shown = window.View;
            File.AppendAllText(file, "third line\n");
            for (int i = 0; i < 60 && ReferenceEquals(window.View, shown); i++)
                await Task.Delay(50);
            Assert.NotSame(shown, window.View);
        }
        finally
        {
            window.Close();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    });
}
