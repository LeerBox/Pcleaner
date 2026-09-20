using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PCleaner.Core.Engine;
using PCleaner.Core.Model;
using PCleaner.Core.Windows;

namespace PCleaner.App.ViewModels;

/// <summary>One selectable cleanup rule in the tree.</summary>
public sealed partial class RuleItemViewModel : ObservableObject
{
    private readonly Action<RuleItemViewModel> _selectionChanged;

    public RuleItemViewModel(CleanupRule rule, bool isSelected, Action<RuleItemViewModel> selectionChanged)
    {
        Rule = rule;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public CleanupRule Rule { get; }

    public string Name => Rule.Name;

    /// <summary>
    /// Name shown inside a group card. Browser rules are named "Browser — Profile: Item"; the group header already
    /// says which browser it is, so the row shows "Profile · Item" instead. When the browser has a single profile the
    /// profile prefix carries no information either and is dropped as well (see <see cref="HideProfilePrefix"/>).
    /// </summary>
    public string ShortName
    {
        get
        {
            if (Rule.Category != RuleCategory.Browsers)
            {
                return Rule.Name;
            }

            var (profile, item, browserWide) = SplitBrowserName();
            if (HideProfilePrefix)
            {
                return item;
            }

            return browserWide ? $"All profiles · {item}" : profile is null ? item : $"{profile} · {item}";
        }
    }

    /// <summary>Profile label of a per-profile browser rule ("Profile 1", "Default"), null for browser-wide rules.</summary>
    public string? ProfileLabel => Rule.Category == RuleCategory.Browsers ? SplitBrowserName().Profile : null;

    /// <summary>Set by the owning group when every rule belongs to the same profile, so the prefix is redundant.</summary>
    public bool HideProfilePrefix { get; private set; }

    internal void SetHideProfilePrefix(bool value)
    {
        if (HideProfilePrefix != value)
        {
            HideProfilePrefix = value;
            OnPropertyChanged(nameof(HideProfilePrefix));
            OnPropertyChanged(nameof(ShortName));
        }
    }

    private (string? Profile, string Item, bool BrowserWide) SplitBrowserName()
    {
        var name = Rule.Name;
        var browserPrefix = Rule.Group + " — ";
        if (name.StartsWith(browserPrefix, StringComparison.Ordinal))
        {
            // "Browser — Profile: Item" (item labels never contain ": ", profile names might)
            var rest = name[browserPrefix.Length..];
            var separator = rest.LastIndexOf(": ", StringComparison.Ordinal);
            return separator > 0
                ? (rest[..separator], rest[(separator + 2)..], false)
                : (null, rest, false);
        }

        if (name.StartsWith(Rule.Group + ": ", StringComparison.Ordinal))
        {
            // "Browser: Item" is a browser-wide rule (shader caches, crash reports...)
            return (null, name[(Rule.Group.Length + 2)..], true);
        }

        return (null, name, false);
    }

    /// <summary>Accessible name of the row's checkbox (screen readers / UI automation).</summary>
    public string SelectionAutomationName => $"Select {Rule.Group} · {ShortName}";

    public string Description => Rule.Description;

    public string? Reference => Rule.Reference;

    public RiskLevel Risk => Rule.Risk;

    public string RiskLabel => Rule.Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Side effects",
        _ => "Privacy",
    };

    public bool RequiresAdministrator => Rule.RequiresAdministrator;

    public bool IsSpecialAction => Rule.Action != RuleAction.DeleteFiles;

    /// <summary>True for rules that remove entries from a store (database rows, remembered files) instead of deleting files.</summary>
    public bool IsDatabasePurge => Rule.Action is RuleAction.PurgeDatabaseRows or RuleAction.ForgetHistory;

    /// <summary>True for rules that forget what an application remembers having opened.</summary>
    public bool IsHistoryPurge => Rule.Action == RuleAction.ForgetHistory;

    /// <summary>True when the database purge removes whole website origins rather than form entries.</summary>
    public bool IsSiteStoragePurge => Rule.Action == RuleAction.PurgeDatabaseRows && Rule.Databases.Any(d => d.Purge == DatabasePurge.ChromiumSiteLocalStorage);

    /// <summary>What one removed database entry is called for this rule.</summary>
    private (string Singular, string Plural) EntryUnit => IsSiteStoragePurge ? ("website", "websites") : ("entry", "entries");

