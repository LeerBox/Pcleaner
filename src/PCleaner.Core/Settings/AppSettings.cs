using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCleaner.Core.Settings;

/// <summary>User preferences, persisted as JSON below %LOCALAPPDATA%\PCleaner.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Explicit user choices per rule id. Rules without an entry use their default (Safe = on).</summary>
    public Dictionary<string, bool> RuleSelection { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Schedule locked files for deletion at the next reboot (administrator only).</summary>
    public bool ScheduleLockedFilesForReboot { get; set; }

    /// <summary>Files in temp folders younger than this many hours are never deleted.</summary>
    public int TempFileMinimumAgeHours { get; set; } = 24;

    /// <summary>Ask for administrator rights automatically when the application starts.</summary>
    public bool RequestElevationOnStartup { get; set; }

    /// <summary>Ask running browsers to close (gracefully, never killed) before cleaning them.</summary>
    public bool CloseBrowsersBeforeCleaning { get; set; }

    /// <summary>
    /// Administrator mode: after asking, end blocking processes that keep no window open (background / tray
    /// instances), clean their items and reopen the applications afterwards. Ignored when not elevated.
    /// </summary>
    public bool AutoCloseBlockingApplications { get; set; }

    /// <summary>Start the applications PCleaner closed again once the cleanup has finished.</summary>
    public bool ReopenClosedApplications { get; set; } = true;

    /// <summary>Show a confirmation dialog before cleaning.</summary>
    public bool ConfirmBeforeCleaning { get; set; } = true;

    /// <summary>Include Chromium "System Profile" and "Guest Profile" directories in the browser cleanup.</summary>
    public bool IncludeSystemAndGuestProfiles { get; set; } = true;

    /// <summary>Bytes freed by the most recent cleanup (shown on the dashboard).</summary>
    public long LastRunFreedBytes { get; set; }

    /// <summary>Items removed by the most recent cleanup.</summary>
    public int LastRunItems { get; set; }

    /// <summary>When the most recent cleanup finished (UTC).</summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>Total bytes freed by PCleaner since it was installed.</summary>
    public long LifetimeFreedBytes { get; set; }

    public static string DefaultPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCleaner", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    settings.TempFileMinimumAgeHours = Math.Clamp(settings.TempFileMinimumAgeHours, 0, 24 * 30);
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt settings: start with defaults rather than failing.
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public bool IsRuleEnabled(string ruleId, bool defaultValue)
        => RuleSelection.TryGetValue(ruleId, out var enabled) ? enabled : defaultValue;

    public void SetRuleEnabled(string ruleId, bool enabled, bool defaultValue)
    {
        if (enabled == defaultValue)
        {
            RuleSelection.Remove(ruleId);
        }
        else
        {
            RuleSelection[ruleId] = enabled;
        }
    }
}