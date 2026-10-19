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
//   * GH #179: EmbedSaveJournal keeps a save's text through ok=True (the Generator can fault after it), and the
//     next open decides from the reloaded slots: persisted -> retired, back at the pre-save text -> offered,
//     changed since -> archived. Torn files are never offered.
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

        // ---------------- pending save journal (GH #179) ----------------
        Console.WriteLine("pending save journal");
        string jdir = Path.Combine(Path.GetTempPath(), "ca-embed-journal-test-" + Guid.NewGuid().ToString("N"));
        EmbedRecovery.FolderOverride = jdir;
        EmbedSaveJournal.FolderOverride = Path.Combine(jdir, "pending");
        try
        {
            const string App = @"C:\Apps\Invoice.app";
            string big = new string('x', 330000);   // the reporter's embed size, in one slot
            var saved = new List<string> { "  x = 5 ! JOHN-179-A", O1, big };
            string arch;

            Ok("nothing changed: no journal", EmbedSaveJournal.Begin(App, "P", origT, origT, out arch) == null &&
                !File.Exists(EmbedSaveJournal.PathFor(App, "P")));

            // A save begins: the journal is written BEFORE Clarion gets the text.
            string jp = EmbedSaveJournal.Begin(App, "BrowseDepartment", origT, saved, out arch);
            Ok("written before the save, per app + procedure", jp != null && File.Exists(jp) && jp == EmbedSaveJournal.PathFor(App, "BrowseDepartment"), jp);
            Ok("no temp file left behind", Directory.GetFiles(EmbedSaveJournal.Folder, "*.tmp").Length == 0);
            var jb = File.ReadAllBytes(jp);
            Ok("no BOM", !(jb.Length >= 3 && jb[0] == 0xEF && jb[1] == 0xBB && jb[2] == 0xBF));
            Ok("another app's same-named procedure has its own journal", EmbedSaveJournal.PathFor(@"C:\Apps\Other.app", "BrowseDepartment") != jp);

            // SaveLive ok=True, refreshPadSources DONE ... and then the process dies: NOTHING on that path retires
            // it (the view's success path is pinned by EmbedSaveOrder.SourceScan). The file is all that is left.
            var e = EmbedSaveJournal.Read(App, "BrowseDepartment");
            Ok("kept through ok=True: readable after the save", e != null && e.Index.Count == 2 && e.SlotCount == 3);
            Ok("...holds only the changed slots, whole (330k chars round-trip)",
                e != null && e.Index.SequenceEqual(new[] { 0, 2 }) && e.Saved[1] == big && e.Baseline[0] == O0);

            // Next start, next open: the Generator died, so the embed is back at its pre-save text.
            Ok("crash: fresh == pre-save text -> Lost (offered)", EmbedSaveJournal.Assess(e, origT) == EmbedSaveJournal.Verdict.Lost);
            var offer = EmbedSaveJournal.RestoredSlots(e, origT);
            Ok("crash: the offer is the saved text", offer != null && offer.SequenceEqual(saved));

            // A normal save: the reopened embed holds what was saved -> retired silently, no offer.
            Ok("normal save: fresh == saved -> Persisted", EmbedSaveJournal.Assess(e, saved) == EmbedSaveJournal.Verdict.Persisted);
            var normalised = new List<string> { "  x = 5 ! JOHN-179-A   \r\n", O1, big + "\r\n" };
            Ok("normal save, Clarion trimmed/re-ended lines -> still Persisted (no false offer)",
                EmbedSaveJournal.Assess(e, normalised) == EmbedSaveJournal.Verdict.Persisted);
            Ok("changed since (edited natively) -> Diverged, not offered",
                EmbedSaveJournal.Assess(e, new List<string> { "  x = 99", O1, O2 }) == EmbedSaveJournal.Verdict.Diverged);
            Ok("slot count changed -> Diverged", EmbedSaveJournal.Assess(e, new List<string> { O0, O1 }) == EmbedSaveJournal.Verdict.Diverged);

            // Pipeline run 1, adversary: a same-session reopen showing the saved text proves only the in-memory .app.
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            Ok("written with this process's pid", e.Pid == me);
            Ok("same session, reopen shows the saved text -> Keep (not retired, no offer)",
                EmbedSaveJournal.Decide(e, saved, me) == EmbedSaveJournal.OpenDecision.Keep);
            Ok("a later session reads the saved text back -> Retire", EmbedSaveJournal.Decide(e, saved, me + 1) == EmbedSaveJournal.OpenDecision.Retire);
            Ok("not landed -> Offer (either session)", EmbedSaveJournal.Decide(e, origT, me) == EmbedSaveJournal.OpenDecision.Offer &&
                EmbedSaveJournal.Decide(e, origT, me + 1) == EmbedSaveJournal.OpenDecision.Offer);
            // Live test 2026-10-19 (PM ruling): only THIS session's changed-since entry is archived silently; an
            // earlier session's is offered (the app may have been reverted past the save).
            Ok("changed since, same session -> Archive", EmbedSaveJournal.Decide(e, new List<string> { "  x = 99", O1, O2 }, me) == EmbedSaveJournal.OpenDecision.Archive);
            Ok("changed since, earlier session -> Offer", EmbedSaveJournal.Decide(e, new List<string> { "  x = 99", O1, O2 }, me + 1) == EmbedSaveJournal.OpenDecision.Offer);

            // PM ruling (pipeline run 2): the #179 reporter's sequence. Journal at T, the crashed process lingers and
            // writes the .app at T+12 min WITHOUT the embed, the developer reopens in a new session: OFFERED, and the
            // wording names the later write. A deliberate revert looks identical and is offered too (accepted).
            DateTime appT12 = e.Written.AddMinutes(12);
            Ok("reporter's sequence: .app written 12 min after the save, new session -> Offer",
                EmbedSaveJournal.Decide(e, origT, me + 1) == EmbedSaveJournal.OpenDecision.Offer && EmbedSaveJournal.AppWrittenSince(e, appT12));
            string m12 = EmbedSaveJournal.OfferMessage(e, appT12);
            Ok("...worded with the .app's last write and both causes", m12.Contains("The app was last written on " +
                appT12.ToString("yyyy-MM-dd HH:mm") + " without it") && m12.Contains("Clarion may have stopped before saving it") &&
                m12.Contains("changed or reverted since") && m12.Contains("or another IDE has this app open") &&
                m12.EndsWith("Restore it as unsaved edits?"), m12);
            string mOld = EmbedSaveJournal.OfferMessage(e, e.Written.AddMinutes(-10));
            Ok(".app older than the save -> the crash/closed-unsaved wording", !EmbedSaveJournal.AppWrittenSince(e, e.Written.AddMinutes(-10)) &&
                mOld.Contains("Clarion stopped before it was saved, or the app was closed without saving") && !mOld.Contains("last written"), mOld);
            Ok("unknown .app time -> the crash/closed-unsaved wording", !EmbedSaveJournal.OfferMessage(e, null).Contains("last written"));

            // Pipeline run 2, debugger: the count-changed fallback needs a unique, non-empty match.
            var emptied = EmbedSaveJournal.Parse(EmbedSaveJournal.Serialize(e));
            emptied.Saved[0] = "";   // the edit deleted slot 0's code
            Ok("count changed, the save emptied a slot -> Diverged (an empty slot elsewhere is no proof)",
                EmbedSaveJournal.Assess(emptied, new List<string> { O0, "", "", O1, big }) == EmbedSaveJournal.Verdict.Diverged);
            var common = EmbedSaveJournal.Parse(EmbedSaveJournal.Serialize(e));
            common.Saved[0] = "  RETURN";
            Ok("count changed, the saved one-liner appears in two slots -> Diverged",
                EmbedSaveJournal.Assess(common, new List<string> { "  RETURN", O1, "  RETURN", "", big }) == EmbedSaveJournal.Verdict.Diverged);
            Ok("count changed, unique non-empty matches -> Persisted",
                EmbedSaveJournal.Assess(common, new List<string> { "  RETURN", O1, "", big }) == EmbedSaveJournal.Verdict.Persisted);

            // Pipeline run 1, debugger: a control added in the designer adds embed points, shifting every slot.
            var shifted = new List<string> { saved[0], "", "  ! new control's embed", O1, big };
            Ok("slot count changed after a landed save -> Persisted (no false alert)",
                EmbedSaveJournal.Assess(e, shifted) == EmbedSaveJournal.Verdict.Persisted);
            Ok("...and a later session retires it", EmbedSaveJournal.Decide(e, shifted, me + 1) == EmbedSaveJournal.OpenDecision.Retire);
            var shiftedLost = new List<string> { O0, "", "  ! new control's embed", O1, O2 };
            Ok("slot count changed and the save is missing -> Diverged (archived, not offered by position)",
                EmbedSaveJournal.Assess(e, shiftedLost) == EmbedSaveJournal.Verdict.Diverged);

            // ---- live test 2026-10-19, Clarion 12, demoleg.app tes:TestProcedure (120 slots, slot 7 edited) ----
            {
                const string Saved1 = "! #179 recovery test  ";                       // typed at column 1, saved
                const string Recovered = "  ! #179 recovery test  ";                   // what Clarion's RECOVER reloaded
                const string Saved2 = "  ! #179 recovery test  \n  ! #179 recovery test TWO  ";
                var empty120 = Enumerable.Repeat("", 120).ToList();
                var withSlot = (Func<string, List<string>>)(t => { var l = new List<string>(empty120); l[7] = t; return l; });
                DateTime app0950 = new DateTime(2026, 10, 19, 9, 50, 58);

                // (a) Save, Clarion killed, reopen, RECOVER: the text IS there, re-indented by two spaces.
                var ea = new EmbedSaveJournal.Entry { App = App, Proc = "tes:TestProcedure", Written = new DateTime(2026, 10, 19, 10, 39, 18), Pid = me + 7, SlotCount = 120 };
                ea.Index.Add(7); ea.Baseline.Add(""); ea.Saved.Add(Saved1);
                Ok("(a) Recover: the saved line re-indented by Clarion -> Persisted",
                    EmbedSaveJournal.Assess(ea, withSlot(Recovered)) == EmbedSaveJournal.Verdict.Persisted);
                Ok("(a) ...a later session retires it (no silent archive, no offer)",
                    EmbedSaveJournal.Decide(ea, withSlot(Recovered), me) == EmbedSaveJournal.OpenDecision.Retire);
                Ok("(a) ...and CRLF + a trailing blank line on top of the indent still count as landed",
                    EmbedSaveJournal.Assess(ea, withSlot("\t" + Saved1.TrimEnd() + "\r\n\r\n")) == EmbedSaveJournal.Verdict.Persisted);
                Ok("(a) guard: a re-indented DIFFERENT text is not 'landed'",
                    EmbedSaveJournal.Assess(ea, withSlot("  ! #179 recovery test X")) != EmbedSaveJournal.Verdict.Persisted);
                var indentOnly = new EmbedSaveJournal.Entry { App = App, Proc = "P", Written = ea.Written, Pid = me + 7, SlotCount = 120 };
                indentOnly.Index.Add(7); indentOnly.Baseline.Add("x = 1"); indentOnly.Saved.Add("  x = 1");
                Ok("(a) guard: an indentation-only edit seen re-indented is NOT called landed (it matches both texts)",
                    EmbedSaveJournal.Assess(indentOnly, withSlot("    x = 1")) == EmbedSaveJournal.Verdict.Diverged);
                Ok("(a) guard: ...but seen exactly as saved it is", EmbedSaveJournal.Assess(indentOnly, withSlot("  x = 1")) == EmbedSaveJournal.Verdict.Persisted);

                // (b) Same steps, LAST SAVED: the embed comes back EMPTY, older than the save's baseline (the text
                // (a) recovered). The entry is from an earlier session -> OFFERED, never archived silently.
                var eb = new EmbedSaveJournal.Entry { App = App, Proc = "tes:TestProcedure", Written = new DateTime(2026, 10, 19, 10, 45, 17), Pid = me + 9, SlotCount = 120 };
                eb.Index.Add(7); eb.Baseline.Add(Recovered); eb.Saved.Add(Saved2);
                var lastSaved = withSlot("");
                Ok("(b) Last Saved: neither the saved nor the pre-save text -> Diverged",
                    EmbedSaveJournal.Assess(eb, lastSaved) == EmbedSaveJournal.Verdict.Diverged);
                Ok("(b) ...from an earlier session -> Offer (the #179 silent-loss case)",
                    EmbedSaveJournal.Decide(eb, lastSaved, me) == EmbedSaveJournal.OpenDecision.Offer);
                Ok("(b) ...same pid but written before this process started (recycled pid) -> still Offer",
                    EmbedSaveJournal.Decide(eb, lastSaved, me + 9, eb.Written.AddMinutes(5)) == EmbedSaveJournal.OpenDecision.Offer);
                Ok("(b) same session (the native-edit noise case) -> Archive silently",
                    EmbedSaveJournal.Decide(eb, lastSaved, me + 9) == EmbedSaveJournal.OpenDecision.Archive &&
                    EmbedSaveJournal.Decide(eb, lastSaved, me + 9, eb.Written.AddMinutes(-5)) == EmbedSaveJournal.OpenDecision.Archive);
                var pb = EmbedSaveJournal.PlanRestore(eb, lastSaved);
                Ok("(b) Restore puts the saved slot back as unsaved edits, nothing else",
                    pb != null && pb.Placed == 1 && pb.Unplaced.Count == 0 && pb.Slots[7] == Saved2 &&
                    pb.Slots.Where((t, i) => i != 7).All(t => t == ""));
                string mb = EmbedSaveJournal.OfferMessage(eb, app0950, lastSaved, pb);
                Ok("(b) the PM's wording", mb == "Your CA Embeditor save of tes:TestProcedure on 2026-10-19 10:45 is not in this procedure: " +
                    "it may have been lost when Clarion stopped, or the app was reverted since. Restore it as unsaved edits?", mb);

                // The reporter's sequence: the .app written AFTER the save, without the embed -> offered, later-write wording.
                DateTime after = eb.Written.AddMinutes(12);
                Ok("reporter: .app written after the save, embed back at its pre-save text -> Offer",
                    EmbedSaveJournal.Decide(eb, withSlot(Recovered), me) == EmbedSaveJournal.OpenDecision.Offer);
                Ok("reporter: ...and the same when it reads as Diverged -> Offer",
                    EmbedSaveJournal.Decide(eb, lastSaved, me) == EmbedSaveJournal.OpenDecision.Offer);
                string mr = EmbedSaveJournal.OfferMessage(eb, after, lastSaved, pb);
                Ok("reporter: worded with the .app's later write", mr.Contains("The app was last written on " + after.ToString("yyyy-MM-dd HH:mm") +
                    " without it") && mr.EndsWith("Restore it as unsaved edits?"), mr);
            }
            {
                // Slot count changed since the save: place a slot only where it is identified unambiguously.
                var e2 = EmbedSaveJournal.Parse(EmbedSaveJournal.Serialize(e));   // slots 0 and 2 of 3 changed
                var grown = new List<string> { O0, "", "  ! new control's embed", O1, O2 };
                var pg = EmbedSaveJournal.PlanRestore(e2, grown);
                Ok("count changed: each slot placed where its pre-save text uniquely is",
                    pg.Placed == 2 && pg.Unplaced.Count == 0 && pg.Slots[0] == saved[0] && pg.Slots[4] == big &&
                    pg.Slots[1] == "" && pg.Slots[2] == "  ! new control's embed" && pg.Slots[3] == O1);
                Ok("count changed, earlier session, save missing -> Offer", EmbedSaveJournal.Decide(e2, grown, me + 1) == EmbedSaveJournal.OpenDecision.Offer);
                var halfThere = new List<string> { saved[0], "", O1, "  ! new", O2 };
                var ph = EmbedSaveJournal.PlanRestore(e2, halfThere);
                Ok("count changed: a slot whose saved text is already there is left alone",
                    ph.Placed == 1 && ph.Unplaced.Count == 0 && ph.Slots[0] == saved[0] && ph.Slots[4] == big);

                // Ambiguous: slot 0's pre-save text is EMPTY (many empty slots), slot 2's appears twice.
                var amb = new EmbedSaveJournal.Entry { App = App, Proc = "AmbProc", Written = DateTime.Now, Pid = me + 1, SlotCount = 3 };
                amb.Index.Add(0); amb.Baseline.Add(""); amb.Saved.Add("  ! AMB-NEW-0");
                amb.Index.Add(2); amb.Baseline.Add("  RETURN"); amb.Saved.Add("  ! AMB-NEW-2");
                var ambFresh = new List<string> { "", "  RETURN", "", "  RETURN" };
                var pa = EmbedSaveJournal.PlanRestore(amb, ambFresh);
                Ok("ambiguous slots are NOT placed (no guess), and are listed",
                    pa.Placed == 0 && pa.Unplaced.SequenceEqual(new[] { 0, 2 }) && pa.Slots.SequenceEqual(ambFresh));
                var oneEmpty = EmbedSaveJournal.PlanRestore(amb, new List<string> { "", "  x", "  y", "  z" });
                Ok("an EMPTY pre-save text is never an identity, even when only one fresh slot is empty",
                    oneEmpty.Placed == 0 && oneEmpty.Unplaced.Contains(0) && oneEmpty.Slots[0] == "");
                amb.Index.Add(1); amb.Baseline.Add(O1); amb.Saved.Add("  ! AMB-NEW-1");
                var pm = EmbedSaveJournal.PlanRestore(amb, new List<string> { "", O1, "", "  RETURN", "  RETURN" });
                Ok("mixed: the identifiable slot is placed, the ambiguous ones listed",
                    pm.Placed == 1 && pm.Slots[1] == "  ! AMB-NEW-1" && pm.Unplaced.SequenceEqual(new[] { 0, 2 }));
                Begin179(App, "AmbProc", amb);
                var ambRead = EmbedSaveJournal.Read(App, "AmbProc");
                string copy = EmbedSaveJournal.WriteCopy(ambRead, "test: not placeable");
                Ok("the copy holds the unplaced text and the journal stays until the developer answers",
                    copy != null && File.ReadAllText(copy).Contains("AMB-NEW-0") && File.ReadAllText(copy).Contains("AMB-NEW-2") &&
                    EmbedSaveJournal.Read(App, "AmbProc") != null);
                string ma = EmbedSaveJournal.OfferMessage(ambRead, null, new List<string> { "", O1, "", "  RETURN", "  RETURN" }, pm, copy);
                Ok("the offer names the copy file for the slots it cannot place",
                    ma.Contains("2 of its 3 changed embed(s) cannot be put back") && ma.Contains(copy) && ma.EndsWith("Restore it as unsaved edits?"), ma);
                string mn = EmbedSaveJournal.NotPlaceableMessage(ambRead, copy);
                Ok("nothing placeable: the toast names the copy file", mn.Contains(copy) && mn.Contains("cannot be put back automatically"), mn);
                EmbedSaveJournal.Retire(App, "AmbProc");
                File.Delete(copy);   // the archive checks below count *.txt in this folder
            }

            // Pipeline run 1, debugger: an unknown app is never guessed.
            Ok("no app: nothing journaled", EmbedSaveJournal.Begin(null, "NoApp", origT, saved, out arch) == null &&
                EmbedSaveJournal.Begin("  ", "NoApp", origT, saved, out arch) == null &&
                Directory.GetFiles(EmbedSaveJournal.Folder, "NoApp*").Length == 0);
            Ok("no app: nothing read", EmbedSaveJournal.Read(null, "BrowseDepartment") == null);

            Ok("retire removes it", EmbedSaveJournal.Retire(App, "BrowseDepartment") && !File.Exists(jp) &&
                EmbedSaveJournal.Read(App, "BrowseDepartment") == null);

            // A newer save over a persisted entry replaces it quietly; over an unconfirmed one it archives it first.
            EmbedSaveJournal.Begin(App, "P2", origT, saved, out arch);
            EmbedSaveJournal.Begin(App, "P2", saved, new List<string> { "  x = 6", O1, big }, out arch);
            Ok("re-save over a persisted entry: no archive", arch == null && !Directory.GetFiles(jdir, "*.txt").Any());
            EmbedSaveJournal.Begin(App, "P3", origT, saved, out arch);              // lost, offered, left alone ...
            EmbedSaveJournal.Begin(App, "P3", origT, new List<string> { "  x = 7", O1, O2 }, out arch);   // ... then a new save
            Ok("re-save over an unconfirmed entry: archived to a dated recovery file first",
                arch != null && File.Exists(arch) && File.ReadAllText(arch).Contains("JOHN-179-A") &&
                File.ReadAllText(arch).Contains("could not be confirmed"), arch);
            var e3 = EmbedSaveJournal.Read(App, "P3");
            Ok("...and the journal now holds the newer save", e3 != null && e3.Saved[0] == "  x = 7");
            string a3 = EmbedSaveJournal.Archive(e3, "test");
            Ok("Archive keeps a readable copy and retires the journal", a3 != null && File.Exists(a3) && EmbedSaveJournal.Read(App, "P3") == null);

            // Pipeline run 2: entries nobody reopened for 30 days are archived unoffered by the session sweep.
            EmbedSaveJournal.Begin(App, "OldProc", origT, saved, out arch);
            var old = EmbedSaveJournal.Read(App, "OldProc");
            old.Written = DateTime.Now.AddDays(-40);
            File.WriteAllText(EmbedSaveJournal.PathFor(App, "OldProc"), EmbedSaveJournal.Serialize(old));
            EmbedSaveJournal.Begin(App, "NewProc", origT, saved, out arch);
            var young = EmbedSaveJournal.Read(App, "NewProc");
            young.Written = DateTime.Now.AddDays(-29);
            File.WriteAllText(EmbedSaveJournal.PathFor(App, "NewProc"), EmbedSaveJournal.Serialize(young));
            File.WriteAllText(Path.Combine(EmbedSaveJournal.Folder, "Broken-x.pending"), "CA-EMBED-PENDING-SAVE 1\napp 3:");
            var swept = EmbedSaveJournal.SweepStale(DateTime.Now);
            Ok("sweep: the 40-day-old entry is archived, the 29-day-old one is kept",
                swept.Count == 1 && File.Exists(swept[0]) && File.ReadAllText(swept[0]).Contains("more than 30 days old") &&
                EmbedSaveJournal.Read(App, "OldProc") == null && EmbedSaveJournal.Read(App, "NewProc") != null,
                string.Join(" | ", swept));
            Ok("sweep: a damaged file is left alone (it is never offered anyway)",
                File.Exists(Path.Combine(EmbedSaveJournal.Folder, "Broken-x.pending")));

            // Pipeline run 3: dated recovery copies older than 30 days are deleted; pending journals never are.
            string oldCopy = Path.Combine(jdir, "OldCopy-20260101-000000.txt");
            string newCopy = Path.Combine(jdir, "NewCopy-20261001-000000.txt");
            string oldTxtx = Path.Combine(jdir, "Other.txtx");
            File.WriteAllText(oldCopy, "old"); File.SetLastWriteTime(oldCopy, DateTime.Now.AddDays(-31));
            File.WriteAllText(newCopy, "new"); File.SetLastWriteTime(newCopy, DateTime.Now.AddDays(-29));
            File.WriteAllText(oldTxtx, "x"); File.SetLastWriteTime(oldTxtx, DateTime.Now.AddDays(-90));
            string pendOld = EmbedSaveJournal.PathFor(App, "NewProc");
            File.SetLastWriteTime(pendOld, DateTime.Now.AddDays(-90));
            string inPending = Path.Combine(EmbedSaveJournal.Folder, "stray.txt");   // pins TopDirectoryOnly on its own
            File.WriteAllText(inPending, "x"); File.SetLastWriteTime(inPending, DateTime.Now.AddDays(-90));
            int pruned = EmbedRecovery.PruneOlderThan(EmbedSaveJournal.MaxAge, DateTime.Now);
            Ok("prune: the 31-day-old copy is deleted, the 29-day-old one kept", pruned == 1 && !File.Exists(oldCopy) && File.Exists(newCopy),
                "pruned=" + pruned);
            Ok("prune: a pending journal file is never touched, however old", File.Exists(pendOld));
            Ok("prune: never descends into the pending folder", File.Exists(inPending));
            Ok("prune: only *.txt recovery copies (not other extensions)", File.Exists(oldTxtx));

            // Torn / damaged files are never offered.
            string full = EmbedSaveJournal.Serialize(EmbedSaveJournal.Read(App, "P2") ?? new EmbedSaveJournal.Entry());
            Ok("serialized form parses back", EmbedSaveJournal.Parse(full) != null);
            bool anyTornParsed = false;
            foreach (int cut in new[] { 10, full.Length / 3, full.Length / 2, full.Length - 5, full.Length - 1 })
                if (EmbedSaveJournal.Parse(full.Substring(0, cut)) != null) anyTornParsed = true;
            Ok("a file torn at any point is rejected", !anyTornParsed);
            Ok("no END trailer: rejected", EmbedSaveJournal.Parse(full.Replace("END\n", "")) == null);
            Ok("trailing junk after END: rejected", EmbedSaveJournal.Parse(full + "x") == null);
            string p4 = EmbedSaveJournal.PathFor(App, "P4");
            File.WriteAllText(p4, full.Substring(0, full.Length / 2));
            Ok("a torn file on disk reads as no journal", EmbedSaveJournal.Read(App, "P4") == null);
            File.WriteAllText(p4, full);   // P2's entry under P4's name
            Ok("an entry for another procedure under this name is not trusted", EmbedSaveJournal.Read(App, "P4") == null);
        }
        finally
        {
            EmbedRecovery.FolderOverride = null;
            EmbedSaveJournal.FolderOverride = null;
            try { Directory.Delete(jdir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static bool Same(List<string> a, List<string> b) { return a != null && b != null && a.SequenceEqual(b); }

    // Put an in-memory entry on disk under its app + procedure (the tests build entries by hand).
    static void Begin179(string app, string proc, EmbedSaveJournal.Entry e)
    {
        Directory.CreateDirectory(EmbedSaveJournal.Folder);
        File.WriteAllText(EmbedSaveJournal.PathFor(app, proc), EmbedSaveJournal.Serialize(e));
    }
}
