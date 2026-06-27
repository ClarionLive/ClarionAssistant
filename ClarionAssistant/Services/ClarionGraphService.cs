using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using ClarionCodeGraph.Graph;
using ClarionCodeGraph.Parsing;
using ClarionCodeGraph.Parsing.Models;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// ClarionGraph — a VERSION-KEYED graph of STATIC Clarion library symbols
    /// (ABC classes/methods, library .inc classes, equates) derived from a Clarion install's
    /// LibSrc.
    ///
    /// Why this exists (ticket 6e8f2439): library symbols are version-stable, so they should
    /// be ingested ONCE per Clarion build, cached, and reused — not re-indexed per project.
    /// This generalizes the narrow, equate-only, non-versioned <see cref="LibraryIndexer"/>:
    ///   * Cache lives in %APPDATA%\ClarionAssistant\clariongraph\ClarionGraph_&lt;version&gt;.db
    ///     (shareable + versioned), NOT the assembly dir LibraryIndexer used.
    ///   * The version key is the full Clarion build (e.g. "12.0.14000"), resolved from the
    ///     running IDE exe via <see cref="ClarionVersionService"/>.
    ///   * Build-on-first-detect: build the version DB the first time a version is seen; reuse
    ///     after (LOCKED decision, John 2026-06-27).
    ///
    /// Ingestion (Phase 2):
    ///   * Every <c>*.inc</c> in <c>libsrc\win</c> → <see cref="ClarionParser.ParseIncFile"/>,
    ///     which extracts CLASS/INTERFACE definitions and their method prototypes
    ///     (Name = "ClassName.Method", ParentName = "ClassName", with params/return). This is the
    ///     headline win — completion/hover/definition can finally see ABC + library classes.
    ///   * The flat equate files (equates/property/builtins/winerr) → a dedicated EQUATE scan,
    ///     because those files have no MEMBER/PROCEDURE structure for the parser to anchor on.
    ///
    /// Storage reuses <see cref="CodeGraphDatabase"/> (same schema as CodeGraph: projects/symbols/
    /// relationships/index_metadata), so <see cref="CodeGraphProvider"/> queries it UNCHANGED.
    ///
    /// Every public method is defensive: on any failure it returns an error result / null and
    /// never throws, because it feeds completion/navigation which must never break the editor.
    /// </summary>
    public static class ClarionGraphService
    {
        private const string CacheFolderName = "clariongraph";

        // Flat equate files (no class structure) — ingested via the dedicated EQUATE scan.
        private static readonly string[] EquateFileNames =
        {
            "equates.clw", "property.clw", "builtins.clw", "winerr.inc"
        };

        private static readonly Regex EquateRegex = new Regex(
            @"^([\w:]+)\s+EQUATE\s*\(([^)]*)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ===== Version + path resolution =====

        /// <summary>
        /// %APPDATA%\ClarionAssistant\clariongraph — the shareable, versioned cache dir.
        /// Does NOT create the directory (build does that). Returns null only on catastrophic failure.
        /// </summary>
        public static string GetCacheDir()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "ClarionAssistant", CacheFolderName);
            }
            catch { return null; }
        }

        /// <summary>
        /// Full Clarion build version key (e.g. "12.0.14000"), read from the running IDE exe's
        /// file version. Returns null if it can't be determined (e.g. running outside the IDE).
        /// </summary>
        public static string ResolveVersionKey()
        {
            try
            {
                var info = ClarionVersionService.Detect();
                string exePath = info?.ClarionExePath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    return null;

                var vi = FileVersionInfo.GetVersionInfo(exePath);
                if (vi.FileMajorPart <= 0)
                    return null;

                return string.Format("{0}.{1}.{2}",
                    vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart);
            }
            catch { return null; }
        }

        /// <summary>
        /// The LibSrc\win directory of the current Clarion version — the source of library symbols.
        /// Returns null if the version/root can't be resolved or the dir doesn't exist.
        /// </summary>
        public static string ResolveLibSrcRoot()
        {
            try
            {
                var info = ClarionVersionService.Detect();
                var cfg = info?.GetCurrentConfig();
                string root = cfg?.RootPath;
                if (string.IsNullOrEmpty(root))
                    return null;

                string libSrcWin = Path.Combine(root, "libsrc", "win");
                return Directory.Exists(libSrcWin) ? libSrcWin : null;
            }
            catch { return null; }
        }

        /// <summary>Versioned DB path for the CURRENT detected version, or null if unknown.</summary>
        public static string ResolveDbPath()
        {
            string version = ResolveVersionKey();
            return version == null ? null : ResolveDbPath(version);
        }

        /// <summary>Versioned DB path for an explicit version key.</summary>
        public static string ResolveDbPath(string version)
        {
            if (string.IsNullOrEmpty(version)) return null;
            string cacheDir = GetCacheDir();
            if (string.IsNullOrEmpty(cacheDir)) return null;
            return Path.Combine(cacheDir, "ClarionGraph_" + SanitizeForFileName(version) + ".db");
        }

        private static string SanitizeForFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        // ===== Build / ensure =====

        /// <summary>
        /// Build-on-first-detect: resolve the current version's DB and, if it's absent (or
        /// <paramref name="force"/> is true), build it from LibSrc; otherwise reuse the cached DB.
        /// Returns a result describing what happened (built vs reused, symbol count, errors).
        /// Never throws.
        /// </summary>
        public static ClarionGraphResult EnsureBuilt(bool force = false)
        {
            var result = new ClarionGraphResult();
            try
            {
                string version = ResolveVersionKey();
                if (version == null)
                {
                    result.Error = "Could not resolve Clarion version (is the IDE running?).";
                    return result;
                }
                result.Version = version;

                string dbPath = ResolveDbPath(version);
                if (string.IsNullOrEmpty(dbPath))
                {
                    result.Error = "Could not resolve ClarionGraph cache path.";
                    return result;
                }
                result.DbPath = dbPath;

                // Reuse the cached DB unless forced.
                if (!force && File.Exists(dbPath))
                {
                    result.Built = false;
                    result.SymbolCount = ReadSymbolCount(dbPath);
                    result.LibSrcRoot = ReadMetadataValue(dbPath, "libsrc_root");
                    return result;
                }

                string libSrcRoot = ResolveLibSrcRoot();
                if (string.IsNullOrEmpty(libSrcRoot))
                {
                    result.Error = "LibSrc\\win not found for this Clarion version.";
                    return result;
                }
                result.LibSrcRoot = libSrcRoot;

                var built = Build(version, dbPath, libSrcRoot);
                result.Built = string.IsNullOrEmpty(built.Error);
                result.SymbolCount = built.SymbolCount;
                result.Error = built.Error;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Build (or rebuild) the version DB at <paramref name="dbPath"/> from
        /// <paramref name="libSrcRoot"/>: all <c>*.inc</c> classes/methods + the flat equate files.
        /// Reuses <see cref="CodeGraphDatabase"/> for storage so the schema matches CodeGraph.
        /// </summary>
        public static LibraryIndexResult Build(string version, string dbPath, string libSrcRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(libSrcRoot) || !Directory.Exists(libSrcRoot))
                    return new LibraryIndexResult { Error = "LibSrc not found: " + libSrcRoot };

                string cacheDir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                    Directory.CreateDirectory(cacheDir);

                // Fresh build: remove any existing db + its WAL sidecar files.
                DeleteDbFiles(dbPath);

                int totalSymbols = 0;
                // Library mode: capture column-0 method prototypes and keep builtin-named
                // methods (FileManager.Open/.Close/.Next/...) — see ClarionParser.LibraryMode.
                var parser = new ClarionParser { LibraryMode = true };

                using (var db = new CodeGraphDatabase())
                {
                    db.Open(dbPath); // creates schema

                    var libProj = new SolutionProject
                    {
                        Name = "ClarionGraph",
                        Guid = "{00000000-0000-0000-0000-000000000001}",
                        OutputType = "Library",
                        CwprojPath = libSrcRoot,
                        SlnPath = libSrcRoot
                    };
                    int projectId = db.InsertProject(libProj);

                    using (var tx = db.BeginTransaction())
                    {
                        // 1) All library .inc files → CLASS/INTERFACE + method prototypes.
                        string[] incFiles;
                        try { incFiles = Directory.GetFiles(libSrcRoot, "*.inc", SearchOption.TopDirectoryOnly); }
                        catch { incFiles = new string[0]; }

                        foreach (string incPath in incFiles)
                        {
                            try
                            {
                                var pr = parser.ParseIncFile(incPath, projectId);
                                foreach (var sym in pr.Symbols)
                                {
                                    db.InsertSymbol(sym);
                                    totalSymbols++;
                                }
                            }
                            catch { /* skip a single bad file, keep building */ }
                        }

                        // 2) Flat equate files → dedicated EQUATE scan.
                        foreach (string fileName in EquateFileNames)
                        {
                            string filePath = Path.Combine(libSrcRoot, fileName);
                            if (File.Exists(filePath))
                                totalSymbols += IngestEquateFile(db, filePath, projectId);
                        }

                        // Metadata (version, libsrc root, built_at, symbol_count).
                        db.SetMetadata("version", version);
                        db.SetMetadata("libsrc_root", libSrcRoot);
                        db.SetMetadata("built_at", DateTime.Now.ToString("o"));
                        db.SetMetadata("symbol_count", totalSymbols.ToString());
                        tx.Commit();
                    }
                }

                return new LibraryIndexResult { SymbolCount = totalSymbols, DbPath = dbPath };
            }
            catch (Exception ex)
            {
                return new LibraryIndexResult { Error = ex.Message };
            }
        }

        private static void DeleteDbFiles(string dbPath)
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); }
                catch { }
            }
        }

        /// <summary>
        /// Scan a flat EQUATE list file (e.g. equates.clw) and insert each EQUATE as a global
        /// "variable" symbol. Mirrors the original LibraryIndexer behaviour. Returns the count.
        /// </summary>
        private static int IngestEquateFile(CodeGraphDatabase db, string filePath, int projectId)
        {
            int count = 0;
            string[] lines;
            try { lines = File.ReadAllLines(filePath); }
            catch { return 0; }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("!"))
                    continue;

                int commentIdx = line.IndexOf('!');
                string codePart = commentIdx >= 0 ? line.Substring(0, commentIdx).Trim() : line;

                var match = EquateRegex.Match(codePart);
                if (match.Success)
                {
                    string name = match.Groups[1].Value;
                    string value = match.Groups[2].Value.Trim();

                    db.InsertSymbol(new ClarionSymbol
                    {
                        Name = name,
                        Type = "variable",
                        FilePath = filePath,
                        LineNumber = i + 1,
                        ProjectId = projectId,
                        Params = "EQUATE",
                        Scope = "global",
                        SourcePreview = name + " EQUATE(" + value + ")"
                    });
                    count++;
                }
            }

            return count;
        }

        // ===== Status =====

        /// <summary>
        /// Status of the CURRENT version's ClarionGraph DB: absent / built / stale / error.
        /// "stale" = a LibSrc source file has a newer write time than the DB's built_at, so a
        /// rebuild would pick up changes. Never throws.
        /// </summary>
        public static ClarionGraphStatus GetStatus()
        {
            var status = new ClarionGraphStatus();
            try
            {
                string version = ResolveVersionKey();
                status.Version = version;
                if (version == null)
                {
                    status.State = "error";
                    status.Error = "Clarion version not detected";
                    return status;
                }

                string dbPath = ResolveDbPath(version);
                status.DbPath = dbPath;
                if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                {
                    status.State = "absent";
                    return status;
                }

                status.Exists = true;
                status.SymbolCount = ReadSymbolCount(dbPath);
                status.LibSrcRoot = ReadMetadataValue(dbPath, "libsrc_root");

                string builtAtRaw = ReadMetadataValue(dbPath, "built_at");
                DateTime builtAt;
                if (DateTime.TryParse(builtAtRaw, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out builtAt))
                    status.BuiltAt = builtAt;

                status.State = IsStale(status.LibSrcRoot, status.BuiltAt) ? "stale" : "built";
                return status;
            }
            catch (Exception ex)
            {
                status.State = "error";
                status.Error = ex.Message;
                return status;
            }
        }

        /// <summary>One-line human-readable status, for the settings dialog.</summary>
        public static string GetStatusText()
        {
            var s = GetStatus();
            switch (s.State)
            {
                case "absent": return "Not built" + (s.Version != null ? " (v" + s.Version + ")" : "");
                case "built":  return s.SymbolCount + " symbols (v" + s.Version + ")";
                case "stale":  return s.SymbolCount + " symbols (v" + s.Version + ", LibSrc changed — rebuild recommended)";
                default:       return "Error: " + (s.Error ?? "unknown");
            }
        }

        /// <summary>
        /// A version DB is stale if any ingested LibSrc file (*.inc or a flat equate file) has a
        /// LastWriteTime newer than the DB's built_at timestamp.
        /// </summary>
        private static bool IsStale(string libSrcRoot, DateTime? builtAt)
        {
            try
            {
                if (builtAt == null || string.IsNullOrEmpty(libSrcRoot) || !Directory.Exists(libSrcRoot))
                    return false;

                var candidates = new List<string>();
                try { candidates.AddRange(Directory.GetFiles(libSrcRoot, "*.inc", SearchOption.TopDirectoryOnly)); }
                catch { }
                foreach (string fileName in EquateFileNames)
                    candidates.Add(Path.Combine(libSrcRoot, fileName));

                foreach (string filePath in candidates)
                {
                    if (File.Exists(filePath) && File.GetLastWriteTime(filePath) > builtAt.Value)
                        return true;
                }
            }
            catch { }
            return false;
        }

        // ===== DB read helpers =====

        private static int ReadSymbolCount(string dbPath)
        {
            string val = ReadMetadataValue(dbPath, "symbol_count");
            int n;
            return int.TryParse(val, out n) ? n : 0;
        }

        private static string ReadMetadataValue(string dbPath, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return null;
                string connStr = "Data Source=" + dbPath + ";Version=3;Read Only=True;Journal Mode=WAL;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(
                        "SELECT value FROM index_metadata WHERE key=@key", conn))
                    {
                        cmd.Parameters.AddWithValue("@key", key);
                        var result = cmd.ExecuteScalar();
                        return result?.ToString();
                    }
                }
            }
            catch { return null; }
        }
    }

    /// <summary>Outcome of <see cref="ClarionGraphService.EnsureBuilt"/>.</summary>
    public class ClarionGraphResult
    {
        public string Version { get; set; }
        public string DbPath { get; set; }
        public string LibSrcRoot { get; set; }
        public int SymbolCount { get; set; }
        /// <summary>True if this call built the DB; false if it reused a cached one.</summary>
        public bool Built { get; set; }
        public string Error { get; set; }
        public bool Success { get { return string.IsNullOrEmpty(Error); } }
    }

    /// <summary>Status snapshot of the current version's ClarionGraph DB.</summary>
    public class ClarionGraphStatus
    {
        public string Version { get; set; }
        public string DbPath { get; set; }
        public string LibSrcRoot { get; set; }
        public bool Exists { get; set; }
        public int SymbolCount { get; set; }
        public DateTime? BuiltAt { get; set; }
        /// <summary>"absent" | "built" | "stale" | "error".</summary>
        public string State { get; set; }
        public string Error { get; set; }
    }
}
