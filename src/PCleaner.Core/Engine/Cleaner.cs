using System.Diagnostics;
using System.Runtime.Versioning;
using PCleaner.Core.Logging;
using PCleaner.Core.Model;
using PCleaner.Core.Windows;

namespace PCleaner.Core.Engine;

/// <summary>
/// Executes cleanup rules using the items collected by the <see cref="Scanner"/>. Every deletion is re-validated
/// by <see cref="SafetyGuard"/> immediately before it happens.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Cleaner
{
    private readonly ICleanerLog _log;
    private readonly EngineOptions _options;

    public Cleaner(ICleanerLog? log = null, EngineOptions? options = null)
    {
        _log = log ?? NullLog.Instance;
        _options = options ?? new EngineOptions();
    }

    public async Task<IReadOnlyList<RuleCleanResult>> CleanAsync(
        IReadOnlyList<RuleScanResult> scans,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken,
        Action<int, RuleCleanResult>? onRuleCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(scans);

        var results = new List<RuleCleanResult>(scans.Count);
        long bytesSoFar = 0;
        var itemsSoFar = 0;
        var completed = 0;
        var throttle = new ProgressThrottle();

        _log.Info($"Clean started: {TextFormat.Count(scans.Count, "rule")}, dry-run={_options.DryRun}, elevated={Elevation.IsElevated}.");
        var stopwatch = Stopwatch.StartNew();

        foreach (var scan in scans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = scan.Rule;

            void Report(string? path)
            {
                if (path is not null && !throttle.ShouldReport())
                {
                    return;
                }

                progress?.Report(new EngineProgress(completed, scans.Count, rule.Name, path, bytesSoFar, itemsSoFar));
            }

            Report(null);

            var result = await CleanRuleAsync(scan, Report, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            onRuleCompleted?.Invoke(results.Count - 1, result);
            completed++;
            bytesSoFar += result.BytesFreed;
            itemsSoFar += result.DeletedFiles + result.DeletedDirectories + result.DeletedEntries;
            Report(null);
        }

        stopwatch.Stop();
        _log.Info($"Clean finished in {stopwatch.Elapsed.TotalSeconds:F1}s: {TextFormat.Count(itemsSoFar, "item")} removed, {Scanner.FormatBytes(bytesSoFar)} freed.");
        return results;
    }

    public async Task<RuleCleanResult> CleanRuleAsync(RuleScanResult scan, Action<string?>? onPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var rule = scan.Rule;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (rule.RequiresAdministrator && !Elevation.IsElevated)
            {
                return Skipped(rule, SkipReason.RequiresAdministrator, "Administrator rights are required.", stopwatch.Elapsed);
            }

            // Re-check right before deleting: the user may have started the browser after the scan.
            var blocking = ProcessMonitor.GetBlockers(rule);
            if (blocking.Count > 0)
            {
                _log.Warn($"Skipping '{rule.Name}': {string.Join(", ", blocking)} is running.");
                return Skipped(rule, SkipReason.ApplicationRunning, $"{string.Join(", ", blocking)} is running.", stopwatch.Elapsed);
            }

            RuleCleanResult result = rule.Action switch
            {
                RuleAction.DeleteFiles => DeleteItems(scan, onPath, cancellationToken),
                RuleAction.EmptyRecycleBin => EmptyRecycleBin(scan),
                RuleAction.FlushDnsCache => FlushDns(rule),
                RuleAction.ClearEventLogs => ClearEventLogs(scan, cancellationToken),
                RuleAction.DiskCleanupHandler => await RunHandlerAsync(scan, onPath, cancellationToken).ConfigureAwait(false),
                RuleAction.ComponentStoreCleanup => await RunComponentCleanupAsync(rule, onPath, cancellationToken).ConfigureAwait(false),
                RuleAction.PurgeDatabaseRows => PurgeDatabases(scan, onPath, cancellationToken),
                RuleAction.ForgetHistory => ForgetHistory(scan, onPath, cancellationToken),
                _ => Skipped(rule, SkipReason.NotSupported, "Unknown action.", TimeSpan.Zero),
            };

            return WithDuration(result, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Cleaning '{rule.Name}' failed.", ex);
            return Skipped(rule, SkipReason.Error, ex.Message, stopwatch.Elapsed);
        }
    }

    private RuleCleanResult DeleteItems(RuleScanResult scan, Action<string?>? onPath, CancellationToken cancellationToken)
    {
        var rule = scan.Rule;
        var failures = new List<(string, string)>();
        var scheduled = new List<string>();
        var deletedFiles = 0;
        var deletedDirs = 0;
        long bytes = 0;

        // Map every item back to the target root it belongs to (needed for the final safety check and age re-check).
        var targetsByRoot = rule.Targets
            .GroupBy(t => SafetyGuard.NormalizeDirectory(t.IsFile ? Path.GetDirectoryName(t.Path) ?? t.Path : t.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var roots = targetsByRoot.Keys.OrderByDescending(r => r.Length).ToList();

        using var services = rule.ServicesToStop.Count > 0 && !_options.DryRun
            ? ServiceScope.Stop(rule.ServicesToStop, _log)
            : null;

        var files = scan.Items.Where(i => !i.IsDirectory).ToList();
        var directories = scan.Items.Where(i => i.IsDirectory).OrderByDescending(i => i.Path.Length).ToList();

        foreach (var item in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onPath?.Invoke(item.Path);

            var root = roots.FirstOrDefault(r => item.Path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (root is null)
            {
                failures.Add((item.Path, "Item is outside every cleanup root."));
                continue;
            }

            if (SafetyGuard.CheckDeletable(item.Path, root, isDirectory: false) is { } blocked)
            {
                failures.Add((item.Path, blocked));
                continue;
            }

            // A temp file may have been touched by an installer since the analysis: honour the age rule again.
            var minimumAge = targetsByRoot[root].Select(t => t.MinimumAge).Where(a => a.HasValue).Select(a => a!.Value).DefaultIfEmpty(TimeSpan.Zero).Min();
            if (minimumAge > TimeSpan.Zero && IsYoungerThan(item.Path, minimumAge))
            {
                failures.Add((item.Path, "Modified since the analysis (kept)."));
                continue;
            }

            var outcome = DeleteFile(item.Path);
            switch (outcome.Kind)
            {
                case DeleteOutcomeKind.Deleted:
                    deletedFiles++;
                    bytes += item.Size;
                    break;
                case DeleteOutcomeKind.AlreadyGone:
                    break;
                case DeleteOutcomeKind.Scheduled:
                    scheduled.Add(item.Path);
                    break;
                default:
                    failures.Add((item.Path, outcome.Message ?? "Unknown error."));
                    break;
            }
        }

        foreach (var dir in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots.FirstOrDefault(r => dir.Path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (root is null || SafetyGuard.CheckDeletable(dir.Path, root, isDirectory: true) is not null)
            {
                continue;
            }

            if (DeleteEmptyDirectory(dir.Path))
            {
                deletedDirs++;
            }
        }

        if (failures.Count > 0)
        {
            _log.Info($"'{rule.Name}': {TextFormat.Count(deletedFiles, "file")} removed, {failures.Count:N0} skipped (locked or protected).");
        }

        return new RuleCleanResult
        {
            Rule = rule,
            DeletedFiles = deletedFiles,
            DeletedDirectories = deletedDirs,
            BytesFreed = bytes,
            Failures = failures,
            ScheduledForReboot = scheduled,
        };
    }

    private enum DeleteOutcomeKind
    {
        Deleted,
        AlreadyGone,
        Scheduled,
        Failed,
    }

    private static bool IsYoungerThan(string path, TimeSpan age)
    {
        try
        {
            var cutoff = DateTime.UtcNow - age;
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            return info.LastWriteTimeUtc > cutoff || info.CreationTimeUtc > cutoff || info.LastAccessTimeUtc > cutoff;
        }
        catch (Exception)
        {
            return true; // cannot tell - err on the side of keeping the file
        }
    }

    private readonly record struct DeleteOutcome(DeleteOutcomeKind Kind, string? Message = null);

    private DeleteOutcome DeleteFile(string path)
    {
        if (_options.DryRun)
        {
            _log.Debug($"[dry-run] delete {path}");
            return new DeleteOutcome(DeleteOutcomeKind.Deleted);
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new DeleteOutcome(DeleteOutcomeKind.Failed, "Symbolic link (skipped).");
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
            return new DeleteOutcome(DeleteOutcomeKind.Deleted);
        }
        catch (FileNotFoundException)
        {
            return new DeleteOutcome(DeleteOutcomeKind.AlreadyGone);
        }
        catch (DirectoryNotFoundException)
        {
            return new DeleteOutcome(DeleteOutcomeKind.AlreadyGone);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_options.ScheduleLockedFilesForReboot && Elevation.IsElevated && DelayedDelete.Schedule(path))
            {
                _log.Debug($"Scheduled for deletion at reboot: {path}");
                return new DeleteOutcome(DeleteOutcomeKind.Scheduled);
            }

            return new DeleteOutcome(DeleteOutcomeKind.Failed, ex is UnauthorizedAccessException ? "Access denied or in use." : "In use by another process.");
        }
        catch (Exception ex)
        {
            return new DeleteOutcome(DeleteOutcomeKind.Failed, ex.Message);
        }
    }

    private bool DeleteEmptyDirectory(string path)
    {
        if (_options.DryRun)
        {
            return true;
        }

        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            if (info.EnumerateFileSystemInfos().Any())
            {
                return false;
            }

            info.Delete(recursive: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private RuleCleanResult EmptyRecycleBin(RuleScanResult scan)
    {
        var (bytes, items) = RecycleBin.Query();
        if (_options.DryRun)
        {
            return new RuleCleanResult { Rule = scan.Rule, BytesFreed = bytes, DeletedFiles = (int)Math.Min(int.MaxValue, items), Message = "[dry-run] Recycle Bin would be emptied." };
        }

        if (items == 0)
        {
            return new RuleCleanResult { Rule = scan.Rule, Message = "Recycle Bin was already empty." };
        }

        var ok = RecycleBin.Empty();
        return ok
            ? new RuleCleanResult { Rule = scan.Rule, BytesFreed = bytes, DeletedFiles = (int)Math.Min(int.MaxValue, items), Message = "Recycle Bin emptied." }
            : Skipped(scan.Rule, SkipReason.Error, "The shell refused to empty the Recycle Bin.", TimeSpan.Zero);
    }

    private RuleCleanResult FlushDns(CleanupRule rule)
    {
        if (_options.DryRun)
        {
            return new RuleCleanResult { Rule = rule, Message = "[dry-run] DNS cache would be flushed." };
        }

        return DnsCache.Flush()
            ? new RuleCleanResult { Rule = rule, Message = "DNS resolver cache flushed." }
            : Skipped(rule, SkipReason.Error, "DnsFlushResolverCache failed.", TimeSpan.Zero);
    }

    private RuleCleanResult ClearEventLogs(RuleScanResult scan, CancellationToken cancellationToken)
    {
        if (_options.DryRun)
        {
            return new RuleCleanResult { Rule = scan.Rule, BytesFreed = scan.TotalBytes, Message = "[dry-run] Event logs would be cleared." };
        }

        var cleared = EventLogCleaner.ClearAll(_log, cancellationToken);
        return new RuleCleanResult { Rule = scan.Rule, BytesFreed = scan.TotalBytes, DeletedFiles = cleared, Message = $"{TextFormat.Count(cleared, "event log channel")} cleared." };
    }

    private async Task<RuleCleanResult> RunHandlerAsync(RuleScanResult scan, Action<string?>? onPath, CancellationToken cancellationToken)
    {
        var rule = scan.Rule;
        var handler = DiskCleanupHandlers.Enumerate().FirstOrDefault(h => string.Equals(h.KeyName, rule.ActionArgument, StringComparison.OrdinalIgnoreCase));
        if (handler is null)
        {
            return Skipped(rule, SkipReason.LocationNotFound, "Handler is not registered on this computer.", TimeSpan.Zero);
        }

        if (_options.DryRun)
        {
            return new RuleCleanResult { Rule = rule, BytesFreed = scan.TotalBytes, Message = $"[dry-run] Disk Cleanup handler '{handler.DisplayName}' would run." };
        }

        var outcome = await DiskCleanupHandlers.PurgeAsync(handler, _log, s => onPath?.Invoke(s), cancellationToken).ConfigureAwait(false);
        if (outcome.AccessDenied)
        {
            return Skipped(rule, SkipReason.RequiresAdministrator, outcome.Error, TimeSpan.Zero);
        }

        if (!outcome.Succeeded)
        {
            return Skipped(rule, SkipReason.Error, outcome.Error, TimeSpan.Zero);
        }

        return new RuleCleanResult { Rule = rule, BytesFreed = outcome.Bytes, Message = $"Disk Cleanup handler '{handler.DisplayName}' completed." };
    }

    private async Task<RuleCleanResult> RunComponentCleanupAsync(CleanupRule rule, Action<string?>? onPath, CancellationToken cancellationToken)
    {
        if (_options.DryRun)
        {
            return new RuleCleanResult { Rule = rule, Message = "[dry-run] DISM /StartComponentCleanup would run." };
        }

        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        if (!File.Exists(dism))
        {
            return Skipped(rule, SkipReason.NotSupported, "dism.exe not found.", TimeSpan.Zero);
        }

        onPath?.Invoke("Running DISM /Online /Cleanup-Image /StartComponentCleanup (this can take several minutes)...");
        var info = new ProcessStartInfo(dism, "/Online /Cleanup-Image /StartComponentCleanup /NoRestart")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(info);
        if (process is null)
        {
            return Skipped(rule, SkipReason.Error, "DISM could not be started.", TimeSpan.Zero);
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _log.Info($"DISM exit code {process.ExitCode}. {output.Trim().Split('\n').LastOrDefault()?.Trim()}");

        return process.ExitCode == 0
            ? new RuleCleanResult { Rule = rule, Message = "Component store cleanup completed." }
            : Skipped(rule, SkipReason.Error, $"DISM exited with code {process.ExitCode}.", TimeSpan.Zero);
    }

    private static RuleCleanResult Skipped(CleanupRule rule, SkipReason reason, string? details, TimeSpan duration)
        => new() { Rule = rule, Skip = reason, SkipDetails = details, Duration = duration };

    /// <summary>
    /// Removes rows from allow-listed tables of each database. Every file is validated again right before it is
    /// opened, the deletion runs in one transaction per file, and the file is compacted afterwards. Values are never
    /// written to the log.
    /// </summary>
    private RuleCleanResult PurgeDatabases(RuleScanResult scan, Action<string?>? onPath, CancellationToken cancellationToken)
    {
        var rule = scan.Rule;
        var failures = new List<(string, string)>();
        var removed = 0;
        long bytes = 0;
        var touched = 0;

        foreach (var database in rule.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (SafetyGuard.ValidateDatabaseTarget(database) is { } reason)
            {
                failures.Add((database.Path, reason));
                continue;
            }

            if (!(DatabasePurger.IsDirectoryTarget(database.Purge) ? Directory.Exists(database.Path) : File.Exists(database.Path)))
            {
                continue;
            }

            onPath?.Invoke(database.Path);
            var outcome = DatabasePurger.Purge(database, _options.DryRun);
            if (outcome.Error is not null)
            {
                failures.Add((database.Path, outcome.Error));
                continue;
            }

            if (outcome.Detail is not null)
            {
                _log.Info($"'{rule.Name}': {outcome.Detail}");
            }

            touched++;
            removed += outcome.EntriesRemoved;
            bytes += outcome.BytesFreed;
        }

        if (touched == 0 && failures.Count > 0)
        {
            return Skipped(rule, SkipReason.Error, failures[0].Item2, TimeSpan.Zero);
        }

        var unit = rule.Databases.Any(d => DatabasePurger.IsDirectoryTarget(d.Purge)) ? ("website", "websites") : ("entry", "entries");
        var what = TextFormat.Count(removed, unit.Item1, unit.Item2);
        var prefix = _options.DryRun ? "[dry-run] " : string.Empty;
        _log.Info($"{prefix}'{rule.Name}': {what} removed from {TextFormat.Count(touched, "database")}, {Scanner.FormatBytes(bytes)} compacted.");

        return new RuleCleanResult
        {
            Rule = rule,
            DeletedEntries = removed,
            BytesFreed = bytes,
            Failures = failures,
            Message = removed == 0
                ? $"No {unit.Item2} to remove."
                : $"{prefix}{what} removed - the database was rewritten, all other data kept.",
        };
    }

    private RuleCleanResult ForgetHistory(RuleScanResult scan, Action<string?>? onPath, CancellationToken cancellationToken)
    {
        var rule = scan.Rule;
        var failures = new List<(string, string)>();
        var removed = 0;
        long bytes = 0;
        var touched = 0;
        var locked = false;

        foreach (var target in rule.History)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (SafetyGuard.ValidateHistoryTarget(target) is { } reason)
            {
                failures.Add((target.Location, reason));
                continue;
            }

            onPath?.Invoke(target.Location);
            var outcome = HistoryPurger.Purge(target, _options.DryRun);
            if (outcome.IsLocked)
            {
                locked = true;
                failures.Add((target.Location, outcome.Error ?? "In use."));
                continue;
            }

            if (outcome.Error is not null)
            {
                failures.Add((target.Location, outcome.Error));
                continue;
            }

            touched++;
            removed += outcome.EntriesRemoved;
            bytes += outcome.BytesFreed;
        }

        if (touched == 0 && locked)
        {
            return Skipped(rule, SkipReason.ApplicationRunning, "The application is still using its settings.", TimeSpan.Zero);
        }

        if (touched == 0 && failures.Count > 0)
        {
            return Skipped(rule, SkipReason.Error, failures[0].Item2, TimeSpan.Zero);
        }

        var what = TextFormat.Count(removed, "entry");
        var prefix = _options.DryRun ? "[dry-run] " : string.Empty;
        _log.Info($"{prefix}'{rule.Name}': {what} forgotten in {TextFormat.Count(touched, "store")}, {Scanner.FormatBytes(bytes)}.");

        return new RuleCleanResult
        {
            Rule = rule,
            DeletedEntries = removed,
            BytesFreed = bytes,
            Failures = failures,
            Message = removed == 0 ? "Nothing remembered - nothing to forget." : $"{prefix}{what} forgotten - settings and favourites kept.",
        };
    }

    private static RuleCleanResult WithDuration(RuleCleanResult result, TimeSpan duration) => new()
    {
        Rule = result.Rule,
        DeletedFiles = result.DeletedFiles,
        DeletedDirectories = result.DeletedDirectories,
        DeletedEntries = result.DeletedEntries,
        BytesFreed = result.BytesFreed,
        Failures = result.Failures,
        ScheduledForReboot = result.ScheduledForReboot,
        Skip = result.Skip,
        SkipDetails = result.SkipDetails,
        Message = result.Message,
        Duration = duration,
    };
}