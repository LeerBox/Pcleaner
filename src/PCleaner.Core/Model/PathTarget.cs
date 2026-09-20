namespace PCleaner.Core.Model;

/// <summary>
/// A file system location that a <see cref="CleanupRule"/> may clean.
/// Paths are stored fully expanded (no environment variables).
/// </summary>
public sealed class PathTarget
{
    /// <summary>Absolute path of the directory (or, when <see cref="IsFile"/> is set, of a single file).</summary>
    public required string Path { get; init; }

    /// <summary>True when <see cref="Path"/> points to a single file rather than a directory.</summary>
    public bool IsFile { get; init; }

    /// <summary>File name wildcards to include (for example <c>*.log</c>). Empty means everything.</summary>
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];

    /// <summary>
    /// Names (relative to <see cref="Path"/>) that must never be touched. Entries may be file names, directory names
    /// or wildcards. Matching is case-insensitive and is applied to every path segment below the root.
    /// </summary>
    public IReadOnlyList<string> ExcludeNames { get; init; } = [];

    /// <summary>Whether to descend into sub directories.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Delete sub directories that end up empty. The root directory itself is never deleted.</summary>
    public bool DeleteEmptyDirectories { get; init; } = true;

    /// <summary>Only delete files that were last written before now minus this age. Null = no age limit.</summary>
    public TimeSpan? MinimumAge { get; init; }

    public override string ToString() => IsFile ? Path : $"{Path}\\{(IncludePatterns.Count == 0 ? "*" : string.Join(';', IncludePatterns))}";
}