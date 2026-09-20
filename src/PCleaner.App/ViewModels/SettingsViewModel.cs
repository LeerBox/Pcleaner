using CommunityToolkit.Mvvm.ComponentModel;
using PCleaner.Core.Logging;
using PCleaner.Core.Settings;
using PCleaner.Core.Windows;

namespace PCleaner.App.ViewModels;

/// <summary>Settings page. Writes through to <see cref="AppSettings"/> and persists immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ICleanerLog _log;

    public SettingsViewModel(AppSettings settings, ICleanerLog log)
    {
        _settings = settings;
        _log = log;
        _confirmBeforeCleaning = settings.ConfirmBeforeCleaning;
        _scheduleLockedFilesForReboot = settings.ScheduleLockedFilesForReboot;
        _closeBrowsersBeforeCleaning = settings.CloseBrowsersBeforeCleaning;
        _autoCloseBlockingApplications = settings.AutoCloseBlockingApplications;
        _reopenClosedApplications = settings.ReopenClosedApplications;
        _requestElevationOnStartup = settings.RequestElevationOnStartup;
        _includeSystemAndGuestProfiles = settings.IncludeSystemAndGuestProfiles;
        _tempFileMinimumAgeHours = settings.TempFileMinimumAgeHours;
        IsElevated = Elevation.IsElevated;
    }

    /// <summary>Automatic closing is an administrator-mode feature; the toggle is disabled otherwise.</summary>
    public bool IsElevated { get; }

    public string AutoCloseHint => IsElevated
        ? "Asks first, then ends only windowless background processes - a browser's startup boost, OneDrive in the tray - and cleans their items. Anything showing a window is left alone; Office and Windows components are never ended."
        : "Needs administrator mode (use 'Restart as administrator'). Asks first, then ends only windowless background processes; anything showing a window is left alone.";

    [ObservableProperty]
    private bool _confirmBeforeCleaning;

    [ObservableProperty]
    private bool _scheduleLockedFilesForReboot;

    [ObservableProperty]
    private bool _closeBrowsersBeforeCleaning;

    [ObservableProperty]
    private bool _autoCloseBlockingApplications;

    [ObservableProperty]
    private bool _reopenClosedApplications;

    [ObservableProperty]
    private bool _requestElevationOnStartup;

    [ObservableProperty]
    private bool _includeSystemAndGuestProfiles;

    [ObservableProperty]
    private int _tempFileMinimumAgeHours;

    /// <summary>True when a changed setting only takes effect after the rule set is rebuilt (Refresh).</summary>
    [ObservableProperty]
    private bool _requiresRuleRebuild;

    public static IReadOnlyList<int> TempAgeChoices { get; } = [0, 1, 6, 12, 24, 48, 72, 168];

    partial void OnConfirmBeforeCleaningChanged(bool value) => Persist(() => _settings.ConfirmBeforeCleaning = value);

    partial void OnScheduleLockedFilesForRebootChanged(bool value) => Persist(() => _settings.ScheduleLockedFilesForReboot = value);

    partial void OnCloseBrowsersBeforeCleaningChanged(bool value) => Persist(() => _settings.CloseBrowsersBeforeCleaning = value);

    partial void OnAutoCloseBlockingApplicationsChanged(bool value)
    {
        Persist(() => _settings.AutoCloseBlockingApplications = value);
        if (value && !CloseBrowsersBeforeCleaning)
        {
            // Automatic closing always starts with a polite request; keep the two switches consistent.
            CloseBrowsersBeforeCleaning = true;
        }
    }

    partial void OnReopenClosedApplicationsChanged(bool value) => Persist(() => _settings.ReopenClosedApplications = value);

    partial void OnRequestElevationOnStartupChanged(bool value) => Persist(() => _settings.RequestElevationOnStartup = value);

    partial void OnIncludeSystemAndGuestProfilesChanged(bool value)
    {
        Persist(() => _settings.IncludeSystemAndGuestProfiles = value);
        RequiresRuleRebuild = true;
    }

    partial void OnTempFileMinimumAgeHoursChanged(int value)
    {
        Persist(() => _settings.TempFileMinimumAgeHours = Math.Clamp(value, 0, 24 * 30));
        RequiresRuleRebuild = true;
    }

    private void Persist(Action apply)
    {
        try
        {
            apply();
            _settings.Save();
        }
        catch (Exception ex)
        {
            _log.Warn("Could not save settings: " + ex.Message);
        }
    }
}