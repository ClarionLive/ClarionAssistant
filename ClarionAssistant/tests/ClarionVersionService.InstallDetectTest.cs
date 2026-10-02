using System;
using System.CodeDom.Compiler;
using System.IO;
using ClarionAssistant.Services;
using Microsoft.CSharp;

// Harness for ClarionVersionService.DetectForInstall (GH #247) — compiles the REAL ClarionVersionService.cs on its
// own and runs it against a throwaway Clarion tree and settings folder.
//
// THE BUG. The standalone MCP server (clarion-mcp-server.exe, in <root>\accessory\addins\ClarionAssistant) ran the
// same Detect() as the addin, which works the settings folder out from the RUNNING exe's version. Inside the IDE that
// is Clarion.exe (11.0 -> %APPDATA%\SoftVelocity\Clarion\11.0). In the server it is the server itself (5.9.0.x): no
// 5.9 folder exists, so it fell back to the NEWEST folder and read another Clarion's ClarionProperties.xml. The
// reporter's IDE used 11.0, the server read a file listing Clarion.NET first, and the index got ClarionNet40.red.
//
// Fixtures: gh247 = the reporter's current 11.0 file (Win32 entries first); gh209 = his older copy (.NET first),
// standing in for the newer folder the server wrongly read.
//
// Run:  tests\Run-Tests.ps1   (passes both fixture paths)
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class ClarionVersionServiceInstallDetectTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    // A stub exe carrying a real file version, which is all Detect reads from Clarion.exe or the server.
    static void StubExe(string path, string fileVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var p = new CompilerParameters { GenerateExecutable = true, OutputAssembly = path };
        var r = new CSharpCodeProvider().CompileAssemblyFromSource(p,
            "[assembly: System.Reflection.AssemblyFileVersion(\"" + fileVersion + "\")]\n" +
            "static class P { static void Main() {} }");
        if (r.Errors.HasErrors) throw new Exception("stub compile failed: " + r.Errors[0].ErrorText);
    }

    static int Main(string[] args)
    {
        string current = args.Length > 0 ? args[0] : null, older = args.Length > 1 ? args[1] : null;
        if (current == null || !File.Exists(current) || older == null || !File.Exists(older))
        {
            Console.WriteLine("COULD NOT RUN: fixtures not found: " + (current ?? "(none)") + ", " + (older ?? "(none)"));
            return 2;
        }

        string tmp = Path.Combine(Path.GetTempPath(), "ca-installdetect-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string settings = Path.Combine(tmp, "settings");
            string xml11 = Path.Combine(settings, "11.0", "ClarionProperties.xml");
            string xml12 = Path.Combine(settings, "12.0", "ClarionProperties.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(xml11));
            Directory.CreateDirectory(Path.GetDirectoryName(xml12));
            File.Copy(current, xml11);
            File.Copy(older, xml12);

            string root = Path.Combine(tmp, "v11");
            string clarionExe = Path.Combine(root, "bin", "Clarion.exe");
            string serverExe = Path.Combine(root, "accessory", "addins", "ClarionAssistant", "clarion-mcp-server.exe");
            StubExe(clarionExe, "11.0.0.13372");
            StubExe(serverExe, "5.9.0.1241");

            // Detect() takes this route whenever the host is not Clarion.exe and sits in such a tree.
            Ok("the server's folder resolves to its Clarion root",
               string.Equals(ClarionVersionService.InstalledClarionRoot(Path.GetDirectoryName(serverExe)), root, StringComparison.OrdinalIgnoreCase),
               ClarionVersionService.InstalledClarionRoot(Path.GetDirectoryName(serverExe)));
            Ok("a folder not named accessory\\addins\\ClarionAssistant is not a Clarion root",
               ClarionVersionService.InstalledClarionRoot(Path.Combine(root, "bin")) == null);

            // The reporter's machine: the server under the v11 tree, settings folders 11.0 and a newer 12.0.
            var info = ClarionVersionService.DetectForInstall(root, serverExe, settings);
            Ok("server under a Clarion 11 tree: detected at all", info != null);
            if (info != null)
            {
                Ok("server under a Clarion 11 tree reads the 11.0 ClarionProperties.xml, not the newest folder's",
                   string.Equals(info.PropertiesXmlPath, xml11, StringComparison.OrdinalIgnoreCase), info.PropertiesXmlPath);
                Ok("... and is matched as that tree's Clarion.exe (11.0.0.13372), not the server's own 5.9",
                   info.ClarionExeVersion != null && info.ClarionExeVersion.Major == 11, info.ClarionExeVersion + "");
                Ok("... so its exe is that tree's Clarion.exe",
                   string.Equals(info.ClarionExePath, clarionExe, StringComparison.OrdinalIgnoreCase), info.ClarionExePath);
                var cfg = info.ResolveByRoot(@"C:\Clarion\v11");
                Ok("... and the root lookup gives Clarion110.red",
                   cfg != null && cfg.RedFileName == "Clarion110.red", cfg != null ? cfg.Name + " / " + cfg.RedFileName : "(null)");
            }

            // No Clarion.exe under the root (a copy outside a real tree): nothing better to go on, behaviour as before.
            string bare = Path.Combine(tmp, "bare");
            string bareServer = Path.Combine(bare, "accessory", "addins", "ClarionAssistant", "clarion-mcp-server.exe");
            StubExe(bareServer, "5.9.0.1241");
            var fallback = ClarionVersionService.DetectForInstall(bare, bareServer, settings);
            Ok("a tree with no bin folder is not a Clarion root",
               ClarionVersionService.InstalledClarionRoot(Path.GetDirectoryName(bareServer)) == null);
            Ok("no Clarion.exe under the root: falls back to the host exe (newest folder, as before)",
               fallback != null && string.Equals(fallback.PropertiesXmlPath, xml12, StringComparison.OrdinalIgnoreCase),
               fallback != null ? fallback.PropertiesXmlPath : "(null)");
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  [FAIL] harness threw: " + ex);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }
}
