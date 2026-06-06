using System;
using System.Collections.Generic;
using ClarionAssistant.Terminal;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Single decision point for "which editor's procedure is the Data pad serving right now?" — the
    /// Modern (Monaco/WebView2) embeditor view OR the native (PWEE) Clarion embeditor. Pad code resolves
    /// one of these and calls through it, staying agnostic to the editor kind.
    ///
    /// Resolution is FOCUS-keyed (proven by the item-0 probe): the native embeditor is "focused" only when
    /// its own ICSharpCode text area is the active surface; a focused Modern tab has no such text area.
    /// Native is checked FIRST because ModernEmbeditorViewContent.ActiveModernView() falls back to the lone
    /// open Modern tab even when it isn't focused — that fallback must not win over a focused native editor.
    ///
    /// For the native path the text area is CAPTURED at resolve time (while focused) and reused afterwards,
    /// because double-clicking the Data pad moves keyboard focus off the embeditor — insert/goto must target
    /// the captured surface, not whatever is "active" at click time.
    /// </summary>
    public sealed class ActiveProcedureContext
    {
        /// <summary>The procedure the focused editor represents.</summary>
        public string ProcedureName { get; private set; }

        /// <summary>True when the focused editor is the native (PWEE) embeditor; false for the Modern view.</summary>
        public bool IsNative { get; private set; }

        private readonly Func<Dictionary<string, object>> _getPadData;
        private readonly Action<string> _insert;
        private readonly Action<string> _gotoRoutine;

        private ActiveProcedureContext(string procedureName, bool isNative,
            Func<Dictionary<string, object>> getPadData, Action<string> insert, Action<string> gotoRoutine)
        {
            ProcedureName = procedureName;
            IsNative = isNative;
            _getPadData = getPadData;
            _insert = insert;
            _gotoRoutine = gotoRoutine;
        }

        /// <summary>Build the Data pad payload for this procedure (same shape for native and Modern).</summary>
        public Dictionary<string, object> GetPadData()
        {
            try { return _getPadData(); }
            catch { return null; }
        }

        /// <summary>Insert text at the focused editor's cursor.</summary>
        public void Insert(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { _insert(text); } catch { }
        }

        /// <summary>Navigate the focused editor to a routine's declaration.</summary>
        public void GotoRoutine(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            try { _gotoRoutine(name); } catch { }
        }

        /// <summary>
        /// Resolve the context for whichever procedure editor is focused, or null when none is.
        /// </summary>
        public static ActiveProcedureContext Resolve()
        {
            // 1. Native (PWEE) embeditor focused? (focus-strict — see class remarks)
            var appTree = new AppTreeService();
            string nativeProc = appTree.GetFocusedNativeEmbeditorProcName();
            if (!string.IsNullOrEmpty(nativeProc))
            {
                var editor = new EditorService();
                object textArea = appTree.GetFocusedNativeTextArea(); // captured while focused
                string proc = nativeProc;
                // Snapshot the buffer NOW, on the (UI) resolve thread — GetPadData runs on a background task and
                // reading an ICSharpCode document off the UI thread is unsafe. The routine list and goto line
                // both come from this one snapshot, so the pad's line numbers and the goto target stay in sync.
                string nativeSource = editor.GetDocumentContent(textArea) ?? "";
                return new ActiveProcedureContext(proc, true,
                    getPadData: () => ModernEmbeditorViewContent.BuildPadData(proc, nativeSource),
                    insert: text =>
                    {
                        editor.InsertTextAtCaret(textArea, text);   // caret ends at the end of the pasted text
                        int end = editor.GetCaretOffset(textArea);  // capture it before focus can reset it
                        editor.FocusTextArea(textArea);             // hand focus back to the embeditor
                        editor.SetCaretOffset(textArea, end);       // re-assert caret at end of paste so the dev keeps typing there
                    },
                    gotoRoutine: name =>
                    {
                        // Find the routine in the LIVE buffer at click time, NOT the resolve-time snapshot: the
                        // snapshot goes stale as the user edits the same procedure (the tick only re-snapshots on a
                        // procedure change), which made goto land a few lines off after inserts. Live read = exact.
                        string live = editor.GetDocumentContent(textArea) ?? nativeSource;
                        int line = FindRoutineLine(live, proc, name);
                        if (line > 0) { editor.GoToLine(textArea, line); editor.FocusTextArea(textArea); }
                    });
            }

            // 2. Modern Embeditor view that is the FOCUSED active document. Focus-strict (no lone-tab fallback):
            // an unfocused background Modern tab must NOT become the pad's action target — that's the no-editor
            // state, handled by the caller clearing _renderCtx.
            var mv = ModernEmbeditorViewContent.FocusedModernView();
            if (mv != null && !string.IsNullOrEmpty(mv.ProcedureName))
            {
                return new ActiveProcedureContext(mv.ProcedureName, false,
                    getPadData: () => mv.GetPadData(),
                    insert: text => mv.InsertAtCursor(text),
                    gotoRoutine: name => mv.GotoRoutine(name));
            }

            return null;
        }

        /// <summary>
        /// Lightweight "which procedure is focused?" for the pad's change-detection tick — returns the active
        /// procedure name (native preferred, then Modern) and whether it's native, WITHOUT snapshotting the
        /// buffer. Resolve() does the heavier full build only when an actual change is detected.
        /// </summary>
        public static string PeekActiveProcedure(out bool isNative)
        {
            isNative = false;
            try
            {
                string nativeProc = new AppTreeService().GetFocusedNativeEmbeditorProcName();
                if (!string.IsNullOrEmpty(nativeProc)) { isNative = true; return nativeProc; }

                var mv = ModernEmbeditorViewContent.FocusedModernView();
                if (mv != null && !string.IsNullOrEmpty(mv.ProcedureName)) return mv.ProcedureName;
            }
            catch { }
            return null;
        }

        // Line of a ROUTINE's declaration in the (raw) buffer — matches the editor's own line numbering so
        // GoToLine lands correctly. Uses the same ParseRoutines the Modern pad uses for its routine list.
        private static int FindRoutineLine(string source, string procName, string routineName)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrWhiteSpace(routineName)) return -1;
            try
            {
                foreach (var r in ClarionAppDataReader.ParseRoutines(source, procName))
                    if (string.Equals(r.Name, routineName, StringComparison.OrdinalIgnoreCase))
                        return r.Line;
            }
            catch { }
            return -1;
        }
    }
}
