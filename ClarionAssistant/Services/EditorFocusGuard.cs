using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #140: decides whether a Monaco surface may claim keyboard focus, or must stand down
    /// because the user has deliberately focused a NON-DOCUMENT IDE surface (a docked pad such as
    /// the Output pane). The #66 tab-activation hooks re-claim focus for the WebView2 whenever the
    /// workbench re-selects the Monaco document window — but the fork re-fires WindowSelected on
    /// every click into a pad while a Monaco document stays active (confirmed via monaco-spike.log:
    /// one claim per Output-pane click), so the pad goes deaf: scroll still works (hover-routed)
    /// but clicks/selection/Ctrl+A die (focus-routed). The CA Find pad previously won this same
    /// fight only via a bespoke timed suppression (CaFindBroker.SuppressEditorFocusSteal); this
    /// guard is the general rule so every other pad is safe by default.
    ///
    /// Classification walks the ancestry of the control that currently HOLDS Win32 focus:
    ///  - inside the claiming Monaco control → claim (harmless, focus is already ours);
    ///  - a pad wrapper / auto-hide host / non-Document dock pane → STAND DOWN;
    ///  - a Document-state dock pane (another editor tab, the document tab strip) → claim — that
    ///    is precisely the legit #66 scenario (tab switch), which must keep claiming;
    ///  - unclassified (unknown fork types, or a native hwnd with no managed wrapper) → claim,
    ///    i.e. pre-guard behavior — the guard can only make focus LESS grabby, never more.
    /// Type checks are name-based (PadContentWrapper / AutoHide / DockPane.DockState via
    /// reflection): the SD-fork surface differs from upstream SharpDevelop, so unclassified chains
    /// are spike-logged (throttled) to let us add real fork type names instead of guessing.
    /// </summary>
    internal static class EditorFocusGuard
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        private static DateTime _lastUnclassifiedLog = DateTime.MinValue;

        /// <summary>True when Win32 focus is held by an IDE surface the user chose OVER the Monaco
        /// document (a pad) — the caller must not steal focus back. False = claim as before.</summary>
        public static bool FocusInForeignPad(Control ours)
        {
            try
            {
                var h = GetFocus();
                if (h == IntPtr.Zero) return false;                  // nobody focused — claim freely
                var focused = Control.FromChildHandle(h);
                if (focused == null) return false;                   // pure-native hwnd — cannot classify
                string chain = null;
                for (var node = focused; node != null; node = node.Parent)
                {
                    if (ReferenceEquals(node, ours)) return false;   // focus already in our surface
                    var t = node.GetType();
                    var name = t.FullName ?? t.Name;
                    chain = chain == null ? name : chain + " < " + name;
                    if (name.IndexOf("PadContentWrapper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("AutoHide", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        MonacoSpikeLog.Write("focus-guard: stand down — focus in " + name);
                        return true;
                    }
                    if (name.EndsWith(".DockPane", StringComparison.Ordinal) || t.Name == "DockPane")
                    {
                        string state = null;
                        try
                        {
                            var p = t.GetProperty("DockState");
                            if (p != null) state = Convert.ToString(p.GetValue(node, null));
                        }
                        catch { }
                        if (state == null) continue;                 // can't read the state — keep walking
                        if (state == "Document") return false;       // document area (other tab / strip) — legit claim
                        MonacoSpikeLog.Write("focus-guard: stand down — focus in " + name + " (" + state + ")");
                        return true;
                    }
                }
                // Focus is somewhere managed we couldn't classify — default to claiming (pre-guard
                // behavior), but log the chain so a missed pad type can be added later. Throttled:
                // WindowSelected re-fires on every pad click, and this line exists to be read once.
                if ((DateTime.UtcNow - _lastUnclassifiedLog).TotalSeconds > 5)
                {
                    _lastUnclassifiedLog = DateTime.UtcNow;
                    MonacoSpikeLog.Write("focus-guard: unclassified focus chain — claiming anyway: " + chain);
                }
                return false;
            }
            catch { return false; }
        }
    }
}
