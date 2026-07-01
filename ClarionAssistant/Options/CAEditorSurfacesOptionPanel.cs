using System;
using System.Drawing;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Services;

namespace ClarionAssistant.Options
{
    /// <summary>
    /// IDE Options pane: Options &gt; Clarion Assistant &gt; Editor Surfaces (ticket 1c0862e1).
    /// Lets the user turn the Monaco editor replacements off and fall back to the native Clarion
    /// surfaces. Registered as a child &lt;DialogPanel&gt; under /SharpDevelop/Dialogs/OptionsDialog
    /// (see ClarionAssistant.addin.template), the same mechanism the third-party "Upper Park
    /// Solutions" pane uses.
    ///
    /// Contract (confirmed by live reflection on ICSharpCode.SharpDevelop 2.1.0.2447):
    /// AbstractOptionPanel IS a UserControl, so we add plain WinForms controls directly.
    /// LoadPanelContents() fires on first display; StorePanelContents() fires ONLY on dialog OK
    /// (Cancel never calls it, so changes discard automatically). Parameterless public ctor required.
    /// Persistence goes through <see cref="CaEditorSettings"/> (shared settings.txt) — NOT
    /// PropertyService — so the editor/embeditor attach code reads the same keys.
    /// </summary>
    public class CAEditorSurfacesOptionPanel : AbstractOptionPanel
    {
        private CheckBox _chkSource;
        private CheckBox _chkEmbeditor;
        private CheckBox _chkLiveLinked;
        private TextBox _txtFileTypes;
        private bool _built;

        public CAEditorSurfacesOptionPanel()
        {
            // UI is built lazily in LoadPanelContents (fired on first show) — see BuildUi.
        }

        public override void LoadPanelContents()
        {
            BuildUi();

            // Populate from current settings each time the pane is shown.
            _chkSource.Checked = CaEditorSettings.MonacoSourceEnabled;
            _chkEmbeditor.Checked = CaEditorSettings.MonacoEmbeditorEnabled;
            _chkLiveLinked.Checked = CaEditorSettings.MonacoEmbeditorLiveLinked;
            _txtFileTypes.Text = CaEditorSettings.MonacoSourceFileTypes;
            UpdateFileTypesEnabled();
            UpdateLiveLinkedEnabled();
        }

        public override bool StorePanelContents()
        {
            // Only called on OK. Persist all three toggles.
            CaEditorSettings.MonacoSourceEnabled = _chkSource.Checked;
            CaEditorSettings.MonacoEmbeditorEnabled = _chkEmbeditor.Checked;
            CaEditorSettings.MonacoEmbeditorLiveLinked = _chkLiveLinked.Checked;

            // Blank file-types box => "all files"; otherwise keep the user's list verbatim
            // (CaEditorSettings normalizes when filtering).
            _txtFileTypes.Text = (_txtFileTypes.Text ?? string.Empty).Trim();
            CaEditorSettings.MonacoSourceFileTypes = _txtFileTypes.Text;

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

            var lblHeader = new Label
            {
                Text = "Monaco editor surfaces",
                Font = new Font(Control.DefaultFont, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
            };

            _chkSource = new CheckBox
            {
                Text = "Use the Monaco source editor (replaces the native Clarion text editor)",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 2),
            };

            _chkEmbeditor = new CheckBox
            {
                Text = "Use the Monaco embeditor (adds the right-click \"Open in CA Embeditor\" item)",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 2),
            };

            _chkLiveLinked = new CheckBox
            {
                Text = "Live-linked embeditor — keep the native embed open, save writes straight back (experimental)",
                AutoSize = true,
                Margin = new Padding(20, 0, 0, 10),   // indented: sub-option of the embeditor toggle
            };

            var lblFileTypes = new Label
            {
                Text = "Apply the Monaco source editor only to these file types (semicolon-separated; blank = all):",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2),
            };

            _txtFileTypes = new TextBox
            {
                Width = 320,
                Margin = new Padding(0, 0, 0, 10),
            };

            var lblNote = new Label
            {
                Text = "Changes take effect the next time you open a file (already-open editors are not changed).",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 0),
            };

            // Grey out the file-types box when the source overlay is off (it has no effect then).
            _chkSource.CheckedChanged += (s, e) => UpdateFileTypesEnabled();
            // Grey out the live-linked sub-option when the embeditor itself is off (it has no effect then).
            _chkEmbeditor.CheckedChanged += (s, e) => UpdateLiveLinkedEnabled();

            layout.Controls.Add(lblHeader);
            layout.Controls.Add(_chkSource);
            layout.Controls.Add(_chkEmbeditor);
            layout.Controls.Add(_chkLiveLinked);
            layout.Controls.Add(lblFileTypes);
            layout.Controls.Add(_txtFileTypes);
            layout.Controls.Add(lblNote);

            this.Controls.Add(layout);
        }

        private void UpdateFileTypesEnabled()
        {
            if (_txtFileTypes != null && _chkSource != null)
                _txtFileTypes.Enabled = _chkSource.Checked;
        }

        private void UpdateLiveLinkedEnabled()
        {
            if (_chkLiveLinked != null && _chkEmbeditor != null)
                _chkLiveLinked.Enabled = _chkEmbeditor.Checked;
        }
    }
}
