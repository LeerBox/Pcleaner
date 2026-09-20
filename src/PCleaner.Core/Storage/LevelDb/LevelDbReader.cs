using System.Buffers.Binary;

namespace PCleaner.Core.Storage.LevelDb;

/// <summary>A live key/value pair of a LevelDB database.</summary>
public sealed record LevelDbEntry(byte[] Key, byte[] Value);

/// <summary>
/// Reads a closed LevelDB database the way <c>DBImpl::Recover</c> does: CURRENT → MANIFEST version edits (live
/// tables, log number), then every live table plus the log files that were not yet compacted. The newest sequence
/// number wins per key and deletion markers hide older values. Only the default bytewise comparator is supported,
/// which is what Chromium's DOM storage databases use.
/// </summary>
public static class LevelDbReader
{
    private const ulong TableMagic = 0xDB4775248B80FB57;
    private const int FooterLength = 48;
    private const int BlockTrailerLength = 5;
    internal const int LogBlockSize = 32768;
    internal const int LogHeaderLength = 7;

    private const int TypeDeletion = 0;
    private const int TypeValue = 1;

    /// <summary>Loads every live entry (tombstones already applied) of the database in <paramref name="directory"/>.</summary>
    /// <param name="directory">The LevelDB folder.</param>
    /// <param name="tolerateTornTail">
    /// When true, a corrupt or truncated record at the END of a log file is treated as end-of-file - the behaviour of
    /// LevelDB's own recovery - which allows inspecting a database the browser is currently writing to. Rewrites
    /// must use false so that any damage is reported instead of silently dropping records.
    /// </param>
    public static IReadOnlyList<LevelDbEntry> ReadAll(string directory, bool tolerateTornTail = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var manifest = ReadManifest(directory, tolerateTornTail);
        if (manifest.Comparator is not null && manifest.Comparator != "leveldb.BytewiseComparator")
        {
            throw new NotSupportedException($"Comparator '{manifest.Comparator}' is not supported.");
        }

        var latest = new Dictionary<byte[], (ulong Sequence, int Type, byte[] Value)>(ByteArrayComparer.Instance);

        void Put(byte[] key, ulong sequence, int type, byte[] value)
        {
            if (!latest.TryGetValue(key, out var existing) || existing.Sequence < sequence)
            {
                latest[key] = (sequence, type, value);
            }
        }

        foreach (var number in manifest.LiveTables.OrderBy(n => n))
        {
            var path = Path.Combine(directory, $"{number:D6}.ldb");
            if (!File.Exists(path))
            {
                path = Path.Combine(directory, $"{number:D6}.sst");
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Table {number} listed in the manifest is missing.", path);
            }

            foreach (var (key, sequence, type, value) in ReadTable(path))
            {
                Put(key, sequence, type, value);
            }
        }

        var logs = Directory.EnumerateFiles(directory, "*.log")
            .Select(p => (Path: p, Number: ParseFileNumber(p)))
            .Where(t => t.Number is not null && t.Number >= manifest.LogNumber)
            .OrderBy(t => t.Number)
            .ToList();

        foreach (var (path, _) in logs)
        {
            foreach (var (key, sequence, type, value) in ReadLog(path, tolerateTornTail))
            {
                Put(key, sequence, type, value);
            }
        }

        return latest.Where(kv => kv.Value.Type == TypeValue)
            .Select(kv => new LevelDbEntry(kv.Key, kv.Value.Value))
            .ToList();
    }

