using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// What the MCP tools may do while the CA Embeditor (Monaco overlay, or a live CA Embeditor tab) holds
    /// a procedure's native embeditor open (ticket 73bd1f03).
    ///
    /// The embed and editor tools act on the NATIVE embeditor document. With the CA Embeditor up, that
    /// document sits underneath Monaco, invisible, and the developer's real buffer is Monaco's. A tool write
    /// there is lost or does damage on the CA Embeditor's next save or close:
    ///   * a write that changes the slot's line count shifts the native slot ranges, so the CA Embeditor's
    ///     save sees "structure changed" and cancels the native embeditor, discarding the tool's code AND the
    ///     developer's unsaved Monaco edits;
    ///   * a same-size write is overwritten silently if the developer edits that slot in Monaco;
    ///   * Cancel, a tab switch or a tab close discards the native buffer, write included.
    ///
    /// FIX (2) ROUTES instead: when the CA Embeditor's page is ready (the "routable" fact), the embed tools go
    /// to EmbedToolRouter and the editor tools to fc420c30's EditorToolRouter, both acting on Monaco's buffer.
    /// The refusals and notes below are then the FALLBACK: while the page is not ready (loading, an old page),
    /// writes are refused and reads say they came from the native buffer, exactly as fix (1) did, and nothing
    /// ever goes to the native document behind the CA Embeditor. Saving is routed too (save_and_close_embeditor,
    /// and save_file on the covered view, run the CA Embeditor's own save and wait for 1565ef7b's
    /// EmbedSaveFinished). Discarding stays refused: cancel_embeditor, and close_file on the covered view, would
    /// throw away the developer's edits, which is their click to make, not Claude's.
    ///
    /// Pure: the facts come from the addin (McpToolRegistry's probe hooks), so tests\EmbedOverlayGuard.Test.cs
    /// can pin every branch without an IDE.
    /// </summary>
    public static class EmbedOverlayGuard
    {
        /// <summary>Embed-slot writes. They target the native embeditor whenever one is open, so the
        /// question is only whether the CA Embeditor holds it.</summary>
        private static readonly string[] EmbedWriteTools = { "write_embed_content" };

        /// <summary>Native embeditor save/cancel. With the CA Embeditor up they close the native embed out
        /// from under it: save persists the native buffer WITHOUT the developer's Monaco edits, cancel
        /// discards it, and either way the CA Embeditor is left with nothing behind it.</summary>
        private static readonly string[] EmbedLifecycleTools = { "save_and_close_embeditor", "cancel_embeditor" };

        /// <summary>Editor mutations. They target the ACTIVE view's text area, which is the covered native
        /// embed document only while the overlay's own workbench window is the active one. close_file is here
        /// because closing that view closes the native embed under the CA Embeditor.</summary>
        private static readonly string[] EditorWriteTools =
        {
            "insert_text_at_cursor", "replace_text", "replace_range", "delete_range",
            "toggle_comment", "undo", "redo", "save_file", "close_file"
        };

        /// <summary>Embed reads: answered from the native buffer, which lacks Monaco's unsaved edits.</summary>
        private static readonly string[] EmbedReadTools =
        {
            "get_embeditor_source", "search_embeditor_source", "get_embed_content"
        };

        /// <summary>Editor reads: answered from the ACTIVE view's text area, which is the covered native
        /// document while the overlay's window is active. Text, caret, selection and the dirty flag there are
        /// all the native editor's, not what the developer sees. (The CA EDITOR overlay's reads are routed to
        /// Monaco by fc420c30; this covers only the CA Embeditor.)</summary>
        private static readonly string[] EditorReadTools =
        {
            "get_active_file", "get_selected_text", "get_word_under_cursor", "get_cursor_position",
            "get_line_text", "get_lines_range", "find_in_file", "is_modified"
        };

        public static bool IsEmbedWriteTool(string tool) { return In(tool, EmbedWriteTools); }
        public static bool IsEmbedLifecycleTool(string tool) { return In(tool, EmbedLifecycleTools); }
        public static bool IsEditorWriteTool(string tool) { return In(tool, EditorWriteTools); }
        /// <summary>Editor writes routing does not take over on the covered view: always refused there.</summary>
        public static bool IsNeverRoutedEditorTool(string tool) { return tool == "close_file"; }

        /// <summary>The native embeditor lifecycle tool that routing serves (through the CA Embeditor's own save).</summary>
        private static bool IsRoutedLifecycleTool(string tool) { return tool == "save_and_close_embeditor"; }
        public static bool IsEmbedReadTool(string tool) { return In(tool, EmbedReadTools); }
        public static bool IsEditorReadTool(string tool) { return In(tool, EditorReadTools); }

        /// <summary>True when <paramref name="tool"/> needs the overlay facts at all, so the caller can skip
        /// probing the IDE for every other tool.</summary>
        public static bool IsGuarded(string tool)
        {
            return IsEmbedWriteTool(tool) || IsEmbedLifecycleTool(tool) || IsEditorWriteTool(tool) ||
                   IsEmbedReadTool(tool) || IsEditorReadTool(tool);
        }

        /// <summary>True when the decision for <paramref name="tool"/> depends on which view is active.</summary>
        private static bool NeedsCovered(string tool) { return IsEditorWriteTool(tool) || IsEditorReadTool(tool); }

        /// <summary>The refusal for a write, or null to let it run.</summary>
        /// <param name="tool">The MCP tool name.</param>
        /// <param name="caEmbeditorLive">The CA Embeditor (overlay or live tab) holds the native embeditor open.</param>
        /// <param name="activeEditorCovered">The active editor's text area is that native embed document,
        /// hidden under the overlay.</param>
        /// <param name="routable">The CA Embeditor's page is ready, so the tool is routed to it instead.</param>
        public static string Refusal(string tool, bool caEmbeditorLive, bool activeEditorCovered, bool routable = false)
        {
            if (IsEmbedWriteTool(tool) && caEmbeditorLive && !routable)
                return "Error: the CA Embeditor is open on this procedure. " + tool + " writes the native embeditor " +
                       "hidden behind it, where the change would not show and would be lost (or would discard the " +
                       "developer's unsaved edits) when the CA Embeditor saves or closes. Nothing was written. " +
                       "Show the developer the code and ask them to paste it in the CA Embeditor, or ask them to save " +
                       "and close the CA Embeditor, then use apply_embed_edits.";

            if (IsEmbedLifecycleTool(tool) && caEmbeditorLive && !(routable && IsRoutedLifecycleTool(tool)))
                return "Error: the CA Embeditor is open on this procedure. " + tool + " would close the native " +
                       "embeditor hidden behind it" + (tool == "save_and_close_embeditor"
                           ? ", saving that buffer WITHOUT the developer's unsaved CA Embeditor edits"
                           : ", discarding it") +
                       ", and leave the CA Embeditor with nothing behind it. Nothing was done. Ask the developer to " +
                       "save or close the CA Embeditor themselves.";

            if (IsEditorWriteTool(tool) && activeEditorCovered && (!routable || IsNeverRoutedEditorTool(tool)))
                return "Error: the active editor is the CA Embeditor. " + tool + " would act on the native embeditor " +
                       "document hidden behind it, not on the code the developer sees, and the CA Embeditor's next " +
                       "save or close would lose or overwrite the change. Nothing was changed. Show the developer the " +
                       "code and ask them to apply it in the CA Embeditor, or ask them to save and close it first.";

            return null;
        }

        /// <summary>A note to put in front of a read's result, or null.</summary>
        public static string ReadNote(string tool, bool caEmbeditorLive, bool activeEditorCovered, bool routable = false)
        {
            if (routable) return null;   // routed: the answer comes from Monaco (the embed route adds its own lineBase)
            if (IsEmbedReadTool(tool) && caEmbeditorLive)
                return "NOTE: the CA Embeditor is open on this procedure. This is the native embeditor's buffer, which " +
                       "does NOT include the developer's unsaved CA Embeditor edits, and write_embed_content is refused " +
                       "until the CA Embeditor is saved and closed.";
            if (IsEditorReadTool(tool) && activeEditorCovered)
                return "NOTE: the active editor is the CA Embeditor. This answer comes from the native embeditor " +
                       "document hidden behind it, which does NOT include the developer's unsaved CA Embeditor edits " +
                       "(its text, cursor, selection and modified flag are the native editor's). For the developer's " +
                       "selection use embeditor_get_selection.";
            return null;
        }

        /// <summary>
        /// Run a tool under the guard: McpToolRegistry.ExecuteTool's whole decision, kept here so the test
        /// drives the real thing. Unguarded tools run without probing the IDE. A guarded write that is refused
        /// never runs <paramref name="run"/>; a guarded read gets <see cref="ReadNote"/> in front of a
        /// successful string result. The active-view probe (a UI round-trip off the UI thread) is asked only
        /// for editor tools and only while a CA Embeditor is live: with none, nothing can be covered.
        /// </summary>
        /// <param name="caEmbeditorLiveProbe">Null on a standalone host (no CA Embeditor there).</param>
        /// <param name="activeEditorCoveredProbe">Null on a standalone host.</param>
        /// <param name="log">Optional; told about each refusal.</param>
        public static object Run(string tool, Func<bool> caEmbeditorLiveProbe, Func<bool> activeEditorCoveredProbe,
            Func<object> run, Action<string> log)
        {
            return Run(tool, caEmbeditorLiveProbe, activeEditorCoveredProbe, null, run, log);
        }

        /// <param name="routableProbe">Fix (2): the CA Embeditor's page is ready for routed tools. Null (no router)
        /// or a throwing probe = NOT routable, so the fix (1) refusals apply (fail closed).</param>
        public static object Run(string tool, Func<bool> caEmbeditorLiveProbe, Func<bool> activeEditorCoveredProbe,
            Func<bool> routableProbe, Func<object> run, Action<string> log)
        {
            if (!IsGuarded(tool)) return run();

            bool caLive = Probe(caEmbeditorLiveProbe);
            bool covered = caLive && NeedsCovered(tool) && Probe(activeEditorCoveredProbe);
            bool routable = caLive && ProbeOr(routableProbe, false);
            string refusal = Refusal(tool, caLive, covered, routable);
            if (refusal != null)
            {
                if (log != null) log("[73bd1f03] refused " + tool + " (caLive=" + caLive + ", covered=" + covered + ", routable=" + routable + ")");
                return refusal;
            }

            object result = run();
            string note = ReadNote(tool, caLive, covered, routable);
            var text = result as string;
            if (note != null && text != null && !text.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                return note + "\n\n" + text;
            return result;
        }

        /// <summary>A probe that throws counts as TRUE (fail closed): when we cannot tell whether the CA
        /// Embeditor holds the embed, refusing a write costs a retry, writing behind it can cost the
        /// developer's code. Unset (standalone host) is false: there is no CA Embeditor.</summary>
        private static bool Probe(Func<bool> probe)
        {
            if (probe == null) return false;
            try { return probe(); }
            catch { return true; }
        }

        private static bool ProbeOr(Func<bool> probe, bool onFailure)
        {
            if (probe == null) return onFailure;
            try { return probe(); }
            catch { return onFailure; }
        }

        private static bool In(string tool, string[] set)
        {
            if (string.IsNullOrEmpty(tool)) return false;
            foreach (var s in set)
                if (string.Equals(s, tool, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
