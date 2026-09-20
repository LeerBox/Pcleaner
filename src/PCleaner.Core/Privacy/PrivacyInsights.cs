using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCleaner.Core.Browsers;
using PCleaner.Core.Engine;
using PCleaner.Core.Logging;
using PCleaner.Core.Model;
using PCleaner.Core.Storage;
using PCleaner.Core.Storage.LevelDb;

namespace PCleaner.Core.Privacy;

/// <summary>How strongly a finding explains "the site still knows me".</summary>
public enum InsightLevel
{
    /// <summary>Nothing local can change this (an account sign-in, a synced profile).</summary>
    Account = 0,

    /// <summary>Local data PCleaner can remove.</summary>
    Local = 1,

    /// <summary>What this PC remembers having opened - lists and saved tabs PCleaner can forget.</summary>
    Device = 2,

    /// <summary>A Windows setting outside the browser.</summary>
    System = 3,

    /// <summary>Everything relevant is already clean.</summary>
    Clear = 4,
}

/// <summary>A link to a platform's own controls (opens in the default browser).</summary>
public sealed record InsightAction(string Label, string Url);

/// <summary>Where a finding lives - the section it is listed under.</summary>
public enum InsightScopeKind
{
    /// <summary>A browser profile ("Brave — Personal").</summary>
    Browser = 0,

    /// <summary>This PC: what Windows and installed applications remember having opened.</summary>
    Device = 1,

    /// <summary>A Windows setting.</summary>
    Windows = 2,
}

/// <summary>One thing PCleaner found out about why sites recognise this PC.</summary>
public sealed class PrivacyInsight
{
    public const string DeviceScope = "This PC";
    public const string WindowsScope = "Windows";

    public required InsightLevel Level { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    /// <summary>Technical proof behind the finding (cookie names, key counts) - shown as a tooltip, not in the text.</summary>
    public string? Evidence { get; init; }

    /// <summary>Section the finding is listed under: the browser profile ("Brave — Personal"), "This PC" or "Windows".</summary>
    public required string Scope { get; init; }

    public InsightScopeKind ScopeKind { get; init; } = InsightScopeKind.Browser;

    /// <summary>Rule ids that remove the local data behind this finding (empty for account/system findings).</summary>
    public IReadOnlyList<string> RelatedRuleIds { get; init; } = [];

    public IReadOnlyList<InsightAction> Actions { get; init; } = [];
}

/// <summary>A video/social platform and how it identifies a signed-in user locally.</summary>
internal sealed record Platform(string Name, string[] Hosts, string[] SessionCookies, string HistoryUrl, string PersonalizationUrl, string Explanation);

/// <summary>
/// Explains recommendation "memory": which platforms are signed in (their history lives in the account, not on
/// this PC), whether the browser syncs its profile, which sites keep device identifiers in Local Storage, and
/// whether Windows exposes an advertising ID. Reads only cookie NAMES, never values, and never writes anything.
/// </summary>
public sealed class PrivacyInsights
{
    private static readonly Platform[] Platforms =
    [
        new("YouTube / Google", ["youtube.com", "google.com", "google.de", "google.co.uk", "google.fr"], ["SID", "HSID", "__Secure-3PSID", "SAPISID", "LOGIN_INFO", "__Secure-1PSID"],
            "https://myactivity.google.com/product/youtube", "https://myactivity.google.com/activitycontrols/youtube",
            "YouTube builds recommendations from the history in your Google account - delete or pause it there (Data & privacy > History settings)."),
        new("TikTok", ["tiktok.com"], ["sessionid", "sid_tt", "sid_guard", "uid_tt", "sessionid_ss"],
            "https://www.tiktok.com/setting", "https://www.tiktok.com/setting",
            "The For You feed follows your account, not this PC: use Settings > Content preferences > 'Refresh your For You feed' and clear the watch history in the app."),
        new("Instagram", ["instagram.com"], ["sessionid", "ds_user_id"],
            "https://www.instagram.com/your_activity/", "https://www.instagram.com/accounts/privacy_and_security/",
            "Recommendations follow your account: Settings > Content preferences > Reset suggested content starts them over."),
        new("Facebook", ["facebook.com"], ["c_user", "xs"],
            "https://www.facebook.com/allactivity", "https://accountscenter.facebook.com/ads",
            "Feed personalisation follows your account (Accounts Center > Ad preferences, Activity log)."),
        new("X (Twitter)", ["x.com", "twitter.com"], ["auth_token", "twid"],
            "https://x.com/settings/your_twitter_data", "https://x.com/settings/your_twitter_data/twitter_interests",
            "Interests and the For You timeline live in your account - X keeps linking this device to it even after you log out."),
        new("Reddit", ["reddit.com"], ["reddit_session", "token_v2"],
            "https://www.reddit.com/settings/privacy", "https://www.reddit.com/settings/privacy",
            "Feed personalisation follows your account (Settings > Privacy > Personalize recommendations)."),
        new("Twitch", ["twitch.tv"], ["auth-token"],
            "https://www.twitch.tv/settings/privacy", "https://www.twitch.tv/settings/recommendations",
            "Recommendations follow your account."),
        new("Netflix", ["netflix.com"], ["NetflixId", "SecureNetflixId"],
            "https://www.netflix.com/viewingactivity", "https://www.netflix.com/viewingactivity",
            "Viewing history is stored in your account's profile."),
    ];

