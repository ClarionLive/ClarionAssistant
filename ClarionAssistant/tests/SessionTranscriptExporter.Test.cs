using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClarionAssistant.Services;

// Harness for the chat transcript export: a Claude session is written to <solution folder>\.CAChats as a Markdown file
// named with its first and last day, when its tab closes or the IDE does. Compiles the REAL
// Services\SessionTranscriptExporter.cs and Services\ClaudeSessionCatalog.cs (no IDE coupling)
// against fixture transcripts written to %TEMP%.
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

    static string J(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n"); }

    // Transcript lines in Claude Code's compact JSON. Times are noon UTC so the local day is the same one
    // in every time zone from UTC-11 to UTC+11.
    static string Ts(string day) { return ",\"timestamp\":\"" + day + "T12:00:00.000Z\""; }
    static string AgentName(string n) { return "{\"type\":\"agent-name\",\"agentName\":\"" + n + "\"}"; }
    static string User(string day, string text, string extra = "")
    {
        return "{\"isSidechain\":false" + extra + ",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + J(text) + "\"}" + Ts(day) + "}";
    }
    static string UserBlocks(string day, string blocks)
    {
        return "{\"isSidechain\":false,\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + blocks + "}" + Ts(day) + "}";
    }
    static string Asst(string day, string text, bool sidechain = false)
    {
        return "{\"isSidechain\":" + (sidechain ? "true" : "false") + ",\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + J(text) + "\"}]}" + Ts(day) + "}";
    }
    static string AsstBlocks(string day, string blocks)
    {
        return "{\"isSidechain\":false,\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":" + blocks + "}" + Ts(day) + "}";
    }

    static void WriteTranscript(string projectsRoot, string workDir, string id, params string[] lines)
    {
        string dir = Path.Combine(projectsRoot, ClaudeSessionCatalog.EncodeProjectPath(workDir));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".jsonl"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    static void AppendTranscript(string projectsRoot, string workDir, string id, params string[] lines)
    {
        string dir = Path.Combine(projectsRoot, ClaudeSessionCatalog.EncodeProjectPath(workDir));
        File.AppendAllText(Path.Combine(dir, id + ".jsonl"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    static string[] SessionFiles(string workDir)
    {
        string folder = SessionTranscriptExporter.FolderFor(workDir);
        if (!Directory.Exists(folder)) return new string[0];
        return Directory.GetFiles(folder, "session_*.md").Select(Path.GetFileName).OrderBy(x => x).ToArray();
    }

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
            string name1 = SessionTranscriptExporter.FileName(id1, new DateTime(2026, 11, 19), new DateTime(2026, 11, 20));
            Ok("named with the first and last day", r.Path != null && Path.GetFileName(r.Path) == name1 && name1 == "session_11111111_2026-11-19_to_2026-11-20.md", r.Path);
            Ok("written to <solution>\\.CAChats", r.Path != null && Path.GetDirectoryName(r.Path) == Path.Combine(work, ".CAChats"), r.Path);
            string md = r.Path != null ? File.ReadAllText(r.Path) : "";
            Ok("header names the session, the tab and the period",
               md.Contains("`" + id1 + "`") && md.Contains("- Tab: CA1") && md.Contains("From: 2026-11-19") && md.Contains("To: 2026-11-20"));
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
            File.WriteAllText(Path.Combine(work, ".CAChats", "session_11111111_notes.md"), "mine");
            File.WriteAllText(Path.Combine(work, ".CAChats", "other.md"), "mine");
            var r2 = SessionTranscriptExporter.Export(projects, work, id1);
            Ok("re-exported under the new end date", r2.Path != null && Path.GetFileName(r2.Path) == "session_11111111_2026-11-19_to_2026-11-21.md", r2.Path + " " + r2.Reason);
            var files = SessionFiles(work);
            Ok("one file per session: the earlier one is gone",
               files.Contains("session_11111111_2026-11-19_to_2026-11-21.md") && !files.Contains(name1), string.Join(",", files));
            Ok("files that are not this class's are left alone",
               File.Exists(Path.Combine(work, ".CAChats", "session_11111111_notes.md")) && File.Exists(Path.Combine(work, ".CAChats", "other.md")));
            var r3 = SessionTranscriptExporter.Export(projects, work, id1);
            Ok("exporting again changes nothing", r3.Path == r2.Path && SessionFiles(work).Length == 2 /* the export + the notes file */);

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
        string path = Path.Combine(projects, ClaudeSessionCatalog.EncodeProjectPath(work), id + ".jsonl");
        using (var held = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var r = SessionTranscriptExporter.Export(projects, work, id);
            return r.Path != null && !r.IsError;
        }
    }
}
