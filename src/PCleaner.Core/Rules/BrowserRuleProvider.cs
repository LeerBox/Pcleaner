using System.Text.Json;
using PCleaner.Core.Browsers;
using PCleaner.Core.Model;

namespace PCleaner.Core.Rules;

/// <summary>
/// Builds cleanup rules for every detected browser profile. The design is an ALLOW-list: only explicitly named
/// cache / junk items are ever targeted, everything else in a profile is left alone. Extension code, extension
/// settings and extension storage are additionally protected by <see cref="Engine.SafetyGuard"/>.
/// </summary>
public sealed class BrowserRuleProvider
{
    private const string ChromiumRef = "Chromium source: chrome/common/chrome_constants.cc, content/browser/storage_partition_impl.cc, gpu/ipc/common/gpu_disk_cache_type.cc; chrome://settings/clearBrowserData 'Cached images and files'.";
    private const string ChromiumFormHistoryRef = "Chromium source: components/autofill/core/browser/webdata/autocomplete/autocomplete_table.cc (table 'autofill'), autocomplete_table_label_sensitive.cc (table 'autocomplete'), autocomplete_sync_bridge.cc (EXPIRE path drops the row and its sync metadata), addresses/address_autofill_table.cc (record_type 0 = local); chrome://settings/clearBrowserData 'Autofill form data'.";
    private const string ChromiumLocalStorageRef = "Chromium source: components/services/storage/dom_storage/local_storage_impl.cc (keys VERSION, META:, METAACCESS:, _<origin>\\0<key>; one LevelDB per profile), third_party/blink/renderer/modules/storage/cached_storage_area.cc (key/value format bytes); chrome://settings/clearBrowserData 'Cookies and other site data' clears the same origins.";
    private const string GeckoRef = "Mozilla: Profiles service docs (local directory 'used for caches that can safely be deleted'), netwerk/cache2, toolkit/profile/nsToolkitProfileService.cpp.";
    private const string GeckoFormHistoryRef = "Mozilla source: toolkit/components/satchel/FormHistory.sys.mjs (schema v5, 'remove' path: DELETE FROM moz_formhistory, prune unused moz_sources), browser/modules/Sanitizer.sys.mjs ('Form & search history' calls FormHistory.update({op: 'remove'})).";

    private readonly IReadOnlyList<DetectedBrowser> _browsers;
    private readonly bool _includeSystemAndGuestProfiles;

    public BrowserRuleProvider(IReadOnlyList<DetectedBrowser> browsers, bool includeSystemAndGuestProfiles = true)
    {
        _browsers = browsers ?? throw new ArgumentNullException(nameof(browsers));
        _includeSystemAndGuestProfiles = includeSystemAndGuestProfiles;
    }

    public IReadOnlyList<CleanupRule> GetRules()
    {
        var rules = new List<CleanupRule>();
        foreach (var browser in _browsers)
        {
            foreach (var root in browser.ExistingDataRoots)
            {
                rules.AddRange(browser.Definition.Family == BrowserFamily.Chromium
                    ? ChromiumRootRules(browser, root)
                    : GeckoRootRules(browser, root));
            }

            foreach (var profile in browser.Profiles)
            {
                if (profile.IsSystemOrGuest && !_includeSystemAndGuestProfiles)
                {
                    continue;
                }

                rules.AddRange(browser.Definition.Family == BrowserFamily.Chromium
                    ? ChromiumProfileRules(browser, profile)
                    : GeckoProfileRules(browser, profile));
            }
        }

        return rules;
    }

    // ================================================================== Chromium

    /// <summary>Per-profile directories that hold nothing but regenerable caches.</summary>
    private static readonly string[] ChromiumCacheDirs =
    [
        "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Application Cache",
        "blob_storage", "Shared Dictionary", "Media Cache",
    ];

    /// <summary>Extension storage partitions (Storage\ext\&lt;id&gt;\def\...) may contain their own cache folders.</summary>
    private static readonly string[] ChromiumPartitionCacheDirs = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    /// <summary>Per-profile databases/folders that are rebuilt automatically and hold no user data.</summary>
    private static readonly string[] ChromiumTransientDirs =
    [
        "Site Characteristics Database", "VideoDecodeStats", "WebrtcVideoStats", "BudgetDatabase", "Download Service",
        "optimization_guide_hint_cache_store", "optimization_guide_model_and_features_store",
        "optimization_guide_model_metadata_store", "OptimizationGuidePredictionModels", "PersistentOriginTrials",
        "Safe Browsing Network", "Platform Notifications", "Feature Engagement Tracker", "Segmentation Platform",
    ];

