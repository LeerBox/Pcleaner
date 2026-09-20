using PCleaner.Core.Browsers;
using PCleaner.Core.Engine;
using PCleaner.Core.Model;
using PCleaner.Core.Rules;

namespace PCleaner.Core.Tests;

public sealed class BrowserRuleProviderTests
{
    private static DetectedBrowser MakeChromium(TempTree tree, string id, string name, string userDataRelative, string? cacheRootRelative = null)
    {
        var root = tree.Dir(userDataRelative);
        var definition = new BrowserDefinition
        {
            Id = id,
            Name = name,
            Family = BrowserFamily.Chromium,
            ProcessNames = [id],
            DataRoots = [root],
            SeparateCacheRoots = cacheRootRelative is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [root] = tree.Dir(cacheRootRelative) },
        };
        return new BrowserDetector([definition]).Detect().Single();
    }

    [Fact]
    public async Task Chromium_cache_rule_removes_caches_and_keeps_everything_else()
    {
        using var tree = new TempTree();
        tree.File(@"Chrome\User Data\Local State", """{"profile":{"info_cache":{"Default":{"name":"Me"}}}}""");
        tree.File(@"Chrome\User Data\Default\Preferences", "{}");
        var cacheFile = tree.File(@"Chrome\User Data\Default\Cache\Cache_Data\f_0001", new string('x', 100));
        var codeCache = tree.File(@"Chrome\User Data\Default\Code Cache\js\index", "x");
        var gpu = tree.File(@"Chrome\User Data\Default\GPUCache\data_0", "x");
        var extCache = tree.File(@"Chrome\User Data\Default\Storage\ext\abcdefgh\def\Cache\f_0002", "x");
        var extData = tree.File(@"Chrome\User Data\Default\Storage\ext\abcdefgh\def\Local Storage\leveldb\000001.log", "x");
        var extension = tree.File(@"Chrome\User Data\Default\Extensions\abcdefgh\1.0\manifest.json", "{}");
        var extSettings = tree.File(@"Chrome\User Data\Default\Local Extension Settings\abcdefgh\000003.log", "x");
        var bookmarks = tree.File(@"Chrome\User Data\Default\Bookmarks", "{}");
        var login = tree.File(@"Chrome\User Data\Default\Login Data", "sqlite");
        var history = tree.File(@"Chrome\User Data\Default\History", "sqlite");
        var cookies = tree.File(@"Chrome\User Data\Default\Network\Cookies", "sqlite");
        var localStorage = tree.File(@"Chrome\User Data\Default\Local Storage\leveldb\000001.log", "x");
        var idbSite = tree.File(@"Chrome\User Data\Default\IndexedDB\https_example.com_0.indexeddb.leveldb\000001.log", "x");
        var idbExt = tree.File(@"Chrome\User Data\Default\IndexedDB\chrome-extension_abcdefgh_0.indexeddb.leveldb\000001.log", "x");
        var shader = tree.File(@"Chrome\User Data\ShaderCache\data_1", "x");
        var localState = tree.File(@"Chrome\User Data\Local State", """{"profile":{"info_cache":{"Default":{"name":"Me"}}}}""");

        var browser = MakeChromium(tree, "chrome", "Chrome", @"Chrome\User Data");
        var rules = new BrowserRuleProvider([browser]).GetRules();

        var cacheRule = rules.Single(r => r.Id.EndsWith(".cache", StringComparison.Ordinal) && r.Id.Contains("default", StringComparison.Ordinal));
        var rootRule = rules.Single(r => r.Id.EndsWith(".root", StringComparison.Ordinal));
        Assert.Equal(RiskLevel.Safe, cacheRule.Risk);
        Assert.Contains("chrome", cacheRule.ConflictingProcesses);

        // Only Safe rules get cleaned here, exactly as the default selection would do.
        var safeRules = rules.Where(r => r.Risk == RiskLevel.Safe).ToList();
        var scans = await new Scanner().ScanAsync(safeRules, null, CancellationToken.None);
        var results = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);
        Assert.All(results, r => Assert.Empty(r.Failures));

        Assert.False(File.Exists(cacheFile));
        Assert.False(File.Exists(codeCache));
        Assert.False(File.Exists(gpu));
        Assert.False(File.Exists(extCache));
        Assert.False(File.Exists(shader));

        Assert.True(File.Exists(extData), "extension partition storage must survive");
        Assert.True(File.Exists(extension));
        Assert.True(File.Exists(extSettings));
        Assert.True(File.Exists(bookmarks));
        Assert.True(File.Exists(login));
        Assert.True(File.Exists(history), "history is a privacy option, not a cache");
        Assert.True(File.Exists(cookies), "cookies are a privacy option, not a cache");
        Assert.True(File.Exists(localStorage), "Local Storage holds extension data and is never a target");
        Assert.True(File.Exists(idbSite), "IndexedDB is site data (off by default)");
        Assert.True(File.Exists(idbExt));
        Assert.True(File.Exists(localState));
        Assert.True(Directory.Exists(Path.Combine(tree.Root, @"Chrome\User Data\Default\Cache")), "the cache root itself stays");
    }

    [Fact]
    public async Task Chromium_site_data_rule_never_touches_extension_origins()
    {
        using var tree = new TempTree();
        tree.File(@"Chrome\User Data\Default\Preferences", "{}");
        var idbSite = tree.File(@"Chrome\User Data\Default\IndexedDB\https_example.com_0.indexeddb.leveldb\000001.log", "x");
        var idbExt = tree.File(@"Chrome\User Data\Default\IndexedDB\chrome-extension_abcdefgh_0.indexeddb.leveldb\000001.log", "x");
        var sw = tree.File(@"Chrome\User Data\Default\Service Worker\CacheStorage\abc\index.txt", "x");

        var browser = MakeChromium(tree, "chrome", "Chrome", @"Chrome\User Data");
        var siteData = new BrowserRuleProvider([browser]).GetRules().Single(r => r.Id.EndsWith(".sitedata", StringComparison.Ordinal));
        Assert.Equal(RiskLevel.Moderate, siteData.Risk);

        var scans = await new Scanner().ScanAsync([siteData], null, CancellationToken.None);
        await new Cleaner().CleanAsync(scans, null, CancellationToken.None);

        Assert.False(File.Exists(idbSite));
        Assert.False(File.Exists(sw));
        Assert.True(File.Exists(idbExt));
    }

    [Fact]
    public void Opera_layout_targets_the_local_cache_twin()
    {
        using var tree = new TempTree();
        tree.File(@"Roaming\Opera Software\Opera Stable\Preferences", "{}");
        tree.Dir(@"Local\Opera Software\Opera Stable\Cache");

        var browser = MakeChromium(tree, "opera", "Opera", @"Roaming\Opera Software\Opera Stable", @"Local\Opera Software\Opera Stable");
        var cacheRule = new BrowserRuleProvider([browser]).GetRules().Single(r => r.Id.EndsWith(".cache", StringComparison.Ordinal) && !r.Id.EndsWith(".root", StringComparison.Ordinal));

        var expected = Path.Combine(tree.Root, @"Local\Opera Software\Opera Stable\Cache");
        Assert.Contains(cacheRule.Targets, t => string.Equals(t.Path, expected, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cacheRule.Targets, t => t.Path.EndsWith(@"Local\Opera Software\Opera Stable\Code Cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Vivaldi_never_gets_a_session_rule()
    {
        using var tree = new TempTree();
        tree.File(@"Vivaldi\User Data\Default\Preferences", "{}");
        var browser = MakeChromium(tree, "vivaldi", "Vivaldi", @"Vivaldi\User Data");

        var rules = new BrowserRuleProvider([browser]).GetRules();

        Assert.DoesNotContain(rules, r => r.Id.EndsWith(".session", StringComparison.Ordinal));
        Assert.Contains(rules, r => r.Id.EndsWith(".cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Firefox_rules_clean_local_caches_and_preserve_addon_storage()
    {
        using var tree = new TempTree();
        var root = tree.Dir(@"Roaming\Mozilla\Firefox");
        var localProfiles = tree.Dir(@"Local\Mozilla\Firefox\Profiles");
        tree.File(@"Roaming\Mozilla\Firefox\profiles.ini", """
            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/abcd.default-release
            Default=1
            """);
        var profile = @"Roaming\Mozilla\Firefox\Profiles\abcd.default-release";
        tree.File($@"{profile}\prefs.js", "// prefs");
        var cache2 = tree.File(@"Local\Mozilla\Firefox\Profiles\abcd.default-release\cache2\entries\ABC", "x");
        var startup = tree.File(@"Local\Mozilla\Firefox\Profiles\abcd.default-release\startupCache\startupCache.8.little", "x");
        var shader = tree.File($@"{profile}\shader-cache\abc.bin", "x");
        var places = tree.File($@"{profile}\places.sqlite", "sqlite");
        var key4 = tree.File($@"{profile}\key4.db", "db");
        var xpi = tree.File($@"{profile}\extensions\uBlock0@raymondhill.net.xpi", "zip");
        var extStorage = tree.File($@"{profile}\storage\default\moz-extension+++1111-2222^userContextId=4294967295\idb\1.sqlite", "x");
        var siteStorage = tree.File($@"{profile}\storage\default\https+++example.com\idb\2.sqlite", "x");
        var storageSync = tree.File($@"{profile}\storage-sync-v2.sqlite", "x");
        var cookies = tree.File($@"{profile}\cookies.sqlite", "x");
        var session = tree.File($@"{profile}\sessionstore-backups\recovery.jsonlz4", "x");
        var crash = tree.File(@"Roaming\Mozilla\Firefox\Crash Reports\pending\1.dmp", "x");

        var definition = new BrowserDefinition
        {
            Id = "firefox",
            Name = "Firefox",
            Family = BrowserFamily.Gecko,
            ProcessNames = ["firefox"],
            DataRoots = [root],
            LocalProfilesRoot = localProfiles,
        };
        var browser = new BrowserDetector([definition]).Detect().Single();
        var rules = new BrowserRuleProvider([browser]).GetRules();

        var safe = rules.Where(r => r.Risk == RiskLevel.Safe).ToList();
        var scans = await new Scanner().ScanAsync(safe, null, CancellationToken.None);
        var results = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);
        Assert.All(results, r => Assert.Empty(r.Failures));

        Assert.False(File.Exists(cache2));
        Assert.False(File.Exists(startup));
        Assert.False(File.Exists(shader));
        Assert.False(File.Exists(crash));

        Assert.True(File.Exists(places));
        Assert.True(File.Exists(key4));
        Assert.True(File.Exists(xpi));
        Assert.True(File.Exists(extStorage));
        Assert.True(File.Exists(siteStorage), "site storage is off by default");
        Assert.True(File.Exists(storageSync));
        Assert.True(File.Exists(cookies));
        Assert.True(File.Exists(session));

        // The opt-in site data rule removes http(s) origins but never moz-extension origins.
        var siteData = rules.Single(r => r.Id.EndsWith(".sitedata", StringComparison.Ordinal));
        var siteScans = await new Scanner().ScanAsync([siteData], null, CancellationToken.None);
        await new Cleaner().CleanAsync(siteScans, null, CancellationToken.None);
        Assert.False(File.Exists(siteStorage));
        Assert.True(File.Exists(extStorage));
    }

    [Fact]
    public void Every_browser_rule_has_conflicting_processes_and_safe_targets()
    {
        using var tree = new TempTree();
        tree.File(@"Chrome\User Data\Default\Preferences", "{}");
        var browser = MakeChromium(tree, "chrome", "Chrome", @"Chrome\User Data");

        var rules = new BrowserRuleProvider([browser]).GetRules();

        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.NotEmpty(r.ConflictingProcesses));
        Assert.All(rules, r => Assert.NotEmpty(r.LockProbeFiles));
        Assert.All(rules.SelectMany(r => r.Targets), t => Assert.Null(SafetyGuard.ValidateTargetRoot(t)));
        Assert.All(rules.SelectMany(r => r.Databases), d => Assert.Null(SafetyGuard.ValidateDatabaseTarget(d)));
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());

        // Form history and addresses live in "Web Data", which is a protected file: they must be row-level purges.
        var formHistory = rules.Single(r => r.Id.EndsWith(".formhistory", StringComparison.Ordinal));
        var addresses = rules.Single(r => r.Id.EndsWith(".addresses", StringComparison.Ordinal));
        foreach (var rule in new[] { formHistory, addresses })
        {
            Assert.Equal(RuleAction.PurgeDatabaseRows, rule.Action);
            Assert.Equal(RiskLevel.Privacy, rule.Risk);
            Assert.False(rule.EnabledByDefault);
            Assert.Empty(rule.Targets);
            var database = Assert.Single(rule.Databases);
            Assert.Equal("Web Data", Path.GetFileName(database.Path));
            Assert.True(SafetyGuard.IsNeverDeleteFileName(Path.GetFileName(database.Path)));
        }

        Assert.Equal(DatabasePurge.ChromiumFormHistory, formHistory.Databases[0].Purge);
        Assert.Equal(DatabasePurge.ChromiumLocalAddresses, addresses.Databases[0].Purge);
    }
}