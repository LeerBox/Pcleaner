using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PCleaner.Core.Windows;

/// <summary>P/Invoke declarations. Kept in one place so they can be reviewed easily.</summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const uint SHERB_NOCONFIRMATION = 0x00000001;
    internal const uint SHERB_NOPROGRESSUI = 0x00000002;
    internal const uint SHERB_NOSOUND = 0x00000004;

    internal const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DnsFlushResolverCache();

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    internal static partial void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    internal const int SHCNE_UPDATEDIR = 0x00001000;
    internal const uint SHCNF_PATHW = 0x0005;

    // ---- Window enumeration / graceful close (ApplicationCloser)

    internal const uint GW_HWNDNEXT = 2;
    internal const uint WM_CLOSE = 0x0010;
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TOOLWINDOW = 0x00000080;
    internal const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetTopWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // ---- Starting a process with the desktop user's (non-elevated) token (ProcessLauncher)

    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    internal const uint TOKEN_DUPLICATE = 0x0002;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    internal const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    internal const int SECURITY_IMPERSONATION = 2;
    internal const int TOKEN_PRIMARY = 1;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetShellWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessWithToken(IntPtr hToken, uint dwLogonFlags, string? lpApplicationName, Span<char> lpCommandLine, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    // ---- Recent-file stores (HistoryPurger)

    internal const uint KEY_READ = 0x20019;
    internal const uint KEY_ALL_ACCESS = 0xF003F;
    internal const uint GPFIDL_DEFAULT = 0;

    /// <summary>Loads a per-application registry hive (a Store app's <c>settings.dat</c>) without privileges.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegLoadAppKey(string lpFile, out IntPtr phkResult, uint samDesired, uint dwOptions, uint reserved);

    /// <summary>Raw value read - needed for the application-hive value types (0x5f5e10x) that <see cref="Microsoft.Win32.RegistryKey"/> cannot return.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegQueryValueEx(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType, Span<byte> lpData, ref uint lpcbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegSetValueEx(IntPtr hKey, string lpValueName, uint reserved, uint dwType, ReadOnlySpan<byte> lpData, uint cbData);

    /// <summary>Resolves a shell item list (PIDL) stored in an MRU value to its file system path.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHGetPathFromIDListEx", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SHGetPathFromIDListEx(IntPtr pidl, Span<char> pszPath, uint cchPath, uint uOpts);

    /// <summary>Removes every item from the shell's usage data: Recent folder, Jump-List "Recent"/"Frequent", Start's recent items.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHAddToRecentDocs")]
    internal static partial void SHAddToRecentDocs(uint uFlags, IntPtr pv);
}