namespace PCleaner.Core.Model;

/// <summary>
/// The kind of row-level purge to run. Each kind is implemented by fixed, reviewed SQL in the engine - rules can
/// only choose WHICH purge to run on WHICH file, never supply their own statements. That keeps the allow-list
/// design of the file rules: an unknown table can never be touched.
/// </summary>
public enum DatabasePurge
{
    /// <summary>
    /// Chromium "Web Data": form-autofill history (the <c>autofill</c> / <c>autocomplete</c> tables, plus the
    /// Microsoft Edge field-value tables). Addresses, payment methods, search engines and tokens stay.
    /// </summary>
    ChromiumFormHistory = 0,

    /// <summary>
    /// Chromium "Web Data": addresses saved locally in Autofill settings (<c>addresses</c> /
    /// <c>local_addresses</c> and the legacy <c>autofill_profiles*</c> tables). Account-stored addresses, payment
    /// methods and passwords stay.
    /// </summary>
    ChromiumLocalAddresses = 1,

    /// <summary>
    /// Firefox "formhistory.sqlite": form and search-bar history (<c>moz_formhistory</c>) with Sync tombstones
    /// written to <c>moz_deleted_formhistory</c>, exactly like Firefox's own "Clear form &amp; search history".
    /// </summary>
    GeckoFormHistory = 2,

    /// <summary>
    /// Chromium "Local Storage\leveldb": every <c>http(s)</c> origin's DOM storage (device and visitor IDs,
    /// analytics tokens, experiment flags, player settings). Extension and browser-internal entries are kept and
    /// the database is rewritten in place; a zip backup is kept for seven days.
    /// </summary>
    ChromiumSiteLocalStorage = 3,
}

/// <summary>A SQLite or LevelDB database that a <see cref="RuleAction.PurgeDatabaseRows"/> rule operates on.</summary>
public sealed class DatabaseTarget
{
    /// <summary>Absolute path of the database file (SQLite) or directory (LevelDB). It is never created or deleted.</summary>
    public required string Path { get; init; }

    public required DatabasePurge Purge { get; init; }

    /// <summary>
    /// Where a purge that rewrites the database keeps its backup. <c>null</c> means PCleaner's own Backups folder
    /// under <c>%LOCALAPPDATA%</c>; tests point it at a temporary folder so they never write into the user profile.
    /// </summary>
    public string? BackupRoot { get; init; }

    public override string ToString() => $"{Path} [{Purge}]";
}

/// <summary>One database entry (row or origin) that a scan found and a clean would remove.</summary>
/// <param name="Field">The form field the value was typed into (for example <c>email</c>), or the website.</param>
/// <param name="Value">The stored value, or a summary such as "130 keys".</param>
/// <param name="TimesUsed">How often the browser filled it, or the number of keys (0 when unknown).</param>
/// <param name="LastUsedUtc">When it was last used or modified (null when unknown).</param>
/// <param name="Bytes">Approximate size of the entry (0 when unknown).</param>
public sealed record DatabaseEntry(string Field, string Value, int TimesUsed, DateTime? LastUsedUtc, long Bytes = 0);