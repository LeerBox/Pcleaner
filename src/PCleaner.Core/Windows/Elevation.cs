using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using PCleaner.Core.Model;

namespace PCleaner.Core.Windows;

/// <summary>Helpers around the current process token and self-elevation.</summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    private static readonly Lazy<bool> IsElevatedLazy = new(ComputeIsElevated);

    /// <summary>True when the process runs with an administrator token.</summary>
    public static bool IsElevated => IsElevatedLazy.Value;

    /// <summary>
    /// Starts a new instance of the current executable with the "runas" verb (UAC prompt). Returns true when the
    /// new process started; the caller should then exit. Returns false when the user cancelled the prompt.
    /// </summary>
    public static bool TryRestartElevated(string arguments = "")
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        try
        {
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };
            return Process.Start(info) is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED: the user declined the UAC prompt.
            return false;
        }
    }

    private static bool ComputeIsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Detects running applications that would be disturbed by a cleanup.</summary>
public static class ProcessMonitor
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// Returns everything that currently blocks the rule: running process names plus a marker for every lock file
    /// that is held open exclusively (e.g. "parent.lock (profile in use)").
    /// </summary>
    public static IReadOnlyList<string> GetBlockers(CleanupRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var blockers = new List<string>();
        if (rule.ConflictingProcesses.Count > 0)
        {
            blockers.AddRange(GetRunning(rule.ConflictingProcesses));
        }

        foreach (var lockFile in rule.LockProbeFiles)
        {
            if (IsHeldExclusively(lockFile))
            {
                blockers.Add($"{Path.GetFileName(lockFile)} (profile in use)");
            }
        }

        return blockers;
    }

    /// <summary>True when the file exists and another process holds it without shared read access.</summary>
    public static bool IsHeldExclusively(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException ex) when (ex.HResult is ErrorSharingViolation or ErrorLockViolation)
        {
            return true;
        }
        catch (Exception)
        {
            // Access denied etc. - not a lock held by a running browser.
            return false;
        }
    }

    /// <summary>Returns the distinct process names (without extension) from <paramref name="names"/> that are running.</summary>
    public static IReadOnlyList<string> GetRunning(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var running = new List<string>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var clean = Path.GetFileNameWithoutExtension(name);
            Process[] processes = [];
            try
            {
                processes = Process.GetProcessesByName(clean);
                if (processes.Length > 0)
                {
                    running.Add(clean);
                }
            }
            catch (Exception)
            {
                // Process enumeration can fail for protected processes; treat as not running.
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }

        return running;
    }

    /// <summary>
    /// Politely asks every main window of the given processes to close (WM_CLOSE) and waits up to
    /// <paramref name="timeout"/>. Never kills processes: unsaved browser state must not be lost.
    /// Returns true when none of the processes is running anymore.
    /// </summary>
    public static async Task<bool> RequestCloseAsync(IEnumerable<string> names, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        var list = names.Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var name in list)
        {
            Process[] processes = [];
            try
            {
                processes = Process.GetProcessesByName(name!);
                foreach (var p in processes)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            p.CloseMainWindow();
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore processes we cannot signal.
                    }
                }
            }
            catch (Exception)
            {
                // Ignore enumeration failures.
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetRunning(list!).Count == 0)
            {
                return true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return GetRunning(list!).Count == 0;
    }
}