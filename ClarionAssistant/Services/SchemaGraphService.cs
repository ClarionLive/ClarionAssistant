using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Xml;
using System.Security.Cryptography;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// SchemaGraph: indexes Clarion dictionary schemas (tables, columns, keys, relationships)
    /// into a per-project SQLite database for fast agent queries.
    /// </summary>
    public class SchemaGraphService
    {
        private string _dbPath;

        public string DbPath { get { return _dbPath; } }

        public SchemaGraphService(string dbPath)
        {
            _dbPath = dbPath;
        }

        /// <summary>
        /// Derives the schemagraph database path from a .dctx or .dct file path.
        /// Stored alongside the dictionary as dictname.schemagraph.db
        /// </summary>
        public static string GetDbPathForDictionary(string dctxPath)
        {
            string dir = Path.GetDirectoryName(dctxPath);
            string name = Path.GetFileNameWithoutExtension(dctxPath);
            return Path.Combine(dir, name + ".schemagraph.db");
        }

        #region Database Setup

        public void EnsureDatabase()
        {
            using (var conn = OpenConnection(readOnly: false))
            {
                CreateSchema(conn);
            }
        }

        private void CreateSchema(SQLiteConnection conn)
        {
            string[] ddl = new[]
            {
                @"CREATE TABLE IF NOT EXISTS tables (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    guid TEXT,
                    name TEXT NOT NULL,
                    prefix TEXT,
                    driver TEXT,
                    description TEXT,
                    source_file TEXT,
                    source TEXT DEFAULT 'dctx',
                    schema_name TEXT
                )",

                @"CREATE TABLE IF NOT EXISTS columns (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    guid TEXT,
                    table_id INTEGER NOT NULL REFERENCES tables(id) ON DELETE CASCADE,
                    name TEXT NOT NULL,
                    data_type TEXT,
                    size INTEGER,
                    places INTEGER,
                    picture TEXT,
                    nullable INTEGER DEFAULT 1,
                    default_value TEXT,
                    description TEXT,
                    ordinal INTEGER
                )",

                @"CREATE TABLE IF NOT EXISTS keys (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    guid TEXT,
                    table_id INTEGER NOT NULL REFERENCES tables(id) ON DELETE CASCADE,
                    name TEXT NOT NULL,
                    is_primary INTEGER DEFAULT 0,
                    is_unique INTEGER DEFAULT 0,
                    is_auto_increment INTEGER DEFAULT 0,
                    is_exclude INTEGER DEFAULT 0
                )",

                @"CREATE TABLE IF NOT EXISTS key_columns (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    key_id INTEGER NOT NULL REFERENCES keys(id) ON DELETE CASCADE,
                    column_id INTEGER,
                    ordinal INTEGER,
                    ascending INTEGER DEFAULT 1
                )",

                @"CREATE TABLE IF NOT EXISTS relationships (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    guid TEXT,
                    parent_table_id INTEGER REFERENCES tables(id),
                    child_table_id INTEGER REFERENCES tables(id),
                    child_key_id INTEGER REFERENCES keys(id),
                    description TEXT
                )",

                @"CREATE TABLE IF NOT EXISTS relationship_mappings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    relationship_id INTEGER NOT NULL REFERENCES relationships(id) ON DELETE CASCADE,
                    field_guid TEXT
                )",

                @"CREATE TABLE IF NOT EXISTS procedures (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    type TEXT DEFAULT 'procedure',
                    return_type TEXT,
                    definition TEXT,
                    description TEXT,
                    source TEXT DEFAULT 'sql',
                    schema_name TEXT
                )",

                @"CREATE TABLE IF NOT EXISTS procedure_params (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    procedure_id INTEGER NOT NULL REFERENCES procedures(id) ON DELETE CASCADE,
                    name TEXT NOT NULL,
                    data_type TEXT,
                    max_length INTEGER,
                    direction TEXT DEFAULT 'in',
                    ordinal INTEGER
                )",

                @"CREATE TABLE IF NOT EXISTS views (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    definition TEXT,
                    description TEXT,
                    source TEXT DEFAULT 'sql',
                    schema_name TEXT
                )",

                @"CREATE TABLE IF NOT EXISTS view_references (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    view_id INTEGER NOT NULL REFERENCES views(id) ON DELETE CASCADE,
                    table_id INTEGER REFERENCES tables(id)
                )",

                // FTS5 for fuzzy search
                @"CREATE VIRTUAL TABLE IF NOT EXISTS schema_fts USING fts5(
                    entity_type,
                    entity_id,
                    name,
                    description,
                    table_name,
                    tokenize='porter unicode61'
                )",

                // Metadata
                @"CREATE TABLE IF NOT EXISTS schema_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT
                )",

                // Indexes
                @"CREATE INDEX IF NOT EXISTS idx_columns_table ON columns(table_id)",
                @"CREATE INDEX IF NOT EXISTS idx_keys_table ON keys(table_id)",
                @"CREATE INDEX IF NOT EXISTS idx_key_columns_key ON key_columns(key_id)",
                @"CREATE INDEX IF NOT EXISTS idx_rel_parent ON relationships(parent_table_id)",
                @"CREATE INDEX IF NOT EXISTS idx_rel_child ON relationships(child_table_id)",
                @"CREATE INDEX IF NOT EXISTS idx_tables_name ON tables(name COLLATE NOCASE)",
                @"CREATE INDEX IF NOT EXISTS idx_columns_name ON columns(name COLLATE NOCASE)",
                @"CREATE INDEX IF NOT EXISTS idx_tables_guid ON tables(guid)",
                @"CREATE INDEX IF NOT EXISTS idx_columns_guid ON columns(guid)",
                @"CREATE INDEX IF NOT EXISTS idx_keys_guid ON keys(guid)"
            };

            foreach (string sql in ddl)
            {
                using (var cmd = new SQLiteCommand(sql, conn))
                    cmd.ExecuteNonQuery();
            }
        }

        #endregion

        #region Ingestion

        /// <summary>
        /// Ingest a .dctx file into the schema database. Clears existing data first.
        /// Returns a summary string.
        /// </summary>
        public string IngestDctx(string dctxPath)
        {
            if (!File.Exists(dctxPath))
                return "Error: File not found: " + dctxPath;

            var doc = new XmlDocument();
            try
            {
                doc.Load(dctxPath);
            }
            catch (Exception ex)
            {
                return "Error: Failed to parse XML: " + ex.Message;
            }

            var root = doc.DocumentElement;
            if (root == null || root.Name != "Dictionary")
                return "Error: Not a valid .dctx file (expected <Dictionary> root element)";

            string dictName = root.GetAttribute("Name");

            using (var conn = OpenConnection(readOnly: false))
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    // Clear existing data
                    ClearAllData(conn);

                    // Build GUID-to-ID lookup maps during ingestion
                    var tableGuidToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    var fieldGuidToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    var keyGuidToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                    int tableCount = 0;
                    int columnCount = 0;
                    int keyCount = 0;
                    int relationCount = 0;

                    // Pass 1: Ingest tables, fields, keys
                    var tableNodes = root.SelectNodes("Table");
                    if (tableNodes != null)
                    {
                        foreach (XmlNode tableNode in tableNodes)
                        {
                            var tableElem = tableNode as XmlElement;
                            if (tableElem == null) continue;

                            string tGuid = tableElem.GetAttribute("Guid");
                            string tName = tableElem.GetAttribute("Name");
                            string tPrefix = tableElem.GetAttribute("Prefix");
                            string tDriver = tableElem.GetAttribute("Driver");
                            string tDesc = tableElem.GetAttribute("Description");

                            long tableId = InsertTable(conn, tGuid, tName, tPrefix, tDriver, tDesc, dctxPath);
                            if (!string.IsNullOrEmpty(tGuid))
                                tableGuidToId[tGuid] = tableId;
                            tableCount++;

                            // Fields
                            int fieldOrdinal = 0;
                            var fieldNodes = tableElem.SelectNodes("Field");
                            if (fieldNodes != null)
                            {
                                foreach (XmlNode fieldNode in fieldNodes)
                                {
                                    var fieldElem = fieldNode as XmlElement;
                                    if (fieldElem == null) continue;

                                    string fGuid = fieldElem.GetAttribute("Guid");
                                    string fName = fieldElem.GetAttribute("Name");
                                    string fType = fieldElem.GetAttribute("DataType");
                                    int fSize = ParseInt(fieldElem.GetAttribute("Size"), 0);
                                    int fPlaces = ParseInt(fieldElem.GetAttribute("Places"), 0);
                                    string fPicture = fieldElem.GetAttribute("ScreenPicture");
                                    string fDesc = fieldElem.GetAttribute("Description");
                                    string fDefault = fieldElem.GetAttribute("Initial");

                                    long colId = InsertColumn(conn, fGuid, tableId, fName, fType, fSize, fPlaces,
                                        fPicture, fDefault, fDesc, fieldOrdinal);
                                    if (!string.IsNullOrEmpty(fGuid))
                                        fieldGuidToId[fGuid] = colId;
                                    fieldOrdinal++;
                                    columnCount++;
                                }
                            }

                            // Keys
                            var keyNodes = tableElem.SelectNodes("Key");
                            if (keyNodes != null)
                            {
                                foreach (XmlNode keyNode in keyNodes)
                                {
                                    var keyElem = keyNode as XmlElement;
                                    if (keyElem == null) continue;

                                    string kGuid = keyElem.GetAttribute("Guid");
                                    string kName = keyElem.GetAttribute("Name");
                                    bool kPrimary = keyElem.GetAttribute("Primary") == "true";
                                    bool kUnique = keyElem.GetAttribute("Unique") == "true";
                                    bool kAutoInc = keyElem.GetAttribute("AutoIncrement") == "true";
                                    bool kExclude = keyElem.GetAttribute("Exclude") == "true";

                                    long keyId = InsertKey(conn, kGuid, tableId, kName, kPrimary, kUnique, kAutoInc, kExclude);
                                    if (!string.IsNullOrEmpty(kGuid))
                                        keyGuidToId[kGuid] = keyId;
                                    keyCount++;

                                    // Key components
                                    var compNodes = keyElem.SelectNodes("Component");
                                    if (compNodes != null)
                                    {
                                        int compOrdinal = 0;
                                        foreach (XmlNode compNode in compNodes)
                                        {
                                            var compElem = compNode as XmlElement;
                                            if (compElem == null) continue;

                                            string cfGuid = compElem.GetAttribute("FieldId");
                                            bool ascending = compElem.GetAttribute("Ascend") != "false";

                                            long colId = 0;
                                            if (!string.IsNullOrEmpty(cfGuid))
                                                fieldGuidToId.TryGetValue(cfGuid, out colId);

                                            InsertKeyColumn(conn, keyId, colId > 0 ? colId : (long?)null, compOrdinal, ascending);
                                            compOrdinal++;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Pass 2: Ingest relationships (at root level, reference tables/keys by GUID)
                    var relNodes = root.SelectNodes("Relation");
                    if (relNodes != null)
                    {
                        foreach (XmlNode relNode in relNodes)
                        {
                            var relElem = relNode as XmlElement;
                            if (relElem == null) continue;

                            string rGuid = relElem.GetAttribute("Guid");
                            string primaryTableGuid = relElem.GetAttribute("PrimaryTable");
                            string foreignTableGuid = relElem.GetAttribute("ForeignTable");
                            string foreignKeyGuid = relElem.GetAttribute("ForeignKey");

                            long parentId = 0, childId = 0, childKeyId = 0;
                            if (!string.IsNullOrEmpty(primaryTableGuid))
                                tableGuidToId.TryGetValue(primaryTableGuid, out parentId);
                            if (!string.IsNullOrEmpty(foreignTableGuid))
                                tableGuidToId.TryGetValue(foreignTableGuid, out childId);
                            if (!string.IsNullOrEmpty(foreignKeyGuid))
                                keyGuidToId.TryGetValue(foreignKeyGuid, out childKeyId);

                            long relId = InsertRelationship(conn, rGuid,
                                parentId > 0 ? parentId : (long?)null,
                                childId > 0 ? childId : (long?)null,
                                childKeyId > 0 ? childKeyId : (long?)null,
                                null);
                            relationCount++;

                            // Foreign mappings
                            var mapNodes = relElem.SelectNodes("ForeignMapping");
                            if (mapNodes != null)
                            {
                                foreach (XmlNode mapNode in mapNodes)
                                {
                                    var mapElem = mapNode as XmlElement;
                                    if (mapElem == null) continue;

                                    string mfGuid = mapElem.GetAttribute("Field");
                                    InsertRelationshipMapping(conn, relId, mfGuid);
                                }
                            }
                        }
                    }

                    // Save metadata
                    SetMetadata(conn, "source_file", dctxPath);
                    SetMetadata(conn, "dictionary_name", dictName);
                    SetMetadata(conn, "ingested_at", DateTime.Now.ToString("o"));
                    SetMetadata(conn, "table_count", tableCount.ToString());
                    SetMetadata(conn, "column_count", columnCount.ToString());
                    SetMetadata(conn, "key_count", keyCount.ToString());
                    SetMetadata(conn, "relationship_count", relationCount.ToString());

                    // Build FTS index
                    RebuildFtsIndex(conn);

                    tx.Commit();

                    return string.Format(
                        "Schema ingested successfully from \"{0}\":\n" +
                        "  Dictionary: {1}\n" +
                        "  Tables: {2}\n" +
                        "  Columns: {3}\n" +
                        "  Keys: {4}\n" +
                        "  Relationships: {5}\n" +
                        "  Database: {6}",
                        Path.GetFileName(dctxPath), dictName, tableCount, columnCount, keyCount, relationCount, _dbPath);
                }
                catch (Exception ex)
                {
                    tx.Rollback();
                    return "Error during ingestion: " + ex.Message;
                }
            }
        }

        /// <summary>
        /// Ingest schema from a SQL Server database via connection string.
        /// If merge=true, adds SQL-only objects alongside existing dctx data.
        /// If merge=false, clears all existing data first.
        /// </summary>
        public string IngestSqlDatabase(string connectionString, bool merge = true)
        {
            if (string.IsNullOrEmpty(connectionString))
                return "Error: connection string is required";

            // Test connection first
            SqlConnection sqlConn;
            try
            {
                sqlConn = new SqlConnection(connectionString);
                sqlConn.Open();
            }
            catch (Exception ex)
            {
                return "Error: Failed to connect to SQL Server: " + ex.Message;
            }

            string dbName;
            using (sqlConn)
            {
                dbName = sqlConn.Database;

                using (var liteConn = OpenConnection(readOnly: false))
                using (var tx = liteConn.BeginTransaction())
                {
                    try
                    {
                        if (!merge)
                            ClearAllData(liteConn);
                        else
                            ClearSqlData(liteConn); // Only clear previous SQL-sourced data

                        int tableCount = 0, columnCount = 0, keyCount = 0, relCount = 0;
                        int procCount = 0, viewCount = 0;

                        // Map SQL table names to SQLite IDs for relationship resolution
                        var tableNameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                        // Also build a map of existing dctx tables for merge matching
                        var existingTables = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        if (merge)
                        {
                            using (var cmd = new SQLiteCommand("SELECT id, name FROM tables WHERE source = 'dctx'", liteConn))
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                    existingTables[reader.GetString(1)] = reader.GetInt64(0);
                            }
                        }

                        // ── Tables ──
                        using (var cmd = new SqlCommand(@"
                            SELECT t.TABLE_SCHEMA, t.TABLE_NAME,
                                   CAST(ISNULL(ep.value, '') AS NVARCHAR(MAX)) as description
                            FROM INFORMATION_SCHEMA.TABLES t
                            LEFT JOIN sys.extended_properties ep
                                ON ep.major_id = OBJECT_ID(t.TABLE_SCHEMA + '.' + t.TABLE_NAME)
                                AND ep.minor_id = 0 AND ep.name = 'MS_Description'
                            WHERE t.TABLE_TYPE = 'BASE TABLE'
                            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME", sqlConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string schema = reader.GetString(0);
                                string tName = reader.GetString(1);
                                string desc = reader.IsDBNull(2) ? "" : reader.GetString(2);

                                // Skip if this table already exists from dctx (merge mode)
                                if (merge && existingTables.ContainsKey(tName))
                                {
                                    tableNameToId[tName] = existingTables[tName];
                                    continue;
                                }

                                long tableId = InsertTable(liteConn, null, tName, null, "MSSQL", desc,
                                    dbName, "sql", schema);
                                tableNameToId[tName] = tableId;
                                tableCount++;
                            }
                        }

                        // ── Columns ──
                        using (var cmd = new SqlCommand(@"
                            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE,
                                   c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE,
                                   c.IS_NULLABLE, c.COLUMN_DEFAULT, c.ORDINAL_POSITION,
                                   CAST(ISNULL(ep.value, '') AS NVARCHAR(MAX)) as description
                            FROM INFORMATION_SCHEMA.COLUMNS c
                            LEFT JOIN sys.columns sc ON sc.name = c.COLUMN_NAME
                                AND sc.object_id = OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME)
                            LEFT JOIN sys.extended_properties ep
                                ON ep.major_id = sc.object_id AND ep.minor_id = sc.column_id
                                AND ep.name = 'MS_Description'
                            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION", sqlConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string tName = reader.GetString(1);
                                long tableId;
                                if (!tableNameToId.TryGetValue(tName, out tableId)) continue;

                                // Skip columns for dctx tables that already have their columns
                                if (merge && existingTables.ContainsKey(tName)) continue;

                                string colName = reader.GetString(2);
                                string dataType = reader.GetString(3);
                                int size = reader.IsDBNull(4) ? (reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader[5])) : Convert.ToInt32(reader[4]);
                                int places = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader[6]);
                                bool nullable = reader.GetString(7) == "YES";
                                string defaultVal = reader.IsDBNull(8) ? "" : reader.GetString(8);
                                int ordinal = Convert.ToInt32(reader[9]);
                                string desc = reader.IsDBNull(10) ? "" : reader.GetString(10);

                                InsertColumn(liteConn, null, tableId, colName, dataType, size, places,
                                    null, defaultVal, desc, ordinal);
                                columnCount++;
                            }
                        }

                        // ── Primary Keys & Unique Constraints ──
                        using (var cmd = new SqlCommand(@"
                            SELECT tc.TABLE_NAME, tc.CONSTRAINT_NAME, tc.CONSTRAINT_TYPE,
                                   kcu.COLUMN_NAME, kcu.ORDINAL_POSITION
                            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                                ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
                                AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
                            WHERE tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')
                            ORDER BY tc.TABLE_NAME, tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION", sqlConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            string lastKey = null;
                            long currentKeyId = 0;

                            while (reader.Read())
                            {
                                string tName = reader.GetString(0);
                                string kName = reader.GetString(1);
                                string kType = reader.GetString(2);
                                string colName = reader.GetString(3);
                                int ordinal = Convert.ToInt32(reader[4]);

                                long tableId;
                                if (!tableNameToId.TryGetValue(tName, out tableId)) continue;
                                if (merge && existingTables.ContainsKey(tName)) continue;

                                string keyIdent = tName + "." + kName;
                                if (keyIdent != lastKey)
                                {
                                    bool isPrimary = kType == "PRIMARY KEY";
                                    currentKeyId = InsertKey(liteConn, null, tableId, kName, isPrimary, true, false, false);
                                    lastKey = keyIdent;
                                    keyCount++;
                                }

                                // Find column ID
                                long colId = FindColumnId(liteConn, tableId, colName);
                                InsertKeyColumn(liteConn, currentKeyId, colId > 0 ? colId : (long?)null, ordinal, true);
                            }
                        }

                        // ── Indexes (non-PK, non-unique-constraint) ──
                        using (var cmd = new SqlCommand(@"
                            SELECT OBJECT_NAME(i.object_id) as table_name,
                                   i.name as index_name, i.is_unique,
                                   COL_NAME(ic.object_id, ic.column_id) as column_name,
                                   ic.key_ordinal, ic.is_descending_key
                            FROM sys.indexes i
                            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                            WHERE i.is_primary_key = 0 AND i.is_unique_constraint = 0
                                AND i.type > 0 AND ic.is_included_column = 0
                                AND OBJECTPROPERTY(i.object_id, 'IsUserTable') = 1
                            ORDER BY OBJECT_NAME(i.object_id), i.name, ic.key_ordinal", sqlConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            string lastIdx = null;
                            long currentKeyId = 0;

                            while (reader.Read())
                            {
                                string tName = reader.GetString(0);
                                string iName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                                bool isUnique = reader.GetBoolean(2);
                                string colName = reader.IsDBNull(3) ? "" : reader.GetString(3);
                                int ordinal = Convert.ToInt32(reader[4]);
                                bool isDesc = reader.GetBoolean(5);

                                long tableId;
                                if (!tableNameToId.TryGetValue(tName, out tableId)) continue;
                                if (merge && existingTables.ContainsKey(tName)) continue;

                                string idxIdent = tName + "." + iName;
                                if (idxIdent != lastIdx)
                                {
                                    currentKeyId = InsertKey(liteConn, null, tableId, iName, false, isUnique, false, false);
                                    lastIdx = idxIdent;
                                    keyCount++;
                                }

                                long colId = FindColumnId(liteConn, tableId, colName);
                                InsertKeyColumn(liteConn, currentKeyId, colId > 0 ? colId : (long?)null, ordinal, !isDesc);
                            }
                        }

                        // ── Foreign Keys (Relationships) ──
                        using (var cmd = new SqlCommand(@"
                            SELECT
                                fk.name as fk_name,
                                tp.name as parent_table,
                                tr.name as child_table,
                                COL_NAME(fkc.parent_object_id, fkc.parent_column_id) as parent_column,
                                COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) as child_column
                            FROM sys.foreign_keys fk
                            JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
                            JOIN sys.tables tp ON fkc.parent_object_id = tp.object_id
                            JOIN sys.tables tr ON fkc.referenced_object_id = tr.object_id
                            ORDER BY fk.name, fkc.constraint_column_id", sqlConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            string lastFk = null;
                            long currentRelId = 0;

                            while (reader.Read())
                            {
                                string fkName = reader.GetString(0);
                                string parentTable = reader.GetString(1);
                                string childTable = reader.GetString(2);

                                long parentId, childId;
                                // Note: in SQL Server FK terminology, "parent" has the FK column,
                                // "referenced" is the target. We map: parent_table = referenced (PK side),
                                // child_table = parent (FK side) to match Clarion's convention.
                                if (!tableNameToId.TryGetValue(childTable, out parentId)) continue;
                                if (!tableNameToId.TryGetValue(parentTable, out childId)) continue;

                                if (fkName != lastFk)
                                {
                                    currentRelId = InsertRelationship(liteConn, null, parentId, childId, null, fkName);
                                    lastFk = fkName;
                                    relCount++;
                                }
                            }
                        }

                        // ── Stored Procedures & Functions ──
                        using (var cmd = new SqlCommand(@"
                            SELECT r.ROUTINE_SCHEMA, r.ROUTINE_NAME, r.ROUTINE_TYPE,
                                   r.DATA_TYPE,
                                   OBJECT_DEFINITION(OBJECT_ID(r.ROUTINE_SCHEMA + '.' + r.ROUTINE_NAME)) as definition
                            FROM INFORMATION_SCHEMA.ROUTINES r
                            ORDER BY r.ROUTINE_SCHEMA, r.ROUTINE_NAME", sqlConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string schema = reader.GetString(0);
                                string pName = reader.GetString(1);
                                string pType = reader.GetString(2).ToLower(); // PROCEDURE or FUNCTION
                                string returnType = reader.IsDBNull(3) ? null : reader.GetString(3);
                                string definition = reader.IsDBNull(4) ? null : reader.GetString(4);

                                long procId = InsertProcedure(liteConn, pName, pType, returnType, definition, null, schema);
                                procCount++;
                            }
                        }

                        // ── Procedure/Function Parameters ──
                        using (var cmd = new SqlCommand(@"
                            SELECT p.SPECIFIC_SCHEMA, p.SPECIFIC_NAME, p.PARAMETER_NAME,
                                   p.DATA_TYPE, p.CHARACTER_MAXIMUM_LENGTH,
                                   p.PARAMETER_MODE, p.ORDINAL_POSITION
                            FROM INFORMATION_SCHEMA.PARAMETERS p
                            ORDER BY p.SPECIFIC_SCHEMA, p.SPECIFIC_NAME, p.ORDINAL_POSITION", sqlConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string pName = reader.GetString(1);
                                string paramName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                                string dataType = reader.IsDBNull(3) ? "" : reader.GetString(3);
                                int maxLen = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader[4]);
                                string mode = reader.IsDBNull(5) ? "IN" : reader.GetString(5);
                                int ordinal = Convert.ToInt32(reader[6]);

                                // Find procedure ID
                                long procId = FindProcedureId(liteConn, pName);
                                if (procId > 0)
                                    InsertProcedureParam(liteConn, procId, paramName, dataType, maxLen, mode, ordinal);
                            }
                        }

                        // ── Views ──
                        using (var cmd = new SqlCommand(@"
                            SELECT v.TABLE_SCHEMA, v.TABLE_NAME, v.VIEW_DEFINITION
                            FROM INFORMATION_SCHEMA.VIEWS v
                            ORDER BY v.TABLE_SCHEMA, v.TABLE_NAME", sqlConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string schema = reader.GetString(0);
                                string vName = reader.GetString(1);
                                string definition = reader.IsDBNull(2) ? null : reader.GetString(2);

                                long viewId = InsertView(liteConn, vName, definition, null, schema);
                                viewCount++;

                                // Link to referenced tables
                                if (!string.IsNullOrEmpty(definition))
                                {
                                    foreach (var kvp in tableNameToId)
                                    {
                                        if (definition.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                                            InsertViewReference(liteConn, viewId, kvp.Value);
                                    }
                                }
                            }
                        }

                        // Metadata
                        SetMetadata(liteConn, "sql_connection", dbName);
                        SetMetadata(liteConn, "sql_ingested_at", DateTime.Now.ToString("o"));
                        SetMetadata(liteConn, "sql_table_count", tableCount.ToString());
                        SetMetadata(liteConn, "sql_column_count", columnCount.ToString());
                        SetMetadata(liteConn, "sql_key_count", keyCount.ToString());
                        SetMetadata(liteConn, "sql_relationship_count", relCount.ToString());
                        SetMetadata(liteConn, "sql_procedure_count", procCount.ToString());
                        SetMetadata(liteConn, "sql_view_count", viewCount.ToString());

                        // Rebuild FTS
                        RebuildFtsIndex(liteConn);

                        tx.Commit();

                        var sb = new StringBuilder();
                        sb.AppendLine(string.Format("SQL Server schema ingested successfully from \"{0}\":", dbName));
                        sb.AppendLine(string.Format("  Tables: {0}{1}", tableCount,
                            merge ? " (SQL-only, " + existingTables.Count + " dctx tables preserved)" : ""));
                        sb.AppendLine("  Columns: " + columnCount);
                        sb.AppendLine("  Keys/Indexes: " + keyCount);
                        sb.AppendLine("  Relationships: " + relCount);
                        sb.AppendLine("  Procedures/Functions: " + procCount);
                        sb.AppendLine("  Views: " + viewCount);
                        sb.AppendLine("  Database: " + _dbPath);
                        return sb.ToString();
                    }
                    catch (Exception ex)
                    {
                        tx.Rollback();
                        return "Error during SQL ingestion: " + ex.Message;
                    }
                }
            }
        }

        /// <summary>
        /// Ingest schema from a SQLite database file.
        /// </summary>
        public string IngestSqliteDatabase(string sqlitePath, bool merge = true)
        {
            if (string.IsNullOrEmpty(sqlitePath))
                return "Error: SQLite file path is required";
            if (!File.Exists(sqlitePath))
                return "Error: SQLite file not found: " + sqlitePath;

            using (var srcConn = new SQLiteConnection("Data Source=" + sqlitePath + ";Version=3;Read Only=True;"))
            {
                srcConn.Open();

                using (var liteConn = OpenConnection(readOnly: false))
                using (var tx = liteConn.BeginTransaction())
                {
                    try
                    {
                        if (!merge)
                            ClearAllData(liteConn);
                        else
                            ClearSqlData(liteConn);

                        int tableCount = 0, columnCount = 0, keyCount = 0, relCount = 0, viewCount = 0;
                        var tableNameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                        // ── Tables ──
                        using (var cmd = new SQLiteCommand(
                            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", srcConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string tName = reader.GetString(0);
                                long tableId = InsertTable(liteConn, null, tName, null, "SQLite", null,
                                    sqlitePath, "sql", null);
                                tableNameToId[tName] = tableId;
                                tableCount++;
                            }
                        }

                        // ── Columns + Primary Keys (via PRAGMA) ──
                        foreach (var kvp in tableNameToId)
                        {
                            string escapedName = kvp.Key.Replace("\"", "\"\"");
                            var pkCols = new List<string>();

                            using (var cmd = new SQLiteCommand("PRAGMA table_info(\"" + escapedName + "\")", srcConn))
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    int cid = reader.GetInt32(0);
                                    string colName = reader.GetString(1);
                                    string dataType = reader.IsDBNull(2) ? "" : reader.GetString(2);
                                    string defaultVal = reader.IsDBNull(4) ? "" : reader.GetValue(4).ToString();
                                    int pkOrdinal = reader.GetInt32(5);

                                    InsertColumn(liteConn, null, kvp.Value, colName, dataType, 0, 0,
                                        null, defaultVal, null, cid);
                                    columnCount++;

                                    if (pkOrdinal > 0)
                                        pkCols.Add(colName);
                                }
                            }

                            if (pkCols.Count > 0)
                            {
                                long keyId = InsertKey(liteConn, null, kvp.Value, "pk_" + kvp.Key, true, true, false, false);
                                for (int i = 0; i < pkCols.Count; i++)
                                {
                                    long colId = FindColumnId(liteConn, kvp.Value, pkCols[i]);
                                    InsertKeyColumn(liteConn, keyId, colId > 0 ? colId : (long?)null, i + 1, true);
                                }
                                keyCount++;
                            }

                            // ── Indexes ──
                            using (var cmd = new SQLiteCommand("PRAGMA index_list(\"" + escapedName + "\")", srcConn))
                            using (var reader = cmd.ExecuteReader())
                            {
                                var indexes = new List<Tuple<string, bool, string>>();
                                while (reader.Read())
                                    indexes.Add(Tuple.Create(reader.GetString(1), reader.GetInt32(2) != 0, reader.GetString(3)));

                                foreach (var idx in indexes)
                                {
                                    if (idx.Item3 == "pk") continue;
                                    long keyId = InsertKey(liteConn, null, kvp.Value, idx.Item1, false, idx.Item2, false, false);
                                    keyCount++;

                                    using (var idxCmd = new SQLiteCommand("PRAGMA index_info(\"" + idx.Item1.Replace("\"", "\"\"") + "\")", srcConn))
                                    using (var idxReader = idxCmd.ExecuteReader())
                                    {
                                        while (idxReader.Read())
                                        {
                                            int seqno = idxReader.GetInt32(0);
                                            string colName = idxReader.IsDBNull(2) ? "" : idxReader.GetString(2);
                                            long colId = FindColumnId(liteConn, kvp.Value, colName);
                                            InsertKeyColumn(liteConn, keyId, colId > 0 ? colId : (long?)null, seqno, true);
                                        }
                                    }
                                }
                            }

                            // ── Foreign Keys ──
                            using (var cmd = new SQLiteCommand("PRAGMA foreign_key_list(\"" + escapedName + "\")", srcConn))
                            using (var reader = cmd.ExecuteReader())
                            {
                                int lastFkId = -1;
                                while (reader.Read())
                                {
                                    int fkId = reader.GetInt32(0);
                                    string refTable = reader.GetString(2);

                                    if (fkId != lastFkId)
                                    {
                                        long parentId;
                                        if (tableNameToId.TryGetValue(refTable, out parentId))
                                        {
                                            InsertRelationship(liteConn, null, parentId, kvp.Value, null,
                                                "FK_" + kvp.Key + "_" + refTable);
                                            relCount++;
                                        }
                                        lastFkId = fkId;
                                    }
                                }
                            }
                        }

                        // ── Views ──
                        using (var cmd = new SQLiteCommand(
                            "SELECT name, sql FROM sqlite_master WHERE type='view' ORDER BY name", srcConn))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string vName = reader.GetString(0);
                                string definition = reader.IsDBNull(1) ? null : reader.GetString(1);
                                long viewId = InsertView(liteConn, vName, definition, null, null);
                                viewCount++;

                                if (!string.IsNullOrEmpty(definition))
                                {
                                    foreach (var tkvp in tableNameToId)
                                    {
                                        if (definition.IndexOf(tkvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                                            InsertViewReference(liteConn, viewId, tkvp.Value);
                                    }
                                }
                            }
                        }

                        SetMetadata(liteConn, "sqlite_source", sqlitePath);
                        SetMetadata(liteConn, "sqlite_ingested_at", DateTime.Now.ToString("o"));
                        SetMetadata(liteConn, "sqlite_table_count", tableCount.ToString());

                        RebuildFtsIndex(liteConn);
                        tx.Commit();

                        var sb = new StringBuilder();
                        sb.AppendLine(string.Format("SQLite schema ingested from \"{0}\":", Path.GetFileName(sqlitePath)));
                        sb.AppendLine("  Tables: " + tableCount);
                        sb.AppendLine("  Columns: " + columnCount);
                        sb.AppendLine("  Keys/Indexes: " + keyCount);
                        sb.AppendLine("  Relationships: " + relCount);
                        sb.AppendLine("  Views: " + viewCount);
                        sb.AppendLine("  Database: " + _dbPath);
                        return sb.ToString();
                    }
                    catch (Exception ex)
                    {
                        tx.Rollback();
                        return "Error during SQLite ingestion: " + ex.Message;
                    }
                }
            }
        }

        /// <summary>
        /// Ingest schema from a PostgreSQL database using Npgsql (loaded dynamically).
        /// Requires Npgsql.dll in the lib folder.
        /// </summary>
        public string IngestPostgresDatabase(string connectionString, bool merge = true)
        {
            if (string.IsNullOrEmpty(connectionString))
                return "Error: connection string is required";

            // Load Npgsql dynamically to avoid hard dependency
            System.Reflection.Assembly npgsqlAsm;
            try
            {
                npgsqlAsm = System.Reflection.Assembly.Load("Npgsql");
            }
            catch
            {
                return "Error: Npgsql.dll not found. Place Npgsql.dll in the ClarionAssistant folder to enable PostgreSQL support.";
            }

            Type connType = npgsqlAsm.GetType("Npgsql.NpgsqlConnection");
            System.Data.Common.DbConnection pgConn;
            string dbName;
            try
            {
                pgConn = (System.Data.Common.DbConnection)Activator.CreateInstance(connType, connectionString);
                pgConn.Open();
                dbName = pgConn.Database;
            }
            catch (Exception ex)
            {
                return "Error: Failed to connect to PostgreSQL: " + ex.Message;
            }

            using (pgConn)
            {
                using (var liteConn = OpenConnection(readOnly: false))
                using (var tx = liteConn.BeginTransaction())
                {
                    try
                    {
                        if (!merge)
                            ClearAllData(liteConn);
                        else
                            ClearSqlData(liteConn);

                        int tableCount = 0, columnCount = 0, keyCount = 0, relCount = 0;
                        int procCount = 0, viewCount = 0;
                        var tableNameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                        // ── Tables ──
                        using (var cmd = pgConn.CreateCommand())
                        {
                            cmd.CommandText = @"SELECT table_schema, table_name
                                FROM information_schema.tables
                                WHERE table_schema NOT IN ('pg_catalog','information_schema')
                                AND table_type = 'BASE TABLE'
                                ORDER BY table_schema, table_name";
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string schema = reader.GetString(0);
                                    string tName = reader.GetString(1);
                                    long tableId = InsertTable(liteConn, null, tName, null, "PostgreSQL", null,
                                        dbName, "sql", schema);
                                    tableNameToId[schema + "." + tName] = tableId;
                                    tableCount++;
                                }
                            }
                        }

                        // ── Columns ──
                        using (var cmd = pgConn.CreateCommand())
                        {
                            cmd.CommandText = @"SELECT table_schema, table_name, column_name, data_type,
                                    character_maximum_length, numeric_precision, numeric_scale,
                                    is_nullable, column_default, ordinal_position
                                FROM information_schema.columns
                                WHERE table_schema NOT IN ('pg_catalog','information_schema')
                                ORDER BY table_schema, table_name, ordinal_position";
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string cSchema = reader.GetString(0);
                                    string tName = reader.GetString(1);
                                    long tableId;
                                    if (!tableNameToId.TryGetValue(cSchema + "." + tName, out tableId)) continue;

                                    string colName = reader.GetString(2);
                                    string dataType = reader.GetString(3);
                                    int size = reader.IsDBNull(4) ? (reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader[5])) : Convert.ToInt32(reader[4]);
                                    int places = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader[6]);
                                    string defaultVal = reader.IsDBNull(8) ? "" : reader.GetValue(8).ToString();
                                    int ordinal = Convert.ToInt32(reader[9]);

                                    InsertColumn(liteConn, null, tableId, colName, dataType, size, places,
                                        null, defaultVal, null, ordinal);
                                    columnCount++;
                                }
                            }
                        }

                        // ── Primary Keys & Unique Constraints ──
                        using (var cmd = pgConn.CreateCommand())
                        {
                            cmd.CommandText = @"SELECT tc.table_schema, tc.table_name, tc.constraint_name, tc.constraint_type,
                                    kcu.column_name, kcu.ordinal_position
                                FROM information_schema.table_constraints tc
                                JOIN information_schema.key_column_usage kcu
                                    ON tc.constraint_name = kcu.constraint_name
                                    AND tc.table_schema = kcu.table_schema
                                WHERE tc.constraint_type IN ('PRIMARY KEY','UNIQUE')
                                AND tc.table_schema NOT IN ('pg_catalog','information_schema')
                                ORDER BY tc.table_schema, tc.table_name, tc.constraint_name, kcu.ordinal_position";
                            using (var reader = cmd.ExecuteReader())
                            {
                                string lastKey = null;
                                long currentKeyId = 0;

                                while (reader.Read())
                                {
                                    string kSchema = reader.GetString(0);
                                    string tName = reader.GetString(1);
                                    string kName = reader.GetString(2);
                                    string kType = reader.GetString(3);
                                    string colName = reader.GetString(4);
                                    int ordinal = Convert.ToInt32(reader[5]);

                                    long tableId;
                                    if (!tableNameToId.TryGetValue(kSchema + "." + tName, out tableId)) continue;

                                    string keyIdent = kSchema + "." + tName + "." + kName;
                                    if (keyIdent != lastKey)
                                    {
                                        bool isPrimary = kType == "PRIMARY KEY";
                                        currentKeyId = InsertKey(liteConn, null, tableId, kName, isPrimary, true, false, false);
                                        lastKey = keyIdent;
                                        keyCount++;
                                    }

                                    long colId = FindColumnId(liteConn, tableId, colName);
                                    InsertKeyColumn(liteConn, currentKeyId, colId > 0 ? colId : (long?)null, ordinal, true);
                                }
                            }
                        }

                        // ── Foreign Keys ──
                        using (var cmd = pgConn.CreateCommand())
                        {
                            cmd.CommandText = @"SELECT tc.constraint_name,
                                    ccu.table_schema AS parent_schema,
                                    ccu.table_name AS parent_table,
                                    tc.table_schema AS child_schema,
                                    tc.table_name AS child_table
                                FROM information_schema.table_constraints tc
                                JOIN information_schema.constraint_column_usage ccu
                                    ON tc.constraint_name = ccu.constraint_name
                                    AND tc.table_schema = ccu.table_schema
                                WHERE tc.constraint_type = 'FOREIGN KEY'
                                AND tc.table_schema NOT IN ('pg_catalog','information_schema')
                                ORDER BY tc.constraint_name";
                            using (var reader = cmd.ExecuteReader())
                            {
                                string lastFk = null;
                                while (reader.Read())
                                {
                                    string fkName = reader.GetString(0);
                                    string parentSchema = reader.GetString(1);
                                    string parentTable = reader.GetString(2);
                                    string childSchema = reader.GetString(3);
                                    string childTable = reader.GetString(4);

                                    if (fkName != lastFk)
                                    {
                                        long parentId, childId;
                                        if (tableNameToId.TryGetValue(parentSchema + "." + parentTable, out parentId) &&
                                            tableNameToId.TryGetValue(childSchema + "." + childTable, out childId))
                                        {
                                            InsertRelationship(liteConn, null, parentId, childId, null, fkName);
                                            relCount++;
                                        }
                                        lastFk = fkName;
                                    }
                                }
                            }
                        }

                        // ── Functions & Stored Procedures ──
                        using (var cmd = pgConn.CreateCommand())
                        {
                            cmd.CommandText = @"SELECT n.nspname, p.proname,
                                    CASE p.prokind WHEN 'f' THEN 'function' WHEN 'p' THEN 'procedure' ELSE 'function' END,
                                    pg_get_function_result(p.oid) as return_type,
                                    pg_get_functiondef(p.oid) as definition
                                FROM pg_proc p
                                JOIN pg_namespace n ON p.pronamespace = n.oid
                                WHERE n.nspname NOT IN ('pg_catalog','information_schema')
                                ORDER BY n.nspname, p.proname";
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string schema = reader.GetString(0);
                                    string pName = reader.GetString(1);
                                    string pType = reader.GetString(2);
                                    string returnType = reader.IsDBNull(3) ? null : reader.GetString(3);
                                    string definition = reader.IsDBNull(4) ? null : reader.GetString(4);

                                    InsertProcedure(liteConn, pName, pType, returnType, definition, null, schema);
                                    procCount++;
                                }
                            }
                        }

                        // ── Views ──
                        using (var cmd = pgConn.CreateCommand())
                        {
                            cmd.CommandText = @"SELECT table_schema, table_name, view_definition
                                FROM information_schema.views
                                WHERE table_schema NOT IN ('pg_catalog','information_schema')
                                ORDER BY table_schema, table_name";
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string schema = reader.GetString(0);
                                    string vName = reader.GetString(1);
                                    string definition = reader.IsDBNull(2) ? null : reader.GetString(2);

                                    long viewId = InsertView(liteConn, vName, definition, null, schema);
                                    viewCount++;

                                    if (!string.IsNullOrEmpty(definition))
                                    {
                                        foreach (var tkvp in tableNameToId)
                                        {
                                            if (definition.IndexOf(tkvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                                                InsertViewReference(liteConn, viewId, tkvp.Value);
                                        }
                                    }
                                }
                            }
                        }

                        SetMetadata(liteConn, "pg_connection", dbName);
                        SetMetadata(liteConn, "pg_ingested_at", DateTime.Now.ToString("o"));
                        SetMetadata(liteConn, "pg_table_count", tableCount.ToString());
                        SetMetadata(liteConn, "pg_column_count", columnCount.ToString());
                        SetMetadata(liteConn, "pg_procedure_count", procCount.ToString());
                        SetMetadata(liteConn, "pg_view_count", viewCount.ToString());

                        RebuildFtsIndex(liteConn);
                        tx.Commit();

                        var sb = new StringBuilder();
                        sb.AppendLine(string.Format("PostgreSQL schema ingested from \"{0}\":", dbName));
                        sb.AppendLine("  Tables: " + tableCount);
                        sb.AppendLine("  Columns: " + columnCount);
                        sb.AppendLine("  Keys/Indexes: " + keyCount);
                        sb.AppendLine("  Relationships: " + relCount);
                        sb.AppendLine("  Procedures/Functions: " + procCount);
                        sb.AppendLine("  Views: " + viewCount);
                        sb.AppendLine("  Database: " + _dbPath);
                        return sb.ToString();
                    }
                    catch (Exception ex)
                    {
                        tx.Rollback();
                        return "Error during PostgreSQL ingestion: " + ex.Message;
                    }
                }
            }
        }

        private void ClearSqlData(SQLiteConnection conn)
        {
            // Remove only SQL-sourced data, preserve dctx data
            using (var cmd = new SQLiteCommand(@"
                DELETE FROM key_columns WHERE key_id IN (SELECT id FROM keys WHERE table_id IN (SELECT id FROM tables WHERE source = 'sql'));
                DELETE FROM keys WHERE table_id IN (SELECT id FROM tables WHERE source = 'sql');
                DELETE FROM columns WHERE table_id IN (SELECT id FROM tables WHERE source = 'sql');
                DELETE FROM relationship_mappings WHERE relationship_id IN (SELECT id FROM relationships WHERE guid IS NULL OR guid = '');
                DELETE FROM relationships WHERE guid IS NULL OR guid = '';
                DELETE FROM tables WHERE source = 'sql';
                DELETE FROM procedure_params;
                DELETE FROM procedures;
                DELETE FROM view_references;
                DELETE FROM views;
                DELETE FROM schema_fts;", conn))
                cmd.ExecuteNonQuery();
        }

        private void ClearAllData(SQLiteConnection conn)
        {
            string[] tables = { "relationship_mappings", "relationships", "key_columns", "keys", "columns", "tables",
                                "procedures", "procedure_params", "views", "view_references",
                                "schema_fts", "schema_metadata" };
            foreach (var t in tables)
            {
                using (var cmd = new SQLiteCommand("DELETE FROM " + t, conn))
                    cmd.ExecuteNonQuery();
            }
        }

        private void RebuildFtsIndex(SQLiteConnection conn)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM schema_fts", conn))
                cmd.ExecuteNonQuery();

            // Index tables
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO schema_fts(entity_type, entity_id, name, description, table_name)
                  SELECT 'table', id, name, COALESCE(description,''), name FROM tables", conn))
                cmd.ExecuteNonQuery();

            // Index columns with their table name for context
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO schema_fts(entity_type, entity_id, name, description, table_name)
                  SELECT 'column', c.id, c.name, COALESCE(c.description,''), t.name
                  FROM columns c JOIN tables t ON c.table_id = t.id", conn))
                cmd.ExecuteNonQuery();

            // Index procedures/functions
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO schema_fts(entity_type, entity_id, name, description, table_name)
                  SELECT type, id, name, COALESCE(description,''), '' FROM procedures", conn))
                cmd.ExecuteNonQuery();

            // Index views
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO schema_fts(entity_type, entity_id, name, description, table_name)
                  SELECT 'view', id, name, COALESCE(description,''), '' FROM views", conn))
                cmd.ExecuteNonQuery();
        }

        #endregion

        #region Insert Helpers

        private long InsertTable(SQLiteConnection conn, string guid, string name, string prefix,
            string driver, string description, string sourceFile, string source = "dctx", string schemaName = null)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO tables (guid, name, prefix, driver, description, source_file, source, schema_name)
                  VALUES (@guid, @name, @prefix, @driver, @desc, @src, @source, @schema)", conn))
            {
                cmd.Parameters.AddWithValue("@guid", guid ?? "");
                cmd.Parameters.AddWithValue("@name", name ?? "");
                cmd.Parameters.AddWithValue("@prefix", prefix ?? "");
                cmd.Parameters.AddWithValue("@driver", driver ?? "");
                cmd.Parameters.AddWithValue("@desc", description ?? "");
                cmd.Parameters.AddWithValue("@src", sourceFile ?? "");
                cmd.Parameters.AddWithValue("@source", source ?? "dctx");
                cmd.Parameters.AddWithValue("@schema", schemaName ?? "");
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        private long InsertColumn(SQLiteConnection conn, string guid, long tableId, string name,
            string dataType, int size, int places, string picture, string defaultValue,
            string description, int ordinal)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO columns (guid, table_id, name, data_type, size, places, picture, default_value, description, ordinal)
                  VALUES (@guid, @tid, @name, @dtype, @size, @places, @pic, @def, @desc, @ord)", conn))
            {
                cmd.Parameters.AddWithValue("@guid", guid ?? "");
                cmd.Parameters.AddWithValue("@tid", tableId);
                cmd.Parameters.AddWithValue("@name", name ?? "");
                cmd.Parameters.AddWithValue("@dtype", dataType ?? "");
                cmd.Parameters.AddWithValue("@size", size);
                cmd.Parameters.AddWithValue("@places", places);
                cmd.Parameters.AddWithValue("@pic", picture ?? "");
                cmd.Parameters.AddWithValue("@def", defaultValue ?? "");
                cmd.Parameters.AddWithValue("@desc", description ?? "");
                cmd.Parameters.AddWithValue("@ord", ordinal);
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        private long InsertKey(SQLiteConnection conn, string guid, long tableId, string name,
            bool isPrimary, bool isUnique, bool isAutoInc, bool isExclude)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO keys (guid, table_id, name, is_primary, is_unique, is_auto_increment, is_exclude)
                  VALUES (@guid, @tid, @name, @pri, @uniq, @auto, @excl)", conn))
            {
                cmd.Parameters.AddWithValue("@guid", guid ?? "");
                cmd.Parameters.AddWithValue("@tid", tableId);
                cmd.Parameters.AddWithValue("@name", name ?? "");
                cmd.Parameters.AddWithValue("@pri", isPrimary ? 1 : 0);
                cmd.Parameters.AddWithValue("@uniq", isUnique ? 1 : 0);
                cmd.Parameters.AddWithValue("@auto", isAutoInc ? 1 : 0);
                cmd.Parameters.AddWithValue("@excl", isExclude ? 1 : 0);
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        private void InsertKeyColumn(SQLiteConnection conn, long keyId, long? columnId, int ordinal, bool ascending)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO key_columns (key_id, column_id, ordinal, ascending)
                  VALUES (@kid, @cid, @ord, @asc)", conn))
            {
                cmd.Parameters.AddWithValue("@kid", keyId);
                cmd.Parameters.AddWithValue("@cid", columnId.HasValue ? (object)columnId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@ord", ordinal);
                cmd.Parameters.AddWithValue("@asc", ascending ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        private long InsertRelationship(SQLiteConnection conn, string guid, long? parentTableId,
            long? childTableId, long? childKeyId, string description)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO relationships (guid, parent_table_id, child_table_id, child_key_id, description)
                  VALUES (@guid, @pid, @cid, @kid, @desc)", conn))
            {
                cmd.Parameters.AddWithValue("@guid", guid ?? "");
                cmd.Parameters.AddWithValue("@pid", parentTableId.HasValue ? (object)parentTableId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@cid", childTableId.HasValue ? (object)childTableId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@kid", childKeyId.HasValue ? (object)childKeyId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@desc", description ?? "");
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        private void InsertRelationshipMapping(SQLiteConnection conn, long relationshipId, string fieldGuid)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO relationship_mappings (relationship_id, field_guid)
                  VALUES (@rid, @fguid)", conn))
            {
                cmd.Parameters.AddWithValue("@rid", relationshipId);
                cmd.Parameters.AddWithValue("@fguid", fieldGuid ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        private void SetMetadata(SQLiteConnection conn, string key, string value)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT OR REPLACE INTO schema_metadata (key, value) VALUES (@k, @v)", conn))
            {
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", value ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        private long InsertProcedure(SQLiteConnection conn, string name, string type,
            string returnType, string definition, string description, string schemaName)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO procedures (name, type, return_type, definition, description, source, schema_name)
                  VALUES (@name, @type, @ret, @def, @desc, 'sql', @schema)", conn))
            {
                cmd.Parameters.AddWithValue("@name", name ?? "");
                cmd.Parameters.AddWithValue("@type", type ?? "procedure");
                cmd.Parameters.AddWithValue("@ret", returnType ?? "");
                cmd.Parameters.AddWithValue("@def", definition ?? "");
                cmd.Parameters.AddWithValue("@desc", description ?? "");
                cmd.Parameters.AddWithValue("@schema", schemaName ?? "");
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        private void InsertProcedureParam(SQLiteConnection conn, long procedureId, string name,
            string dataType, int maxLength, string direction, int ordinal)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO procedure_params (procedure_id, name, data_type, max_length, direction, ordinal)
                  VALUES (@pid, @name, @dtype, @maxlen, @dir, @ord)", conn))
            {
                cmd.Parameters.AddWithValue("@pid", procedureId);
                cmd.Parameters.AddWithValue("@name", name ?? "");
                cmd.Parameters.AddWithValue("@dtype", dataType ?? "");
                cmd.Parameters.AddWithValue("@maxlen", maxLength);
                cmd.Parameters.AddWithValue("@dir", direction ?? "IN");
                cmd.Parameters.AddWithValue("@ord", ordinal);
                cmd.ExecuteNonQuery();
            }
        }

        private long InsertView(SQLiteConnection conn, string name, string definition,
            string description, string schemaName)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO views (name, definition, description, source, schema_name)
                  VALUES (@name, @def, @desc, 'sql', @schema)", conn))
            {
                cmd.Parameters.AddWithValue("@name", name ?? "");
                cmd.Parameters.AddWithValue("@def", definition ?? "");
                cmd.Parameters.AddWithValue("@desc", description ?? "");
                cmd.Parameters.AddWithValue("@schema", schemaName ?? "");
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        private void InsertViewReference(SQLiteConnection conn, long viewId, long tableId)
        {
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO view_references (view_id, table_id) VALUES (@vid, @tid)", conn))
            {
                cmd.Parameters.AddWithValue("@vid", viewId);
                cmd.Parameters.AddWithValue("@tid", tableId);
                cmd.ExecuteNonQuery();
            }
        }

        private long FindColumnId(SQLiteConnection conn, long tableId, string columnName)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT id FROM columns WHERE table_id = @tid AND name = @name COLLATE NOCASE", conn))
            {
                cmd.Parameters.AddWithValue("@tid", tableId);
                cmd.Parameters.AddWithValue("@name", columnName);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt64(result) : 0;
            }
        }

        private long FindProcedureId(SQLiteConnection conn, string procName)
        {
            using (var cmd = new SQLiteCommand(
                "SELECT id FROM procedures WHERE name = @name COLLATE NOCASE LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("@name", procName);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt64(result) : 0;
            }
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Search tables by name pattern using FTS5.
        /// </summary>
        public string SearchTables(string pattern, int limit = 50)
        {
            if (string.IsNullOrEmpty(pattern)) return "Error: pattern is required";
            if (!File.Exists(_dbPath)) return "Error: SchemaGraph database not found. Run ingest_schema first.";

            using (var conn = OpenConnection(readOnly: true))
            {
                // Try FTS first, fall back to LIKE
                string sql;
                bool useFts = !pattern.Contains("%") && !pattern.Contains("_");

                if (useFts)
                {
                    sql = @"SELECT t.name, t.prefix, t.driver, t.description,
                               (SELECT COUNT(*) FROM columns WHERE table_id = t.id) as col_count,
                               (SELECT COUNT(*) FROM keys WHERE table_id = t.id) as key_count
                            FROM schema_fts f
                            JOIN tables t ON f.entity_id = t.id
                            WHERE f.entity_type = 'table' AND schema_fts MATCH @pattern
                            ORDER BY rank
                            LIMIT @limit";
                }
                else
                {
                    sql = @"SELECT t.name, t.prefix, t.driver, t.description,
                               (SELECT COUNT(*) FROM columns WHERE table_id = t.id) as col_count,
                               (SELECT COUNT(*) FROM keys WHERE table_id = t.id) as key_count
                            FROM tables t
                            WHERE t.name LIKE @pattern
                            ORDER BY t.name
                            LIMIT @limit";
                }

                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@pattern", useFts ? EscapeFts(pattern) + "*" : "%" + pattern + "%");
                    cmd.Parameters.AddWithValue("@limit", limit);

                    using (var reader = cmd.ExecuteReader())
                    {
                        var sb = new StringBuilder();
                        int count = 0;
                        while (reader.Read())
                        {
                            if (count == 0)
                                sb.AppendLine("Name\tPrefix\tDriver\tColumns\tKeys\tDescription");
                            sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                                reader["name"], reader["prefix"], reader["driver"],
                                reader["col_count"], reader["key_count"],
                                Truncate(reader["description"]?.ToString(), 60)));
                            count++;
                        }
                        if (count == 0) return "No tables found matching \"" + pattern + "\"";
                        return sb.ToString() + "(" + count + " tables)";
                    }
                }
            }
        }

        /// <summary>
        /// Get full detail for a table: columns, keys, and relationships.
        /// </summary>
        public string GetTable(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return "Error: table name is required";
            if (!File.Exists(_dbPath)) return "Error: SchemaGraph database not found. Run ingest_schema first.";

            using (var conn = OpenConnection(readOnly: true))
            {
                // Find table
                long tableId = 0;
                string tName = "", tPrefix = "", tDriver = "", tDesc = "";

                using (var cmd = new SQLiteCommand(
                    "SELECT id, name, prefix, driver, description FROM tables WHERE name = @name COLLATE NOCASE", conn))
                {
                    cmd.Parameters.AddWithValue("@name", tableName);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return "Table \"" + tableName + "\" not found. Use search_tables to find it.";
                        tableId = reader.GetInt64(0);
                        tName = reader.GetString(1);
                        tPrefix = reader["prefix"]?.ToString() ?? "";
                        tDriver = reader["driver"]?.ToString() ?? "";
                        tDesc = reader["description"]?.ToString() ?? "";
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("## " + tName);
                if (!string.IsNullOrEmpty(tPrefix)) sb.AppendLine("Prefix: " + tPrefix);
                if (!string.IsNullOrEmpty(tDriver)) sb.AppendLine("Driver: " + tDriver);
                if (!string.IsNullOrEmpty(tDesc)) sb.AppendLine("Description: " + tDesc);
                sb.AppendLine();

                // Columns
                sb.AppendLine("### Columns");
                sb.AppendLine("Name\tType\tSize\tPicture\tDescription");
                using (var cmd = new SQLiteCommand(
                    "SELECT name, data_type, size, places, picture, description FROM columns WHERE table_id = @tid ORDER BY ordinal", conn))
                {
                    cmd.Parameters.AddWithValue("@tid", tableId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string dt = reader["data_type"]?.ToString() ?? "";
                            int sz = reader["size"] != DBNull.Value ? Convert.ToInt32(reader["size"]) : 0;
                            int pl = reader["places"] != DBNull.Value ? Convert.ToInt32(reader["places"]) : 0;
                            string sizeStr = sz > 0 ? sz.ToString() : "";
                            if (pl > 0) sizeStr += "." + pl;
                            sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}",
                                reader["name"], dt, sizeStr, reader["picture"],
                                Truncate(reader["description"]?.ToString(), 40)));
                        }
                    }
                }
                sb.AppendLine();

                // Keys
                sb.AppendLine("### Keys");
                using (var cmd = new SQLiteCommand(
                    @"SELECT k.id, k.name, k.is_primary, k.is_unique, k.is_auto_increment, k.is_exclude
                      FROM keys k WHERE k.table_id = @tid", conn))
                {
                    cmd.Parameters.AddWithValue("@tid", tableId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long keyId = reader.GetInt64(0);
                            string kName = reader["name"]?.ToString() ?? "";
                            var flags = new List<string>();
                            if (Convert.ToInt32(reader["is_primary"]) == 1) flags.Add("PRIMARY");
                            if (Convert.ToInt32(reader["is_unique"]) == 1) flags.Add("UNIQUE");
                            if (Convert.ToInt32(reader["is_auto_increment"]) == 1) flags.Add("AUTO");
                            if (Convert.ToInt32(reader["is_exclude"]) == 1) flags.Add("EXCLUDE");

                            // Get key columns
                            var keyCols = new List<string>();
                            using (var cmd2 = new SQLiteCommand(
                                @"SELECT c.name, kc.ascending FROM key_columns kc
                                  LEFT JOIN columns c ON kc.column_id = c.id
                                  WHERE kc.key_id = @kid ORDER BY kc.ordinal", conn))
                            {
                                cmd2.Parameters.AddWithValue("@kid", keyId);
                                using (var r2 = cmd2.ExecuteReader())
                                {
                                    while (r2.Read())
                                    {
                                        string colName = r2["name"]?.ToString() ?? "?";
                                        bool asc = r2["ascending"] != DBNull.Value && Convert.ToInt32(r2["ascending"]) == 1;
                                        keyCols.Add(colName + (asc ? "" : " DESC"));
                                    }
                                }
                            }

                            sb.AppendLine(string.Format("  {0} ({1}) [{2}]",
                                kName, string.Join(", ", keyCols), string.Join(", ", flags)));
                        }
                    }
                }
                sb.AppendLine();

                // Relationships
                sb.AppendLine("### Relationships");
                bool hasRels = false;

                // As parent (this table is referenced BY others)
                using (var cmd = new SQLiteCommand(
                    @"SELECT t.name as child_table, k.name as child_key
                      FROM relationships r
                      JOIN tables t ON r.child_table_id = t.id
                      LEFT JOIN keys k ON r.child_key_id = k.id
                      WHERE r.parent_table_id = @tid", conn))
                {
                    cmd.Parameters.AddWithValue("@tid", tableId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            sb.AppendLine(string.Format("  {0} -> {1} (via {2})",
                                tName, reader["child_table"], reader["child_key"]));
                            hasRels = true;
                        }
                    }
                }

                // As child (this table references others)
                using (var cmd = new SQLiteCommand(
                    @"SELECT t.name as parent_table, k.name as child_key
                      FROM relationships r
                      JOIN tables t ON r.parent_table_id = t.id
                      LEFT JOIN keys k ON r.child_key_id = k.id
                      WHERE r.child_table_id = @tid", conn))
                {
                    cmd.Parameters.AddWithValue("@tid", tableId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            sb.AppendLine(string.Format("  {0} <- {1} (via {2})",
                                tName, reader["parent_table"], reader["child_key"]));
                            hasRels = true;
                        }
                    }
                }

                if (!hasRels) sb.AppendLine("  (none)");

                return sb.ToString();
            }
        }

        /// <summary>
        /// Search columns across all tables by name pattern.
        /// </summary>
        public string SearchColumns(string pattern, int limit = 100)
        {
            if (string.IsNullOrEmpty(pattern)) return "Error: pattern is required";
            if (!File.Exists(_dbPath)) return "Error: SchemaGraph database not found. Run ingest_schema first.";

            using (var conn = OpenConnection(readOnly: true))
            {
                bool useFts = !pattern.Contains("%") && !pattern.Contains("_");
                string sql;

                if (useFts)
                {
                    sql = @"SELECT t.name as table_name, c.name, c.data_type, c.size, c.picture
                            FROM schema_fts f
                            JOIN columns c ON f.entity_id = c.id
                            JOIN tables t ON c.table_id = t.id
                            WHERE f.entity_type = 'column' AND schema_fts MATCH @pattern
                            ORDER BY rank
                            LIMIT @limit";
                }
                else
                {
                    sql = @"SELECT t.name as table_name, c.name, c.data_type, c.size, c.picture
                            FROM columns c
                            JOIN tables t ON c.table_id = t.id
                            WHERE c.name LIKE @pattern
                            ORDER BY t.name, c.name
                            LIMIT @limit";
                }

                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@pattern", useFts ? EscapeFts(pattern) + "*" : "%" + pattern + "%");
                    cmd.Parameters.AddWithValue("@limit", limit);

                    using (var reader = cmd.ExecuteReader())
                    {
                        var sb = new StringBuilder();
                        int count = 0;
                        while (reader.Read())
                        {
                            if (count == 0)
                                sb.AppendLine("Table\tColumn\tType\tSize\tPicture");
                            sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}",
                                reader["table_name"], reader["name"], reader["data_type"],
                                reader["size"], reader["picture"]));
                            count++;
                        }
                        if (count == 0) return "No columns found matching \"" + pattern + "\"";
                        return sb.ToString() + "(" + count + " columns)";
                    }
                }
            }
        }

        /// <summary>
        /// Get all relationships for a table (both directions).
        /// </summary>
        public string GetRelationships(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return "Error: table name is required";
            if (!File.Exists(_dbPath)) return "Error: SchemaGraph database not found. Run ingest_schema first.";

            using (var conn = OpenConnection(readOnly: true))
            {
                // Find table ID
                long tableId = 0;
                using (var cmd = new SQLiteCommand(
                    "SELECT id FROM tables WHERE name = @name COLLATE NOCASE", conn))
                {
                    cmd.Parameters.AddWithValue("@name", tableName);
                    var result = cmd.ExecuteScalar();
                    if (result == null) return "Table \"" + tableName + "\" not found.";
                    tableId = Convert.ToInt64(result);
                }

                var sb = new StringBuilder();
                sb.AppendLine("Relationships for " + tableName + ":");
                sb.AppendLine();
                int count = 0;

                // As parent
                sb.AppendLine("Referenced BY (children):");
                using (var cmd = new SQLiteCommand(
                    @"SELECT ct.name as child, k.name as via_key
                      FROM relationships r
                      JOIN tables ct ON r.child_table_id = ct.id
                      LEFT JOIN keys k ON r.child_key_id = k.id
                      WHERE r.parent_table_id = @tid
                      ORDER BY ct.name", conn))
                {
                    cmd.Parameters.AddWithValue("@tid", tableId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        bool any = false;
                        while (reader.Read())
                        {
                            sb.AppendLine("  -> " + reader["child"] + " (key: " + reader["via_key"] + ")");
                            count++;
                            any = true;
                        }
                        if (!any) sb.AppendLine("  (none)");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("References (parents):");
                using (var cmd = new SQLiteCommand(
                    @"SELECT pt.name as parent, k.name as via_key
                      FROM relationships r
                      JOIN tables pt ON r.parent_table_id = pt.id
                      LEFT JOIN keys k ON r.child_key_id = k.id
                      WHERE r.child_table_id = @tid
                      ORDER BY pt.name", conn))
                {
                    cmd.Parameters.AddWithValue("@tid", tableId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        bool any = false;
                        while (reader.Read())
                        {
                            sb.AppendLine("  <- " + reader["parent"] + " (key: " + reader["via_key"] + ")");
                            count++;
                            any = true;
                        }
                        if (!any) sb.AppendLine("  (none)");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("(" + count + " total relationships)");
                return sb.ToString();
            }
        }

        /// <summary>
        /// Validate that table/column names exist. Returns matches and suggestions for misspelled names.
        /// </summary>
        public string ValidateNames(string names)
        {
            if (string.IsNullOrEmpty(names)) return "Error: names parameter is required (comma-separated)";
            if (!File.Exists(_dbPath)) return "Error: SchemaGraph database not found. Run ingest_schema first.";

            var items = names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            using (var conn = OpenConnection(readOnly: true))
            {
                var sb = new StringBuilder();
                foreach (var raw in items)
                {
                    string item = raw.Trim();
                    if (string.IsNullOrEmpty(item)) continue;

                    // Check if it's a Prefix:Column format
                    bool found = false;

                    if (item.Contains(":"))
                    {
                        // Prefix:Column format
                        var parts = item.Split(':');
                        string prefix = parts[0].Trim();
                        string colName = parts[1].Trim();

                        using (var cmd = new SQLiteCommand(
                            @"SELECT t.name, c.name FROM columns c
                              JOIN tables t ON c.table_id = t.id
                              WHERE t.prefix = @prefix COLLATE NOCASE AND c.name = @col COLLATE NOCASE", conn))
                        {
                            cmd.Parameters.AddWithValue("@prefix", prefix);
                            cmd.Parameters.AddWithValue("@col", colName);
                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    sb.AppendLine("OK  " + item + " -> " + reader[0] + "." + reader[1]);
                                    found = true;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Plain name — check tables first, then columns
                        using (var cmd = new SQLiteCommand(
                            "SELECT name FROM tables WHERE name = @name COLLATE NOCASE", conn))
                        {
                            cmd.Parameters.AddWithValue("@name", item);
                            var result = cmd.ExecuteScalar();
                            if (result != null)
                            {
                                sb.AppendLine("OK  " + item + " (table: " + result + ")");
                                found = true;
                            }
                        }

                        if (!found)
                        {
                            using (var cmd = new SQLiteCommand(
                                @"SELECT t.name, c.name FROM columns c
                                  JOIN tables t ON c.table_id = t.id
                                  WHERE c.name = @name COLLATE NOCASE LIMIT 5", conn))
                            {
                                cmd.Parameters.AddWithValue("@name", item);
                                using (var reader = cmd.ExecuteReader())
                                {
                                    var matches = new List<string>();
                                    while (reader.Read())
                                        matches.Add(reader[0] + "." + reader[1]);
                                    if (matches.Count > 0)
                                    {
                                        sb.AppendLine("OK  " + item + " (column in: " + string.Join(", ", matches) + ")");
                                        found = true;
                                    }
                                }
                            }
                        }
                    }

                    if (!found)
                    {
                        // Suggest similar names via FTS
                        sb.Append("??  " + item + " NOT FOUND");
                        string suggestion = FindSimilar(conn, item);
                        if (!string.IsNullOrEmpty(suggestion))
                            sb.Append(" — did you mean: " + suggestion + "?");
                        sb.AppendLine();
                    }
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Get schema database statistics.
        /// </summary>
        public string GetStats()
        {
            if (!File.Exists(_dbPath)) return "Error: SchemaGraph database not found. Run ingest_schema first.";

            using (var conn = OpenConnection(readOnly: true))
            {
                var sb = new StringBuilder();
                sb.AppendLine("SchemaGraph Statistics:");
                sb.AppendLine();

                // Metadata
                using (var cmd = new SQLiteCommand("SELECT key, value FROM schema_metadata ORDER BY key", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        sb.AppendLine("  " + reader["key"] + ": " + reader["value"]);
                }
                sb.AppendLine();

                // Source breakdown
                sb.AppendLine("Tables by source:");
                using (var cmd = new SQLiteCommand(
                    "SELECT COALESCE(source,'dctx') as src, COUNT(*) as cnt FROM tables GROUP BY source ORDER BY cnt DESC", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        sb.AppendLine("  " + reader["src"] + ": " + reader["cnt"]);
                }
                sb.AppendLine();

                // Driver breakdown
                sb.AppendLine("Tables by driver:");
                using (var cmd = new SQLiteCommand(
                    "SELECT COALESCE(driver,'(none)') as drv, COUNT(*) as cnt FROM tables GROUP BY driver ORDER BY cnt DESC", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        sb.AppendLine("  " + reader["drv"] + ": " + reader["cnt"]);
                }
                sb.AppendLine();

                // Procedures/Views
                long procCount = 0, viewCount = 0;
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM procedures", conn))
                    procCount = Convert.ToInt64(cmd.ExecuteScalar());
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM views", conn))
                    viewCount = Convert.ToInt64(cmd.ExecuteScalar());
                if (procCount > 0 || viewCount > 0)
                {
                    sb.AppendLine("SQL Objects:");
                    if (procCount > 0) sb.AppendLine("  Procedures/Functions: " + procCount);
                    if (viewCount > 0) sb.AppendLine("  Views: " + viewCount);
                    sb.AppendLine();
                }

                // File size
                var fi = new FileInfo(_dbPath);
                sb.AppendLine("Database size: " + FormatSize(fi.Length));
                sb.AppendLine("Database path: " + _dbPath);

                return sb.ToString();
            }
        }

        /// <summary>
        /// Execute a raw SQL SELECT query against the schema database.
        /// </summary>
        public string ExecuteQuery(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "Error: sql parameter is required";
            if (!File.Exists(_dbPath)) return "Error: SchemaGraph database not found. Run ingest_schema first.";

            string trimmed = sql.TrimStart();
            if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
                return "Error: only SELECT/WITH/PRAGMA queries are allowed (read-only)";

            try
            {
                using (var conn = OpenConnection(readOnly: true))
                using (var cmd = new SQLiteCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    var sb = new StringBuilder();
                    int colCount = reader.FieldCount;

                    // Header
                    for (int i = 0; i < colCount; i++)
                    {
                        if (i > 0) sb.Append("\t");
                        sb.Append(reader.GetName(i));
                    }
                    sb.AppendLine();

                    // Rows (cap at 500)
                    int rowCount = 0;
                    while (reader.Read() && rowCount < 500)
                    {
                        for (int i = 0; i < colCount; i++)
                        {
                            if (i > 0) sb.Append("\t");
                            sb.Append(reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString());
                        }
                        sb.AppendLine();
                        rowCount++;
                    }

                    if (rowCount == 0) return "Query returned 0 rows.";
                    sb.AppendLine("(" + rowCount + " rows)");
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                return "SQL Error: " + ex.Message;
            }
        }

        #endregion

        #region Helpers

        private SQLiteConnection OpenConnection(bool readOnly)
        {
            string mode = readOnly ? "Read Only=True;" : "";
            string connStr = "Data Source=" + _dbPath + ";Version=3;" + mode + "Journal Mode=WAL;";
            var conn = new SQLiteConnection(connStr);
            conn.Open();

            conn.EnableExtensions(true);
            conn.LoadExtension("SQLite.Interop.dll", "sqlite3_fts5_init");

            return conn;
        }

        private string FindSimilar(SQLiteConnection conn, string name)
        {
            // Try FTS prefix search for suggestions
            try
            {
                string prefix = name.Length > 2 ? name.Substring(0, 3) : name;
                using (var cmd = new SQLiteCommand(
                    @"SELECT name FROM schema_fts WHERE schema_fts MATCH @p LIMIT 5", conn))
                {
                    cmd.Parameters.AddWithValue("@p", EscapeFts(prefix) + "*");
                    using (var reader = cmd.ExecuteReader())
                    {
                        var suggestions = new List<string>();
                        while (reader.Read())
                            suggestions.Add(reader.GetString(0));
                        return suggestions.Count > 0 ? string.Join(", ", suggestions) : null;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string EscapeFts(string input)
        {
            // Escape FTS5 special characters
            return "\"" + input.Replace("\"", "\"\"") + "\"";
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }

        private static int ParseInt(string s, int defaultValue)
        {
            int result;
            return int.TryParse(s, out result) ? result : defaultValue;
        }

        #endregion

        #region Global Source Registry

        private static string _globalDbPath;
        private static bool _globalDbInitialized;

        /// <summary>
        /// Path to the global schema-sources.db in %APPDATA%\ClarionAssistant
        /// </summary>
        public static string GlobalDbPath
        {
            get
            {
                if (_globalDbPath == null)
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string dir = Path.Combine(appData, "ClarionAssistant");
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    _globalDbPath = Path.Combine(dir, "schema-sources.db");
                }
                return _globalDbPath;
            }
        }

        public static void EnsureGlobalDatabase()
        {
            if (_globalDbInitialized) return;
            try
            {
                using (var conn = OpenGlobalConnection(readOnly: false))
                {
                    CreateGlobalSchema(conn);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SchemaGraphService] EnsureGlobalDatabase error: " + ex.Message);
            }
            _globalDbInitialized = true;
        }

        private static void CreateGlobalSchema(SQLiteConnection conn)
        {
            string[] ddl = new[]
            {
                @"CREATE TABLE IF NOT EXISTS sources (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL,
                    connection_info BLOB,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                )",
                @"CREATE TABLE IF NOT EXISTS solution_sources (
                    solution_path TEXT NOT NULL,
                    source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                    added_at TEXT NOT NULL,
                    PRIMARY KEY (solution_path, source_id)
                )",
                @"CREATE INDEX IF NOT EXISTS idx_ss_path ON solution_sources(solution_path)",
                @"CREATE INDEX IF NOT EXISTS idx_ss_source ON solution_sources(source_id)",

                @"CREATE TABLE IF NOT EXISTS solution_repos (
                    solution_path TEXT PRIMARY KEY,
                    account_id TEXT,
                    repo_name TEXT
                )",

                @"CREATE TABLE IF NOT EXISTS github_accounts (
                    id TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    username TEXT NOT NULL,
                    token BLOB,
                    provider TEXT NOT NULL DEFAULT 'github',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                )"
            };
            foreach (string sql in ddl)
            {
                using (var cmd = new SQLiteCommand(sql, conn))
                    cmd.ExecuteNonQuery();
            }

            // Migrations — add columns to existing tables
            try
            {
                using (var cmd = new SQLiteCommand("ALTER TABLE github_accounts ADD COLUMN provider TEXT NOT NULL DEFAULT 'github'", conn))
                    cmd.ExecuteNonQuery();
            }
            catch { } // Column already exists — ignore
        }

        // ── Encryption (DPAPI, current-user scope) ──

        private static byte[] EncryptConnectionInfo(string json)
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(json);
            return ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        }

        private static string DecryptConnectionInfo(byte[] encrypted)
        {
            byte[] plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }

        // ── Source CRUD ──

        public static string AddSource(string name, string type, string connectionInfoJson)
        {
            EnsureGlobalDatabase();
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            string now = DateTime.UtcNow.ToString("o");
            byte[] encrypted = EncryptConnectionInfo(connectionInfoJson);

            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                using (var cmd = new SQLiteCommand(
                    @"INSERT INTO sources (id, name, type, connection_info, created_at, updated_at)
                      VALUES (@id, @name, @type, @info, @now, @now)", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@type", type);
                    cmd.Parameters.Add("@info", DbType.Binary).Value = encrypted;
                    cmd.Parameters.AddWithValue("@now", now);
                    cmd.ExecuteNonQuery();
                }
            }
            return id;
        }

        public static void UpdateSource(string id, string name, string type, string connectionInfoJson)
        {
            EnsureGlobalDatabase();
            string now = DateTime.UtcNow.ToString("o");
            byte[] encrypted = EncryptConnectionInfo(connectionInfoJson);

            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                using (var cmd = new SQLiteCommand(
                    "UPDATE sources SET name=@name, type=@type, connection_info=@info, updated_at=@now WHERE id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@type", type);
                    cmd.Parameters.Add("@info", DbType.Binary).Value = encrypted;
                    cmd.Parameters.AddWithValue("@now", now);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static void DeleteSource(string id)
        {
            EnsureGlobalDatabase();
            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                // Remove solution links first, then source
                using (var cmd = new SQLiteCommand("DELETE FROM solution_sources WHERE source_id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SQLiteCommand("DELETE FROM sources WHERE id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static List<Dictionary<string, object>> GetAllSources()
        {
            EnsureGlobalDatabase();
            var results = new List<Dictionary<string, object>>();
            using (var conn = OpenGlobalConnection(readOnly: true))
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT id, name, type, connection_info, created_at, updated_at FROM sources ORDER BY name", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var source = new Dictionary<string, object>();
                        source["id"] = reader.GetString(0);
                        source["name"] = reader.GetString(1);
                        source["type"] = reader.GetString(2);
                        try
                        {
                            byte[] encrypted = (byte[])reader["connection_info"];
                            source["connectionInfo"] = DecryptConnectionInfo(encrypted);
                        }
                        catch { source["connectionInfo"] = "{}"; }
                        source["createdAt"] = reader.GetString(4);
                        source["updatedAt"] = reader.GetString(5);
                        results.Add(source);
                    }
                }
            }
            return results;
        }

        public static Dictionary<string, object> GetSource(string id)
        {
            EnsureGlobalDatabase();
            using (var conn = OpenGlobalConnection(readOnly: true))
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT id, name, type, connection_info, created_at, updated_at FROM sources WHERE id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        var source = new Dictionary<string, object>();
                        source["id"] = reader.GetString(0);
                        source["name"] = reader.GetString(1);
                        source["type"] = reader.GetString(2);
                        try
                        {
                            byte[] encrypted = (byte[])reader["connection_info"];
                            source["connectionInfo"] = DecryptConnectionInfo(encrypted);
                        }
                        catch { source["connectionInfo"] = "{}"; }
                        source["createdAt"] = reader.GetString(4);
                        source["updatedAt"] = reader.GetString(5);
                        return source;
                    }
                }
            }
        }

        // ── Solution Linking ──

        public static List<Dictionary<string, object>> GetSourcesForSolution(string solutionPath)
        {
            EnsureGlobalDatabase();
            var results = new List<Dictionary<string, object>>();
            using (var conn = OpenGlobalConnection(readOnly: true))
            {
                using (var cmd = new SQLiteCommand(
                    @"SELECT s.id, s.name, s.type, s.connection_info, s.created_at, s.updated_at, ss.added_at
                      FROM sources s
                      INNER JOIN solution_sources ss ON s.id = ss.source_id
                      WHERE ss.solution_path = @path
                      ORDER BY s.name", conn))
                {
                    cmd.Parameters.AddWithValue("@path", solutionPath);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var source = new Dictionary<string, object>();
                            source["id"] = reader.GetString(0);
                            source["name"] = reader.GetString(1);
                            source["type"] = reader.GetString(2);
                            try
                            {
                                byte[] encrypted = (byte[])reader["connection_info"];
                                source["connectionInfo"] = DecryptConnectionInfo(encrypted);
                            }
                            catch { source["connectionInfo"] = "{}"; }
                            source["createdAt"] = reader.GetString(4);
                            source["updatedAt"] = reader.GetString(5);
                            source["linkedAt"] = reader.GetString(6);
                            results.Add(source);
                        }
                    }
                }
            }
            return results;
        }

        public static void LinkSourceToSolution(string solutionPath, string sourceId)
        {
            EnsureGlobalDatabase();
            string now = DateTime.UtcNow.ToString("o");
            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                using (var cmd = new SQLiteCommand(
                    "INSERT OR IGNORE INTO solution_sources (solution_path, source_id, added_at) VALUES (@path, @id, @now)", conn))
                {
                    cmd.Parameters.AddWithValue("@path", solutionPath);
                    cmd.Parameters.AddWithValue("@id", sourceId);
                    cmd.Parameters.AddWithValue("@now", now);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static void UnlinkSourceFromSolution(string solutionPath, string sourceId)
        {
            EnsureGlobalDatabase();
            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                using (var cmd = new SQLiteCommand(
                    "DELETE FROM solution_sources WHERE solution_path=@path AND source_id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@path", solutionPath);
                    cmd.Parameters.AddWithValue("@id", sourceId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ── Schema DB Path Resolution ──

        /// <summary>
        /// Compute where the schema graph DB lives for a given source.
        /// DCTX: next to the dictionary file. Others: %APPDATA%\ClarionAssistant\schemas\{sourceId}.schemagraph.db
        /// </summary>
        public static string GetDbPathForSource(string sourceId, string sourceType, string connectionInfoJson)
        {
            if (sourceType == "dctx")
            {
                // Parse filePath from connection info JSON
                string filePath = ExtractJsonValue(connectionInfoJson, "filePath");
                if (!string.IsNullOrEmpty(filePath))
                    return GetDbPathForDictionary(filePath);
            }

            // SQL sources: store in %APPDATA%\ClarionAssistant\schemas\
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string schemasDir = Path.Combine(appData, "ClarionAssistant", "schemas");
            if (!Directory.Exists(schemasDir))
                Directory.CreateDirectory(schemasDir);
            return Path.Combine(schemasDir, sourceId + ".schemagraph.db");
        }

        // ── Source Status ──

        /// <summary>
        /// Get indexing status for a source: table count, last indexed, DB file size.
        /// </summary>
        public static Dictionary<string, object> GetSourceStatus(string sourceId, string sourceType, string connectionInfoJson)
        {
            var status = new Dictionary<string, object>();
            string dbPath = GetDbPathForSource(sourceId, sourceType, connectionInfoJson);
            status["dbPath"] = dbPath;
            status["indexed"] = false;

            if (!File.Exists(dbPath))
                return status;

            status["indexed"] = true;
            var fi = new FileInfo(dbPath);
            status["fileSize"] = fi.Length;

            try
            {
                string connStr = "Data Source=" + dbPath + ";Version=3;Read Only=True;Journal Mode=WAL;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM tables", conn))
                        status["tableCount"] = Convert.ToInt32(cmd.ExecuteScalar());
                    using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM columns", conn))
                        status["columnCount"] = Convert.ToInt32(cmd.ExecuteScalar());

                    // Get last indexed time from metadata
                    string[] metaKeys = { "ingested_at", "sql_ingested_at", "sqlite_ingested_at", "pg_ingested_at" };
                    foreach (string key in metaKeys)
                    {
                        using (var cmd = new SQLiteCommand("SELECT value FROM schema_metadata WHERE key=@k", conn))
                        {
                            cmd.Parameters.AddWithValue("@k", key);
                            var val = cmd.ExecuteScalar();
                            if (val != null && val != DBNull.Value)
                            {
                                status["lastIndexed"] = val.ToString();
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            return status;
        }

        // ── Index Dispatch ──

        /// <summary>
        /// Index a source by ID: looks up type and connection info, dispatches to appropriate ingestion method.
        /// </summary>
        public static string IndexSource(string sourceId)
        {
            var source = GetSource(sourceId);
            if (source == null)
                return "Error: Source not found: " + sourceId;

            string type = (string)source["type"];
            string connInfo = (string)source["connectionInfo"];
            string dbPath = GetDbPathForSource(sourceId, type, connInfo);

            var svc = new SchemaGraphService(dbPath);
            svc.EnsureDatabase();

            switch (type)
            {
                case "dctx":
                    string filePath = ExtractJsonValue(connInfo, "filePath");
                    if (string.IsNullOrEmpty(filePath))
                        return "Error: No filePath in connection info";
                    return svc.IngestDctx(filePath);

                case "mssql":
                    string mssqlConn = BuildMssqlConnectionString(connInfo);
                    return svc.IngestSqlDatabase(mssqlConn);

                case "sqlite":
                    string sqlitePath = ExtractJsonValue(connInfo, "filePath");
                    if (string.IsNullOrEmpty(sqlitePath))
                        return "Error: No filePath in connection info";
                    return svc.IngestSqliteDatabase(sqlitePath);

                case "postgres":
                    string pgConn = BuildPostgresConnectionString(connInfo);
                    return svc.IngestPostgresDatabase(pgConn);

                default:
                    return "Error: Unknown source type: " + type;
            }
        }

        // ── Connection String Builders ──

        public static string BuildMssqlConnectionString(string json)
        {
            string server = ExtractJsonValue(json, "server");
            string database = ExtractJsonValue(json, "database");
            string username = ExtractJsonValue(json, "username");
            string password = ExtractJsonValue(json, "password");

            if (!string.IsNullOrEmpty(username))
                return string.Format("Server={0};Database={1};User Id={2};Password={3};", server, database, username, password);
            else
                return string.Format("Server={0};Database={1};Integrated Security=True;", server, database);
        }

        public static string BuildPostgresConnectionString(string json)
        {
            string server = ExtractJsonValue(json, "server");
            string port = ExtractJsonValue(json, "port");
            string database = ExtractJsonValue(json, "database");
            string username = ExtractJsonValue(json, "username");
            string password = ExtractJsonValue(json, "password");

            if (string.IsNullOrEmpty(port)) port = "5432";
            return string.Format("Host={0};Port={1};Database={2};Username={3};Password={4};",
                server, port, database, username, password);
        }

        /// <summary>
        /// Minimal JSON value extractor — avoids dependency on JSON library for simple flat objects.
        /// </summary>
        private static string ExtractJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            // Skip whitespace and opening quote
            int start = colonIdx + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;

            if (start >= json.Length) return null;

            if (json[start] == '"')
            {
                // String value
                start++;
                var sb = new StringBuilder();
                for (int i = start; i < json.Length; i++)
                {
                    if (json[i] == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                    if (json[i] == '"') break;
                    sb.Append(json[i]);
                }
                return sb.ToString();
            }
            else
            {
                // Number or other non-string value
                var sb = new StringBuilder();
                for (int i = start; i < json.Length; i++)
                {
                    if (json[i] == ',' || json[i] == '}' || json[i] == ' ') break;
                    sb.Append(json[i]);
                }
                return sb.ToString();
            }
        }

        private static SQLiteConnection OpenGlobalConnection(bool readOnly)
        {
            string mode = readOnly ? "Read Only=True;" : "";
            string connStr = "Data Source=" + GlobalDbPath + ";Version=3;" + mode + "Journal Mode=WAL;";
            var conn = new SQLiteConnection(connStr);
            conn.Open();
            return conn;
        }

        // ── Solution Source Control ──

        public static void SetSolutionRepo(string solutionPath, string accountId, string repoName)
        {
            EnsureGlobalDatabase();
            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                if (string.IsNullOrEmpty(accountId) && string.IsNullOrEmpty(repoName))
                {
                    using (var cmd = new SQLiteCommand("DELETE FROM solution_repos WHERE solution_path=@path", conn))
                    {
                        cmd.Parameters.AddWithValue("@path", solutionPath);
                        cmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    using (var cmd = new SQLiteCommand(
                        @"INSERT OR REPLACE INTO solution_repos (solution_path, account_id, repo_name)
                          VALUES (@path, @acct, @repo)", conn))
                    {
                        cmd.Parameters.AddWithValue("@path", solutionPath);
                        cmd.Parameters.AddWithValue("@acct", accountId ?? "");
                        cmd.Parameters.AddWithValue("@repo", repoName ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        public static Dictionary<string, string> GetSolutionRepo(string solutionPath)
        {
            EnsureGlobalDatabase();
            using (var conn = OpenGlobalConnection(readOnly: true))
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT account_id, repo_name FROM solution_repos WHERE solution_path=@path", conn))
                {
                    cmd.Parameters.AddWithValue("@path", solutionPath);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        return new Dictionary<string, string>
                        {
                            { "accountId", reader.IsDBNull(0) ? "" : reader.GetString(0) },
                            { "repoName", reader.IsDBNull(1) ? "" : reader.GetString(1) }
                        };
                    }
                }
            }
        }

        // ── GitHub Accounts ──

        public static string AddGitHubAccount(string displayName, string username, string token, string provider = "github")
        {
            EnsureGlobalDatabase();
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            string now = DateTime.UtcNow.ToString("o");
            byte[] encrypted = EncryptConnectionInfo(token ?? "");

            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                using (var cmd = new SQLiteCommand(
                    @"INSERT INTO github_accounts (id, display_name, username, token, provider, created_at, updated_at)
                      VALUES (@id, @name, @user, @token, @prov, @now, @now)", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@name", displayName);
                    cmd.Parameters.AddWithValue("@user", username);
                    cmd.Parameters.Add("@token", DbType.Binary).Value = encrypted;
                    cmd.Parameters.AddWithValue("@prov", provider ?? "github");
                    cmd.Parameters.AddWithValue("@now", now);
                    cmd.ExecuteNonQuery();
                }
            }
            return id;
        }

        public static void UpdateGitHubAccount(string id, string displayName, string username, string token, string provider = null)
        {
            EnsureGlobalDatabase();
            string now = DateTime.UtcNow.ToString("o");
            byte[] encrypted = EncryptConnectionInfo(token ?? "");

            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                string sql = provider != null
                    ? "UPDATE github_accounts SET display_name=@name, username=@user, token=@token, provider=@prov, updated_at=@now WHERE id=@id"
                    : "UPDATE github_accounts SET display_name=@name, username=@user, token=@token, updated_at=@now WHERE id=@id";
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@name", displayName);
                    cmd.Parameters.AddWithValue("@user", username);
                    cmd.Parameters.Add("@token", DbType.Binary).Value = encrypted;
                    if (provider != null) cmd.Parameters.AddWithValue("@prov", provider);
                    cmd.Parameters.AddWithValue("@now", now);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static void DeleteGitHubAccount(string id)
        {
            EnsureGlobalDatabase();
            using (var conn = OpenGlobalConnection(readOnly: false))
            {
                using (var cmd = new SQLiteCommand("DELETE FROM github_accounts WHERE id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static List<Dictionary<string, object>> GetAllGitHubAccounts()
        {
            EnsureGlobalDatabase();
            var results = new List<Dictionary<string, object>>();
            using (var conn = OpenGlobalConnection(readOnly: true))
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT id, display_name, username, token, provider, created_at, updated_at FROM github_accounts ORDER BY display_name", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var acct = new Dictionary<string, object>();
                        acct["id"] = reader.GetString(0);
                        acct["displayName"] = reader.GetString(1);
                        acct["username"] = reader.GetString(2);
                        try
                        {
                            byte[] encrypted = (byte[])reader["token"];
                            acct["token"] = DecryptConnectionInfo(encrypted);
                        }
                        catch { acct["token"] = ""; }
                        acct["provider"] = reader.IsDBNull(4) ? "github" : reader.GetString(4);
                        acct["createdAt"] = reader.GetString(5);
                        acct["updatedAt"] = reader.GetString(6);
                        results.Add(acct);
                    }
                }
            }
            return results;
        }

        public static Dictionary<string, object> GetGitHubAccount(string id)
        {
            EnsureGlobalDatabase();
            using (var conn = OpenGlobalConnection(readOnly: true))
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT id, display_name, username, token, provider, created_at, updated_at FROM github_accounts WHERE id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        var acct = new Dictionary<string, object>();
                        acct["id"] = reader.GetString(0);
                        acct["displayName"] = reader.GetString(1);
                        acct["username"] = reader.GetString(2);
                        try
                        {
                            byte[] encrypted = (byte[])reader["token"];
                            acct["token"] = DecryptConnectionInfo(encrypted);
                        }
                        catch { acct["token"] = ""; }
                        acct["provider"] = reader.IsDBNull(4) ? "github" : reader.GetString(4);
                        acct["createdAt"] = reader.GetString(5);
                        acct["updatedAt"] = reader.GetString(6);
                        return acct;
                    }
                }
            }
        }

        #endregion
    }
}
