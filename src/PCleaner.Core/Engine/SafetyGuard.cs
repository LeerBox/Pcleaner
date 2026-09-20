using System.Text.RegularExpressions;
using PCleaner.Core.Model;

namespace PCleaner.Core.Engine;

/// <summary>
/// Defense-in-depth checks that make sure the cleaner can never delete anything that would harm Windows,
/// an application, or the user's data - regardless of what a rule says.
/// </summary>
public static class SafetyGuard
{
    private static readonly StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Directories that must never be used as a cleanup root, nor be deleted. A rule may target a
    /// sub directory of these (for example <c>%WINDIR%\Temp</c>), but never the directory itself.
    /// </summary>
    private static readonly Lazy<HashSet<string>> ProtectedRoots = new(BuildProtectedRoots);

    /// <summary>
    /// File names that are never deleted, wherever they are found. These hold browser bookmarks, passwords,
    /// settings and extension registries.
    /// </summary>
    private static readonly HashSet<string> NeverDeleteFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chromium family
        "Bookmarks", "Bookmarks.bak", "Login Data", "Login Data-journal", "Login Data For Account",
        "Login Data For Account-journal", "Web Data", "Web Data-journal", "Account Web Data", "Account Web Data-journal",
        "Preferences", "Secure Preferences", "Local State", "Affiliation Database", "trusted_vault.pb",
        "CdmStorage.db", "MediaDeviceSalts", "ClientCertificates", "ServerCertificate", "Extension Cookies",
        // Firefox family
        "places.sqlite", "places.sqlite-wal", "places.sqlite-shm", "key3.db", "key4.db", "logins.json",
        "logins-backup.json", "cert9.db", "cert8.db", "pkcs11.txt", "prefs.js", "user.js", "extensions.json",
        "extension-preferences.json", "extension-settings.json", "addonStartup.json.lz4", "addons.json",
        "storage-sync-v2.sqlite", "storage-sync-v2.sqlite-wal", "storage-sync-v2.sqlite-shm", "storage-sync.sqlite",
        "storage.sqlite", "signedInUser.json", "profiles.ini", "installs.ini", "search.json.mozlz4", "xulstore.json",
        "handlers.json", "containers.json", "permissions.sqlite", "content-prefs.sqlite", "protections.sqlite",
        "compatibility.ini", "times.json", "sessionCheckpoints.json", "extensions.ini", "autofill-profiles.json",
        "formhistory.sqlite",
        // Windows
        "desktop.ini", "ntuser.dat", "ntuser.dat.log1", "ntuser.dat.log2", "usrclass.dat", "hiberfil.sys",
        "pagefile.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "DumpStack.log.tmp",
    };

    /// <summary>
    /// Directory names that are never entered nor deleted when they appear anywhere below a browser data root.
    /// All of them hold extension code, extension settings or synced user data.
    /// </summary>
    private static readonly HashSet<string> ProtectedBrowserDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chromium family
        "Extensions", "Extension Rules", "Extension Scripts", "Extension State", "Local Extension Settings",
        "Sync Extension Settings", "Managed Extension Settings", "DNR Extension Rules", "extensions_crx_cache",
        "Sync Data", "Sync App Settings", "Web Applications", "GCM Store", "ext", "Accounts",
        "BraveWallet", "ads_service", "Rewards", "Collections", "EdgeWallet", "Workspaces",
        // Firefox family
        "extensions", "browser-extension-data", "extension-store", "extension-store-permissions", "weave",
        "bookmarkbackups", "gmp-widevinecdm", "gmp-gmpopenh264", "gmp-clearkey", "features", "security_state",
    };

    /// <summary>Markers that identify a browser data root, used to switch on the browser specific protections.</summary>
    private static readonly string[] BrowserRootMarkers =
    [
        "\\User Data\\", "\\Opera Software\\", "\\Mozilla\\", "\\Firefox\\", "\\Waterfox\\", "\\librewolf\\",
        "\\Floorp\\", "\\zen\\", "\\Pale Moon\\", "\\Basilisk\\", "\\Thunderbird\\", "\\SeaMonkey\\", "\\Vivaldi\\",
    ];

    public static IReadOnlyCollection<string> GetProtectedRoots() => ProtectedRoots.Value;

    /// <summary>
    /// Validates that a target root is acceptable: absolute, rooted on a drive, not a protected root, and not a
    /// parent of a protected root. Returns null when acceptable, otherwise the reason.
    /// </summary>
    public static string? ValidateTargetRoot(PathTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var path = NormalizeDirectory(target.Path);

        if (!Path.IsPathFullyQualified(path))
        {
            return "Path is not absolute.";
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "Network paths are not supported.";
        }

        var root = Path.GetPathRoot(path);
        if (string.Equals(root, path, Cmp))
        {
            return "Refusing to clean a drive root.";
        }

        if (!target.IsFile)
        {
            foreach (var protectedRoot in ProtectedRoots.Value)
            {
                if (string.Equals(protectedRoot, path, Cmp))
                {
                    return $"'{path}' is a protected system or user folder.";
                }

                // Never allow a target that contains a protected root (e.g. C:\Users would contain the profile).
                if (target.Recursive && IsParentOf(path, protectedRoot))
                {
                    return $"'{path}' contains the protected folder '{protectedRoot}'.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a database target: absolute local path, a file name that the purge kind is allowed to operate on
    /// (e.g. only "Web Data" for the Chromium purges) and located below a browser data root. Returns null when
    /// acceptable, otherwise the reason.
    /// </summary>
    public static string? ValidateDatabaseTarget(DatabaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var raw = target.Path.Trim();
        if (!Path.IsPathFullyQualified(raw))
        {
            return "Path is not absolute.";
        }

        if (raw.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "Network paths are not supported.";
        }

        string path;
        try
        {
            path = Path.GetFullPath(raw);
        }
        catch (Exception)
        {
            return "Path is malformed.";
        }

        var allowedNames = DatabasePurger.AllowedNames(target.Purge);
        var fileName = Path.GetFileName(path);
        if (!allowedNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            return $"'{fileName}' is not a database this purge may operate on.";
        }

        if (DatabasePurger.IsDirectoryTarget(target.Purge)
            && !string.Equals(Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty), DatabasePurger.LocalStorageParentName, Cmp))
        {
            return $"'{fileName}' is not inside a '{DatabasePurger.LocalStorageParentName}' folder.";
        }

        if (!IsUnderBrowserRoot(path))
        {
            return "Database is not inside a browser profile.";
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        foreach (var segment in directory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ProtectedBrowserDirectoryNames.Contains(segment))
            {
                return "Database lies inside protected extension or sync data.";
            }
        }

        return null;
    }

    /// <summary>
    /// Registry locations a history rule may empty (contents only, the key itself stays) or take named values
    /// from. Everything is below HKEY_CURRENT_USER; each entry is a case-insensitive regular expression over the
    /// key path, optionally with the value names that may be removed (null = the key's contents may be cleared).
    /// Favourites, window layout and preferences that live next to these lists are deliberately absent.
    /// </summary>
    private static readonly (Regex Key, string[]? Values)[] AllowedHistoryKeys =
    [
        (new(@"^Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\(RecentDocs|RunMRU|TypedPaths|WordWheelQuery|Map Network Drive MRU)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null),
        (new(@"^Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ComDlg32\\(OpenSavePidlMRU|LastVisitedPidlMRU|LastVisitedPidlMRULegacy|CIDSizeMRU)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null),
        (new(@"^Software\\Microsoft\\Windows\\CurrentVersion\\Applets\\(Paint|Wordpad)\\Recent File List$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null),
        (new(@"^Software\\Microsoft\\Windows\\CurrentVersion\\Applets\\Regedit$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), ["LastKey"]),
        (new(@"^Software\\Microsoft\\MediaPlayer\\Player\\(RecentFileList|RecentURLList)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null),
        (new(@"^Software\\Microsoft\\Office\\1[0-9]\.0\\(Word|Excel|PowerPoint|Access|Publisher|Visio|Project)\\(File MRU|Place MRU|Reading Locations|User MRU\\\*\\(File MRU|Place MRU))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null),
        (new(@"^Software\\Adobe\\(Acrobat Reader|Adobe Acrobat)\\[^\\]+\\AVGeneral\\(cRecentFiles|cRecentFolders)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null),
        (new(@"^Software\\Adobe\\(Acrobat Reader|Adobe Acrobat)\\[^\\]+\\SessionManagement\\(cWindowsCurrent|cWindowsPrev)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null),
        (new(@"^Software\\7-Zip\\FM$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), ["FolderHistory", "CopyHistory", "PanelPath0", "PanelPath1"]),
        (new(@"^Software\\7-Zip\\Extraction$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), ["PathHistory"]),
        (new(@"^Software\\7-Zip\\Compression$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), ["ArcHistory"]),
        (new(@"^Software\\WinRAR\\(ArcHistory|DialogEditHistory\\[^\\]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null),
        (new(@"^Software\\WinRAR\\General$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), ["LastFolder"]),
    ];

    private const string NotepadPackageState = @"\Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\LocalState";
    private const string NotepadPackageSettings = @"\Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\Settings\settings.dat";

    /// <summary>
    /// Validates a history target: registry locations must be on the allow-list above (never HKLM, never a whole
    /// application key), files must be the application's own state file in its own folder. Returns null when
    /// acceptable, otherwise the reason.
    /// </summary>
    public static string? ValidateHistoryTarget(HistoryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var location = target.Location.Trim();
        if (location.Length == 0)
        {
            return "Location is empty.";
        }

        switch (target.Store)
        {
            case HistoryStore.RegistryKeyContents:
            case HistoryStore.RegistryValues:
            {
                var key = location.Trim('\\');
                if (key.StartsWith("HKEY", Cmp) || key.Contains(':', StringComparison.Ordinal))
                {
                    return "Registry locations are relative to HKEY_CURRENT_USER.";
                }

                if (key.Count(c => c == '*') > 1)
                {
                    return "Only one wildcard segment is allowed.";
                }

                foreach (var (pattern, values) in AllowedHistoryKeys)
                {
                    if (!pattern.IsMatch(key))
                    {
                        continue;
                    }

                    if (target.Store == HistoryStore.RegistryKeyContents)
                    {
                        return values is null ? null : $"'{key}' may only lose named values, not its contents.";
                    }

                    if (target.ValueNames.Count == 0)
                    {
                        return "No value names given.";
                    }

                    if (values is not null && target.ValueNames.Any(v => !values.Contains(v, StringComparer.OrdinalIgnoreCase)))
                    {
                        return $"A value of '{key}' is not on the allow-list.";
                    }

                    return null;
                }

                return $"'{key}' is not a known history location.";
            }

            case HistoryStore.NotepadTabs:
                return EndsWith(location, NotepadPackageState) ? null : "Not Notepad's state folder.";

            case HistoryStore.NotepadRecentFiles:
                return EndsWith(location, NotepadPackageSettings) ? null : "Not Notepad's settings file.";

            case HistoryStore.NotepadPlusPlusSession:
            case HistoryStore.NotepadPlusPlusRecentFiles:
                return string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(location)), "Notepad++", Cmp) ? null : "Not the Notepad++ settings folder.";

            case HistoryStore.VlcRecentMedia:
                return string.Equals(Path.GetFileName(location), "vlc-qt-interface.ini", Cmp)
                    && string.Equals(Path.GetFileName(Path.GetDirectoryName(location) ?? string.Empty), "vlc", Cmp)
                    ? null
                    : "Not VLC's interface settings file.";

            default:
                return "Unknown history store.";
        }

        static bool EndsWith(string path, string suffix) => Path.TrimEndingDirectorySeparator(path).EndsWith(suffix, Cmp) && Path.IsPathFullyQualified(path);
    }

    /// <summary>
    /// Final check that runs immediately before a file or directory is deleted.
    /// Returns null when deletion is allowed, otherwise the reason it is blocked.
    /// </summary>
    public static string? CheckDeletable(string fullPath, string targetRoot, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        var normalizedRoot = NormalizeDirectory(targetRoot);
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));

        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, Cmp)
            && !string.Equals(normalizedPath, normalizedRoot, Cmp))
        {
            return "Path escaped the cleanup root.";
        }

        if (isDirectory && string.Equals(normalizedPath, normalizedRoot, Cmp))
        {
            return "The cleanup root itself is never deleted.";
        }

        foreach (var protectedRoot in ProtectedRoots.Value)
        {
            if (string.Equals(protectedRoot, normalizedPath, Cmp))
            {
                return "Protected system or user folder.";
            }
        }

        var name = Path.GetFileName(normalizedPath);
        if (!isDirectory && NeverDeleteFileNames.Contains(name))
        {
            return "Protected user data file.";
        }

        if (IsUnderBrowserRoot(normalizedPath) && HasProtectedBrowserSegment(normalizedPath, normalizedRoot))
        {
            return "Browser extension or user data (protected).";
        }

        return null;
    }

    /// <summary>True when the directory is (or is under) a browser data root and its name is protected.</summary>
    public static bool IsProtectedBrowserDirectory(string directoryPath)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        return IsUnderBrowserRoot(directoryPath) && ProtectedBrowserDirectoryNames.Contains(name);
    }

    public static bool IsNeverDeleteFileName(string fileName) => NeverDeleteFileNames.Contains(fileName);

    internal static bool IsParentOf(string parent, string child)
    {
        var p = NormalizeDirectory(parent) + Path.DirectorySeparatorChar;
        var c = NormalizeDirectory(child) + Path.DirectorySeparatorChar;
        return c.Length > p.Length && c.StartsWith(p, Cmp);
    }

    internal static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static bool IsUnderBrowserRoot(string path)
    {
        foreach (var marker in BrowserRootMarkers)
        {
            if (path.Contains(marker, Cmp))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasProtectedBrowserSegment(string normalizedPath, string normalizedRoot)
    {
        // Only inspect segments below the rule root: a rule that intentionally targets e.g.
        // "...\Default\Extensions\Temp" has "Extensions" in its root and that is the rule author's decision.
        var relative = normalizedPath.Length > normalizedRoot.Length
            ? normalizedPath[(normalizedRoot.Length + 1)..]
            : string.Empty;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ProtectedBrowserDirectoryNames.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> BuildProtectedRoots()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                try
                {
                    set.Add(NormalizeDirectory(p));
                }
                catch (Exception)
                {
                    // Ignore malformed environment values.
                }
            }
        }

        void AddSpecial(Environment.SpecialFolder folder) => Add(Environment.GetFolderPath(folder));

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windir) ?? "C:\\";

        Add(windir);
        Add(Path.Combine(windir, "System32"));
        Add(Path.Combine(windir, "SysWOW64"));
        Add(Path.Combine(windir, "WinSxS"));
        Add(Path.Combine(windir, "Installer"));
        Add(Path.Combine(windir, "Boot"));
        Add(Path.Combine(windir, "Fonts"));
        Add(Path.Combine(windir, "servicing"));
        Add(Path.Combine(windir, "System32", "config"));
        Add(Path.Combine(windir, "System32", "drivers"));
        Add(Path.Combine(windir, "System32", "DriverStore"));
        Add(Path.Combine(windir, "SoftwareDistribution", "DataStore"));
        Add(Path.Combine(windir, "assembly"));
        Add(Path.Combine(windir, "Microsoft.NET"));
        Add(Path.Combine(systemDrive, "Recovery"));
        Add(Path.Combine(systemDrive, "System Volume Information"));
        Add(Path.Combine(systemDrive, "Users"));
        Add(Path.Combine(systemDrive, "$Recycle.Bin"));
        Add(Path.Combine(systemDrive, "Program Files"));
        Add(Path.Combine(systemDrive, "Program Files (x86)"));
        Add(Path.Combine(systemDrive, "ProgramData"));
        Add(Path.Combine(systemDrive, "ProgramData", "Microsoft", "Network", "Downloader"));

        AddSpecial(Environment.SpecialFolder.ProgramFiles);
        AddSpecial(Environment.SpecialFolder.ProgramFilesX86);
        AddSpecial(Environment.SpecialFolder.CommonApplicationData);
        AddSpecial(Environment.SpecialFolder.UserProfile);
        AddSpecial(Environment.SpecialFolder.ApplicationData);
        AddSpecial(Environment.SpecialFolder.LocalApplicationData);
        AddSpecial(Environment.SpecialFolder.Desktop);
        AddSpecial(Environment.SpecialFolder.DesktopDirectory);
        AddSpecial(Environment.SpecialFolder.MyDocuments);
        AddSpecial(Environment.SpecialFolder.MyPictures);
        AddSpecial(Environment.SpecialFolder.MyMusic);
        AddSpecial(Environment.SpecialFolder.MyVideos);
        AddSpecial(Environment.SpecialFolder.Favorites);
        AddSpecial(Environment.SpecialFolder.StartMenu);
        AddSpecial(Environment.SpecialFolder.Startup);
        AddSpecial(Environment.SpecialFolder.CommonStartMenu);
        AddSpecial(Environment.SpecialFolder.CommonDesktopDirectory);
        AddSpecial(Environment.SpecialFolder.CommonDocuments);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(profile, "Downloads"));
        Add(Path.Combine(profile, "OneDrive"));
        Add(Path.Combine(profile, "Contacts"));
        Add(Path.Combine(profile, "Links"));
        Add(Path.Combine(profile, "Saved Games"));
        Add(Path.Combine(profile, "Searches"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Add(Path.Combine(local, "Microsoft"));
        Add(Path.Combine(local, "Microsoft", "Windows"));
        Add(Path.Combine(local, "Packages"));
        Add(Path.Combine(local, "Programs"));
        Add(Path.Combine(roaming, "Microsoft"));
        Add(Path.Combine(roaming, "Microsoft", "Windows"));

        return set;
    }
}