    public string ActionLabel => Rule.Action switch
    {
        RuleAction.DeleteFiles => "Deletes files",
        RuleAction.EmptyRecycleBin => "Empties the bin",
        RuleAction.FlushDnsCache => "Flushes the cache",
        RuleAction.ClearEventLogs => "Clears logs",
        RuleAction.DiskCleanupHandler => "Windows handler",
        RuleAction.ComponentStoreCleanup => "Runs DISM",
        RuleAction.PurgeDatabaseRows => "Edits the database",
        RuleAction.ForgetHistory => "Clears the list",
        _ => string.Empty,
    };

    /// <summary>Short glyph for the rule's category/action, used as the row icon (Segoe Fluent Icons).</summary>
    public string Glyph => Rule.Action switch
    {
        RuleAction.EmptyRecycleBin => "\uE74D",
        RuleAction.FlushDnsCache => "\uE968",
        RuleAction.ClearEventLogs => "\uE9D5",
        RuleAction.DiskCleanupHandler => "\uE7F8",
        RuleAction.ComponentStoreCleanup => "\uE90F",
        RuleAction.PurgeDatabaseRows when Rule.Id.EndsWith(".addresses", StringComparison.Ordinal) => "\uE779",
        RuleAction.PurgeDatabaseRows when Rule.Id.EndsWith(".localstorage", StringComparison.Ordinal) => "\uE928",
        RuleAction.PurgeDatabaseRows => "\uE8AC",
        RuleAction.ForgetHistory when Rule.Id.EndsWith(".notepad", StringComparison.Ordinal) || Rule.Id.EndsWith(".notepadpp", StringComparison.Ordinal) => "\uE70B",
        RuleAction.ForgetHistory when Rule.Id.EndsWith(".vlc", StringComparison.Ordinal) || Rule.Id.EndsWith(".accessories", StringComparison.Ordinal) => "\uE714",
        RuleAction.ForgetHistory when Rule.Id.EndsWith(".7zip", StringComparison.Ordinal) || Rule.Id.EndsWith(".winrar", StringComparison.Ordinal) => "\uF012",
        RuleAction.ForgetHistory when Rule.Id.EndsWith(".acrobat", StringComparison.Ordinal) => "\uEA90",
        RuleAction.ForgetHistory => "\uE81C",
        _ when Rule.Category == RuleCategory.Browsers && Rule.Id.EndsWith(".cookies", StringComparison.Ordinal) => "\uEA0D",
        _ when Rule.Category == RuleCategory.Browsers && Rule.Id.EndsWith(".history", StringComparison.Ordinal) => "\uE81C",
        _ when Rule.Category == RuleCategory.Browsers && Rule.Id.EndsWith(".session", StringComparison.Ordinal) => "\uE7C4",
        _ when Rule.Category == RuleCategory.Browsers && Rule.Id.EndsWith(".sitedata", StringComparison.Ordinal) => "\uE8B7",
        _ when Rule.Category == RuleCategory.Browsers && Rule.Id.EndsWith(".root", StringComparison.Ordinal) => "\uE7F4",
        _ when Rule.Category == RuleCategory.Browsers => "\uE774",
        _ when Rule.Id.Contains("shader", StringComparison.OrdinalIgnoreCase) => "\uE7F4",
        _ when Rule.Id.Contains("temp", StringComparison.OrdinalIgnoreCase) => "\uE8B7",
        _ when Rule.Id.Contains("log", StringComparison.OrdinalIgnoreCase) => "\uE9D5",
        _ when Rule.Id.Contains("dump", StringComparison.OrdinalIgnoreCase) || Rule.Id.Contains("wer", StringComparison.OrdinalIgnoreCase) => "\uE783",
        _ when Rule.Id.Contains("recent", StringComparison.OrdinalIgnoreCase) => "\uE81C",
        _ when Rule.Id.Contains("thumb", StringComparison.OrdinalIgnoreCase) => "\uE8B9",
        _ when Rule.Id.Contains("update", StringComparison.OrdinalIgnoreCase) => "\uE896",
        _ when Rule.Id.Contains("store", StringComparison.OrdinalIgnoreCase) => "\uE7BF",
        _ when Rule.Id.Contains("teams", StringComparison.OrdinalIgnoreCase) || Rule.Id.Contains("office", StringComparison.OrdinalIgnoreCase) => "\uE8F1",
        _ when Rule.Id.Contains("onedrive", StringComparison.OrdinalIgnoreCase) => "\uE753",
        _ when Rule.Id.Contains("rdp", StringComparison.OrdinalIgnoreCase) => "\uE7F7",
        _ when Rule.Id.Contains("inet", StringComparison.OrdinalIgnoreCase) => "\uE774",
        _ => "\uE8A5",
    };

