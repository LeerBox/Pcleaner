using System.Runtime.Versioning;
using PCleaner.Core.Model;
using PCleaner.Core.Windows;

namespace PCleaner.Core.Rules;

/// <summary>
/// Windows cleanup rules. Every location was checked against Microsoft documentation and the behaviour of
/// Microsoft's own Disk Cleanup handlers; see the <see cref="CleanupRule.Reference"/> of each rule.
/// Locations with a documented breakage history (WinSxS, Installer cache, catroot2, DataStore, Prefetch,
/// FNTCACHE.DAT, BITS queue, notification database, ...) are deliberately absent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRuleProvider
{
    private const string MsDiskCleanup = "Microsoft: Disk Cleanup in Windows - https://support.microsoft.com/windows/disk-cleanup-in-windows-8a96ff42-5751-39ad-23d6-434b4d5b9a68";
    private const string MsWerSettings = "Microsoft: WER settings (LocalDumps, LiveKernelReports) - https://learn.microsoft.com/windows/win32/wer/wer-settings";
    private const string MsWuReset = "Microsoft: Additional resources for Windows Update (reset procedure) - https://learn.microsoft.com/troubleshoot/windows-client/installing-updates-features-roles/additional-resources-for-windows-update";
    private const string MsWuLogs = "Microsoft: Windows Update log files - https://learn.microsoft.com/windows/deployment/update/windows-update-logs";
    private const string MsSetupLogs = "Microsoft: Windows Setup log files and event logs - https://learn.microsoft.com/windows-hardware/manufacture/desktop/windows-setup-log-files-and-event-logs";
    private const string MsTeams = "Microsoft: Clear the Teams client cache - https://learn.microsoft.com/troubleshoot/microsoftteams/teams-administration/clear-teams-cache";
    private const string MsShell = "Microsoft: SHEmptyRecycleBinW - https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shemptyrecyclebinw";
    private const string MsDism = "Microsoft: Clean up the WinSxS folder - https://learn.microsoft.com/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder";
    private const string MsWevtutil = "Microsoft: wevtutil - https://learn.microsoft.com/windows-server/administration/windows-commands/wevtutil";
    private const string MsDiskCleanupHandlers = "Microsoft: Disk Cleanup handlers (IEmptyVolumeCache) - https://learn.microsoft.com/windows/win32/lwef/disk-cleanup";

    private readonly RuleContext _ctx;

    public WindowsRuleProvider(RuleContext context)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
    }

    public IReadOnlyList<CleanupRule> GetRules()
    {
        var rules = new List<CleanupRule>();
        rules.AddRange(UserRules());
        rules.AddRange(RecentFileRules());
        rules.AddRange(ApplicationRules());
        rules.AddRange(ApplicationRecentFileRules());
        rules.AddRange(SystemRules());
        rules.AddRange(DiskCleanupHandlerRules());
        return rules;
    }

    // ------------------------------------------------------------------ per-user (no administrator needed)

    private IEnumerable<CleanupRule> UserRules()
    {
        var local = _ctx.LocalAppData;
        var roaming = _ctx.RoamingAppData;
        const string group = RuleOrder.TempAndCaches;

        yield return new CleanupRule
        {
            Id = "windows.temp.user",
            Name = "Temporary files",
            Description = "Leftovers installers and apps dropped in your Temp folder. Anything from the last 24 hours or still in use is kept.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Order = 1,
            Reference = MsDiskCleanup,
            Targets =
            [
                Dir(Path.Combine(local, "Temp"), minAge: _ctx.TempFileMinimumAge, exclude: ["Low"]),
            ],
        };

        yield return new CleanupRule
        {
            Id = "windows.wer.user",
            Name = "Error reports",
            Description = "Crash reports Windows already sent or never will. Pure diagnostics - nothing of yours is in them.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Reference = MsWerSettings,
            Targets =
            [
                Dir(Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive")),
                Dir(Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue")),
                Dir(Path.Combine(local, @"Microsoft\Windows\WER\Temp")),
            ],
        };

        yield return new CleanupRule
        {
            Id = "windows.crashdumps.user",
            Name = "App crash dumps",
            Description = "Memory snapshots saved when an app crashed. Useful only to developers debugging that crash.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Reference = MsWerSettings,
            Targets = [Dir(Path.Combine(local, "CrashDumps"), include: ["*.dmp"])],
        };

        yield return new CleanupRule
        {
            Id = "windows.shadercache.d3d",
            Name = "DirectX shader cache",
            Description = "Compiled graphics shaders. Windows rebuilds them; the next game start may take a moment longer.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Reference = MsDiskCleanup + " (handler: D3D Shader Cache)",
            Targets = [Dir(Path.Combine(local, "D3DSCache"))],
        };

        yield return new CleanupRule
        {
            Id = "windows.inetcache",
            Name = "Legacy internet cache",
            Description = "Web content cached by Internet Explorer mode, Office and Outlook. Fetched again when needed - close Outlook first.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Reference = MsDiskCleanup + " (handler: Internet Cache Files)",
            ConflictingProcesses = ["OUTLOOK", "iexplore"],
            Targets = [Dir(Path.Combine(local, @"Microsoft\Windows\INetCache"), exclude: ["Low", "container.dat", "counters.dat", "desktop.ini"])],
        };

        yield return new CleanupRule
        {
            Id = "windows.clr.usagelogs",
            Name = ".NET usage logs",
            Description = "Tiny notes the .NET Framework keeps to plan background compilation. Recreated automatically.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Reference = "CLR UsageLogs are plain assembly usage traces consumed by the .NET Framework NGEN service; safe to remove.",
            Targets =
            [
                Dir(Path.Combine(local, @"Microsoft\CLR_v4.0\UsageLogs"), include: ["*.log"]),
                Dir(Path.Combine(local, @"Microsoft\CLR_v4.0_32\UsageLogs"), include: ["*.log"]),
            ],
        };

        yield return new CleanupRule
        {
            Id = "windows.rdp.cache",
            Name = "Remote Desktop cache",
            Description = "Screen fragments saved to speed up remote sessions. Rebuilt during the next connection.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Reference = "Remote Desktop Connection persistent bitmap cache (%LOCALAPPDATA%\\Microsoft\\Terminal Server Client\\Cache).",
            ConflictingProcesses = ["mstsc"],
            Targets = [Dir(Path.Combine(local, @"Microsoft\Terminal Server Client\Cache"))],
        };

        yield return new CleanupRule
        {
            Id = "windows.store.cache",
            Name = "Microsoft Store cache",
            Description = "The Store app's local cache - what wsreset.exe clears. Your apps, purchases and account are not affected.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Reference = MsDiskCleanup + " (wsreset.exe guidance)",
            ConflictingProcesses = ["WinStore.App"],
            Targets =
            [
                Dir(Path.Combine(local, @"Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalCache")),
                Dir(Path.Combine(local, @"Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\TempState")),
            ],
        };

        var packageTemp = new List<PathTarget>();
        foreach (var dir in PathExpander.ExpandDirectories(Path.Combine(local, @"Packages\*\TempState")))
        {
            packageTemp.Add(Dir(dir, minAge: _ctx.TempFileMinimumAge));
        }

        foreach (var dir in PathExpander.ExpandDirectories(Path.Combine(local, @"Packages\*\AC\Temp")))
        {
            packageTemp.Add(Dir(dir, minAge: _ctx.TempFileMinimumAge));
        }

        foreach (var dir in PathExpander.ExpandDirectories(Path.Combine(local, @"Packages\*\AC\INetCache")))
        {
            packageTemp.Add(Dir(dir, minAge: _ctx.TempFileMinimumAge));
        }

        if (packageTemp.Count > 0)
        {
            yield return new CleanupRule
            {
                Id = "windows.packages.temp",
                Name = "Store app temp files",
                Description = "Temporary folders of installed Store apps. App settings and data are never touched; the last 24 hours are kept.",
                Category = RuleCategory.WindowsUser,
                Group = group,
                Reference = "Windows app container temporary folders; Winapp2 'TempState'/'AC\\Temp' definitions.",
                Targets = packageTemp,
            };
        }

        yield return new CleanupRule
        {
            Id = "windows.thumbnails",
            Name = "Thumbnail & icon cache",
            Description = "Preview images and icons Explorer keeps for folders you viewed. Rebuilt as you browse; files Explorer holds open are skipped.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Order = 2,
            Reference = MsDiskCleanup + " (handler: Thumbnail Cache - 'they will be automatically recreated as needed')",
            Targets =
            [
                Dir(Path.Combine(local, @"Microsoft\Windows\Explorer"), include: ["thumbcache_*.db", "iconcache_*.db"], recursive: false, deleteEmptyDirs: false),
                File(Path.Combine(local, "IconCache.db")),
            ],
        };

        yield return new CleanupRule
        {
            Id = "windows.dns",
            Name = "DNS cache",
            Description = "Remembered website addresses, like 'ipconfig /flushdns'. Helps after network changes; nothing is stored on disk.",
            Category = RuleCategory.WindowsUser,
            Group = group,
            Action = RuleAction.FlushDnsCache,
            Reference = "ipconfig /flushdns equivalent (DnsFlushResolverCache).",
        };

        yield return new CleanupRule
        {
            Id = "windows.recyclebin",
            Name = "Recycle Bin",
            Description = "Empties the Recycle Bin of every drive. Off by default - deleted files can no longer be restored afterwards.",
            Category = RuleCategory.WindowsUser,
            Group = RuleOrder.RecycleBin,
            Risk = RiskLevel.Moderate,
            Action = RuleAction.EmptyRecycleBin,
            Reference = MsShell,
        };

        yield return new CleanupRule
        {
            Id = "windows.recent",
            Name = "Recent files & jump lists",
            Description = "Shortcuts to recently opened documents and the taskbar jump lists. Pinned items stay.",
            Category = RuleCategory.WindowsUser,
            Group = RuleOrder.RecentFiles,
            Order = 1,
            Risk = RiskLevel.Privacy,
            Reference = "Explorer Recent Items / AutomaticDestinations / CustomDestinations (Winapp2 and BleachBit definitions; f01b4d95cf55d32a & 5f7b5f1e01b83767 hold Quick Access pins and are excluded).",
            Targets =
            [
                Dir(Path.Combine(roaming, @"Microsoft\Windows\Recent"), include: ["*.lnk"], recursive: false, deleteEmptyDirs: false),
                Dir(Path.Combine(roaming, @"Microsoft\Windows\Recent\AutomaticDestinations"), exclude: ["f01b4d95cf55d32a.automaticDestinations-ms", "5f7b5f1e01b83767.automaticDestinations-ms"], recursive: false, deleteEmptyDirs: false),
                Dir(Path.Combine(roaming, @"Microsoft\Windows\Recent\CustomDestinations"), recursive: false, deleteEmptyDirs: false),
            ],
        };
    }

    // ------------------------------------------------------------------ third-party applications (no administrator needed)

    private IEnumerable<CleanupRule> ApplicationRules()
    {
        var local = _ctx.LocalAppData;
        var roaming = _ctx.RoamingAppData;
        var localLow = _ctx.LocalLowAppData;

        yield return new CleanupRule
        {
            Id = "apps.shadercache.nvidia",
            Name = "NVIDIA shader cache",
            Description = "Compiled shaders saved by the NVIDIA driver. Rebuilt on demand - games may load a little slower once.",
            Category = RuleCategory.Applications,
            Group = RuleOrder.GraphicsDrivers,
            Reference = "NVIDIA driver shader cache folders (Winapp2 'NVIDIA Shader Cache').",
            Targets = ExistingDirs(
                Path.Combine(local, @"NVIDIA\DXCache"),
                Path.Combine(local, @"NVIDIA\GLCache"),
                Path.Combine(local, @"NVIDIA\OptixCache"),
                Path.Combine(local, @"NVIDIA Corporation\NV_Cache"),
                Path.Combine(roaming, @"NVIDIA\ComputeCache"),
                Path.Combine(localLow, @"NVIDIA\DXCache"),
                Path.Combine(localLow, @"NVIDIA\PerDriverVersion\DXCache")),
        };

        yield return new CleanupRule
        {
            Id = "apps.shadercache.amd",
            Name = "AMD shader cache",
            Description = "Compiled shaders saved by the AMD Radeon driver. Rebuilt on demand - games may load a little slower once.",
            Category = RuleCategory.Applications,
            Group = RuleOrder.GraphicsDrivers,
            Reference = "AMD driver shader cache folders (Winapp2 'AMD').",
            Targets = ExistingDirs(
                Path.Combine(local, @"AMD\DxCache"),
                Path.Combine(local, @"AMD\DxcCache"),
                Path.Combine(local, @"AMD\GLCache"),
                Path.Combine(local, @"AMD\VkCache"),
                Path.Combine(local, @"AMD\RadeonSoftware\vkcache"),
                Path.Combine(localLow, @"AMD\DxCache")),
        };

        yield return new CleanupRule
        {
            Id = "apps.shadercache.intel",
            Name = "Intel shader cache",
            Description = "Compiled shaders saved by the Intel graphics driver. Rebuilt on demand.",
            Category = RuleCategory.Applications,
            Group = RuleOrder.GraphicsDrivers,
            Reference = "Intel graphics driver shader cache (%LOCALAPPDATA%\\Intel\\ShaderCache).",
            Targets = ExistingDirs(Path.Combine(local, @"Intel\ShaderCache")),
        };

        yield return new CleanupRule
        {
            Id = "apps.teams.cache",
            Name = "Teams cache",
            Description = "Web cache of classic and new Teams. Chats and settings live in the cloud, so nothing is lost - close Teams first.",
            Category = RuleCategory.Applications,
            Group = RuleOrder.MicrosoftApps,
            Reference = MsTeams,
            ConflictingProcesses = ["Teams", "ms-teams", "msteams"],
            Targets = ExistingDirs(
                [
                    Path.Combine(roaming, @"Microsoft\Teams\Cache"),
                    Path.Combine(roaming, @"Microsoft\Teams\Code Cache"),
                    Path.Combine(roaming, @"Microsoft\Teams\GPUCache"),
                    Path.Combine(roaming, @"Microsoft\Teams\tmp"),
                    Path.Combine(roaming, @"Microsoft\Teams\Service Worker\CacheStorage"),
                ],
                wildcardPatterns:
                [
                    Path.Combine(local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\Cache"),
                    Path.Combine(local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\Code Cache"),
                    Path.Combine(local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\GPUCache"),
                ]),
        };

        yield return new CleanupRule
        {
            Id = "apps.office.cache",
            Name = "Office add-in cache",
            Description = "Cache of Office web add-ins and Skype for Business traces. Documents and Outlook data are never touched.",
            Category = RuleCategory.Applications,
            Group = RuleOrder.MicrosoftApps,
            Reference = "Office 'Wef' web extension framework cache and 'Lync\\Tracing' logs are regenerable diagnostics/caches.",
            ConflictingProcesses = ["WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "lync"],
            Targets = ExistingDirs(
                Path.Combine(local, @"Microsoft\Office\16.0\Wef"),
                Path.Combine(local, @"Microsoft\Office\16.0\Lync\Tracing")),
        };

        yield return new CleanupRule
        {
            Id = "apps.onedrive.logs",
            Name = "OneDrive logs",
            Description = "Diagnostic logs of the OneDrive sync client. Your files and sync settings stay.",
            Category = RuleCategory.Applications,
            Group = RuleOrder.MicrosoftApps,
            Reference = "OneDrive client *.odl / *.odlgz / *.odlsent / *.aodl diagnostic logs.",
            ConflictingProcesses = ["OneDrive"],
            Targets = ExistingDirs([Path.Combine(local, @"Microsoft\OneDrive\logs"), Path.Combine(local, @"Microsoft\OneDrive\setup\logs")], include: ["*.odl", "*.odlgz", "*.odlsent", "*.aodl", "*.log", "*.etl"]),
        };
    }

    // ------------------------------------------------------------------ system-wide (administrator)

    private IEnumerable<CleanupRule> SystemRules()
    {
        var win = _ctx.WindowsDirectory;
        var programData = _ctx.ProgramData;

        var systemTemp = new List<PathTarget>
        {
            Dir(Path.Combine(win, "Temp"), minAge: _ctx.TempFileMinimumAge),
            Dir(Path.Combine(win, "SystemTemp"), minAge: _ctx.TempFileMinimumAge),
            Dir(Path.Combine(win, @"System32\config\systemprofile\AppData\Local\Temp"), minAge: _ctx.TempFileMinimumAge),
            Dir(Path.Combine(win, @"SysWOW64\config\systemprofile\AppData\Local\Temp"), minAge: _ctx.TempFileMinimumAge),
        };
        foreach (var dir in PathExpander.ExpandDirectories(Path.Combine(win, @"ServiceProfiles\*\AppData\Local\Temp")))
        {
            systemTemp.Add(Dir(dir, minAge: _ctx.TempFileMinimumAge));
        }

        yield return new CleanupRule
        {
            Id = "windows.temp.system",
            Name = "System temp files",
            Description = "Leftovers in the Windows and service Temp folders. The last 24 hours and files in use are kept.",
            Category = RuleCategory.WindowsSystem,
            Group = RuleOrder.SystemFiles,
            Order = 1,
            RequiresAdministrator = true,
            Reference = MsDiskCleanup + " (handler: Temporary Files)",
            Targets = systemTemp,
        };

        yield return new CleanupRule
        {
            Id = "windows.wer.system",
            Name = "System error reports",
            Description = "Crash reports of all users and Windows services. Diagnostics only - nothing personal.",
            Category = RuleCategory.WindowsSystem,
            Group = RuleOrder.SystemFiles,
            RequiresAdministrator = true,
            Reference = MsDiskCleanup + " (handler: Windows Error Reporting Files)",
            Targets =
            [
                Dir(Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive")),
                Dir(Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue")),
                Dir(Path.Combine(programData, @"Microsoft\Windows\WER\Temp")),
            ],
        };

        yield return new CleanupRule
        {
            Id = "windows.memory.dumps",
            Name = "System crash dumps",
            Description = "Memory dumps written after blue screens or driver hangs. Useful only for post-mortem debugging.",
            Category = RuleCategory.WindowsSystem,
            Group = RuleOrder.SystemFiles,
            RequiresAdministrator = true,
            Reference = MsSetupLogs + "; " + MsWerSettings,
            Targets =
            [
                File(Path.Combine(win, "MEMORY.DMP")),
                Dir(Path.Combine(win, "Minidump"), include: ["*.dmp"]),
                Dir(Path.Combine(win, "LiveKernelReports"), include: ["*.dmp"]),
            ],
        };

        yield return new CleanupRule
        {
            Id = "windows.logs.archived",
            Name = "Old Windows logs",
            Description = "Servicing, update and setup logs older than a week - the same age rule Windows itself applies. Logs still in use are skipped.",
            Category = RuleCategory.WindowsSystem,
            Group = RuleOrder.SystemFiles,
            RequiresAdministrator = true,
            Reference = MsWuLogs + "; " + MsSetupLogs,
            Targets =
            [
                Dir(Path.Combine(win, "Logs"), include: ["*.log", "*.etl", "*.cab", "*.txt", "*.old", "*.bak"], minAge: TimeSpan.FromDays(7)),
                Dir(Path.Combine(win, @"System32\LogFiles"), include: ["*.log", "*.etl", "*.txt", "*.old"], minAge: TimeSpan.FromDays(7), exclude: ["WMI"]),
                Dir(Path.Combine(programData, @"USOShared\Logs"), include: ["*.etl", "*.log"], minAge: TimeSpan.FromDays(7)),
                Dir(Path.Combine(win, "debug"), include: ["*.log", "*.old"], minAge: TimeSpan.FromDays(7), deleteEmptyDirs: false),
                Dir(Path.Combine(win, "inf"), include: ["setupapi.*.log"], recursive: false, minAge: TimeSpan.FromDays(7), deleteEmptyDirs: false),
                Dir(Path.Combine(programData, @"Microsoft\Diagnosis\ETLLogs"), include: ["*.etl"], minAge: TimeSpan.FromDays(7)),
            ],
        };

        yield return new CleanupRule
        {
            Id = "windows.update.download",
            Name = "Update download cache",
            Description = "Update packages Windows already downloaded. Pending updates simply download again; skipped while updates are being installed.",
            Category = RuleCategory.WindowsSystem,
            Group = RuleOrder.SystemFiles,
            Risk = RiskLevel.Moderate,
            RequiresAdministrator = true,
            ServicesToStop = ["wuauserv", "bits"],
            ConflictingProcesses = ["TiWorker", "TrustedInstaller"],
            Reference = MsWuReset,
            Targets = [Dir(Path.Combine(win, @"SoftwareDistribution\Download"))],
        };

        yield return new CleanupRule
        {
            Id = "windows.eventlogs",
            Name = "Windows event logs",
            Description = "Clears the Application, System and Setup logs; the Security log stays. Off by default - logs help with troubleshooting.",
            Category = RuleCategory.WindowsSystem,
            Group = RuleOrder.Privacy,
            Risk = RiskLevel.Privacy,
            RequiresAdministrator = true,
            Action = RuleAction.ClearEventLogs,
            Reference = MsWevtutil,
        };

        yield return new CleanupRule
        {
            Id = "windows.componentstore",
            Name = "Component store cleanup",
            Description = "Microsoft's own DISM cleanup of superseded Windows components. Safe, but takes minutes and older updates can no longer be uninstalled.",
            Category = RuleCategory.WindowsSystem,
            Group = RuleOrder.Advanced,
            Risk = RiskLevel.Moderate,
            RequiresAdministrator = true,
            Action = RuleAction.ComponentStoreCleanup,
            Reference = MsDism,
        };
    }

    // ------------------------------------------------------------------ Microsoft Disk Cleanup handlers

    /// <summary>Handlers that duplicate a native rule (which shows the individual files) and are therefore hidden.</summary>
    private static readonly HashSet<string> DuplicatedHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temporary Files", "Recycle Bin", "System error memory dump files", "System error minidump files",
        "Windows Error Reporting Files", "D3D Shader Cache", "Internet Cache Files", "Thumbnail Cache",
    };

    /// <summary>
    /// Short, plain-language texts for the handlers Windows ships. Microsoft's own registry texts are long, partly
    /// empty (many handlers register no display string) and written for cleanmgr's list box.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Description)> HandlerTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Active Setup Temp Folders"] = ("Setup leftovers", "Temporary folders of setup programs that finished long ago."),
        ["BranchCache"] = ("BranchCache files", "Content cached by the BranchCache network feature. Fetched again when needed."),
        ["Content Indexer Cleaner"] = ("Search indexer leftovers", "Stale files of the Windows Search indexer. The search index itself stays."),
        ["Delivery Optimization Files"] = ("Delivery Optimization files", "Update files kept for sharing with other PCs on your network. Safe to remove."),
        ["Device Driver Packages"] = ("Old driver packages", "Older versions of installed drivers. The current version of each driver stays, but rolling a driver back is no longer possible."),
        ["Diagnostic Data Viewer database files"] = ("Diagnostic Data Viewer files", "Local database of the Diagnostic Data Viewer app."),
        ["Downloaded Program Files"] = ("Downloaded program files", "Old ActiveX controls and Java applets Internet Explorer downloaded."),
        ["Feedback Hub Archive log files"] = ("Feedback Hub logs", "Log files collected for the Feedback Hub app."),
        ["Language Pack"] = ("Unused language packs", "Language packs and optional features nobody on this PC uses. Reinstalled from Windows Update if needed later."),
        ["Offline Pages Files"] = ("Offline web pages", "Pages Internet Explorer saved for offline reading."),
        ["Old ChkDsk Files"] = ("Check Disk fragments", "Lost file fragments (FOUND folders) that Check Disk recovered from a damaged drive."),
        ["Previous Installations"] = ("Previous Windows installation", "The Windows.old folder from the last upgrade. Removing it means you can no longer go back to the previous version."),
        ["RetailDemo Offline Content"] = ("Retail demo content", "Demo content meant for PCs on store display."),
        ["Setup Log Files"] = ("Setup log files", "Logs written by Windows setup."),
        ["Temporary Setup Files"] = ("Windows installation leftovers", "Files left over from installing Windows."),
        ["Temporary Sync Files"] = ("Temporary sync files", "Temporary files of Windows sync features."),
        ["Update Cleanup"] = ("Windows Update cleanup", "Older versions of installed updates. Frees a lot of space; may need a restart and older updates can no longer be uninstalled."),
        ["Upgrade Discarded Files"] = ("Files discarded by upgrade", "Files an upgrade could not place in the new Windows. Remove only if none of your files are missing."),
        ["User file versions"] = ("File History versions", "Older versions of your files kept by File History. Only the current versions remain."),
        ["Windows Defender"] = ("Defender leftovers", "Non-critical files of Microsoft Defender Antivirus. Protection is not affected."),
        ["Windows Reset Log Files"] = ("System recovery logs", "Logs from earlier resets or recoveries. Keep them while troubleshooting a recovery problem."),
        ["Windows Upgrade Log Files"] = ("Windows upgrade logs", "Logs from Windows upgrades. Keep them while troubleshooting an upgrade problem."),
    };

    private static IEnumerable<CleanupRule> DiskCleanupHandlerRules()
    {
        foreach (var handler in DiskCleanupHandlers.Enumerate())
        {
            if (DiskCleanupHandlers.NeverOffer.Contains(handler.KeyName) || DuplicatedHandlers.Contains(handler.KeyName))
            {
                continue;
            }

            var moderate = DiskCleanupHandlers.ModerateRisk.Contains(handler.KeyName);
            var perUser = DiskCleanupHandlers.PerUserHandlers.Contains(handler.KeyName);
            var (name, description) = HandlerTexts.TryGetValue(handler.KeyName, out var text)
                ? text
                : (handler.DisplayName, FirstSentence(handler.Description, "Built-in Windows Disk Cleanup handler.") + (moderate ? " Has side effects beyond a cache - review before enabling." : string.Empty));
            yield return new CleanupRule
            {
                Id = "windows.diskcleanup." + Slug(handler.KeyName),
                Name = name,
                Description = description,
                Category = RuleCategory.WindowsSystem,
                Group = RuleOrder.DiskCleanup,
                Risk = moderate ? RiskLevel.Moderate : RiskLevel.Safe,
                RequiresAdministrator = !perUser,
                Action = RuleAction.DiskCleanupHandler,
                ActionArgument = handler.KeyName,
                Reference = MsDiskCleanupHandlers,
            };
        }
    }

    /// <summary>Microsoft's handler descriptions run to several sentences; the first one says what the files are.</summary>
    internal static string FirstSentence(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var trimmed = text.Trim();
        var end = trimmed.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? trimmed[..(end + 1)] : trimmed;
    }

    // ------------------------------------------------------------------ helpers

    internal static PathTarget Dir(string path, IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null, bool recursive = true, bool deleteEmptyDirs = true, TimeSpan? minAge = null) => new()
    {
        Path = path,
        IncludePatterns = include ?? [],
        ExcludeNames = exclude ?? [],
        Recursive = recursive,
        DeleteEmptyDirectories = deleteEmptyDirs,
        MinimumAge = minAge,
    };

    internal static PathTarget File(string path) => new() { Path = path, IsFile = true };

    private static List<PathTarget> ExistingDirs(params string[] paths) => ExistingDirs(paths, wildcardPatterns: null);

    private static List<PathTarget> ExistingDirs(IReadOnlyList<string> paths, IReadOnlyList<string>? wildcardPatterns = null, IReadOnlyList<string>? include = null)
    {
        var list = new List<PathTarget>();
        foreach (var p in paths)
        {
            // Non-existing folders are kept as targets so the rule reports "not found" instead of silently vanishing.
            list.Add(Dir(p, include: include));
        }

        if (wildcardPatterns is not null)
        {
            foreach (var pattern in wildcardPatterns)
            {
                foreach (var dir in PathExpander.ExpandDirectories(pattern))
                {
                    list.Add(Dir(dir, include: include));
                }
            }
        }

        return list;
    }

    private static string Slug(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}