using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClarionAssistant.Services;

// 1565ef7b: a CA Embeditor save that cannot go through must NEVER lose the developer's text.
//   * EmbedSavePlanner matches Monaco's slots to the CURRENT native slots by read-only skeleton, not by line
//     number: a drift elsewhere re-maps and saves (John's live repro, Case A); a slot changed in both places,
//     or a changed skeleton, refuses.
//   * EmbedLiveSaveFlow plans and writes BEFORE beforeClose (the overlay detach) and never cancels: a refusal or
//     a write failure leaves the editor intact; only a failure after the detach is reported as not intact.
//   * EmbedRecovery writes a BOM-free, uniquely named file atomically (no .tmp left behind).
//   * MergeStash restores per slot.
//
// Run:  tests\Run-Tests.ps1
static class EmbedSaveFlowTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    // A tiny pwee-shaped buffer: read-only generated lines (G*) around three editable slots (S*).
    //   1 G-a   2 S0   3 G-b   4 G-c   5 S1   6 G-d   7 S2   8 S2'   9 G-e
    static string Src(string s0, string s1, string s2, string eol = "\n")
    {
        var lines = new List<string> { "  ! G-a generated" };
        lines.AddRange(s0.Split('\n'));
        lines.Add("  ! G-b generated"); lines.Add("  ! G-c generated");
        lines.AddRange(s1.Split('\n'));
        lines.Add("  ! G-d generated");
        lines.AddRange(s2.Split('\n'));
        lines.Add("  ! G-e generated");
        return string.Join(eol, lines);
    }
    static List<int[]> Ranges(string s0, string s1, string s2)
    {
        int n0 = s0.Split('\n').Length, n1 = s1.Split('\n').Length, n2 = s2.Split('\n').Length;
        int a = 2, b = a + n0 + 2, c = b + n1 + 1;
        return new List<int[]> { new[] { a, a + n0 - 1 }, new[] { b, b + n1 - 1 }, new[] { c, c + n2 - 1 } };
    }

    const string O0 = "  x = 1", O1 = "  y = 2", O2 = "  z = 3\n  z += 1";

    sealed class FakeOps : IEmbedLiveSaveOps
    {
        public readonly List<string> Log = new List<string>();
        public bool Open = true, ReadOk = true, Closed = true;
        public string Source; public List<int[]> RangesNow;
        public int FailWriteAt = -1;
        public string SaveResult = "Embeditor saved and closed.";
        public bool EmbedOpen() { Log.Add("open?"); return Open; }
        public bool ReadNative(out string source, out List<int[]> ranges, out string error)
        {
            Log.Add("read"); source = Source; ranges = RangesNow; error = ReadOk ? null : "boom"; return ReadOk;
        }
        public string WriteSlot(int line, string code) { Log.Add("write:" + line); return line == FailWriteAt ? "Error: write failed" : "ok"; }
        public string SaveAndClose() { Log.Add("save"); return SaveResult; }
        public bool WaitClosed(int ms) { Log.Add("waitclosed"); return Closed; }
    }

    static int Main(string[] args)
    {
        string orig = Src(O0, O1, O2);
        var origR = Ranges(O0, O1, O2);
        var origT = new List<string> { O0, O1, O2 };

        // ---------------- planner ----------------
        Console.WriteLine("planner");
        {
            var cur = new List<string> { "  x = 10", O1, O2 };
            var p = EmbedSavePlanner.Plan(orig, origR, origT, cur, null, orig, origR);
            Ok("unmoved: saves", p.CanSave, p.Refusal);
            Ok("unmoved: one write at the slot's line", p.Writes.Count == 1 && p.Writes[0].Key == 2 && p.Writes[0].Value == "  x = 10");
            Ok("unmoved: not remapped, no foreign", !p.Remapped && p.Foreign == 0 && p.Changed == 1);
        }
        {
            // CASE A (the live repro): something else wrote 3 lines into S1; the developer edited S2 (after it).
            string f1 = "  y = 2\n  y += 1\n  y += 2";
            string fresh = Src(O0, f1, O2);
            var freshR = Ranges(O0, f1, O2);
            var cur = new List<string> { O0, O1, "  z = 30\n  z += 1" };
            var p = EmbedSavePlanner.Plan(orig, origR, origT, cur, null, fresh, freshR);
            Ok("case A drift: saves", p.CanSave, p.Refusal);
            Ok("case A drift: remapped", p.Remapped);
            Ok("case A drift: write lands on S2's NEW line (" + freshR[2][0] + ", was " + origR[2][0] + ")",
                p.Writes.Count == 1 && p.Writes[0].Key == freshR[2][0] && freshR[2][0] == origR[2][0] + 2,
                string.Join(",", p.Writes.Select(w => w.Key)));
            Ok("case A drift: the outside change is counted, not overwritten", p.Foreign == 1 && p.Writes.All(w => w.Key != freshR[1][0]));
        }
        {
            // Drift AFTER the developer's slot: their line is unchanged.
            string f2 = "  z = 3\n  z += 1\n  z += 2";
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { "  x = 9", O1, O2 }, null,
                Src(O0, O1, f2), Ranges(O0, O1, f2));
            Ok("drift below: saves at the original line", p.CanSave && p.Writes.Count == 1 && p.Writes[0].Key == 2, p.Refusal);
        }
        {
            // Same slot changed in both places: refuse, never overwrite.
            string f1 = "  y = 2\n  y += 99";
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { O0, "  y = 5", O2 }, null,
                Src(O0, f1, O2), Ranges(O0, f1, O2));
            Ok("same slot both places: refused", !p.CanSave && p.Refusal.Contains("also changed outside"), p.Refusal);
        }
        {
            // Same slot changed in both places WITHOUT any line shift: still refused (the old code overwrote it).
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { O0, "  y = 5", O2 }, null,
                Src(O0, "  y = 7", O2), origR);
            Ok("same slot both places, ranges unmoved: refused", !p.CanSave, p.Refusal);
        }
        {
            // Slot count changed: ambiguous.
            var r = new List<int[]>(origR); r.RemoveAt(2);
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { "  x = 2", O1, O2 }, null, orig, r);
            Ok("slot count changed: refused", !p.CanSave && p.Refusal.Contains("structure changed"), p.Refusal);
        }
        {
            // Slots moved AND a generated line changed: the skeleton no longer identifies them.
            string f1 = "  y = 2\n  y += 1";
            string fresh = Src(O0, f1, O2).Replace("G-d generated", "G-d REGENERATED");
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { O0, O1, "  z = 0\n  z += 1" }, null,
                fresh, Ranges(O0, f1, O2));
            Ok("skeleton changed: refused", !p.CanSave && p.Refusal.Contains("generated code changed"), p.Refusal);
        }
        {
            // A generated line whose length is equal but content differs is also caught.
            string f1 = "  y = 2\n  y += 1";
            string fresh = Src(O0, f1, O2).Replace("G-b generated", "G-b genXrated");
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { "  x = 0", O1, O2 }, null,
                fresh, Ranges(O0, f1, O2));
            Ok("skeleton same-length change: refused", !p.CanSave, p.Refusal);
        }
        {
            // Retry after a partial write: the slot already holds the developer's text -> no write needed.
            string f2 = "  z = 30\n  z += 1";
            var cur = new List<string> { "  x = 10", O1, f2 };
            var p = EmbedSavePlanner.Plan(orig, origR, origT, cur, null, Src(O0, O1, f2), Ranges(O0, O1, f2));
            Ok("idempotent retry: only the unwritten slot is written", p.CanSave && p.Writes.Count == 1 && p.Writes[0].Key == 2 && p.Changed == 2, p.Refusal);
        }
        {
            // After a Ctrl+F4 sync the developer answered Cancel and kept editing: the native slot holds the
            // synced text, which is ours.
            string synced = "  y = 3\n  y += 1";
            var alt = new List<string> { O0, synced, O2 };
            var cur = new List<string> { O0, "  y = 4", O2 };
            var p = EmbedSavePlanner.Plan(orig, origR, origT, cur, alt, Src(O0, synced, O2), Ranges(O0, synced, O2));
            Ok("synced then edited: saves over our own sync", p.CanSave && p.Writes.Count == 1 && p.Foreign == 0, p.Refusal);
            var p2 = EmbedSavePlanner.Plan(orig, origR, origT, cur, null, Src(O0, synced, O2), Ranges(O0, synced, O2));
            Ok("...and WITHOUT the sync record it is a conflict", !p2.CanSave, p2.Refusal);
            // Developer reverted the slot after the sync: the native buffer must be put back.
            var p3 = EmbedSavePlanner.Plan(orig, origR, origT, origT, alt, Src(O0, synced, O2), Ranges(O0, synced, O2));
            Ok("synced then reverted: writes the original back", p3.CanSave && p3.Writes.Count == 1 && p3.Writes[0].Value == O1, p3.Refusal);
        }
        {
            // The native buffer uses CRLF where the open-time text was LF: lines are the same lines.
            string f1 = "  y = 2\n  y += 1";
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { O0, O1, "  z = 8\n  z += 1" }, null,
                Src(O0, f1, O2, "\r\n"), Ranges(O0, f1, O2));
            Ok("CRLF native vs LF baseline: still matches", p.CanSave && p.Remapped && p.Writes.Count == 1, p.Refusal);
        }
        {
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { O0, O1 }, null, orig, origR);
            Ok("Monaco slot count mismatch: refused", !p.CanSave);
            var p2 = EmbedSavePlanner.Plan(null, origR, origT, new List<string> { "  x = 0", O1, O2 }, null,
                Src(O0, "a\nb", O2), Ranges(O0, "a\nb", O2));
            Ok("moved but no open-time source: refused (can't prove identity)", !p2.CanSave, p2.Refusal);
        }
        {
            // Two writes come out bottom-to-top.
            var cur = new List<string> { "  x = 0", O1, "  z = 0\n  z += 1" };
            var p = EmbedSavePlanner.Plan(orig, origR, origT, cur, null, orig, origR);
            Ok("writes are bottom-to-top", p.Writes.Count == 2 && p.Writes[0].Key > p.Writes[1].Key);
        }

        // ---------------- live save flow ----------------
        Console.WriteLine("live save flow");
        {
            // CASE A end to end.
            string f1 = "  y = 2\n  y += 1\n  y += 2";
            var ops = new FakeOps { Source = Src(O0, f1, O2), RangesNow = Ranges(O0, f1, O2) };
            var cur = new List<string> { O0, O1, "  z = 30\n  z += 1" };
            var o = EmbedLiveSaveFlow.SaveLive(ops, "BrowseDepartment", orig, origR, origT, cur, null,
                () => ops.Log.Add("DETACH"));
            Ok("case A: saved", o.Ok, o.Message);
            string seq = string.Join(" ", ops.Log);
            Ok("case A: write -> detach -> save -> confirm", seq == "open? read write:9 DETACH save waitclosed", seq);
            Ok("case A: says what else got saved (D1)", o.Message.Contains("Also saved 1 slot(s) changed outside the CA Embeditor"), o.Message);
        }
        {
            // THE BUG: a refusal. The surface must not be detached and the embed must not be touched.
            string f1 = "  y = 2\n  y += 99";
            var ops = new FakeOps { Source = Src(O0, f1, O2), RangesNow = Ranges(O0, f1, O2) };
            bool detached = false;
            var o = EmbedLiveSaveFlow.SaveLive(ops, "P", orig, origR, origT, new List<string> { O0, "  y = 5", O2 }, null,
                () => detached = true);
            string seq = string.Join(" ", ops.Log);
            Ok("refusal: not saved", !o.Ok);
            Ok("refusal: editor intact, never detached", o.EditorIntact && !detached);
            Ok("refusal: no write, no save", seq == "open? read", seq);
            Ok("refusal: tells the developer their edits are still there", o.Message.Contains("still in the CA Embeditor"), o.Message);
        }
        {
            var ops = new FakeOps { Source = orig, RangesNow = origR, FailWriteAt = 2 };
            bool detached = false;
            var o = EmbedLiveSaveFlow.SaveLive(ops, "P", orig, origR, origT, new List<string> { "  x = 0", O1, "  z = 0\n  z += 1" },
                null, () => detached = true);
            string seq = string.Join(" ", ops.Log);
            Ok("write failure: editor intact, never detached", !o.Ok && o.EditorIntact && !detached);
            Ok("write failure: stops before save", !seq.Contains("save"), seq);
        }
        {
            var ops = new FakeOps { Source = orig, RangesNow = origR, SaveResult = "Error: TryClose returned false" };
            var o = EmbedLiveSaveFlow.SaveLive(ops, "P", orig, origR, origT, new List<string> { "  x = 0", O1, O2 }, null, () => { });
            Ok("save failure after detach: reported NOT intact (caller writes recovery)", !o.Ok && !o.EditorIntact, o.Message);
            var ops2 = new FakeOps { Source = orig, RangesNow = origR, SaveResult = "Error: x" };
            var o2 = EmbedLiveSaveFlow.SaveLive(ops2, "P", orig, origR, origT, new List<string> { "  x = 0", O1, O2 }, null, null);
            Ok("save failure, tab mode (no beforeClose): still intact", !o2.Ok && o2.EditorIntact);
            var ops3 = new FakeOps { Source = orig, RangesNow = origR, Closed = false };
            var o3 = EmbedLiveSaveFlow.SaveLive(ops3, "P", orig, origR, origT, new List<string> { "  x = 0", O1, O2 }, null, () => { });
            Ok("unconfirmed close: not ok", !o3.Ok && o3.Message.Contains("did not confirm closed"));
        }
        {
            var ops = new FakeOps { Open = false };
            bool detached = false;
            var o = EmbedLiveSaveFlow.SaveLive(ops, "P", orig, origR, origT, origT, null, () => detached = true);
            Ok("embed gone: NotLive, intact, no detach", o.NotLive && o.EditorIntact && !detached && !o.Ok);
            var ops2 = new FakeOps { ReadOk = false };
            var o2 = EmbedLiveSaveFlow.SaveLive(ops2, "P", orig, origR, origT, origT, null, () => detached = true);
            Ok("re-read fails: intact, no detach", !o2.Ok && o2.EditorIntact && !detached);
        }
        {
            var ops = new FakeOps { Source = orig, RangesNow = origR };
            var o = EmbedLiveSaveFlow.SaveLive(ops, "P", orig, origR, origT, new List<string> { "  x = 0", O1, O2 }, null,
                () => { throw new InvalidOperationException("detach blew up"); });
            Ok("a throwing detach does not abandon a save already written", o.Ok && ops.Log.Contains("save"), o.Message);
        }

        // ---------------- pipeline Run 1 fixes ----------------
        Console.WriteLine("pipeline run 1");
        {
            // Adversary: equal coordinates are not proof of identity — a changed generated line still refuses.
            string fresh = orig.Replace("G-d generated", "G-d REGENERATED");
            var p = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { "  x = 4", O1, O2 }, null, fresh, origR);
            Ok("unmoved ranges but changed skeleton: refused", !p.CanSave && p.Refusal.Contains("generated code changed"), p.Refusal);
            var p2 = EmbedSavePlanner.Plan(null, origR, origT, new List<string> { "  x = 4", O1, O2 }, null, fresh, origR);
            Ok("unmoved ranges, no open-time source: the old line-number rule still saves", p2.CanSave, p2.Refusal);
        }
        {
            // Debugger: a save that fails partway, then more typing, must not lock the developer out.
            var cur1 = new List<string> { "  x = 10", O1, "  z = 30\n  z += 1" };
            var ops = new FakeOps { Source = orig, RangesNow = origR, FailWriteAt = 2 };   // S2 (line 7) writes, S0 (line 2) fails
            var o = EmbedLiveSaveFlow.SaveLive(ops, "P", orig, origR, origT, cur1, null, () => { });
            Ok("partial write: reports the slot that DID get written", !o.Ok && o.EditorIntact && o.WrittenSlots.SequenceEqual(new[] { 2 }),
                string.Join(",", o.WrittenSlots));
            // The native buffer now holds cur1[2] in S2; the developer then edits S2 again.
            string nat = Src(O0, O1, cur1[2]);
            var cur2 = new List<string> { "  x = 10", O1, "  z = 31\n  z += 1" };
            var stale = EmbedSavePlanner.Plan(orig, origR, origT, cur2, null, nat, origR);
            Ok("...without the record the retry is locked out (the bug)", !stale.CanSave, stale.Refusal);
            var alt = EmbedSavePlanner.RecordWrites(null, origT, cur1, o.WrittenSlots);
            var p = EmbedSavePlanner.Plan(orig, origR, origT, cur2, alt, nat, origR);
            Ok("...with the record the retry saves both slots", p.CanSave && p.Writes.Count == 2, p.Refusal);
            Ok("RecordWrites keeps earlier records and baseline elsewhere", alt[0] == O0 && alt[1] == O1 && alt[2] == cur1[2]);
        }
        {
            var plan = EmbedSavePlanner.Plan(orig, origR, origT, new List<string> { "  x = 1!", O1, "  z = 0\n  z += 1" }, null, orig, origR);
            var written = new List<int>();
            string err = EmbedSavePlanner.WriteAll(plan, (line, code) => { if (line == 2) throw new InvalidOperationException("pwee"); return "ok"; }, written);
            Ok("WriteAll: a throwing write stops, names the line, keeps what was written",
                err != null && err.Contains("line 2") && err.Contains("pwee") && written.SequenceEqual(new[] { 2 }), err);
        }
        {
            var gate = new EmbedSaveGate();
            var t0 = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            int a = gate.TryEnter(t0);
            Ok("gate: first save gets a token", a > 0);
            Ok("gate: a second save while one is queued does NOT enter", gate.TryEnter(t0.AddSeconds(2)) == 0 && gate.Busy(t0.AddSeconds(2)));
            Ok("gate: the queued save starts", gate.Start(a));
            // Pipeline Run 2 (both Codex gates): a RUNNING save never expires, however long the IDE stalls in it.
            var late = t0 + EmbedSaveGate.StaleAfter + TimeSpan.FromMinutes(30);
            Ok("gate: a RUNNING save is never stale", gate.TryEnter(late) == 0 && gate.Busy(late));
            gate.Exit(a);
            int b = gate.TryEnter(late);
            Ok("gate: free again after the owner exits", b > 0);
            Ok("gate: an old token's Exit does not release the new owner", ((Func<bool>)(() => { gate.Exit(a); return gate.Busy(late); }))());
            // A queued callback that was dropped (destroyed handle) goes stale; its token can no longer start.
            var later = late + EmbedSaveGate.StaleAfter;
            int c = gate.TryEnter(later);
            Ok("gate: a never-started QUEUED entry goes stale after StaleAfter", c > 0);
            Ok("gate: the superseded token can't start (no overlap)", !gate.Start(b) && gate.Start(c));
            gate.Exit(c);
        }
        {
            // Charlie's scope cut: a second save while one runs is REFUSED (its own answer), never queued or merged.
            Ok("busy refusal message says to try again", EmbedSaveGate.BusyMessage.Contains("already in progress") &&
                EmbedSaveGate.BusyMessage.Contains("try again"));
            // SameSlots decides whether text typed during a save-and-exit must be kept (tab stays open; recovery copy).
            var saved = new List<string> { "  x = 1", O1, O2 };
            Ok("typed-during-save: identical text is not 'newer'", EmbedSavePlanner.SameSlots(saved, new List<string> { "  x = 1", O1, O2 }));
            Ok("typed-during-save: CRLF-only difference is not 'newer'", EmbedSavePlanner.SameSlots(saved, new List<string> { "  x = 1", O1, O2.Replace("\n", "\r\n") }));
            Ok("typed-during-save: a changed slot IS newer", !EmbedSavePlanner.SameSlots(saved, new List<string> { "  x = 2", O1, O2 }));
            Ok("typed-during-save: a different slot count IS newer", !EmbedSavePlanner.SameSlots(saved, new List<string> { "  x = 1", O1 }));
        }

        // ---------------- MergeStash ----------------
        Console.WriteLine("stash restore");
        {
            var edited = new List<string> { "  x = 5", O1, O2 };
            Ok("unchanged baseline: restores", Same(EmbedSavePlanner.MergeStash(origT, edited, origT), edited));
            var fresh = new List<string> { O0, "  y = 2 ! saved elsewhere", O2 };
            var m = EmbedSavePlanner.MergeStash(origT, edited, fresh);
            Ok("an UNEDITED slot changed elsewhere: restores, keeping the other change",
                m != null && m[0] == "  x = 5" && m[1] == "  y = 2 ! saved elsewhere");
            var fresh2 = new List<string> { "  x = 1 ! saved elsewhere", O1, O2 };
            Ok("the EDITED slot's baseline changed: refused", EmbedSavePlanner.MergeStash(origT, edited, fresh2) == null);
            Ok("count changed: refused", EmbedSavePlanner.MergeStash(origT, edited, new List<string> { O0, O1 }) == null);
        }

        // ---------------- recovery file ----------------
        Console.WriteLine("recovery file");
        string dir = Path.Combine(Path.GetTempPath(), "ca-embed-recovery-test-" + Guid.NewGuid().ToString("N"));
        EmbedRecovery.FolderOverride = dir;
        try
        {
            var edited = new List<string> { "  x = 5 ! JOHN-73BD-A", O1, O2 };
            Ok("nothing changed: no file", EmbedRecovery.Write("P", "test", origR, origT, origT) == null && !Directory.Exists(dir));
            string a = EmbedRecovery.Write("BrowseDepartment", "test", origR, origT, edited);
            string b = EmbedRecovery.Write("BrowseDepartment", "test", origR, origT, edited);
            Ok("written under the recovery folder", a != null && Path.GetDirectoryName(a) == dir, a);
            Ok("same-second writes never overwrite each other", a != b && File.Exists(a) && File.Exists(b), a + " | " + b);
            var bytes = File.ReadAllBytes(a);
            Ok("no BOM", !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
            string text = File.ReadAllText(a);
            Ok("holds the edited slot, not the untouched ones", text.Contains("JOHN-73BD-A") && !text.Contains("y = 2"), text);
            Ok("names the procedure", text.Contains("'BrowseDepartment'"));
            Ok("no temp file left behind", Directory.GetFiles(dir, "*.tmp").Length == 0);
        }
        finally
        {
            EmbedRecovery.FolderOverride = null;
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static bool Same(List<string> a, List<string> b) { return a != null && b != null && a.SequenceEqual(b); }
}
