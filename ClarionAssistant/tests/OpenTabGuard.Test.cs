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

    static TabProbe Ide(string kind, bool dirty, string ide = "IDE 100")
    {
        return new TabProbe { Ide = ide, Reachable = true, Kind = kind, Dirty = dirty, Handle = ide };
    }

    static TabProbe Unreachable(string why, string ide = "IDE 200")
    {
        return new TabProbe { Ide = ide, Reachable = false, Reason = why };
    }

    // Scripts the IDE: what the check returns, and what a reload returns. Every call is recorded.
    static void Script(Func<List<TabProbe>> probe, Func<TabProbe, TabReload> reload)
    {
        OpenTabGuard.Probe = probe == null ? (Func<IList<string>, List<TabProbe>>)null
            : p => { calls.Add("check"); lastChecked = new List<string>(p); return probe(); };
        OpenTabGuard.Reload = reload == null ? (Func<IList<string>, TabProbe, TabReload>)null
            : (p, tab) => { calls.Add("reload:" + tab.Handle); lastReloaded = new List<string>(p); return reload(tab); };
    }

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
            Script(() => new List<TabProbe> { Unreachable("no IDE has a Clarion Assistant pane running", null) }, null);
            r = OpenTabGuard.Run(file, false, Write);
            Ok("written as before", !Unchanged() && Calls() == "check,write", Calls());
            Ok("one line says the tabs could not be checked, and why",
               r == "File written: " + file + "\nNote: open native Clarion editor tabs could not be checked (no IDE has a Clarion Assistant pane running).", r);
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
            Ok("one clean + one dirty native tab: refused, nothing written or reloaded", Unchanged() && Calls() == "check" && r.StartsWith("Error: "), Calls() + " | " + r);
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
            Ok("nothing reloaded after a failed write", Calls() == "check,write", Calls());
            Ok("the write's error comes back unchanged", r == "Error: character not in cp1252", r);

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
