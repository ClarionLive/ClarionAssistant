using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClarionAssistant.Services;

// Harness for OpenTabGuard (GH #205): write_file / append_to_file must not write a file underneath a NATIVE Clarion
// editor tab that holds it, because a later save there puts the old text back.
//
// Drives the REAL OpenTabGuard.Run with scripted IDE answers and a write that touches a REAL temp file, so "refused"
// is checked as "the bytes on disk did not change", not just as "the message said so", and the order (check, write,
// THEN reload; never reload after a failed write) is recorded call by call.
//
// The IDE half (NativeTabSync: FileService lookup, IsDirty, AbstractViewContent.Load) needs a live IDE; its shape is
// pinned by tests\OpenTabGuard.SourceScan.ps1 and its behaviour by the live steps on the ticket.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class OpenTabGuardTest
{
    static int pass = 0, fail = 0;
    static readonly List<string> calls = new List<string>();

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    // An IDE the writer asked (so it has a hold_id); like the real IDE it HOLDS only a clean native tab.
    static TabProbe Ide(string kind, bool dirty, string ide = "IDE 100")
    {
        var p = new TabProbe { Ide = ide, Reachable = true, Kind = kind, Dirty = dirty, Handle = ide, HoldId = "hold-" + ide.Replace(" ", "") };
        if (kind == OpenTabGuard.KindNative && !dirty) p.HeldTabs.Add("Main001.clw");
        return p;
    }

    // An IDE that was NOT asked (none running, or skipped after a recent timeout): no hold_id, nothing to release.
    static TabProbe Unreachable(string why, string ide = "IDE 200")
    {
        return new TabProbe { Ide = ide, Reachable = false, Reason = why };
    }

    // GH #205 re-review D1: an IDE that WAS asked to hold but whose answer did not arrive in time (it may have held).
    static TabProbe TimedOutAsked(string ide)
    {
        return new TabProbe { Ide = ide, Reachable = false, TimedOut = true, Reason = "did not answer: timed out after 3000 ms", Handle = ide, HoldId = "hold-" + ide.Replace(" ", "") };
    }

    // Scripts the IDE: what the check returns, and what a reload returns. Every call is recorded.
    static void Script(Func<List<TabProbe>> probe, Func<TabProbe, TabReload> reload)
    {
        OpenTabGuard.Probe = probe == null ? (Func<IList<string>, List<TabProbe>>)null
            : p => { calls.Add("check"); lastChecked = new List<string>(p); return probe(); };
        OpenTabGuard.Reload = reload == null ? (Func<IList<string>, TabProbe, TabReload>)null
            : (p, tab) => { calls.Add("reload:" + tab.Handle); lastReloaded = new List<string>(p); reloadTokens.Add(tab.HoldId); return reload(tab); };
        // GH #205 review F1-1: a refused or failed write gives a held watcher back at once (recorded like the rest).
        OpenTabGuard.Release = (p, tab) => { calls.Add("release:" + tab.Handle); lastReleased = new List<string>(p); releaseTokens.Add(tab.HoldId); };
        reloadTokens.Clear();
        releaseTokens.Clear();
    }

    static List<string> lastReleased;
    static readonly List<string> reloadTokens = new List<string>(), releaseTokens = new List<string>();

    static List<string> lastChecked, lastReloaded;

    static string file;
    static readonly byte[] Original = Encoding.ASCII.GetBytes("  MEMBER()\r\n! original\r\n");

    static void Reset()
    {
        calls.Clear();
        File.WriteAllBytes(file, Original);
    }

    // The tool's write, against the real file.
    static string Write()
    {
        calls.Add("write");
        File.WriteAllText(file, "  MEMBER()\r\n! written\r\n");
        return "File written: " + file;
    }

    static bool Unchanged() { return Convert.ToBase64String(File.ReadAllBytes(file)) == Convert.ToBase64String(Original); }
    static string Calls() { return string.Join(",", calls.ToArray()); }

    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ca-opentab-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        file = Path.Combine(dir, "Main001.clw");
        try
        {
            Console.WriteLine("-- not open anywhere");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNone, false) }, t => new TabReload { Reloaded = true });
            string r = OpenTabGuard.Run(file, false, Write);
            Ok("written, result is the write's own line exactly", r == "File written: " + file, r);
            Ok("check, then write, no reload", Calls() == "check,write", Calls());

            Console.WriteLine("-- open in a native tab, no unsaved changes");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false) },
                   t => new TabReload { Reloaded = true, Line = 12 });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("written", !Unchanged());
            Ok("check, write, THEN reload of that IDE's tab", Calls() == "check,write,reload:IDE 100", Calls());
            Ok("result says the tab was reloaded, caret line kept",
               r.StartsWith("File written: ") && r.Contains("tab for Main001.clw was reloaded from disk (caret kept on line 12)"), r);

            Console.WriteLine("-- native, no unsaved changes, but the reload fails (typed into meanwhile)");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false) },
                   t => new TabReload { Reloaded = false, Reason = "the tab has unsaved changes now" });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("written (the write itself was allowed)", !Unchanged());
            Ok("warns not to save that tab, with the reason",
               r.Contains("WARNING: Main001.clw is open in the Clarion editor and could not be reloaded (the tab has unsaved changes now)")
               && r.Contains("do not save that tab"), r);

            Console.WriteLine("-- native, reload throws");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false) }, t => { throw new IOException("pipe broke"); });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("a throwing reload is a warning, not an exception", r.Contains("could not be reloaded (pipe broke)"), r);

            Console.WriteLine("-- open in a native tab WITH unsaved changes");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, true) }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("REFUSED: the file on disk is unchanged", Unchanged());
            Ok("refused: neither written nor reloaded", Calls() == "check", Calls());
            Ok("the refusal names the file and says to save or close it there",
               r.StartsWith("Error: Main001.clw is open in the Clarion editor with unsaved changes; save or close it there first."), r);
            Ok("the refusal says nothing was written and names the override", r.Contains("Nothing was written") && r.Contains("overwrite_open_buffer: true"), r);

            Console.WriteLine("-- dirty native tab, overwrite_open_buffer: true");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, true) }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, true, Write);
            Ok("written", !Unchanged());
            Ok("the dirty tab is NOT reloaded (that would drop its edits)", Calls() == "check,write", Calls());
            Ok("warns plainly that saving there will overwrite this write",
               r.Contains("WARNING: Main001.clw is open in the Clarion editor with unsaved changes") && r.Contains("WILL OVERWRITE THIS WRITE"), r);

            Console.WriteLine("-- open in the CA Editor");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindCaEditor, false) }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("written, not reloaded by us (the CA Editor watches the disk)", !Unchanged() && Calls() == "check,write", Calls());
            Ok("says the CA Editor offers a reload", r.Contains("open in the CA Editor, which shows a 'File changed on disk' notice"), r);
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindCaEditor, true) }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("CA Editor with edits: written, and the note says what Reload/Save there do",
               !Unchanged() && r.Contains("CA Editor with unsaved edits") && r.Contains("saving there instead overwrites this write"), r);

            Console.WriteLine("-- no IDE can be asked");
            Reset();
            Script(() => new List<TabProbe> { Unreachable("no Clarion with Clarion Assistant is running", null) }, null);
            r = OpenTabGuard.Run(file, false, Write);
            Ok("written as before", !Unchanged() && Calls() == "check,write", Calls());
            Ok("one line says the tabs could not be checked, and why",
               r == "File written: " + file + "\nNote: open native Clarion editor tabs could not be checked (no Clarion with Clarion Assistant is running).", r);
            Reset();
            Script(null, null);
            r = OpenTabGuard.Run(file, false, Write);
            Ok("no probe wired at all: written, with the could-not-check line",
               !Unchanged() && r.EndsWith("could not be checked (" + OpenTabGuard.NoProbeReason + ")."), r);
            Reset();
            Script(() => { throw new InvalidOperationException("boom"); }, null);
            r = OpenTabGuard.Run(file, false, Write);
            Ok("a throwing probe: written, could-not-check line carries the reason", !Unchanged() && r.Contains("could not be checked (the IDE check failed: boom)"), r);

            Console.WriteLine("-- several IDEs");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false, "IDE 1"), Ide(OpenTabGuard.KindNative, true, "IDE 2") },
                   t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("one clean + one dirty native tab: refused, nothing written or reloaded; the CLEAN tab's hold is released at once",
               Unchanged() && Calls() == "check,release:IDE 1" && r.StartsWith("Error: "), Calls() + " | " + r);
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false, "IDE 1"), Unreachable("timed out after 3000 ms", "IDE 2") },
                   t => new TabReload { Reloaded = true, Line = 3 });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("one clean tab + one silent IDE: written, the clean tab reloaded", !Unchanged() && Calls() == "check,write,reload:IDE 1", Calls());
            Ok("...and the silent IDE is named as unchecked", r.Contains("another Clarion IDE could not be checked") && r.Contains("IDE 2: timed out after 3000 ms"), r);

            Console.WriteLine("-- a failed write");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false) }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, () => { calls.Add("write"); return "Error: character not in cp1252"; });
            Ok("nothing reloaded after a failed write; its hold is released at once (F1-1)", Calls() == "check,write,release:IDE 100", Calls());
            Ok("the write's error comes back unchanged", r == "Error: character not in cp1252", r);

            Console.WriteLine("-- review F1-1: every write that does not happen releases the hold, exactly once per held IDE");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false) }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, "C:\\Link\\Main001.clw", false, () => { calls.Add("write"); return "Error: disk full"; });
            Ok("a failed write: released with BOTH spellings (so the IDE finds the tab it held)",
               Calls() == "check,write,release:IDE 100" && lastReleased != null && lastReleased.Count == 2 && lastReleased[0] == file, Calls());
            Reset();
            bool threw = false;
            try { OpenTabGuard.Run(file, false, () => { calls.Add("write"); throw new IOException("locked"); }); }
            catch (IOException) { threw = true; }
            Ok("a write that THROWS releases too, and the exception still surfaces", threw && Calls() == "check,write,release:IDE 100", Calls());
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false, "IDE 1"), Ide(OpenTabGuard.KindNative, true, "IDE 2"),
                                              Ide(OpenTabGuard.KindCaEditor, false, "IDE 3"), Ide(OpenTabGuard.KindNone, false, "IDE 4"),
                                              Unreachable("timed out", "IDE 5") }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("a refusal releases only the IDEs that answered a CLEAN native tab (the only ones that hold)",
               Unchanged() && Calls() == "check,release:IDE 1", Calls());
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false) }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("a successful write does not release (its reload ends the hold)", Calls() == "check,write,reload:IDE 100", Calls());
            Reset();
            OpenTabGuard.Release = (p, tab) => { calls.Add("release"); throw new InvalidOperationException("pipe"); };
            r = OpenTabGuard.Run(file, false, () => { calls.Add("write"); return "Error: nope"; });
            Ok("a release that throws is swallowed (the IDE's cap gives it back)", r == "Error: nope" && Calls() == "check,write,release", Calls());
            OpenTabGuard.Release = null;
            Reset();
            r = OpenTabGuard.Run(file, false, () => { calls.Add("write"); return "Error: nope"; });
            Ok("no release wired (a bare host): the failed write still returns its error", r == "Error: nope", r);
            Ok("an older addin's replies to 'release' are recognised and tolerated",
               OpenTabGuard.IsOlderAddinReleaseReply(OpenTabGuard.UnknownToolReply)
               && OpenTabGuard.IsOlderAddinReleaseReply("Error: action must be 'check' or 'reload'.")
               && !OpenTabGuard.IsOlderAddinReleaseReply("Error: action must be 'check', 'reload' or 'release'.")
               && !OpenTabGuard.IsOlderAddinReleaseReply("Error: something else") && !OpenTabGuard.IsOlderAddinReleaseReply(null));

            Console.WriteLine("-- GH #205 re-review: holder tokens");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false, "IDE 1"), Ide(OpenTabGuard.KindNative, true, "IDE 2"), TimedOutAsked("IDE 5") },
                   t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("D1: a refusal also releases an IDE whose check TIMED OUT here (it may still have held), by its hold_id",
               Unchanged() && Calls() == "check,release:IDE 1,release:IDE 5" && releaseTokens.Contains("hold-IDE5"), Calls());
            Reset();
            Script(() => new List<TabProbe> { TimedOutAsked("IDE 5") }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, () => { calls.Add("write"); return "Error: disk full"; });
            Ok("D1: a failed write releases a timed-out check too", Calls() == "check,write,release:IDE 5", Calls());
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false, "IDE 1"), TimedOutAsked("IDE 5") }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("D1: a SUCCESSFUL write reloads the answered tab and releases the timed-out check (no reload will end its hold)",
               !Unchanged() && Calls() == "check,write,reload:IDE 1,release:IDE 5", Calls());
            Ok("...the reload names its own check's hold_id", reloadTokens.Count == 1 && reloadTokens[0] == "hold-IDE1", string.Join(",", reloadTokens.ToArray()));
            Reset();
            var heldDirty = Ide(OpenTabGuard.KindNative, true, "IDE 3");
            heldDirty.HeldTabs.Add("Main001.clw");   // the IDE held a clean full-check tab; a dirty lexical match made the answer dirty
            Script(() => new List<TabProbe> { heldDirty }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("D3: an answer that is native DIRTY but says it HELD a tab: the refusal releases it (tokens, not the Dirty flag)",
               Unchanged() && Calls() == "check,release:IDE 3" && releaseTokens.Contains("hold-IDE3"), Calls());
            Reset();
            r = OpenTabGuard.Run(file, true, Write);
            Ok("D3: ...and with overwrite_open_buffer it is written, not reloaded, and the hold released at once",
               !Unchanged() && Calls() == "check,write,release:IDE 3", Calls());
            Reset();
            var cooling = Ide(OpenTabGuard.KindNative, false, "IDE 4");
            cooling.HeldTabs.Clear();   // a clean tab that did NOT get a hold (cooling down, releasing, the IDE gave up in time)
            Script(() => new List<TabProbe> { cooling, Ide(OpenTabGuard.KindNone, false, "IDE 6") }, t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, () => { calls.Add("write"); return "Error: nope"; });
            Ok("an IDE that ANSWERED and held nothing is not sent a release (no extra round trip)", Calls() == "check,write", Calls());
            Reset();
            r = OpenTabGuard.Run(file, false, Write);
            Ok("...a clean tab that was not held is still reloaded, naming its hold_id (the IDE drops nobody else's holder)",
               Calls() == "check,write,reload:IDE 4" && reloadTokens.Count == 1 && reloadTokens[0] == "hold-IDE4", Calls());
            var heldBy = OpenTabGuard.HeldBy(new List<TabProbe> { Ide(OpenTabGuard.KindNative, false, "IDE 1"), Unreachable("no IDE", "IDE 7"), TimedOutAsked("IDE 8"), cooling, null });
            Ok("HeldBy: held tabs, and asked-but-unanswered checks; never an un-asked IDE or one that held nothing",
               heldBy.Count == 2 && heldBy[0].Ide == "IDE 1" && heldBy[1].Ide == "IDE 8");

            // The wire: held: [{view, token}] travels on every answer, an error included, and an older addin sends none.
            var heldAnswer = new TabProbe { Reachable = true, Kind = OpenTabGuard.KindNative, HoldId = "abcdef0123456789" };
            heldAnswer.HeldTabs.Add("Main001.clw");
            var wired = OpenTabGuard.CheckToWire(heldAnswer);
            var json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(wired);
            var parsed = OpenTabGuard.CheckFromWire(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>, "IDE 9");
            Ok("wire: a holding check's held: [{view, token}] round-trips through JSON",
               json.Contains("\"held\":[{\"view\":\"Main001.clw\",\"token\":\"abcdef0123456789\"}]") && parsed.HeldTabs.Count == 1 && parsed.HeldTabs[0] == "Main001.clw" && parsed.HoldId == "abcdef0123456789", json);
            var heldErr = new TabProbe { Reachable = false, Reason = "lookup failed", HoldId = "abcdef0123456789" };
            heldErr.HeldTabs.Add("Main001.clw");
            parsed = OpenTabGuard.CheckFromWire(OpenTabGuard.CheckToWire(heldErr), "IDE 9");
            Ok("wire: an ERROR answer still carries what was held", !parsed.Reachable && parsed.HeldTabs.Count == 1);
            parsed = OpenTabGuard.CheckFromWire(OpenTabGuard.CheckToWire(Ide(OpenTabGuard.KindNone, false)), "IDE 9");
            Ok("wire: a check that held nothing sends no held list", parsed.HeldTabs.Count == 0 && !OpenTabGuard.CheckToWire(Ide(OpenTabGuard.KindNone, false)).ContainsKey(OpenTabGuard.HeldKey));

            // IDE side: CheckGated must not lose the full check's holds when another lookup decides the answer (D3).
            Func<IList<string>, TabProbe> fullHeld = q => { var p = new TabProbe { Reachable = true, Kind = OpenTabGuard.KindNative, HoldId = "tok-12345678" }; p.HeldTabs.Add("Main001.clw"); return p; };
            var mixedHold = new List<OpenTabGuard.GatedSpelling>
            {
                new OpenTabGuard.GatedSpelling { Path = file },
                new OpenTabGuard.GatedSpelling { Path = @"C:\Outside\Main001.clw", Refusal = "Error: outside", OutsideRoots = true }
            };
            var gd = OpenTabGuard.CheckGated(mixedHold, true, true, fullHeld, q => new TabProbe { Reachable = true, Kind = OpenTabGuard.KindNative, Dirty = true }, null);
            Ok("D3: clean full-check tab (held) + dirty lexical tab: the merged answer is native DIRTY and still names the hold",
               gd.Reachable && gd.Dirty && gd.HeldTabs.Count == 1 && gd.HoldId == "tok-12345678");
            gd = OpenTabGuard.CheckGated(mixedHold, true, true, fullHeld, q => new TabProbe { Reachable = false, TimedOut = true, Reason = "UI busy" }, null);
            Ok("D3: ...and when the lexical lookup times out, the timeout answer names the hold too", !gd.Reachable && gd.TimedOut && gd.HeldTabs.Count == 1 && gd.HoldId == "tok-12345678");
            gd = OpenTabGuard.CheckGated(mixedHold, true, true, fullHeld, q => new TabProbe { Reachable = true, Kind = OpenTabGuard.KindCaEditor, Dirty = true }, null);
            Ok("D3: a native (held) answer outranking a CA Editor one keeps its hold", gd.Kind == OpenTabGuard.KindNative && gd.HeldTabs.Count == 1);

            Func<object, string> hid = v => OpenTabGuard.ParseHoldId(v == null ? new Dictionary<string, object>() : new Dictionary<string, object> { { OpenTabGuard.HoldIdArg, v } });
            Ok("hold_id: absent = null (an older standalone)", hid(null) == null);
            Ok("hold_id: a well-formed token is taken as is", hid("0123456789abcdef0123456789ABCDEF") == "0123456789abcdef0123456789ABCDEF" && hid("anon-0123456789") == "anon-0123456789");
            Ok("hold_id: malformed (too short, too long, odd characters, not a string) = \"\" (no hold, matches no holder)",
               hid("short") == "" && hid(new string('a', 65)) == "" && hid("abc def 12345") == "" && hid("..\\..\\x12345") == "" && hid(42) == "" && hid(true) == "");
            Ok("a hold id is fresh per check and well-formed", OpenTabGuard.NewHoldId() != OpenTabGuard.NewHoldId() && hid(OpenTabGuard.NewHoldId()).Length == 32);

            Console.WriteLine("-- an IDE ANSWERED but its check failed (pipeline run 2: fail closed)");
            Reset();
            Script(() => new List<TabProbe> { new TabProbe { Ide = "IDE 100", Reachable = false, Answered = true,
                       Reason = "the IDE: Error: editor_tab_sync refused by the Clarion Assistant path policy" } },
                   t => new TabReload { Reloaded = true });
            r = OpenTabGuard.Run(file, false, Write);
            Ok("REFUSED: the file on disk is unchanged", Unchanged() && Calls() == "check", Calls());
            Ok("the refusal says the check could not be made and names the error",
               r.StartsWith("Error: could not check whether Main001.clw is open in the Clarion editor (IDE 100: the IDE: Error: editor_tab_sync refused")
               && r.Contains("Nothing was written") && r.Contains("overwrite_open_buffer: true"), r);
            Reset();
            r = OpenTabGuard.Run(file, true, Write);
            Ok("with overwrite_open_buffer: written, with a WARNING naming the failed check",
               !Unchanged() && r.Contains("WARNING: the open-tab check failed (IDE 100") && !r.Contains("could not be checked ("), r);
            Reset();
            Script(() => new List<TabProbe> { new TabProbe { Ide = "IDE 100", Reachable = false, Answered = false, TimedOut = true,
                       Reason = "the IDE: the IDE's UI thread did not answer within 2 s" } }, null);
            r = OpenTabGuard.Run(file, false, Write);
            Ok("a TIMEOUT is still only a note (written)", !Unchanged() && r.Contains("could not be checked (IDE 100: the IDE: the IDE's UI thread"), r);
            var wire = OpenTabGuard.CheckFromWire(OpenTabGuard.CheckToWire(new TabProbe { Reachable = false, Reason = "path policy" }), "IDE 5");
            Ok("on the wire, an IDE's error is Answered (a failed check)", !wire.Reachable && wire.Answered && !wire.TimedOut);
            wire = OpenTabGuard.CheckFromWire(OpenTabGuard.CheckToWire(new TabProbe { Reachable = false, TimedOut = true, Reason = "UI busy" }), "IDE 5");
            Ok("on the wire, an IDE's UI-thread timeout is a timeout, not a failed check", !wire.Reachable && !wire.Answered && wire.TimedOut);
            wire = OpenTabGuard.CheckFromWire(new Dictionary<string, object> { { "kind", "weird" } }, "IDE 5");
            Ok("a garbled answer is a failed check", !wire.Reachable && wire.Answered);
            wire = OpenTabGuard.CheckFromWire(OpenTabGuard.CheckToWire(new TabProbe { Reachable = false, OutOfScope = true, Reason = "outside every allowed root" }), "IDE 6");
            Ok("on the wire, 'outside that IDE's roots' is out of scope, not a failed check", !wire.Reachable && !wire.Answered && wire.OutOfScope);
            Reset();
            Script(() => new List<TabProbe> { wire }, null);
            r = OpenTabGuard.Run(file, false, Write);
            Ok("an IDE that would not say (out of scope) leaves the write going ahead, with a note", !Unchanged() && r.Contains("could not be checked (IDE 6: the file is outside that IDE's allowed roots"), r);

            Console.WriteLine("-- the IDE's gate refused the path: the tab lookup still runs (pipeline run 3)");
            const string outsideRefusal = "Error: editor_tab_sync refused by the Clarion Assistant path policy (GH #266): 'X' - it is outside every allowed root (C:\\Sln). Hint.";
            const string denyRefusal = "Error: editor_tab_sync refused by the Clarion Assistant path policy (GH #266): 'X' - it is inside a protected folder (.ssh). Hint.";
            var sp = new List<string> { file };
            var lookups = new List<string>();
            var audits = new List<string>();
            Func<IList<string>, TabProbe> full = q => { lookups.Add("identity"); return Ide(OpenTabGuard.KindNone, false); };
            Func<IList<string>, TabProbe> lexDirty = q => { lookups.Add("lexical"); return Ide(OpenTabGuard.KindNative, true); };
            Func<IList<string>, TabProbe> lexNone = q => { lookups.Add("lexical"); return Ide(OpenTabGuard.KindNone, false); };
            Action<string> audit = s => audits.Add(s);
            // What the writer sees: the IDE's answer through the wire, then the guard.
            Func<TabProbe, string> writeAgainst = ans =>
            {
                Reset();
                var back = OpenTabGuard.CheckFromWire(OpenTabGuard.CheckToWire(ans), "IDE 100");
                Script(() => new List<TabProbe> { back }, t => new TabReload { Reloaded = true });
                return OpenTabGuard.Run(file, false, Write);
            };

            var g = OpenTabGuard.CheckGated(null, false, sp, true, true, full, lexDirty, audit);
            Ok("allowed: the full (identity) lookup answers, the lexical one is not used", lookups.Count == 1 && lookups[0] == "identity" && g.Reachable);
            lookups.Clear();
            g = OpenTabGuard.CheckGated(outsideRefusal, true, sp, true, true, full, lexDirty, audit);
            Ok("refused: the identity lookup (file handles) is NOT used on the refused path", !lookups.Contains("identity") && lookups.Contains("lexical"), string.Join(",", lookups.ToArray()));
            Ok("refused, a tab holds it by name: its kind and dirty state are answered", g.Reachable && g.Kind == OpenTabGuard.KindNative && g.Dirty);
            Ok("...and the #266 audit records that a refused path was answered", audits.Count == 1 && audits[0].Contains("refused path matched an open native tab"), string.Join("|", audits.ToArray()));
            r = writeAgainst(g);
            Ok("a DIRTY tab holding a file outside the IDE's roots: the write is REFUSED", Unchanged() && r.StartsWith("Error: Main001.clw is open in the Clarion editor with unsaved changes"), r);

            g = OpenTabGuard.CheckGated(outsideRefusal, true, sp, true, true, full, lexNone, audit);
            Ok("refused, no tab, outside the roots of an IDE with a solution: out of scope", !g.Reachable && g.OutOfScope && !g.Answered);
            r = writeAgainst(g);
            Ok("...the write goes ahead with a note saying the check was by name only",
               !Unchanged() && r.Contains("outside that IDE's allowed roots and no tab there holds it (checked by path name only)"), r);

            // Softened (coordinator, after run 3): the lookup by name always runs, so with no solution open a miss is a real answer.
            g = OpenTabGuard.CheckGated(outsideRefusal, true, sp, true, false, full, lexNone, audit);
            Ok("refused, no tab, IDE with NO solution open: out of scope (a note), not a failed check", !g.Reachable && g.OutOfScope && !g.Answered, g.Reason);
            r = writeAgainst(g);
            Ok("...written, the note says by name only and that the IDE has no solution open",
               !Unchanged() && r.Contains("(checked by path name only; that IDE has no solution open)"), r);
            g = OpenTabGuard.CheckGated(outsideRefusal, true, sp, true, false, full, lexDirty, audit);
            Ok("IDE with NO solution open, a DIRTY tab matches by name: answered", g.Reachable && g.Kind == OpenTabGuard.KindNative && g.Dirty);
            r = writeAgainst(g);
            Ok("...and the write is REFUSED", Unchanged() && r.StartsWith("Error: Main001.clw is open in the Clarion editor with unsaved changes"), r);

            g = OpenTabGuard.CheckGated(outsideRefusal, true, sp, false, false, full, lexNone, audit);
            Ok("refused, no tab, a host with NO workspace at all (the run-2 bug): a failed check", !g.Reachable && g.Answered && !g.OutOfScope && g.Reason.Contains("no workspace"), g.Reason);
            r = writeAgainst(g);
            Ok("...the write is REFUSED", Unchanged() && r.StartsWith("Error: could not check whether Main001.clw"), r);

            g = OpenTabGuard.CheckGated(denyRefusal, false, sp, true, true, full, lexNone, audit);
            Ok("refused by a deny rule (not 'outside every allowed root'), no tab: a failed check", !g.Reachable && g.Answered && !g.OutOfScope);
            r = writeAgainst(g);
            Ok("...the write is REFUSED", Unchanged() && r.StartsWith("Error: could not check whether"), r);

            g = OpenTabGuard.CheckGated("Error: editor_tab_sync refused by the Clarion Assistant path policy (GH #266): the policy could not be evaluated (boom). This fails closed.", false,
                                        sp, true, true, full, lexNone, audit);
            Ok("a gate EXCEPTION, no tab: a failed check", !g.Reachable && g.Answered);
            g = OpenTabGuard.CheckGated(outsideRefusal, true, sp, true, true, full,
                q => new TabProbe { Reachable = false, TimedOut = true, Reason = "UI busy" }, audit);
            Ok("refused, and the lexical lookup timed out: stays a timeout (not out of scope, not a failure)", !g.Reachable && g.TimedOut && !g.OutOfScope && !g.Answered);
            g = OpenTabGuard.CheckGated(outsideRefusal, true, sp, true, true, full, null, audit);
            Ok("refused with no lexical lookup registered: a failed check, never out of scope", !g.Reachable && g.Answered && !g.OutOfScope);
            // Pipeline run 4: the out-of-scope decision reads the gate's STRUCTURED flag, never the refusal text (which echoes
            // the caller's path): a deny refusal whose echoed path happens to contain the words is still a failed check.
            g = OpenTabGuard.CheckGated("Error: ... refused ...: 'C:\\it is outside every allowed root\\x.clw' - it is inside a protected folder", false,
                                        sp, true, true, full, lexNone, audit);
            Ok("the policy's words inside an echoed PATH do not make a deny refusal 'out of scope'", !g.Reachable && g.Answered && !g.OutOfScope);

            Console.WriteLine("-- every spelling is gated on its own (pipeline run 4: an also_path is the caller's choice too)");
            var fullAsked = new List<string>();
            var lexAsked = new List<string>();
            Func<IList<string>, TabProbe> fullRec = q => { fullAsked.AddRange(q); return Ide(OpenTabGuard.KindNone, false); };
            Func<IList<string>, TabProbe> lexRec = q => { lexAsked.AddRange(q); return Ide(OpenTabGuard.KindNone, false); };
            var mixed = new List<OpenTabGuard.GatedSpelling>
            {
                new OpenTabGuard.GatedSpelling { Path = file },
                new OpenTabGuard.GatedSpelling { Path = @"\\attacker\s\x.clw", Refusal = "Error: UNC refused", OutsideRoots = false }
            };
            g = OpenTabGuard.CheckGated(mixed, true, true, fullRec, lexRec, audit);
            Ok("the allowed file_path gets the identity lookup, ONLY it", fullAsked.Count == 1 && fullAsked[0] == file, string.Join(",", fullAsked.ToArray()));
            Ok("the refused UNC also_path gets the by-name lookup only", lexAsked.Count == 1 && lexAsked[0] == @"\\attacker\s\x.clw", string.Join(",", lexAsked.ToArray()));
            Ok("nothing matched, a spelling was allowed (fully checked): not open", g.Reachable && g.Kind == OpenTabGuard.KindNone);
            g = OpenTabGuard.CheckGated(mixed, true, true, fullRec, lexDirty, audit);
            Ok("a dirty tab matching the refused spelling by name still decides", g.Reachable && g.Kind == OpenTabGuard.KindNative && g.Dirty);
            g = OpenTabGuard.CheckGated(mixed, true, true, q => Ide(OpenTabGuard.KindCaEditor, true), lexDirty, audit);
            Ok("a native match outranks a CA Editor match", g.Kind == OpenTabGuard.KindNative && g.Dirty);

            Console.WriteLine("-- an IDE on an older Clarion Assistant (pipeline run 4)");
            string unknown = "Error executing tool 'editor_tab_sync': Unknown tool: editor_tab_sync";
            var old = OpenTabGuard.FromCheckReply(unknown, null, () => "it runs an older Clarion Assistant (in C:\\Clarion11) that cannot check its editor tabs; update Clarion Assistant in that Clarion");
            Ok("'Unknown tool' is an IDE that cannot be asked, not a failed check", !old.Reachable && !old.Answered && !old.TimedOut);
            Reset();
            old.Ide = "IDE 4242";
            Script(() => new List<TabProbe> { old }, null);
            r = OpenTabGuard.Run(file, false, Write);
            Ok("...the write proceeds, with a note naming the IDE, its Clarion, and what to do",
               !Unchanged() && r.Contains("IDE 4242: it runs an older Clarion Assistant (in C:\\Clarion11)") && r.Contains("update Clarion Assistant in that Clarion"), r);
            var other = OpenTabGuard.FromCheckReply("Error executing tool 'editor_tab_sync': Object reference not set to an instance of an object.", null, null);
            Ok("any OTHER error from editor_tab_sync stays a failed check", !other.Reachable && other.Answered);
            Reset();
            other.Ide = "IDE 4242";
            Script(() => new List<TabProbe> { other }, null);
            r = OpenTabGuard.Run(file, false, Write);
            Ok("...and refuses the write", Unchanged() && r.StartsWith("Error: could not check whether"), r);
            Ok("'Unknown tool' for ANOTHER tool is not taken as an older addin", !OpenTabGuard.IsUnknownToolReply("Error executing tool 'get_live_text': Unknown tool: get_live_text"));
            // Pipeline run 5: exact text only.
            Ok("the exact reply, with surrounding whitespace, is an older addin", OpenTabGuard.IsUnknownToolReply("  " + unknown + "\r\n"));
            var echo = OpenTabGuard.FromCheckReply("Error executing tool 'editor_tab_sync': could not read 'C:\\Unknown tool\\x.clw'", null, null);
            Ok("an error that merely contains 'Unknown tool' (an echoed path) is a FAILED check, not an older addin", !echo.Reachable && echo.Answered);
            Ok("...nor is the reply with anything appended", !OpenTabGuard.IsUnknownToolReply(unknown + " (and more)"));

            Console.WriteLine("-- both spellings reach the IDE (pipeline run 1: aliases)");
            Reset();
            Script(() => new List<TabProbe> { Ide(OpenTabGuard.KindNative, false) }, t => new TabReload { Reloaded = true });
            string alias = @"J:\alias\Main001.clw";
            r = OpenTabGuard.Run(file, alias, false, Write);
            Ok("the check is asked about the resolved path AND the caller's spelling",
               lastChecked != null && lastChecked.Count == 2 && lastChecked[0] == file && lastChecked[1] == alias, string.Join(",", (lastChecked ?? new List<string>()).ToArray()));
            Ok("...and so is the reload", lastReloaded != null && lastReloaded.Count == 2 && lastReloaded[1] == alias);
            Reset();
            r = OpenTabGuard.Run(file, file.ToUpperInvariant(), false, Write);
            Ok("the same spelling twice (case aside) is asked once", lastChecked.Count == 1, lastChecked.Count.ToString());
            Ok("Spellings drops empties", OpenTabGuard.Spellings(file, null).Count == 1 && OpenTabGuard.Spellings(file, "").Count == 1);

            Console.WriteLine("-- overwrite_open_buffer parsing");
            Func<object, bool> flag = v => OpenTabGuard.ParseFlag(v == null ? new Dictionary<string, object>()
                : new Dictionary<string, object> { { OpenTabGuard.OverrideArg, v } }, OpenTabGuard.OverrideArg);
            Ok("absent = false", !flag(null));
            Ok("JSON true / \"true\" / \"TRUE\" / \"1\" / \"yes\" = true", flag(true) && flag("true") && flag("TRUE") && flag("1") && flag("yes"));
            Ok("JSON false / \"false\" / \"no\" / \"\" = false", !flag(false) && !flag("false") && !flag("no") && !flag(""));

            Console.WriteLine("-- editor_tab_sync wire format");
            var back = OpenTabGuard.CheckFromWire(OpenTabGuard.CheckToWire(Ide(OpenTabGuard.KindNative, true)), "IDE 9");
            Ok("check round-trips kind and dirty", back.Reachable && back.Kind == OpenTabGuard.KindNative && back.Dirty && back.Ide == "IDE 9");
            back = OpenTabGuard.CheckFromWire(OpenTabGuard.CheckToWire(null), "IDE 9");
            Ok("an unregistered IDE check is unreachable, not 'none'", !back.Reachable && back.Reason.Contains("not registered"), back.Reason);
            back = OpenTabGuard.CheckFromWire(new Dictionary<string, object> { { "kind", "weird" } }, "IDE 9");
            Ok("an unknown kind is unreachable, never read as 'not open'", !back.Reachable, back.Kind);
            back = OpenTabGuard.CheckFromWire(null, "IDE 9");
            Ok("a non-JSON answer is unreachable", !back.Reachable);
            var rb = OpenTabGuard.ReloadFromWire(OpenTabGuard.ReloadToWire(new TabReload { Reloaded = true, Line = 7 }));
            Ok("reload round-trips", rb.Reloaded && rb.Line == 7);
            rb = OpenTabGuard.ReloadFromWire(OpenTabGuard.ReloadToWire(null));
            Ok("an unregistered reload is not-reloaded with a reason", !rb.Reloaded && rb.Reason.Contains("not registered"), rb.Reason);
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  [FAIL] harness threw: " + ex);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("OpenTabGuard: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
