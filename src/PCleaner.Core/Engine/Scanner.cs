using System.Diagnostics;
using System.Runtime.Versioning;
using PCleaner.Core.Logging;
using PCleaner.Core.Model;
using PCleaner.Core.Windows;

namespace PCleaner.Core.Engine;

/// <summary>Options shared by the scanner and the cleaner.</summary>
public sealed class EngineOptions
{
    /// <summary>When true the cleaner only logs what it would delete.</summary>
    public bool DryRun { get; set; }

    /// <summary>Schedule files that are locked for deletion at the next reboot (administrator only).</summary>
    public bool ScheduleLockedFilesForReboot { get; set; }

    /// <summary>Maximum number of rules scanned concurrently.</summary>
    public int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
}

/// <summary>Limits high-frequency progress notifications to a handful per second. Thread-safe.</summary>
internal sealed class ProgressThrottle
{
    private readonly long _intervalTicks;
    private long _lastTicks;

    public ProgressThrottle(int intervalMilliseconds = 60)
    {
        _intervalTicks = intervalMilliseconds * Stopwatch.Frequency / 1000;
    }

    public bool ShouldReport()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastTicks);
        if (now - last < _intervalTicks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _lastTicks, now, last) == last;
    }
}

/// <summary>
/// Evaluates rules and produces a preview of everything that would be removed. Scanning never modifies anything.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Scanner
{
    private readonly ICleanerLog _log;
    private readonly EngineOptions _options;
    private readonly FileSystemWalker _walker;

    public Scanner(ICleanerLog? log = null, EngineOptions? options = null)
    {
        _log = log ?? NullLog.Instance;
        _options = options ?? new EngineOptions();
        _walker = new FileSystemWalker(_log);
    }

    public async Task<IReadOnlyList<RuleScanResult>> ScanAsync(
        IReadOnlyList<CleanupRule> rules,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken,
        Action<int, RuleScanResult>? onRuleCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var results = new RuleScanResult[rules.Count];
        var completed = 0;
        long bytesSoFar = 0;
        var itemsSoFar = 0;
        var gate = new object();
        var throttle = new ProgressThrottle();

        void Report(string ruleName, string? path)
        {
            // Path updates arrive for every directory; only forward a few per second (rule completions always go through).
            if (path is not null && !throttle.ShouldReport())
            {
                return;
            }

            lock (gate)
            {
                progress?.Report(new EngineProgress(completed, rules.Count, ruleName, path, bytesSoFar, itemsSoFar));
            }
        }

        _log.Info($"Scan started: {TextFormat.Count(rules.Count, "rule")}, dry-run={_options.DryRun}, elevated={Elevation.IsElevated}.");
        var stopwatch = Stopwatch.StartNew();

        // Disk Cleanup handlers must run one at a time (shared COM handlers); everything else runs in parallel.
        var handlerIndexes = new List<int>();
        var parallelIndexes = new List<int>();
        for (var i = 0; i < rules.Count; i++)
        {
            (rules[i].Action is RuleAction.DiskCleanupHandler or RuleAction.ComponentStoreCleanup ? handlerIndexes : parallelIndexes).Add(i);
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(parallelIndexes, parallelOptions, async (index, ct) =>
        {
            var rule = rules[index];
            Report(rule.Name, null);
            var result = await ScanRuleAsync(rule, path => Report(rule.Name, path), ct).ConfigureAwait(false);
            results[index] = result;
            lock (gate)
            {
                completed++;
                bytesSoFar += result.TotalBytes;
                itemsSoFar += result.Items.Count + result.EntryCount;
            }

            onRuleCompleted?.Invoke(index, result);
            Report(rule.Name, null);
        }).ConfigureAwait(false);

        foreach (var index in handlerIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = rules[index];
            Report(rule.Name, null);
            var result = await ScanRuleAsync(rule, path => Report(rule.Name, path), cancellationToken).ConfigureAwait(false);
            results[index] = result;
            lock (gate)
            {
                completed++;
                bytesSoFar += result.TotalBytes;
            }

            onRuleCompleted?.Invoke(index, result);
            Report(rule.Name, null);
        }

        stopwatch.Stop();
        _log.Info($"Scan finished in {stopwatch.Elapsed.TotalSeconds:F1}s: {TextFormat.Count(itemsSoFar, "item")} found, {FormatBytes(bytesSoFar)} in total (including items that are currently blocked).");
        return results;
    }

    public async Task<RuleScanResult> ScanRuleAsync(CleanupRule rule, Action<string>? onPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var blocking = ProcessMonitor.GetBlockers(rule);
            var needsAdmin = rule.RequiresAdministrator && !Elevation.IsElevated;

            RuleScanResult result = rule.Action switch
            {
                RuleAction.DeleteFiles => ScanFiles(rule, onPath, cancellationToken),
                RuleAction.EmptyRecycleBin => ScanRecycleBin(rule),
                RuleAction.FlushDnsCache => new RuleScanResult { Rule = rule, TotalBytes = 0, IsEstimate = true },
                RuleAction.ClearEventLogs => ScanEventLogs(rule),
                RuleAction.DiskCleanupHandler => await ScanHandlerAsync(rule, cancellationToken).ConfigureAwait(false),
                RuleAction.ComponentStoreCleanup => new RuleScanResult { Rule = rule, TotalBytes = 0, IsEstimate = true },
                RuleAction.PurgeDatabaseRows => ScanDatabases(rule, onPath, cancellationToken),
                RuleAction.ForgetHistory => ScanHistory(rule, onPath, cancellationToken),
                _ => new RuleScanResult { Rule = rule, Skip = SkipReason.NotSupported, SkipDetails = "Unknown action." },
            };

            if (result.IsSkipped)
            {
                return WithDuration(result, stopwatch.Elapsed);
            }

            if (blocking.Count > 0)
            {
                return new RuleScanResult
                {
                    Rule = rule,
                    Items = result.Items,
                    Entries = result.Entries,
                    EntryCount = result.EntryCount,
                    TotalBytes = result.TotalBytes,
                    IsEstimate = result.IsEstimate,
                    Skip = SkipReason.ApplicationRunning,
                    SkipDetails = $"Close {string.Join(", ", blocking)} before cleaning.",
                    BlockingProcesses = blocking,
                    Duration = stopwatch.Elapsed,
                };
            }

            if (needsAdmin)
            {
                return new RuleScanResult
                {
                    Rule = rule,
                    Items = result.Items,
                    Entries = result.Entries,
                    EntryCount = result.EntryCount,
                    TotalBytes = result.TotalBytes,
                    IsEstimate = result.IsEstimate,
                    Skip = SkipReason.RequiresAdministrator,
                    SkipDetails = "Restart PCleaner as administrator to clean this item.",
                    Duration = stopwatch.Elapsed,
                };
            }

            return WithDuration(result, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Scan of '{rule.Name}' failed.", ex);
            return new RuleScanResult { Rule = rule, Skip = SkipReason.Error, SkipDetails = ex.Message, Duration = stopwatch.Elapsed };
        }
    }

    private RuleScanResult ScanFiles(CleanupRule rule, Action<string>? onPath, CancellationToken cancellationToken)
    {
        var items = new List<ScanItem>();
        var anyLocationExists = false;

        foreach (var target in rule.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (SafetyGuard.ValidateTargetRoot(target) is { } reason)
            {
                _log.Warn($"Rule '{rule.Id}' target '{target.Path}' rejected: {reason}");
                continue;
            }

            if (target.IsFile ? !File.Exists(target.Path) : !Directory.Exists(target.Path))
            {
                continue;
            }

            anyLocationExists = true;
            items.AddRange(_walker.Enumerate(target, cancellationToken, onPath));
        }

        if (!anyLocationExists)
        {
            return new RuleScanResult { Rule = rule, Skip = SkipReason.LocationNotFound, SkipDetails = "Location does not exist on this computer." };
        }

        return new RuleScanResult { Rule = rule, Items = items, TotalBytes = items.Sum(i => i.Size) };
    }

    /// <summary>
    /// Counts the rows a <see cref="RuleAction.PurgeDatabaseRows"/> rule would remove. Read-only; the databases are
    /// opened in read-only mode and nothing is written.
    /// </summary>
    private RuleScanResult ScanDatabases(CleanupRule rule, Action<string>? onPath, CancellationToken cancellationToken)
    {
        var entries = new List<DatabaseEntry>();
        var count = 0;
        long bytes = 0;
        var anyExists = false;
        var locked = false;
        string? error = null;

        foreach (var database in rule.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (SafetyGuard.ValidateDatabaseTarget(database) is { } reason)
            {
                _log.Warn($"Rule '{rule.Id}' database '{database.Path}' rejected: {reason}");
                continue;
            }

            onPath?.Invoke(database.Path);
            var inspection = DatabasePurger.Inspect(database);
            if (!inspection.Exists)
            {
                continue;
            }

            anyExists = true;
            if (inspection.IsLocked)
            {
                locked = true;
                continue;
            }

            if (inspection.Error is not null)
            {
                error ??= inspection.Error;
                _log.Warn($"Rule '{rule.Id}': could not read '{Path.GetFileName(database.Path)}': {inspection.Error}");
                continue;
            }

            count += inspection.EntryCount;
            bytes += inspection.EstimatedBytes;
            entries.AddRange(inspection.Entries);
        }

        if (!anyExists)
        {
            return new RuleScanResult { Rule = rule, Skip = SkipReason.LocationNotFound, SkipDetails = "Database does not exist on this computer." };
        }

        if (locked && count == 0)
        {
            // The browser holds the file: the outer check normally reports the running process; this covers the
            // case where the process was not recognised (portable build, renamed executable).
            return new RuleScanResult { Rule = rule, IsEstimate = true, Skip = SkipReason.ApplicationRunning, SkipDetails = "The database is in use by the browser." };
        }

        if (error is not null && count == 0)
        {
            return new RuleScanResult { Rule = rule, Skip = SkipReason.Error, SkipDetails = error };
        }

        return new RuleScanResult { Rule = rule, Entries = entries, EntryCount = count, TotalBytes = bytes, IsEstimate = true };
    }

    private RuleScanResult ScanHistory(CleanupRule rule, Action<string>? onPath, CancellationToken cancellationToken)
    {
        var entries = new List<DatabaseEntry>();
        var count = 0;
        long bytes = 0;
        var anyExists = false;
        var locked = false;
        string? error = null;

        foreach (var target in rule.History)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SafetyGuard.ValidateHistoryTarget(target) is { } reason)
            {
                _log.Warn($"Rule '{rule.Id}' history store '{target.Location}' rejected: {reason}");
                continue;
            }

            onPath?.Invoke(target.Location);
            var inspection = HistoryPurger.Inspect(target);
            if (!inspection.Exists)
            {
                continue;
            }

            anyExists = true;
            if (inspection.IsLocked)
            {
                locked = true;
                continue;
            }

            if (inspection.Error is not null)
            {
                error ??= inspection.Error;
                _log.Warn($"Rule '{rule.Id}': could not read '{target.Location}': {inspection.Error}");
                continue;
            }

            count += inspection.EntryCount;
            bytes += inspection.Bytes;
            entries.AddRange(inspection.Entries);
        }

        if (!anyExists)
        {
            return new RuleScanResult { Rule = rule, Skip = SkipReason.LocationNotFound, SkipDetails = "The application keeps no such list on this computer." };
        }

        if (locked && count == 0)
        {
            return new RuleScanResult { Rule = rule, IsEstimate = true, Skip = SkipReason.ApplicationRunning, SkipDetails = "The application is using its settings." };
        }

        if (error is not null && count == 0)
        {
            return new RuleScanResult { Rule = rule, Skip = SkipReason.Error, SkipDetails = error };
        }

        return new RuleScanResult { Rule = rule, Entries = entries, EntryCount = count, TotalBytes = bytes, IsEstimate = true };
    }

    private static RuleScanResult ScanRecycleBin(CleanupRule rule)
    {
        var (bytes, count) = RecycleBin.Query();
        return new RuleScanResult
        {
            Rule = rule,
            TotalBytes = bytes,
            IsEstimate = true,
            SkipDetails = count == 0 ? null : $"{TextFormat.Count(count, "item")} in the Recycle Bin.",
        };
    }

    private static RuleScanResult ScanEventLogs(CleanupRule rule)
    {
        var logs = EventLogCleaner.Enumerate();
        return new RuleScanResult
        {
            Rule = rule,
            TotalBytes = logs.Sum(l => l.FileSize),
            IsEstimate = true,
            SkipDetails = $"{TextFormat.Count(logs.Count, "event log channel")} with {TextFormat.Count(logs.Sum(l => l.Records), "record")}.",
        };
    }

    private async Task<RuleScanResult> ScanHandlerAsync(CleanupRule rule, CancellationToken cancellationToken)
    {
        if (rule.RequiresAdministrator && !Elevation.IsElevated)
        {
            return new RuleScanResult { Rule = rule, IsEstimate = true, Skip = SkipReason.RequiresAdministrator, SkipDetails = "Restart PCleaner as administrator to use this Windows Disk Cleanup handler." };
        }

        var handler = DiskCleanupHandlers.Enumerate().FirstOrDefault(h => string.Equals(h.KeyName, rule.ActionArgument, StringComparison.OrdinalIgnoreCase));
        if (handler is null)
        {
            return new RuleScanResult { Rule = rule, Skip = SkipReason.LocationNotFound, SkipDetails = "Handler is not registered on this computer." };
        }

        var scan = await DiskCleanupHandlers.ScanAsync(handler, _log, cancellationToken).ConfigureAwait(false);
        if (scan.AccessDenied)
        {
            return new RuleScanResult { Rule = rule, IsEstimate = true, Skip = SkipReason.RequiresAdministrator, SkipDetails = scan.Error };
        }

        if (!scan.Succeeded)
        {
            return new RuleScanResult { Rule = rule, Skip = SkipReason.Error, SkipDetails = scan.Error };
        }

        return new RuleScanResult { Rule = rule, TotalBytes = scan.Bytes, IsEstimate = true };
    }

    private static RuleScanResult WithDuration(RuleScanResult result, TimeSpan duration) => new()
    {
        Rule = result.Rule,
        Items = result.Items,
        Entries = result.Entries,
        EntryCount = result.EntryCount,
        TotalBytes = result.TotalBytes,
        IsEstimate = result.IsEstimate,
        Skip = result.Skip,
        SkipDetails = result.SkipDetails,
        BlockingProcesses = result.BlockingProcesses,
        Duration = duration,
    };

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
    }
}