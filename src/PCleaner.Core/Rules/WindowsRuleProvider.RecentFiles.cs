using Microsoft.Win32;
using PCleaner.Core.Model;

namespace PCleaner.Core.Rules;

/// <summary>
/// "Forget what was opened": the lists Windows and applications keep of recently opened files, plus the tabs
/// editors restore at start. Locations follow the vendors' source code and settings documentation and the
/// BleachBit / Winapp2 cleaner definitions (see each rule's <see cref="CleanupRule.Reference"/>); every one of
/// them is additionally allow-listed in <c>SafetyGuard.ValidateHistoryTarget</c>.
/// </summary>
public sealed partial class WindowsRuleProvider
{
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string NotepadPackage = "Microsoft.WindowsNotepad_8wekyb3d8bbwe";

    private static readonly string[] OfficeApps = ["Word", "Excel", "PowerPoint"];
    private static readonly string[] OfficeVersions = ["16.0", "15.0"];
    private static readonly string[] AdobeRoots = [@"Software\Adobe\Acrobat Reader", @"Software\Adobe\Adobe Acrobat"];

    // ------------------------------------------------------------------ Windows (per user)

    private IEnumerable<CleanupRule> RecentFileRules()
    {
        var local = _ctx.LocalAppData;

        yield return new CleanupRule
        {
            Id = "windows.recent.explorer",
            Name = "Explorer & dialog history",
            Description = "What Explorer, the Run box and Open/Save dialogs remember: recent documents, last folders, typed paths and searches.",
            Category = RuleCategory.WindowsUser,
            Group = RuleOrder.RecentFiles,
            Order = 2,
            Risk = RiskLevel.Privacy,
            Action = RuleAction.ForgetHistory,
            Reference = "Explorer MRU keys (libyal winreg-kb 'Most recently used', BleachBit windows_explorer.xml; Microsoft ADMX_StartMenu 'ClearRecentDocsOnExit' describes the same data). Windows recreates the keys as files are opened.",
            History =
            [
                RegistryContents($@"{ExplorerKey}\RecentDocs", "Recent documents"),
                RegistryContents($@"{ExplorerKey}\ComDlg32\OpenSavePidlMRU", "Open/Save dialogs"),
                RegistryContents($@"{ExplorerKey}\ComDlg32\LastVisitedPidlMRU", "Last folder per app"),
                RegistryContents($@"{ExplorerKey}\ComDlg32\LastVisitedPidlMRULegacy", "Last folder per app"),
                RegistryContents($@"{ExplorerKey}\RunMRU", "Run box"),
                RegistryContents($@"{ExplorerKey}\TypedPaths", "Address bar"),
                RegistryContents($@"{ExplorerKey}\WordWheelQuery", "Searches"),
                RegistryContents($@"{ExplorerKey}\Map Network Drive MRU", "Network drives"),
            ],
        };

        var notepadPackage = Path.Combine(local, "Packages", NotepadPackage);
        if (Directory.Exists(notepadPackage))
        {
            yield return new CleanupRule
            {
                Id = "windows.recent.notepad",
                Name = "Notepad tabs & recent files",
                Description = "The tabs Notepad reopens at start - unsaved ones are lost - and its Recent files list. Notepad starts empty next time.",
                Category = RuleCategory.WindowsUser,
                Group = RuleOrder.RecentFiles,
                Order = 3,
                Risk = RiskLevel.Moderate,
                Action = RuleAction.ForgetHistory,
                ConflictingProcesses = ["Notepad"],
                RelaunchClosedApplications = false,
                Reference = "Notepad state files (ogmini 'Notepad-State-Library': TabState/WindowState *.bin, settings.dat 'RecentFiles'); Windows Insider Blog 2025-03-13 'Recent Files' feature. Winapp2 '[Windows Notepad *]' removes the same files.",
                History =
                [
                    new HistoryTarget { Store = HistoryStore.NotepadTabs, Location = Path.Combine(notepadPackage, "LocalState"), Label = "Tab" },
                    new HistoryTarget { Store = HistoryStore.NotepadRecentFiles, Location = Path.Combine(notepadPackage, "Settings", "settings.dat"), Label = "Recent files" },
                ],
            };
        }

        yield return new CleanupRule
        {
            Id = "windows.recent.accessories",
            Name = "Paint, WordPad & Media Player",
            Description = "Recent-file lists of Paint, WordPad and Windows Media Player, plus the last key Registry Editor opened. Favourites stay.",
            Category = RuleCategory.WindowsUser,
            Group = RuleOrder.RecentFiles,
            Order = 4,
            Risk = RiskLevel.Privacy,
            Action = RuleAction.ForgetHistory,
            ConflictingProcesses = ["mspaint", "wordpad", "wmplayer", "regedit"],
            RelaunchClosedApplications = false,
            Reference = "MFC 'Recent File List' sections (Microsoft Learn CRecentFileList), BleachBit paint.xml / wordpad.xml / windows_media_player.xml, Winapp2 '[Windows Registry Editor *]' (LastKey only; Favorites are kept).",
            History =
            [
                RegistryContents(@"Software\Microsoft\Windows\CurrentVersion\Applets\Paint\Recent File List", "Paint"),
                RegistryContents(@"Software\Microsoft\Windows\CurrentVersion\Applets\Wordpad\Recent File List", "WordPad"),
                RegistryContents(@"Software\Microsoft\MediaPlayer\Player\RecentFileList", "Media Player"),
                RegistryContents(@"Software\Microsoft\MediaPlayer\Player\RecentURLList", "Media Player"),
                RegistryValues(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "Registry Editor", "LastKey"),
            ],
        };
    }

