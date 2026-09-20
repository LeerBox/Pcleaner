using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PCleaner.Core.Model;
using PCleaner.Core.Windows;

namespace PCleaner.Core.Engine;

/// <summary>What a history store currently remembers.</summary>
public sealed class HistoryInspection
{
    public bool Exists { get; init; }

    /// <summary>Number of remembered items (files, folders, searches, tabs).</summary>
    public int EntryCount { get; init; }

    /// <summary>The remembered items, capped for the UI.</summary>
    public IReadOnlyList<DatabaseEntry> Entries { get; init; } = [];

    /// <summary>Bytes the store occupies (registry data, tab files).</summary>
    public long Bytes { get; init; }

    /// <summary>True when the owning application holds the store open.</summary>
    public bool IsLocked { get; init; }

    public string? Error { get; init; }
}

public sealed class HistoryPurgeResult
{
    public int EntriesRemoved { get; init; }

    public long BytesFreed { get; init; }

    public bool IsLocked { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Reads and clears the places where Windows and applications remember what was opened. Every store kind is
/// implemented here with fixed logic - registry keys are only ever emptied below HKEY_CURRENT_USER, files are
/// edited in place or deleted inside the application's own state folder, and settings that are not history
/// (favourites, window layout, preferences) are left alone. <see cref="SafetyGuard.ValidateHistoryTarget"/>
/// additionally restricts which locations a rule may name.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class HistoryPurger
{
    private const int EntryCap = 300;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorFileNotFound = 2;

    /// <summary>Registry values that only hold the ordering of an MRU list, never an entry of their own.</summary>
    private static readonly HashSet<string> OrderValueNames = new(StringComparer.OrdinalIgnoreCase) { "MRUList", "MRUListEx" };

    /// <summary>String values that make a subkey represent one remembered file (Office "Reading Locations", Adobe "cRecentFiles").</summary>
    private static readonly string[] EntryKeyPathValues = ["File Path", "tDIText", "tFileName"];

    /// <summary>Keys whose REG_BINARY values are lists of NUL-terminated UTF-16 strings (7-Zip's history format).</summary>
    private static readonly string[] StringListKeyMarkers = [@"\7-Zip\"];

    public static HistoryInspection Inspect(HistoryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            return target.Store switch
            {
                HistoryStore.RegistryKeyContents => InspectRegistry(target, valuesOnly: false),
                HistoryStore.RegistryValues => InspectRegistry(target, valuesOnly: true),
                HistoryStore.NotepadTabs => InspectNotepadTabs(target),
                HistoryStore.NotepadRecentFiles => InspectNotepadRecentFiles(target),
                HistoryStore.NotepadPlusPlusSession => InspectNppSession(target),
                HistoryStore.NotepadPlusPlusRecentFiles => InspectNppHistory(target),
                HistoryStore.VlcRecentMedia => InspectVlc(target),
                _ => new HistoryInspection { Error = "Unknown history store." },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or XmlException or JsonException or InvalidDataException)
        {
            return IsSharingViolation(ex)
                ? new HistoryInspection { Exists = true, IsLocked = true, Error = "The application is using this store." }
                : new HistoryInspection { Exists = true, Error = ex.Message };
        }
    }

    public static HistoryPurgeResult Purge(HistoryTarget target, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(target);
        var inspection = Inspect(target);
        if (!inspection.Exists || inspection.Error is not null)
        {
            return new HistoryPurgeResult { IsLocked = inspection.IsLocked, Error = inspection.Error };
        }

        if (inspection.EntryCount == 0 && inspection.Bytes == 0)
        {
            return new HistoryPurgeResult();
        }

        if (dryRun)
        {
            return new HistoryPurgeResult { EntriesRemoved = inspection.EntryCount, BytesFreed = inspection.Bytes };
        }

        try
        {
            switch (target.Store)
            {
                case HistoryStore.RegistryKeyContents:
                    ClearRegistryKeys(target);
                    break;
                case HistoryStore.RegistryValues:
                    DeleteRegistryValues(target);
                    break;
                case HistoryStore.NotepadTabs:
                    DeleteNotepadTabs(target);
                    break;
                case HistoryStore.NotepadRecentFiles:
                    ClearNotepadRecentFiles(target);
                    break;
                case HistoryStore.NotepadPlusPlusSession:
                    DeleteNppSession(target);
                    break;
                case HistoryStore.NotepadPlusPlusRecentFiles:
                    ClearNppHistory(target);
                    break;
                case HistoryStore.VlcRecentMedia:
                    ClearVlc(target);
                    break;
                default:
                    return new HistoryPurgeResult { Error = "Unknown history store." };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or XmlException or InvalidDataException)
        {
            return IsSharingViolation(ex)
                ? new HistoryPurgeResult { IsLocked = true, Error = "The application is using this store." }
                : new HistoryPurgeResult { Error = ex.Message };
        }

        return new HistoryPurgeResult { EntriesRemoved = inspection.EntryCount, BytesFreed = inspection.Bytes };
    }

    // ------------------------------------------------------------------ registry

    /// <summary>Concrete key paths (below HKCU) for a location that may contain one <c>*</c> segment.</summary>
    internal static IReadOnlyList<string> ExpandKeyPaths(string location)
    {
        var segments = location.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var star = Array.IndexOf(segments, "*");
        if (star < 0)
        {
            return [string.Join('\\', segments)];
        }

        var parent = string.Join('\\', segments.Take(star));
        var rest = string.Join('\\', segments.Skip(star + 1));
        using var key = Registry.CurrentUser.OpenSubKey(parent, writable: false);
        if (key is null)
        {
            return [];
        }

        return key.GetSubKeyNames()
            .Select(sub => rest.Length == 0 ? $"{parent}\\{sub}" : $"{parent}\\{sub}\\{rest}")
            .ToList();
    }

    private static HistoryInspection InspectRegistry(HistoryTarget target, bool valuesOnly)
    {
        var entries = new List<DatabaseEntry>();
        var count = 0;
        long bytes = 0;
        var exists = false;

        foreach (var path in ExpandKeyPaths(target.Location))
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            if (key is null)
            {
                continue;
            }

            exists = true;
            if (valuesOnly)
            {
                foreach (var name in target.ValueNames)
                {
                    if (key.GetValue(name) is null)
                    {
                        continue;
                    }

                    foreach (var entry in DecodeValue(key, name, key.GetValueKind(name), path, target.Label))
                    {
                        Add(entries, entry, ref count, ref bytes);
                    }
                }
            }
            else
            {
                CollectKey(key, path, target.Label, entries, ref count, ref bytes, depth: 0);
            }
        }

        return new HistoryInspection { Exists = exists, EntryCount = count, Entries = entries, Bytes = bytes };
    }

    private static void CollectKey(RegistryKey key, string keyPath, string label, List<DatabaseEntry> entries, ref int count, ref long bytes, int depth)
    {
        var hadEntries = false;
        foreach (var name in key.GetValueNames())
        {
            if (OrderValueNames.Contains(name))
            {
                continue;
            }

            RegistryValueKind kind;
            try
            {
                kind = key.GetValueKind(name);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in DecodeValue(key, name, kind, keyPath, label))
            {
                hadEntries = true;
                Add(entries, entry, ref count, ref bytes);
            }
        }

        foreach (var sub in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(sub, writable: false);
            if (subKey is null)
            {
                continue;
            }

            if (TryDecodeEntryKey(subKey, label, out var entry))
            {
                Add(entries, entry, ref count, ref bytes);
                continue;
            }

            // Explorer's RecentDocs repeats the root list once per file extension - list it only once.
            if (hadEntries && depth == 0)
            {
                continue;
            }

            if (depth < 3)
            {
                CollectKey(subKey, $"{keyPath}\\{sub}", label, entries, ref count, ref bytes, depth + 1);
            }
        }
    }

    private static void Add(List<DatabaseEntry> entries, DatabaseEntry entry, ref int count, ref long bytes)
    {
        count++;
        bytes += entry.Bytes;
        if (entries.Count < EntryCap)
        {
            entries.Add(entry);
        }
    }

    /// <summary>A subkey that stands for one remembered file: Office "Document 0" (File Path) or Adobe "c1" (tDIText).</summary>
    private static bool TryDecodeEntryKey(RegistryKey key, string label, out DatabaseEntry entry)
    {
        entry = null!;
        foreach (var valueName in EntryKeyPathValues)
        {
            if (key.GetValue(valueName) is string path && path.Length > 0)
            {
                DateTime? when = null;
                if (key.GetValue("Datetime") is string office && DateTime.TryParse(office, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                {
                    when = parsed.ToUniversalTime();
                }
                else if (key.GetValue("sDate") is string pdfDate && TryParsePdfDate(pdfDate, out var pdf))
                {
                    when = pdf;
                }

                entry = new DatabaseEntry(label, path, 0, when, KeyDataSize(key));
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^\[F[0-9A-F]{8}\]\[T([0-9A-F]{16})\]\[O[0-9A-F]{8}\]\*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfficeMruPattern();

    private static IEnumerable<DatabaseEntry> DecodeValue(RegistryKey key, string name, RegistryValueKind kind, string keyPath, string label)
    {
        switch (kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string text || text.Length == 0)
                {
                    yield break;
                }

                var size = (text.Length + 1) * 2L;
                if (keyPath.EndsWith(@"\RunMRU", StringComparison.OrdinalIgnoreCase) && text.EndsWith("\\1", StringComparison.Ordinal))
                {
                    text = text[..^2];
                }

                var office = OfficeMruPattern().Match(text);
                if (office.Success)
                {
                    DateTime? when = null;
                    if (long.TryParse(office.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime) && fileTime > 0)
                    {
                        try
                        {
                            when = DateTime.FromFileTimeUtc(fileTime);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                        }
                    }

                    yield return new DatabaseEntry(label, office.Groups[2].Value, 0, when, size);
                    yield break;
                }

                yield return new DatabaseEntry(label, text, 0, null, size);
                yield break;
            }

            case RegistryValueKind.MultiString:
            {
                if (key.GetValue(name) is string[] lines && lines.Length > 0)
                {
                    yield return new DatabaseEntry(label, string.Join(", ", lines), 0, null, lines.Sum(l => (l.Length + 1) * 2L));
                }

                yield break;
            }

            case RegistryValueKind.Binary:
            {
                if (key.GetValue(name) is not byte[] data || data.Length == 0)
                {
                    yield break;
                }

                if (StringListKeyMarkers.Any(m => keyPath.Contains(m, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var item in DecodeStringList(data))
                    {
                        yield return new DatabaseEntry(label, item, 0, null, (item.Length + 1) * 2L);
                    }

                    yield break;
                }

                if (keyPath.Contains(@"ComDlg32\LastVisitedPidlMRU", StringComparison.OrdinalIgnoreCase))
                {
                    var exe = LeadingUtf16(data, out var consumed);
                    var folder = consumed < data.Length ? ShellItemListPath(data.AsSpan(consumed)) : null;
                    var text = folder is null ? exe ?? "(folder)" : exe is null ? folder : $"{folder}  ({exe})";
                    yield return new DatabaseEntry(label, text, 0, null, data.Length);
                    yield break;
                }

                if (keyPath.Contains(@"ComDlg32\OpenSavePidlMRU", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new DatabaseEntry(label, ShellItemListPath(data) ?? "(file entry)", 0, null, data.Length);
                    yield break;
                }

                yield return new DatabaseEntry(label, LeadingUtf16(data, out _) ?? "(binary entry)", 0, null, data.Length);
                yield break;
            }

            default:
                yield break; // DWORD/QWORD/None: metadata such as a reading position, not an entry.
        }
    }

    private static long KeyDataSize(RegistryKey key)
    {
        long size = 0;
        foreach (var name in key.GetValueNames())
        {
            size += key.GetValue(name) switch
            {
                string s => (s.Length + 1) * 2L,
                byte[] b => b.Length,
                string[] a => a.Sum(l => (l.Length + 1) * 2L),
                int => 4,
                long => 8,
                _ => 0,
            };
        }

        return size;
    }

    /// <summary>The UTF-16LE string at the start of a binary MRU value (Explorer's RecentDocs, WordWheelQuery).</summary>
    internal static string? LeadingUtf16(ReadOnlySpan<byte> data, out int consumed)
    {
        consumed = 0;
        var chars = new StringBuilder();
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i, 2));
            if (c == '\0')
            {
                consumed = i + 2;
                break;
            }

            if (char.IsControl(c) && c != '\t')
            {
                return null;
            }

            chars.Append(c);
        }

        if (consumed == 0)
        {
            consumed = data.Length;
        }

        return chars.Length == 0 ? null : chars.ToString();
    }

    /// <summary>7-Zip's history format: NUL-terminated UTF-16LE strings back to back.</summary>
    internal static List<string> DecodeStringList(ReadOnlySpan<byte> data)
    {
        var list = new List<string>();
        var offset = 0;
        while (offset + 1 < data.Length)
        {
            var text = LeadingUtf16(data[offset..], out var consumed);
            if (consumed <= 0)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                list.Add(text);
            }

            offset += consumed;
        }

        return list;
    }

    /// <summary>Resolves a shell item list (as stored by the Open/Save dialogs) to a file system path via the shell.</summary>
    internal static string? ShellItemListPath(ReadOnlySpan<byte> pidl)
    {
        if (!IsWellFormedItemList(pidl))
        {
            return null;
        }

        var buffer = new char[1024];
        var native = Marshal.AllocHGlobal(pidl.Length);
        try
        {
            Marshal.Copy(pidl.ToArray(), 0, native, pidl.Length);
            if (!NativeMethods.SHGetPathFromIDListEx(native, buffer, (uint)buffer.Length, NativeMethods.GPFIDL_DEFAULT))
            {
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }

        var end = Array.IndexOf(buffer, '\0');
        var path = new string(buffer, 0, end < 0 ? buffer.Length : end);
        return path.Length == 0 ? null : path;
    }

    /// <summary>Walks the item sizes of an ITEMIDLIST; the shell is only asked about lists that end properly inside the data.</summary>
    internal static bool IsWellFormedItemList(ReadOnlySpan<byte> pidl)
    {
        var offset = 0;
        var items = 0;
        while (offset + 2 <= pidl.Length)
        {
            var cb = BinaryPrimitives.ReadUInt16LittleEndian(pidl.Slice(offset, 2));
            if (cb == 0)
            {
                return items > 0;
            }

            if (cb < 3 || offset + cb > pidl.Length)
            {
                return false;
            }

            offset += cb;
            items++;
        }

        return false;
    }

    private static bool TryParsePdfDate(string text, out DateTime utc)
    {
        // Adobe stores "D:YYYYMMDDHHmmss+HH'mm'"; only the leading 14 digits are needed.
        utc = default;
        if (text.StartsWith("D:", StringComparison.Ordinal) && text.Length >= 16
            && DateTime.TryParseExact(text.Substring(2, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            utc = local.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static void ClearRegistryKeys(HistoryTarget target)
    {
        foreach (var path in ExpandKeyPaths(target.Location))
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetValueNames())
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            foreach (var sub in key.GetSubKeyNames())
            {
                key.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            }
        }
    }

    private static void DeleteRegistryValues(HistoryTarget target)
    {
        foreach (var path in ExpandKeyPaths(target.Location))
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key is null)
            {
                continue;
            }

            foreach (var name in target.ValueNames)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
    }

    // ------------------------------------------------------------------ Windows 11 Notepad

    /// <summary>Tab files are "&lt;guid&gt;.bin"; "&lt;guid&gt;.0.bin" / ".1.bin" are their double-buffered state companions.</summary>
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.bin$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotepadTabFilePattern();

    private static HistoryInspection InspectNotepadTabs(HistoryTarget target)
    {
        var tabState = Path.Combine(target.Location, "TabState");
        var windowState = Path.Combine(target.Location, "WindowState");
        if (!Directory.Exists(tabState) && !Directory.Exists(windowState))
        {
            return new HistoryInspection { Exists = false };
        }

        var entries = new List<DatabaseEntry>();
        var count = 0;
        long bytes = 0;
        foreach (var file in NotepadStateFiles(target.Location))
        {
            var info = new FileInfo(file);
            bytes += info.Length;
            if (!NotepadTabFilePattern().IsMatch(info.Name))
            {
                continue;
            }

            var tab = ReadNotepadTab(file);
            if (tab is null)
            {
                continue;
            }

            count++;
            if (entries.Count < EntryCap)
            {
                entries.Add(new DatabaseEntry(tab.Value.IsSaved ? "Open file" : "Unsaved tab", tab.Value.Title, 0, info.LastWriteTimeUtc, info.Length));
            }
        }

        return new HistoryInspection { Exists = true, EntryCount = count, Entries = entries, Bytes = bytes };
    }

    private static IEnumerable<string> NotepadStateFiles(string localState)
    {
        foreach (var folder in new[] { "TabState", "WindowState" })
        {
            var directory = Path.Combine(localState, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Reads the header of a Notepad tab file: "NP", sequence number, type flag (1 = file on disk with its UTF-16
    /// path, 0 = never-saved tab whose text exists only here). For unsaved tabs the first line serves as title,
    /// like Notepad's own tab caption.
    /// </summary>
    internal static (bool IsSaved, string Title)? ReadNotepadTab(string file)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(file);
        }
        catch (IOException)
        {
            return null;
        }

        if (data.Length < 4 || data[0] != (byte)'N' || data[1] != (byte)'P')
        {
            return null;
        }

        var offset = 2;
        if (!TryReadVarint(data, ref offset, out _) || !TryReadVarint(data, ref offset, out var type))
        {
            return null;
        }

        switch (type)
        {
            case 1:
            {
                if (!TryReadVarint(data, ref offset, out var pathLength) || pathLength == 0 || pathLength > int.MaxValue / 2 || offset + (long)pathLength * 2 > data.Length)
                {
                    return (true, "(file tab)");
                }

                return (true, Encoding.Unicode.GetString(data, offset, (int)pathLength * 2));
            }

            case 0:
            {
                // unknown flag, selection start/end, 3 option bytes, option count + options, then the text length.
                if (TryReadVarint(data, ref offset, out _) && TryReadVarint(data, ref offset, out _) && TryReadVarint(data, ref offset, out _)
                    && offset + 3 <= data.Length)
                {
                    offset += 3;
                    if (TryReadVarint(data, ref offset, out var options) && offset + (int)options <= data.Length)
                    {
                        offset += (int)options;
                        if (TryReadVarint(data, ref offset, out var length) && length > 0 && offset + (long)length * 2 <= data.Length)
                        {
                            var text = Encoding.Unicode.GetString(data, offset, (int)length * 2);
                            return (false, FirstLine(text));
                        }
                    }
                }

                return (false, "(unsaved text)");
            }

            default:
                return null;
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? text : text[..end]).Trim();
        if (line.Length == 0)
        {
            return "(unsaved text)";
        }

        return line.Length > 60 ? line[..60] + "…" : line;
    }

    /// <summary>Unsigned LEB128, as used throughout Notepad's state files.</summary>
    internal static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (offset < data.Length && shift < 64)
        {
            var b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static void DeleteNotepadTabs(HistoryTarget target)
    {
        foreach (var file in NotepadStateFiles(target.Location).ToList())
        {
            File.Delete(file);
        }
    }

    private const uint AppHiveStringType = 0x5f5e10c;

    private static HistoryInspection InspectNotepadRecentFiles(HistoryTarget target)
    {
        if (!File.Exists(target.Location))
        {
            return new HistoryInspection { Exists = false };
        }

        using var hive = AppHive.Open(target.Location, writable: false, out var locked);
        if (hive is null)
        {
            return locked
                ? new HistoryInspection { Exists = true, IsLocked = true, Error = "Notepad is using its settings." }
                : new HistoryInspection { Exists = true, Error = "The settings file could not be opened." };
        }

        using var localState = hive.OpenSubKey("LocalState", writable: false);
        if (localState is null)
        {
            return new HistoryInspection { Exists = true };
        }

        var raw = AppHive.ReadRaw(localState, "RecentFiles", out _);
        var paths = DecodeNotepadRecentFiles(raw);
        var entries = paths.Take(EntryCap).Select(p => new DatabaseEntry(target.Label, p, 0, null, (p.Length + 1) * 2L)).ToList();
        return new HistoryInspection { Exists = true, EntryCount = paths.Count, Entries = entries, Bytes = raw?.Length ?? 0 };
    }

    /// <summary>The value is a UTF-16 JSON array of paths followed by an 8-byte FILETIME trailer.</summary>
    internal static List<string> DecodeNotepadRecentFiles(byte[]? raw)
    {
        if (raw is null || raw.Length <= 8)
        {
            return [];
        }

        var text = Encoding.Unicode.GetString(raw, 0, raw.Length - 8).TrimEnd('\0').Trim();
        if (text.Length == 0 || text[0] != '[')
        {
            return [];
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ClearNotepadRecentFiles(HistoryTarget target)
    {
        using var hive = AppHive.Open(target.Location, writable: true, out var locked);
        if (hive is null)
        {
            throw locked ? new IOException("Notepad is using its settings.", unchecked((int)0x80070020)) : new IOException("The settings file could not be opened.");
        }

        using var localState = hive.OpenSubKey("LocalState", writable: true);
        if (localState is null)
        {
            return;
        }

        var raw = AppHive.ReadRaw(localState, "RecentFiles", out var type);
        if (raw is null)
        {
            return;
        }

        // Same layout Notepad writes: UTF-16 text (NUL-terminated when the original was) + FILETIME of the change.
        var keepNul = raw.Length >= 10 && raw[raw.Length - 10] == 0 && raw[raw.Length - 9] == 0;
        var text = Encoding.Unicode.GetBytes(keepNul ? "[]\0" : "[]");
        var data = new byte[text.Length + 8];
        text.CopyTo(data, 0);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(text.Length), DateTime.UtcNow.ToFileTimeUtc());
        AppHive.WriteRaw(localState, "RecentFiles", type == 0 ? AppHiveStringType : type, data);
        localState.Flush();
    }

    // ------------------------------------------------------------------ Notepad++

    private static HistoryInspection InspectNppSession(HistoryTarget target)
    {
        var session = Path.Combine(target.Location, "session.xml");
        var backup = Path.Combine(target.Location, "backup");
        var corruption = session + ".inCaseOfCorruption.bak";
        if (!File.Exists(session) && !Directory.Exists(backup) && !File.Exists(corruption))
        {
            return new HistoryInspection { Exists = false };
        }

        var entries = new List<DatabaseEntry>();
        var count = 0;
        long bytes = 0;
        if (File.Exists(session))
        {
            bytes += new FileInfo(session).Length;
            var document = LoadXml(session);
            foreach (var file in document.Descendants("Session").Descendants("File"))
            {
                var name = (string?)file.Attribute("filename");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var unsaved = !string.IsNullOrEmpty((string?)file.Attribute("backupFilePath"));
                count++;
                if (entries.Count < EntryCap)
                {
                    entries.Add(new DatabaseEntry(unsaved ? "Unsaved tab" : "Open tab", name, 0, null, 0));
                }
            }
        }

        if (File.Exists(corruption))
        {
            bytes += new FileInfo(corruption).Length;
        }

        if (Directory.Exists(backup))
        {
            bytes += Directory.EnumerateFiles(backup).Sum(f => new FileInfo(f).Length);
        }

        return new HistoryInspection { Exists = true, EntryCount = count, Entries = entries, Bytes = bytes };
    }

    private static void DeleteNppSession(HistoryTarget target)
    {
        foreach (var file in new[] { Path.Combine(target.Location, "session.xml"), Path.Combine(target.Location, "session.xml.inCaseOfCorruption.bak") })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        var backup = Path.Combine(target.Location, "backup");
        if (Directory.Exists(backup))
        {
            foreach (var file in Directory.EnumerateFiles(backup).ToList())
            {
                File.Delete(file);
            }
        }
    }

    private static HistoryInspection InspectNppHistory(HistoryTarget target)
    {
        var config = Path.Combine(target.Location, "config.xml");
        if (!File.Exists(config))
        {
            return new HistoryInspection { Exists = false };
        }

        var document = LoadXml(config);
        var files = document.Root?.Element("History")?.Elements("File").ToList() ?? [];
        var entries = files
            .Select(f => (string?)f.Attribute("filename"))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(EntryCap)
            .Select(n => new DatabaseEntry(target.Label, n!, 0, null, (n!.Length + 1) * 2L))
            .ToList();
        return new HistoryInspection { Exists = true, EntryCount = files.Count, Entries = entries, Bytes = entries.Sum(e => e.Bytes) };
    }

    private static void ClearNppHistory(HistoryTarget target)
    {
        var config = Path.Combine(target.Location, "config.xml");
        var hadBom = HasUtf8Bom(config);
        var document = LoadXml(config);
        var history = document.Root?.Element("History");
        if (history is null)
        {
            return;
        }

        foreach (var node in history.Nodes().ToList())
        {
            node.Remove(); // the <File> children and the whitespace between them; the <History> attributes stay
        }

        SaveXml(document, config, hadBom);
    }

    private static XDocument LoadXml(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void SaveXml(XDocument document, string path, bool utf8Bom)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(utf8Bom),
            OmitXmlDeclaration = document.Declaration is null,
            Indent = false,
        };
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = XmlWriter.Create(stream, settings);
        document.Save(writer);
    }

    private static bool HasUtf8Bom(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> head = stackalloc byte[3];
        return stream.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    // ------------------------------------------------------------------ VLC

    /// <summary>INI keys VLC uses for its memory: recent media and resume positions, the last dialog folder, the last network URL.</summary>
    private static readonly (string Section, string Key)[] VlcKeys =
    [
        ("RecentsMRL", "list"),
        ("RecentsMRL", "times"),
        ("General", "filedialog-path"),
        ("OpenDialog", "netMRL"),
    ];

    private static HistoryInspection InspectVlc(HistoryTarget target)
    {
        if (!File.Exists(target.Location))
        {
            return new HistoryInspection { Exists = false };
        }

        var lines = File.ReadAllLines(target.Location);
        var entries = new List<DatabaseEntry>();
        var count = 0;
        long bytes = 0;
        foreach (var (section, key, value) in IniValues(lines))
        {
            if (!VlcKeys.Any(k => k.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            bytes += value.Length;
            if (key.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var mrl in SplitQtList(value))
                {
                    count++;
                    if (entries.Count < EntryCap)
                    {
                        entries.Add(new DatabaseEntry(target.Label, MrlToPath(mrl), 0, null, mrl.Length));
                    }
                }
            }
            else if (key.Equals("netMRL", StringComparison.OrdinalIgnoreCase) || key.Equals("filedialog-path", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0)
                {
                    count++;
                    entries.Add(new DatabaseEntry(key.Equals("netMRL", StringComparison.OrdinalIgnoreCase) ? "Network stream" : "Last folder", MrlToPath(value), 0, null, value.Length));
                }
            }
        }

        return new HistoryInspection { Exists = true, EntryCount = count, Entries = entries, Bytes = bytes };
    }

    private static void ClearVlc(HistoryTarget target)
    {
        var raw = File.ReadAllBytes(target.Location);
        var hadBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(raw, hadBom ? 3 : 0, raw.Length - (hadBom ? 3 : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var kept = new List<string>();
        string? section = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var content = trimmed.Trim();
            if (content.StartsWith('[') && content.EndsWith(']'))
            {
                section = content[1..^1];
            }
            else if (section is not null)
            {
                var eq = content.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    var key = content[..eq].Trim();
                    if (VlcKeys.Any(k => k.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                }
            }

            kept.Add(trimmed);
        }

        var output = new UTF8Encoding(false).GetBytes(string.Join(newline, kept));
        var prefix = hadBom ? new byte[] { 0xEF, 0xBB, 0xBF } : [];
        File.WriteAllBytes(target.Location, [.. prefix, .. output]);
    }

    internal static IEnumerable<(string Section, string Key, string Value)> IniValues(IEnumerable<string> lines)
    {
        string? section = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (section is not null && eq > 0)
            {
                yield return (section, line[..eq].Trim(), line[(eq + 1)..].Trim());
            }
        }
    }

    /// <summary>Qt writes a QStringList as comma separated items; items containing commas are quoted.</summary>
    internal static List<string> SplitQtList(string value)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in value)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (c == ',' && !quoted)
            {
                items.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        items.Add(current.ToString().Trim());
        return items.Where(i => i.Length > 0).ToList();
    }

    internal static string MrlToPath(string mrl)
    {
        if (Uri.TryCreate(mrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return mrl;
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsSharingViolation(Exception ex)
        => ex is IOException io && (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    /// <summary>A Store app's settings.dat, loaded as a private registry hive (no privileges needed, no HKLM/HKU involvement).</summary>
    internal static class AppHive
    {
        public static RegistryKey? Open(string path, bool writable, out bool locked)
        {
            var rc = NativeMethods.RegLoadAppKey(path, out var handle, writable ? NativeMethods.KEY_ALL_ACCESS : NativeMethods.KEY_READ, 0, 0);
            locked = rc is ErrorSharingViolation or ErrorLockViolation;
            return rc == 0 ? RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true)) : null;
        }

        public static byte[]? ReadRaw(RegistryKey key, string name, out uint type)
        {
            type = 0;
            uint size = 0;
            var rc = NativeMethods.RegQueryValueEx(key.Handle.DangerousGetHandle(), name, IntPtr.Zero, out type, Span<byte>.Empty, ref size);
            if (rc == ErrorFileNotFound)
            {
                return null;
            }

            if (rc != 0 && rc != 234 /* ERROR_MORE_DATA */)
            {
                throw new IOException($"Reading '{name}' failed (error {rc}).");
            }

            var data = new byte[size];
            rc = NativeMethods.RegQueryValueEx(key.Handle.DangerousGetHandle(), name, IntPtr.Zero, out type, data, ref size);
            if (rc != 0)
            {
                throw new IOException($"Reading '{name}' failed (error {rc}).");
            }

            return size == data.Length ? data : data[..(int)size];
        }

        public static void WriteRaw(RegistryKey key, string name, uint type, byte[] data)
        {
            var rc = NativeMethods.RegSetValueEx(key.Handle.DangerousGetHandle(), name, 0, type, data, (uint)data.Length);
            if (rc != 0)
            {
                throw new IOException($"Writing '{name}' failed (error {rc}).");
            }
        }
    }
}