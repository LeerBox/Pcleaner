using PCleaner.Core.Model;

namespace PCleaner.Core.Rules;

/// <summary>
/// The one place that decides in which order categories, groups and items appear. Rule providers may yield rules
/// in whatever order is convenient for the code; the UI sorts with these keys so that every page reads the same
/// way: what is cleaned first (caches), then what changes behaviour, then what is a privacy choice.
/// </summary>
public static class RuleOrder
{
    /// <summary>Group names shared by the Windows and application rule providers.</summary>
    public const string TempAndCaches = "Temporary files & caches";
    public const string RecycleBin = "Deleted files";
    public const string SystemFiles = "System files";
    public const string DiskCleanup = "Disk Cleanup (Microsoft)";
    public const string RecentFiles = "Recently opened files";
    public const string Privacy = "Privacy";
    public const string Advanced = "Advanced";
    public const string GraphicsDrivers = "Graphics drivers";
    public const string MicrosoftApps = "Microsoft apps";

    /// <summary>
    /// Fixed group order per category: the rebuildable caches first, then Windows' own handlers, then the lists
    /// and traces that are a privacy choice, and the long-running or hard-to-undo maintenance last. Browser groups
    /// are the browsers themselves and sort by name.
    /// </summary>
    private static readonly string[] GroupSequence =
    [
        TempAndCaches, SystemFiles, DiskCleanup, GraphicsDrivers, MicrosoftApps, RecentFiles, Privacy, RecycleBin, Advanced,
    ];

    /// <summary>
    /// Fixed item order inside a browser profile, mirroring Chromium's own "Clear browsing data" dialog: caches,
    /// then history, cookies and the autofill data, then the site storage that changes what web apps remember.
    /// </summary>
    private static readonly string[] BrowserRuleSequence =
    [
        ".root", ".cache", ".transient", ".telemetry", ".history", ".cookies", ".formhistory", ".addresses", ".localstorage", ".session", ".sitedata",
    ];

    public static int CategoryRank(RuleCategory category) => category switch
    {
        RuleCategory.WindowsUser => 0,
        RuleCategory.WindowsSystem => 1,
        RuleCategory.Applications => 2,
        RuleCategory.Browsers => 3,
        _ => 4,
    };

    /// <summary>Position of a group inside its category; unknown groups (browser names) sort after the known ones, alphabetically.</summary>
    public static int GroupRank(string group)
    {
        var index = Array.FindIndex(GroupSequence, g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? GroupSequence.Length : index;
    }

    /// <summary>
    /// Step of a browser rule inside its profile (caches first, site storage last); non-browser rules return 0.
    /// </summary>
    public static int BrowserStep(CleanupRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Category != RuleCategory.Browsers)
        {
            return 0;
        }

        var step = Array.FindIndex(BrowserRuleSequence, s => rule.Id.EndsWith(s, StringComparison.Ordinal));
        return step < 0 ? BrowserRuleSequence.Length : step;
    }

    /// <summary>
    /// Sorts rules for display. Categories and groups follow the fixed sequences above. Inside a group, browser
    /// rules keep their profiles together in the order the browser lists them and follow
    /// <see cref="BrowserRuleSequence"/> within a profile; all other rules sort by their explicit
    /// <see cref="CleanupRule.Order"/> hint first (used to lead a group with its most general items), then by risk
    /// (what is selected by default comes first) and then by name, so a group reads "safe caches, then side
    /// effects, then privacy choices". The sort is stable: rules that tie keep the providers' order.
    /// </summary>
    public static IReadOnlyList<CleanupRule> Sort(IEnumerable<CleanupRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var list = rules.ToList();
        var profileOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in list.Where(r => r.Category == RuleCategory.Browsers))
        {
            profileOrder.TryAdd(ProfileKey(rule), profileOrder.Count);
        }

        return list
            .OrderBy(r => CategoryRank(r.Category))
            .ThenBy(r => GroupRank(r.Group))
            .ThenBy(r => r.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Category == RuleCategory.Browsers ? (r.Id.EndsWith(".root", StringComparison.Ordinal) ? -1 : profileOrder[ProfileKey(r)]) : 0)
            .ThenBy(BrowserStep)
            .ThenBy(r => r.Order ?? int.MaxValue)
            .ThenBy(r => r.Category == RuleCategory.Browsers || r.Order is not null ? 0 : (int)r.Risk)
            .ThenBy(r => r.Category == RuleCategory.Browsers ? string.Empty : r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>"browser.brave.default" for "browser.brave.default.cookies" - every rule of one profile shares it.</summary>
    internal static string ProfileKey(CleanupRule rule)
    {
        var lastDot = rule.Id.LastIndexOf('.');
        return lastDot > 0 ? rule.Id[..lastDot] : rule.Id;
    }
}