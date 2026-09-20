using PCleaner.Core.Privacy;

namespace PCleaner.App.ViewModels;

/// <summary>
/// One section of the Privacy page: every finding that belongs to the same scope (a browser profile, this PC,
/// Windows), listed once under a single header instead of repeating the scope on every card.
/// </summary>
public sealed class InsightGroupViewModel
{
    public InsightGroupViewModel(string scope, InsightScopeKind kind, IReadOnlyList<PrivacyInsight> items)
    {
        Scope = scope;
        Kind = kind;
        Items = items;
    }

    public string Scope { get; }

    public InsightScopeKind Kind { get; }

    public IReadOnlyList<PrivacyInsight> Items { get; }

    /// <summary>Header icon: browser globe, this PC, Windows settings.</summary>
    public string Glyph => Kind switch
    {
        InsightScopeKind.Browser => "\uE774",
        InsightScopeKind.Device => "\uE7F8",
        _ => "\uE713",
    };

    /// <summary>What the section is about, in one line under the scope name.</summary>
    public string Subtitle => Kind switch
    {
        InsightScopeKind.Browser => "Sign-ins, sync and what websites stored in this profile.",
        InsightScopeKind.Device => "Lists of recently opened files kept by Windows and installed apps.",
        _ => "System-wide settings that identify you to apps.",
    };

    /// <summary>"3 findings · 2 cleanable here" - or "all clear" when nothing is left to do.</summary>
    public string CountText
    {
        get
        {
            var cleanable = Items.Count(i => i.RelatedRuleIds.Count > 0);
            if (Items.All(i => i.Level == InsightLevel.Clear))
            {
                return "all clear";
            }

            var findings = Items.Count == 1 ? "1 finding" : $"{Items.Count} findings";
            return cleanable > 0 ? $"{findings} · {cleanable} cleanable here" : findings;
        }
    }

    public bool IsAllClear => Items.All(i => i.Level == InsightLevel.Clear);
}