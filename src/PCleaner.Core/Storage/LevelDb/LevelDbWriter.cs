using System.Buffers.Binary;

namespace PCleaner.Core.Storage.LevelDb;

/// <summary>
/// Writes a brand-new LevelDB database that contains exactly the given entries: <c>CURRENT</c>, a one-edit
/// <c>MANIFEST-000001</c> (what <c>DBImpl::NewDB</c> produces) and a single log file <c>000003.log</c> holding the
/// data as ordinary write batches. LevelDB recovers such a database on open like any log that was not compacted yet.
/// </summary>
public static class LevelDbWriter
{
    private const string Comparator = "leveldb.BytewiseComparator";
    private const int MaxBatchEntries = 512;
    private const int MaxBatchBytes = 2 * 1024 * 1024;

    public static void WriteFreshDatabase(string directory, IEnumerable<LevelDbEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(entries);

        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException("Target directory for the new database is not empty.");
        }

        // MANIFEST: comparator, log number 0, next file number 2, last sequence 0 (db/db_impl.cc NewDB).
        var edit = new List<byte>();
        Varint.Write(edit, 1);
        Varint.Write(edit, (ulong)Comparator.Length);
        edit.AddRange(System.Text.Encoding.ASCII.GetBytes(Comparator));
        Varint.Write(edit, 2);
        Varint.Write(edit, 0);
        Varint.Write(edit, 3);
        Varint.Write(edit, 2);
        Varint.Write(edit, 4);
        Varint.Write(edit, 0);

        using (var manifest = new LogWriter(Path.Combine(directory, "MANIFEST-000001")))
        {
            manifest.AddRecord(edit.ToArray());
        }

        File.WriteAllText(Path.Combine(directory, "CURRENT"), "MANIFEST-000001\n");

        using var log = new LogWriter(Path.Combine(directory, "000003.log"));
        ulong sequence = 1;
        var batch = new List<byte>();
        var batchCount = 0;

        void Flush()
        {
            if (batchCount == 0)
            {
                return;
            }

            var record = new byte[12 + batch.Count];
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8, 4), (uint)batchCount);
            batch.CopyTo(record, 12);
            log.AddRecord(record);
            sequence += (ulong)batchCount;
            batch.Clear();
            batchCount = 0;
        }

        foreach (var entry in entries)
        {
            batch.Add(1); // kTypeValue
            Varint.Write(batch, (ulong)entry.Key.Length);
            batch.AddRange(entry.Key);
            Varint.Write(batch, (ulong)entry.Value.Length);
            batch.AddRange(entry.Value);
            batchCount++;
            if (batchCount >= MaxBatchEntries || batch.Count >= MaxBatchBytes)
            {
                Flush();
            }
        }

        Flush();
    }

    /// <summary>Log file framing (<c>db/log_writer.cc</c>): 32 KiB blocks, 7-byte headers, fragmented records.</summary>
    private sealed class LogWriter : IDisposable
    {
        private readonly FileStream _stream;
        private int _blockOffset;

        public LogWriter(string path)
        {
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        public void AddRecord(ReadOnlySpan<byte> record)
        {
            var begin = true;
            var left = record.Length;
            var offset = 0;
            do
            {
                var leftover = LevelDbReader.LogBlockSize - _blockOffset;
                if (leftover < LevelDbReader.LogHeaderLength)
                {
                    if (leftover > 0)
                    {
                        _stream.Write(new byte[leftover]);
                    }

                    _blockOffset = 0;
                }

                var available = LevelDbReader.LogBlockSize - _blockOffset - LevelDbReader.LogHeaderLength;
                var fragment = Math.Min(left, available);
                var end = left == fragment;
                byte type = begin && end ? (byte)1 : begin ? (byte)2 : end ? (byte)4 : (byte)3;
                Emit(type, record.Slice(offset, fragment));
                offset += fragment;
                left -= fragment;
                begin = false;
            }
            while (left > 0);
        }

        private void Emit(byte type, ReadOnlySpan<byte> payload)
        {
            Span<byte> header = stackalloc byte[LevelDbReader.LogHeaderLength];
            var crc = Crc32C.Mask(Crc32C.Extend(Crc32C.Compute([type]), payload));
            BinaryPrimitives.WriteUInt32LittleEndian(header[..4], crc);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), (ushort)payload.Length);
            header[6] = type;
            _stream.Write(header);
            _stream.Write(payload);
            _blockOffset += LevelDbReader.LogHeaderLength + payload.Length;
        }

        public void Dispose()
        {
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
        }
    }
}