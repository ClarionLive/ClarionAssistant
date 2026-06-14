using System;
using System.IO;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Services;
using ClarionAssistant.Terminal;

namespace ClarionAssistant
{
    /// <summary>
    /// Open plain source files (.clw/.inc/.equ/...) in the CA (Monaco) editor — file mode
    /// (ticket 564aa142). No embeditor round-trip: the tab edits the file on disk directly,
    /// so hand-coded classes and includes get the modern editor without an .app procedure.
    /// </summary>
    public class OpenSourceFileInCaEditorCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                string[] files;
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Open in CA Editor";
                    dlg.Filter = "Clarion source (*.clw;*.inc;*.equ;*.int;*.trn;*.tpl;*.tpw)|*.clw;*.inc;*.equ;*.int;*.trn;*.tpl;*.tpw|All files (*.*)|*.*";
                    dlg.Multiselect = true;
                    dlg.CheckFileExists = true;
                    try
                    {
                        string sln = EditorService.GetOpenSolutionPath();
                        if (!string.IsNullOrEmpty(sln))
                            dlg.InitialDirectory = Path.GetDirectoryName(sln);
                    }
                    catch { }
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    files = dlg.FileNames;
                }

                foreach (var f in files)
                {
                    // One tab per file: re-opening an already-open file focuses its tab.
                    var existing = ModernEmbeditorViewContent.FindByFilePath(f);
                    if (existing != null) { existing.ActivateTab(); continue; }
                    var view = new ModernEmbeditorViewContent(f, isDark: false);
                    WorkbenchSingleton.Workbench.ShowView(view);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "CA Editor failed to open the file: " + ex.Message,
                    "CA Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
