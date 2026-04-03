using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Manages tabs using a Panel-based approach (MultiTerminal HudTabContainer pattern).
    /// Tab strip is a custom-painted Panel hidden when only 1 tab exists.
    /// Content area is a Panel where each tab's control is shown/hidden via Visible.
    /// This avoids WinForms TabControl reliability issues with WebView2 initialization.
    /// </summary>
    public class TabManager : IDisposable
    {
        private readonly List<TerminalTab> _tabs = new List<TerminalTab>();
        private readonly Panel _tabStrip;
        private readonly Panel _contentArea;
        private TerminalTab _activeTab;
        private int _terminalCounter;
        private bool _isDarkTheme = true;

        private const int TabPadding = 12;
        private const int CloseButtonWidth = 18;

        public event EventHandler<TerminalTab> ActiveTabChanged;
        public event EventHandler<TerminalTab> TabAdded;
        public event EventHandler<TerminalTab> TabRemoved;

        public TerminalTab ActiveTab { get { return _activeTab; } }
        public IReadOnlyList<TerminalTab> Tabs { get { return _tabs.AsReadOnly(); } }

        public TabManager(Panel tabStrip, Panel contentArea)
        {
            _tabStrip = tabStrip;
            _contentArea = contentArea;
            _tabStrip.Paint += OnTabStripPaint;
            _tabStrip.MouseClick += OnTabStripMouseClick;
        }

        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            _tabStrip.BackColor = isDark ? Color.FromArgb(24, 24, 37) : Color.FromArgb(210, 214, 222);
            _tabStrip.Invalidate();
        }

        /// <summary>Create the Home tab (non-closable, always first, visible immediately).</summary>
        public TerminalTab CreateHomeTab(Control homeControl)
        {
            var tab = new TerminalTab
            {
                Name = "Home",
                IsHome = true,
                IsClosable = false,
                ContentControl = homeControl
            };

            homeControl.Dock = DockStyle.Fill;
            homeControl.Visible = true;
            _contentArea.Controls.Add(homeControl);

            _tabs.Insert(0, tab);
            _activeTab = tab;
            UpdateTabStripVisibility();

            System.Diagnostics.Debug.WriteLine("[TabManager] CreateHomeTab: tabs=" + _tabs.Count + " tabStrip.Visible=" + _tabStrip.Visible);
            TabAdded?.Invoke(this, tab);
            return tab;
        }

        /// <summary>Create a new terminal tab (starts hidden; caller must call ActivateTab).</summary>
        public TerminalTab CreateTerminalTab(string name, WebViewTerminalRenderer renderer)
        {
            _terminalCounter++;
            var tab = new TerminalTab
            {
                Name = string.IsNullOrEmpty(name) ? "Terminal " + _terminalCounter : name,
                IsHome = false,
                IsClosable = true,
                Renderer = renderer,
                ContentControl = renderer
            };

            renderer.Dock = DockStyle.Fill;
            renderer.Visible = false;   // hidden until ActivateTab
            _contentArea.Controls.Add(renderer);

            _tabs.Add(tab);
            UpdateTabStripVisibility();

            System.Diagnostics.Debug.WriteLine("[TabManager] CreateTerminalTab: " + tab.Name + " tabs=" + _tabs.Count + " tabStrip.Visible=" + _tabStrip.Visible);
            TabAdded?.Invoke(this, tab);
            return tab;
        }

        /// <summary>Switch to a tab by ID.</summary>
        public void ActivateTab(string tabId)
        {
            var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
            System.Diagnostics.Debug.WriteLine("[TabManager] ActivateTab: " + tabId + " found=" + (tab != null));
            if (tab == null || tab == _activeTab) return;

            foreach (var t in _tabs)
            {
                if (t.ContentControl != null)
                    t.ContentControl.Visible = (t == tab);
            }

            _activeTab = tab;
            _tabStrip.Invalidate();
            ActiveTabChanged?.Invoke(this, _activeTab);
        }

        /// <summary>Close a tab by ID. Cannot close the Home tab.</summary>
        public void CloseTab(string tabId)
        {
            var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab == null || tab.IsHome) return;

            bool wasActive = tab == _activeTab;

            if (tab.ContentControl != null)
                _contentArea.Controls.Remove(tab.ContentControl);

            _tabs.Remove(tab);
            TabRemoved?.Invoke(this, tab);
            tab.Dispose();

            UpdateTabStripVisibility();

            if (wasActive)
            {
                _activeTab = null;
                if (_tabs.Count > 0)
                    ActivateTab(_tabs[0].Id);
            }
        }

        /// <summary>Find a tab by ID.</summary>
        public TerminalTab FindTab(string tabId)
        {
            return _tabs.FirstOrDefault(t => t.Id == tabId);
        }

        private void UpdateTabStripVisibility()
        {
            bool shouldShow = _tabs.Count > 1;
            if (_tabStrip.Visible != shouldShow)
                _tabStrip.Visible = shouldShow;
        }

        // -----------------------------------------------------------------
        // Tab strip rendering (custom paint — no WinForms TabControl)
        // -----------------------------------------------------------------

        private readonly List<TabHitArea> _hitAreas = new List<TabHitArea>();

        private void OnTabStripPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            _hitAreas.Clear();

            var activeBg    = _isDarkTheme ? Color.FromArgb(49, 50, 68)     : Color.FromArgb(198, 208, 228);
            var inactiveBg  = _isDarkTheme ? Color.FromArgb(30, 30, 46)     : Color.FromArgb(220, 224, 232);
            var activeText  = _isDarkTheme ? Color.FromArgb(205, 214, 244)  : Color.FromArgb(30,  30,  50);
            var inactText   = _isDarkTheme ? Color.FromArgb(108, 112, 134)  : Color.FromArgb(110, 114, 130);
            var closeColor  = _isDarkTheme ? Color.FromArgb(108, 112, 134)  : Color.FromArgb(120, 120, 130);
            int stripH      = _tabStrip.Height;

            int x = 2;
            using (var font = new Font("Segoe UI", 8.5f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            using (var csf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var closeFont = new Font("Segoe UI", 8f))
            {
                for (int i = 0; i < _tabs.Count; i++)
                {
                    var tab = _tabs[i];
                    bool isActive = (tab == _activeTab);

                    var titleSize = g.MeasureString(tab.Name, font);
                    int tabW = (int)titleSize.Width + TabPadding * 2;
                    if (tab.IsClosable) tabW += CloseButtonWidth;

                    var tabRect = new Rectangle(x, 2, tabW, stripH - 4);

                    using (var bg = new SolidBrush(isActive ? activeBg : inactiveBg))
                        g.FillRectangle(bg, tabRect);

                    var titleRect = new RectangleF(x + TabPadding, 2, titleSize.Width, stripH - 4);
                    using (var textBr = new SolidBrush(isActive ? activeText : inactText))
                        g.DrawString(tab.Name, font, textBr, titleRect, sf);

                    var hit = new TabHitArea { Tab = tab, Rect = tabRect };

                    if (tab.IsClosable)
                    {
                        var closeRect = new Rectangle(x + tabW - CloseButtonWidth - 2, 6, CloseButtonWidth - 4, stripH - 12);
                        hit.CloseRect = closeRect;
                        using (var closeBr = new SolidBrush(closeColor))
                            g.DrawString("\u2715", closeFont, closeBr, closeRect, csf);
                    }

                    _hitAreas.Add(hit);
                    x += tabW + 2;
                }
            }
        }

        private void OnTabStripMouseClick(object sender, MouseEventArgs e)
        {
            foreach (var hit in _hitAreas)
            {
                if (hit.Tab.IsClosable && hit.CloseRect.Contains(e.Location))
                {
                    CloseTab(hit.Tab.Id);
                    return;
                }
                if (hit.Rect.Contains(e.Location))
                {
                    ActivateTab(hit.Tab.Id);
                    return;
                }
            }
        }

        private class TabHitArea
        {
            public TerminalTab Tab;
            public Rectangle Rect;
            public Rectangle CloseRect;
        }

        public void Dispose()
        {
            for (int i = _tabs.Count - 1; i >= 0; i--)
                _tabs[i].Dispose();
            _tabs.Clear();
            _activeTab = null;
        }
    }
}
