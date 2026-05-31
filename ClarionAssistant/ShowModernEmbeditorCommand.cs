using System;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Services;
using ClarionAssistant.Terminal;

namespace ClarionAssistant
{
    /// <summary>
    /// Path B — Modern Embeditor (M1). Opens the active embeditor's assembled source in a
    /// Monaco/WebView2 view alongside Clarion's own ICSharpCode editor (mirror model — Clarion
    /// keeps generation + parse-back + persistence; this is a parallel surface).
    ///
    /// M1 scope: read-only render. Generation/save are untouched; the save round-trip back through
    /// WriteEmbedContentByLine / SaveAndCloseEmbeditor is M2.
    ///
    /// Registered on the embeditor toolbar (/SoftVelocity/Clarion/ToolBar/EmbedEditor), next to the
    /// Path A "Completion Test" item.
    /// </summary>
    public class ShowModernEmbeditorCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                string title, source, error;
                System.Collections.Generic.List<int[]> editableRanges;
                bool ok = EmbeditorCompletionService.TryGetActiveEmbeditorSource(
                    out title, out source, out editableRanges, out error);
                if (!ok)
                {
                    MessageBox.Show(
                        "Could not open the Modern Embeditor:\r\n\r\n" + (error ?? "No embeditor is open.") +
                        "\r\n\r\nOpen a procedure embeditor first, then try again.",
                        "Modern Embeditor",
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
                    "Modern Embeditor failed to open: " + ex.Message,
                    "Modern Embeditor",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
