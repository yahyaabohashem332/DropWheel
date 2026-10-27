using System.Runtime.InteropServices;
using System.Text;
using Windows.Services.Store;

namespace Petal.App.Shell;

/// <summary>
/// Petal from the Microsoft Store runs as a package: Windows starts it at sign-in through the package's
/// startup task, the Store updates it, its "Convert with Petal" entry comes from the package, and the
/// Store's licence says whether it is in its trial. Installed from Petal's own setup, none of this applies.
/// </summary>
public static partial class Packaged
{
    public const string StartupTaskId = "PetalStartup";

    /// <summary>True when Petal runs from its Store package.</summary>
    public static bool IsPackaged { get; } = Check();

    private static bool Check()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    /// <summary>Starts with Windows, or not, through the package's startup task (the user can also turn it off in Task Manager).</summary>
    public static async Task SetStartupAsync(bool enabled)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (enabled && task.State is Windows.ApplicationModel.StartupTaskState.Disabled)
                await task.RequestEnableAsync();
            else if (!enabled && task.State is Windows.ApplicationModel.StartupTaskState.Enabled)
                task.Disable();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            Log.Error(ex, "Changing the startup task");
        }
    }

    // ---------- the Store's licence ----------

    /// <summary>What the Store says about this copy: bought, in its trial (with the time left), or the trial over.</summary>
    public sealed record Licence(bool Active, bool Trial, TimeSpan TrialLeft);

    public static Licence? Current { get; private set; }

    public static event Action<Licence>? LicenceChanged;

    private static StoreContext? _context;

    private static StoreContext Context(IntPtr owner)
    {
        _context ??= StoreContext.GetDefault();
        // A desktop app shows the Store's own dialogs over one of its windows.
        if (owner != IntPtr.Zero)
            WinRT.Interop.InitializeWithWindow.Initialize(_context, owner);
        return _context;
    }

    /// <summary>Reads the licence; null outside the Store package or when the Store can't be reached.</summary>
    public static async Task<Licence?> CheckAsync(IntPtr owner = default)
    {
        if (!IsPackaged)
            return null;
        try
        {
            var licence = await Context(owner).GetAppLicenseAsync();
            var left = licence.IsTrial ? licence.ExpirationDate - DateTimeOffset.Now : TimeSpan.Zero;
            Current = new Licence(licence.IsActive, licence.IsTrial, left < TimeSpan.Zero ? TimeSpan.Zero : left);
            LicenceChanged?.Invoke(Current);
            if (_watching is false)
            {
                _watching = true;
                Context(owner).OfflineLicensesChanged += async (_, _) => await CheckAsync();
            }
            return Current;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Reading the Store licence");
            return null;
        }
    }

    private static bool _watching;

    /// <summary>The Store's own purchase dialog for Petal; true when it was bought.</summary>
    public static async Task<bool> BuyAsync(IntPtr owner)
    {
        if (!IsPackaged)
            return false;
        try
        {
            var context = Context(owner);
            var product = await context.GetStoreProductForCurrentAppAsync();
            if (product.Product is null)
                return false;
            var result = await context.RequestPurchaseAsync(product.Product.StoreId);
            await CheckAsync(owner);
            return result.Status is StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Buying DropWheel in the Store");
            return false;
        }
    }
}
