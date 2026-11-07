using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClarionAssistant.Services;

// Harness for ticket 1976d2e9: SoftVelocity ships ClarionLAA.exe (Clarion.exe built LargeAddressAware) beside the
// standard Clarion.exe. Every "is this the IDE?" check must take both. Compiles the REAL Services\ClarionIdeProcess.cs.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
//
// Argument 1 (passed by Run-Tests.ps1): the ClarionAssistant project dir, for the source checks on the call sites
// (IdeEndpointRecord, IdeSolutionRecord, ClarionVersionService, MemoryHeadroom, deploy.ps1). Those are IDE- or
// process-coupled, so they are checked by source, not behaviour.
static class ClarionIdeProcessTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    // A method's text, from its signature to its closing brace (the first "\n        }" after it), or null.
    static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return null;
        int end = src.IndexOf("\n        }", at, StringComparison.Ordinal);
        return end < 0 ? src.Substring(at) : src.Substring(at, end - at);
    }

    static int Main(string[] args)
    {
        Console.WriteLine("ClarionIdeProcess: Clarion.exe and ClarionLAA.exe are both the IDE (1976d2e9)");

        // ---- Image names ----
        Ok("Clarion is the IDE", ClarionIdeProcess.IsIdeImageName("Clarion"));
        Ok("ClarionLAA is the IDE", ClarionIdeProcess.IsIdeImageName("ClarionLAA"));
        Ok("any case: CLARIONLAA, clarion", ClarionIdeProcess.IsIdeImageName("CLARIONLAA") && ClarionIdeProcess.IsIdeImageName("clarion"));
        Ok("ClarionClone.IDE is NOT (StartsWith used to take it)", !ClarionIdeProcess.IsIdeImageName("ClarionClone.IDE"));
        Ok("ClarionCL, ClarionLAAx, Clarion12 are not", !ClarionIdeProcess.IsIdeImageName("ClarionCL") &&
           !ClarionIdeProcess.IsIdeImageName("ClarionLAAx") && !ClarionIdeProcess.IsIdeImageName("Clarion12"));
        Ok("null and empty are not", !ClarionIdeProcess.IsIdeImageName(null) && !ClarionIdeProcess.IsIdeImageName(""));

        // ---- Exe paths ----
        Ok("bin\\Clarion.exe is the IDE exe", ClarionIdeProcess.IsIdeExe(@"C:\Clarion12\bin\Clarion.exe"));
        Ok("bin\\ClarionLAA.exe is the IDE exe", ClarionIdeProcess.IsIdeExe(@"C:\Clarion12\bin\ClarionLAA.exe"));
        Ok("clarion-mcp-server.exe, ClarionCL.exe, Clarion.dll are not",
           !ClarionIdeProcess.IsIdeExe(@"C:\Clarion12\accessory\addins\ClarionAssistant\clarion-mcp-server.exe") &&
           !ClarionIdeProcess.IsIdeExe(@"C:\Clarion12\bin\ClarionCL.exe") && !ClarionIdeProcess.IsIdeExe(@"C:\x\Clarion.dll"));
        Ok("null path is not", !ClarionIdeProcess.IsIdeExe(null));
        Ok("IsLaaExe only for ClarionLAA.exe", ClarionIdeProcess.IsLaaExe(@"C:\C12\bin\clarionlaa.EXE") && !ClarionIdeProcess.IsLaaExe(@"C:\C12\bin\Clarion.exe"));

        // ---- Low-memory advice ----
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Clarion12\bin\ClarionLAA.exe" };
        Func<string, bool> exists = files.Contains;
        string adv = ClarionIdeProcess.LowMemoryAdvice(@"C:\Clarion12\bin\Clarion.exe", exists);
        Ok("standard exe with ClarionLAA.exe beside it: advice names ClarionLAA.exe", adv != null && adv.Contains("ClarionLAA.exe"), adv);
        Ok("...and says it doubles the memory", adv != null && adv.Contains("twice the memory"), adv);
        Ok("already running ClarionLAA.exe: no advice", ClarionIdeProcess.LowMemoryAdvice(@"C:\Clarion12\bin\ClarionLAA.exe", exists) == null);
        Ok("no ClarionLAA.exe beside it: no advice", ClarionIdeProcess.LowMemoryAdvice(@"C:\Clarion11\bin\Clarion.exe", exists) == null);
        Ok("host not the IDE (the MCP server): no advice", ClarionIdeProcess.LowMemoryAdvice(@"C:\Clarion12\bin\clarion-mcp-server.exe", exists) == null);
        Ok("a throwing file check: no advice, no throw", ClarionIdeProcess.LowMemoryAdvice(@"C:\Clarion12\bin\Clarion.exe", p => { throw new IOException(); }) == null);

        // ---- Call sites (source checks) ----
        if (args.Length == 0) { Ok("project dir argument given", false, "Run-Tests.ps1 passes it"); }
        else
        {
            string dir = args[0];
            string ep = File.ReadAllText(Path.Combine(dir, "Services", "IdeEndpointRecord.cs"));
            Ok("IdeEndpointRecord: the IDE check uses IsIdeImageName", ep.Contains("ClarionIdeProcess.IsIdeImageName(p.ProcessName)"));
            Ok("IdeEndpointRecord: no hard-coded image = \"Clarion\" left", !ep.Contains("image = \"Clarion\""));

            string sr = File.ReadAllText(Path.Combine(dir, "Services", "IdeSolutionRecord.cs"));
            string verify = Body(sr, "private static bool VerifyWriter(");
            Ok("IdeSolutionRecord.VerifyWriter: uses IsIdeImageName", verify != null && verify.Contains("ClarionIdeProcess.IsIdeImageName(liveName)"));
            Ok("IdeSolutionRecord.VerifyWriter: no StartsWith(\"Clarion\") default left", verify != null && !verify.Contains("image = \"Clarion\""));

            string vs = File.ReadAllText(Path.Combine(dir, "Services", "ClarionVersionService.cs"));
            string host = Body(vs, "internal static ClarionVersionInfo DetectForHost(");
            Ok("DetectForHost: either IDE exe detects as the IDE", host != null && host.Contains("ClarionIdeProcess.IsIdeExe(hostExePath)")
               && !host.Contains("\"Clarion.exe\""));

            string mh = File.ReadAllText(Path.Combine(dir, "Services", "MemoryHeadroom.cs"));
            string check = Body(mh, "private static void Check(");
            Ok("MemoryHeadroom: the low-memory notice appends LowMemoryAdvice", check != null && check.Contains("ClarionIdeProcess.LowMemoryAdvice(")
               && check.Contains("(advice != null ? \" \" + advice : \"\")"));

            string dp = File.ReadAllText(Path.Combine(dir, "deploy.ps1"));
            var m = Regex.Match(dp, @"\$IdeImageNames\s*=\s*@\(([^)]*)\)");
            var listed = new List<string>();
            if (m.Success) foreach (Match q in Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"")) listed.Add(q.Groups[1].Value);
            Ok("deploy.ps1: $IdeImageNames is the same list as the helper",
               m.Success && string.Join(",", listed) == string.Join(",", ClarionIdeProcess.IdeImageNames), string.Join(",", listed));
            Ok("deploy.ps1: the idle check uses it", dp.Contains("$IdeImageNames -contains $p.ProcessName"));
            Ok("deploy.ps1: -Kill uses it", dp.Contains("Get-Process -Name $IdeImageNames"));
            Ok("deploy.ps1: no ProcessName -eq \"Clarion\" or Get-Process -Name \"Clarion\" left",
               !dp.Contains("ProcessName -eq \"Clarion\"") && !dp.Contains("Get-Process -Name \"Clarion\""));
        }

        Console.WriteLine();
        Console.WriteLine("  " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
