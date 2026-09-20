using Microsoft.Data.Sqlite;
using PCleaner.Core.Engine;
using PCleaner.Core.Model;

namespace PCleaner.Core.Tests;

/// <summary>
/// Row-level database cleaning. The schemas below are the exact CREATE statements of Chromium's "Web Data"
/// (schema 153, including Microsoft Edge's extra tables) and Firefox's formhistory.sqlite (schema 5).
/// </summary>
public sealed class SqlitePurgerTests
{
    private const string Email = "someone.private@example-mail.test";

    private static string ChromiumPath(TempTree tree) => tree.Dir(@"Brave-Browser\User Data\Default") + @"\Web Data";

    private static string GeckoPath(TempTree tree) => tree.Dir(@"Roaming\Mozilla\Firefox\Profiles\abcd.default-release") + @"\formhistory.sqlite";

    private static void Exec(SqliteConnection connection, params string[] statements)
    {
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarText(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Creates a Web Data file with the tables the purges touch plus the ones they must never touch.</summary>
    private static void CreateWebData(string path, bool edge = false)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Exec(connection,
            "PRAGMA page_size = 2048",
            "CREATE TABLE meta(key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY, value LONGVARCHAR)",
            "INSERT INTO meta VALUES ('version', '153'), ('last_compatible_version', '151')",
            "CREATE TABLE autofill (name VARCHAR, value VARCHAR, value_lower VARCHAR, date_created INTEGER DEFAULT 0, date_last_used INTEGER DEFAULT 0, count INTEGER DEFAULT 1, PRIMARY KEY (name, value))",
            "CREATE INDEX autofill_name ON autofill(name)",
            "CREATE INDEX autofill_name_value_lower ON autofill(name, value_lower)",
            "CREATE TABLE autocomplete (name VARCHAR, label VARCHAR, label_normalized VARCHAR, value VARCHAR, value_lower VARCHAR, date_created INTEGER DEFAULT 0, date_last_used INTEGER DEFAULT 0, count INTEGER DEFAULT 1, PRIMARY KEY (name, label, value))",
            "CREATE TABLE autofill_sync_metadata (model_type INTEGER NOT NULL, storage_key VARCHAR NOT NULL, value BLOB, PRIMARY KEY (model_type, storage_key))",
            "CREATE TABLE autofill_model_type_state (model_type INTEGER NOT NULL PRIMARY KEY, value BLOB)",
            "CREATE TABLE addresses (guid VARCHAR PRIMARY KEY, use_count INTEGER NOT NULL DEFAULT 0, use_date INTEGER NOT NULL DEFAULT 0, date_modified INTEGER NOT NULL DEFAULT 0, language_code VARCHAR, label VARCHAR, initial_creator_id INTEGER DEFAULT 0, record_type INTEGER)",
            "CREATE TABLE address_type_tokens (guid VARCHAR, type INTEGER, value VARCHAR, verification_status INTEGER DEFAULT 0, observations BLOB, PRIMARY KEY (guid, type))",
            "CREATE TABLE keywords (id INTEGER PRIMARY KEY, short_name VARCHAR NOT NULL, keyword VARCHAR NOT NULL, url VARCHAR NOT NULL)",
            "CREATE TABLE credit_cards (guid VARCHAR PRIMARY KEY, name_on_card VARCHAR, expiration_month INTEGER, expiration_year INTEGER, card_number_encrypted BLOB, date_modified INTEGER NOT NULL DEFAULT 0, use_count INTEGER NOT NULL DEFAULT 0, use_date INTEGER NOT NULL DEFAULT 0, billing_address_id VARCHAR, nickname VARCHAR)",
            "CREATE TABLE token_service (service VARCHAR PRIMARY KEY NOT NULL, encrypted_token BLOB, binding_key BLOB)",
            // 200 form-history rows, three of them e-mail addresses
            "WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < 197) INSERT INTO autofill SELECT 'q', 'search term ' || i, 'search term ' || i, 1700000000 + i, 1700000000 + i, 1 FROM seq",
            $"INSERT INTO autofill VALUES ('email', '{Email}', '{Email}', 1700000000, 1725000000, 12)",
            $"INSERT INTO autofill VALUES ('identifier', '{Email}', '{Email}', 1700000000, 1726000000, 3)",
            "INSERT INTO autofill VALUES ('username', 'adel', 'adel', 1700000000, 1724000000, 2)",
            $"INSERT INTO autocomplete VALUES ('identifier', 'Email or phone', 'email or phone', '{Email}', '{Email}', 1700000000, 1726000000, 3)",
            // sync metadata: autocomplete entries (7) and an address profile (6); progress markers for both
            "INSERT INTO autofill_sync_metadata VALUES (7, X'0A05656D61696C120A736F6D65406D61696C', X'00'), (7, X'0A0871', X'00'), (6, 'local-guid-1', X'00'), (6, 'other-guid', X'00')",
            "INSERT INTO autofill_model_type_state VALUES (7, X'AA'), (6, X'BB')",
            // addresses: one local, one from the account, one account name+email profile
            $"INSERT INTO addresses VALUES ('local-guid-1', 4, 1725000000, 1725000000, 'en', 'Home', 0, 0), ('account-guid-1', 9, 1725000000, 1725000000, 'en', 'Work', 0, 1), ('name-email-guid', 0, 0, 0, 'en', '', 0, 4)",
            $"INSERT INTO address_type_tokens VALUES ('local-guid-1', 9, '{Email}', 0, NULL), ('local-guid-1', 3, 'Adel Example', 0, NULL), ('account-guid-1', 9, 'work@example-mail.test', 0, NULL), ('name-email-guid', 9, 'account@example-mail.test', 0, NULL)",
            "INSERT INTO keywords VALUES (1, 'Google', 'google.com', 'https://www.google.com/search?q={searchTerms}'), (2, 'DuckDuckGo', 'duckduckgo.com', 'https://duckduckgo.com/?q={searchTerms}')",
            "INSERT INTO credit_cards VALUES ('card-1', 'A. Example', 12, 2030, X'DEADBEEF', 0, 0, 0, '', 'Main card')",
            "INSERT INTO token_service VALUES ('AccountId-1', X'01', NULL)");

        if (edge)
        {
            Exec(connection,
                "CREATE TABLE autofill_edge_field_values (field_id VARCHAR, value VARCHAR, count INTEGER DEFAULT 1, date_created INTEGER DEFAULT 0, date_last_used INTEGER DEFAULT 0, submission_source INTEGER NOT NULL DEFAULT 0, is_masked BOOLEAN NOT NULL DEFAULT FALSE, PRIMARY KEY (field_id, value))",
                "CREATE TABLE autofill_edge_field_client_info (field_id VARCHAR PRIMARY KEY, form_signature_v1 VARCHAR, form_signature_v2 VARCHAR, field_signature_v1 VARCHAR, field_signature_v2 VARCHAR, domain_signature VARCHAR, domain_value VARCHAR, label VARCHAR, date_created INTEGER DEFAULT 0)",
                "CREATE TABLE autofill_edge_fieldid_cid_mapping (field_id VARCHAR PRIMARY KEY, c_id VARCHAR, last_sync_time INTEGER DEFAULT 0)",
                "CREATE TABLE autofill_edge_extended (name VARCHAR, value VARCHAR, label VARCHAR DEFAULT '', guid VARCHAR, url_domain VARCHAR DEFAULT '', form_signature VARCHAR DEFAULT '', field_signature VARCHAR DEFAULT '', date_created INTEGER DEFAULT 0, date_last_used INTEGER DEFAULT 0, source INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (name, value, form_signature, field_signature))",
                "CREATE TABLE autofill_profile_edge_extended (guid VARCHAR PRIMARY KEY, date_of_birth_day VARCHAR, date_of_birth_month VARCHAR, date_of_birth_year VARCHAR, source INTEGER NOT NULL DEFAULT 0, source_id VARCHAR, digital_id_category INTEGER NOT NULL DEFAULT 0)",
                "CREATE TABLE credit_cards_edge_extended (guid VARCHAR PRIMARY KEY, card_art_url VARCHAR)",
                "CREATE TABLE edge_server_addresses (guid VARCHAR PRIMARY KEY, use_count INTEGER)",
                $"INSERT INTO autofill_edge_field_values VALUES ('f1', '{Email}', 5, 0, 13400000000000000, 0, 0), ('f2', 'Adel', 1, 0, 13400000000000000, 0, 0)",
                "INSERT INTO autofill_edge_field_client_info VALUES ('f1', '', '', '', '', '', 'accounts.google.com', 'Email or phone', 0), ('f2', '', '', '', '', '', 'example.com', 'First name', 0)",
                "INSERT INTO autofill_edge_fieldid_cid_mapping VALUES ('f1', 'cid-1', 0)",
                $"INSERT INTO autofill_edge_extended VALUES ('email', '{Email}', 'Email', 'g1', 'example.com', '1', '2', 0, 0, 0)",
                "INSERT INTO autofill_profile_edge_extended VALUES ('local-guid-1', '1', '2', '1990', 0, NULL, 0), ('account-guid-1', '3', '4', '1991', 1, 'sid', 0)",
                "INSERT INTO credit_cards_edge_extended VALUES ('card-1', 'https://art')",
                "INSERT INTO edge_server_addresses VALUES ('server-1', 1)");
        }
    }

    private static void CreateFormHistory(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Exec(connection,
            "PRAGMA page_size = 32768",
            "PRAGMA user_version = 5",
            "CREATE TABLE moz_formhistory (id INTEGER PRIMARY KEY, fieldname TEXT NOT NULL, value TEXT NOT NULL, timesUsed INTEGER, firstUsed INTEGER, lastUsed INTEGER, guid TEXT)",
            "CREATE TABLE moz_deleted_formhistory (id INTEGER PRIMARY KEY, timeDeleted INTEGER, guid TEXT)",
            "CREATE TABLE moz_sources (id INTEGER PRIMARY KEY, source TEXT NOT NULL)",
            "CREATE TABLE moz_history_to_sources (history_id INTEGER, source_id INTEGER, PRIMARY KEY (history_id, source_id), FOREIGN KEY (history_id) REFERENCES moz_formhistory(id) ON DELETE CASCADE, FOREIGN KEY (source_id) REFERENCES moz_sources(id) ON DELETE CASCADE) WITHOUT ROWID",
            "CREATE INDEX moz_formhistory_index ON moz_formhistory(fieldname)",
            "CREATE INDEX moz_formhistory_lastused_index ON moz_formhistory(lastUsed)",
            "CREATE INDEX moz_formhistory_guid_index ON moz_formhistory(guid)",
            $"INSERT INTO moz_formhistory VALUES (1, 'email', '{Email}', 7, 1700000000000000, 1726000000000000, 'guidAAAAAAAAAAAA'), (2, 'searchbar-history', 'weather', 2, 1700000000000000, 1725000000000000, 'guidBBBBBBBBBBBB')",
            "INSERT INTO moz_deleted_formhistory VALUES (1, 1720000000000000, 'guidOLDOLDOLDOLD')",
            "INSERT INTO moz_sources VALUES (1, 'https://accounts.example.test/login')",
            "INSERT INTO moz_history_to_sources VALUES (1, 1)");
    }

    private static bool FileContains(string path, string text)
    {
        var bytes = File.ReadAllBytes(path);
        var needle = System.Text.Encoding.UTF8.GetBytes(text);
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }

    [Fact]
    public void Chromium_form_history_purge_removes_entries_and_keeps_everything_else()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        CreateWebData(path);
        Assert.True(FileContains(path, Email));
        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumFormHistory };
        Assert.Null(SafetyGuard.ValidateDatabaseTarget(target));

