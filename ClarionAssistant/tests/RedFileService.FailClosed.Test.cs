using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

// Harness for RedFileService.LoadForProject failing CLOSED (f3b47441; raised by both Codex gates in
// 905928c7's pipeline).
//
// THE BUG. Active changed only on a successful parse. A Build > Set Clarion Version switch to a version
// whose .red was missing or unreadable logged "nothing loaded" and left Active on the PREVIOUS version's
// file, so generated-module capture and the Data pad's Files tab went on resolving through the wrong
// version's paths while looking healthy. Now every LoadForProject failure clears Active; a later
// successful load restores it. ForProjectDirectory (a per-.app lookup, never made Active) must not clear it.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class RedFileServiceFailClosedTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static ClarionVersionConfig Config(string redPath)
    {
        return new ClarionVersionConfig
        {
            RedFileName = redPath != null ? Path.GetFileName(redPath) : "Clarion120.red",
            RedFilePath = redPath,
            Macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-red-failclosed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string redA = Path.Combine(root, "binA", "Clarion100.red");
            Directory.CreateDirectory(Path.GetDirectoryName(redA));
            File.WriteAllText(redA, "[Common]\r\n*.clw = .\\Source\r\n");
            string redB = Path.Combine(root, "binB", "Clarion120.red");
            Directory.CreateDirectory(Path.GetDirectoryName(redB));
            string slnDir = Path.Combine(root, "sln");
            Directory.CreateDirectory(slnDir);

            Func<RedFileService> loadA = () =>
            {
                var a = new RedFileService();
                return a.LoadForProject(slnDir, Config(redA)) ? a : null;
            };

            // (a) Baseline: a good load becomes Active.
            var svcA = loadA();
            Ok("version A's .red loads and becomes Active", svcA != null && ReferenceEquals(RedFileService.Active, svcA));

            // (b) Switch to a version whose .red does not exist.
            bool okMissing = new RedFileService().LoadForProject(slnDir, Config(redB));
            Ok("missing .red: LoadForProject returns false", !okMissing);
            Ok("missing .red: Active is cleared, not left on A", RedFileService.Active == null,
               RedFileService.Active != null ? RedFileService.Active.RedFilePath : null);

            // (c) The file exists but can't be read (held exclusively, as by an editor mid-save).
            svcA = loadA();
            File.WriteAllText(redB, "[Common]\r\n*.clw = .\\Other\r\n");
            using (new FileStream(redB, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool okLocked = new RedFileService().LoadForProject(slnDir, Config(redB));
                Ok("unreadable .red: LoadForProject returns false", !okLocked);
                Ok("unreadable .red: Active is cleared, not left on A", RedFileService.Active == null,
                   RedFileService.Active != null ? RedFileService.Active.RedFilePath : null);
            }

            // (d) Once it is readable again, a load recovers.
            var svcB = new RedFileService();
            Ok("readable again: B loads", svcB.LoadForProject(slnDir, Config(redB)));
            Ok("readable again: B is Active", ReferenceEquals(RedFileService.Active, svcB));

            // (e) No config, and a config with no RedFilePath: nothing is in force.
            loadA();
            new RedFileService().LoadForProject(slnDir, null);
            Ok("no version config: Active is cleared", RedFileService.Active == null);
            loadA();
            new RedFileService().LoadForProject(slnDir, Config(null));
            Ok("config without a RedFilePath (and no local .red): Active is cleared", RedFileService.Active == null);

            // (f) ForProjectDirectory is a per-.app lookup: a local .red it can't read falls back to the
            //     solution's service and must NOT clear Active.
            svcA = loadA();
            string projDir = Path.Combine(root, "proj");
            Directory.CreateDirectory(projDir);
            string localRed = Path.Combine(projDir, "Clarion100.red");
            File.WriteAllText(localRed, "[Common]\r\n*.clw = .\\Local\r\n");
            RedFileService got;
            using (new FileStream(localRed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                got = RedFileService.ForProjectDirectory(projDir, svcA);
            Ok("ForProjectDirectory with an unreadable local .red falls back", ReferenceEquals(got, svcA));
            Ok("...and leaves Active alone", ReferenceEquals(RedFileService.Active, svcA));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("RedFileService.FailClosed: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
