// Harness for Services\ExplorerHeader.cs (ticket 16d140e9): the CA Explorer header's APP / VERSION /
// ROOT lines and the "open in Windows Explorer" request behind APP and ROOT.
//
// Compiled against the REAL ExplorerHeader.cs by Run-Tests.ps1. Existence checks are injected, so
// no path here has to exist on the machine running it.
//
// What each check stops:
//   app beats solution      - the header naming the .sln when an .app is open (the Owner asked for APP)
//   solution fallback       - a .sln shown under the APP label, or nothing shown when only a .sln is open
//   bare app name           - an app known only as "clbrws.app" rendered as if it were a path
//   unknown = null          - an empty string reaching the page as a clickable blank line
//   root trailing slash     - "C:\Clarion10v8\" rendering differently from the version's own entry
//   args quoted             - a path with spaces reaching explorer.exe unquoted
//   drive root unquoted     - "C:\" + quote = an escaped quote, i.e. a broken argument
//   refusals                - relative, device-namespace, URL, stream, wildcard, quote and control-char
//                             paths, and paths that do not exist, ever reaching Process.Start
using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

static class ExplorerHeaderTest
{
    static int _failures, _assertions;

    static void Check(bool ok, string what)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what); }
        else Console.WriteLine("  ok   " + what);
    }

    static void Eq(string actual, string expected, string what)
    {
        Check(actual == expected, what + (actual == expected ? "" : "  (got <" + (actual ?? "null") + ">, want <" + (expected ?? "null") + ">)"));
    }

    static readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Apps\School\clbrws.app",
        @"C:\My Apps\Invoice Pro\invoice.app",
        @"\\nas\clarion\apps\shared.app",
        @"C:\R&D\tool.app",
    };
    static readonly HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\", @"C:\Clarion10v8", @"C:\Program Files\SoftVelocity\Clarion11", @"\\nas\clarion",
        @"C:\Apps\School",
    };
    static bool FileExists(string p) { return Files.Contains(p); }
    static bool DirExists(string p) { return Dirs.Contains(p); }

    static string Args(string path, bool select)
    {
        string a;
        return ExplorerHeader.TryBuildExplorerArgs(path, select, FileExists, DirExists, out a) ? a : null;
    }

    static int Main()
    {
        Console.WriteLine("-- Compose");
        var m = ExplorerHeader.Compose(@"C:\Apps\School\clbrws.app", @"C:\Apps\School\clbrws.sln",
                                       "Clarion 10 Active And Updated", @"C:\Clarion10v8\");
        Eq(m.AppLabel, "APP", "open .app -> label APP");
        Eq(m.AppPath, @"C:\Apps\School\clbrws.app", "open .app -> its full path (not the .sln)");
        Eq(m.VersionName, "Clarion 10 Active And Updated", "version NAME, not the root folder name");
        Eq(m.RootPath, @"C:\Clarion10v8", "root keeps its full path, trailing separator dropped");

        m = ExplorerHeader.Compose(null, @"C:\Apps\School\clbrws.sln", "Clarion 11", @"C:\Clarion11");
        Eq(m.AppLabel, "SOLUTION", "no app, solution open -> label SOLUTION");
        Eq(m.AppPath, @"C:\Apps\School\clbrws.sln", "no app -> the solution's full path");

        m = ExplorerHeader.Compose("clbrws.app", @"C:\Apps\School\clbrws.sln", null, null);
        Eq(m.AppLabel, "APP", "bare app name + solution -> still APP");
        Eq(m.AppPath, @"C:\Apps\School\clbrws.app", "bare app name placed in the solution's folder");

        m = ExplorerHeader.Compose("clbrws.app", null, null, null);
        Eq(m.AppLabel, "APP", "bare app name, no solution -> APP label kept");
        Eq(m.AppPath, null, "bare app name, no solution -> unknown, not a fake path");

        m = ExplorerHeader.Compose("  ", "", "  ", "");
        Eq(m.AppLabel, "APP", "nothing known -> APP label");
        Check(m.AppPath == null && m.VersionName == null && m.RootPath == null, "blank values -> null (rendered as a dash)");

        m = ExplorerHeader.Compose(null, null, "Clarion 12", @"C:\");
        Eq(m.RootPath, @"C:\", "drive-root install keeps its backslash");

        Console.WriteLine("-- TryBuildExplorerArgs: accepted");
        Eq(Args(@"C:\Apps\School\clbrws.app", true), "/select,\"C:\\Apps\\School\\clbrws.app\"", "app -> /select,\"path\"");
        Eq(Args(@"C:\My Apps\Invoice Pro\invoice.app", true), "/select,\"C:\\My Apps\\Invoice Pro\\invoice.app\"", "spaces stay inside the quotes");
        Eq(Args(@"C:\R&D\tool.app", true), "/select,\"C:\\R&D\\tool.app\"", "'&' in a real folder name is allowed (no shell involved)");
        Eq(Args(@"\\nas\clarion\apps\shared.app", true), "/select,\"\\\\nas\\clarion\\apps\\shared.app\"", "UNC app accepted");
        Eq(Args(@"C:\Clarion10v8", false), "\"C:\\Clarion10v8\"", "root folder -> quoted path");
        Eq(Args(@"C:\Clarion10v8\", false), "\"C:\\Clarion10v8\"", "trailing backslash dropped before the closing quote");
        Eq(Args(@"C:\Program Files\SoftVelocity\Clarion11", false), "\"C:\\Program Files\\SoftVelocity\\Clarion11\"", "root with spaces quoted");
        Eq(Args(@"C:\", false), @"C:\", "drive root passed bare (a quoted C:\\\" would escape the quote)");
        Eq(Args(@"\\nas\clarion", false), "\"\\\\nas\\clarion\"", "UNC share root accepted");
        Eq(Args(@"C:\Apps\School\..\School\clbrws.app", true), "/select,\"C:\\Apps\\School\\clbrws.app\"", "dot-dot normalised before the existence check");

        Console.WriteLine("-- TryBuildExplorerArgs: refused");
        string[] refused =
        {
            null, "", "   ",
            @"Apps\School\clbrws.app",               // relative
            @"C:clbrws.app",                          // drive-relative
            @"\\?\C:\Apps\School\clbrws.app",         // long-path namespace
            @"\\.\C:\Apps\School\clbrws.app",         // device namespace
            @"\\nas",                                 // server with no share
            "file:///C:/Apps/School/clbrws.app",      // URL
            "https://example.com/x.app",              // URL
            @"C:\Apps\School\clbrws.app:evil",         // alternate data stream
            "C:\\Apps\\School\\clbrws.app\" /e,\"C:\\", // quote breaking out of the argument
            @"C:\Apps\School\*.app",                  // wildcard
            @"C:\Apps\School\clbrws.app|calc",        // pipe
            "C:\\Apps\\School\\clbrws.app\ncalc",      // control char
            @"C:\Apps\School\missing.app",            // does not exist
            @"C:\Apps\School",                        // a folder is not a file to select
        };
        foreach (var r in refused)
            Check(Args(r, true) == null, "select refused: " + Show(r));

        // Each syntactic refusal again with probes that say EVERYTHING exists, and with a sample that only
        // the layer under test can reject (no stray colon for the stream check to catch instead), so no
        // later layer can be the one deciding.
        Func<string, bool> yes = p => true;
        string[] syntaxOnly =
        {
            "C:\\Apps\\School\\clb\"rws.app",         // quote alone
            @"\\.\PhysicalDrive0",                   // device namespace alone
            @"\\?\UNC\nas\clarion\x.app",             // long-path UNC alone
            @"C:\Apps\School\clb*rws.app",            // wildcard alone
            @"C:\Apps\School\clb<rws.app",            // redirect alone
            "C:\\Apps\\School\\clb\trws.app",         // tab alone
            @"relative\clbrws.app",                   // relative alone
        };
        foreach (var r in syntaxOnly)
        {
            string a1, a2;
            bool s = ExplorerHeader.TryBuildExplorerArgs(r, true, yes, yes, out a1);
            bool o = ExplorerHeader.TryBuildExplorerArgs(r, false, yes, yes, out a2);
            Check(!s && !o, "refused even when it 'exists': " + Show(r));
        }

        Check(Args(@"C:\Nope", false) == null, "open refused: folder that does not exist");
        Check(Args(@"C:\Apps\School\clbrws.app", false) == null, "open refused: a file is not a folder");
        Check(Args("C:\\Clarion10v8\"", false) == null, "open refused: stray quote");

        string outArgs;
        Check(!ExplorerHeader.TryBuildExplorerArgs(@"C:\Clarion10v8", false, FileExists, p => { throw new Exception("io"); }, out outArgs) && outArgs == null,
              "a throwing existence probe refuses, never throws");
        Check(!ExplorerHeader.TryBuildExplorerArgs(@"C:\Clarion10v8", false, FileExists, null, out outArgs),
              "no existence probe -> refused (fail closed)");

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "PASS  " + _assertions + " assertions"
            : "FAIL  " + _failures + " of " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }

    static string Show(string s)
    {
        if (s == null) return "(null)";
        return "<" + s.Replace("\n", "\\n") + ">";
    }
}
