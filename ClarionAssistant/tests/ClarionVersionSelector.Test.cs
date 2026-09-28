using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClarionAssistant.Services;

// Harness for ClarionVersionSelector (16d140e9, 286f2e57) — compiles the REAL ClarionVersionService.cs on its own
// and drives the pure selection rule with in-memory version lists, then scans the addin's sources.
//
// THE RULE (286f2e57, the Owner's decision): CA's Clarion version is the IDE's Build > Set Clarion Version and
// nothing else. CA DISPLAYS it (the header VERSION is read-only) and has no picker of its own.
//   - an explicit IDE name that is configured -> that entry;
//   - "(Current Version)" / empty / an unknown name -> the running Clarion.exe's own entry, labelled "(IDE)".
//
// THE BUG. CA's VERSION dropdown saved an override (GH #32; per solution since 16d140e9) that could outrank the
// IDE. On the Owner's C12 IDE, settings.txt held a legacy "Clarion.Version.Override=Clarion 10 Active And
// Updated" (written by another IDE — settings.txt is shared), and with the IDE on "(Current Version)" — or on
// "Clarion 12.0.14313", which Clarion STORES as Current (Versions.ActiveWinVersion setter) — CA showed
// "Clarion 10 Active And Updated (saved)". The overrides are no longer read, and are deleted at addin start.
//
// Run:  tests\Run-Tests.ps1   (passes the ClarionAssistant project dir as the only argument)
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class ClarionVersionSelectorTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    const string C10 = "Clarion 10 Active And Updated";
    const string C11 = "Clarion 11.0.13372";
    const string C12 = "Clarion 12.0.14313";
    const string C12Net = "Clarion.NET 4.0.14313";
    const string Aion = "AionPOS";
    const string C12Custom = "POSitive C12";   // a hand-named Win32 entry sharing the running C12 bin
    const string C12Exe = @"C:\Clarion12\bin\Clarion.exe";
    const string C10Exe = @"C:\Clarion10v8\bin\Clarion.exe";

    // The Owner's machine, cut down: by default the running IDE is C12; C10 and C11-13372 are other installs.
    // C:\Clarion12\bin carries the .NET compiler entry, a hand-named Win32 entry and the IDE's own entry, with
    // the IDE's own entry LAST, so first-match would be wrong.
    static ClarionVersionInfo Info(string ideChoice, bool live = true, string exe = C12Exe)
    {
        var info = new ClarionVersionInfo
        {
            ClarionExePath = exe,
            ClarionExeVersion = exe == C12Exe ? new Version(12, 0, 0, 14313) : new Version(10, 0, 0, 12799),
            CurrentVersionName = ideChoice,
            CurrentVersionFromLiveIde = live
        };
        info.Versions.Add(Cfg(C11, @"C:\Clarion11-13372\bin", true, 11000));
        info.Versions.Add(Cfg(C10, @"C:\Clarion10v8\bin", true, 10000));
        info.Versions.Add(Cfg(C12Net, @"C:\Clarion12\bin", false, 2000));
        info.Versions.Add(Cfg(Aion, @"C:\Clarion10v8\bin", true, 10000));
        info.Versions.Add(Cfg(C12Custom, @"C:\Clarion12\bin", true, 12026));
        info.Versions.Add(Cfg(C12, @"C:\Clarion12\bin", true, 12026));
        return info;
    }

    static ClarionVersionConfig Cfg(string name, string bin, bool? win, int? cw)
    {
        return new ClarionVersionConfig { Name = name, BinPath = bin, IsWindowsVersion = win, CWVersion = cw,
            LibSrcPaths = new List<string>(), Macros = new Dictionary<string, string>() };
    }

    static void Expect(string name, ClarionVersionSelection sel, string wantName, ClarionVersionTier wantTier)
    {
        string got = sel.Config != null ? sel.Config.Name : "(null)";
        Ok(name, got == wantName && sel.Tier == wantTier,
           "want " + wantName + "/" + wantTier + ", got " + got + "/" + sel.Tier);
    }

    static string Read(string repo, string rel)
    {
        string p = Path.Combine(repo, rel);
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    static int Main(string[] args)
    {
        // --- The IDE decides, and says which tier did.
        Expect("IDE names C10 -> C10 by IDE selection",
               ClarionVersionSelector.Select(Info(C10)), C10, ClarionVersionTier.IdeSelection);
        Expect("IDE on Current (empty Clarion.Version) -> the running C12 IDE's Win32 entry",
               ClarionVersionSelector.Select(Info("")), C12, ClarionVersionTier.RunningExe);
        Expect("IDE names an entry that no longer exists -> running exe, not silently the first entry",
               ClarionVersionSelector.Select(Info("Clarion 9 Gone")), C12, ClarionVersionTier.RunningExe);

        // ===== 286f2e57: the Owner's live repro on 5.9.0.1264, C12 IDE (C:\Clarion12\bin\Clarion.exe, 12.0.14313) =====
        // Clarion stores both "(Current Version)" and the running version's own name as a NULL Clarion.Version
        // (Versions.ActiveWinVersion setter: IsCurrent(value) is true for value == the running version's name),
        // so steps 1 and 3 reach CA as the same live empty string.
        var step1 = ClarionVersionSelector.Select(Info(""));
        Expect("owner step 1: IDE '(Current Version)' (live empty) -> the running C12", step1, C12, ClarionVersionTier.RunningExe);
        Ok("  ... labelled (IDE)", step1.ShortSource == "IDE", step1.ShortSource);
        var step2 = ClarionVersionSelector.Select(Info(Aion));
        Expect("owner step 2: IDE names AionPOS -> AionPOS", step2, Aion, ClarionVersionTier.IdeSelection);
        Ok("  ... labelled (IDE)", step2.ShortSource == "IDE", step2.ShortSource);
        var step3 = ClarionVersionSelector.Select(Info(""));
        Expect("owner step 3: IDE picks 'Clarion 12.0.14313' (stored as Current) -> the running C12 again", step3, C12, ClarionVersionTier.RunningExe);
        Ok("  ... labelled (IDE)", step3.ShortSource == "IDE", step3.ShortSource);
        Expect("owner step 3, had the IDE kept the explicit name -> C12 by IDE selection",
               ClarionVersionSelector.Select(Info(C12)), C12, ClarionVersionTier.IdeSelection);
        Expect("the literal '(Current Version)' string behaves as step 1",
               ClarionVersionSelector.Select(Info("(Current Version)")), C12, ClarionVersionTier.RunningExe);
        Ok("Describe() names the running exe and the IDE's choice",
           step1.Describe().Contains("the running Clarion.exe") && step1.Describe().Contains("'Current'"), step1.Describe());

        // --- Shared bin: C:\Clarion12\bin holds the .NET compiler, a hand-named Win32 entry and the IDE's own.
        Ok("shared bin: running C12 resolves to 'Clarion 12.0.14313', not the .NET or hand-named entry",
           Info("").GetCurrentConfig().Name == C12, Info("").GetCurrentConfig().Name);
        // CWVersion alone must separate C12 from Clarion.NET when neither entry says IsWindowsVersion and the exe's
        // build is unreadable — the only discriminator left is the major version in CWVersion.
        var cwOnly = new ClarionVersionInfo { ClarionExePath = C12Exe, ClarionExeVersion = new Version(12, 0, 0, 0), CurrentVersionName = "" };
        cwOnly.Versions.Add(Cfg(C12Net, @"C:\Clarion12\bin", null, 2000));
        cwOnly.Versions.Add(Cfg(C12, @"C:\Clarion12\bin", null, 12026));
        Ok("shared bin, no IsWindowsVersion, no build: CWVersion's major picks Clarion 12 over Clarion.NET (2000)",
           cwOnly.GetCurrentConfig().Name == C12, cwOnly.GetCurrentConfig().Name);
        var cwMissing = new ClarionVersionInfo { ClarionExePath = C12Exe, ClarionExeVersion = new Version(12, 0, 0, 0), CurrentVersionName = "" };
        cwMissing.Versions.Add(Cfg(C12Net, @"C:\Clarion12\bin", null, null));
        cwMissing.Versions.Add(Cfg(C12, @"C:\Clarion12\bin", null, null));
        Ok("  ... and with no CWVersion either, nothing narrows: first match, as before (degrades, never to none)",
           cwMissing.GetCurrentConfig().Name == C12Net, cwMissing.GetCurrentConfig().Name);
        Expect("a C10 IDE on Current resolves its own bin's entry",
               ClarionVersionSelector.Select(Info("", exe: C10Exe)), C10, ClarionVersionTier.RunningExe);

        // --- Outside the IDE the choice comes from the XML (the standalone server), and Describe says so.
        var offline = ClarionVersionSelector.Select(Info(C10, live: false));
        Ok("XML-sourced choice is labelled as such", !offline.IdeChoiceLive && offline.Describe().Contains("ClarionProperties.xml"),
           offline.Describe());
        Ok("no ClarionProperties.xml -> no config, tier None", ClarionVersionSelector.Select(null).Config == null
           && ClarionVersionSelector.Select(null).Tier == ClarionVersionTier.None);

        // --- The retired overrides: which settings keys addin start deletes.
        var stale = ClarionVersionSelector.StaleOverrideKeys(new[] {
            "Clarion.Version.Override", @"Clarion.Version.Override@H:\DEV\APOSITIVE\V61POSITIVE.SLN",
            "Clarion.Version", "Clarion.Version.OverrideX", "Theme", null });
        Ok("retired keys: the legacy global and every per-solution record, nothing else",
           stale.Count == 2 && stale.Contains("Clarion.Version.Override")
           && stale.Contains(@"Clarion.Version.Override@H:\DEV\APOSITIVE\V61POSITIVE.SLN"), string.Join(" | ", stale));

        // ===== Source scans: CA has no second place to set the version =====
        string repo = args.Length > 0 ? args[0] : null;
        if (repo == null || !Directory.Exists(repo))
        {
            Ok("source scans: project dir passed by Run-Tests.ps1", false, repo ?? "(no argument)");
        }
        else
        {
            string header = Read(repo, @"Terminal\header.html") ?? "";
            string acc = Read(repo, "AssistantChatControl.cs") ?? "";
            string eff = Read(repo, @"Services\EffectiveClarionVersion.cs") ?? "";
            string svc = Read(repo, @"Services\ClarionVersionService.cs") ?? "";
            Ok("scan premise: the header, panel, resolver and service sources were read",
               header.Length > 0 && acc.Length > 0 && eff.Length > 0 && svc.Length > 0);
            Ok("header: VERSION is a read-only value (#versionValue), not a <select>",
               header.Contains("id=\"versionValue\"") && !Regex.IsMatch(header, @"<select[^>]*id=""versionSelect""")
               && !header.Contains("versionSelect"));
            Ok("header: nothing posts 'versionChanged'", !header.Contains("versionChanged"));
            Ok("panel: no 'versionChanged' action and no OnVersionChanged handler",
               !acc.Contains("versionChanged") && !acc.Contains("OnVersionChanged"));
            // A legacy global override or a per-solution record in settings.txt is IGNORED: resolution is the pure
            // Select(info) with nothing read from settings, and nothing in the resolver saves or clears one.
            Ok("resolver: Resolve(info) is exactly Select(info) - no legacy or per-solution override is read",
               Regex.IsMatch(eff, @"Resolve\(ClarionVersionInfo info\)\s*\{\s*return ClarionVersionSelector\.Select\(info\);\s*\}")
               && !eff.Contains(".Get(") && !eff.Contains("OverrideKeyFor") && !eff.Contains("LegacyOverrideKey"));
            Ok("resolver: no SaveOverride / ClearOverride setter", !eff.Contains("SaveOverride") && !eff.Contains("ClearOverride"));
            Ok("service: no SavedOverride tier, no override-aware Select",
               !svc.Contains("SavedOverride") && !svc.Contains("SelectForSolution") && !svc.Contains("BasisMatches"));
            Ok("panel: the refresh action no longer clears an override",
               !acc.Contains("ClearOverride"));
        }

        // ===== Library-graph key per version (16d140e9 pipeline run 1, finding 2) =====
        var info2 = Info(C10);
        var c10 = info2.Versions.Find(v => v.Name == C10);
        var c11 = info2.Versions.Find(v => v.Name == C11);
        c10.RootPath = @"C:\Clarion10v8"; c11.RootPath = @"C:\Clarion11-13372";
        Ok("two configured versions under one running IDE -> distinct library-graph keys (same build string)",
           c10.LibraryGraphKey("12.0.0.14313") != c11.LibraryGraphKey("12.0.0.14313"));
        var twin = Cfg("Clarion 10 v8", @"C:\Clarion10v8\bin", true, 10000); twin.RootPath = @"c:\clarion10v8\";
        Ok("two entries on one root -> one key (same LibSrc)", c10.LibraryGraphKey("10.0.0.12799") == twin.LibraryGraphKey("10.0.0.12799"));
        Ok("key carries the version's own build", c10.LibraryGraphKey("10.0.0.12799").StartsWith("10.0.0.12799_"));

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
