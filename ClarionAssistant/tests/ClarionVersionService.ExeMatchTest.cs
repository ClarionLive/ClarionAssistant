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
// GH #247, the same fault by other routes: the standalone server's root lookup, the first-listed fallback
// and an IDE choice naming the .NET entry each handed a Win32 Clarion ClarionNet40.red.
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

    static string Name(ClarionVersionConfig cfg) { return cfg != null ? cfg.Name : "(null)"; }
    static string Red(ClarionVersionConfig cfg) { return cfg != null ? cfg.RedFileName : "(null)"; }

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

        // An older XML: the .NET entry says IsWindowsVersion=False but the Win32 entry omits the flag.
        // Only a PROVEN .NET entry may be dropped; "unknown" must stay a candidate.
        var older = new ClarionVersionInfo { ClarionExePath = @"C:\C11\bin\Clarion.exe", ClarionExeVersion = null };
        older.Versions.Add(new ClarionVersionConfig { Name = "Clarion.NET 4.0.13372", BinPath = @"C:\C11\bin", IsWindowsVersion = false });
        older.Versions.Add(new ClarionVersionConfig { Name = "Clarion 11.0.13372", BinPath = @"C:\C11\bin", IsWindowsVersion = null });
        Expect("flag missing on the Win32 entry, False on .NET, exe version unreadable -> the Win32 entry",
               "Clarion 11.0.13372", older.GetCurrentConfig() != null ? older.GetCurrentConfig().Name : "(null)");

        // ===== GH #247: ClarionNet40.red handed to a Win32 Clarion. The bin-folder match above was fixed for
        // #209, but three other routes still took a Clarion.NET entry: the standalone server's root lookup and
        // the first-listed fallback took the FIRST entry in XML order, and an IDE choice naming a .NET entry was
        // accepted as is. Every Clarion registers its .NET compiler on its own bin and root, so which machines
        // broke depended only on the order Clarion wrote the entries in.
        Ok("premise: the fixture lists a Clarion.NET entry first", parsed.Versions[0].IsWindowsVersion == false,
           parsed.Versions[0].Name);
        var v11 = ClarionVersionService.ParsePropertiesXml(xml);
        Expect("root lookup: a server under C:\\Clarion\\v11 -> the Win32 entry, not the .NET one sharing the root",
               "Clarion 11.0.13372", Name(v11.ResolveByRoot(@"C:\Clarion\v11")));
        Expect("root lookup: a trailing backslash on the root changes nothing",
               "Clarion 11.0.13372", Name(v11.ResolveByRoot(@"C:\Clarion\v11\")));
        Expect("first listed: an exe on no configured bin -> the first Win32 entry, not the first entry",
               "Clarion v11.0.13401.TXDocs", Pick(xml, @"D:\Junction\v11\bin\Clarion.exe", "11.0.0.13372", null));
        Expect("first listed: the standalone server (its own exe, no version name) -> the first Win32 entry",
               "Clarion v11.0.13401.TXDocs",
               Pick(xml, @"C:\Clarion\v11\accessory\addins\ClarionAssistant\ClarionMcpServer.exe", "5.9.0.1267", ""));
        Expect("IDE choice naming a Clarion.NET entry -> the running Win32 IDE's own entry",
               "Clarion 11.0.13372", Pick(xml, V11, "11.0.0.13372", "Clarion.NET 4.0.13372"));

        // A Clarion 12 layout with the .NET entry first on the shared bin and root (the order Kevin's file has;
        // a machine whose file lists Win32 first never showed the bug).
        var c12 = new ClarionVersionInfo { ClarionExePath = @"D:\Elsewhere\bin\Clarion.exe", CurrentVersionName = "" };
        c12.Versions.Add(new ClarionVersionConfig { Name = "Clarion.NET 4.0.14373", BinPath = @"C:\Clarion\C12\bin",
            RootPath = @"C:\Clarion\C12", IsWindowsVersion = false, CWVersion = 2000, RedFileName = "ClarionNet40.red" });
        c12.Versions.Add(new ClarionVersionConfig { Name = "Clarion 12.0.14373", BinPath = @"C:\Clarion\C12\bin",
            RootPath = @"C:\Clarion\C12", IsWindowsVersion = true, CWVersion = 12026, RedFileName = "Clarion120.red" });
        Expect("C12, .NET listed first: root lookup -> Clarion120.red", "Clarion120.red", Red(c12.ResolveByRoot(@"C:\Clarion\C12")));
        Expect("C12, .NET listed first: first listed -> Clarion120.red", "Clarion120.red", Red(c12.GetCurrentConfig()));

        // Degrade, never to none: with only .NET entries there is nothing better, so the old answer stands.
        var netOnly = new ClarionVersionInfo { ClarionExePath = @"D:\Elsewhere\bin\Clarion.exe" };
        netOnly.Versions.Add(new ClarionVersionConfig { Name = "Clarion.NET 4.0.14373", BinPath = @"C:\Clarion\C12\bin",
            RootPath = @"C:\Clarion\C12", IsWindowsVersion = false });
        Expect("only a .NET entry: first listed still returns it", "Clarion.NET 4.0.14373", Name(netOnly.GetCurrentConfig()));
        Expect("only a .NET entry at the root: root lookup still returns it", "Clarion.NET 4.0.14373",
               Name(netOnly.ResolveByRoot(@"C:\Clarion\C12")));
        netOnly.CurrentVersionName = "Clarion.NET 4.0.14373";
        Expect("only a .NET entry, and the IDE names it: still returned", "Clarion.NET 4.0.14373", Name(netOnly.GetCurrentConfig()));

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }
}
