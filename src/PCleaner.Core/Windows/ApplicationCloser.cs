using System.Diagnostics;
using System.Runtime.Versioning;
using PCleaner.Core.Browsers;
using PCleaner.Core.Logging;

namespace PCleaner.Core.Windows;

/// <summary>What PCleaner may do about an application that keeps a cleanup item locked.</summary>
public enum BlockingAppPolicy
{
    /// <summary>Leave it alone and skip the item (default).</summary>
    Skip = 0,

    /// <summary>Ask its windows to close (WM_CLOSE) and wait; skip the item if it stays open.</summary>
    RequestClose = 1,

    /// <summary>
    /// Ask first and wait; then end processes that keep no window open (background / tray instances). Anything that
    /// still shows a window - a prompt to save, a download warning - is left alone. Administrator mode only.
    /// </summary>
    AutoClose = 2,
}

/// <summary>An application PCleaner closed, with what is needed to bring it back afterwards.</summary>
/// <param name="ProcessName">Process name without extension (e.g. <c>brave</c>).</param>
/// <param name="ExecutablePath">Full path of the executable (null when it could not be read).</param>
/// <param name="HadVisibleWindows">True when the user could see it before it was closed.</param>
/// <param name="WasTerminated">True when it had to be ended, false when it closed on request.</param>
public sealed record ClosedApplication(string ProcessName, string? ExecutablePath, bool HadVisibleWindows, bool WasTerminated);

/// <summary>Result of <see cref="ApplicationCloser.CloseAsync"/>.</summary>
public sealed class CloseOutcome
{
    public IReadOnlyList<ClosedApplication> Closed { get; init; } = [];

    /// <summary>
    /// Applications whose visible windows were closed on request but whose processes kept running in the background
    /// (e.g. a browser with "startup boost"). Their items stay blocked; the window can be brought back.
    /// </summary>
    public IReadOnlyList<ClosedApplication> WindowsClosed { get; init; } = [];

    /// <summary>Process names that are still running after all allowed steps.</summary>
    public IReadOnlyList<string> StillRunning { get; init; } = [];

    /// <summary>Human readable explanations (for the status line and the log). Never contains window titles.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool AllClosed => StillRunning.Count == 0;
}

