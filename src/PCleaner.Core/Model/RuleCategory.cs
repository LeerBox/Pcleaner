namespace PCleaner.Core.Model;

/// <summary>Top level grouping used by the user interface.</summary>
public enum RuleCategory
{
    /// <summary>Windows components that live outside the user profile (usually require administrator rights).</summary>
    WindowsSystem = 0,

    /// <summary>Per-user Windows caches and temporary files.</summary>
    WindowsUser = 1,

    /// <summary>Third-party application caches (graphics drivers, runtimes, ...).</summary>
    Applications = 2,

    /// <summary>Web browser caches (one rule group per detected browser profile).</summary>
    Browsers = 3,
}

/// <summary>What a rule does when it is executed.</summary>
public enum RuleAction
{
    /// <summary>Delete files (and optionally empty folders) that match the rule's <see cref="CleanupRule.Targets"/>.</summary>
    DeleteFiles = 0,

    /// <summary>Empty the Recycle Bin of all drives through the Windows Shell API.</summary>
    EmptyRecycleBin = 1,

    /// <summary>Flush the DNS resolver cache through the Windows DNS API.</summary>
    FlushDnsCache = 2,

    /// <summary>Clear Windows event logs through the Windows Event Log API.</summary>
    ClearEventLogs = 3,

    /// <summary>Run a built-in Microsoft Disk Cleanup handler (the same COM handlers cleanmgr.exe uses).</summary>
    DiskCleanupHandler = 4,

    /// <summary>Run <c>DISM /Online /Cleanup-Image /StartComponentCleanup</c> (component store / WinSxS).</summary>
    ComponentStoreCleanup = 5,

    /// <summary>
    /// Delete rows from allow-listed tables of an application database (SQLite) while keeping the file and every
    /// other table intact - used for browser form-autofill history, where the same database also holds payment
    /// methods, search engines and sign-in tokens that must never be touched.
    /// </summary>
    PurgeDatabaseRows = 6,

    /// <summary>
    /// Forget what an application remembers about opened files: recent-file lists, Open/Save dialog history, the
    /// tabs an editor restores at start. Works on fixed, reviewed stores (<see cref="CleanupRule.History"/>) and
    /// never deletes the application's settings.
    /// </summary>
    ForgetHistory = 7,
}