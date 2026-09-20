using PCleaner.Core.Browsers;
using PCleaner.Core.Rules;

namespace PCleaner.Core.Tests;

public sealed class BrowserDetectionTests
{
    [Fact]
    public void Chromium_profiles_are_read_from_local_state_and_disk()
    {
        using var tree = new TempTree();
        var userData = tree.Dir(@"Google\Chrome\User Data");
        tree.File(@"Google\Chrome\User Data\Local State", """
            {"profile":{"info_cache":{"Default":{"name":"Personal","gaia_name":"Someone"},"Profile 1":{"name":"","gaia_name":"Work Account"}},"profiles_order":["Default","Profile 1"]}}
            """);
        tree.File(@"Google\Chrome\User Data\Default\Preferences", "{}");
        tree.File(@"Google\Chrome\User Data\Profile 1\Preferences", "{}");
        tree.File(@"Google\Chrome\User Data\Profile 2\Secure Preferences", "{}"); // missing from Local State
        tree.File(@"Google\Chrome\User Data\System Profile\Preferences", "{}");
        tree.Dir(@"Google\Chrome\User Data\ShaderCache"); // not a profile

        var definition = new BrowserDefinition
        {
            Id = "chrome",
            Name = "Google Chrome",
            Family = BrowserFamily.Chromium,
            ProcessNames = ["chrome"],
            DataRoots = [userData],
        };

        var detected = new BrowserDetector([definition]).Detect();

        Assert.Single(detected);
        var profiles = detected[0].Profiles;
        Assert.Equal(4, profiles.Count);
        Assert.Equal("Personal", profiles.Single(p => p.DirectoryName == "Default").DisplayName);
        Assert.Equal("Work Account", profiles.Single(p => p.DirectoryName == "Profile 1").DisplayName);
        Assert.Equal("Profile 2", profiles.Single(p => p.DirectoryName == "Profile 2").DisplayName);
        Assert.True(profiles.Single(p => p.DirectoryName == "System Profile").IsSystemOrGuest);
        Assert.All(profiles, p => Assert.Equal(p.ProfilePath, p.CachePath));
    }

    [Fact]
    public void Opera_layout_uses_root_as_profile_and_separate_cache_root()
    {
        using var tree = new TempTree();
        var roamingRoot = tree.Dir(@"Roaming\Opera Software\Opera Stable");
        var localRoot = tree.Dir(@"Local\Opera Software\Opera Stable");
        tree.File(@"Roaming\Opera Software\Opera Stable\Preferences", "{}");
        tree.File(@"Roaming\Opera Software\Opera Stable\_side_profiles\whatsapp\Preferences", "{}");

        var definition = new BrowserDefinition
        {
            Id = "opera",
            Name = "Opera",
            Family = BrowserFamily.Chromium,
            ProcessNames = ["opera"],
            DataRoots = [roamingRoot],
            SeparateCacheRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [roamingRoot] = localRoot },
        };

        var detected = new BrowserDetector([definition]).Detect();

        var profiles = detected.Single().Profiles;
        Assert.Equal(2, profiles.Count);
        var main = profiles.Single(p => p.DirectoryName == "(root)");
        Assert.Equal(roamingRoot, main.ProfilePath);
        Assert.Equal(localRoot, main.CachePath);
        var side = profiles.Single(p => p.DirectoryName != "(root)");
        Assert.Equal(Path.Combine(localRoot, "_side_profiles", "whatsapp"), side.CachePath);
    }

    [Fact]
    public void Gecko_profiles_ini_is_parsed_including_absolute_paths()
    {
        using var tree = new TempTree();
        var root = tree.Dir(@"Roaming\Mozilla\Firefox");
        var localProfiles = tree.Dir(@"Local\Mozilla\Firefox\Profiles");
        var absolute = tree.Dir(@"Elsewhere\custom.profile");
        tree.File(@"Roaming\Mozilla\Firefox\Profiles\abcd1234.default-release\prefs.js", "// prefs");
        tree.File(@"Elsewhere\custom.profile\prefs.js", "// prefs");
        tree.File(@"Roaming\Mozilla\Firefox\Profiles\orphan.profile\prefs.js", "// not in ini");
        tree.File(@"Roaming\Mozilla\Firefox\profiles.ini", $"""
            [Install308046B0AF4A39CB]
            Default=Profiles/abcd1234.default-release
            Locked=1

            [Profile1]
            Name=custom
            IsRelative=0
            Path={absolute.Replace('\\', '/')}

            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/abcd1234.default-release
            Default=1

            [General]
            StartWithLastProfile=1
            Version=2
            """);

        var definition = new BrowserDefinition
        {
            Id = "firefox",
            Name = "Firefox",
            Family = BrowserFamily.Gecko,
            ProcessNames = ["firefox"],
            DataRoots = [root],
            LocalProfilesRoot = localProfiles,
        };

        var profiles = new BrowserDetector([definition]).Detect().Single().Profiles;

        Assert.Equal(3, profiles.Count);
        var main = profiles.Single(p => p.DisplayName == "default-release");
        Assert.Equal(Path.Combine(root, "Profiles", "abcd1234.default-release"), main.ProfilePath);
        Assert.Equal(Path.Combine(localProfiles, "abcd1234.default-release"), main.CachePath);

        var custom = profiles.Single(p => p.DisplayName == "custom");
        Assert.Equal(absolute, custom.ProfilePath);
        Assert.Equal(absolute, custom.CachePath); // absolute profiles keep cache2 inside

        var orphan = profiles.Single(p => p.DisplayName == "orphan.profile");
        Assert.Equal(Path.Combine(localProfiles, "orphan.profile"), orphan.CachePath);
    }

    [Fact]
    public void Undetected_browsers_are_not_reported()
    {
        using var tree = new TempTree();
        var definition = new BrowserDefinition
        {
            Id = "x",
            Name = "X",
            Family = BrowserFamily.Chromium,
            ProcessNames = ["x"],
            DataRoots = [Path.Combine(tree.Root, "missing")],
        };

        Assert.Empty(new BrowserDetector([definition]).Detect());
    }

    [Fact]
    public void Catalog_covers_major_browsers()
    {
        var defs = BrowserCatalog.GetDefinitions(@"C:\Users\x\AppData\Local", @"C:\Users\x\AppData\Roaming");
        var ids = defs.Select(d => d.Id).ToHashSet();
        Assert.Contains("chrome", ids);
        Assert.Contains("edge", ids);
        Assert.Contains("brave", ids);
        Assert.Contains("opera", ids);
        Assert.Contains("opera-gx", ids);
        Assert.Contains("vivaldi", ids);
        Assert.Contains("firefox", ids);
        Assert.Equal(defs.Count, ids.Count); // unique ids
        Assert.All(defs, d => Assert.NotEmpty(d.ProcessNames));
        Assert.All(defs.Where(d => d.Family == BrowserFamily.Gecko), d => Assert.NotNull(d.LocalProfilesRoot));
    }

    [Fact]
    public void PathExpander_expands_wildcards()
    {
        using var tree = new TempTree();
        tree.Dir(@"Packages\App1\TempState");
        tree.Dir(@"Packages\App2\TempState");
        tree.Dir(@"Packages\App3\Other");

        var result = PathExpander.ExpandDirectories(Path.Combine(tree.Root, @"Packages\*\TempState"));

        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.EndsWith("TempState", p, StringComparison.Ordinal));
        Assert.Empty(PathExpander.ExpandDirectories(Path.Combine(tree.Root, @"Nope\*\TempState")));
    }
}