    private static readonly string[] ChromiumTransientFiles =
    [
        "Visited Links", "Top Sites", "Top Sites-journal", "heavy_ad_intervention_opt_out.db", "heavy_ad_intervention_opt_out.db-journal",
        "previews_opt_out.db", "previews_opt_out.db-journal", "Safe Browsing Cookies", "Safe Browsing Cookies-journal",
        "chrome_shutdown_ms.txt", @"Network\Network Persistent State", @"Network\Reporting and NEL",
        @"Network\Reporting and NEL-journal", @"Network\SCT Auditing Pending Reports",
    ];

    private static readonly string[] ChromiumHistoryFiles =
    [
        "History", "History-journal", "History Provider Cache", "Archived History", "Archived History-journal",
        "Shortcuts", "Shortcuts-journal", "Network Action Predictor", "Network Action Predictor-journal",
        "Favicons", "Favicons-journal", "DownloadMetadata", "Media History",
        "Media History-journal",
    ];

    private static readonly string[] ChromiumHistoryPatterns = ["HistoryEmbeddings*"];

    private static readonly string[] ChromiumCookieFiles =
    [
        "Cookies", "Cookies-journal", @"Network\Cookies", @"Network\Cookies-journal", @"Network\Device Bound Sessions",
        @"Network\Device Bound Sessions-journal", @"Network\TransportSecurity", @"Network\Trust Tokens",
        @"Network\Trust Tokens-journal", "MediaDeviceSalts", "MediaDeviceSalts-journal", "DIPS", "DIPS-journal", "DIPS-wal",
    ];

    private static readonly string[] ChromiumCookiePatterns =
    [
        "SharedStorage*", "InterestGroups*", "Conversions*", "PrivateAggregation*", "BrowsingTopics*",
        "AggregationService*", "KAnonymityService*",
    ];

    private static readonly string[] ChromiumSessionDirs = ["Sessions", "Sessions_Encrypted", "Session Storage"];
    private static readonly string[] ChromiumSessionFiles = ["Current Session", "Current Tabs", "Last Session", "Last Tabs"];

    /// <summary>Site data folders. Extension origins inside them are excluded by name (chrome-extension_*).</summary>
    private static readonly string[] ChromiumSiteDataDirs = ["IndexedDB", "File System", "WebStorage", "databases", "BackgroundFetch", "Service Worker"];
    private static readonly string[] ChromiumSiteDataFiles = ["QuotaManager", "QuotaManager-journal"];

    private static readonly string[] ChromiumRootCacheDirs =
    [
        "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "GPUPersistentCache", "component_crx_cache", @"Crashpad\reports", @"Crashpad\pending",
        @"Crashpad\completed", @"Crashpad\new", @"Crashpad\attachments", "Crash Reports", "BrowserMetrics", "Stability", "Local Traces",
    ];

    private static readonly string[] ChromiumRootCacheFiles =
    [
        "BrowserMetrics-spare.pma", "CrashpadMetrics-active.pma", "CrashpadMetrics.pma", "chrome_debug.log", "debug.log", "Module Info Cache",
        @"Crashpad\metadata",
    ];

    private static IEnumerable<CleanupRule> ChromiumRootRules(DetectedBrowser browser, string root)
    {
        var def = browser.Definition;
        var group = def.Name;
        var targets = new List<PathTarget>();
        targets.AddRange(ChromiumRootCacheDirs.Select(d => WindowsRuleProvider.Dir(Path.Combine(root, d))));
        targets.AddRange(ChromiumRootCacheFiles.Select(f => WindowsRuleProvider.File(Path.Combine(root, f))));
        targets.Add(WindowsRuleProvider.Dir(root, include: ["*.tmp"], recursive: false, deleteEmptyDirs: false));

        yield return new CleanupRule
        {
            Id = $"browser.{def.Id}.{Slug(root)}.root",
            Name = $"{def.Name}: Shader cache & crash reports",
            Description = "Shader caches, crash dumps and usage metrics the browser itself keeps outside the profiles. All of it regenerates automatically.",
            Category = RuleCategory.Browsers,
            Group = group,
            Reference = ChromiumRef,
            ConflictingProcesses = def.ProcessNames,
            LockProbeFiles = [Path.Combine(root, "lockfile")],
            Targets = targets,
        };
    }

