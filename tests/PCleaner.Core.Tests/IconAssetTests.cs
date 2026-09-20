using System.Buffers.Binary;

namespace PCleaner.Core.Tests;

/// <summary>
/// The application icon (src/PCleaner.App/Assets/PCleaner.ico) is built by tools/icon/Build-Icon.ps1 from the vector
/// mark in Assets/Logo.xaml. These tests read the .ico container directly and pin down what the build guarantees:
/// one frame for every size Windows requests across DPI scales, 32-bit colour with alpha everywhere, uncompressed
/// frames where the shell expects them and a PNG frame at 256 px, plus a mark that still fills the tile at 16 px.
/// </summary>
public sealed class IconAssetTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCleaner.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    private static string IconPath => Path.Combine(RepositoryRoot, "src", "PCleaner.App", "Assets", "PCleaner.ico");

    private sealed record Frame(int Width, int Height, int BitsPerPixel, byte[] Data)
    {
        public bool IsPng => Data.Length > 8 && Data[0] == 0x89 && Data[1] == (byte)'P' && Data[2] == (byte)'N' && Data[3] == (byte)'G';
    }

    private static List<Frame> ReadFrames()
    {
        var bytes = File.ReadAllBytes(IconPath);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)));   // reserved
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)));   // type: icon
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        var frames = new List<Frame>();
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.AsSpan(6 + i * 16, 16);
            int width = entry[0] == 0 ? 256 : entry[0];
            int height = entry[1] == 0 ? 256 : entry[1];
            var bpp = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(6, 2));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
            Assert.InRange(offset + size, 0, bytes.Length);
            frames.Add(new Frame(width, height, bpp, bytes.AsSpan(offset, size).ToArray()));
        }

        return frames;
    }

    [Fact]
    public void Icon_has_one_32bit_frame_for_every_size_windows_asks_for()
    {
        var frames = ReadFrames();
        Assert.Equal(ExpectedSizes, frames.Select(f => f.Width).ToArray());
        Assert.All(frames, f => Assert.Equal(f.Width, f.Height));
        Assert.All(frames, f => Assert.Equal(32, f.BitsPerPixel));
    }

    [Fact]
    public void Frames_up_to_128px_are_uncompressed_and_the_256px_frame_is_png()
    {
        foreach (var frame in ReadFrames())
        {
            if (frame.Width == 256)
            {
                Assert.True(frame.IsPng, "the 256 px frame must be PNG-compressed");
                continue;
            }

            Assert.False(frame.IsPng, $"the {frame.Width} px frame must be a plain DIB for older shell components");
            Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Data.AsSpan(0, 4)));           // BITMAPINFOHEADER
            Assert.Equal(frame.Width, BinaryPrimitives.ReadInt32LittleEndian(frame.Data.AsSpan(4, 4)));
            Assert.Equal(frame.Height * 2, BinaryPrimitives.ReadInt32LittleEndian(frame.Data.AsSpan(8, 4))); // XOR + AND mask
            var maskRow = (frame.Width + 31) / 32 * 4;
            Assert.Equal(40 + frame.Width * frame.Height * 4 + maskRow * frame.Height, frame.Data.Length);
        }
    }

    [Fact]
    public void Small_frames_fill_the_tile_and_keep_the_shield_visible()
    {
        // 16 px is what the title bar shows at 100 % scale: the tile must be solid and the white shield must survive.
        var frame = ReadFrames().Single(f => f.Width == 16);
        var pixels = frame.Data.AsSpan(40, 16 * 16 * 4);
        int opaque = 0, white = 0, blue = 0;
        for (var i = 0; i < 256; i++)
        {
            var b = pixels[i * 4];
            var g = pixels[i * 4 + 1];
            var r = pixels[i * 4 + 2];
            var a = pixels[i * 4 + 3];
            if (a > 250)
            {
                opaque++;
            }

            if (a > 250 && r > 225 && g > 225 && b > 225)
            {
                white++;
            }

            if (a > 250 && b > r + 40)
            {
                blue++;
            }
        }

        Assert.InRange(opaque, 200, 256);   // rounded tile: nearly the whole square is solid
        Assert.InRange(white, 40, 140);     // the shield is a large, clearly white shape ...
        Assert.InRange(blue, 60, 200);      // ... on a blue tile that shows through the cut-out
    }

    [Fact]
    public void Vector_mark_and_icon_builder_ship_together()
    {
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "src", "PCleaner.App", "Assets", "Logo.xaml")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "tools", "icon", "Build-Icon.ps1")));
        var logo = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "PCleaner.App", "Assets", "Logo.xaml"));
        Assert.Contains("x:Key=\"Logo.Image\"", logo, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Logo.Mark\"", logo, StringComparison.Ordinal);
        Assert.Contains("FillRule=\"EvenOdd\"", logo, StringComparison.Ordinal); // the sparkle is negative space
    }
}