using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Turns the raw <see cref="ExplorerRecentsStore"/> recents + pinned lists into the grouped
    /// view-model the Explorer panel renders: Classes (.inc+.clw pairs), Templates (.tpl/.tpw),
    /// Source (standalone .clw/.inc/.equ), and Other (everything else). The DTOs here map 1:1 to
    /// the JSON contract that item 6 serializes for modern-data-pad.html — see <see cref="ExplorerViewModel"/>.
    ///
    /// Classification of a single .inc/.clw (does it define / belong to a CLASS, and what's its
    /// partner?) is cached by (fullPath, File.GetLastWriteTimeUtc) so re-renders don't re-parse.
    /// Nothing here throws on a missing file — File.Exists guards everywhere, and the parser already
    /// returns empty for non-existent paths.
    /// </summary>
    public static class ExplorerFileClassifier
    {
        // ---- DTOs (map directly to the JSON contract) ------------------------

        public sealed class QuickLocation
        {
            public string key;
            public string label;
            public string dir;
        }

        public sealed class PinnedFolder
        {
            public string label;
            public string dir;
        }

        public sealed class PinnedFile
        {
            public string name;
            public string ext;
            public string path;
            public string kind;       // "class" | "template" | "source" | "other"
            public string incPath;    // class only
            public string clwPath;    // class only
        }

        public sealed class ClassRow
        {
            public string name;
            public string incPath;
            public string clwPath;
            public string dir;
            public string ts;         // relative, e.g. "4 min ago"
            public bool incExists;
            public bool clwExists;
            public bool pinned;

            // Not serialized — used only for de-dup/ordering during build (the "ts" string is the wire value).
            internal long _tsTicks;
        }

        public sealed class FileRow
        {
            public string name;
            public string ext;
            public string path;
            public string dir;
            public string ts;         // relative
            public bool exists;
            public bool pinned;
        }

        public sealed class Groups
        {
            public List<ClassRow> classes = new List<ClassRow>();
            public List<FileRow> templates = new List<FileRow>();
            public List<FileRow> source = new List<FileRow>();
            public List<FileRow> other = new List<FileRow>();
        }

        public sealed class ExplorerViewModel
        {
            public string lastFolder = "";
            public List<QuickLocation> quickLocations = new List<QuickLocation>();
            public List<PinnedFolder> pinnedFolders = new List<PinnedFolder>();
            public List<PinnedFile> pinned = new List<PinnedFile>();
            public Groups groups = new Groups();
        }

        // ---- classification cache -------------------------------------------

        private sealed class Classification
        {
            public bool IsClass;        // path participates in a class (defines one, or its partner does)
            public string ClassName;    // class name (or base name) when IsClass
            public string IncPath;      // resolved .inc of the pair when IsClass
            public string ClwPath;      // resolved .clw of the pair when IsClass
        }

        /// <summary>Cached classification + the mtimes it depends on. For a .clw classified as a class the
        /// result is derived from the partner .inc, so we track that .inc's mtime separately (PartnerStamp)
        /// and re-validate it on every cache hit — otherwise editing the .inc (changing its CLASS) without
        /// touching the .clw would leave the .clw's grouping/label stale.</summary>
        private sealed class CacheEntry
        {
            public DateTime SelfStamp;      // mtime of the classified path
            public DateTime PartnerStamp;   // mtime of the partner .inc (for a .clw class side); else MinValue
            public Classification Result;
        }

        private static readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();
        private static readonly ClarionClassParser _parser = new ClarionClassParser();

        // ---- public API ------------------------------------------------------

        /// <summary>
        /// Build the grouped view-model from the current store. When <paramref name="includeQuickLocations"/>
        /// is false the quickLocations list is left empty (cheap re-render path that skips version detection).
        /// </summary>
        public static ExplorerViewModel BuildViewModel(bool includeQuickLocations)
        {
            var vm = new ExplorerViewModel();
            ExplorerRecentsStore.Model store;
            try { store = ExplorerRecentsStore.LoadRaw(); }
            catch { store = new ExplorerRecentsStore.Model(); }

            vm.lastFolder = store.LastFolder ?? "";

            var pinnedSet = new HashSet<string>(
                (store.PinnedFiles ?? new List<string>()).Select(NormSafe).Where(s => s != null),
                StringComparer.OrdinalIgnoreCase);

            // Pinned files (flat list, classified individually for kind + class paths).
            foreach (var p in store.PinnedFiles ?? new List<string>())
            {
                if (string.IsNullOrEmpty(p)) continue;
                var c = Classify(p);
                string ext = Ext(p);
                vm.pinned.Add(new PinnedFile
                {
                    // Label class entries by base FILENAME (not the first CLASS name) — an .inc can define
                    // several classes (ABC files do), so the first-class name would mislabel the file.
                    name = c.IsClass ? BaseName(c.IncPath ?? c.ClwPath ?? p) : Path.GetFileName(p),
                    ext = ext,
                    path = p,
                    kind = KindOf(p, c),
                    incPath = c.IsClass ? c.IncPath : null,
                    clwPath = c.IsClass ? c.ClwPath : null
                });
            }

            // Pinned folders.
            foreach (var dir in store.PinnedFolders ?? new List<string>())
            {
                if (string.IsNullOrEmpty(dir)) continue;
                vm.pinnedFolders.Add(new PinnedFolder { label = LeafName(dir), dir = dir });
            }

            // Recents -> groups. Class pairs are folded under a single base-name key so the two
            // sides of a class collapse into one row (keeping the most-recent ts of the pair).
            var classRows = new Dictionary<string, ClassRow>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in store.Recents ?? new List<ExplorerRecentsStore.RecentEntry>())
            {
                if (r == null || string.IsNullOrEmpty(r.Path)) continue;
                string path = r.Path;
                var c = Classify(path);

                // Class pairs are the one special case: fold both sides into a single classRows entry.
                if (c.IsClass)
                {
                    string key = NormSafe(c.IncPath ?? c.ClwPath ?? path) ?? path;
                    ClassRow row;
                    if (!classRows.TryGetValue(key, out row))
                    {
                        row = new ClassRow
                        {
                            // Base filename, not first-CLASS name (multi-class .inc files would mislabel).
                            name = BaseName(c.IncPath ?? c.ClwPath ?? path),
                            incPath = c.IncPath,
                            clwPath = c.ClwPath,
                            dir = Path.GetDirectoryName(c.IncPath ?? c.ClwPath ?? path) ?? "",
                            incExists = !string.IsNullOrEmpty(c.IncPath) && File.Exists(c.IncPath),
                            clwExists = !string.IsNullOrEmpty(c.ClwPath) && File.Exists(c.ClwPath),
                            pinned = IsPinned(pinnedSet, c.IncPath) || IsPinned(pinnedSet, c.ClwPath),
                            _tsTicks = r.Ts
                        };
                        classRows[key] = row;
                    }
                    else if (r.Ts > row._tsTicks)
                    {
                        row._tsTicks = r.Ts;   // keep the most-recent ts of the pair
                    }
                    continue;
                }

                // Everything else is a flat FileRow in one of templates/source/other (same taxonomy as KindOf).
                GroupFor(vm.groups, KindOf(path, c)).Add(FileRowFor(path, r.Ts, pinnedSet));
            }

            foreach (var row in classRows.Values)
            {
                row.ts = RelativeTime(row._tsTicks);
                vm.groups.classes.Add(row);
            }
            // Stable, most-recent-first ordering within each group.
            vm.groups.classes = vm.groups.classes.OrderByDescending(c => c._tsTicks).ToList();

            if (includeQuickLocations)
                vm.quickLocations = BuildQuickLocations();

            return vm;
        }

        /// <summary>Format a UTC-ticks timestamp as a friendly relative string.</summary>
        public static string RelativeTime(long utcTicks)
        {
            if (utcTicks <= 0) return "";
            DateTime when;
            try { when = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime(); }
            catch { return ""; }

            DateTime now = DateTime.Now;
            TimeSpan delta = now - when;

            if (delta.TotalSeconds < 0) return "just now";       // clock skew guard
            if (delta.TotalSeconds < 45) return "just now";
            if (delta.TotalMinutes < 60)
            {
                int m = (int)Math.Round(delta.TotalMinutes);
                return m <= 1 ? "1 min ago" : m + " min ago";
            }
            if (when.Date == now.Date)
            {
                int h = (int)Math.Floor(delta.TotalHours);
                return h <= 1 ? "1 hour ago" : h + " hours ago";
            }
            if (when.Date == now.Date.AddDays(-1)) return "Yesterday";
            if (delta.TotalDays < 7)
            {
                int d = (int)Math.Floor(delta.TotalDays);
                return d + " days ago";
            }
            return when.ToString("yyyy-MM-dd");
        }

        // ---- internals -------------------------------------------------------

        /// <summary>
        /// Classify a single .inc/.clw: does it participate in a CLASS, and what's the resolved pair?
        /// Cached by (fullPath, lastWriteTimeUtc). For a .clw we resolve its partner .inc and re-check
        /// from the .inc side so the cache key + result are consistent regardless of which side opened.
        /// </summary>
        private static Classification Classify(string path)
        {
            string full = NormSafe(path);
            if (full == null) return NotAClass(path);

            DateTime selfStamp = MtimeOf(full);

            lock (_cacheLock)
            {
                CacheEntry hit;
                if (_cache.TryGetValue(full, out hit) && hit.SelfStamp == selfStamp)
                {
                    // For a .clw class the classification is derived from the partner .inc — re-validate the
                    // .inc's mtime so an edit to the .inc invalidates the .clw entry too.
                    string partner = PartnerIncFor(full, hit.Result);
                    if (partner == null || MtimeOf(partner) == hit.PartnerStamp)
                        return hit.Result;
                }
            }

            var result = ClassifyUncached(full);
            string partnerInc = PartnerIncFor(full, result);

            lock (_cacheLock)
            {
                _cache[full] = new CacheEntry
                {
                    SelfStamp = selfStamp,
                    PartnerStamp = partnerInc != null ? MtimeOf(partnerInc) : DateTime.MinValue,
                    Result = result
                };
            }
            return result;
        }

        private static DateTime MtimeOf(string path)
        {
            try { return (!string.IsNullOrEmpty(path) && File.Exists(path)) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>The .inc whose CONTENT a classification depends on (for partner-staleness checks): the
        /// resolved partner .inc when classifying the .clw side of a class; otherwise none.</summary>
        private static string PartnerIncFor(string full, Classification c)
        {
            if (c != null && c.IsClass
                && string.Equals(Ext(full), ".clw", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(c.IncPath)
                && !string.Equals(c.IncPath, full, StringComparison.OrdinalIgnoreCase))
                return c.IncPath;
            return null;
        }

        private static Classification ClassifyUncached(string path)
        {
            string ext = Ext(path);
            try
            {
                if (ext == ".inc")
                {
                    var def = FirstClass(path);
                    if (def == null) return NotAClass(path);
                    return AsClass(path, def, incPath: path, clwPath: _parser.ResolveClwPath(path, def));
                }

                if (ext == ".clw")
                {
                    // Does a partner .inc define a class? Resolve via parser first, then same-basename on disk.
                    string inc = _parser.FindIncFromClw(path);
                    if (string.IsNullOrEmpty(inc))
                    {
                        string sameName = Path.Combine(Path.GetDirectoryName(path) ?? "", BaseName(path) + ".inc");
                        if (File.Exists(sameName)) inc = sameName;
                    }
                    if (!string.IsNullOrEmpty(inc) && File.Exists(inc))
                    {
                        var def = FirstClass(inc);
                        if (def != null) return AsClass(path, def, incPath: inc, clwPath: path);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ExplorerFileClassifier] ClassifyUncached: " + ex.Message);
            }
            return NotAClass(path);
        }

        private static Classification NotAClass(string path)
        {
            return new Classification { IsClass = false, ClassName = BaseName(path) };
        }

        /// <summary>First CLASS defined in an .inc (or null). Returns null for a missing/parse-empty file.</summary>
        private static ClassDefinition FirstClass(string incPath)
        {
            var classes = _parser.ParseIncFile(incPath);   // empty if file missing
            return classes != null ? classes.FirstOrDefault() : null;
        }

        /// <summary>Build a class Classification, falling back to the base name when the parser found no name.</summary>
        private static Classification AsClass(string path, ClassDefinition def, string incPath, string clwPath)
        {
            return new Classification
            {
                IsClass = true,
                ClassName = !string.IsNullOrEmpty(def.ClassName) ? def.ClassName : BaseName(path),
                IncPath = incPath,
                ClwPath = clwPath
            };
        }

        private static FileRow FileRowFor(string path, long ts, HashSet<string> pinnedSet)
        {
            return new FileRow
            {
                name = Path.GetFileName(path),
                ext = Ext(path),
                path = path,
                dir = Path.GetDirectoryName(path) ?? "",
                ts = RelativeTime(ts),
                exists = File.Exists(path),
                pinned = IsPinned(pinnedSet, path)
            };
        }

        private static List<QuickLocation> BuildQuickLocations()
        {
            var list = new List<QuickLocation>();
            try
            {
                string root = null;
                try
                {
                    var cfg = ClarionVersionService.Detect()?.GetCurrentConfig();
                    root = cfg != null ? cfg.RootPath : null;
                }
                catch { }

                if (!string.IsNullOrEmpty(root))
                {
                    string libsrcWin = Path.Combine(root, "LibSrc", "win");
                    if (Directory.Exists(libsrcWin))
                        list.Add(new QuickLocation { key = "libsrc", label = "LibSrc (win)", dir = libsrcWin });

                    string accessory = Path.Combine(root, "accessory");
                    if (Directory.Exists(accessory))
                        list.Add(new QuickLocation { key = "accessory", label = "Accessory", dir = accessory });
                }

                string sln = null;
                try { sln = EditorService.GetOpenSolutionPath(); }
                catch { }
                if (!string.IsNullOrEmpty(sln))
                {
                    string slnDir = Path.GetDirectoryName(sln);
                    if (!string.IsNullOrEmpty(slnDir) && Directory.Exists(slnDir))
                        list.Add(new QuickLocation { key = "solution", label = "Solution", dir = slnDir });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ExplorerFileClassifier] BuildQuickLocations: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// The single file-taxonomy: maps a path (+ its classification) to a group key. Consumed both by the
        /// recents grouping loop and by the pinned-file list so the two never drift.
        /// </summary>
        private static string KindOf(string path, Classification c)
        {
            if (c != null && c.IsClass) return "class";
            string ext = Ext(path);
            if (ext == ".tpl" || ext == ".tpw") return "template";
            if (ext == ".inc" || ext == ".clw" || ext == ".equ") return "source";
            return "other";
        }

        /// <summary>Pick the destination FileRow list for a non-class kind ("template"/"source"/"other").</summary>
        private static List<FileRow> GroupFor(Groups groups, string kind)
        {
            switch (kind)
            {
                case "template": return groups.templates;
                case "source": return groups.source;
                default: return groups.other;
            }
        }

        private static bool IsPinned(HashSet<string> pinnedSet, string path)
        {
            string n = NormSafe(path);
            return n != null && pinnedSet.Contains(n);
        }

        private static string Ext(string path)
        {
            try { return (Path.GetExtension(path) ?? "").ToLowerInvariant(); }
            catch { return ""; }
        }

        private static string BaseName(string path)
        {
            try { return Path.GetFileNameWithoutExtension(path) ?? ""; }
            catch { return ""; }
        }

        private static string LeafName(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            try
            {
                string leaf = Path.GetFileName(dir.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(leaf) ? dir : leaf;
            }
            catch { return dir; }
        }

        private static string NormSafe(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Path.GetFullPath(path); }
            catch { return null; }
        }
    }
}
