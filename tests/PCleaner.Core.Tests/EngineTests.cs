using PCleaner.Core.Engine;
using PCleaner.Core.Model;

namespace PCleaner.Core.Tests;

public sealed class EngineTests
{
    private static CleanupRule Rule(string root, RiskLevel risk = RiskLevel.Safe, params string[] conflicting) => new()
    {
        Id = "test.rule",
        Name = "Test rule",
        Description = "test",
        Category = RuleCategory.WindowsUser,
        Risk = risk,
        ConflictingProcesses = conflicting,
        Targets = [new PathTarget { Path = root }],
    };

    [Fact]
    public async Task Scan_then_clean_removes_files_and_empty_directories_but_keeps_root()
    {
        using var tree = new TempTree();
        var root = tree.Dir("cache");
        tree.File(@"cache\a\b\one.bin", new string('x', 1000));
        tree.File(@"cache\two.bin", new string('y', 500));

        var scanner = new Scanner();
        var scans = await scanner.ScanAsync([Rule(root)], null, CancellationToken.None);

        Assert.Single(scans);
        Assert.False(scans[0].IsSkipped);
        Assert.Equal(2, scans[0].FileCount);
        Assert.Equal(2, scans[0].DirectoryCount);
        Assert.Equal(1500, scans[0].TotalBytes);

        var cleaner = new Cleaner();
        var results = await cleaner.CleanAsync(scans, null, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(2, results[0].DeletedFiles);
        Assert.Equal(2, results[0].DeletedDirectories);
        Assert.Equal(1500, results[0].BytesFreed);
        Assert.Empty(results[0].Failures);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Dry_run_deletes_nothing()
    {
        using var tree = new TempTree();
        var root = tree.Dir("cache");
        var file = tree.File(@"cache\one.bin");

        var scans = await new Scanner().ScanAsync([Rule(root)], null, CancellationToken.None);
        var results = await new Cleaner(options: new EngineOptions { DryRun = true }).CleanAsync(scans, null, CancellationToken.None);

        Assert.Equal(1, results[0].DeletedFiles);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Locked_files_are_reported_not_forced()
    {
        using var tree = new TempTree();
        var root = tree.Dir("cache");
        var locked = tree.File(@"cache\locked.bin");
        tree.File(@"cache\free.bin");

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var scans = await new Scanner().ScanAsync([Rule(root)], null, CancellationToken.None);
            var results = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);

            Assert.Equal(1, results[0].DeletedFiles);
            Assert.Single(results[0].Failures);
            Assert.Equal(locked, results[0].Failures[0].Path);
        }

        Assert.True(File.Exists(locked));
    }

    [Fact]
    public async Task Read_only_files_are_deleted()
    {
        using var tree = new TempTree();
        var root = tree.Dir("cache");
        var file = tree.File(@"cache\ro.bin");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var scans = await new Scanner().ScanAsync([Rule(root)], null, CancellationToken.None);
        var results = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);

        Assert.Equal(1, results[0].DeletedFiles);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Missing_location_is_reported_as_not_found()
    {
        using var tree = new TempTree();
        var scans = await new Scanner().ScanAsync([Rule(Path.Combine(tree.Root, "does-not-exist"))], null, CancellationToken.None);

        Assert.Equal(SkipReason.LocationNotFound, scans[0].Skip);
    }

    [Fact]
    public async Task Running_conflicting_process_blocks_cleaning()
    {
        using var tree = new TempTree();
        var root = tree.Dir("cache");
        var file = tree.File(@"cache\one.bin");

        // The test host itself is always running.
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var rule = Rule(root, RiskLevel.Safe, self);

        var scans = await new Scanner().ScanAsync([rule], null, CancellationToken.None);
        Assert.Equal(SkipReason.ApplicationRunning, scans[0].Skip);
        Assert.Contains(self, scans[0].BlockingProcesses);
        Assert.Equal(1, scans[0].FileCount); // still previewed

        var results = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);
        Assert.Equal(SkipReason.ApplicationRunning, results[0].Skip);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Protected_file_names_survive_even_inside_a_target()
    {
        using var tree = new TempTree();
        var root = tree.Dir(@"User Data\Default\Cache");
        tree.File(@"User Data\Default\Cache\f_0001");
        var bookmarks = tree.File(@"User Data\Default\Cache\Bookmarks");

        var scans = await new Scanner().ScanAsync([Rule(root)], null, CancellationToken.None);
        var results = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);

        Assert.Equal(1, results[0].DeletedFiles);
        Assert.True(File.Exists(bookmarks));
    }

    [Fact]
    public async Task Extension_folders_inside_a_browser_target_survive()
    {
        using var tree = new TempTree();
        var profile = tree.Dir(@"Google\Chrome\User Data\Default");
        tree.File(@"Google\Chrome\User Data\Default\Cache\f_0001");
        var ext = tree.File(@"Google\Chrome\User Data\Default\Extensions\abc\1.0\manifest.json");
        var settings = tree.File(@"Google\Chrome\User Data\Default\Local Extension Settings\abc\000003.log");

        // Deliberately (mis)configured rule targeting the whole profile - the guard must still protect extensions.
        var scans = await new Scanner().ScanAsync([Rule(profile)], null, CancellationToken.None);
        var results = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);

        Assert.Equal(1, results[0].DeletedFiles);
        Assert.True(File.Exists(ext));
        Assert.True(File.Exists(settings));
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        using var tree = new TempTree();
        var root = tree.Dir("cache");
        for (var i = 0; i < 50; i++)
        {
            tree.File($@"cache\d{i}\f{i}.bin");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Scanner().ScanAsync([Rule(root)], null, cts.Token));
    }

    [Fact]
    public void FormatBytes_is_human_readable()
    {
        Assert.Equal("0 B", Scanner.FormatBytes(0));
        Assert.Equal("1.0 KB", Scanner.FormatBytes(1024));
        Assert.Equal("1.5 MB", Scanner.FormatBytes(1024 * 1024 + 512 * 1024));
    }
}