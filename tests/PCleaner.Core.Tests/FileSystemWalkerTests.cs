using PCleaner.Core.Engine;
using PCleaner.Core.Model;

namespace PCleaner.Core.Tests;

/// <summary>Creates an isolated directory tree below %TEMP% that is removed on dispose.</summary>
public sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "PCleanerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relative, string content = "x", DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        if (lastWriteUtc is { } stamp)
        {
            System.IO.File.SetLastWriteTimeUtc(path, stamp);
            System.IO.File.SetCreationTimeUtc(path, stamp);
            System.IO.File.SetLastAccessTimeUtc(path, stamp);
        }

        return path;
    }

    public string Dir(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class FileSystemWalkerTests
{
    [Fact]
    public void Enumerates_files_and_empty_directories_bottom_up()
    {
        using var tree = new TempTree();
        tree.File(@"a\b\one.txt");
        tree.File(@"a\two.txt");
        tree.File("three.txt");

        var walker = new FileSystemWalker();
        var items = walker.Enumerate(new PathTarget { Path = tree.Root }, CancellationToken.None);

        var files = items.Where(i => !i.IsDirectory).Select(i => Path.GetFileName(i.Path)).OrderBy(n => n).ToArray();
        Assert.Equal(["one.txt", "three.txt", "two.txt"], files);

        var dirs = items.Where(i => i.IsDirectory).Select(i => Path.GetRelativePath(tree.Root, i.Path)).ToArray();
        Assert.Equal([@"a\b", "a"], dirs);
        Assert.DoesNotContain(items, i => i.IsDirectory && string.Equals(i.Path, tree.Root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Include_patterns_limit_files_and_keep_directories()
    {
        using var tree = new TempTree();
        tree.File(@"logs\a.log");
        tree.File(@"logs\keep.txt");

        var items = new FileSystemWalker().Enumerate(new PathTarget { Path = tree.Root, IncludePatterns = ["*.log"] }, CancellationToken.None);

        Assert.Single(items, i => !i.IsDirectory);
        Assert.EndsWith("a.log", items.Single(i => !i.IsDirectory).Path, StringComparison.Ordinal);
        Assert.DoesNotContain(items, i => i.IsDirectory); // logs\ still holds keep.txt
    }

    [Fact]
    public void Excluded_names_are_skipped_entirely()
    {
        using var tree = new TempTree();
        tree.File(@"Low\secret.txt");
        tree.File("junk.txt");
        tree.File("pinned.automaticDestinations-ms");

        var items = new FileSystemWalker().Enumerate(new PathTarget { Path = tree.Root, ExcludeNames = ["Low", "pinned.*"] }, CancellationToken.None);

        Assert.Single(items);
        Assert.EndsWith("junk.txt", items[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Minimum_age_keeps_recent_files()
    {
        using var tree = new TempTree();
        tree.File("old.tmp", lastWriteUtc: DateTime.UtcNow.AddDays(-3));
        tree.File("new.tmp");

        var items = new FileSystemWalker().Enumerate(new PathTarget { Path = tree.Root, MinimumAge = TimeSpan.FromHours(24) }, CancellationToken.None);

        Assert.Single(items);
        Assert.EndsWith("old.tmp", items[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_recursive_targets_ignore_sub_directories()
    {
        using var tree = new TempTree();
        tree.File(@"sub\inner.txt");
        tree.File("outer.txt");

        var items = new FileSystemWalker().Enumerate(new PathTarget { Path = tree.Root, Recursive = false }, CancellationToken.None);

        Assert.Single(items);
        Assert.EndsWith("outer.txt", items[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Junctions_are_never_followed()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        var outsideFile = Path.Combine(outside, "precious.txt");
        File.WriteAllText(outsideFile, "keep me");
        var cleanRoot = tree.Dir("clean");
        tree.File(@"clean\junk.txt");

        // Create a junction clean\link -> outside (no admin needed for junctions).
        var link = Path.Combine(cleanRoot, "link");
        Directory.CreateSymbolicLink(link, outside);
        if (!Directory.Exists(link))
        {
            return; // Symbolic links may be unavailable without developer mode; nothing to verify.
        }

        var items = new FileSystemWalker().Enumerate(new PathTarget { Path = cleanRoot }, CancellationToken.None);

        Assert.DoesNotContain(items, i => i.Path.Contains("precious", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Path.EndsWith("link", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(outsideFile));
    }

    [Fact]
    public void Single_file_targets_work()
    {
        using var tree = new TempTree();
        var dump = tree.File("MEMORY.DMP");

        var items = new FileSystemWalker().Enumerate(new PathTarget { Path = dump, IsFile = true }, CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(dump, items[0].Path);
    }
}