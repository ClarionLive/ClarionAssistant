using System;
using System.IO;
using System.Threading;
using ClarionAssistant.Services;
using ClarionAssistant.Terminal;

namespace ClarionAssistant
{
    /// <summary>
    /// 44a1b10c: the addin's answer to SharedLspBridge.LiveTextProvider: which text is open for a path, so
    /// lsp_diagnostics checks the editor's current text (unsaved edits included) instead of the disk file.
    /// Registered by LspAutostartCommand at addin start, so it works with no chat tab open.
    ///
    /// In order:
    ///   1. the native embeditor, when a procedure of THIS module is open in it: its whole document (where
    ///      write_embed_content's edits live; the module on disk only gets them at the next generate), wrapped
    ///      exactly as the CA Embeditor wraps it for the LSP (the module's MEMBER line), lines reported in the
    ///      embeditor's own numbering (= «E:N»). It wins over the CA Embeditor overlay (Charlie, 2026-10-04: the
    ///      native document is where Claude's edits are);
    ///   2. a CA Editor (Monaco overlay) tab with unsaved edits on the path;
    ///   3. a file-mode CA Embeditor tab on the path;
    ///   else null: the disk.
    ///
    /// THREADING. Called on the MCP worker thread. (2) and (3) are reference reads of fields the UI replaces whole.
    /// (1) needs the IDE's object model, so it is POSTED to the UI thread and waited for at most UiWaitMs. It is never
    /// an Invoke, and the MCP thread never waits unbounded: if the UI thread is busy, or is itself waiting on this
    /// call (the sync-over-async stall, ~60 s freeze), the wait times out and the tool checks the disk, saying why.
    /// A late-running posted read just finishes and is discarded.
    /// </summary>
    internal static class EditorLiveTextProvider
    {
        internal const int UiWaitMs = 2000;
        private static SynchronizationContext _ui;

        /// <summary>Call on the UI thread (addin start): captures its SynchronizationContext and registers.</summary>
        internal static void Register()
        {
            _ui = SynchronizationContext.Current;
            SharedLspBridge.LiveTextProvider = Get;
        }

        internal static SharedLspBridge.LiveText Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var answer = Lookup(path);
            // One line per lookup, so a live run says which branch answered and why (44a1b10c).
            MonacoSpikeLog.Write("[live-text] path=" + path
                + (answer != null && answer.Text != null
                    ? " hit branch=" + answer.Origin + " chars=" + answer.Text.Length
                      + (answer.Procedure != null ? " procedure=" + answer.Procedure : "")
                    : " miss reason=" + (answer != null && answer.Reason != null ? answer.Reason : "no editor has it open"))
                + " ms=" + sw.ElapsedMilliseconds);
            return answer;
        }

        private static SharedLspBridge.LiveText Lookup(string path)
        {
            var embed = ReadEmbeditorDocument(path);
            if (embed != null && embed.Text != null) return embed;

            // These two need no UI thread, so they still answer when the embeditor read timed out.
            string text = MonacoClarionEditor.TryGetLiveText(path);
            if (text != null) return new SharedLspBridge.LiveText { Text = text, Origin = "ca-editor-buffer" };

            text = ModernEmbeditorViewContent.TryGetFileModeLiveText(path);
            if (text != null) return new SharedLspBridge.LiveText { Text = text, Origin = "embeditor-file-buffer" };

            return embed;   // null (nothing open), or why an open embeditor could not be checked
        }

        // (1): null when no embed of this module is open; a LiveText with Text, or with only a Reason on a timeout.
        private static SharedLspBridge.LiveText ReadEmbeditorDocument(string path)
        {
            var ui = _ui;
            if (ui == null) return null;

            SharedLspBridge.LiveText answer = null;
            Exception failure = null;
            var done = new ManualResetEventSlim(false);
            SendOrPostCallback read = _ =>
            {
                try { answer = ReadEmbeditorDocumentOnUi(path); }
                catch (Exception ex) { failure = ex; }
                finally { try { done.Set(); } catch (ObjectDisposedException) { } }
            };

            if (SynchronizationContext.Current == ui)
                read(null);   // already on the UI thread: read inline, nothing to wait for
            else
            {
                ui.Post(read, null);
                if (!done.Wait(UiWaitMs))
                    return new SharedLspBridge.LiveText
                    {
                        Reason = "the IDE's UI thread did not answer within " + (UiWaitMs / 1000) + " s, so an open embeditor could not be checked"
                    };
            }
            done.Dispose();
            if (failure != null)
                return new SharedLspBridge.LiveText { Reason = "reading the open embeditor failed: " + failure.Message };
            return answer;
        }

        private static SharedLspBridge.LiveText ReadEmbeditorDocumentOnUi(string path)
        {
            var appTree = new AppTreeService();
            if (appTree.GetOpenPweeDetails() == null) return null;   // no embed open

            var ctx = EmbedLspContext.TryCapture(appTree);
            if (ctx == null || !SamePath(ctx.RealPath, path)) return null;   // an embed of another module

            string doc = appTree.GetEmbeditorDocumentText();
            if (doc == null) return null;
            return new SharedLspBridge.LiveText
            {
                Text = ctx.WrapBuffer(doc),
                Origin = "embeditor-document",
                LineOffset = ctx.LineOffsetFor(doc),
                Procedure = appTree.GetOpenNativeEmbeditorProcName()
                // EmbedRanges deliberately unset: PWEE does not refresh its slot line ranges after write_embed_content
                // inserts (AppTreeService.GetEmbeditorSource), so inEmbed would be wrong exactly after an edit.
            };
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { a = Path.GetFullPath(a); b = Path.GetFullPath(b); } catch { }
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
