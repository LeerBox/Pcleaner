using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PCleaner.Core.Logging;

namespace PCleaner.Core.Windows;

/// <summary>Description of a registered Disk Cleanup handler (HKLM\...\Explorer\VolumeCaches\*).</summary>
public sealed record DiskCleanupHandlerInfo(string KeyName, string DisplayName, string Description, Guid Clsid, int Priority);

/// <summary>Result of asking a handler how much space it can free.</summary>
public sealed record DiskCleanupScan(long Bytes, bool EnabledByDefault, bool Succeeded, string? Error, bool AccessDenied = false);

/// <summary>
/// Drives the built-in Microsoft Disk Cleanup handlers through the documented <c>IEmptyVolumeCache2</c> COM
/// interface - exactly what cleanmgr.exe does. This lets the application clean Windows Update leftovers,
/// Delivery Optimization files, driver packages, Windows.old, etc. using Microsoft's own, supported logic.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class DiskCleanupHandlers
{
    public const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    private const uint EVCF_ENABLEBYDEFAULT = 0x2;
    private const uint EVCF_REMOVEFROMLIST = 0x4;
    private const uint EVCF_ENABLEBYDEFAULT_AUTO = 0x8;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_ABORT = unchecked((int)0x80004004);
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    /// <summary>
    /// Handlers that are never offered: they delete the user's own files or files Microsoft recommends keeping.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverOffer = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DownloadsFolder",                 // the user's Downloads folder - never
        "Windows ESD installation files",  // needed by "Reset this PC"; Microsoft advises keeping them
    };

    /// <summary>
    /// Handlers with side effects that go beyond a cache: they are offered but disabled by default.
    /// </summary>
    public static readonly IReadOnlySet<string> ModerateRisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Previous Installations",   // Windows.old - removes the ability to roll back the last feature update
        "User file versions",       // File History versions
        "Language Pack",            // removes unused language packs / features on demand
        "Device Driver Packages",   // removes older driver versions from the driver store (no driver rollback)
        "Update Cleanup",           // safe but takes minutes and removes the ability to uninstall old updates
    };

    /// <summary>
    /// Handlers that operate on per-user data and work without elevation (the ones cleanmgr.exe offers to a
    /// standard user). Everything else needs administrator rights.
    /// </summary>
    public static readonly IReadOnlySet<string> PerUserHandlers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Downloaded Program Files", "Offline Pages Files", "Temporary Sync Files", "Delivery Optimization Files",
        "D3D Shader Cache", "Internet Cache Files", "Thumbnail Cache",
    };

    /// <summary>Reads the registered handlers (no COM activation).</summary>
    public static IReadOnlyList<DiskCleanupHandlerInfo> Enumerate()
    {
        var list = new List<DiskCleanupHandlerInfo>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: false);
            if (root is null)
            {
                return list;
            }

            foreach (var keyName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(keyName, writable: false);
                    if (key is null)
                    {
                        continue;
                    }

                    var clsidText = key.GetValue(string.Empty) as string;
                    if (string.IsNullOrWhiteSpace(clsidText) || !Guid.TryParse(clsidText, out var clsid))
                    {
                        continue;
                    }

                    var display = ResolveString(key.GetValue("Display") as string) ?? keyName;
                    var description = ResolveString(key.GetValue("Description") as string) ?? string.Empty;
                    var priority = key.GetValue("Priority") is int p ? p : 200;
                    list.Add(new DiskCleanupHandlerInfo(keyName, display, description, clsid, priority));
                }
                catch (Exception)
                {
                    // Skip malformed handler registrations.
                }
            }
        }
        catch (Exception)
        {
            // Registry not accessible.
        }

        return list.OrderBy(h => h.Priority).ThenBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Asks the handler how much it can free on the system drive.</summary>
    public static Task<DiskCleanupScan> ScanAsync(DiskCleanupHandlerInfo handler, ICleanerLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return StaThread.RunAsync(() => Execute(handler, purge: false, log, null, cancellationToken), cancellationToken);
    }

    /// <summary>Runs the handler's purge on the system drive.</summary>
    public static Task<DiskCleanupScan> PurgeAsync(DiskCleanupHandlerInfo handler, ICleanerLog log, Action<string>? status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return StaThread.RunAsync(() => Execute(handler, purge: true, log, status, cancellationToken), cancellationToken);
    }

    private static DiskCleanupScan Execute(DiskCleanupHandlerInfo handler, bool purge, ICleanerLog log, Action<string>? status, CancellationToken cancellationToken)
    {
        log ??= NullLog.Instance;
        var volume = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        object? instance = null;
        IEmptyVolumeCache? cache = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath + "\\" + handler.KeyName, writable: false);
            if (key is null)
            {
                return new DiskCleanupScan(0, false, false, "Handler registry key not found.");
            }

            var type = Type.GetTypeFromCLSID(handler.Clsid, throwOnError: false);
            if (type is null)
            {
                return new DiskCleanupScan(0, false, false, "Handler COM class not registered.");
            }

            instance = Activator.CreateInstance(type);
            cache = instance as IEmptyVolumeCache;
            if (cache is null)
            {
                return new DiskCleanupScan(0, false, false, "Handler does not implement IEmptyVolumeCache.");
            }

            uint flags = 0;
            int hr;
            var displayPtr = IntPtr.Zero;
            var descriptionPtr = IntPtr.Zero;
            var buttonPtr = IntPtr.Zero;
            try
            {
                if (cache is IEmptyVolumeCache2 cache2)
                {
                    hr = cache2.InitializeEx(key.Handle.DangerousGetHandle(), volume, handler.KeyName, out displayPtr, out descriptionPtr, out buttonPtr, ref flags);
                }
                else
                {
                    hr = cache.Initialize(key.Handle.DangerousGetHandle(), volume, out displayPtr, out descriptionPtr, ref flags);
                }
            }
            finally
            {
                FreeCoTaskMem(displayPtr);
                FreeCoTaskMem(descriptionPtr);
                FreeCoTaskMem(buttonPtr);
            }

            if (hr == S_FALSE || (flags & EVCF_REMOVEFROMLIST) != 0)
            {
                return new DiskCleanupScan(0, false, true, null);
            }

            if (hr < 0)
            {
                return hr == E_ACCESSDENIED
                    ? new DiskCleanupScan(0, false, false, "Administrator rights are required for this handler.", AccessDenied: true)
                    : new DiskCleanupScan(0, false, false, $"Initialize failed (0x{hr:X8}).");
            }

            var enabledByDefault = (flags & (EVCF_ENABLEBYDEFAULT | EVCF_ENABLEBYDEFAULT_AUTO)) != 0;
            var callback = new Callback(status, cancellationToken);

            hr = cache.GetSpaceUsed(out var spaceUsed, callback);
            if (hr == E_ACCESSDENIED)
            {
                return new DiskCleanupScan(0, enabledByDefault, false, "Administrator rights are required for this handler.", AccessDenied: true);
            }

            if (hr < 0 && hr != E_ABORT)
            {
                log.Debug($"Disk Cleanup handler '{handler.DisplayName}': GetSpaceUsed failed (0x{hr:X8}).");
                spaceUsed = 0;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new DiskCleanupScan((long)spaceUsed, enabledByDefault, false, "Cancelled.");
            }

            if (purge && spaceUsed > 0)
            {
                status?.Invoke($"Running Disk Cleanup handler: {handler.DisplayName}...");
                hr = cache.Purge(spaceUsed, callback);
                if (hr == E_ACCESSDENIED)
                {
                    return new DiskCleanupScan((long)spaceUsed, enabledByDefault, false, "Administrator rights are required for this handler.", AccessDenied: true);
                }

                if (hr < 0 && hr != E_ABORT)
                {
                    return new DiskCleanupScan((long)spaceUsed, enabledByDefault, false, $"Purge failed (0x{hr:X8}).");
                }
            }

            uint deactivateFlags = 0;
            cache.Deactivate(ref deactivateFlags);
            return new DiskCleanupScan((long)spaceUsed, enabledByDefault, true, null);
        }
        catch (Exception ex)
        {
            log.Warn($"Disk Cleanup handler '{handler.DisplayName}' failed: {ex.Message}");
            return new DiskCleanupScan(0, false, false, ex.Message);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                try
                {
                    Marshal.FinalReleaseComObject(instance);
                }
                catch (Exception)
                {
                    // Nothing more we can do.
                }
            }
        }
    }

    private static void FreeCoTaskMem(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>Resolves indirect strings such as "@%SystemRoot%\System32\dll,-123".</summary>
    private static string? ResolveString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!value.StartsWith('@'))
        {
            return value;
        }

        const int capacity = 1024;
        var buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
            var hr = SHLoadIndirectString(value, buffer, capacity, IntPtr.Zero);
            if (hr == S_OK)
            {
                var text = Marshal.PtrToStringUni(buffer);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (Exception)
        {
            // Fall through.
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string pszSource, IntPtr pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    [ComImport]
    [Guid("8FCE5227-04DA-11d1-A004-00805F8ABE06")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEmptyVolumeCache
    {
        [PreserveSig]
        int Initialize(IntPtr hkRegKey, [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume, out IntPtr ppwszDisplayName, out IntPtr ppwszDescription, ref uint pdwFlags);

        [PreserveSig]
        int GetSpaceUsed(out ulong pdwlSpaceUsed, [MarshalAs(UnmanagedType.Interface)] IEmptyVolumeCacheCallBack? picb);

        [PreserveSig]
        int Purge(ulong dwlSpaceToFree, [MarshalAs(UnmanagedType.Interface)] IEmptyVolumeCacheCallBack? picb);

        [PreserveSig]
        int ShowProperties(IntPtr hwnd);

        [PreserveSig]
        int Deactivate(ref uint pdwFlags);
    }

    [ComImport]
    [Guid("02b7e3ba-4db3-11d2-b2d9-00c04f8eec8c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEmptyVolumeCache2 : IEmptyVolumeCache
    {
        [PreserveSig]
        new int Initialize(IntPtr hkRegKey, [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume, out IntPtr ppwszDisplayName, out IntPtr ppwszDescription, ref uint pdwFlags);

        [PreserveSig]
        new int GetSpaceUsed(out ulong pdwlSpaceUsed, [MarshalAs(UnmanagedType.Interface)] IEmptyVolumeCacheCallBack? picb);

        [PreserveSig]
        new int Purge(ulong dwlSpaceToFree, [MarshalAs(UnmanagedType.Interface)] IEmptyVolumeCacheCallBack? picb);

        [PreserveSig]
        new int ShowProperties(IntPtr hwnd);

        [PreserveSig]
        new int Deactivate(ref uint pdwFlags);

        [PreserveSig]
        int InitializeEx(
            IntPtr hkRegKey,
            [MarshalAs(UnmanagedType.LPWStr)] string pcwszVolume,
            [MarshalAs(UnmanagedType.LPWStr)] string pcwszKeyName,
            out IntPtr ppwszDisplayName,
            out IntPtr ppwszDescription,
            out IntPtr ppwszBtnText,
            ref uint pdwFlags);
    }

    [ComImport]
    [Guid("6E793361-73C6-11D0-8469-00AA00442901")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEmptyVolumeCacheCallBack
    {
        [PreserveSig]
        int ScanProgress(ulong dwlSpaceUsed, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string? pcwszStatus);

        [PreserveSig]
        int PurgeProgress(ulong dwlSpaceFreed, ulong dwlSpaceToFree, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string? pcwszStatus);
    }

    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    private sealed class Callback : IEmptyVolumeCacheCallBack
    {
        private readonly Action<string>? _status;
        private readonly CancellationToken _cancellationToken;

        public Callback(Action<string>? status, CancellationToken cancellationToken)
        {
            _status = status;
            _cancellationToken = cancellationToken;
        }

        public int ScanProgress(ulong dwlSpaceUsed, uint dwFlags, string? pcwszStatus)
        {
            if (!string.IsNullOrWhiteSpace(pcwszStatus))
            {
                _status?.Invoke(pcwszStatus);
            }

            return _cancellationToken.IsCancellationRequested ? E_ABORT : S_OK;
        }

        public int PurgeProgress(ulong dwlSpaceFreed, ulong dwlSpaceToFree, uint dwFlags, string? pcwszStatus)
        {
            if (!string.IsNullOrWhiteSpace(pcwszStatus))
            {
                _status?.Invoke(pcwszStatus);
            }

            return _cancellationToken.IsCancellationRequested ? E_ABORT : S_OK;
        }
    }
}

/// <summary>Runs work on a dedicated single-threaded-apartment thread (required by shell COM handlers).</summary>
internal static class StaThread
{
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PCleaner STA worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}