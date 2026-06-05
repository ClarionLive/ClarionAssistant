using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ClarionAssistant.Dialogs
{
    /// <summary>
    /// Procedure picker for the Modern Embeditor multi-editor flow. Multi-select + filter; returns the
    /// chosen procedure names so each can be opened as its own snapshot tab.
    /// </summary>
    public class ModernEmbeditorPickerDialog : Form
    {
        private readonly List<string> _all;
        private readonly TextBox _filter;
        private readonly ListBox _list;

        public List<string> SelectedProcedures { get; private set; } = new List<string>();

        public ModernEmbeditorPickerDialog(IEnumerable<string> procedureNames)
        {
            _all = (procedureNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Text = "Open Procedures in CA Embeditor";
            ClientSize = new Size(420, 500);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));  // hint
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));  // filter
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // list
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));  // buttons

            var hint = new Label
            {
                Text = "Select one or more procedures (Ctrl / Shift for multi):",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _filter = new TextBox { Dock = DockStyle.Fill };
            _filter.TextChanged += (s, e) => ApplyFilter();

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                SelectionMode = SelectionMode.MultiExtended,
                IntegralHeight = false,
                Font = new Font("Segoe UI", 9.5f)
            };
            _list.DoubleClick += (s, e) => { if (_list.SelectedItems.Count > 0) Accept(); };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var open = new Button { Text = "Open", Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
            open.Click += (s, e) => Accept();
            buttons.Controls.Add(open);
            buttons.Controls.Add(cancel);

            root.Controls.Add(hint, 0, 0);
            root.Controls.Add(_filter, 0, 1);
            root.Controls.Add(_list, 0, 2);
            root.Controls.Add(buttons, 0, 3);
            Controls.Add(root);

            AcceptButton = open;
            CancelButton = cancel;

            ApplyFilter();
        }

        private void Accept()
        {
            SelectedProcedures = _list.SelectedItems.Cast<string>().ToList();
        }

        private void ApplyFilter()
        {
            string f = (_filter.Text ?? "").Trim();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var n in _all)
                if (f.Length == 0 || n.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                    _list.Items.Add(n);
            _list.EndUpdate();
        }
    }
}
