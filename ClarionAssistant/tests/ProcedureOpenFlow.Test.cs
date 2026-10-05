using System;
using System.Collections.Generic;
using System.Linq;
using ClarionAssistant.Services;

// a964cde3: open_procedure_embed opened VerifyInventoryFiles when asked for CheckComma. The locator typed the name
// and clicked Embeditor whatever was selected, nothing checked what opened, and with a CA Editor tab in front the
// app tree was not even visible ("ClaList: NOT FOUND"). ProcedureOpenFlow, with the IDE operations faked:
//   * refuses while the app is loading, and names that aren't exact procedures (no prefix-match guess);
//   * brings the app tree forward BEFORE typing, and refuses when its list never becomes visible;
//   * never clicks when the tree selection reads back as another procedure; retries slower, then refuses;
//   * after the open, an embeditor showing another procedure (or one whose name can't be read) is cancelled
//     WITHOUT saving, and the result says so; a cancel that fails says the embeditor is still open;
//   * an open that never arrives returns an error, cancelling one that arrived late.
//
// Run:  tests\Run-Tests.ps1
static class ProcedureOpenFlowTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    sealed class FakeIde : IProcedureOpenOps
    {
        public readonly List<string> Log = new List<string>();
        public List<string> Names = new List<string> { "Main", "CheckComma", "CheckCommaList", "VerifyInventoryFiles" };
        public bool? Loaded = true;
        public bool App = true;
        public string AlreadyOpen;                  // file of an embeditor open before the call
        public bool ActivateOk = true;
        public bool TreeVisible = true;             // ClaList becomes visible once the app tree is activated
        public string TypeError;
        // What the tree selection reads after each TypeLocator call (index = attempt); null = unreadable.
        public List<string> Selections = new List<string>();
        public string ClickError;
        public bool Opens = true;
        // What the opened embeditor's name reads after each click (index = click); null = unreadable.
        public List<string> OpenedNames = new List<string>();
        public string CancelError;
        public bool LateOpen;                       // WaitForOpen times out but the embeditor is open by then
        public bool OpensDuringDrain;               // WaitForOpen times out, then the open arrives during the drain

        bool activated, open;
        int typed, clicks, waits;

        public string OpenEmbeditorFile() { Log.Add("openfile?"); return AlreadyOpen ?? (open ? "PRM002022.clw" : null); }
        public bool HasApp() { return App; }
        public bool? IsAppLoaded() { return Loaded; }
        public IList<string> ProcedureNames() { return Names; }
        public bool ActivateAppTree() { Log.Add("activate"); activated = ActivateOk; return ActivateOk; }
        public bool WaitForClaList(int ms) { Log.Add("claList"); return activated && TreeVisible; }
        public string TypeLocator(string name, int delay)
        {
            Log.Add("type:" + name + "@" + delay);
            typed++;
            return TypeError;
        }
        public string WaitForSelected(string name, int ms)
        {
            Log.Add("selected?");
            int i = typed - 1;
            return i < Selections.Count ? Selections[i] : name;
        }
        public string ClickEmbeditor()
        {
            Log.Add("click");
            if (ClickError != null) return ClickError;
            clicks++;
            if (Opens || LateOpen) open = true;
            return null;
        }
        public bool WaitForOpen(int ms)
        {
            Log.Add("waitOpen:" + ms);
            if (++waits > 1 && OpensDuringDrain) open = true;
            return (Opens || waits > 1) && open;
        }
        public string OpenProcedureName(string requested)
        {
            Log.Add("name?:" + requested);
            int i = clicks - 1;
            return i < OpenedNames.Count ? OpenedNames[i] : "CheckComma";
        }
        public string CancelAndWaitClosed()
        {
            Log.Add("cancel");
            if (CancelError != null) return CancelError;
            open = false;
            return null;
        }
        public bool IsOpen { get { return open; } }
        public int Count(string prefix) { return Log.Count(l => l.StartsWith(prefix)); }
        public override string ToString() { return string.Join(",", Log); }
    }

    static readonly int[] Delays = { 100, 150 };

    static int Main()
    {
        Console.WriteLine("ProcedureOpenFlow (a964cde3)");

        // 1. Happy path: activate BEFORE the locator, click once, verified open left open.
        {
            var ide = new FakeIde();
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("happy: ok", r.Ok, r.Message);
            Ok("happy: message names the procedure", r.Message.Contains("'CheckComma'"), r.Message);
            Ok("happy: app tree activated before typing", ide.Log.IndexOf("activate") >= 0 && ide.Log.IndexOf("activate") < ide.Log.FindIndex(l => l.StartsWith("type:")), ide.ToString());
            Ok("happy: ClaList waited for before typing", ide.Log.IndexOf("claList") < ide.Log.FindIndex(l => l.StartsWith("type:")), ide.ToString());
            Ok("happy: one click, no cancel", ide.Count("click") == 1 && ide.Count("cancel") == 0, ide.ToString());
            Ok("happy: embeditor left open", ide.IsOpen, null);
        }

        // 2. Case-insensitive request is canonicalised to the app's spelling before typing.
        {
            var ide = new FakeIde();
            var r = ProcedureOpenFlow.Open(ide, "  checkcomma ", Delays);
            Ok("canonical: ok", r.Ok, r.Message);
            Ok("canonical: types the app's spelling", ide.Log.Contains("type:CheckComma@100"), ide.ToString());
            Ok("canonical: result carries the app's spelling", r.Procedure == "CheckComma", r.Procedure);
        }

        // 3. Loading gate: IsLoaded false -> retryable error at once, nothing touched.
        {
            var ide = new FakeIde { Loaded = false };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("loading: refused", !r.Ok && r.Message.Contains("still loading") && r.Message.Contains("Retry"), r.Message);
            Ok("loading: nothing activated, typed or clicked", ide.Count("activate") == 0 && ide.Count("type:") == 0 && ide.Count("click") == 0, ide.ToString());
        }
        // ... and an empty procedure list while loading is the same answer.
        {
            var ide = new FakeIde { Loaded = null, Names = new List<string>() };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("loading (no procedures yet): refused", !r.Ok && r.Message.Contains("still loading"), r.Message);
        }
        // ... while IsLoaded unreadable (null) with procedures present proceeds.
        {
            var ide = new FakeIde { Loaded = null };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("loaded unknown but procedures listed: proceeds", r.Ok, r.Message);
        }

        // 4. Name gate: a prefix is not a procedure; refuse with suggestions, touch nothing.
        {
            var ide = new FakeIde();
            var r = ProcedureOpenFlow.Open(ide, "CheckCom", Delays);
            Ok("unknown name: refused", !r.Ok && r.Message.Contains("no procedure named 'CheckCom'"), r.Message);
            Ok("unknown name: suggests the near names", r.Message.Contains("CheckComma") && r.Message.Contains("CheckCommaList"), r.Message);
            Ok("unknown name: nothing typed or clicked", ide.Count("type:") == 0 && ide.Count("click") == 0, ide.ToString());
        }

        // 5. An embeditor already open: refuse, touch nothing.
        {
            var ide = new FakeIde { AlreadyOpen = "Other.clw" };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("already open: refused", !r.Ok && r.Message.Contains("already open (Other.clw)"), r.Message);
            Ok("already open: nothing activated", ide.Count("activate") == 0 && ide.Count("cancel") == 0, ide.ToString());
        }

        // 6. No app at all.
        {
            var ide = new FakeIde { App = false };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("no app: refused", !r.Ok && r.Message.Contains("no .app is open"), r.Message);
        }

        // 7. The app tree never becomes visible (the CA Editor tab case, activation failing): refuse, no typing.
        {
            var ide = new FakeIde { TreeVisible = false };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("tree not visible: refused", !r.Ok && r.Message.Contains("could not bring the app tree forward"), r.Message);
            Ok("tree not visible: nothing typed or clicked", ide.Count("type:") == 0 && ide.Count("click") == 0, ide.ToString());
        }
        {
            var ide = new FakeIde { ActivateOk = false };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("activation failed: refused before typing", !r.Ok && ide.Count("type:") == 0, r.Message + " | " + ide);
        }

        // 8. Pre-click: the tree reads another procedure -> no click; retry slower; then success.
        {
            var ide = new FakeIde { Selections = new List<string> { "VerifyInventoryFiles", "CheckComma" } };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("pre-click miss then hit: ok", r.Ok, r.Message);
            Ok("pre-click miss then hit: retried at the slower speed", ide.Log.Contains("type:CheckComma@100") && ide.Log.Contains("type:CheckComma@150"), ide.ToString());
            Ok("pre-click miss then hit: exactly one click (after the hit)", ide.Count("click") == 1 && ide.Log.IndexOf("click") > ide.Log.IndexOf("type:CheckComma@150"), ide.ToString());
            Ok("pre-click miss then hit: re-activated before the retry", ide.Count("activate") == 2, ide.ToString());
        }
        // ... missing on every attempt -> refuse, never clicked.
        {
            var ide = new FakeIde { Selections = new List<string> { "VerifyInventoryFiles", "VerifyInventoryFiles" } };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("pre-click always wrong: refused", !r.Ok && r.Message.Contains("selected 'VerifyInventoryFiles', not 'CheckComma'"), r.Message);
            Ok("pre-click always wrong: never clicked", ide.Count("click") == 0, ide.ToString());
        }
        // ... unreadable selection (null) does not block: the post-open check guards.
        {
            var ide = new FakeIde { Selections = new List<string> { null } };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("selection unreadable: proceeds to the verified open", r.Ok && ide.Count("click") == 1 && ide.Count("name?") == 1, r.Message + " | " + ide);
            Ok("post-open name is asked for the canonical request", ide.Log.Contains("name?:CheckComma"), ide.ToString());
        }

        // 9. THE BUG: the embeditor opens another procedure -> cancel (no save), retry slower, verified.
        {
            var ide = new FakeIde { OpenedNames = new List<string> { "VerifyInventoryFiles", "CheckComma" } };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("wrong open then right: ok", r.Ok, r.Message);
            Ok("wrong open then right: the wrong one was cancelled before the retry", ide.Count("cancel") == 1 && ide.Log.IndexOf("cancel") < ide.Log.IndexOf("type:CheckComma@150"), ide.ToString());
        }
        // ... wrong on every attempt -> error naming both, nothing left open.
        {
            var ide = new FakeIde { OpenedNames = new List<string> { "VerifyInventoryFiles", "VerifyInventoryFiles" } };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("wrong open always: refused", !r.Ok, r.Message);
            Ok("wrong open always: names what opened and what was asked", r.Message.Contains("'VerifyInventoryFiles'") && r.Message.Contains("'CheckComma'"), r.Message);
            Ok("wrong open always: says cancelled without saving", r.Message.Contains("cancelled without saving"), r.Message);
            Ok("wrong open always: each wrong open cancelled", ide.Count("cancel") == 2, ide.ToString());
            Ok("wrong open always: no embeditor left open", !ide.IsOpen, ide.ToString());
        }
        // ... an unreadable opened name is a mismatch, never a pass.
        {
            var ide = new FakeIde { OpenedNames = new List<string> { null, null } };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("opened name unreadable: refused and cancelled", !r.Ok && ide.Count("cancel") == 2 && !ide.IsOpen, r.Message + " | " + ide);
            Ok("opened name unreadable: says the name could not be read", r.Message.Contains("could not be read"), r.Message);
        }
        // ... the cancel itself fails -> stop at once and say it is still open.
        {
            var ide = new FakeIde { OpenedNames = new List<string> { "VerifyInventoryFiles" }, CancelError = "Error: TryClose hung" };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("cancel fails: refused", !r.Ok, r.Message);
            Ok("cancel fails: warns it is still open and must be cancelled unsaved", r.Message.Contains("still open") && r.Message.Contains("WITHOUT saving"), r.Message);
            Ok("cancel fails: no retry on top of an open wrong embeditor", ide.Count("type:") == 1, ide.ToString());
        }

        // 10. The open never arrives -> error, nothing clicked again.
        {
            var ide = new FakeIde { Opens = false };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("no open: refused", !r.Ok && r.Message.Contains("did not open"), r.Message);
            Ok("no open: one click only", ide.Count("click") == 1, ide.ToString());
        }
        // ... arrived late: cancelled, never left unverified.
        {
            var ide = new FakeIde { Opens = false, LateOpen = true };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("late open: cancelled", !r.Ok && ide.Count("cancel") == 1 && !ide.IsOpen, r.Message + " | " + ide);
            Ok("late open: says it opened late and was cancelled", r.Message.Contains("opened late and was cancelled without saving"), r.Message);
        }
        // ... arrives only during the drain after the timeout: still cancelled, never left unverified.
        {
            var ide = new FakeIde { Opens = false, OpensDuringDrain = true };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("drain: waited a second time, for the drain", ide.Log.Contains("waitOpen:" + ProcedureOpenFlow.LateOpenDrainMs), ide.ToString());
            Ok("drain: the late open was cancelled", !r.Ok && ide.Count("cancel") == 1 && !ide.IsOpen, r.Message + " | " + ide);
        }
        // ... never arrives: the error warns that a later one is unverified.
        {
            var ide = new FakeIde { Opens = false };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("never opens: warns a later embeditor is NOT verified", r.Message.Contains("NOT verified") && r.Message.Contains("cancel_embeditor"), r.Message);
        }

        // 11. Locator / click failures are reported and nothing opens.
        {
            var ide = new FakeIde { TypeError = "the app tree's procedure list (ClaList) was not found" };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("type error: refused, never clicked", !r.Ok && r.Message.Contains("ClaList") && ide.Count("click") == 0, r.Message);
        }
        {
            var ide = new FakeIde { ClickError = "the app tree's Embeditor button was not found" };
            var r = ProcedureOpenFlow.Open(ide, "CheckComma", Delays);
            Ok("click error: refused", !r.Ok && r.Message.Contains("Embeditor button") && !ide.IsOpen, r.Message);
        }

        // 12. select_procedure: same gates, never clicks.
        {
            var ide = new FakeIde();
            var r = ProcedureOpenFlow.Select(ide, "checkcomma", Delays);
            Ok("select: ok and verified", r.Ok && r.Message.StartsWith("Selected 'CheckComma'"), r.Message);
            Ok("select: activated first, never clicked", ide.Log.IndexOf("activate") < ide.Log.FindIndex(l => l.StartsWith("type:")) && ide.Count("click") == 0, ide.ToString());
        }
        {
            var ide = new FakeIde { Selections = new List<string> { "Main", "Main" } };
            var r = ProcedureOpenFlow.Select(ide, "CheckComma", Delays);
            Ok("select wrong row: refused", !r.Ok && r.Message.Contains("selected 'Main'") && r.Message.Contains("Nothing was selected"), r.Message);
        }
        {
            var ide = new FakeIde { Selections = new List<string> { null } };
            var r = ProcedureOpenFlow.Select(ide, "CheckComma", Delays);
            Ok("select unreadable: says it could not be read back", r.Ok && r.Message.Contains("could not be read back"), r.Message);
        }
        {
            var ide = new FakeIde { Loaded = false };
            var r = ProcedureOpenFlow.Select(ide, "CheckComma", Delays);
            Ok("select while loading: refused", !r.Ok && r.Message.Contains("still loading"), r.Message);
        }

        // 13. DecideOpenedName: one reading of the open's identity (pipeline run 1, the stale-header race).
        {
            bool st; string n;
            n = ProcedureOpenFlow.DecideOpenedName("CheckComma", "CheckComma", "PRM002007.clw", "PRM002007.clw", false, out st);
            Ok("identity: all agree -> settled on the name", st && n == "CheckComma", n);

            // THE RACE: a stale header (and document) still naming the request, while the open's own module says otherwise.
            n = ProcedureOpenFlow.DecideOpenedName("CheckComma", "CheckComma", "PRM002022.clw", "PRM002007.clw", false, out st);
            Ok("identity: stale name but this open's module differs -> settled, never the requested name", st && n != null && !string.Equals(n, "CheckComma", StringComparison.OrdinalIgnoreCase) && n.Contains("PRM002022"), n);

            n = ProcedureOpenFlow.DecideOpenedName("CheckComma", "VerifyInventoryFiles", "PRM002007.clw", "PRM002007.clw", false, out st);
            Ok("identity: header and document disagree -> keep reading", !st && n == null, n);
            n = ProcedureOpenFlow.DecideOpenedName("CheckComma", "VerifyInventoryFiles", "PRM002007.clw", "PRM002007.clw", true, out st);
            Ok("identity: still disagreeing at the end -> a description that cannot match", st && n != null && n != "CheckComma" && n.Contains("VerifyInventoryFiles"), n);

            n = ProcedureOpenFlow.DecideOpenedName("CheckComma", "CheckComma", null, "PRM002007.clw", false, out st);
            Ok("identity: this open's details not readable yet -> keep reading", !st && n == null, n);

            n = ProcedureOpenFlow.DecideOpenedName("CheckComma", null, "PRM002007.clw", "PRM002007.clw", false, out st);
            Ok("identity: only the header readable -> keep reading until the end", !st && n == null, n);
            n = ProcedureOpenFlow.DecideOpenedName("CheckComma", null, "PRM002007.clw", "PRM002007.clw", true, out st);
            Ok("identity: only the header readable at the end -> taken", st && n == "CheckComma", n);

            n = ProcedureOpenFlow.DecideOpenedName(null, null, null, null, true, out st);
            Ok("identity: nothing readable at the end -> null (fails the check)", n == null, n);

            n = ProcedureOpenFlow.DecideOpenedName(" CheckComma ", "checkcomma", @"C:\Apps\prm002007.CLW", "PRM002007", false, out st);
            Ok("identity: module spelled differently (path, case, extension) still matches", st && n == "CheckComma", n);

            n = ProcedureOpenFlow.DecideOpenedName("CheckComma", "CheckComma", "PRM002022.clw", null, false, out st);
            Ok("identity: requested module unknown -> name agreement decides", st && n == "CheckComma", n);
        }

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