/// <summary>
/// Closes applications that block a cleanup - gracefully first, and only in <see cref="BlockingAppPolicy.AutoClose"/>
/// mode by ending processes that have no window left to lose. Rules of the game:
/// <list type="bullet">
/// <item>Only processes of the current desktop session are touched, never another user's.</item>
/// <item>Termination is limited to an allow-list of applications known to survive it (browsers, OneDrive, Teams);
/// Office, Remote Desktop, Windows components and everything else only ever receive a close request.</item>
/// <item>A process that still shows a window is never ended: a "save changes?" or "cancel downloads?" prompt must
/// reach the user.</item>
/// <item>Everything that was closed is remembered so it can be reopened - without administrator rights - once the
/// cleanup has finished (<see cref="Relaunch"/>).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ApplicationCloser
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Known browsers (for display names, the Chromium test and the termination allow-list).</summary>
    private static readonly Lazy<IReadOnlyList<BrowserDefinition>> Catalog = new(() =>
    {
        try
        {
            return BrowserCatalog.GetDefinitions(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        }
        catch (Exception)
        {
            return [];
        }
    });

    /// <summary>Applications with an official "please exit" command (run with the user's own rights).</summary>
    private static readonly Dictionary<string, string> GracefulExitArguments = new(NameComparer)
    {
        ["OneDrive"] = "/shutdown",
    };

    /// <summary>How to bring an application back that had no window when it was closed (tray / background instance).</summary>
    private static readonly Dictionary<string, string> BackgroundRelaunchArguments = new(NameComparer)
    {
        ["OneDrive"] = "/background",
    };

    /// <summary>Processes that are never ended, whatever the policy or the allow-list says.</summary>
    private static readonly HashSet<string> NeverTerminate = new(NameComparer)
    {
        "explorer", "svchost", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "dwm", "sihost", "ctfmon",
        "taskhostw", "conhost", "RuntimeBroker", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "TiWorker", "TrustedInstaller", "MsMpEng", "OUTLOOK", "WINWORD", "EXCEL", "POWERPNT", "ONENOTE", "lync", "mstsc",
        "WinStore.App", "iexplore", "PCleaner",
    };

    private readonly ICleanerLog _log;

    public ApplicationCloser(ICleanerLog? log = null)
    {
        _log = log ?? NullLog.Instance;
        TerminationAllowList = DefaultTerminationAllowList();
    }

    /// <summary>How long to wait after asking windows to close before giving up (or escalating).</summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Wait after a background process was ended, so that its children can go away too.</summary>
    public TimeSpan TerminationSettleTime { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Process names (without extension) that <see cref="BlockingAppPolicy.AutoClose"/> may end.</summary>
    public IReadOnlySet<string> TerminationAllowList { get; init; }

    /// <summary>Every known browser process plus the two background applications PCleaner has rules for.</summary>
    public static IReadOnlySet<string> DefaultTerminationAllowList()
    {
        var set = new HashSet<string>(NameComparer) { "OneDrive", "ms-teams", "msteams", "Teams" };
        foreach (var name in Catalog.Value.SelectMany(d => d.ProcessNames))
        {
            set.Add(Path.GetFileNameWithoutExtension(name));
        }

        set.ExceptWith(NeverTerminate);
        return set;
    }

    /// <summary>
    /// Closes the given applications according to <paramref name="policy"/>. Names may contain the lock-file markers
    /// produced by <see cref="ProcessMonitor.GetBlockers"/>; those are ignored.
    /// </summary>
    public async Task<CloseOutcome> CloseAsync(IEnumerable<string> processNames, BlockingAppPolicy policy, Action<string>? status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        var names = processNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Contains('(', StringComparison.Ordinal))
            .Select(n => Path.GetFileNameWithoutExtension(n.Trim()))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(NameComparer)
            .ToList();

        if (policy == BlockingAppPolicy.Skip || names.Count == 0)
        {
            return new CloseOutcome { StillRunning = names.Where(n => ProcessMonitor.GetRunning([n]).Count > 0).ToList() };
        }

        var notes = new List<string>();
        var initial = Snapshot(names);
        if (initial.Count == 0)
        {
            return new CloseOutcome();
        }

        // Remembered per application (process name): what the user saw, where it lives, what we did to it.
        var hadVisibleWindows = new HashSet<string>(initial.Where(p => p.HadVisibleWindows).Select(p => p.Name), NameComparer);
        var executables = initial.Where(p => p.ExecutablePath is not null).GroupBy(p => p.Name, NameComparer).ToDictionary(g => g.Key, g => g.First().ExecutablePath!, NameComparer);
        var exitCommandSent = new HashSet<string>(NameComparer);
        var terminated = new HashSet<string>(NameComparer);

        // ---- 1. Ask nicely: official exit commands, then WM_CLOSE to every visible window.
        foreach (var group in initial.GroupBy(p => p.Name, NameComparer))
        {
            status?.Invoke($"Asking {DisplayName(group.Key)} to close...");
            if (executables.TryGetValue(group.Key, out var exe) && GracefulExitArguments.TryGetValue(group.Key, out var exitArguments) && ProcessLauncher.StartAsDesktopUser(exe, exitArguments, _log))
            {
                exitCommandSent.Add(group.Key);
            }

            foreach (var process in group)
            {
                RequestClose(process.Id);
            }
        }

        // Wait for windows to close. Always look at the processes that exist NOW, by name: a browser with "startup
        // boost" exits and immediately re-spawns itself in the background under new process ids. Do not wait for
        // processes that have nothing to close (a windowless instance ignores WM_CLOSE).
        var started = DateTime.UtcNow;
        var deadline = started + GracePeriod;
        while (DateTime.UtcNow < deadline)
        {
            var current = Snapshot(names);
            if (current.Count == 0)
            {
                break;
            }

            var worthWaitingFor = current.Any(p => exitCommandSent.Contains(p.Name) || p.HadVisibleWindows);
            if (!worthWaitingFor && DateTime.UtcNow - started > TimeSpan.FromSeconds(3))
            {
                break;
            }

            var remaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds);
            var waitingFor = string.Join(", ", current.Select(p => DisplayName(p.Name)).Distinct(NameComparer));
            status?.Invoke($"Waiting for {waitingFor} to close ({remaining}s)...");
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        // ---- 2. Administrator mode: end what is left, but only processes that show no window at all.
        if (policy == BlockingAppPolicy.AutoClose)
        {
            var terminatedAny = false;
            var announced = new HashSet<string>(NameComparer);
            foreach (var process in Snapshot(names))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = DisplayName(process.Name);
                executables.TryAdd(process.Name, process.ExecutablePath ?? string.Empty);

                if (!TerminationAllowList.Contains(process.Name) || NeverTerminate.Contains(process.Name))
                {
                    AddNote(notes, $"{name} is not on the list of applications PCleaner may end - left running.");
                    continue;
                }

                if (process.HadVisibleWindows)
                {
                    AddNote(notes, $"{name} still shows a window (it may be asking you something) - left running.");
                    continue;
                }

                if (IsWindowsComponent(process.ExecutablePath))
                {
                    AddNote(notes, $"{name} is a Windows component - left running.");
                    continue;
                }

                if (announced.Add(process.Name))
                {
                    status?.Invoke($"Ending background process {name}...");
                }

                if (Terminate(process.Id, name))
                {
                    terminated.Add(process.Name);
                    terminatedAny = true;
                }
            }

            if (terminatedAny)
            {
                await Task.Delay(TerminationSettleTime, cancellationToken).ConfigureAwait(false);
            }
        }

        // ---- 3. Report. "Closed" is judged per application (all of its processes are gone), so it can be reopened.
        var closed = new List<ClosedApplication>();
        var windowsClosed = new List<ClosedApplication>();
        var stillRunning = new List<string>();
        foreach (var name in initial.Select(p => p.Name).Distinct(NameComparer))
        {
            executables.TryGetValue(name, out var exe);
            var exePath = string.IsNullOrEmpty(exe) ? null : exe;
            var remaining = Snapshot([name]);
            if (remaining.Count > 0)
            {
                stillRunning.Add(name);
                if (hadVisibleWindows.Contains(name) && remaining.All(p => !p.HadVisibleWindows))
                {
                    // We closed what the user saw, but a background instance survived: remember to give the window back.
                    windowsClosed.Add(new ClosedApplication(name, exePath, true, false));
                    AddNote(notes, $"{DisplayName(name)} closed its window but keeps running in the background (startup boost / background apps); its items stay blocked.");
                }

                continue;
            }

            closed.Add(new ClosedApplication(name, exePath, hadVisibleWindows.Contains(name), terminated.Contains(name)));
        }

        foreach (var app in closed)
        {
            _log.Info(app.WasTerminated
                ? $"{DisplayName(app.ProcessName)} kept running in the background without a window and was ended for the cleanup."
                : $"{DisplayName(app.ProcessName)} closed on request.");
        }

        foreach (var note in notes)
        {
            _log.Warn(note);
        }

        return new CloseOutcome { Closed = closed, WindowsClosed = windowsClosed, StillRunning = stillRunning, Notes = notes };
    }

    private static void AddNote(List<string> notes, string note)
    {
        if (!notes.Contains(note, StringComparer.Ordinal))
        {
            notes.Add(note);
        }
    }

    /// <summary>
    /// Starts the applications again that <see cref="CloseAsync"/> closed - with the desktop user's normal rights
    /// even when PCleaner runs elevated. Applications that were invisible before (tray/background) are only brought
    /// back when a background start is known for them, so no unexpected window pops up.
    /// Returns the display names that were started.
    /// </summary>
    public IReadOnlyList<string> Relaunch(IEnumerable<ClosedApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var started = new List<string>();

        foreach (var app in applications.Where(a => a.ExecutablePath is not null).DistinctBy(a => a.ExecutablePath!, StringComparer.OrdinalIgnoreCase))
        {
            string arguments;
            if (app.HadVisibleWindows)
            {
                arguments = string.Empty;
            }
            else if (BackgroundRelaunchArguments.TryGetValue(app.ProcessName, out var background))
            {
                arguments = background;
            }
            else if (IsChromiumBrowser(app.ProcessName))
            {
                arguments = "--no-startup-window";
            }
            else
            {
                _log.Info($"{DisplayName(app.ProcessName)} ran in the background only and was not reopened.");
                continue;
            }

            if (!File.Exists(app.ExecutablePath))
            {
                continue;
            }

            if (ProcessLauncher.StartAsDesktopUser(app.ExecutablePath!, arguments, _log))
            {
                started.Add(DisplayName(app.ProcessName));
                _log.Info($"{DisplayName(app.ProcessName)} reopened after the cleanup.");
            }
        }

        return started;
    }

    // ------------------------------------------------------------------ helpers

    private sealed class TrackedProcess
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public string? ExecutablePath { get; init; }

        public bool HadVisibleWindows { get; init; }
    }

    private static List<TrackedProcess> Snapshot(IEnumerable<string> names)
    {
        var session = SessionOf(Environment.ProcessId);
        var list = new List<TrackedProcess>();
        foreach (var name in names)
        {
            Process[] processes = [];
            try
            {
                processes = Process.GetProcessesByName(name);
                foreach (var process in processes)
                {
                    try
                    {
                        if (session >= 0 && process.SessionId != session)
                        {
                            continue; // another user's session
                        }

                        string? path = null;
                        try
                        {
                            path = process.MainModule?.FileName;
                        }
                        catch (Exception)
                        {
                            // Protected or foreign-bitness process: relaunch will not be possible, closing still is.
                        }

                        list.Add(new TrackedProcess { Id = process.Id, Name = name, ExecutablePath = path, HadVisibleWindows = HasVisibleWindow(process.Id) });
                    }
                    catch (Exception)
                    {
                        // Process vanished while we looked at it.
                    }
                }
            }
            catch (Exception)
            {
                // Enumeration failure - treat as not running.
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return list;
    }

    private static int SessionOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.SessionId;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>Posts WM_CLOSE to every visible top-level window of the process (a browser may have several).</summary>
    private static void RequestClose(int processId)
    {
        foreach (var window in TopLevelWindows((uint)processId))
        {
            _ = NativeMethods.PostMessage(window, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>True when the process owns at least one window a user can see (visible, not cloaked, not a tool window).</summary>
    public static bool HasVisibleWindow(int processId) => TopLevelWindows((uint)processId).Count > 0;

    private static List<IntPtr> TopLevelWindows(uint processId)
    {
        var result = new List<IntPtr>();
        var hwnd = NativeMethods.GetTopWindow(IntPtr.Zero);
        var guard = 0;
        while (hwnd != IntPtr.Zero && guard++ < 50_000)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == processId && NativeMethods.IsWindowVisible(hwnd) && !IsCloaked(hwnd) && !IsToolWindow(hwnd) && HasArea(hwnd))
            {
                result.Add(hwnd);
            }

            hwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT);
        }

        return result;
    }

    private static bool IsCloaked(IntPtr hwnd)
        => NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsToolWindow(IntPtr hwnd)
        => (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_TOOLWINDOW) != 0;

    private static bool HasArea(IntPtr hwnd)
        => NativeMethods.GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;

    private static bool IsWindowsComponent(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return executablePath.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private bool Terminate(int processId, string displayName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not end {displayName}: {ex.Message}");
            return false;
        }
    }

    private static bool IsChromiumBrowser(string processName)
        => Catalog.Value.Any(d => d.Family == BrowserFamily.Chromium && d.ProcessNames.Any(n => NameComparer.Equals(Path.GetFileNameWithoutExtension(n), processName)));

    /// <summary>Plain names for the non-browser processes rules may list as conflicting.</summary>
    private static readonly Dictionary<string, string> KnownProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OneDrive"] = "OneDrive",
        ["mstsc"] = "Remote Desktop Connection",
        ["OUTLOOK"] = "Outlook",
        ["WINWORD"] = "Word",
        ["EXCEL"] = "Excel",
        ["POWERPNT"] = "PowerPoint",
        ["ONENOTE"] = "OneNote",
        ["lync"] = "Skype for Business",
        ["iexplore"] = "Internet Explorer",
        ["WinStore.App"] = "Microsoft Store",
        ["TiWorker"] = "Windows Update installer",
        ["TrustedInstaller"] = "Windows Update installer",
    };

    /// <summary>Friendly name for the status line ("brave" → "Brave", "msedge" → "Microsoft Edge", "mstsc" → "Remote Desktop Connection").</summary>
    public static string DisplayName(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var definition = Catalog.Value.FirstOrDefault(d => d.ProcessNames.Any(n => NameComparer.Equals(Path.GetFileNameWithoutExtension(n), processName)));
        if (definition is not null)
        {
            return definition.Name;
        }

        if (KnownProcessNames.TryGetValue(processName, out var known))
        {
            return known;
        }

        if (processName.StartsWith("ms-teams", StringComparison.OrdinalIgnoreCase) || NameComparer.Equals(processName, "Teams") || NameComparer.Equals(processName, "msteams"))
        {
            return "Microsoft Teams";
        }

        return processName;
    }
}

