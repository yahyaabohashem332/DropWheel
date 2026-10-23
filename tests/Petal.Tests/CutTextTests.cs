using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Petal.App;
using Petal.App.Tools;

namespace Petal.Tests;

/// <summary>
/// The snapshot run's check for words a window cuts (used to review every language) finds a cut label,
/// a cut segment and a trimmed name in a tree laid out away from any window, and passes words that fit.
/// </summary>
public sealed class CutTextTests
{
    private static List<string> Lay(FrameworkElement content, double width)
    {
        var frame = new Border { Width = width, Child = content };
        frame.Measure(new Size(width, double.PositiveInfinity));
        frame.Arrange(new Rect(new Size(width, frame.DesiredSize.Height)));
        frame.UpdateLayout();
        return DesignSnapshots.CutTexts(frame).ToList();
    }

    // One test on one thread: a process has a single WPF Application, and the segments need its styles.
    [Fact]
    public void Cut_words_are_found_and_words_that_fit_are_not()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Petal;component/Styles/Controls.xaml") });
                Petal.App.Visuals.ThemeResources.Install(app);

                // A label wider than its column.
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new TextBlock { Text = "A label far too long for sixty pixels" });
                Assert.Contains(Lay(grid, 300), c => c.StartsWith("CUT") && c.EndsWith("A label far too long for sixty pixels"));

                // Words that fit, and hidden words, are left alone.
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Short" });
                panel.Children.Add(new TextBlock { Text = "A hidden label far too long for the frame it sits in", Visibility = Visibility.Collapsed });
                Assert.Empty(Lay(panel, 300));

                // A trimmed name is told apart from a cut.
                var name = new TextBlock { Text = "a very long file name that will not fit here.mov", TextTrimming = TextTrimming.CharacterEllipsis };
                var trimmed = Lay(name, 80);
                Assert.Single(trimmed);
                Assert.StartsWith("trimmed", trimmed[0]);

                // Equal thirds of 300 would cut the long segment; segments as wide as their words fit.
                var wide = new Segmented<int>([(0, "Tel que prononcé"), (1, "En anglais"), (2, "En arabe")], 0);
                Assert.Empty(Lay(wide, 300));
                // Where even that can't fit, they go on more rows, still whole.
                var cramped = new Segmented<int>([(0, "Tel que prononcé"), (1, "En anglais"), (2, "En arabe")], 0);
                Assert.Empty(Lay(cramped, 120));
                Assert.True(cramped.ActualHeight > wide.ActualHeight * 1.5, $"{cramped.ActualHeight} vs {wide.ActualHeight}");

                // A label cut by a box the segments can't widen is still found.
                var tight = new Grid { Width = 40, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
                tight.Children.Add(new TextBlock { Text = "Majuscule à chaque mot" });
                Assert.NotEmpty(Lay(tight, 300));
            }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
