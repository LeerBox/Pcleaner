using System.Text.Json;
using PCleaner.Core.Logging;

namespace PCleaner.Core.Browsers;

/// <summary>
/// Finds installed browsers and their profiles. Detection is driven by the data folders that actually exist on disk
/// (that is what needs cleaning); the registry is consulted only to describe how a browser was found.
/// </summary>
public sealed class BrowserDetector
{
    private static readonly string[] ChromiumProfileMarkers = ["Preferences", "Secure Preferences"];

    private readonly ICleanerLog _log;
    private readonly IReadOnlyList<BrowserDefinition> _definitions;

    public BrowserDetector(IReadOnlyList<BrowserDefinition> definitions, ICleanerLog? log = null)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _log = log ?? NullLog.Instance;
    }

    public IReadOnlyList<DetectedBrowser> Detect()
    {
        var result = new List<DetectedBrowser>();
        foreach (var definition in _definitions)
        {
            try
            {
                var detected = DetectOne(definition);
                if (detected is not null)
                {
                    result.Add(detected);
                    _log.Info($"Detected {definition.Name}: {TextFormat.Count(detected.Profiles.Count, "profile")} [{detected.DetectionSource}].");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Browser detection for {definition.Name} failed: {ex.Message}");
            }
        }

        return result;
    }

    private static DetectedBrowser? DetectOne(BrowserDefinition definition)
    {
        var existingRoots = definition.DataRoots.Where(Directory.Exists).ToList();
        if (existingRoots.Count == 0)
        {
            return null;
        }

        var profiles = new List<BrowserProfile>();
        foreach (var root in existingRoots)
        {
            profiles.AddRange(definition.Family == BrowserFamily.Chromium
                ? EnumerateChromiumProfiles(definition, root)
                : EnumerateGeckoProfiles(definition, root));
        }

        if (profiles.Count == 0)
        {
            return null;
        }

        var source = RegistryProbe.IsRegistered(definition) ? "registry + data folder" : "data folder";
        return new DetectedBrowser
        {
            Definition = definition,
            Profiles = profiles,
            ExistingDataRoots = existingRoots,
            DetectionSource = source,
        };
    }

    // ------------------------------------------------------------------ Chromium

    private static IEnumerable<BrowserProfile> EnumerateChromiumProfiles(BrowserDefinition definition, string root)
    {
        var names = ReadChromiumProfileNames(Path.Combine(root, "Local State"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cacheRoot = definition.SeparateCacheRoots.TryGetValue(root, out var separate) ? separate : null;

        // Opera-style layout: the data root itself is the profile.
        if (IsChromiumProfileDirectory(root))
        {
            seen.Add(string.Empty);
            yield return MakeChromiumProfile(definition, root, root, string.Empty, names.GetValueOrDefault("Default") ?? "Default", cacheRoot ?? root);
        }

        foreach (var (dirName, displayName) in names)
        {
            var path = Path.Combine(root, dirName);
            if (Directory.Exists(path) && seen.Add(dirName))
            {
                yield return MakeChromiumProfile(definition, root, path, dirName, displayName, cacheRoot is null ? path : Path.Combine(cacheRoot, dirName));
            }
        }

        foreach (var dir in SafeEnumerateDirectories(root))
        {
            var dirName = Path.GetFileName(dir);
            if (seen.Contains(dirName) || !IsChromiumProfileDirectory(dir))
            {
                continue;
            }

            seen.Add(dirName);
            yield return MakeChromiumProfile(definition, root, dir, dirName, dirName, cacheRoot is null ? dir : Path.Combine(cacheRoot, dirName));
        }

        // Opera "side profiles" (messenger panels) are full Chromium profiles with their own caches.
        var sideProfiles = Path.Combine(root, "_side_profiles");
        if (Directory.Exists(sideProfiles))
        {
            foreach (var dir in SafeEnumerateDirectories(sideProfiles))
            {
                if (!IsChromiumProfileDirectory(dir))
                {
                    continue;
                }

                var dirName = Path.Combine("_side_profiles", Path.GetFileName(dir));
                yield return MakeChromiumProfile(definition, root, dir, dirName, "Sidebar: " + Path.GetFileName(dir), cacheRoot is null ? dir : Path.Combine(cacheRoot, dirName));
            }
        }
    }

    private static BrowserProfile MakeChromiumProfile(BrowserDefinition definition, string root, string path, string dirName, string displayName, string cachePath)
    {
        var isSystem = dirName.Equals("System Profile", StringComparison.OrdinalIgnoreCase) || dirName.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase);
        return new BrowserProfile
        {
            Browser = definition,
            DataRoot = root,
            ProfilePath = path,
            CachePath = cachePath,
            DirectoryName = string.IsNullOrEmpty(dirName) ? "(root)" : dirName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? (string.IsNullOrEmpty(dirName) ? "Default" : dirName) : displayName,
            IsSystemOrGuest = isSystem,
        };
    }

    private static bool IsChromiumProfileDirectory(string path)
        => ChromiumProfileMarkers.Any(marker => File.Exists(Path.Combine(path, marker)));

    /// <summary>Reads profile.info_cache from Local State: directory name → display name.</summary>
    internal static Dictionary<string, string> ReadChromiumProfileNames(string localStatePath)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(localStatePath))
        {
            return names;
        }

        try
        {
            using var stream = new FileStream(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("profile", out var profile) || !profile.TryGetProperty("info_cache", out var infoCache) || infoCache.ValueKind != JsonValueKind.Object)
            {
                return names;
            }

            foreach (var entry in infoCache.EnumerateObject())
            {
                var name = GetString(entry.Value, "name");
                var gaia = GetString(entry.Value, "gaia_name");
                var display = !string.IsNullOrWhiteSpace(name) ? name : (!string.IsNullOrWhiteSpace(gaia) ? gaia : entry.Name);
                names[entry.Name] = display!;
            }
        }
        catch (Exception)
        {
            // Malformed or locked Local State: fall back to directory scanning.
        }

        return names;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ------------------------------------------------------------------ Gecko

    private static IEnumerable<BrowserProfile> EnumerateGeckoProfiles(BrowserDefinition definition, string root)
    {
        var ini = Path.Combine(root, "profiles.ini");
        var entries = File.Exists(ini) ? GeckoProfilesIni.Parse(ini, root) : [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!Directory.Exists(entry.Path) || !seen.Add(entry.Path))
            {
                continue;
            }

            yield return new BrowserProfile
            {
                Browser = definition,
                DataRoot = root,
                ProfilePath = entry.Path,
                CachePath = GeckoLocalDirectory(definition, root, entry),
                DirectoryName = Path.GetFileName(entry.Path),
                DisplayName = string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileName(entry.Path) : entry.Name,
            };
        }

        // Profiles that exist on disk but are missing from profiles.ini (e.g. after a broken reset) - still cleanable.
        var profilesDir = Path.Combine(root, "Profiles");
        foreach (var dir in SafeEnumerateDirectories(profilesDir))
        {
            if (seen.Contains(dir) || !(File.Exists(Path.Combine(dir, "prefs.js")) || File.Exists(Path.Combine(dir, "times.json"))))
            {
                continue;
            }

            seen.Add(dir);
            var leaf = Path.GetFileName(dir);
            yield return new BrowserProfile
            {
                Browser = definition,
                DataRoot = root,
                ProfilePath = dir,
                CachePath = definition.LocalProfilesRoot is null ? dir : Path.Combine(definition.LocalProfilesRoot, leaf),
                DirectoryName = leaf,
                DisplayName = leaf,
            };
        }
    }

    private static string GeckoLocalDirectory(BrowserDefinition definition, string dataRoot, GeckoProfileEntry entry)
    {
        // nsToolkitProfileService::GetLocalDirFromRootDir: only profiles that live directly below the default
        // "<data root>\Profiles" folder get a mirrored local directory; any other location keeps its caches inside
        // the profile directory itself.
        var defaultProfilesRoot = Path.Combine(dataRoot, "Profiles");
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(entry.Path));
        if (definition.LocalProfilesRoot is not null && parent is not null
            && string.Equals(Path.GetFullPath(parent), Path.GetFullPath(defaultProfilesRoot), StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(definition.LocalProfilesRoot, Path.GetFileName(entry.Path));
        }

        return entry.Path;
    }

    private static List<string> SafeEnumerateDirectories(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(path)
                .EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}