    /// <summary>
    /// Reads a whole file while allowing the owner to keep writing to it (Chromium opens LevelDB files with
    /// FILE_SHARE_READ | FILE_SHARE_WRITE, so a plain read would fail while the browser runs).
    /// </summary>
    internal static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[stream.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    // ------------------------------------------------------------------ manifest

    internal sealed class Manifest
    {
        public string? Comparator { get; set; }

        public ulong LogNumber { get; set; }

        public ulong NextFileNumber { get; set; }

        public ulong LastSequence { get; set; }

        public HashSet<ulong> LiveTables { get; } = [];
    }

    internal static Manifest ReadManifest(string directory, bool tolerateTornTail = false)
    {
        var currentPath = Path.Combine(directory, "CURRENT");
        if (!File.Exists(currentPath))
        {
            throw new FileNotFoundException("CURRENT file is missing.", currentPath);
        }

        var manifestName = File.ReadAllText(currentPath).Trim();
        if (manifestName.Length == 0 || manifestName.Contains('/', StringComparison.Ordinal) || manifestName.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException("CURRENT does not name a manifest.");
        }

        var manifest = new Manifest();
        // The manifest is appended to while the database is open; in tolerant mode a torn last edit is ignored.
        foreach (var edit in ReadLogRecords(Path.Combine(directory, manifestName), tolerateTornTail))
        {
            ApplyVersionEdit(manifest, edit);
        }

        return manifest;
    }

    /// <summary>Tags from <c>db/version_edit.cc</c>.</summary>
    private static void ApplyVersionEdit(Manifest manifest, byte[] edit)
    {
        var pos = 0;
        while (pos < edit.Length)
        {
            var tag = Varint.ReadUInt64(edit, ref pos);
            switch (tag)
            {
                case 1: // comparator
                    manifest.Comparator = ReadLengthPrefixedString(edit, ref pos);
                    break;
                case 2: // log number
                    manifest.LogNumber = Varint.ReadUInt64(edit, ref pos);
                    break;
                case 3: // next file number
                    manifest.NextFileNumber = Varint.ReadUInt64(edit, ref pos);
                    break;
                case 4: // last sequence
                    manifest.LastSequence = Varint.ReadUInt64(edit, ref pos);
                    break;
                case 5: // compact pointer: level + internal key
                    Varint.ReadUInt64(edit, ref pos);
                    SkipLengthPrefixed(edit, ref pos);
                    break;
                case 6: // deleted file: level + number
                    Varint.ReadUInt64(edit, ref pos);
                    manifest.LiveTables.Remove(Varint.ReadUInt64(edit, ref pos));
                    break;
                case 7: // new file: level, number, size, smallest, largest
                    Varint.ReadUInt64(edit, ref pos);
                    var number = Varint.ReadUInt64(edit, ref pos);
                    Varint.ReadUInt64(edit, ref pos);
                    SkipLengthPrefixed(edit, ref pos);
                    SkipLengthPrefixed(edit, ref pos);
                    manifest.LiveTables.Add(number);
                    break;
                case 9: // previous log number (legacy)
                    Varint.ReadUInt64(edit, ref pos);
                    break;
                default:
                    throw new InvalidDataException($"Unknown version edit tag {tag}.");
            }
        }
    }

    private static string ReadLengthPrefixedString(byte[] buffer, ref int pos)
    {
        var length = (int)Varint.ReadUInt64(buffer, ref pos);
        var text = System.Text.Encoding.ASCII.GetString(buffer, pos, length);
        pos += length;
        return text;
    }

    private static void SkipLengthPrefixed(byte[] buffer, ref int pos)
    {
        var length = (int)Varint.ReadUInt64(buffer, ref pos);
        pos += length;
    }

    // ------------------------------------------------------------------ log files

    /// <summary>Physical records of a log file (<c>db/log_reader.cc</c>): fragments reassembled, CRCs verified.</summary>
    internal static IEnumerable<byte[]> ReadLogRecords(string path, bool tolerateTornTail = false)
    {
        var data = ReadShared(path);
        var pos = 0;
        List<byte>? pending = null;

        while (pos + LogHeaderLength <= data.Length)
        {
            var blockRemaining = LogBlockSize - (pos % LogBlockSize);
            if (blockRemaining < LogHeaderLength)
            {
                pos += blockRemaining; // trailer padding
                continue;
            }

            var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4, 2));
            var type = data[pos + 6];
            pos += LogHeaderLength;

            if (type == 0 && length == 0)
            {
                yield break; // zero-filled remainder of a pre-allocated file
            }

            if (pos + length > data.Length)
            {
                if (tolerateTornTail)
                {
                    yield break; // record still being written
                }

                throw new InvalidDataException("Truncated log record.");
            }

            var payload = data.AsSpan(pos, length);
            var expected = Crc32C.Mask(Crc32C.Extend(Crc32C.Compute([type]), payload));
            if (expected != storedCrc)
            {
                if (tolerateTornTail)
                {
                    yield break;
                }

                throw new InvalidDataException("Log record checksum mismatch.");
            }

            pos += length;
            switch (type)
            {
                case 1: // full
                    pending = null;
                    yield return payload.ToArray();
                    break;
                case 2: // first
                    pending = [.. payload];
                    break;
                case 3: // middle
                    pending?.AddRange(payload);
                    break;
                case 4: // last
                    if (pending is not null)
                    {
                        pending.AddRange(payload);
                        yield return pending.ToArray();
                        pending = null;
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unknown log record type {type}.");
            }
        }
    }

