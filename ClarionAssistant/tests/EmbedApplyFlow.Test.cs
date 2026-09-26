using System;
using System.Collections.Generic;
using System.Linq;
using ClarionAssistant.Services;

// PR #198 pipeline round: apply_embed_edits' write -> commit -> save half (EmbedApplyFlow), with the IDE
// operations faked. Guards two review findings:
//   * a call McpDispatcher ABANDONED on timeout must not save afterwards - it rolls back (Discard) instead;
//     one already committed to saving is allowed to finish (and the dispatcher reports "may still complete");
//   * a failed save / unconfirmed close after we wrote must Discard, adopted editor or not, so no dirty
//     half-written tab is left behind.
//
// Run:  tests\Run-Tests.ps1
static class EmbedApplyFlowTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    sealed class FakeOps : IEmbedApplyOps
    {
        public readonly List<string> Log = new List<string>();
        public string SaveResult = "Embeditor saved and closed (Strategy A).";
        public bool Closed = true;
        public int FailWriteAt = -1;
        public Action OnLastWrite;          // e.g. the dispatcher abandoning mid-call
        public Action OnSave;
        public int WritesLeft;

        public string WriteSlot(int line, string code)
        {
            Log.Add("write:" + line);
            if (--WritesLeft == 0 && OnLastWrite != null) OnLastWrite();
            return line == FailWriteAt ? "Error: slot write failed" : "ok";
        }
        public string SaveAndClose() { Log.Add("save"); if (OnSave != null) OnSave(); return SaveResult; }
        public bool WaitClosed(int timeoutMs) { Log.Add("waitclosed"); return Closed; }
        public string DiscardError;         // non-null = CancelEmbeditor failed
        public bool DiscardThrows;
        public string Discard()
        {
            Log.Add("discard");
            if (DiscardThrows) throw new InvalidOperationException("cancel blew up");
            return DiscardError;
        }
        public bool Has(string op) { return Log.Contains(op); }
        public override string ToString() { return string.Join(",", Log); }
    }

    static readonly List<int[]> Ranges = new List<int[]> { new[] { 10, 12 }, new[] { 40, 41 }, new[] { 90, 95 } };

    static List<KeyValuePair<int, string>> Edits(params int[] lines)
    {
        return lines.Select(l => new KeyValuePair<int, string>(l, "code" + l + "\r\n")).ToList();
    }

    static McpCallToken Running() { var t = new McpCallToken(); t.TryStart(); return t; }

    static int Main()
    {
        bool ok; string msg;

        // --- happy path: bottom-to-top writes, one save, no discard ---
        {
            var ops = new FakeOps { WritesLeft = 2 };
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(10, 90), false, Running(), out ok);
            Ok("happy: ok", ok, msg);
            Ok("happy: writes bottom-to-top then save", ops.ToString() == "write:90,write:10,save,waitclosed", ops.ToString());
        }
        {
            var ops = new FakeOps { WritesLeft = 1 };
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(40), false, null, out ok);
            Ok("no call context (token null): saves normally", ok && ops.Has("save"), ops + " / " + msg);
        }

        // --- abandoned BEFORE save: no save, rollback ---
        foreach (bool adopted in new[] { false, true })
        {
            var tok = Running();
            var ops = new FakeOps { WritesLeft = 2 };
            ops.OnLastWrite = () => tok.Abandon();   // dispatcher times out while we are writing
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(10, 90), adopted, tok, out ok);
            string tag = adopted ? " (adopted)" : " (ours)";
            Ok("abandoned before save: NOT saved" + tag, !ops.Has("save"), ops.ToString());
            Ok("abandoned before save: writes rolled back (discard)" + tag, ops.Has("discard"), ops.ToString());
            Ok("abandoned before save: reports cancelled + rolled back" + tag,
                !ok && msg.Contains("timed out before saving") && msg.Contains("rolled back"), msg);
        }

        // --- abandoned before anything was written ---
        {
            var tok = Running(); tok.Abandon();
            var ops = new FakeOps { WritesLeft = 1 };
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(10), true, tok, out ok);
            Ok("abandoned before writing, adopted: nothing written, editor left alone",
                ops.Log.Count == 0 && !ok && msg.Contains("Nothing was written"), ops + " / " + msg);
            var ops2 = new FakeOps { WritesLeft = 1 };
            msg = EmbedApplyFlow.Apply(ops2, "P", Ranges, Edits(10), false, tok, out ok);
            Ok("abandoned before writing, ours: closed without writing", ops2.ToString() == "discard", ops2.ToString());
        }

        // --- abandoned AFTER the commit point: the save goes ahead, the dispatcher saw Committed ---
        {
            var tok = Running();
            int seen = -1;
            var ops = new FakeOps { WritesLeft = 1 };
            ops.OnSave = () => seen = tok.Abandon();
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(40), false, tok, out ok);
            Ok("abandoned after commit: save completes", ok && ops.Has("save") && !ops.Has("discard"), ops + " / " + msg);
            Ok("abandoned after commit: dispatcher is told Committed", seen == McpCallToken.Committed, seen.ToString());

        }

        // --- save failure / unconfirmed close after writing: discard, adopted or not ---
        foreach (bool adopted in new[] { false, true })
        {
            string tag = adopted ? " (adopted)" : " (ours)";
            var ops = new FakeOps { WritesLeft = 1, SaveResult = "Error: Save() did not confirm persistence (IsDirty=True)" };
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(40), adopted, Running(), out ok);
            Ok("save error: discards our writes" + tag, !ok && ops.ToString() == "write:40,save,discard", ops + " / " + msg);
            Ok("save error: message says discarded and to re-read" + tag,
                msg.Contains("discarded") && msg.Contains("get_embeditor_source"), msg);

            var ops2 = new FakeOps { WritesLeft = 1, Closed = false };
            msg = EmbedApplyFlow.Apply(ops2, "P", Ranges, Edits(40), adopted, Running(), out ok);
            Ok("close unconfirmed: discards (closes)" + tag, !ok && ops2.Has("discard"), ops2 + " / " + msg);
        }

        // --- rollback itself fails: never claim it happened (Codex run-2 HIGH) ---
        {
            var ops = new FakeOps { WritesLeft = 2, FailWriteAt = 10, DiscardError = "Error: TryClose returned false" };
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(10, 90), true, Running(), out ok);
            Ok("failed rollback after write error: says it could not roll back",
                !ok && msg.Contains("Could not roll back") && msg.Contains("close it WITHOUT saving")
                && !msg.Contains("was closed without saving our edits"), msg);

            var tok = Running();
            var ops2 = new FakeOps { WritesLeft = 1, DiscardError = "Error: TryClose returned false" };
            ops2.OnLastWrite = () => tok.Abandon();
            msg = EmbedApplyFlow.Apply(ops2, "P", Ranges, Edits(40), false, tok, out ok);
            Ok("failed rollback after abandon: not saved, and not claimed rolled back",
                !ops2.Has("save") && msg.Contains("Could not roll back") && !msg.Contains("were rolled back"), msg);

            var ops3 = new FakeOps { WritesLeft = 1, SaveResult = "Error: Save() threw: boom", DiscardThrows = true };
            msg = EmbedApplyFlow.Apply(ops3, "P", Ranges, Edits(40), true, Running(), out ok);
            Ok("throwing rollback after save error: says it could not roll back",
                msg.Contains("Could not roll back") && msg.Contains("cancel blew up") && !msg.Contains("were discarded"), msg);

            var ops4 = new FakeOps { WritesLeft = 1, DiscardError = "Error: stuck" };
            msg = EmbedApplyFlow.Apply(ops4, "P", Ranges, Edits(11), false, Running(), out ok);
            Ok("failed close before any write: says it could not be closed, holds no edits",
                msg.Contains("could not be closed") && msg.Contains("holds no edits"), msg);
        }

        // --- write failure: discard, no save ---
        {
            var ops = new FakeOps { WritesLeft = 2, FailWriteAt = 10 };
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(10, 90), true, Running(), out ok);
            Ok("write failure (adopted): discarded, never saved", !ok && ops.Has("discard") && !ops.Has("save"), ops.ToString());
        }

        // --- invalid slot: nothing written; adopted left alone, ours closed ---
        {
            var ops = new FakeOps { WritesLeft = 1 };
            msg = EmbedApplyFlow.Apply(ops, "P", Ranges, Edits(11), true, Running(), out ok);
            Ok("invalid slot, adopted: untouched", ops.Log.Count == 0 && msg.Contains("untouched"), ops + " / " + msg);
            var ops2 = new FakeOps { WritesLeft = 1 };
            msg = EmbedApplyFlow.Apply(ops2, "P", Ranges, Edits(11), false, Running(), out ok);
            Ok("invalid slot, ours: closed without writing", ops2.ToString() == "discard", ops2.ToString());
        }

        Console.WriteLine();
        Console.WriteLine("  " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

}
