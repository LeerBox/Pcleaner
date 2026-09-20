using Microsoft.Data.Sqlite;
using PCleaner.Core.Model;

namespace PCleaner.Core.Engine;

/// <summary>What a database scan found. Never contains an error message with user data.</summary>
public sealed class DatabaseInspection
{
    public required bool Exists { get; init; }

    public int EntryCount { get; init; }

    /// <summary>Preview of the rows that would be removed (capped).</summary>
    public IReadOnlyList<DatabaseEntry> Entries { get; init; } = [];

    /// <summary>Rough number of bytes the rows occupy (what a VACUUM is expected to give back).</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>Set when the database could not be read (locked by the browser, corrupt, unknown schema).</summary>
    public string? Error { get; init; }

    /// <summary>True when the file is currently held by another process (typically the running browser).</summary>
    public bool IsLocked { get; init; }
}

/// <summary>Outcome of a purge.</summary>
public sealed class DatabasePurgeResult
{
    public int EntriesRemoved { get; init; }

    /// <summary>Bytes the file shrank by after VACUUM (0 when it did not shrink).</summary>
    public long BytesFreed { get; init; }

    public string? Error { get; init; }

    /// <summary>Extra information for the log (for example where the backup was written).</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Row-level cleaning of application SQLite databases. Every <see cref="DatabasePurge"/> kind is implemented by
/// fixed statements against explicitly allow-listed tables; a table that is not in the list can never be touched,
/// and the database file is never created, replaced or deleted.
///
/// Design notes (verified against the Chromium and Mozilla sources, see the README "Sources" section):
/// - Chromium keeps form history in the <c>autofill</c> table of "Web Data", next to addresses, payment methods,
///   search engines and OAuth tokens - which is why the file itself is on the never-delete list.
/// - Chromium's own expiry path (<c>AutocompleteChange::EXPIRE</c>) deletes the row and its sync metadata without a
///   tombstone; this cleaner mirrors that. The per-type progress marker (<c>autofill_model_type_state</c>) is kept on
///   purpose: dropping it would make the browser re-download every entry from the account on the next sync.
/// - Firefox desktop never writes <c>moz_deleted_formhistory</c> tombstones; its "Clear form &amp; search history"
///   is <c>DELETE FROM moz_formhistory</c> followed by pruning orphan <c>moz_sources</c>, which is what we run.
/// - Deleted content is overwritten (<c>secure_delete</c>) and the file is compacted (<c>VACUUM</c>) so that the
///   removed values do not linger in free pages.
/// </summary>
public static class SqlitePurger
{
    private const int PreviewCap = 500;
    private const int BusyTimeoutMs = 1500;

    /// <summary>Sync data-type identifiers stored in autofill_sync_metadata.model_type (DataTypeHistogramValue + 1).</summary>
    private const int ChromiumSyncTypeAutocomplete = 7;
    private const int ChromiumSyncTypeAutofillProfile = 6;

    /// <summary>Chromium <c>AutofillProfile::RecordType::kLocalOrSyncable</c>.</summary>
    private const int ChromiumRecordTypeLocal = 0;

    private static readonly string[] ChromiumFormHistoryTables = ["autofill", "autocomplete"];

    /// <summary>Microsoft Edge stores typed field values in its own tables next to the Chromium ones.</summary>
    private static readonly string[] EdgeFormHistoryTables =
    [
        "autofill_edge_field_values", "autofill_edge_field_client_info", "autofill_edge_fieldid_cid_mapping", "autofill_edge_extended",
    ];

    /// <summary>Legacy local-address tables (Web Data schema &lt; 134): every row is a local profile.</summary>
    private static readonly string[] ChromiumLegacyAddressTables =
    [
        "local_addresses", "local_addresses_type_tokens", "autofill_profiles", "autofill_profile_names", "autofill_profile_emails",
        "autofill_profile_phones", "autofill_profile_addresses", "autofill_profile_birthdates", "autofill_profiles_trash",
    ];

