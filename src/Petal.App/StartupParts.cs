using Petal.App.Localization;

namespace Petal.App;

/// <summary>A part of DropWheel that didn't start, by its name in the interface, and why.</summary>
public sealed record StartupFailure(string Part, Exception Error)
{
    /// <summary>The kind of error and the first line of its message, for Health and the report.</summary>
    public string Reason => $"{Error.GetType().Name}: {Error.Message.Split('\n')[0].Trim()}";
}

/// <summary>
/// The parts DropWheel starts with, each on its own. One that fails is written to the activity log, named in
/// DropWheel Health and in a card, and the others start all the same. Before, the first failure stopped
/// start-up without a word and left DropWheel running with no icon, no keys and no shortcuts.
/// </summary>
public static class StartupParts
{
    private static readonly List<StartupFailure> Failures = [];

    /// <summary>The parts that didn't start since DropWheel was opened.</summary>
    public static IReadOnlyList<StartupFailure> Failed
    {
        get
        {
            lock (Failures)
                return [.. Failures];
        }
    }

    /// <summary>
    /// Starts one part, named as the interface names it ("Clipboard History"). A failure is kept and written
    /// down, and start-up goes on without that part. Returns whether it started.
    /// </summary>
    public static bool Run(string part, Action start)
    {
        try
        {
            start();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Start-up: {part} didn't start");
            lock (Failures)
                Failures.Add(new StartupFailure(part, ex));
            return false;
        }
    }

    /// <summary>The parts' names in the interface language, each once, in a list ("Shortcuts, Quick Look").</summary>
    public static string Names(IEnumerable<string> parts) =>
        string.Join(L.IsRightToLeft ? "، " : ", ", parts.Distinct().Select(L.T));

    /// <summary>For tests: as if DropWheel had just been opened.</summary>
    internal static void Forget()
    {
        lock (Failures)
            Failures.Clear();
    }
}
