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
