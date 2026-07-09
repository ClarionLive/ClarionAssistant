using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.TextEditor;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Embeditor source access for the CA (Modern/Monaco) Embeditor.
    ///
    /// History: this class once injected LSP-backed code completion (Ctrl+Space),
    /// hover tooltips, and diagnostic squiggles into the NATIVE Clarion embeditor
    /// (ICSharpCode.TextEditor). That experiment was removed — the native embeditor
    /// now uses its own built-in completion. What remains is the read-side support
    /// the CA Embeditor relies on: locate the active embeditor and pull its assembled
    /// source + editable embed-slot ranges. The LSP itself still backs the chat MCP
    /// tools and the CA Embeditor's own (Monaco) completion.
    ///
    /// Verified facts this relies on (all reflected from the live IDE, do not assume):
    ///   - embeditor text surface = ICSharpCode.TextEditor (ClaGenEditor : TextEditorControl)
    ///   - read-only generated zones enforced by Document.CustomLineManager = PweeLineManager
    /// </summary>
    public static class EmbeditorCompletionService
    {
        private const BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// Hook to start the LSP in the background when an embeditor consumer finds no active
        /// client (self-heal). Set by the addin at startup, e.g.
        /// () =&gt; toolRegistry.EnsureLspRunningInBackground(). Fire-and-forget + idempotent,
        /// so invoking it on each attempt is safe. Used by the CA/Modern Embeditor.
        /// </summary>
        internal static Action LspStarter;

        /// <summary>
        /// Path B (M1): returns the live assembled source of the active ClaGenEditor plus the map of
        /// editable embed-slot line ranges. Returns false if no embeditor is open (<paramref name="error"/>
        /// then carries the reason). <paramref name="text"/> is the full generated buffer (read-only zones
        /// and embed slots together). <paramref name="editableRanges"/> holds 1-based, inclusive
        /// [startLine, endLine] pairs for the writable embed slots, aligned to <paramref name="text"/> —
        /// everything outside them is generated/read-only. Mirrors the CustomLines walk in
        /// AppTreeService.GetEmbeditorSource.
        /// </summary>
        public static bool TryGetActiveEmbeditorSource(out string title, out string text,
            out List<int[]> editableRanges, out string error)
        {
            title = null;
            text = null;
            editableRanges = new List<int[]>();
            error = null;

            var sb = new StringBuilder();
            var control = GetActiveEmbeditorControl(sb);
            if (control == null)
            {
                error = sb.ToString().Trim();
                if (string.IsNullOrEmpty(error)) error = "No embeditor is currently open.";
                return false;
            }
            var doc = control.Document;
            if (doc == null) { error = "Embeditor control has no document."; return false; }
            try { title = Path.GetFileName(control.FileName); } catch { }
            if (string.IsNullOrEmpty(title)) title = "Embeditor";
            text = doc.TextContent;

            // Editable embed slots: walk PweeLineManager.CustomLines. Each CustomPweeLine exposes
            // ReadOnly / StartLineNr / EndLineNr as FIELDS (not properties). Editable slot ⟺
            // ReadOnly == false (PweeLineManager enforces read-only on generated lines). Collect the
            // 0-based [Start,End] of every editable line, then merge adjacent/overlapping into clean
            // 1-based inclusive ranges. Non-fatal on failure (view still renders).
            try
            {
                var lineManager = GetProp(doc, "CustomLineManager");
                var customLines = (lineManager != null) ? GetProp(lineManager, "CustomLines") as IEnumerable : null;
                if (customLines != null)
                {
                    var raw = new List<int[]>();
                    foreach (var cl in customLines)
                    {
                        if (cl == null) continue;
                        if (!(GetField(cl, "ReadOnly") is bool ro) || ro) continue; // editable = not read-only
                        if (!(GetField(cl, "StartLineNr") is int s)) continue;
                        if (!(GetField(cl, "EndLineNr") is int e)) continue;
                        if (e < s) e = s;
                        raw.Add(new[] { s, e }); // 0-based inclusive
                    }
                    editableRanges = MergeRanges(raw); // → 1-based inclusive, merged
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[EmbeditorCompletion] editable-range extraction failed: " + ex.Message);
            }

            return true;
        }

        /// <summary>Return the 1-based line the native embeditor caret is on, or 0 on failure.</summary>
        public static int GetNativeCaretLine()
        {
            try
            {
                var control = GetActiveEmbeditorControl(new StringBuilder());
                if (control == null) return 0;
                return control.ActiveTextAreaControl.Caret.Line + 1; // ICSharpCode caret is 0-based
            }
            catch { return 0; }
        }

        /// <summary>
        /// Reflection-based locate of the embeditor's ICSharpCode TextEditorControl,
        /// then a typed cast. Mirrors AppTreeService.GetClaGenEditor()'s search.
        /// </summary>
        private static TextEditorControl GetActiveEmbeditorControl(StringBuilder sb)
        {
            var workbench = WorkbenchSingleton.Workbench;
            if (workbench == null) { sb.AppendLine("Workbench is null."); return null; }

            object FindClaGen(object vc)
            {
                if (vc == null) return null;
                string n = vc.GetType().Name;
                if (n == "ClaGenEditor" || n.Contains("GenEditor")) return vc;
                var sec = GetProp(vc, "SecondaryViewContents");
                if (sec is IEnumerable views)
                    foreach (var v in views)
                    {
                        string vn = v.GetType().Name;
                        if (vn == "ClaGenEditor" || vn.Contains("GenEditor")) return v;
                    }
                return null;
            }

            object editor = null;
            var activeWindow = GetProp(workbench, "ActiveWorkbenchWindow");
            if (activeWindow != null)
                editor = FindClaGen(GetProp(activeWindow, "ViewContent")
                                    ?? GetProp(activeWindow, "ActiveViewContent"));

            if (editor == null)
            {
                var windows = GetProp(workbench, "WorkbenchWindowCollection")
                              ?? GetProp(workbench, "ViewContentCollection");
                if (windows is IEnumerable all)
                    foreach (var w in all)
                    {
                        editor = FindClaGen(GetProp(w, "ViewContent") ?? GetProp(w, "ActiveViewContent"));
                        if (editor != null) break;
                    }
            }

            if (editor == null) { sb.AppendLine("No ClaGenEditor found in any window."); return null; }

            var tec = GetProp(editor, "TextEditorControl") as TextEditorControl;
            if (tec == null)
                sb.AppendLine("ClaGenEditor.TextEditorControl was not a TextEditorControl (got: "
                              + (GetProp(editor, "TextEditorControl")?.GetType().FullName ?? "null") + ").");
            return tec;
        }

        /// <summary>Reads an instance field by name (CustomPweeLine exposes ReadOnly/StartLineNr/EndLineNr as fields).</summary>
        private static object GetField(object obj, string name)
        {
            if (obj == null) return null;
            try { var f = obj.GetType().GetField(name, AllInstance); return f != null ? f.GetValue(obj) : null; }
            catch { return null; }
        }

        /// <summary>
        /// Sorts and merges 0-based inclusive [start,end] line ranges (merging when adjacent or
        /// overlapping), returning 1-based inclusive ranges suitable for Monaco line decorations.
        /// </summary>
        private static List<int[]> MergeRanges(List<int[]> raw)
        {
            var result = new List<int[]>();
            if (raw == null || raw.Count == 0) return result;
            raw.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]));
            int curS = raw[0][0], curE = raw[0][1];
            for (int i = 1; i < raw.Count; i++)
            {
                int s = raw[i][0], e = raw[i][1];
                if (s <= curE + 1) { if (e > curE) curE = e; }   // adjacent or overlapping → extend
                else { result.Add(new[] { curS + 1, curE + 1 }); curS = s; curE = e; }
            }
            result.Add(new[] { curS + 1, curE + 1 });
            return result;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var p = obj.GetType().GetProperty(name, AllInstance);
                return (p != null && p.GetIndexParameters().Length == 0) ? p.GetValue(obj, null) : null;
            }
            catch { return null; }
        }
    }
}
