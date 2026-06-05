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
                // NOTE: the LSP warmup is intentionally NOT kicked here. Kicking it at picker-start spawns the
                // background solution parse CONCURRENTLY with the native ABC load inside OpenProcedure, which
                // ~halves the ABC load speed (the native right-click→embeditor path doesn't kick the LSP, so it
                // loads full-speed). ModernEmbeditorLauncher.OpenProcedure now kicks it AFTER the mirror (ABC
                // already loaded) so it warms during the WebView2/Monaco load instead of fighting the ABC load.

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
                        "CA Embeditor", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                        "CA Embeditor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "CA Embeditor picker failed: " + ex.Message,
                    "CA Embeditor", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