internal sealed record GeckoProfileEntry(string Name, string Path, bool IsRelative, bool IsDefault);

/// <summary>Parser for Mozilla profiles.ini files.</summary>
internal static class GeckoProfilesIni
{
    public static IReadOnlyList<GeckoProfileEntry> Parse(string iniPath, string root)
    {
        var entries = new List<GeckoProfileEntry>();
        string? section = null;
        string? name = null;
        string? path = null;
        var isRelative = true;
        var isDefault = false;

        void Flush()
        {
            if (section is not null && section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(path))
            {
                var normalized = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
                var full = isRelative ? System.IO.Path.GetFullPath(System.IO.Path.Combine(root, normalized)) : System.IO.Path.GetFullPath(normalized);
                entries.Add(new GeckoProfileEntry(name ?? string.Empty, full, isRelative, isDefault));
            }

            name = null;
            path = null;
            isRelative = true;
            isDefault = false;
        }

        IEnumerable<string> lines;
        try
        {
            lines = File.ReadAllLines(iniPath);
        }
        catch (Exception)
        {
            return entries;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                section = line[1..^1];
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key.ToUpperInvariant())
            {
                case "NAME":
                    name = value;
                    break;
                case "PATH":
                    path = value;
                    break;
                case "ISRELATIVE":
                    isRelative = value != "0";
                    break;
                case "DEFAULT":
                    isDefault = value == "1";
                    break;
            }
        }

        Flush();
        return entries;
    }
}

/// <summary>Lightweight registry probe used only to report how a browser was detected.</summary>
internal static class RegistryProbe
{
    public static bool IsRegistered(BrowserDefinition definition)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var marker in definition.RegistryMarkers)
        {
            if (KeyExists(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\" + marker)
                || KeyExists(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + marker)
                || KeyExists(Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\" + marker))
            {
                return true;
            }
        }

        foreach (var exe in definition.ExecutableNames)
        {
            if (KeyExists(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe)
                || KeyExists(Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
            {
                return true;
            }
        }

        return false;
    }

    private static bool KeyExists(Microsoft.Win32.RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path, writable: false);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}