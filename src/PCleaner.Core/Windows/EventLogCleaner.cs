using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using PCleaner.Core.Logging;

namespace PCleaner.Core.Windows;

/// <summary>
/// Clears Windows event logs through the Event Log API (wevtutil equivalent). The .evtx files below
/// System32\winevt\Logs are never deleted directly - that would corrupt the event log service state.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EventLogCleaner
{
    /// <summary>Channels that are deliberately left alone.</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Security", // audit trail; clearing it is an auditable event and rarely what a user wants
        "ForwardedEvents",
    };

    /// <summary>Returns the clearable channels with their on-disk size and record count.</summary>
    public static IReadOnlyList<(string Name, long FileSize, long Records)> Enumerate()
    {
        var list = new List<(string, long, long)>();
        try
        {
            using var session = new EventLogSession();
            foreach (var name in session.GetLogNames())
            {
                if (Excluded.Contains(name))
                {
                    continue;
                }

                try
                {
                    using var config = new EventLogConfiguration(name, session);
                    if (config.LogType is EventLogType.Analytical or EventLogType.Debug)
                    {
                        continue;
                    }

                    var info = session.GetLogInformation(name, PathType.LogName);
                    var records = info.RecordCount ?? 0;
                    if (records <= 0)
                    {
                        continue;
                    }

                    list.Add((name, info.FileSize ?? 0, records));
                }
                catch (Exception)
                {
                    // Inaccessible channel (permissions) - skip silently.
                }
            }
        }
        catch (Exception)
        {
            // Event log service not available.
        }

        return list;
    }

    /// <summary>Clears every clearable channel. Returns the number of channels cleared.</summary>
    public static int ClearAll(ICleanerLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        var cleared = 0;
        using var session = new EventLogSession();
        foreach (var (name, _, _) in Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                session.ClearLog(name);
                cleared++;
            }
            catch (Exception ex)
            {
                log.Warn($"Could not clear event log '{name}': {ex.Message}");
            }
        }

        return cleared;
    }
}