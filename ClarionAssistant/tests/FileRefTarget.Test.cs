using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

// Harness for FileRefTarget: the file named by INCLUDE / MODULE / MEMBER / LINK under the cursor, and where the
// build finds it through the .red.
//
// THE BUG. Ctrl+Click on the file name in INCLUDE('CWSyncHM.inc') opened ABFILE.INC at line 9. When the language
// server answered nothing, the definition fallback looked "CWSyncHM.inc" up by name in the CodeGraph symbols,
// where an 'include' row records the INCLUDE statement's site: the first one by path was ABFILE.INC's own
// INCLUDE('CWSYNCHM.INC'). A file name is resolved through the .red, the way the compiler finds it.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class FileRefTargetTest
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

    static string Show(string s) { return s ?? "(null)"; }

    static void FileNames()
    {
        const string inc = " INCLUDE( 'CWSyncHM.inc'), ONCE               !For critical sections";
        int open = inc.IndexOf('\''), close = inc.IndexOf('\'', open + 1);
        bool all = true;
        for (int i = open; i <= close; i++)
            if (FileRefTarget.FileNameAt(inc, i) != "CWSyncHM.inc") { all = false; Console.WriteLine("    miss at " + i); }
        Ok("F1 every position from the opening to the closing quote names the file", all);
        Ok("F2 the INCLUDE keyword itself is not the file name", FileRefTarget.FileNameAt(inc, 3) == null);
        Ok("F3 after the closing parenthesis: null", FileRefTarget.FileNameAt(inc, inc.IndexOf("ONCE")) == null);
        Ok("F4 inside the trailing comment: null", FileRefTarget.FileNameAt(inc, inc.IndexOf("critical")) == null);

        const string section = "  INCLUDE('Lib.inc','MySection')";
        Ok("F5 INCLUDE's SECTION argument is not a file", FileRefTarget.FileNameAt(section, section.IndexOf("MySection")) == null);
        Ok("F6 its first argument is", FileRefTarget.FileNameAt(section, section.IndexOf("Lib")) == "Lib.inc");

        Ok("F7 MODULE('') names no file", FileRefTarget.FileNameAt("    MODULE('')", 12) == null);
        const string module = "    MODULE('Util.clw')";
        Ok("F8 MODULE", FileRefTarget.FileNameAt(module, module.IndexOf("Util")) == "Util.clw");
        const string member = "  MEMBER('Prog.clw')";
        Ok("F9 MEMBER", FileRefTarget.FileNameAt(member, member.IndexOf("Prog")) == "Prog.clw");
        const string link = "MyClass CLASS,TYPE,MODULE('My.clw'),LINK('My.clw',_MyLink_)";
        Ok("F10 LINK, and MODULE before it on the same line",
           FileRefTarget.FileNameAt(link, link.LastIndexOf("My.clw")) == "My.clw" && FileRefTarget.FileNameAt(link, link.IndexOf("My.clw")) == "My.clw");
        Ok("F11 keyword case does not matter", FileRefTarget.FileNameAt("  include('a.inc')", 12) == "a.inc");

        const string commented = "! INCLUDE('x.inc')";
        Ok("F12 a commented-out INCLUDE: null", FileRefTarget.FileNameAt(commented, commented.IndexOf("x.inc")) == null);
        const string literal = "  Msg = 'CWSyncHM.inc'";
        Ok("F13 a plain string literal is not a file reference", FileRefTarget.FileNameAt(literal, literal.IndexOf("CWSync")) == null);
        const string longer = "  X = MYINCLUDE('a.inc')";
        Ok("F14 a name ending in INCLUDE is not the keyword", FileRefTarget.FileNameAt(longer, longer.IndexOf("a.inc")) == null);
        const string bang = "  INCLUDE('a!b.inc')";
        Ok("F15 a '!' inside the string is not a comment", FileRefTarget.FileNameAt(bang, bang.IndexOf("b.inc")) == "a!b.inc");
        Ok("F16 no line / negative position: null", FileRefTarget.FileNameAt(null, 0) == null && FileRefTarget.FileNameAt(inc, -1) == null);
        const string method = "  ST.Include('a.inc')";
        Ok("F17 a method that happens to be called Include is not the statement", FileRefTarget.FileNameAt(method, method.IndexOf("a.inc")) == null);
    }

    static void Resolution(string root)
    {
        string sln = Path.Combine(root, "MySolution");
        string lib = Path.Combine(root, "libsrc");
        string rel = Path.Combine(root, "release");
        string redPath = Path.Combine(root, "cfg", "Clarion110.red");
        Write(redPath, "[Release32]\r\n*.inc = " + rel + "\r\n[Common]\r\n*.inc = .;" + lib + "\r\n");
        Write(Path.Combine(sln, "App.cwproj"), "<Project/>");
        Write(Path.Combine(sln, "Main.inc"), "");
        Write(Path.Combine(sln, "Local.inc"), "");
        Write(Path.Combine(lib, "Sync.inc"), "");
        Write(Path.Combine(lib, "AbFile.inc"), "");
        Write(Path.Combine(rel, "Sync.inc"), "");
        // A project in its own folder, under the solution, without its own .red.
        string sub = Path.Combine(sln, "Sub");
        Write(Path.Combine(sub, "Sub.cwproj"), "<Project/>");
        Write(Path.Combine(sub, "Local.inc"), "");
        Write(Path.Combine(sub, "Deeper", "Code.inc"), "");
        // A project with its own version-named .red, which supersedes the solution's.
        string own = Path.Combine(sln, "Own");
        Write(Path.Combine(own, "Own.cwproj"), "<Project/>");
        Write(Path.Combine(own, "Clarion110.red"), "[Common]\r\n*.inc = .\\mine\r\n");
        Write(Path.Combine(own, "mine", "Mine.inc"), "");

        var red = new RedFileService();
        Ok("R0 the fixture .red loads", red.Load(redPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        // What RedResolution.SectionsFor gives for a Release build, and for an unknown configuration.
        string[] release = { "Release32", "Release", "Common" }, unknown = { "Common" };
        string main = Path.Combine(sln, "Main.inc");

        string got = FileRefTarget.Resolve("Sync.inc", main, sln, red, release);
        Ok("R1 a Release build finds the [Release32] copy first", got == Path.Combine(rel, "Sync.inc"), Show(got));
        got = FileRefTarget.Resolve("Sync.inc", main, sln, red, unknown);
        Ok("R2 configuration unknown: [Common] only", got == Path.Combine(lib, "Sync.inc"), Show(got));
        got = FileRefTarget.Resolve("Local.inc", main, sln, red, unknown);
        Ok("R3 '.' is the project folder", got == Path.Combine(sln, "Local.inc"), Show(got));
        got = FileRefTarget.Resolve("Local.inc", Path.Combine(sub, "Deeper", "Code.inc"), sln, red, unknown);
        Ok("R4 a file in a sub-project's folder (or below it) resolves from THAT project's folder", got == Path.Combine(sub, "Local.inc"), Show(got));
        got = FileRefTarget.Resolve("Local.inc", Path.Combine(lib, "AbFile.inc"), sln, red, unknown);
        Ok("R5 a file outside the solution (a libsrc header) resolves from the solution folder", got == Path.Combine(sln, "Local.inc"), Show(got));
        got = FileRefTarget.Resolve("Mine.inc", Path.Combine(own, "Code.inc"), sln, red, unknown);
        Ok("R6 a project's own .red supersedes the solution's", got == Path.Combine(own, "mine", "Mine.inc"), Show(got));
        got = FileRefTarget.Resolve("Nowhere.inc", main, sln, red, release);
        Ok("R7 not on any .red path: null, no guess", got == null, Show(got));
        Ok("R8 no .red: null", FileRefTarget.Resolve("Sync.inc", main, sln, null, release) == null);
        string rooted = Path.Combine(lib, "Sync.inc");
        Ok("R9 a rooted name is used as written when it exists",
           FileRefTarget.Resolve(rooted, main, sln, red, unknown) == rooted && FileRefTarget.Resolve(rooted + ".gone", main, sln, red, unknown) == null);
        got = FileRefTarget.Resolve("Local.inc", Path.Combine(sub, "Code.inc"), null, red, unknown);
        Ok("R10 no solution folder: the context file's own folder", got == Path.Combine(sub, "Local.inc"), Show(got));

        // The embeditor's synthetic bare "Proc.clw" has no folder of its own.
        Ok("P1 a bare synthetic name: the solution folder, else none (never the working directory)",
           FileRefTarget.ProjectDirFor("MyProc.clw", sln) == sln && FileRefTarget.ProjectDirFor("MyProc.clw", null) == null,
           Show(FileRefTarget.ProjectDirFor("MyProc.clw", null)));
        string driveRoot = FileRefTarget.ProjectDirFor(@"Z:\NoProject\x.clw", @"Z:\");
        Ok("P2 a solution at a drive root keeps the root's backslash", driveRoot == @"Z:\", Show(driveRoot));
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-fileref-" + Guid.NewGuid().ToString("N"));
        try
        {
            FileNames();
            Resolution(root);
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
