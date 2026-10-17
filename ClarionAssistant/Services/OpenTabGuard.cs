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
    ///   - no IDE could be asked .............. write as before, plus one line saying the tabs could not be checked.
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
        public static Func<IList<string>, TabReload> LocalReload;

        /// <summary>editor_tab_sync's optional second spelling of file_path.</summary>
        public const string AlsoPathArg = "also_path";

        /// <summary>The distinct, non-empty spellings, <paramref name="primary"/> first.</summary>
        public static List<string> Spellings(string primary, string alsoPath)
        {
            var list = new List<string>();
            foreach (var p in new[] { primary, alsoPath })
            {
                if (string.IsNullOrEmpty(p)) continue;
                bool dup = false;
                foreach (var q in list) if (string.Equals(q.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) dup = true;
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
            bool anyReached = false;
            var dirtyNative = new List<TabProbe>();

            if (probes != null)
            {
                foreach (var p in probes)
                {
                    if (p == null) continue;
                    if (!p.Reachable)
                    {
                        unreachable.Add((string.IsNullOrEmpty(p.Ide) ? "" : p.Ide + ": ") + (p.Reason ?? "no answer"));
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
            if (d.Refusal != null) return d.Refusal;

            string result = write();
            if (result == null) result = "";
            if (result.StartsWith("Error", StringComparison.Ordinal)) return result;   // nothing new on disk: nothing to reload

            var lines = new List<string>();
            foreach (var tab in d.ReloadAfter) lines.Add(ReloadNote(spellings, tab));
            lines.AddRange(d.Notes);
            if (lines.Count == 0) return result;
            return result + "\n" + string.Join("\n", lines.ToArray());
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
                return a;
            }
            a["kind"] = p.Kind ?? KindNone;
            a["dirty"] = p.Dirty;
            return a;
        }

        public static TabProbe CheckFromWire(Dictionary<string, object> a, string ide)
        {
            object o;
            if (a == null) return new TabProbe { Ide = ide, Reachable = false, Reason = "the IDE's answer was not JSON" };
            if (a.TryGetValue("error", out o) && o != null)
                return new TabProbe { Ide = ide, Reachable = false, Reason = "the IDE: " + o };
            string kind = a.TryGetValue("kind", out o) ? o as string : null;
            if (kind != KindNone && kind != KindNative && kind != KindCaEditor)
                return new TabProbe { Ide = ide, Reachable = false, Reason = "the IDE gave an unknown tab kind '" + (kind ?? "") + "'" };
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
