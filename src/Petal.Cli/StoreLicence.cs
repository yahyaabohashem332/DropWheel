using System.Runtime.InteropServices;
using System.Text;
using Windows.Services.Store;

namespace Petal.Cli;

/// <summary>
/// From the Microsoft Store's package, the command line works while Petal is bought or in its trial, like
/// the app. Installed from Petal's own setup, or when the Store can't say, it always works.
/// </summary>
internal static class StoreLicence
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    public static async Task<bool> AllowsAsync()
    {
        try
        {
            int length = 0;
            if (GetCurrentPackageFullName(ref length, null) == AppModelErrorNoPackage)
                return true;
            var licence = await StoreContext.GetDefault().GetAppLicenseAsync();
            return licence.IsActive;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
