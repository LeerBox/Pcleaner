using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PCleaner.Core.Windows;

/// <summary>Recycle Bin access through the documented Shell API (never by touching $Recycle.Bin folders).</summary>
[SupportedOSPlatform("windows")]
public static class RecycleBin
{
    /// <summary>Returns the total size and item count of the Recycle Bin across all drives.</summary>
    public static (long Bytes, long Items) Query()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        var hr = NativeMethods.SHQueryRecycleBinW(null, ref info);
        return hr == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin on all drives without confirmation, progress UI or sound.</summary>
    /// <returns>True on success or when the bin was already empty.</returns>
    public static bool Empty()
    {
        var hr = NativeMethods.SHEmptyRecycleBinW(
            IntPtr.Zero,
            null,
            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);

        // S_OK, or E_UNEXPECTED (0x8000FFFF) which the shell returns when the bin is already empty.
        return hr == 0 || hr == unchecked((int)0x8000FFFF);
    }
}

/// <summary>DNS resolver cache access through the documented DNS API.</summary>
[SupportedOSPlatform("windows")]
public static class DnsCache
{
    public static bool Flush()
    {
        try
        {
            return NativeMethods.DnsFlushResolverCache();
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Schedules locked files for deletion at the next reboot (requires administrator rights).</summary>
[SupportedOSPlatform("windows")]
public static class DelayedDelete
{
    public static bool Schedule(string path)
    {
        try
        {
            return NativeMethods.MoveFileEx(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch (Exception)
        {
            return false;
        }
    }
}