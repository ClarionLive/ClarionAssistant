// Harness for Terminal\TabManager.cs closing tabs (ticket 6a4b4d71): with several tabs open, closing
// one that was not the active tab left the custom-painted strip showing it, and the other tabs'
// close buttons seemed dead until opening Settings forced a repaint.
//
// Compiled against the REAL TabManager.cs and TerminalTab.cs (stubs: TabManager.Close.Stubs.cs) by
// Run-Tests.ps1. It never shows a window: paints are driven with DrawToBitmap, clicks through the
// panel's OnMouseClick.
//
// What each check stops:
//   non-active close repaints - the reported bug: CloseTab invalidated the strip only when the
//                               closed tab was active (via ActivateTab)
//   add repaints              - the same gap on the add side, hidden today because callers activate
//   handler throws            - a TabRemoved handler that throws leaving the strip and the active
//                               tab pointing at a tab that is gone
//   stale click               - a click on a hit area painted before the close acting on, or
//                               throwing for, a tab that no longer exists
//   active close              - the original path (close the active tab, next one activates) intact
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ClarionAssistant.Terminal;

static class TabManagerCloseTest
{
    static int _failures, _assertions;

    static void Check(bool ok, string what)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what); }
        else Console.WriteLine("  ok   " + what);
    }

    class Rig
    {
        public Panel Strip = new Panel { Width = 800, Height = 28 };
        public Panel Content = new Panel { Width = 800, Height = 400 };
        public TabManager Tabs;
        public int Invalidations;
        public TerminalTab Home, A, B, C;

        public Rig()
        {
            Tabs = new TabManager(Strip, Content);
            Strip.Invalidated += (s, e) => Invalidations++;
            Home = Tabs.CreateHomeTab(new Label());
            A = Tabs.CreateContentTab("Alpha", new Label());
            B = Tabs.CreateContentTab("Bravo", new Label());
            C = Tabs.CreateContentTab("Charlie", new Label());
            Tabs.ActivateTab(A.Id);
            Paint();
        }

        // Runs OnTabStripPaint, which is what rebuilds the hit areas clicks are tested against.
        public void Paint()
        {
            using (var bmp = new Bitmap(Strip.Width, Strip.Height))
                Strip.DrawToBitmap(bmp, new Rectangle(0, 0, Strip.Width, Strip.Height));
        }

        public void Click(Point p)
        {
            typeof(Control).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(Strip, new object[] { new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0) });
        }

        // The painted hit area for a tab, read from TabManager's private list.
        public Rectangle AreaOf(TerminalTab tab, bool closeButton)
        {
            var list = (IList)typeof(TabManager).GetField("_hitAreas", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Tabs);
            foreach (var h in list)
            {
                var t = h.GetType();
                if (ReferenceEquals(t.GetField("Tab").GetValue(h), tab))
                    return (Rectangle)t.GetField(closeButton ? "CloseRect" : "Rect").GetValue(h);
            }
            return Rectangle.Empty;
        }

        public static Point Centre(Rectangle r) { return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }
    }

    [STAThread]
    static int Main()
    {
        Console.WriteLine("TabManager close (6a4b4d71)");

        // ---- The reported bug: closing a tab that is not the active one ----
        var r = new Rig();
        Check(r.Tabs.ActiveTab == r.A, "setup: Alpha active");
        int before = r.Invalidations;
        r.Tabs.CloseTab(r.B.Id);
        Check(r.Invalidations > before, "closing a NON-active tab repaints the strip");
        Check(r.Tabs.ActiveTab == r.A, "closing a non-active tab leaves the active tab alone");
        Check(r.Tabs.FindTab(r.B.Id) == null && r.Tabs.Tabs.Count == 3, "the tab is gone from the list");

        // A click on the strip as painted BEFORE the close (no repaint has run yet) must not act on
        // the closed tab or throw, and must leave the others intact.
        var r2 = new Rig();
        var staleBravoClose = Rig.Centre(r2.AreaOf(r2.B, true));
        r2.Tabs.CloseTab(r2.B.Id);          // invalidated, but no paint has run: hit areas are stale
        int inv2 = r2.Invalidations;
        Exception clickError = null;
        try { r2.Click(staleBravoClose); } catch (Exception ex) { clickError = ex; }
        Check(clickError == null, "a click on a closed tab's stale X does not throw");
        Check(r2.Tabs.Tabs.Count == 3 && r2.Tabs.ActiveTab == r2.A, "...and closes or activates nothing else");
        Check(r2.Invalidations > inv2, "...and asks for a repaint");

        // After the repaint, the other tabs' close buttons work again (the reported symptom).
        r2.Paint();
        var charlieClose = Rig.Centre(r2.AreaOf(r2.C, true));
        Check(charlieClose != Point.Empty, "after repaint Charlie has a close button");
        int inv3 = r2.Invalidations;
        r2.Click(charlieClose);
        Check(r2.Tabs.FindTab(r2.C.Id) == null, "clicking Charlie's X closes Charlie");
        Check(r2.Invalidations > inv3, "...and repaints the strip");

        // ---- Closing the active tab (the path that always worked) ----
        var r3 = new Rig();
        r3.Tabs.CloseTab(r3.A.Id);
        Check(r3.Tabs.ActiveTab == r3.Home, "closing the active tab activates the first tab");

        // ---- A TabRemoved handler that throws must not leave the strip stale ----
        var r4 = new Rig();
        r4.Tabs.TabRemoved += (s, t) => { throw new InvalidOperationException("handler failed"); };
        int inv4 = r4.Invalidations;
        Exception handlerError = null;
        try { r4.Tabs.CloseTab(r4.A.Id); } catch (InvalidOperationException ex) { handlerError = ex; }
        Check(handlerError != null, "setup: the throwing handler did throw");
        Check(r4.Invalidations > inv4, "strip repainted even though the handler threw");
        Check(r4.Tabs.ActiveTab == r4.Home, "active tab moved off the closed tab even though the handler threw");

        // ---- Adding a tab repaints without relying on the caller activating it ----
        var r5 = new Rig();
        int inv5 = r5.Invalidations;
        r5.Tabs.CreateContentTab("Delta", new Label());
        Check(r5.Invalidations > inv5, "adding a tab repaints the strip");

        Console.WriteLine();
        if (_failures == 0) Console.WriteLine("ALL " + _assertions + " CHECKS PASSED");
        else Console.WriteLine(_failures + " FAILED, " + (_assertions - _failures) + " passed");
        return _failures == 0 ? 0 : 1;
    }
}
