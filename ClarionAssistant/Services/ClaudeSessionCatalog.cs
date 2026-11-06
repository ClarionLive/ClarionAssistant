using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>One past Claude Code session a CA tab can resume (ticket f9843525).</summary>
    public sealed class ClaudeSessionInfo
    {
        /// <summary>The session id: the transcript's file name, a lowercase GUID.</summary>
        public string Id;
        /// <summary>The tab's name (CA1, CA2, ...) from the transcript's agent-name / custom-title record; may be null.</summary>
        public string AgentName;
        /// <summary>The developer's first prompt, CA's own greeting skipped. Null when the head holds none.</summary>
        public string FirstPrompt;
        /// <summary>The newest prompt Claude Code recorded (its last-prompt record), when it is a real prompt.</summary>
        public string LastPrompt;
        /// <summary>The folder the session started in; always the solution folder it was listed for.</summary>
        public string Cwd;
        public DateTime LastWriteUtc;
        /// <summary>Written in the last <see cref="ClaudeSessionCatalog.RecentWriteWindow"/>: another terminal may have it open.</summary>
        public bool PossiblyOpenElsewhere;
        public string TranscriptPath;

        /// <summary>
        /// What the list shows for the session: the LAST prompt, else the first. Measured on real CA
        /// transcripts (2026-11-05): CA's start-up output fills the 256 KB head, so the first prompt
        /// is usually out of reach, and the last one is the better clue to where the work stopped.
        /// </summary>
        public string Summary { get { return LastPrompt ?? FirstPrompt; } }
    }

    public sealed class ClaudeSessionList
    {
        public readonly List<ClaudeSessionInfo> Sessions = new List<ClaudeSessionInfo>();
        /// <summary>True when transcripts beyond the ones read may hold more sessions (offer "Show more").</summary>
        public bool HasMore;
    }

    /// <summary>
    /// Lists the Claude Code sessions saved for a solution folder, so a CA tab can offer
    /// "Resume last session" and a list of earlier ones (ticket f9843525). A C# 7.3 port of the
    /// relevant parts of MultiTerminal's SessionCatalog: same folder encoding, same bounded
    /// head/tail reads, same rules for what counts as a real prompt. No IDE coupling, so the
    /// harness in tests\ClaudeSessionCatalog.Test.cs compiles this file as it is.
    ///
    /// A transcript is ~/.claude/projects/&lt;encoded cwd&gt;/&lt;session-guid&gt;.jsonl (CLAUDE_CONFIG_DIR
    /// replaces ~/.claude). Only the head (first 200 lines / 256 KB) and a 64 KB tail are read:
    /// CA transcripts run to many megabytes.
    /// </summary>
    public static class ClaudeSessionCatalog
    {
        internal const int HeadMaxLines = 200;
        internal const int HeadMaxBytes = 256 * 1024;
        internal const int TailBytes = 64 * 1024;
        internal const int SummaryMaxChars = 160;
        /// <summary>Read at most this many transcripts per listed session, so a folder of empty sessions stays cheap.</summary>
        internal const int ReadBudgetFactor = 3;
        public static readonly TimeSpan RecentWriteWindow = TimeSpan.FromMinutes(2);

        /// <summary>
        /// The first prompt CA sends every new tab (AssistantChatControl.BuildInitialPrompt picks one).
        /// It is not the developer's, so it never becomes a session's summary. Keep the two lists the
        /// same: BuildInitialPrompt reads its greetings from here.
        /// </summary>
        public static readonly string[] TimeGreetingsMorning = { "Good morning!", "Morning!", "Top of the morning!" };
        public static readonly string[] TimeGreetingsAfternoon = { "Good afternoon!", "Afternoon!" };
        public static readonly string[] TimeGreetingsEvening = { "Good evening!", "Evening!" };
        public static readonly string[] FunGreetings =
        {
            "Clarion Assistant is on-line!",
            "Greetings, Clarion Developer!",
            "Ready to write some Clarion!",
            "Let's build something!",
            "Reporting for duty!",
            "At your service!",
        };

        private static readonly string[] NotAPromptPrefixes =
        {
            "[MultiTerminal message",
            "initializing...",
            "Caveat:",
            "[Request interrupted",
            "Another Claude session sent a message", // a peer's message rendered as a prompt
        };

        // A prompt that is only a pasted-file reference (CA saves a long paste to a file and sends "@<path>").
        private static readonly Regex FileRefOnly = new Regex(@"^@\S+$", RegexOptions.Compiled);
        // CA tab names: CA1, CA2, ... (7792e3e0), and the older CA-Terminal-2-CC form. A STRICT allowlist:
        // a resumed session's name comes from its transcript and goes into the pwsh command line (-n,
        // MULTITERMINAL_NAME), where the escape helper doubles only the ASCII quote while PowerShell also
        // ends a string at U+2018-U+201B. Letters, digits, '.', '_' and '-' only, capped (pipeline run 1, security).
        private static readonly Regex CaTabName = new Regex(@"^CA(\d{1,4}|-[A-Za-z0-9._-]{1,64})$", RegexOptions.Compiled);

        /// <summary>
        /// Claude Code cuts a project folder name longer than this and appends "-&lt;hash&gt;" (seen on
        /// this machine: 207-character names). The hash can't be recomputed here, so a long path is
        /// matched by this prefix and each transcript's cwd decides (pipeline run 1, adversary).
        /// </summary>
        internal const int MaxFolderNameLength = 200;

        private static readonly string[][] AllGreetings;

        /// <summary>
        /// Appended to a resumed session's system prompt. CA's briefing says to greet the developer when a
        /// session starts; a resume continues one, so the model must not greet or recap (pipeline run 1).
        /// </summary>
        public const string ResumedSessionNote =
            "# Resumed Session\n" +
            "The developer resumed this conversation from the Clarion Assistant Home tab. It is not a new " +
            "session: do not greet them or summarise earlier work. Wait for their next message and carry on " +
            "from where the conversation left off. Your tab name may differ from the one used earlier in " +
            "this conversation; the identity section above is the current one.";

        static ClaudeSessionCatalog()
        {
            AllGreetings = new[] { TimeGreetingsMorning, TimeGreetingsAfternoon, TimeGreetingsEvening, FunGreetings };
        }

        private static readonly Regex GuidName = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
        private static readonly Regex SystemReminder = new Regex(
            @"<system-reminder>.*?</system-reminder>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        // ── Paths ────────────────────────────────────────────

        /// <summary>The projects folder the claude CA launches writes to: CLAUDE_CONFIG_DIR\projects, else %USERPROFILE%\.claude\projects.</summary>
        public static string DefaultProjectsRoot()
        {
            string cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            string root = !string.IsNullOrWhiteSpace(cfg)
                ? cfg.Trim()
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(root, "projects");
        }

        /// <summary>Claude Code's folder name for a cwd: every character outside [A-Za-z0-9] becomes '-'.</summary>
        public static string EncodeProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var sb = new StringBuilder(path.Length);
            foreach (char c in path)
            {
                bool keep = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                sb.Append(keep ? c : '-');
            }
            return sb.ToString();
        }

        /// <summary>A session id fit for --resume / --session-id (a GUID, lowercased), or null.</summary>
        public static string NormalizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();
            return GuidName.IsMatch(id) ? id.ToLowerInvariant() : null;
        }

        private static string NormalizeDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try { dir = Path.GetFullPath(dir.Trim()); } catch { return null; }
            return dir.TrimEnd('\\', '/');
        }

        private static bool SameDir(string a, string b)
        {
            a = NormalizeDir(a); b = NormalizeDir(b);
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // ── Listing ──────────────────────────────────────────

        /// <summary>
        /// The newest resumable sessions for <paramref name="workDir"/>, newest first, at most
        /// <paramref name="max"/>. Sessions whose id is in <paramref name="liveIds"/> (open in a CA
        /// tab) are left out, as are sessions with no prompt of the developer's and sessions that
        /// started in another folder whose name encodes the same (H:\a-b and H:\a\b share a folder).
        /// Never throws: an unreadable folder or transcript is skipped. With
        /// <paramref name="agentFilter"/>, only sessions whose agent name it accepts are listed (CA
        /// passes <see cref="IsCaTabName"/>).
        /// </summary>
        public static ClaudeSessionList List(string projectsRoot, string workDir, ICollection<string> liveIds,
                                             DateTime utcNow, int max, Func<string, bool> agentFilter = null)
        {
            var result = new ClaudeSessionList();
            var dirs = FoldersFor(projectsRoot, workDir);
            if (dirs.Count == 0 || max <= 0) return result;

            var files = new List<FileInfo>();
            foreach (var dir in dirs)
            {
                try
                {
                    foreach (var fi in new DirectoryInfo(dir).GetFiles("*.jsonl"))
                    {
                        string id = NormalizeId(Path.GetFileNameWithoutExtension(fi.Name));
                        if (id == null || fi.Length == 0) continue;
                        if (liveIds != null && Contains(liveIds, id)) continue;
                        files.Add(fi);
                    }
                }
                catch { } // one unreadable folder doesn't hide the others
            }

            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            int budget = max * ReadBudgetFactor, read = 0, i = 0;
            for (; i < files.Count && result.Sessions.Count < max && read < budget; i++)
            {
                read++;
                var info = Scan(files[i].FullName, workDir, utcNow);
                if (info != null && (agentFilter == null || agentFilter(info.AgentName))) result.Sessions.Add(info);
            }
            result.HasMore = i < files.Count;
            return result;
        }

        /// <summary>
        /// One session by id, re-read now, or null when it can no longer be resumed: the
        /// transcript is gone, it is open in a CA tab, or it no longer qualifies. The check made
        /// just before launching a resume.
        /// </summary>
        public static ClaudeSessionInfo TryGet(string projectsRoot, string workDir, string id,
                                               ICollection<string> liveIds, DateTime utcNow, Func<string, bool> agentFilter = null)
        {
            id = NormalizeId(id);
            if (id == null) return null;
            if (liveIds != null && Contains(liveIds, id)) return null;
            foreach (var dir in FoldersFor(projectsRoot, workDir))
            {
                string path = Path.Combine(dir, id + ".jsonl");
                if (!File.Exists(path)) continue;
                var info = Scan(path, workDir, utcNow);
                if (info != null && (agentFilter == null || agentFilter(info.AgentName))) return info;
            }
            return null;
        }

        /// <summary>
        /// The project folder(s) that may hold workDir's transcripts: the encoded name itself, or, for a
        /// name over <see cref="MaxFolderNameLength"/>, every folder starting with its first 200 characters
        /// (Claude Code appends a hash this code can't reproduce; Scan's cwd check picks the right files).
        /// </summary>
        internal static List<string> FoldersFor(string projectsRoot, string workDir)
        {
            var dirs = new List<string>();
            string wd = NormalizeDir(workDir);
            if (wd == null || string.IsNullOrEmpty(projectsRoot)) return dirs;
            string name = EncodeProjectPath(wd);
            try
            {
                if (name.Length <= MaxFolderNameLength)
                {
                    string dir = Path.Combine(projectsRoot, name);
                    if (Directory.Exists(dir)) dirs.Add(dir);
                }
                else if (Directory.Exists(projectsRoot))
                {
                    string prefix = name.Substring(0, MaxFolderNameLength);
                    foreach (var d in new DirectoryInfo(projectsRoot).GetDirectories(prefix + "*"))
                        if (d.Name.Length > MaxFolderNameLength) dirs.Add(d.FullName);
                    string exact = Path.Combine(projectsRoot, name);   // an older claude that did not cut
                    if (Directory.Exists(exact) && !dirs.Contains(exact)) dirs.Add(exact);
                }
            }
            catch { }
            return dirs;
        }

        private static bool Contains(ICollection<string> ids, string id)
        {
            foreach (var s in ids)
                if (string.Equals(NormalizeId(s), id, StringComparison.Ordinal)) return true;
            return false;
        }

        // ── One transcript ───────────────────────────────────

        /// <summary>Reads one transcript's head and tail; null when it is not a resumable session for workDir.</summary>
        internal static ClaudeSessionInfo Scan(string path, string workDir, DateTime utcNow)
        {
            string id = NormalizeId(Path.GetFileNameWithoutExtension(path));
            if (id == null) return null;
            try
            {
                string head, tail;
                long length;
                DateTime lastWrite;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    length = fs.Length;
                    if (length == 0) return null;
                    head = ReadText(fs, 0, (int)Math.Min(length, HeadMaxBytes));
                    long tailStart = Math.Max(0, length - TailBytes);
                    tail = ReadText(fs, tailStart, (int)(length - tailStart));
                    if (tailStart > 0)
                    {
                        int nl = tail.IndexOf('\n');
                        tail = nl < 0 ? string.Empty : tail.Substring(nl + 1); // first segment is a partial line
                    }
                }
                lastWrite = File.GetLastWriteTimeUtc(path);

                var info = new ClaudeSessionInfo { Id = id, TranscriptPath = path, LastWriteUtc = lastWrite };
                bool hasAssistant = false;
                string firstCwd = null, customTitle = null, tailAgent = null, tailTitle = null;
                var ser = NewSerializer();

                string[] headLines = head.Split('\n');
                int lineCount = headLines.Length;
                if (length > HeadMaxBytes) lineCount--; // partial line at the window edge
                for (int n = 0; n < lineCount && n < HeadMaxLines; n++)
                {
                    string line = headLines[n].Trim();
                    if (line.Length == 0 || line[0] != '{') continue;
                    bool user = line.Contains("\"type\":\"user\"");
                    bool asst = line.Contains("\"type\":\"assistant\"");
                    bool name = line.Contains("\"agent-name\"") || line.Contains("\"custom-title\"");
                    bool cwdHere = firstCwd == null && line.Contains("\"cwd\":");
                    if (!user && !asst && !name && !cwdHere) continue;

                    var obj = Parse(ser, line);
                    if (obj == null) continue;
                    if (firstCwd == null) firstCwd = GetString(obj, "cwd");
                    string type = GetString(obj, "type");
                    if (type == "assistant") hasAssistant = true;
                    else if (type == "user" && info.FirstPrompt == null) info.FirstPrompt = PromptText(obj);
                    else if (type == "agent-name" && info.AgentName == null) info.AgentName = Clean(GetString(obj, "agentName"));
                    else if (type == "custom-title" && customTitle == null) customTitle = Clean(GetString(obj, "customTitle"));
                }

                foreach (var raw in tail.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] != '{') continue;
                    if (!hasAssistant && line.Contains("\"type\":\"assistant\"")) hasAssistant = true;
                    bool lp = line.Contains("\"last-prompt\"");
                    bool nm = line.Contains("\"agent-name\"") || line.Contains("\"custom-title\"");
                    if (!lp && !nm) continue;
                    var obj = Parse(ser, line);
                    if (obj == null) continue;
                    string type = GetString(obj, "type");
                    if (type == "last-prompt")
                    {
                        string p = Genuine(GetString(obj, "lastPrompt"));
                        if (p != null) info.LastPrompt = p; // file order: the last one wins
                    }
                    // Claude Code re-writes the name records as it goes, so the tail has the CURRENT name
                    // (after a rename, or a resume under another -n), and has it when the head is all
                    // start-up output (pipeline run 1, adversary). The last one wins.
                    else if (type == "agent-name") { string n = Clean(GetString(obj, "agentName")); if (n != null) tailAgent = n; }
                    else if (type == "custom-title") { string n = Clean(GetString(obj, "customTitle")); if (n != null) tailTitle = n; }
                }

                if (tailAgent != null) info.AgentName = tailAgent;
                if (info.AgentName == null) info.AgentName = tailTitle ?? customTitle;
                // A session only CA's greeting ever reached is not worth resuming.
                if (info.Summary == null || !hasAssistant) return null;
                // Same folder name, different folder: H:\a-b and H:\a\b encode alike.
                if (firstCwd == null || !SameDir(firstCwd, workDir)) return null;
                info.Cwd = NormalizeDir(firstCwd);
                info.PossiblyOpenElsewhere = utcNow - lastWrite < RecentWriteWindow;
                return info;
            }
            catch
            {
                return null; // deleted or locked mid-read, or malformed: not offered
            }
        }

        private static string ReadText(FileStream fs, long offset, int count)
        {
            var buf = new byte[count];
            fs.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int n = fs.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return Encoding.UTF8.GetString(buf, 0, total);
        }

        private static JavaScriptSerializer NewSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        }

        private static IDictionary<string, object> Parse(JavaScriptSerializer ser, string line)
        {
            try { return ser.DeserializeObject(line) as IDictionary<string, object>; }
            catch { return null; }
        }

        private static string GetString(IDictionary<string, object> obj, string key)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) ? v as string : null;
        }

        private static bool IsTrue(IDictionary<string, object> obj, string key)
        {
            object v;
            return obj.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        private static string Clean(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        /// <summary>The developer's prompt in a user line, or null for tool results, meta lines, hook output and CA's greeting.</summary>
        private static string PromptText(IDictionary<string, object> obj)
        {
            if (IsTrue(obj, "isMeta") || IsTrue(obj, "isSidechain")) return null;
            object m;
            var message = obj.TryGetValue("message", out m) ? m as IDictionary<string, object> : null;
            if (message == null) return null;
            object content;
            if (!message.TryGetValue("content", out content)) return null;

            string text;
            if (content is string)
                text = (string)content;
            else if (content is IEnumerable)
            {
                var sb = new StringBuilder();
                foreach (var b in (IEnumerable)content)
                {
                    var block = b as IDictionary<string, object>;
                    if (block == null) continue;
                    string bt = GetString(block, "type");
                    if (bt == "tool_result") return null;
                    if (bt == "text") sb.Append(GetString(block, "text")).Append(' ');
                }
                text = sb.ToString();
            }
            else return null;
            return Genuine(text);
        }

        /// <summary>The text cleaned and cut to <see cref="SummaryMaxChars"/>, or null when it is not a prompt the developer typed.</summary>
        internal static string Genuine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = SystemReminder.Replace(text, " ").Trim();
            if (text.Length == 0 || text[0] == '<') return null; // <command-name>, <cross-session-message>, ...
            foreach (var p in NotAPromptPrefixes)
                if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return null;
            if (IsCaGreeting(text)) return null;
            if (FileRefOnly.IsMatch(text)) return null; // "@C:\...\PASTE-~1.TXT" says nothing about the session
            text = Whitespace.Replace(text, " ");
            if (text.Length > SummaryMaxChars) text = text.Substring(0, SummaryMaxChars - 1).TrimEnd() + "\u2026";
            return text;
        }

        /// <summary>
        /// True for a name a CA Claude tab runs under (CA1, CA-Terminal-2-CC). The Home tab lists only
        /// these: a MultiTerminal agent's session (Charlie, a helper) in the same folder belongs to MT,
        /// and resuming it in CA would give one conversation two owners.
        /// </summary>
        public static bool IsCaTabName(string agentName)
        {
            return agentName != null && CaTabName.IsMatch(agentName.Trim());
        }

        public static bool IsCaGreeting(string text)
        {
            if (text == null) return false;
            text = text.Trim();
            foreach (var set in AllGreetings)
                foreach (var g in set)
                    if (string.Equals(text, g, StringComparison.Ordinal)) return true;
            return false;
        }

        // ── Display ──────────────────────────────────────────

        /// <summary>"just now", "12 min ago", "3 h ago", "yesterday", "4 days ago", else "Oct 28" (local time).</summary>
        public static string FormatAge(DateTime lastWriteUtc, DateTime utcNow)
        {
            var span = utcNow - lastWriteUtc;
            if (span < TimeSpan.FromMinutes(1)) return "just now";
            if (span < TimeSpan.FromHours(1)) return (int)span.TotalMinutes + " min ago";
            DateTime local = lastWriteUtc.ToLocalTime(), today = utcNow.ToLocalTime().Date;
            if (local.Date == today) return (int)span.TotalHours + " h ago";
            int days = (int)(today - local.Date).TotalDays;
            if (days == 1) return "yesterday";
            if (days < 7) return days + " days ago";
            return local.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