    /// <summary>
    /// Local Storage key fragments that identify a device across sessions (matched case-insensitively). ByteDance's
    /// AppLog SDK keeps web_id / user_unique_id in <c>__tea_cache_tokens_*</c>; YouTube keeps a remote device id;
    /// Meta keeps a browser id; analytics SDKs keep client ids.
    /// </summary>
    private static readonly string[] IdentifierKeyFragments =
    [
        "__tea_cache_tokens", "__tea_sdk", "tt_webid", "ttwid", "s_v_web_id", "yt-remote-device-id", "yt-remote-connected-devices",
        "device_id", "deviceid", "visitor", "_ga", "ajs_anonymous_id", "amplitude_", "ig_did", "datr", "fingerprint",
        "client_id", "clientid", "uuid", "guest_id",
    ];

    private readonly ICleanerLog _log;

    public PrivacyInsights(ICleanerLog? log = null)
    {
        _log = log ?? NullLog.Instance;
    }

    /// <summary>Collects findings for every detected browser profile plus the Windows advertising ID.</summary>
    public IReadOnlyList<PrivacyInsight> Collect(IReadOnlyList<DetectedBrowser> browsers, Func<BrowserProfile, string, string> ruleIdFor)
        => Collect(browsers, ruleIdFor, []);

    /// <summary>
    /// Collects findings for every detected browser profile, for what the PC remembers having opened (the given
    /// <see cref="RuleAction.ForgetHistory"/> rules) and for the Windows advertising ID.
    /// </summary>
    public IReadOnlyList<PrivacyInsight> Collect(IReadOnlyList<DetectedBrowser> browsers, Func<BrowserProfile, string, string> ruleIdFor, IReadOnlyList<CleanupRule> historyRules)
    {
        ArgumentNullException.ThrowIfNull(browsers);
        ArgumentNullException.ThrowIfNull(ruleIdFor);
        ArgumentNullException.ThrowIfNull(historyRules);

        var insights = new List<PrivacyInsight>();
        foreach (var browser in browsers)
        {
            foreach (var profile in browser.Profiles.Where(p => !p.IsSystemOrGuest))
            {
                var scope = $"{browser.Definition.Name} — {profile.DisplayName}";
                try
                {
                    insights.AddRange(browser.Definition.Family == BrowserFamily.Chromium
                        ? InspectChromiumProfile(profile, scope, ruleIdFor)
                        : InspectGeckoProfile(profile, scope, ruleIdFor));
                }
                catch (Exception ex)
                {
                    _log.Warn($"Privacy insights for {scope} failed: {ex.Message}");
                }
            }
        }

        insights.AddRange(InspectOpenedFiles(historyRules));
        insights.AddRange(InspectWindows());

        // Section order: browsers alphabetically (the order the Browsers page uses), profile by profile, then this
        // PC, then Windows. Inside a section the account findings come first (nothing local can change them), then
        // what PCleaner can clean, then what is already clear - the reading order of the explainer cards above.
        return insights
            .OrderBy(i => i.ScopeKind)
            .ThenBy(i => i.Scope, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Level)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ recently opened files

    /// <summary>One finding per application that still remembers what it opened; one "clear" finding when none does.</summary>
    private IEnumerable<PrivacyInsight> InspectOpenedFiles(IReadOnlyList<CleanupRule> historyRules)
    {
        var anyRule = false;
        var anyMemory = false;
        foreach (var rule in historyRules.Where(r => r.Action == RuleAction.ForgetHistory))
        {
            anyRule = true;
            var count = 0;
            var tabs = 0;
            var unsaved = 0;
            var locked = false;
            foreach (var target in rule.History)
            {
                if (SafetyGuard.ValidateHistoryTarget(target) is not null)
                {
                    continue;
                }

                HistoryInspection inspection;
                try
                {
                    inspection = HistoryPurger.Inspect(target);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Opened-files insight for '{rule.Name}' failed: {ex.Message}");
                    continue;
                }

                locked |= inspection.IsLocked;
                count += inspection.EntryCount;
                if (target.Store is HistoryStore.NotepadTabs or HistoryStore.NotepadPlusPlusSession)
                {
                    tabs += inspection.EntryCount;
                    unsaved += inspection.Entries.Count(e => e.Field.StartsWith("Unsaved", StringComparison.Ordinal));
                }
            }

            if (count == 0 && !locked)
            {
                continue;
            }

            anyMemory = true;
            var app = AppNameOf(rule);
            var files = count - tabs;
            var parts = new List<string>();
            if (tabs > 0)
            {
                parts.Add(unsaved > 0 ? $"{Plural(tabs, "tab")} ({Plural(unsaved, "unsaved tab")})" : Plural(tabs, "tab"));
            }

            if (files > 0)
            {
                parts.Add(Plural(files, "recently opened file"));
            }

            var what = parts.Count == 0 ? "its list" : string.Join(" and ", parts);
            yield return new PrivacyInsight
            {
                Level = InsightLevel.Device,
                Scope = PrivacyInsight.DeviceScope,
                ScopeKind = InsightScopeKind.Device,
                Title = locked && count == 0 ? $"{app} is open" : $"{app} remembers {what}",
                Detail = locked && count == 0
                    ? $"{app} is using its list right now. Close it and click 'Refresh findings'."
                    : tabs > 0
                        ? $"It reopens {(tabs == 1 ? "this tab" : "these tabs")} at start" + (unsaved > 0 ? " - the unsaved text exists nowhere else" : string.Empty) + ". Forgetting closes the app if needed and starts it empty next time."
                        : "Anyone using this PC sees what was opened. Forgetting clears the list; the files themselves are not touched.",
                Evidence = $"{rule.Name}: {TextFormat.Count(count, "entry")} across {TextFormat.Count(rule.History.Count, "store")}.",
                RelatedRuleIds = [rule.Id],
            };
        }

        if (anyRule && !anyMemory)
        {
            yield return new PrivacyInsight
            {
                Level = InsightLevel.Clear,
                Scope = PrivacyInsight.DeviceScope,
                ScopeKind = InsightScopeKind.Device,
                Title = "No remembered files",
                Detail = "Windows and the installed apps keep no lists of recently opened files right now.",
            };
        }
    }

    private static string AppNameOf(CleanupRule rule) => rule.Id switch
    {
        "windows.recent.explorer" => "Explorer",
        "windows.recent.notepad" => "Notepad",
        "windows.recent.accessories" => "Paint, WordPad or Media Player",
        _ => rule.Name,
    };

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    // ------------------------------------------------------------------ Chromium

    private static IEnumerable<PrivacyInsight> InspectChromiumProfile(BrowserProfile profile, string scope, Func<BrowserProfile, string, string> ruleIdFor)
    {
        var p = profile.ProfilePath;
        var cookieDb = Path.Combine(p, "Network", "Cookies");
        if (!File.Exists(cookieDb))
        {
            cookieDb = Path.Combine(p, "Cookies");
        }

        var cookieNames = ReadCookieNames(cookieDb, out var cookiesLocked, gecko: false);
        foreach (var insight in SignedInPlatforms(cookieNames, scope, ruleIdFor(profile, "cookies")))
        {
            yield return insight;
        }

        if (cookiesLocked)
        {
            yield return LockedCookies(scope);
        }

        // Sync: a synced profile restores history, open tabs and settings from the account after a local cleanup.
        var sync = ReadChromiumSyncState(Path.Combine(p, "Preferences"));
        if (sync is not null)
        {
            yield return new PrivacyInsight
            {
                Level = InsightLevel.Account,
                Scope = scope,
                Title = "Browser sync is on",
                Detail = $"{sync} Whatever you clean here comes back from the account on the next sync unless you pause sync or clear it there too.",
                Actions = [new InsightAction("Open sync settings", ChromiumSettingsUrl(profile.Browser, "syncSetup"))],
            };
        }

        // Local Storage identifiers: the part that survives cookie deletion.
        var levelDb = Path.Combine(p, "Local Storage", "leveldb");
        if (!Directory.Exists(levelDb))
        {
            yield break;
        }

        var inventory = ChromiumLocalStorage.Inspect(levelDb);
        if (inventory.Error is not null)
        {
            yield break;
        }

        if (inventory.Websites.Count == 0)
        {
            yield return new PrivacyInsight
            {
                Level = InsightLevel.Clear,
                Scope = scope,
                Title = "No website identifiers",
                Detail = "No website keeps identifiers in this profile.",
            };
            yield break;
        }

        var platformsWithStorage = inventory.Websites
            .Select(o => Platforms.FirstOrDefault(pl => pl.Hosts.Any(h => HostMatches(HostOf(o.Origin), h)))?.Name)
            .Where(n => n is not null)
            .Distinct()
            .ToList();
        var identifierOrigins = CountIdentifierOrigins(levelDb);
        var siteCount = inventory.Websites.Count;
        var known = platformsWithStorage.Count > 0 ? $" ({string.Join(", ", platformsWithStorage)})" : string.Empty;
        var detail = identifierOrigins switch
        {
            0 => $"{siteCount} {(siteCount == 1 ? "website stores" : "websites store")} data in this profile{known}. Stored data can still tell a site it has seen this browser before - the reset items remove it.",
            1 when siteCount == 1 => $"1 website stores data here{known} and keeps a device or visitor ID. Such IDs let a site recognise this browser even without cookies - the reset items remove them.",
            _ => $"{siteCount} websites store data here{known}; {identifierOrigins} of them {(identifierOrigins == 1 ? "keeps" : "keep")} device or visitor IDs. Such IDs let a site recognise this browser even without cookies - the reset items remove them.",
        };

        yield return new PrivacyInsight
        {
            Level = InsightLevel.Local,
            Scope = scope,
            Title = "Websites remember this browser",
            Detail = detail,
            Evidence = $"{inventory.WebsiteKeys:N0} Local Storage entries from {siteCount} website origins; {identifierOrigins} origins with identifier-like keys.",
            RelatedRuleIds = [ruleIdFor(profile, "localstorage"), ruleIdFor(profile, "cookies"), ruleIdFor(profile, "sitedata")],
        };
    }

    private static IEnumerable<PrivacyInsight> SignedInPlatforms(List<(string Host, string Name)> cookieNames, string scope, string cookieRuleId)
    {
        foreach (var platform in Platforms)
        {
            var hits = cookieNames
                .Where(c => platform.Hosts.Any(h => HostMatches(c.Host, h)) && platform.SessionCookies.Contains(c.Name, StringComparer.Ordinal))
                .Select(c => c.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (hits.Count == 0)
            {
                continue;
            }

            yield return new PrivacyInsight
            {
                Level = InsightLevel.Account,
                Scope = scope,
                Title = $"Signed in to {platform.Name}",
                Detail = platform.Explanation + " Clearing cookies only disconnects this PC - the history stays in the account.",
                Evidence = $"Sign-in cookies found: {string.Join(", ", hits.Take(3))} (names only, values are never read).",
                RelatedRuleIds = [cookieRuleId],
                Actions =
                [
                    new InsightAction("Manage history", platform.HistoryUrl),
                    new InsightAction("Personalisation settings", platform.PersonalizationUrl),
                ],
            };
        }
    }

    private static PrivacyInsight LockedCookies(string scope) => new()
    {
        Level = InsightLevel.Local,
        Scope = scope,
        Title = "Sign-in state unknown",
        Detail = "The browser locks its cookies while it runs. Close it and click 'Refresh findings' to see where you are signed in.",
    };

    private static string ChromiumSettingsUrl(BrowserDefinition browser, string page)
    {
        var scheme = browser.Id.StartsWith("edge", StringComparison.OrdinalIgnoreCase) ? "edge"
            : browser.Id.StartsWith("brave", StringComparison.OrdinalIgnoreCase) ? "brave"
            : browser.Id.StartsWith("vivaldi", StringComparison.OrdinalIgnoreCase) ? "vivaldi"
            : "chrome";
        return $"{scheme}://settings/{page}";
    }

    /// <summary>Returns a short description when the profile syncs, otherwise null.</summary>
    internal static string? ReadChromiumSyncState(string preferencesPath)
    {
        if (!File.Exists(preferencesPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(preferencesPath));
            var root = document.RootElement;

            if (TryGetBool(root, "sync", "has_setup_completed") == true || TryGetBool(root, "sync", "requested") == true)
            {
                var email = TryGetString(root, "account_info", "0", "email") ?? TryGetString(root, "google", "services", "username");
                return email is null ? "This profile syncs with an account." : $"This profile syncs with {Mask(email)}.";
            }

            if (root.TryGetProperty("brave_sync_v2", out var braveSync) && braveSync.ValueKind == JsonValueKind.Object
                && braveSync.TryGetProperty("seed", out var seed) && seed.ValueKind == JsonValueKind.String && seed.GetString()?.Length > 0)
            {
                return "Brave Sync is set up for this profile.";
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static string Mask(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "an account" : email[0] + new string('•', Math.Max(2, at - 1)) + email[at..];
    }

    private static JsonElement? Navigate(JsonElement root, string[] path)
    {
        var element = root;
        foreach (var segment in path)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var next))
            {
                element = next;
            }
            else if (element.ValueKind == JsonValueKind.Array && int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < element.GetArrayLength())
            {
                element = element[index];
            }
            else
            {
                return null;
            }
        }

        return element;
    }

    private static bool? TryGetBool(JsonElement root, params string[] path) => Navigate(root, path) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    private static string? TryGetString(JsonElement root, params string[] path)
        => Navigate(root, path) is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

    /// <summary>Number of website origins whose Local Storage holds a key that looks like a device/visitor identifier.</summary>
    private static int CountIdentifierOrigins(string levelDb)
    {
        try
        {
            var origins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in LevelDbReader.ReadAll(levelDb))
            {
                var origin = ChromiumLocalStorage.OriginOf(entry.Key, out var isData);
                if (origin is null || !isData || !ChromiumLocalStorage.IsWebsiteOrigin(origin))
                {
                    continue;
                }

                var name = ChromiumLocalStorage.KeyNameOf(entry.Key);
                if (name is not null && IdentifierKeyFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                {
                    origins.Add(origin);
                }
            }

            return origins.Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ Gecko

    private static IEnumerable<PrivacyInsight> InspectGeckoProfile(BrowserProfile profile, string scope, Func<BrowserProfile, string, string> ruleIdFor)
    {
        var names = ReadCookieNames(Path.Combine(profile.ProfilePath, "cookies.sqlite"), out var locked, gecko: true);
        foreach (var insight in SignedInPlatforms(names, scope, ruleIdFor(profile, "cookies")))
        {
            yield return insight;
        }

        if (locked)
        {
            yield return LockedCookies(scope);
        }

        if (File.Exists(Path.Combine(profile.ProfilePath, "signedInUser.json")))
        {
            yield return new PrivacyInsight
            {
                Level = InsightLevel.Account,
                Scope = scope,
                Title = "Firefox Sync is on",
                Detail = "Whatever you clean here comes back from the Mozilla account on the next sync unless you disconnect or clear it there too.",
                Actions = [new InsightAction("Open sync settings", "about:preferences#sync")],
            };
        }
    }

    // ------------------------------------------------------------------ Windows

    private static IEnumerable<PrivacyInsight> InspectWindows()
    {
        bool? enabled = null;
        var hasId = false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo");
            if (key is not null)
            {
                enabled = key.GetValue("Enabled") is int value ? value != 0 : null;
                hasId = key.GetValue("Id") is string id && id.Length > 0;
            }
        }
        catch (Exception)
        {
            // Registry not readable - report nothing rather than something wrong.
            yield break;
        }

        if (enabled == true || hasId)
        {
            yield return new PrivacyInsight
            {
                Level = InsightLevel.System,
                Scope = PrivacyInsight.WindowsScope,
                ScopeKind = InsightScopeKind.Windows,
                Title = "Windows advertising ID is on",
                Detail = "Store apps use this ID for personalised ads. Turn it off under Settings > Privacy & security > General - a new ID is only created if you turn it back on.",
                Actions = [new InsightAction("Open Windows privacy settings", "ms-settings:privacy-general")],
            };
        }
        else
        {
            yield return new PrivacyInsight
            {
                Level = InsightLevel.Clear,
                Scope = PrivacyInsight.WindowsScope,
                ScopeKind = InsightScopeKind.Windows,
                Title = "Windows advertising ID is off",
                Detail = "Store apps get no Windows-wide ID to recognise you by.",
            };
        }
    }

    // ------------------------------------------------------------------ cookies (names only)

    private static List<(string Host, string Name)> ReadCookieNames(string cookieDb, out bool locked, bool gecko)
    {
        locked = false;
        var list = new List<(string, string)>();
        if (!File.Exists(cookieDb))
        {
            return list;
        }

        // Chromium keeps the cookie database open (exclusive SQLite locking) while it runs, so it is copied with
        // shared access first and the copy is read. Only the host and name columns are ever read.
        var temp = Path.Combine(Path.GetTempPath(), $"pcleaner-cookies-{Guid.NewGuid():N}.db");
        try
        {
            using (var source = new FileStream(cookieDb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(copy);
            }

            SqlitePurger.EnsureEngine();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = gecko ? "SELECT host, name FROM moz_cookies" : "SELECT host_key, name FROM cookies";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    list.Add((reader.GetString(0).TrimStart('.'), reader.GetString(1)));
                }
            }
        }
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
        {
            locked = true;
        }
        catch (Exception)
        {
            // Unreadable database: report nothing.
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }

        return list;
    }

    private static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static string HostOf(string origin)
    {
        var start = origin.IndexOf("://", StringComparison.Ordinal);
        var host = start >= 0 ? origin[(start + 3)..] : origin;
        var end = host.IndexOfAny([':', '/', '^']);
        return end >= 0 ? host[..end] : host;
    }
}