/// <summary>
/// Starts a program with the interactive user's normal token even when PCleaner runs elevated, so a browser that
/// is reopened after a cleanup does not inherit administrator rights. Uses the Explorer shell's token
/// (CreateProcessWithTokenW); falls back to letting Explorer start the program.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessLauncher
{
    public static bool StartAsDesktopUser(string executable, string arguments, ICleanerLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        log ??= NullLog.Instance;

        if (!Elevation.IsElevated)
        {
            return StartDirect(executable, arguments, log);
        }

        if (TryStartWithShellToken(executable, arguments, log))
        {
            return true;
        }

        // Explorer runs at medium integrity and launches its children the same way (arguments cannot be passed).
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var process = Process.Start(new ProcessStartInfo(explorer, $"\"{executable}\"") { UseShellExecute = false });
            log.Debug($"Started {Path.GetFileName(executable)} through Explorer (without arguments).");
            return process is not null;
        }
        catch (Exception ex)
        {
            log.Warn($"Could not start {Path.GetFileName(executable)}: {ex.Message}");
            return false;
        }
    }

    private static bool StartDirect(string executable, string arguments, ICleanerLog log)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            });
            return process is not null;
        }
        catch (Exception ex)
        {
            log.Warn($"Could not start {Path.GetFileName(executable)}: {ex.Message}");
            return false;
        }
    }

    private static bool TryStartWithShellToken(string executable, string arguments, ICleanerLog log)
    {
        var shell = NativeMethods.GetShellWindow();
        if (shell == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(shell, out var shellProcessId);
        if (shellProcessId == 0)
        {
            return false;
        }

        var process = IntPtr.Zero;
        var token = IntPtr.Zero;
        var primary = IntPtr.Zero;
        try
        {
            process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION, false, shellProcessId);
            if (process == IntPtr.Zero)
            {
                return false;
            }

            const uint access = NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_ASSIGN_PRIMARY | NativeMethods.TOKEN_ADJUST_DEFAULT | NativeMethods.TOKEN_ADJUST_SESSIONID;
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_DUPLICATE, out token))
            {
                return false;
            }

            if (!NativeMethods.DuplicateTokenEx(token, access, IntPtr.Zero, NativeMethods.SECURITY_IMPERSONATION, NativeMethods.TOKEN_PRIMARY, out primary))
            {
                return false;
            }

            var commandLine = (string.IsNullOrWhiteSpace(arguments) ? $"\"{executable}\"" : $"\"{executable}\" {arguments}") + "\0";
            var buffer = commandLine.ToCharArray();
            var startup = new NativeMethods.STARTUPINFOW { cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.STARTUPINFOW>() };
            var ok = NativeMethods.CreateProcessWithToken(primary, 0, null, buffer, NativeMethods.CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var info);
            if (!ok)
            {
                log.Debug($"CreateProcessWithTokenW failed for {Path.GetFileName(executable)} (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
                return false;
            }

            _ = NativeMethods.CloseHandle(info.hThread);
            _ = NativeMethods.CloseHandle(info.hProcess);
            return true;
        }
        finally
        {
            if (primary != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(primary);
            }

            if (token != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(token);
            }

            if (process != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(process);
            }
        }
    }
}