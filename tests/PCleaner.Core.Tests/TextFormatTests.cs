namespace PCleaner.Core.Tests;

/// <summary>User-facing counts must read like a person wrote them: "1 item", "22 items", never "item(s)".</summary>
public sealed class TextFormatTests
{
    [Theory]
    [InlineData(0, "item", "0 items")]
    [InlineData(1, "item", "1 item")]
    [InlineData(22, "item", "22 items")]
    [InlineData(1500, "file", "1,500 files")]
    [InlineData(1, "entry", "1 entry")]
    [InlineData(3, "entry", "3 entries")]
    [InlineData(2, "process", "2 processes")]
    [InlineData(2, "form entry", "2 form entries")]
    [InlineData(1, "key", "1 key")]
    [InlineData(2, "key", "2 keys")]
    public void Count_pluralises_regular_english_nouns(long count, string noun, string expected)
    {
        Assert.Equal(expected, TextFormat.Count(count, noun));
    }

    [Fact]
    public void Count_accepts_an_explicit_plural()
    {
        Assert.Equal("1 website", TextFormat.Count(1, "website", "websites"));
        Assert.Equal("4 websites", TextFormat.Count(4, "website", "websites"));
    }

    [Fact]
    public void No_shipped_text_falls_back_to_the_parenthesised_plural()
    {
        var sources = Directory.EnumerateFiles(FindSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(FindSourceRoot(), "*.xaml", SearchOption.AllDirectories));

        var offenders = sources
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => l.Text.Contains('"') && System.Text.RegularExpressions.Regex.IsMatch(l.Text, @"\b[a-z]+\(s\)"))
            .Where(l => !l.Text.Contains("http(s)", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Use TextFormat.Count instead of \"(s)\":\n" + string.Join('\n', offenders));
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), "src");
    }
}