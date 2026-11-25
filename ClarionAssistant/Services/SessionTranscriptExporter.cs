using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
    /// exited, or been killed with the IDE. This file turns it into: a heading with the session id, name and
    /// period, then each of the developer's messages (with its time) and Claude's replies, and one line for
    /// each tool Claude called. Tool output and Claude's reasoning are left out. A message typed while Claude
    /// was working is Claude Code's "queued_command" attachment, not a user record: it is kept and marked.
    ///
    /// Files go to &lt;solution folder&gt;\.CAChats\&lt;first day&gt;_to_&lt;last day&gt;[_&lt;name&gt;]_&lt;id8&gt;.md, the name
    /// being the developer's /rename, when the session has one that is not a plain CA tab name. A session
    /// exported again after it grew into another day, or was renamed, gets a new name, and the file the
    /// earlier export wrote for the same session is removed, so one session is one file. A .gitignore holding
    /// * is written into the folder: a transcript holds everything typed or shown in the session, secrets
    /// included, and must not be committed by accident.
    ///
    /// Pictures: a screenshot pasted into a CA tab is saved by CA as paste-&lt;time&gt;.png in its paste-cache folder
    /// (pruned after 24 h) and sent as "@&lt;path&gt;"; one pasted straight into claude is an image block in the
    /// message. Both are copied to &lt;folder&gt;\&lt;id8&gt;\ and linked from the message, named from the message's
    /// own id, so exporting again finds the copy even after the original is gone, and never loses a link.
    /// An image that cannot be found stays in the text as its path.
    ///
    /// No IDE coupling and C# 7.3, like <see cref="ClaudeSessionCatalog"/>, so the harness in
    /// tests\SessionTranscriptExporter.Test.cs compiles this file as it is. Never throws.
    /// </summary>
    public static class SessionTranscriptExporter
    {
        public const string FolderName = ".CAChats";
        /// <summary>One message is cut here, so a pasted log of many MB cannot make the file unreadable.</summary>
        internal const int MaxMessageChars = 100000;
        /// <summary>The longest part of a chat's name used in a file name.</summary>
        internal const int MaxTitleChars = 60;
        /// <summary>A picture bigger than this is left as its path / left out, so one paste cannot fill the folder.</summary>
        internal const int MaxImageBytes = 25 * 1024 * 1024;
        // The longest line that can carry a picture of MaxImageBytes: its base64 plus the rest of the JSON.
        private const int MaxImageLineChars = MaxImageBytes / 3 * 4 + 1024 * 1024;
        /// <summary>A line of the transcript longer than this (characters) is not parsed unless it carries a picture: a tool's output can be many MB and the add-in is a 32-bit process.</summary>
        internal const int MaxLineChars = 8 * 1024 * 1024;
        private const string IgnoreFileText = "# Clarion Assistant chat transcripts hold everything typed or shown in the chats, secrets included.\r\n# Keep them out of git.\r\n*\r\n";

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        // "@<path>.png" as CA's paste puts it in a message.
        private static readonly Regex PasteToken = new Regex(@"(?<![\w])@(\S+?\.png)(?=\s|$|[,.;:)\]])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // The name CA gives a pasted screenshot, in the cache folder named paste-cache.
        private static readonly Regex PasteFileName = new Regex(@"^paste-\d{8}-\d{6}-\d{3}\.png$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferSize);

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

                string folder = FolderFor(workDir);
                string id8 = id.Substring(0, 8);
                var sink = new ImageSink(id8, Path.Combine(folder, id8));
                Rendered r = Render(transcript, sink);
                if (r.Prompts == 0) { res.Reason = "no prompt from the developer"; return res; }

                var sb = new StringBuilder();
                sb.Append("# Chat transcript - ").Append(Path.GetFileName(workDir)).Append("\r\n\r\n");
                sb.Append("- Session: `").Append(id).Append("`\r\n");
                if (!string.IsNullOrEmpty(r.Title)) sb.Append("- Name: ").Append(r.Title).Append("\r\n");
                if (!string.IsNullOrEmpty(r.TabName)) sb.Append("- Tab: ").Append(r.TabName).Append("\r\n");
                sb.Append("- From: ").Append(Stamp(r.First)).Append("   To: ").Append(Stamp(r.Last)).Append("  (local time)\r\n\r\n---\r\n");
                sb.Append(r.Body);

                Directory.CreateDirectory(folder);
                string ignore = Path.Combine(folder, ".gitignore");
                if (!File.Exists(ignore)) File.WriteAllText(ignore, IgnoreFileText, new UTF8Encoding(false));

                string name = FileName(id, r.First, r.Last, r.Title);
                string target = Path.Combine(folder, name);

                string tmp = target + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                // Only now, with the new file in place: a failed write (disk full, file locked) keeps the earlier export.
                RemoveOlderFilesOf(folder, id, name);
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

        /// <summary>&lt;first day&gt;_to_&lt;last day&gt;[_&lt;name&gt;]_&lt;first 8 of the id&gt;.md; the day the chat started comes first, so the folder sorts by it.</summary>
        public static string FileName(string id, DateTime first, DateTime last, string title)
        {
            string t = SafeTitle(title);
            return Day(first) + "_to_" + Day(last) + (t.Length > 0 ? "_" + t : "") + "_" + id.Substring(0, 8) + ".md";
        }

        /// <summary>The chat's name made fit for a file name: no invalid characters, one space between words, cut to
        /// <see cref="MaxTitleChars"/>, no trailing dot or space. Empty when nothing is left.</summary>
        internal static string SafeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder(title.Length);
            foreach (char c in title)
                sb.Append(bad.Contains(c) || char.IsControl(c) ? '-' : c);
            string s = Whitespace.Replace(sb.ToString(), " ").Trim();
            if (s.Length > MaxTitleChars) s = s.Substring(0, MaxTitleChars);
            return s.TrimEnd(' ', '.').TrimStart(' ');
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

        // The file an earlier export of this session wrote under another end date or name, and the ones the first
        // version wrote (session_<id8>_<first>_to_<last>.md). Only names this class produces for this id are
        // touched, never anything else in the folder, and never the <id8> folder of pictures.
        private static void RemoveOlderFilesOf(string folder, string id, string keepName)
        {
            string id8 = Regex.Escape(id.Substring(0, 8));
            var current = new Regex(@"^\d{4}-\d{2}-\d{2}_to_\d{4}-\d{2}-\d{2}(_.+)?_" + id8 + @"\.md$", RegexOptions.IgnoreCase);
            var first = new Regex(@"^session_" + id8 + @"_\d{4}-\d{2}-\d{2}_to_\d{4}-\d{2}-\d{2}\.md$", RegexOptions.IgnoreCase);
            foreach (var f in Directory.GetFiles(folder, "*.md"))
            {
                string n = Path.GetFileName(f);
                if ((current.IsMatch(n) || first.IsMatch(n)) && !string.Equals(n, keepName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }

        // ── The transcript ───────────────────────────────────

        private sealed class Rendered
        {
            public string Body = "";
            public string TabName, Title;
            public DateTime First, Last;
            public int Prompts;
        }

        private static Rendered Render(string path, ImageSink sink)
        {
            var res = new Rendered();
            string tabName = null, title = null;
            DateTime? firstTs = null, lastTs = null;
            var sb = new StringBuilder();
            string lastRole = null;
            bool lastWasCall = false;   // consecutive tool lines form one list, with no blank line between them
            int anonymous = 0;
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
                    bool name = line.Contains("\"agent-name\"") || line.Contains("\"custom-title\"");
                    bool queued = line.Contains("\"queued_command\"");
                    if (!user && !asst && !name && !queued) continue;
                    // A tool's result is a user line holding {"type":"tool_result"} and can be many MB; it is never
                    // exported. The quotes inside a JSON string are escaped, so this unescaped text is the block's own
                    // structure: a prompt that merely mentions tool_result does not match.
                    if (user && line.Contains("\"type\":\"tool_result\"")) continue;
                    // Too long to parse (in a 32-bit process), unless it is a picture within the size the exporter keeps.
                    if (line.Length > MaxLineChars && !(line.Length <= MaxImageLineChars && line.Contains("\"type\":\"image\""))) continue;

                    IDictionary<string, object> obj;
                    try { obj = ser.DeserializeObject(line) as IDictionary<string, object>; }
                    catch { continue; }
                    if (obj == null) continue;

                    string type = Str(obj, "type");
                    if (type == "agent-name" || type == "custom-title")
                    {
                        string n = Str(obj, type == "agent-name" ? "agentName" : "customTitle");
                        if (string.IsNullOrWhiteSpace(n)) continue;
                        n = n.Trim();
                        // The last CA tab name is the tab; the last name that is not one is what the developer called the chat.
                        if (ClaudeSessionCatalog.IsCaTabName(n)) tabName = n; else title = n;
                        continue;
                    }
                    if (IsTrue(obj, "isSidechain")) continue;   // a sub-agent's own conversation

                    DateTime? at = Time(Str(obj, "timestamp"));
                    if (type == "user" || type == "attachment")
                    {
                        string text;
                        var images = new List<IDictionary<string, object>>();
                        bool whileWorking = false;
                        if (type == "user")
                        {
                            if (!UserContent(obj, out text, images)) continue;
                        }
                        else
                        {
                            // A message typed while Claude was working: not a user record but an attachment.
                            text = QueuedPrompt(obj, ref at);
                            if (text == null) continue;
                            whileWorking = true;
                        }

                        res.Prompts++;
                        Note(ref firstTs, ref lastTs, at);
                        string key = MessageKey(obj, ref anonymous);
                        sb.Append("\r\n### USER").Append(at.HasValue ? " - " + Stamp(at.Value) : "")
                          .Append(whileWorking ? " (while Claude was working)" : "").Append("\r\n\r\n");
                        if (text != null && text.Length > 0) sb.Append(Cut(LinkPastes(text, key, sink))).Append("\r\n");
                        for (int b = 0; b < images.Count; b++)
                        {
                            string link = LinkImageBlock(images[b], key, b, sink);
                            if (link != null) sb.Append("\r\n").Append(link).Append("\r\n");
                        }
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

            res.Body = sb.ToString();
            res.TabName = tabName;
            res.Title = title;
            if (firstTs.HasValue) { res.First = firstTs.Value; res.Last = lastTs.Value; }
            else { res.First = res.Last = File.GetLastWriteTime(path); }
            return res;
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

        /// <summary>
        /// The developer's message in a user line: its text (null when it has none) and its image blocks. False for
        /// tool results, meta lines, hook output, CA's greeting, and a message with neither text nor a picture.
        /// </summary>
        private static bool UserContent(IDictionary<string, object> obj, out string text, List<IDictionary<string, object>> images)
        {
            text = null;
            if (IsTrue(obj, "isMeta")) return false;
            object m;
            var message = obj.TryGetValue("message", out m) ? m as IDictionary<string, object> : null;
            if (message == null) return false;
            object content;
            if (!message.TryGetValue("content", out content)) return false;

            string raw;
            if (content is string) raw = (string)content;
            else if (content is IEnumerable)
            {
                var sb = new StringBuilder();
                foreach (var b in (IEnumerable)content)
                {
                    var block = b as IDictionary<string, object>;
                    if (block == null) continue;
                    string bt = Str(block, "type");
                    if (bt == "tool_result") { images.Clear(); return false; }
                    if (bt == "text") sb.Append(Str(block, "text")).Append("\n");
                    else if (bt == "image") images.Add(block);
                }
                raw = sb.ToString();
            }
            else return false;
            text = ClaudeSessionCatalog.GenuineFull(raw);
            return text != null || images.Count > 0;
        }

        /// <summary>
        /// The text of a message typed while Claude was working ({"type":"attachment","attachment":{"type":"queued_command",
        /// "prompt":...}}), or null when the attachment is something else: another kind, a slash command, or not from the
        /// developer (a background task's notification also arrives queued). <paramref name="at"/> becomes the time the
        /// attachment carries, when it has one.
        /// </summary>
        private static string QueuedPrompt(IDictionary<string, object> obj, ref DateTime? at)
        {
            object a;
            var att = obj.TryGetValue("attachment", out a) ? a as IDictionary<string, object> : null;
            if (att == null || Str(att, "type") != "queued_command") return null;
            string mode = Str(att, "commandMode");
            if (mode != null && mode != "prompt") return null;
            object o;
            var origin = att.TryGetValue("origin", out o) ? o as IDictionary<string, object> : null;
            string kind = origin == null ? null : Str(origin, "kind");
            if (kind != null && kind != "human") return null;
            DateTime? own = Time(Str(att, "timestamp"));
            if (own.HasValue) at = own;
            return ClaudeSessionCatalog.GenuineFull(Str(att, "prompt"));
        }

        // The message's own id, first 12 hex characters (48 bits: a collision inside one chat is not a real risk): what the copies of its pictures are named from, so a
        // second export finds them again. A record with no id gets a counter (stable while the transcript only grows).
        private static string MessageKey(IDictionary<string, object> obj, ref int anonymous)
        {
            string u = Str(obj, "uuid");
            if (!string.IsNullOrEmpty(u))
            {
                var sb = new StringBuilder();
                foreach (char c in u)
                {
                    if (Uri.IsHexDigit(c)) sb.Append(char.ToLowerInvariant(c));
                    if (sb.Length == 12) break;
                }
                if (sb.Length >= 8) return sb.ToString();
            }
            anonymous++;
            return "m" + anonymous.ToString(CultureInfo.InvariantCulture);
        }

        // ── Pictures ─────────────────────────────────────────

        /// <summary>Where the pictures of one session are copied to: &lt;.CAChats&gt;\&lt;id8&gt;\, created only when the first one is.</summary>
        private sealed class ImageSink
        {
            private readonly string _id8, _folder;
            public ImageSink(string id8, string folder) { _id8 = id8; _folder = folder; }

            public string Link(string destName) { return "![](" + _id8 + "/" + destName + ")"; }
            public bool Has(string destName) { return File.Exists(Path.Combine(_folder, destName)); }

            public bool CopyFrom(string source, string destName)
            {
                try
                {
                    Directory.CreateDirectory(_folder);
                    File.Copy(source, Path.Combine(_folder, destName), true);
                    return true;
                }
                catch { return false; }
            }

            public bool Put(string destName, byte[] bytes)
            {
                try
                {
                    Directory.CreateDirectory(_folder);
                    File.WriteAllBytes(Path.Combine(_folder, destName), bytes);
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// The text with each "@&lt;path&gt;.png" that is a screenshot CA pasted (paste-&lt;time&gt;.png, in a folder named
        /// paste-cache) replaced by a link to its copy in &lt;id8&gt;\. The copy is made now when the original still exists;
        /// when it does not, an earlier export's copy of the same message and position is linked, and when there is none
        /// the path stays in the text.
        /// </summary>
        private static string LinkPastes(string text, string key, ImageSink sink)
        {
            int n = 0;
            return PasteToken.Replace(text, m =>
            {
                string dest = key + "-p" + (n++).ToString(CultureInfo.InvariantCulture) + ".png";
                if (sink.Has(dest)) return sink.Link(dest);
                string source = PasteCacheFile(m.Groups[1].Value);
                if (source != null && sink.CopyFrom(source, dest)) return sink.Link(dest);
                return m.Value;
            });
        }

        // The existing file a message's @path names, when it is one of CA's pasted screenshots and not too big; else null.
        // CA sends a path with spaces in its 8.3 form (PASTE-~1.PNG), so the long name is read back before it is judged.
        private static string PasteCacheFile(string path)
        {
            try
            {
                // The path comes from the transcript: a UNC or \?\ path would make File.Exists wait on the network,
                // and the shutdown export has only seconds. CA's pastes are on a local drive.
                if (!IsLocalDrivePath(path)) return null;
                if (!File.Exists(path)) return null;
                string full = LongPath(path);
                string name = Path.GetFileName(full);
                string dir = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
                if (!PasteFileName.IsMatch(name) || !string.Equals(dir, "paste-cache", StringComparison.OrdinalIgnoreCase)) return null;
                if (new FileInfo(full).Length > MaxImageBytes) return null;
                return full;
            }
            catch { return null; }
        }

        internal static bool IsLocalDrivePath(string path)
        {
            return path != null && path.Length >= 3 && path[1] == ':' && char.IsLetter(path[0]) && (path[2] == '\\' || path[2] == '/');
        }

        private static string LongPath(string path)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint n = GetLongPathName(path, sb, (uint)sb.Capacity);
                if (n > 0 && n < sb.Capacity) return sb.ToString();
            }
            catch { }
            return path;
        }

        // An image block sent in the message itself ({"type":"image","source":{"type":"base64",...}}): decoded into
        // <id8>\<message id>-b<n>.<ext> and linked. Null when it cannot be (another source type, too big, unreadable).
        private static string LinkImageBlock(IDictionary<string, object> block, string key, int index, ImageSink sink)
        {
            try
            {
                object s;
                var source = block.TryGetValue("source", out s) ? s as IDictionary<string, object> : null;
                if (source == null || Str(source, "type") != "base64") return null;
                string ext = ImageExtension(Str(source, "media_type"));
                string data = Str(source, "data");
                if (ext == null || string.IsNullOrEmpty(data)) return null;
                string dest = key + "-b" + index.ToString(CultureInfo.InvariantCulture) + ext;
                if (sink.Has(dest)) return sink.Link(dest);
                if ((long)data.Length * 3 / 4 > MaxImageBytes) return null;
                return sink.Put(dest, Convert.FromBase64String(data)) ? sink.Link(dest) : null;
            }
            catch { return null; }
        }

        private static string ImageExtension(string mediaType)
        {
            switch ((mediaType ?? "").ToLowerInvariant())
            {
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                default: return null;
            }
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
