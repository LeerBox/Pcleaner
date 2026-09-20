using PCleaner.Core.Browsers;
using PCleaner.Core.Model;
using PCleaner.Core.Rules;
using PCleaner.Core.Windows;

namespace PCleaner.Core.Tests;

/// <summary>
/// Every option the user sees must have a short name and a one- or two-sentence description in plain language.
/// Technical detail (paths, table names, APIs) belongs in the details pane, not in the text.
/// </summary>
public sealed class RuleTextTests
{
    private static List<CleanupRule> AllRules()
    {
        var context = RuleContext.FromCurrentMachine(Elevation.IsElevated);
        var browsers = new BrowserDetector(BrowserCatalog.GetDefinitions(context.LocalAppData, context.RoamingAppData)).Detect();
        return new WindowsRuleProvider(context).GetRules().Concat(new BrowserRuleProvider(browsers).GetRules()).ToList();
    }

    /// <summary>The part of the name after the "Browser — Profile:" prefix, or the whole name for Windows rules.</summary>
    private static string ShortName(CleanupRule rule)
    {
        var colon = rule.Name.LastIndexOf(": ", StringComparison.Ordinal);
        return rule.Category == RuleCategory.Browsers && colon > 0 ? rule.Name[(colon + 2)..] : rule.Name;
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Names_are_short_and_free_of_technical_noise()
    {
        var rules = AllRules();
        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            var name = ShortName(rule);
            Assert.True(name.Length is >= 5 and <= 30, $"{rule.Id}: '{name}' has {name.Length} characters");
            Assert.DoesNotContain('(', name);
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain('%', name);
            Assert.False(name.EndsWith('.'), rule.Id);
        }
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Descriptions_are_one_or_two_plain_sentences()
    {
        foreach (var rule in AllRules())
        {
            var text = rule.Description;
            Assert.True(text.Length is >= 30 and <= 170, $"{rule.Id}: description has {text.Length} characters");
            Assert.True(text.EndsWith('.'), $"{rule.Id}: '{text}'");
            var sentences = text.Split(". ", StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(sentences <= 3, $"{rule.Id}: {sentences} sentences");
            Assert.DoesNotContain("%LOCALAPPDATA%", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".sqlite", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".db", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public void Every_moderate_risk_disk_cleanup_handler_has_a_curated_text()
    {
        var handlers = new WindowsRuleProvider(RuleContext.FromCurrentMachine(Elevation.IsElevated)).GetRules()
            .Where(r => r.Action == RuleAction.DiskCleanupHandler)
            .ToList();
        Assert.NotEmpty(handlers);
        foreach (var rule in handlers.Where(r => DiskCleanupHandlers.ModerateRisk.Contains(r.ActionArgument!)))
        {
            // Curated texts spell out the side effect themselves instead of appending a generic warning.
            Assert.DoesNotContain("review before enabling", rule.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RiskLevel.Moderate, rule.Risk);
        }

        Assert.DoesNotContain(handlers, r => r.Description == "Built-in Windows Disk Cleanup handler.");
    }

    [Theory]
    [InlineData("Files created by Windows", "Files created by Windows")]
    [InlineData("First sentence.  Second sentence.", "First sentence.")]
    [InlineData("   ", "fallback")]
    [InlineData(null, "fallback")]
    public void First_sentence_is_extracted_with_fallback(string? input, string expected)
    {
        Assert.Equal(expected, WindowsRuleProvider.FirstSentence(input, "fallback"));
    }
}