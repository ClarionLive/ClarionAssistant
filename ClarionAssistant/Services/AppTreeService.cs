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
    /// Service to interact with the Clarion Application tree and embeditor.
    /// Uses reflection to access the Clarion-specific IDE objects.
    /// </summary>
    public class AppTreeService
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags PubStatic = BindingFlags.Public | BindingFlags.Static;

        /// <summary>
        /// Open a .app file in the IDE.
        /// </summary>
        public bool OpenApp(string appPath)
        {
            try
            {
                var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
                if (sharpDevelopAsm == null) return false;

                var fileServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.FileService");
                if (fileServiceType == null) return false;

                var openFileMethod = fileServiceType.GetMethod("OpenFile",
                    PubStatic, null, new Type[] { typeof(string) }, null);
                if (openFileMethod == null) return false;

                openFileMethod.Invoke(null, new object[] { appPath });
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Close the currently open .app file by finding its workbench window and closing it.
        /// </summary>
        public string CloseApp()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return "Error: workbench not available";

                // Reuse the same search that GetAppInfo uses (proven to work)
                var viewContent = FindAppViewContent();
                if (viewContent == null) return "Error: no .app file is open";

                // Get the WorkbenchWindow that hosts this ViewContent
                var parentWindow = GetProp(viewContent, "WorkbenchWindow");
                if (parentWindow == null)
                    return "Error: could not find WorkbenchWindow for the app ViewContent";

                // Try CloseWindow(bool force)
                var closeMethod = parentWindow.GetType().GetMethod("CloseWindow",
                    AllInstance, null, new[] { typeof(bool) }, null);
                if (closeMethod != null)
                {
                    closeMethod.Invoke(parentWindow, new object[] { true });
                    return "App closed";
                }

                // Fallback: try parameterless CloseWindow()
                closeMethod = parentWindow.GetType().GetMethod("CloseWindow",
                    AllInstance, null, Type.EmptyTypes, null);
                if (closeMethod != null)
                {
                    closeMethod.Invoke(parentWindow, null);
                    return "App closed";
                }

                // Last resort: try Close()
                closeMethod = parentWindow.GetType().GetMethod("Close",
                    AllInstance, null, Type.EmptyTypes, null);
                if (closeMethod != null)
                {
                    closeMethod.Invoke(parentWindow, null);
                    return "App closed";
                }

                return "Error: no close method found on " + parentWindow.GetType().FullName;
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        /// <summary>
        /// Get the Application object from the active view (if an .app is open).
        /// </summary>
        /// <summary>
        /// Find the ViewContent for an open .app file. Checks active window first,
        /// then searches all open windows so it works regardless of which tab is focused.
        /// </summary>
        private object FindAppViewContent()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return null;

                // Try active window first (fast path)
                var activeWindow = GetProp(workbench, "ActiveWorkbenchWindow");
                if (activeWindow != null)
                {
                    var vc = GetProp(activeWindow, "ViewContent")
                          ?? GetProp(activeWindow, "ActiveViewContent");
                    if (vc != null && GetProp(vc, "App") != null)
                        return vc;
                }

                // Search all open windows for an app ViewContent
                var windows = GetProp(workbench, "WorkbenchWindowCollection")
                           ?? GetProp(workbench, "ViewContentCollection");
                if (windows is System.Collections.IEnumerable enumerable)
                {
                    foreach (var win in enumerable)
                    {
                        var vc = GetProp(win, "ViewContent")
                              ?? GetProp(win, "ActiveViewContent");
                        if (vc != null && GetProp(vc, "App") != null)
                            return vc;
                    }
                }

                return null;
            }
            catch { return null; }
        }

        private object GetAppObject()
        {
            var viewContent = FindAppViewContent();
            if (viewContent == null) return null;
            return GetProp(viewContent, "App");
        }

        /// <summary>
        /// Get info about the currently open application.
        /// </summary>
        public Dictionary<string, object> GetAppInfo()
        {
            var app = GetAppObject();
            if (app == null) return null;

            return new Dictionary<string, object>
            {
                { "name", GetProp(app, "Name")?.ToString() ?? "" },
                { "fileName", GetProp(app, "FileName")?.ToString() ?? "" },
                { "isLoaded", GetProp(app, "IsLoaded") },
                { "targetType", GetProp(app, "TargetType")?.ToString() ?? "" },
                { "language", GetProp(app, "Language")?.ToString() ?? "" }
            };
        }

        /// <summary>
        /// List all procedure names in the open application.
        /// </summary>
        public List<string> GetProcedureNames()
        {
            var result = new List<string>();
            var app = GetAppObject();
            if (app == null) return result;

            // Try ProcedureNames property (string array)
            var procNames = GetProp(app, "ProcedureNames");
            if (procNames is string[] names)
            {
                result.AddRange(names);
                return result;
            }

            // Fallback: iterate Procedures array
            var procedures = GetProp(app, "Procedures");
            if (procedures is Array procArray)
            {
                foreach (var proc in procArray)
                {
                    var name = GetProp(proc, "Name") ?? GetProp(proc, "ProcedureName");
                    if (name != null) result.Add(name.ToString());
                }
            }

            return result;
        }

        /// <summary>
        /// Get detailed info about procedures in the app (name, type, prototype, module).
        /// </summary>
        public List<Dictionary<string, object>> GetProcedureDetails()
        {
            var result = new List<Dictionary<string, object>>();
            var app = GetAppObject();
            if (app == null) return result;

            var procedures = GetProp(app, "Procedures");
            if (procedures is Array procArray)
            {
                foreach (var proc in procArray)
                {
                    var info = new Dictionary<string, object>
                    {
                        { "name", (GetProp(proc, "Name") ?? GetProp(proc, "ProcedureName") ?? "").ToString() },
                        { "prototype", (GetProp(proc, "Prototype") ?? "").ToString() },
                        { "module", (GetProp(proc, "Module") ?? "").ToString() },
                        { "parent", (GetProp(proc, "Parent") ?? "").ToString() },
                        { "from", (GetProp(proc, "From") ?? "").ToString() }
                    };
                    result.Add(info);
                }
            }

            return result;
        }

        /// <summary>
        /// Find the ClaGenEditor (embeditor) in the active view's secondary view contents.
        /// </summary>
        private object GetClaGenEditor()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return null;

                // Helper to search a ViewContent for a ClaGenEditor
                object SearchViewContent(object vc)
                {
                    if (vc == null) return null;
                    // Check if the ViewContent itself is a ClaGenEditor
                    string vcType = vc.GetType().Name;
                    if (vcType == "ClaGenEditor" || vcType.Contains("GenEditor"))
                        return vc;
                    // Check SecondaryViewContents
                    var secViews = GetProp(vc, "SecondaryViewContents");
                    if (secViews is System.Collections.IEnumerable views)
                    {
                        foreach (var view in views)
                        {
                            string typeName = view.GetType().Name;
                            if (typeName == "ClaGenEditor" || typeName.Contains("GenEditor"))
                                return view;
                        }
                    }
                    return null;
                }

                // Try active window first (fast path)
                var activeWindow = GetProp(workbench, "ActiveWorkbenchWindow");
                if (activeWindow != null)
                {
                    var vc = GetProp(activeWindow, "ViewContent")
                          ?? GetProp(activeWindow, "ActiveViewContent");
                    var editor = SearchViewContent(vc);
                    if (editor != null) return editor;
                }

                // Search all open windows (same pattern as FindAppViewContent)
                var windows = GetProp(workbench, "WorkbenchWindowCollection")
                           ?? GetProp(workbench, "ViewContentCollection");
                if (windows is System.Collections.IEnumerable enumerable)
                {
                    foreach (var win in enumerable)
                    {
                        var vc = GetProp(win, "ViewContent")
                              ?? GetProp(win, "ActiveViewContent");
                        var editor = SearchViewContent(vc);
                        if (editor != null) return editor;
                    }
                }

                return null;
            }
            catch { return null; }
        }

        #region P/Invoke declarations

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const byte VK_SHIFT = 0x10;

        #endregion

        #region Win32 constants

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CHAR = 0x0102;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;

        private const uint LB_GETCOUNT = 0x018B;
        private const uint LB_GETCURSEL = 0x0188;
        private const uint LB_SETCURSEL = 0x0186;
        private const uint LB_GETTEXT = 0x0189;
        private const uint LB_GETTEXTLEN = 0x018A;
        private const uint LB_FINDSTRING = 0x018F;
        private const uint LB_FINDSTRINGEXACT = 0x01A2;

        private const uint BM_CLICK = 0x00F5;
        private const uint BN_CLICKED = 0;

        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;
        private const uint WM_SYSCHAR = 0x0106;

        private const int VK_HOME = 0x24;
        private const int VK_MENU = 0x12;  // ALT key
        private const int VK_RETURN = 0x0D;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int GWL_ID = -12;
        private const int MK_LBUTTON = 0x0001;

        #endregion

        /// <summary>
        /// Enumerate all child windows of a parent and return them with class names.
        /// </summary>
        private List<(IntPtr hwnd, string className, bool visible)> GetChildWindows(IntPtr parentHwnd)
        {
            var children = new List<(IntPtr, string, bool)>();
            EnumChildWindows(parentHwnd, (hwnd, _) =>
            {
                var sb = new StringBuilder(256);
                GetClassName(hwnd, sb, 256);
                children.Add((hwnd, sb.ToString(), IsWindowVisible(hwnd)));
                return true;
            }, IntPtr.Zero);
            return children;
        }

        /// <summary>
        /// Open the embeditor for a specific procedure.
        /// Iteration 14: PostMessage WM_KEYDOWN/WM_CHAR directly to ClaList handle
        /// + AttachThreadInput for cross-thread focus.
        /// </summary>
        public string OpenProcedureEmbed(string procedureName)
        {
            try
            {
                // Check if an embeditor is already open
                var embedInfo = GetEmbedInfo();
                if (embedInfo != null)
                {
                    var openFile = (embedInfo["fileName"] ?? "").ToString();
                    return "Error: An embeditor is already open (" + openFile + "). Please close it before opening another procedure.";
                }

                var log = new StringBuilder();
                log.AppendLine("=== Iteration 14: PostMessage + AttachThreadInput ===");
                log.AppendLine("Target: " + procedureName);

                // --- Get the ApplicationMainWindowControl (searches all windows) ---
                var viewContent = FindAppViewContent();
                if (viewContent == null) return "Error: no ViewContent — is an .app file open?";

                var container = GetProp(viewContent, "_Container")
                             ?? GetProp(viewContent, "ApplicationContainer");
                if (!(container is Control containerCtrl) || containerCtrl.Controls.Count == 0)
                    return "Error: cannot access ApplicationContainer";

                var mainCtrl = containerCtrl.Controls[0] as Control;
                if (mainCtrl == null || !mainCtrl.IsHandleCreated)
                    return "Error: ApplicationMainWindowControl has no handle";

                // --- Find native controls ---
                var children = GetChildWindows(mainCtrl.Handle);
                IntPtr listHwnd = IntPtr.Zero;
                foreach (var (hwnd, cls, vis) in children)
                {
                    if (cls.Contains("ClaList") && vis && listHwnd == IntPtr.Zero)
                        listHwnd = hwnd;
                }

                log.AppendLine("ClaList: " + (listHwnd != IntPtr.Zero ? "0x" + listHwnd.ToString("X") : "NOT FOUND"));
                if (listHwnd == IntPtr.Zero)
                    return log + "\nCannot proceed — ClaList not found";

                // ================================================================
                // PHASE 1: AttachThreadInput + SetFocus on ClaList
                // ================================================================
                log.AppendLine("\n--- Phase 1: AttachThreadInput + Focus ---");

                uint listThreadId = GetWindowThreadProcessId(listHwnd, out _);
                uint curThreadId = GetCurrentThreadId();
                bool attached = false;

                if (listThreadId != curThreadId)
                {
                    attached = AttachThreadInput(curThreadId, listThreadId, true);
                    log.AppendLine("AttachThreadInput: " + (attached ? "OK" : "FAILED") +
                                   " (cur=" + curThreadId + " list=" + listThreadId + ")");
                }
                else
                {
                    log.AppendLine("Same thread — no attach needed");
                }

                SetFocus(listHwnd);
                Application.DoEvents();
                System.Threading.Thread.Sleep(100);

                IntPtr focusWnd = GetFocus();
                log.AppendLine("Focused: 0x" + focusWnd.ToString("X") +
                               (focusWnd == listHwnd ? " (ClaList!)" : " (not ClaList)"));

                // ================================================================
                // PHASE 2: Select procedure in ClaList
                // ================================================================
                log.AppendLine("\n--- Phase 2: Select procedure in ClaList ---");

                bool selected = false;

                // Probe: check if ClaList actually supports LB_ messages
                // LB_GETCOUNT returns item count for real listboxes, but ClaList returns 0
                const uint LB_GETCOUNT = 0x018B;
                int lbCount = (int)SendMessage(listHwnd, LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
                log.AppendLine("LB_GETCOUNT probe: " + lbCount);
                bool claListSupportsLB = (lbCount > 0);

                if (claListSupportsLB)
                {
                    // Approach 1: LB_FINDSTRINGEXACT + LB_SETCURSEL (direct listbox selection)
                    IntPtr foundIndex = SendMessage(listHwnd, LB_FINDSTRINGEXACT, new IntPtr(-1), procedureName);
                    log.AppendLine("LB_FINDSTRINGEXACT('" + procedureName + "'): index=" + foundIndex.ToInt32());

                    if (foundIndex.ToInt32() >= 0)
                    {
                        SendMessage(listHwnd, LB_SETCURSEL, foundIndex, IntPtr.Zero);

                        // Notify parent of selection change (LBN_SELCHANGE = 1)
                        IntPtr listParent = GetParent(listHwnd);
                        int controlId = GetWindowLong(listHwnd, GWL_ID);
                        int wParamNotify = (controlId & 0xFFFF) | (1 << 16);
                        SendMessage(listParent, WM_COMMAND, (IntPtr)wParamNotify, listHwnd);

                        Application.DoEvents();
                        System.Threading.Thread.Sleep(200);
                        Application.DoEvents();

                        log.AppendLine("Selected via LB_SETCURSEL at index " + foundIndex.ToInt32());
                        selected = true;
                    }

                    if (!selected)
                    {
                        // Approach 2: LB_FINDSTRING (prefix match)
                        IntPtr foundIndex2 = SendMessage(listHwnd, LB_FINDSTRING, new IntPtr(-1), procedureName);
                        log.AppendLine("LB_FINDSTRING('" + procedureName + "'): index=" + foundIndex2.ToInt32());

                        if (foundIndex2.ToInt32() >= 0)
                        {
                            SendMessage(listHwnd, LB_SETCURSEL, foundIndex2, IntPtr.Zero);

                            IntPtr listParent = GetParent(listHwnd);
                            int controlId = GetWindowLong(listHwnd, GWL_ID);
                            int wParamNotify = (controlId & 0xFFFF) | (1 << 16);
                            SendMessage(listParent, WM_COMMAND, (IntPtr)wParamNotify, listHwnd);

                            Application.DoEvents();
                            System.Threading.Thread.Sleep(200);
                            Application.DoEvents();

                            log.AppendLine("Selected via LB_FINDSTRING + LB_SETCURSEL at index " + foundIndex2.ToInt32());
                            selected = true;
                        }
                    }
                }

                if (!selected)
                {
                    // Approach 3: PostMessage + WM_CHAR only (matches working SelectProcedure)
                    log.AppendLine("ClaList does not support LB_ messages (count=" + lbCount + "), using keystrokes");

                    foreach (char c in procedureName)
                    {
                        PostMessage(listHwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(100);
                    }
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(500);
                    Application.DoEvents();

                    log.AppendLine("Posted " + procedureName.Length + " WM_CHAR messages");

                    // Down+Up clears the locator's incremental search buffer
                    // without changing the selected item
                    PostMessage(listHwnd, WM_KEYDOWN, (IntPtr)0x28, IntPtr.Zero); // VK_DOWN
                    PostMessage(listHwnd, WM_KEYUP, (IntPtr)0x28, IntPtr.Zero);
                    System.Threading.Thread.Sleep(100);
                    Application.DoEvents();
                    PostMessage(listHwnd, WM_KEYDOWN, (IntPtr)0x26, IntPtr.Zero); // VK_UP
                    PostMessage(listHwnd, WM_KEYUP, (IntPtr)0x26, IntPtr.Zero);
                    System.Threading.Thread.Sleep(300);
                    Application.DoEvents();
                }

                // Verify: read back current selection
                int verifyIdx = (int)SendMessage(listHwnd, LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                if (verifyIdx >= 0)
                {
                    int textLen = (int)SendMessage(listHwnd, LB_GETTEXTLEN, (IntPtr)verifyIdx, IntPtr.Zero);
                    if (textLen > 0)
                    {
                        var selBuf = new StringBuilder(textLen + 1);
                        SendMessage(listHwnd, LB_GETTEXT, (IntPtr)verifyIdx, selBuf);
                        log.AppendLine("Verify — selected item: '" + selBuf.ToString() + "' at index " + verifyIdx);
                    }
                    else
                    {
                        log.AppendLine("Verify — LB_GETTEXTLEN returned " + textLen + " (ClaList may not support LB_ read)");
                    }
                }
                else
                {
                    log.AppendLine("Verify — LB_GETCURSEL returned " + verifyIdx + " (no selection or not a standard listbox)");
                }

                // ================================================================
                // PHASE 3: Open embeditor — find and click the Embeditor button
                // ================================================================
                log.AppendLine("\n--- Phase 3: Click Embeditor button ---");

                IntPtr embeditorBtn = IntPtr.Zero;
                foreach (var (hwnd, cls, vis) in children)
                {
                    if (cls.Contains("ClaButton") && vis)
                    {
                        // Try GetWindowText first
                        var textBuf = new StringBuilder(256);
                        GetWindowText(hwnd, textBuf, 256);
                        string btnText = textBuf.ToString();

                        // If empty, try WM_GETTEXT (custom controls may not respond to GetWindowText)
                        if (string.IsNullOrEmpty(btnText))
                        {
                            int textLen = (int)SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                            if (textLen > 0)
                            {
                                textBuf = new StringBuilder(textLen + 1);
                                SendMessage(hwnd, WM_GETTEXT, (IntPtr)(textLen + 1), textBuf);
                                btnText = textBuf.ToString();
                            }
                        }

                        log.AppendLine("  ClaButton: 0x" + hwnd.ToString("X") + " text='" + btnText + "'");

                        if (btnText.IndexOf("beditor", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            embeditorBtn = hwnd;
                            log.AppendLine("  ^ MATCH — this is the Embeditor button");
                        }
                    }
                }

                if (embeditorBtn == IntPtr.Zero)
                {
                    log.AppendLine("Embeditor button NOT FOUND among ClaButtons");

                    if (attached)
                        AttachThreadInput(curThreadId, listThreadId, false);
                    return log.ToString();
                }

                // Send BM_CLICK to the Embeditor button
                SendMessage(embeditorBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                Application.DoEvents();
                System.Threading.Thread.Sleep(500);
                Application.DoEvents();

                log.AppendLine("BM_CLICK sent to Embeditor button");

                // Detach thread input
                if (attached)
                    AttachThreadInput(curThreadId, listThreadId, false);

                log.AppendLine("\nEmbeditor opened for " + procedureName);
                return log.ToString();
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message) + "\n" + ex.StackTrace;
            }
        }

        /// <summary>
        /// Select a procedure in the ClaList without opening the embeditor. For testing.
        /// </summary>
        public string SelectProcedure(string procedureName)
        {
            try
            {
                // Check if an embeditor is already open
                var embedInfo = GetEmbedInfo();
                if (embedInfo != null)
                {
                    var openFile = (embedInfo["fileName"] ?? "").ToString();
                    return "Error: An embeditor is already open (" + openFile + "). Please close it before selecting a procedure.";
                }

                var log = new StringBuilder();
                log.AppendLine("=== SelectProcedure: " + procedureName + " ===");

                // --- Get the ApplicationMainWindowControl (searches all windows) ---
                var viewContent = FindAppViewContent();
                if (viewContent == null) return "Error: no ViewContent — is an .app file open?";

                var container = GetProp(viewContent, "_Container")
                             ?? GetProp(viewContent, "ApplicationContainer");
                if (!(container is Control containerCtrl) || containerCtrl.Controls.Count == 0)
                    return "Error: cannot access ApplicationContainer";

                var mainCtrl = containerCtrl.Controls[0] as Control;
                if (mainCtrl == null || !mainCtrl.IsHandleCreated)
                    return "Error: ApplicationMainWindowControl has no handle";

                // --- Find ClaList ---
                var children = GetChildWindows(mainCtrl.Handle);
                IntPtr listHwnd = IntPtr.Zero;
                foreach (var (hwnd, cls, vis) in children)
                {
                    if (cls.Contains("ClaList") && vis && listHwnd == IntPtr.Zero)
                        listHwnd = hwnd;
                }

                if (listHwnd == IntPtr.Zero)
                    return "Error: ClaList not found";

                log.AppendLine("ClaList: 0x" + listHwnd.ToString("X"));

                // --- AttachThreadInput + Focus ---
                uint listThreadId = GetWindowThreadProcessId(listHwnd, out _);
                uint curThreadId = GetCurrentThreadId();
                bool attached = false;

                if (listThreadId != curThreadId)
                {
                    attached = AttachThreadInput(curThreadId, listThreadId, true);
                    log.AppendLine("AttachThreadInput: " + (attached ? "OK" : "FAILED"));
                }

                SetFocus(listHwnd);
                Application.DoEvents();
                System.Threading.Thread.Sleep(100);

                IntPtr focusWnd = GetFocus();
                log.AppendLine("Focused: 0x" + focusWnd.ToString("X") +
                               (focusWnd == listHwnd ? " (ClaList)" : " (not ClaList)"));

                // --- Keystroke selection using PostMessage + WM_CHAR only ---
                // Type each character via PostMessage (async, natural queue)
                // 100ms delay + DoEvents between each char so ClaList locator processes them
                foreach (char c in procedureName)
                {
                    PostMessage(listHwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                }
                Application.DoEvents();
                System.Threading.Thread.Sleep(500);
                Application.DoEvents();

                log.AppendLine("Posted " + procedureName.Length + " WM_CHAR messages");

                // Down+Up clears the locator's incremental search buffer
                // without changing the selected item
                PostMessage(listHwnd, WM_KEYDOWN, (IntPtr)0x28, IntPtr.Zero); // VK_DOWN
                PostMessage(listHwnd, WM_KEYUP, (IntPtr)0x28, IntPtr.Zero);
                System.Threading.Thread.Sleep(100);
                Application.DoEvents();
                PostMessage(listHwnd, WM_KEYDOWN, (IntPtr)0x26, IntPtr.Zero); // VK_UP
                PostMessage(listHwnd, WM_KEYUP, (IntPtr)0x26, IntPtr.Zero);
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();

                // Detach
                if (attached)
                    AttachThreadInput(curThreadId, listThreadId, false);

                log.AppendLine("Done — check ClaList visually for selected procedure");
                return log.ToString();
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// Get embed editor info when the embeditor is active.
        /// </summary>
        public Dictionary<string, object> GetEmbedInfo()
        {
            var editor = GetClaGenEditor();
            if (editor == null) return null;

            // ClaGenEditor persists in SecondaryViewContents even when closed.
            // Use IGeneratorDialog as the active-embeditor indicator (same check that
            // save_and_close_embeditor/cancel_embeditor rely on).
            var dialogInterface = editor.GetType().GetInterface("SoftVelocity.Generator.IGeneratorDialog");
            if (dialogInterface == null) return null;

            var appName = (GetProp(editor, "AppName") ?? "").ToString();
            var fileName = (GetProp(editor, "FileName") ?? "").ToString();

            // ClaGenEditor persists with IGeneratorDialog even after closing.
            // When actually active, at least one of appName/fileName will be populated.
            if (string.IsNullOrEmpty(appName) && string.IsNullOrEmpty(fileName))
                return null;

            return new Dictionary<string, object>
            {
                { "appName", appName },
                { "fileName", fileName },
                { "isPwee", GetProp(editor, "IsPwee") },
                { "isOnFirstEmbed", GetProp(editor, "IsOnFirstEmbed") },
                { "isOnLastEmbed", GetProp(editor, "IsOnLastEmbed") },
                { "editorType", editor.GetType().Name }
            };
        }

        /// <summary>
        /// Save changes and close the embeditor.
        /// </summary>
        public string SaveAndCloseEmbeditor()
        {
            var editor = GetClaGenEditor();
            if (editor == null) return "Error: No embeditor is currently open.";

            try
            {
                // Check for the IGeneratorDialog interface which provides TryClose/HaveChanges
                var dialogInterface = editor.GetType().GetInterface("SoftVelocity.Generator.IGeneratorDialog");
                if (dialogInterface == null)
                    return "Error: ClaGenEditor does not implement IGeneratorDialog.";

                var tryCloseMethod = dialogInterface.GetMethod("TryClose");
                if (tryCloseMethod == null)
                    return "Error: TryClose method not found on IGeneratorDialog.";

                bool closed = (bool)tryCloseMethod.Invoke(editor, null);
                return closed
                    ? "Embeditor saved and closed."
                    : "Embeditor TryClose returned false — may have validation errors or was cancelled.";
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// Discard changes and close the embeditor.
        /// </summary>
        public string CancelEmbeditor()
        {
            var editor = GetClaGenEditor();
            if (editor == null) return "Error: No embeditor is currently open.";

            try
            {
                var dialogInterface = editor.GetType().GetInterface("SoftVelocity.Generator.IGeneratorDialog");
                if (dialogInterface == null)
                    return "Error: ClaGenEditor does not implement IGeneratorDialog.";

                var discardMethod = dialogInterface.GetMethod("Discard");
                if (discardMethod == null)
                    return "Error: Discard method not found on IGeneratorDialog.";

                discardMethod.Invoke(editor, null);

                // After discarding, close the editor
                var tryCloseMethod = dialogInterface.GetMethod("TryClose");
                if (tryCloseMethod != null)
                    tryCloseMethod.Invoke(editor, null);

                return "Embeditor changes discarded and closed.";
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// Navigate to the next/previous embed or filled embed in the embeditor
        /// by invoking the corresponding SharpDevelop command class.
        /// </summary>
        public string NavigateEmbed(string direction, bool filledOnly)
        {
            var editor = GetClaGenEditor();
            if (editor == null) return "Error: No embeditor is currently open.";

            string commandName = "SoftVelocity.Generator.Editor.Commands.Goto"
                + (direction == "prev" ? "Prev" : "Next")
                + (filledOnly ? "Filled" : "")
                + "Embed";

            try
            {
                // Find the command type in loaded assemblies (CommonSources.dll)
                Type cmdType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    cmdType = asm.GetType(commandName);
                    if (cmdType != null) break;
                }
                if (cmdType == null)
                    return "Error: Command type not found: " + commandName;

                var cmd = Activator.CreateInstance(cmdType);

                // AbstractMenuCommand requires Owner set to the editor before Run()
                var ownerProp = cmdType.GetProperty("Owner", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (ownerProp != null)
                    ownerProp.SetValue(cmd, editor, null);

                // AbstractMenuCommand.Run() performs the navigation
                var runMethod = cmdType.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (runMethod == null)
                    return "Error: Run() method not found on " + commandName;

                runMethod.Invoke(cmd, null);

                string label = (direction == "prev" ? "Previous" : "Next")
                    + (filledOnly ? " filled" : "")
                    + " embed";
                return "Navigated to " + label + ".";
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// Get the Win32App object from the Application object.
        /// This provides access to lower-level operations like Export/Import.
        /// </summary>
        private object GetWin32App()
        {
            var app = GetAppObject();
            if (app == null) return null;
            return GetProp(app, "Win32App");
        }

        /// <summary>
        /// Export the entire current app to a TXA file.
        /// </summary>
        /// <param name="txaPath">Output TXA file path</param>
        /// <returns>Status message</returns>
        public string ExportTxa(string txaPath)
        {
            try
            {
                var win32App = GetWin32App();
                if (win32App == null)
                    return "Error: No .app file is currently open";

                // Call Export(string txaName, bool all) — always export all
                var exportMethod = win32App.GetType().GetMethod("Export", AllInstance,
                    null, new Type[] { typeof(string), typeof(bool) }, null);

                if (exportMethod == null)
                    return "Error: Export method not found on Win32App";

                bool result = (bool)exportMethod.Invoke(win32App, new object[] { txaPath, true });

                if (!result)
                    return "Error: Export returned false — export may have failed";

                // Verify the file was created (retry briefly — IDE may not have flushed to disk yet)
                bool fileFound = false;
                for (int i = 0; i < 10; i++)
                {
                    if (System.IO.File.Exists(txaPath))
                    {
                        fileFound = true;
                        break;
                    }
                    System.Threading.Thread.Sleep(200);
                }
                if (!fileFound)
                    return "Error: Export completed but TXA file was not created at " + txaPath;

                long fileSize = new System.IO.FileInfo(txaPath).Length;
                return "Exported entire app to " + txaPath + " (" + fileSize + " bytes)";
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// Import a TXA file into the current app.
        /// </summary>
        /// <param name="txaPath">Input TXA file path</param>
        /// <param name="clashMode">"rename" (default) or "replace"</param>
        /// <returns>Status message</returns>
        public string ImportTxa(string txaPath, string clashMode)
        {
            try
            {
                var win32App = GetWin32App();
                if (win32App == null)
                    return "Error: No .app file is currently open";

                if (!System.IO.File.Exists(txaPath))
                    return "Error: TXA file not found: " + txaPath;

                // Resolve ImportClashMode enum value
                // Values: Ask=0, Rename=2, Replace=3
                var genAsm = win32App.GetType().Assembly;
                var clashModeType = genAsm.GetType("Clarion.GEN.ImportClashMode");
                if (clashModeType == null)
                    return "Error: ImportClashMode enum not found";

                object clashModeValue;
                switch ((clashMode ?? "rename").ToLowerInvariant())
                {
                    case "replace":
                        clashModeValue = Enum.ToObject(clashModeType, 3);
                        break;
                    case "rename":
                    default:
                        clashModeValue = Enum.ToObject(clashModeType, 2);
                        break;
                }

                // Call Import(string txaName, ImportClashMode clashMode)
                var importMethod = win32App.GetType().GetMethod("Import", AllInstance,
                    null, new Type[] { typeof(string), clashModeType }, null);

                if (importMethod == null)
                    return "Error: Import method not found on Win32App";

                bool result = (bool)importMethod.Invoke(win32App, new object[] { txaPath, clashModeValue });

                if (!result)
                    return "Error: Import returned false — import may have failed";

                return "Successfully imported " + txaPath + " (clash mode: " + (clashMode ?? "rename") + ")";
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// List all embed sections in the active embeditor by walking the PweeEditorDetails.Parts tree.
        /// Returns section names with filled status and nesting depth.
        /// </summary>
        public List<Dictionary<string, object>> ListEmbeds()
        {
            var editor = GetClaGenEditor();
            if (editor == null) return null;

            var pweeDetails = GetProp(editor, "PweeEditorDetails");
            if (pweeDetails == null) return null;

            var parts = GetProp(pweeDetails, "Parts") as Array;
            if (parts == null) return null;

            var results = new List<Dictionary<string, object>>();
            WalkParts(parts, results, 0);
            return results;
        }

        private void WalkParts(Array parts, List<Dictionary<string, object>> results, int depth)
        {
            if (parts == null) return;
            foreach (var part in parts)
            {
                if (part == null) continue;
                string typeName = part.GetType().Name;

                if (typeName == "CPweeSection")
                {
                    string header = (GetProp(part, "Header") ?? "").ToString();
                    // Parse embed name from header like: ! Start of "Local Procedures"
                    string embedName = ParseEmbedName(header);
                    if (!string.IsNullOrEmpty(embedName))
                    {
                        // Check if any child embed points have content
                        bool hasFilled = HasFilledEmbedPoints(GetProp(part, "Parts") as Array);
                        results.Add(new Dictionary<string, object>
                        {
                            { "name", embedName },
                            { "filled", hasFilled },
                            { "depth", depth }
                        });
                    }

                    // Recurse into child parts
                    var childParts = GetProp(part, "Parts") as Array;
                    if (childParts != null)
                        WalkParts(childParts, results, depth + 1);
                }
            }
        }

        private bool HasFilledEmbedPoints(Array parts)
        {
            if (parts == null) return false;
            foreach (var part in parts)
            {
                if (part == null) continue;
                string typeName = part.GetType().Name;
                if (typeName == "CPweeEmbedPoint")
                {
                    try
                    {
                        var text = GetProp(part, "Text");
                        if (text != null)
                        {
                            string content = (GetProp(text, "Text") ?? "").ToString().Trim();
                            if (!string.IsNullOrEmpty(content))
                                return true;
                        }
                    }
                    catch { }
                }
                else if (typeName == "CPweeSection")
                {
                    if (HasFilledEmbedPoints(GetProp(part, "Parts") as Array))
                        return true;
                }
            }
            return false;
        }

        private static string ParseEmbedName(string header)
        {
            if (string.IsNullOrEmpty(header)) return null;
            // Format: ! Start of "Embed Name"
            const string prefix = "! Start of \"";
            int idx = header.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int start = idx + prefix.Length;
            int end = header.IndexOf('"', start);
            if (end < 0) return header.Substring(start);
            return header.Substring(start, end - start);
        }

        /// <summary>
        /// Find an embed section by name in the embeditor and navigate to it.
        /// Searches the editor text for the section header comment and positions the cursor there.
        /// </summary>
        public string FindEmbed(string searchName, EditorService editorService)
        {
            var editor = GetClaGenEditor();
            if (editor == null) return "Error: No embeditor is currently open.";

            // Get the editor text content
            string text = editorService.GetActiveDocumentContent();
            if (string.IsNullOrEmpty(text))
                return "Error: Could not read embeditor text content.";

            // Search for the section header: ! Start of "NAME"
            // Use case-insensitive contains matching on the search term
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            string searchLower = searchName.ToLowerInvariant();

            int bestLine = -1;
            string bestName = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string embedName = ParseEmbedName(line);
                if (embedName != null && embedName.ToLowerInvariant().Contains(searchLower))
                {
                    bestLine = i;
                    bestName = embedName;
                    break;
                }
            }

            if (bestLine < 0)
                return "Error: No embed section matching \"" + searchName + "\" found. Use list_embeds to see available sections.";

            // Navigate to the line after the header (where the embed point is)
            // The embed point content starts on the next line after "! Start of ..."
            int targetLine = bestLine + 2; // +1 for 1-based, +1 to skip header line
            editorService.GoToLine(targetLine);

            return "Navigated to embed \"" + bestName + "\" at line " + targetLine + ".";
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var prop = obj.GetType().GetProperty(name, AllInstance);
                if (prop != null) return prop.GetValue(obj, null);
                var field = obj.GetType().GetField(name, AllInstance);
                return field?.GetValue(obj);
            }
            catch { return null; }
        }
    }
}
