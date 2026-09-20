using PCleaner.Core.Model;
using PCleaner.Core.Storage;

namespace PCleaner.Core.Engine;

/// <summary>
/// Entry point for every <see cref="RuleAction.PurgeDatabaseRows"/> target: SQLite purges go to
/// <see cref="SqlitePurger"/>, Chromium's LevelDB-based Local Storage to <see cref="ChromiumLocalStorage"/>.
/// </summary>
public static class DatabasePurger
{
    /// <summary>Directory name of Chromium's DOM storage database and its required parent.</summary>
    internal const string LocalStorageLevelDbName = "leveldb";
    internal const string LocalStorageParentName = "Local Storage";

    /// <summary>The file (or directory) name each purge kind is allowed to operate on (checked by <see cref="SafetyGuard"/>).</summary>
    public static IReadOnlyList<string> AllowedNames(DatabasePurge purge) => purge switch
    {
        DatabasePurge.ChromiumSiteLocalStorage => [LocalStorageLevelDbName],
        _ => SqlitePurger.AllowedFileNames(purge),
    };

    public static bool IsDirectoryTarget(DatabasePurge purge) => purge == DatabasePurge.ChromiumSiteLocalStorage;

    public static DatabaseInspection Inspect(DatabaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Purge != DatabasePurge.ChromiumSiteLocalStorage)
        {
            return SqlitePurger.Inspect(target);
        }

        if (!Directory.Exists(target.Path))
        {
            return new DatabaseInspection { Exists = false };
        }

        var inventory = ChromiumLocalStorage.Inspect(target.Path);
        if (inventory.Error is not null)
        {
            return new DatabaseInspection { Exists = true, IsLocked = inventory.IsLocked, Error = inventory.Error };
        }

        var websites = inventory.Websites;
        var entries = websites
            .Select(o => new DatabaseEntry(DisplayHost(o.Origin), o.Keys == 1 ? "1 key" : $"{o.Keys:N0} keys", o.Keys, o.LastModifiedUtc, o.Bytes))
            .ToList();

        // Values are stored compressed; what can come back is at most the files on disk.
        long onDisk = 0;
        try
        {
            onDisk = Directory.EnumerateFiles(target.Path).Sum(f => new FileInfo(f).Length);
        }
        catch (IOException)
        {
        }

        return new DatabaseInspection
        {
            Exists = true,
            EntryCount = websites.Count,
            Entries = entries,
            EstimatedBytes = onDisk > 0 ? Math.Min(inventory.WebsiteBytes, onDisk) : inventory.WebsiteBytes,
        };
    }

    public static DatabasePurgeResult Purge(DatabaseTarget target, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Purge != DatabasePurge.ChromiumSiteLocalStorage)
        {
            return SqlitePurger.Purge(target, dryRun);
        }

        if (!Directory.Exists(target.Path))
        {
            return new DatabasePurgeResult { Error = "Database not found." };
        }

        var backupRoot = string.IsNullOrWhiteSpace(target.BackupRoot) ? ChromiumLocalStorage.DefaultBackupRoot : target.BackupRoot;
        var result = ChromiumLocalStorage.RemoveWebsites(target.Path, backupRoot, BackupLabel(target.Path), dryRun);
        return new DatabasePurgeResult
        {
            EntriesRemoved = result.RemovedOrigins,
            BytesFreed = result.BytesFreed,
            Error = result.Error,
            Detail = result.BackupPath is null ? null : $"Backup: {result.BackupPath}",
        };
    }

    /// <summary>"www.tiktok.com" for "https://www.tiktok.com" (partitioned keys keep their suffix so they stay distinguishable).</summary>
    internal static string DisplayHost(string origin)
    {
        var host = origin;
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            host = host[(scheme + 3)..];
        }

        var slash = host.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0 && !host.Contains('^', StringComparison.Ordinal))
        {
            host = host[..slash];
        }

        return host;
    }

    /// <summary>Backup folder name: browser + profile derived from the profile path ("Brave-Browser_Default").</summary>
    private static string BackupLabel(string levelDbPath)
    {
        var profile = Path.GetDirectoryName(Path.GetDirectoryName(levelDbPath)) ?? string.Empty; // ...\Default
        var userData = Path.GetDirectoryName(profile) ?? string.Empty;                            // ...\User Data
        var product = Path.GetFileName(Path.GetDirectoryName(userData) ?? string.Empty);          // Brave-Browser / Edge
        var name = Path.GetFileName(profile);
        return string.IsNullOrEmpty(product) ? name : $"{product}_{name}";
    }
}