using PCleaner.Core.Browsers;
using PCleaner.Core.Model;
using PCleaner.Core.Rules;
using PCleaner.Core.Windows;

namespace PCleaner.Core.Tests;

/// <summary>The pages must read in one fixed, explainable order - never in the order the code happened to yield rules.</summary>
public sealed class RuleOrderTests
{
    private static CleanupRule Rule(string id, RuleCategory category, string group, RiskLevel risk = RiskLevel.Safe, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Description = "test",
        Category = category,
        Group = group,
        Risk = risk,
    };

    [Fact]
    public void Groups_follow_the_fixed_sequence_regardless_of_source_order()
    {
        var rules = new[]
        {
            Rule("a", RuleCategory.WindowsUser, RuleOrder.RecycleBin, RiskLevel.Moderate),
            Rule("b", RuleCategory.WindowsUser, RuleOrder.RecentFiles, RiskLevel.Privacy),
            Rule("c", RuleCategory.WindowsUser, RuleOrder.TempAndCaches),
            Rule("d", RuleCategory.WindowsSystem, RuleOrder.Advanced, RiskLevel.Moderate),
            Rule("e", RuleCategory.WindowsSystem, RuleOrder.Privacy, RiskLevel.Privacy),
            Rule("f", RuleCategory.WindowsSystem, RuleOrder.DiskCleanup),
            Rule("g", RuleCategory.WindowsSystem, RuleOrder.SystemFiles),
            Rule("h", RuleCategory.Applications, RuleOrder.RecentFiles, RiskLevel.Privacy),
            Rule("i", RuleCategory.Applications, RuleOrder.MicrosoftApps),
            Rule("j", RuleCategory.Applications, RuleOrder.GraphicsDrivers),
        };

        var sorted = RuleOrder.Sort(rules.Reverse()).Select(r => r.Id).ToArray();

        Assert.Equal(["c", "b", "a", "g", "f", "e", "d", "j", "i", "h"], sorted);
    }

    [Fact]
    public void Items_inside_a_group_sort_by_risk_then_name()
    {
        var rules = new[]
        {
            Rule("z", RuleCategory.WindowsUser, RuleOrder.TempAndCaches, RiskLevel.Privacy, "Zeta"),
            Rule("m", RuleCategory.WindowsUser, RuleOrder.TempAndCaches, RiskLevel.Moderate, "Mu"),
            Rule("b", RuleCategory.WindowsUser, RuleOrder.TempAndCaches, RiskLevel.Safe, "beta"),
            Rule("a", RuleCategory.WindowsUser, RuleOrder.TempAndCaches, RiskLevel.Safe, "Alpha"),
        };

        Assert.Equal(["Alpha", "beta", "Mu", "Zeta"], RuleOrder.Sort(rules).Select(r => r.Name).ToArray());
    }

    [Fact]
    public void An_explicit_order_hint_leads_the_group_ahead_of_the_automatic_order()
    {
        var general = Rule("general", RuleCategory.WindowsUser, RuleOrder.RecentFiles, RiskLevel.Privacy, "Windows lists");
        var rules = new[]
        {
            Rule("aaa", RuleCategory.WindowsUser, RuleOrder.RecentFiles, RiskLevel.Safe, "Aaa safe item"),
            new CleanupRule { Id = general.Id, Name = general.Name, Description = "t", Category = general.Category, Group = general.Group, Risk = general.Risk, Order = 1 },
            Rule("editor", RuleCategory.WindowsUser, RuleOrder.RecentFiles, RiskLevel.Moderate, "Editor tabs"),
        };

        Assert.Equal(["Windows lists", "Aaa safe item", "Editor tabs"], RuleOrder.Sort(rules).Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Browser_rules_keep_profiles_together_and_follow_the_clear_browsing_data_sequence()
    {
        var rules = new[]
        {
            Rule("browser.brave.work.sitedata", RuleCategory.Browsers, "Brave", RiskLevel.Moderate, "Work: Website storage"),
            Rule("browser.brave.default.cookies", RuleCategory.Browsers, "Brave", RiskLevel.Privacy, "Personal: Cookies"),
            Rule("browser.brave.default.cache", RuleCategory.Browsers, "Brave", RiskLevel.Safe, "Personal: Cache"),
            Rule("browser.brave.work.cache", RuleCategory.Browsers, "Brave", RiskLevel.Safe, "Work: Cache"),
            Rule("browser.brave.user-data.root", RuleCategory.Browsers, "Brave", RiskLevel.Safe, "Brave: Shader cache"),
            Rule("browser.edge.default.cache", RuleCategory.Browsers, "Microsoft Edge", RiskLevel.Safe, "Cache"),
            Rule("browser.brave.default.history", RuleCategory.Browsers, "Brave", RiskLevel.Privacy, "Personal: History"),
        };

        var sorted = RuleOrder.Sort(rules).Select(r => r.Id).ToArray();

        // The browser-wide rule first; then the profiles in the order the provider yielded them (here "work" came
        // first), each following the clear-browsing-data sequence; then the next browser.
        Assert.Equal(
        [
            "browser.brave.user-data.root",
            "browser.brave.work.cache", "browser.brave.work.sitedata",
            "browser.brave.default.cache", "browser.brave.default.history", "browser.brave.default.cookies",
            "browser.edge.default.cache",
        ], sorted);
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Real_rule_set_has_no_stray_groups_and_every_group_name_is_shared()
    {
        var context = RuleContext.FromCurrentMachine(Elevation.IsElevated);
        var browsers = new BrowserDetector(BrowserCatalog.GetDefinitions(context.LocalAppData, context.RoamingAppData)).Detect();
        var rules = new WindowsRuleProvider(context).GetRules().Concat(new BrowserRuleProvider(browsers).GetRules()).ToList();

        var known = new[]
        {
            RuleOrder.TempAndCaches, RuleOrder.RecycleBin, RuleOrder.SystemFiles, RuleOrder.DiskCleanup, RuleOrder.RecentFiles,
            RuleOrder.Privacy, RuleOrder.Advanced, RuleOrder.GraphicsDrivers, RuleOrder.MicrosoftApps,
        };
        foreach (var rule in rules.Where(r => r.Category != RuleCategory.Browsers))
        {
            Assert.Contains(rule.Group, known);
        }

        // Browser groups are browser names; every browser rule of one browser shares the group.
        foreach (var browser in browsers)
        {
            var groups = rules.Where(r => r.Id.StartsWith($"browser.{browser.Definition.Id}.", StringComparison.Ordinal)).Select(r => r.Group).Distinct().ToList();
            Assert.Equal([browser.Definition.Name], groups);
        }

        // Without an explicit hint, a sorted group never puts a Privacy item before a Safe item.
        var sorted = RuleOrder.Sort(rules);
        foreach (var group in sorted.Where(r => r.Category != RuleCategory.Browsers && r.Order is null).GroupBy(r => (r.Category, r.Group)))
        {
            var risks = group.Select(r => (int)r.Risk).ToList();
            Assert.Equal(risks.Order(), risks);
        }

        // Hinted rules come first in their group, in hint order.
        foreach (var group in sorted.Where(r => r.Category != RuleCategory.Browsers).GroupBy(r => (r.Category, r.Group)))
        {
            var hinted = group.TakeWhile(r => r.Order is not null).Select(r => r.Order!.Value).ToList();
            Assert.Equal(group.Count(r => r.Order is not null), hinted.Count);
            Assert.Equal(hinted.Order(), hinted);
        }
    }
}