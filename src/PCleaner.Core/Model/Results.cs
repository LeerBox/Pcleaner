namespace PCleaner.Core.Model;

/// <summary>A single file (or empty directory) that a scan found and that a clean would remove.</summary>
public sealed record ScanItem(string Path, long Size, bool IsDirectory, DateTime LastWriteUtc);

/// <summary>Why a rule was skipped or could only partially run.</summary>
public enum SkipReason
{
    None = 0,
    RequiresAdministrator,
    ApplicationRunning,
    LocationNotFound,
    ServiceUnavailable,
    NotSupported,
    Error,
}

/// <summary>Result of scanning a single rule.</summary>
public sealed class RuleScanResult
{
    public required CleanupRule Rule { get; init; }

    /// <summary>Items that would be deleted. Empty for rules whose action does not delete files.</summary>
    public IReadOnlyList<ScanItem> Items { get; init; } = [];

    /// <summary>Database rows that would be removed (<see cref="RuleAction.PurgeDatabaseRows"/> only, capped for the UI).</summary>
    public IReadOnlyList<DatabaseEntry> Entries { get; init; } = [];

    /// <summary>Total number of database rows that would be removed (may exceed <see cref="Entries"/>.Count).</summary>
    public int EntryCount { get; init; }

    /// <summary>Estimated bytes that will be reclaimed (sum of items, or an estimate for special actions).</summary>
    public long TotalBytes { get; init; }

    public int FileCount => Items.Count(i => !i.IsDirectory);

    public int DirectoryCount => Items.Count(i => i.IsDirectory);

    public SkipReason Skip { get; init; }

    public string? SkipDetails { get; init; }

    /// <summary>Processes (display names) that block this rule, when <see cref="Skip"/> is <see cref="SkipReason.ApplicationRunning"/>.</summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = [];

    /// <summary>Time the scan of this rule took.</summary>
    public TimeSpan Duration { get; init; }

    public bool IsSkipped => Skip != SkipReason.None;

    /// <summary>True when the rule has an estimate that is not backed by an item list (Recycle Bin, DNS, handlers...).</summary>
    public bool IsEstimate { get; init; }
}

/// <summary>Result of cleaning a single rule.</summary>
public sealed class RuleCleanResult
{
    public required CleanupRule Rule { get; init; }

    public int DeletedFiles { get; init; }

    public int DeletedDirectories { get; init; }

    /// <summary>Database rows removed (<see cref="RuleAction.PurgeDatabaseRows"/> only).</summary>
    public int DeletedEntries { get; init; }

    public long BytesFreed { get; init; }

    /// <summary>Files that could not be removed (locked, access denied, ...), with the reason.</summary>
    public IReadOnlyList<(string Path, string Reason)> Failures { get; init; } = [];

    /// <summary>Files scheduled for deletion on the next reboot (only when the user enabled that option).</summary>
    public IReadOnlyList<string> ScheduledForReboot { get; init; } = [];

    public SkipReason Skip { get; init; }

    public string? SkipDetails { get; init; }

    public TimeSpan Duration { get; init; }

    public bool IsSkipped => Skip != SkipReason.None;

    /// <summary>Human readable summary for special actions (for example "DNS cache flushed").</summary>
    public string? Message { get; init; }
}

/// <summary>Progress information published while scanning or cleaning.</summary>
public sealed record EngineProgress(
    int CompletedRules,
    int TotalRules,
    string CurrentRuleName,
    string? CurrentPath,
    long BytesSoFar,
    int ItemsSoFar)
{
    public double Percent => TotalRules == 0 ? 0 : Math.Min(100d, CompletedRules * 100d / TotalRules);
}