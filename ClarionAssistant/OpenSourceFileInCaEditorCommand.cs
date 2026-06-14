using System;
using System.IO;
using System.Windows.Forms;
using ICSharpCode.Core;
using ClarionAssistant.Services;

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
                    dlg.Filter = MonacoFileOpener.OpenFileFilter; // single source of truth (shared with the Explorer Files tab)
                    dlg.Multiselect = true;
                    dlg.CheckFileExists = true;
                    try
                    {
                        // Prefer the Explorer's last-used folder; fall back to the open solution's directory.
                        string last = ExplorerRecentsStore.GetLastFolder();
                        if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
                        {
                            dlg.InitialDirectory = last;
                        }
                        else
                        {
                            string sln = EditorService.GetOpenSolutionPath();
                            if (!string.IsNullOrEmpty(sln))
                                dlg.InitialDirectory = Path.GetDirectoryName(sln);
                        }
                    }
                    catch { }
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    files = dlg.FileNames;
                }

                // Route every open through the single choke point so recents + last-folder are recorded.
                foreach (var f in files)
                    MonacoFileOpener.OpenFile(f, isDark: false);
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
