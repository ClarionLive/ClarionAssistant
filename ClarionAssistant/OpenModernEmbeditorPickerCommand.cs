using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using ICSharpCode.Core;
using ClarionAssistant.Dialogs;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// Path B multi-editor: pick one or more procedures and open each in its own Monaco snapshot tab
    /// (open → mirror → close per procedure, via ModernEmbeditorLauncher). Lets multiple procedures be
    /// open at once — something Clarion's single embeditor doesn't allow.
    /// </summary>
    public class OpenModernEmbeditorPickerCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                var appTree = new AppTreeService();
                var procs = appTree.GetProcedureDetails();
                var names = (procs ?? new List<Dictionary<string, object>>())
                    .Select(p => p != null && p.ContainsKey("name") ? (p["name"]?.ToString() ?? "") : "")
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                if (names.Count == 0)
                {
                    MessageBox.Show(
                        "No procedures found.\r\n\r\nMake sure an application (.app) tab is open AND selected, " +
                        "then try again. Clarion only populates the procedure list once the app tab has been " +
                        "activated.",
                        "Modern Embeditor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                List<string> selected;
                using (var dlg = new ModernEmbeditorPickerDialog(names))
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    selected = dlg.SelectedProcedures;
                }
                if (selected == null || selected.Count == 0) return;

                var errors = new List<string>();
                using (var busy = new WaitCursor())
                {
                    foreach (var name in selected)
                    {
                        string err = ModernEmbeditorLauncher.OpenProcedure(name, isDark: false);
                        if (err != null) errors.Add("• " + name + ": " + err);
                    }
                }

                if (errors.Count > 0)
                {
                    MessageBox.Show(
                        "Opened " + (selected.Count - errors.Count) + " of " + selected.Count +
                        " procedure(s). Issues:\r\n\r\n" + string.Join("\r\n", errors),
                        "Modern Embeditor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Modern Embeditor picker failed: " + ex.Message,
                    "Modern Embeditor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Shows an hourglass while procedures load (each open/mirror/close takes a moment).</summary>
        private sealed class WaitCursor : IDisposable
        {
            private readonly Cursor _prev;
            public WaitCursor() { _prev = Cursor.Current; Cursor.Current = Cursors.WaitCursor; }
            public void Dispose() { Cursor.Current = _prev; }
        }
    }
}
