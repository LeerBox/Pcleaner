using System.Text;
using PCleaner.Core.Engine;
using PCleaner.Core.Model;
using PCleaner.Core.Storage;
using PCleaner.Core.Storage.LevelDb;

namespace PCleaner.Core.Tests;

/// <summary>
/// Managed LevelDB reader/writer and the site-only Local Storage purge. Synthetic databases are written with our
/// own writer; the machine's real Brave/Edge "Local Storage" folders (copied) prove the reader against files that
/// Chromium's LevelDB produced (snappy blocks, manifests with many edits, multi-level tables).
/// </summary>
public sealed class LocalStorageTests
{
    private static byte[] DataKey(string origin, string key) => [.. Encoding.UTF8.GetBytes("_" + origin), 0, 1, .. Encoding.Latin1.GetBytes(key)];

    private static byte[] Value(string text) => [1, .. Encoding.Latin1.GetBytes(text)];

    private static byte[] MetaKey(string origin) => Encoding.UTF8.GetBytes("META:" + origin);

    private static byte[] AccessMetaKey(string origin) => Encoding.UTF8.GetBytes("METAACCESS:" + origin);

    /// <summary>protobuf: field 1 varint (last_modified, base::Time micros), field 2 varint (size).</summary>
    private static byte[] MetaValue(long micros, ulong size)
    {
        var buffer = new List<byte> { 0x08 };
        Varint.Write(buffer, (ulong)micros);
        buffer.Add(0x10);
        Varint.Write(buffer, size);
        return buffer.ToArray();
    }

    private static string ProfileLevelDb(TempTree tree) => Path.Combine(tree.Dir(@"Brave-Browser\User Data\Default\Local Storage"), "leveldb");

    private static List<LevelDbEntry> SampleEntries()
    {
        var micros = (DateTime.UtcNow - DateTime.FromFileTimeUtc(0)).Ticks / 10;
        return
        [
            new LevelDbEntry("VERSION"u8.ToArray(), "1"u8.ToArray()),
            new LevelDbEntry(MetaKey("https://www.tiktok.com"), MetaValue(micros, 500)),
            new LevelDbEntry(AccessMetaKey("https://www.tiktok.com"), MetaValue(micros, 0)),
            new LevelDbEntry(DataKey("https://www.tiktok.com", "ttwid_cache"), Value("device-id-123")),
            new LevelDbEntry(DataKey("https://www.tiktok.com", "__tea_cache_tokens_1988"), Value("{\"web_id\":\"777\"}")),
            new LevelDbEntry(MetaKey("https://www.youtube.com"), MetaValue(micros, 100)),
            new LevelDbEntry(DataKey("https://www.youtube.com", "yt-remote-device-id"), Value("uuid")),
            new LevelDbEntry(DataKey("http://insecure.example", "k"), Value("v")),
            new LevelDbEntry(MetaKey("http://insecure.example"), MetaValue(micros, 2)),
            new LevelDbEntry(MetaKey("chrome-extension://abcdefghijklmnopabcdefghijklmnop"), MetaValue(micros, 40)),
            new LevelDbEntry(DataKey("chrome-extension://abcdefghijklmnopabcdefghijklmnop", "settings"), Value("{\"theme\":\"dark\"}")),
            new LevelDbEntry(DataKey("chrome-extension://abcdefghijklmnopabcdefghijklmnop", "license"), Value("KEEP-ME")),
            new LevelDbEntry(DataKey("chrome://newtab", "layout"), Value("grid")),
            new LevelDbEntry(DataKey("devtools://devtools", "panel"), Value("elements")),
            new LevelDbEntry(DataKey("file:///C:/notes/index.html", "draft"), Value("mine")),
        ];
    }

    [Fact]
    public void Writer_and_reader_round_trip_including_large_values_and_many_keys()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        var random = new Random(42);
        var entries = new List<LevelDbEntry>();
        for (var i = 0; i < 3000; i++)
        {
            var value = new byte[random.Next(0, 4000)];
            random.NextBytes(value);
            entries.Add(new LevelDbEntry(DataKey("https://bulk.example", $"key-{i:D5}"), value));
        }

        var big = new byte[300_000];
        random.NextBytes(big);
        entries.Add(new LevelDbEntry(DataKey("https://bulk.example", "huge"), big)); // spans several 32 KiB log blocks

        LevelDbWriter.WriteFreshDatabase(directory, entries);
        var read = LevelDbReader.ReadAll(directory);

        Assert.Equal(entries.Count, read.Count);
        var map = read.ToDictionary(e => Convert.ToHexString(e.Key), e => e.Value);
        foreach (var entry in entries)
        {
            Assert.Equal(entry.Value, map[Convert.ToHexString(entry.Key)]);
        }

