using System;
using System.Diagnostics;
using System.IO;
using ClarionAssistant.Services;

// Harness for IdeEndpointRecord.SweepStale (GH #205 pipeline run 2): endpoint records carry a bearer token, and a
// record of an IDE that died without Stop (deploy kill, crash) used to linger until a standalone server scanned. Every
// Publish now sweeps them. Real records in a temp record dir (CA_IDE_RECORD_DIR), this test process standing in for the
// IDE (CA_IDE_IMAGE_NAME), so the pid/start-time trust checks run for real.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class EndpointRecordSweepTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static string Rec(string dir, int pid, int port, DateTime start)
    {
        string f = Path.Combine(dir, pid + "-" + port + ".json");
        File.WriteAllText(f, "{\"pid\":" + pid + ",\"port\":" + port + ",\"token\":\"t\",\"startTime\":\""
            + start.ToUniversalTime().ToString("o") + "\",\"writtenAt\":\"" + DateTime.UtcNow.ToString("o") + "\"}");
        return f;
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-sweep-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Environment.SetEnvironmentVariable("CA_TEST_HOOKS", "1");   // L2: the record hooks need it
        Environment.SetEnvironmentVariable(IdeEndpointRecord.RecordDirEnv, root);
        var me = Process.GetCurrentProcess();
        Environment.SetEnvironmentVariable(IdeEndpointRecord.ImageNameEnv, me.ProcessName);
        try
        {
            string dir = IdeEndpointRecord.DirectoryPath;
            Directory.CreateDirectory(dir);
            int dead = 999000 + new Random().Next(900);
            try { Process.GetProcessById(dead); dead++; } catch { }
            string live = Rec(dir, me.Id, 19401, me.StartTime);
            string gone = Rec(dir, dead, 19402, DateTime.UtcNow.AddMinutes(-5));
            string reused = Rec(dir, me.Id, 19403, me.StartTime.AddHours(-3));   // our pid, another start: pid reuse
            string garbled = Path.Combine(dir, me.Id + "-19404.json");
            File.WriteAllText(garbled, "{ half a rec");

            IdeEndpointRecord.Publish(19405, "secret");

            Ok("a record of a pid that is not running is deleted", !File.Exists(gone));
            Ok("a record whose start time does not match (pid reused) is deleted", !File.Exists(reused));
            Ok("this IDE's live record is kept", File.Exists(live));
            Ok("an unreadable record (maybe mid-replace) is left alone", File.Exists(garbled));
            Ok("the new record is written", File.Exists(Path.Combine(dir, me.Id + "-19405.json")));
            Ok("a sweep with nothing stale deletes nothing", IdeEndpointRecord.SweepStale() == 0);

            // Pipeline run 3: a record whose pid/port is not a number used to THROW out of discovery (Convert.ToInt32
            // outside any try), so one bad file hid every IDE.
            string badLive = Path.Combine(dir, me.Id + "-19406.json");
            File.WriteAllText(badLive, "{\"pid\":\"abc\",\"port\":1e12,\"token\":\"t\",\"startTime\":\"" + me.StartTime.ToUniversalTime().ToString("o")
                + "\",\"writtenAt\":\"" + DateTime.UtcNow.ToString("o") + "\"}");
            string badDead = Path.Combine(dir, dead + "-19407.json");
            File.WriteAllText(badDead, "{\"pid\":\"abc\",\"port\":\"x\",\"token\":\"t\"}");
            string reason;
            System.Collections.Generic.List<IdeEndpointRecord.Endpoint> eps = null;
            try { eps = IdeEndpointRecord.DiscoverAll(out reason); } catch (Exception ex) { reason = "THREW " + ex.GetType().Name; }
            Ok("a record with a non-numeric pid/port does not abort discovery", eps != null && eps.Exists(e => e.Pid == me.Id), eps == null ? "threw" : eps.Count + " found");
            Ok("...it is skipped but KEPT while its file name's pid is running (not provably stale)", File.Exists(badLive));
            Ok("...and deleted once its file name's pid is not running (provably stale)", !File.Exists(badDead));

            // b8f9431c review L5: Publish writes the forwarding protocol (IdeProxy.StandaloneE2E L5 proves discovery reads it).
            string pubText = File.ReadAllText(Path.Combine(dir, me.Id + "-19405.json"));
            Ok("L5: the published record says which IDE-tool forwarding protocol this add-in serves (\"ideTools\":1)",
                pubText.Contains("\"ideTools\":1"), pubText.Length > 160 ? pubText.Substring(0, 160) : pubText);

            // b8f9431c review L2: the two env hooks only count with CA_TEST_HOOKS=1. Without it, discovery reads the
            // REAL record folder (and only a process named Clarion is trusted: the same TestHook gates CA_IDE_IMAGE_NAME).
            Environment.SetEnvironmentVariable("CA_TEST_HOOKS", null);
            try
            {
                string realDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClarionAssistant", "ide-endpoint");
                Ok("L2: without CA_TEST_HOOKS=1, CA_IDE_RECORD_DIR is ignored (the real record folder)",
                    string.Equals(IdeEndpointRecord.DirectoryPath, realDir, StringComparison.OrdinalIgnoreCase), IdeEndpointRecord.DirectoryPath);
            }
            finally { Environment.SetEnvironmentVariable("CA_TEST_HOOKS", "1"); }
            Ok("L2: with CA_TEST_HOOKS=1 it is honoured again", IdeEndpointRecord.DirectoryPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { fail++; Console.WriteLine("  [FAIL] harness threw: " + ex); }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine("EndpointRecordSweep: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
