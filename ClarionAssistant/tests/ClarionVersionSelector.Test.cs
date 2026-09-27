using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

// Harness for ClarionVersionSelector.Select (16d140e9) — compiles the REAL ClarionVersionService.cs on its
// own and drives the pure selection rule with in-memory version lists.
//
// THE BUG. The Owner picked "Clarion 10 Active And Updated" in the IDE's Build > Set Clarion Version, but CA
// (panel, CodeGraph indexer, LSP) kept resolving "Clarion 11.0.13372": CA's own VERSION dropdown choice
// (settings key Clarion.Version.Override) was ONE global value that beat the IDE's choice forever, and
// nothing re-read the IDE when it changed. The rule under test: the IDE's choice is the authority; a CA
// override holds only while the IDE's choice is the one it was saved against (its basis).
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

    // The Owner's machine, cut down: the running IDE is C12; C10 and C11-13372 are other installs.
    static ClarionVersionInfo Info(string ideChoice, bool live = true)
    {
        var info = new ClarionVersionInfo
        {
            ClarionExePath = @"C:\Clarion12\bin\Clarion.exe",
            ClarionExeVersion = new Version(12, 0, 0, 14313),
            CurrentVersionName = ideChoice,
            CurrentVersionFromLiveIde = live
        };
        info.Versions.Add(Cfg(C11, @"C:\Clarion11-13372\bin", true));
        info.Versions.Add(Cfg(C10, @"C:\Clarion10v8\bin", true));
        info.Versions.Add(Cfg(C12Net, @"C:\Clarion12\bin", false));
        info.Versions.Add(Cfg(C12, @"C:\Clarion12\bin", true));
        return info;
    }

    static ClarionVersionConfig Cfg(string name, string bin, bool win)
    {
        return new ClarionVersionConfig { Name = name, BinPath = bin, IsWindowsVersion = win,
            LibSrcPaths = new List<string>(), Macros = new Dictionary<string, string>() };
    }

    static void Expect(string name, ClarionVersionSelection sel, string wantName, ClarionVersionTier wantTier,
                       SavedOverrideAction wantAction)
    {
        string got = sel.Config != null ? sel.Config.Name : "(null)";
        Ok(name, got == wantName && sel.Tier == wantTier && sel.OverrideAction == wantAction,
           "want " + wantName + "/" + wantTier + "/" + wantAction + ", got " + got + "/" + sel.Tier + "/" + sel.OverrideAction);
    }

    static int Main()
    {
        // --- No override: the IDE decides, and says which tier did.
        Expect("IDE names C10, no override -> C10 by IDE selection",
               ClarionVersionSelector.Select(Info(C10), null, null), C10, ClarionVersionTier.IdeSelection, SavedOverrideAction.None);
        Expect("IDE on Current (empty Clarion.Version), no override -> the running C12 IDE's Win32 entry",
               ClarionVersionSelector.Select(Info(""), null, null), C12, ClarionVersionTier.RunningExe, SavedOverrideAction.None);
        Expect("IDE on '(Current Version)' -> running exe",
               ClarionVersionSelector.Select(Info("(Current Version)"), null, null), C12, ClarionVersionTier.RunningExe, SavedOverrideAction.None);
        Expect("IDE names an entry that no longer exists -> running exe, not silently the first entry",
               ClarionVersionSelector.Select(Info("Clarion 9 Gone"), null, null), C12, ClarionVersionTier.RunningExe, SavedOverrideAction.None);

        // --- THE OWNER'S CASE: a CA override saved while the IDE was on C11-13372; the developer then picks
        //     C10 in Build > Set Clarion Version. The IDE must win and the stale override must be dropped.
        var owner = ClarionVersionSelector.Select(Info(C10), C11, C11);
        Expect("override C11 saved against IDE=C11, IDE now C10 -> C10 wins, override cleared",
               owner, C10, ClarionVersionTier.IdeSelection, SavedOverrideAction.Clear);
        Ok("  ... and the Note says why the override was dropped",
           owner.Note != null && owner.Note.Contains(C11) && owner.Note.Contains(C10), owner.Note);
        Ok("  ... and Describe() names the deciding tier",
           owner.Describe().Contains("chosen by the IDE's Build > Set Clarion Version") && owner.Describe().Contains(C10), owner.Describe());

        // A LEGACY override (no basis — every override saved before this fix) against an explicit IDE choice.
        Expect("legacy override C11 (no basis), IDE explicitly C10 -> C10 wins, override cleared",
               ClarionVersionSelector.Select(Info(C10), C11, null), C10, ClarionVersionTier.IdeSelection, SavedOverrideAction.Clear);

        // --- The override still does its job while the IDE has not moved.
        Expect("override C10 saved against IDE=C11, IDE still C11 -> the override holds",
               ClarionVersionSelector.Select(Info(C11), C10, C11), C10, ClarionVersionTier.SavedOverride, SavedOverrideAction.None);
        Expect("override saved against 'Current', IDE still Current -> the override holds",
               ClarionVersionSelector.Select(Info(""), C10, "Current"), C10, ClarionVersionTier.SavedOverride, SavedOverrideAction.None);
        Expect("basis spellings of Current are equivalent (basis '(Current Version)', IDE empty)",
               ClarionVersionSelector.Select(Info(null), C10, "(Current Version)"), C10, ClarionVersionTier.SavedOverride, SavedOverrideAction.None);

        // GH #32's case: a legacy override while the IDE is on Current keeps working, and gets its basis recorded.
        var legacyKept = ClarionVersionSelector.Select(Info(""), C10, null);
        Expect("legacy override, IDE on Current -> override holds and its basis is recorded",
               legacyKept, C10, ClarionVersionTier.SavedOverride, SavedOverrideAction.RecordBasis);
        Ok("  ... recorded basis is 'Current'", legacyKept.OverrideBasisToRecord == "Current", legacyKept.OverrideBasisToRecord);

        // Override whose entry was removed from ClarionProperties.xml.
        Expect("override names a version that is gone -> IDE decides, override cleared",
               ClarionVersionSelector.Select(Info(C10), "Clarion 9 Gone", C10), C10, ClarionVersionTier.IdeSelection, SavedOverrideAction.Clear);

        // Returning the IDE to the override's basis after it was dropped does NOT resurrect it: the caller
        // cleared it (Clear), so the next call sees no override. Modelled by passing none.
        Expect("after a Clear, IDE back on the old basis -> IDE decides (no resurrection)",
               ClarionVersionSelector.Select(Info(C11), null, null), C11, ClarionVersionTier.IdeSelection, SavedOverrideAction.None);

        // Outside the IDE the choice comes from the XML, and Describe says so.
        var offline = ClarionVersionSelector.Select(Info(C10, live: false), null, null);
        Ok("XML-sourced choice is labelled as such", !offline.IdeChoiceLive && offline.Describe().Contains("ClarionProperties.xml"),
           offline.Describe());

        // Null info.
        var none = ClarionVersionSelector.Select(null, C10, C10);
        Ok("no ClarionProperties.xml -> no config, tier None", none.Config == null && none.Tier == ClarionVersionTier.None);

        // GetCurrentConfig (used by the pre-existing ExeMatch harness and IDE-free callers) is unchanged.
        Ok("GetCurrentConfig still returns the IDE's named choice", Info(C10).GetCurrentConfig().Name == C10);

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