        Assert.Equal("MANIFEST-000001\n", File.ReadAllText(Path.Combine(directory, "CURRENT")));
        Assert.True(File.Exists(Path.Combine(directory, "000003.log")));
    }

    [Fact]
    public void Reader_applies_newest_sequence_and_deletions()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        LevelDbWriter.WriteFreshDatabase(directory, [new LevelDbEntry("a"u8.ToArray(), "1"u8.ToArray()), new LevelDbEntry("b"u8.ToArray(), "1"u8.ToArray())]);

        // Append a second log file (higher number = newer) that overwrites "a" and deletes "b".
        var batch = new List<byte>();
        batch.AddRange(BitConverter.GetBytes(100UL));
        batch.AddRange(BitConverter.GetBytes(2U));
        batch.Add(1); Varint.Write(batch, 1); batch.Add((byte)'a'); Varint.Write(batch, 1); batch.Add((byte)'2');
        batch.Add(0); Varint.Write(batch, 1); batch.Add((byte)'b');
        var record = batch.ToArray();
        var crc = Crc32C.Mask(Crc32C.Extend(Crc32C.Compute([1]), record));
        var log = new List<byte>();
        log.AddRange(BitConverter.GetBytes(crc));
        log.AddRange(BitConverter.GetBytes((ushort)record.Length));
        log.Add(1);
        log.AddRange(record);
        File.WriteAllBytes(Path.Combine(directory, "000004.log"), log.ToArray());

        var read = LevelDbReader.ReadAll(directory);
        var entry = Assert.Single(read);
        Assert.Equal("a", Encoding.ASCII.GetString(entry.Key));
        Assert.Equal("2", Encoding.ASCII.GetString(entry.Value));
    }

    [Fact]
    public void Corrupt_checksum_is_detected()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        LevelDbWriter.WriteFreshDatabase(directory, SampleEntries());
        var log = Path.Combine(directory, "000003.log");
        var bytes = File.ReadAllBytes(log);
        bytes[20] ^= 0xFF;
        File.WriteAllBytes(log, bytes);

        // Strict reading (used before any rewrite) reports the damage; the purge refuses and leaves the file alone.
        Assert.Throws<InvalidDataException>(() => LevelDbReader.ReadAll(directory));
        var result = ChromiumLocalStorage.RemoveWebsites(directory, null, "test", dryRun: false);
        Assert.NotNull(result.Error);
        Assert.Equal(bytes, File.ReadAllBytes(log));

        // Tolerant reading (inspection while the browser writes) stops at the torn record instead of failing.
        var tolerant = LevelDbReader.ReadAll(directory, tolerateTornTail: true);
        Assert.Empty(tolerant);
        Assert.Null(ChromiumLocalStorage.Inspect(directory).Error);
    }

    [Fact]
    public void Inventory_groups_by_origin_and_flags_websites()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        LevelDbWriter.WriteFreshDatabase(directory, SampleEntries());

        var inventory = ChromiumLocalStorage.Inspect(directory);

        Assert.Null(inventory.Error);
        Assert.Equal(7, inventory.Origins.Count);
        var tiktok = inventory.Origins.Single(o => o.Origin == "https://www.tiktok.com");
        Assert.Equal(2, tiktok.Keys);
        Assert.True(tiktok.IsWebsite);
        Assert.NotNull(tiktok.LastModifiedUtc);
        Assert.Equal(DateTime.UtcNow.Year, tiktok.LastModifiedUtc!.Value.Year);
        Assert.Equal(3, inventory.Websites.Count);
        Assert.Equal(4, inventory.WebsiteKeys);
        Assert.False(inventory.Origins.Single(o => o.Origin.StartsWith("chrome-extension://", StringComparison.Ordinal)).IsWebsite);
        Assert.False(inventory.Origins.Single(o => o.Origin.StartsWith("file://", StringComparison.Ordinal)).IsWebsite);
    }

    [Fact]
    public void Purge_removes_websites_only_and_keeps_extension_bytes_identical()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        var entries = SampleEntries();
        LevelDbWriter.WriteFreshDatabase(directory, entries);
        var backupRoot = tree.Dir("Backups");

        var result = ChromiumLocalStorage.RemoveWebsites(directory, backupRoot, "Brave-Browser_Default", dryRun: false);

        Assert.Null(result.Error);
        Assert.Equal(4, result.RemovedKeys);
        Assert.Equal(3, result.RemovedOrigins);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        var remaining = LevelDbReader.ReadAll(directory);
        var expected = entries.Where(e => ChromiumLocalStorage.OriginOf(e.Key, out _) is not { } o || !ChromiumLocalStorage.IsWebsiteOrigin(o)).ToList();
        Assert.Equal(expected.Count, remaining.Count);
        foreach (var entry in expected)
        {
            var match = remaining.Single(r => r.Key.AsSpan().SequenceEqual(entry.Key));
            Assert.Equal(entry.Value, match.Value);
        }

        Assert.DoesNotContain(remaining, r => Encoding.UTF8.GetString(r.Key).Contains("tiktok", StringComparison.Ordinal));
        Assert.DoesNotContain(remaining, r => Encoding.UTF8.GetString(r.Key).Contains("youtube", StringComparison.Ordinal));
        Assert.Contains(remaining, r => Encoding.UTF8.GetString(r.Key) == "VERSION");
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(directory)!, "leveldb.pcleaner-new")));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(directory)!, "leveldb.pcleaner-old")));

        // Second pass: nothing left to remove, database untouched.
        var stamp = Directory.GetLastWriteTimeUtc(directory);
        var again = ChromiumLocalStorage.RemoveWebsites(directory, backupRoot, "Brave-Browser_Default", dryRun: false);
        Assert.Null(again.Error);
        Assert.Equal(0, again.RemovedOrigins);
        Assert.Equal(stamp, Directory.GetLastWriteTimeUtc(directory));
    }

    [Fact]
    public void Purge_carries_over_files_that_leveldb_does_not_own()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        LevelDbWriter.WriteFreshDatabase(directory, SampleEntries());
        // Chromium's DOM-storage SQLite rollout tags LevelDB folders it created with this marker.
        File.WriteAllText(Path.Combine(directory, "exp-v1"), string.Empty);
        File.WriteAllBytes(Path.Combine(directory, "LOG"), "info log"u8.ToArray());

        var result = ChromiumLocalStorage.RemoveWebsites(directory, tree.Dir("Backups"), "Brave-Browser_Default", dryRun: false);

        Assert.Null(result.Error);
        Assert.Equal(3, result.RemovedOrigins);
        Assert.True(File.Exists(Path.Combine(directory, "exp-v1")));
        Assert.False(File.Exists(Path.Combine(directory, "LOG"))); // LevelDB's own info log is regenerated by the browser
        Assert.True(File.Exists(Path.Combine(directory, "CURRENT")));
        var keptExpected = SampleEntries().Count(e => ChromiumLocalStorage.OriginOf(e.Key, out _) is not { } o || !ChromiumLocalStorage.IsWebsiteOrigin(o));
        Assert.Equal(keptExpected, LevelDbReader.ReadAll(directory).Count);
        Assert.False(ChromiumLocalStorage.IsLevelDbFile("exp-v1"));
        Assert.True(ChromiumLocalStorage.IsLevelDbFile("MANIFEST-000001"));
        Assert.True(ChromiumLocalStorage.IsLevelDbFile("000005.ldb"));
        Assert.True(ChromiumLocalStorage.IsLevelDbFile("000003.log"));
    }

    [Fact]
    public void Inspection_works_while_the_log_is_held_open_for_writing()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        LevelDbWriter.WriteFreshDatabase(directory, SampleEntries());

        // Chromium keeps the current log open with FILE_SHARE_READ | FILE_SHARE_WRITE and appends to it.
        using var writer = new FileStream(Path.Combine(directory, "000003.log"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        writer.Seek(0, SeekOrigin.End);
        writer.Write([0x11, 0x22, 0x33]); // a record header that is still being written
        writer.Flush();

        var inventory = ChromiumLocalStorage.Inspect(directory);

        Assert.Null(inventory.Error);
        Assert.Equal(3, inventory.Websites.Count);
    }

    [Fact]
    public void Dry_run_reports_but_changes_nothing()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        LevelDbWriter.WriteFreshDatabase(directory, SampleEntries());
        var before = Directory.GetFiles(directory).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);

        var result = ChromiumLocalStorage.RemoveWebsites(directory, tree.Dir("Backups"), "x", dryRun: true);

        Assert.Null(result.Error);
        Assert.Equal(3, result.RemovedOrigins);
        Assert.Null(result.BackupPath);
        foreach (var (name, bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, name)));
        }
    }

    [Fact]
    public void Locked_database_is_left_alone()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        LevelDbWriter.WriteFreshDatabase(directory, SampleEntries());

        // The browser keeps LOCK (and the log) open; an open handle inside the folder blocks the rename.
        using var handle = new FileStream(Path.Combine(directory, "LOCK"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var result = ChromiumLocalStorage.RemoveWebsites(directory, tree.Dir("Backups"), "x", dryRun: false);

        Assert.NotNull(result.Error);
        Assert.Contains("in use", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, ChromiumLocalStorage.Inspect(directory).Websites.Count);
    }

    [Fact]
    public void Old_backups_are_pruned()
    {
        using var tree = new TempTree();
        var root = tree.Dir("Backups");
        var old = tree.File(@"Backups\Brave\Local Storage-old.zip", "x", DateTime.UtcNow.AddDays(-9));
        var recent = tree.File(@"Backups\Brave\Local Storage-new.zip", "x", DateTime.UtcNow.AddDays(-1));

        ChromiumLocalStorage.PruneBackups(root);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task Engine_handles_the_local_storage_purge_end_to_end()
    {
        using var tree = new TempTree();
        var directory = ProfileLevelDb(tree);
        LevelDbWriter.WriteFreshDatabase(directory, SampleEntries());
        var backupRoot = tree.Dir("Backups");
        var target = new DatabaseTarget { Path = directory, Purge = DatabasePurge.ChromiumSiteLocalStorage, BackupRoot = backupRoot };
        Assert.Null(SafetyGuard.ValidateDatabaseTarget(target));

        var rule = new CleanupRule
        {
            Id = "test.localstorage",
            Name = "Local storage",
            Description = "test",
            Category = RuleCategory.Browsers,
            Group = "Test",
            Risk = RiskLevel.Privacy,
            Action = RuleAction.PurgeDatabaseRows,
            Databases = [target],
        };

        var scans = await new Scanner().ScanAsync([rule], null, CancellationToken.None);
        var scan = Assert.Single(scans);
        Assert.Equal(SkipReason.None, scan.Skip);
        Assert.Equal(3, scan.EntryCount);
        Assert.Contains(scan.Entries, e => e.Field == "www.tiktok.com" && e.Value == "2 keys" && e.Bytes > 0);

        var cleans = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);
        var clean = Assert.Single(cleans);
        Assert.Equal(SkipReason.None, clean.Skip);
        Assert.Equal(3, clean.DeletedEntries);
        Assert.Contains("3 websites removed", clean.Message, StringComparison.Ordinal);
        Assert.Empty(ChromiumLocalStorage.Inspect(directory).Websites);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(backupRoot, "Brave-Browser_Default"), "Local Storage-*.zip"));
        Assert.True(new FileInfo(backup).Length > 0);
    }

    [Fact]
    public void Safety_guard_requires_the_local_storage_folder()
    {
        using var tree = new TempTree();
        var good = ProfileLevelDb(tree);
        var wrongParent = tree.Dir(@"Brave-Browser\User Data\Default\IndexedDB") + @"\leveldb";
        var wrongName = tree.Dir(@"Brave-Browser\User Data\Default\Local Storage") + @"\other";
        var extension = tree.Dir(@"Brave-Browser\User Data\Default\Extensions\abc\Local Storage") + @"\leveldb";

        Assert.Null(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = good, Purge = DatabasePurge.ChromiumSiteLocalStorage }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = wrongParent, Purge = DatabasePurge.ChromiumSiteLocalStorage }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = wrongName, Purge = DatabasePurge.ChromiumSiteLocalStorage }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = extension, Purge = DatabasePurge.ChromiumSiteLocalStorage }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = good, Purge = DatabasePurge.ChromiumFormHistory }));
    }

    /// <summary>Reads the machine's real Local Storage databases (copies) - the strongest test of the reader.</summary>
    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Real_chromium_databases_on_this_machine_are_readable_and_rewritable()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data\Default\Local Storage\leveldb"),
            Path.Combine(local, @"Microsoft\Edge\User Data\Default\Local Storage\leveldb"),
            Path.Combine(local, @"Google\Chrome\User Data\Default\Local Storage\leveldb"),
        }.Where(Directory.Exists).ToList();

        if (candidates.Count == 0)
        {
            return; // nothing to test on this machine
        }

        foreach (var source in candidates)
        {
            using var tree = new TempTree();
            var copy = Path.Combine(tree.Dir(@"Copy\User Data\Default\Local Storage"), "leveldb");
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(source))
            {
                try
                {
                    File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
                }
                catch (IOException)
                {
                    // LOCK is held while the browser runs; everything else copies.
                }
            }

            var inventory = ChromiumLocalStorage.Inspect(copy);
            Assert.True(inventory.Error is null, $"{source}: {inventory.Error}");
            var before = LevelDbReader.ReadAll(copy);
            var keptExpected = before.Where(e => ChromiumLocalStorage.OriginOf(e.Key, out _) is not { } o || !ChromiumLocalStorage.IsWebsiteOrigin(o)).ToList();

            var result = ChromiumLocalStorage.RemoveWebsites(copy, tree.Dir("Backups"), "real", dryRun: false);
            Assert.True(result.Error is null, $"{source}: {result.Error}");
            Assert.Equal(inventory.Websites.Count, result.RemovedOrigins);

            var after = LevelDbReader.ReadAll(copy);
            Assert.Equal(keptExpected.Count, after.Count);
            Assert.Empty(ChromiumLocalStorage.Inspect(copy).Websites);
            Assert.Contains(after, e => Encoding.UTF8.GetString(e.Key) == "VERSION");
        }
    }
}