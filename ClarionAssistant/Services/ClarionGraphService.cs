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

        // Bump when the INGESTION (ClarionParser output / what symbols we store) changes, so existing cached
        // DBs built by an older parser are treated as stale and auto-rebuilt (LibSrc mtimes alone can't detect
        // a parser change). v2: capture CLASS data members (dotted "Class.Member"); member queries dotted-only.
        private const int ParserVersion = 2;

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
        /// Full Clarion build version key (e.g. "12.0.0.14000"), read from the running IDE exe's
        /// PRODUCT version. Returns null if it can't be determined (e.g. running outside the IDE).
        ///
        /// Uses all FOUR parts of the product version on purpose: Clarion stamps the build
        /// differentiator in the PRIVATE part (Clarion.exe reports 12.0.0.14000), so the old
        /// 3-part key "{Major}.{Minor}.{Build}" resolved to "12.0.0" and COLLIDED across every
        /// 12.0 patch build — they'd all share one cache DB. Including the private part keys each
        /// build to its own DB (ticket 6e8f2439 item 7). NOTE: this changes the cache filename, so
        /// the previously-built ClarionGraph_12.0.0.db is orphaned and rebuilt once under the new key.
        ///
        /// MEMOIZED with a short TTL: the uncached resolve runs ClarionVersionService.Detect() (a
        /// ClarionProperties.xml parse + ICSharpCode.Core reflection), and this is called on the hot path
        /// (every completion keystroke via ResolveDbPath, and the 3s build heartbeat). Caching the key for a
        /// few seconds removes that per-call XML-parse/reflection cost; a Clarion version switch is picked up
        /// within the TTL. Failures (null) are not cached, so detection keeps retrying until it succeeds.
        /// </summary>
        public static string ResolveVersionKey()
        {
            lock (_versionCacheLock)
            {
                if (_cachedVersion != null &&
                    (DateTime.UtcNow.Ticks - _cachedVersionAtTicks) < _versionCacheTtl.Ticks)
                    return _cachedVersion;
            }
            string v = ResolveVersionKeyUncached();
            if (v != null)
                lock (_versionCacheLock) { _cachedVersion = v; _cachedVersionAtTicks = DateTime.UtcNow.Ticks; }
            return v;
        }

        private static readonly object _versionCacheLock = new object();
        private static string _cachedVersion;
        private static long _cachedVersionAtTicks;
        private static readonly TimeSpan _versionCacheTtl = TimeSpan.FromSeconds(20);

        private static string ResolveVersionKeyUncached()
        {
            try
            {
                var info = ClarionVersionService.Detect();
                string exePath = info?.ClarionExePath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    return null;

                var vi = FileVersionInfo.GetVersionInfo(exePath);
                // Reject only when BOTH versions are unpopulated — an install with a present FileVersion but
                // absent ProductVersion must still key (gating on ProductMajorPart alone would make the
                // FileVersion fallback below unreachable and the graph would never build).
                if (vi.ProductMajorPart <= 0 && vi.FileMajorPart <= 0)
                    return null;

                int maj, min, bld, pri;
                if (vi.ProductMajorPart > 0)
                {
                    maj = vi.ProductMajorPart; min = vi.ProductMinorPart;
                    bld = vi.ProductBuildPart; pri = vi.ProductPrivatePart;

                    // Collapse guard: the build differentiator normally lives in the PRIVATE part
                    // (12.0.0.14000). If an install reports a short/non-numeric ProductVersion, FileVersionInfo
                    // returns 0 for the unparsed parts → the key would collapse to "12.0.0.0" and re-COLLIDE
                    // across builds (the very bug this fixes). When Product build+private are both 0, fall back
                    // to the FileVersion parts, then to the raw ProductVersion string.
                    if (bld == 0 && pri == 0)
                    {
                        if (vi.FileBuildPart != 0 || vi.FilePrivatePart != 0)
                        {
                            maj = vi.FileMajorPart; min = vi.FileMinorPart;
                            bld = vi.FileBuildPart; pri = vi.FilePrivatePart;
                        }
                        else if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
                        {
                            return vi.ProductVersion.Trim();   // raw string; SanitizeForFileName handles the filename
                        }
                    }
                }
                else
                {
                    // ProductVersion absent but FileVersion present → key off FileVersion.
                    maj = vi.FileMajorPart; min = vi.FileMinorPart;
                    bld = vi.FileBuildPart; pri = vi.FilePrivatePart;
                }

                return string.Format("{0}.{1}.{2}.{3}", maj, min, bld, pri);
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
                        db.SetMetadata("parser_version", ParserVersion.ToString());
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

        // ===== Background build (item 7) =====

        private static readonly object _bgLock = new object();
        private static bool _bgBuilding;
        private static string _bgEnsuredVersion;   // version whose DB we've ensured (built or confirmed fresh) this session
        private static string _bgFailedVersion;    // version whose last bg build FAILED (for cooldown backoff)
        private static long _bgFailedAtTicks;       // DateTime.UtcNow.Ticks of that failure
        private static readonly TimeSpan _bgFailureCooldown = TimeSpan.FromMinutes(5);

        /// <summary>True while a background build is in flight (for the settings "Building…" status).</summary>
        public static bool IsBuilding { get { lock (_bgLock) { return _bgBuilding; } } }

        /// <summary>
        /// Fire-and-forget build-on-detect: ensures the CURRENT version's DB exists and is fresh, on a
        /// background thread, AT MOST ONCE per version per session. Builds when the DB is ABSENT, and
        /// force-rebuilds ONCE when it's STALE (a LibSrc file changed after built_at) — otherwise the
        /// "stale" status would be unactionable. No-op while a build is in flight, after this version is
        /// ensured, or within the failure-cooldown after a failed build. Safe to call repeatedly (startup,
        /// every poll, with or without a solution open) — it self-guards. <paramref name="onComplete"/> runs
        /// on the worker thread only when a build actually ran. Never throws. Until the DB lands,
        /// completion/definition/hover degrade gracefully to LSP-only (they File.Exists-guard the DB).
        /// </summary>
        public static void EnsureBuiltInBackground(Action<ClarionGraphResult> onComplete = null)
        {
            try
            {
                string version = ResolveVersionKey();
                if (string.IsNullOrEmpty(version)) return;   // no IDE/version → nothing to build

                // Fast pre-check: skip the DB I/O below once this version is ensured, a build is running, or
                // we're inside the post-failure cooldown (don't even open the DB for staleness during cooldown).
                lock (_bgLock)
                {
                    if (_bgBuilding) return;
                    if (string.Equals(_bgEnsuredVersion, version, StringComparison.OrdinalIgnoreCase)) return;
                    if (InFailureCooldown(version)) return;
                }

                // DB existence + staleness (DB reads) computed OUTSIDE the lock.
                string dbPath = ResolveDbPath(version);
                bool dbExists = !string.IsNullOrEmpty(dbPath) && File.Exists(dbPath);
                bool stale = dbExists && IsDbStale(dbPath);

                lock (_bgLock)
                {
                    if (_bgBuilding) return;                                       // another thread won the race
                    if (string.Equals(_bgEnsuredVersion, version, StringComparison.OrdinalIgnoreCase)) return;
                    if (dbExists && !stale) { _bgEnsuredVersion = version; return; } // fresh cached DB → done
                    if (InFailureCooldown(version)) return;                         // recent failure → back off
                    _bgBuilding = true;
                }

                bool force = stale;   // stale → force rebuild; absent → normal (DB missing) build
                var t = new System.Threading.Thread(() =>
                {
                    ClarionGraphResult result = null;
                    try { result = EnsureBuilt(force); }
                    catch { }
                    finally
                    {
                        lock (_bgLock)
                        {
                            _bgBuilding = false;
                            if (result != null && result.Success)
                            {
                                _bgEnsuredVersion = result.Version;
                                _bgFailedVersion = null;                 // clear any prior failure
                            }
                            else
                            {
                                _bgFailedVersion = version;              // start the cooldown window
                                _bgFailedAtTicks = DateTime.UtcNow.Ticks;
                            }
                        }
                    }
                    try { if (onComplete != null) onComplete(result); } catch { }
                })
                { IsBackground = true, Name = "ClarionGraphBuild" };
                t.Start();
            }
            catch { }
        }

        /// <summary>True when the last bg build for <paramref name="version"/> failed within the cooldown
        /// window — avoids re-spawning a fast-failing build every poll (e.g. LibSrc transiently missing).
        /// Call under <see cref="_bgLock"/>.</summary>
        private static bool InFailureCooldown(string version)
        {
            if (!string.Equals(_bgFailedVersion, version, StringComparison.OrdinalIgnoreCase)) return false;
            return (DateTime.UtcNow.Ticks - _bgFailedAtTicks) < _bgFailureCooldown.Ticks;
        }

        /// <summary>Staleness check for an explicit DB path (reads its built_at + libsrc_root metadata and
        /// compares against LibSrc write times). False on any read failure. Never throws.</summary>
        private static bool IsDbStale(string dbPath)
        {
            try
            {
                string libsrc = ReadMetadataValue(dbPath, "libsrc_root");
                string builtAtRaw = ReadMetadataValue(dbPath, "built_at");
                DateTime builtAt;
                if (!DateTime.TryParse(builtAtRaw, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out builtAt))
                    return IsParserOutdated(dbPath);   // can't read built_at, but a parser bump still forces rebuild
                return IsStale(libsrc, builtAt) || IsParserOutdated(dbPath);
            }
            catch { return false; }
        }

        /// <summary>True when the DB was built by an older ingestion parser than the current
        /// <see cref="ParserVersion"/> (or has no parser_version stamp) → it should be rebuilt. Never throws.</summary>
        private static bool IsParserOutdated(string dbPath)
        {
            try
            {
                string raw = ReadMetadataValue(dbPath, "parser_version");
                int stored;
                if (!int.TryParse(raw, out stored)) return true;   // unstamped (built before parser versioning)
                return stored < ParserVersion;
            }
            catch { return false; }
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
                    // A background build-on-first-detect may be in flight for this version.
                    status.State = IsBuilding ? "building" : "absent";
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

                // Stale if a LibSrc file changed OR the DB was built by an older ingestion parser.
                status.State = (IsStale(status.LibSrcRoot, status.BuiltAt) || IsParserOutdated(dbPath))
                    ? "stale" : "built";
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
                case "absent":   return "Not built" + (s.Version != null ? " (v" + s.Version + ")" : "");
                case "building": return "Building…" + (s.Version != null ? " (v" + s.Version + ")" : "");
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
