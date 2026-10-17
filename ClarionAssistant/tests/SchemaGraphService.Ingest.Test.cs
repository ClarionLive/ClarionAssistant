// GH #267 (b9260ec6): the SchemaGraph .dctx ingest carries the dictionary attributes it used to drop -
// key CaseSensitive/AutoNumber/ExternalName, field InitialValue/DerivedFrom/BaseType/ExternalName/Binary/
// Password/ReadOnly, table Path - and a database made before them gains the columns without losing rows.
// Compiles the REAL Services\SchemaGraphService.cs over real x86 SQLite (FTS5 from lib\sqlite-fts5).
// Args: <fixture .dctx>. Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using ClarionAssistant.Services;

static class SchemaGraphIngestTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static SQLiteConnection Open(string db)
    {
        var c = new SQLiteConnection("Data Source=" + db + ";Version=3;");
        c.Open();
        c.EnableExtensions(true);
        c.LoadExtension("SQLite.Interop.dll", "sqlite3_fts5_init");
        return c;
    }

    static object Scalar(string db, string sql)
    {
        using (var c = Open(db))
        using (var cmd = new SQLiteCommand(sql, c))
        {
            var v = cmd.ExecuteScalar();
            return v == DBNull.Value ? null : v;
        }
    }

    static string Str(string db, string sql) { var v = Scalar(db, sql); return v == null ? null : v.ToString(); }
    static long Num(string db, string sql) { var v = Scalar(db, sql); return v == null ? -1 : Convert.ToInt64(v); }

    static HashSet<string> Columns(string db, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var c = Open(db))
        using (var cmd = new SQLiteCommand("PRAGMA table_info(" + table + ")", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) set.Add(r["name"].ToString());
        return set;
    }

    static void Exec(SQLiteConnection c, string sql)
    {
        using (var cmd = new SQLiteCommand(sql, c)) cmd.ExecuteNonQuery();
    }

    // The schema as builds before GH #267 created it (no path / derived_from / ... / is_case_sensitive).
    static void CreatePre267Db(string db)
    {
        SQLiteConnection.CreateFile(db);
        using (var c = Open(db))
        {
            // Every real .schemagraph.db was made through OpenConnection's "Journal Mode=WAL", so it is already WAL
            // (a read-only open of a non-WAL db fails setting it, which is not what this case is about).
            Exec(c, "PRAGMA journal_mode=WAL");
            Exec(c, @"CREATE TABLE tables (id INTEGER PRIMARY KEY AUTOINCREMENT, guid TEXT, name TEXT NOT NULL, prefix TEXT,
                      driver TEXT, description TEXT, source_file TEXT, source TEXT DEFAULT 'dctx', schema_name TEXT)");
            Exec(c, @"CREATE TABLE columns (id INTEGER PRIMARY KEY AUTOINCREMENT, guid TEXT, table_id INTEGER NOT NULL REFERENCES tables(id) ON DELETE CASCADE,
                      name TEXT NOT NULL, data_type TEXT, size INTEGER, places INTEGER, picture TEXT, nullable INTEGER DEFAULT 1,
                      default_value TEXT, description TEXT, ordinal INTEGER)");
            Exec(c, @"CREATE TABLE keys (id INTEGER PRIMARY KEY AUTOINCREMENT, guid TEXT, table_id INTEGER NOT NULL REFERENCES tables(id) ON DELETE CASCADE,
                      name TEXT NOT NULL, is_primary INTEGER DEFAULT 0, is_unique INTEGER DEFAULT 0, is_auto_increment INTEGER DEFAULT 0, is_exclude INTEGER DEFAULT 0)");
            Exec(c, @"CREATE TABLE key_columns (id INTEGER PRIMARY KEY AUTOINCREMENT, key_id INTEGER NOT NULL, column_id INTEGER, ordinal INTEGER, ascending INTEGER DEFAULT 1)");
            Exec(c, @"CREATE TABLE relationships (id INTEGER PRIMARY KEY AUTOINCREMENT, guid TEXT, parent_table_id INTEGER, child_table_id INTEGER, child_key_id INTEGER, description TEXT)");
            Exec(c, @"CREATE TABLE relationship_mappings (id INTEGER PRIMARY KEY AUTOINCREMENT, relationship_id INTEGER NOT NULL, field_guid TEXT)");
            Exec(c, @"CREATE TABLE procedures (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, type TEXT DEFAULT 'procedure', return_type TEXT, definition TEXT, description TEXT, source TEXT DEFAULT 'sql', schema_name TEXT)");
            Exec(c, @"CREATE TABLE procedure_params (id INTEGER PRIMARY KEY AUTOINCREMENT, procedure_id INTEGER NOT NULL, name TEXT NOT NULL, data_type TEXT, max_length INTEGER, direction TEXT DEFAULT 'in', ordinal INTEGER)");
            Exec(c, @"CREATE TABLE views (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, definition TEXT, description TEXT, source TEXT DEFAULT 'sql', schema_name TEXT)");
            Exec(c, @"CREATE TABLE view_references (id INTEGER PRIMARY KEY AUTOINCREMENT, view_id INTEGER NOT NULL, table_id INTEGER)");
            Exec(c, @"CREATE VIRTUAL TABLE schema_fts USING fts5(entity_type, entity_id, name, description, table_name, tokenize='porter unicode61')");
            Exec(c, @"CREATE TABLE schema_metadata (key TEXT PRIMARY KEY, value TEXT)");
            Exec(c, "INSERT INTO tables (id, guid, name, prefix, driver, source) VALUES (1, '{old}', 'OldTable', 'OLD', 'TOPSPEED', 'dctx')");
            Exec(c, "INSERT INTO columns (table_id, name, data_type, size, picture, default_value, ordinal) VALUES (1, 'Code', 'STRING', 10, '@s10', '', 0)");
            Exec(c, "INSERT INTO keys (table_id, name, is_primary, is_unique) VALUES (1, 'CodeKey', 1, 1)");
            Exec(c, "INSERT INTO key_columns (key_id, column_id, ordinal) VALUES (1, 1, 0)");
            Exec(c, "INSERT INTO schema_metadata (key, value) VALUES ('dictionary_name', 'old')");
        }
    }

    static int Main(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.WriteLine("usage: SchemaGraphService.Ingest.Test <fixture.dctx>");
            return 1;
        }
        string dctx = args[0];
        string work = Path.Combine(Path.GetTempPath(), "ca-sg267-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(work);
        try
        {
            // ---------------------------------------------------------------- fresh database
            Console.WriteLine("fresh ingest");
            string db = Path.Combine(work, "fresh.schemagraph.db");
            var svc = new SchemaGraphService(db);
            svc.EnsureDatabase();
            string result = svc.IngestDctx(dctx);
            Check(result.StartsWith("Schema ingested successfully"), "F0", "ingest succeeds: " + result.Split('\n')[0]);

            const string cus = "(SELECT id FROM tables WHERE name='Customer')";
            Func<string, string> col = n => "FROM columns WHERE table_id=" + cus + " AND name='" + n + "'";
            Func<string, string> key = n => "FROM keys WHERE name='" + n + "'";

            Check(Num(db, "SELECT is_case_sensitive " + key("SysIDKey")) == 1, "K1", "CaseSensitive=\"true\" -> keys.is_case_sensitive = 1");
            Check(Num(db, "SELECT is_case_sensitive " + key("NameKey")) == 0, "K2", "CaseSensitive absent -> 0 (NOCASE)");
            Check(Num(db, "SELECT is_auto_increment " + key("SysIDKey")) == 1, "K3", "AutoNumber=\"true\" -> keys.is_auto_increment = 1");
            Check(Num(db, "SELECT is_auto_increment " + key("NameKey")) == 0, "K4", "AutoNumber absent -> 0");
            Check(Str(db, "SELECT external_name " + key("SysIDKey")) == "cus_sysid_pk", "K5", "key ExternalName -> keys.external_name");
            Check(Str(db, "SELECT external_name " + key("NameKey")) == null, "K6", "key ExternalName absent -> NULL");
            Check(Num(db, "SELECT is_primary " + key("SysIDKey")) == 1 && Num(db, "SELECT is_exclude " + key("NameKey")) == 1,
                  "K7", "Primary / Exclude still read");

            Check(Str(db, "SELECT default_value " + col("Created")) == "!TODAY()", "C1", "InitialValue expression kept verbatim, leading '!': " + Str(db, "SELECT default_value " + col("Created")));
            Check(Str(db, "SELECT default_value " + col("Name")) == "'Unknown'", "C2", "InitialValue literal verbatim");
            Check(Str(db, "SELECT derived_from " + col("Status")) == "COD:Status", "C3", "DerivedFrom -> columns.derived_from");
            Check(Str(db, "SELECT base_type " + col("Status")) == "STA:Status", "C4", "BaseType -> columns.base_type");
            Check(Str(db, "SELECT external_name " + col("SysID")) == "SYS_ID", "C5", "field ExternalName -> columns.external_name");
            Check(Num(db, "SELECT is_binary " + col("Photo")) == 1, "C6", "Binary=\"true\" -> is_binary = 1");
            Check(Num(db, "SELECT is_password " + col("Pwd")) == 1, "C7", "Password=\"true\" -> is_password = 1");
            Check(Num(db, "SELECT is_read_only " + col("SysID")) == 1, "C8", "ReadOnly=\"true\" -> is_read_only = 1");
            Check(Num(db, "SELECT is_binary + is_password + is_read_only " + col("Plain")) == 0
                  && Str(db, "SELECT derived_from " + col("Plain")) == null
                  && Str(db, "SELECT base_type " + col("Plain")) == null
                  && Str(db, "SELECT external_name " + col("Plain")) == null,
                  "C9", "a field with none of them: flags 0, text attributes NULL");

            Check(Str(db, "SELECT path FROM tables WHERE name='Customer'") == "Customer.tps", "T1", "table Path -> tables.path");
            Check(Str(db, "SELECT path FROM tables WHERE name='ApiQueue'") == "!MEM:FileName_APIQUE", "T2", "memory-file Path kept verbatim");

            Check(Str(db, "SELECT value FROM schema_metadata WHERE key='schema_version'") == SchemaGraphService.SchemaVersion.ToString(),
                  "V1", "schema_metadata.schema_version survives the ingest's metadata wipe");
            Check(Str(db, "SELECT value FROM schema_metadata WHERE key='" + SchemaGraphService.DctxAttributesMetadataKey + "'") == SchemaGraphService.SchemaVersion.ToString(),
                  "V2", "ingest records which attribute set it captured");

            string t = svc.GetTable("Customer");
            Check(t.Contains("Path: Customer.tps"), "G1", "get_table shows Path");
            Check(t.Contains("initial=!TODAY()") && t.Contains("initial='Unknown'"), "G2", "get_table shows initial values");
            Check(t.Contains("derived_from=COD:Status") && t.Contains("base_type=STA:Status") && t.Contains("external_name=SYS_ID"), "G3", "get_table shows derived_from/base_type/external_name");
            Check(t.Contains("BINARY") && t.Contains("PASSWORD") && t.Contains("READONLY"), "G4", "get_table shows field flags");
            Check(t.Contains("SysIDKey (SysID) [PRIMARY, UNIQUE, AUTO, EXCLUDE, CASE, external_name=cus_sysid_pk]"), "G5", "key line: AUTO, CASE, external name");
            Check(t.Contains("NameKey (Name) [EXCLUDE, NOCASE]"), "G6", "key without CaseSensitive reads NOCASE");
            Check(!t.Contains("Note: this dictionary was ingested before"), "G7", "no re-ingest note after a current ingest");
            Check(svc.GetTable("ApiQueue").Contains("Path: !MEM:FileName_APIQUE"), "G8", "memory-file path shown");

            // SQL-side ingest merging into the same tables: new columns stay NULL/0 there, and it never claims NOCASE.
            string src = Path.Combine(work, "src.sqlite");
            SQLiteConnection.CreateFile(src);
            using (var c = new SQLiteConnection("Data Source=" + src + ";Version=3;"))
            {
                c.Open();
                Exec(c, "CREATE TABLE invoice (id INTEGER PRIMARY KEY, total REAL DEFAULT 0)");
                Exec(c, "CREATE UNIQUE INDEX ix_total ON invoice(total)");
            }
            string sqlRes = svc.IngestSqliteDatabase(src, merge: true);
            Check(!sqlRes.StartsWith("Error"), "S1", "SQLite merge ingest still works with the new columns: " + sqlRes.Split('\n')[0]);
            Check(Num(db, "SELECT COUNT(*) FROM columns WHERE table_id=(SELECT id FROM tables WHERE name='invoice') AND derived_from IS NULL AND is_binary=0") == 2,
                  "S2", "SQL-side columns: derived_from NULL, is_binary 0");
            string inv = svc.GetTable("invoice");
            Check(!inv.Contains("NOCASE") && !inv.Contains("CASE,") && !inv.Contains("Note: this dictionary"), "S3", "SQL-side table: no CASE/NOCASE claim, no dctx note");
            Check(Num(db, "SELECT is_case_sensitive " + key("SysIDKey")) == 1, "S4", "merge kept the dctx rows");

            // ---------------------------------------------------------------- pre-#267 database
            Console.WriteLine("pre-#267 database");
            string old = Path.Combine(work, "old.schemagraph.db");
            CreatePre267Db(old);
            var oldSvc = new SchemaGraphService(old);
            string oldTable = oldSvc.GetTable("OldTable");   // read-only, before any migration
            Check(oldTable.Contains("CodeKey (Code) [PRIMARY, UNIQUE]") && !oldTable.StartsWith("Error"),
                  "M1", "get_table on an unmigrated (read-only) db answers without the new columns");
            Check(oldTable.Contains("Re-run ingest_schema"), "M2", "and tells the model to re-ingest");

            oldSvc.EnsureDatabase();
            var tc = Columns(old, "tables"); var cc = Columns(old, "columns"); var kc = Columns(old, "keys");
            Check(tc.Contains("path"), "M3", "migration adds tables.path");
            Check(cc.Contains("derived_from") && cc.Contains("base_type") && cc.Contains("external_name")
                  && cc.Contains("is_binary") && cc.Contains("is_password") && cc.Contains("is_read_only"),
                  "M4", "migration adds the six columns.* attributes");
            Check(kc.Contains("is_case_sensitive") && kc.Contains("external_name"), "M5", "migration adds keys.is_case_sensitive + keys.external_name");
            Check(Num(old, "SELECT COUNT(*) FROM columns WHERE name='Code'") == 1 && Num(old, "SELECT is_case_sensitive FROM keys WHERE name='CodeKey'") == 0
                  && Num(old, "SELECT is_binary FROM columns WHERE name='Code'") == 0,
                  "M6", "existing rows kept, new flags default 0");
            Check(Str(old, "SELECT value FROM schema_metadata WHERE key='schema_version'") == SchemaGraphService.SchemaVersion.ToString()
                  && Str(old, "SELECT value FROM schema_metadata WHERE key='dictionary_name'") == "old",
                  "M7", "schema_version stamped, other metadata untouched");
            Check(oldSvc.GetTable("OldTable").Contains("Re-run ingest_schema") && !oldSvc.GetTable("OldTable").Contains("NOCASE"),
                  "M8", "migrated but not re-ingested: still says re-ingest, never claims NOCASE from the default 0");
            oldSvc.EnsureDatabase();
            Check(Columns(old, "keys").Count == kc.Count, "M9", "migration is idempotent (second EnsureDatabase adds nothing)");
            string reIngest = oldSvc.IngestDctx(dctx);
            Check(reIngest.StartsWith("Schema ingested successfully") && Num(old, "SELECT is_case_sensitive " + key("SysIDKey")) == 1
                  && Str(old, "SELECT default_value FROM columns WHERE name='Created'") == "!TODAY()",
                  "M10", "re-ingest into the migrated db fills the attributes");
        }
        catch (Exception ex)
        {
            Check(false, "X", "unexpected exception: " + ex);
        }
        finally
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? "PASS  " + _assertions + " assertions"
            : "FAIL  " + Failures.Count + " of " + _assertions + " assertions");
        foreach (var f in Failures) Console.WriteLine("  " + f);
        return Failures.Count == 0 ? 0 : 1;
    }
}
