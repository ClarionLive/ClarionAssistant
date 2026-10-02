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
// Fixtures: gh247\ClarionProperties.xml = the reporter's 11.0 file (his Clarion 11 IDE's; Win32 entries first);
// gh247\ClarionProperties-12.0.xml = his 12.0 file, the NEWEST folder, which the server read instead (.NET first).
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

    // A minimal ClarionProperties.xml with the elements ParsePropertiesXml reads.
    static void WriteXml(string path, params string[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "<ClarionProperties><Properties name=\"Clarion.Versions\">" + string.Concat(entries) +
            "</Properties><Clarion.Version value=\"\" /></ClarionProperties>");
    }

    static string Entry(string name, string bin, string red)
    {
        return "<Properties name=\"" + name + "\"><path value=\"" + bin + "\" /><IsWindowsVersion value=\"True\" />" +
               "<Properties name=\"RedirectionFile\"><Name value=\"" + red + "\" /><Properties name=\"Macros\">" +
               "<root value=\"" + Path.GetDirectoryName(bin) + "\" /></Properties></Properties></Properties>";
    }

    static int Main(string[] args)
    {
        string current = args.Length > 0 ? args[0] : null, newest = args.Length > 1 ? args[1] : null;
        if (current == null || !File.Exists(current) || newest == null || !File.Exists(newest))
        {
            Console.WriteLine("COULD NOT RUN: fixtures not found: " + (current ?? "(none)") + ", " + (newest ?? "(none)"));
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
            File.Copy(newest, xml12);

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
                Ok("... and is flagged as not hosted by an IDE (its log line claims no running IDE)", info.HostIsNotIde);
                var cfg = info.ResolveByRoot(@"C:\Clarion\v11");
                Ok("... and the root lookup gives Clarion110.red",
                   cfg != null && cfg.RedFileName == "Clarion110.red", cfg != null ? cfg.Name + " / " + cfg.RedFileName : "(null)");
            }

            // ---- #247 exactly: the reporter's own 12.0 file, the one the server read ----
            var kevin12 = ClarionVersionService.ParsePropertiesXml(xml12);
            Ok("his 12.0 file parses", kevin12 != null && kevin12.Versions.Count > 10, kevin12 == null ? "null" : kevin12.Versions.Count.ToString());
            if (kevin12 != null)
            {
                // The OLD lookup by root was the first entry whose root matched. Pin the premise, so the assertion below
                // stays meaningful: on this file that first match IS the Clarion.NET entry and its .red.
                var firstMatch = kevin12.Versions.Find(v => !string.IsNullOrEmpty(v.RootPath) &&
                    string.Equals(v.RootPath.TrimEnd('\\'), @"C:\Clarion\v11", StringComparison.OrdinalIgnoreCase));
                Ok("premise: first-match on his 12.0 file gives Clarion.NET 4.0.13372 / ClarionNet40.red (the #247 log)",
                   firstMatch != null && firstMatch.Name == "Clarion.NET 4.0.13372" && firstMatch.RedFileName == "ClarionNet40.red",
                   firstMatch != null ? firstMatch.Name + " / " + firstMatch.RedFileName : "(null)");
                var byRoot = kevin12.ResolveByRoot(@"C:\Clarion\v11");
                Ok("even reading that file, the root lookup now gives Clarion v11 (Base) / Clarion110.red",
                   byRoot != null && byRoot.Name == "Clarion v11 (Base)" && byRoot.RedFileName == "Clarion110.red",
                   byRoot != null ? byRoot.Name + " / " + byRoot.RedFileName : "(null)");
            }

            // ---- Routing by host (what Detect() does with the running process) ----
            var viaHost = ClarionVersionService.DetectForHost(serverExe, root, settings);
            Ok("DetectForHost: a server inside a Clarion tree detects for that tree",
               viaHost != null && string.Equals(viaHost.PropertiesXmlPath, xml11, StringComparison.OrdinalIgnoreCase),
               viaHost != null ? viaHost.PropertiesXmlPath : "(null)");
            var viaIde = ClarionVersionService.DetectForHost(clarionExe, null, settings);
            Ok("DetectForHost: Clarion.exe itself detects from its own version",
               viaIde != null && string.Equals(viaIde.PropertiesXmlPath, xml11, StringComparison.OrdinalIgnoreCase),
               viaIde != null ? viaIde.PropertiesXmlPath : "(null)");

            // ---- A host that cannot say which Clarion it belongs to: never guess ----
            // The 5.9 -> "newest folder" fallback is the guess that read another Clarion's file in #247. A server with
            // no Clarion.exe to go by (a development build, a copy outside a Clarion tree) now detects NOTHING, so the
            // caller says "no Clarion version" instead of quietly serving whichever Clarion was installed last.
            string bare = Path.Combine(tmp, "bare");
            string bareServer = Path.Combine(bare, "accessory", "addins", "ClarionAssistant", "clarion-mcp-server.exe");
            StubExe(bareServer, "5.9.0.1241");
            Ok("a tree with no bin folder is not a Clarion root",
               ClarionVersionService.InstalledClarionRoot(Path.GetDirectoryName(bareServer)) == null);
            var outside = ClarionVersionService.DetectForHost(bareServer, null, settings);
            Ok("a server outside any Clarion tree detects nothing, instead of reading the newest folder",
               outside == null, outside != null ? outside.PropertiesXmlPath : "(null)");
            var noExe = ClarionVersionService.DetectForInstall(bare, bareServer, settings);
            Ok("a root with no Clarion.exe detects nothing either",
               noExe == null, noExe != null ? noExe.PropertiesXmlPath : "(null)");

            // ---- A version NAMED outright (--clarion-version / clarion-assistant.json) is found in any folder ----
            // With no host Clarion to pick the settings folder, a name is the only safe answer, so it is looked up
            // across every folder. The same name can sit in several: Kevin's 12.0 file carries a copy of his Clarion 11
            // entry. The copy in the folder its OWN Clarion.exe writes (11.0 for an 11.0 exe) is the live one.
            string s2 = Path.Combine(tmp, "settings2");
            string s2x11 = Path.Combine(s2, "11.0", "ClarionProperties.xml");
            string s2x12 = Path.Combine(s2, "12.0", "ClarionProperties.xml");
            string bin11 = Path.Combine(root, "bin");
            WriteXml(s2x11, Entry("Clarion 11.0.13372", bin11, "Clarion110.red"));
            WriteXml(s2x12, Entry("Clarion 11.0.13372", bin11, "STALE.red"), Entry("Clarion 12.0.14373", @"C:\NoSuch\C12\bin", "Clarion120.red"));
            string at;
            var named = ClarionVersionService.FindVersionByName("Clarion 11.0.13372", s2, out at);
            Ok("a named version is found", named != null);
            Ok("... from the folder its own Clarion.exe writes, not a stale copy in a newer folder",
               named != null && named.RedFileName == "Clarion110.red" && string.Equals(at, s2x11, StringComparison.OrdinalIgnoreCase),
               named != null ? named.RedFileName + " @ " + at : "(null)");
            Ok("... case-insensitively", ClarionVersionService.FindVersionByName("clarion 11.0.13372", s2, out at) != null);
            var c12 = ClarionVersionService.FindVersionByName("Clarion 12.0.14373", s2, out at);
            Ok("an entry whose Clarion.exe is not on this machine is still found, in the newest folder listing it",
               c12 != null && c12.RedFileName == "Clarion120.red" && string.Equals(at, s2x12, StringComparison.OrdinalIgnoreCase));
            Ok("an unknown name finds nothing", ClarionVersionService.FindVersionByName("Clarion 99", s2, out at) == null && at == null);

            // ---- GH #244: a NAMED version on a server installed under ANOTHER Clarion ----
            // A C12 server (its own file = the 12.0 one, holding a stale copy of the Clarion 11 entry) with
            // clarion-assistant.json naming Clarion 11: the copy in 11.0, which the 11.0 IDE and MSBuild read, must win.
            var host12 = ClarionVersionService.ParsePropertiesXml(s2x12);
            var named11 = ClarionVersionService.FindNamedVersion(host12, "Clarion 11.0.13372", s2);
            Ok("named on another Clarion's server: the entry's own folder beats the host's stale copy",
               named11 != null && named11.RedFileName == "Clarion110.red", named11 != null ? named11.RedFileName : "(null)");
            // Its own folder unknown (no Clarion.exe on this machine): the host's own copy beats the newest folder's.
            var host11 = new ClarionVersionInfo();
            host11.Versions.Add(new ClarionVersionConfig { Name = "Clarion 12.0.14373", BinPath = @"C:\NoSuch\C12\bin", RedFileName = "HOST.red" });
            var named12 = ClarionVersionService.FindNamedVersion(host11, "Clarion 12.0.14373", s2);
            Ok("named, own folder unknown: the host's copy, not the newest folder's",
               named12 != null && named12.RedFileName == "HOST.red", named12 != null ? named12.RedFileName : "(null)");
            var namedNoHost = ClarionVersionService.FindNamedVersion(null, "Clarion 12.0.14373", s2);
            Ok("named, no host Clarion: the newest folder listing it",
               namedNoHost != null && namedNoHost.RedFileName == "Clarion120.red", namedNoHost != null ? namedNoHost.RedFileName : "(null)");
            Ok("named, unknown anywhere: nothing", ClarionVersionService.FindNamedVersion(host12, "Clarion 99", s2) == null);
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
