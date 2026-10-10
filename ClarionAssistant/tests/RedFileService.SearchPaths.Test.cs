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
