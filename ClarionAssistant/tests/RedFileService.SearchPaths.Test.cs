using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClarionAssistant.Services;

// Harness for the .red search-path answers the agent is told to use instead of searching drives (5a5581e6).
//
// THE BUG. Asked about an error in UltimateSQL.tpl, the in-IDE agent grepped the IDE's install folder and then
// searched whole drives, where 60+ stale copies live; resolve_red_path would have named the one the build uses
// instantly. Nothing told it templates go through the .red: get_solution_info listed only .clw/.inc paths, and
// get_red_search_paths handed back ".\Source"-style entries that are relative to the solution folder, which
// a Grep from anywhere else silently misreads. And resolve_red_path's own fallback anchored ".\" at the process
// CWD (the Clarion bin), not the solution.
//
//   T  GetSearchPathsForAny: one walk over the .red in its own order, an entry joins when ANY extension
//      matches - *.tp? (both .tpl and .tpw), *.tpl, *.tpw - so tplSearchPaths keeps the build's precedence.
//   A  AnchorAt / AnchorAll: relative entries hang off the solution folder, absolute ones are untouched,
//      order kept, duplicates dropped; no base dir = no answer (never the process CWD).
//   R  ResolveFrom finds the file exactly where AnchorAll says the directory is (the same anchoring rule).
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class RedFileServiceSearchPathsTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
    }

    static string Show(IEnumerable<string> xs) { return xs == null ? "(null)" : string.Join(" | ", xs); }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-red-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sln = Path.Combine(root, "MySolution");
            string shared = Path.Combine(root, "SharedTemplates");
            string red = Path.Combine(sln, "Clarion110.red");
            Write(red, "[Common]\r\n"
                     + "*.tpl = " + shared + "\r\n"
                     + "*.tp? = .;%ROOT%\\template\\win\r\n"
                     + "*.tpw = .\\Source\r\n"
                     + "*.clw = .\\Source;" + shared + "\r\n"
                     + "*.inc = .\r\n"
                     + "[Debug32]\r\n*.tpl = C:\\DebugOnly\r\n");
            var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "root", "C:\\Clarion11" } };
            var svc = new RedFileService();
            Ok("the fixture .red loads", svc.Load(red, macros));

            // ---- T ----
            var tpl = svc.GetSearchPathsForAny(new[] { ".tpl", ".tpw" });
            Ok("T templates: every *.tpl / *.tp? / *.tpw entry of [Common], in .red order",
               tpl.SequenceEqual(new[] { shared, ".", "C:\\Clarion11\\template\\win", ".\\Source" }), Show(tpl));
            Ok("T ...extensions without the dot work too",
               svc.GetSearchPathsForAny(new[] { "tpl", "tpw" }).SequenceEqual(tpl), Show(svc.GetSearchPathsForAny(new[] { "tpl", "tpw" })));
            Ok("T ...a .tpl-only lookup skips the *.tpw entry",
               svc.GetSearchPaths(".tpl").SequenceEqual(new[] { shared, ".", "C:\\Clarion11\\template\\win" }), Show(svc.GetSearchPaths(".tpl")));
            Ok("T ...the section argument is honoured",
               svc.GetSearchPathsForAny(new[] { ".tpl" }, "Debug32").SequenceEqual(new[] { "C:\\DebugOnly" }),
               Show(svc.GetSearchPathsForAny(new[] { ".tpl" }, "Debug32")));
            Ok("T ...no extensions, or an absent section, is an empty list",
               svc.GetSearchPathsForAny(new string[0]).Count == 0 && svc.GetSearchPathsForAny(new[] { ".tpl" }, "Nope").Count == 0
               && svc.GetSearchPathsForAny(null).Count == 0);

            // ---- A ----
            var abs = RedFileService.AnchorAll(tpl, sln);
            Ok("A relative entries are anchored at the solution folder, absolute ones untouched, order kept",
               abs != null && abs.SequenceEqual(new[] { shared, sln, "C:\\Clarion11\\template\\win", Path.Combine(sln, "Source") },
                                                StringComparer.OrdinalIgnoreCase), Show(abs));
            var dup = RedFileService.AnchorAll(new[] { ".", sln, ".\\Source", sln + "\\Source\\" }, sln);
            Ok("A duplicates (the same folder written two ways) are dropped",
               dup != null && dup.Count == 2, Show(dup));
            Ok("A no base dir: no anchored list at all (a relative entry would land on the CWD)",
               RedFileService.AnchorAll(tpl, null) == null && RedFileService.AnchorAll(tpl, "") == null);
            Ok("A AnchorAt: '..\\x' climbs from the solution folder",
               string.Equals(RedFileService.AnchorAt("..\\x", sln), Path.Combine(root, "x"), StringComparison.OrdinalIgnoreCase),
               RedFileService.AnchorAt("..\\x", sln));
            Ok("A AnchorAt: an empty or invalid path is null, not a throw",
               RedFileService.AnchorAt("", sln) == null && RedFileService.AnchorAt("bad|<path>", sln) == null);

            // ---- R ----
            Write(Path.Combine(sln, "Source", "Mine.tpw"), "#GROUP(%Mine)\r\n");
            Write(Path.Combine(shared, "UltimateSQL.tpl"), "#TEMPLATE(UltimateSQL,'x')\r\n");
            Write(Path.Combine(sln, "UltimateSQL.tpl"), "#TEMPLATE(UltimateSQL,'stale copy')\r\n");
            string tpw = svc.ResolveFrom("Mine.tpw", sln);
            Ok("R a template in .\\Source resolves where AnchorAll puts .\\Source",
               tpw != null && string.Equals(Path.GetDirectoryName(tpw), abs[3], StringComparison.OrdinalIgnoreCase), tpw);
            string usql = svc.ResolveFrom("UltimateSQL.tpl", sln);
            Ok("R the first .red entry wins over a copy in the solution folder (order is precedence)",
               usql != null && usql.StartsWith(shared, StringComparison.OrdinalIgnoreCase), usql);
            Ok("R the build sections fall through to Common when Debug32's folder lacks the file",
               svc.ResolveFrom("UltimateSQL.tpl", sln, RedFileService.BuildSectionOrder) == usql);

            // ---- C (pipeline run 1, item 1): the IDE's configuration picks the section; others are shown, never chosen ----
            string cfgRoot = Path.Combine(root, "Cfg");
            string dbg = Path.Combine(cfgRoot, "dbg"), rel = Path.Combine(cfgRoot, "rel"), com = Path.Combine(cfgRoot, "com");
            Write(Path.Combine(cfgRoot, "Clarion110.red"), "[Debug32]\r\n*.inc = " + dbg + "\r\n[Release32]\r\n*.inc = " + rel
                + "\r\n[Common]\r\n*.inc = " + com + "\r\n[Copy]\r\n*.inc = " + Path.Combine(cfgRoot, "copy") + "\r\n");
            foreach (string d in new[] { dbg, rel, com, Path.Combine(cfgRoot, "copy") }) Write(Path.Combine(d, "Both.inc"), "!\r\n");
            Write(Path.Combine(dbg, "DebugOnly.inc"), "!\r\n");
            var cfg = new RedFileService();
            cfg.Load(Path.Combine(cfgRoot, "Clarion110.red"), null);
            Ok("C SectionsFor: Release -> Release32, Release, Common; unknown -> Common only",
               RedResolution.SectionsFor("Release").SequenceEqual(new[] { "Release32", "Release", "Common" })
               && RedResolution.SectionsFor(null).SequenceEqual(new[] { "Common" }) && RedResolution.SectionsFor(" ").SequenceEqual(new[] { "Common" }),
               Show(RedResolution.SectionsFor("Release")));
            var relAns = RedResolution.Resolve(cfg, "Both.inc", cfgRoot, RedResolution.SectionsFor("Release"));
            Ok("C a Release build gets the [Release32] copy, not the [Debug32] one",
               relAns.Path != null && relAns.Path.StartsWith(rel, StringComparison.OrdinalIgnoreCase) && relAns.Section == "Release32", relAns.Path);
            Ok("C ...and the [Debug32] copy is listed in alsoFoundIn ([Copy] is not searched)",
               relAns.AlsoFoundIn.Count == 1 && relAns.AlsoFoundIn[0].Section == "Debug32", Show(relAns.AlsoFoundIn.Select(h => h.Section + "=" + h.Path)));
            var unk = RedResolution.Resolve(cfg, "Both.inc", cfgRoot, RedResolution.SectionsFor(null));
            Ok("C configuration unknown: [Common] only, both build sections' copies listed",
               unk.Path != null && unk.Path.StartsWith(com, StringComparison.OrdinalIgnoreCase)
               && unk.AlsoFoundIn.Select(h => h.Section).OrderBy(s => s).SequenceEqual(new[] { "Debug32", "Release32" }),
               unk.Path + " / " + Show(unk.AlsoFoundIn.Select(h => h.Section)));
            var dOnly = RedResolution.Resolve(cfg, "DebugOnly.inc", cfgRoot, RedResolution.SectionsFor("Release"));
            Ok("C a file only under [Debug32] is NOT found for a Release build, and is shown in alsoFoundIn",
               dOnly.Path == null && dOnly.AlsoFoundIn.Count == 1 && dOnly.AlsoFoundIn[0].Section == "Debug32", dOnly.Path);

            // ---- U (run 2, item 1): the language server's Debug answer is used only for a KNOWN Debug configuration ----
            string lspDebug = Path.Combine(dbg, "Both.inc");
            var unkLocal = RedResolution.Resolve(cfg, "Both.inc", cfgRoot, RedResolution.SectionsFor(null));
            var dUnknown = RedResolution.Decide(unkLocal, lspDebug, "Debug", false);
            Ok("U unknown configuration: the local [Common] answer stands, the server's Debug path is in alsoFoundIn",
               dUnknown.Resolver == "redfile" && dUnknown.Path != null && dUnknown.Path.StartsWith(com, StringComparison.OrdinalIgnoreCase)
               && dUnknown.AlsoFoundIn.Any(h => h.Section.Contains("language server") && h.Path == lspDebug), dUnknown.Path);
            var noLocal = RedResolution.Resolve(cfg, "DebugOnly.inc", cfgRoot, RedResolution.SectionsFor(null));
            var dOnlyLsp = RedResolution.Decide(noLocal, Path.Combine(dbg, "DebugOnly.inc"), "Debug", false);
            Ok("U unknown configuration and a Debug-only server hit: NOT found, the hit in alsoFoundIn",
               dOnlyLsp.Path == null && dOnlyLsp.AlsoFoundIn.Any(h => h.Section.Contains("language server")), Show(dOnlyLsp.AlsoFoundIn.Select(h => h.Section)));
            var dKnown = RedResolution.Decide(RedResolution.Resolve(cfg, "Both.inc", cfgRoot, RedResolution.SectionsFor("Debug")), lspDebug, "Debug", true);
            Ok("U a KNOWN Debug configuration uses the server's answer", dKnown.Resolver == "lsp" && dKnown.Path == lspDebug, dKnown.Path);

            // ---- N (run 2, item 4): no base folder - relative entries are skipped, never the process CWD ----
            string nRoot = Path.Combine(root, "N");
            Write(Path.Combine(nRoot, "rel", "Cwd.inc"), "!\r\n");
            Write(Path.Combine(nRoot, "abs", "Cwd.inc"), "!\r\n");
            Write(Path.Combine(nRoot, "n.red"), "[Common]\r\n*.inc = .\\rel;" + Path.Combine(nRoot, "abs") + "\r\n");
            var nRed = new RedFileService();
            nRed.Load(Path.Combine(nRoot, "n.red"), null);
            string cwdBefore = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = nRoot;   // where ".\rel" WOULD land
                var nHits = nRed.FindInSections("Cwd.inc", null, new[] { "Common" });
                Ok("N no base folder: '.\\rel' is skipped and the absolute entry answers",
                   nHits.Count == 1 && nHits[0].Path.StartsWith(Path.Combine(nRoot, "abs"), StringComparison.OrdinalIgnoreCase),
                   Show(nHits.Select(h => h.Path)));
            }
            finally { Environment.CurrentDirectory = cwdBefore; }

            // ---- S (item 2): search paths walk the same sections, in the same order ----
            Ok("S SearchPaths for Release = [Release32] then [Common] folders, as Resolve walks them",
               RedResolution.SearchPaths(cfg, new[] { ".inc" }, RedResolution.SectionsFor("Release")).SequenceEqual(new[] { rel, com }),
               Show(RedResolution.SearchPaths(cfg, new[] { ".inc" }, RedResolution.SectionsFor("Release"))));

            // ---- P (item 3): relative entries hang off the PROJECT's folder, and its own .red applies ----
            string psln = Path.Combine(root, "Multi");
            Write(Path.Combine(psln, "Multi.sln"),
                "Project(\"{12345678-1234-1234-1234-123456789012}\") = \"ProjA\", \"ProjA\\ProjA.cwproj\", \"{AAAAAAAA-0000-0000-0000-000000000001}\"\r\nEndProject\r\n"
              + "Project(\"{12345678-1234-1234-1234-123456789012}\") = \"ProjB\", \"Sub\\ProjB\\ProjB.cwproj\", \"{AAAAAAAA-0000-0000-0000-000000000002}\"\r\nEndProject\r\n");
            Write(Path.Combine(psln, "ProjA", "ProjA.cwproj"), "<Project />");
            Write(Path.Combine(psln, "Sub", "ProjB", "ProjB.cwproj"), "<Project />");
            string note, error;
            string pb = RedResolution.ProjectDirectory(Path.Combine(psln, "Multi.sln"), "projb", out note, out error);
            Ok("P project_name names the project's folder", string.Equals(pb, Path.Combine(psln, "Sub", "ProjB"), StringComparison.OrdinalIgnoreCase), pb);
            string none = RedResolution.ProjectDirectory(Path.Combine(psln, "Multi.sln"), "Nope", out note, out error);
            Ok("P an unknown project is an error naming the real ones", none == null && error != null && error.Contains("ProjA") && error.Contains("ProjB"), error);
            string multi = RedResolution.ProjectDirectory(Path.Combine(psln, "Multi.sln"), null, out note, out error);
            Ok("P no project in a multi-project solution: the solution folder, with a per-project note",
               multi == psln && note != null && note.Contains("project_name"), multi + " / " + note);
            Write(Path.Combine(root, "Single", "Single.sln"),
                "Project(\"{12345678-1234-1234-1234-123456789012}\") = \"Only\", \"App\\Only.cwproj\", \"{AAAAAAAA-0000-0000-0000-000000000003}\"\r\nEndProject\r\n");
            string single = RedResolution.ProjectDirectory(Path.Combine(root, "Single", "Single.sln"), null, out note, out error);
            Ok("P a single-project solution uses that project's folder",
               string.Equals(single, Path.Combine(root, "Single", "App"), StringComparison.OrdinalIgnoreCase) && note == null, single);
            // ProjB has its own version-named .red; relative entries resolve in ProjB's folder.
            Write(Path.Combine(psln, "Clarion110.red"), "[Common]\r\n*.inc = .\\slnonly\r\n");
            Write(Path.Combine(pb, "Clarion110.red"), "[Common]\r\n*.inc = .\\mine\r\n");
            Write(Path.Combine(pb, "mine", "Proj.inc"), "!\r\n");
            var slnRed = new RedFileService();
            slnRed.Load(Path.Combine(psln, "Clarion110.red"), null);
            var projRed = RedFileService.ForProjectDirectory(pb, slnRed);
            var pAns = RedResolution.Resolve(projRed, "Proj.inc", pb, RedResolution.SectionsFor(null));
            Ok("P the project's own .red and folder resolve its file",
               pAns.Path != null && pAns.Path.StartsWith(Path.Combine(pb, "mine"), StringComparison.OrdinalIgnoreCase), pAns.Path);

            // ---- L (item 4): bare names only; a candidate never leaves its entry's folder ----
            foreach (string bad in new[] { "", ".", "..", "..\\x.tpl", "sub\\x.tpl", "C:\\x.tpl", "C:x.tpl", "\\x.tpl", "x.tpl:ads", "x/y.tpl", "x<.tpl" })
                Ok("L rejected: '" + bad + "'", RedResolution.LeafNameError(bad) != null);
            Ok("L a bare name is fine", RedResolution.LeafNameError("UltimateSQL.tpl") == null);
            Write(Path.Combine(cfgRoot, "Escaped.inc"), "!\r\n");
            Ok("L FindInSections never matches a name that climbs out of the entry's folder",
               cfg.FindInSections("..\\Escaped.inc", cfgRoot, new[] { "Common" }).Count == 0);

            // ---- D (item 5): drive-relative entries ----
            char cwdDrive = char.ToUpperInvariant(Environment.CurrentDirectory[0]);
            string fakeDrive = cwdDrive == 'Z' ? "Y" : "Z";             // a drive that is NOT the process's
            string fakeBase = fakeDrive + ":\\Proj\\App";
            Ok("D '\\x' takes the base folder's drive, not the process's",
               string.Equals(RedFileService.AnchorAt("\\slsshared\\x", fakeBase), fakeDrive + ":\\slsshared\\x", StringComparison.OrdinalIgnoreCase),
               RedFileService.AnchorAt("\\slsshared\\x", fakeBase));
            Ok("D 'Z:foo' on the base folder's drive is relative to the base folder",
               string.Equals(RedFileService.AnchorAt(fakeDrive + ":foo", fakeBase), fakeBase + "\\foo", StringComparison.OrdinalIgnoreCase),
               RedFileService.AnchorAt(fakeDrive + ":foo", fakeBase));
            string otherDrive = (fakeDrive == "Y" ? "X" : "Y") + ":foo";
            Ok("D a drive-relative entry on another drive is not anchored, and is flagged",
               RedFileService.AnchorAt(otherDrive, fakeBase) == null
               && RedFileService.Unanchorable(new[] { otherDrive }, fakeBase).Count == 1
               && RedFileService.AnchorAll(new[] { otherDrive }, fakeBase).Count == 0);
            Ok("D a UNC path is untouched", RedFileService.AnchorAt("\\\\server\\share\\x", fakeBase) == "\\\\server\\share\\x",
               RedFileService.AnchorAt("\\\\server\\share\\x", fakeBase));

            // ---- M (item 6): unexpanded macros are not folders ----
            var withMacro = new[] { "%NOPE%\\libsrc", "C:\\Real" };
            Ok("M an unexpanded %MACRO% entry is left out of the anchored list",
               RedFileService.AnchorAll(withMacro, sln).SequenceEqual(new[] { "C:\\Real" }), Show(RedFileService.AnchorAll(withMacro, sln)));
            Ok("M ...and listed as unresolved", RedFileService.Unanchorable(withMacro, sln).Count == 1
               && RedFileService.Unanchorable(withMacro, sln)[0].StartsWith("%NOPE%\\libsrc"), Show(RedFileService.Unanchorable(withMacro, sln)));

            // ---- E (item 7): the trace and the enumerator anchor exactly as AnchorAt does ----
            // A drive-relative entry tells them apart: AnchorAt puts "<drive>:sub" under the base folder, a bare
            // GetFullPath under the drive's current folder - which this test sets to somewhere else.
            string eBase = Path.Combine(root, "E", "proj");
            Write(Path.Combine(eBase, "sub", "Trace.inc"), "!\r\n");
            Write(Path.Combine(root, "E", "Clarion110.red"), "[Common]\r\n*.inc = " + eBase.Substring(0, 2) + "sub\r\n");
            var eRed = new RedFileService();
            eRed.Load(Path.Combine(root, "E", "Clarion110.red"), null);
            string savedCwd = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = Path.Combine(root, "E");
                var trace = new List<string>();
                string traced = eRed.ResolveTrace("Trace.inc", eBase, trace, "Common");
                Ok("E ResolveTrace anchors a drive-relative entry at the base folder", traced != null, Show(trace));
                bool truncated;
                var listed = eRed.EnumerateFiles(eBase, System.Threading.CancellationToken.None, out truncated, "Common");
                Ok("E EnumerateFiles anchors it the same way", listed.Any(m => m.Name == "Trace.inc"), Show(listed.Select(m => m.FullPath)));
            }
            finally { Environment.CurrentDirectory = savedCwd; }
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  [FAIL] threw: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail));
        return fail == 0 ? 0 : 1;
    }
}
