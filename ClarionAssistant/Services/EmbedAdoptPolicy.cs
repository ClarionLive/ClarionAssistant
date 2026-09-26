using System;

namespace ClarionAssistant.Services
{
    /// <summary>What apply_embed_edits does about an embeditor that is already open (PR #198).</summary>
    public enum EmbedAdoptDecision
    {
        /// <summary>No embeditor is open: open the procedure fresh (the pre-PR path).</summary>
        OpenFresh,
        /// <summary>Write into the open embeditor, then save and close it.</summary>
        Adopt,
        /// <summary>Touch nothing; report the error to the agent.</summary>
        Refuse
    }

    /// <summary>
    /// The adopt-an-open-embeditor decision behind ModernEmbeditorLauncher.TryAdoptOpenEmbeditor, kept pure
    /// so tests\EmbedAdoptPolicy.Test.cs can pin every branch without an IDE. The launcher gathers the
    /// facts from the live IDE; this decides.
    ///
    /// Adopting ends in SaveAndCloseEmbeditor - the IDE has no save-without-close - and that save persists
    /// the WHOLE buffer. So adoption is only safe when every byte in that buffer is either already saved
    /// or ours. Two ways it would not be, both refused:
    ///
    /// 1. THE CA EMBEDITOR (Monaco overlay, or a live CA Embeditor tab) HOLDS THE EMBED. Monaco is then the
    ///    developer's real buffer and the native one underneath is invisible: a native write would not show,
    ///    Monaco's own save would overwrite it, and Monaco's unsaved edits never reach the native IsDirty flag,
    ///    so check 2 could not see them. Routing the write through Monaco is a larger change (async page
    ///    edits plus the overlay's own save round-trip), so this refuses instead of writing behind it.
    ///    Checked BEFORE dirty for exactly that reason: with Monaco up, a clean native flag proves nothing.
    ///
    /// 2. THE NATIVE BUFFER IS DIRTY (or its dirty flag cannot be read). Saving would persist the developer's
    ///    unrelated unsaved edits along with ours. Fail closed: only an explicit IsDirty == false adopts.
    /// </summary>
    public static class EmbedAdoptPolicy
    {
        /// <param name="embeditorOpen">A native embeditor is open (GetEmbedInfo() != null).</param>
        /// <param name="sourceReadError">Non-null when the open embeditor's source could not be mirrored.</param>
        /// <param name="openProcName">The procedure declared at column 0 of the open buffer, or null/empty.</param>
        /// <param name="targetProc">The procedure the agent asked to edit.</param>
        /// <param name="caEmbeditorLive">The CA Embeditor (overlay or live tab) holds this embed open.</param>
        /// <param name="nativeDirty">The native editor's IsDirty; null = unreadable.</param>
        public static EmbedAdoptDecision Decide(bool embeditorOpen, string sourceReadError, string openProcName,
            string targetProc, bool caEmbeditorLive, bool? nativeDirty, out string error)
        {
            error = null;
            if (!embeditorOpen) return EmbedAdoptDecision.OpenFresh;

            if (sourceReadError != null)
            {
                error = "An embeditor is open but its source could not be read (" + sourceReadError +
                        "); close it and try again.";
                return EmbedAdoptDecision.Refuse;
            }

            string target = (targetProc ?? "").Trim();
            if (string.IsNullOrEmpty(openProcName) ||
                !string.Equals(openProcName, target, StringComparison.OrdinalIgnoreCase))
            {
                error = "An embeditor is still open on '" +
                        (string.IsNullOrEmpty(openProcName) ? "an unidentified procedure" : openProcName) +
                        "', not '" + target + "'; close it and try again.";
                return EmbedAdoptDecision.Refuse;
            }

            if (caEmbeditorLive)
            {
                error = "The CA Embeditor is open on '" + target + "'. apply_embed_edits will not write into the " +
                        "native embeditor behind it: the change would be invisible there and lost when the CA " +
                        "Embeditor saves. Ask the developer to save and close the CA Embeditor on '" + target +
                        "', then retry. Nothing was written.";
                return EmbedAdoptDecision.Refuse;
            }

            if (nativeDirty != false)
            {
                error = nativeDirty == true
                    ? "The developer has UNSAVED changes in the embeditor open on '" + target + "'. Applying would " +
                      "save them along with your edits, so nothing was written. Ask the developer to save or " +
                      "discard their changes in the IDE, then retry."
                    : "Could not confirm that the embeditor open on '" + target + "' has no unsaved changes " +
                      "(its dirty flag is unreadable), so nothing was written. Ask the developer to save or " +
                      "close it in the IDE, then retry.";
                return EmbedAdoptDecision.Refuse;
            }

            return EmbedAdoptDecision.Adopt;
        }
    }
}
