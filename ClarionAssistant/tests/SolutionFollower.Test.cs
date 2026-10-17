// Harness for Services\SolutionFollower.cs (ticket 9e1bd280): every tool in a tab answers for the solution the
// IDE has open NOW. The defect: the Owner opened UltimateSQLTest in the IDE, the header followed, and the open
// tab "CA1 · v61POSitive" kept answering get_solution_info for v61POSitive.sln (and its Clarion10v61.red) -
// --solution was a launch-time snapshot that only the LSP outgrew (77aceec5).
//
// Compiled against the REAL SolutionFollower.cs and IdeSolutionRecord.cs by Run-Tests.ps1. The record
// directory is redirected to a temp folder; the "IDE" is this process (Publish writes its own pid).
//
// What each check stops:
//   launch-arg, no IDE      - --solution lost when nothing is followed
//   launch-arg, no record   - an IDE that has published nothing yet erasing --solution
//   ide-live switch         - THE DEFECT: the launch solution winning over the IDE's
//   closed = none           - the old solution silently kept after the IDE closed it
//   switchedFrom            - get_solution_info unable to say which solution it left
//   threads                 - a torn read while tool calls, the LSP and the heartbeat ask at once
//
// Argument 1 (passed by Run-Tests.ps1): the ClarionAssistant project dir, for the wiring checks - the
// standalone's workspace and LSP must read THIS follower, and the addin must relabel its tabs. Those files
// cannot be compiled here (IDE- and tool-registry-coupled), so they are source checks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ClarionAssistant.Services;

static class SolutionFollowerTest
{
    static int _failures, _assertions;

