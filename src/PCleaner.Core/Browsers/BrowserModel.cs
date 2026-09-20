namespace PCleaner.Core.Browsers;

public enum BrowserFamily
{
    Chromium = 0,
    Gecko = 1,
}

/// <summary>Static knowledge about one browser product (or channel).</summary>
public sealed class BrowserDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required BrowserFamily Family { get; init; }

    /// <summary>Executable names (without extension) whose presence means the browser is running.</summary>
    public required IReadOnlyList<string> ProcessNames { get; init; }

    /// <summary>
    /// Chromium: the "User Data" directory. Gecko: the directory containing profiles.ini.
    /// Paths are absolute (environment variables already expanded).
    /// </summary>
    public required IReadOnlyList<string> DataRoots { get; init; }

    /// <summary>
    /// Chromium only: browsers such as Opera keep the profile in Roaming AppData while the HTTP cache lives in Local
    /// AppData. Each entry maps a data root to its cache root; unmapped roots keep the cache inside the profile.
    /// </summary>
    public IReadOnlyDictionary<string, string> SeparateCacheRoots { get; init; } = new Dictionary<string, string>();

    /// <summary>Gecko only: directory that holds the "local" profile data (cache2, startupCache, ...).</summary>
    public string? LocalProfilesRoot { get; init; }

    /// <summary>Registry keys (relative to HKLM/HKCU SOFTWARE) that indicate an installation.</summary>
    public IReadOnlyList<string> RegistryMarkers { get; init; } = [];

    /// <summary>Executable file names for App Paths lookups (e.g. chrome.exe).</summary>
    public IReadOnlyList<string> ExecutableNames { get; init; } = [];

    public override string ToString() => Name;
}

/// <summary>A profile directory that belongs to a detected browser.</summary>
public sealed class BrowserProfile
{
    public required BrowserDefinition Browser { get; init; }

    /// <summary>Directory name inside the data root (Chromium: "Default"; Gecko: "abcd1234.default-release").</summary>
    public required string DirectoryName { get; init; }

    /// <summary>Human readable profile name (from Local State / profiles.ini) - falls back to the directory name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Absolute path of the profile directory (the one holding Preferences / prefs.js).</summary>
    public required string ProfilePath { get; init; }

    /// <summary>Absolute path where the HTTP cache lives. Same as <see cref="ProfilePath"/> for most Chromium browsers.</summary>
    public required string CachePath { get; init; }

    /// <summary>The data root the profile was found in.</summary>
    public required string DataRoot { get; init; }

    public bool IsSystemOrGuest { get; init; }

    public override string ToString() => $"{Browser.Name} — {DisplayName}";
}

/// <summary>A detected browser installation with its profiles.</summary>
public sealed class DetectedBrowser
{
    public required BrowserDefinition Definition { get; init; }

    public required IReadOnlyList<BrowserProfile> Profiles { get; init; }

    /// <summary>Data roots that exist on disk for this browser.</summary>
    public required IReadOnlyList<string> ExistingDataRoots { get; init; }

    /// <summary>How the browser was found (registry, data folder, ...) - for the log/UI.</summary>
    public required string DetectionSource { get; init; }

    /// <summary>"1 profile · registry + data folder" - the Dashboard tile caption.</summary>
    public string Summary => $"{TextFormat.Count(Profiles.Count, "profile")} · {DetectionSource}";

    public override string ToString() => $"{Definition.Name} ({TextFormat.Count(Profiles.Count, "profile")})";
}