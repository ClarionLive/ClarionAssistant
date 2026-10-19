using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>One IDE's answer about one file: is it open in an editor tab, which kind, and does that tab hold unsaved edits.</summary>
    public sealed class TabProbe
    {
        /// <summary>Who answered ("IDE 1234"), for messages and logs. Never a token.</summary>
        public string Ide;
        /// <summary>false: this IDE could not be asked; <see cref="Reason"/> says why.</summary>
        public bool Reachable;
        public string Reason;
        /// <summary><see cref="OpenTabGuard.KindNone"/>, <see cref="OpenTabGuard.KindNative"/> or <see cref="OpenTabGuard.KindCaEditor"/>.</summary>
        public string Kind;
        public bool Dirty;
        /// <summary>Host-private handle the reload goes back through (the standalone's endpoint). Opaque here.</summary>
        public object Handle;
        /// <summary>Unreachable because it ran out of time (not refused, not down): IdeFanOut stops asking it for a while.</summary>
        public bool TimedOut;
        /// <summary>
        /// Pipeline run 2: with <see cref="Reachable"/> false, the IDE DID answer, but its check failed (a path-policy
        /// refusal, an exception, a garbled answer). That is not "no IDE": the file may well be open there, so the guard
        /// refuses the write instead of noting it. False for silence (no IDE, connection refused, timeout).
        /// </summary>
        public bool Answered;
        /// <summary>
        /// The IDE answered that the path is outside ITS allowed roots (the #266 read gate on editor_tab_sync), with a
        /// workspace in place: that IDE will not say whether the file is open there. Normal for a second IDE on another
        /// solution, so it is a note, not a refusal. The same refusal from an IDE with NO workspace (a mis-wired host, the
        /// pipeline run 2 HIGH) is not out of scope: it is a failed check.
        /// </summary>
        public bool OutOfScope;
        /// <summary>With <see cref="OutOfScope"/>: that IDE has no solution open (said in the note).</summary>
        public bool NoSolution;
    }

    /// <summary>The outcome of reloading one native tab from disk.</summary>
    public sealed class TabReload
    {
        public bool Reloaded;
        /// <summary>1-based caret line the tab was left on, 0 when unknown.</summary>
        public int Line;
        public string Reason;
    }

    /// <summary>What a write tool must do about the open tabs, decided BEFORE anything is written.</summary>
    public sealed class TabGuardDecision
    {
        /// <summary>Non-null: do not write; return this (an "Error: ..." line).</summary>
        public string Refusal;
        /// <summary>The native tabs to reload from disk once the write has succeeded.</summary>
        public List<TabProbe> ReloadAfter = new List<TabProbe>();
        /// <summary>Lines appended to the write's result whatever happens next.</summary>
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// GH #205: write_file / append_to_file must not write a file underneath a NATIVE Clarion editor tab that holds it.
    /// That tab keeps its own buffer; a later save there silently puts the old text back. The CA Editor (Monaco overlay)
    /// already watches the disk and offers a reload, so it is left as it is.
    ///
    /// Before the write the guard asks the IDE(s) about the file, then:
    ///   - not open anywhere ................. write.
    ///   - open in a native tab, no edits ..... write, then reload that tab from disk (caret line kept).
    ///   - open in a native tab WITH edits .... refuse, nothing written; unless overwrite_open_buffer: true, which writes
    ///                                          and says plainly that the tab still has its edits and a save there will
    ///                                          overwrite this write (the tab is NOT reloaded: that would drop them).
    ///   - open in the CA Editor .............. write; say it will offer to reload.
    ///   - no IDE could be asked .............. write as before, plus one line saying the tabs could not be checked
    ///                                          (no IDE, connection refused, or it did not answer in time).
    ///   - an IDE answered but its check FAILED (path-policy refusal, exception, garbled answer): refuse, naming the
    ///                                          error, unless overwrite_open_buffer: true (pipeline run 2: fail closed).
    ///
    /// Host wiring (both sets of delegates are set at start-up; this file has no IDE references and is compiled into
    /// the addin and the standalone clarion-mcp-server):
    ///   - <see cref="Probe"/> / <see cref="Reload"/>: what write_file uses. The standalone points them at
    ///     IdeTabClient (every trusted IDE pane, over its MCP endpoint); the addin at its own editors.
    ///   - <see cref="LocalCheck"/> / <see cref="LocalReload"/>: the addin's own editors, behind the IdeOnly tool
    ///     editor_tab_sync, which is what IdeTabClient calls.
    /// </summary>
    public static class OpenTabGuard
    {
        public const string KindNone = "none";
        public const string KindNative = "native";
        public const string KindCaEditor = "ca-editor";
        public const string OverrideArg = "overwrite_open_buffer";

        // Every delegate takes ALL the spellings of the target (pipeline run 1): the gated, link-resolved path first, then
        // the path as the caller gave it. The IDE matches a tab against any of them by file identity (FileIdentity), so a
        // tab opened through a junction, a subst drive, a symlink or an 8.3 name is still found.
        public static Func<IList<string>, List<TabProbe>> Probe;
        public static Func<IList<string>, TabProbe, TabReload> Reload;
        public static Func<IList<string>, TabProbe> LocalCheck;
        /// <summary>Pipeline run 4: (spellings the gate allowed - matched by identity, spellings it refused - matched by
        /// path text only). A refused path can only reload a tab already open under that very name.</summary>
        public static Func<IList<string>, IList<string>, TabReload> LocalReload;

        /// <summary>editor_tab_sync's optional second spelling of file_path.</summary>
        public const string AlsoPathArg = "also_path";

        /// <summary>The IDE's tab lookup by path TEXT only (no file handles), for a path its #266 gate refused.</summary>
        public static Func<IList<string>, TabProbe> LocalCheckLexical;

        /// <summary>
        /// GH #205 review (SEC MED): the write path's check - (spellings to look up, spellings the #266 WRITE gate allowed).
        /// It also holds the IDE file watcher of a clean native tab of the second set until the reload (NativeTabSync).
        /// Asked only for editor_tab_sync check with <see cref="HoldArg"/>: true; a plain check never holds.
        /// </summary>
        public static Func<IList<string>, IList<string>, TabProbe> LocalCheckHold;
        /// <summary>editor_tab_sync check's flag: this check is the write path's, about to write (hold the watcher).</summary>
        public const string HoldArg = "hold";

        /// <summary>
        /// GH #205 review F1-1: the write was refused or failed after the check held a tab's watcher; give it back now.
        /// What <see cref="Run"/> uses (the standalone: IdeTabClient.Release; the addin: its own). Best effort, silent.
        /// </summary>
        public static Action<IList<string>, TabProbe> Release;
        /// <summary>editor_tab_sync action 'release' in the IDE: (allowed spellings by identity, refused ones by name).</summary>
        public static Func<IList<string>, IList<string>, TabReload> LocalRelease;

        /// <summary>
        /// An IDE that cannot release (an addin from before the review fix): "Unknown tool" (before #205), or the old
        /// handler's "action must be 'check' or 'reload'". Tolerated silently - such an addin never held anything.
        /// </summary>
        public static bool IsOlderAddinReleaseReply(string reply)
        {
            if (reply == null) return false;
            string r = reply.Trim();
            return string.Equals(r, UnknownToolReply, StringComparison.Ordinal)
                || string.Equals(r, OldActionReply, StringComparison.Ordinal);
        }

        /// <summary>The editor_tab_sync handler's unknown-action reply before 'release' existed, verbatim.</summary>
        public const string OldActionReply = "Error: action must be 'check' or 'reload'.";

        /// <summary>One spelling of the target with the IDE's #266 read-gate verdict on it.</summary>
        public sealed class GatedSpelling
        {
            /// <summary>The canonical path when allowed; the spelling as given when refused (it is then compared as text only).</summary>
            public string Path;
            /// <summary>Null when the gate allowed it; else the refusal.</summary>
            public string Refusal;
            /// <summary>The refusal is "not inside any allowed root" (McpPathGate's structured flag, never the refusal text).</summary>
            public bool OutsideRoots;
        }

        /// <summary>
        /// GH #205 pipeline runs 3 and 4: editor_tab_sync's check, given the IDE's #266 read gate verdict on EVERY spelling.
        ///
        /// The gate must NOT stop the tab lookup. The IDE's allowed roots and the writer's differ for the same file (the
        /// standalone's CLARION_MCP_ALLOWED_ROOTS, a clarion-assistant.json version pin, an IDE with no solution open, the
        /// seconds after a switch), so "outside my roots" says nothing about whether a dirty tab holds it. So:
        ///   - allowed spellings: the full lookup (file identity, <paramref name="fullCheck"/>);
        ///   - refused spellings (each one is gated - an also_path is the caller's choice too): the lookup still runs, by
        ///     path TEXT only (<paramref name="lexicalCheck"/>): no handle is opened on a refused path, so an attacker's
        ///     \\host\share\x cannot make the IDE authenticate to it. A matching tab is answered normally - kind and dirty,
        ///     one bit about a path the caller already named - and <paramref name="audit"/> records that it was;
        ///   - a tab matched either way: answered (a native tab outranks a CA Editor one; dirty if any matched tab is);
        ///   - nothing matched and some spelling was allowed: not open (that spelling was fully checked);
        ///   - nothing matched and every spelling was refused: OUT OF SCOPE (a note, "checked by path name only") when every
        ///     refusal is "outside every allowed root" and the IDE HAS a workspace, solution open or not. Anything else - a
        ///     deny rule, a policy exception, a host with NO workspace at all (pipeline run 2's mis-wired host) - is a
        ///     FAILED check, which refuses the write.
        /// Pure: the lookups and the audit are delegates.
        /// </summary>
        public static TabProbe CheckGated(IList<GatedSpelling> spellings, bool hasWorkspace, bool workspaceHasSolution,
                                          Func<IList<string>, TabProbe> fullCheck, Func<IList<string>, TabProbe> lexicalCheck,
                                          Action<string> audit)
        {
            var allowed = new List<string>();
            var refused = new List<GatedSpelling>();
            if (spellings != null)
                foreach (var s in spellings)
                {
                    if (s == null || string.IsNullOrEmpty(s.Path)) continue;
                    if (s.Refusal == null) allowed.Add(s.Path); else refused.Add(s);
                }
            if (allowed.Count == 0 && refused.Count == 0)
                return new TabProbe { Reachable = false, Answered = true, Reason = "no path to check" };

            TabProbe full = null, lex = null;
            if (allowed.Count > 0)
            {
                full = Ask(fullCheck, allowed);
                if (!full.Reachable) return full;   // a timeout stays a timeout; a failure stays a failure
            }
            if (refused.Count > 0)
            {
                var texts = new List<string>();
                foreach (var s in refused) texts.Add(s.Path);
                lex = Ask(lexicalCheck, texts);
                if (!lex.Reachable) return lex;
                if (lex.Kind != KindNone && audit != null)
                    try { audit("refused path matched an open " + lex.Kind + " tab by name; answered its kind and dirty state only"); } catch { }
            }

            var merged = Merge(full, lex);
            if (merged.Kind != KindNone || allowed.Count > 0) return merged;

            bool allOutside = true;
            foreach (var s in refused) if (!s.OutsideRoots) allOutside = false;
            if (allOutside && hasWorkspace)
                return new TabProbe { Reachable = false, OutOfScope = true, NoSolution = !workspaceHasSolution, Reason = refused[0].Refusal };
            GatedSpelling why = refused.Find(s => !s.OutsideRoots) ?? refused[0];
            return new TabProbe
            {
                Reachable = false, Answered = true,
                Reason = why.Refusal + (allOutside && !hasWorkspace ? " (this IDE's tab check has no workspace)" : "")
            };
        }

        /// <summary>The single-verdict form (one refusal, or none, for every spelling): for tests and simple callers.</summary>
        public static TabProbe CheckGated(string gateRefusal, bool outsideRoots, IList<string> spellings, bool hasWorkspace,
                                          bool workspaceHasSolution, Func<IList<string>, TabProbe> fullCheck,
                                          Func<IList<string>, TabProbe> lexicalCheck, Action<string> audit)
        {
            var list = new List<GatedSpelling>();
            if (spellings != null)
                foreach (var p in spellings) list.Add(new GatedSpelling { Path = p, Refusal = gateRefusal, OutsideRoots = gateRefusal != null && outsideRoots });
            return CheckGated(list, hasWorkspace, workspaceHasSolution, fullCheck, lexicalCheck, audit);
        }

        private static TabProbe Ask(Func<IList<string>, TabProbe> check, IList<string> paths)
        {
            if (check == null) return new TabProbe { Reachable = false, Answered = true, Reason = "the IDE's editor check is not registered" };
            try { return check(paths) ?? new TabProbe { Reachable = false, Answered = true, Reason = "the IDE's editor check gave no answer" }; }
            catch (Exception ex) { return new TabProbe { Reachable = false, Answered = true, Reason = "the editor lookup failed: " + ex.Message }; }
        }

        // Two reachable answers about the same file: a native tab outranks a CA Editor one (it is the one that loses data).
        private static TabProbe Merge(TabProbe a, TabProbe b)
        {
            if (a == null) return b;
            if (b == null) return a;
            int ra = Rank(a.Kind), rb = Rank(b.Kind);
            if (ra != rb) return ra > rb ? a : b;
            return new TabProbe { Reachable = true, Kind = a.Kind, Dirty = a.Dirty || b.Dirty, Ide = a.Ide ?? b.Ide };
        }

        private static int Rank(string kind) { return kind == KindNative ? 2 : kind == KindCaEditor ? 1 : 0; }

        /// <summary>The distinct, non-empty spellings, <paramref name="primary"/> first.</summary>
        public static List<string> Spellings(string primary, string alsoPath)
        {
            var list = new List<string>();
            foreach (var p in new[] { primary, alsoPath })
            {
                if (string.IsNullOrEmpty(p)) continue;
                bool dup = false;
                foreach (var q in list) if (FileIdentity.Lexical(q, p)) dup = true;
                if (!dup) list.Add(p);
            }
            return list;
        }

        /// <summary>Why the tabs could not be checked when no host wired a probe (a bare test host, say).</summary>
        public const string NoProbeReason = "this server has no connection to a Clarion IDE";

        /// <summary>The decision for <paramref name="path"/>, from every IDE's answer. Pure: no I/O.</summary>
        public static TabGuardDecision Decide(string path, IList<TabProbe> probes, bool overwriteOpenBuffer)
        {
            var d = new TabGuardDecision();
            string name = SafeName(path);
            var unreachable = new List<string>();
            var failedChecks = new List<string>();
            bool anyReached = false;
            var dirtyNative = new List<TabProbe>();

            if (probes != null)
            {
                foreach (var p in probes)
                {
                    if (p == null) continue;
                    if (!p.Reachable)
                    {
                        string who = (string.IsNullOrEmpty(p.Ide) ? "" : p.Ide + ": ") + (p.Reason ?? "no answer");
                        if (p.Answered) failedChecks.Add(who); else unreachable.Add(who);
                        continue;
                    }
                    anyReached = true;
                    if (p.Kind == KindNative)
                    {
                        if (p.Dirty) dirtyNative.Add(p);
                        else d.ReloadAfter.Add(p);
                    }
                    else if (p.Kind == KindCaEditor)
                    {
                        d.Notes.Add(p.Dirty
                            ? name + " is open in the CA Editor with unsaved edits. It shows a 'File changed on disk' notice: Reload "
                              + "there takes this write and discards those edits; saving there instead overwrites this write."
                            : name + " is open in the CA Editor, which shows a 'File changed on disk' notice and offers to reload it.");
                    }
                }
            }

            if (dirtyNative.Count > 0)
            {
                if (!overwriteOpenBuffer)
                {
                    d.Refusal = "Error: " + name + " is open in the Clarion editor with unsaved changes; save or close it there first. "
                        + "Nothing was written. (To write anyway, pass " + OverrideArg + ": true; the tab then keeps its unsaved "
                        + "changes, and saving it there will overwrite this write.)";
                    d.ReloadAfter.Clear();
                    d.Notes.Clear();
                    return d;
                }
                d.Notes.Add("WARNING: " + name + " is open in the Clarion editor with unsaved changes, and " + OverrideArg
                    + " was set, so it was written anyway. That tab was NOT reloaded (it would lose its edits): saving it there "
                    + "WILL OVERWRITE THIS WRITE. To keep this write, close that tab without saving.");
            }

            // Pipeline run 2: an IDE that ANSWERED but whose check failed may hold the file in a dirty tab: fail closed.
            if (failedChecks.Count > 0)
            {
                string why = string.Join("; ", failedChecks.ToArray());
                if (!overwriteOpenBuffer)
                {
                    d.Refusal = "Error: could not check whether " + name + " is open in the Clarion editor (" + why + "). "
                        + "Nothing was written. Fix that, or close the file's tab in that IDE; to write anyway, pass "
                        + OverrideArg + ": true (an open tab there would then keep its old text, and saving it would overwrite this write).";
                    d.ReloadAfter.Clear();
                    d.Notes.Clear();
                    return d;
                }
                d.Notes.Add("WARNING: the open-tab check failed (" + why + "), and " + OverrideArg + " was set, so " + name
                    + " was written anyway. If it is open in that IDE's Clarion editor, saving the tab there will overwrite this write.");
                anyReached = true;   // that IDE did answer: do not also call it "could not be checked"
            }

            if (!anyReached)
            {
                string why = unreachable.Count > 0 ? string.Join("; ", unreachable.ToArray()) : NoProbeReason;
                d.Notes.Add("Note: open native Clarion editor tabs could not be checked (" + why + ").");
            }
            else if (unreachable.Count > 0)
            {
                d.Notes.Add("Note: another Clarion IDE could not be checked for an open tab of " + name + " ("
                    + string.Join("; ", unreachable.ToArray()) + ").");
            }
            return d;
        }

        /// <summary>
        /// The whole guarded write: ask, decide, write (only if allowed), reload (only after a successful write), and
        /// compose the result. <paramref name="write"/> returns the tool's own result line; one starting "Error" is a
        /// failed write, so nothing is reloaded. Never throws for a probe or reload failure.
        /// </summary>
        public static string Run(string path, bool overwriteOpenBuffer, Func<string> write)
        {
            return Run(path, null, overwriteOpenBuffer, write);
        }

        /// <summary>As above; <paramref name="askedPath"/> is the path as the caller spelled it, before the path gate
        /// resolved it to <paramref name="path"/>. The IDE is asked about both.</summary>
        public static string Run(string path, string askedPath, bool overwriteOpenBuffer, Func<string> write)
        {
            var spellings = Spellings(path, askedPath);
            List<TabProbe> probes = null;
            var probe = Probe;
            if (probe != null)
            {
                try { probes = probe(spellings); }
                catch (Exception ex)
                {
                    probes = new List<TabProbe> { new TabProbe { Reachable = false, Reason = "the IDE check failed: " + ex.Message } };
                }
            }

            var d = Decide(path, probes, overwriteOpenBuffer);
            // GH #205 review F1-1: a check that found a clean native tab held that IDE's file watcher for this write. A
            // refused or failed write reloads nothing, so it gives every such hold back AT ONCE (not at the 30 s cap).
            if (d.Refusal != null) { ReleaseHolds(spellings, probes); return d.Refusal; }

            string result;
            try { result = write(); }
            catch { ReleaseHolds(spellings, probes); throw; }
            if (result == null) result = "";
            if (result.StartsWith("Error", StringComparison.Ordinal)) { ReleaseHolds(spellings, probes); return result; }   // nothing new on disk: nothing to reload

            var lines = new List<string>();
            foreach (var tab in d.ReloadAfter) lines.Add(ReloadNote(spellings, tab));
            lines.AddRange(d.Notes);
            if (lines.Count == 0) return result;
            return result + "\n" + string.Join("\n", lines.ToArray());
        }

        /// <summary>The probes whose check may have held a watcher: a reachable, CLEAN native tab (the only kind held).</summary>
        public static List<TabProbe> HeldBy(IList<TabProbe> probes)
        {
            var held = new List<TabProbe>();
            if (probes != null)
                foreach (var p in probes)
                    if (p != null && p.Reachable && p.Kind == KindNative && !p.Dirty) held.Add(p);
            return held;
        }

        // Never throws: a release that fails leaves the hold to its cap.
        private static void ReleaseHolds(IList<string> spellings, IList<TabProbe> probes)
        {
            var release = Release;
            if (release == null) return;
            foreach (var p in HeldBy(probes))
                try { release(spellings, p); } catch { }
        }

        private static string ReloadNote(IList<string> spellings, TabProbe tab)
        {
            string name = SafeName(spellings.Count > 0 ? spellings[0] : "");
            TabReload r = null;
            var reload = Reload;
            if (reload != null)
            {
                try { r = reload(spellings, tab); }
                catch (Exception ex) { r = new TabReload { Reloaded = false, Reason = ex.Message }; }
            }
            if (r != null && r.Reloaded)
                return "The open Clarion editor tab for " + name + " was reloaded from disk"
                    + (r.Line > 0 ? " (caret kept on line " + r.Line + ")." : ".");
            return "WARNING: " + name + " is open in the Clarion editor and could not be reloaded ("
                + (r != null && !string.IsNullOrEmpty(r.Reason) ? r.Reason : "no reload available") + "). Its buffer is now "
                + "older than the file: do not save that tab; close it without saving (or reopen the file) to see this write.";
        }

        /// <summary>overwrite_open_buffer: a JSON boolean, or "true"/"1"/"yes". Anything else, or absent, is false.</summary>
        public static bool ParseFlag(Dictionary<string, object> args, string key)
        {
            object v;
            if (args == null || !args.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            string s = v.ToString().Trim();
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        // ---- the IDE tool's wire format (editor_tab_sync), shared by its handler and the standalone client ----

        public static Dictionary<string, object> CheckToWire(TabProbe p)
        {
            var a = new Dictionary<string, object>();
            if (p == null || !p.Reachable)
            {
                a["error"] = p != null && p.Reason != null ? p.Reason : "the IDE's editor check is not registered";
                if (p != null && p.TimedOut) a["timedOut"] = true;   // its UI thread was busy: silence, not a failed check
                if (p != null && p.OutOfScope) a["outOfScope"] = true;   // outside that IDE's allowed roots; no tab matched by name
                if (p != null && p.OutOfScope && p.NoSolution) a["noSolution"] = true;
                return a;
            }
            a["kind"] = p.Kind ?? KindNone;
            a["dirty"] = p.Dirty;
            return a;
        }

        /// <summary>
        /// Pipeline run 4: the pane's answer for a tool it does not have - a Clarion IDE running a Clarion Assistant from
        /// before #205. McpDispatcher wraps the registry's ArgumentException("Unknown tool: " + name) as
        /// "Error executing tool 'editor_tab_sync': Unknown tool: editor_tab_sync" (pinned by tests\OpenTabGuard.SourceScan.ps1
        /// against both). Only THAT reply: any other error stays a failed check.
        /// </summary>
        public static bool IsUnknownToolReply(string reply)
        {
            // Pipeline run 5: EXACT text only (the reply of every addin from 1f4d918 through d3116a7^). A newer addin's
            // error that merely contains the words - say, echoing a path like C:\Unknown tool\x - is a failed check.
            return reply != null && string.Equals(reply.Trim(), UnknownToolReply, StringComparison.Ordinal);
        }

        /// <summary>McpDispatcher's reply for a pane without editor_tab_sync, verbatim.</summary>
        public const string UnknownToolReply = "Error executing tool 'editor_tab_sync': Unknown tool: editor_tab_sync";

        /// <summary>
        /// One IDE's raw editor_tab_sync check reply as a TabProbe: an older addin's "Unknown tool" is an IDE that cannot be
        /// asked (a note naming it, <paramref name="olderAddinReason"/>; the write proceeds); any other "Error" reply is a
        /// FAILED check (refuses the write); otherwise the JSON answer.
        /// </summary>
        public static TabProbe FromCheckReply(string text, Dictionary<string, object> json, Func<string> olderAddinReason)
        {
            if (IsUnknownToolReply(text))
            {
                string why = null;
                try { why = olderAddinReason != null ? olderAddinReason() : null; } catch { }
                return new TabProbe { Reachable = false, Answered = false, Reason = why ?? "it runs an older Clarion Assistant that cannot check its editor tabs; update Clarion Assistant in that Clarion" };
            }
            if (text != null && text.StartsWith("Error", StringComparison.Ordinal))
                return new TabProbe { Reachable = false, Answered = true, Reason = "the IDE refused: " + text };
            return CheckFromWire(json, null);
        }

        /// <summary>An IDE's answer. Anything but a well-formed kind is a FAILED CHECK (<see cref="TabProbe.Answered"/>),
        /// which refuses the write, except an error the IDE flags timedOut (its UI thread did not answer in time).</summary>
        public static TabProbe CheckFromWire(Dictionary<string, object> a, string ide)
        {
            object o;
            if (a == null) return new TabProbe { Ide = ide, Reachable = false, Answered = true, Reason = "the IDE's answer was not JSON" };
            if (a.TryGetValue("error", out o) && o != null)
            {
                object t;
                bool timedOut = a.TryGetValue("timedOut", out t) && t is bool && (bool)t;
                bool outOfScope = a.TryGetValue("outOfScope", out t) && t is bool && (bool)t;
                bool noSolution = a.TryGetValue("noSolution", out t) && t is bool && (bool)t;
                return new TabProbe
                {
                    Ide = ide, Reachable = false, Answered = !timedOut && !outOfScope, TimedOut = timedOut,
                    OutOfScope = outOfScope, NoSolution = outOfScope && noSolution,
                    Reason = outOfScope
                        ? "the file is outside that IDE's allowed roots and no tab there holds it (checked by path name only"
                          + (noSolution ? "; that IDE has no solution open)" : ")")
                        : "the IDE: " + o
                };
            }
            string kind = a.TryGetValue("kind", out o) ? o as string : null;
            if (kind != KindNone && kind != KindNative && kind != KindCaEditor)
                return new TabProbe { Ide = ide, Reachable = false, Answered = true, Reason = "the IDE gave an unknown tab kind '" + (kind ?? "") + "'" };
            bool dirty = a.TryGetValue("dirty", out o) && o is bool && (bool)o;
            return new TabProbe { Ide = ide, Reachable = true, Kind = kind, Dirty = dirty };
        }

        public static Dictionary<string, object> ReloadToWire(TabReload r)
        {
            var a = new Dictionary<string, object>();
            a["reloaded"] = r != null && r.Reloaded;
            if (r != null && r.Line > 0) a["line"] = r.Line;
            if (r == null || !r.Reloaded) a["reason"] = r != null && r.Reason != null ? r.Reason : "the IDE's reload is not registered";
            return a;
        }

        public static TabReload ReloadFromWire(Dictionary<string, object> a)
        {
            object o;
            if (a == null) return new TabReload { Reason = "the IDE's answer was not JSON" };
            var r = new TabReload { Reloaded = a.TryGetValue("reloaded", out o) && o is bool && (bool)o };
            if (a.TryGetValue("line", out o) && o != null) { try { r.Line = Convert.ToInt32(o); } catch { } }
            if (a.TryGetValue("reason", out o)) r.Reason = o as string;
            return r;
        }

        private static string SafeName(string path)
        {
            try { return Path.GetFileName(path) ?? path; } catch { return path; }
        }
    }
}
