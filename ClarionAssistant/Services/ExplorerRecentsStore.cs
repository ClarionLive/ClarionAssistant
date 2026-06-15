using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Solution-scoped persistence for the Explorer panel's recents / last-folder / pinned lists.
    /// Mirrors <see cref="ModernEmbeditorHistory"/>: one JSON file per Clarion-version + solution at
    ///   %APPDATA%\ClarionAssistant\&lt;VersionTag&gt;\&lt;SolutionTag&gt;\explorer-recents.json
    /// reusing ModernEmbeditorHistory.VersionTag() / SolutionTag(solutionPath) so the Explorer's
    /// state lands beside find-history.json for the same solution. Serialized with JavaScriptSerializer.
    /// All IO is best-effort: every method swallows exceptions and never throws to callers.
    /// </summary>
    public static class ExplorerRecentsStore
    {
        private const int RecentsCap = 50;
        private const int PinnedFilesCap = 50;

        /// <summary>One recently-opened file with the UTC tick at which it was last opened.</summary>
        public sealed class RecentEntry
        {
            public string Path = "";
            public long Ts;   // DateTime.UtcNow.Ticks
        }

        /// <summary>The full persisted model. Never null after <see cref="LoadRaw"/>.</summary>
        public sealed class Model
        {
            public string LastFolder = "";
            public List<RecentEntry> Recents = new List<RecentEntry>();
            public List<string> PinnedFiles = new List<string>();
            public List<string> PinnedFolders = new List<string>();
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

        // ---- internals -------------------------------------------------------

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
                    { "pinnedFolders", m.PinnedFolders ?? new List<string>() }
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

        private static void Debug(string where, Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ExplorerRecentsStore] " + where + ": " + ex.Message);
        }
    }
}
