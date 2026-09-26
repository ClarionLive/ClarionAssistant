using System;
using System.IO;
using ClarionAssistant.Services;

// Harness for ClarionVersionInfo.GetCurrentConfig's running-exe match (GH #209) — compiles the REAL
// ClarionVersionService.cs on its own and runs it against the reporter's own ClarionProperties.xml
// (tests\fixtures\gh209, cut down to the elements the service reads).
//
// THE BUG. With no usable IDE version name, the running Clarion.exe is matched to a version entry by
// its bin folder, and the FIRST entry with that bin path won. The reporter's XML registers
// "Clarion.NET 4.0.13372" and "Clarion 11.0.13372" on the same C:\Clarion\v11\bin, .NET first — so the
// Win32 IDE resolved to its own Clarion# compiler entry.
//
// Run:  tests\Run-Tests.ps1   (passes the fixture path as the only argument)
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class ClarionVersionServiceExeMatchTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static string Pick(string xml, string exePath, string build, string currentName)
    {
        var info = ClarionVersionService.ParsePropertiesXml(xml);
        info.ClarionExePath = exePath;
        info.ClarionExeVersion = build != null ? new Version(build) : null;
        info.CurrentVersionName = currentName;
        var cfg = info.GetCurrentConfig();
        return cfg != null ? cfg.Name : "(null)";
    }

    static void Expect(string name, string want, string got) { Ok(name, want == got, "want '" + want + "', got '" + got + "'"); }

    static int Main(string[] args)
    {
        string xml = args.Length > 0 ? args[0] : null;
        if (xml == null || !File.Exists(xml))
        {
            Console.WriteLine("COULD NOT RUN: fixture not found: " + (xml ?? "(no argument)"));
            return 2;
        }

        var parsed = ClarionVersionService.ParsePropertiesXml(xml);
        Ok("fixture parses to the reporter's 7 version entries", parsed != null && parsed.Versions.Count == 7,
           parsed == null ? "null" : parsed.Versions.Count.ToString());
        // Guard the premise: if the fixture ever stops listing the .NET entry first on the shared bin,
        // the first-match bug can no longer show here and the headline assertion proves nothing.
        int net = parsed.Versions.FindIndex(v => v.Name == "Clarion.NET 4.0.13372");
        int win = parsed.Versions.FindIndex(v => v.Name == "Clarion 11.0.13372");
        Ok("premise: the .NET entry precedes the Win32 entry on C:\\Clarion\\v11\\bin", net >= 0 && win > net);
        Ok("IsWindowsVersion is parsed (False on the .NET entry, True on the Win32 one)",
           net >= 0 && win >= 0 && parsed.Versions[net].IsWindowsVersion == false && parsed.Versions[win].IsWindowsVersion == true);

        const string V11 = @"C:\Clarion\v11\bin\Clarion.exe";

        // The reporter's case: the IDE's version name is not usable, so the exe decides.
        Expect("v11 IDE, no version name -> the Win32 entry, not the .NET compiler on the same bin",
               "Clarion 11.0.13372", Pick(xml, V11, "11.0.0.13372", null));
        Expect("v11 IDE, a '(Current Version)' name -> the Win32 entry",
               "Clarion 11.0.13372", Pick(xml, V11, "11.0.0.13372", "(Current Version)"));
        Expect("v11 IDE, a stale name matching nothing -> the Win32 entry",
               "Clarion 11.0.13372", Pick(xml, V11, "11.0.0.13372", "Clarion 11.0.99999"));
        Expect("v11 IDE, exe version unreadable -> IsWindowsVersion alone still picks the Win32 entry",
               "Clarion 11.0.13372", Pick(xml, V11, null, null));

        // The other shared bin lists the Win32 entry first; the answer must not change.
        Expect("TXDocs IDE -> its Win32 entry",
               "Clarion v11.0.13401.TXDocs", Pick(xml, @"C:\Clarion\v11.0.13401.TXDocs\bin\Clarion.exe", "11.0.0.13401", null));
        Expect("CStone IDE (its own bin) -> the CStone entry",
               "Clarion v11.0.13372.CStone", Pick(xml, @"C:\Clarion\v11.0.13372.CStone\bin\Clarion.exe", "11.0.0.13372", null));

        // A real version name still wins over the exe — unchanged behaviour.
        Expect("a version name that exists wins over the running exe",
               "Clarion v11.0.13401.TXDocs", Pick(xml, V11, "11.0.0.13372", "Clarion v11.0.13401.TXDocs"));

        // Build-number tie-break and the first-match fallback, on synthetic entries.
        var info = new ClarionVersionInfo { ClarionExePath = @"C:\C11\bin\Clarion.exe", ClarionExeVersion = new Version("11.0.0.13505") };
        info.Versions.Add(new ClarionVersionConfig { Name = "Clarion 11.0.13401", BinPath = @"C:\C11\bin", IsWindowsVersion = true });
        info.Versions.Add(new ClarionVersionConfig { Name = "Clarion 11.0.13505", BinPath = @"C:\C11\bin\", IsWindowsVersion = true });
        Expect("two Win32 entries on one bin -> the one naming the exe's build",
               "Clarion 11.0.13505", info.GetCurrentConfig() != null ? info.GetCurrentConfig().Name : "(null)");
        info.ClarionExeVersion = new Version("11.0.0.135");
        Expect("the build must match as a whole number, not a prefix -> nothing discriminates, first match",
               "Clarion 11.0.13401", info.GetCurrentConfig() != null ? info.GetCurrentConfig().Name : "(null)");
        info.ClarionExeVersion = null;
        Expect("nothing discriminates -> first match, as before",
               "Clarion 11.0.13401", info.GetCurrentConfig() != null ? info.GetCurrentConfig().Name : "(null)");

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }
}