    // ------------------------------------------------------------------ third-party applications (only when installed)

    private IEnumerable<CleanupRule> ApplicationRecentFileRules()
    {
        var roaming = _ctx.RoamingAppData;

        var officeVersion = OfficeVersions.FirstOrDefault(v => OfficeApps.Any(app => KeyExists($@"Software\Microsoft\Office\{v}\{app}")));
        if (officeVersion is not null)
        {
            var history = new List<HistoryTarget>();
            foreach (var app in OfficeApps)
            {
                var root = $@"Software\Microsoft\Office\{officeVersion}\{app}";
                history.Add(RegistryContents($@"{root}\File MRU", app));
                history.Add(RegistryContents($@"{root}\Place MRU", $"{app} folders"));
                history.Add(RegistryContents($@"{root}\User MRU\*\File MRU", app));
                history.Add(RegistryContents($@"{root}\User MRU\*\Place MRU", $"{app} folders"));
            }

            history.Add(RegistryContents($@"Software\Microsoft\Office\{officeVersion}\Word\Reading Locations", "Word reading positions"));

            yield return new CleanupRule
            {
                Id = "apps.recent.office",
                Name = "Microsoft Office",
                Description = "Recent files and folders of Word, Excel and PowerPoint, and Word's remembered reading positions. A signed-in account also keeps the list online.",
                Category = RuleCategory.Applications,
                Group = RuleOrder.RecentFiles,
                Order = 10,
                Risk = RiskLevel.Privacy,
                Action = RuleAction.ForgetHistory,
                ConflictingProcesses = ["WINWORD", "EXCEL", "POWERPNT"],
                Reference = "Office 'File MRU' / 'Place MRU' / 'User MRU' item format (libyal winreg-kb 'Microsoft Office', Eric Zimmerman RegistryPlugins OfficeMRU), Word 'Reading Locations' (RegRipper msoffice.pl), Winapp2 '[Microsoft Office *]'.",
                History = history,
            };
        }

        var notepadPlusPlus = Path.Combine(roaming, "Notepad++");
        if (Directory.Exists(notepadPlusPlus))
        {
            yield return new CleanupRule
            {
                Id = "apps.recent.notepadpp",
                Name = "Notepad++",
                Description = "The tabs Notepad++ reopens at start - unsaved ones are lost - and its recent-file list. Settings, plugins and themes stay.",
                Category = RuleCategory.Applications,
                Group = RuleOrder.RecentFiles,
                Order = 10,
                Risk = RiskLevel.Moderate,
                Action = RuleAction.ForgetHistory,
                ConflictingProcesses = ["notepad++"],
                RelaunchClosedApplications = false,
                Reference = "Notepad++ Parameters.cpp (session.xml, config.xml <History>, backup folder) and the user manual 'Configuration files' (written on exit - close Notepad++ first). Winapp2 '[Notepad++ - Session *]', '[Notepad++ - Backups & Unsaved Files *]'.",
                History =
                [
                    new HistoryTarget { Store = HistoryStore.NotepadPlusPlusSession, Location = notepadPlusPlus, Label = "Tab" },
                    new HistoryTarget { Store = HistoryStore.NotepadPlusPlusRecentFiles, Location = notepadPlusPlus, Label = "Recent files" },
                ],
            };
        }

        var vlcSettings = Path.Combine(roaming, "vlc", "vlc-qt-interface.ini");
        if (System.IO.File.Exists(vlcSettings))
        {
            yield return new CleanupRule
            {
                Id = "apps.recent.vlc",
                Name = "VLC media player",
                Description = "VLC's recent media with resume positions, its last folder and last network stream. Playlists and settings stay.",
                Category = RuleCategory.Applications,
                Group = RuleOrder.RecentFiles,
                Order = 10,
                Risk = RiskLevel.Privacy,
                Action = RuleAction.ForgetHistory,
                ConflictingProcesses = ["vlc"],
                RelaunchClosedApplications = false,
                Reference = "VLC modules/gui/qt/recents.cpp ([RecentsMRL] list/times, saved on exit) and qt.cpp (filedialog-path); BleachBit vlc.xml.",
                History = [new HistoryTarget { Store = HistoryStore.VlcRecentMedia, Location = vlcSettings, Label = "Recent media" }],
            };
        }

        if (KeyExists(@"Software\7-Zip"))
        {
            yield return new CleanupRule
            {
                Id = "apps.recent.7zip",
                Name = "7-Zip",
                Description = "Folder, copy, extraction and archive history of the 7-Zip File Manager. Favourite folders stay.",
                Category = RuleCategory.Applications,
                Group = RuleOrder.RecentFiles,
                Order = 10,
                Risk = RiskLevel.Privacy,
                Action = RuleAction.ForgetHistory,
                ConflictingProcesses = ["7zFM", "7zG"],
                RelaunchClosedApplications = false,
                Reference = "7-Zip source: FileManager/ViewSettings.cpp (FM FolderHistory, CopyHistory, PanelPath), UI/Common/ZipRegistry.cpp (Extraction PathHistory, Compression ArcHistory); FolderShortcuts are the user's favourites and stay.",
                History =
                [
                    RegistryValues(@"Software\7-Zip\FM", "File Manager", "FolderHistory", "CopyHistory", "PanelPath0", "PanelPath1"),
                    RegistryValues(@"Software\7-Zip\Extraction", "Extraction", "PathHistory"),
                    RegistryValues(@"Software\7-Zip\Compression", "Archives", "ArcHistory"),
                ],
            };
        }

        if (KeyExists(@"Software\WinRAR"))
        {
            yield return new CleanupRule
            {
                Id = "apps.recent.winrar",
                Name = "WinRAR",
                Description = "Recent archives and the paths WinRAR remembers from its dialogs. Settings and profiles stay.",
                Category = RuleCategory.Applications,
                Group = RuleOrder.RecentFiles,
                Order = 10,
                Risk = RiskLevel.Privacy,
                Action = RuleAction.ForgetHistory,
                ConflictingProcesses = ["WinRAR"],
                RelaunchClosedApplications = false,
                Reference = "BleachBit winrar.xml and Winapp2 '[WinRAR *]' (ArcHistory, DialogEditHistory, General\\LastFolder); libyal winreg-kb 'WinRAR'.",
                History =
                [
                    RegistryContents(@"Software\WinRAR\ArcHistory", "Recent archives"),
                    RegistryContents(@"Software\WinRAR\DialogEditHistory\*", "Dialog history"),
                    RegistryValues(@"Software\WinRAR\General", "Last folder", "LastFolder"),
                ],
            };
        }

        var adobeRoots = AdobeRoots.Where(KeyExists).ToList();
        if (adobeRoots.Count > 0)
        {
            var history = new List<HistoryTarget>();
            foreach (var root in adobeRoots)
            {
                var label = root.EndsWith("Reader", StringComparison.Ordinal) ? "Acrobat Reader" : "Acrobat";
                history.Add(RegistryContents($@"{root}\*\AVGeneral\cRecentFiles", label));
                history.Add(RegistryContents($@"{root}\*\AVGeneral\cRecentFolders", $"{label} folders"));
                history.Add(RegistryContents($@"{root}\*\SessionManagement\cWindowsCurrent", $"{label} session"));
                history.Add(RegistryContents($@"{root}\*\SessionManagement\cWindowsPrev", $"{label} session"));
            }

            yield return new CleanupRule
            {
                Id = "apps.recent.acrobat",
                Name = "Adobe Acrobat & Reader",
                Description = "Recent files and folders of Acrobat and Acrobat Reader, and the documents they reopen from the last session.",
                Category = RuleCategory.Applications,
                Group = RuleOrder.RecentFiles,
                Order = 10,
                Risk = RiskLevel.Privacy,
                Action = RuleAction.ForgetHistory,
                ConflictingProcesses = ["AcroRd32", "Acrobat"],
                RelaunchClosedApplications = false,
                Reference = "Adobe Acrobat Preference Reference, AVGeneral 'cRecentFiles' (up to iMaxMRUCntToBeStored = 100 entries); Winapp2 '[Adobe Acrobat *]' / '[Adobe Reader *]' (cRecentFolders, SessionManagement cWindowsCurrent/cWindowsPrev).",
                History = history,
            };
        }
    }

    // ------------------------------------------------------------------ helpers

    private static HistoryTarget RegistryContents(string key, string label) => new() { Store = HistoryStore.RegistryKeyContents, Location = key, Label = label };

    private static HistoryTarget RegistryValues(string key, string label, params string[] values) => new() { Store = HistoryStore.RegistryValues, Location = key, Label = label, ValueNames = values };

    private static bool KeyExists(string relativePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(relativePath, writable: false);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}