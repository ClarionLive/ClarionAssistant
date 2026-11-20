using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>What <see cref="SessionTranscriptExporter.Export"/> did with one session.</summary>
    public sealed class SessionExportResult
    {
        public string SessionId;
        /// <summary>The Markdown file written; null when nothing was written.</summary>
        public string Path;
        /// <summary>Why nothing was written (no transcript, no prompt of the developer's), or the error text.</summary>
        public string Reason;
        public bool IsError;
    }

    /// <summary>The Claude sessions one CA tab ran, and the folder they were run in.</summary>
    public sealed class SessionExportRequest
    {
        public string WorkDir;
        public readonly List<string> Ids = new List<string>();
    }

    /// <summary>
    /// Writes a Claude Code session as a Markdown file in the solution folder, so a conversation can be
    /// read in an editor afterwards without resuming it or asking for a summary.
    ///
    /// A transcript is ~/.claude/projects/&lt;encoded cwd&gt;/&lt;session-guid&gt;.jsonl, one JSON object per
    /// line, and Claude Code appends to it as the conversation goes, so it can be read after claude has
    /// exited, or been killed with the IDE. This file turns it into: a heading with the session id and
    /// period, then each of the developer's messages (with its time) and Claude's replies, and one line
    /// for each tool Claude called. Tool output and Claude's reasoning are left out.
    ///
    /// Files go to &lt;solution folder&gt;\.CAChats\session_&lt;id8&gt;_&lt;first day&gt;_to_&lt;last day&gt;.md. A
    /// session exported again after it grew into another day gets a new name, and the file the earlier export
    /// wrote for the same session is removed, so one session is one file. A .gitignore holding * is written
    /// into the folder: a transcript holds everything typed or shown in the session, secrets included, and
    /// must not be committed by accident.
    ///
    /// No IDE coupling and C# 7.3, like <see cref="ClaudeSessionCatalog"/>, so the harness in
    /// tests\SessionTranscriptExporter.Test.cs compiles this file as it is. Never throws.
    /// </summary>
    public static class SessionTranscriptExporter
    {
        public const string FolderName = ".CAChats";
        /// <summary>One message is cut here, so a pasted log of many MB cannot make the file unreadable.</summary>
        internal const int MaxMessageChars = 100000;
        private const string IgnoreFileText = "# Clarion Assistant chat transcripts hold everything typed or shown in the chats, secrets included.\r\n# Keep them out of git.\r\n*\r\n";

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>The folder a session run in <paramref name="workDir"/> is exported to.</summary>
        public static string FolderFor(string workDir)
        {
            return Path.Combine(workDir, FolderName);
        }

        /// <summary>Exports every session in every request. A session listed twice is written once. Never throws.</summary>
        public static List<SessionExportResult> ExportAll(string projectsRoot, IEnumerable<SessionExportRequest> requests)
        {
            var results = new List<SessionExportResult>();
            if (requests == null) return results;
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var req in requests)
            {
                if (req == null) continue;
                foreach (var id in req.Ids)
                {
                    string key = (ClaudeSessionCatalog.NormalizeId(id) ?? id ?? "") + "|" + (req.WorkDir ?? "");
                    if (!done.Add(key)) continue;
                    results.Add(Export(projectsRoot, req.WorkDir, id));
                }
            }
            return results;
        }

        /// <summary>
        /// Writes one session's Markdown file. Nothing is written (and Reason says why) when the session has no
        /// transcript or the developer never typed a prompt in it (a tab that only ever got CA's greeting).
        /// </summary>
        public static SessionExportResult Export(string projectsRoot, string workDir, string sessionId)
        {
            var res = new SessionExportResult { SessionId = sessionId };
            try
            {
                string id = ClaudeSessionCatalog.NormalizeId(sessionId);
                if (id == null) { res.Reason = "not a session id"; return res; }
                if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir)) { res.Reason = "work folder not found"; return res; }
                workDir = Path.GetFullPath(workDir.Trim()).TrimEnd('\\', '/');

                string transcript = FindTranscript(projectsRoot, workDir, id);
                if (transcript == null) { res.Reason = "no transcript"; return res; }

                string agentName;
                DateTime first, last;
                int prompts;
                string body = Render(transcript, out agentName, out first, out last, out prompts);
                if (prompts == 0) { res.Reason = "no prompt from the developer"; return res; }

                var sb = new StringBuilder();
                sb.Append("# Chat transcript - ").Append(Path.GetFileName(workDir)).Append("\r\n\r\n");
                sb.Append("- Session: `").Append(id).Append("`\r\n");
                if (!string.IsNullOrEmpty(agentName)) sb.Append("- Tab: ").Append(agentName).Append("\r\n");
                sb.Append("- From: ").Append(Stamp(first)).Append("   To: ").Append(Stamp(last)).Append("  (local time)\r\n\r\n---\r\n");
                sb.Append(body);

                string folder = FolderFor(workDir);
                Directory.CreateDirectory(folder);
                string ignore = Path.Combine(folder, ".gitignore");
                if (!File.Exists(ignore)) File.WriteAllText(ignore, IgnoreFileText, new UTF8Encoding(false));

                string name = FileName(id, first, last);
                string target = Path.Combine(folder, name);
                RemoveOlderFilesOf(folder, id, name);

                string tmp = target + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                res.Path = target;
                return res;
            }
            catch (Exception ex)
            {
                res.IsError = true;
                res.Reason = ex.GetType().Name + ": " + ex.Message;
                return res;
            }
        }

        /// <summary>session_&lt;first 8 of the id&gt;_&lt;first day&gt;_to_&lt;last day&gt;.md</summary>
        public static string FileName(string id, DateTime first, DateTime last)
        {
            return "session_" + id.Substring(0, 8) + "_" + Day(first) + "_to_" + Day(last) + ".md";
        }

        private static string Day(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        private static string Stamp(DateTime d) { return d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }

        private static string FindTranscript(string projectsRoot, string workDir, string id)
        {
            foreach (var dir in ClaudeSessionCatalog.FoldersFor(projectsRoot, workDir))
            {
                string path = Path.Combine(dir, id + ".jsonl");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // The file an earlier export of this session wrote under another end date. Only names this class
        // produces for this id are touched, never anything else in the folder.
        private static void RemoveOlderFilesOf(string folder, string id, string keepName)
        {
            string id8 = id.Substring(0, 8);
            var ours = new Regex("^session_" + id8 + @"_\d{4}-\d{2}-\d{2}_to_\d{4}-\d{2}-\d{2}\.md$", RegexOptions.IgnoreCase);
            foreach (var f in Directory.GetFiles(folder, "session_" + id8 + "_*.md"))
            {
                string n = Path.GetFileName(f);
                if (ours.IsMatch(n) && !string.Equals(n, keepName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }

        // ── The transcript ───────────────────────────────────

        private static string Render(string path, out string agentName, out DateTime first, out DateTime last, out int prompts)
        {
            agentName = null;
            DateTime? firstTs = null, lastTs = null;
            prompts = 0;
            var sb = new StringBuilder();
            string lastRole = null;
            bool lastWasCall = false;   // consecutive tool lines form one list, with no blank line between them
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] != '{') continue;
                    bool user = line.Contains("\"type\":\"user\"");
                    bool asst = line.Contains("\"type\":\"assistant\"");
                    bool name = line.Contains("\"agent-name\"");
                    if (!user && !asst && !name) continue;

                    IDictionary<string, object> obj;
                    try { obj = ser.DeserializeObject(line) as IDictionary<string, object>; }
                    catch { continue; }
                    if (obj == null) continue;

                    string type = Str(obj, "type");
                    if (type == "agent-name")
                    {
                        string an = Str(obj, "agentName");
                        if (!string.IsNullOrWhiteSpace(an)) agentName = an.Trim();
                        continue;
                    }
                    if (IsTrue(obj, "isSidechain")) continue;   // a sub-agent's own conversation

                    DateTime? at = Time(Str(obj, "timestamp"));
                    if (type == "user")
                    {
                        string text = UserText(obj);
                        if (text == null) continue;
                        prompts++;
                        Note(ref firstTs, ref lastTs, at);
                        sb.Append("\r\n### USER").Append(at.HasValue ? " - " + Stamp(at.Value) : "").Append("\r\n\r\n");
                        sb.Append(Cut(text)).Append("\r\n");
                        lastRole = "user";
                        lastWasCall = false;
                    }
                    else if (type == "assistant")
                    {
                        string text; var calls = new List<string>();
                        AssistantParts(obj, out text, calls);
                        if (text == null && calls.Count == 0) continue;
                        Note(ref firstTs, ref lastTs, at);
                        if (lastRole != "claude") sb.Append("\r\n### CLAUDE\r\n");
                        lastRole = "claude";
                        if (text != null)
                        {
                            sb.Append("\r\n").Append(Cut(text)).Append("\r\n");
                            lastWasCall = false;
                        }
                        if (calls.Count > 0)
                        {
                            if (!lastWasCall) sb.Append("\r\n");
                            foreach (var c in calls) sb.Append(c).Append("\r\n");
                            lastWasCall = true;
                        }
                    }
                }
            }

            if (firstTs.HasValue) { first = firstTs.Value; last = lastTs.Value; }
            else { first = last = File.GetLastWriteTime(path); }
            return sb.ToString();
        }

        private static void Note(ref DateTime? first, ref DateTime? last, DateTime? at)
        {
            if (!at.HasValue) return;
            if (!first.HasValue || at.Value < first.Value) first = at;
            if (!last.HasValue || at.Value > last.Value) last = at;
        }

        private static DateTime? Time(string ts)
        {
            if (string.IsNullOrEmpty(ts)) return null;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto)) return dto.LocalDateTime;
            return null;
        }

        private static string Cut(string text)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
            text = string.Join("\r\n", lines).Trim();
            if (text.Length > MaxMessageChars)
                text = text.Substring(0, MaxMessageChars) + "\r\n\r\n[... " + (text.Length - MaxMessageChars) + " more characters left out]";
            return text;
        }

        /// <summary>The developer's message in a user line, or null for tool results, meta lines, hook output and CA's greeting.</summary>
        private static string UserText(IDictionary<string, object> obj)
        {
            if (IsTrue(obj, "isMeta")) return null;
            object m;
            var message = obj.TryGetValue("message", out m) ? m as IDictionary<string, object> : null;
            if (message == null) return null;
            object content;
            if (!message.TryGetValue("content", out content)) return null;

            string text;
            if (content is string) text = (string)content;
            else if (content is IEnumerable)
            {
                var sb = new StringBuilder();
                foreach (var b in (IEnumerable)content)
                {
                    var block = b as IDictionary<string, object>;
                    if (block == null) continue;
                    string bt = Str(block, "type");
                    if (bt == "tool_result") return null;
                    if (bt == "text") sb.Append(Str(block, "text")).Append("\n");
                }
                text = sb.ToString();
            }
            else return null;
            return ClaudeSessionCatalog.GenuineFull(text);
        }

        private static void AssistantParts(IDictionary<string, object> obj, out string text, List<string> calls)
        {
            text = null;
            object m;
            var message = obj.TryGetValue("message", out m) ? m as IDictionary<string, object> : null;
            if (message == null) return;
            object content;
            if (!message.TryGetValue("content", out content)) return;

            var sb = new StringBuilder();
            if (content is string) sb.Append((string)content);
            else if (content is IEnumerable)
            {
                foreach (var b in (IEnumerable)content)
                {
                    var block = b as IDictionary<string, object>;
                    if (block == null) continue;
                    string bt = Str(block, "type");
                    if (bt == "text")
                    {
                        if (sb.Length > 0) sb.Append("\n\n");
                        sb.Append(Str(block, "text"));
                    }
                    else if (bt == "tool_use")
                    {
                        object inp;
                        var input = block.TryGetValue("input", out inp) ? inp as IDictionary<string, object> : null;
                        string key = null;
                        if (input != null)
                        {
                            foreach (var k in new[] { "description", "command", "file_path", "pattern", "query", "prompt", "skill" })
                            {
                                string v = Str(input, k);
                                if (!string.IsNullOrWhiteSpace(v)) { key = v; break; }
                            }
                        }
                        key = key == null ? "" : Whitespace.Replace(key, " ").Trim();
                        if (key.Length > 100) key = key.Substring(0, 100);
                        calls.Add("- `[" + (Str(block, "name") ?? "") + "]` " + key);
                    }
                }
            }
            string t = sb.ToString();
            if (!string.IsNullOrWhiteSpace(t)) text = t;
        }

        private static string Str(IDictionary<string, object> obj, string key)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) ? v as string : null;
        }

        private static bool IsTrue(IDictionary<string, object> obj, string key)
        {
            object v;
            return obj.TryGetValue(key, out v) && v is bool && (bool)v;
        }
    }
}
