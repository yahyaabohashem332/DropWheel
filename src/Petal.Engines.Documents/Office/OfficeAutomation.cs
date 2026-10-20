using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Petal.Engines.Documents.Office;

internal enum OfficeApp
{
    Word,
    Excel,
    PowerPoint,
}

/// <summary>
/// Drives Word, Excel and PowerPoint through late-bound COM for exact-layout PDF export.
/// Each job runs on its own STA thread, invisibly, with alerts and macros off. The Office
/// process Petal started is always quit, and killed if it hangs past the timeout or the job is
/// cancelled; an instance the user already had open (PowerPoint shares one) is never quit.
/// </summary>
internal static partial class OfficeAutomation
{
    private static readonly Dictionary<OfficeApp, SemaphoreSlim> Gates = new()
    {
        [OfficeApp.Word] = new(1, 1),
        [OfficeApp.Excel] = new(1, 1),
        [OfficeApp.PowerPoint] = new(1, 1),
    };

    private static readonly Dictionary<OfficeApp, bool> InstalledCache = [];

    private static string ProgId(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    private static string ProcessName(OfficeApp app) => app switch
    {
        OfficeApp.Word => "WINWORD",
        OfficeApp.Excel => "EXCEL",
        _ => "POWERPNT",
    };

    /// <summary>True when the application is registered for automation and its executable exists.</summary>
    public static bool IsInstalled(OfficeApp app)
    {
        lock (InstalledCache)
        {
            if (InstalledCache.TryGetValue(app, out var cached))
                return cached;
            var installed = false;
            try
            {
                using var progId = Registry.ClassesRoot.OpenSubKey(ProgId(app) + @"\CLSID");
                if (progId?.GetValue(null) is string clsid)
                {
                    using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\LocalServer32");
                    var command = server?.GetValue(null) as string;
                    var exe = ExecutableFrom(command);
                    installed = exe is not null && File.Exists(exe);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                installed = false;
            }
            InstalledCache[app] = installed;
            return installed;
        }
    }

    private static string? ExecutableFrom(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }

    /// <summary>Opens <paramref name="input"/> read-only in the Office app and exports it as PDF.</summary>
    public static Task ExportPdfAsync(OfficeApp app, string input, string outputPdf, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(app, timeout, ct, (application, _) =>
        {
            switch (app)
            {
                case OfficeApp.Word: WordExport(application, input, outputPdf); break;
                case OfficeApp.Excel: ExcelExport(application, input, outputPdf); break;
                default: PowerPointExport(application, input, outputPdf); break;
            }
        });

    /// <summary>Saves a copy in another Office format (used to make legacy .ppt/.xls/.doc copies).</summary>
    public static Task SaveAsAsync(OfficeApp app, string input, string output, int fileFormat, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(app, timeout, ct, (application, _) =>
        {
            switch (app)
            {
                case OfficeApp.Word:
                {
                    var doc = OpenWord(application, input);
                    try { doc.SaveAs2(FileName: output, FileFormat: fileFormat); }
                    finally { CloseWord(doc); }
                    break;
                }
                case OfficeApp.Excel:
                {
                    var wb = OpenExcel(application, input);
                    try { wb.SaveAs(Filename: output, FileFormat: fileFormat); }
                    finally { CloseExcel(wb); }
                    break;
                }
                default:
                {
                    var pres = OpenPowerPoint(application, input);
                    try { pres.SaveAs(FileName: output, FileFormat: fileFormat); }
                    finally { ClosePowerPoint(pres); }
                    break;
                }
            }
        });

    private static async Task RunAsync(OfficeApp app, TimeSpan timeout, CancellationToken ct, Action<dynamic, int?> work)
    {
        var gate = Gates[app];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var name = ProcessName(app);
            var before = ProcessIds(name);
            var ownedPid = new int?[1];
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                dynamic? application = null;
                try
                {
                    var type = Type.GetTypeFromProgID(ProgId(app), throwOnError: true)!;
                    application = Activator.CreateInstance(type)!;
                    int? pid = ProcessIdOf(app, (object)application, before);
                    // PowerPoint is single-instance: a pid that existed before belongs to the user.
                    if (pid is { } p && !before.Contains(p))
                        ownedPid[0] = p;
                    work(application, ownedPid[0]);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(Unwrap(ex));
                }
                finally
                {
                    if (application is not null)
                    {
                        try
                        {
                            if (ownedPid[0] is not null || app != OfficeApp.PowerPoint)
                            {
                                if (app == OfficeApp.Word) application.Quit(SaveChanges: 0);
                                else application.Quit();
                            }
                        }
                        catch (Exception)
                        {
                            // Already gone, or killed after a timeout.
                        }
                        try { Marshal.FinalReleaseComObject(application); } catch (ArgumentException) { }
                    }
                    application = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            })
            {
                IsBackground = true,
                Name = $"Petal {app} automation",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            var finished = await Task.WhenAny(done.Task, Task.Delay(timeout, ct)).ConfigureAwait(false) == done.Task;
            if (!finished)
            {
                // Hung or cancelled: killing our Office process makes the pending COM call fail and the thread exit.
                KillOwned(ownedPid[0], before, name);
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"{app} did not finish within {timeout.TotalSeconds:0} seconds.");
            }
            thread.Join(TimeSpan.FromSeconds(10));
            EnsureExited(ownedPid[0], before, name);
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static Exception Unwrap(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static HashSet<int> ProcessIds(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Select(p => p.Id).ToHashSet(); }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    private static int? ProcessIdOf(OfficeApp app, object comObject, HashSet<int> before)
    {
        dynamic application = comObject;
        try
        {
            long hwnd = 0;
            if (app == OfficeApp.Excel) hwnd = (long)application.Hwnd;
            else if (app == OfficeApp.PowerPoint) hwnd = (long)application.HWND;
            if (hwnd != 0 && GetWindowThreadProcessId((nint)hwnd, out var pid) != 0)
                return (int)pid;
        }
        catch (Exception)
        {
            // Fall back to comparing process lists.
        }
        if (app == OfficeApp.Word)
        {
            try
            {
                // Word has no Hwnd property; give the hidden window a unique caption and find it.
                var caption = "Petal-" + Guid.NewGuid().ToString("N");
                application.Caption = caption;
                var hwnd = FindWindow("OpusApp", caption);
                if (hwnd != 0 && GetWindowThreadProcessId(hwnd, out var pid) != 0)
                    return (int)pid;
            }
            catch (Exception)
            {
                // Fall back to comparing process lists.
            }
        }
        var fresh = ProcessIds(ProcessName(app)).Except(before).ToList();
        if (fresh.Count == 1)
            return fresh[0];
        // Nothing new: we attached to an instance that was already running.
        var existing = ProcessIds(ProcessName(app));
        return existing.Count == 1 ? existing.First() : null;
    }

    private static void KillOwned(int? pid, HashSet<int> before, string name)
    {
        var targets = pid is { } p ? [p] : ProcessIds(name).Except(before).ToList();
        foreach (var id in targets)
        {
            if (before.Contains(id))
                continue;
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exited.
            }
        }
    }

    /// <summary>After Quit the process should end on its own; if it lingers, end it.</summary>
    private static void EnsureExited(int? pid, HashSet<int> before, string name)
    {
        var targets = pid is { } p ? [p] : ProcessIds(name).Except(before).ToList();
        foreach (var id in targets)
        {
            if (before.Contains(id))
                continue;
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.WaitForExit(10_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exited.
            }
        }
    }

    // ───────────────────────────── Word ─────────────────────────────

    private static dynamic OpenWord(dynamic app, string input)
    {
        app.Visible = false;
        app.DisplayAlerts = 0; // wdAlertsNone
        TrySet(() => app.ScreenUpdating = false);
        TrySet(() => app.AutomationSecurity = 3); // msoAutomationSecurityForceDisable
        TrySet(() => app.Options.DoNotPromptForConvert = true);
        TrySet(() => app.Options.ConfirmConversions = false);
        TrySet(() => app.Options.SaveNormalPrompt = false);
        dynamic documents = app.Documents;
        try
        {
            // A dummy password makes protected files fail instead of showing a password prompt.
            return documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                PasswordDocument: "\u0001petal", PasswordTemplate: "\u0001petal", Revert: false, Visible: false,
                OpenAndRepair: false, NoEncodingDialog: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(documents);
        }
    }

    private static void CloseWord(dynamic doc)
    {
        try { doc.Close(SaveChanges: 0); } catch (COMException) { }
        Marshal.FinalReleaseComObject(doc);
    }

    private static void WordExport(dynamic app, string input, string output)
    {
        var doc = OpenWord(app, input);
        try
        {
            doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17 /* wdExportFormatPDF */, OpenAfterExport: false,
                OptimizeFor: 0, Range: 0, Item: 0, IncludeDocProps: true, KeepIRM: true, CreateBookmarks: 1 /* headings */,
                DocStructureTags: true, BitmapMissingFonts: true, UseISO19005_1: false);
        }
        finally
        {
            CloseWord(doc);
        }
    }

    // ───────────────────────────── Excel ─────────────────────────────

    private static dynamic OpenExcel(dynamic app, string input)
    {
        app.Visible = false;
        app.DisplayAlerts = false;
        TrySet(() => app.ScreenUpdating = false);
        TrySet(() => app.Interactive = false);
        TrySet(() => app.AskToUpdateLinks = false);
        TrySet(() => app.EnableEvents = false);
        TrySet(() => app.AutomationSecurity = 3);
        dynamic workbooks = app.Workbooks;
        try
        {
            return workbooks.Open(Filename: input, UpdateLinks: 0, ReadOnly: true, Password: "\u0001petal",
                IgnoreReadOnlyRecommended: true, Notify: false, AddToMru: false, Editable: false);
        }
        finally
        {
            Marshal.FinalReleaseComObject(workbooks);
        }
    }

    private static void CloseExcel(dynamic wb)
    {
        try { wb.Close(SaveChanges: false); } catch (COMException) { }
        Marshal.FinalReleaseComObject(wb);
    }

    private static void ExcelExport(dynamic app, string input, string output)
    {
        var wb = OpenExcel(app, input);
        try
        {
            wb.ExportAsFixedFormat(Type: 0 /* xlTypePDF */, Filename: output, Quality: 0, IncludeDocProperties: true,
                IgnorePrintAreas: false, OpenAfterPublish: false);
        }
        finally
        {
            CloseExcel(wb);
        }
    }

    // ───────────────────────────── PowerPoint ─────────────────────────────

    private static dynamic OpenPowerPoint(dynamic app, string input)
    {
        // PowerPoint refuses Visible = false; WithWindow: false keeps the presentation hidden instead.
        TrySet(() => app.DisplayAlerts = 1); // ppAlertsNone
        dynamic presentations = app.Presentations;
        try
        {
            return presentations.Open(FileName: input, ReadOnly: -1 /* msoTrue */, Untitled: 0, WithWindow: 0);
        }
        finally
        {
            Marshal.FinalReleaseComObject(presentations);
        }
    }

    private static void ClosePowerPoint(dynamic pres)
    {
        try { pres.Close(); } catch (COMException) { }
        Marshal.FinalReleaseComObject(pres);
    }

    private static void PowerPointExport(dynamic app, string input, string output)
    {
        int? previousSecurity = null;
        try { previousSecurity = (int)app.AutomationSecurity; app.AutomationSecurity = 3; } catch (Exception) { }
        var pres = OpenPowerPoint(app, input);
        try
        {
            pres.SaveAs(FileName: output, FileFormat: 32 /* ppSaveAsPDF */);
        }
        finally
        {
            ClosePowerPoint(pres);
            // The instance may be the user's own; put their setting back.
            if (previousSecurity is { } s)
                TrySet(() => app.AutomationSecurity = s);
        }
    }

    private static void TrySet(Action set)
    {
        try { set(); }
        catch (Exception)
        {
            // Not every Office version exposes every option.
        }
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
