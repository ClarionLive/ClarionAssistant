using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>73bd1f03: optional on an <see cref="IEditorOverlayChannel"/>. Names what a successful write changed, in
    /// place of the default "file:line (path)" (the CA Embeditor: "BrowseDepartment line 265 (CA Embeditor)").
    /// Null or empty = the default.</summary>
    public interface IOverlayWriteLabel
    {
        string WriteLabel(int line);
    }

    /// <summary>73bd1f03: optional on an <see cref="IEditorOverlayChannel"/>. Other names a write's file_path may use for
    /// this editor besides its FilePath (the CA Embeditor's FilePath is the .app). A channel without it accepts FilePath
    /// only, exactly as before.</summary>
    public interface IOverlayPathAliases
    {
        /// <summary>Full paths, compared as paths (SamePath) and nothing looser. Null or empty = none.</summary>
        IList<string> AcceptedPaths();
        /// <summary>Bare names (a module file name, a procedure name), matched ONLY when the caller's file_path is itself
        /// a bare name (no directory), so a same-named file in another folder never matches. Null or empty = none.</summary>
        IList<string> AcceptedNames();
    }

    /// <summary>fc420c30: the CA Editor (Monaco overlay) that owns the active file, as the router sees it.</summary>
    public interface IEditorOverlayChannel
    {
        /// <summary>The file the overlay edits.</summary>
        string FilePath { get; }
        /// <summary>The page has loaded the file and answers requests.</summary>
        bool PageReady { get; }
        /// <summary>Ask the page (HostRequestBroker semantics: never on the UI thread; TimeoutException,
        /// HostRequestBroker.RefusedException).</summary>
        Dictionary<string, object> Request(string action, Dictionary<string, object> args, int timeoutMs);
    }

    /// <summary>
    /// fc420c30: routes the MCP editor tools to the CA Editor when one is up for the active file, so Claude's edits land
    /// in the Monaco text John sees (and fire fileState), undo/redo use Monaco's stack, and reads come from it. Before
    /// this, every tool acted on the native ClarionEditor document hidden UNDER the overlay: John saw nothing, and an
    /// overlay save then discarded Claude's edits.
    ///
    /// THREADING. The tools run off the UI thread (RequiresUiThread = false). The router finds the target ON the UI
    /// thread through a bounded marshal (BeginInvokeOnUi + a wait; never Invoke). The native path runs today's
    /// EditorService call the same way, so its behaviour is unchanged. The overlay path waits for the page from the
    /// calling (non-UI) thread, because the page's reply is delivered on the UI thread.
    ///
    /// FAIL CLOSED. With an overlay up, a page that is not ready, does not answer in time, or refuses gives an error.
    /// It never falls back to the native document: that IS the bug. Only "no overlay" goes native.
    /// </summary>
    public sealed class EditorToolRouter
    {
        /// <summary>Finds the CA Editor overlay of the ACTIVE workbench view, or null. Called on the UI thread. The addin
        /// sets it at startup (LspAutostartCommand); null (the standalone server) means native.</summary>
        public static Func<IEditorOverlayChannel> ActiveOverlayResolver;
        /// <summary>The managed id of the IDE's UI thread (set with the resolver): a call already on it runs inline.</summary>
        public static int UiThreadId;
        /// <summary>[editor-route] log sink (monaco-spike.log in the addin).</summary>
        public static Action<string> Log;
        /// <summary>get_open_files: marks tabs whose CA Editor holds unsaved edits (the native list calls them clean).
        /// UI thread. Set by the addin; null = the native list as is.</summary>
        public static Func<List<string>, List<string>> OpenFilesAdjuster;

        /// <summary>Returned by an overlay operation that wants the native call after all (close_file on a clean tab).</summary>
        public static readonly object UseNative = new object();

        public const int ResolveTimeoutMs = 3000;
        public const int NativeTimeoutMs = 30000;
        /// <summary>open_file: how long to wait for the opened file to become the active editor (and its CA Editor ready).</summary>
        public const int OpenActivateTimeoutMs = 15000;
        /// <summary>open_file: when the file is still not active this long after the open, select its tab once more.</summary>
        public const int ReactivateAfterMs = 1500;

        /// <summary>open_file: whether a CA Editor overlay will take this file (MonacoSourceOverlay.Enabled +
        /// CaEditorSettings.SourceAppliesTo). UI thread. Null = never (the standalone server).</summary>
        public static Func<string, bool> OverlayExpectedFor;

        /// <summary>open_file, second level of activation: give the file's tab keyboard focus. UI thread. Set by the addin
        /// (MonacoClarionEditor.FocusTabFor); null = select only.</summary>
        public static Func<string, bool> FocusTab;

        /// <summary>
        /// Bring an open file's workbench window to the front (UI thread): SelectWindow, THEN keyboard focus to its
        /// editor. SelectWindow alone only displays the tab: the IDE's ActiveWorkbenchWindow moves only when a document
        /// takes keyboard focus, and with focus in the terminal it stayed on the previous window (live, combined-1005c:
        /// from the .app view, open_file of an already-open CA Editor tab timed out twice with the .app still active).
        /// </summary>
        public static bool ActivateTab(object window, string path)
        {
            if (window == null) return false;
            try { window.GetType().GetMethod("SelectWindow", Type.EmptyTypes)?.Invoke(window, null); } catch { }
            var focus = FocusTab;
            if (focus != null) { try { focus(path); } catch { } }
            return true;
        }

        /// <summary>How one call is routed: a WRITE names the file (and line) it changed in its result; ExpectedPath
        /// refuses the call unless that file is the active editor.</summary>
        public sealed class RouteOptions
        {
            public bool IsWrite;
            public string ExpectedPath;
            /// <summary>The line a native write touched, when the arguments say (0 = unknown).</summary>
            public int NativeLine;
        }

        private readonly Func<IUiDispatcher> _ui;
        private readonly Func<string> _nativeActivePath;

        /// <param name="nativeActivePath">The native editor's active document path (UI thread), for the active-file
        /// checks when no CA Editor holds it.</param>
        public EditorToolRouter(Func<IUiDispatcher> ui, Func<string> nativeActivePath = null)
        {
            _ui = ui;
            _nativeActivePath = nativeActivePath;
        }

        private sealed class Target { public IEditorOverlayChannel Channel; public string NativePath; }

        /// <summary>
        /// Run tool <paramref name="tool"/>: <paramref name="overlay"/> when a CA Editor is up for the active file, else
        /// <paramref name="native"/> on the UI thread.
        /// </summary>
        public object Run(string tool, Func<object> native, Func<OverlayEditorOps, object> overlay, RouteOptions opts = null)
        {
            opts = opts ?? new RouteOptions();
            var sw = Stopwatch.StartNew();
            bool timedOut;
            var target = OnUi(() => new Target
            {
                Channel = ActiveOverlayResolver != null ? ActiveOverlayResolver() : null,
                NativePath = (ActiveOverlayResolver == null || opts.IsWrite || opts.ExpectedPath != null) && _nativeActivePath != null
                    ? SafeNativePath() : null
            }, ResolveTimeoutMs, out timedOut);
            if (timedOut)
            {
                Write(tool, "?", null, sw, "error: UI did not answer");
                return "Error: the IDE's UI thread did not answer within " + (ResolveTimeoutMs / 1000)
                    + " s, so it could not tell which editor holds the file; nothing was changed.";
            }
            var ch = target.Channel;
            string activePath = ch != null ? ch.FilePath : target.NativePath;

            // fc420c30 safety: a write meant for one file must never land in another (live: open_file returned before
            // its tab was active, and the next insert would have gone into the developer's real file).
            if (!string.IsNullOrEmpty(opts.ExpectedPath) && !SamePath(activePath, opts.ExpectedPath)
                && !AliasMatches(ch, opts.ExpectedPath))
            {
                Write(tool, ch != null ? "overlay" : "native", activePath, sw, "refused: expected " + opts.ExpectedPath);
                var aliases = Aliases(ch);
                return "Error: the active editor holds " + (string.IsNullOrEmpty(activePath) ? "no file" : activePath)
                    + (aliases.Count > 0 ? " (file_path also accepts " + string.Join(", ", aliases) + ")" : "")
                    + ", not " + opts.ExpectedPath + "; nothing was changed. Open it with open_file (and wait for it) first.";
            }

            if (ch == null) return Label(RunNative(tool, native, sw), opts, activePath, opts.NativeLine);

            if (!ch.PageReady)
            {
                Write(tool, "overlay", ch.FilePath, sw, "error: page not ready");
                return "Error: the CA Editor for " + ch.FilePath + " is still loading; nothing was changed. Try again in a moment.";
            }
            try
            {
                var ops = new OverlayEditorOps(ch);
                var result = overlay(ops);
                if (ReferenceEquals(result, UseNative)) return Label(RunNative(tool, native, sw), opts, ch.FilePath, 0);
                Write(tool, "overlay", ch.FilePath, sw, result is string && ((string)result).StartsWith("Error") ? (string)result : "ok");
                return Label(result, opts, ch.FilePath, ops.LastLine, ch);
            }
            catch (TimeoutException ex)
            {
                Write(tool, "overlay", ch.FilePath, sw, "error: " + ex.Message);
                return "Error: " + ex.Message + "; nothing was done in the native editor underneath. Try again, or ask the developer.";
            }
            catch (HostRequestBroker.RefusedException ex)
            {
                Write(tool, "overlay", ch.FilePath, sw, "refused: " + ex.Code);
                return "Error: " + Describe(ex.Code);
            }
            catch (Exception ex)
            {
                Write(tool, "overlay", ch.FilePath, sw, "error: " + ex.GetType().Name + ": " + ex.Message);
                return "Error: the CA Editor request failed (" + ex.Message + "); nothing was done in the native editor underneath.";
            }
        }

        private string SafeNativePath()
        {
            try { return _nativeActivePath(); } catch { return null; }
        }

        // A successful write names the file (and line) it changed, so a write that landed in the wrong file shows at once.
        // 73bd1f03: a channel that is not a file's editor (the CA Embeditor, whose native path is the .app) names the
        // write itself through IOverlayWriteLabel.
        private static object Label(object result, RouteOptions opts, string path, int line, IEditorOverlayChannel ch = null)
        {
            var s = result as string;
            if (!opts.IsWrite || s == null || s.StartsWith("Error") || s.StartsWith("Nothing")) return result;
            var labeler = ch as IOverlayWriteLabel;
            // The edit has ALREADY landed: a throwing label must not turn it into "nothing was done" in Run's catch.
            string own = null;
            try { own = labeler != null ? labeler.WriteLabel(line) : null; } catch { }
            if (!string.IsNullOrEmpty(own)) return s + " — " + own;
            if (string.IsNullOrEmpty(path)) return result;
            return s + " — " + System.IO.Path.GetFileName(path) + (line > 0 ? ":" + line : "") + " (" + path + ")";
        }

        // 73bd1f03: what a channel answers to in file_path besides FilePath (IOverlayPathAliases). A channel without the
        // interface gets nothing extra, so the CA Editor and native paths cannot loosen.
        private static IList<string> AliasPaths(IEditorOverlayChannel ch)
        {
            var a = ch as IOverlayPathAliases;
            if (a == null) return new string[0];
            try { return a.AcceptedPaths() ?? (IList<string>)new string[0]; } catch { return new string[0]; }
        }

        private static IList<string> AliasNames(IEditorOverlayChannel ch)
        {
            var a = ch as IOverlayPathAliases;
            if (a == null) return new string[0];
            try { return a.AcceptedNames() ?? (IList<string>)new string[0]; } catch { return new string[0]; }
        }

        private static List<string> Aliases(IEditorOverlayChannel ch)
        {
            var all = new List<string>();
            foreach (var p in AliasPaths(ch)) if (!string.IsNullOrEmpty(p)) all.Add(p);
            foreach (var n in AliasNames(ch)) if (!string.IsNullOrEmpty(n)) all.Add(n);
            return all;
        }

        // Full-path aliases only through SamePath; name aliases only against a BARE file_path (no directory), never by
        // stripping a caller's path down to its file name (a same-named module in another folder must not match).
        private static bool AliasMatches(IEditorOverlayChannel ch, string expected)
        {
            foreach (var p in AliasPaths(ch))
                if (!string.IsNullOrEmpty(p) && SamePath(p, expected)) return true;
            bool bare = expected.IndexOf('\\') < 0 && expected.IndexOf('/') < 0 && expected.IndexOf(':') < 0;
            if (!bare) return false;
            foreach (var n in AliasNames(ch))
                if (!string.IsNullOrEmpty(n) && string.Equals(n, expected.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { a = System.IO.Path.GetFullPath(a); b = System.IO.Path.GetFullPath(b); } catch { }
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // Files open_file hands to something other than a text editor (the app tree, the dictionary editor, a solution):
        // there is no editor to wait for.
        private static readonly HashSet<string> NoEditorExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".app", ".dct", ".dctx", ".sln", ".cwproj", ".txa", ".txd" };

        /// <summary>
        /// open_file (fc420c30 safety): open <paramref name="path"/>, then return only once it is the ACTIVE editor and,
        /// when a CA Editor takes it, that editor's page is ready. Bounded: on timeout an error says it is not active
        /// yet, so no write can follow into whichever file IS active (live: "Opened" came back while the developer's real
        /// file was still active). <paramref name="activate"/> (UI thread) selects the file's tab: run right after the open
        /// and again once if it is still not active after <see cref="ReactivateAfterMs"/> (live: an already-open native-mode
        /// tab stayed behind the previous one).
        /// </summary>
        public object OpenAndWait(string path, Func<object> nativeOpen, Action activate = null, int timeoutMs = OpenActivateTimeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var opened = RunNative("open_file", nativeOpen, sw);
            var s = opened as string;
            if (s == null || s.StartsWith("Error")) return opened;
            if (NoEditorExtensions.Contains(System.IO.Path.GetExtension(path) ?? "")) return opened;

            string lastActive = null;
            bool expectOverlay = false, overlayReady = false;
            int activations = 0;
            while (true)
            {
                if (activate != null && (activations == 0 || (activations == 1 && sw.ElapsedMilliseconds >= ReactivateAfterMs)))
                {
                    activations++;
                    bool t;
                    OnUi<object>(() => { activate(); return null; }, ResolveTimeoutMs, out t);
                }
                bool timedOut;
                var probe = OnUi(() =>
                {
                    var ch = ActiveOverlayResolver != null ? ActiveOverlayResolver() : null;
                    return new object[]
                    {
                        ch != null ? ch.FilePath : SafeNativePathOrNull(),
                        OverlayExpectedFor != null && OverlayExpectedFor(path),
                        ch != null && ch.PageReady
                    };
                }, ResolveTimeoutMs, out timedOut);
                if (!timedOut && probe != null)
                {
                    lastActive = (string)probe[0];
                    expectOverlay = (bool)probe[1];
                    overlayReady = (bool)probe[2];
                    if (SamePath(lastActive, path) && (!expectOverlay || overlayReady))
                    {
                        Write("open_file", expectOverlay ? "overlay" : "native", path, sw, "active");
                        return opened;
                    }
                }
                if (sw.ElapsedMilliseconds >= timeoutMs) break;
                Thread.Sleep(150);
            }
            Write("open_file", "?", path, sw, "error: not active (active: " + (lastActive ?? "none") + ")");
            return "Error: opened " + path + ", but it is not the active editor yet (the active editor is "
                + (lastActive ?? "none") + (expectOverlay && SamePath(lastActive, path) && !overlayReady ? ", its CA Editor still loading" : "")
                + "). Do NOT edit yet: wait and check with get_active_file, or pass file_path to the write so a mismatch is refused.";
        }

        private string SafeNativePathOrNull() { return _nativeActivePath != null ? SafeNativePath() : null; }

        private object RunNative(string tool, Func<object> native, Stopwatch sw)
        {
            bool timedOut;
            var result = OnUi(native, NativeTimeoutMs, out timedOut);
            Write(tool, "native", null, sw, timedOut ? "error: UI timeout" : "ok");
            return timedOut ? "Error: the IDE's UI thread did not answer within " + (NativeTimeoutMs / 1000) + " s." : result;
        }

        private static string Describe(string code)
        {
            switch (code)
            {
                case "stale": return "the CA Editor's text changed while the edit was being applied (the developer is typing); nothing was changed. Try again.";
                case "readOnly": return "the file is read-only in the CA Editor; nothing was changed.";
                case "notEditable": return "that range is not editable in the CA Editor; nothing was changed.";
                case "saveDisabled": return "this CA Editor tab cannot save.";
                case "undoDidNothing": return "the CA Editor had something to undo, but the undo changed nothing; the text is as it was.";
                case "redoDidNothing": return "the CA Editor had something to redo, but the redo changed nothing; the text is as it was.";
                case "undoUnavailable":
                case "redoUnavailable": return "this CA Editor cannot " + code.Replace("Unavailable", "") + " from a tool; ask the developer to press Ctrl+Z / Ctrl+Y.";
                default: return "the CA Editor refused (" + code + "); nothing was changed.";
            }
        }

        // Run on the UI thread and wait at most timeoutMs. Inline when there is no UI thread (standalone) or when
        // already on it. Never Invoke: a busy UI thread costs a bounded wait, not a hang.
        private T OnUi<T>(Func<T> f, int timeoutMs, out bool timedOut) where T : class
        {
            timedOut = false;
            var ui = _ui != null ? _ui() : null;
            if (ui == null || !ui.HasUiThread || Thread.CurrentThread.ManagedThreadId == UiThreadId) return f();

            T result = null;
            Exception failure = null;
            var done = new ManualResetEventSlim(false);
            ui.BeginInvokeOnUi(() =>
            {
                try { result = f(); }
                catch (Exception ex) { failure = ex; }
                finally { try { done.Set(); } catch (ObjectDisposedException) { } }
            });
            if (!done.Wait(timeoutMs)) { timedOut = true; return null; }
            done.Dispose();
            if (failure != null) throw failure;
            return result;
        }

        private static void Write(string tool, string target, string file, Stopwatch sw, string outcome)
        {
            var log = Log;
            if (log == null) return;
            try
            {
                log("[editor-route] tool=" + tool + " target=" + target + (file != null ? " file=" + file : "")
                    + " ms=" + sw.ElapsedMilliseconds + " result=" + outcome);
            }
            catch { }
        }
    }

    /// <summary>
    /// fc420c30: the MCP editor tools against the CA Editor's Monaco text. Each returns exactly what the native tool
    /// returns (same strings and shapes) and follows the same rules (EditorTextOps). Edits go to the page as ONE
    /// executeEdits batch guarded by the text version the edit was computed against; when the developer typed in
    /// between ("stale") the edit is recomputed once from fresh text.
    /// </summary>
    public sealed class OverlayEditorOps
    {
        public const int StateTimeoutMs = 15000;   // getState carries the whole buffer: PRM002023 is ~2.4 MB
        public const int EditTimeoutMs = 15000;
        public const int SaveTimeoutMs = 30000;    // the write of a large module plus the page's save round trip

        private readonly IEditorOverlayChannel _ch;
        public OverlayEditorOps(IEditorOverlayChannel ch) { _ch = ch; }
        public string FilePath { get { return _ch.FilePath; } }
        /// <summary>The line the last edit touched (for the result's "file:line"), 0 = none.</summary>
        public int LastLine { get; private set; }

        public sealed class State
        {
            public string Text;
            public long VersionId;
            public bool Dirty;
            public int CursorLine = 1, CursorCol = 1;
            public int SelStartLine, SelStartCol, SelEndLine, SelEndCol;
            public string SelText;
            public int LineCount;
        }

        public State GetState(bool withText)
        {
            var d = _ch.Request("getState", new Dictionary<string, object> { { "withText", withText } }, StateTimeoutMs);
            var s = new State();
            object v;
            if (d.TryGetValue("text", out v)) s.Text = v as string;
            if (d.TryGetValue("versionId", out v) && v != null) s.VersionId = Convert.ToInt64(v);
            if (d.TryGetValue("dirty", out v) && v is bool) s.Dirty = (bool)v;
            if (d.TryGetValue("lineCount", out v) && v != null) s.LineCount = Convert.ToInt32(v);
            var cur = d.TryGetValue("cursor", out v) ? v as Dictionary<string, object> : null;
            if (cur != null) { s.CursorLine = Int(cur, "line", 1); s.CursorCol = Int(cur, "column", 1); }
            var sel = d.TryGetValue("selection", out v) ? v as Dictionary<string, object> : null;
            if (sel != null)
            {
                s.SelStartLine = Int(sel, "startLine", 0); s.SelStartCol = Int(sel, "startCol", 0);
                s.SelEndLine = Int(sel, "endLine", 0); s.SelEndCol = Int(sel, "endCol", 0);
                object t; s.SelText = sel.TryGetValue("text", out t) ? t as string : null;
            }
            return s;
        }

        private static int Int(Dictionary<string, object> d, string k, int dflt)
        {
            object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v) : dflt;
        }

        private static Dictionary<string, object> Range(int sl, int sc, int el, int ec, string text)
        {
            return new Dictionary<string, object>
            {
                { "startLine", sl }, { "startCol", sc }, { "endLine", el }, { "endCol", ec }, { "text", text ?? "" }
            };
        }

        private static Dictionary<string, object> RangeOf(string text, int startOffset, int endOffset, string newText)
        {
            var a = EditorTextOps.Position(text, startOffset);
            var b = EditorTextOps.Position(text, endOffset);
            return Range(a[0], a[1], b[0], b[1], newText);
        }

        // Compute edits from the current text and apply them as one batch, recomputing once if the text moved.
        // plan returns the edits, or null with an error string.
        private object Edit(bool withText, Func<State, Tuple<List<Dictionary<string, object>>, string>> plan, string ok,
                            bool caretAtEnd = false)
        {
            for (int attempt = 0; ; attempt++)
            {
                var st = GetState(withText);
                var p = plan(st);
                if (p.Item1 == null) return "Error: " + p.Item2;
                try
                {
                    _ch.Request("applyEdits", new Dictionary<string, object>
                    {
                        { "expectedVersionId", st.VersionId }, { "edits", p.Item1 }, { "caretAtEnd", caretAtEnd }
                    }, EditTimeoutMs);
                    return ok;
                }
                catch (HostRequestBroker.RefusedException ex)
                {
                    if (ex.Code == "stale" && attempt == 0) continue;
                    throw;
                }
            }
        }

        private static Tuple<List<Dictionary<string, object>>, string> Plan(params Dictionary<string, object>[] edits)
        {
            return Tuple.Create(new List<Dictionary<string, object>>(edits), (string)null);
        }

        private static Tuple<List<Dictionary<string, object>>, string> Fail(string error)
        {
            return Tuple.Create((List<Dictionary<string, object>>)null, error);
        }

        // ---- reads ----

        public object GetActiveFile()
        {
            var st = GetState(true);
            return new Dictionary<string, object> { { "path", FilePath }, { "content", st.Text ?? "(unable to read)" } };
        }

        public object GetSelectedText()
        {
            var st = GetState(false);
            string s = st.SelText;
            return !string.IsNullOrEmpty(s) && s.Trim().Length > 0 ? s.Trim() : "(no selection)";
        }

        public object GetWordUnderCursor()
        {
            var st = GetState(true);
            if (!string.IsNullOrEmpty(st.SelText) && st.SelText.Trim().Length > 0) return st.SelText.Trim();
            int off = EditorTextOps.Offset(st.Text, st.CursorLine, st.CursorCol);
            return (off >= 0 ? EditorTextOps.WordAt(st.Text, off) : null) ?? "(no word at cursor)";
        }

        public object GetCursorPosition()
        {
            var st = GetState(false);
            return new Dictionary<string, object> { { "line", st.CursorLine }, { "column", st.CursorCol }, { "totalLines", st.LineCount } };
        }

        public object GetLineText(int line)
        {
            var st = GetState(true);
            return EditorTextOps.LineText(st.Text, line) ?? "Error: could not read line " + line;
        }

        public object GetLinesRange(int startLine, int endLine)
        {
            var st = GetState(true);
            return EditorTextOps.LinesRange(st.Text, startLine, endLine) ?? "Error: could not read lines " + startLine + "-" + endLine;
        }

        /// <summary>The matches (List&lt;int[]&gt;), formatted by the handler exactly as for the native editor.</summary>
        public object FindInFile(string search, bool caseSensitive)
        {
            var st = GetState(true);
            return EditorTextOps.FindAll(st.Text, search, caseSensitive);
        }

        public object IsModified()
        {
            return GetState(false).Dirty ? "Yes - file has unsaved changes" : "No - file is saved";
        }

        // ---- navigation / selection ----

        public object GoToLine(int line)
        {
            var st = GetState(false);
            if (line < 1 || line > st.LineCount) return "Error: could not navigate to line " + line;
            _ch.Request("reveal", new Dictionary<string, object> { { "line", line } }, EditTimeoutMs);
            return "Moved to line " + line;
        }

        public object SelectRange(int sl, int sc, int el, int ec)
        {
            var st = GetState(true);
            int a = EditorTextOps.Offset(st.Text, sl, sc), b = EditorTextOps.Offset(st.Text, el, ec);
            if (a < 0 || b < 0) return "Error: Invalid line/column range";
            var r = RangeOf(st.Text, a, b, null);
            r.Remove("text");
            _ch.Request("setSelection", r, EditTimeoutMs);
            return "Text selected";
        }

        // ---- edits ----

        public object InsertTextAtCursor(string text)
        {
            return Edit(false, st =>
            {
                LastLine = st.CursorLine;
                return Plan(Range(st.CursorLine, st.CursorCol, st.CursorLine, st.CursorCol, text));
            }, "Text inserted successfully", caretAtEnd: true);
        }

        public object ReplaceText(string oldText, string newText)
        {
            return Edit(true, st =>
            {
                string o = MatchEol(st.Text, oldText), n = MatchEol(st.Text, newText ?? "");
                var offs = EditorTextOps.ReplaceOffsets(st.Text, o);
                if (offs.Count == 0) return Fail("Text not found in document");
                LastLine = EditorTextOps.Position(st.Text, offs[0])[0];   // the first occurrence
                var edits = new List<Dictionary<string, object>>();
                foreach (int off in offs) edits.Add(RangeOf(st.Text, off, off + o.Length, n));
                return Tuple.Create(edits, (string)null);
            }, "Text replaced successfully");
        }

        public object ReplaceRange(int sl, int sc, int el, int ec, string newText, string ok = "Range replaced successfully")
        {
            return Edit(true, st =>
            {
                int a = EditorTextOps.Offset(st.Text, sl, sc), b = EditorTextOps.Offset(st.Text, el, ec);
                if (a < 0 || b < 0) return Fail("Invalid line/column range");
                LastLine = sl;
                return Plan(RangeOf(st.Text, a, b, newText ?? ""));
            }, ok, caretAtEnd: true);
        }

        public object DeleteRange(int sl, int sc, int el, int ec)
        {
            return ReplaceRange(sl, sc, el, ec, "", "Text deleted");
        }

        public object ToggleComment(int startLine, int endLine)
        {
            return Edit(true, st =>
            {
                string error;
                string block = EditorTextOps.ToggleCommentLines(st.Text, startLine, endLine, out error);
                if (block == null) return Fail(error);
                LastLine = startLine;
                // Monaco keeps line breaks out of line content: replace the lines' content (to the end of endLine,
                // before its break) with the block's lines, line breaks as '\n' (Monaco applies the model's EOL).
                var lines = block.Split('\n');
                for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');
                string endText = EditorTextOps.LineText(st.Text, endLine) ?? "";
                return Plan(Range(startLine, 1, endLine, endText.Length + 1, string.Join("\n", lines)));
            }, "Comment toggled on lines " + startLine + "-" + endLine);
        }

        public object Undo()
        {
            var d = _ch.Request("undo", null, EditTimeoutMs);
            object v; return d.TryGetValue("done", out v) && v is bool && (bool)v ? "Undo successful" : "Nothing to undo";
        }

        public object Redo()
        {
            var d = _ch.Request("redo", null, EditTimeoutMs);
            object v; return d.TryGetValue("done", out v) && v is bool && (bool)v ? "Redo successful" : "Nothing to redo";
        }

        // ---- save / close ----

        /// <summary>Save through the CA Editor's own save (Ctrl+S): its text, CRLF, the file's encoding, no BOM.</summary>
        public object Save()
        {
            var d = _ch.Request("save", null, SaveTimeoutMs);
            object v; bool saved = d.TryGetValue("saved", out v) && v is bool && (bool)v;
            object m; string msg = d.TryGetValue("message", out m) ? m as string : null;
            return saved ? "File saved" : "Error: could not save" + (string.IsNullOrEmpty(msg) ? "" : " (" + msg + ")");
        }

        /// <summary>Refuse to close a tab with unsaved CA Editor edits (John, 2026-10-05); a clean tab closes natively.</summary>
        public object Close()
        {
            return GetState(false).Dirty
                ? (object)"Error: unsaved changes in the CA Editor: save_file first, or ask the developer."
                : EditorToolRouter.UseNative;
        }

        // The search/replacement text with the buffer's line breaks, as the native path normalizes to its CRLF buffer.
        private static string MatchEol(string buffer, string s)
        {
            if (s == null) return s;
            bool crlf = buffer != null && buffer.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
            string lf = s.Replace("\r\n", "\n");
            return crlf ? lf.Replace("\n", "\r\n") : lf;
        }
    }
}
