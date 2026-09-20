using System.Diagnostics;
using PCleaner.Core.Windows;

namespace PCleaner.Core.Tests;

/// <summary>
/// Closing blocking applications. A copy of ping.exe (60 s, no window) plays the role of a background process
/// such as a browser's "startup boost" instance; the real System32 ping.exe plays a Windows component.
/// </summary>
public sealed class ApplicationCloserTests
{
    private static string SystemPing => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");

    private static Process StartHidden(string executable, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("could not start test process");
        Thread.Sleep(300);
        return process;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>A private copy of ping.exe with a unique process name so the test never touches anything else.</summary>
    private static string CopyPing(TempTree tree, string name)
    {
        var target = Path.Combine(tree.Root, name + ".exe");
        File.Copy(SystemPing, target);
        return target;
    }

    [Fact]
    public async Task Skip_policy_touches_nothing()
    {
        using var tree = new TempTree();
        var exe = CopyPing(tree, "pcl_skiptest");
        var process = StartHidden(exe, "-n 60 127.0.0.1");
        try
        {
            var closer = new ApplicationCloser { GracePeriod = TimeSpan.FromSeconds(2) };
            var outcome = await closer.CloseAsync(["pcl_skiptest"], BlockingAppPolicy.Skip, null, CancellationToken.None);

            Assert.False(process.HasExited);
            Assert.Empty(outcome.Closed);
            Assert.Contains("pcl_skiptest", outcome.StillRunning, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Fact]
    public async Task Request_close_never_ends_a_windowless_process_and_does_not_wait_for_it()
    {
        using var tree = new TempTree();
        var exe = CopyPing(tree, "pcl_requesttest");
        var process = StartHidden(exe, "-n 60 127.0.0.1");
        try
        {
            var closer = new ApplicationCloser { GracePeriod = TimeSpan.FromSeconds(30) };
            var stopwatch = Stopwatch.StartNew();
            var outcome = await closer.CloseAsync(["pcl_requesttest"], BlockingAppPolicy.RequestClose, null, CancellationToken.None);

            Assert.False(process.HasExited, "a close request must never kill");
            Assert.Empty(outcome.Closed);
            Assert.Contains("pcl_requesttest", outcome.StillRunning, StringComparer.OrdinalIgnoreCase);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "nothing to wait for: a windowless process ignores WM_CLOSE");
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Fact]
    public async Task Auto_close_ends_an_allow_listed_background_process_and_remembers_it()
    {
        using var tree = new TempTree();
        var exe = CopyPing(tree, "pcl_autotest");
        var process = StartHidden(exe, "-n 60 127.0.0.1");
        var statuses = new List<string>();
        try
        {
            var closer = new ApplicationCloser
            {
                GracePeriod = TimeSpan.FromSeconds(5),
                TerminationSettleTime = TimeSpan.FromMilliseconds(200),
                TerminationAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pcl_autotest" },
            };
            var outcome = await closer.CloseAsync(["pcl_autotest", "parent.lock (profile in use)"], BlockingAppPolicy.AutoClose, statuses.Add, CancellationToken.None);

            Assert.True(process.HasExited);
            Assert.True(outcome.AllClosed);
            var closed = Assert.Single(outcome.Closed);
            Assert.Equal("pcl_autotest", closed.ProcessName);
            Assert.True(closed.WasTerminated);
            Assert.False(closed.HadVisibleWindows);
            Assert.Equal(exe, closed.ExecutablePath, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(statuses, s => s.Contains("Ending background process", StringComparison.Ordinal));
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Fact]
    public async Task Auto_close_refuses_processes_outside_the_allow_list()
    {
        using var tree = new TempTree();
        var exe = CopyPing(tree, "pcl_notallowed");
        var process = StartHidden(exe, "-n 60 127.0.0.1");
        try
        {
            var closer = new ApplicationCloser { GracePeriod = TimeSpan.FromSeconds(4), TerminationAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brave" } };
            var outcome = await closer.CloseAsync(["pcl_notallowed"], BlockingAppPolicy.AutoClose, null, CancellationToken.None);

            Assert.False(process.HasExited);
            Assert.Contains("pcl_notallowed", outcome.StillRunning, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(outcome.Notes, n => n.Contains("not on the list", StringComparison.Ordinal));
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Fact]
    public async Task Auto_close_never_ends_a_windows_component()
    {
        var process = StartHidden(SystemPing, "-n 60 127.0.0.1");
        try
        {
            var closer = new ApplicationCloser { GracePeriod = TimeSpan.FromSeconds(4), TerminationAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PING" } };
            var outcome = await closer.CloseAsync(["PING"], BlockingAppPolicy.AutoClose, null, CancellationToken.None);

            Assert.False(process.HasExited);
            Assert.Contains(outcome.Notes, n => n.Contains("Windows component", StringComparison.Ordinal));
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Fact]
    public void Default_allow_list_contains_browsers_and_background_apps_but_never_office_or_windows()
    {
        var list = ApplicationCloser.DefaultTerminationAllowList();

        Assert.Contains("chrome", list);
        Assert.Contains("msedge", list);
        Assert.Contains("brave", list);
        Assert.Contains("firefox", list);
        Assert.Contains("OneDrive", list);
        Assert.Contains("ms-teams", list);
        Assert.DoesNotContain("OUTLOOK", list);
        Assert.DoesNotContain("WINWORD", list);
        Assert.DoesNotContain("explorer", list);
        Assert.DoesNotContain("TiWorker", list);
        Assert.DoesNotContain("TrustedInstaller", list);
        Assert.DoesNotContain("mstsc", list);
    }

    [Fact]
    public void Display_names_are_friendly()
    {
        Assert.Equal("Brave", ApplicationCloser.DisplayName("brave"));
        Assert.Equal("Microsoft Edge", ApplicationCloser.DisplayName("msedge"));
        Assert.Equal("Mozilla Firefox", ApplicationCloser.DisplayName("firefox"));
        Assert.Equal("OneDrive", ApplicationCloser.DisplayName("onedrive"));
        Assert.Equal("Microsoft Teams", ApplicationCloser.DisplayName("ms-teams"));
        Assert.Equal("Remote Desktop Connection", ApplicationCloser.DisplayName("mstsc"));
        Assert.Equal("Outlook", ApplicationCloser.DisplayName("OUTLOOK"));
        Assert.Equal("Windows Update installer", ApplicationCloser.DisplayName("TiWorker"));
        Assert.Equal("something", ApplicationCloser.DisplayName("something"));
    }

    [Fact]
    public void Relaunch_skips_background_only_apps_without_a_known_background_start()
    {
        using var tree = new TempTree();
        var exe = CopyPing(tree, "pcl_relaunch_bg");
        var closer = new ApplicationCloser();

        var started = closer.Relaunch([new ClosedApplication("pcl_relaunch_bg", exe, HadVisibleWindows: false, WasTerminated: true)]);

        Assert.Empty(started);
        Assert.Empty(Process.GetProcessesByName("pcl_relaunch_bg"));
    }

    [Fact]
    public void Relaunch_restarts_apps_the_user_could_see()
    {
        using var tree = new TempTree();
        var exe = CopyPing(tree, "pcl_relaunch_fg");
        var closer = new ApplicationCloser();

        // ping without arguments prints its usage and exits - enough to prove the start happened.
        var started = closer.Relaunch([new ClosedApplication("pcl_relaunch_fg", exe, HadVisibleWindows: true, WasTerminated: false)]);

        Assert.Equal(["pcl_relaunch_fg"], started);
        Thread.Sleep(1500);
        foreach (var p in Process.GetProcessesByName("pcl_relaunch_fg"))
        {
            KillQuietly(p);
        }
    }

    [Fact]
    public void Missing_executable_is_not_relaunched()
    {
        var closer = new ApplicationCloser();
        var started = closer.Relaunch([new ClosedApplication("ghost", @"C:\definitely\not\here\ghost.exe", true, false), new ClosedApplication("nopath", null, true, false)]);
        Assert.Empty(started);
    }
}