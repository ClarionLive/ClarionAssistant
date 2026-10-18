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
        }
        catch (Exception ex) { fail++; Console.WriteLine("  [FAIL] harness threw: " + ex); }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine("EndpointRecordSweep: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
