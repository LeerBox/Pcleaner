using System.IO.Enumeration;
using PCleaner.Core.Logging;
using PCleaner.Core.Model;

namespace PCleaner.Core.Engine;

/// <summary>
/// Enumerates the files a <see cref="PathTarget"/> would delete. Never follows reparse points (junctions and
/// symbolic links), never enters protected browser directories and never reports protected files.
/// </summary>
public sealed class FileSystemWalker
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
    };

    private readonly ICleanerLog _log;

    public FileSystemWalker(ICleanerLog? log = null)
    {
        _log = log ?? NullLog.Instance;
    }

    /// <summary>
    /// Enumerates everything the target would delete. Directories are yielded after their content and only when
    /// every child was selected for deletion, so the caller can remove them once they are empty.
    /// </summary>
    public IReadOnlyList<ScanItem> Enumerate(PathTarget target, CancellationToken cancellationToken, Action<string>? onDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var results = new List<ScanItem>();
        var cutoff = target.MinimumAge is { } age ? DateTime.UtcNow - age : (DateTime?)null;

        if (target.IsFile)
        {
            var file = new FileInfo(target.Path);
            if (file.Exists && IsDeletableFile(file, target, cutoff, target.Path))
            {
                results.Add(new ScanItem(file.FullName, file.Length, false, file.LastWriteTimeUtc));
            }

            return results;
        }

        var root = new DirectoryInfo(target.Path);
        if (!root.Exists)
        {
            return results;
        }

        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            _log.Warn($"Skipping '{root.FullName}': the cleanup root is a reparse point.");
            return results;
        }

        var rootPath = SafetyGuard.NormalizeDirectory(root.FullName);
        Walk(root, target, rootPath, cutoff, results, onDirectory, 0, cancellationToken);
        return results;
    }

    /// <summary>Returns true when the whole directory content was selected (so the directory can be removed).</summary>
    private bool Walk(
        DirectoryInfo directory,
        PathTarget target,
        string rootPath,
        DateTime? cutoff,
        List<ScanItem> results,
        Action<string>? onDirectory,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        onDirectory?.Invoke(directory.FullName);

        var everythingSelected = true;
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos("*", Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Debug($"Cannot enumerate '{directory.FullName}': {ex.Message}");
            return false;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExcluded(entry.Name, target))
            {
                everythingSelected = false;
                continue;
            }

            if (entry is DirectoryInfo sub)
            {
                if (!target.Recursive)
                {
                    everythingSelected = false;
                    continue;
                }

                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Junctions / symlinks could point anywhere (even into user documents). Never follow them.
                    everythingSelected = false;
                    continue;
                }

                if (SafetyGuard.IsProtectedBrowserDirectory(sub.FullName))
                {
                    everythingSelected = false;
                    continue;
                }

                if (depth > 64)
                {
                    everythingSelected = false;
                    continue;
                }

                var subSelected = Walk(sub, target, rootPath, cutoff, results, onDirectory, depth + 1, cancellationToken);
                if (subSelected && target.DeleteEmptyDirectories && SafetyGuard.CheckDeletable(sub.FullName, rootPath, isDirectory: true) is null)
                {
                    results.Add(new ScanItem(sub.FullName, 0, true, sub.LastWriteTimeUtc));
                }
                else
                {
                    everythingSelected = false;
                }

                continue;
            }

            if (entry is FileInfo file)
            {
                if (IsDeletableFile(file, target, cutoff, rootPath))
                {
                    long length;
                    try
                    {
                        length = file.Length;
                    }
                    catch (Exception)
                    {
                        length = 0;
                    }

                    results.Add(new ScanItem(file.FullName, length, false, file.LastWriteTimeUtc));
                }
                else
                {
                    everythingSelected = false;
                }
            }
        }

        return everythingSelected;
    }

    private static bool IsDeletableFile(FileInfo file, PathTarget target, DateTime? cutoff, string rootPath)
    {
        if (target.IncludePatterns.Count > 0 && !MatchesAny(file.Name, target.IncludePatterns))
        {
            return false;
        }

        if (cutoff is { } limit)
        {
            try
            {
                var newest = Max(file.LastWriteTimeUtc, file.CreationTimeUtc, file.LastAccessTimeUtc);
                if (newest > limit)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        return SafetyGuard.CheckDeletable(file.FullName, rootPath, isDirectory: false) is null;
    }

    private static bool IsExcluded(string name, PathTarget target)
        => target.ExcludeNames.Count > 0 && MatchesAny(name, target.ExcludeNames);

    private static bool MatchesAny(string name, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime Max(DateTime a, DateTime b, DateTime c)
    {
        var m = a > b ? a : b;
        return m > c ? m : c;
    }
}