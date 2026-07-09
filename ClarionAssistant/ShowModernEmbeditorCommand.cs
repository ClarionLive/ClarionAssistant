using System;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Services;
using ClarionAssistant.Terminal;

namespace ClarionAssistant
{
    /// <summary>
    /// Path B — CA Embeditor (M1). Opens the active embeditor's assembled source in a
    /// Monaco/WebView2 view alongside Clarion's own ICSharpCode editor (mirror model — Clarion
    /// keeps generation + parse-back + persistence; this is a parallel surface).
    ///
    /// M1 scope: read-only render. Generation/save are untouched; the save round-trip back through
    /// WriteEmbedContentByLine / SaveAndCloseEmbeditor is M2.
    ///
    /// RETIRED FROM THE UI (2026-07-09, per John): the /SoftVelocity/Clarion/ToolBar/EmbedEditor
    /// ToolbarItem was removed from ClarionAssistant.addin. The auto-overlay (4d16b53a) now attaches
    /// when an embed opens, and pressing this legacy button on top of a live overlay opened a second
    /// Modern view against the same embed. Class kept unreferenced in case a mirror surface is ever
    /// wanted again.
    /// </summary>
    public class ShowModernEmbeditorCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                // Warm the language server as early as possible (idempotent, fire-and-forget) so
                // completion/hover are ready by the time the Monaco view finishes loading.
                try { EmbeditorCompletionService.LspStarter?.Invoke(); } catch { }

                string title, source, error;
                System.Collections.Generic.List<int[]> editableRanges;
                bool ok = EmbeditorCompletionService.TryGetActiveEmbeditorSource(
                    out title, out source, out editableRanges, out error);
                if (!ok)
                {
                    MessageBox.Show(
                        "Could not open the CA Embeditor:\r\n\r\n" + (error ?? "No embeditor is open.") +
                        "\r\n\r\nOpen a procedure embeditor first, then try again.",
                        "CA Embeditor",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Light by default to match the Clarion IDE; auto-following the IDE theme is M3 polish.
                var view = new ModernEmbeditorViewContent(title ?? "Embeditor", source, editableRanges, "clarion", isDark: false);
                WorkbenchSingleton.Workbench.ShowView(view);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "CA Embeditor failed to open: " + ex.Message,
                    "CA Embeditor",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