    /// <summary>
    /// Tables that must never be modified by any purge, whatever the schema version. Guards against a future
    /// rename that would make an allow-listed name collide with something valuable.
    /// </summary>
    private static readonly HashSet<string> NeverTouchTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "meta", "keywords", "token_service", "credit_cards", "masked_credit_cards", "server_card_metadata", "local_ibans",
        "masked_ibans", "masked_ibans_metadata", "payments_customer_data", "server_card_cloud_token_data", "offer_data",
        "offer_eligible_instrument", "offer_merchant_domain", "virtual_card_usage_data", "local_stored_cvc", "server_stored_cvc",
        "masked_bank_accounts", "masked_bank_accounts_metadata", "masked_credit_card_benefits", "benefit_merchant_domains",
        "generic_payment_instruments", "payment_instrument_creation_options", "unmasked_credit_cards", "server_credit_cards",
        "credit_cards_edge_extended", "credit_cards_edge_metadata", "edge_tokenized_credit_cards", "edge_wallet_config",
        "edge_server_addresses", "edge_server_addresses_type_tokens", "contact_info", "contact_info_type_tokens",
        "autofill_model_type_state", "autofill_data_type_state", "plus_addresses", "logins", "moz_deleted_formhistory",
    };

    /// <summary>Binds Microsoft.Data.Sqlite to the SQLite engine that ships with Windows (winsqlite3.dll).</summary>
    private static readonly Lazy<bool> Engine = new(() =>
    {
        SQLitePCL.Batteries_V2.Init();
        return true;
    });

    /// <summary>Makes sure the SQLite engine is initialised (for other read-only consumers of browser databases).</summary>
    public static void EnsureEngine() => _ = Engine.Value;

    /// <summary>Reads the database and reports what a purge would remove. Never modifies the file.</summary>
    public static DatabaseInspection Inspect(DatabaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!File.Exists(target.Path))
        {
            return new DatabaseInspection { Exists = false };
        }

