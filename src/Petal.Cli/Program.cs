using System.Globalization;
using System.Text;

namespace Petal.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // Output is meant for scripts as much as people: the same numbers whatever the PC's region.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // The first Ctrl+C stops the work cleanly; partial results are removed by the engines.
            e.Cancel = true;
            cancel.Cancel();
        };
        var cli = new Cli(Console.Out, Console.Error, showProgress: !Console.IsErrorRedirected);
        return await cli.RunAsync(args, cancel.Token);
    }
}
