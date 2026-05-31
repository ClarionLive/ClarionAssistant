using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.TextEditor;
using ICSharpCode.TextEditor.Document;
using ICSharpCode.TextEditor.Gui.CompletionWindow;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Modern Embeditor — Path A, Step 4: REAL code completion in the live Clarion
    /// embeditor (ICSharpCode.TextEditor).
    ///
    /// The POC (step 3) proved we can attach our own ICompletionDataProvider and pop
    /// a completion window. This replaces the two stub items with a provider-MERGE
    /// over pluggable sources so each can evolve independently:
    ///   - LspCompletionSource  — REAL: textDocument/completion from the live Clarion
    ///       language server (LspClient.Active.GetCompletion). Supplies built-in
    ///       functions, data types, attributes, and controls (context-free in v1).
    ///   - CodeGraphCompletionSource — REAL (guarded): symbol names from the indexed
    ///       ClarionLib.codegraph.db next to the addin (procedures/classes/variables,
    ///       which also includes indexed EQUATEs). Degrades to empty on any failure.
    ///   - EquatesCompletionSource — documented stub (empty for now): equate families
    ///       currently arrive via the LSP attributes + the CodeGraph library DB; a
    ///       dedicated EVENT:/PROP:/COLOR: source can be layered in here later.
    ///
    /// Merge → dedupe (first source wins, LSP prioritised) → per-kind icon → the
    /// ICSharpCode completion list. A non-null ImageList is mandatory (a null one
    /// NPEs in CodeCompletionListView.get_ItemHeight() — POC lesson).
    ///
    /// See docs/ModernEmbeditor-PathA.md for the full design + build log.
    ///
    /// Verified facts this relies on (all reflected from the live IDE, do not assume):
    ///   - embeditor text surface = ICSharpCode.TextEditor (ClaGenTextAreaControl : TextEditorControl)
    ///   - read-only generated zones enforced by Document.CustomLineManager = PweeLineManager
    ///   - CodeCompletionWindow.ShowCompletionWindow(Form, TextEditorControl, string,
    ///       ICompletionDataProvider, char, CompletionOptions) — note the trailing
    ///       CompletionOptions (a [Flags] enum) param is Clarion-specific.
    /// </summary>
    public static class EmbeditorCompletionService
    {
        private const BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>Hard cap on items handed to the completion window (the user filters by typing).</summary>
        private const int MaxItems = 4000;

        /// <summary>
        /// Timeout for the synchronous LSP completion call. It runs on the UI thread,
        /// so this bounds the worst-case freeze. The server caches its context-free set
        /// after the first request, so warm calls return well under this.
        /// </summary>
        private const int LspTimeoutMs = 1200;

        // Drops re-entrant LSP completion calls so repeated/held Ctrl+Space can't stack
        // multiple synchronous UI-thread waits. 0 = idle, 1 = a call is in flight.
        private static int _lspInFlight;

        // Last LSP-source outcome, surfaced in the completion-test result file for diagnosis.
        internal static string LastLspDiag = "(not run)";

        // Hook to start the LSP when completion finds no active client (self-heal). Set by
        // the addin at startup, e.g. () => toolRegistry.EnsureLspRunningInBackground().
        // Fire-and-forget + idempotent, so calling it on each completion attempt is safe.
        internal static Action LspStarter;

        /// <summary>
        /// Fixed result-file path so the operator (in C12) and the assistant
        /// (reading from elsewhere) agree on where output lands.
        /// </summary>
        public static string ResultFilePath =>
            Path.Combine(Path.GetTempPath(), "embeditor-completion-test.txt");

        private const int WM_KEYDOWN = 0x0100;

        // Single app-wide message filter that triggers completion on Ctrl+Space. Installed
        // once on the UI thread (see InstallCtrlSpaceTrigger). A global filter is used
        // instead of a per-editor KeyDown hook because ICSharpCode/Clarion consume Ctrl+Space
        // in their own command routing before a WinForms KeyDown event fires — the filter
        // sees WM_KEYDOWN first. Being global, it also needs no per-editor arming.
        private static IMessageFilter _ctrlSpaceFilter;

        /// <summary>
        /// Manual hook (toolbar "Completion Test" button). Finds the active embeditor,
        /// shows the REAL completion window, wires Ctrl+Space for that editor, and
        /// writes a human-readable result (with per-source counts) to
        /// <see cref="ResultFilePath"/> for verification from another IDE instance.
        /// </summary>
        public static string RunCompletionTest()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Modern Embeditor — Completion (Step 4: real data) ===");
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                var editorControl = GetActiveEmbeditorControl(sb);
                if (editorControl == null)
                {
                    sb.AppendLine("RESULT: FAIL — no active embeditor TextEditorControl found.");
                    sb.AppendLine("Open a procedure embeditor and make sure its Source tab is focused, then retry.");
                    return Finish(sb);
                }

                sb.AppendLine("Editor control: " + editorControl.GetType().FullName);

                var textArea = editorControl.ActiveTextAreaControl != null
                    ? editorControl.ActiveTextAreaControl.TextArea
                    : null;
                if (textArea == null)
                {
                    sb.AppendLine("RESULT: FAIL — ActiveTextAreaControl.TextArea is null.");
                    return Finish(sb);
                }

                int caretLine = textArea.Caret != null ? textArea.Caret.Line : 0;
                bool readOnlyHere = textArea.IsReadOnly(caretLine);
                sb.AppendLine("Caret line(0-based): " + caretLine + "  col: " +
                              (textArea.Caret != null ? textArea.Caret.Column : 0) +
                              "  IsReadOnly(line): " + readOnlyHere);
                if (readOnlyHere)
                {
                    sb.AppendLine("NOTE: caret is in a read-only (generated) zone. Move the caret into an");
                    sb.AppendLine("editable embed slot for a representative test — continuing anyway to test the popup.");
                }

                // Report LSP availability up front — it's the primary source.
                var lsp = LspClient.Active;
                sb.AppendLine("LSP: " + (lsp == null ? "no active client" :
                              (lsp.IsRunning ? "running" : "present but not running")));

                var provider = new AggregatingCompletionDataProvider();
                var window = ShowCompletion(editorControl, provider);

                // Ensure the global Ctrl+Space trigger is installed (idempotent).
                InstallCtrlSpaceTrigger();

                // Diagnostics snapshot (squiggle path debugging): force a sync, give the
                // server a moment to publish, then report cached diagnostics + rendered markers.
                try
                {
                    var dclient = LspClient.Active;
                    string dfile = ToLspClwPath(editorControl.FileName);
                    sb.AppendLine("--- diagnostics ---");
                    if (dclient == null || !dclient.IsRunning)
                    {
                        sb.AppendLine("  (no active LSP client)");
                    }
                    else
                    {
                        dclient.EnsureBufferSynced(dfile, textArea.Document.TextContent);
                        System.Threading.Thread.Sleep(1000); // let the server validate+publish
                        var diags = dclient.GetCachedDiagnostics(dfile);
                        sb.AppendLine("  uri: " + dfile);
                        sb.AppendLine("  cached diagnostics: " + (diags == null ? "null (none published yet)" : diags.Count.ToString()));
                        if (diags != null)
                        {
                            int n = 0;
                            foreach (var d in diags)
                            {
                                if (n++ >= 10) break;
                                sb.AppendLine("    sev=" + d.Severity + " @" + d.Line + ":" + d.Character +
                                              "-" + d.EndLine + ":" + d.EndCharacter + " " + d.Message);
                            }
                        }
                        EnsureDiagnosticsRendered();
                        List<TextMarker> mk;
                        sb.AppendLine("  rendered markers: " + (_diagMarkers.TryGetValue(textArea, out mk) ? mk.Count : 0));

                        // Did the server EVER publish, and under which URIs? (uri-key mismatch check)
                        var dbg = dclient.GetDebugStatus();
                        if (dbg != null)
                        {
                            object ncObj;
                            if (dbg.TryGetValue("notificationCounts", out ncObj) && ncObj is Dictionary<string, object>)
                            {
                                var parts = new List<string>();
                                foreach (var kv in (Dictionary<string, object>)ncObj) parts.Add(kv.Key + "=" + kv.Value);
                                sb.AppendLine("  server notifications: " + (parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "(none)"));
                            }
                            object dcObj;
                            if (dbg.TryGetValue("diagnosticsCache", out dcObj) && dcObj is List<Dictionary<string, object>>)
                            {
                                var dcs = (List<Dictionary<string, object>>)dcObj;
                                sb.AppendLine("  diag cache entries: " + dcs.Count);
                                foreach (var e in dcs)
                                    sb.AppendLine("    uri=" + (e.ContainsKey("uri") ? e["uri"] : "?") +
                                                  " published=" + (e.ContainsKey("wasPublished") ? e["wasPublished"] : "?") +
                                                  " count=" + (e.ContainsKey("entryCount") ? e["entryCount"] : "?"));
                            }
                        }
                    }
                }
                catch (Exception dex) { sb.AppendLine("  diag dump error: " + dex.Message); }

                sb.AppendLine("--- source contribution (raw, pre-dedupe) ---");
                foreach (var kv in provider.LastSourceCounts)
                    sb.AppendLine("  " + kv.Key + ": " + kv.Value);
                sb.AppendLine("  [lsp diag] " + LastLspDiag);
                sb.AppendLine("Merged (deduped) items shown: " + provider.LastMergedCount);
                sb.AppendLine("PreSelection (caret prefix): '" + (provider.PreSelection ?? "(none)") + "'");
                sb.AppendLine("GenerateCompletionData calls: " + provider.GenerateCalls);

                if (window == null)
                {
                    sb.AppendLine("RESULT: PARTIAL — ShowCompletionWindow returned null " +
                                  "(no items, or the window self-closed).");
                    return Finish(sb);
                }

                sb.AppendLine("Completion window: " + window.GetType().FullName + "  Visible=" + window.Visible);
                if (provider.LastMergedCount > 0)
                {
                    sb.AppendLine("RESULT: SUCCESS — completion window shown with " +
                                  provider.LastMergedCount + " REAL items.");
                    sb.AppendLine("First few: " + string.Join(", ", provider.LastSampleLabels));
                }
                else
                {
                    sb.AppendLine("RESULT: PARTIAL — window shown but 0 items merged. " +
                                  "Check that the LSP server is running and rebuilt with completion support.");
                }
                sb.AppendLine("Press Ctrl+Space in an editable slot to trigger completion directly.");
            }
            catch (Exception ex)
            {
                sb.AppendLine("RESULT: EXCEPTION — " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                    sb.AppendLine("  Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                sb.AppendLine(ex.StackTrace);
            }
            return Finish(sb);
        }

        /// <summary>
        /// Shows the completion window for the given editor using our aggregating
        /// provider. firstChar='\0' => not triggered by a typed char.
        /// </summary>
        private static CodeCompletionWindow ShowCompletion(TextEditorControl editorControl,
                                                           AggregatingCompletionDataProvider provider)
        {
            var parentForm = editorControl.FindForm()
                             ?? (WorkbenchSingleton.Workbench as Form);

            return CodeCompletionWindow.ShowCompletionWindow(
                parentForm,
                editorControl,
                editorControl.FileName ?? "embeditor.clw",
                provider,
                '\0',
                CodeCompletionWindow.CompletionOptions.FilterListOnTyping);
        }

        /// <summary>
        /// Installs the app-wide Ctrl+Space completion trigger (idempotent). Must be called
        /// on the UI thread — e.g. from the addin's startup. Once installed, Ctrl+Space pops
        /// completion in ANY embeditor text area (split panes included) without per-editor
        /// wiring, because the filter checks focus at keypress time.
        /// </summary>
        public static void InstallCtrlSpaceTrigger()
        {
            if (_ctrlSpaceFilter != null) return;
            _ctrlSpaceFilter = new CtrlSpaceMessageFilter();
            Application.AddMessageFilter(_ctrlSpaceFilter);
        }

        // Text areas that already have an LSP hover handler. UI-thread only.
        private static readonly HashSet<TextArea> _hoverAttached = new HashSet<TextArea>();

        /// <summary>
        /// Attaches an LSP hover tooltip handler to the active embeditor's text area, if not
        /// already attached (idempotent). Call periodically (e.g. from the LSP UI timer) so
        /// hover arms shortly after any embeditor opens — there is no single embeditor-open
        /// event, and ToolTipRequest is a per-TextArea event (unlike the global Ctrl+Space filter).
        /// </summary>
        public static void EnsureHoverHandlerAttached()
        {
            try
            {
                var editor = GetActiveEmbeditorControl(new StringBuilder());
                if (editor == null) return;
                var atc = editor.ActiveTextAreaControl;
                var ta = atc != null ? atc.TextArea : null;
                if (ta == null || _hoverAttached.Contains(ta)) return;

                ta.ToolTipRequest += (s, e) => HandleToolTipRequest(editor, ta, e);
                ta.Disposed += (s, e) => _hoverAttached.Remove(ta);
                _hoverAttached.Add(ta);
            }
            catch { /* never let hover wiring break the editor */ }
        }

        private static void HandleToolTipRequest(TextEditorControl editor, TextArea ta, ToolTipRequestEventArgs e)
        {
            try
            {
                if (!e.InDocument) return;                 // not over document text
                var client = LspClient.Active;
                if (client == null || !client.IsRunning) return;

                string fileName = ToLspClwPath(editor.FileName);
                string buffer = ta.Document != null ? ta.Document.TextContent : null;
                int line = e.LogicalPosition.Line;         // 0-based, matches LSP
                int col = e.LogicalPosition.Column;

                var response = client.GetHover(fileName, line, col, buffer);
                string text = ExtractHoverText(response);
                if (!string.IsNullOrEmpty(text))
                    e.ShowToolTip(text);
            }
            catch { /* swallow — a failed hover must never disrupt editing */ }
        }

        /// <summary>Pulls a plain-text hover string out of an LSP textDocument/hover response.</summary>
        private static string ExtractHoverText(System.Collections.Generic.Dictionary<string, object> response)
        {
            if (response == null) return null;
            var result = response.ContainsKey("result") ? response["result"] as System.Collections.Generic.Dictionary<string, object> : null;
            if (result == null || !result.ContainsKey("contents")) return null;
            string raw = MarkupToText(result["contents"]);
            return CleanHoverMarkdown(raw);
        }

        private static string MarkupToText(object contents)
        {
            if (contents == null) return null;
            var s = contents as string;
            if (s != null) return s;

            var d = contents as System.Collections.Generic.Dictionary<string, object>;
            if (d != null)
                return d.ContainsKey("value") ? d["value"] as string : null;

            var arr = contents as System.Collections.ArrayList;
            if (arr != null)
            {
                var sb = new StringBuilder();
                foreach (var item in arr)
                {
                    string t = MarkupToText(item);
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (sb.Length > 0) sb.Append("\n");
                        sb.Append(t);
                    }
                }
                return sb.ToString();
            }
            return null;
        }

        // The server returns markdown; the ICSharpCode tooltip is plain text, so drop code
        // fences (``` / ```clarion) for readability. Keep everything else as-is.
        private static string CleanHoverMarkdown(string md)
        {
            if (string.IsNullOrEmpty(md)) return md;
            var lines = md.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("```")) continue;
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(line);
            }
            return sb.ToString().Trim();
        }

        // ─────────────────────── diagnostics (squiggles) ───────────────────────

        // The WaveLine markers we've added per text area, so we can clear ours without
        // touching Clarion's own markers/bookmarks. UI-thread only.
        private static readonly Dictionary<TextArea, List<TextMarker>> _diagMarkers =
            new Dictionary<TextArea, List<TextMarker>>();

        /// <summary>
        /// Syncs the active embeditor's buffer to the LSP (so it (re)validates), then renders
        /// the server's published diagnostics as WaveLine squiggles. Call periodically (LSP UI
        /// timer): the publish is async, so an edit's diagnostics land a tick or two later.
        /// </summary>
        public static void EnsureDiagnosticsRendered()
        {
            try
            {
                var editor = GetActiveEmbeditorControl(new StringBuilder());
                if (editor == null) return;
                var atc = editor.ActiveTextAreaControl;
                var ta = atc != null ? atc.TextArea : null;
                if (ta == null || ta.Document == null) return;

                var client = LspClient.Active;
                if (client == null || !client.IsRunning) return;

                string fileName = ToLspClwPath(editor.FileName);
                string buffer = ta.Document.TextContent;

                // Push current buffer → server validates → publishes diagnostics (no-op if unchanged).
                client.EnsureBufferSynced(fileName, buffer);

                // Render whatever the server has published so far for this file.
                RenderDiagnostics(ta, client.GetCachedDiagnostics(fileName));
            }
            catch { /* never let diagnostics rendering disrupt the editor */ }
        }

        private static void RenderDiagnostics(TextArea ta, List<LspClient.DiagnosticEntry> diags)
        {
            var doc = ta.Document;
            var strategy = doc.MarkerStrategy;
            if (strategy == null) return;

            // Clear only OUR previous markers.
            List<TextMarker> prev;
            if (_diagMarkers.TryGetValue(ta, out prev))
            {
                foreach (var m in prev) { try { strategy.RemoveMarker(m); } catch { } }
                prev.Clear();
            }
            else
            {
                prev = new List<TextMarker>();
                _diagMarkers[ta] = prev;
                ta.Disposed += (s, e) => _diagMarkers.Remove(ta);
            }

            if (diags != null && diags.Count > 0)
            {
                int totalLines = doc.TotalNumberOfLines;
                foreach (var d in diags)
                {
                    try
                    {
                        if (d.Line < 0 || d.Line >= totalLines) continue;
                        int startOffset = doc.PositionToOffset(new TextLocation(Math.Max(0, d.Character), d.Line));

                        int endOffset;
                        bool hasEnd = d.EndLine > d.Line || (d.EndLine == d.Line && d.EndCharacter > d.Character);
                        if (hasEnd && d.EndLine >= 0 && d.EndLine < totalLines)
                            endOffset = doc.PositionToOffset(new TextLocation(Math.Max(0, d.EndCharacter), d.EndLine));
                        else
                            endOffset = startOffset + 1;

                        int length = Math.Max(1, endOffset - startOffset);
                        var marker = new TextMarker(startOffset, length, TextMarkerType.WaveLine, SeverityColor(d.Severity));
                        marker.ToolTip = d.Message;
                        strategy.AddMarker(marker);
                        prev.Add(marker);
                    }
                    catch { /* skip a single bad range, keep the rest */ }
                }
            }

            // Repaint so marker changes show.
            doc.RequestUpdate(new TextAreaUpdate(TextAreaUpdateType.WholeTextArea));
            doc.CommitUpdate();
        }

        private static Color SeverityColor(int severity)
        {
            switch (severity)
            {
                case 1: return Color.Red;        // Error
                case 2: return Color.Goldenrod;  // Warning
                case 3: return Color.SteelBlue;  // Information
                default: return Color.Gray;      // Hint
            }
        }

        private static bool IsSelfOrAncestor(Control ancestor, Control node)
        {
            for (var c = node; c != null; c = c.Parent)
                if (ReferenceEquals(c, ancestor)) return true;
            return false;
        }

        /// <summary>
        /// Catches Ctrl+Space at the message-pump level (before ICSharpCode/Clarion command
        /// routing can swallow it) and pops completion when an embeditor text area is focused
        /// and the caret is in an editable zone. Cheap early-outs keep non-matching keystrokes
        /// essentially free; the heavier embeditor lookup only runs on an actual Ctrl+Space.
        /// </summary>
        private sealed class CtrlSpaceMessageFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_KEYDOWN) return false;
                if (m.WParam.ToInt32() != (int)Keys.Space) return false;
                if ((Control.ModifierKeys & Keys.Control) != Keys.Control) return false;

                try
                {
                    var editor = GetActiveEmbeditorControl(new StringBuilder());
                    if (editor == null) return false;            // not in an embeditor
                    var atc = editor.ActiveTextAreaControl;
                    var ta = atc != null ? atc.TextArea : null;
                    if (ta == null) return false;

                    var focused = Control.FromHandle(m.HWnd);
                    if (focused == null || (!IsSelfOrAncestor(editor, focused) && focused != ta))
                        return false;                            // focus isn't in this embeditor

                    int line = ta.Caret != null ? ta.Caret.Line : 0;
                    if (ta.IsReadOnly(line)) return false;       // generated zone — don't hijack

                    ShowCompletion(editor, new AggregatingCompletionDataProvider());
                    return true;                                 // consume so nothing else handles it
                }
                catch { return false; }
            }
        }

        private static string Finish(StringBuilder sb)
        {
            string text = sb.ToString();
            try { File.WriteAllText(ResultFilePath, text); }
            catch { /* best effort */ }
            return text;
        }

        /// <summary>
        /// Reflection-based locate of the embeditor's ICSharpCode TextEditorControl,
        /// then a typed cast. Mirrors AppTreeService.GetClaGenEditor()'s search.
        /// </summary>
        /// <summary>
        /// Path B (M1): returns the live assembled source of the active ClaGenEditor plus the map of
        /// editable embed-slot line ranges. Returns false if no embeditor is open (<paramref name="error"/>
        /// then carries the reason). <paramref name="text"/> is the full generated buffer (read-only zones
        /// and embed slots together). <paramref name="editableRanges"/> holds 1-based, inclusive
        /// [startLine, endLine] pairs for the writable embed slots, aligned to <paramref name="text"/> —
        /// everything outside them is generated/read-only. Mirrors the CustomLines walk in
        /// AppTreeService.GetEmbeditorSource; reuses the same embeditor-discovery as completion.
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

        /// <summary>
        /// The embeditor's virtual filename ends in <c>.appclw</c>, which the LSP's diagnostics
        /// filter rejects (it validates only .clw/.inc/.equ). Present the buffer to the server
        /// under a <c>.clw</c> URI so it gets validated. Used consistently for completion,
        /// hover, and diagnostics so the (URI-keyed) diagnostics line up with what we sync.
        /// </summary>
        private static string ToLspClwPath(string editorFileName)
        {
            string name = string.IsNullOrEmpty(editorFileName) ? "embeditor.clw" : editorFileName;
            try { return Path.ChangeExtension(name, ".clw"); }
            catch { return "embeditor.clw"; }
        }

        // ───────────────────────── merge model ─────────────────────────

        /// <summary>A unified completion item produced by a source, before mapping to ICSharpCode.</summary>
        private sealed class Item
        {
            public string Label;
            public string Detail;
            public string Documentation;
            public int Kind;       // LSP CompletionItemKind, 0 = unspecified
            public string Source;  // "lsp" / "codegraph" / "equate"
        }

        private sealed class CompletionContext
        {
            public string FilePath;
            public int Line;       // 0-based (matches LSP and ICSharpCode caret)
            public int Character;  // 0-based
            public string BufferText; // live embeditor buffer, synced to the LSP for scope analysis
        }

        private interface ICompletionSource
        {
            string Name { get; }
            IEnumerable<Item> GetItems(CompletionContext ctx);
        }

        /// <summary>REAL — pulls textDocument/completion from the live Clarion LSP.</summary>
        private sealed class LspCompletionSource : ICompletionSource
        {
            public string Name { get { return "lsp"; } }

            public IEnumerable<Item> GetItems(CompletionContext ctx)
            {
                var items = new List<Item>();
                var client = LspClient.Active;
                if (client == null)
                {
                    // Self-heal: kick off LSP startup so the next invocation has it.
                    LastLspDiag = "LspClient.Active == null — started LSP in background, retry shortly";
                    try { LspStarter?.Invoke(); } catch { }
                    return items;
                }
                if (!client.IsRunning) { LastLspDiag = "client present but not running"; return items; }

                // Re-entrancy guard — if a completion request is already blocking the UI
                // thread, don't queue another behind it (a held/repeated Ctrl+Space would
                // otherwise stack multiple synchronous waits).
                if (Interlocked.CompareExchange(ref _lspInFlight, 1, 0) != 0)
                {
                    LastLspDiag = "skipped (another completion already in flight)";
                    return items;
                }
                try
                {
                    var result = client.GetCompletion(ctx.FilePath, ctx.Line, ctx.Character, LspTimeoutMs, ctx.BufferText);
                    foreach (var ci in result)
                    {
                        items.Add(new Item
                        {
                            Label = ci.Label,
                            Detail = ci.Detail,
                            Documentation = ci.Documentation,
                            Kind = ci.Kind,
                            Source = "lsp"
                        });
                    }
                    LastLspDiag = client.LastCompletionDiagnostic + " | mapped=" + items.Count;
                }
                catch (Exception ex)
                {
                    LastLspDiag = "EXCEPTION: " + ex.GetType().Name + ": " + ex.Message;
                }
                finally { Interlocked.Exchange(ref _lspInFlight, 0); }
                return items;
            }
        }

        /// <summary>
        /// REAL (guarded) — symbol names from the indexed ClarionLib.codegraph.db that
        /// ships next to the addin. Includes procedures/classes/variables and indexed
        /// EQUATEs. Read-only connection; any failure degrades to an empty list.
        /// </summary>
        private sealed class CodeGraphCompletionSource : ICompletionSource
        {
            public string Name { get { return "codegraph"; } }

            public IEnumerable<Item> GetItems(CompletionContext ctx)
            {
                var items = new List<Item>();

                string dbPath;
                try { dbPath = LibraryIndexer.GetDefaultDbPath(); }
                catch { return items; }
                if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return items;

                try
                {
                    using (var conn = new SQLiteConnection(
                        "Data Source=" + dbPath + ";Version=3;Read Only=True;"))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText =
                                "SELECT DISTINCT name, type, params, return_type FROM symbols " +
                                "WHERE name IS NOT NULL AND name <> '' LIMIT 6000";
                            using (var r = cmd.ExecuteReader())
                            {
                                while (r.Read())
                                {
                                    string name = r["name"] as string;
                                    if (string.IsNullOrEmpty(name)) continue;
                                    // Bound field sizes — a corrupt/tampered DB shouldn't be
                                    // able to inject huge strings into UI completion objects.
                                    if (name.Length > 256) name = name.Substring(0, 256);
                                    string type = r["type"] as string;
                                    string prms = r["params"] as string;
                                    string ret = r["return_type"] as string;

                                    string detail = BuildCodeGraphDetail(name, type, prms, ret);
                                    if (detail.Length > 512) detail = detail.Substring(0, 512);

                                    items.Add(new Item
                                    {
                                        Label = name,
                                        Kind = MapCodeGraphType(type),
                                        Detail = detail,
                                        Documentation = "From CodeGraph (" + (type ?? "symbol") + ")",
                                        Source = "codegraph"
                                    });
                                }
                            }
                        }
                    }
                }
                catch { /* DB missing/locked/schema drift — degrade to empty */ }
                return items;
            }

            private static int MapCodeGraphType(string type)
            {
                if (string.IsNullOrEmpty(type)) return 1; // Text
                switch (type.ToLowerInvariant())
                {
                    case "procedure":
                    case "function":
                    case "routine":
                    case "method": return 3;  // Function
                    case "class":
                    case "interface": return 7; // Class
                    case "variable":
                    case "field": return 6;     // Variable
                    default: return 1;          // Text
                }
            }

            private static string BuildCodeGraphDetail(string name, string type, string prms, string ret)
            {
                var sb = new StringBuilder(name);
                if (!string.IsNullOrEmpty(prms)) sb.Append("(").Append(prms).Append(")");
                if (!string.IsNullOrEmpty(ret)) sb.Append(" → ").Append(ret);
                if (!string.IsNullOrEmpty(type)) sb.Append("  [").Append(type).Append("]");
                return sb.ToString();
            }
        }

        /// <summary>
        /// Documented stub. Equate families (EVENT:/PROP:/COLOR:/…) currently arrive via
        /// the LSP attribute set and the CodeGraph library DB, so this intentionally
        /// returns nothing to avoid duplicates. A dedicated equate-family source can be
        /// added here later (e.g. grouped by prefix) without touching the merge logic.
        /// </summary>
        private sealed class EquatesCompletionSource : ICompletionSource
        {
            public string Name { get { return "equate"; } }
            public IEnumerable<Item> GetItems(CompletionContext ctx) { return new List<Item>(); }
        }

        // ──────────────────── ICSharpCode provider ────────────────────

        /// <summary>
        /// ICompletionDataProvider that merges all sources. Built fresh per invocation;
        /// its GenerateCompletionData does the source fan-out, dedupe, and mapping.
        /// </summary>
        private sealed class AggregatingCompletionDataProvider : ICompletionDataProvider
        {
            private static readonly ICompletionSource[] Sources =
            {
                new LspCompletionSource(),
                new CodeGraphCompletionSource(),
                new EquatesCompletionSource()
            };

            private readonly ImageList _imageList = CreateImageList();

            public int GenerateCalls { get; private set; }
            public int LastMergedCount { get; private set; }
            public Dictionary<string, int> LastSourceCounts { get; } = new Dictionary<string, int>();
            public List<string> LastSampleLabels { get; } = new List<string>();

            // Image indices (see CreateImageList).
            private const int ImgKeyword = 0;
            private const int ImgFunction = 1;
            private const int ImgType = 2;
            private const int ImgClass = 3;
            private const int ImgVariable = 4;
            private const int ImgProperty = 5;
            private const int ImgDefault = 6;

            private static ImageList CreateImageList()
            {
                // Distinct solid-colour 16x16 chips per category so the demo visibly
                // distinguishes kinds. Real glyphs are a later polish. A non-null
                // ImageList is mandatory (null NPEs in get_ItemHeight()).
                var il = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
                Color[] colors =
                {
                    Color.SteelBlue,   // keyword
                    Color.MediumPurple,// function
                    Color.LightSeaGreen,// type
                    Color.DarkOrange,  // class/control
                    Color.ForestGreen, // variable
                    Color.Goldenrod,   // property/attribute
                    Color.Gray         // default
                };
                foreach (var c in colors)
                {
                    var bmp = new Bitmap(16, 16);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        using (var b = new SolidBrush(c)) g.FillRectangle(b, 2, 2, 12, 12);
                    }
                    il.Images.Add(bmp);
                }
                return il;
            }

            // The partial identifier already typed before the caret when completion was
            // invoked. ICSharpCode uses PreSelection to pre-select/scroll the list to the
            // matching item (so Ctrl+Space after "clas" lands on CLASS instead of the top).
            private string _preSelection;

            public ImageList ImageList { get { return _imageList; } }
            public string PreSelection { get { return _preSelection; } }
            public int DefaultIndex { get { return 0; } }

            public CompletionDataProviderKeyResult ProcessKey(char key)
            {
                if (char.IsLetterOrDigit(key) || key == '_' || key == ':')
                    return CompletionDataProviderKeyResult.NormalKey;
                return CompletionDataProviderKeyResult.InsertionKey;
            }

            public bool InsertAction(ICompletionData data, TextArea textArea, int insertionOffset, char key)
            {
                textArea.Caret.Position = textArea.Document.OffsetToPosition(insertionOffset);
                return data.InsertAction(textArea, key);
            }

            public ICompletionData[] GenerateCompletionData(string fileName, TextArea textArea, char charTyped)
            {
                GenerateCalls++;
                LastSourceCounts.Clear();
                LastSampleLabels.Clear();

                // NOTE: fileName is the editor's (often virtual) FileName, not guaranteed
                // to be a real on-disk path. Fine for v1 context-free completion (the
                // server returns items regardless). Phase-2 context-aware completion must
                // first resolve a real buffer/path before trusting Line/Character.
                var ctx = new CompletionContext
                {
                    FilePath = ToLspClwPath(fileName),
                    Line = textArea != null && textArea.Caret != null ? textArea.Caret.Line : 0,
                    Character = textArea != null && textArea.Caret != null ? textArea.Caret.Column : 0,
                    // The live generated buffer — synced to the LSP so it can resolve in-scope
                    // locals/params at the caret (the buffer isn't on disk).
                    BufferText = (textArea != null && textArea.Document != null) ? textArea.Document.TextContent : null
                };

                // Partial word already typed before the caret → drives PreSelection so the
                // popup lands on the matching item rather than the top of the list.
                _preSelection = ComputeCaretPrefix(textArea);

                // Cross-SOURCE dedupe (first source wins; Sources are ordered LSP >
                // CodeGraph > Equate). The LSP server already deduped its OWN set
                // internally; this pass dedupes across the different sources. Clarion is
                // case-insensitive, so OrdinalIgnoreCase is the correct key comparer.
                var merged = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in Sources)
                {
                    int count = 0;
                    IEnumerable<Item> produced;
                    try { produced = src.GetItems(ctx) ?? Enumerable.Empty<Item>(); }
                    catch { produced = Enumerable.Empty<Item>(); }

                    foreach (var it in produced)
                    {
                        if (it == null || string.IsNullOrEmpty(it.Label)) continue;
                        count++;
                        if (!merged.ContainsKey(it.Label)) merged[it.Label] = it;
                    }
                    LastSourceCounts[src.Name] = count;
                }

                var ordered = merged.Values
                    .OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxItems)
                    .ToList();

                LastMergedCount = ordered.Count;
                LastSampleLabels.AddRange(ordered.Take(8).Select(i => i.Label));

                var result = new ICompletionData[ordered.Count];
                for (int i = 0; i < ordered.Count; i++)
                {
                    var it = ordered[i];
                    result[i] = new DefaultCompletionData(it.Label, BuildDescription(it), MapKindToImage(it.Kind));
                }
                return result;
            }

            // Returns the run of identifier chars immediately before the caret (the word
            // being typed), or null. Clarion identifiers include ':' (EVENT:/PROP:/COLOR:).
            private static string ComputeCaretPrefix(TextArea textArea)
            {
                try
                {
                    if (textArea == null || textArea.Caret == null) return null;
                    var doc = textArea.Document;
                    int line = textArea.Caret.Line;
                    int col = textArea.Caret.Column;
                    var seg = doc.GetLineSegment(line);
                    string lineText = doc.GetText(seg.Offset, seg.Length);
                    int c = Math.Min(col, lineText.Length);
                    int start = c;
                    while (start > 0)
                    {
                        char ch = lineText[start - 1];
                        if (char.IsLetterOrDigit(ch) || ch == '_' || ch == ':') start--;
                        else break;
                    }
                    string prefix = c > start ? lineText.Substring(start, c - start) : null;
                    return string.IsNullOrEmpty(prefix) ? null : prefix;
                }
                catch { return null; }
            }

            private static string BuildDescription(Item it)
            {
                string doc = it.Documentation;
                if (!string.IsNullOrEmpty(doc) && doc.Length > 500) doc = doc.Substring(0, 500) + "…";

                if (string.IsNullOrEmpty(it.Detail)) return doc ?? "";
                if (string.IsNullOrEmpty(doc)) return it.Detail;
                return it.Detail + "\n\n" + doc;
            }

            private static int MapKindToImage(int kind)
            {
                // LSP CompletionItemKind → our image chip.
                switch (kind)
                {
                    case 2:  // Method
                    case 3:  // Function
                        return ImgFunction;
                    case 7:  // Class
                    case 8:  // Interface
                    case 22: // Struct
                        return ImgClass;
                    case 5:  // Field
                    case 6:  // Variable
                        return ImgVariable;
                    case 10: // Property
                        return ImgProperty;
                    case 25: // TypeParameter
                        return ImgType;
                    case 14: // Keyword
                    case 21: // Constant
                        return ImgKeyword;
                    default:
                        return ImgDefault;
                }
            }
        }
    }
}
