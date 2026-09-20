using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCleaner.Core;
using PCleaner.Core.Browsers;
using PCleaner.Core.Engine;
using PCleaner.Core.Logging;
using PCleaner.Core.Model;
using PCleaner.Core.Privacy;
using PCleaner.Core.Rules;
using PCleaner.Core.Settings;
using PCleaner.Core.Windows;

namespace PCleaner.App.ViewModels;

public enum EngineState
{
    Loading,
    Idle,
    Analyzing,
    Analyzed,
    Cleaning,
    Cleaned,
}

public enum Section
{
    Dashboard,
    Windows,
    Applications,
    Browsers,
    Tracking,
    Settings,
    Log,
}

/// <summary>Data for a confirmation prompt; the view decides how to render it.</summary>
public sealed record ConfirmationRequest(string Title, string Message, string? Warning, string? Detail);

/// <summary>One bar of the dashboard breakdown.</summary>
public sealed record BreakdownEntry(string Name, string Glyph, long Bytes, double Fraction, string SizeText, int Items);

[SupportedOSPlatform("windows")]
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MemoryLog _memoryLog;
    private readonly FileLog _fileLog;
    private readonly ICleanerLog _log;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private bool _suppressSelectionPersistence;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = AppSettings.Load();
        _memoryLog = new MemoryLog();
        _fileLog = new FileLog();
        _log = new CompositeLog(_memoryLog, _fileLog);
        _memoryLog.EntryAdded += OnLogEntry;
        Settings = new SettingsViewModel(_settings, _log);
        Settings.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(HasPendingSettingsRestart));
            if (e.PropertyName is nameof(SettingsViewModel.CloseBrowsersBeforeCleaning) or nameof(SettingsViewModel.AutoCloseBlockingApplications))
            {
                OnPropertyChanged(nameof(BlockingPolicy));
                OnPropertyChanged(nameof(BlockedBannerText));
                OnPropertyChanged(nameof(CanClean));
                CleanCommand.NotifyCanExecuteChanged();
                PrimaryActionCommand.NotifyCanExecuteChanged();
            }
        };
        IsElevated = Elevation.IsElevated;
        _lastFreedBytes = _settings.LastRunFreedBytes;
        _lastFreedItems = _settings.LastRunItems;
        _log.Info($"PCleaner {AppVersion} started (elevated={IsElevated}, OS={Environment.OSVersion.VersionString}).");
    }

    public static string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string LastRunText => _settings.LastRunUtc is { } utc
        ? $"{TextFormat.Count(LastFreedItems, "item")} removed on {utc.ToLocalTime():g}"
        : "No cleanup has been run yet";

    public string LifetimeFreedText => Scanner.FormatBytes(_settings.LifetimeFreedBytes) + " freed in total";

    public string LifetimeFreedShort => Scanner.FormatBytes(_settings.LifetimeFreedBytes);

    public SettingsViewModel Settings { get; }

    /// <summary>Set by the view: shows a confirmation prompt and returns true when the user confirmed.</summary>
    public Func<ConfirmationRequest, bool>? ConfirmHandler { get; set; }

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    public ObservableCollection<DetectedBrowser> Browsers { get; } = [];

    [ObservableProperty]
    private bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanAnalyze), nameof(CanClean), nameof(PrimaryActionText), nameof(PrimaryActionGlyph), nameof(IsIdleOrDone), nameof(ShowResults), nameof(IsCleaned), nameof(StateLabel))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CleanCommand), nameof(CancelCommand), nameof(RefreshCommand), nameof(PrimaryActionCommand), nameof(SelectTrackingResetCommand), nameof(SelectForgetOpenedFilesCommand))]
    private EngineState _state = EngineState.Loading;

    /// <summary>Per-category breakdown for the dashboard (only categories with something to show).</summary>
    public ObservableCollection<BreakdownEntry> Breakdown { get; } = [];

    public bool IsCleaned => State == EngineState.Cleaned;

    public string StateLabel => State switch
    {
        EngineState.Loading => "Loading",
        EngineState.Idle => "Ready",
        EngineState.Analyzing => "Analyzing",
        EngineState.Analyzed => "Analyzed",
        EngineState.Cleaning => "Cleaning",
        _ => "Done",
    };

    public string PrimaryActionGlyph => State switch
    {
        EngineState.Analyzed when HasCleanableSelection => "\uEA99",
        EngineState.Analyzing or EngineState.Cleaning or EngineState.Loading => "\uE916",
        _ => "\uE721",
    };

    /// <summary>Something can be cleaned right now - or is only waiting for an application PCleaner may ask to close.</summary>
    private bool HasCleanableSelection => AllItems.Any(i => i.CanClean || (BlockingPolicy != BlockingAppPolicy.Skip && IsBlockedByApplication(i)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboard), nameof(IsSettings), nameof(IsLog), nameof(IsTracking), nameof(IsRulesPage), nameof(SectionTitle), nameof(SectionSubtitle), nameof(VisibleCategories), nameof(SelectionSummary), nameof(HasBlockedSelection))]
    private Section _currentSection = Section.Dashboard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    private RuleItemViewModel? _selectedItem;

    public bool HasSelectedItem => SelectedItem is not null;

    partial void OnSelectedItemChanged(RuleItemViewModel? oldValue, RuleItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsHighlighted = false;
        }

        if (newValue is not null)
        {
            newValue.IsHighlighted = true;
        }
    }

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    private string _statusText = "Loading rules...";

    [ObservableProperty]
    private string? _currentPath;

    [ObservableProperty]
    private string _summaryHeadline = string.Empty;

    [ObservableProperty]
    private string _summaryDetails = string.Empty;

    [ObservableProperty]
    private long _totalSelectedBytes;

    [ObservableProperty]
    private long _lastFreedBytes;

    [ObservableProperty]
    private int _lastFreedItems;

    [ObservableProperty]
    private int _lastSkippedItems;

    [ObservableProperty]
    private int _selectedRuleCount;

    [ObservableProperty]
    private int _totalRuleCount;

    public bool IsBusy => State is EngineState.Loading or EngineState.Analyzing or EngineState.Cleaning;

    public bool IsIdleOrDone => !IsBusy;

    public bool ShowResults => State is EngineState.Analyzed or EngineState.Cleaned;

    public bool CanAnalyze => State is EngineState.Idle or EngineState.Analyzed or EngineState.Cleaned;

    public bool CanClean => State == EngineState.Analyzed && HasCleanableSelection;

    /// <summary>What happens to applications that keep an item locked, resolved from the settings and the token.</summary>
    public BlockingAppPolicy BlockingPolicy => _settings.AutoCloseBlockingApplications && IsElevated
        ? BlockingAppPolicy.AutoClose
        : _settings.CloseBrowsersBeforeCleaning ? BlockingAppPolicy.RequestClose : BlockingAppPolicy.Skip;

    /// <summary>Text for the "application is running" banner, after the application's name.</summary>
    public string BlockedBannerText => BlockingPolicy switch
    {
        BlockingAppPolicy.AutoClose => "is running - Clean now asks it to close, ends leftover background processes, cleans its items and reopens it afterwards.",
        BlockingAppPolicy.RequestClose => "is running - Clean now asks it to close first. If it stays open in the background, its items are skipped (administrator mode can end it).",
        _ => "is running - its items are skipped until you close it. PCleaner never terminates programs unless you enable that in Settings.",
    };

    private static bool IsBlockedByApplication(RuleItemViewModel item) => item.IsSelected && item.Scan?.Skip == SkipReason.ApplicationRunning;

    public string PrimaryActionText => State switch
    {
        EngineState.Analyzed when HasCleanableSelection => "Clean now",
        EngineState.Analyzing => "Analyzing...",
        EngineState.Cleaning => "Cleaning...",
        EngineState.Loading => "Loading...",
        _ => "Analyze",
    };

    public string TotalSelectedText => Scanner.FormatBytes(TotalSelectedBytes);

    public string LastFreedText => Scanner.FormatBytes(LastFreedBytes);

    public bool IsDashboard => CurrentSection == Section.Dashboard;

    public bool IsSettings => CurrentSection == Section.Settings;

    public bool IsLog => CurrentSection == Section.Log;

    public bool IsTracking => CurrentSection == Section.Tracking;

    public bool IsRulesPage => CurrentSection is Section.Windows or Section.Applications or Section.Browsers;

    public string SectionTitle => CurrentSection switch
    {
        Section.Dashboard => "Dashboard",
        Section.Windows => "Windows",
        Section.Applications => "Applications",
        Section.Browsers => "Web browsers",
        Section.Tracking => "Privacy & tracking",
        Section.Settings => "Settings",
        _ => "Activity log",
    };

    public string SectionSubtitle => CurrentSection switch
    {
        Section.Dashboard => "Overview of what can be reclaimed safely on this PC.",
        Section.Windows => "Temporary files and caches, what Windows remembers you opened, the Recycle Bin, system files and Microsoft's own Disk Cleanup handlers.",
        Section.Applications => "Caches of graphics drivers and Microsoft applications - and what your apps remember opening.",
        Section.Browsers => "Detected browsers and profiles. Extensions, bookmarks, passwords and settings are never touched.",
        Section.Tracking => "What websites and this PC remember about you - and how to reset it.",
        Section.Settings => "Behaviour and safety options.",
        _ => "Everything PCleaner did, with timestamps. A copy is written to disk.",
    };

    public IEnumerable<CategoryViewModel> VisibleCategories => CurrentSection switch
    {
        Section.Windows => Categories.Where(c => c.Category is RuleCategory.WindowsUser or RuleCategory.WindowsSystem),
        Section.Applications => Categories.Where(c => c.Category == RuleCategory.Applications),
        Section.Browsers => Categories.Where(c => c.Category == RuleCategory.Browsers),
        _ => [],
    };

    public bool HasPendingSettingsRestart => Settings.RequiresRuleRebuild;

    public string LogDirectory => _fileLog.Directory;

    public string ElevationText => IsElevated ? "Administrator" : "Standard user";

    public string BrowsersSummary => Browsers.Count == 0
        ? "No supported browser data found."
        : string.Join(", ", Browsers.Select(b => $"{b.Definition.Name} ({b.Profiles.Count} profile{(b.Profiles.Count == 1 ? string.Empty : "s")})"));

    private IEnumerable<RuleItemViewModel> AllItems => Categories.SelectMany(c => c.AllItems);

    // ------------------------------------------------------------------ loading

    public async Task LoadAsync()
    {
        State = EngineState.Loading;
        StatusText = "Detecting browsers and building rules...";
        try
        {
            var (rules, browsers) = await Task.Run(BuildRules).ConfigureAwait(true);
            Browsers.Clear();
            foreach (var b in browsers.OrderBy(b => b.Definition.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Browsers.Add(b); // alphabetical, like the Browsers page groups and the Privacy sections
            }

            PopulateCategories(rules);
            Settings.RequiresRuleRebuild = false;
            OnPropertyChanged(nameof(HasPendingSettingsRestart));
            OnPropertyChanged(nameof(BrowsersSummary));
            OnPropertyChanged(nameof(VisibleCategories));
            StatusText = $"{TextFormat.Count(TotalRuleCount, "cleanup rule")} ready · {TextFormat.Count(Browsers.Count, "browser")} detected. Click Analyze to preview.";
            State = EngineState.Idle;
            await RefreshInsightsAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to build the rule set.", ex);
            StatusText = "Failed to load rules: " + ex.Message;
            State = EngineState.Idle;
        }
    }

    // ------------------------------------------------------------------ tracking insights

    public ObservableCollection<PrivacyInsight> Insights { get; } = [];

    /// <summary>The same findings, one section per scope, in the order the engine returns them.</summary>
    public ObservableCollection<InsightGroupViewModel> InsightGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InsightsSummary), nameof(OpenedFilesSummary))]
    private bool _insightsLoading;

    public string InsightsSummary
    {
        get
        {
            if (InsightsLoading)
            {
                return "Looking at sign-in cookies, sync state and Local Storage...";
            }

            var accounts = Insights.Count(i => i.Level == InsightLevel.Account);
            var local = Insights.Count(i => i.Level == InsightLevel.Local && i.RelatedRuleIds.Count > 0);
            var system = Insights.Count(i => i.Level == InsightLevel.System);
            var parts = new List<string>();
            if (accounts > 0)
            {
                parts.Add($"{accounts} account sign-in{(accounts == 1 ? string.Empty : "s")}");
            }

            if (local > 0)
            {
                parts.Add($"{local} browser profile{(local == 1 ? string.Empty : "s")} that websites remember");
            }

            if (system > 0)
            {
                parts.Add("Windows advertising ID on");
            }

            return parts.Count == 0 ? "Nothing here lets websites recognise you across visits." : string.Join(" · ", parts) + ".";
        }
    }

    /// <summary>Summary line of the "opened files" card.</summary>
    public string OpenedFilesSummary
    {
        get
        {
            if (InsightsLoading)
            {
                return "Looking at recent-file lists and saved tabs...";
            }

            var apps = Insights.Where(i => i.Level == InsightLevel.Device).ToList();
            if (apps.Count == 0)
            {
                return "No app on this PC remembers what you opened right now.";
            }

            var names = apps.Select(i => i.Title.Split(" remembers ", 2, StringSplitOptions.None)[0].Split(" is open", 2, StringSplitOptions.None)[0]).Distinct().ToList();
            return $"{names.Count} app{(names.Count == 1 ? string.Empty : "s")} remember{(names.Count == 1 ? "s" : string.Empty)} opened files: {string.Join(", ", names)}.";
        }
    }

    /// <summary>Rule ids the "reset" preset selects: everything that identifies this browser to websites.</summary>
    private static readonly string[] ResetRuleSuffixes = [".cookies", ".localstorage", ".sitedata", ".history", ".session"];

    [RelayCommand]
    private Task RefreshInsights() => RefreshInsightsAsync();

    public async Task RefreshInsightsAsync()
    {
        if (InsightsLoading)
        {
            return;
        }

        InsightsLoading = true;
        try
        {
            var browsers = Browsers.ToList();
            var historyRules = AllItems.Where(i => i.Rule.Action == RuleAction.ForgetHistory).Select(i => i.Rule).ToList();
            var results = await Task.Run(() => new PrivacyInsights(_log).Collect(browsers, RuleIdFor, historyRules)).ConfigureAwait(true);
            Insights.Clear();
            foreach (var insight in results)
            {
                Insights.Add(insight);
            }

            InsightGroups.Clear();
            foreach (var group in results.GroupBy(i => i.Scope, StringComparer.Ordinal))
            {
                InsightGroups.Add(new InsightGroupViewModel(group.Key, group.First().ScopeKind, group.ToList()));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Privacy insights failed: " + ex.Message);
        }
        finally
        {
            InsightsLoading = false;
            OnPropertyChanged(nameof(InsightsSummary));
            OnPropertyChanged(nameof(OpenedFilesSummary));
        }
    }

    /// <summary>Rule id of a browser profile rule ("cookies", "localstorage", ...), mirroring <see cref="BrowserRuleProvider"/>.</summary>
    private string RuleIdFor(BrowserProfile profile, string suffix)
        => AllItems.Select(i => i.Rule.Id)
            .FirstOrDefault(id => id.EndsWith("." + suffix, StringComparison.Ordinal) && id.StartsWith($"browser.{profile.Browser.Id}.", StringComparison.Ordinal)
                && id.Contains($".{Slug(profile.DirectoryName)}.", StringComparison.Ordinal))
            ?? $"browser.{profile.Browser.Id}.{suffix}";

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

    /// <summary>
    /// Selects everything that identifies this browser to websites (cookies, Local Storage identifiers, website
    /// storage, history, saved session) for every browser profile, on top of the current selection, and jumps to the
    /// browser page so the user can review before cleaning.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private void SelectTrackingReset()
    {
        var count = 0;
        foreach (var item in AllItems.Where(i => i.Rule.Category == RuleCategory.Browsers && ResetRuleSuffixes.Any(s => i.Rule.Id.EndsWith(s, StringComparison.Ordinal))))
        {
            if (!item.IsSelected)
            {
                item.IsSelected = true;
                count++;
            }
        }

        CurrentSection = Section.Browsers;
        StatusText = count == 0
            ? "The reset items were already selected. Close the browsers, click Analyze, then Clean now."
            : $"{TextFormat.Count(count, "item")} added to the selection: cookies, website identifiers, website storage, history and saved sessions of every browser. Close the browsers, click Analyze, then Clean now. Sites you stay signed in to keep their account history.";
    }

    /// <summary>
    /// Selects everything that remembers which files were opened - the recent-file lists of Windows and of the
    /// installed applications and the tabs editors restore - and jumps to the Windows page for review. Applications
    /// that still hold their list open are asked to close when the cleanup starts.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private void SelectForgetOpenedFiles()
    {
        var count = 0;
        foreach (var item in AllItems.Where(i => i.Rule.Action == RuleAction.ForgetHistory || i.Rule.Id == "windows.recent"))
        {
            if (!item.IsSelected)
            {
                item.IsSelected = true;
                count++;
            }
        }

        CurrentSection = Section.Windows;
        var apps = string.Join(", ", AllItems.Where(i => i.Rule.Action == RuleAction.ForgetHistory && i.Rule.Category == RuleCategory.Applications).Select(i => i.Rule.Name));
        StatusText = (count == 0 ? "The 'forget' items were already selected" : $"{TextFormat.Count(count, "item")} added to the selection on the Windows and Applications pages")
            + (apps.Length > 0 ? $" (including {apps})" : string.Empty)
            + ". Click Analyze, then Clean now - open editors are asked to close first and unsaved tabs are lost.";
    }

    /// <summary>Selects the rules behind one insight and shows them.</summary>
    [RelayCommand]
    private void SelectInsightRules(PrivacyInsight? insight)
    {
        if (insight is null || insight.RelatedRuleIds.Count == 0)
        {
            return;
        }

        var items = AllItems.Where(i => insight.RelatedRuleIds.Contains(i.Rule.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var item in items)
        {
            item.IsSelected = true;
        }

        if (items.Count > 0)
        {
            SelectedItem = items[0];
            CurrentSection = items[0].Rule.Category switch
            {
                RuleCategory.Browsers => Section.Browsers,
                RuleCategory.Applications => Section.Applications,
                _ => Section.Windows,
            };
            StatusText = insight.ScopeKind == InsightScopeKind.Browser
                ? $"{TextFormat.Count(items.Count, "item")} selected for {insight.Scope}. Close the browser, click Analyze, then Clean now."
                : $"{TextFormat.Count(items.Count, "item")} selected. Click Analyze, then Clean now - a running application is asked to close first.";
        }
    }

    [RelayCommand]
    private void OpenInsightLink(InsightAction? action)
    {
        if (action is null || string.IsNullOrWhiteSpace(action.Url))
        {
            return;
        }

        // Only well-known schemes: https links to the platforms, browser settings pages and Windows settings.
        var allowed = action.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || action.Url.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            || action.Url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)
            || action.Url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase)
            || action.Url.StartsWith("brave://", StringComparison.OrdinalIgnoreCase)
            || action.Url.StartsWith("vivaldi://", StringComparison.OrdinalIgnoreCase)
            || action.Url.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            return;
        }

        try
        {
            if (action.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || action.Url.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(action.Url) { UseShellExecute = true });
                return;
            }

            // Browser-internal pages cannot be opened through the shell: put the address on the clipboard instead.
            Clipboard.SetText(action.Url);
            StatusText = $"'{action.Url}' copied - paste it into the browser's address bar.";
        }
        catch (Exception ex)
        {
            _log.Warn("Could not open link: " + ex.Message);
        }
    }

    private (IReadOnlyList<CleanupRule> Rules, IReadOnlyList<DetectedBrowser> Browsers) BuildRules()
    {
        var context = RuleContext.FromCurrentMachine(IsElevated, TimeSpan.FromHours(_settings.TempFileMinimumAgeHours));
        var rules = new List<CleanupRule>(new WindowsRuleProvider(context).GetRules());
        var detector = new BrowserDetector(BrowserCatalog.GetDefinitions(context.LocalAppData, context.RoamingAppData), _log);
        var browsers = detector.Detect();
        rules.AddRange(new BrowserRuleProvider(browsers, _settings.IncludeSystemAndGuestProfiles).GetRules());
        _log.Info($"Rule set built: {rules.Count} rules ({rules.Count(r => r.Category == RuleCategory.Browsers)} browser rules).");
        return (rules, browsers);
    }

    private void PopulateCategories(IReadOnlyList<CleanupRule> rules)
    {
        _suppressSelectionPersistence = true;
        try
        {
            Categories.Clear();
            SelectedItem = null;

            // One fixed order for everything the user sees: groups by purpose, items by risk (browsers: by profile
            // and the "Clear browsing data" sequence). GroupBy keeps the first-appearance order of the sorted list.
            var ordered = RuleOrder.Sort(rules);

            CategoryViewModel Build(RuleCategory category, string title, string subtitle, string glyph)
            {
                var groups = ordered
                    .Where(r => r.Category == category)
                    .GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new RuleGroupViewModel(
                        string.IsNullOrWhiteSpace(g.Key) ? title : g.Key,
                        category,
                        g.Select(r => new RuleItemViewModel(r, _settings.IsRuleEnabled(r.Id, DefaultSelection(r)), OnItemSelectionChanged))));
                return new CategoryViewModel(category, title, subtitle, glyph, groups);
            }

            Categories.Add(Build(RuleCategory.WindowsUser, "Your account", "Caches and temporary files of the current user, what it recently opened, and the Recycle Bin. No administrator rights needed.", "\uE77B"));
            Categories.Add(Build(RuleCategory.WindowsSystem, "System", IsElevated ? "System-wide files, Microsoft's Disk Cleanup handlers, event logs and maintenance." : "System-wide files, Microsoft's Disk Cleanup handlers, event logs and maintenance. Items marked 'Admin' need 'Restart as administrator'.", "\uE7F8"));
            Categories.Add(Build(RuleCategory.Applications, "Applications", "Graphics driver caches, Microsoft app caches, and what installed apps remember opening.", "\uE71D"));
            Categories.Add(Build(RuleCategory.Browsers, "Web browsers", "One group per detected browser, profile by profile - caches first, then history, cookies and site data. Never extensions.", "\uE774"));

            foreach (var category in Categories)
            {
                category.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(CategoryViewModel.TotalBytes) or nameof(CategoryViewModel.SelectedCount))
                    {
                        RecomputeTotals();
                    }
                };
            }

            TotalRuleCount = AllItems.Count();
            RecomputeTotals();
        }
        finally
        {
            _suppressSelectionPersistence = false;
        }
    }

    /// <summary>Safe rules are pre-selected, except administrator-only rules while running as a standard user.</summary>
    private bool DefaultSelection(CleanupRule rule) => rule.EnabledByDefault && (IsElevated || !rule.RequiresAdministrator);

    private void OnItemSelectionChanged(RuleItemViewModel item)
    {
        if (!_suppressSelectionPersistence)
        {
            _settings.SetRuleEnabled(item.Rule.Id, item.IsSelected, DefaultSelection(item.Rule));
            SaveSettingsSafe();
        }

        // Changing the selection after an analysis invalidates the "Clean" step for unanalyzed items.
        if (State is EngineState.Analyzed or EngineState.Cleaned && item.IsSelected && item.Scan is null)
        {
            State = EngineState.Idle;
            StatusText = "Selection changed - click Analyze to preview the new selection.";
        }

        RecomputeTotals();
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(PrimaryActionText));
        CleanCommand.NotifyCanExecuteChanged();
        PrimaryActionCommand.NotifyCanExecuteChanged();
    }

    private void RecomputeTotals()
    {
        var items = AllItems.ToList();
        SelectedRuleCount = items.Count(i => i.IsSelected);
        TotalSelectedBytes = items.Where(i => i.IsSelected && i.Scan is { IsSkipped: false }).Sum(i => i.Scan!.TotalBytes);
        OnPropertyChanged(nameof(TotalSelectedText));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasBlockedSelection));
        DeselectBlockedCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void Navigate(Section section)
    {
        CurrentSection = section;
        OnPropertyChanged(nameof(VisibleCategories));
    }

    [RelayCommand(CanExecute = nameof(CanRunPrimaryAction))]
    private Task PrimaryActionAsync() => State == EngineState.Analyzed && HasCleanableSelection ? CleanAsync() : AnalyzeAsync();

    private bool CanRunPrimaryAction() => CanAnalyze;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        var selected = AllItems.Where(i => i.IsSelected).ToList();
        foreach (var item in AllItems)
        {
            item.ResetResults();
        }

        if (selected.Count == 0)
        {
            StatusText = "Nothing selected. Tick at least one item.";
            return;
        }

        _cts = new CancellationTokenSource();
        State = EngineState.Analyzing;
        ProgressIndeterminate = false;
        ProgressPercent = 0;
        LastFreedBytes = 0;
        LastFreedItems = 0;
        LastSkippedItems = 0;
        StatusText = "Analyzing...";
        foreach (var item in selected)
        {
            item.IsWorking = true;
        }

        var options = new EngineOptions { DryRun = false, ScheduleLockedFilesForReboot = _settings.ScheduleLockedFilesForReboot };
        var scanner = new Scanner(_log, options);
        var progress = new Progress<EngineProgress>(p =>
        {
            ProgressPercent = p.Percent;
            StatusText = $"Analyzing {p.CurrentRuleName}  ({p.CompletedRules}/{p.TotalRules})";
            CurrentPath = p.CurrentPath;
        });

        try
        {
            var rules = selected.Select(i => i.Rule).ToList();
            var results = await scanner.ScanAsync(rules, progress, _cts.Token, (index, result) => _dispatcher.BeginInvoke(() =>
            {
                // Stream each finished rule into the UI so the list fills up while the scan is still running.
                selected[index].Scan = result;
                selected[index].IsWorking = false;
                RecomputeTotals();
            }));

            for (var i = 0; i < selected.Count; i++)
            {
                selected[i].Scan = results[i];
                selected[i].IsWorking = false;
            }

            var total = results.Where(r => !r.IsSkipped).Sum(r => r.TotalBytes);
            var blocked = results.Count(r => r.Skip == SkipReason.ApplicationRunning);
            var admin = results.Count(r => r.Skip == SkipReason.RequiresAdministrator);
            var files = results.Where(r => !r.IsSkipped).Sum(r => r.FileCount);
            var entries = results.Where(r => !r.IsSkipped).Sum(r => r.EntryCount);

            SummaryHeadline = $"{Scanner.FormatBytes(total)} can be cleaned";
            var itemCount = results.Count(r => !r.IsSkipped && (r.TotalBytes > 0 || r.EntryCount > 0 || r.IsEstimate));
            var what = entries > 0 ? $"{TextFormat.Count(files, "file")}, {TextFormat.Count(entries, "form entry")}" : TextFormat.Count(files, "file");
            var details = new List<string> { $"{what} in {TextFormat.Count(itemCount, "item")}" };
            if (blocked > 0)
            {
                details.Add($"{TextFormat.Count(blocked, "item")} blocked by running applications");
            }

            if (admin > 0)
            {
                details.Add($"{TextFormat.Count(admin, "item")} {(admin == 1 ? "needs" : "need")} administrator rights");
            }

            SummaryDetails = string.Join(" · ", details);
            StatusText = "Analysis complete. Review the results, then click Clean now.";
            State = EngineState.Analyzed;
            RebuildBreakdown();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analysis cancelled.";
            State = EngineState.Idle;
        }
        catch (Exception ex)
        {
            _log.Error("Analysis failed.", ex);
            StatusText = "Analysis failed: " + ex.Message;
            State = EngineState.Idle;
        }
        finally
        {
            foreach (var item in selected)
            {
                item.IsWorking = false;
            }

            CurrentPath = null;
            ProgressPercent = 0;
            _cts?.Dispose();
            _cts = null;
            RecomputeTotals();
        }
    }

    /// <summary>Recomputes the dashboard breakdown from the current scan/clean results.</summary>
    private void RebuildBreakdown()
    {
        Breakdown.Clear();
        var cleaned = State == EngineState.Cleaned;
        var entries = Categories
            .Select(c => (Category: c,
                          Bytes: c.AllItems.Where(i => i.IsSelected && i.Scan is { IsSkipped: false }).Sum(i => cleaned ? (i.CleanResult?.BytesFreed ?? 0) : i.Scan!.TotalBytes),
                          Items: c.AllItems.Count(i => i.IsSelected && i.Scan is { IsSkipped: false } && (cleaned ? (i.CleanResult?.BytesFreed ?? 0) > 0 : (i.Scan.TotalBytes > 0 || i.Scan.IsEstimate)))))
            .Where(e => e.Bytes > 0)
            .OrderByDescending(e => e.Bytes)
            .ToList();
        var max = entries.Count == 0 ? 1 : Math.Max(1, entries.Max(e => e.Bytes));
        foreach (var e in entries)
        {
            Breakdown.Add(new BreakdownEntry(e.Category.Title, e.Category.Glyph, e.Bytes, Math.Max(0.04, (double)e.Bytes / max), Scanner.FormatBytes(e.Bytes), e.Items));
        }

        BreakdownTotalText = Scanner.FormatBytes(entries.Sum(e => e.Bytes));
        BreakdownSubtitle = cleaned ? "Per category, freed in the last run." : "Per category, for the items currently selected.";
        OnPropertyChanged(nameof(HasBreakdown));
    }

    public bool HasBreakdown => Breakdown.Count > 0;

    [ObservableProperty]
    private string _breakdownTotalText = string.Empty;

    [ObservableProperty]
    private string _breakdownSubtitle = "Per category, for the items currently selected.";

    /// <summary>
    /// Warning line for the confirmation dialog: names every selected item that is not a plain cache, grouped by
    /// what it costs the user, so a selection remembered from an earlier session can never come as a surprise.
    /// </summary>
    private static string? DescribeRiskyItems(IReadOnlyList<RuleItemViewModel> toClean)
    {
        var privacy = toClean.Where(i => i.Risk == RiskLevel.Privacy).ToList();
        var moderate = toClean.Where(i => i.Risk == RiskLevel.Moderate).ToList();
        if (privacy.Count == 0 && moderate.Count == 0)
        {
            return null;
        }

        static string Names(IEnumerable<RuleItemViewModel> items)
        {
            var list = items.Select(i => i.Rule.Category == RuleCategory.Browsers ? $"{i.Rule.Group}: {i.ShortName}" : i.ShortName).ToList();
            return string.Join(", ", list.Take(3)) + (list.Count > 3 ? $" and {list.Count - 3} more" : string.Empty);
        }

        var parts = new List<string>();
        if (privacy.Count > 0)
        {
            var signsOut = privacy.Any(i => i.Rule.Id.EndsWith(".cookies", StringComparison.Ordinal)) ? " (cookies: you will be signed out of websites)" : string.Empty;
            parts.Add($"{TextFormat.Count(privacy.Count, "privacy item")}{signsOut}: {Names(privacy)}");
        }

        if (moderate.Count > 0)
        {
            parts.Add($"{TextFormat.Count(moderate.Count, "item")} with side effects: {Names(moderate)}");
        }

        return "More than caches is selected - " + string.Join(". ", parts) + ". Choose 'Recommended' to keep only safe items.";
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var policy = BlockingPolicy;
        var toClean = AllItems.Where(i => i.CanClean).ToList();
        var blocked = policy == BlockingAppPolicy.Skip ? [] : AllItems.Where(IsBlockedByApplication).ToList();
        if (toClean.Count == 0 && blocked.Count == 0)
        {
            return;
        }

        var blockers = blocked
            .SelectMany(i => i.Scan!.BlockingProcesses)
            .Where(b => !b.Contains('(', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_settings.ConfirmBeforeCleaning)
        {
            var bytes = Scanner.FormatBytes(toClean.Concat(blocked).Sum(i => i.Scan!.TotalBytes));
            var detail = "Nothing in use is forced, extensions, bookmarks, passwords and settings are never touched, and every step is written to the activity log.";
            if (blocked.Count > 0 && blockers.Count > 0)
            {
                var apps = string.Join(", ", blockers.Select(ApplicationCloser.DisplayName));
                var keepClosed = KeepClosed(blocked);
                var stayClosed = blockers.Where(keepClosed.Contains).Select(ApplicationCloser.DisplayName).Distinct().ToList();
                var afterwards = !_settings.ReopenClosedApplications ? "left closed"
                    : stayClosed.Count == blockers.Count ? "left closed (its remembered tabs are forgotten)"
                    : stayClosed.Count > 0 ? $"reopened afterwards ({string.Join(", ", stayClosed)} stays closed)"
                    : "reopened afterwards";
                var blockedItems = TextFormat.Count(blocked.Count, "blocked item");
                detail = policy == BlockingAppPolicy.AutoClose
                    ? $"{apps} will be asked to close first; background processes without a window are then ended, the {blockedItems} {(blocked.Count == 1 ? "is" : "are")} cleaned and the application is {afterwards}. " + detail
                    : $"{apps} will be asked to close first ({blockedItems}); whatever stays open is skipped, the rest is {afterwards}. " + detail;
            }

            var request = new ConfirmationRequest(
                "Start cleaning?",
                $"{TextFormat.Count(toClean.Count + blocked.Count, "item")} will be cleaned and about {bytes} reclaimed. Files are deleted permanently.",
                DescribeRiskyItems(toClean.Concat(blocked).ToList()),
                detail);

            if (ConfirmHandler is null || !ConfirmHandler(request))
            {
                StatusText = "Cleaning cancelled.";
                return;
            }
        }

        _cts = new CancellationTokenSource();
        State = EngineState.Cleaning;
        ProgressPercent = 0;
        StatusText = "Cleaning...";
        foreach (var item in toClean)
        {
            item.IsWorking = true;
        }

        var options = new EngineOptions { DryRun = false, ScheduleLockedFilesForReboot = _settings.ScheduleLockedFilesForReboot };
        var closedApplications = new List<ClosedApplication>();

        try
        {
            if (blocked.Count > 0 && blockers.Count > 0)
            {
                var freed = await CloseBlockingApplicationsAsync(blocked, blockers, policy, options, closedApplications, _cts.Token);
                toClean.AddRange(freed);
            }

            if (toClean.Count == 0)
            {
                StatusText = "Nothing could be cleaned: the blocking applications are still running.";
                State = EngineState.Analyzed;
                return;
            }

            var cleaner = new Cleaner(_log, options);
            var progress = new Progress<EngineProgress>(p =>
            {
                ProgressPercent = p.Percent;
                StatusText = $"Cleaning {p.CurrentRuleName}  ({p.CompletedRules}/{p.TotalRules}) · {Scanner.FormatBytes(p.BytesSoFar)} freed";
                CurrentPath = p.CurrentPath;
            });

            var results = await cleaner.CleanAsync(toClean.Select(i => i.Scan!).ToList(), progress, _cts.Token, (index, result) => _dispatcher.BeginInvoke(() =>
            {
                toClean[index].CleanResult = result;
                toClean[index].IsWorking = false;
            }));

            for (var i = 0; i < toClean.Count; i++)
            {
                toClean[i].CleanResult = results[i];
                toClean[i].IsWorking = false;
            }

            LastFreedBytes = results.Sum(r => r.BytesFreed);
            LastFreedItems = results.Sum(r => r.DeletedFiles + r.DeletedDirectories + r.DeletedEntries);
            LastSkippedItems = results.Sum(r => r.Failures.Count) + results.Count(r => r.IsSkipped);
            var entriesRemoved = results.Sum(r => r.DeletedEntries);
            SummaryHeadline = $"{Scanner.FormatBytes(LastFreedBytes)} freed";
            SummaryDetails = $"{TextFormat.Count(LastFreedItems, "item")} removed"
                + (entriesRemoved > 0 ? $" (including {TextFormat.Count(entriesRemoved, "form entry")})" : string.Empty)
                + (LastSkippedItems > 0 ? $" · {LastSkippedItems:N0} skipped (in use or protected)" : string.Empty);
            StatusText = "Cleaning complete.";
            State = EngineState.Cleaned;
            RebuildBreakdown();

            _settings.LastRunFreedBytes = LastFreedBytes;
            _settings.LastRunItems = LastFreedItems;
            _settings.LastRunUtc = DateTime.UtcNow;
            _settings.LifetimeFreedBytes += LastFreedBytes;
            SaveSettingsSafe();
            OnPropertyChanged(nameof(LastRunText));
            OnPropertyChanged(nameof(LifetimeFreedText));
            OnPropertyChanged(nameof(LifetimeFreedShort));
            await RefreshInsightsAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cleaning cancelled. Already removed files stay removed.";
            State = EngineState.Idle;
        }
        catch (Exception ex)
        {
            _log.Error("Cleaning failed.", ex);
            StatusText = "Cleaning failed: " + ex.Message;
            State = EngineState.Idle;
        }
        finally
        {
            ReopenClosedApplications(closedApplications, KeepClosed(blocked));
            foreach (var item in toClean)
            {
                item.IsWorking = false;
            }

            CurrentPath = null;
            ProgressPercent = 0;
            _cts?.Dispose();
            _cts = null;
            RecomputeTotals();
            OnPropertyChanged(nameof(LastFreedText));
        }
    }

    /// <summary>
    /// Closes the applications that block <paramref name="blocked"/> (per <paramref name="policy"/>), re-analyzes
    /// those items and returns the ones that can be cleaned now. Items whose application stayed open keep their
    /// "blocked" result.
    /// </summary>
    private async Task<List<RuleItemViewModel>> CloseBlockingApplicationsAsync(
        List<RuleItemViewModel> blocked,
        List<string> blockers,
        BlockingAppPolicy policy,
        EngineOptions options,
        List<ClosedApplication> closedApplications,
        CancellationToken cancellationToken)
    {
        ProgressIndeterminate = true;
        try
        {
            var closer = new ApplicationCloser(_log);
            var outcome = await closer.CloseAsync(blockers, policy, s => _dispatcher.BeginInvoke(() => StatusText = s), cancellationToken);
            closedApplications.AddRange(outcome.Closed);
            closedApplications.AddRange(outcome.WindowsClosed);

            foreach (var note in outcome.Notes)
            {
                _log.Info(note);
            }

            if (outcome.Closed.Count == 0)
            {
                StatusText = $"{string.Join(", ", outcome.StillRunning.Select(ApplicationCloser.DisplayName))} stayed open - its items are skipped.";
                return [];
            }

            // The blocked scans were taken while the application ran: refresh them before deleting anything.
            StatusText = $"Re-analyzing {TextFormat.Count(blocked.Count, "item")}...";
            foreach (var item in blocked)
            {
                item.IsWorking = true;
            }

            var scanner = new Scanner(_log, options);
            var results = await scanner.ScanAsync(blocked.Select(i => i.Rule).ToList(), null, cancellationToken);
            var freed = new List<RuleItemViewModel>();
            for (var i = 0; i < blocked.Count; i++)
            {
                blocked[i].Scan = results[i];
                if (results[i].IsSkipped)
                {
                    blocked[i].IsWorking = false;
                }
                else
                {
                    freed.Add(blocked[i]);
                }
            }

            _log.Info($"{freed.Count} of {TextFormat.Count(blocked.Count, "blocked item")} became available after closing {string.Join(", ", outcome.Closed.Select(c => ApplicationCloser.DisplayName(c.ProcessName)))}.");
            return freed;
        }
        finally
        {
            ProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// Process names that should stay closed after the cleanup: an editor whose saved tabs were just forgotten would
    /// only reopen with an empty window.
    /// </summary>
    private static HashSet<string> KeepClosed(IEnumerable<RuleItemViewModel> blocked)
        => new(blocked.Where(i => !i.Rule.RelaunchClosedApplications).SelectMany(i => i.Rule.ConflictingProcesses), StringComparer.OrdinalIgnoreCase);

    /// <summary>Brings back what PCleaner closed for the cleanup - with the user's normal rights, never elevated.</summary>
    private void ReopenClosedApplications(List<ClosedApplication> closedApplications, HashSet<string> keepClosed)
    {
        if (closedApplications.Count == 0)
        {
            return;
        }

        if (!_settings.ReopenClosedApplications)
        {
            _log.Info($"{string.Join(", ", closedApplications.Select(c => ApplicationCloser.DisplayName(c.ProcessName)))} closed for the cleanup and left closed (setting).");
            return;
        }

        var stayClosed = closedApplications.Where(c => keepClosed.Contains(c.ProcessName)).ToList();
        if (stayClosed.Count > 0)
        {
            _log.Info($"{string.Join(", ", stayClosed.Select(c => ApplicationCloser.DisplayName(c.ProcessName)).Distinct())} left closed - its remembered tabs and files were forgotten, so reopening would only show an empty window.");
        }

        var started = new ApplicationCloser(_log).Relaunch(closedApplications.Where(c => !keepClosed.Contains(c.ProcessName)));
        if (started.Count > 0)
        {
            StatusText = $"{StatusText} {string.Join(", ", started)} reopened.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Items of the page currently shown (selection commands operate on the visible page only).</summary>
    private IEnumerable<RuleItemViewModel> VisibleItems => IsRulesPage ? VisibleCategories.SelectMany(c => c.AllItems) : AllItems;

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var item in VisibleItems)
        {
            item.IsSelected = DefaultSelection(item.Rule);
        }

        StatusText = $"{PageLabel}: recommended selection applied ({VisibleItems.Count(i => i.IsSelected)} of {VisibleItems.Count()} items).";
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var item in VisibleItems)
        {
            item.IsSelected = IsElevated || !item.RequiresAdministrator;
        }

        StatusText = $"{PageLabel}: everything selected, including privacy and side-effect items. Review before cleaning.";
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in VisibleItems)
        {
            item.IsSelected = false;
        }

        StatusText = $"{PageLabel}: selection cleared.";
    }

    /// <summary>
    /// Deselects items that cannot run right now: blocked by a running application or by missing rights.
    /// Items that have not been analysed yet are probed live, so a browser that is still running is skipped as well.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasBlockedSelection))]
    private void DeselectBlocked()
    {
        var count = 0;
        foreach (var item in VisibleItems.Where(i => i.IsSelected && IsCurrentlyBlocked(i)))
        {
            item.IsSelected = false;
            count++;
        }

        StatusText = count == 0
            ? "Nothing is blocked any more - run Analyze to refresh the results."
            : $"{TextFormat.Count(count, "blocked item")} deselected. {(count == 1 ? "It" : "They")} can be selected again once the application is closed.";
    }

    private bool IsCurrentlyBlocked(RuleItemViewModel item)
    {
        if (item.RequiresAdministrator && !IsElevated)
        {
            return true;
        }

        if (item.Scan?.Skip == SkipReason.RequiresAdministrator)
        {
            return true;
        }

        return ProcessMonitor.GetBlockers(item.Rule).Count > 0;
    }

    public bool HasBlockedSelection => VisibleItems.Any(i => i.IsSelected && i.IsBlocked);

    private string PageLabel => CurrentSection switch
    {
        Section.Windows => "Windows",
        Section.Applications => "Applications",
        Section.Browsers => "Web browsers",
        _ => "All pages",
    };

    /// <summary>Compact selection summary for the toolbar, e.g. "27 of 62 selected · 23.9 MB".</summary>
    public string SelectionSummary
    {
        get
        {
            var items = VisibleItems.ToList();
            var selected = items.Count(i => i.IsSelected);
            var bytes = items.Where(i => i.IsSelected && i.Scan is { IsSkipped: false }).Sum(i => i.Scan!.TotalBytes);
            var text = $"{selected} of {items.Count} selected";
            return bytes > 0 ? $"{text} · {Scanner.FormatBytes(bytes)}" : text;
        }
    }

    [RelayCommand]
    private void RestartElevated()
    {
        if (IsElevated)
        {
            return;
        }

        SaveSettingsSafe();
        if (Elevation.TryRestartElevated("--elevated"))
        {
            Application.Current.Shutdown();
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Could not open the log folder: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task CloseBrowserAsync(RuleGroupViewModel? group)
    {
        if (group is null || IsBusy)
        {
            return;
        }

        var processes = group.Items
            .Where(i => i.Scan?.Skip == SkipReason.ApplicationRunning)
            .SelectMany(i => i.Scan!.BlockingProcesses)
            .Where(b => !b.Contains('(', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (processes.Count == 0)
        {
            processes = group.Items.SelectMany(i => i.Rule.ConflictingProcesses).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (processes.Count == 0)
        {
            return;
        }

        // The banner button never ends a process unless automatic closing (administrator mode) is enabled.
        var policy = BlockingPolicy == BlockingAppPolicy.AutoClose ? BlockingAppPolicy.AutoClose : BlockingAppPolicy.RequestClose;
        var closer = new ApplicationCloser(_log);
        var outcome = await closer.CloseAsync(processes, policy, s => _dispatcher.BeginInvoke(() => StatusText = s), CancellationToken.None);

        if (outcome.AllClosed)
        {
            StatusText = $"{group.Name} closed. Refreshing the analysis...";
            if (CanAnalyze)
            {
                await AnalyzeAsync();
            }

            return;
        }

        var reason = outcome.Notes.Count > 0 ? outcome.Notes[0] : null;
        StatusText = policy == BlockingAppPolicy.AutoClose
            ? $"{group.Name} is still running. {reason ?? "Close it from its window or tray icon and analyze again."}"
            : $"{group.Name} is still running (background processes such as a browser's 'startup boost' keep the profile open). Close it from its tray icon, or enable automatic closing in Settings (administrator mode).";
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
    }

    [RelayCommand]
    private void OpenReference(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var start = url.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return;
        }

        var link = url[start..].Split(' ', ';', ')')[0];
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Could not open link: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ helpers

    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level == LogLevel.Debug)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            LogEntries.Add(entry);
            while (LogEntries.Count > 2000)
            {
                LogEntries.RemoveAt(0);
            }
        });
    }

    public void SaveSettingsSafe()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            _log.Warn("Could not save settings: " + ex.Message);
        }
    }

    /// <summary>Records how a confirmation prompt was answered.</summary>
    public void LogConfirmation(string details) => _log.Info("Confirmation prompt: " + details);

    public void Shutdown()
    {
        _cts?.Cancel();
        SaveSettingsSafe();
        _log.Info("PCleaner closed.");
        Dispose();
    }

    public void Dispose()
    {
        _memoryLog.EntryAdded -= OnLogEntry;
        _cts?.Dispose();
        _cts = null;
        _fileLog.Dispose();
    }
}



