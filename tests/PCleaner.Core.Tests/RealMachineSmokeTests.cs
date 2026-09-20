using PCleaner.Core.Browsers;
using PCleaner.Core.Engine;
using PCleaner.Core.Model;
using PCleaner.Core.Rules;
using PCleaner.Core.Windows;
using Xunit.Abstractions;

namespace PCleaner.Core.Tests;

/// <summary>
/// Runs the complete rule set of the current machine in preview mode (nothing is deleted) and validates that every
/// rule is well formed. Also prints a summary so the output can be inspected.
/// </summary>
public sealed class RealMachineSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RealMachineSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RealMachineFact]
    [Trait("Category", "RealMachine")]
    public async Task All_rules_scan_without_errors_and_never_target_protected_roots()
    {
        var context = RuleContext.FromCurrentMachine(Elevation.IsElevated);
        var windowsRules = new WindowsRuleProvider(context).GetRules();
        var browsers = new BrowserDetector(BrowserCatalog.GetDefinitions(context.LocalAppData, context.RoamingAppData)).Detect();
        var browserRules = new BrowserRuleProvider(browsers).GetRules();
        var rules = windowsRules.Concat(browserRules).ToList();

        Assert.NotEmpty(rules);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Name), rule.Id);
            Assert.False(string.IsNullOrWhiteSpace(rule.Description), rule.Id);
            foreach (var target in rule.Targets)
            {
                Assert.True(Path.IsPathFullyQualified(target.Path), $"{rule.Id}: {target.Path}");
                var reason = SafetyGuard.ValidateTargetRoot(target);
                Assert.True(reason is null, $"{rule.Id}: {target.Path} -> {reason}");
            }
        }

        var scanner = new Scanner(options: new EngineOptions { DryRun = true });
        var results = await scanner.ScanAsync(rules, null, CancellationToken.None);

        Assert.Equal(rules.Count, results.Count);
        Assert.DoesNotContain(results, r => r.Skip == SkipReason.Error);

        _output.WriteLine($"Elevated: {Elevation.IsElevated}");
        _output.WriteLine($"Browsers: {string.Join(", ", browsers.Select(b => $"{b.Definition.Name} [{b.Profiles.Count}]"))}");
        foreach (var r in results.OrderBy(r => r.Rule.Category).ThenBy(r => r.Rule.Group).ThenBy(r => r.Rule.Name))
        {
            _output.WriteLine($"{r.Rule.Category,-14} {r.Rule.Risk,-9} {Scanner.FormatBytes(r.TotalBytes),10} {r.FileCount,7} files  {r.Skip,-22} {r.Rule.Name}");
        }

        // Nothing selected for deletion may be a protected file or live in an extension folder.
        foreach (var result in results)
        {
            foreach (var item in result.Items.Where(i => !i.IsDirectory))
            {
                Assert.False(SafetyGuard.IsNeverDeleteFileName(Path.GetFileName(item.Path)), item.Path);
                Assert.DoesNotContain(@"\Extensions\", item.Path, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"\Local Extension Settings\", item.Path, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"\Storage\ext\", item.Path.Replace(@"\def\Cache", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(@"\def\Code Cache", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(@"\def\GPUCache", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(@"\def\DawnWebGPUCache", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(@"\def\DawnGraphiteCache", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace(@"\def\DawnCache", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}