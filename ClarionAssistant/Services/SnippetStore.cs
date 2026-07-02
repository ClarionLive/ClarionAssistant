using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// A single Ctrl+J code snippet. Body uses Monaco snippet syntax ($1, ${1:default}, $0) plus the
    /// CA Embeditor's own ${SELECTED} placeholder, substituted client-side with the text that was
    /// selected when the snippet picker was triggered (see monaco-embeditor.html's snippet picker).
    /// </summary>
    public sealed class Snippet
    {
        public string Id;
        public string Trigger;
        public string Description;
        public List<string> Extensions = new List<string>();  // e.g. [".clw"]; empty = applies to all extensions
        public string Body;

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                { "id", Id }, { "trigger", Trigger }, { "description", Description ?? "" },
                { "extensions", Extensions ?? new List<string>() }, { "body", Body ?? "" }
            };
        }

        public static Snippet FromDict(IDictionary<string, object> d)
        {
            var s = new Snippet();
            if (d == null) return s;
            object v;
            if (d.TryGetValue("id", out v) && v != null) s.Id = v.ToString();
            if (d.TryGetValue("trigger", out v) && v != null) s.Trigger = v.ToString();
            if (d.TryGetValue("description", out v) && v != null) s.Description = v.ToString();
            if (d.TryGetValue("body", out v) && v != null) s.Body = v.ToString();
            if (d.TryGetValue("extensions", out v) && v is object[])
            {
                s.Extensions = new List<string>();
                foreach (var e in (object[])v) if (e != null) s.Extensions.Add(e.ToString());
            }
            return s;
        }
    }

    /// <summary>
    /// Global (NOT per-Clarion-version, NOT per-solution) storage for Ctrl+J code snippets:
    /// %APPDATA%\ClarionAssistant\snippets.json. A snippet is reusable Clarion source across every
    /// project, so — unlike ModernEmbeditorHistory's per-solution find/replace lists — there is
    /// exactly one file for the whole install. The list is authoritative on every Save (mirrors
    /// ModernEmbeditorHistory.Save): callers always pass the full set, so a delete sticks.
    /// </summary>
    public static class SnippetStore
    {
        private const int MaxSnippets = 500;
        private const int MaxTriggerLength = 64;
        private const int MaxDescriptionLength = 200;
        private const int MaxBodyLength = 32 * 1024;
        private const int MaxExtensions = 16;
        private const int MaxExtensionLength = 16;

        /// <summary>Load the full list from disk, tolerant of a missing/corrupt file (returns empty).</summary>
        public static List<Snippet> Load()
        {
            var list = new List<Snippet>();
            try
            {
                string path = FilePath();
                if (!File.Exists(path)) return list;
                var arr = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(File.ReadAllText(path)) as object[];
                if (arr == null) return list;
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d != null) list.Add(Snippet.FromDict(d));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SnippetStore] Load: " + ex.Message); }
            return Sanitize(list);
        }

        /// <summary>Persist the full list (authoritative — replaces the file).</summary>
        public static void Save(List<Snippet> all)
        {
            var clean = Sanitize(all);
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var payload = new List<object>();
                foreach (var s in clean) payload.Add(s.ToDict());
                File.WriteAllText(Path.Combine(dir, "snippets.json"),
                    new JavaScriptSerializer().Serialize(payload), Encoding.UTF8);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SnippetStore] Save: " + ex.Message); }
        }

        public static List<Snippet> Add(string trigger, string description, List<string> extensions, string body)
        {
            var all = Load();
            all.Add(new Snippet
            {
                Id = Guid.NewGuid().ToString("N"),
                Trigger = trigger, Description = description,
                Extensions = extensions ?? new List<string>(), Body = body
            });
            Save(all);
            return Load();
        }

        public static List<Snippet> Update(string id, string trigger, string description, List<string> extensions, string body)
        {
            var all = Load();
            foreach (var s in all)
            {
                if (!string.Equals(s.Id, id, StringComparison.Ordinal)) continue;
                s.Trigger = trigger; s.Description = description;
                s.Extensions = extensions ?? new List<string>(); s.Body = body;
                break;
            }
            Save(all);
            return Load();
        }

        public static List<Snippet> Delete(string id)
        {
            var all = Load();
            all.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            Save(all);
            return Load();
        }

        /// <summary>Serialize for the JS bridge (setSource's initial payload + the applySnippets broadcast).</summary>
        public static string ToJson(List<Snippet> list)
        {
            try
            {
                var payload = new List<object>();
                if (list != null) foreach (var s in list) payload.Add(s.ToDict());
                return new JavaScriptSerializer().Serialize(payload);
            }
            catch { return "[]"; }
        }

        private static string FilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant", "snippets.json");
        }

        /// <summary>
        /// Defensive copy/clamp before persisting or handing back to the bridge: drop snippets with no
        /// trigger, reject CR/LF in the trigger (it's used as a JS key / HTML list label, not code),
        /// clamp lengths and counts — mirrors ModernEmbeditorSettings.SanitizeBindings so a
        /// crafted/hand-edited snippets.json can't corrupt the file or balloon it.
        /// </summary>
        private static List<Snippet> Sanitize(List<Snippet> list)
        {
            var outp = new List<Snippet>();
            if (list == null) return outp;
            foreach (var s in list)
            {
                if (outp.Count >= MaxSnippets) break;
                if (s == null || string.IsNullOrWhiteSpace(s.Trigger)) continue;
                string trigger = s.Trigger.Trim();
                if (HasCrLf(trigger)) continue;
                if (trigger.Length > MaxTriggerLength) trigger = trigger.Substring(0, MaxTriggerLength);

                string description = s.Description ?? "";
                if (HasCrLf(description)) description = description.Replace("\r", " ").Replace("\n", " ");
                if (description.Length > MaxDescriptionLength) description = description.Substring(0, MaxDescriptionLength);

                string body = s.Body ?? "";
                if (body.Length > MaxBodyLength) body = body.Substring(0, MaxBodyLength);

                var extensions = new List<string>();
                if (s.Extensions != null)
                {
                    foreach (var e in s.Extensions)
                    {
                        if (string.IsNullOrWhiteSpace(e) || extensions.Count >= MaxExtensions) continue;
                        string ext = e.Trim();
                        if (ext.Length > MaxExtensionLength || HasCrLf(ext)) continue;
                        extensions.Add(ext);
                    }
                }

                outp.Add(new Snippet
                {
                    Id = string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString("N") : s.Id,
                    Trigger = trigger, Description = description, Extensions = extensions, Body = body
                });
            }
            return outp;
        }

        private static bool HasCrLf(string s) { return s != null && (s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0); }
    }
}
