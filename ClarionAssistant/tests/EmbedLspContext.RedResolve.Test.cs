using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

// Harness for PR #228 — the CA Embeditor finds the generated module through the .red, including a
// redirection that lives only under a BUILD section ([Debug32]/[Release32]/...), not just [Common].
// Compiles the REAL EmbedLspContext.cs + RedFileService.cs + EncodingHelper.cs; only the IDE-coupled
// types are stubbed (EmbedLspContext.RedResolve.Stubs.cs).
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class EmbedLspContextRedResolveTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static string Root;

    static string Touch(string rel)
    {
        string p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(p, "  MEMBER('Demo.clw')\r\n");
        return p;
    }

    static RedFileService LoadRed(string name, string body, Dictionary<string, string> macros = null)
    {
        string p = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(p, body);
        var red = new RedFileService();
        if (!red.Load(p, macros ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))
            throw new Exception("could not load fixture .red " + p);
        return red;
    }

    static bool SamePath(string a, string b)
    {
        return a != null && b != null &&
               string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    static int Main()
    {
        Root = Path.Combine(Path.GetTempPath(), "ca-embedred-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Root);
        try
        {
            string app = Path.Combine(Root, "app", "Demo.app");
            Directory.CreateDirectory(Path.GetDirectoryName(app));
            File.WriteAllText(app, "");

            string gen32 = Touch(@"gen32\Demo001.clw");
            string rel32 = Touch(@"rel32\Demo002.clw");
            string common = Touch(@"common\Demo003.clw");
            string both32 = Touch(@"gen32\Demo004.clw");
            Touch(@"common\Demo004.clw");               // same name under Common — Debug32 must win

            // Relative entries, anchored at the .app directory (Clarion's rule; ResolveFrom's baseDir).
            var red = LoadRed("sections.red",
                "-- fixture for PR #228: generated sources redirected under build sections\r\n" +
                "[Debug32]\r\n" +
                "Demo001.clw = ..\\gen32\r\n" +
                "Demo004.clw = ..\\gen32\r\n" +
                "[Release32]\r\n" +
                "Demo002.clw = ..\\rel32\r\n" +
                "[Common]\r\n" +
                "*.clw = ..\\common\r\n");

            // THE #228 triage case: the redirection exists ONLY under [Debug32].
            string r1 = EmbedLspContext.ResolveModulePath(app, "Demo001.clw", red);
            Ok("a module redirected only under [Debug32] resolves", SamePath(r1, gen32), "got " + (r1 ?? "null"));

            string r2 = EmbedLspContext.ResolveModulePath(app, "Demo002.clw", red);
            Ok("a module redirected only under [Release32] resolves", SamePath(r2, rel32), "got " + (r2 ?? "null"));

            string r3 = EmbedLspContext.ResolveModulePath(app, "Demo003.clw", red);
            Ok("[Common] is still the fallback", SamePath(r3, common), "got " + (r3 ?? "null"));

            string r4 = EmbedLspContext.ResolveModulePath(app, "Demo004.clw", red);
            Ok("[Debug32] takes precedence over [Common]", SamePath(r4, both32), "got " + (r4 ?? "null"));

            // Legacy [Debug]/[Release] names are in the order too.
            string legacy = Touch(@"legacy\Demo005.clw");
            var redLegacy = LoadRed("legacy.red", "[Release]\r\n*.clw = ..\\legacy\r\n");
            string r5 = EmbedLspContext.ResolveModulePath(app, "Demo005.clw", redLegacy);
            Ok("a module redirected only under legacy [Release] resolves", SamePath(r5, legacy), "got " + (r5 ?? "null"));

            // The pre-existing probe still wins when the module sits next to the .app.
            string beside = Touch(@"app\Demo006.clw");
            Touch(@"common\Demo006.clw");
            string r6 = EmbedLspContext.ResolveModulePath(app, " Demo006.clw ", red);
            Ok("a module next to the .app is used first (and the name is trimmed)", SamePath(r6, beside), "got " + (r6 ?? "null"));

            // An .app whose own project folder has its own .red is resolved through THAT .red, not the
            // solution's (Active) — but only under the running version's name (Clarion120.red here); Clarion
            // ignores any other *.red in the folder. The solution .red is loaded the way the IDE loads it:
            // LoadForProject(solution folder, version config).
            string binRed = Path.Combine(Root, "bin", "Clarion120.red");
            Directory.CreateDirectory(Path.GetDirectoryName(binRed));
            File.WriteAllText(binRed, "[Common]\r\n*.clw = ..\\common\r\n");
            string slnDir = Path.Combine(Root, "sln");
            Directory.CreateDirectory(slnDir);
            File.WriteAllText(Path.Combine(slnDir, "Other.red"), "[Common]\r\n*.clw = ..\\decoy\r\n");   // not version-named
            var solutionRed = new RedFileService();
            bool loaded = solutionRed.LoadForProject(slnDir, new ClarionVersionConfig
            {
                RedFileName = "Clarion120.red",
                RedFilePath = binRed,
                Macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "GENROOT", Root } }
            });
            Ok("LoadForProject ignores a solution-folder .red that isn't version-named",
               loaded && SamePath(solutionRed.RedFilePath, binRed), "loaded " + solutionRed.RedFilePath);

            string ProjApp(string name)
            {
                string a = Path.Combine(Root, name, "Demo.app");
                Directory.CreateDirectory(Path.GetDirectoryName(a));
                File.WriteAllText(a, "");
                return a;
            }
            string mine = Touch(@"proj2gen\Demo007.clw");
            Touch(@"common\Demo007.clw");
            Touch(@"decoy\Demo007.clw");
            string viaMacro = Touch(@"macrogen\Demo008.clw");

            string proj2 = ProjApp("proj2");
            File.WriteAllText(Path.Combine(Root, "proj2", "Clarion120.red"),
                "[Debug32]\r\nDemo008.clw = %GENROOT%\\macrogen\r\n[Common]\r\n*.clw = ..\\proj2gen\r\n");
            string r7 = EmbedLspContext.ResolveModulePath(proj2, "Demo007.clw", solutionRed);
            Ok("an .app with its own version-named .red resolves through it, not the solution's", SamePath(r7, mine),
               "got " + (r7 ?? "null"));
            Ok("...without replacing RedFileService.Active", ReferenceEquals(RedFileService.Active, solutionRed));
            string r8 = EmbedLspContext.ResolveModulePath(proj2, "Demo008.clw", solutionRed);
            Ok("the project .red is expanded with the solution's macros", SamePath(r8, viaMacro), "got " + (r8 ?? "null"));

            // A *.red Clarion would ignore (wrong name) must not be honoured.
            string proj3 = ProjApp("proj3");
            File.WriteAllText(Path.Combine(Root, "proj3", "MyApp.red"), "[Common]\r\n*.clw = ..\\decoy\r\n");
            string r10 = EmbedLspContext.ResolveModulePath(proj3, "Demo007.clw", solutionRed);
            Ok("an unrelated *.red in the .app folder is ignored (solution .red used)",
               SamePath(r10, Path.Combine(Root, "common", "Demo007.clw")), "got " + (r10 ?? "null"));

            // Several *.red: only the version-named one counts, whatever order the folder lists them in
            // ("Aaa.red" sorts first).
            string proj4 = ProjApp("proj4");
            File.WriteAllText(Path.Combine(Root, "proj4", "Aaa.red"), "[Common]\r\n*.clw = ..\\decoy\r\n");
            File.WriteAllText(Path.Combine(Root, "proj4", "Clarion120.red"), "[Common]\r\n*.clw = ..\\proj2gen\r\n");
            string r11 = EmbedLspContext.ResolveModulePath(proj4, "Demo007.clw", solutionRed);
            Ok("with several *.red, the version-named one wins", SamePath(r11, mine), "got " + (r11 ?? "null"));

            string r9 = EmbedLspContext.ResolveModulePath(app, "Demo003.clw", solutionRed);
            Ok("an .app folder without a .red still uses the solution's", SamePath(r9, common), "got " + (r9 ?? "null"));
            // Give-up paths: nowhere on disk, and no .red loaded.
            Ok("a module found nowhere returns null", EmbedLspContext.ResolveModulePath(app, "Nope.clw", red) == null);
            Ok("no .red loaded and not next to the .app returns null",
               EmbedLspContext.ResolveModulePath(app, "Demo001.clw", null) == null);
            Ok("no app name returns null", EmbedLspContext.ResolveModulePath(null, "Demo001.clw", red) == null);
        }
        finally
        {
            try { Directory.Delete(Root, true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }
}
