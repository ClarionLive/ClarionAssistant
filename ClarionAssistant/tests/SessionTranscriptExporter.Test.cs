using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClarionAssistant.Services;

// Harness for the chat transcript export: a Claude session is written to <solution folder>\.CAChats as
// Markdown named with its first and last day (and the chat's name), when its tab closes or the IDE does.
// Compiles the REAL Services\SessionTranscriptExporter.cs and Services\ClaudeSessionCatalog.cs (no IDE
// coupling) against fixture transcripts written to %TEMP%.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
static class SessionTranscriptExporterTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferSize);

    static string J(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n"); }

    // Transcript lines in Claude Code's compact JSON. Times are noon UTC so the local day is the same one
    // in every time zone from UTC-11 to UTC+11.
    static string Ts(string day) { return ",\"timestamp\":\"" + day + "T12:00:00.000Z\""; }
    static string Uuid(string u) { return ",\"uuid\":\"" + u + "\""; }
    static string AgentName(string n) { return "{\"type\":\"agent-name\",\"agentName\":\"" + J(n) + "\"}"; }
    static string Title(string n) { return "{\"type\":\"custom-title\",\"customTitle\":\"" + J(n) + "\"}"; }
    static string User(string day, string text, string extra = "")
    {
        return "{\"isSidechain\":false" + extra + ",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + J(text) + "\"}" + Ts(day) + "}";
    }
    static string UserBlocks(string day, string blocks, string extra = "")
    {
        return "{\"isSidechain\":false" + extra + ",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + blocks + "}" + Ts(day) + "}";
    }
    static string Queued(string day, string prompt, string origin = "human", string mode = "prompt")
    {
        return "{\"isSidechain\":false,\"attachment\":{\"type\":\"queued_command\",\"prompt\":\"" + J(prompt) + "\",\"commandMode\":\"" + mode +
               "\",\"origin\":{\"kind\":\"" + origin + "\"},\"timestamp\":\"" + day + "T12:30:00.000Z\"},\"type\":\"attachment\"" + Ts(day) + "}";
    }
    static string Asst(string day, string text, bool sidechain = false)
    {
        return "{\"isSidechain\":" + (sidechain ? "true" : "false") + ",\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + J(text) + "\"}]}" + Ts(day) + "}";
    }
    static string AsstBlocks(string day, string blocks)
    {
        return "{\"isSidechain\":false,\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":" + blocks + "}" + Ts(day) + "}";
    }

    static string TranscriptPath(string projectsRoot, string workDir, string id)
    {
        return Path.Combine(projectsRoot, ClaudeSessionCatalog.EncodeProjectPath(workDir), id + ".jsonl");
    }

    static void WriteTranscript(string projectsRoot, string workDir, string id, params string[] lines)
    {
        string path = TranscriptPath(projectsRoot, workDir, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    static void AppendTranscript(string projectsRoot, string workDir, string id, params string[] lines)
    {
        File.AppendAllText(TranscriptPath(projectsRoot, workDir, id), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    static string[] SessionFiles(string workDir)
    {
        string folder = SessionTranscriptExporter.FolderFor(workDir);
        if (!Directory.Exists(folder)) return new string[0];
        return Directory.GetFiles(folder, "*.md").Select(Path.GetFileName).OrderBy(x => x).ToArray();
    }

    // The path as CA puts it in a message: when it has a space (%TEMP% under "C:\Users\John Hickey"), CA sends the 8.3 form
    // (ToShortPathIfPossible), which PasteToken can read. A file that does not exist has no short name: its folder's is used.
    static string Short(string path)
    {
        var sb = new StringBuilder(1024);
        if (File.Exists(path) || Directory.Exists(path))
        {
            uint n = GetShortPathName(path, sb, 1024);
            return n > 0 && n < 1024 ? sb.ToString() : path;
        }
        return Path.Combine(Short(Path.GetDirectoryName(path)), Path.GetFileName(path));
    }

    static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8 };

    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-export-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string projects = Path.Combine(root, "projects");
        string work = Path.Combine(root, "My Solution");
        Directory.CreateDirectory(projects);
        Directory.CreateDirectory(work);

        try
        {
            const string id1 = "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            string greeting = ClaudeSessionCatalog.FunGreetings[0];

            Console.WriteLine("A normal session");
            WriteTranscript(projects, work, id1,
                AgentName("CA1"),
                User("2026-11-19", greeting),
                Asst("2026-11-19", "Clarion Assistant is on-line, what are we working on?"),
                User("2026-11-19", "Hola.\r\nPodes revisar el repositorio?"),
                User("2026-11-19", "<command-name>/model</command-name>"),
                User("2026-11-19", "ruido", ",\"isMeta\":true"),
                UserBlocks("2026-11-19", "[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"SECRET-TOOL-OUTPUT\"}]"),
                Asst("2026-11-19", "SIDECHAIN-REPLY", sidechain: true),
                AsstBlocks("2026-11-20", "[{\"type\":\"thinking\",\"thinking\":\"HIDDEN-REASONING\"},{\"type\":\"text\",\"text\":\"Reviso el repo.\"},{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{\"command\":\"git   fetch\\nupstream\",\"description\":\"\"}}]"),
                AsstBlocks("2026-11-20", "[{\"type\":\"text\",\"text\":\"Hay 3 commits nuevos.\"}]"));
            var r = SessionTranscriptExporter.Export(projects, work, id1);
            Ok("exported", r.Path != null && File.Exists(r.Path), r.Reason);
            string name1 = SessionTranscriptExporter.FileName(id1, new DateTime(2026, 11, 19), new DateTime(2026, 11, 20), null);
            Ok("named with the first and last day, first day first",
               name1 == "2026-11-19_to_2026-11-20_11111111.md" && r.Path != null && Path.GetFileName(r.Path) == name1, r.Path);
            Ok("written to <solution>\\.CAChats", r.Path != null && Path.GetDirectoryName(r.Path) == Path.Combine(work, ".CAChats"), r.Path);
            string md = r.Path != null ? File.ReadAllText(r.Path) : "";
            Ok("header names the session, the tab and the period",
               md.Contains("`" + id1 + "`") && md.Contains("- Tab: CA1") && md.Contains("From: 2026-11-19") && md.Contains("To: 2026-11-20"));
            Ok("a plain CA tab name is not the chat's name", !md.Contains("- Name:"));
            Ok("the developer's message keeps its line breaks", md.Contains("Hola.\r\nPodes revisar el repositorio?"));
            Ok("headed USER with its day", md.Contains("### USER - 2026-11-19"));
            Ok("Claude's reply and the tool line are in", md.Contains("### CLAUDE") && md.Contains("Reviso el repo.") && md.Contains("- `[Bash]` git fetch upstream"));
            // The fixture has two stretches of Claude: the reply before the first message, and two records
            // in a row after it. Two headings, not three.
            int claudeHeadings = (md.Length - md.Replace("### CLAUDE", "").Length) / "### CLAUDE".Length;
            Ok("replies in a row share one CLAUDE heading", claudeHeadings == 2, claudeHeadings + " headings");
            Ok("CA's greeting is not a message", !md.Contains(greeting));
            Ok("command lines and meta lines are left out", !md.Contains("/model") && !md.Contains("ruido"));
            Ok("tool output is left out", !md.Contains("SECRET-TOOL-OUTPUT"));
            Ok("a sub-agent's reply is left out", !md.Contains("SIDECHAIN-REPLY"));
            Ok("reasoning is left out", !md.Contains("HIDDEN-REASONING"));
            string ignore = Path.Combine(work, ".CAChats", ".gitignore");
            Ok(".gitignore keeps the folder out of git", File.Exists(ignore) && File.ReadAllLines(ignore).Any(l => l.Trim() == "*"));

            Console.WriteLine("A session that grows into another day");
            AppendTranscript(projects, work, id1, User("2026-11-21", "Una pregunta mas"));
            string chats = Path.Combine(work, ".CAChats");
            File.WriteAllText(Path.Combine(chats, "session_11111111_2026-11-19_to_2026-11-20.md"), "the first version's name");
            File.WriteAllText(Path.Combine(chats, "session_11111111_notes.md"), "mine");
            File.WriteAllText(Path.Combine(chats, "other.md"), "mine");
            File.WriteAllText(Path.Combine(chats, "2026-11-19_to_2026-11-20_22222222.md"), "another session's");
            var r2 = SessionTranscriptExporter.Export(projects, work, id1);
            Ok("re-exported under the new end date", r2.Path != null && Path.GetFileName(r2.Path) == "2026-11-19_to_2026-11-21_11111111.md", r2.Path + " " + r2.Reason);
            var files = SessionFiles(work);
            Ok("one file per session: the earlier ones, in either naming, are gone",
               files.Contains("2026-11-19_to_2026-11-21_11111111.md") && !files.Contains(name1)
               && !files.Contains("session_11111111_2026-11-19_to_2026-11-20.md"), string.Join(",", files));
            Ok("files that are not this session's are left alone",
               File.Exists(Path.Combine(chats, "session_11111111_notes.md")) && File.Exists(Path.Combine(chats, "other.md"))
               && File.Exists(Path.Combine(chats, "2026-11-19_to_2026-11-20_22222222.md")));
            var r3 = SessionTranscriptExporter.Export(projects, work, id1);
            Ok("exporting again changes nothing", r3.Path == r2.Path && SessionFiles(work).Length == 4, string.Join(",", SessionFiles(work)));

            Console.WriteLine("A chat the developer named");
            const string id7 = "77777777-1111-4111-8111-111111111111";
            string work7 = Path.Combine(root, "Named");
            Directory.CreateDirectory(work7);
            WriteTranscript(projects, work7, id7,
                AgentName("CA1"),
                User("2026-11-23", "Arreglar el dialogo de login"),
                Asst("2026-11-23", "Voy a revisarlo."),
                Title("Fix login/dialog: ñ"),
                AgentName("Fix login/dialog: ñ"));
            var r10 = SessionTranscriptExporter.Export(projects, work7, id7);
            Ok("the name is in the file name, between the days and the id",
               r10.Path != null && Path.GetFileName(r10.Path) == "2026-11-23_to_2026-11-23_Fix login-dialog- ñ_77777777.md", r10.Path + " " + r10.Reason);
            string md7 = r10.Path != null ? File.ReadAllText(r10.Path) : "";
            Ok("the heading has the name as typed, and the tab", md7.Contains("- Name: Fix login/dialog: ñ") && md7.Contains("- Tab: CA1"));
            AppendTranscript(projects, work7, id7, Title("Otro nombre"));
            var r11 = SessionTranscriptExporter.Export(projects, work7, id7);
            Ok("renamed: the new name replaces the old file",
               r11.Path != null && SessionFiles(work7).Length == 1 && Path.GetFileName(r11.Path) == "2026-11-23_to_2026-11-23_Otro nombre_77777777.md", string.Join(",", SessionFiles(work7)));
            string longTitle = new string('a', 200) + " b";
            string safe = SessionTranscriptExporter.FileName(id7, new DateTime(2026, 11, 23), new DateTime(2026, 11, 23), longTitle);
            Ok("a long name is cut", safe == "2026-11-23_to_2026-11-23_" + new string('a', SessionTranscriptExporter.MaxTitleChars) + "_77777777.md", safe);
            Ok("a name that is only invalid characters leaves no hole",
               SessionTranscriptExporter.FileName(id7, new DateTime(2026, 11, 23), new DateTime(2026, 11, 23), " . ") == "2026-11-23_to_2026-11-23_77777777.md");

            Console.WriteLine("Messages typed while Claude was working");
            const string id8 = "88888888-2222-4222-8222-222222222222";
            string work8 = Path.Combine(root, "Queued");
            Directory.CreateDirectory(work8);
            WriteTranscript(projects, work8, id8,
                User("2026-11-23", "Primera pregunta"),
                AsstBlocks("2026-11-23", "[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"description\":\"Trabajando\"}}]"),
                Queued("2026-11-23", "Pará, usá la otra carpeta"),
                Queued("2026-11-23", "TASK-NOTIFICATION-TEXT", origin: "task-notification"),
                Queued("2026-11-23", "echo bash-mode-text", mode: "bash"),
                Asst("2026-11-23", "Entendido."));
            var r12 = SessionTranscriptExporter.Export(projects, work8, id8);
            string md8 = r12.Path != null ? File.ReadAllText(r12.Path) : "";
            Ok("a message typed while Claude was working is kept, marked, with its own time",
               md8.Contains("### USER - 2026-11-23 ") && md8.Contains("(while Claude was working)") && md8.Contains("Pará, usá la otra carpeta"), r12.Reason);
            Ok("one that did not come from the developer is left out", !md8.Contains("TASK-NOTIFICATION-TEXT"));
            Ok("a queued command that is not a prompt is left out", !md8.Contains("bash-mode-text"));
            Ok("it comes after the tool line and before Claude's answer",
               md8.IndexOf("[Bash]", StringComparison.Ordinal) < md8.IndexOf("Pará", StringComparison.Ordinal)
               && md8.IndexOf("Pará", StringComparison.Ordinal) < md8.IndexOf("Entendido.", StringComparison.Ordinal));
            const string id9 = "99999999-3333-4333-8333-333333333333";
            WriteTranscript(projects, work8, id9, Queued("2026-11-23", "Solo esto"));
            Ok("a chat whose only message was typed while Claude worked counts as a chat",
               SessionTranscriptExporter.Export(projects, work8, id9).Path != null);

            Console.WriteLine("Pictures");
            const string idA = "aaaaaaaa-4444-4444-8444-444444444444";
            string workA = Path.Combine(root, "Pics");
            Directory.CreateDirectory(workA);
            string cache = Path.Combine(root, "AppData", "ClarionAssistant", "paste-cache");
            Directory.CreateDirectory(cache);
            string shot = Path.Combine(cache, "paste-20261123-075637-607.png");
            File.WriteAllBytes(shot, Png);
            string elsewhere = Path.Combine(root, "docs", "foo.png");
            Directory.CreateDirectory(Path.GetDirectoryName(elsewhere));
            File.WriteAllBytes(elsewhere, Png);
            string gone = Path.Combine(cache, "paste-20261101-010101-001.png");   // never existed: pruned long ago
            string b64 = Convert.ToBase64String(Png);
            string shotS = Short(shot), goneS = Short(gone), elsewhereS = Short(elsewhere);
            WriteTranscript(projects, workA, idA,
                User("2026-11-23", "@" + shotS + " que dice este error?", Uuid("abcd1234-0001-4000-8000-000000000000")),
                Asst("2026-11-23", "Es un aviso de Trayslate."),
                User("2026-11-23", "@" + goneS + " y esta?", Uuid("abcd1234-0002-4000-8000-000000000000")),
                User("2026-11-23", "mira @" + elsewhereS + " tambien", Uuid("abcd1234-0003-4000-8000-000000000000")),
                UserBlocks("2026-11-23", "[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + b64 + "\"}},{\"type\":\"text\",\"text\":\"y esta otra\"}]", Uuid("abcd1234-0004-4000-8000-000000000000")),
                UserBlocks("2026-11-23", "[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + b64 + "\"}}]", Uuid("abcd1234-0005-4000-8000-000000000000")));
            var r13 = SessionTranscriptExporter.Export(projects, workA, idA);
            string mdA = r13.Path != null ? File.ReadAllText(r13.Path) : "";
            string picFolder = Path.Combine(workA, ".CAChats", "aaaaaaaa");
            Ok("a pasted screenshot is copied to <id8>\\ and linked", File.Exists(Path.Combine(picFolder, "abcd12340001-p0.png"))
               && mdA.Contains("![](aaaaaaaa/abcd12340001-p0.png) que dice este error?"), r13.Reason + " " + mdA.Substring(0, Math.Min(400, mdA.Length)));
            Ok("the copy is the same picture", File.Exists(Path.Combine(picFolder, "abcd12340001-p0.png")) && File.ReadAllBytes(Path.Combine(picFolder, "abcd12340001-p0.png")).SequenceEqual(Png));
            Ok("a screenshot that is gone and was never copied stays as its path", mdA.Contains("@" + goneS));
            Ok("a picture that is not one of CA's pastes is not copied", mdA.Contains("@" + elsewhereS) && Directory.GetFiles(picFolder).Length == 3, string.Join(",", Directory.GetFiles(picFolder).Select(Path.GetFileName)));
            Ok("a picture sent in the message is decoded and linked, with its text",
               File.Exists(Path.Combine(picFolder, "abcd12340004-b0.png")) && mdA.Contains("![](aaaaaaaa/abcd12340004-b0.png)") && mdA.Contains("y esta otra"));
            Ok("a message that is only a picture is kept", mdA.Contains("![](aaaaaaaa/abcd12340005-b0.png)") && mdA.Split(new[] { "### USER" }, StringSplitOptions.None).Length - 1 == 5,
               (mdA.Split(new[] { "### USER" }, StringSplitOptions.None).Length - 1) + " USER headings");

            Console.WriteLine("Pictures: the original is pruned, the chat is exported again");
            File.Delete(shot);   // CA prunes its paste cache after 24 h
            AppendTranscript(projects, workA, idA, Asst("2026-11-24", "Sigo."));
            var r14 = SessionTranscriptExporter.Export(projects, workA, idA);
            string mdA2 = r14.Path != null ? File.ReadAllText(r14.Path) : "";
            Ok("the link survives: the copy made earlier is found again", mdA2.Contains("![](aaaaaaaa/abcd12340001-p0.png) que dice este error?"), r14.Reason);
            Ok("and the copy is still there", File.Exists(Path.Combine(picFolder, "abcd12340001-p0.png")));
            Ok("the picture folder is not mistaken for an old export", Directory.Exists(picFolder) && SessionFiles(workA).Length == 1, string.Join(",", SessionFiles(workA)));

            Console.WriteLine("Pictures: a path CA sent in its 8.3 form");
            const string idB = "bbbbbbbb-5555-4555-8555-555555555555";
            string workB = Path.Combine(root, "Short");
            Directory.CreateDirectory(workB);
            string shot2 = Path.Combine(cache, "paste-20261123-080000-001.png");
            File.WriteAllBytes(shot2, Png);
            var sb83 = new StringBuilder(1024);
            uint n83 = GetShortPathName(shot2, sb83, 1024);
            string shortForm = n83 > 0 ? sb83.ToString() : shot2;
            if (string.Equals(shortForm, shot2, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("  [skip] this volume makes no 8.3 names, so the short form cannot be built here");
            else
            {
                WriteTranscript(projects, workB, idB, User("2026-11-23", "@" + shortForm + " mira", Uuid("feedbeef-0001-4000-8000-000000000000")));
                var r15 = SessionTranscriptExporter.Export(projects, workB, idB);
                Ok("the long name is read back from the short one before it is judged",
                   r15.Path != null && File.Exists(Path.Combine(workB, ".CAChats", "bbbbbbbb", "feedbeef0001-p0.png")), r15.Reason);
            }

            Console.WriteLine("Pictures: a path on the network is not touched");
            Ok("only a drive-letter path is looked at",
               SessionTranscriptExporter.IsLocalDrivePath(@"C:\a\paste-cache\p.png") && SessionTranscriptExporter.IsLocalDrivePath("d:/a/p.png")
               && !SessionTranscriptExporter.IsLocalDrivePath(@"\\host\share\p.png") && !SessionTranscriptExporter.IsLocalDrivePath(@"\\?\C:\a\p.png")
               && !SessionTranscriptExporter.IsLocalDrivePath(@"paste-cache\p.png") && !SessionTranscriptExporter.IsLocalDrivePath(@"\a\p.png") && !SessionTranscriptExporter.IsLocalDrivePath(null));
            const string idU = "cccccccc-6666-4666-8666-666666666666";
            string workU = Path.Combine(root, "Unc");
            Directory.CreateDirectory(workU);
            string shot3 = Path.Combine(cache, "paste-20261123-090000-001.png");
            File.WriteAllBytes(shot3, Png);
            string shot3S = Short(shot3);
            // The same file through the machine's own admin share: it exists, so only the path's form can keep it out.
            string unc = @"\\localhost\" + shot3S[0] + "$" + shot3S.Substring(2);
            WriteTranscript(projects, workU, idU, User("2026-11-23", "@" + unc + " mira", Uuid("c0c0c0c0-0001-4000-8000-000000000000")));
            var rU = SessionTranscriptExporter.Export(projects, workU, idU);
            string mdU = rU.Path != null ? File.ReadAllText(rU.Path) : "";
            Ok("a UNC path is left as it is, not copied", rU.Path != null && mdU.Contains("@" + unc) && !Directory.Exists(Path.Combine(workU, ".CAChats", "cccccccc")), rU.Reason + " " + mdU);

            Console.WriteLine("A failed write keeps the earlier export");
            const string idK = "dddddddd-7777-4777-8777-777777777777";
            string workK = Path.Combine(root, "Keep");
            Directory.CreateDirectory(workK);
            WriteTranscript(projects, workK, idK, User("2026-11-23", "Hola"), Asst("2026-11-23", "Que tal."));
            var rK1 = SessionTranscriptExporter.Export(projects, workK, idK);
            AppendTranscript(projects, workK, idK, Asst("2026-11-25", "Sigo."));   // a later last day: the next export has another name
            string nextName = SessionTranscriptExporter.FileName(idK, new DateTime(2026, 11, 23), new DateTime(2026, 11, 25), null);
            // A folder where the temporary file goes: the write fails like a locked or full disk would.
            Directory.CreateDirectory(Path.Combine(workK, ".CAChats", nextName + ".tmp"));
            var rK2 = SessionTranscriptExporter.Export(projects, workK, idK);
            Ok("the write failed", rK2.IsError && rK2.Path == null, rK2.Reason);
            Ok("the earlier export is still there", rK1.Path != null && File.Exists(rK1.Path), string.Join(",", SessionFiles(workK)));
            Directory.Delete(Path.Combine(workK, ".CAChats", nextName + ".tmp"));
            var rK3 = SessionTranscriptExporter.Export(projects, workK, idK);
            Ok("a write that works replaces it", rK3.Path != null && SessionFiles(workK).Length == 1 && Path.GetFileName(rK3.Path) == nextName, string.Join(",", SessionFiles(workK)));

            Console.WriteLine("Lines too big to read");
            const string idL = "eeeeeeee-8888-4888-8888-888888888888";
            string workL = Path.Combine(root, "Lines");
            Directory.CreateDirectory(workL);
            string huge = "HUGELINEMARK" + new string('x', SessionTranscriptExporter.MaxLineChars);
            string bigPng = Convert.ToBase64String(new byte[SessionTranscriptExporter.MaxLineChars * 3 / 4 + 1000]);   // a line over the cap that is a picture
            WriteTranscript(projects, workL, idL,
                User("2026-11-23", "primero"),
                User("2026-11-23", huge),
                UserBlocks("2026-11-23", "[{\"tool_use_id\":\"toolu_1\",\"type\":\"tool_result\",\"content\":\"TOOLOUTMARK\"}]"),
                User("2026-11-23", "explica {\"type\":\"tool_result\"} y tool_use_id por favor"),
                UserBlocks("2026-11-23", "[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + bigPng + "\"}},{\"type\":\"text\",\"text\":\"con foto\"}]", Uuid("1a1a1a1a-0001-4000-8000-000000000000")),
                User("2026-11-23", "ultimo"));
            var rL = SessionTranscriptExporter.Export(projects, workL, idL);
            string mdL = rL.Path != null ? File.ReadAllText(rL.Path) : "";
            Ok("a line over the cap is skipped, the rest of the chat is kept", rL.Path != null && !mdL.Contains("HUGELINEMARK") && mdL.Contains("primero") && mdL.Contains("ultimo"), rL.Reason);
            Ok("a tool result is left out", !mdL.Contains("TOOLOUTMARK"));
            Ok("a prompt that only mentions tool_result is kept", mdL.Contains("explica {\"type\":\"tool_result\"} y tool_use_id por favor"));
            Ok("a picture over the line cap but within the picture limit is still kept", mdL.Contains("con foto") && File.Exists(Path.Combine(workL, ".CAChats", "eeeeeeee", "1a1a1a1a0001-b0.png")));

            Console.WriteLine("Nothing to write");
            const string id2 = "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
            string work2 = Path.Combine(root, "Other Solution");
            Directory.CreateDirectory(work2);
            WriteTranscript(projects, work2, id2, AgentName("CA2"), User("2026-11-19", greeting), Asst("2026-11-19", "Ready."));
            var r4 = SessionTranscriptExporter.Export(projects, work2, id2);
            Ok("a tab that only got CA's greeting writes nothing", r4.Path == null && !r4.IsError, r4.Reason);
            Ok("and leaves no folder behind", !Directory.Exists(Path.Combine(work2, ".CAChats")));
            var r5 = SessionTranscriptExporter.Export(projects, work2, "33333333-cccc-4ccc-8ccc-cccccccccccc");
            Ok("an id with no transcript writes nothing", r5.Path == null && !r5.IsError && r5.Reason == "no transcript", r5.Reason);
            var r6 = SessionTranscriptExporter.Export(projects, work2, "not-a-guid");
            Ok("a bad id writes nothing", r6.Path == null && !r6.IsError);
            var r7 = SessionTranscriptExporter.Export(projects, Path.Combine(root, "gone"), id2);
            Ok("a missing work folder writes nothing", r7.Path == null && !r7.IsError);

            Console.WriteLine("Tool lines");
            const string id6 = "66666666-ffff-4fff-8fff-ffffffffffff";
            WriteTranscript(projects, work2, id6,
                User("2026-11-19", "Mira el repo, por favor"),
                AsstBlocks("2026-11-19", "[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"description\":\"First\"}}]"),
                AsstBlocks("2026-11-19", "[{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"C:\\\\a.txt\"}}]"),
                AsstBlocks("2026-11-19", "[{\"type\":\"text\",\"text\":\"Listo.\"}]"));
            var r9 = SessionTranscriptExporter.Export(projects, work2, id6);
            string mdTools = r9.Path != null ? File.ReadAllText(r9.Path) : "";
            Ok("consecutive tool lines are one list", mdTools.Contains("- `[Bash]` First\r\n- `[Read]` C:\\a.txt\r\n"), r9.Reason);

            Console.WriteLine("Limits and batches");
            const string id3 = "44444444-dddd-4ddd-8ddd-dddddddddddd";
            string big = new string('x', SessionTranscriptExporter.MaxMessageChars + 500);
            WriteTranscript(projects, work2, id3, User("2026-11-19", big));
            var r8 = SessionTranscriptExporter.Export(projects, work2, id3);
            string mdBig = r8.Path != null ? File.ReadAllText(r8.Path) : "";
            Ok("one huge message is cut", mdBig.Contains("500 more characters left out") && mdBig.Length < big.Length, r8.Reason);

            var req = new SessionExportRequest { WorkDir = work2 };
            req.Ids.Add(id3); req.Ids.Add(id3.ToUpperInvariant()); req.Ids.Add(id2);
            var all = SessionTranscriptExporter.ExportAll(projects, new[] { req });
            Ok("ExportAll lists a session once however it is spelled", all.Count == 2, all.Count.ToString());

            Ok("a transcript being written by claude can be read", ReadWhileWritten(projects, work));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine((pass + fail) + " checks, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    // Claude Code keeps the file open for append while it runs: the export must not need exclusive access.
    static bool ReadWhileWritten(string projects, string work)
    {
        const string id = "55555555-eeee-4eee-8eee-eeeeeeeeeeee";
        WriteTranscript(projects, work, id, User("2026-11-19", "Hola, esto es una prueba"), Asst("2026-11-19", "Respuesta"));
        string path = TranscriptPath(projects, work, id);
        using (var held = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var r = SessionTranscriptExporter.Export(projects, work, id);
            return r.Path != null && !r.IsError;
        }
    }
}
