using System;
using System.Drawing;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Services;

namespace ClarionAssistant.Options
{
    /// <summary>
    /// IDE Options pane: Options &gt; Clarion Assistant &gt; Find / Replace (GitHub #66 phase 2).
    /// Picks which Find/Replace UI answers Ctrl+F / Ctrl+H in the CA editors: the dockable
    /// CA Find/Replace pad (default) or the in-editor overlay (quick find top-right + Find-All
    /// left column). Both drive the same match/replace engine; this is presentation only.
    ///
    /// Same contract as <see cref="CAEditorSurfacesOptionPanel"/> (confirmed by live reflection):
    /// AbstractOptionPanel IS a UserControl; LoadPanelContents() fires on first display;
    /// StorePanelContents() fires ONLY on dialog OK. Persistence via <see cref="CaFindSettings"/>
    /// (shared settings.txt) — the value rides into each editor page inside setSource.
    /// </summary>
    public class CAFindOptionPanel : AbstractOptionPanel
    {
        private RadioButton _rbPad;
        private RadioButton _rbOverlay;
        private bool _built;

        public CAFindOptionPanel()
        {
            // UI is built lazily in LoadPanelContents (fired on first show) — see BuildUi.
        }

        public override void LoadPanelContents()
        {
            BuildUi();
            bool overlay = CaFindSettings.FindUiMode == CaFindSettings.ModeOverlay;
            _rbOverlay.Checked = overlay;
            _rbPad.Checked = !overlay;
        }

        public override bool StorePanelContents()
        {
            // Only called on OK.
            CaFindSettings.FindUiMode = _rbOverlay.Checked
                ? CaFindSettings.ModeOverlay : CaFindSettings.ModePad;
            return true;   // allow the dialog to close
        }

        private void BuildUi()
        {
            if (_built) return;
            _built = true;

            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12),
            };

            var lblIntro = new Label
            {
                Text = "Ctrl+F / Ctrl+H in the CA Editor and CA Embeditor open:",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6),
            };

            _rbPad = new RadioButton
            {
                Text = "CA Find/Replace pad — dockable IDE pad (results grid, search tabs)",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 2),
            };

            _rbOverlay = new RadioButton
            {
                Text = "In-editor overlay — floating quick find (VS Code-style); Ctrl+Shift+F lists all matches",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 10),
            };

            var lblNote = new Label
            {
                Text = "Both use the same search engine and shared history. Ctrl+Alt+F always opens the pad.\r\n" +
                       "Changes take effect the next time you open an editor (already-open editors are not changed).",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 0),
            };

            layout.Controls.Add(lblIntro);
            layout.Controls.Add(_rbPad);
            layout.Controls.Add(_rbOverlay);
            layout.Controls.Add(lblNote);
            Controls.Add(layout);
        }
    }
}
