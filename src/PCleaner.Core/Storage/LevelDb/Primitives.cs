using System.Buffers.Binary;

namespace PCleaner.Core.Storage.LevelDb;

/// <summary>
/// CRC-32C (Castagnoli) with LevelDB's masking, as used by its log records and table blocks
/// (<c>util/crc32c.h</c>). Hardware acceleration is not needed for the file sizes involved here.
/// </summary>
internal static class Crc32C
{
    private const uint Polynomial = 0x82F63B78;
    private const uint MaskDelta = 0xA282EAD8;
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data) => Extend(0, data);

    public static uint Extend(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        foreach (var b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }

    /// <summary>LevelDB stores CRCs masked so that a CRC of data that already contains CRCs behaves well.</summary>
    public static uint Mask(uint crc) => ((crc >> 15) | (crc << 17)) + MaskDelta;

    public static uint Unmask(uint masked)
    {
        var rot = masked - MaskDelta;
        return (rot >> 17) | (rot << 15);
    }

    public static uint ReadMasked(ReadOnlySpan<byte> fourBytes) => BinaryPrimitives.ReadUInt32LittleEndian(fourBytes);

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}

/// <summary>Snappy block decompression (the only compression Chromium's LevelDB tables use on Windows).</summary>
internal static class Snappy
{
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var pos = 0;
        var length = (int)Varint.ReadUInt64(input, ref pos);
        var output = new byte[length];
        var outPos = 0;

        while (pos < input.Length)
        {
            var tag = input[pos++];
            switch (tag & 3)
            {
                case 0:
                {
                    var literalLength = tag >> 2;
                    if (literalLength >= 60)
                    {
                        var extraBytes = literalLength - 59;
                        literalLength = 0;
                        for (var i = 0; i < extraBytes; i++)
                        {
                            literalLength |= input[pos++] << (8 * i);
                        }
                    }

                    literalLength++;
                    input.Slice(pos, literalLength).CopyTo(output.AsSpan(outPos));
                    pos += literalLength;
                    outPos += literalLength;
                    break;
                }

                case 1:
                {
                    var copyLength = ((tag >> 2) & 7) + 4;
                    var offset = ((tag >> 5) << 8) | input[pos++];
                    CopyBack(output, ref outPos, offset, copyLength);
                    break;
                }

                case 2:
                {
                    var copyLength = (tag >> 2) + 1;
                    var offset = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(pos, 2));
                    pos += 2;
                    CopyBack(output, ref outPos, offset, copyLength);
                    break;
                }

                default:
                {
                    var copyLength = (tag >> 2) + 1;
                    var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(pos, 4));
                    pos += 4;
                    CopyBack(output, ref outPos, offset, copyLength);
                    break;
                }
            }
        }

        if (outPos != length)
        {
            throw new InvalidDataException("Snappy stream ended early.");
        }

        return output;
    }

    private static void CopyBack(byte[] output, ref int outPos, int offset, int length)
    {
        if (offset <= 0 || offset > outPos)
        {
            throw new InvalidDataException("Snappy copy offset is out of range.");
        }

        // Byte-by-byte on purpose: overlapping copies (offset < length) repeat the pattern, as the format requires.
        var start = outPos - offset;
        for (var i = 0; i < length; i++)
        {
            output[outPos++] = output[start + i];
        }
    }
}

/// <summary>LevelDB varint32/64 encoding (<c>util/coding.h</c>).</summary>
internal static class Varint
{
    public static ulong ReadUInt64(ReadOnlySpan<byte> buffer, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (shift <= 63)
        {
            if (pos >= buffer.Length)
            {
                throw new InvalidDataException("Truncated varint.");
            }

            var b = buffer[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("Varint too long.");
    }

    public static void Write(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }
}