// Harness for Services\IdeSolutionRecord.cs (ticket 77aceec5): the addin -> standalone handover of
// "which solution the IDE has open now", which lets a plain Chat tab's LSP start on a solution
// opened after the tab was launched.
//
// Compiled against the REAL IdeSolutionRecord.cs by Run-Tests.ps1. The record directory is
// redirected to a temp folder, so nothing touches the developer's real %LOCALAPPDATA%.
//
// What each check stops:
//   round trip          - Publish and Read disagreeing about location or shape (the e2e harness
//                         plants a record by hand, so the shape is asserted here too)
//   closed = removed    - "the IDE has nothing open" reading as whatever was open before
//   dead pid ignored    - a crashed IDE's record handing a stale solution to a new server
//   missing .sln        - a deleted solution being handed to the language server
//   no record           - Read inventing an answer
using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

static class IdeSolutionRecordTest
{
    static int _failures, _assertions;

    static void Check(bool ok, string what)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what); }
        else Console.WriteLine("  ok   " + what);
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-idesln-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string recDir = Path.Combine(root, "records");
        Directory.CreateDirectory(root);
        try
        {
            IdeSolutionRecord.RootOverride = recDir;
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            string sln = Path.Combine(root, "a.sln");
            File.WriteAllText(sln, "");
            string note;

            // -- no record at all
            Check(IdeSolutionRecord.Read(self, out note) == null, "no record -> null (" + note + ")");

            // -- round trip
            IdeSolutionRecord.ResetForTest();
            IdeSolutionRecord.Publish(sln);
            string file = IdeSolutionRecord.PathForPid(self);
            Check(File.Exists(file), "Publish writes " + Path.GetFileName(file));
            Check(string.Equals(IdeSolutionRecord.Read(self, out note), sln, StringComparison.OrdinalIgnoreCase),
                "Read returns the published solution");

            // -- the on-disk shape the e2e harness (LspStart.WorkspacePathTest.ps1) relies on
            var rec = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
            Check(rec.ContainsKey("solution") && rec.ContainsKey("pid") && Convert.ToInt32(rec["pid"]) == self,
                "record carries 'solution' and 'pid' = this process");
            byte[] bytes = File.ReadAllBytes(file);
            Check(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "record has no BOM");

            // -- closed solution removes the record
            IdeSolutionRecord.Publish(null);
            Check(!File.Exists(file), "Publish(null) removes the record");
            Check(IdeSolutionRecord.Read(self, out note) == null, "after close, Read -> null");

            // -- a record naming a .sln that no longer exists
            IdeSolutionRecord.Publish(sln);
            File.Delete(sln);
            Check(IdeSolutionRecord.Read(self, out note) == null, "deleted .sln -> null (" + note + ")");

            // -- a record written by an IDE that is no longer running
            string deadSln = Path.Combine(root, "b.sln");
            File.WriteAllText(deadSln, "");
            int dead = 2147483632;   // not a live pid on any machine this runs on
            File.WriteAllText(IdeSolutionRecord.PathForPid(dead),
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "solution", deadSln }, { "pid", dead } }));
            Check(IdeSolutionRecord.Read(dead, out note) == null, "dead IDE pid -> null (" + note + ")");

            // -- a record under THIS pid's name whose payload names another (live) pid: refused.
            // The other pid is a live process, so only the pid-match rule can reject this one.
            int other = 0;
            try
            {
                foreach (var pr in System.Diagnostics.Process.GetProcesses())
                {
                    using (pr) { if (pr.Id != self && pr.Id > 4 && other == 0) other = pr.Id; }
                }
            }
            catch { }
            File.WriteAllText(sln, "");
            File.WriteAllText(IdeSolutionRecord.PathForPid(self),
                new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "solution", sln }, { "pid", other } }));
            Check(other != 0 && IdeSolutionRecord.Read(self, out note) == null,
                "payload pid != the IDE pid being read -> null (" + note + ")");

            // -- ReadCached follows a change to the file (A -> B -> gone)
            string sln2 = Path.Combine(root, "c.sln");
            File.WriteAllText(sln2, "");
            IdeSolutionRecord.ResetForTest();
            IdeSolutionRecord.Publish(sln);
            Check(string.Equals(IdeSolutionRecord.ReadCached(self, out note), sln, StringComparison.OrdinalIgnoreCase),
                "ReadCached returns A");
            IdeSolutionRecord.Publish(sln2);
            Check(string.Equals(IdeSolutionRecord.ReadCached(self, out note), sln2, StringComparison.OrdinalIgnoreCase),
                "ReadCached sees the change to B at once (mtime/size)");
            IdeSolutionRecord.Publish(null);
            Check(IdeSolutionRecord.ReadCached(self, out note) == null, "ReadCached sees the record removed");

            // -- R4: an UNREADABLE record is transient, not "gone"
            IdeSolutionRecord.ResetForTest();
            IdeSolutionRecord.Publish(sln);
            string rf = IdeSolutionRecord.PathForPid(self);
            Check(string.Equals(IdeSolutionRecord.ReadCached(self, out note), sln, StringComparison.OrdinalIgnoreCase),
                "R4: ReadCached returns A before the lock");
            File.SetLastWriteTimeUtc(rf, DateTime.UtcNow.AddSeconds(5));   // new stamp: forces a re-read
            using (new FileStream(rf, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool transient;
                string locked = IdeSolutionRecord.Read(self, out note, out transient);
                Check(locked == null && transient, "R4: a locked record reads as TRANSIENT (" + note + ")");
                Check(string.Equals(IdeSolutionRecord.ReadCached(self, out note), sln, StringComparison.OrdinalIgnoreCase),
                    "R4: ReadCached keeps the last definite answer while the record is locked");
            }
            File.WriteAllText(rf, "{\"solution\":");                    // half-written copy
            Check(string.Equals(IdeSolutionRecord.ReadCached(self, out note), sln, StringComparison.OrdinalIgnoreCase),
                "R4: a half-written record keeps the last definite answer");
            IdeSolutionRecord.Publish(null);
            Check(IdeSolutionRecord.ReadCached(self, out note) == null, "R4: a record really removed is definite: null");

            // -- publishing a path that does not exist is "nothing open", not a stale write
            IdeSolutionRecord.ResetForTest();
            IdeSolutionRecord.Publish(Path.Combine(root, "missing.sln"));
            Check(!File.Exists(IdeSolutionRecord.PathForPid(self)), "Publish(missing .sln) leaves no record");
        }
        finally
        {
            IdeSolutionRecord.RootOverride = null;
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(_failures == 0
            ? "PASS - " + _assertions + " assertions"
            : "FAIL - " + _failures + " of " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }
}
