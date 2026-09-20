using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using PCleaner.Core.Engine;
using PCleaner.Core.Model;
using PCleaner.Core.Rules;
using PCleaner.Core.Windows;

namespace PCleaner.App.ViewModels;

/// <summary>A group of rules (e.g. one browser profile, or "Temporary files & caches") with a tri-state checkbox.</summary>
public sealed partial class RuleGroupViewModel : ObservableObject
{
    private bool _updating;

    public RuleGroupViewModel(string name, RuleCategory category, IEnumerable<RuleItemViewModel> items)
    {
        Name = name;
        Category = category;
        Items = new ObservableCollection<RuleItemViewModel>(items);
        var index = 0;
        foreach (var item in Items)
        {
            item.SourceIndex = index++;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(RuleItemViewModel.IsSelected))
                {
                    RefreshSelection();
                }
                else if (e.PropertyName is nameof(RuleItemViewModel.Scan) or nameof(RuleItemViewModel.CleanResult))
                {
                    OnPropertyChanged(nameof(TotalBytes));
                    OnPropertyChanged(nameof(SizeText));
                    OnPropertyChanged(nameof(HasBlockedItems));
                    OnPropertyChanged(nameof(BlockingProcesses));
                    OnPropertyChanged(nameof(BlockingLabel));
                }
            };
        }

        // What the page shows: the rule-set order, except that locations missing on this PC sink to the end of the
        // group once the analysis knows they are missing (live sorting re-orders as scan results arrive).
        var view = new ListCollectionView(Items) { IsLiveSorting = true };
        view.SortDescriptions.Add(new SortDescription(nameof(RuleItemViewModel.IsNotPresent), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(RuleItemViewModel.SourceIndex), ListSortDirection.Ascending));
        view.LiveSortingProperties.Add(nameof(RuleItemViewModel.IsNotPresent));
        ItemsView = view;

        // A browser with a single profile: "Profile 1 · Cache" says nothing the header does not, so rows show "Cache".
        var profiles = Items.Select(i => i.ProfileLabel).Where(p => p is not null).Distinct(StringComparer.Ordinal).Count();
        if (category == RuleCategory.Browsers && profiles <= 1)
        {
            foreach (var item in Items)
            {
                item.SetHideProfilePrefix(true);
            }
        }

        RefreshSelection();
    }

    public string Name { get; }

    public RuleCategory Category { get; }

    public ObservableCollection<RuleItemViewModel> Items { get; }

    /// <summary>Sorted, live view of <see cref="Items"/> for the page (present rows first, then the rule-set order).</summary>
    public ICollectionView ItemsView { get; }

    /// <summary>Icon for the group header (browser globe, driver chip, ...), keyed by the shared group names.</summary>
    public string Glyph => Category switch
    {
        RuleCategory.Browsers => "\uE774",
        _ when Is(RuleOrder.RecentFiles) => "\uE81C",
        _ when Is(RuleOrder.RecycleBin) => "\uE74D",
        _ when Is(RuleOrder.GraphicsDrivers) => "\uE7F4",
        _ when Is(RuleOrder.MicrosoftApps) => "\uE71D",
        _ when Is(RuleOrder.DiskCleanup) => "\uE7F8",
        _ when Is(RuleOrder.Privacy) => "\uE72E",
        _ when Is(RuleOrder.Advanced) => "\uE90F",
        _ when Is(RuleOrder.SystemFiles) => "\uE7F8",
        RuleCategory.Applications => "\uE71D",
        _ => "\uE8B7",
    };

    private bool Is(string group) => string.Equals(Name, group, StringComparison.OrdinalIgnoreCase);

    public int SelectedCount => Items.Count(i => i.IsSelected);

    public string CountText => $"{SelectedCount} / {Items.Count}";

    [ObservableProperty]
    private bool? _isSelected;

    [ObservableProperty]
    private bool _isExpanded = true;

    public long TotalBytes => Items.Where(i => i.IsSelected).Sum(i => i.Bytes);

    public string SizeText => Items.Any(i => i.HasScan) ? Scanner.FormatBytes(TotalBytes) : string.Empty;

    public bool HasBlockedItems => Items.Any(i => i.Scan?.Skip == SkipReason.ApplicationRunning);

    public IReadOnlyList<string> BlockingProcesses => Items
        .Where(i => i.Scan is not null)
        .SelectMany(i => i.Scan!.BlockingProcesses)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// What to name in the "is running" banner: the browser itself for browser groups ("Brave" rather than the
    /// raw process name "brave" or a lock-file marker), otherwise the running applications by their plain names.
    /// </summary>
    public string BlockingLabel => Category == RuleCategory.Browsers
        ? Name
        : string.Join(", ", BlockingProcesses.Select(ApplicationCloser.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase));

    partial void OnIsSelectedChanged(bool? value)
    {
        if (_updating || value is null)
        {
            return;
        }

        _updating = true;
        try
        {
            foreach (var item in Items)
            {
                item.IsSelected = value.Value;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshSelection()
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            var selected = Items.Count(i => i.IsSelected);
            IsSelected = selected == 0 ? false : selected == Items.Count ? true : null;
            OnPropertyChanged(nameof(TotalBytes));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(CountText));
        }
        finally
        {
            _updating = false;
        }
    }
}

/// <summary>A top level category (Windows system / Windows user / Applications / Browsers).</summary>
public sealed partial class CategoryViewModel : ObservableObject
{
    public CategoryViewModel(RuleCategory category, string title, string subtitle, string glyph, IEnumerable<RuleGroupViewModel> groups)
    {
        Category = category;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
        Groups = new ObservableCollection<RuleGroupViewModel>(groups);
        foreach (var group in Groups)
        {
            group.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(RuleGroupViewModel.TotalBytes) or nameof(RuleGroupViewModel.IsSelected))
                {
                    OnPropertyChanged(nameof(TotalBytes));
                    OnPropertyChanged(nameof(SizeText));
                    OnPropertyChanged(nameof(SelectedCount));
                }
            };
        }
    }

    public RuleCategory Category { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Glyph { get; }

    public ObservableCollection<RuleGroupViewModel> Groups { get; }

    public IEnumerable<RuleItemViewModel> AllItems => Groups.SelectMany(g => g.Items);

    public int SelectedCount => AllItems.Count(i => i.IsSelected);

    public int ItemCount => AllItems.Count();

    public long TotalBytes => Groups.Sum(g => g.TotalBytes);

    public string SizeText => AllItems.Any(i => i.HasScan) ? Scanner.FormatBytes(TotalBytes) : string.Empty;

    public bool IsEmpty => Groups.Count == 0;
}