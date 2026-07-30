using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Solution-scoped persistence for the Explorer panel's recents / last-folder / pinned lists,
    /// recent compare PAIRS, and the Files tab's view state (scope / extension filter / collapsed groups).
    /// Mirrors <see cref="ModernEmbeditorHistory"/>: one JSON file per Clarion-version + solution at
    ///   %APPDATA%\ClarionAssistant\&lt;VersionTag&gt;\&lt;SolutionTag&gt;\explorer-recents.json
    /// reusing ModernEmbeditorHistory.VersionTag() / SolutionTag(solutionPath) so the Explorer's
    /// state lands beside find-history.json for the same solution. Serialized with JavaScriptSerializer.
    /// All IO is best-effort: every method swallows exceptions and never throws to callers.
    ///
    /// Schema v2 added <c>recentCompares</c> and <c>viewState</c>. Both are backward AND forward
    /// compatible: a v1 file simply loads with an empty compare list and a default view state, and
    /// every v1 key keeps being written, so an older build reading a v2 file is unaffected.
    /// </summary>
    public static class ExplorerRecentsStore
    {
        private const int RecentsCap = 50;
        private const int PinnedFilesCap = 50;

        /// <summary>Cap on UNPINNED compare pairs. Pinned pairs are immune (see <see cref="TrimCompares"/>).</summary>
        private const int RecentComparesCap = 20;

        /// <summary>Cap on pinned compare pairs, mirroring <see cref="PinnedFilesCap"/> for pinned files.</summary>
        private const int PinnedComparesCap = 50;

        /// <summary>
        /// Absolute ceiling on compare pairs accepted from disk, pinned included. The per-kind caps above are
        /// enforced when pairs arrive through the API, but nothing stops a hand-edited or corrupted file from
        /// declaring thousands of PINNED pairs — and every loaded pair costs two File.Exists probes on every
        /// Files-tab render (ExplorerFileClassifier.BuildCompares), so an unbounded list is a UI stall.
        /// </summary>
        private const int TotalComparesCap = RecentComparesCap + PinnedComparesCap;

        /// <summary>Cap on persisted collapsed-group keys — a backstop against an unbounded key list.</summary>
        private const int CollapsedCap = 64;

        /// <summary>One recently-opened file with the UTC tick at which it was last opened.</summary>
        public sealed class RecentEntry
        {
            public string Path = "";
            public long Ts;   // DateTime.UtcNow.Ticks
        }

        /// <summary>
        /// One recently-compared pair of files. A/B carry the order the compare was last RUN in
        /// (so swapping sides and re-running rewrites this entry in place rather than adding a second
        /// one) — identity is the UNORDERED pair, see <see cref="PairKey"/>.
        /// </summary>
        public sealed class ComparePair
        {
            public string A = "";
            public string B = "";
            public long Ts;      // DateTime.UtcNow.Ticks of the last run
            public bool Pinned;
        }

        /// <summary>
        /// The Files tab's cross-session view state. Lived in session-scoped ModernDataPad fields until
        /// v2 of this file, because persisting it meant touching this schema.
        /// </summary>
        public sealed class ViewState
        {
            public string Scope = "";        // "" = the page's own default ("all")
            public string ExtMode = "";      // "" = the page's own default
            public string CustomExt = "";
            public List<string> Collapsed = new List<string>();
        }

        /// <summary>The full persisted model. Never null after <see cref="LoadRaw"/>.</summary>
        public sealed class Model
        {
            public string LastFolder = "";
            public List<RecentEntry> Recents = new List<RecentEntry>();
            public List<string> PinnedFiles = new List<string>();
            public List<string> PinnedFolders = new List<string>();
            public List<ComparePair> RecentCompares = new List<ComparePair>();
            public ViewState View = new ViewState();
        }

        /// <summary>Load the model for the current solution (defaults to an empty model on any error).</summary>
        public static Model LoadRaw()
        {
            var model = new Model();
            try
            {
                string path = FilePath();
                if (!File.Exists(path)) return model;
                var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                if (d == null) return model;

                model.LastFolder = AsString(d, "lastFolder");
                model.PinnedFiles = AsStringList(d, "pinnedFiles", PinnedFilesCap);
                model.PinnedFolders = AsStringList(d, "pinnedFolders", PinnedFilesCap);

                object rv;
                if (d.TryGetValue("recents", out rv) && rv is object[])
                {
                    foreach (var item in (object[])rv)
                    {
                        var rd = item as Dictionary<string, object>;
                        if (rd == null) continue;
                        string p = AsString(rd, "path");
                        if (string.IsNullOrEmpty(p)) continue;
                        model.Recents.Add(new RecentEntry { Path = p, Ts = AsLong(rd, "ts") });
                        if (model.Recents.Count >= RecentsCap) break;
                    }
                }

                object cv;
                if (d.TryGetValue("recentCompares", out cv) && cv is object[])
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in (object[])cv)
                    {
                        var cd = item as Dictionary<string, object>;
                        if (cd == null) continue;
                        string a = AsString(cd, "a");
                        string b = AsString(cd, "b");
                        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) continue;
                        string key = PairKey(a, b);
                        if (key == null || !seen.Add(key)) continue;   // drop unresolvable + duplicate pairs
                        model.RecentCompares.Add(new ComparePair
                        {
                            A = a,
                            B = b,
                            Ts = AsLong(cd, "ts"),
                            Pinned = AsBool(cd, "pinned")
                        });
                        // Absolute ceiling, pinned included — a hand-edited file could otherwise declare an
                        // unbounded number of PINNED pairs, which TrimCompares (unpinned-only, by design) would
                        // happily keep and the classifier would File.Exists on every render.
                        if (model.RecentCompares.Count >= TotalComparesCap) break;
                    }
                    // Then trim by the per-kind rule. Done AFTER the loop rather than breaking at
                    // RecentComparesCap: breaking there would drop PINNED pairs sitting past it, and pinned
                    // pairs are supposed to be immune to the unpinned cap.
                    TrimCompares(model.RecentCompares);
                }

                var vd = Get(d, "viewState") as Dictionary<string, object>;
                if (vd != null)
                {
                    model.View.Scope = AsString(vd, "scope");
                    model.View.ExtMode = AsString(vd, "extMode");
                    model.View.CustomExt = AsString(vd, "customExt");
                    model.View.Collapsed = AsPlainStringList(vd, "collapsed");
                }
            }
            catch (Exception ex) { Debug("LoadRaw", ex); }
            return model;
        }

        public static string GetLastFolder()
        {
            try { return LoadRaw().LastFolder ?? ""; }
            catch { return ""; }
        }

        public static void SetLastFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                var m = LoadRaw();
                m.LastFolder = dir;
                Save(m);
            }
            catch (Exception ex) { Debug("SetLastFolder", ex); }
        }

        /// <summary>Record a file open: dedup by normalized full path, move-to-front, stamp ts=now, cap at 50.</summary>
        public static void RecordOpen(string path) { RecordOpen(path, null); }

        /// <summary>
        /// Record a file open AND set the last folder in a single load+save cycle (the common path from
        /// <see cref="MonacoFileOpener"/>). Pass <paramref name="dir"/> null to leave the last folder unchanged.
        /// </summary>
        public static void RecordOpen(string path, string dir)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string norm = Normalize(path);
                if (norm == null) return;

                var m = LoadRaw();
                m.Recents.RemoveAll(r => SamePath(r.Path, norm));
                m.Recents.Insert(0, new RecentEntry { Path = path, Ts = DateTime.UtcNow.Ticks });
                if (m.Recents.Count > RecentsCap) m.Recents.RemoveRange(RecentsCap, m.Recents.Count - RecentsCap);
                if (!string.IsNullOrEmpty(dir)) m.LastFolder = dir;
                Save(m);
            }
            catch (Exception ex) { Debug("RecordOpen", ex); }
        }

        public static void RemoveRecent(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string norm = Normalize(path);
                if (norm == null) return;
                var m = LoadRaw();
                m.Recents.RemoveAll(r => SamePath(r.Path, norm));
                Save(m);
            }
            catch (Exception ex) { Debug("RemoveRecent", ex); }
        }

        public static void Pin(string path) { PinInto(path, m => m.PinnedFiles, PinnedFilesCap); }
        public static void Unpin(string path) { UnpinFrom(path, m => m.PinnedFiles); }
        public static void PinFolder(string dir) { PinInto(dir, m => m.PinnedFolders, PinnedFilesCap); }
        public static void UnpinFolder(string dir) { UnpinFrom(dir, m => m.PinnedFolders); }

        // ---- compare pairs ---------------------------------------------------

        /// <summary>The recent compare pairs, most-recently-run first. Never null.</summary>
        public static List<ComparePair> GetCompares()
        {
            try { return LoadRaw().RecentCompares ?? new List<ComparePair>(); }
            catch { return new List<ComparePair>(); }
        }

        /// <summary>
        /// Record a compare run: dedup on the normalized UNORDERED pair, move-to-front, stamp ts=now,
        /// and store the INCOMING side order (so re-running A/B swapped rewrites the one entry rather
        /// than creating a mirror of it). An existing entry's pinned flag is carried forward.
        /// The cap applies to unpinned pairs only.
        /// </summary>
        public static void RecordCompare(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
            try
            {
                string key = PairKey(a, b);
                if (key == null) return;

                var m = LoadRaw();
                bool pinned = false;
                int at = IndexOfPair(m.RecentCompares, key);
                if (at >= 0)
                {
                    pinned = m.RecentCompares[at].Pinned;   // carry the pin forward across the re-run
                    m.RecentCompares.RemoveAt(at);
                }
                m.RecentCompares.Insert(0, new ComparePair
                {
                    A = a,
                    B = b,
                    Ts = DateTime.UtcNow.Ticks,
                    Pinned = pinned
                });
                TrimCompares(m.RecentCompares);
                Save(m);
            }
            catch (Exception ex) { Debug("RecordCompare", ex); }
        }

        public static void PinCompare(string a, string b) { SetComparePinned(a, b, true); }
        public static void UnpinCompare(string a, string b) { SetComparePinned(a, b, false); }

        /// <summary>Forget a compare pair entirely, pinned or not.</summary>
        public static void RemoveCompare(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
            try
            {
                string key = PairKey(a, b);
                if (key == null) return;
                var m = LoadRaw();
                int at = IndexOfPair(m.RecentCompares, key);
                if (at < 0) return;
                m.RecentCompares.RemoveAt(at);
                Save(m);
            }
            catch (Exception ex) { Debug("RemoveCompare", ex); }
        }

        // ---- Files tab view state --------------------------------------------

        /// <summary>The persisted Files-tab view state. Never null; empty strings mean "page default".</summary>
        public static ViewState GetViewState()
        {
            try { return LoadRaw().View ?? new ViewState(); }
            catch { return new ViewState(); }
        }

        /// <summary>
        /// Write through the Files-tab view state. Null arguments leave that facet unchanged, so the page
        /// can persist just the bit the user touched.
        /// </summary>
        public static void SaveViewState(string scope, string extMode, string customExt, List<string> collapsed)
        {
            try
            {
                var m = LoadRaw();
                if (m.View == null) m.View = new ViewState();
                if (scope != null) m.View.Scope = scope;
                if (extMode != null) m.View.ExtMode = extMode;
                if (customExt != null) m.View.CustomExt = customExt;
                if (collapsed != null)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    m.View.Collapsed = collapsed
                        .Where(k => !string.IsNullOrEmpty(k) && seen.Add(k))
                        .Take(CollapsedCap)
                        .ToList();
                }
                Save(m);
            }
            catch (Exception ex) { Debug("SaveViewState", ex); }
        }

        // ---- internals -------------------------------------------------------

        private static void SetComparePinned(string a, string b, bool pinned)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
            try
            {
                string key = PairKey(a, b);
                if (key == null) return;
                var m = LoadRaw();
                int at = IndexOfPair(m.RecentCompares, key);
                if (at < 0) return;
                if (m.RecentCompares[at].Pinned == pinned) return;
                if (pinned && m.RecentCompares.Count(c => c.Pinned) >= PinnedComparesCap) return;
                var entry = m.RecentCompares[at];
                entry.Pinned = pinned;
                if (!pinned)
                {
                    // Unpinning must DEMOTE the pair to a recent, never destroy it — ✕ is the destructive
                    // affordance, ★ is not. A pair pinned long ago sits deep in insertion order, so simply
                    // re-trimming would find it past the unpinned cap and delete it outright: pin a pair, run 20
                    // more compares, unpin, and it silently vanishes. Move it to the front so the eviction lands
                    // on a genuinely unwanted entry instead.
                    //
                    // Ts is deliberately NOT restamped. Restamping would make the row claim it was compared "just
                    // now", falsifying the one fact the timestamp exists to report — so the pair survives at the
                    // head of the list while still DISPLAYING its true age (the classifier orders by Ts). The cost
                    // is that this one trim can evict an entry slightly newer than the moved pair; keeping a pair
                    // the user cared enough to pin is the better trade.
                    m.RecentCompares.RemoveAt(at);
                    m.RecentCompares.Insert(0, entry);
                    TrimCompares(m.RecentCompares);
                }
                Save(m);
            }
            catch (Exception ex) { Debug("SetComparePinned", ex); }
        }

        /// <summary>
        /// Identity key for a compare pair: both sides resolved to full paths, then sorted
        /// case-insensitively and joined — so A/B and B/A are ONE entry. Null if either side
        /// can't be resolved.
        /// </summary>
        private static string PairKey(string a, string b)
        {
            string na = Normalize(a), nb = Normalize(b);
            if (na == null || nb == null) return null;
            return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase) <= 0
                ? na + "|" + nb
                : nb + "|" + na;
        }

        private static int IndexOfPair(List<ComparePair> list, string key)
        {
            for (int i = 0; i < list.Count; i++)
            {
                string k = PairKey(list[i].A, list[i].B);
                if (k != null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        /// <summary>
        /// Drop the oldest UNPINNED pairs past <see cref="RecentComparesCap"/>, in place, preserving order.
        /// Pinned pairs never count toward the cap and are never dropped by it — that is what stops a burst
        /// of casual class-row compares from evicting a deliberately pinned pair.
        /// </summary>
        private static void TrimCompares(List<ComparePair> list)
        {
            if (list == null) return;
            int unpinned = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Pinned) continue;
                if (++unpinned > RecentComparesCap) { list.RemoveAt(i); i--; unpinned--; }
            }
        }

        private static void PinInto(string value, Func<Model, List<string>> pick, int cap)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                string norm = Normalize(value);
                if (norm == null) return;
                var m = LoadRaw();
                var list = pick(m);
                if (list.Any(p => SamePath(p, norm))) return;   // already pinned
                if (list.Count >= cap) return;
                list.Add(value);
                Save(m);
            }
            catch (Exception ex) { Debug("Pin", ex); }
        }

        private static void UnpinFrom(string value, Func<Model, List<string>> pick)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                string norm = Normalize(value);
                if (norm == null) return;
                var m = LoadRaw();
                pick(m).RemoveAll(p => SamePath(p, norm));
                Save(m);
            }
            catch (Exception ex) { Debug("Unpin", ex); }
        }

        private static void Save(Model m)
        {
            try
            {
                string path = FilePath();
                var payload = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "lastFolder", m.LastFolder ?? "" },
                    { "recents", m.Recents.Select(r => new Dictionary<string, object>
                        {
                            { "path", r.Path ?? "" },
                            { "ts", r.Ts }
                        }).ToList() },
                    { "pinnedFiles", m.PinnedFiles ?? new List<string>() },
                    { "pinnedFolders", m.PinnedFolders ?? new List<string>() },
                    // v2 keys. The v1 keys above are still written unconditionally, so an older
                    // build reading this file behaves exactly as it did before.
                    { "recentCompares", (m.RecentCompares ?? new List<ComparePair>())
                        .Select(c => new Dictionary<string, object>
                        {
                            { "a", c.A ?? "" },
                            { "b", c.B ?? "" },
                            { "ts", c.Ts },
                            { "pinned", c.Pinned }
                        }).ToList() },
                    { "viewState", new Dictionary<string, object>
                        {
                            { "scope", (m.View != null ? m.View.Scope : null) ?? "" },
                            { "extMode", (m.View != null ? m.View.ExtMode : null) ?? "" },
                            { "customExt", (m.View != null ? m.View.CustomExt : null) ?? "" },
                            { "collapsed", (m.View != null ? m.View.Collapsed : null) ?? new List<string>() }
                        } }
                });
                File.WriteAllText(path, payload, Encoding.UTF8);
            }
            catch (Exception ex) { Debug("Save", ex); }
        }

        /// <summary>Absolute path to the version+solution recents file (folders created on demand).</summary>
        private static string FilePath()
        {
            string solution = null;
            try { solution = EditorService.GetOpenSolutionPath(); }
            catch { }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant",
                ModernEmbeditorHistory.VersionTag(),
                ModernEmbeditorHistory.SolutionTag(solution));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "explorer-recents.json");
        }

        /// <summary>Normalized comparison key (resolved full path; compared case-insensitively by callers).
        /// Null if the path can't be resolved.</summary>
        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return null; }
        }

        private static bool SamePath(string a, string b)
        {
            string na = Normalize(a);
            if (na == null) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            return string.Equals(na, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string AsString(IDictionary<string, object> d, string key)
        {
            object o;
            return (d.TryGetValue(key, out o) && o != null) ? o.ToString() : "";
        }

        private static object Get(IDictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) ? o : null;
        }

        private static bool AsBool(IDictionary<string, object> d, string key)
        {
            object o;
            if (d.TryGetValue(key, out o) && o != null)
            {
                if (o is bool) return (bool)o;
                bool b;
                if (bool.TryParse(o.ToString(), out b)) return b;
            }
            return false;
        }

        private static long AsLong(IDictionary<string, object> d, string key)
        {
            object o;
            if (d.TryGetValue(key, out o) && o != null)
            {
                try { return Convert.ToInt64(o); } catch { }
            }
            return 0;
        }

        private static List<string> AsStringList(IDictionary<string, object> d, string key, int cap)
        {
            var outp = new List<string>();
            object o;
            if (d.TryGetValue(key, out o) && o is object[])
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in (object[])o)
                {
                    if (item == null) continue;
                    string s = item.ToString();
                    if (string.IsNullOrEmpty(s)) continue;
                    string key2 = Normalize(s) ?? s;
                    if (!seen.Add(key2)) continue;   // drop duplicate pins
                    outp.Add(s);
                    if (outp.Count >= cap) break;
                }
            }
            return outp;
        }

        /// <summary>
        /// Like <see cref="AsStringList"/> but WITHOUT path normalization — for lists whose members are
        /// opaque keys (collapsed group ids), not file paths.
        /// </summary>
        private static List<string> AsPlainStringList(IDictionary<string, object> d, string key)
        {
            var outp = new List<string>();
            object o;
            if (d.TryGetValue(key, out o) && o is object[])
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in (object[])o)
                {
                    if (item == null) continue;
                    string s = item.ToString();
                    if (string.IsNullOrEmpty(s) || !seen.Add(s)) continue;
                    outp.Add(s);
                    if (outp.Count >= CollapsedCap) break;
                }
            }
            return outp;
        }

        private static void Debug(string where, Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ExplorerRecentsStore] " + where + ": " + ex.Message);
        }
    }
}
