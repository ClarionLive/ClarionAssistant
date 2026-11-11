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
    ///
    /// When the tabs do not fit the strip (tab overflow): they first shrink, titles ending in an
    /// ellipsis, down to <see cref="MinTabWidth"/>. Past that Home stays pinned on the left, as many whole
    /// tabs as fit share the rest, ‹ › arrows (bright while tabs are hidden on their side) and the mouse
    /// wheel scroll one tab at a time, the active tab is always brought into view, and an overflow button
    /// on the right lists every tab, each with its own close button. Before this the strip simply drew
    /// past its right edge, so a fourth tab's close button could not be reached at all.
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
        private const int TabGap = 2;
        // A shrunk tab is never narrower than this: its close button and the start of its title stay visible.
        // 96 (the first version) left "CA2 · Sa…", which tells tabs apart by number only; 130 keeps
        // "CA2 · SampleSol…", and overflows sooner.
        private const int MinTabWidth = 130;
        private const int OverflowButtonWidth = 26;
        private const int ArrowWidth = 16;
        // The right-hand part of an overflow-menu row that acts as that row's close button (the ✕ is
        // drawn there as the item's shortcut text).
        private const int MenuCloseZone = 40;
        private const int MenuMaxTitleLength = 60;

        // Overflow state, rebuilt by every paint (layout depends on the strip's width and the titles).
        // The strip scrolls by whole tabs: _firstVisible is the first scrolling tab shown (Home not counted),
        // and only tabs that fit whole are drawn. A tab cut by the edge looked like a tab shrunk further than
        // the others, which is what the first version did.
        private int _firstVisible;
        private int _maxFirstVisible;
        private bool _revealActive = true;     // next paint scrolls the active tab into view
        private Rectangle _overflowRect;       // the overflow button; empty while every tab fits
        private Rectangle _leftArrowRect;      // ‹ / ›: scroll one tab; empty while every tab fits
        private Rectangle _rightArrowRect;
        private ContextMenuStrip _overflowMenu;
        private bool _menuCloseClick;          // the pending overflow-menu click landed on a row's ✕
        private readonly ToolTip _toolTip = new ToolTip();
        private string _toolTipText;

        public event EventHandler<TerminalTab> ActiveTabChanged;
        public event EventHandler<TerminalTab> TabAdded;
        public event EventHandler<TerminalTab> TabRemoved;

        /// <summary>
        /// Asked before a middle-click (pressing the mouse wheel) on a tab closes it; return false to keep
        /// the tab. Null closes without asking. A middle-click close is common in browsers but not known
        /// to everyone, so the host warns before the first one rather than let it surprise.
        /// </summary>
        public Func<TerminalTab, bool> ConfirmMiddleClickClose { get; set; }

        public TerminalTab ActiveTab { get { return _activeTab; } }
        public IReadOnlyList<TerminalTab> Tabs { get { return _tabs.AsReadOnly(); } }

        public TabManager(Panel tabStrip, Panel contentArea)
        {
            _tabStrip = tabStrip;
            _contentArea = contentArea;
            _tabStrip.Paint += OnTabStripPaint;
            _tabStrip.MouseClick += OnTabStripMouseClick;
            _tabStrip.MouseWheel += OnTabStripMouseWheel;
            _tabStrip.MouseMove += OnTabStripMouseMove;
            _tabStrip.MouseLeave += (s, e) => SetToolTip(null);
            // The layout depends on the strip's width, and a Panel does not repaint on resize by itself.
            _tabStrip.Resize += (s, e) => { _revealActive = true; _tabStrip.Invalidate(); };
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
            RefreshTabStrip();

            System.Diagnostics.Debug.WriteLine("[TabManager] CreateHomeTab: tabs=" + _tabs.Count + " tabStrip.Visible=" + _tabStrip.Visible);
            TabAdded?.Invoke(this, tab);
            return tab;
        }

        /// <summary>Create a non-terminal content tab (starts hidden; caller must call ActivateTab).</summary>
        public TerminalTab CreateContentTab(string name, Control content)
        {
            var tab = new TerminalTab
            {
                Name = name ?? "Content",
                IsHome = false,
                IsClosable = true,
                ContentControl = content
            };

            content.Dock = DockStyle.Fill;
            content.Visible = false;
            _contentArea.Controls.Add(content);

            _tabs.Add(tab);
            RefreshTabStrip();

            System.Diagnostics.Debug.WriteLine("[TabManager] CreateContentTab: " + tab.Name + " tabs=" + _tabs.Count + " tabStrip.Visible=" + _tabStrip.Visible);
            TabAdded?.Invoke(this, tab);
            return tab;
        }

        /// <summary>Create a new terminal tab (starts hidden; caller must call ActivateTab).</summary>
        public TerminalTab CreateTerminalTab(string name, WebViewTerminalRenderer renderer)
        {
            _terminalCounter++;
            // "Terminal N" is matched by CaAgentIdentity.IsDefaultTabName: keep the two in step.
            var tab = new TerminalTab
            {
                Name = string.IsNullOrEmpty(name) ? "Terminal " + _terminalCounter : name,
                DefaultName = "Terminal " + _terminalCounter,
                IsHome = false,
                IsClosable = true,
                Renderer = renderer,
                ContentControl = renderer
            };

            renderer.Dock = DockStyle.Fill;
            renderer.Visible = false;   // hidden until ActivateTab
            _contentArea.Controls.Add(renderer);

            _tabs.Add(tab);
            RefreshTabStrip();

            System.Diagnostics.Debug.WriteLine("[TabManager] CreateTerminalTab: " + tab.Name + " tabs=" + _tabs.Count + " tabStrip.Visible=" + _tabStrip.Visible);
            TabAdded?.Invoke(this, tab);
            return tab;
        }

        /// <summary>Rename a tab and repaint the tab strip so the new text
        /// shows immediately. No-op if tab is null or newName matches.</summary>
        public void RenameTab(TerminalTab tab, string newName)
        {
            if (tab == null || string.IsNullOrEmpty(newName)) return;
            if (string.Equals(tab.Name, newName, StringComparison.Ordinal)) return;
            tab.Name = newName;
            _tabStrip.Invalidate();
        }

        /// <summary>Switch to a tab by ID.</summary>
        public void ActivateTab(string tabId)
        {
            var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
            System.Diagnostics.Debug.WriteLine("[TabManager] ActivateTab: " + tabId + " found=" + (tab != null));
            if (tab == null) return;

            // Clicking the active tab while it is partly scrolled out of view brings it fully into view.
            _revealActive = true;
            if (tab == _activeTab) { _tabStrip.Invalidate(); return; }

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
            try
            {
                // Disposal runs even if a handler throws: the tab is already unreachable, so a skipped
                // Dispose would leave its ConPTY process running with no tab to close it from.
                try { TabRemoved?.Invoke(this, tab); }
                finally { tab.Dispose(); }
            }
            finally
            {
                // The tab is gone from _tabs whatever a handler or Dispose does: the strip and the
                // active tab must follow, or the display is left showing a tab that no longer exists.
                RefreshTabStrip();

                if (wasActive)
                {
                    _activeTab = null;
                    if (_tabs.Count > 0)
                        ActivateTab(_tabs[0].Id);
                }
            }
        }

        /// <summary>Find a tab by ID.</summary>
        public TerminalTab FindTab(string tabId)
        {
            return _tabs.FirstOrDefault(t => t.Id == tabId);
        }

        // Called after every add and close. Always repaints: the strip is custom-painted and the hit
        // areas clicks are tested against are rebuilt only by OnTabStripPaint (ticket 6a4b4d71).
        private void RefreshTabStrip()
        {
            // Always shown once Home exists (d4e941e3): hiding it while Home was the only tab made the strip
            // appear with the first opened tab and vanish again with the last closed one.
            bool shouldShow = _tabs.Count > 0;
            if (_tabStrip.Visible != shouldShow)
                _tabStrip.Visible = shouldShow;
            _tabStrip.Invalidate();
        }

        // -----------------------------------------------------------------
        // Tab strip layout (shrink, then scroll + overflow button)
        // -----------------------------------------------------------------

        private struct TabSlot
        {
            public TerminalTab Tab;
            public int X;          // strip coordinates, scroll already applied
            public int Width;
            public bool Pinned;    // Home: never scrolls
            public bool Truncated; // the title does not fit and ends in an ellipsis
        }

        // The width a tab would take with its whole title.
        private static int NaturalWidth(Graphics g, Font font, TerminalTab tab)
        {
            int w = (int)Math.Ceiling(g.MeasureString(tab.Name ?? "", font).Width) + TabPadding * 2;
            if (tab.IsClosable) w += CloseButtonWidth;
            return w;
        }

        // Width of the scrolling tabs side by side when none is wider than cap.
        private static int RowWidth(List<int> natural, int cap)
        {
            if (natural.Count == 0) return 0;
            int sum = TabGap * (natural.Count - 1);
            foreach (int w in natural) sum += Math.Min(w, cap);
            return sum;
        }

        // Lays the tabs out for the strip's current width and sets _overflowRect, _maxFirstVisible and the
        // clamped _firstVisible. Returns only the tabs drawn. viewport receives the band the scrolling tabs
        // are shown in.
        private List<TabSlot> LayoutTabs(Graphics g, Font font, out Rectangle viewport)
        {
            int stripW = _tabStrip.ClientSize.Width;
            int stripH = _tabStrip.Height;
            var slots = new List<TabSlot>();

            int x = TabGap;
            var scrolling = new List<TerminalTab>();
            var natural = new List<int>();
            foreach (var tab in _tabs)
            {
                if (tab.IsHome)
                {
                    int w = NaturalWidth(g, font, tab);
                    slots.Add(new TabSlot { Tab = tab, X = x, Width = w, Pinned = true });
                    x += w + TabGap;
                }
                else
                {
                    scrolling.Add(tab);
                    natural.Add(NaturalWidth(g, font, tab));
                }
            }

            int viewLeft = x;
            int avail = Math.Max(0, stripW - TabGap - viewLeft);
            int maxNatural = natural.Count > 0 ? natural.Max() : 0;
            int cap = maxNatural;
            bool overflow = false;

            if (RowWidth(natural, maxNatural) > avail)
            {
                if (RowWidth(natural, MinTabWidth) <= avail)
                {
                    // Shrink: the widest cap that fits (binary search; RowWidth grows with cap).
                    int lo = MinTabWidth, hi = maxNatural;
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) / 2;
                        if (RowWidth(natural, mid) <= avail) lo = mid; else hi = mid - 1;
                    }
                    cap = lo;
                }
                else
                {
                    overflow = true;
                }
            }

            if (!overflow)
            {
                _overflowRect = _leftArrowRect = _rightArrowRect = Rectangle.Empty;
                _firstVisible = _maxFirstVisible = 0;
                _revealActive = false;
                viewport = new Rectangle(viewLeft, 0, avail, stripH);
                int sx = viewLeft;
                for (int i = 0; i < scrolling.Count; i++)
                {
                    int w = Math.Min(natural[i], cap);
                    slots.Add(new TabSlot { Tab = scrolling[i], X = sx, Width = w, Truncated = w < natural[i] });
                    sx += w + TabGap;
                }
                return slots;
            }

            // Overflow: [Home] ‹ [k whole tabs, filling the band] › [⋯]. The arrows are there whenever tabs
            // are hidden; each is bright while there are tabs hidden on its side, so the strip shows there
            // is more. Only whole tabs are drawn: one cut by an edge looked like a tab shrunk more than the rest.
            int tabH = Math.Max(0, stripH - 4);
            _leftArrowRect = new Rectangle(viewLeft, 2, ArrowWidth, tabH);
            _overflowRect = new Rectangle(stripW - TabGap - OverflowButtonWidth, 2, OverflowButtonWidth, tabH);
            _rightArrowRect = new Rectangle(_overflowRect.Left - TabGap - ArrowWidth, 2, ArrowWidth, tabH);
            int bandLeft = _leftArrowRect.Right + TabGap;
            int band = Math.Max(0, _rightArrowRect.Left - TabGap - bandLeft);
            viewport = new Rectangle(bandLeft, 0, band, stripH);

            int count = scrolling.Count;
            int shown = Math.Max(1, Math.Min(count, (band + TabGap) / (MinTabWidth + TabGap)));
            _maxFirstVisible = count - shown;

            int active = _activeTab != null ? scrolling.IndexOf(_activeTab) : -1;
            if (_revealActive && active >= 0)
            {
                if (active < _firstVisible) _firstVisible = active;
                else if (active >= _firstVisible + shown) _firstVisible = active - shown + 1;
            }
            _revealActive = false;
            _firstVisible = Math.Max(0, Math.Min(_firstVisible, _maxFirstVisible));

            // The shown tabs share the band equally (the remainder pixel by pixel), so it ends flush with ›,
            // a short title included: a tab narrower than its share left a gap before the arrow.
            int share = Math.Max(0, band - TabGap * (shown - 1));
            int each = share / shown, extra = share - each * shown;
            int x2 = bandLeft;
            for (int k = 0; k < shown; k++)
            {
                int i = _firstVisible + k;
                int w = each + (k < extra ? 1 : 0);
                slots.Add(new TabSlot { Tab = scrolling[i], X = x2, Width = w, Truncated = w < natural[i] });
                x2 += w + TabGap;
            }
            return slots;
        }

        // Scroll the overflowing strip by whole tabs (negative = towards the first tab).
        private void ScrollTabs(int by)
        {
            int next = Math.Max(0, Math.Min(_firstVisible + by, _maxFirstVisible));
            if (next == _firstVisible) return;
            _firstVisible = next;
            _tabStrip.Invalidate();
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

            using (var font = new Font("Segoe UI", 8.5f))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            using (var csf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var closeFont = new Font("Segoe UI", 8f))
            {
                Rectangle viewport;
                var slots = LayoutTabs(g, font, out viewport);

                foreach (var slot in slots)
                {
                    var tab = slot.Tab;
                    bool isActive = (tab == _activeTab);
                    var tabRect = new Rectangle(slot.X, 2, slot.Width, stripH - 4);

                    // A scrolling tab is drawn, and clickable, only inside the band between Home and the
                    // overflow button; one cut by its edge shows its visible part.
                    Rectangle visible = slot.Pinned ? tabRect : Rectangle.Intersect(tabRect, viewport);
                    if (visible.Width <= 0) continue;

                    var oldClip = g.Clip;
                    if (!slot.Pinned) g.SetClip(viewport);
                    try
                    {
                        using (var bg = new SolidBrush(isActive ? activeBg : inactiveBg))
                            g.FillRectangle(bg, tabRect);

                        int titleW = slot.Width - TabPadding * 2 - (tab.IsClosable ? CloseButtonWidth : 0);
                        var titleRect = new RectangleF(slot.X + TabPadding, 2, Math.Max(0, titleW) + 1, stripH - 4);
                        using (var textBr = new SolidBrush(isActive ? activeText : inactText))
                            g.DrawString(tab.Name, font, textBr, titleRect, sf);

                        var hit = new TabHitArea { Tab = tab, Rect = visible, Truncated = slot.Truncated };

                        if (tab.IsClosable)
                        {
                            var closeRect = new Rectangle(slot.X + slot.Width - CloseButtonWidth - 2, 6, CloseButtonWidth - 4, stripH - 12);
                            // Only a close button that is wholly in view can be clicked.
                            if (slot.Pinned || viewport.Contains(closeRect))
                                hit.CloseRect = closeRect;
                            using (var closeBr = new SolidBrush(closeColor))
                                g.DrawString("✕", closeFont, closeBr, closeRect, csf);
                        }

                        _hitAreas.Add(hit);
                    }
                    finally
                    {
                        g.Clip = oldClip;
                        oldClip.Dispose();
                    }
                }

                if (!_overflowRect.IsEmpty)
                {
                    // Bright = tabs hidden that way, faint = at that end.
                    var faint = _isDarkTheme ? Color.FromArgb(69, 71, 90) : Color.FromArgb(180, 184, 196);
                    DrawArrow(g, _leftArrowRect, true, _firstVisible > 0 ? activeText : faint);
                    DrawArrow(g, _rightArrowRect, false, _firstVisible < _maxFirstVisible ? activeText : faint);
                    DrawOverflowButton(g, inactiveBg, activeText);
                }
            }
        }

        private static void DrawArrow(Graphics g, Rectangle r, bool left, Color color)
        {
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 1.8f))
            {
                if (left)
                    g.DrawLines(pen, new[] { new Point(cx + 2, cy - 5), new Point(cx - 3, cy), new Point(cx + 2, cy + 5) });
                else
                    g.DrawLines(pen, new[] { new Point(cx - 2, cy - 5), new Point(cx + 3, cy), new Point(cx - 2, cy + 5) });
            }
            g.SmoothingMode = oldMode;
        }

        // Three dots ("more"): drawn rather than taken from a font, so they look the same everywhere.
        private void DrawOverflowButton(Graphics g, Color back, Color dots)
        {
            using (var bg = new SolidBrush(back))
                g.FillRectangle(bg, _overflowRect);
            int cy = _overflowRect.Y + _overflowRect.Height / 2;
            int cx = _overflowRect.X + _overflowRect.Width / 2;
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(dots))
            {
                for (int i = -1; i <= 1; i++)
                    g.FillEllipse(br, cx + i * 6 - 2, cy - 2, 4, 4);
            }
            g.SmoothingMode = oldMode;
        }

        private void OnTabStripMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !_overflowRect.IsEmpty && _overflowRect.Contains(e.Location))
            {
                ShowOverflowMenu();
                return;
            }
            if (e.Button == MouseButtons.Left && !_leftArrowRect.IsEmpty && _leftArrowRect.Contains(e.Location))
            {
                ScrollTabs(-1);
                return;
            }
            if (e.Button == MouseButtons.Left && !_rightArrowRect.IsEmpty && _rightArrowRect.Contains(e.Location))
            {
                ScrollTabs(1);
                return;
            }

            foreach (var hit in _hitAreas)
            {
                // A hit area from before the last paint can name a tab that has since closed: repaint
                // rather than act on it, so the next click lands on the strip as it really is. Areas
                // are in paint order, so tabs LEFT of the closed one (whose positions haven't moved)
                // are matched before this point and still work.
                if (!_tabs.Contains(hit.Tab))
                {
                    _tabStrip.Invalidate();
                    return;
                }
                if (e.Button == MouseButtons.Middle)
                {
                    if (!hit.Rect.Contains(e.Location)) continue;
                    if (hit.Tab.IsClosable)
                    {
                        var confirm = ConfirmMiddleClickClose;
                        if (confirm == null || confirm(hit.Tab))
                            CloseTab(hit.Tab.Id);
                    }
                    return;
                }
                if (e.Button == MouseButtons.Left && hit.Tab.IsClosable && hit.CloseRect.Contains(e.Location))
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

        private void OnTabStripMouseWheel(object sender, MouseEventArgs e)
        {
            if (_overflowRect.IsEmpty || e.Delta == 0) return;
            // Wheel up / away scrolls one tab towards the first tabs.
            ScrollTabs(-Math.Sign(e.Delta));
        }

        // A shrunk tab shows its whole title as a tooltip; the overflow button says what it does.
        private void OnTabStripMouseMove(object sender, MouseEventArgs e)
        {
            string text = null;
            int before = _firstVisible;
            int after = _maxFirstVisible - _firstVisible;
            if (!_overflowRect.IsEmpty && _overflowRect.Contains(e.Location))
                text = "All " + _tabs.Count(t => !t.IsHome) + " tabs";   // as listed: Home is not
            else if (!_leftArrowRect.IsEmpty && _leftArrowRect.Contains(e.Location))
                text = before > 0 ? before + (before == 1 ? " more tab" : " more tabs") + " to the left" : "No more tabs to the left";
            else if (!_rightArrowRect.IsEmpty && _rightArrowRect.Contains(e.Location))
                text = after > 0 ? after + (after == 1 ? " more tab" : " more tabs") + " to the right" : "No more tabs to the right";
            else
            {
                foreach (var hit in _hitAreas)
                {
                    if (!hit.Rect.Contains(e.Location)) continue;
                    if (hit.Truncated && _tabs.Contains(hit.Tab)) text = hit.Tab.Name;
                    break;
                }
            }
            SetToolTip(text);
        }

        private void SetToolTip(string text)
        {
            if (string.Equals(text, _toolTipText, StringComparison.Ordinal)) return;
            _toolTipText = text;
            _toolTip.Hide(_tabStrip);
            _toolTip.SetToolTip(_tabStrip, text ?? "");
        }

        // -----------------------------------------------------------------
        // Overflow menu: every tab, the active one checked; a click on a row's ✕ closes that tab and
        // leaves the menu open, so several can be closed in one go.
        // -----------------------------------------------------------------

        private void EnsureOverflowMenu()
        {
            if (_overflowMenu != null) return;
            _overflowMenu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            _overflowMenu.Opening += (s, e) => _menuCloseClick = false;
            _overflowMenu.Closing += (s, e) =>
            {
                // The ✕ of a row was clicked: keep the menu open for the next one.
                if (_menuCloseClick && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                    e.Cancel = true;
            };
        }

        private void ShowOverflowMenu()
        {
            EnsureOverflowMenu();
            RebuildOverflowMenu();
            // Follows the strip's theme; the default renderer is always the light system menu.
            _overflowMenu.Renderer = new OverflowMenuRenderer(_isDarkTheme);
            _overflowMenu.BackColor = OverflowMenuRenderer.Palette(_isDarkTheme).Back;
            SetToolTip(null);
            _overflowMenu.Show(_tabStrip, new Point(_overflowRect.Right, _overflowRect.Bottom), ToolStripDropDownDirection.BelowLeft);
        }

        private void RebuildOverflowMenu()
        {
            var old = _overflowMenu.Items.Cast<ToolStripItem>().ToList();
            _overflowMenu.Items.Clear();
            foreach (var item in old) item.Dispose();

            foreach (var tab in _tabs)
            {
                if (tab.IsHome) continue;   // always on the strip; the list is for the tabs that scroll
                string title = tab.Name ?? "";
                if (title.Length > MenuMaxTitleLength) title = title.Substring(0, MenuMaxTitleLength - 1) + "…";
                var item = new ToolStripMenuItem(title.Replace("&", "&&"))
                {
                    Tag = tab,
                    Checked = tab == _activeTab
                };
                if (tab.Name != null && tab.Name.Length > MenuMaxTitleLength) item.ToolTipText = tab.Name;
                if (tab.IsClosable)
                {
                    // Drawn right-aligned in the shortcut column; MenuCloseZone is where it lands.
                    item.ShortcutKeyDisplayString = "✕";
                    item.MouseDown += OnOverflowItemMouseDown;
                }
                item.Click += OnOverflowItemClick;
                _overflowMenu.Items.Add(item);
            }
        }

        private void OnOverflowItemMouseDown(object sender, MouseEventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            if (item == null || _overflowMenu == null) return;
            // Item.Bounds are in the menu's client coordinates; read the cursor in the same terms rather
            // than rely on how the event's own coordinates are translated.
            var p = _overflowMenu.PointToClient(Control.MousePosition);
            _menuCloseClick = e.Button == MouseButtons.Left && p.X >= item.Bounds.Right - MenuCloseZone;
        }

        private void OnOverflowItemClick(object sender, EventArgs e)
        {
            bool close = _menuCloseClick;
            _menuCloseClick = false;
            var item = sender as ToolStripMenuItem;
            var tab = item != null ? item.Tag as TerminalTab : null;
            if (tab == null || !_tabs.Contains(tab)) return;

            if (close && tab.IsClosable)
            {
                CloseTab(tab.Id);
                if (_overflowMenu != null)
                {
                    _overflowMenu.Items.Remove(item);
                    item.Dispose();
                    foreach (ToolStripItem other in _overflowMenu.Items)
                    {
                        var mi = other as ToolStripMenuItem;
                        if (mi != null) mi.Checked = mi.Tag == _activeTab;
                    }
                }
                return;
            }
            ActivateTab(tab.Id);
        }

        /// <summary>The overflow menu in the tab strip's colours, dark or light.</summary>
        internal sealed class OverflowMenuRenderer : ToolStripProfessionalRenderer
        {
            internal sealed class Colors
            {
                public Color Back, Hover, Border, Text, Dim, CheckBack;
            }

            internal static Colors Palette(bool dark)
            {
                return dark
                    ? new Colors
                      {
                          Back = Color.FromArgb(30, 30, 46), Hover = Color.FromArgb(49, 50, 68),
                          Border = Color.FromArgb(69, 71, 90), Text = Color.FromArgb(205, 214, 244),
                          Dim = Color.FromArgb(127, 132, 156), CheckBack = Color.FromArgb(49, 50, 68)
                      }
                    : new Colors
                      {
                          Back = Color.FromArgb(240, 242, 246), Hover = Color.FromArgb(198, 208, 228),
                          Border = Color.FromArgb(170, 176, 190), Text = Color.FromArgb(30, 30, 50),
                          Dim = Color.FromArgb(110, 114, 130), CheckBack = Color.FromArgb(220, 224, 232)
                      };
            }

            private readonly Colors _c;

            public OverflowMenuRenderer(bool dark) : base(new Table(Palette(dark)))
            {
                _c = Palette(dark);
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                // The row's ✕ (its shortcut text) is dimmer than the title, like the strip's own ✕.
                e.TextColor = e.Text == "✕" ? _c.Dim : _c.Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // The base draws a black check bitmap, invisible on the dark background: draw one instead.
                var r = e.ImageRectangle;
                if (r.Width <= 0 || r.Height <= 0) return;
                using (var back = new SolidBrush(_c.CheckBack))
                    e.Graphics.FillRectangle(back, r);
                var old = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(_c.Text, 1.6f))
                {
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                        new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                        new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f)
                    });
                }
                e.Graphics.SmoothingMode = old;
            }

            private sealed class Table : ProfessionalColorTable
            {
                private readonly Colors _c;
                public Table(Colors c) { _c = c; UseSystemColors = false; }

                public override Color ToolStripDropDownBackground { get { return _c.Back; } }
                public override Color ImageMarginGradientBegin { get { return _c.Back; } }
                public override Color ImageMarginGradientMiddle { get { return _c.Back; } }
                public override Color ImageMarginGradientEnd { get { return _c.Back; } }
                public override Color MenuBorder { get { return _c.Border; } }
                public override Color MenuItemBorder { get { return _c.Hover; } }
                public override Color MenuItemSelected { get { return _c.Hover; } }
                public override Color MenuItemSelectedGradientBegin { get { return _c.Hover; } }
                public override Color MenuItemSelectedGradientEnd { get { return _c.Hover; } }
                public override Color MenuItemPressedGradientBegin { get { return _c.Hover; } }
                public override Color MenuItemPressedGradientEnd { get { return _c.Hover; } }
                public override Color CheckBackground { get { return _c.CheckBack; } }
                public override Color CheckSelectedBackground { get { return _c.CheckBack; } }
                public override Color CheckPressedBackground { get { return _c.CheckBack; } }
                public override Color SeparatorDark { get { return _c.Border; } }
                public override Color SeparatorLight { get { return _c.Border; } }
            }
        }

        private class TabHitArea
        {
            public TerminalTab Tab;
            public Rectangle Rect;
            public Rectangle CloseRect;
            public bool Truncated;
        }

        public void Dispose()
        {
            for (int i = _tabs.Count - 1; i >= 0; i--)
                _tabs[i].Dispose();
            _tabs.Clear();
            _activeTab = null;
            if (_overflowMenu != null) { _overflowMenu.Dispose(); _overflowMenu = null; }
            _toolTip.Dispose();
        }
    }
}
