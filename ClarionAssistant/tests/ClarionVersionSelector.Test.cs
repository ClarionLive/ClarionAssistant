using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

// Harness for ClarionVersionSelector (16d140e9) — compiles the REAL ClarionVersionService.cs on its own and
// drives the pure selection rules with in-memory version lists.
//
// THE BUG. The Owner picked "Clarion 10 Active And Updated" in the IDE's Build > Set Clarion Version, but CA
// (panel, CodeGraph indexer, LSP) kept resolving "Clarion 11.0.13372": CA's own VERSION dropdown choice
// (settings key Clarion.Version.Override) was ONE global value that beat the IDE's choice forever, and
// nothing re-read the IDE when it changed. The rules under test:
//   - the IDE's choice is the authority; a CA override applies only while the IDE's choice is the one it was
//     saved against (its basis), and is otherwise SUSPENDED — never deleted by a resolution;
//   - overrides are stored per solution, as one record; "Current" bases are qualified by the running exe;
//   - the library-graph DB key belongs to the selected version, not the running IDE.
//
// Run:  tests\Run-Tests.ps1
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
    static ClarionVersionInfo Info(string ideChoice, bool live = true, string exe = C12Exe)
    {
        var info = new ClarionVersionInfo
        {
            ClarionExePath = exe,
            ClarionExeVersion = exe == C12Exe ? new Version(12, 0, 0, 14313) : new Version(10, 0, 0, 12799),
            CurrentVersionName = ideChoice,
            CurrentVersionFromLiveIde = live
        };
        info.Versions.Add(Cfg(C11, @"C:\Clarion11-13372\bin", true));
        info.Versions.Add(Cfg(C10, @"C:\Clarion10v8\bin", true));
        info.Versions.Add(Cfg(C12Net, @"C:\Clarion12\bin", false));
        info.Versions.Add(Cfg(Aion, @"C:\Clarion10v8\bin", true));
        info.Versions.Add(Cfg(C12Custom, @"C:\Clarion12\bin", true));
        info.Versions.Add(Cfg(C12, @"C:\Clarion12\bin", true));
        return info;
    }

    static ClarionVersionConfig Cfg(string name, string bin, bool win)
    {
        return new ClarionVersionConfig { Name = name, BinPath = bin, IsWindowsVersion = win,
            LibSrcPaths = new List<string>(), Macros = new Dictionary<string, string>() };
    }

    static void Expect(string name, ClarionVersionSelection sel, string wantName, ClarionVersionTier wantTier,
                       SavedOverrideState wantState)
    {
        string got = sel.Config != null ? sel.Config.Name : "(null)";
        Ok(name, got == wantName && sel.Tier == wantTier && sel.OverrideState == wantState,
           "want " + wantName + "/" + wantTier + "/" + wantState + ", got " + got + "/" + sel.Tier + "/" + sel.OverrideState);
    }

    static string Key(ClarionVersionInfo i) { return ClarionVersionSelector.IdeChoiceKey(i); }

    static int Main()
    {
        // --- No override: the IDE decides, and says which tier did.
        Expect("IDE names C10, no override -> C10 by IDE selection",
               ClarionVersionSelector.Select(Info(C10), null, null), C10, ClarionVersionTier.IdeSelection, SavedOverrideState.None);
        Expect("IDE on Current (empty Clarion.Version), no override -> the running C12 IDE's Win32 entry",
               ClarionVersionSelector.Select(Info(""), null, null), C12, ClarionVersionTier.RunningExe, SavedOverrideState.None);
        Expect("IDE on '(Current Version)' -> running exe",
               ClarionVersionSelector.Select(Info("(Current Version)"), null, null), C12, ClarionVersionTier.RunningExe, SavedOverrideState.None);
        Expect("IDE names an entry that no longer exists -> running exe, not silently the first entry",
               ClarionVersionSelector.Select(Info("Clarion 9 Gone"), null, null), C12, ClarionVersionTier.RunningExe, SavedOverrideState.None);

        // --- THE OWNER'S CASE: a CA override saved while the IDE was on C11-13372; the developer then picks
        //     C10 in Build > Set Clarion Version. The IDE must win; the override is suspended, not applied.
        var owner = ClarionVersionSelector.Select(Info(C10), C11, C11);
        Expect("override C11 saved against IDE=C11, IDE now C10 -> C10 wins, override suspended",
               owner, C10, ClarionVersionTier.IdeSelection, SavedOverrideState.Suspended);
        Ok("  ... and the Note says why the override is not applied",
           owner.Note != null && owner.Note.Contains(C11) && owner.Note.Contains(C10), owner.Note);
        Ok("  ... and Describe() names the deciding tier",
           owner.Describe().Contains("chosen by the IDE's Build > Set Clarion Version") && owner.Describe().Contains(C10), owner.Describe());
        Expect("  ... and when the IDE returns to C11 the SAME record applies again (suspended, not deleted)",
               ClarionVersionSelector.Select(Info(C11), C11, C11), C11, ClarionVersionTier.SavedOverride, SavedOverrideState.Applied);

        // A LEGACY override (no basis — every override saved before this fix) against an explicit IDE choice.
        Expect("legacy override C11 (no basis), IDE explicitly C10 -> C10 wins, override suspended",
               ClarionVersionSelector.Select(Info(C10), C11, null), C10, ClarionVersionTier.IdeSelection, SavedOverrideState.Suspended);

        // --- The override still does its job while the IDE has not moved.
        Expect("override C10 saved against IDE=C11, IDE still C11 -> the override applies",
               ClarionVersionSelector.Select(Info(C11), C10, C11), C10, ClarionVersionTier.SavedOverride, SavedOverrideState.Applied);
        Expect("legacy override on the running bin, IDE on Current -> applies (GH #32's case)",
               ClarionVersionSelector.Select(Info(""), C12Custom, null), C12Custom, ClarionVersionTier.SavedOverride, SavedOverrideState.Applied);

        // "Current" bases are qualified by the running exe: a C10 IDE and a C12 IDE both on Current differ.
        string keyC12Current = Key(Info(""));
        Ok("IdeChoiceKey for Current names the running exe folder", keyC12Current == @"Current@C:\CLARION12\BIN", keyC12Current);
        Expect("override saved on Current in the C12 IDE applies in the C12 IDE on Current",
               ClarionVersionSelector.Select(Info(""), C10, keyC12Current), C10, ClarionVersionTier.SavedOverride, SavedOverrideState.Applied);
        Expect("... but is suspended in a C10 IDE on Current (a different version)",
               ClarionVersionSelector.Select(Info("", exe: C10Exe), C11, keyC12Current), C10, ClarionVersionTier.RunningExe, SavedOverrideState.Suspended);
        Ok("an unqualified 'Current' basis (legacy) matches Current in any IDE",
           ClarionVersionSelector.BasisMatches("Current", Key(Info("", exe: C10Exe))) && ClarionVersionSelector.BasisMatches("(Current Version)", keyC12Current));

        // Override naming a version this IDE does not have: IDE decides, record kept.
        Expect("override names a version not configured here -> IDE decides (Unavailable, kept)",
               ClarionVersionSelector.Select(Info(C10), "Clarion 9 Gone", C10), C10, ClarionVersionTier.IdeSelection, SavedOverrideState.Unavailable);

        // Outside the IDE the choice comes from the XML, and Describe says so.
        var offline = ClarionVersionSelector.Select(Info(C10, live: false), null, null);
        Ok("XML-sourced choice is labelled as such", !offline.IdeChoiceLive && offline.Describe().Contains("ClarionProperties.xml"),
           offline.Describe());

        // Null info.
        var none = ClarionVersionSelector.Select(null, C10, C10);
        Ok("no ClarionProperties.xml -> no config, tier None", none.Config == null && none.Tier == ClarionVersionTier.None);

        // GetCurrentConfig (used by the pre-existing ExeMatch harness and IDE-free callers) is unchanged.
        Ok("GetCurrentConfig still returns the IDE's named choice", Info(C10).GetCurrentConfig().Name == C10);

        // ===== 286f2e57: the Owner's live repro on 5.9.0.1264, C12 IDE (C:\Clarion12\bin\Clarion.exe, 12.0.14313) =====
        // settings.txt holds ONLY the legacy global "Clarion.Version.Override=Clarion 10 Active And Updated" (no
        // per-solution record); ClarionProperties.xml still says "Clarion 10 Active And Updated". Clarion stores
        // both "(Current Version)" and the running version's own name as a NULL Clarion.Version
        // (Versions.ActiveWinVersion setter: IsCurrent(value) is true for value == the running version's name),
        // so steps 1 and 3 reach CA as the same live empty string.
        var step1 = ClarionVersionSelector.SelectForSolution(Info(""), null, C10);
        Expect("owner step 1: IDE '(Current Version)' (live empty), legacy C10 override -> running C12, override suspended",
               step1, C12, ClarionVersionTier.RunningExe, SavedOverrideState.Suspended);
        Ok("  ... labelled (IDE), not (saved)", step1.ShortSource == "IDE", step1.ShortSource);
        Ok("  ... and the Note names the override and its bin", step1.Note != null && step1.Note.Contains(C10) && step1.Note.Contains(@"C:\Clarion10v8\bin"), step1.Note);
        var step2 = ClarionVersionSelector.SelectForSolution(Info(Aion), null, C10);
        Expect("owner step 2: IDE names AionPOS -> AionPOS by the IDE",
               step2, Aion, ClarionVersionTier.IdeSelection, SavedOverrideState.Suspended);
        Ok("  ... labelled (IDE)", step2.ShortSource == "IDE", step2.ShortSource);
        var step3 = ClarionVersionSelector.SelectForSolution(Info(""), null, C10);
        Expect("owner step 3: IDE picks 'Clarion 12.0.14313' (stored as Current) -> running C12 again, not the saved C10",
               step3, C12, ClarionVersionTier.RunningExe, SavedOverrideState.Suspended);
        Ok("  ... labelled (IDE)", step3.ShortSource == "IDE", step3.ShortSource);
        Expect("owner step 3, had the IDE kept the explicit name -> C12 by IDE selection, override suspended",
               ClarionVersionSelector.SelectForSolution(Info(C12), null, C10), C12, ClarionVersionTier.IdeSelection, SavedOverrideState.Suspended);
        Expect("the literal '(Current Version)' string behaves as step 1",
               ClarionVersionSelector.SelectForSolution(Info("(Current Version)"), null, C10), C12, ClarionVersionTier.RunningExe, SavedOverrideState.Suspended);
        Expect("a per-solution record with an unqualified 'Current' basis naming another install's entry is suspended too",
               ClarionVersionSelector.SelectForSolution(Info(""), ClarionVersionSelector.EncodeOverride(C10, "Current"), null),
               C12, ClarionVersionTier.RunningExe, SavedOverrideState.Suspended);
        Expect("a deliberate per-solution pick made in THIS IDE on Current still applies (38de012's intent)",
               ClarionVersionSelector.SelectForSolution(Info(""), ClarionVersionSelector.EncodeOverride(C10, keyC12Current), C10),
               C10, ClarionVersionTier.SavedOverride, SavedOverrideState.Applied);
        // Shared bin: C:\Clarion12\bin carries the .NET compiler entry, a hand-named Win32 entry and the IDE's own
        // entry; the running exe resolves to the one whose name carries its build, listed LAST here.
        Ok("shared bin: running C12 resolves to 'Clarion 12.0.14313', not the .NET or hand-named entry",
           Info("").GetCurrentConfig().Name == C12, Info("").GetCurrentConfig().Name);

        // ===== Per-solution storage (pipeline run 1, finding 3) =====
        const string SlnA = @"H:\Dev\aPOSitive\v61POSitive.sln";
        const string SlnB = @"H:\Dev\Other\Other.sln";
        string keyA = ClarionVersionSelector.OverrideKeyFor(SlnA);
        Ok("key: one .sln under different case/slashes -> one key",
           keyA == ClarionVersionSelector.OverrideKeyFor(@"h:/dev/APOSITIVE/v61positive.SLN"), keyA);
        Ok("key: two solutions -> two keys", keyA != ClarionVersionSelector.OverrideKeyFor(SlnB));
        Ok("key: never contains '=' (settings.txt forbids it)", !ClarionVersionSelector.OverrideKeyFor(@"C:\a=b\x.sln").Contains("="));

        string rec = ClarionVersionSelector.EncodeOverride(C10, C11);
        string rn, rb;
        Ok("record: name + basis round-trip in ONE value",
           ClarionVersionSelector.TryDecodeOverride(rec, out rn, out rb) && rn == C10 && rb == C11, rec);
        Ok("record: a qualified Current basis round-trips",
           ClarionVersionSelector.TryDecodeOverride(ClarionVersionSelector.EncodeOverride(C10, keyC12Current), out rn, out rb) && rb == keyC12Current, rb);
        Ok("record: empty value is no override", !ClarionVersionSelector.TryDecodeOverride("", out rn, out rb));

        // Solution A saved C10 against IDE=C11. An IDE on solution B reads B's key (no record) and is unaffected.
        Expect("solution B (no record of its own) ignores A's record",
               ClarionVersionSelector.SelectForSolution(Info(C12), null, null), C12, ClarionVersionTier.IdeSelection, SavedOverrideState.None);
        Expect("solution A's record applies while A's IDE choice is its basis",
               ClarionVersionSelector.SelectForSolution(Info(C11), rec, null), C10, ClarionVersionTier.SavedOverride, SavedOverrideState.Applied);
        Expect("a solution's own record wins over the legacy global value",
               ClarionVersionSelector.SelectForSolution(Info(C11), rec, C12), C10, ClarionVersionTier.SavedOverride, SavedOverrideState.Applied);
        Expect("no record: the legacy global value applies while the IDE is on Current (entry on the running bin)",
               ClarionVersionSelector.SelectForSolution(Info(""), null, C12Custom), C12Custom, ClarionVersionTier.SavedOverride, SavedOverrideState.Applied);
        Expect("no record: the legacy global value is suspended when the solution names a version",
               ClarionVersionSelector.SelectForSolution(Info(C10), null, C11), C10, ClarionVersionTier.IdeSelection, SavedOverrideState.Suspended);

        // A non-live (XML) read, or any mismatch, never produces a delete: resolution is read-only by type —
        // the selection carries no write instruction at all, only Applied / Suspended / Unavailable.
        var nonLive = ClarionVersionSelector.SelectForSolution(Info(C10, live: false), rec, C11);
        Ok("a non-live read with a mismatched basis suspends, and a later matching read applies the same record",
           nonLive.OverrideState == SavedOverrideState.Suspended
           && ClarionVersionSelector.SelectForSolution(Info(C11), rec, C11).OverrideState == SavedOverrideState.Applied);

        // ===== Library-graph key per version (pipeline run 1, finding 2) =====
        var info2 = Info(C10);
        var c10 = info2.Versions.Find(v => v.Name == C10);
        var c11 = info2.Versions.Find(v => v.Name == C11);
        c10.RootPath = @"C:\Clarion10v8"; c11.RootPath = @"C:\Clarion11-13372";
        Ok("two configured versions under one running IDE -> distinct library-graph keys (same build string)",
           c10.LibraryGraphKey("12.0.0.14313") != c11.LibraryGraphKey("12.0.0.14313"));
        var twin = Cfg("Clarion 10 v8", @"C:\Clarion10v8\bin", true); twin.RootPath = @"c:\clarion10v8\";
        Ok("two entries on one root -> one key (same LibSrc)", c10.LibraryGraphKey("10.0.0.12799") == twin.LibraryGraphKey("10.0.0.12799"));
        Ok("key carries the version's own build", c10.LibraryGraphKey("10.0.0.12799").StartsWith("10.0.0.12799_"));

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
