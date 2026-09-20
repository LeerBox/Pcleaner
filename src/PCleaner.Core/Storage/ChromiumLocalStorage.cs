using System.IO.Compression;
using PCleaner.Core.Model;
using PCleaner.Core.Storage.LevelDb;

namespace PCleaner.Core.Storage;

/// <summary>One origin's footprint in a Local Storage database.</summary>
public sealed record LocalStorageOrigin(string Origin, int Keys, long Bytes, DateTime? LastModifiedUtc)
{
    /// <summary>True for ordinary websites; false for extensions and browser-internal pages.</summary>
    public bool IsWebsite => ChromiumLocalStorage.IsWebsiteOrigin(Origin);
}

/// <summary>What <see cref="ChromiumLocalStorage.Inspect"/> found.</summary>
public sealed class LocalStorageInventory
{
    public IReadOnlyList<LocalStorageOrigin> Origins { get; init; } = [];

    public IReadOnlyList<LocalStorageOrigin> Websites => Origins.Where(o => o.IsWebsite).ToList();

    public int WebsiteKeys => Websites.Sum(o => o.Keys);

    public long WebsiteBytes => Websites.Sum(o => o.Bytes);

    public string? Error { get; init; }

    /// <summary>True when the browser holds the database open.</summary>
    public bool IsLocked { get; init; }
}

/// <summary>Outcome of <see cref="ChromiumLocalStorage.RemoveWebsites"/>.</summary>
public sealed class LocalStoragePurgeResult
{
    public int RemovedKeys { get; init; }

    public int RemovedOrigins { get; init; }

    public long BytesFreed { get; init; }

    public string? BackupPath { get; init; }

    public string? Error { get; init; }

    /// <summary>True when nothing was changed because the browser holds the database open.</summary>
    public bool IsLocked { get; init; }
}

/// <summary>
/// Chromium's DOM Local Storage lives in one LevelDB per profile (<c>Local Storage\leveldb</c>) shared by websites,
/// extensions and browser pages. Keys (<c>components/services/storage/dom_storage/local_storage_impl.cc</c>):
/// <list type="bullet">
/// <item><c>VERSION</c> - schema version.</item>
/// <item><c>META:&lt;storage key&gt;</c> / <c>METAACCESS:&lt;storage key&gt;</c> - per-origin metadata.</item>
/// <item><c>_&lt;storage key&gt;\0&lt;format byte&gt;&lt;key&gt;</c> - the values themselves.</item>
/// </list>
/// This class removes the entries of <c>http(s)</c> origins only and rewrites the database, so extension storage
/// (<c>chrome-extension://</c>) and browser pages survive byte for byte.
/// </summary>
public static class ChromiumLocalStorage
{
    private static readonly byte[] DataPrefix = "_"u8.ToArray();
    private static readonly byte[] MetaPrefix = "META:"u8.ToArray();
    private static readonly byte[] AccessMetaPrefix = "METAACCESS:"u8.ToArray();
    private const string InUseMessage = "The database is in use by the browser - nothing was changed.";
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>Backups are kept this long so a rewrite can be undone (copy the folder back while the browser is closed).</summary>
    public static readonly TimeSpan BackupRetention = TimeSpan.FromDays(7);

    public static string DefaultBackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCleaner", "Backups");