    /// <summary>Locations shown in the details pane.</summary>
    public IReadOnlyList<string> Locations =>
    [
        .. Rule.Targets.Select(t => t.ToString()),
        .. Rule.Databases.Select(d => $"{d.Path}  (rows only - the file is kept)"),
        .. Rule.History.Select(DescribeHistoryTarget),
    ];

    private static string DescribeHistoryTarget(HistoryTarget target) => target.Store switch
    {
        HistoryStore.RegistryKeyContents => $@"HKCU\{target.Location}  (contents only - the key is kept)",
        HistoryStore.RegistryValues => $@"HKCU\{target.Location}  (values: {string.Join(", ", target.ValueNames)})",
        HistoryStore.NotepadTabs => Path.Combine(target.Location, "TabState") + @"  and  WindowState\*.bin",
        HistoryStore.NotepadRecentFiles => $"{target.Location}  (RecentFiles only - other settings are kept)",
        HistoryStore.NotepadPlusPlusSession => Path.Combine(target.Location, "session.xml") + @"  and  backup\*",
        HistoryStore.NotepadPlusPlusRecentFiles => Path.Combine(target.Location, "config.xml") + "  (<History> entries only)",
        HistoryStore.VlcRecentMedia => $"{target.Location}  ([RecentsMRL] and last folder only)",
        _ => target.Location,
    };

    /// <summary>Tab headers of the details pane adapt to what the rule removes.</summary>
    public string PreviewHeader => IsHistoryPurge ? "Latest" : IsDatabasePurge && !IsSiteStoragePurge ? "Frequent" : "Largest";

    public string AllItemsHeader => IsSiteStoragePurge ? "Websites" : IsDatabasePurge ? "Entries" : "All files";

