// Harness for Terminal\TabManager.cs when the tabs do not fit the strip. Before this the strip drew
// every tab at full width past its right edge: with four or more tabs the last one's close button was
// off-screen and could not be clicked at all.
//
// Compiled against the REAL TabManager.cs and TerminalTab.cs (stubs: TabManager.Close.Stubs.cs) by
// Run-Tests.ps1. It never shows a window: paints are driven with DrawToBitmap, clicks and the wheel
// through the panel's OnMouseClick / OnMouseWheel. The overflow menu is built but never shown.
//
// What each check stops:
//   fits               - a wide strip changing: no overflow button, no tab shrunk
//   shrink             - tabs too wide for the strip but not for MinTabWidth shrink instead of
//                        overflowing, and every close button stays on the strip
//   overflow           - past MinTabWidth: the overflow button appears, nothing drawn or clickable
//                        past it, and the active tab (the last one opened) is in full view
//   wheel              - the wheel scrolls the tabs, within bounds
//   overflow menu      - lists every tab, active checked, '&' shown literally; a click on a row's
//                        close zone closes that tab, on the rest of the row activates it
//   middle click       - asks first; "keep" keeps the tab, "close" closes it, Home is never closed,
//                        no confirm hook closes straight away
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ClarionAssistant.Terminal;

static class TabManagerOverflowTest
{
    static int _failures, _assertions;