    private static IEnumerable<CleanupRule> ChromiumProfileRules(DetectedBrowser browser, BrowserProfile profile)
    {
        var def = browser.Definition;
        var p = profile.ProfilePath;
        var cacheRoot = ResolveChromiumCacheRoot(profile);
        var group = def.Name;
        var prefix = $"browser.{def.Id}.{Slug(profile.DataRoot)}.{Slug(profile.DirectoryName)}";
        var profileLabel = $"{def.Name} — {profile.DisplayName}";
        var conflicts = def.ProcessNames;
        var locks = new[] { Path.Combine(profile.DataRoot, "lockfile") };

        // ---- Cache (default on)
        var cache = new List<PathTarget>
        {
            WindowsRuleProvider.Dir(Path.Combine(cacheRoot, "Cache")),
        };
        var separateCacheRoot = !string.Equals(cacheRoot, p, StringComparison.OrdinalIgnoreCase);
        if (separateCacheRoot)
        {
            cache.Add(WindowsRuleProvider.Dir(Path.Combine(p, "Cache")));
        }

        cache.AddRange(ChromiumCacheDirs.Select(d => WindowsRuleProvider.Dir(Path.Combine(p, d))));
        if (separateCacheRoot)
        {
            // Opera-style layouts keep every cache-type folder in the Local AppData twin of the profile.
            cache.AddRange(ChromiumCacheDirs.Select(d => WindowsRuleProvider.Dir(Path.Combine(cacheRoot, d))));
        }
        foreach (var partitionCache in ChromiumPartitionCacheDirs)
        {
            foreach (var dir in PathExpander.ExpandDirectories(Path.Combine(p, "Storage", "ext", "*", "def", partitionCache)))
            {
                cache.Add(WindowsRuleProvider.Dir(dir));
            }
        }

        yield return new CleanupRule
        {
            Id = prefix + ".cache",
            Name = $"{profileLabel}: Cache",
            Description = "Page content, scripts, images and compiled shaders kept for speed. Re-downloaded as needed; bookmarks, passwords, extensions and settings stay.",
            Category = RuleCategory.Browsers,
            Group = group,
            Reference = ChromiumRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = cache,
        };

        // ---- Transient databases (default on)
        var transient = new List<PathTarget>();
        transient.AddRange(ChromiumTransientDirs.Select(d => WindowsRuleProvider.Dir(Path.Combine(p, d))));
        transient.AddRange(ChromiumTransientFiles.Select(f => WindowsRuleProvider.File(Path.Combine(p, f))));
        foreach (var dir in PathExpander.ExpandDirectories(Path.Combine(p, "JumpListIcons*")))
        {
            transient.Add(WindowsRuleProvider.Dir(dir));
        }

        yield return new CleanupRule
        {
            Id = prefix + ".transient",
            Name = $"{profileLabel}: Temporary databases",
            Description = "Small databases the browser rebuilds on its own: site performance stats, media statistics and network reports.",
            Category = RuleCategory.Browsers,
            Group = group,
            Reference = ChromiumRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = transient,
        };

        // ---- History (privacy, default off)
        var history = new List<PathTarget>();
        history.AddRange(ChromiumHistoryFiles.Select(f => WindowsRuleProvider.File(Path.Combine(p, f))));
        history.Add(WindowsRuleProvider.Dir(p, include: ChromiumHistoryPatterns, recursive: false, deleteEmptyDirs: false));
        yield return new CleanupRule
        {
            Id = prefix + ".history",
            Name = $"{profileLabel}: Browsing & download history",
            Description = "Visited pages, address-bar suggestions, most-visited tiles and download records. Bookmarks stay.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Privacy,
            Reference = ChromiumRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = history,
        };

        // ---- Cookies & tracking state (privacy, default off)
        var cookies = new List<PathTarget>();
        cookies.AddRange(ChromiumCookieFiles.Select(f => WindowsRuleProvider.File(Path.Combine(p, f))));
        cookies.Add(WindowsRuleProvider.Dir(p, include: ChromiumCookiePatterns, recursive: false, deleteEmptyDirs: false));
        yield return new CleanupRule
        {
            Id = prefix + ".cookies",
            Name = $"{profileLabel}: Cookies & tracking data",
            Description = "Cookies and the tracking state sites keep alongside them. You will be signed out of websites; extension cookies stay.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Privacy,
            Reference = ChromiumRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = cookies,
        };

        // ---- Form autofill history (privacy, default off). Row-level: "Web Data" also holds payment methods,
        //      search engines and sign-in tokens, so the file itself is protected and only these tables are emptied.
        var webData = Path.Combine(p, "Web Data");
        yield return new CleanupRule
        {
            Id = prefix + ".formhistory",
            Name = $"{profileLabel}: Form autofill history",
            Description = "E-mails, names, phone numbers and search terms the browser remembered from web forms and suggests again. Passwords, payment methods and saved addresses stay.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Privacy,
            Action = RuleAction.PurgeDatabaseRows,
            Reference = ChromiumFormHistoryRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Databases = [new DatabaseTarget { Path = webData, Purge = DatabasePurge.ChromiumFormHistory }],
        };

        // ---- Saved addresses (privacy, default off)
        yield return new CleanupRule
        {
            Id = prefix + ".addresses",
            Name = $"{profileLabel}: Saved addresses",
            Description = "Addresses saved on this device in Autofill settings. Addresses in your Google or Microsoft account, payment methods and passwords stay.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Privacy,
            Action = RuleAction.PurgeDatabaseRows,
            Reference = ChromiumFormHistoryRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Databases = [new DatabaseTarget { Path = webData, Purge = DatabasePurge.ChromiumLocalAddresses }],
        };

        // ---- Website identifiers in Local Storage (privacy, default off). The LevelDB is shared with extensions and
        //      browser pages, so only http(s) origins are removed and the database is rewritten (backup kept 7 days).
        yield return new CleanupRule
        {
            Id = prefix + ".localstorage",
            Name = $"{profileLabel}: Website identifiers",
            Description = "Device and visitor IDs websites keep in Local Storage to recognise you after their cookies are gone. Extension data stays and a backup is kept for 7 days.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Privacy,
            Action = RuleAction.PurgeDatabaseRows,
            Reference = ChromiumLocalStorageRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Databases = [new DatabaseTarget { Path = Path.Combine(p, "Local Storage", "leveldb"), Purge = DatabasePurge.ChromiumSiteLocalStorage }],
        };

        // ---- Session (moderate, default off). Vivaldi stores user-saved sessions here: never offered.
        if (!def.Id.StartsWith("vivaldi", StringComparison.OrdinalIgnoreCase))
        {
            var session = new List<PathTarget>();
            session.AddRange(ChromiumSessionDirs.Select(d => WindowsRuleProvider.Dir(Path.Combine(p, d))));
            session.AddRange(ChromiumSessionFiles.Select(f => WindowsRuleProvider.File(Path.Combine(p, f))));
            yield return new CleanupRule
            {
                Id = prefix + ".session",
                Name = $"{profileLabel}: Saved session & tabs",
                Description = "Open tabs and windows restored by 'Continue where you left off'. The browser starts with a clean window next time.",
                Category = RuleCategory.Browsers,
                Group = group,
                Risk = RiskLevel.Moderate,
                Reference = ChromiumRef,
                ConflictingProcesses = conflicts,
                LockProbeFiles = locks,
                Targets = session,
            };
        }

        // ---- Site data (moderate, default off). The Service Worker registration database and script cache are
        //      shared with MV3 extensions, so only the per-origin cache storage is removed there.
        var siteData = ChromiumSiteDataDirs
            .Select(d => WindowsRuleProvider.Dir(Path.Combine(p, d), exclude: d == "Service Worker"
                ? ["chrome-extension_*", "chrome-extension://*", "Database", "ScriptCache"]
                : ["chrome-extension_*", "chrome-extension://*"]))
            .ToList();
        yield return new CleanupRule
        {
            Id = prefix + ".sitedata",
            Name = $"{profileLabel}: Website storage",
            Description = "Offline data web apps store on this PC. Rebuilt on the next visit and you may be signed out; extension storage stays.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Moderate,
            Reference = ChromiumRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = siteData,
        };
    }