    public static bool IsWebsiteOrigin(string origin)
        => origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the database (read-only, tolerant of a browser that is writing to it) and groups it by origin.</summary>
    public static LocalStorageInventory Inspect(string levelDbDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(levelDbDirectory);
        try
        {
            var entries = LevelDbReader.ReadAll(levelDbDirectory, tolerateTornTail: true);
            return new LocalStorageInventory { Origins = Summarize(entries) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return IsSharingViolation(ex)
                ? new LocalStorageInventory { IsLocked = true, Error = InUseMessage }
                : new LocalStorageInventory { Error = Describe(ex) };
        }
    }

    /// <summary>
    /// Removes every website origin. Steps: read everything, zip a backup, write the filtered database next to the
    /// original, verify it by reading it back, then swap the folders. Any failure before the swap leaves the
    /// original untouched; a failed swap is rolled back.
    /// </summary>
    public static LocalStoragePurgeResult RemoveWebsites(string levelDbDirectory, string? backupRoot, string backupLabel, bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(levelDbDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupLabel);

        try
        {
            var directory = Path.GetFullPath(levelDbDirectory);
            var entries = LevelDbReader.ReadAll(directory);
            var keep = new List<LevelDbEntry>();
            var removedKeys = 0;
            var removedOrigins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var origin = OriginOf(entry.Key, out var isData);
                if (origin is not null && IsWebsiteOrigin(origin))
                {
                    if (isData)
                    {
                        removedKeys++;
                    }

                    removedOrigins.Add(origin);
                    continue;
                }

                keep.Add(entry);
            }

            if (removedOrigins.Count == 0)
            {
                return new LocalStoragePurgeResult();
            }

            if (dryRun)
            {
                return new LocalStoragePurgeResult { RemovedKeys = removedKeys, RemovedOrigins = removedOrigins.Count };
            }

            var sizeBefore = DirectorySize(directory);
            string? backupPath = null;
            if (backupRoot is not null)
            {
                backupPath = WriteBackup(directory, backupRoot, backupLabel);
                PruneBackups(backupRoot);
            }

            var parent = Path.GetDirectoryName(directory)!;
            var fresh = Path.Combine(parent, "leveldb.pcleaner-new");
            var old = Path.Combine(parent, "leveldb.pcleaner-old");
            DeleteDirectoryQuietly(fresh);
            DeleteDirectoryQuietly(old);

            LevelDbWriter.WriteFreshDatabase(fresh, keep);
            CopyForeignFiles(directory, fresh);

            // The new database must read back as exactly the kept set before it replaces anything.
            var verification = LevelDbReader.ReadAll(fresh);
            if (!SameEntries(keep, verification))
            {
                DeleteDirectoryQuietly(fresh);
                return new LocalStoragePurgeResult { Error = "Verification of the rewritten database failed - the original was left untouched." };
            }

            try
            {
                Directory.Move(directory, old);
            }
            catch (IOException)
            {
                DeleteDirectoryQuietly(fresh);
                return new LocalStoragePurgeResult { IsLocked = true, Error = InUseMessage };
            }

            try
            {
                Directory.Move(fresh, directory);
            }
            catch (IOException)
            {
                Directory.Move(old, directory);
                DeleteDirectoryQuietly(fresh);
                return new LocalStoragePurgeResult { Error = "Could not put the rewritten database in place - the original was restored." };
            }

            DeleteDirectoryQuietly(old);
            RemoveLegacyWebsiteFiles(parent);

            var sizeAfter = DirectorySize(directory);
            return new LocalStoragePurgeResult
            {
                RemovedKeys = removedKeys,
                RemovedOrigins = removedOrigins.Count,
                BytesFreed = Math.Max(0, sizeBefore - sizeAfter),
                BackupPath = backupPath,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return IsSharingViolation(ex)
                ? new LocalStoragePurgeResult { IsLocked = true, Error = InUseMessage }
                : new LocalStoragePurgeResult { Error = Describe(ex) };
        }
    }

    /// <summary>Removes backups older than <see cref="BackupRetention"/>.</summary>
    public static void PruneBackups(string backupRoot)
    {
        try
        {
            if (!Directory.Exists(backupRoot))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - BackupRetention;
            foreach (var file in Directory.EnumerateFiles(backupRoot, "*.zip", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping only.
        }
    }

    // ------------------------------------------------------------------ key format

    /// <summary>Returns the storage key (origin) a database key belongs to, or null for VERSION and unknown keys.</summary>
    internal static string? OriginOf(byte[] key, out bool isData)
    {
        isData = false;
        if (StartsWith(key, DataPrefix))
        {
            var separator = Array.IndexOf(key, (byte)0, DataPrefix.Length);
            if (separator < 0)
            {
                return null;
            }

            isData = true;
            return System.Text.Encoding.UTF8.GetString(key, DataPrefix.Length, separator - DataPrefix.Length);
        }

        if (StartsWith(key, AccessMetaPrefix))
        {
            return System.Text.Encoding.UTF8.GetString(key, AccessMetaPrefix.Length, key.Length - AccessMetaPrefix.Length);
        }

        if (StartsWith(key, MetaPrefix))
        {
            return System.Text.Encoding.UTF8.GetString(key, MetaPrefix.Length, key.Length - MetaPrefix.Length);
        }

        return null;
    }

    /// <summary>
    /// Decodes the key name of a data entry. Chromium stores keys as UTF-16 (format byte 0) or Latin-1 (format byte
    /// 1); returns null for entries that are not website data.
    /// </summary>
    internal static string? KeyNameOf(byte[] key)
    {
        if (!StartsWith(key, DataPrefix))
        {
            return null;
        }

        var separator = Array.IndexOf(key, (byte)0, DataPrefix.Length);
        if (separator < 0 || separator + 1 >= key.Length)
        {
            return null;
        }

        var format = key[separator + 1];
        var start = separator + 2;
        var length = key.Length - start;
        try
        {
            return format switch
            {
                0 => System.Text.Encoding.Unicode.GetString(key, start, length - (length % 2)),
                1 => System.Text.Encoding.Latin1.GetString(key, start, length),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static List<LocalStorageOrigin> Summarize(IReadOnlyList<LevelDbEntry> entries)
    {
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        var bytes = new Dictionary<string, long>(StringComparer.Ordinal);
        var modified = new Dictionary<string, DateTime?>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var origin = OriginOf(entry.Key, out var isData);
            if (origin is null)
            {
                continue;
            }

            if (isData)
            {
                keys[origin] = keys.GetValueOrDefault(origin) + 1;
                bytes[origin] = bytes.GetValueOrDefault(origin) + entry.Key.Length + entry.Value.Length;
            }
            else
            {
                keys.TryAdd(origin, 0);
                if (StartsWith(entry.Key, MetaPrefix) && !StartsWith(entry.Key, AccessMetaPrefix))
                {
                    modified[origin] = ReadLastModified(entry.Value);
                }
            }
        }

        return keys.Select(kv => new LocalStorageOrigin(kv.Key, kv.Value, bytes.GetValueOrDefault(kv.Key), modified.GetValueOrDefault(kv.Key)))
            .OrderByDescending(o => o.Keys)
            .ToList();
    }

    /// <summary>META values are a protobuf (LocalStorageAreaWriteMetaData): field 1 = last_modified (base::Time), field 2 = size.</summary>
    private static DateTime? ReadLastModified(byte[] value)
    {
        try
        {
            var pos = 0;
            while (pos < value.Length)
            {
                var tag = Varint.ReadUInt64(value, ref pos);
                var field = tag >> 3;
                var wireType = tag & 7;
                if (wireType != 0)
                {
                    return null; // only varints are expected in this message
                }

                var number = Varint.ReadUInt64(value, ref pos);
                if (field == 1 && number > 0)
                {
                    var time = DateTime.FromFileTimeUtc(0).AddTicks((long)number * 10);
                    return time.Year is >= 2000 and <= 2100 ? time : null;
                }
            }
        }
        catch (InvalidDataException)
        {
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static string WriteBackup(string directory, string backupRoot, string label)
    {
        var folder = Path.Combine(backupRoot, Sanitize(label));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Local Storage-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("LOCK", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LOG", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // lock handle and info logs carry no data
                }

                archive.CreateEntryFromFile(file, name, CompressionLevel.Fastest);
            }
        }

        return path;
    }

    private static string Sanitize(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray();
        return new string(chars).Trim('-');
    }

    /// <summary>Pre-2018 Chromium kept one SQLite file per origin next to the LevelDB folder.</summary>
    private static void RemoveLegacyWebsiteFiles(string localStorageDirectory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(localStorageDirectory, "http*_*.localstorage*"))
            {
                File.Delete(file);
            }
        }
        catch (Exception)
        {
            // Legacy leftovers only.
        }
    }

    /// <summary>
    /// Carries over files LevelDB itself does not own, e.g. Chromium's <c>exp-v1</c> marker that records which
    /// storage backend (LevelDB vs. the SQLite rollout) created the database.
    /// </summary>
    private static void CopyForeignFiles(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (IsLevelDbFile(name))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, name), overwrite: false);
        }
    }

    internal static bool IsLevelDbFile(string name)
    {
        if (name.Equals("CURRENT", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LOCK", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LOG", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MANIFEST-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(name);
        return extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ldb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sst", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dbtmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameEntries(List<LevelDbEntry> expected, IReadOnlyList<LevelDbEntry> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        var map = new Dictionary<byte[], byte[]>(ByteArrayComparer.Instance);
        foreach (var entry in actual)
        {
            map[entry.Key] = entry.Value;
        }

        foreach (var entry in expected)
        {
            if (!map.TryGetValue(entry.Key, out var value) || !value.AsSpan().SequenceEqual(entry.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWith(byte[] key, byte[] prefix) => key.Length >= prefix.Length && key.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private static bool IsSharingViolation(Exception ex) => ex is IOException && ex.HResult is ErrorSharingViolation or ErrorLockViolation;

    private static string Describe(Exception ex)
    {
        var message = ex.Message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return message.Length > 160 ? message[..160] + "…" : message;
    }
}