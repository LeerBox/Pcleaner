using System.Text;
using Microsoft.Data.Sqlite;
using PCleaner.Core.Browsers;
using PCleaner.Core.Model;
using PCleaner.Core.Privacy;
using PCleaner.Core.Storage.LevelDb;

namespace PCleaner.Core.Tests;

/// <summary>The "why does the site still know me" findings, on a synthetic Chromium profile.</summary>
public sealed class PrivacyInsightsTests
{
    private static byte[] DataKey(string origin, string key) => [.. Encoding.UTF8.GetBytes("_" + origin), 0, 1, .. Encoding.Latin1.GetBytes(key)];

    private static DetectedBrowser MakeBrowser(TempTree tree, bool signedInToTikTok, bool syncing, bool withLocalStorage)
    {
        var root = tree.Dir(@"Brave-Browser\User Data");
        var profile = tree.Dir(@"Brave-Browser\User Data\Default");
        tree.File(@"Brave-Browser\User Data\Local State", """{"profile":{"info_cache":{"Default":{"name":"Personal"}}}}""");
        tree.File(@"Brave-Browser\User Data\Default\Preferences", syncing
            ? """{"sync":{"has_setup_completed":true},"account_info":[{"email":"someone@example.test"}]}"""
            : """{"sync":{}}""");

        var cookies = Path.Combine(tree.Dir(@"Brave-Browser\User Data\Default\Network"), "Cookies");
        using (var connection = new SqliteConnection($"Data Source={cookies};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE cookies (creation_utc INTEGER, host_key TEXT, name TEXT, value TEXT, encrypted_value BLOB)";
            command.ExecuteNonQuery();
            command.CommandText = signedInToTikTok
                ? "INSERT INTO cookies VALUES (1, '.tiktok.com', 'sessionid', '', X''), (2, '.tiktok.com', 'sid_tt', '', X''), (3, '.tiktok.com', 'ttwid', '', X''), (4, '.example.com', 'theme', '', X'')"
                : "INSERT INTO cookies VALUES (3, '.tiktok.com', 'ttwid', '', X''), (4, '.example.com', 'theme', '', X'')";
            command.ExecuteNonQuery();
        }

        if (withLocalStorage)
        {
            var levelDb = Path.Combine(tree.Dir(@"Brave-Browser\User Data\Default\Local Storage"), "leveldb");
            LevelDbWriter.WriteFreshDatabase(levelDb,
            [
                new LevelDbEntry("VERSION"u8.ToArray(), "1"u8.ToArray()),
                new LevelDbEntry(DataKey("https://www.tiktok.com", "__tea_cache_tokens_1988"), [1, .. "{}"u8.ToArray()]),
                new LevelDbEntry(DataKey("https://www.tiktok.com", "PUMBAA_FREQ"), [1, .. "x"u8.ToArray()]),
                new LevelDbEntry(DataKey("https://www.youtube.com", "yt-remote-device-id"), [1, .. "id"u8.ToArray()]),
                new LevelDbEntry(DataKey("https://shop.example", "cart"), [1, .. "[]"u8.ToArray()]),
                new LevelDbEntry(DataKey("chrome-extension://abcdefghijklmnopabcdefghijklmnop", "settings"), [1, .. "{}"u8.ToArray()]),
            ]);
        }

        var definition = new BrowserDefinition
        {
            Id = "brave",
            Name = "Brave",
            Family = BrowserFamily.Chromium,
            ProcessNames = ["brave"],
            DataRoots = [root],
        };
        return new BrowserDetector([definition]).Detect().Single();
    }

    private static string RuleId(BrowserProfile profile, string suffix) => $"browser.{profile.Browser.Id}.test.{suffix}";

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Signed_in_platform_and_identifiers_are_reported()
    {
        using var tree = new TempTree();
        var browser = MakeBrowser(tree, signedInToTikTok: true, syncing: true, withLocalStorage: true);

        var insights = new PrivacyInsights().Collect([browser], RuleId);

        var tiktok = Assert.Single(insights, i => i.Title == "Signed in to TikTok");
        Assert.Equal(InsightLevel.Account, tiktok.Level);
        Assert.Equal("Brave — Personal", tiktok.Scope);
        Assert.DoesNotContain("sessionid", tiktok.Detail, StringComparison.Ordinal); // technical proof stays out of the text
        Assert.Contains("sessionid", tiktok.Evidence, StringComparison.Ordinal);
        Assert.Equal(2, tiktok.Actions.Count);
        Assert.All(tiktok.Actions, a => Assert.StartsWith("https://", a.Url, StringComparison.Ordinal));
        Assert.Equal(["browser.brave.test.cookies"], tiktok.RelatedRuleIds);

        var sync = Assert.Single(insights, i => i.Title == "Browser sync is on");
        Assert.Contains("s••••••@example.test", sync.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("someone@", sync.Detail, StringComparison.Ordinal);

        var storage = Assert.Single(insights, i => i.Title == "Websites remember this browser");
        Assert.Equal(InsightLevel.Local, storage.Level);
        Assert.StartsWith("3 websites store data here (TikTok, YouTube / Google); 2 of them keep device or visitor IDs.", storage.Detail, StringComparison.Ordinal);
        Assert.Contains("4 Local Storage entries from 3 website origins", storage.Evidence, StringComparison.Ordinal);
        Assert.Equal(["browser.brave.test.localstorage", "browser.brave.test.cookies", "browser.brave.test.sitedata"], storage.RelatedRuleIds);

        // Sections: the browser profile first (account findings before local ones), then Windows.
        Assert.Equal(InsightLevel.Account, insights[0].Level);
        Assert.Equal("Brave — Personal", insights[0].Scope);
        Assert.Equal(InsightScopeKind.Browser, insights[0].ScopeKind);
        var windows = Assert.Single(insights, i => i.Title.StartsWith("Windows advertising ID", StringComparison.Ordinal));
        Assert.Equal(PrivacyInsight.WindowsScope, windows.Scope);
        Assert.Equal(InsightScopeKind.Windows, windows.ScopeKind);
        Assert.Same(windows, insights[^1]);
        var levelsInProfile = insights.Where(i => i.Scope == "Brave — Personal").Select(i => (int)i.Level).ToList();
        Assert.Equal(levelsInProfile.Order(), levelsInProfile);
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Signed_out_profile_without_storage_is_reported_clear()
    {
        using var tree = new TempTree();
        var browser = MakeBrowser(tree, signedInToTikTok: false, syncing: false, withLocalStorage: false);

        var insights = new PrivacyInsights().Collect([browser], RuleId);

        Assert.DoesNotContain(insights, i => i.Title.StartsWith("Signed in", StringComparison.Ordinal));
        Assert.DoesNotContain(insights, i => i.Title == "Browser sync is on");
        Assert.DoesNotContain(insights, i => i.Title == "Websites remember this browser");
        Assert.All(insights.Where(i => i.ScopeKind == InsightScopeKind.Browser), i => Assert.NotEqual(InsightLevel.Account, i.Level));
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Opened_file_findings_are_grouped_under_this_pc_between_browsers_and_windows()
    {
        using var tree = new TempTree();
        var browser = MakeBrowser(tree, signedInToTikTok: true, syncing: false, withLocalStorage: false);
        var localState = tree.Dir(@"Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\LocalState");
        var tabState = tree.Dir(@"Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\LocalState\TabState");
        File.WriteAllBytes(Path.Combine(tabState, "11111111-1111-1111-1111-111111111111.bin"), [(byte)'N', (byte)'P', 0, 1, 8, .. Encoding.Unicode.GetBytes(@"C:\a.txt"), 0, 5, 0, 0, 0, 0, 0, 0, 0, 0]);
        var rule = new CleanupRule
        {
            Id = "windows.recent.notepad",
            Name = "Notepad tabs & recent files",
            Description = "test",
            Category = RuleCategory.WindowsUser,
            Group = "Recently opened files",
            Action = RuleAction.ForgetHistory,
            History = [new HistoryTarget { Store = HistoryStore.NotepadTabs, Location = localState, Label = "Tab" }],
        };

        var insights = new PrivacyInsights().Collect([browser], RuleId, [rule]);

        var kinds = insights.Select(i => i.ScopeKind).ToList();
        Assert.Equal(kinds.Order(), kinds); // Browser sections, then This PC, then Windows
        var notepad = Assert.Single(insights, i => i.Title.StartsWith("Notepad remembers", StringComparison.Ordinal));
        Assert.Equal(PrivacyInsight.DeviceScope, notepad.Scope);
        Assert.Equal(InsightLevel.Device, notepad.Level);
        Assert.Equal(["windows.recent.notepad"], notepad.RelatedRuleIds);
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Storage_without_identifier_keys_is_worded_without_mentioning_ids()
    {
        using var tree = new TempTree();
        var browser = MakeBrowser(tree, signedInToTikTok: false, syncing: false, withLocalStorage: false);
        var levelDb = Path.Combine(tree.Dir(@"Brave-Browser\User Data\Default\Local Storage"), "leveldb");
        LevelDbWriter.WriteFreshDatabase(levelDb,
        [
            new LevelDbEntry("VERSION"u8.ToArray(), "1"u8.ToArray()),
            new LevelDbEntry(DataKey("https://ntp.msn.com", "layout"), [1, .. "grid"u8.ToArray()]),
        ]);

        var insights = new PrivacyInsights().Collect([browser], RuleId);

        var storage = Assert.Single(insights, i => i.Title == "Websites remember this browser");
        Assert.Equal("1 website stores data in this profile. Stored data can still tell a site it has seen this browser before - the reset items remove it.", storage.Detail);
        Assert.DoesNotContain("Such IDs", storage.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Brave_sync_seed_counts_as_sync()
    {
        using var tree = new TempTree();
        var preferences = tree.File("Preferences", """{"brave_sync_v2":{"seed":"abc"}}""");
        Assert.Equal("Brave Sync is set up for this profile.", PrivacyInsights.ReadChromiumSyncState(preferences));

        var none = tree.File("Preferences2", """{"brave_sync_v2":{"seed":""},"sync":{"requested":false}}""");
        Assert.Null(PrivacyInsights.ReadChromiumSyncState(none));
    }
}