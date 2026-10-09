using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClarionAssistant.Services;

// Harness for RedFileService following {include} lines, and reloading when the .red changes (GH #261).
//
// THE BUG. A project-local Clarion110.red of the shape
//     {include %BIN%\clarion110.red}
//     [Common]
//     *.clw = \slsshared\libsrc\adminModels;...
// resolved NONE of the app's generated modules (.\clw\*.clw): Parse() skipped the {include} line, so the
// default .red's *.clw = .\clw entry never existed, and 359 cwproj-listed files came back 'unresolved'.
// And a [section] named twice REPLACED the earlier one, so following the include alone would still have
// lost the included [Common] the moment the local [Common] header was read. Clarion's own parser
// (Clarion.Core RedirectionFile.Load) reads the include in place and keeps both.
//
// Second half: the standalone server cached the parsed .red until the Clarion version changed, so an edit
// to the .red changed nothing until a restart. IsStale() is what the hosts now check.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class RedFileServiceIncludeTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static ClarionVersionConfig Config(string root)
    {
        string bin = Path.Combine(root, "bin");
        return new ClarionVersionConfig
        {
            Name = "Clarion 11.0.13401",
            RootPath = root,
            BinPath = bin,
            RedFileName = "Clarion110.red",
            RedFilePath = Path.Combine(bin, "Clarion110.red"),
            Macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "root", root } }
        };
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
    }

    static string Show(IEnumerable<string> xs) { return string.Join(" | ", xs); }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-red-include-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ---- Kevin's layout (GH #261) ----
            string clarion = Path.Combine(root, "Clarion11");
            string lib = Path.Combine(root, "slsshared", "libsrc", "adminModels");
            string sln = Path.Combine(root, "WebTXAdmin");
            Write(Path.Combine(clarion, "bin", "Clarion110.red"),
                  "-- default redirection\r\n[Common]\r\n*.clw = .\\clw;.\\libsrc;%ROOT%\\libsrc\\win\r\n*.inc = .\\inc;.\\libsrc;%ROOT%\\libsrc\\win\r\n");
            Write(Path.Combine(sln, "Clarion110.red"),
                  "-- Redirection for SLSDev Clarion 11.0\r\n{include %BIN%\\clarion110.red}\r\n[Common]\r\n"
                  + "*.inc = " + lib + ";\r\n*.clw = " + lib + ";\r\n");
            Write(Path.Combine(sln, "clw", "AdminGlobal.clw"), "  PROGRAM\r\n");
            Write(Path.Combine(lib, "AdminModel.clw"), "  MEMBER\r\n");

            var red = new RedFileService();
            bool ok = red.LoadForProject(sln, Config(clarion));
            Ok("the local Clarion110.red loads", ok && string.Equals(red.RedFilePath, Path.Combine(sln, "Clarion110.red"),
               StringComparison.OrdinalIgnoreCase), red.RedFilePath);
            Ok("the {include}d default .red is followed", red.LoadedFiles.Count == 2, Show(red.LoadedFiles));
            Ok("...and nothing was skipped", red.SkippedIncludes.Count == 0, Show(red.SkippedIncludes));

            string found = red.ResolveFrom("AdminGlobal.clw", sln, "Common", "Debug", "Release");
            Ok("a generated module in .\\clw resolves (the 359 'unresolved' files)",
               found != null && found.EndsWith(Path.Combine("clw", "AdminGlobal.clw"), StringComparison.OrdinalIgnoreCase), found);
            Ok("the local [Common] entry still resolves", red.ResolveFrom("AdminModel.clw", sln) != null);

            var clw = red.GetSearchPaths(".clw");
            Ok("*.clw search order: the included entries first, then the local ones (Clarion's textual order)",
               clw.Count == 4 && clw[0] == ".\\clw" && clw[1] == ".\\libsrc" && clw[3] == lib, Show(clw));
            Ok("%ROOT% inside the included file is expanded",
               clw.Count > 2 && clw[2].Equals(Path.Combine(clarion, "libsrc", "win"), StringComparison.OrdinalIgnoreCase), Show(clw));

            // ---- staleness ----
            Ok("not stale right after loading", !red.IsStale());
            File.SetLastWriteTimeUtc(Path.Combine(sln, "Clarion110.red"), DateTime.UtcNow.AddMinutes(1));
            Ok("an edit to the local .red makes it stale", red.IsStale());
            red.LoadForProject(sln, Config(clarion));
            File.SetLastWriteTimeUtc(Path.Combine(clarion, "bin", "Clarion110.red"), DateTime.UtcNow.AddMinutes(2));
            Ok("an edit to the INCLUDED .red makes it stale too", red.IsStale());
            Ok("a never-loaded instance is not stale", !new RedFileService().IsStale());

            // ---- self-include: %BIN% pointing at the local folder must not loop ----
            var selfCfg = Config(clarion);
            selfCfg.BinPath = sln;
            var self = new RedFileService();
            bool selfOk = self.LoadForProject(sln, selfCfg);
            Ok("a .red that includes itself loads (no hang, no throw)", selfOk);
            Ok("...the self-include is reported", self.SkippedIncludes.Count == 1
               && self.SkippedIncludes[0].IndexOf("already loaded", StringComparison.OrdinalIgnoreCase) >= 0, Show(self.SkippedIncludes));
            Ok("...and its own entries are kept", self.GetSearchPaths(".clw").Contains(lib), Show(self.GetSearchPaths(".clw")));

            // ---- cycle, missing file, relative path, %THISDIR%, headerless entries, repeated section ----
            string dir = Path.Combine(root, "misc");
            Write(Path.Combine(dir, "a.red"), "{include sub\\b.red}\r\n{include missing.red}\r\n*.tpl = C:\\HeaderlessIsCommon\r\n"
                                            + "[Common]\r\n*.inc = C:\\First\r\n[Debug]\r\n*.lib = C:\\Dbg\r\n[Common]\r\n*.inc = C:\\Second\r\n");
            Write(Path.Combine(dir, "sub", "b.red"), "{ include \"%THISDIR%\\..\\a.red\" }\r\n[Common]\r\n*.equ = %THISDIR%\\equates\r\n");
            var misc = new RedFileService();
            bool miscOk = misc.Load(Path.Combine(dir, "a.red"), null);
            Ok("a cycle (a -> b -> a) and a missing include load without throwing", miscOk);
            Ok("...both are reported", misc.SkippedIncludes.Count == 2
               && misc.SkippedIncludes.Any(s => s.Contains("already loaded"))
               && misc.SkippedIncludes.Any(s => s.Contains("file not found")), Show(misc.SkippedIncludes));
            Ok("a relative include path is relative to the including file", misc.LoadedFiles.Count == 2
               && misc.LoadedFiles[1].EndsWith(Path.Combine("sub", "b.red"), StringComparison.OrdinalIgnoreCase), Show(misc.LoadedFiles));
            Ok("%THISDIR% is the folder of the file the line is in",
               misc.GetSearchPaths(".equ").SequenceEqual(new[] { Path.Combine(dir, "sub", "equates") }), Show(misc.GetSearchPaths(".equ")));
            Ok("an entry before any [section] belongs to [Common]",
               misc.GetSearchPaths(".tpl").SequenceEqual(new[] { "C:\\HeaderlessIsCommon" }), Show(misc.GetSearchPaths(".tpl")));
            Ok("a [section] named twice keeps both, in order (it used to replace)",
               misc.GetSearchPaths(".inc").SequenceEqual(new[] { "C:\\First", "C:\\Second" }), Show(misc.GetSearchPaths(".inc")));
            Ok("an included file's headerless lines don't land in the includer's current section",
               misc.GetSearchPaths(".lib", "Debug").SequenceEqual(new[] { "C:\\Dbg" }), Show(misc.GetSearchPaths(".lib", "Debug")));

            // ---- pipeline run 1 (debugger): what IsStale must notice ----
            // A .red held by an editor mid-save can't be read: the failed load must stay stale, so the next
            // access retries, instead of recording the new time and keeping the empty load as current.
            string lockedRed = Path.Combine(sln, "Clarion110.red");
            var locked = new RedFileService();
            bool lockedOk;
            using (new FileStream(lockedRed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                lockedOk = locked.LoadForProject(sln, Config(clarion));
            Ok("a .red locked mid-save fails to load", !lockedOk);
            Ok("...and stays stale once it is readable again, so the next access retries", locked.IsStale());
            locked.LoadForProject(sln, Config(clarion));
            Ok("...and the retry loads it", locked.GetSearchPaths(".clw").Contains(lib) && !locked.IsStale(),
               Show(locked.GetSearchPaths(".clw")));

            // A missing {include} target that is created later.
            Ok("a missing include is not itself stale", !misc.IsStale());
            Write(Path.Combine(dir, "missing.red"), "[Common]\r\n*.ico = C:\\Icons\r\n");
            Ok("...creating the missing include makes it stale", misc.IsStale());

            // A project-local version-named .red added after the version-level one was loaded.
            string sln2 = Path.Combine(root, "NoLocalYet");
            Directory.CreateDirectory(sln2);
            var noLocal = new RedFileService();
            noLocal.LoadForProject(sln2, Config(clarion));
            Ok("no local .red: the version-level one loads, not stale",
               noLocal.RedFilePath.EndsWith(Path.Combine("bin", "Clarion110.red"), StringComparison.OrdinalIgnoreCase) && !noLocal.IsStale(),
               noLocal.RedFilePath);
            Write(Path.Combine(sln2, "Clarion110.red"), "[Common]\r\n*.clw = .\\mine\r\n");
            Ok("...a local Clarion110.red created later makes it stale", noLocal.IsStale());

            // A top-level path with ".." must still catch a self-include the first time (no duplicate entries).
            Write(Path.Combine(dir, "self.red"), "{include %THISDIR%\\self.red}\r\n[Common]\r\n*.self = C:\\Once\r\n");
            var dotted = new RedFileService();
            dotted.Load(Path.Combine(dir, "sub", "..", "self.red"), null);
            Ok("a '..' top-level path catches its self-include at once (entries not duplicated)",
               dotted.SkippedIncludes.Count == 1 && dotted.Sections.ContainsKey("Common")
               && dotted.Sections["Common"].Entries.Count(e => e.Pattern == "*.self") == 1,
               Show(dotted.SkippedIncludes));
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
