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
    /// Routing the write into Monaco is the follow-up (it needs fc420c30's page channel); until then the
    /// write is refused, the same call apply_embed_edits makes through <see cref="EmbedAdoptPolicy"/>.
    ///
    /// Reads are not refused, but they do not show the developer's unsaved Monaco edits, so they say so.
    ///
    /// Pure: the facts come from the addin (McpToolRegistry's probe hooks), so tests\EmbedOverlayGuard.Test.cs
    /// can pin every branch without an IDE.
    /// </summary>
    public static class EmbedOverlayGuard
    {
        /// <summary>Embed-slot writes. They target the native embeditor whenever one is open, so the
        /// question is only whether the CA Embeditor holds it.</summary>
        private static readonly string[] EmbedWriteTools = { "write_embed_content" };

        /// <summary>Editor mutations. They target the ACTIVE view's text area, which is the covered native
        /// embed document only while the overlay's own workbench window is the active one.</summary>
        private static readonly string[] EditorWriteTools =
        {
            "insert_text_at_cursor", "replace_text", "replace_range", "delete_range",
            "toggle_comment", "undo", "redo", "save_file"
        };

        /// <summary>Embed reads: answered from the native buffer, which lacks Monaco's unsaved edits.</summary>
        private static readonly string[] EmbedReadTools =
        {
            "get_embeditor_source", "search_embeditor_source", "get_embed_content"
        };

        public static bool IsEmbedWriteTool(string tool) { return In(tool, EmbedWriteTools); }
        public static bool IsEditorWriteTool(string tool) { return In(tool, EditorWriteTools); }
        public static bool IsEmbedReadTool(string tool) { return In(tool, EmbedReadTools); }

        /// <summary>True when <paramref name="tool"/> needs the overlay facts at all, so the caller can skip
        /// probing the IDE for every other tool.</summary>
        public static bool IsGuarded(string tool)
        {
            return IsEmbedWriteTool(tool) || IsEditorWriteTool(tool) || IsEmbedReadTool(tool);
        }

        /// <summary>The refusal for a write, or null to let it run.</summary>
        /// <param name="tool">The MCP tool name.</param>
        /// <param name="caEmbeditorLive">The CA Embeditor (overlay or live tab) holds the native embeditor open.</param>
        /// <param name="activeEditorCovered">The active editor's text area is that native embed document,
        /// hidden under the overlay.</param>
        public static string Refusal(string tool, bool caEmbeditorLive, bool activeEditorCovered)
        {
            if (IsEmbedWriteTool(tool) && caEmbeditorLive)
                return "Error: the CA Embeditor is open on this procedure. " + tool + " writes the native embeditor " +
                       "hidden behind it, where the change would not show and would be lost (or would discard the " +
                       "developer's unsaved edits) when the CA Embeditor saves or closes. Nothing was written. " +
                       "Show the developer the code and ask them to paste it in the CA Embeditor, or ask them to save " +
                       "and close the CA Embeditor, then use apply_embed_edits.";

            if (IsEditorWriteTool(tool) && activeEditorCovered)
                return "Error: the active editor is the CA Embeditor. " + tool + " would act on the native embeditor " +
                       "document hidden behind it, not on the code the developer sees, and the CA Embeditor's next " +
                       "save or close would lose or overwrite the change. Nothing was changed. Show the developer the " +
                       "code and ask them to apply it in the CA Embeditor, or ask them to save and close it first.";

            return null;
        }

        /// <summary>A note to put in front of a read's result, or null.</summary>
        public static string ReadNote(string tool, bool caEmbeditorLive)
        {
            if (!IsEmbedReadTool(tool) || !caEmbeditorLive) return null;
            return "NOTE: the CA Embeditor is open on this procedure. This is the native embeditor's buffer, which " +
                   "does NOT include the developer's unsaved CA Embeditor edits, and write_embed_content is refused " +
                   "until the CA Embeditor is saved and closed.";
        }

        /// <summary>
        /// Run a tool under the guard: McpToolRegistry.ExecuteTool's whole decision, kept here so the test
        /// drives the real thing. Unguarded tools run without probing the IDE. A guarded write that is refused
        /// never runs <paramref name="run"/>; a guarded read gets <see cref="ReadNote"/> in front of a
        /// successful string result.
        /// </summary>
        /// <param name="caEmbeditorLiveProbe">Null on a standalone host (no CA Embeditor there).</param>
        /// <param name="activeEditorCoveredProbe">Null on a standalone host.</param>
        /// <param name="log">Optional; told about each refusal.</param>
        public static object Run(string tool, Func<bool> caEmbeditorLiveProbe, Func<bool> activeEditorCoveredProbe,
            Func<object> run, Action<string> log)
        {
            if (!IsGuarded(tool)) return run();

            bool caLive = Probe(caEmbeditorLiveProbe);
            bool covered = IsEditorWriteTool(tool) && Probe(activeEditorCoveredProbe);
            string refusal = Refusal(tool, caLive, covered);
            if (refusal != null)
            {
                if (log != null) log("[73bd1f03] refused " + tool + " (caLive=" + caLive + ", covered=" + covered + ")");
                return refusal;
            }

            object result = run();
            string note = ReadNote(tool, caLive);
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

        private static bool In(string tool, string[] set)
        {
            if (string.IsNullOrEmpty(tool)) return false;
            foreach (var s in set)
                if (string.Equals(s, tool, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