        try
        {
            using var connection = Open(target.Path, readOnly: true);
            var tables = ListTables(connection);
            var plan = BuildPlan(target.Purge, tables);
            if (plan.Count == 0)
            {
                return new DatabaseInspection { Exists = true, Error = "No form-history tables found in this database (unknown schema)." };
            }

            var entries = new List<DatabaseEntry>();
            var count = 0;
            long bytes = 0;
            foreach (var step in plan.Where(s => s.CountsAsEntries))
            {
                count += ExecuteScalarInt(connection, $"SELECT COUNT(*) FROM \"{step.Table}\"{step.WhereClause}");
                bytes += ExecuteScalarLong(connection, $"SELECT COALESCE(SUM({step.RowSizeExpression}), 0) FROM \"{step.Table}\"{step.WhereClause}");
                if (step.PreviewQuery is not null && entries.Count < PreviewCap)
                {
                    ReadPreview(connection, step.PreviewQuery, entries, PreviewCap - entries.Count);
                }
            }

            return new DatabaseInspection { Exists = true, EntryCount = count, Entries = entries, EstimatedBytes = bytes };
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            return new DatabaseInspection { Exists = true, IsLocked = true, Error = "The database is in use by the browser." };
        }
        catch (Exception ex)
        {
            return new DatabaseInspection { Exists = true, Error = Sanitize(ex) };
        }
    }

    /// <summary>
    /// Removes the rows in one transaction, then compacts the file. Fails fast (without changes) when another
    /// process holds the database.
    /// </summary>
    public static DatabasePurgeResult Purge(DatabaseTarget target, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!File.Exists(target.Path))
        {
            return new DatabasePurgeResult { Error = "Database not found." };
        }

        try
        {
            var sizeBefore = new FileInfo(target.Path).Length;
            using var connection = Open(target.Path, readOnly: dryRun);
            var tables = ListTables(connection);
            var plan = BuildPlan(target.Purge, tables);
            if (plan.Count == 0)
            {
                return new DatabasePurgeResult { Error = "No form-history tables found in this database (unknown schema)." };
            }

            if (dryRun)
            {
                var wouldRemove = plan.Where(s => s.CountsAsEntries).Sum(s => ExecuteScalarInt(connection, $"SELECT COUNT(*) FROM \"{s.Table}\"{s.WhereClause}"));
                return new DatabasePurgeResult { EntriesRemoved = wouldRemove };
            }

            // Overwrite deleted content with zeros for this connection (Chromium and Firefox build SQLite with
            // SQLITE_SECURE_DELETE; winsqlite3 does not default to it).
            Execute(connection, "PRAGMA secure_delete = ON");

            var removed = 0;
            using (var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable))
            {
                foreach (var step in plan)
                {
                    var affected = ExecuteNonQuery(connection, transaction, step.DeleteStatement);
                    if (step.CountsAsEntries)
                    {
                        removed += affected;
                    }
                }

                transaction.Commit();
            }

            // Compact so the freed pages (and any stale copies of the values) leave the file.
            Execute(connection, "VACUUM");
            TryExecute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            connection.Close();

            var sizeAfter = new FileInfo(target.Path).Length;
            return new DatabasePurgeResult { EntriesRemoved = removed, BytesFreed = Math.Max(0, sizeBefore - sizeAfter) };
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            return new DatabasePurgeResult { Error = "The database is in use by the browser - nothing was changed." };
        }
        catch (Exception ex)
        {
            return new DatabasePurgeResult { Error = Sanitize(ex) };
        }
    }

    /// <summary>The database file name each purge kind is allowed to operate on (checked by <see cref="SafetyGuard"/>).</summary>
    public static IReadOnlyList<string> AllowedFileNames(DatabasePurge purge) => purge switch
    {
        DatabasePurge.ChromiumFormHistory or DatabasePurge.ChromiumLocalAddresses => ["Web Data"],
        DatabasePurge.GeckoFormHistory => ["formhistory.sqlite"],
        _ => [],
    };

    // ------------------------------------------------------------------ plan

    /// <summary>One table operation. Statements are built only from constants in this file.</summary>
    private sealed record PurgeStep(string Table, string DeleteStatement, bool CountsAsEntries, string WhereClause = "", string RowSizeExpression = "0", string? PreviewQuery = null);

    private static List<PurgeStep> BuildPlan(DatabasePurge purge, HashSet<string> tables)
    {
        var plan = new List<PurgeStep>();

        void Add(PurgeStep step)
        {
            if (NeverTouchTables.Contains(step.Table))
            {
                throw new InvalidOperationException($"Refusing to touch protected table '{step.Table}'.");
            }

            if (tables.Contains(step.Table))
            {
                plan.Add(step);
            }
        }

        switch (purge)
        {
            case DatabasePurge.ChromiumFormHistory:
                Add(new PurgeStep("autofill", "DELETE FROM \"autofill\"", true,
                    RowSizeExpression: "LENGTH(name) + LENGTH(value) + LENGTH(value_lower) + 24",
                    PreviewQuery: "SELECT name, value, count, date_last_used FROM \"autofill\" ORDER BY date_last_used DESC"));
                Add(new PurgeStep("autocomplete", "DELETE FROM \"autocomplete\"", true,
                    RowSizeExpression: "LENGTH(name) + LENGTH(value) + LENGTH(value_lower) + LENGTH(label) + LENGTH(label_normalized) + 24",
                    PreviewQuery: "SELECT COALESCE(NULLIF(label, ''), name), value, count, date_last_used FROM \"autocomplete\" ORDER BY date_last_used DESC"));
                Add(new PurgeStep("autofill_edge_field_values", "DELETE FROM \"autofill_edge_field_values\"", true,
                    RowSizeExpression: "LENGTH(field_id) + LENGTH(value) + 24",
                    PreviewQuery: "SELECT COALESCE((SELECT NULLIF(c.label, '') FROM \"autofill_edge_field_client_info\" c WHERE c.field_id = v.field_id), 'field'), v.value, v.count, v.date_last_used FROM \"autofill_edge_field_values\" v ORDER BY v.date_last_used DESC"));
                Add(new PurgeStep("autofill_edge_extended", "DELETE FROM \"autofill_edge_extended\"", true,
                    RowSizeExpression: "LENGTH(name) + LENGTH(value) + 32",
                    PreviewQuery: "SELECT COALESCE(NULLIF(label, ''), name), value, 0, date_last_used FROM \"autofill_edge_extended\" ORDER BY date_last_used DESC"));
                // Field descriptors and sync mapping that only describe the values removed above.
                Add(new PurgeStep("autofill_edge_field_client_info", "DELETE FROM \"autofill_edge_field_client_info\"", false));
                Add(new PurgeStep("autofill_edge_fieldid_cid_mapping", "DELETE FROM \"autofill_edge_fieldid_cid_mapping\"", false));
                // Chromium's EXPIRE path: drop the per-entry sync metadata, keep the data-type progress marker.
                Add(new PurgeStep("autofill_sync_metadata", $"DELETE FROM \"autofill_sync_metadata\" WHERE model_type = {ChromiumSyncTypeAutocomplete}", false));
                break;

            case DatabasePurge.ChromiumLocalAddresses:
                // Schema >= 134: local and account addresses share one table, told apart by record_type.
                if (tables.Contains("addresses"))
                {
                    var localAddresses = $" WHERE record_type = {ChromiumRecordTypeLocal} OR record_type IS NULL";
                    var localGuids = $"(SELECT guid FROM \"addresses\"{localAddresses})";
                    Add(new PurgeStep("address_type_tokens", $"DELETE FROM \"address_type_tokens\" WHERE guid IN {localGuids}", false));
                    Add(new PurgeStep("autofill_profile_edge_extended", $"DELETE FROM \"autofill_profile_edge_extended\" WHERE guid IN {localGuids}", false));
                    Add(new PurgeStep("autofill_sync_metadata", $"DELETE FROM \"autofill_sync_metadata\" WHERE model_type = {ChromiumSyncTypeAutofillProfile} AND storage_key IN {localGuids}", false));
                    Add(new PurgeStep("addresses", $"DELETE FROM \"addresses\"{localAddresses}", true, localAddresses,
                        RowSizeExpression: "160 + (SELECT COALESCE(SUM(LENGTH(t.value)), 0) FROM \"address_type_tokens\" t WHERE t.guid = \"addresses\".guid)",
                        PreviewQuery: $"SELECT COALESCE(NULLIF(a.label, ''), 'address'), COALESCE((SELECT GROUP_CONCAT(t.value, ', ') FROM \"address_type_tokens\" t WHERE t.guid = a.guid AND t.value <> ''), ''), a.use_count, a.use_date FROM \"addresses\" a{localAddresses.Replace("record_type", "a.record_type", StringComparison.Ordinal)} ORDER BY a.use_date DESC"));
                }

                // Schema 107-133 and older: separate tables that only ever held local profiles.
                if (tables.Contains("local_addresses"))
                {
                    Add(new PurgeStep("autofill_sync_metadata", $"DELETE FROM \"autofill_sync_metadata\" WHERE model_type = {ChromiumSyncTypeAutofillProfile} AND storage_key IN (SELECT guid FROM \"local_addresses\")", false));
                    Add(new PurgeStep("local_addresses", "DELETE FROM \"local_addresses\"", true,
                        RowSizeExpression: "160",
                        PreviewQuery: "SELECT COALESCE(NULLIF(a.label, ''), 'address'), COALESCE((SELECT GROUP_CONCAT(t.value, ', ') FROM \"local_addresses_type_tokens\" t WHERE t.guid = a.guid AND t.value <> ''), ''), a.use_count, a.use_date FROM \"local_addresses\" a ORDER BY a.use_date DESC"));
                    Add(new PurgeStep("local_addresses_type_tokens", "DELETE FROM \"local_addresses_type_tokens\"", false));
                }

                if (tables.Contains("autofill_profiles"))
                {
                    Add(new PurgeStep("autofill_sync_metadata", $"DELETE FROM \"autofill_sync_metadata\" WHERE model_type = {ChromiumSyncTypeAutofillProfile} AND storage_key IN (SELECT guid FROM \"autofill_profiles\")", false));
                    Add(new PurgeStep("autofill_profiles", "DELETE FROM \"autofill_profiles\"", true, RowSizeExpression: "160",
                        PreviewQuery: "SELECT COALESCE(NULLIF(label, ''), 'address'), COALESCE((SELECT GROUP_CONCAT(e.email, ', ') FROM \"autofill_profile_emails\" e WHERE e.guid = p.guid), ''), use_count, use_date FROM \"autofill_profiles\" p ORDER BY use_date DESC"));
                    foreach (var legacy in ChromiumLegacyAddressTables.Where(t => t is not "local_addresses" and not "local_addresses_type_tokens" and not "autofill_profiles"))
                    {
                        Add(new PurgeStep(legacy, $"DELETE FROM \"{legacy}\"", false));
                    }
                }

                break;

            case DatabasePurge.GeckoFormHistory:
                Add(new PurgeStep("moz_formhistory", "DELETE FROM \"moz_formhistory\"", true,
                    RowSizeExpression: "LENGTH(fieldname) + LENGTH(value) + 48",
                    PreviewQuery: "SELECT fieldname, value, timesUsed, lastUsed FROM \"moz_formhistory\" ORDER BY lastUsed DESC"));
                // Same clean-up Firefox performs after a remove: link rows go with the entries, unused sources are pruned.
                Add(new PurgeStep("moz_history_to_sources", "DELETE FROM \"moz_history_to_sources\" WHERE history_id NOT IN (SELECT id FROM \"moz_formhistory\")", false));
                Add(new PurgeStep("moz_sources", "DELETE FROM \"moz_sources\" WHERE id NOT IN (SELECT DISTINCT source_id FROM \"moz_history_to_sources\")", false));
                break;

            default:
                throw new NotSupportedException($"Unknown purge '{purge}'.");
        }

        // Order matters for the FK-less Firefox schema and for the guid sub-selects: dependent rows first.
        return plan;
    }

    // ------------------------------------------------------------------ helpers

    private static SqliteConnection Open(string path, bool readOnly)
    {
        _ = Engine.Value;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, // never ReadWriteCreate
            Pooling = false,
            DefaultTimeout = BusyTimeoutMs / 1000,
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMs}");
        if (!readOnly)
        {
            Execute(connection, "PRAGMA foreign_keys = ON");
        }

        return connection;
    }

    private static HashSet<string> ListTables(SqliteConnection connection)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    private static void ReadPreview(SqliteConnection connection, string sql, List<DatabaseEntry> entries, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql + $" LIMIT {limit}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var field = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var times = reader.IsDBNull(2) ? 0 : (int)Math.Clamp(reader.GetInt64(2), 0, int.MaxValue);
            var last = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            entries.Add(new DatabaseEntry(field, value, times, ToUtc(last)));
        }
    }

    /// <summary>
    /// Timestamps differ per browser: Chromium autocomplete uses time_t seconds, Firefox and Edge use microseconds
    /// (Unix epoch, or the Windows 1601 epoch for Chromium's base::Time). Pick the interpretation that lands in a
    /// plausible range instead of trusting one format.
    /// </summary>
    internal static DateTime? ToUtc(long raw)
    {
        if (raw <= 0)
        {
            return null;
        }

        var unixSeconds = raw;
        if (raw > 10_000_000_000_000L)
        {
            // Microseconds since 1601 (base::Time) or since 1970.
            var since1601 = DateTime.FromFileTimeUtc(0).AddTicks(raw * 10);
            if (since1601.Year is >= 2000 and <= 2100)
            {
                return since1601;
            }

            unixSeconds = raw / 1_000_000;
        }
        else if (raw > 10_000_000_000L)
        {
            unixSeconds = raw / 1000; // milliseconds
        }

        try
        {
            var result = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            return result.Year is >= 1990 and <= 2100 ? result : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int ExecuteScalarInt(SqliteConnection connection, string sql) => (int)Math.Min(int.MaxValue, ExecuteScalarLong(connection, sql));

    private static long ExecuteScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryExecute(SqliteConnection connection, string sql)
    {
        try
        {
            Execute(connection, sql);
        }
        catch (SqliteException)
        {
            // Optional step (e.g. checkpoint on a rollback-journal database).
        }
    }

    private static bool IsLocked(SqliteException ex) => ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY, SQLITE_LOCKED

    /// <summary>Error text for the log/UI: SQLite messages never include row data, but keep them short anyway.</summary>
    private static string Sanitize(Exception ex)
    {
        var message = ex.Message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return message.Length > 160 ? message[..160] + "…" : message;
    }
}