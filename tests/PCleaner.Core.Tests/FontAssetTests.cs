using System.Buffers.Binary;
using System.Text;

namespace PCleaner.Core.Tests;

/// <summary>
/// PCleaner ships its own typeface (src/PCleaner.App/Fonts, built by tools/fonts/build-fonts.py from the Ubuntu font
/// family). These tests read the TrueType binaries directly and pin down what the build guarantees: the
/// licence-mandated family name and the weight records WPF groups on, the Ubuntu Font Licence notice every copy
/// must carry, tabular figures, preserved hinting, and the absence of ligatures that would merge letters in file names.
/// </summary>
public sealed class FontAssetTests
{
    private const string Family = "Ubuntu derivative PCleaner";

    private static readonly (string File, string Style, int Weight)[] Expected =
    [
        ("UbuntuDerivativePCleaner-Regular.ttf", "Regular", 400),
        ("UbuntuDerivativePCleaner-Medium.ttf", "Medium", 500),
        ("UbuntuDerivativePCleaner-Bold.ttf", "Bold", 700),
    ];

    private static string FontsDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCleaner.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory.FullName, "src", "PCleaner.App", "Fonts");
        }
    }

    [Fact]
    public void Every_face_exists_with_the_licence_mandated_name_weight_and_notices()
    {
        foreach (var (file, style, weight) in Expected)
        {
            var font = TrueType.Load(Path.Combine(FontsDirectory, file));
            Assert.Equal(Family, font.Name(16));
            Assert.Equal(style, font.Name(17));
            Assert.Equal(weight, font.WeightClass);
            Assert.Equal(style is "Regular" or "Bold" ? Family : $"{Family} {style}", font.Name(1)); // legacy Win32 grouping
            Assert.Equal($"UbuntuDerivativePCleaner-{style}", font.Name(6));
            Assert.StartsWith("Ubuntu derivative", font.Name(16), StringComparison.Ordinal); // UFL 1.0 clause 2(c): original name + "derivative X"
            Assert.Contains("Canonical", font.Name(0), StringComparison.Ordinal);
            Assert.Contains("Ubuntu Font Licence", font.Name(0), StringComparison.Ordinal);
            Assert.Contains("Ubuntu Font Licence 1.0", font.Name(13), StringComparison.Ordinal);
            Assert.Equal("https://ubuntu.com/legal/font-licence", font.Name(14));
        }

        Assert.True(File.Exists(Path.Combine(FontsDirectory, "LICENCE-Ubuntu-UFL.txt")), "the UFL text must ship next to the fonts (clause 1)");
        Assert.True(File.Exists(Path.Combine(FontsDirectory, "COPYRIGHT-Ubuntu.txt")), "the copyright notice must ship next to the fonts (clause 1)");
        Assert.Contains("UBUNTU FONT LICENCE Version 1.0", File.ReadAllText(Path.Combine(FontsDirectory, "LICENCE-Ubuntu-UFL.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void Digits_are_tabular_in_every_face()
    {
        foreach (var (file, _, _) in Expected)
        {
            var font = TrueType.Load(Path.Combine(FontsDirectory, file));
            var widths = "0123456789".Select(c => font.AdvanceWidth(c)).ToList();
            Assert.All(widths, w => Assert.True(w > 0));
            Assert.Single(widths.Distinct());
            Assert.NotEqual(font.AdvanceWidth('1'), font.AdvanceWidth('.')); // sanity: not everything is one width
        }
    }

    [Fact]
    public void Ligatures_are_gone_but_kerning_and_manual_hinting_stay()
    {
        foreach (var (file, _, _) in Expected)
        {
            var font = TrueType.Load(Path.Combine(FontsDirectory, file));
            var features = font.FeatureTags("GSUB");
            Assert.DoesNotContain("liga", features);
            Assert.DoesNotContain("calt", features);
            Assert.Contains("tnum", features);
            Assert.Contains("kern", font.FeatureTags("GPOS"));
            Assert.True(font.HasTable("fpgm") && font.HasTable("prep") && font.HasTable("cvt "), "Dalton Maag's TrueType hinting must survive the subset");
            Assert.True(new FileInfo(Path.Combine(FontsDirectory, file)).Length < 200 * 1024, "subset must stay small");
        }
    }

    [Fact]
    public void Latin_punctuation_and_symbols_are_covered()
    {
        var font = TrueType.Load(Path.Combine(FontsDirectory, "UbuntuDerivativePCleaner-Regular.ttf"));
        foreach (var c in "AZaz09 ·•…—–‹›«»€£¥™−≈≤≥∞×÷°±½©®ÄéñçØßČěř")
        {
            Assert.True(font.HasGlyph(c), $"missing U+{(int)c:X4} '{c}'");
        }

        Assert.False(font.HasGlyph('→'), "Ubuntu has no arrows; they come from Windows' fallback fonts");
        Assert.False(font.HasGlyph('ع'), "Arabic is intentionally left to Windows' fallback fonts");
        Assert.False(font.HasGlyph('日'), "CJK is intentionally left to Windows' fallback fonts");
    }

    [Fact]
    public void App_project_embeds_the_fonts_and_the_licence()
    {
        var csproj = File.ReadAllText(Path.Combine(FontsDirectory, "..", "PCleaner.App.csproj"));
        Assert.Contains(@"<Resource Include=""Fonts\*.ttf"" />", csproj, StringComparison.Ordinal);
        Assert.Contains(@"<Resource Include=""Fonts\LICENCE-Ubuntu-UFL.txt"" />", csproj, StringComparison.Ordinal);
        Assert.Contains(@"<Resource Include=""Fonts\COPYRIGHT-Ubuntu.txt"" />", csproj, StringComparison.Ordinal);

        var theme = File.ReadAllText(Path.Combine(FontsDirectory, "..", "Themes", "Dark.xaml"));
        Assert.Contains($"pack://application:,,,/Fonts/#{Family}", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("Segoe UI Variable", theme, StringComparison.Ordinal);
    }

    /// <summary>Minimal TrueType reader: table directory, name, OS/2, cmap (format 4), hmtx, and the GSUB/GPOS feature tags.</summary>
    private sealed class TrueType
    {
        private readonly byte[] _data;
        private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
        private readonly Dictionary<char, int> _cmap = [];

        private TrueType(byte[] data)
        {
            _data = data;
            var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
            for (var i = 0; i < numTables; i++)
            {
                var record = 12 + i * 16;
                var tag = Encoding.ASCII.GetString(data, record, 4);
                _tables[tag] = ((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12)));
            }

            ReadCmap();
        }

        public static TrueType Load(string path) => new(File.ReadAllBytes(path));

        public bool HasTable(string tag) => _tables.ContainsKey(tag);

        public int WeightClass => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_tables["OS/2"].Offset + 4));

        public bool HasGlyph(char c) => _cmap.TryGetValue(c, out var glyph) && glyph != 0;

        /// <summary>English Windows-platform name record.</summary>
        public string Name(int nameId)
        {
            var (offset, _) = _tables["name"];
            var count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 2));
            var storage = offset + BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 4));
            for (var i = 0; i < count; i++)
            {
                var record = offset + 6 + i * 12;
                var platform = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record));
                var language = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record + 4));
                var id = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record + 6));
                if (platform != 3 || language != 0x409 || id != nameId)
                {
                    continue;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record + 8));
                var start = storage + BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record + 10));
                return Encoding.BigEndianUnicode.GetString(_data, start, length);
            }

            return string.Empty;
        }

        public int AdvanceWidth(char c)
        {
            Assert.True(_cmap.TryGetValue(c, out var glyph), $"no glyph for '{c}'");
            var numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_tables["hhea"].Offset + 34));
            var index = Math.Min(glyph, numberOfHMetrics - 1);
            return BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_tables["hmtx"].Offset + index * 4));
        }

        public HashSet<string> FeatureTags(string table)
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            if (!_tables.TryGetValue(table, out var location))
            {
                return tags;
            }

            var featureList = location.Offset + BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(location.Offset + 6));
            var count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(featureList));
            for (var i = 0; i < count; i++)
            {
                tags.Add(Encoding.ASCII.GetString(_data, featureList + 2 + i * 6, 4));
            }

            return tags;
        }

        private void ReadCmap()
        {
            var (offset, _) = _tables["cmap"];
            var count = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 2));
            for (var i = 0; i < count; i++)
            {
                var record = offset + 4 + i * 8;
                var platform = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record));
                var encoding = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(record + 2));
                var subtable = offset + (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(record + 4));
                if (platform == 3 && encoding == 1 && BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(subtable)) == 4)
                {
                    ReadFormat4(subtable);
                    return;
                }
            }

            Assert.Fail("no Windows Unicode BMP (format 4) cmap subtable");
        }

        private void ReadFormat4(int subtable)
        {
            var segCount = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(subtable + 6)) / 2;
            var ends = subtable + 14;
            var starts = ends + segCount * 2 + 2;
            var deltas = starts + segCount * 2;
            var rangeOffsets = deltas + segCount * 2;
            for (var s = 0; s < segCount; s++)
            {
                var end = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(ends + s * 2));
                var start = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(starts + s * 2));
                var delta = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(deltas + s * 2));
                var rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(rangeOffsets + s * 2));
                if (start == 0xFFFF)
                {
                    continue;
                }

                for (var code = start; code <= end; code++)
                {
                    int glyph;
                    if (rangeOffset == 0)
                    {
                        glyph = (code + delta) & 0xFFFF;
                    }
                    else
                    {
                        var address = rangeOffsets + s * 2 + rangeOffset + (code - start) * 2;
                        glyph = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(address));
                        if (glyph != 0)
                        {
                            glyph = (glyph + delta) & 0xFFFF;
                        }
                    }

                    if (glyph != 0)
                    {
                        _cmap[(char)code] = glyph;
                    }
                }
            }
        }
    }
}