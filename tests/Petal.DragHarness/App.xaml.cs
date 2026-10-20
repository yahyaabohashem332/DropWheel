using System.Windows;

namespace Petal.DragHarness;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var script = HarnessScript.Parse(e.Args);
        var window = new HarnessWindow(script);
        MainWindow = window;
        window.Show();
    }
}