        var inspection = SqlitePurger.Inspect(target);
        Assert.Null(inspection.Error);
        Assert.Equal(201, inspection.EntryCount); // 200 autofill + 1 autocomplete
        Assert.Contains(inspection.Entries, e => e.Field == "email" && e.Value == Email && e.TimesUsed == 12);
        Assert.Contains(inspection.Entries, e => e.Field == "Email or phone" && e.Value == Email);
        Assert.Equal(200, Scalar(path, "SELECT COUNT(*) FROM autofill")); // inspect never modifies
        Assert.True(inspection.EstimatedBytes > 0);
        Assert.NotNull(inspection.Entries[0].LastUsedUtc);
        Assert.Equal(2024, inspection.Entries[0].LastUsedUtc!.Value.Year);

        var sizeBefore = new FileInfo(path).Length;
        var result = SqlitePurger.Purge(target, dryRun: false);
        Assert.Null(result.Error);
        Assert.Equal(201, result.EntriesRemoved);

        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autocomplete"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill_sync_metadata WHERE model_type = 7"));
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM autofill_sync_metadata WHERE model_type = 6"));
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM autofill_model_type_state"));
        Assert.Equal(3, Scalar(path, "SELECT COUNT(*) FROM addresses"));
        Assert.Equal(4, Scalar(path, "SELECT COUNT(*) FROM address_type_tokens"));
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM keywords"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM credit_cards"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM token_service"));
        Assert.Equal("153", ScalarText(path, "SELECT value FROM meta WHERE key = 'version'"));
        Assert.Equal("ok", ScalarText(path, "PRAGMA integrity_check"));
        Assert.Equal(2048, Scalar(path, "PRAGMA page_size"));
        Assert.True(new FileInfo(path).Length < sizeBefore, "VACUUM must shrink the file");
        Assert.True(result.BytesFreed > 0);

        // The address still legitimately contains the e-mail; the form-history copies must be gone from the raw file.
        Assert.Equal(1, Scalar(path, $"SELECT COUNT(*) FROM address_type_tokens WHERE value = '{Email}'"));
        Assert.False(FileContains(path, "search term 1"), "deleted rows must not linger in free pages");
        Assert.False(FileContains(path, "Email or phone"));
        Assert.False(File.Exists(path + "-journal"));
    }

    [Fact]
    public void Edge_form_history_purge_covers_edge_tables_and_protects_wallet_tables()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        CreateWebData(path, edge: true);
        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumFormHistory };

        var inspection = SqlitePurger.Inspect(target);
        Assert.Null(inspection.Error);
        Assert.Equal(204, inspection.EntryCount); // 200 + 1 + 2 edge field values + 1 edge extended
        Assert.Contains(inspection.Entries, e => e.Field == "Email or phone" && e.Value == Email && e.TimesUsed == 5);

        var result = SqlitePurger.Purge(target, dryRun: false);
        Assert.Null(result.Error);
        Assert.Equal(204, result.EntriesRemoved);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill_edge_field_values"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill_edge_field_client_info"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill_edge_fieldid_cid_mapping"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill_edge_extended"));
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM autofill_profile_edge_extended"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM credit_cards_edge_extended"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM edge_server_addresses"));
        Assert.False(FileContains(path, "accounts.google.com"), "field descriptors (visited domains) go with the values");
    }

    [Fact]
    public void Chromium_local_addresses_purge_keeps_account_addresses()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        CreateWebData(path, edge: true);
        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumLocalAddresses };

        var inspection = SqlitePurger.Inspect(target);
        Assert.Null(inspection.Error);
        Assert.Equal(1, inspection.EntryCount);
        var entry = Assert.Single(inspection.Entries);
        Assert.Equal("Home", entry.Field);
        Assert.Contains(Email, entry.Value, StringComparison.Ordinal);
        Assert.Contains("Adel Example", entry.Value, StringComparison.Ordinal);
        Assert.Equal(4, entry.TimesUsed);

        var result = SqlitePurger.Purge(target, dryRun: false);
        Assert.Null(result.Error);
        Assert.Equal(1, result.EntriesRemoved);

        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM addresses"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM addresses WHERE record_type = 0"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM addresses WHERE guid = 'account-guid-1'"));
        Assert.True(1 == Scalar(path, "SELECT COUNT(*) FROM addresses WHERE guid = 'name-email-guid'"), "the account name+email profile is browser-managed and stays");
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM address_type_tokens WHERE guid = 'local-guid-1'"));
        Assert.Equal(2, Scalar(path, "SELECT COUNT(*) FROM address_type_tokens"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill_sync_metadata WHERE model_type = 6 AND storage_key = 'local-guid-1'"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM autofill_sync_metadata WHERE model_type = 6 AND storage_key = 'other-guid'"));
        Assert.True(2 == Scalar(path, "SELECT COUNT(*) FROM autofill_sync_metadata WHERE model_type = 7"), "form-history metadata is not this rule's business");
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill_profile_edge_extended WHERE guid = 'local-guid-1'"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM autofill_profile_edge_extended"));
        Assert.True(200 == Scalar(path, "SELECT COUNT(*) FROM autofill"), "form history is a separate rule");
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM credit_cards"));
        Assert.Equal("ok", ScalarText(path, "PRAGMA integrity_check"));
    }

    [Fact]
    public void Legacy_local_addresses_schema_is_handled_and_contact_info_is_protected()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            Exec(connection,
                "CREATE TABLE meta(key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY, value LONGVARCHAR)",
                "CREATE TABLE local_addresses (guid VARCHAR PRIMARY KEY, use_count INTEGER, use_date INTEGER, date_modified INTEGER, language_code VARCHAR, label VARCHAR, initial_creator_id INTEGER, last_modifier_id INTEGER)",
                "CREATE TABLE local_addresses_type_tokens (guid VARCHAR, type INTEGER, value VARCHAR, verification_status INTEGER, observations BLOB, PRIMARY KEY (guid, type))",
                "CREATE TABLE contact_info (guid VARCHAR PRIMARY KEY, use_count INTEGER, use_date INTEGER, date_modified INTEGER, language_code VARCHAR, label VARCHAR, initial_creator_id INTEGER, last_modifier_id INTEGER)",
                "CREATE TABLE contact_info_type_tokens (guid VARCHAR, type INTEGER, value VARCHAR, verification_status INTEGER, observations BLOB, PRIMARY KEY (guid, type))",
                "CREATE TABLE autofill_sync_metadata (model_type INTEGER NOT NULL, storage_key VARCHAR NOT NULL, value BLOB, PRIMARY KEY (model_type, storage_key))",
                "INSERT INTO local_addresses VALUES ('l1', 1, 1725000000, 0, 'en', 'Home', 0, 0)",
                $"INSERT INTO local_addresses_type_tokens VALUES ('l1', 9, '{Email}', 0, NULL)",
                "INSERT INTO contact_info VALUES ('a1', 1, 1725000000, 0, 'en', 'Work', 0, 0)",
                "INSERT INTO contact_info_type_tokens VALUES ('a1', 9, 'work@example-mail.test', 0, NULL)",
                "INSERT INTO autofill_sync_metadata VALUES (6, 'l1', X'00'), (54, 'a1', X'00')");
        }

        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumLocalAddresses };
        Assert.Equal(1, SqlitePurger.Inspect(target).EntryCount);
        var result = SqlitePurger.Purge(target, dryRun: false);
        Assert.Null(result.Error);
        Assert.Equal(1, result.EntriesRemoved);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM local_addresses"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM local_addresses_type_tokens"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM contact_info"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM contact_info_type_tokens"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill_sync_metadata WHERE storage_key = 'l1'"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM autofill_sync_metadata WHERE storage_key = 'a1'"));
    }

    [Fact]
    public void Gecko_form_history_purge_mirrors_firefox_remove_path()
    {
        using var tree = new TempTree();
        var path = GeckoPath(tree);
        CreateFormHistory(path);
        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.GeckoFormHistory };
        Assert.Null(SafetyGuard.ValidateDatabaseTarget(target));

        var inspection = SqlitePurger.Inspect(target);
        Assert.Null(inspection.Error);
        Assert.Equal(2, inspection.EntryCount);
        Assert.Equal(Email, inspection.Entries[0].Value); // ordered by lastUsed desc
        Assert.Equal(7, inspection.Entries[0].TimesUsed);
        Assert.Equal(2024, inspection.Entries[0].LastUsedUtc!.Value.Year); // microseconds since 1970

        var result = SqlitePurger.Purge(target, dryRun: false);
        Assert.Null(result.Error);
        Assert.Equal(2, result.EntriesRemoved);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM moz_formhistory"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM moz_history_to_sources"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM moz_sources"));
        Assert.True(1 == Scalar(path, "SELECT COUNT(*) FROM moz_deleted_formhistory"), "tombstone table is left to Firefox");
        Assert.Equal(5, Scalar(path, "PRAGMA user_version"));
        Assert.Equal(32768, Scalar(path, "PRAGMA page_size"));
        Assert.Equal("ok", ScalarText(path, "PRAGMA integrity_check"));
        Assert.False(FileContains(path, Email));
        Assert.False(FileContains(path, "accounts.example.test"));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-journal"));
    }

    [Fact]
    public void Dry_run_counts_but_changes_nothing()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        CreateWebData(path);
        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumFormHistory };
        var stamp = File.GetLastWriteTimeUtc(path);

        var result = SqlitePurger.Purge(target, dryRun: true);

        Assert.Null(result.Error);
        Assert.Equal(201, result.EntriesRemoved);
        Assert.Equal(200, Scalar(path, "SELECT COUNT(*) FROM autofill"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Unknown_schema_is_refused_without_changes()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            Exec(connection, "CREATE TABLE something_else (x)", "INSERT INTO something_else VALUES (1)");
        }

        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumFormHistory };
        Assert.NotNull(SqlitePurger.Inspect(target).Error);
        var result = SqlitePurger.Purge(target, dryRun: false);
        Assert.NotNull(result.Error);
        Assert.Equal(0, result.EntriesRemoved);
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM something_else"));
    }

    [Fact]
    public void Missing_database_is_reported_not_created()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumFormHistory };

        Assert.False(SqlitePurger.Inspect(target).Exists);
        Assert.NotNull(SqlitePurger.Purge(target, dryRun: false).Error);
        Assert.False(File.Exists(path), "the purger must never create a database");
    }

    [Fact]
    public void Locked_database_fails_fast_without_changes()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        CreateWebData(path);
        var target = new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumFormHistory };

        // Emulate the running browser: Chromium holds "Web Data" in exclusive locking mode.
        using var browser = new SqliteConnection($"Data Source={path};Pooling=False");
        browser.Open();
        Exec(browser, "PRAGMA locking_mode = EXCLUSIVE", "BEGIN IMMEDIATE", "UPDATE meta SET value = value WHERE key = 'version'");

        var result = SqlitePurger.Purge(target, dryRun: false);
        Assert.NotNull(result.Error);
        Assert.Contains("in use", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.EntriesRemoved);

        Exec(browser, "ROLLBACK");
        browser.Close();
        Assert.Equal(200, Scalar(path, "SELECT COUNT(*) FROM autofill"));
    }

    [Fact]
    public void Safety_guard_restricts_database_targets()
    {
        using var tree = new TempTree();
        var webData = ChromiumPath(tree);
        var loginData = Path.Combine(Path.GetDirectoryName(webData)!, "Login Data");
        var outside = Path.Combine(tree.Root, "Documents", "Web Data");
        var inExtensions = tree.Dir(@"Chrome\User Data\Default\Extensions\abc") + @"\Web Data";

        Assert.Null(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = webData, Purge = DatabasePurge.ChromiumFormHistory }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = loginData, Purge = DatabasePurge.ChromiumFormHistory }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = webData, Purge = DatabasePurge.GeckoFormHistory }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = outside, Purge = DatabasePurge.ChromiumFormHistory }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = inExtensions, Purge = DatabasePurge.ChromiumFormHistory }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = @"\\server\share\User Data\Default\Web Data", Purge = DatabasePurge.ChromiumFormHistory }));
        Assert.NotNull(SafetyGuard.ValidateDatabaseTarget(new DatabaseTarget { Path = @"User Data\Default\Web Data", Purge = DatabasePurge.ChromiumFormHistory }));
    }

    [Fact]
    public void Timestamps_of_every_browser_are_understood()
    {
        Assert.Equal(new DateTime(2024, 9, 4, 6, 40, 0, DateTimeKind.Utc), SqlitePurger.ToUtc(1725432000)); // Chromium: seconds
        Assert.Equal(new DateTime(2024, 9, 4, 6, 40, 0, DateTimeKind.Utc), SqlitePurger.ToUtc(1725432000_000_000)); // Firefox: microseconds
        Assert.Equal(new DateTime(2024, 9, 4, 6, 40, 0, DateTimeKind.Utc), SqlitePurger.ToUtc(13369905600_000_000)); // base::Time: microseconds since 1601
        Assert.Null(SqlitePurger.ToUtc(0));
        Assert.Null(SqlitePurger.ToUtc(-5));
    }

    [Fact]
    public async Task Engine_scans_and_cleans_database_rules_end_to_end()
    {
        using var tree = new TempTree();
        var path = ChromiumPath(tree);
        CreateWebData(path);
        var rule = new CleanupRule
        {
            Id = "test.formhistory",
            Name = "Form history",
            Description = "test",
            Category = RuleCategory.Browsers,
            Group = "Test",
            Risk = RiskLevel.Privacy,
            Action = RuleAction.PurgeDatabaseRows,
            Databases = [new DatabaseTarget { Path = path, Purge = DatabasePurge.ChromiumFormHistory }],
        };

        var scans = await new Scanner().ScanAsync([rule], null, CancellationToken.None);
        var scan = Assert.Single(scans);
        Assert.Equal(SkipReason.None, scan.Skip);
        Assert.Equal(201, scan.EntryCount);
        Assert.NotEmpty(scan.Entries);
        Assert.Empty(scan.Items);
        Assert.True(scan.IsEstimate);

        var cleans = await new Cleaner().CleanAsync(scans, null, CancellationToken.None);
        var clean = Assert.Single(cleans);
        Assert.Equal(SkipReason.None, clean.Skip);
        Assert.Equal(201, clean.DeletedEntries);
        Assert.Empty(clean.Failures);
        Assert.Contains("201 entries removed", clean.Message, StringComparison.Ordinal);
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM autofill"));

        // A second pass finds nothing and says so.
        var again = await new Scanner().ScanAsync([rule], null, CancellationToken.None);
        Assert.Equal(0, again[0].EntryCount);
        var cleanAgain = await new Cleaner().CleanAsync(again, null, CancellationToken.None);
        Assert.Equal("No entries to remove.", cleanAgain[0].Message);
    }
}