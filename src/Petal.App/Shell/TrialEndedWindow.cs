using System.Windows;
using System.Windows.Interop;
using Petal.App.Localization;
using Petal.App.Tools;
using Petal.App.Tools.Windows;

namespace Petal.App.Shell;

/// <summary>
/// The Store's trial is over: Buy opens the Store's own purchase dialog; closing the window without buying
/// quits Petal, whose wheel stays off until then.
/// </summary>
public sealed class TrialEndedWindow : ToolWindow
{
    private static TrialEndedWindow? _open;
    private bool _bought;

    public static void ShowSingle()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return;
        }
        _open = new TrialEndedWindow();
        _open.Closed += (_, _) => _open = null;
        _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
        _open.Activate();
    }

    private TrialEndedWindow() : base("Your Trial Has Ended", 440)
    {
        Settings.SettingsStore.Current.Paused = true;
        var title = Text("Thank you for trying Petal", "Petal.Section");
        title.FontSize = 15;
        Body.Children.Add(title);
        Body.Children.Add(Text("The trial is over. Buy Petal in the Microsoft Store to keep converting and using the tools and the wheels. Your settings, wheels and history are kept.", "Petal.Body").WithMarginT(8));
        var status = Text("", "Petal.Caption");
        status.Margin = new Thickness(0, 10, 0, 0);
        Body.Children.Add(status);
        PrimaryButton.Content = L.T("Buy Petal");
        PrimaryButton.Click += async (_, _) =>
        {
            PrimaryButton.IsEnabled = false;
            if (await Packaged.BuyAsync(new WindowInteropHelper(this).Handle))
            {
                _bought = true;
                Settings.SettingsStore.Current.Paused = false;
                Settings.SettingsStore.Save();
                Close();
                return;
            }
            status.Text = L.T("The purchase didn't go through. You can try again.");
            PrimaryButton.IsEnabled = true;
        };
        ResetButton.Content = L.T("Quit Petal");
        ResetButton.Visibility = Visibility.Visible;
        ResetButton.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            // Only the Store's copy has a trial (a design snapshot of this window must not end the app).
            if (!_bought && Packaged.IsPackaged)
                App.Current.Quit();
        };
    }
}
