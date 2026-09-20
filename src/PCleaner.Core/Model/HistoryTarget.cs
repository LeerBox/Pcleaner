namespace PCleaner.Core.Model;

/// <summary>
/// Where an application keeps its memory of what was opened: recent-file lists, the last session's tabs, dialog
/// history. Each kind is handled by fixed, reviewed code in the engine - a rule only chooses WHICH store to
/// forget, never how, so an unknown key or file can never be touched.
/// </summary>
public enum HistoryStore
{
    /// <summary>
    /// Every value and subkey inside a registry key below HKEY_CURRENT_USER. The key itself stays; Windows and
    /// the application recreate the contents as new files are opened (Explorer's RecentDocs, Open/Save dialogs,
    /// Office "File MRU", Paint's "Recent File List", ...).
    /// </summary>
    RegistryKeyContents = 0,

    /// <summary>Named values inside a registry key below HKEY_CURRENT_USER (Registry Editor's <c>LastKey</c>, 7-Zip's histories).</summary>
    RegistryValues = 1,

    /// <summary>
    /// Windows 11 Notepad's <c>TabState</c> or <c>WindowState</c> folder: one <c>.bin</c> per tab that Notepad
    /// reopens at start. Unsaved tabs live only here.
    /// </summary>
    NotepadTabs = 2,

    /// <summary>Notepad++'s <c>session.xml</c> (open tabs) plus its <c>backup</c> folder (unsaved buffers).</summary>
    NotepadPlusPlusSession = 3,

    /// <summary>Notepad++'s recent-file list (<c>History</c> in <c>config.xml</c>).</summary>
    NotepadPlusPlusRecentFiles = 4,

    /// <summary>VLC's recent media list (<c>[RecentsMRL]</c> in <c>vlc-qt-interface.ini</c>).</summary>
    VlcRecentMedia = 5,

    /// <summary>
    /// Windows 11 Notepad's "Recent files" list: the <c>RecentFiles</c> value in the app's <c>settings.dat</c>
    /// hive, reset to an empty list exactly like Notepad's own "Clear" command. Other settings stay.
    /// </summary>
    NotepadRecentFiles = 6,
}

/// <summary>One store a <see cref="RuleAction.ForgetHistory"/> rule clears.</summary>
public sealed class HistoryTarget
{
    public required HistoryStore Store { get; init; }

    /// <summary>
    /// Registry key path below HKEY_CURRENT_USER (one segment may be <c>*</c> to match every subkey), the state
    /// folder (Notepad), the application data folder (Notepad++) or the INI file (VLC).
    /// </summary>
    public required string Location { get; init; }

    /// <summary>Value names for <see cref="HistoryStore.RegistryValues"/>; unused otherwise.</summary>
    public IReadOnlyList<string> ValueNames { get; init; } = [];

    /// <summary>Shown next to each remembered entry, e.g. "Recent documents" or "Word".</summary>
    public required string Label { get; init; }

    public override string ToString() => $"{Store}: {Location}";
}