    /// <summary>Write batches of a log file (<c>db/write_batch.cc</c>): sequence, count, then typed records.</summary>
    private static IEnumerable<(byte[] Key, ulong Sequence, int Type, byte[] Value)> ReadLog(string path, bool tolerateTornTail)
    {
        foreach (var batch in ReadLogRecords(path, tolerateTornTail))
        {
            if (batch.Length < 12)
            {
                throw new InvalidDataException("Write batch is too short.");
            }

            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(batch.AsSpan(0, 8));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(batch.AsSpan(8, 4));
            var pos = 12;
            for (uint i = 0; i < count; i++)
            {
                var type = batch[pos++];
                var keyLength = (int)Varint.ReadUInt64(batch, ref pos);
                var key = batch.AsSpan(pos, keyLength).ToArray();
                pos += keyLength;
                var value = Array.Empty<byte>();
                if (type == TypeValue)
                {
                    var valueLength = (int)Varint.ReadUInt64(batch, ref pos);
                    value = batch.AsSpan(pos, valueLength).ToArray();
                    pos += valueLength;
                }
                else if (type != TypeDeletion)
                {
                    throw new InvalidDataException($"Unknown write batch record type {type}.");
                }

                yield return (key, sequence + i, type, value);
            }
        }
    }

    // ------------------------------------------------------------------ tables

    /// <summary>Entries of a sorted table (<c>table/format.cc</c>, <c>table/block.cc</c>).</summary>
    private static IEnumerable<(byte[] Key, ulong Sequence, int Type, byte[] Value)> ReadTable(string path)
    {
        var file = ReadShared(path);
        if (file.Length < FooterLength)
        {
            throw new InvalidDataException("Table file is too small.");
        }

        var footer = file.AsSpan(file.Length - FooterLength, FooterLength);
        if (BinaryPrimitives.ReadUInt64LittleEndian(footer[(FooterLength - 8)..]) != TableMagic)
        {
            throw new InvalidDataException("Table magic number mismatch.");
        }

        var footerPos = 0;
        Varint.ReadUInt64(footer, ref footerPos); // metaindex offset
        Varint.ReadUInt64(footer, ref footerPos); // metaindex size
        var indexOffset = (long)Varint.ReadUInt64(footer, ref footerPos);
        var indexSize = (int)Varint.ReadUInt64(footer, ref footerPos);

        var index = ReadBlock(file, indexOffset, indexSize);
        foreach (var (_, handle) in BlockEntries(index))
        {
            var handlePos = 0;
            var offset = (long)Varint.ReadUInt64(handle, ref handlePos);
            var size = (int)Varint.ReadUInt64(handle, ref handlePos);
            foreach (var (internalKey, value) in BlockEntries(ReadBlock(file, offset, size)))
            {
                if (internalKey.Length < 8)
                {
                    throw new InvalidDataException("Internal key is too short.");
                }

                var packed = BinaryPrimitives.ReadUInt64LittleEndian(internalKey.AsSpan(internalKey.Length - 8, 8));
                yield return (internalKey[..^8], packed >> 8, (int)(packed & 0xFF), value);
            }
        }
    }

    private static byte[] ReadBlock(byte[] file, long offset, int size)
    {
        if (offset < 0 || size < 0 || offset + size + BlockTrailerLength > file.Length)
        {
            throw new InvalidDataException("Block handle points outside the file.");
        }

        var body = file.AsSpan((int)offset, size);
        var type = file[(int)offset + size];
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)offset + size + 1, 4));
        var expected = Crc32C.Mask(Crc32C.Extend(Crc32C.Compute(body), [type]));
        if (expected != storedCrc)
        {
            throw new InvalidDataException("Block checksum mismatch.");
        }

        return type switch
        {
            0 => body.ToArray(),
            1 => Snappy.Decompress(body),
            _ => throw new NotSupportedException($"Block compression type {type} is not supported."),
        };
    }

    private static IEnumerable<(byte[] Key, byte[] Value)> BlockEntries(byte[] block)
    {
        if (block.Length < 4)
        {
            throw new InvalidDataException("Block is too small.");
        }

        var restartCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4, 4));
        var end = block.Length - 4 - 4 * restartCount;
        if (end < 0)
        {
            throw new InvalidDataException("Block restart array is corrupt.");
        }

        var pos = 0;
        var key = Array.Empty<byte>();
        while (pos < end)
        {
            var shared = (int)Varint.ReadUInt64(block, ref pos);
            var nonShared = (int)Varint.ReadUInt64(block, ref pos);
            var valueLength = (int)Varint.ReadUInt64(block, ref pos);
            if (shared > key.Length)
            {
                throw new InvalidDataException("Block entry shares more bytes than the previous key has.");
            }

            var next = new byte[shared + nonShared];
            key.AsSpan(0, shared).CopyTo(next);
            block.AsSpan(pos, nonShared).CopyTo(next.AsSpan(shared));
            pos += nonShared;
            var value = block.AsSpan(pos, valueLength).ToArray();
            pos += valueLength;
            key = next;
            yield return (key, value);
        }
    }

    internal static ulong? ParseFileNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return ulong.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}

/// <summary>Structural equality for byte-array dictionary keys.</summary>
internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj)
    {
        var hash = new HashCode();
        hash.AddBytes(obj);
        return hash.ToHashCode();
    }
}