    /// <summary>
    /// Chromium honours <c>browser.disk_cache_dir</c> (Local State) / the DiskCacheDir policy by placing the HTTP
    /// cache at &lt;dir&gt;\&lt;ProfileDir&gt;\Cache. Opera style browsers already carry a separate cache root.
    /// </summary>
    internal static string ResolveChromiumCacheRoot(BrowserProfile profile)
    {
        var redirected = ReadDiskCacheDir(Path.Combine(profile.DataRoot, "Local State"));
        if (!string.IsNullOrWhiteSpace(redirected) && Path.IsPathFullyQualified(redirected))
        {
            var candidate = profile.DirectoryName == "(root)" ? redirected : Path.Combine(redirected, profile.DirectoryName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return profile.CachePath;
    }

    private static string? ReadDiskCacheDir(string localStatePath)
    {
        try
        {
            if (!File.Exists(localStatePath))
            {
                return null;
            }

            using var stream = new FileStream(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("browser", out var browser) && browser.ValueKind == JsonValueKind.Object
                && browser.TryGetProperty("disk_cache_dir", out var dir) && dir.ValueKind == JsonValueKind.String)
            {
                return dir.GetString();
            }
        }
        catch (Exception)
        {
            // Fall back to the default location.
        }

        return null;
    }

    // ================================================================== Gecko (Firefox family)

    /// <summary>Cache folders of the LOCAL profile directory (also checked in the root for custom-location profiles).</summary>
    private static readonly string[] GeckoLocalCacheDirs = ["cache2", "startupCache", "OfflineCache", "thumbnails", "jumpListCache"];

    /// <summary>Cache folders that live in the ROOT (roaming) profile directory.</summary>
    private static readonly string[] GeckoRootCacheDirs = ["shader-cache", "mediacapabilities", "chrome_debugger_profile"];

    private static readonly string[] GeckoTelemetryDirs = ["crashes", "minidumps", "datareporting", "saved-telemetry-pings", "sessionstore-logs", @"weave\logs"];
    private static readonly string[] GeckoTelemetryFiles = ["Telemetry.ShutdownTime.txt", "Telemetry.FailedProfileLocks.txt"];

    private static readonly string[] GeckoCookieFiles =
    [
        "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm", "SiteSecurityServiceState.txt", "SiteSecurityServiceState.bin",
        "AlternateServices.txt", "AlternateServices.bin", "bounce-tracking-protection.sqlite", "bounce-tracking-protection.sqlite-wal",
        "bounce-tracking-protection.sqlite-shm", "enumerate_devices.txt", "protections.sqlite", "protections.sqlite-wal", "protections.sqlite-shm",
    ];

    private static readonly string[] GeckoSessionFiles = ["sessionstore.jsonlz4", "sessionstore.js", "sessionstore.bak"];

    private static readonly string[] GeckoSiteDataFiles =
    [
        "webappsstore.sqlite", "webappsstore.sqlite-wal", "webappsstore.sqlite-shm", @"storage\ls-archive.sqlite",
        @"storage\ls-archive.sqlite-wal", @"storage\ls-archive.sqlite-shm", "serviceworker.txt",
    ];

    private static IEnumerable<CleanupRule> GeckoRootRules(DetectedBrowser browser, string root)
    {
        var def = browser.Definition;
        yield return new CleanupRule
        {
            Id = $"browser.{def.Id}.{Slug(root)}.root",
            Name = $"{def.Name}: Crash reports & telemetry",
            Description = "Crash dumps and telemetry pings waiting to be sent, shared by all profiles. Nothing else is touched.",
            Category = RuleCategory.Browsers,
            Group = def.Name,
            Reference = GeckoRef,
            ConflictingProcesses = def.ProcessNames,
            Targets =
            [
                WindowsRuleProvider.Dir(Path.Combine(root, "Crash Reports")),
                WindowsRuleProvider.Dir(Path.Combine(root, "Pending Pings")),
            ],
        };
    }

    private static IEnumerable<CleanupRule> GeckoProfileRules(DetectedBrowser browser, BrowserProfile profile)
    {
        var def = browser.Definition;
        var root = profile.ProfilePath;
        var local = profile.CachePath;
        var group = def.Name;
        var prefix = $"browser.{def.Id}.{Slug(profile.DataRoot)}.{Slug(profile.DirectoryName)}";
        var profileLabel = $"{def.Name} — {profile.DisplayName}";
        var conflicts = def.ProcessNames;
        var locks = new[] { Path.Combine(root, "parent.lock"), Path.Combine(root, ".parentlock") };

        // ---- Cache (default on)
        var cache = new List<PathTarget>();
        foreach (var d in GeckoLocalCacheDirs)
        {
            cache.Add(WindowsRuleProvider.Dir(Path.Combine(local, d)));
            if (!string.Equals(local, root, StringComparison.OrdinalIgnoreCase))
            {
                cache.Add(WindowsRuleProvider.Dir(Path.Combine(root, d)));
            }
        }

        foreach (var purge in PathExpander.ExpandDirectories(Path.Combine(local, "cache2.*.purge.bg_rm")))
        {
            cache.Add(WindowsRuleProvider.Dir(purge));
        }

        cache.AddRange(GeckoRootCacheDirs.Select(d => WindowsRuleProvider.Dir(Path.Combine(root, d))));

        yield return new CleanupRule
        {
            Id = prefix + ".cache",
            Name = $"{profileLabel}: Cache",
            Description = "Page content, startup and shader caches kept for speed. Firefox rebuilds them; bookmarks, history, passwords, add-ons and settings stay.",
            Category = RuleCategory.Browsers,
            Group = group,
            Reference = GeckoRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = cache,
        };

        // ---- Crash & telemetry (default on)
        var telemetry = new List<PathTarget>();
        telemetry.AddRange(GeckoTelemetryDirs.Select(d => WindowsRuleProvider.Dir(Path.Combine(root, d))));
        telemetry.AddRange(GeckoTelemetryFiles.Select(f => WindowsRuleProvider.File(Path.Combine(root, f))));
        yield return new CleanupRule
        {
            Id = prefix + ".telemetry",
            Name = $"{profileLabel}: Profile crash reports",
            Description = "Crash records, archived telemetry and sync logs of this profile. Diagnostics only.",
            Category = RuleCategory.Browsers,
            Group = group,
            Reference = GeckoRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = telemetry,
        };

        // ---- Form history (privacy, default off). Row-level, exactly like Firefox's own "Form & search history":
        //      the database keeps its schema and version, and the WAL/journal files are handled by SQLite itself.
        yield return new CleanupRule
        {
            Id = prefix + ".formhistory",
            Name = $"{profileLabel}: Form & search history",
            Description = "E-mails, names, phone numbers and search terms Firefox remembered from web forms and the search bar. Browsing history, bookmarks, logins and saved addresses stay.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Privacy,
            Action = RuleAction.PurgeDatabaseRows,
            Reference = GeckoFormHistoryRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Databases = [new DatabaseTarget { Path = Path.Combine(root, "formhistory.sqlite"), Purge = DatabasePurge.GeckoFormHistory }],
        };

        // ---- Cookies (privacy, default off)
        yield return new CleanupRule
        {
            Id = prefix + ".cookies",
            Name = $"{profileLabel}: Cookies & tracking data",
            Description = "Cookies and the tracking state sites keep alongside them. You will be signed out of websites.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Privacy,
            Reference = GeckoRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = GeckoCookieFiles.Select(f => WindowsRuleProvider.File(Path.Combine(root, f))).ToList(),
        };

        // ---- Session (moderate, default off)
        var session = new List<PathTarget> { WindowsRuleProvider.Dir(Path.Combine(root, "sessionstore-backups")) };
        session.AddRange(GeckoSessionFiles.Select(f => WindowsRuleProvider.File(Path.Combine(root, f))));
        yield return new CleanupRule
        {
            Id = prefix + ".session",
            Name = $"{profileLabel}: Saved session & tabs",
            Description = "Open tabs and windows restored at startup. Firefox starts with a fresh window next time.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Moderate,
            Reference = GeckoRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = session,
        };

        // ---- Site data (moderate, default off). Only http/https origins - never moz-extension.
        var siteData = new List<PathTarget>();
        foreach (var origin in PathExpander.ExpandDirectories(Path.Combine(root, "storage", "default", "http*")))
        {
            siteData.Add(WindowsRuleProvider.Dir(origin));
        }

        siteData.Add(WindowsRuleProvider.Dir(Path.Combine(root, "storage", "temporary"), exclude: ["moz-extension*", "chrome"]));
        siteData.AddRange(GeckoSiteDataFiles.Select(f => WindowsRuleProvider.File(Path.Combine(root, f))));
        yield return new CleanupRule
        {
            Id = prefix + ".sitedata",
            Name = $"{profileLabel}: Website storage",
            Description = "Offline data websites store on this PC. Rebuilt on the next visit and you may be signed out; add-on storage stays.",
            Category = RuleCategory.Browsers,
            Group = group,
            Risk = RiskLevel.Moderate,
            Reference = GeckoRef,
            ConflictingProcesses = conflicts,
            LockProbeFiles = locks,
            Targets = siteData,
        };
    }

    private static string Slug(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Length > 60 ? slug[^60..] : slug;
    }
}