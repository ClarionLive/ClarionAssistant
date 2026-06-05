using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// TRANSIENT DIAGNOSTIC — right-click → "Open in Modern Embeditor" native-hook spike, PHASE 1 v2
    /// (ticket 4b82f1de). LOGGING + READ ONLY. This phase makes the SAFEST possible first contact and
    /// NEVER mutates a menu (no AppendMenu), never touches WebView2/ShowView/embeditor-lock, always calls
    /// CallNextHookEx, and unhooks everything it installs.
    ///
    /// HOOK PIVOT (root cause: the Clarion IDE runs TWO UI threads in ONE process — a NATIVE thread owns
    /// ClaList/ClaChildClient/ClaWin, a MANAGED thread owns the WinForms host + main frame, and our addin
    /// runs on the MANAGED thread). SetWindowSubclass is thread-affine → it silently FAILS cross-thread,
    /// so we cannot subclass the native windows. Instead we install THREAD-SPECIFIC SetWindowsHookEx on the
    /// native thread id (install is NOT thread-affine; the hook proc runs on the target thread; a same-process
    /// thread-targeted hook accepts a managed delegate with NO DLL injection). One thread-level hook covers
    /// ALL native windows on that thread — no per-window roster. The native thread is recreated per app
    /// close/reopen (Case B), so phase-2 re-hooks per app-open; this phase just hooks the current tid once.
    ///
    /// Exposed via inspect_ide commands (no new MCP registration; inspect_ide is RequiresUiThread):
    ///   probe_native_chain     — GetParent walk ClaList → IDE frame               [chain, in-proc]
    ///   probe_clalist_read     — in-process LB_* reads on the ClaList             [in-proc]
    ///   probe_enum_lists       — enumerate all ClaList* under the workbench + parent chains [read-only]
    ///   probe_popup_arm        — install WH_CALLWNDPROCRET + WH_GETMESSAGE on the live native tid (logging)
    ///   probe_popup_arm_inject — PHASE 1.5: same hooks + AppendMenu one MF_STRING [PROBE] item @0xE001
    ///   probe_mark[:label]     — PHASE 1.5 Tier-0: labeled separator in the trace between right-clicks
    ///   probe_popup_report     — dump captured trace + UnhookWindowsHookEx (both) + inject OFF
    ///
    /// PHASE-1 v2 must answer 4 things: (1) native tid stable across app reopen [answered: NO, Case B],
    /// (2) menu command POSTED (→ WM_COMMAND/WM_MENUCOMMAND via GETMESSAGE) vs TPM_RETURNCMD (inline, no
    /// command msg — only WM_MENUSELECT then the embeditor opens), (3) HMENU item count > 0 at
    /// CALLWNDPROCRET, (4) the EXACT native "Embeditor" item string. All entry points run on the IDE UI thread.
    ///
    /// DEADLOCK RULE for phase 2/3 (noted here): the hook proc runs on the NATIVE thread and must NEVER
    /// sync-SendMessage / Control.Invoke to the managed thread — async only (Control.BeginInvoke).
    /// </summary>
    internal static class NativeProbeService
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        #region P/Invoke
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        // Menu READ APIs (logging). GetMenuItemID is owner-draw-safe (returns the cmdId regardless of text).
        [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetMenuString(IntPtr hMenu, uint id, StringBuilder buf, int max, uint flags);
        [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr hMenu, int pos);
        // Menu MUTATE — used ONLY by probe_popup_arm_inject, gated behind the _inject flag (PHASE 1.5).
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string item);

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, StringBuilder l);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, ref RECT l);

        // Thread-specific message hooks — install is NOT thread-affine (unlike SetWindowSubclass), the proc
        // runs on the target native thread, and same-process targeting needs no DLL injection.
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc cb, IntPtr hMod, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hHook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hHook, int code, IntPtr w, IntPtr l);

        private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
        private delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        // lParam of a WH_CALLWNDPROCRET hook (message already processed by the wndproc → menu fully built).
        [StructLayout(LayoutKind.Sequential)]
        private struct CWPRETSTRUCT { public IntPtr lResult; public IntPtr lParam; public IntPtr wParam; public uint message; public IntPtr hwnd; }
        // lParam of a WH_GETMESSAGE hook (a POSTED message just retrieved from the queue).
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

        private const int WH_GETMESSAGE = 3, WH_CALLWNDPROCRET = 12;
        private const int PM_REMOVE = 1;

        private const uint WM_INITMENUPOPUP = 0x0117, WM_COMMAND = 0x0111, WM_CONTEXTMENU = 0x007B,
                           WM_RBUTTONUP = 0x0205, WM_ENTERMENULOOP = 0x0211, WM_EXITMENULOOP = 0x0212,
                           WM_MENUSELECT = 0x011F, WM_MENUCOMMAND = 0x0126;
        private const uint GA_ROOT = 2, MF_BYPOSITION = 0x400, MF_STRING = 0x0, GW_OWNER = 4;
        // PHASE 1.5 inject probe: a custom cmdId well clear of Clarion's proc-popup range (1-12).
        private const uint PROBE_CMDID = 0xE001;
        private const uint LB_GETCOUNT = 0x018B, LB_GETTEXTLEN = 0x018A, LB_GETTEXT = 0x0189,
                           LB_GETITEMRECT = 0x0198, LB_ITEMFROMPOINT = 0x01A9, LB_GETCURSEL = 0x0188;
        #endregion

        // GC-rooted, addin-lifetime delegates. NEVER a per-install lambda (a collected delegate hit by a
        // native callback = AccessViolation). Cardinal rule.
        private static readonly HookProc s_cwpRet = CwpRetProc;
        private static readonly HookProc s_getMsg = GetMsgProc;

        private static IntPtr _hCwpRet = IntPtr.Zero;
        private static IntPtr _hGetMsg = IntPtr.Zero;
        private static uint _hookedTid;
        // PHASE 1.5: when true, the CWPRETSTRUCT hook AppendMenus our [PROBE] item onto each live popup HMENU
        // (bounded/reversible — flipped off by probe_popup_report/unhook). volatile: written on the UI thread,
        // read on the native hook thread.
        private static volatile bool _inject;

        private static readonly object _gate = new object();
        private static readonly List<string> _log = new List<string>();

        private static void Log(string s) { lock (_gate) _log.Add(s); }

        // ============================ discovery ============================
        private static List<(IntPtr hwnd, string cls, bool vis)> ChildWindows(IntPtr parent)
        {
            var list = new List<(IntPtr, string, bool)>();
            EnumChildWindows(parent, (h, _) =>
            {
                var sb = new StringBuilder(256); GetClassName(h, sb, 256);
                list.Add((h, sb.ToString(), IsWindowVisible(h)));
                return true;
            }, IntPtr.Zero);
            return list;
        }
        private static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
        private static string TextOf(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }

        private static IntPtr GetAppHostHandle()
        {
            var svc = new AppTreeService();
            var ctrl = typeof(AppTreeService).GetMethod("GetAppMainControl", AllInstance)?.Invoke(svc, null) as Control;
            return (ctrl != null && ctrl.IsHandleCreated) ? ctrl.Handle : IntPtr.Zero;
        }

        private static IntPtr FindClaList()
        {
            var host = GetAppHostHandle();
            if (host == IntPtr.Zero) return IntPtr.Zero;
            foreach (var (h, cls, vis) in ChildWindows(host))
                if (vis && cls.StartsWith("ClaList", StringComparison.OrdinalIgnoreCase)) return h;
            return IntPtr.Zero;
        }

        // The managed workbench main-window handle (plain WinForms — DefaultWorkbench IS a Form). This is the
        // enumeration root phase-2's ArmPass will use, so the phase-1 GA_ROOT count baselines against it.
        private static IntPtr GetWorkbenchMainHandle()
        {
            try { if (WorkbenchSingleton.Workbench is Control wb && wb.IsHandleCreated) return wb.Handle; } catch { }
            return IntPtr.Zero;
        }

        // ============================ probe_native_chain ============================
        public static string DumpNativeChain()
        {
            IntPtr list = FindClaList();
            if (list == IntPtr.Zero) return "probe_native_chain: ClaList not found (open an .app + procedure view).";
            IntPtr host = GetAppHostHandle();
            var sb = new StringBuilder();
            int lvl = 0;
            for (IntPtr h = list; h != IntPtr.Zero && lvl <= 25; h = GetParent(h), lvl++)
            {
                GetWindowRect(h, out RECT r);
                string mark = (h == host) ? "  <== ApplicationMainWindowControl HOST (managed)" : "";
                sb.AppendLine($"L{lvl}: 0x{h.ToString("X")} cls='{ClassOf(h)}' vis={IsWindowVisible(h)} text='{TextOf(h)}'{mark}");
            }

            // GA_ROOT roster baseline (phase-2 over-arming check): count visible ClaWin*/ClaList* under the
            // MANAGED workbench main handle — the exact root phase-2's ArmPass enumerates.
            IntPtr mainH = GetWorkbenchMainHandle();
            sb.AppendLine();
            if (mainH == IntPtr.Zero) sb.AppendLine("GA_ROOT count: workbench main handle unavailable.");
            else
            {
                int claWin = 0, claList = 0;
                // Distinct native-tid map: with 2 .app tabs open simultaneously this reveals whether they
                // share ONE native UI thread or get TWO (the sequential close/reopen test can't tell). Drives
                // the phase-2 tid-keyed hook roster (1 tid → single pair; N tids → N pairs).
                var tidWindows = new Dictionary<uint, List<string>>();
                foreach (var (h, cls, vis) in ChildWindows(mainH))
                {
                    if (!vis) continue;
                    bool isWin = cls.StartsWith("ClaWin", StringComparison.OrdinalIgnoreCase);
                    bool isList = cls.StartsWith("ClaList", StringComparison.OrdinalIgnoreCase);
                    if (isWin) claWin++;
                    else if (isList) claList++;
                    if (isWin || isList)
                    {
                        uint t = GetWindowThreadProcessId(h, out _);
                        if (!tidWindows.TryGetValue(t, out var lst)) { lst = new List<string>(); tidWindows[t] = lst; }
                        lst.Add($"0x{h.ToString("X")}({cls})");
                    }
                }
                sb.AppendLine($"GA_ROOT roster under main 0x{mainH.ToString("X")}: ClaWin={claWin} ClaList={claList} (open .app tabs + designers as applicable).");
                sb.AppendLine($"DISTINCT native tids across {claWin + claList} Cla* window(s): {tidWindows.Count}");
                foreach (var kv in tidWindows)
                    sb.AppendLine($"  tid {kv.Key}: {string.Join(", ", kv.Value)}");
            }
            return sb.ToString();
        }

        // ============================ probe_clalist_read ============================
        public static string ProbeClaListRead()
        {
            IntPtr h = FindClaList();
            if (h == IntPtr.Zero) return "probe_clalist_read: ClaList not found.";
            var sb = new StringBuilder();
            sb.AppendLine($"ClaList = 0x{h.ToString("X")} cls='{ClassOf(h)}'");
            GetClientRect(h, out RECT cr); sb.AppendLine($"client = ({cr.L},{cr.T})-({cr.R},{cr.B})");
            sb.AppendLine($"LB_GETCOUNT  = {SendMessage(h, LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt64()}");
            sb.AppendLine($"LB_GETCURSEL = {SendMessage(h, LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64()}");
            for (int i = 0; i <= 5; i++)
            {
                int len = (int)SendMessage(h, LB_GETTEXTLEN, (IntPtr)i, IntPtr.Zero);
                var buf = new StringBuilder(Math.Max(8, len + 1));
                IntPtr gt = SendMessage(h, LB_GETTEXT, (IntPtr)i, buf);
                sb.AppendLine($"LB_GETTEXTLEN({i})={len}  LB_GETTEXT({i})=ret{gt.ToInt64()} text='{buf}'");
            }
            RECT ir = default;
            IntPtr irr = SendMessage(h, LB_GETITEMRECT, (IntPtr)0, ref ir);
            sb.AppendLine($"LB_GETITEMRECT(0) ret={irr.ToInt64()} rect=({ir.L},{ir.T})-({ir.R},{ir.B})");
            IntPtr lp = (IntPtr)((8 << 16) | (40 & 0xFFFF));
            IntPtr ifp = SendMessage(h, LB_ITEMFROMPOINT, IntPtr.Zero, lp);
            sb.AppendLine($"LB_ITEMFROMPOINT(40,8) ret={ifp.ToInt64()}");
            return sb.ToString();
        }

        // ============================ probe_enum_lists (READ-ONLY structural enum) ============================
        // Eve's arm-gate edge: how many ClaList* instances live under the managed workbench, and which one is
        // the PROC tree (the ClaList whose parent chain is ClaChildClient -> ClaWin). Tells us how hard the
        // phase-2 arm gate must work to single out the proc tree vs other list/tree controls. READ ONLY.
        public static string EnumClaLists()
        {
            IntPtr mainH = GetWorkbenchMainHandle();
            if (mainH == IntPtr.Zero) return "probe_enum_lists: workbench main handle unavailable.";
            var sb = new StringBuilder();
            sb.AppendLine($"Workbench main 0x{mainH.ToString("X")} ({ClassOf(mainH)})");

            var lists = new List<IntPtr>();
            foreach (var (h, cls, vis) in ChildWindows(mainH))
                if (cls.StartsWith("ClaList", StringComparison.OrdinalIgnoreCase)) lists.Add(h);

            sb.AppendLine($"ClaList* instances under main: {lists.Count}");
            foreach (var h in lists)
            {
                uint tid = GetWindowThreadProcessId(h, out _);
                sb.AppendLine($"  ClaList 0x{h.ToString("X")} vis={IsWindowVisible(h)} tid={tid} '{TextOf(h)}'");
                var chain = new List<string>();
                int lvl = 0;
                for (IntPtr p = GetParent(h); p != IntPtr.Zero && lvl < 8; p = GetParent(p), lvl++)
                    chain.Add($"{ClassOf(p)}(0x{p.ToString("X")})");
                sb.AppendLine($"      chain: {string.Join(" -> ", chain)}");
                bool isProcTree = chain.Count >= 2
                    && chain[0].StartsWith("ClaChildClient", StringComparison.OrdinalIgnoreCase)
                    && chain[1].StartsWith("ClaWin", StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"      => {(isProcTree ? "PROC TREE (ClaChildClient -> ClaWin)" : "other list control")}");
            }

            // Structural context — class histogram of Cla*/list/tree controls (no menus raised) so we can
            // predict the editor + solution-tree popup owners before John raises them.
            sb.AppendLine();
            sb.AppendLine("Visible Cla*/list/tree controls under main (class histogram):");
            var hist = new Dictionary<string, int>();
            foreach (var (h, cls, vis) in ChildWindows(mainH))
            {
                if (!vis) continue;
                if (cls.StartsWith("Cla", StringComparison.OrdinalIgnoreCase)
                    || cls.IndexOf("List", StringComparison.OrdinalIgnoreCase) >= 0
                    || cls.IndexOf("Tree", StringComparison.OrdinalIgnoreCase) >= 0)
                { hist.TryGetValue(cls, out int c); hist[cls] = c + 1; }
            }
            foreach (var kv in hist) sb.AppendLine($"  {kv.Key} x{kv.Value}");
            return sb.ToString();
        }

        // ============================ probe_popup_arm / report (LOGGING ONLY, HOOK PIVOT) ============================
        // Install thread-specific WH_CALLWNDPROCRET + WH_GETMESSAGE on the NATIVE thread that owns the ClaList,
        // so whichever native window TrackPopupMenu uses, we capture WM_INITMENUPOPUP (+HMENU items) /
        // WM_MENUSELECT on the SENT path and WM_COMMAND / WM_MENUCOMMAND on the POSTED path. NO AppendMenu.
        public static string PopupArm() => ArmCore(false);

        // PHASE 1.5: same two hooks, but with the append-inject flag ON. The CWPRETSTRUCT hook will AppendMenu
        // ONE plain MF_STRING item (cmdId 0xE001) onto each live popup HMENU it sees while armed — bounded and
        // reversible (probe_popup_report flips _inject off and unhooks). No DeleteMenu: the HMENU is transient,
        // rebuilt per right-click, so simply not re-appending after disarm is the "undo".
        public static string PopupArmInject() => ArmCore(true);

        private static string ArmCore(bool inject)
        {
            PopupRemoveInternal();                 // idempotent unhook (also clears _inject)
            lock (_gate) _log.Clear();

            IntPtr list = FindClaList();
            if (list == IntPtr.Zero) return "probe_popup_arm: ClaList not found.";
            uint pid;
            uint tid = GetWindowThreadProcessId(list, out pid);
            if (tid == 0) return "probe_popup_arm: GetWindowThreadProcessId returned 0 (no native tid).";

            var sb = new StringBuilder();
            string verb = inject ? "probe_popup_arm_inject (HOOK pivot + INJECT)" : "probe_popup_arm (HOOK pivot)";
            sb.AppendLine($"{verb} — ClaList=0x{list.ToString("X")} nativeTid={tid} pid={pid}");

            _hCwpRet = SetWindowsHookEx(WH_CALLWNDPROCRET, s_cwpRet, IntPtr.Zero, tid);
            int e1 = Marshal.GetLastWin32Error();
            _hGetMsg = SetWindowsHookEx(WH_GETMESSAGE, s_getMsg, IntPtr.Zero, tid);
            int e2 = Marshal.GetLastWin32Error();

            sb.AppendLine($"  WH_CALLWNDPROCRET → {(_hCwpRet != IntPtr.Zero ? "OK 0x" + _hCwpRet.ToString("X") : "FAILED err=" + e1)}");
            sb.AppendLine($"  WH_GETMESSAGE     → {(_hGetMsg != IntPtr.Zero ? "OK 0x" + _hGetMsg.ToString("X") : "FAILED err=" + e2)}");

            if (_hCwpRet == IntPtr.Zero && _hGetMsg == IntPtr.Zero)
            {
                _hookedTid = 0;
                sb.AppendLine("  BOTH hooks failed — nothing armed.");
                return sb.ToString();
            }
            _hookedTid = tid;
            _inject = inject;                      // arm injection AFTER the hooks are confirmed installed

            sb.AppendLine();
            if (!inject)
            {
                sb.AppendLine("ARMED (logging only). Now run the TWO gating tests, then call probe_popup_report:");
                sb.AppendLine("  (1) RIGHT-CLICK a procedure, then press Esc to DISMISS (do not pick anything).");
                sb.AppendLine("  (2) RIGHT-CLICK a procedure, then SELECT the native 'Embeditor' item (it just opens the");
                sb.AppendLine("      embeditor — cancel/close it afterward). This settles posted-WM_COMMAND vs TPM_RETURNCMD.");
            }
            else
            {
                sb.AppendLine("ARMED + INJECTING (appends 'Open in Modern Embeditor [PROBE]' @0xE001 to each popup on this tid).");
                sb.AppendLine("Run in order, calling probe_mark:<label> BETWEEN steps so the trace segments, then probe_popup_report:");
                sb.AppendLine("  (1) RENDER: right-click a PROC → look for the [PROBE] item → appear? sane height? clickable?");
                sb.AppendLine("  (2) NOTIFY: CLICK the [PROBE] item → expect a posted WM_COMMAND cmdId=0xE001 in the trace.");
                sb.AppendLine("  -- probe_mark:proc2 --");
                sb.AppendLine("  (3) right-click a 2nd PROC → confirm [PROBE] + 0xE001 again (cross-proc stability).");
                sb.AppendLine("  -- probe_mark:editor --");
                sb.AppendLine("  (4a) TIER-0: right-click in the EDITOR (Esc). If NO native msgs land in this segment, our");
                sb.AppendLine("       native-tid hook never fired → managed popup → 'fired on our hook' discriminates.");
                sb.AppendLine("  -- probe_mark:solution --");
                sb.AppendLine("  (4b) TIER-0: right-click the SOLUTION/PROJECT tree (Esc). Same read. Any popup that DOES");
                sb.AppendLine("       land logs OWNER + GW_OWNER + GetParent lineage + clicked-child + cmdId layout.");
            }
            return sb.ToString();
        }

        // PHASE 1.5 TIER-0: drop a labeled separator into the live trace BETWEEN right-clicks so the report
        // segments cleanly. A segment that contains ONLY its mark and NO native messages proves our native-tid
        // hook NEVER FIRED for that action (the managed editor/solution-tree popups → "fired on our native
        // hook" is itself a near-discriminator). Call between John's proc / editor / solution right-clicks.
        public static string PopupMark(string label)
        {
            if (_hookedTid == 0) return "probe_mark: not armed (run probe_popup_arm[_inject] first).";
            Log($"=================== MARK: {(string.IsNullOrEmpty(label) ? "(unlabeled)" : label)} ===================");
            return $"marked: {label}";
        }

        public static string PopupReport()
        {
            uint tid = _hookedTid;
            string dump;
            lock (_gate) dump = _log.Count == 0 ? "(no messages captured)" : string.Join("\r\n", _log);
            int removed = PopupRemoveInternal();
            return $"probe_popup_report (HOOK) — {removed} hook(s) removed, hookedTid={tid}.\r\n--- captured ---\r\n{dump}";
        }

        private static int PopupRemoveInternal()
        {
            _inject = false;                       // flip inject OFF before unhooking (no more AppendMenu)
            int n = 0;
            if (_hCwpRet != IntPtr.Zero) { if (UnhookWindowsHookEx(_hCwpRet)) n++; _hCwpRet = IntPtr.Zero; }
            if (_hGetMsg != IntPtr.Zero) { if (UnhookWindowsHookEx(_hGetMsg)) n++; _hGetMsg = IntPtr.Zero; }
            _hookedTid = 0;
            return n;
        }

        // ============================ hook procs (run on the NATIVE thread) ============================
        // HOT-PATH HYGIENE: these fire on EVERY message on the native thread. code<0 → pass through immediately;
        // peek the message id FIRST (cheap field read, no full marshal) and bail on non-targets; only the 2-3
        // target messages are marshalled + logged; ALWAYS CallNextHookEx; entire body in try/catch (an exception
        // escaping a native callback corrupts the stack). NEVER sync-call the managed thread from here.

        private static IntPtr CwpRetProc(int code, IntPtr w, IntPtr l)
        {
            if (code < 0) return CallNextHookEx(_hCwpRet, code, w, l);
            try
            {
                // CWPRETSTRUCT.message sits after lResult, lParam, wParam (3 pointer-sized fields).
                uint m = unchecked((uint)Marshal.ReadInt32(l, IntPtr.Size * 3));
                string name = TargetName(m);
                if (name != null)
                {
                    var st = (CWPRETSTRUCT)Marshal.PtrToStructure(l, typeof(CWPRETSTRUCT));
                    LogMsg("CWPRETSTRUCT", name, st.hwnd, st.message, st.wParam, st.lParam);
                }
            }
            catch (Exception ex) { try { Log("CwpRetProc EX: " + ex.Message); } catch { } }
            return CallNextHookEx(_hCwpRet, code, w, l);
        }

        private static IntPtr GetMsgProc(int code, IntPtr w, IntPtr l)
        {
            if (code < 0) return CallNextHookEx(_hGetMsg, code, w, l);
            try
            {
                // Log only PM_REMOVE retrievals so a NOREMOVE peek doesn't double-log the same posted message.
                if (w.ToInt64() == PM_REMOVE)
                {
                    // MSG.message sits right after hwnd (1 pointer-sized field).
                    uint m = unchecked((uint)Marshal.ReadInt32(l, IntPtr.Size));
                    string name = TargetName(m);
                    if (name != null)
                    {
                        var msg = (MSG)Marshal.PtrToStructure(l, typeof(MSG));
                        LogMsg("MSG(GETMESSAGE)", name, msg.hwnd, msg.message, msg.wParam, msg.lParam);
                    }
                }
            }
            catch (Exception ex) { try { Log("GetMsgProc EX: " + ex.Message); } catch { } }
            return CallNextHookEx(_hGetMsg, code, w, l);
        }

        private static string TargetName(uint m)
        {
            switch (m)
            {
                case WM_INITMENUPOPUP: return "WM_INITMENUPOPUP";
                case WM_MENUSELECT:    return "WM_MENUSELECT";
                case WM_MENUCOMMAND:   return "WM_MENUCOMMAND";
                case WM_COMMAND:       return "WM_COMMAND";
                case WM_CONTEXTMENU:   return "WM_CONTEXTMENU";
                case WM_RBUTTONUP:     return "WM_RBUTTONUP";
                case WM_ENTERMENULOOP: return "WM_ENTERMENULOOP";
                case WM_EXITMENULOOP:  return "WM_EXITMENULOOP";
                default:               return null;
            }
        }

        private static void LogMsg(string via, string name, IntPtr hwnd, uint m, IntPtr w, IntPtr l)
        {
            string extra = "";
            if (m == WM_INITMENUPOPUP)
            {
                // Lineage of the popup receiver — the stateless CHAIN-WALK discriminator test (Eve/Diana):
                // does the PROC popup chain back (GW_OWNER / GetParent) to the proc-tree ClaList, while the
                // editor + solution-tree popups chain elsewhere? If so, tier-2 (chain-walk) is fully stateless.
                IntPtr owner = GetWindow(hwnd, GW_OWNER);
                IntPtr parent = GetParent(hwnd);
                extra = $"  HMENU=0x{w.ToString("X")} sysMenu={((l.ToInt64() >> 16) & 0xFFFF)}"
                      + $"  | owner=0x{owner.ToString("X")}({ClassOf(owner)}) parent=0x{parent.ToString("X")}({ClassOf(parent)})";
            }
            else if (m == WM_CONTEXTMENU)
                // wParam = the window that was right-clicked (the CLICKED CHILD) — the correlation/arm signal.
                extra = $"  clickedChild=0x{w.ToString("X")}({ClassOf(w)})";
            else if (m == WM_COMMAND)
            {
                long id = w.ToInt64() & 0xFFFF;
                extra = $"  cmdId={id} notify={(w.ToInt64() >> 16) & 0xFFFF} ctl=0x{l.ToString("X")}";
                if (id == PROBE_CMDID) extra += "  <== *** OUR [PROBE] ITEM (0xE001) POSTED A COMMAND ***";
            }
            else if (m == WM_MENUCOMMAND)
                extra = $"  itemPos={w.ToInt64()} hMenu=0x{l.ToString("X")}";
            else if (m == WM_MENUSELECT)
                extra = $"  item={w.ToInt64() & 0xFFFF} flags={(w.ToInt64() >> 16) & 0xFFFF} hMenu=0x{l.ToString("X")}";

            Log($"[{via} recv 0x{hwnd.ToString("X")} {ClassOf(hwnd)}] {name} w=0x{w.ToString("X")} l=0x{l.ToString("X")}{extra}");

            // Non-system popup only: full owner-draw-safe id dump (GetMenuItemID), then — if armed for inject —
            // AppendMenu our one [PROBE] item. Confirms HMENU item count > 0 + the cmdId layout at CALLWNDPROCRET.
            if (m == WM_INITMENUPOPUP && ((l.ToInt64() >> 16) & 0xFFFF) == 0)
            {
                DumpMenuItems(w);
                if (_inject) TryInject(w);
            }
        }

        // PHASE 1.5 — append exactly ONE plain MF_STRING item to the live popup. Dedup by scanning our cmdId
        // first (owner-draw-safe via GetMenuItemID). Reversible: the HMENU is transient, so disarming = simply
        // not re-appending on the next popup. Runs on the native hook thread → fully wrapped in try/catch.
        private static void TryInject(IntPtr hMenu)
        {
            try
            {
                int n = GetMenuItemCount(hMenu);
                for (int i = 0; i < n; i++)
                    if (GetMenuItemID(hMenu, i) == PROBE_CMDID) { Log($"    [inject] 0xE001 already present (n={n}) — skip"); return; }
                bool ok = AppendMenu(hMenu, MF_STRING, (UIntPtr)PROBE_CMDID, "Open in Modern Embeditor [PROBE]");
                int err = Marshal.GetLastWin32Error();
                Log($"    [inject] AppendMenu hMenu=0x{hMenu.ToString("X")} id=0xE001 MF_STRING -> {(ok ? "OK" : "FAILED err=" + err)} (was n={n}, now n={GetMenuItemCount(hMenu)})");
            }
            catch (Exception ex) { try { Log("    [inject] EX: " + ex.Message); } catch { } }
        }

        // Log every popup item's EXACT text (by position) so phase-2 fingerprints the real 'Embeditor' string.
        private static void DumpMenuItems(IntPtr hMenu)
        {
            int n = GetMenuItemCount(hMenu);
            Log($"    HMENU 0x{hMenu.ToString("X")} items={n}:");
            for (int i = 0; i < n; i++)
            {
                var sb = new StringBuilder(256);
                int len = GetMenuString(hMenu, (uint)i, sb, 256, MF_BYPOSITION);
                uint id = GetMenuItemID(hMenu, i);   // owner-draw-safe: real cmdId regardless of (empty) text
                string idStr = id == 0xFFFFFFFF ? "(submenu/sep)" : "0x" + id.ToString("X");
                Log($"      [{i}] id={idStr} (len={len}) '{sb}'");
            }
        }
    }
}
