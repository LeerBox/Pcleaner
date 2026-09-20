using System.IO.Enumeration;

namespace PCleaner.Core.Rules;

/// <summary>
/// Everything a rule provider needs to know about the machine. Kept as data so unit tests can point providers at a
/// temporary directory tree instead of the real profile.
/// </summary>
public sealed class RuleContext
{
    public required string LocalAppData { get; init; }

    public required string RoamingAppData { get; init; }

    public required string UserProfile { get; init; }

    public required string WindowsDirectory { get; init; }

    public required string ProgramData { get; init; }

    public required string SystemDrive { get; init; }

    public bool IsElevated { get; init; }

    /// <summary>Files in temp folders younger than this are left alone (installers may still need them).</summary>
    public TimeSpan TempFileMinimumAge { get; init; } = TimeSpan.FromHours(24);

    public string LocalLowAppData => Path.Combine(UserProfile, "AppData", "LocalLow");

    public string System32 => Path.Combine(WindowsDirectory, "System32");

    public static RuleContext FromCurrentMachine(bool isElevated, TimeSpan? tempFileMinimumAge = null)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new RuleContext
        {
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            WindowsDirectory = windows,
            ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            SystemDrive = Path.GetPathRoot(windows) ?? "C:\\",
            IsElevated = isElevated,
            TempFileMinimumAge = tempFileMinimumAge ?? TimeSpan.FromHours(24),
        };
    }
}

/// <summary>Expands wildcard path segments (for example <c>Packages\*\TempState</c>) into existing directories.</summary>
public static class PathExpander
{
    /// <summary>
    /// Returns every existing directory matching the pattern. Only directory names may contain wildcards; the
    /// drive/root must be literal. Reparse points are never expanded.
    /// </summary>
    public static IReadOnlyList<string> ExpandDirectories(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var root = Path.GetPathRoot(pattern);
        if (string.IsNullOrEmpty(root))
        {
            return [];
        }

        var segments = pattern[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string> { root };

        foreach (var segment in segments)
        {
            var next = new List<string>();
            var hasWildcard = segment.Contains('*', StringComparison.Ordinal) || segment.Contains('?', StringComparison.Ordinal);

            foreach (var dir in current)
            {
                if (!hasWildcard)
                {
                    var candidate = Path.Combine(dir, segment);
                    if (Directory.Exists(candidate))
                    {
                        next.Add(candidate);
                    }

                    continue;
                }

                try
                {
                    foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        if (FileSystemName.MatchesSimpleExpression(segment, sub.Name, ignoreCase: true))
                        {
                            next.Add(sub.FullName);
                        }
                    }
                }
                catch (Exception)
                {
                    // Inaccessible directory - nothing to expand.
                }
            }

            current = next;
            if (current.Count == 0)
            {
                break;
            }
        }

        return current;
    }
}