    static void Check(bool ok, string what)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what); }
        else Console.WriteLine("  ok   " + what);
    }

    static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    /// <summary>The text from <paramref name="start"/> to the next <paramref name="end"/>, or null when either is missing.</summary>
    static string Between(string text, string start, string end)
    {
        int s = text.IndexOf(start, StringComparison.Ordinal);
        if (s < 0) return null;
        int e = text.IndexOf(end, s + start.Length, StringComparison.Ordinal);
        return e < 0 ? null : text.Substring(s, e - s);
    }

    static int Count(string text, string what)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }

    static int Main(string[] args)
    {
        Console.WriteLine("SolutionFollower: every tool follows the IDE's open solution (9e1bd280)");
        // Run 1: a record names its writer, which must be a Clarion IDE; THIS process plays the IDE.
        Environment.SetEnvironmentVariable("CA_IDE_IMAGE_NAME", System.Diagnostics.Process.GetCurrentProcess().ProcessName);
        string root = Path.Combine(Path.GetTempPath(), "ca-follow-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            IdeSolutionRecord.RootOverride = Path.Combine(root, "records");
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            // Names of different lengths, so each Publish changes the record's size as well as its mtime.
            string a = Path.Combine(root, "v61POSitive.sln");
            string b = Path.Combine(root, "UltimateSQLTest.sln");
            string c = Path.Combine(root, "c.sln");
            foreach (var f in new[] { a, b, c }) File.WriteAllText(f, "");

            // -- not launched by an IDE: --solution, as before
            var plain = new SolutionFollower(a, () => null);
            var s0 = plain.Current();
            Check(Same(s0.Path, a) && s0.Source == SolutionSourceSnapshot.LaunchArg,
                "no IDE: --solution, source launch-arg (" + s0.Source + ": " + s0.Note + ")");
            Check(new SolutionFollower(null, () => null).Current().Source == SolutionSourceSnapshot.None,
                "no IDE, no --solution: none");

            // -- launched by an IDE that has published nothing yet: --solution is the fallback
            IdeSolutionRecord.ResetForTest();
            IdeSolutionRecord.Publish(null);
            var changes = new List<string>();
            var f1 = new SolutionFollower(a, () => self);
            f1.Changed = (was, now) => { lock (changes) changes.Add((was ?? "-") + ">" + (now ?? "-")); };
            var s1 = f1.Current();
            Check(Same(s1.Path, a) && s1.Source == SolutionSourceSnapshot.LaunchArg,
                "IDE, no record yet: --solution, launch-arg (" + s1.Source + ": " + s1.Note + ")");
            Check(s1.SwitchedFrom == null, "no switch yet: switchedFrom is null");

            // -- THE DEFECT: the IDE opens UltimateSQLTest; the tab must follow
            IdeSolutionRecord.Publish(b);
            var s2 = f1.Current();
            Check(Same(s2.Path, b), "IDE switched: the solution is the IDE's (" + s2.Path + ")");
            Check(s2.Source == SolutionSourceSnapshot.IdeLive, "IDE switched: source ide-live (" + s2.Source + ")");
            Check(Same(s2.SwitchedFrom, a) && s2.SwitchedAt.HasValue, "IDE switched: switchedFrom names the launch solution");
            Check(Same(f1.CurrentPath, b), "CurrentPath agrees with the snapshot");
            Check(changes.Count == 1 && changes[0].EndsWith(">" + b, StringComparison.OrdinalIgnoreCase),
                "Changed fired once for the switch (" + string.Join(" | ", changes) + ")");

            // -- an unchanged record: no new switch
            f1.Current(); f1.Current();
            Check(changes.Count == 1, "re-reads of an unchanged record fire nothing");

            // -- the IDE closes its solution: NONE, not the old one, and not --solution either
            IdeSolutionRecord.Publish(null);
            var s3 = f1.Current();
            Check(s3.Path == null && s3.Source == SolutionSourceSnapshot.None,
                "IDE closed the solution: none (" + s3.Source + " " + s3.Path + ")");
            Check(s3.Note != null && s3.Note.Contains("no solution is open in the IDE"),
                "IDE closed: the note says so (" + s3.Note + ")");
            Check(Same(s3.SwitchedFrom, b), "IDE closed: switchedFrom names the solution it had");

            // -- and opens another: followed, switchedFrom is the last REAL solution
            IdeSolutionRecord.Publish(c);
            var s4 = f1.Current();
            Check(Same(s4.Path, c) && s4.Source == SolutionSourceSnapshot.IdeLive, "IDE reopened: follows to c.sln");
            Check(Same(s4.SwitchedFrom, b), "through none, switchedFrom is the last real solution (" + s4.SwitchedFrom + ")");

            // -- run 1 (Codex): PID REUSE. The record's writer started at another time than the live process on its pid:
            //    not the IDE - no record, so (the IDE having published before) none, latched, with the reason.
            {
                string rf = IdeSolutionRecord.PathForPid(self);
                var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                var rec = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(rf));
                rec["startTime"] = DateTime.UtcNow.AddHours(-3).ToString("o");
                File.WriteAllText(rf, js.Serialize(rec));
                var sr = f1.Current();
                Check(sr.Path == null && sr.Source == SolutionSourceSnapshot.None && sr.Note.Contains("pid reused"),
                    "a record left by a dead IDE whose pid was reused: none, saying why (" + sr.Source + ": " + sr.Note + ")");
                IdeSolutionRecord.ResetForTest();
                IdeSolutionRecord.Publish(c);
                Check(Same(f1.CurrentPath, c), "...and a genuine record is followed again");
            }

            // -- launched with no --solution, IDE has nothing: none
            IdeSolutionRecord.Publish(null);
            var f2 = new SolutionFollower(null, () => self);
            Check(f2.Current().Source == SolutionSourceSnapshot.None, "IDE, no record, no --solution: none");
            IdeSolutionRecord.Publish(a);
            Check(Same(f2.CurrentPath, a), "...and it picks the solution up when the IDE publishes one");

            // -- threads: concurrent readers while the IDE switches never see anything but a, b or none
            var bad = new List<string>();
            int stop = 0;
            var readers = new List<Thread>();
            for (int t = 0; t < 4; t++)
            {
                var th = new Thread(() =>
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        try
                        {
                            var s = f2.Current();
                            bool ok = (s.Path == null && s.Source == SolutionSourceSnapshot.None)
                                || ((Same(s.Path, a) || Same(s.Path, b)) && s.Source == SolutionSourceSnapshot.IdeLive);
                            if (!ok) lock (bad) bad.Add(s.Source + ":" + s.Path);
                        }
                        catch (Exception ex) { lock (bad) bad.Add(ex.GetType().Name); }
                    }
                });
                th.Start();
                readers.Add(th);
            }
            for (int i = 0; i < 40; i++)
            {
                IdeSolutionRecord.Publish(i % 2 == 0 ? b : a);
                Thread.Sleep(5);
            }
            Volatile.Write(ref stop, 1);
            foreach (var th in readers) th.Join();
            Check(bad.Count == 0, "concurrent reads during switches are always whole answers (" + string.Join(", ", bad) + ")");
            IdeSolutionRecord.Publish(null);

            // ---- wiring (source checks) ----
            string proj = args.Length > 0 ? args[0] : null;
            if (string.IsNullOrEmpty(proj) || !Directory.Exists(proj))
            {
                Check(false, "project dir argument missing - wiring checks could not run");
            }
            else
            {
                string ws = File.ReadAllText(Path.Combine(proj, "mcp-server", "StandaloneWorkspace.cs"));
                Check(ws.Contains("public string CurrentSolutionPath { get { return Follower.CurrentPath; } }"),
                    "StandaloneWorkspace.CurrentSolutionPath reads the follower");
                Check(ws.Contains("public string GetHostOpenSolutionPath() { return CurrentSolutionPath; }"),
                    "GetHostOpenSolutionPath (build tools, get_solution_info) reads the same followed solution");
                Check(ws.Contains("SolutionFollower.SamePath(solutionPath, _redSolution)"),
                    "the .red cache is keyed on the solution, not the version alone");

                string prog = File.ReadAllText(Path.Combine(proj, "mcp-server", "Program.cs"));
                Check(prog.Contains("LspService.FollowedSolutionProvider = () => workspace.CurrentSolutionPath;"),
                    "the LSP follows the workspace's follower (cannot disagree with the tools)");
                Check(prog.Contains("IdeLiveTextClient.SolutionProvider = () => workspace.CurrentSolutionPath;"),
                    "lsp_diagnostics' live-text match uses the followed solution");
                Check(prog.Contains("coord.SolutionPathProvider = () => workspace.CurrentSolutionPath;"),
                    "instance coordination re-reads the followed solution");
                Check(!prog.Contains("IdeSolutionRecord.ReadCached("),
                    "no second, private reader of the IDE record left in Program.cs");

                // Run 1 (debugger LOW): get_solution_info derives the db, version, .red and RedScope from ONE snapshot.
                string reg = File.ReadAllText(Path.Combine(proj, "Services", "McpToolRegistry.cs"));
                string info = Between(reg, "Name = \"get_solution_info\"", "Name = \"resolve_red_path\"");
                Check(info != null && !info.Contains("_workspace.CurrentDbPath") && !info.Contains("_workspace.CurrentVersionConfig")
                      && !info.Contains("_workspace.RedFile"),
                    "get_solution_info re-reads none of the db / version / .red after its snapshot");
                Check(info != null && Count(info, "SnapshotSolution(") == 1 && info.Contains("snap.DbPath") && info.Contains("snap.Version")
                      && info.Contains("snap.Red") && info.Contains("RedScopeFor(new Dictionary<string, object>(), snap)"),
                    "get_solution_info takes ONE snapshot and keys the db, version, .red and RedScope on it");
                string scopeFor = Between(reg, "private RedScope RedScopeFor(Dictionary<string, object> args, SolutionScope snap)", "private static string ActiveBuildConfiguration");
                Check(scopeFor != null && scopeFor.Contains("scope.Red = snap.Red") && !scopeFor.Contains("_workspace.")
                      && !scopeFor.Contains("CurrentSolutionPath"),
                    "RedScope (resolve_red_path, get_red_search_paths) takes its .red from the same one read of the solution");
                Check(reg.Contains("WithVersion(result, decision.Resolver == \"lsp\", scope.Snap)")
                      && reg.Contains("WithVersion(WithAbsolutePaths(result, paths, scope.BaseDir), false, scope.Snap)"),
                    "resolve_red_path / get_red_search_paths report the version of the scope's snapshot");
                string snapImpl = Between(ws, "public SolutionScope Snapshot()", "public ClarionVersionConfig VersionConfigFor(");
                Check(snapImpl != null && snapImpl.Contains("ScopeOf(src.Path, src.Record, src)")
                      // e1df6c2e: the call gained ", out settings" (the settings folder note); still the same rec.
                      && snapImpl.Contains("VersionConfigFor(sln, rec, out note"),
                    "the snapshot's version is decided from the SAME record read as its solution");
                // Run 1 (code review): the two "no solution" replies speak one vocabulary; one version resolver.
                Check(info != null && !info.Contains("lastSelectedPath") && Count(info, "{ \"switchedFrom\", slnPath }") == 1,
                    "both 'no solution' replies name the solution they had as switchedFrom");
                Check(Count(ws, "ResolveVersionFor(") >= 4 && !ws.Contains("var cfg = VersionConfigFor(CurrentSolutionPath"),
                    "CurrentVersionConfig and RedFileFor share ResolveVersionFor");
                // Run 1 (debugger MEDIUM + Codex HIGH): an index run derives its .red and library paths from its snapshot.
                string held = Between(ws, "private void IndexHeld(", "catch (Exception ex)");
                Check(held != null && held.Contains("scope.Red") && held.Contains("scope.IndexLibraryPaths")
                      && !held.Contains("BuildIndexLibraryPaths()") && !held.Contains("RedFileFor(") && !held.Contains("ActiveRedFileService"),
                    "index_solution's IndexHeld uses the snapshotted scope's .red and library paths");
                string icg = Between(reg, "private object ExecuteIndexCodeGraph(", "private static string DescribeIndexEvent");
                Check(icg != null && icg.Contains("SnapshotSolution(true)") && icg.Contains("red = own.Red")
                      && !icg.Contains("_workspace.CurrentSolutionPath"),
                    "index_codegraph uses the snapshotted solution's .red and library paths");
                // Run 2 (debugger LOW): an explicit sln_path that is not the followed solution gets its own scope.
                Check(icg != null && icg.Contains("scopedWs.ScopeFor(slnPath)"),
                    "index_codegraph with another solution's sln_path uses THAT solution's scope, not the followed one's");

                string acc = File.ReadAllText(Path.Combine(proj, "AssistantChatControl.cs"));
                int onChanged = acc.IndexOf("private void OnSolutionChanged(string path)", StringComparison.Ordinal);
                int body = onChanged < 0 ? -1 : acc.IndexOf("RetitleSolutionTabs(path);", onChanged, StringComparison.Ordinal);
                int next = onChanged < 0 ? -1 : acc.IndexOf("        private ", onChanged + 10, StringComparison.Ordinal);
                Check(onChanged >= 0 && body > onChanged && (next < 0 || body < next),
                    "OnSolutionChanged relabels the tabs");
                int retitle = acc.IndexOf("private void RetitleSolutionTabs(string newPath)", StringComparison.Ordinal);
                Check(retitle >= 0 && acc.IndexOf("CaAgentIdentity.TabLabel(tab.AgentName, followed)", retitle, StringComparison.Ordinal) > retitle,
                    "the relabel keeps the agent name and swaps the solution part");
            }
        }
        finally
        {
            IdeSolutionRecord.RootOverride = null;
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(_failures == 0
            ? "PASS - " + _assertions + " assertions"
            : "FAIL - " + _failures + " of " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }
}
