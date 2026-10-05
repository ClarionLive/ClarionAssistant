using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>73bd1f03 fix (2): the CA Embeditor that holds the procedure's native embeditor open, as the embed
    /// tools see it.</summary>
    public interface IEmbedOverlayChannel
    {
        /// <summary>The procedure the CA Embeditor edits.</summary>
        string ProcedureName { get; }
        /// <summary>The page has loaded the procedure and answers requests.</summary>
        bool PageReady { get; }
        /// <summary>The slot ranges the CA Embeditor opened with: the NATIVE embeditor document's, which nothing writes
        /// while the CA Embeditor holds it (73bd1f03 fix 1), so slot i here is native slot i.</summary>
        IList<int[]> NativeRanges { get; }
        /// <summary>Ask the page (HostRequestBroker semantics: never on the UI thread; TimeoutException,
        /// HostRequestBroker.RefusedException).</summary>
        Dictionary<string, object> Request(string action, Dictionary<string, object> args, int timeoutMs);
    }

    /// <summary>
    /// 73bd1f03 fix (2): routes get_embeditor_source / search_embeditor_source / get_embed_content /
    /// write_embed_content to the CA Embeditor's Monaco buffer while one holds the procedure, so Claude reads what
    /// the developer sees (unsaved edits included) and its writes land there, visible, saved by the CA Embeditor's
    /// own save. Before fix 1 they acted on the native document hidden underneath, and the CA Embeditor's save
    /// then lost them (or, on a line-count change, cancelled the embed and lost the developer's edits too).
    ///
    /// Same shape as fc420c30's EditorToolRouter: the tools run OFF the UI thread; the target is found ON it through a
    /// bounded marshal (never Invoke); the native path runs today's AppTreeService call the same way; the page is
    /// waited on from the calling thread.
    ///
    /// FAIL CLOSED: with a CA Embeditor up, a page that is not ready, does not answer, or refuses gives an error and
    /// never falls back to the native document. Only "no CA Embeditor" goes native.
    /// </summary>
    public sealed class EmbedToolRouter
    {
        /// <summary>The CA Embeditor holding the native embed (overlay or live tab), or null. Called on the UI thread.
        /// Set by the addin; null (standalone) = native.</summary>
        public static Func<IEmbedOverlayChannel> LiveEmbedResolver;
        /// <summary>The embed point's column for a slot starting at a 1-based native line (0 = none). UI thread.</summary>
        public static Func<int, int> NativeEmbedColumn;
        /// <summary>[embed-route] log sink.</summary>
        public static Action<string> Log;
        /// <summary>The procedure the NATIVE embeditor has open, for naming a native write's result, or null. UI thread.
        /// Set by the addin.</summary>
        public static Func<string> NativeEmbedProcedure;

        /// <summary>
        /// A successful embed write names the procedure and slot it changed, in fc420c30's style for file writes
        /// ("... — file:line (path)"), so a write that landed in the wrong procedure shows at once. Errors pass through.
        /// </summary>
        public static string Named(string result, string procedure, int line, string where)
        {
            if (result == null || result.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) return result;
            return result + " — " + (string.IsNullOrEmpty(procedure) ? "(procedure unknown)" : procedure)
                + " «E:" + line + "» (" + where + ")";
        }

        /// <summary><see cref="NativeEmbedProcedure"/>, or null when unset or it fails. UI thread.</summary>
        public static string NativeProcedureName()
        {
            try { return NativeEmbedProcedure != null ? NativeEmbedProcedure() : null; }
            catch { return null; }
        }

        public const int ResolveTimeoutMs = 3000;
        public const int NativeTimeoutMs = 30000;

        private readonly Func<IUiDispatcher> _ui;

        public EmbedToolRouter(Func<IUiDispatcher> ui) { _ui = ui; }

        /// <summary>True when an addin has wired a CA Embeditor resolver (the standalone server has none).</summary>
        public static bool Wired { get { return LiveEmbedResolver != null; } }

        public object Run(string tool, Func<object> native, Func<EmbedOverlayOps, object> overlay)
        {
            var sw = Stopwatch.StartNew();
            IEmbedOverlayChannel ch = null;
            if (LiveEmbedResolver != null)
            {
                bool timedOut;
                ch = OnUi(() => LiveEmbedResolver(), ResolveTimeoutMs, out timedOut);
                if (timedOut)
                {
                    Write(tool, "?", null, sw, "error: UI did not answer");
                    return "Error: the IDE's UI thread did not answer within " + (ResolveTimeoutMs / 1000)
                        + " s, so it could not tell whether the CA Embeditor holds the procedure; nothing was done.";
                }
            }

            if (ch == null)
            {
                bool timedOut;
                var result = OnUi(native, NativeTimeoutMs, out timedOut);
                Write(tool, "native", null, sw, timedOut ? "error: UI timeout" : "ok");
                return timedOut ? "Error: the IDE's UI thread did not answer within " + (NativeTimeoutMs / 1000) + " s." : result;
            }

            if (!ch.PageReady)
            {
                Write(tool, "ca-embeditor", ch.ProcedureName, sw, "error: page not ready");
                return "Error: the CA Embeditor for '" + ch.ProcedureName + "' is still loading; nothing was done. Try again in a moment.";
            }
            try
            {
                var ops = new EmbedOverlayOps(ch, line =>
                {
                    bool timedOut;
                    var col = OnUi(() => (object)(NativeEmbedColumn != null ? NativeEmbedColumn(line) : 0), ResolveTimeoutMs, out timedOut);
                    if (timedOut) throw new TimeoutException("the IDE's UI thread did not answer the embed-column lookup");
                    return (int)col;
                });
                var result = overlay(ops);
                Write(tool, "ca-embeditor", ch.ProcedureName, sw, result is string && ((string)result).StartsWith("Error") ? (string)result : "ok");
                return result;
            }
            catch (TimeoutException ex)
            {
                Write(tool, "ca-embeditor", ch.ProcedureName, sw, "error: " + ex.Message);
                return "Error: " + ex.Message + "; nothing was done in the native embeditor underneath. Try again, or ask the developer.";
            }
            catch (HostRequestBroker.RefusedException ex)
            {
                Write(tool, "ca-embeditor", ch.ProcedureName, sw, "refused: " + ex.Code);
                return "Error: " + Describe(ex.Code);
            }
            catch (Exception ex)
            {
                Write(tool, "ca-embeditor", ch.ProcedureName, sw, "error: " + ex.GetType().Name + ": " + ex.Message);
                return "Error: the CA Embeditor request failed (" + ex.Message + "); nothing was done in the native embeditor underneath.";
            }
        }

        internal static string Describe(string code)
        {
            switch (code)
            {
                case "stale": return "the CA Embeditor's text changed while the edit was being applied (the developer is typing); nothing was changed. Read the embed again and retry.";
                case "readOnly": return "the CA Embeditor is read-only right now; nothing was changed.";
                case "notEditable": return "that range is not an editable embed slot in the CA Embeditor; nothing was changed.";
                case "notReady": return "the CA Embeditor has not loaded the procedure yet; nothing was done. Try again in a moment.";
                case EmbedSaveWait.InProgressCode: return "a save is already in progress in the CA Embeditor; try again shortly. Nothing was saved by this call, and the developer's edits are still open.";
                default: return "the CA Embeditor refused (" + code + "); nothing was changed.";
            }
        }

        // Run on the UI thread and wait at most timeoutMs. Inline when there is no UI thread (standalone) or when
        // already on it. Never Invoke: a busy UI thread costs a bounded wait, not a hang.
        private T OnUi<T>(Func<T> f, int timeoutMs, out bool timedOut) where T : class
        {
            timedOut = false;
            var ui = _ui != null ? _ui() : null;
            if (ui == null || !ui.HasUiThread || Thread.CurrentThread.ManagedThreadId == EditorToolRouter.UiThreadId) return f();

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

        private static void Write(string tool, string target, string proc, Stopwatch sw, string outcome)
        {
            var log = Log;
            if (log == null) return;
            try
            {
                log("[embed-route] tool=" + tool + " target=" + target + (proc != null ? " proc=" + proc : "")
                    + " ms=" + sw.ElapsedMilliseconds + " result=" + outcome);
            }
            catch { }
        }
    }

    /// <summary>
    /// 73bd1f03 fix (2): ask the CA Embeditor to save, and wait for 1565ef7b's EmbedSaveFinished for the outcome.
    ///
    /// The page's own reply cannot be the answer: a SUCCESSFUL overlay save disposes the page before the save runs
    /// (the freeze rule), so that reply never comes. EmbedSaveFinished is raised exactly once per save on every
    /// branch, refusals included. The save request is therefore posted from a worker thread and its reply only
    /// matters when it is a refusal (the page never started a save, so no event will follow).
    ///
    /// ONE SAVE AT A TIME (1565ef7b's final contract): a save requested while another is in flight is refused at
    /// once, editor intact, with its own refusal event whose message starts <see cref="InProgressPrefix"/>. The
    /// events carry no request id (dc4f7115), so such an event may be ANOTHER request's refusal (the developer's
    /// Ctrl+S pressed while our save runs): it is not taken as our outcome. Ours is recognised by the page's reply
    /// to OUR request carrying that refusal, which arrives only when the save we asked for was the one refused.
    /// Residual ambiguity until dc4f7115: the page answers its pending host save from ANY saveResult, so a
    /// developer Ctrl+S refused while our save runs can reach our request and make us report "in progress" while
    /// our save in fact completes (no harm: a retry is refused or saves nothing new).
    /// </summary>
    public static class EmbedSaveWait
    {
        /// <summary>The start of 1565ef7b's refusal message for a save requested while another is in flight.</summary>
        public const string InProgressPrefix = "A save is already in progress";

        /// <summary>The RefusedException code for that refusal (EmbedToolRouter.Describe words it for Claude).</summary>
        public const string InProgressCode = "saveInProgress";

        private static bool IsInProgress(string message)
        {
            return message != null && message.StartsWith(InProgressPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <returns>{ saved, message, editorIntact }.</returns>
        /// <exception cref="TimeoutException">No outcome within <paramref name="timeoutMs"/>.</exception>
        /// <exception cref="HostRequestBroker.RefusedException">The page refused to start the save.</exception>
        public static Dictionary<string, object> Run(string procedure,
            Action<Action<string, bool, string, bool>> subscribe, Action<Action<string, bool, string, bool>> unsubscribe,
            Func<Dictionary<string, object>> requestSave, int timeoutMs)
        {
            var gate = new object();
            var done = new ManualResetEventSlim(false);
            bool ok = false, intact = false;
            string message = null, refused = null;
            Action<string, bool, string, bool> handler = (proc, o, m, i) =>
            {
                if (!string.Equals(proc, procedure, StringComparison.OrdinalIgnoreCase)) return;
                if (IsInProgress(m)) return;   // some request's busy-refusal; ours shows up as our page reply (below)
                lock (gate)
                {
                    if (done.IsSet) return;
                    ok = o; message = m; intact = i;
                    done.Set();
                }
            };
            subscribe(handler);   // before the request: the outcome cannot slip past us
            try
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var reply = requestSave();
                        object v;
                        string m = reply != null && reply.TryGetValue("message", out v) ? v as string : null;
                        bool saved = reply != null && reply.TryGetValue("saved", out v) && v is bool && (bool)v;
                        if (!saved && IsInProgress(m))
                            lock (gate) { if (!done.IsSet) { refused = InProgressCode; done.Set(); } }
                        // any other reply: the event is the authority
                    }
                    catch (HostRequestBroker.RefusedException ex)
                    {
                        lock (gate) { if (!done.IsSet) { refused = ex.Code; done.Set(); } }
                    }
                    catch { }   // no reply (the page closed on a successful save): the event is the answer
                });
                if (!done.Wait(timeoutMs))
                    throw new TimeoutException("the CA Embeditor's save did not report back within " + (timeoutMs / 1000)
                        + " s; check the IDE before retrying");
                lock (gate)
                {
                    if (refused != null) throw new HostRequestBroker.RefusedException(refused);
                    return new Dictionary<string, object> { { "saved", ok }, { "message", message }, { "editorIntact", intact } };
                }
            }
            finally { unsubscribe(handler); }
        }
    }

    /// <summary>73bd1f03 fix (2): the embed tools against the CA Embeditor's Monaco buffer. Answers use
    /// <see cref="EmbedSlotText"/>, so they read exactly like the native tools, prefixed with
    /// <see cref="EmbedSlotText.LineBaseNote"/>.</summary>
    public sealed class EmbedOverlayOps
    {
        public const int StateTimeoutMs = 15000;   // getSlots carries the whole buffer
        public const int EditTimeoutMs = 15000;
        /// <summary>A save regenerates the module: the native save_and_close_embeditor's 180 s budget.</summary>
        public const int SaveTimeoutMs = 180000;

        private readonly IEmbedOverlayChannel _ch;
        private readonly Func<int, int> _nativeColumn;

        /// <param name="nativeColumn">The embed column for a 1-based NATIVE slot start line (0 = unknown).</param>
        public EmbedOverlayOps(IEmbedOverlayChannel ch, Func<int, int> nativeColumn)
        {
            _ch = ch;
            _nativeColumn = nativeColumn;
        }

        public sealed class Slots
        {
            public string Text;
            public long VersionId;
            public List<int[]> Ranges = new List<int[]>();
        }

        public Slots GetSlots()
        {
            var d = _ch.Request("getSlots", new Dictionary<string, object> { { "withText", true } }, StateTimeoutMs);
            var s = new Slots();
            object v;
            if (d.TryGetValue("text", out v)) s.Text = v as string;
            if (d.TryGetValue("versionId", out v) && v != null) s.VersionId = Convert.ToInt64(v);
            if (d.TryGetValue("ranges", out v) && v is System.Collections.IEnumerable && !(v is string))
            {
                foreach (var r in (System.Collections.IEnumerable)v)
                {
                    var pair = r as System.Collections.IList;
                    if (pair == null || pair.Count < 2) continue;
                    s.Ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) });
                }
            }
            if (s.Text == null) throw new InvalidOperationException("the CA Embeditor returned no text");
            return s;
        }

        private static string Noted(string answer)
        {
            return EmbedSlotText.LineBaseNote + "\n\n" + answer;
        }

        public object GetEmbeditorSource()
        {
            var s = GetSlots();
            return Noted(EmbedSlotText.Annotate(s.Text, s.Ranges));
        }

        public object SearchEmbeditorSource(string pattern, int contextLines)
        {
            var s = GetSlots();
            string r = EmbedSlotText.Search(EmbedSlotText.Annotate(s.Text, s.Ranges), pattern, contextLines);
            return r.StartsWith("Error") ? r : Noted(r);
        }

        public object GetEmbedContent(int line)
        {
            var s = GetSlots();
            string r = EmbedSlotText.SlotContent(s.Text, s.Ranges, line);
            return r.StartsWith("Error") ? r : Noted(r);
        }

        /// <summary>save_and_close_embeditor through the CA Embeditor's own save (save-and-exit, as the developer's
        /// Save button): its text, its structure checks, its error handling. The channel's "save" request waits
        /// for EmbedSaveFinished (see <see cref="EmbedSaveWait"/>).</summary>
        public object SaveAndClose()
        {
            Dictionary<string, object> d;
            try { d = _ch.Request("save", null, SaveTimeoutMs); }
            catch (TimeoutException ex)
            {
                // Not the router's generic "try again": the save may still be running and may still succeed.
                return "Error: " + ex.Message + ". The save may still complete - do NOT retry until the developer " +
                       "confirms what happened.";
            }
            object v;
            bool saved = d.TryGetValue("saved", out v) && v is bool && (bool)v;
            bool intact = d.TryGetValue("editorIntact", out v) && v is bool && (bool)v;
            string msg = d.TryGetValue("message", out v) ? v as string : null;
            if (saved)
                return "Saved and closed the CA Embeditor on '" + _ch.ProcedureName + "'" +
                       (string.IsNullOrEmpty(msg) ? "." : ": " + msg);
            return "Error: the CA Embeditor's save failed" + (string.IsNullOrEmpty(msg) ? "" : ": " + msg) + ". " +
                   (intact ? "The developer's edits are still open in the CA Embeditor; nothing was lost."
                           : "The CA Embeditor closed. Ask the developer to check the procedure.");
        }

        /// <summary>write_embed_content into the CA Embeditor: one applyEdits batch (one undo step) guarded by the
        /// buffer version it was planned against; recomputed once when the developer typed in between.</summary>
        public object WriteEmbedContent(int line, string code)
        {
            for (int attempt = 0; ; attempt++)
            {
                var s = GetSlots();
                var native = _ch.NativeRanges;
                int i = EmbedSlotText.SlotAt(s.Ranges, line);
                if (i < 0) return EmbedSlotText.NoSlotError(line);
                if (native == null || native.Count != s.Ranges.Count)
                    return "Error: the CA Embeditor's embed slots no longer match the procedure (" + s.Ranges.Count +
                           " vs " + (native == null ? 0 : native.Count) + "); nothing was written. Ask the developer to save and reopen it.";

                int column = _nativeColumn != null ? _nativeColumn(native[i][0]) : 0;
                string error;
                var plan = EmbedSlotText.PlanWrite(s.Text, s.Ranges, line, code, Math.Max(1, column), true, out error);
                if (plan == null) return error;

                try
                {
                    _ch.Request("applyEdits", new Dictionary<string, object>
                    {
                        { "expectedVersionId", s.VersionId },
                        { "edits", new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    { "startLine", plan.StartLine }, { "startCol", 1 },
                                    { "endLine", plan.EndLine }, { "endCol", plan.EndCol }, { "text", plan.NewText }
                                }
                            } },
                        { "caretAtEnd", false }
                    }, EditTimeoutMs);
                    return Noted(EmbedToolRouter.Named(EmbedSlotText.WriteReport(line, plan.LineDelta) +
                        "\nThe code is in the CA Embeditor (unsaved); the developer's save persists it.",
                        _ch.ProcedureName, line, "CA Embeditor"));
                }
                catch (HostRequestBroker.RefusedException ex)
                {
                    if (ex.Code == "stale" && attempt == 0) continue;
                    throw;
                }
            }
        }
    }
}