    static void Check(bool ok, string what)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what); }
        else Console.WriteLine("  ok   " + what);
    }

    const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    class Rig
    {
        public Panel Strip;
        public Panel Content = new Panel { Width = 800, Height = 400 };
        public TabManager Tabs;
        public TerminalTab Home;
        public List<TerminalTab> Opened = new List<TerminalTab>();

        public Rig(int width, params string[] names)
        {
            Strip = new Panel { Width = width, Height = 28 };
            Tabs = new TabManager(Strip, Content);
            Home = Tabs.CreateHomeTab(new Label());
            foreach (var n in names)
            {
                var t = Tabs.CreateContentTab(n, new Label());
                Tabs.ActivateTab(t.Id);   // as the host does with every new tab
                Opened.Add(t);
            }
            Paint();
        }

        public void Paint()
        {
            using (var bmp = new Bitmap(Strip.Width, Strip.Height))
                Strip.DrawToBitmap(bmp, new Rectangle(0, 0, Strip.Width, Strip.Height));
        }

        public void Mouse(string method, MouseButtons button, Point p, int delta)
        {
            typeof(Control).GetMethod(method, Priv)
                .Invoke(Strip, new object[] { new MouseEventArgs(button, 1, p.X, p.Y, delta) });
        }

        public Rectangle Overflow { get { return (Rectangle)typeof(TabManager).GetField("_overflowRect", Priv).GetValue(Tabs); } }
        public int Scroll { get { return (int)typeof(TabManager).GetField("_firstVisible", Priv).GetValue(Tabs); } }
        public Rectangle LeftArrow { get { return (Rectangle)typeof(TabManager).GetField("_leftArrowRect", Priv).GetValue(Tabs); } }
        public Rectangle RightArrow { get { return (Rectangle)typeof(TabManager).GetField("_rightArrowRect", Priv).GetValue(Tabs); } }

        // (tab, Rect, CloseRect, Truncated) for every painted hit area.
        public List<object[]> Areas()
        {
            var result = new List<object[]>();
            var list = (IList)typeof(TabManager).GetField("_hitAreas", Priv).GetValue(Tabs);
            foreach (var h in list)
            {
                var t = h.GetType();
                result.Add(new object[] { t.GetField("Tab").GetValue(h), t.GetField("Rect").GetValue(h),
                                          t.GetField("CloseRect").GetValue(h), t.GetField("Truncated").GetValue(h) });
            }
            return result;
        }

        public object[] AreaOf(TerminalTab tab)
        {
            foreach (var a in Areas()) if (ReferenceEquals(a[0], tab)) return a;
            return null;
        }

        public ContextMenuStrip BuildMenu()
        {
            typeof(TabManager).GetMethod("EnsureOverflowMenu", Priv).Invoke(Tabs, null);
            typeof(TabManager).GetMethod("RebuildOverflowMenu", Priv).Invoke(Tabs, null);
            return (ContextMenuStrip)typeof(TabManager).GetField("_overflowMenu", Priv).GetValue(Tabs);
        }

        public void SetMenuCloseClick(bool v) { typeof(TabManager).GetField("_menuCloseClick", Priv).SetValue(Tabs, v); }
    }

    static Point Centre(Rectangle r) { return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

    [STAThread]
    static int Main()
    {
        Console.WriteLine("TabManager overflow");
        string[] five = { "SampleSolution", "DemoServices", "ClarionAssistant session", "Another long session name", "Fifth" };

        // ---- Everything fits ----
        var wide = new Rig(1600, "Alpha", "Bravo", "Charlie");
        Check(wide.Overflow.IsEmpty, "fits: no overflow button");
        bool anyTruncated = false;
        foreach (var a in wide.Areas()) anyTruncated |= (bool)a[3];
        Check(!anyTruncated, "fits: no tab shrunk");

        // ---- Too wide at full size, fits shrunk ----
        var shrink = new Rig(680, five);
        Check(shrink.Overflow.IsEmpty, "shrink: no overflow button");
        bool allClosable = true, someTruncated = false;
        foreach (var t in shrink.Opened)
        {
            var a = shrink.AreaOf(t);
            var close = a == null ? Rectangle.Empty : (Rectangle)a[2];
            allClosable &= !close.IsEmpty && close.Right <= shrink.Strip.Width;
            if (a != null) someTruncated |= (bool)a[3];
        }
        Check(allClosable, "shrink: every tab's close button is on the strip");
        Check(someTruncated, "shrink: the long titles are shortened");

        // ---- Overflow ----
        var over = new Rig(600, five);
        var ovr = over.Overflow;
        Check(!ovr.IsEmpty && ovr.Right <= over.Strip.Width, "overflow: the overflow button is on the strip");
        bool nothingPast = true;
        foreach (var a in over.Areas())
        {
            var rect = (Rectangle)a[1]; var close = (Rectangle)a[2];
            if (rect.Right > ovr.Left) nothingPast = false;
            if (!close.IsEmpty && close.Right > ovr.Left) nothingPast = false;
        }
        Check(nothingPast, "overflow: no tab or close button is clickable under or past the button");
        var lastArea = over.AreaOf(over.Opened[4]);
        Check(lastArea != null && !((Rectangle)lastArea[2]).IsEmpty, "overflow: the active (last opened) tab's close button is in view");
        var homeArea = over.AreaOf(over.Home);
        Check(homeArea != null && ((Rectangle)homeArea[1]).X <= 2, "overflow: Home stays pinned on the left");
        Check(over.Scroll > 0, "overflow: scrolled to show the active tab");

        // Reported after the first version: the leftmost tab was cut by Home and looked shrunk further
        // than the others. Every tab drawn must be whole, and the shrunk ones all the same width.
        bool allWhole = true, sameWidth = true;
        int shrunkWidth = -1;
        foreach (var a in over.Areas())
        {
            var t = (TerminalTab)a[0];
            if (t.IsHome) continue;
            if (((Rectangle)a[2]).IsEmpty) allWhole = false;
            if ((bool)a[3])
            {
                int w = ((Rectangle)a[1]).Width;
                if (shrunkWidth < 0) shrunkWidth = w; else if (Math.Abs(w - shrunkWidth) > 1) sameWidth = false;
            }
        }
        Check(allWhole, "overflow: every tab drawn is whole (none cut by Home or the button)");
        Check(sameWidth, "overflow: the shortened tabs are all the same width");
        Check(over.Areas().Count < 6, "overflow: the tabs that do not fit are not drawn");
        Check(over.Areas().Count >= 3, "overflow: more than one tab is shown in a normal-width pad");

        // Reported after the second version: a gap between the last tab and the ⋯ button, and no sign
        // that more tabs were hidden.
        Rectangle lastRect = Rectangle.Empty;
        foreach (var a in over.Areas()) if (!((TerminalTab)a[0]).IsHome) lastRect = (Rectangle)a[1];
        Check(!over.RightArrow.IsEmpty && lastRect.Right == over.RightArrow.Left - 2, "overflow: the shown tabs fill the band up to the › arrow");
        Check(!over.LeftArrow.IsEmpty && over.LeftArrow.Left > ((Rectangle)homeArea[1]).Right, "overflow: the ‹ arrow sits right of Home");

        var arrows = new Rig(600, five);   // last tab active: tabs hidden to the left only
        int beforeArrow = arrows.Scroll;
        arrows.Mouse("OnMouseClick", MouseButtons.Left, Centre(arrows.LeftArrow), 0);
        arrows.Paint();
        Check(arrows.Scroll == beforeArrow - 1, "arrows: ‹ scrolls one tab towards the start");
        arrows.Mouse("OnMouseClick", MouseButtons.Left, Centre(arrows.RightArrow), 0);
        arrows.Paint();
        Check(arrows.Scroll == beforeArrow, "arrows: › scrolls one tab back");
        arrows.Mouse("OnMouseClick", MouseButtons.Left, Centre(arrows.RightArrow), 0);
        arrows.Paint();
        Check(arrows.Scroll == beforeArrow, "arrows: › does nothing at the end");
        Check(arrows.Tabs.Tabs.Count == 6 && arrows.Tabs.ActiveTab == arrows.Opened[4], "arrows: clicking them neither closes nor switches tabs");

        var fits = new Rig(1600, five);
        Check(fits.LeftArrow.IsEmpty && fits.RightArrow.IsEmpty, "fits: no arrows while every tab fits");

        // The menu's renderer builds for both themes (it draws its own check mark and colours).
        Exception rendererError = null;
        try
        {
            using (var probe = new ContextMenuStrip())
            {
                probe.Renderer = new TabManager.OverflowMenuRenderer(true);
                probe.Renderer = new TabManager.OverflowMenuRenderer(false);
            }
        }
        catch (Exception ex) { rendererError = ex; }
        Check(rendererError == null, "menu: the themed renderer builds for dark and light");

        // Clicking the close button the user can now reach closes it.
        over.Mouse("OnMouseClick", MouseButtons.Left, Centre((Rectangle)lastArea[2]), 0);
        Check(over.Tabs.FindTab(over.Opened[4].Id) == null, "overflow: clicking the visible close button closes that tab");

        // Activating the first tab brings it back into view.
        over.Tabs.ActivateTab(over.Opened[0].Id);
        over.Paint();
        var first = over.AreaOf(over.Opened[0]);
        Check(first != null && !((Rectangle)first[2]).IsEmpty, "overflow: activating a scrolled-away tab brings its close button into view");

        // ---- Wheel ----
        var wheel = new Rig(600, five);
        int s0 = wheel.Scroll;
        wheel.Mouse("OnMouseWheel", MouseButtons.None, new Point(150, 10), 120);   // wheel up: towards the start
        wheel.Paint();
        Check(wheel.Scroll < s0, "wheel up scrolls towards the first tabs");
        for (int i = 0; i < 50; i++) wheel.Mouse("OnMouseWheel", MouseButtons.None, new Point(150, 10), 120);
        wheel.Paint();
        Check(wheel.Scroll == 0, "...and stops at the start");
        for (int i = 0; i < 50; i++) wheel.Mouse("OnMouseWheel", MouseButtons.None, new Point(150, 10), -120);
        wheel.Paint();
        int sMax = wheel.Scroll;
        Check(sMax > 0 && sMax == s0, "wheel down stops at the end (the last tab in full view)");

        // ---- Overflow menu ----
        var menuRig = new Rig(360, "One", "Two & Three", "Four", "Five", "Six");
        var menu = menuRig.BuildMenu();
        // Home is always on the strip, so the list leaves it out.
        Check(menu.Items.Count == 5, "menu: one row per tab, Home left out");
        bool homeListed = false;
        foreach (ToolStripItem it in menu.Items) homeListed |= ReferenceEquals(it.Tag, menuRig.Home);
        Check(!homeListed, "menu: no row for Home");
        var two = (ToolStripMenuItem)menu.Items[1];
        Check(two.Text == "Two && Three", "menu: '&' in a title is shown, not taken as a mnemonic");
        Check(((ToolStripMenuItem)menu.Items[4]).Checked && !two.Checked, "menu: only the active tab is checked");
        Check(two.ShortcutKeyDisplayString == "✕", "menu: rows show a close mark");
        menuRig.Mouse("OnMouseMove", MouseButtons.None, Centre(menuRig.Overflow), 0);
        string tip = (string)typeof(TabManager).GetField("_toolTipText", Priv).GetValue(menuRig.Tabs);
        Check(tip == "All 5 tabs", "menu: the button's tooltip counts the tabs it lists (Home not counted), got \"" + tip + "\"");

        menuRig.SetMenuCloseClick(false);
        two.PerformClick();
        Check(menuRig.Tabs.ActiveTab == menuRig.Opened[1], "menu: clicking a row activates its tab");

        var four = (ToolStripMenuItem)menu.Items[2];
        menuRig.SetMenuCloseClick(true);
        four.PerformClick();
        Check(menuRig.Tabs.FindTab(menuRig.Opened[2].Id) == null, "menu: clicking a row's close mark closes that tab");
        Check(menu.Items.Count == 4 && !menu.Items.Contains(four), "menu: ...and its row goes, the rest stay");
        Check(menuRig.Tabs.ActiveTab == menuRig.Opened[1], "menu: ...leaving the active tab alone");

        // ---- ⋯ toggles: the press that closed the list does not reopen it ----
        var toggle = new Rig(600, five);
        var closedOnButton = typeof(TabManager).GetField("_menuClosedOnButton", Priv);
        closedOnButton.SetValue(toggle.Tabs, true);   // as the list's Closed handler leaves it after a press on ⋯
        typeof(TabManager).GetField("_menuClosedOnButtonTick", Priv).SetValue(toggle.Tabs, Environment.TickCount);
        toggle.Mouse("OnMouseClick", MouseButtons.Left, Centre(toggle.Overflow), 0);
        Check(typeof(TabManager).GetField("_overflowMenu", Priv).GetValue(toggle.Tabs) == null,
              "toggle: the click of the press that closed the list does not open it again");
        Check(!(bool)closedOnButton.GetValue(toggle.Tabs), "toggle: ...and the next click on ⋯ opens it as usual");

        // With no list open, the Esc hook passes every key on (it is only installed while the list is open,
        // but must never swallow a key it has no list for), and the strip losing focus does nothing.
        Exception escError = null;
        object passed = null;
        try
        {
            passed = typeof(TabManager).GetMethod("EscHookProc", Priv)
                .Invoke(toggle.Tabs, new object[] { 0, (IntPtr)(int)Keys.Escape, IntPtr.Zero });
            typeof(Control).GetMethod("OnLostFocus", Priv).Invoke(toggle.Strip, new object[] { EventArgs.Empty });
        }
        catch (Exception ex) { escError = ex; }
        Check(escError == null && passed is IntPtr && (IntPtr)passed != (IntPtr)1,
              "esc: with no list open the hook passes Esc on");
        Check(toggle.Tabs.Tabs.Count == 6, "focus: the strip losing focus with no list open changes nothing");

        // ---- Middle click ----
        var mid = new Rig(1600, "Alpha", "Bravo");
        int asked = 0;
        bool answer = false;
        mid.Tabs.ConfirmMiddleClickClose = t => { asked++; return answer; };
        var alpha = mid.AreaOf(mid.Opened[0]);
        mid.Mouse("OnMouseClick", MouseButtons.Middle, Centre((Rectangle)alpha[1]), 0);
        Check(asked == 1 && mid.Tabs.FindTab(mid.Opened[0].Id) != null, "middle: asks, and 'keep' keeps the tab");
        Check(mid.Tabs.ActiveTab == mid.Opened[1], "middle: ...without activating it either");
        answer = true;
        mid.Mouse("OnMouseClick", MouseButtons.Middle, Centre((Rectangle)alpha[1]), 0);
        Check(asked == 2 && mid.Tabs.FindTab(mid.Opened[0].Id) == null, "middle: 'close' closes it");

        mid.Paint();
        var home = mid.AreaOf(mid.Home);
        mid.Mouse("OnMouseClick", MouseButtons.Middle, Centre((Rectangle)home[1]), 0);
        Check(asked == 2 && mid.Tabs.FindTab(mid.Home.Id) != null, "middle: Home is neither asked about nor closed");

        mid.Tabs.ConfirmMiddleClickClose = null;
        var bravo = mid.AreaOf(mid.Opened[1]);
        mid.Mouse("OnMouseClick", MouseButtons.Middle, Centre((Rectangle)bravo[1]), 0);
        Check(mid.Tabs.FindTab(mid.Opened[1].Id) == null, "middle: with no confirm hook it closes straight away");

        Console.WriteLine();
        if (_failures == 0) Console.WriteLine("ALL " + _assertions + " CHECKS PASSED");
        else Console.WriteLine(_failures + " FAILED, " + (_assertions - _failures) + " passed");
        return _failures == 0 ? 0 : 1;
    }
}