    public string EmptyPreviewText => Scan is null
        ? (IsDatabasePurge ? "No entries listed yet - run Analyze." : "Nothing listed yet - run Analyze.")
        : (IsDatabasePurge ? "No entries found - nothing to remove." : "No files found - nothing to remove.");

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScan), nameof(SizeText), nameof(StatusText), nameof(IsBlocked), nameof(IsSkipped), nameof(IsNotPresent), nameof(ItemCountText), nameof(CanClean), nameof(PreviewPaths), nameof(PreviewOverflow), nameof(Bytes), nameof(Self), nameof(IsWorking), nameof(RowToolTip), nameof(EmptyPreviewText))]
    private RuleScanResult? _scan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCleanResult), nameof(CleanSummary), nameof(StatusText), nameof(SizeText), nameof(FailureLines), nameof(Bytes), nameof(ItemCountText), nameof(Self), nameof(IsWorking), nameof(RowToolTip))]
    private RuleCleanResult? _cleanResult;

    /// <summary>True when this item is shown in the details pane.</summary>
    [ObservableProperty]
    private bool _isHighlighted;

    /// <summary>True while the engine is currently processing this rule (drives the row spinner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Self))]
    private bool _isWorking;

    /// <summary>Returns the item itself; re-raised whenever state changes so multi-property converters refresh.</summary>
    public RuleItemViewModel Self => this;

    public bool HasScan => Scan is not null;

    public bool HasCleanResult => CleanResult is not null;

    public bool IsSkipped => Scan?.IsSkipped == true;

    /// <summary>The location does not exist on this PC - the row is kept for transparency but sorted to the end of its group.</summary>
    public bool IsNotPresent => Scan?.Skip == SkipReason.LocationNotFound;

    /// <summary>Position inside the group as the rule set defines it; the live view sorts by presence first, then by this.</summary>
    public int SourceIndex { get; set; }

    public bool IsBlocked => Scan?.Skip is SkipReason.ApplicationRunning or SkipReason.RequiresAdministrator;

    /// <summary>True when the rule is selected, analyzed and nothing prevents it from running.</summary>
    public bool CanClean => IsSelected && Scan is { IsSkipped: false };

    public long Bytes => CleanResult?.BytesFreed ?? Scan?.TotalBytes ?? 0;

    public string SizeText
    {
        get
        {
            if (CleanResult is { } clean)
            {
                return clean.IsSkipped ? "—" : Scanner.FormatBytes(clean.BytesFreed);
            }

            if (Scan is null)
            {
                return string.Empty;
            }

            if (Scan.Skip == SkipReason.LocationNotFound)
            {
                return "—";
            }

            if (IsDatabasePurge)
            {
                // Rows do not have a file size; the estimate is what the compaction is expected to give back.
                return Scan.EntryCount == 0 ? "0 B" : Scanner.FormatBytes(Scan.TotalBytes);
            }

            return (Scan.IsEstimate && Scan.TotalBytes == 0 ? "n/a" : Scanner.FormatBytes(Scan.TotalBytes));
        }
    }

    public string ItemCountText
    {
        get
        {
            if (CleanResult is { IsSkipped: false } clean)
            {
                var parts = new List<string>();
                if (clean.DeletedFiles > 0)
                {
                    parts.Add($"{Plural(clean.DeletedFiles, "file")} removed");
                }

                if (clean.DeletedEntries > 0)
                {
                    parts.Add($"{Plural(clean.DeletedEntries, EntryUnit.Singular, EntryUnit.Plural)} removed");
                }

                if (clean.Failures.Count > 0)
                {
                    parts.Add($"{clean.Failures.Count:N0} skipped");
                }

                return string.Join(", ", parts);
            }

            if (Scan is null)
            {
                return string.Empty;
            }

            if (IsDatabasePurge)
            {
                return Scan.EntryCount == 0 ? string.Empty : Plural(Scan.EntryCount, EntryUnit.Singular, EntryUnit.Plural);
            }

            if (Scan.Items.Count == 0)
            {
                return string.Empty;
            }

            var files = Scan.FileCount;
            var dirs = Scan.DirectoryCount;
            if (files == 0)
            {
                return $"{Plural(dirs, "empty folder")}";
            }

            return dirs > 0 ? $"{Plural(files, "file")}, {Plural(dirs, "folder")}" : Plural(files, "file");
        }
    }

    public string StatusText
    {
        get
        {
            if (CleanResult is { } clean)
            {
                if (clean.IsSkipped)
                {
                    return clean.Skip switch
                    {
                        SkipReason.ApplicationRunning => "Skipped: application running",
                        SkipReason.RequiresAdministrator => "Skipped: administrator required",
                        _ => "Skipped: " + (clean.SkipDetails ?? clean.Skip.ToString()),
                    };
                }

                return clean.Message ?? (clean.Failures.Count > 0
                    ? $"Cleaned, {Plural(clean.Failures.Count, "item")} in use skipped"
                    : "Cleaned");
            }

            if (Scan is null)
            {
                return "Not analyzed";
            }

            return Scan.Skip switch
            {
                SkipReason.None when IsDatabasePurge => Scan.EntryCount == 0 ? "Nothing to clean" : "Ready",
                SkipReason.None => Scan.SkipDetails ?? (Scan.IsEstimate ? "Ready" : (Scan.FileCount == 0 ? (Scan.DirectoryCount > 0 ? "Only empty folders left" : "Nothing to clean") : "Ready")),
                SkipReason.ApplicationRunning => Rule.Category == RuleCategory.Browsers
                    ? $"Close {Rule.Group} first"
                    : $"Close {string.Join(", ", Scan.BlockingProcesses.Select(ApplicationCloser.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase))} first",
                SkipReason.RequiresAdministrator => "Administrator rights required",
                SkipReason.LocationNotFound => "Not present on this PC",
                SkipReason.ServiceUnavailable => "Service unavailable",
                SkipReason.NotSupported => "Not supported",
                _ => "Error: " + (Scan.SkipDetails ?? "unknown"),
            };
        }
    }

    public string CleanSummary
    {
        get
        {
            if (CleanResult is not { } clean || clean.IsSkipped)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (clean.DeletedFiles > 0)
            {
                parts.Add($"{Plural(clean.DeletedFiles, "file")} removed");
            }

            if (clean.DeletedEntries > 0)
            {
                parts.Add($"{Plural(clean.DeletedEntries, EntryUnit.Singular, EntryUnit.Plural)} removed");
            }

            if (clean.DeletedDirectories > 0)
            {
                parts.Add($"{Plural(clean.DeletedDirectories, "empty folder")} removed");
            }

            if (clean.Failures.Count > 0)
            {
                parts.Add($"{clean.Failures.Count:N0} skipped (in use/protected)");
            }

            if (clean.ScheduledForReboot.Count > 0)
            {
                parts.Add($"{clean.ScheduledForReboot.Count:N0} scheduled for reboot");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>File list for the details pane (capped for UI performance). Database rules list their entries.</summary>
    public IReadOnlyList<string> PreviewPaths => IsDatabasePurge
        ? Scan?.Entries.Select(e => $"{e.Value}    ‹{e.Field}›").ToList() ?? []
        : Scan?.Items.Take(500).Select(i => i.Path).ToList() ?? [];

    public int PreviewOverflow => IsDatabasePurge
        ? Math.Max(0, (Scan?.EntryCount ?? 0) - (Scan?.Entries.Count ?? 0))
        : Math.Max(0, (Scan?.Items.Count ?? 0) - 500);

    public IReadOnlyList<string> FailureLines => CleanResult?.Failures.Select(f => $"{f.Path}  —  {f.Reason}").ToList() ?? [];

    /// <summary>Largest files first - what the user usually wants to see. Database rules: most used entries; history rules: the latest.</summary>
    public IReadOnlyList<PreviewEntry> LargestItems => IsHistoryPurge
        ? Scan?.Entries
            .OrderByDescending(e => e.LastUsedUtc ?? DateTime.MinValue)
            .Take(8)
            .Select(e => new PreviewEntry(DisplayNameOf(e.Value), DescribeEntry(e), e.Field))
            .ToList() ?? []
        : IsSiteStoragePurge
        ? Scan?.Entries
            .OrderByDescending(e => e.TimesUsed)
            .ThenByDescending(e => e.Bytes)
            .Take(8)
            .Select(e => new PreviewEntry(e.Field, DescribeEntry(e), e.Value))
            .ToList() ?? []
        : IsDatabasePurge
        ? Scan?.Entries
            .OrderByDescending(e => e.TimesUsed)
            .ThenByDescending(e => e.LastUsedUtc)
            .Take(8)
            .Select(e => new PreviewEntry(e.Value, DescribeEntry(e), e.TimesUsed > 0 ? $"{e.TimesUsed}×" : e.Field))
            .ToList() ?? []
        : Scan?.Items
            .Where(i => !i.IsDirectory)
            .OrderByDescending(i => i.Size)
            .Take(8)
            .Select(i => new PreviewEntry(Path.GetFileName(i.Path), i.Path, Scanner.FormatBytes(i.Size)))
            .ToList() ?? [];

    /// <summary>A remembered path is shown by its file name; anything else (a search, a command) as is.</summary>
    private static string DisplayNameOf(string value)
    {
        if (value.Length > 3 && (value[1] == ':' || value.StartsWith(@"\\", StringComparison.Ordinal)))
        {
            var name = Path.GetFileName(value.TrimEnd('\\'));
            return name.Length > 0 ? name : value;
        }

        return value;
    }

    private string DescribeEntry(DatabaseEntry entry)
    {
        if (IsSiteStoragePurge)
        {
            var size = entry.Bytes > 0 ? $" · {Scanner.FormatBytes(entry.Bytes)}" : string.Empty;
            var modified = entry.LastUsedUtc is { } when ? $" · last change {when.ToLocalTime():g}" : string.Empty;
            return $"{entry.Value}{size}{modified}";
        }

        var parts = new List<string> { IsHistoryPurge ? entry.Value : $"Field: {entry.Field}" };
        if (entry.TimesUsed > 0)
        {
            parts.Add($"used {Plural(entry.TimesUsed, "time")}");
        }

        if (entry.LastUsedUtc is { } last)
        {
            parts.Add($"last used {last.ToLocalTime():g}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Full name plus the current status, for the row tooltip (rows trim long text).</summary>
    public string RowToolTip
    {
        get
        {
            var detail = string.Join(" · ", new[] { StatusText, ItemCountText }.Where(s => !string.IsNullOrEmpty(s)));
            return string.IsNullOrEmpty(detail) ? Name : $"{Name}\n{detail}";
        }
    }

    private static string Plural(long count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    private static string Plural(long count, string singular, string plural) => count == 1 ? $"1 {singular}" : $"{count:N0} {plural}";

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClean));
        _selectionChanged(this);
    }

    partial void OnScanChanged(RuleScanResult? value) => OnPropertyChanged(nameof(LargestItems));

    public void ResetResults()
    {
        CleanResult = null;
        Scan = null;
        IsWorking = false;
    }
}

public sealed record PreviewEntry(string Name, string Path, string Size);