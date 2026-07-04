using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
        /// <summary>
        /// Locate the application (.app) ViewContent regardless of which tab is currently active.
        /// A collection entry may be an IWorkbenchWindow (has .ViewContent) OR an IViewContent directly
        /// (has .App) — we check both, across several possible collection property names. This is what
        /// lets save/open work when a Modern Embeditor tab (not the app) is the active document.
        /// </summary>
        private object FindAppViewContent()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return null;

                // From a candidate (window or view content), return an app-bearing view content.
                Func<object, object> appFrom = obj =>
                {
                    if (obj == null) return null;
                    if (GetProp(obj, "App") != null) return obj;                 // the item IS the app view
                    var vc = GetProp(obj, "ViewContent") ?? GetProp(obj, "ActiveViewContent");
                    if (vc != null && GetProp(vc, "App") != null) return vc;     // item is a window hosting it
                    return null;
                };

                var fast = appFrom(GetProp(workbench, "ActiveWorkbenchWindow"));
                if (fast != null) return fast;

                string[] collNames = { "WorkbenchWindowCollection", "ViewContentCollection",
                                       "PrimaryViewContents", "Windows" };
                foreach (var cn in collNames)
                {
                    var coll = GetProp(workbench, cn) as System.Collections.IEnumerable;
                    if (coll == null) continue;
                    foreach (var item in coll)
                    {
                        var found = appFrom(item);
                        if (found != null) return found;
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
        /// Bring the application (.app) view to the foreground so embeditor automation has the app tree
        /// to drive. OpenProcedureEmbed manipulates the native ClaList in the app window; if a different
        /// tab (e.g. a Modern Embeditor view) is active, the open silently fails. Call this first.
        /// Returns true if an app view was found and selected.
        /// </summary>
        public bool ActivateAppView()
        {
            try
            {
                var vc = FindAppViewContent();
                if (vc == null) return false;
                var window = GetProp(vc, "WorkbenchWindow");
                if (window == null) return false;
                var select = window.GetType().GetMethod("SelectWindow", Type.EmptyTypes);
                if (select == null) return false;
                select.Invoke(window, null);
                Application.DoEvents();
                System.Threading.Thread.Sleep(150);
                Application.DoEvents();
                return true;
            }
            catch { return false; }
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
        /// The procedure currently SELECTED in the app tree (single left-click), read from the app
        /// ViewContent's FileSchema provider — the same managed surface that drives Clarion's own
        /// "Data / Tables" pad. Updates live on selection change with NO embeditor open. Pure managed
        /// reflection (never reinterprets native pointers). Returns null when no .app view is present
        /// or nothing is selected.
        ///
        /// FOCUS-AGNOSTIC by design: returns the tree selection regardless of which tab is focused.
        /// The Data pad enforces FOCUS-WINS precedence by checking the focused embeditor FIRST
        /// (ActiveProcedureContext.PeekActiveProcedure) and only falling back to this when none is.
        /// FileSchema.ProcedureName is the generated-module proc name and matches App.ProcedureNames.
        /// </summary>
        public string GetAppTreeSelectedProcedureName()
        {
            try
            {
                // MULTI-APP AMBIGUITY GUARD (fail-closed): selection lookup AND the pad's .txa/dict caches all
                // resolve "the app" via FindAppViewContent, which is unambiguous only with exactly ONE app open.
                // FindAppViewContent must stay loose (it has to find the app view even while an embeditor is the
                // active document — the native/Modern paths depend on that), so with 2+ apps open and no embeditor
                // focused it could bind the wrong app and the pad would show plausible-but-WRONG data. Rather than
                // guess, fail closed: return null so the pad shows nothing in selection mode while multiple apps
                // are open. Focus mode (open the procedure in an embeditor) is per-editor and stays unambiguous,
                // so multi-app users still get pad data by opening the procedure.
                if (CountOpenAppViews() != 1) return null;
                var vc = FindAppViewContent();               // ApplicationMainWindowControl_ViewContent — has .App and .FileSchema
                if (vc == null) return null;
                var fileSchema = GetProp(vc, "FileSchema");  // SoftVelocity.DataDictionary.Schema.FileSchema, repopulated on selection
                if (fileSchema == null) return null;
                var name = GetProp(fileSchema, "ProcedureName") as string;
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch { return null; }
        }

        /// <summary>
        /// The app view's live FileSchema model (SoftVelocity.DataDictionary.Schema.FileSchema) for the procedure
        /// the app tree is CURRENTLY showing, or null. This is the per-procedure schema that backs Clarion's own
        /// "Data / Tables" pad — it exposes <c>ProcedureName</c> plus the per-template-instance <c>Templates</c>
        /// (File-Browsing List Box / Update Record on Disk / Relation Tree ...), <c>OtherFiles</c>, and the
        /// Local/Global/Module FieldLists. Repopulated on app-tree selection. Pure managed reflection — NEVER
        /// reinterprets native pointers. Unlike <see cref="GetAppTreeSelectedProcedureName"/> this does NOT impose
        /// the single-app guard: callers gate on the returned <c>ProcedureName</c> instead (an exact proc match is
        /// a stronger correctness check than app-count), so it still serves multi-app users.
        /// </summary>
        public object GetAppFileSchema()
        {
            try
            {
                var vc = FindAppViewContent();
                return vc == null ? null : GetProp(vc, "FileSchema");
            }
            catch { return null; }
        }

        /// <summary>
        /// Number of DISTINCT open application (.app) views in the workbench. Used by the Data pad's app-tree
        /// SELECTION path to detect ambiguity: with more than one app open, the loose FindAppViewContent resolver
        /// can't tell which app a tree selection belongs to, so the selection path fails closed. Counts distinct
        /// App objects across the same workbench collections FindAppViewContent searches. Pure managed reflection.
        /// </summary>
        public int CountOpenAppViews()
        {
            var apps = new HashSet<object>();
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return 0;

                Func<object, object> appFrom = obj =>
                {
                    if (obj == null) return null;
                    var a = GetProp(obj, "App");
                    if (a != null) return a;
                    var vc = GetProp(obj, "ViewContent") ?? GetProp(obj, "ActiveViewContent");
                    return vc != null ? GetProp(vc, "App") : null;
                };

                // UNION across ALL collections (do NOT break on the first non-empty one): a single collection can
                // expose only a subset of open apps (the workbench properties are alternate heuristics, not one
                // canonical list). Undercounting here would let the guard read "1" while 2 apps are open and
                // re-enable ambiguous binding — the exact failure this guard prevents. The HashSet dedups the same
                // App object appearing across multiple collections (App is a reference-stable model object), so the
                // union counts DISTINCT apps. Strictly more conservative than first-collection-wins (fails closed
                // more readily), which is the safe direction for an ambiguity guard.
                string[] collNames = { "WorkbenchWindowCollection", "ViewContentCollection",
                                       "PrimaryViewContents", "Windows" };
                foreach (var cn in collNames)
                {
                    var coll = GetProp(workbench, cn) as System.Collections.IEnumerable;
                    if (coll == null) continue;
                    foreach (var item in coll)
                    {
                        var app = appFrom(item);
                        if (app != null) apps.Add(app);
                    }
                }
            }
            catch { }
            return apps.Count;
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

        /// <summary>The open native embeditor (ClaGenEditor) view content, or null. Public entry for the
        /// live-linked Monaco OVERLAY (ticket a5bbf005): we dock the Monaco surface onto its host panel and
        /// hook its Disposed for teardown.</summary>
        public object GetOpenClaGenEditor()
        {
            return GetClaGenEditor();
        }

        /// <summary>The WinForms Panel that hosts the open native embeditor's text area — i.e. ClaGenEditor.Control,
        /// which CC's probe (a5bbf005) proved is a System.Windows.Forms.Panel sitting exactly over the source text
        /// area (below the Source/Design tab strip). This is the DOCK TARGET for the Monaco overlay: adding a
        /// Dock=Fill child + BringToFront covers exactly the embeditor and nothing else. Null if no embed is open,
        /// or the host isn't a Control.</summary>
        public System.Windows.Forms.Control GetClaGenEditorHost()
        {
            try { return GetProp(GetClaGenEditor(), "Control") as System.Windows.Forms.Control; }
            catch { return null; }
        }

        /// <summary>The PweeEditorDetails object of the currently-open native embeditor — non-null exactly when a
        /// PWEE embed is loaded (CommonGenEditor.IsPwee). This is the type-name-independent "an embed is open"
        /// signal the poll monitor (ticket 4d16b53a, adapted from Mark Sarson's EmbedEditorMonitor, CA issue #55)
        /// keys on; its OBJECT IDENTITY changes when a different procedure is loaded into the reused ClaGenEditor,
        /// which is the monitor's "reload the overlay" trigger. Null if no embed is open.</summary>
        public object GetOpenPweeDetails()
        {
            try { return GetProp(GetClaGenEditor(), "PweeEditorDetails"); }
            catch { return null; }
        }

        /// <summary>
        /// The native ClaGenEditor IFF its text area is the currently-focused editor surface, else null.
        /// "Existence ≠ focus": GetClaGenEditor() returns the editor even when it persists hidden in
        /// SecondaryViewContents, so we prove focus by object identity — the active text surface must BE
        /// this editor's own text area. A focused Modern (WebView2) tab or proc tree yields no text area
        /// (so this returns null); a focused plain .clw editor yields a DIFFERENT text area (also null).
        /// </summary>
        public object GetFocusedClaGenEditor()
        {
            try
            {
                var editor = GetClaGenEditor();
                if (editor == null) return null;
                var tec = GetProp(editor, "TextEditorControl");
                var tac = GetProp(tec, "ActiveTextAreaControl");
                var ta = GetProp(tac, "TextArea");
                if (ta == null) return null;
                return new EditorService().IsActiveTextArea(ta) ? editor : null;
            }
            catch { return null; }
        }

        /// <summary>True when the native (PWEE) embeditor is the focused editor surface.</summary>
        public bool IsNativeEmbeditorFocused()
        {
            return GetFocusedClaGenEditor() != null;
        }

        /// <summary>
        /// The focused native embeditor's ICSharpCode text area (for direct, focus-independent insert/goto),
        /// or null when the native embeditor isn't the focused surface. The resolver captures this while the
        /// embeditor is focused and reuses it after the Data pad takes focus.
        /// </summary>
        public object GetFocusedNativeTextArea()
        {
            try
            {
                var editor = GetFocusedClaGenEditor();
                if (editor == null) return null;
                var tec = GetProp(editor, "TextEditorControl");
                var tac = GetProp(tec, "ActiveTextAreaControl");
                return GetProp(tac, "TextArea");
            }
            catch { return null; }
        }

        /// <summary>
        /// If the native (PWEE) embeditor is the FOCUSED editor, return its procedure name; else null.
        /// Primary source is the ClaGenEditor view header "&lt;Proc&gt; - Embeditor - (&lt;module&gt;.clw)"
        /// (carries proc + module, no buffer parse), validated against App.ProcedureNames. Falls back to a
        /// validated col-0 "Name PROCEDURE" scan of the live buffer (ProcNameFromSource).
        /// </summary>
        public string GetFocusedNativeEmbeditorProcName()
        {
            try
            {
                var editor = GetFocusedClaGenEditor();
                if (editor == null) return null;

                var known = GetProcedureNames();
                string fromHeader = ProcFromHeaderTitle(
                    (GetProp(editor, "HeaderTitle") ?? GetProp(editor, "TitleName") ?? GetProp(editor, "TabPageText")) as string);
                if (!string.IsNullOrEmpty(fromHeader) && (known.Count == 0 || ContainsIgnoreCase(known, fromHeader)))
                    return fromHeader;

                // Fallback: validated col-0 regex over the live (raw) buffer.
                return ModernEmbeditorLauncher.ProcNameFromSource(new EditorService().GetActiveDocumentContent(), known);
            }
            catch { return null; }
        }

        // "Main - Embeditor - (clbrws002.clw)" -> "Main". Null if the " - Embeditor" marker is absent.
        private static string ProcFromHeaderTitle(string header)
        {
            if (string.IsNullOrEmpty(header)) return null;
            int i = header.IndexOf(" - Embeditor", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? header.Substring(0, i).Trim() : null;
        }

        private static bool ContainsIgnoreCase(ICollection<string> names, string name)
        {
            if (names == null || name == null) return false;
            foreach (var n in names)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
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
        private const int VK_CONTROL = 0x11;
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

        // The native ApplicationMainWindowControl (CWControl_Host) hosting the open app, or null.
        private Control GetAppMainControl()
        {
            var viewContent = FindAppViewContent();
            if (viewContent == null) return null;
            var container = GetProp(viewContent, "_Container") ?? GetProp(viewContent, "ApplicationContainer");
            if (!(container is Control containerCtrl) || containerCtrl.Controls.Count == 0) return null;
            return containerCtrl.Controls[0] as Control;
        }

        // Read a control's text — GetWindowText, falling back to WM_GETTEXT for custom Clarion controls
        // (which often don't answer GetWindowText).
        private string GetControlText(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            if (sb.Length > 0) return sb.ToString();
            var sb2 = new StringBuilder(256);
            SendMessage(hwnd, WM_GETTEXT, (IntPtr)256, sb2);
            return sb2.ToString();
        }

        // Post a Ctrl+<vk> chord to a window (Ctrl down, key down/up, Ctrl up).
        private void CtrlKey(IntPtr hwnd, int vk)
        {
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_CONTROL, IntPtr.Zero);
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_CONTROL, IntPtr.Zero);
        }

        /// <summary>
        /// Read-only reflection dump of the native ApplicationMainWindowControl (reachable only from addin
        /// code, not the App root): its managed methods plus the enum values behind GlobalRequest/
        /// GlobalResponse. Used to hunt for a clean managed trigger to switch the app's in-window tab to
        /// "Global Embeds" (which fires the ABC class read) instead of synthetic input.
        /// </summary>
        public string DumpAppMainControlApi()
        {
            var sb = new StringBuilder();
            try
            {
                var mainCtrl = GetAppMainControl();
                if (mainCtrl == null) return "Error: no ApplicationMainWindowControl — is an .app open?";
                sb.AppendLine("ApplicationMainWindowControl = " + mainCtrl.GetType().FullName);
                sb.AppendLine();
                DumpReflectMembers(mainCtrl, sb);

                var t = mainCtrl.GetType();
                foreach (var name in new[] { "GlobalRequest", "GlobalResponse", "RequestType", "GlobalRequestType" })
                {
                    var fi = t.GetField(name, AllInstance);
                    var pi = fi == null ? t.GetProperty(name, AllInstance) : null;
                    Type mt = fi != null ? fi.FieldType : (pi != null ? pi.PropertyType : null);
                    if (mt == null) continue;
                    object val = null;
                    try { val = fi != null ? fi.GetValue(mainCtrl) : pi.GetValue(mainCtrl); } catch { }
                    sb.AppendLine();
                    sb.AppendLine("== " + name + " : " + mt.FullName + (mt.IsEnum ? " [enum]" : "") + " ==");
                    sb.AppendLine("current = " + (val ?? "(null)"));
                    if (mt.IsEnum) sb.AppendLine("values  = " + string.Join(", ", Enum.GetNames(mt)));
                }
            }
            catch (Exception ex) { return sb + "\nError: " + (ex.InnerException?.Message ?? ex.Message); }
            return sb.ToString();
        }

        /// <summary>
        /// Open the embeditor for a specific procedure.
        /// Iteration 14: PostMessage WM_KEYDOWN/WM_CHAR directly to ClaList handle
        /// + AttachThreadInput for cross-thread focus.
        /// </summary>
        public string OpenProcedureEmbed(string procedureName) { return OpenProcedureEmbed(procedureName, 100); }

        /// <summary>
        /// Opens the embeditor for a procedure by driving the native app tree. The procedure name is typed
        /// into the ClaList incremental-search locator one char at a time with <paramref name="charDelayMs"/>
        /// between keys — ClaList drops keystrokes that arrive too fast, so this can't be rushed. (A Ctrl+V
        /// paste would be instant but only works when the locator FIELD has focus, which we can't set
        /// programmatically; WM_CHAR drives the search without focus.) Callers needing certainty should verify
        /// the opened procedure and retry slower if it mismatched.
        /// </summary>
        public string OpenProcedureEmbed(string procedureName, int charDelayMs)
        {
            bool attached = false;
            uint curThreadId = 0, listThreadId = 0;
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

                listThreadId = GetWindowThreadProcessId(listHwnd, out _);
                curThreadId = GetCurrentThreadId();

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
                    log.AppendLine("ClaList does not support LB_ messages (count=" + lbCount + "), using locator");

                    log.AppendLine("Typing name via WM_CHAR (" + charDelayMs + "ms/char)");
                    foreach (char c in procedureName)
                    {
                        PostMessage(listHwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(charDelayMs < 1 ? 1 : charDelayMs);
                    }
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(250);
                    Application.DoEvents();

                    // Down+Up commits the incremental-search highlight as the real selection.
                    PostMessage(listHwnd, WM_KEYDOWN, (IntPtr)0x28, IntPtr.Zero); // VK_DOWN
                    PostMessage(listHwnd, WM_KEYUP, (IntPtr)0x28, IntPtr.Zero);
                    System.Threading.Thread.Sleep(80);
                    Application.DoEvents();
                    PostMessage(listHwnd, WM_KEYDOWN, (IntPtr)0x26, IntPtr.Zero); // VK_UP
                    PostMessage(listHwnd, WM_KEYUP, (IntPtr)0x26, IntPtr.Zero);
                    System.Threading.Thread.Sleep(200);
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
                // PHASE 3: Open embeditor — find and click the Embeditor button (shared helper)
                // ================================================================
                if (!ClickEmbeditorButton(children, log))
                    return log.ToString();

                log.AppendLine("\nEmbeditor opened for " + procedureName);
                return log.ToString();
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message) + "\n" + ex.StackTrace;
            }
            finally
            {
                // Always release the merged input queue. A leaked AttachThreadInput (e.g. an exception between
                // attach and a manual detach) poisons the NEXT Modern open's WebView2 init and can hang the IDE.
                if (attached) AttachThreadInput(curThreadId, listThreadId, false);
            }
        }

        /// <summary>
        /// <summary>
        /// PHASE 3 (shared) — locate the Embeditor button among the app-window ClaButtons (matched by
        /// "beditor" in its caption) and BM_CLICK it to open the embeditor for the CURRENTLY selected
        /// procedure. Called by both <see cref="OpenProcedureEmbed(string,int)"/> (after type-to-select)
        /// and <see cref="OpenProcedureEmbedCurrentSelection"/> (selection already committed by right-click).
        /// Returns true if the click was sent; false (with diagnostics appended to <paramref name="log"/>)
        /// when the button isn't found.
        /// </summary>
        private bool ClickEmbeditorButton(List<(IntPtr hwnd, string className, bool visible)> children, StringBuilder log)
        {
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
                return false;
            }

            // Send BM_CLICK to the Embeditor button. One DoEvents dispatches the posted click; we do NOT
            // sleep here — the caller (OpenAndMirror) immediately runs WaitForEmbedOpen, which polls
            // GetEmbedInfo() while pumping DoEvents until the embed actually opens. The old fixed
            // Sleep(500) was therefore pure dead latency the poll already covers (~500ms off every open). [perf]
            SendMessage(embeditorBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            Application.DoEvents();

            log.AppendLine("BM_CLICK sent to Embeditor button");
            return true;
        }

        /// <summary>
        /// Select a procedure in the ClaList without opening the embeditor. For testing.
        /// </summary>
        public string SelectProcedure(string procedureName)
        {
            bool attached = false;
            uint curThreadId = 0, listThreadId = 0;
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
                listThreadId = GetWindowThreadProcessId(listHwnd, out _);
                curThreadId = GetCurrentThreadId();

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

                log.AppendLine("Done — check ClaList visually for selected procedure");
                return log.ToString();
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
            finally
            {
                // Always release the merged input queue. A leaked AttachThreadInput (e.g. an exception
                // between attach and the manual detach) poisons the NEXT Modern open's WebView2 init and
                // can hang the IDE. Mirrors the OpenProcedureEmbed try/finally guard.
                if (attached) AttachThreadInput(curThreadId, listThreadId, false);
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
        /// Prefers CommonGenEditor.SaveAndExit() which saves silently; falls back to
        /// IGeneratorDialog.TryClose() if the direct method is unavailable. TryClose
        /// routes through OnBackClick which shows a "Save changes?" MessageBox when
        /// IsDirty is true, blocking the MCP call — so SaveAndExit is strongly preferred.
        /// </summary>
        public string SaveAndCloseEmbeditor()
        {
            var editor = GetClaGenEditor();
            if (editor == null) return "Error: No embeditor is currently open.";

            var et = editor.GetType();
            var dialogInterface = et.GetInterface("SoftVelocity.Generator.IGeneratorDialog");
            var tryCloseMethod = dialogInterface?.GetMethod("TryClose");

            // Strategy A (preferred): decompose persist + close to avoid the SaveAndExit() UI-thread hang.
            // SaveAndExit() bundles save + view-close; the view-close (or a dirty-state "Save changes?" modal)
            // hard-hangs the UI thread — the same family as the open-path Discard() hang. Persist via a discrete
            // Save(), confirm the dirty flag cleared, then close via the reliable IGeneratorDialog.TryClose()
            // (~13ms, no modal because no longer dirty) — the exact primitive that fixed the open path.
            //
            // Activated whenever a no-arg Save() resolves — INDEPENDENT of TryClose — so a missing close
            // primitive never reroutes us into the bundled SaveAndExit() hang (review: Strategy-A fall-through).
            var saveMethod = et.GetMethod("Save", AllInstance, null, Type.EmptyTypes, null);
            if (saveMethod != null)
            {
                try
                {
                    saveMethod.Invoke(editor, null);
                }
                catch (Exception ex)
                {
                    return "Error: Save() threw: " + (ex.InnerException?.Message ?? ex.Message);
                }

                // FAIL CLOSED: only an explicitly-cleared dirty flag confirms the write. A still-true OR an
                // unreadable (null) flag means persistence is unconfirmed — never claim success and never close
                // (review: IsDirty==null treated as success). The native flag may clear ASYNCHRONOUSLY on the UI
                // loop after Save() returns, so poll over a short pumped window before failing — otherwise a
                // healthy save reads as a false failure (review: synchronous-dirty-clear assumption).
                bool? stillDirty = WaitForDirtyClear(editor, 750);
                if (stillDirty != false)
                    return "Error: Save() did not confirm persistence (IsDirty=" + (stillDirty?.ToString() ?? "unreadable") +
                           ") — nothing was closed; the embeditor is left open. Verify and save it manually in the IDE.";

                if (tryCloseMethod == null)
                    return "Error: Save() persisted but no IGeneratorDialog.TryClose is available to close the embeditor — close it manually.";

                // CLOSE STEP — make this byte-for-byte identical to the PROVEN open-path discard close
                // (CancelEmbeditor), the only TryClose() that has ever completed reliably here. The save-close hung
                // because it differed in TWO ways from that path; both re-applied now (team 3-way root-cause):
                //  (1) [Diana] force-SET IsDirty=false via the WRITABLE reflection setter. Save() makes the readable
                //      flag false but evidently leaves a SEPARATE internal modified flag set that TryClose->OnBackClick
                //      reads; while it still looks "dirty", OnBackClick pops a "Save changes?" modal that is stuck
                //      behind the active WebView2 tab -> UI-thread hang. The setter (used by the discard path) clears
                //      that flag. Safe: persistence is already confirmed above, so this can't drop real changes.
                TrySetIsDirtyFalse(editor);
                //  (2) [Eve] re-assert the app view as the active document so TryClose's close-time focus-restore
                //      targets the app window, not the live WebView2 Modern tab (SetFocus on the WebView2 child on the
                //      pumped close stack deadlocks the browser focus handshake). Mirrors OpenAndMirror at open time.
                //      The Modern tab is re-selected afterward by ModernEmbeditorViewContent.BringToFront (deferred).
                ActivateAppView();

                bool closedA;
                try
                {
                    closedA = (bool)tryCloseMethod.Invoke(editor, null);
                }
                catch (Exception ex)
                {
                    return "Error: TryClose threw after a successful save: " + (ex.InnerException?.Message ?? ex.Message);
                }
                // TryClose()==false is a HARD ERROR (review: a wedged-open editor breaks the single-editor
                // invariant; the saver only treats "Error"-prefixed strings as failure).
                if (!closedA)
                    return "Error: changes were saved, but the embeditor did not close (TryClose returned false) — close it in the IDE before the next save/open.";
                // CONFIRM CLOSURE before claiming success, so the MCP-exposed save_and_close_embeditor has the
                // SAME fail-closed-on-close semantics as the saver path (review: split-brain success definition).
                if (!ModernEmbeditorLauncher.WaitForEmbedClosed(this, 3000))
                    return "Error: changes were saved and TryClose succeeded, but the embeditor did not confirm closed — close it in the IDE before the next save/open.";
                return "Embeditor saved and closed (Strategy A).";
            }

            // Strategy B (fallback): NO discrete Save() exists, so the only persist primitive is SaveAndExit() —
            // the SAME call identified as the UI-thread hang. We surface this explicitly (return string) so a hang
            // is unambiguous and a passing test conclusively shows which path ran; it is NOT a silent reroute into
            // the hang (review: silent SaveAndExit fallback).
            var saveAndExit = et.GetMethod("SaveAndExit", AllInstance, null, Type.EmptyTypes, null);
            if (saveAndExit != null)
            {
                // NOTE: effectively DEAD on the current build — a discrete Save() exists, so
                // Strategy A always wins. Kept for older generators. SaveAndExit() bundles save+close, so we can't
                // inject TrySetIsDirtyFalse between them (it must still save); we can at least re-assert the app view
                // first so its internal close's focus-restore targets the app window, not the WebView2 tab. [Eve]
                ActivateAppView();
                try
                {
                    saveAndExit.Invoke(editor, null);
                }
                catch (Exception ex)
                {
                    return "Error: SaveAndExit threw: " + (ex.InnerException?.Message ?? ex.Message);
                }

                // Re-check IsDirty (fail closed, with the same pumped stabilization window): a still-true OR
                // unreadable flag means SaveAndExit may have closed the view with unpersisted changes.
                bool? stillDirty = WaitForDirtyClear(editor, 750);
                if (stillDirty != false)
                    return "Error: SaveAndExit did not confirm persistence (IsDirty=" + (stillDirty?.ToString() ?? "unreadable") +
                           ") — save may not have persisted.";

                // Confirm closure before claiming success (single definition of success — see Strategy A).
                if (!ModernEmbeditorLauncher.WaitForEmbedClosed(this, 3000))
                    return "Error: SaveAndExit reported done but the embeditor did not confirm closed — close it in the IDE.";
                return "Embeditor saved and closed (Strategy B: SaveAndExit fallback).";
            }

            // Last-resort fallback: older builds with neither Save() nor SaveAndExit(). Go through TryClose,
            // which may pop a modal when dirty. Kept for compatibility; should rarely fire.
            try
            {
                if (dialogInterface == null)
                    return "Error: ClaGenEditor does not implement IGeneratorDialog and has no Save/SaveAndExit method.";
                if (tryCloseMethod == null)
                    return "Error: TryClose method not found on IGeneratorDialog.";

                bool closed = (bool)tryCloseMethod.Invoke(editor, null);
                if (!closed)
                    return "Error: TryClose returned false — may have validation errors or was cancelled.";
                if (!ModernEmbeditorLauncher.WaitForEmbedClosed(this, 3000))
                    return "Error: TryClose succeeded but the embeditor did not confirm closed — close it in the IDE.";
                return "Embeditor saved and closed (via TryClose fallback).";
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        // Poll IsDirty over a short event-pumped window: native editors can clear the flag asynchronously on
        // the UI loop AFTER the reflected Save()/SaveAndExit() returns, so an immediate read can spuriously
        // report a healthy save as still-dirty. Returns the final observed value (false = persisted-confirmed;
        // true/null = unconfirmed). Logs first+final so a live run can tell a stale-state race from a real
        // persist failure. UI thread only (mirrors WaitForEmbedClosed's DoEvents pumping).
        private bool? WaitForDirtyClear(object editor, int timeoutMs)
        {
            bool? first = GetIsDirty(editor);
            bool? last = first;
            if (first != false)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(5);
                    last = GetIsDirty(editor);
                    if (last == false) break;
                }
            }
            return last;
        }

        private bool? GetIsDirty(object editor)
        {
            var t = editor.GetType();
            while (t != null)
            {
                var prop = t.GetProperty("IsDirty", AllInstance);
                if (prop != null && prop.CanRead)
                {
                    try { return (bool)prop.GetValue(editor, null); }
                    catch { return null; }
                }
                t = t.BaseType;
            }
            return null;
        }

        // Force the editor's IsDirty flag to false (walking base types for the writable property). Used
        // before TryClose on the DISCARD path so its OnBackClick dirty-check can never pop a blocking
        // "Save changes?" modal — that modal hard-hangs the inline (UI-thread) Modern-open close.
        private void TrySetIsDirtyFalse(object editor)
        {
            var t = editor.GetType();
            while (t != null)
            {
                var prop = t.GetProperty("IsDirty", AllInstance);
                if (prop != null && prop.CanWrite)
                {
                    try { prop.SetValue(editor, false, null); } catch { }
                    return;
                }
                t = t.BaseType;
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

                // Only call the native Discard() when the editor actually HAS changes. On the OPEN path we
                // only READ the embed (never edit), so it isn't dirty — and Discard() on a clean PWEE editor
                // is an unnecessary native call that intermittently takes ~2s or HANGS the UI thread (the
                // confirmed open-freeze). The SAVE error path writes slots first (dirty=true) and still needs
                // to discard. So gate Discard on IsDirty. (NOTE: TryClose was once thought reliable ~13ms, but the
                // 10:52 live log proves it ALSO hangs intermittently — see the close block below for the real cause.)
                bool? dirty = GetIsDirty(editor);
                if (dirty == true)
                {
                    var discardMethod = dialogInterface.GetMethod("Discard");
                    if (discardMethod != null)
                    {
                        discardMethod.Invoke(editor, null);
                    }
                }

                // Force IsDirty=false so TryClose's OnBackClick can never pop a BLOCKING "Save changes?" modal.
                TrySetIsDirtyFalse(editor);

                var tryCloseMethod = dialogInterface.GetMethod("TryClose");
                if (tryCloseMethod != null)
                {
                    // INTERMITTENT OPEN FREEZE (live log 10:52): TryClose() hangs here on OPEN too — IsDirty=False,
                    // no Save(), TrySetIsDirtyFalse already applied — so it is NOT the dirty flag. The unifying cause
                    // is that TryClose's close-time focus-restore touches an ACTIVE WebView2 Modern tab and deadlocks
                    // the browser focus handshake (hangs only when a Modern tab is active; first-open-of-session is
                    // fast). So apply the same fix the save path uses: re-assert the app view as active document so
                    // the focus-restore targets the app window, not the WebView2 child.
                    ActivateAppView();
                    tryCloseMethod.Invoke(editor, null);
                }

                return "Embeditor changes discarded and closed.";
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// Returns the full annotated embeditor source as a string.
        /// Editable embed slots are wrapped in «E:N»/«/E:N» or «E:N/» (empty) tokens where
        /// N is 1-based and maps directly to the line_number param of WriteEmbedContentByLine.
        /// Read-only generated code passes through as-is to provide structural context.
        /// Metadata noise (! Start of, ! End of, ! [Priority N], !!!) is stripped.
        /// Returns null if no active PWEE editor is open.
        /// </summary>
        public string GetEmbeditorSource()
        {
            var editor = GetClaGenEditor();
            if (editor == null) return null;

            var textControl = GetProp(editor, "TextEditorControl");
            if (textControl == null) return null;

            var document = GetProp(textControl, "Document");
            if (document == null) return null;

            var lineManager = GetProp(document, "CustomLineManager");
            if (lineManager == null || !lineManager.GetType().Name.Contains("Pwee")) return null;

            var customLines = GetProp(lineManager, "CustomLines") as System.Collections.IEnumerable;
            if (customLines == null) return null;

            // Build startLine0 → endLine0 map for editable embed points only
            var lineMap = new Dictionary<int, int>();
            foreach (var cl in customLines)
            {
                if (cl == null) continue;
                var readOnly = GetProp(cl, "ReadOnly");
                if (readOnly is bool ro && ro) continue;
                var pweePart = GetProp(cl, "PweePart");
                if (pweePart == null) continue;
                if (pweePart.GetType().GetInterface("SoftVelocity.Generator.PWEE.IPweeEmbedPoint") == null)
                    continue;
                var startNr = GetProp(cl, "StartLineNr");
                var endNr   = GetProp(cl, "EndLineNr");
                if (startNr == null || endNr == null) continue;
                lineMap[(int)startNr] = (int)endNr;
            }

            var totalLinesObj = GetProp(document, "TotalNumberOfLines");
            if (totalLinesObj == null) return null;
            int total = (int)totalLinesObj;

            var getSegMethod  = document.GetType().GetMethod("GetLineSegment", AllInstance);
            var getTextMethod = document.GetType().GetMethod("GetText", AllInstance, null,
                new[] { typeof(int), typeof(int) }, null);
            if (getSegMethod == null || getTextMethod == null) return null;

            var sb = new StringBuilder();
            int lineIdx = 0;
            while (lineIdx < total)
            {
                if (lineMap.TryGetValue(lineIdx, out int endLine))
                {
                    // Decide empty vs filled from the actual buffer content rather than
                    // from (endLine > lineIdx): PWEE does not refresh CustomLine metadata
                    // after our Document.Insert calls, so a freshly written 1-line slot
                    // still reports endLine == lineIdx even though the buffer has text.
                    int embedEnd = Math.Max(endLine, lineIdx);
                    var embedLines = new List<string>();
                    bool hasContent = false;
                    for (int j = lineIdx; j <= embedEnd && j < total; j++)
                    {
                        var seg    = getSegMethod.Invoke(document, new object[] { j });
                        int offset = (int)GetProp(seg, "Offset");
                        int length = (int)GetProp(seg, "Length");
                        string line = (string)getTextMethod.Invoke(document, new object[] { offset, length });
                        embedLines.Add(line);
                        if (line.Trim().Length > 0) hasContent = true;
                    }

                    if (hasContent)
                    {
                        sb.AppendLine("\u00ABE:" + (lineIdx + 1) + "\u00BB");
                        foreach (var line in embedLines)
                            sb.AppendLine(line);
                        sb.AppendLine("\u00AB/E:" + (lineIdx + 1) + "\u00BB");
                    }
                    else
                    {
                        sb.AppendLine("\u00ABE:" + (lineIdx + 1) + "/\u00BB");
                    }
                    lineIdx = embedEnd + 1;
                }
                else
                {
                    var seg    = getSegMethod.Invoke(document, new object[] { lineIdx });
                    int offset = (int)GetProp(seg, "Offset");
                    int length = (int)GetProp(seg, "Length");
                    string text = (string)getTextMethod.Invoke(document, new object[] { offset, length });
                    string trimmed = text.Trim();

                    if (trimmed.Length == 0                 ||
                        trimmed.StartsWith("! Start of ")  ||
                        trimmed.StartsWith("! End of ")    ||
                        trimmed.StartsWith("! [Priority ") ||
                        trimmed.StartsWith("!!!"))
                    {
                        lineIdx++;
                        continue;
                    }

                    sb.AppendLine(text);
                    lineIdx++;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Write code into the embed point identified by the 1-based line number from
        /// GetEmbeditorSource «E:N» tokens. Returns a status message including the line
        /// delta so the caller knows if subsequent token line numbers are stale.
        ///
        /// Implementation notes: this routes through ActiveTextAreaControl.TextArea.Document
        /// and uses Document.Remove + Document.Insert rather than Document.Replace via the
        /// TextEditorControl.Document path. The TextEditorControl.Document/Replace path
        /// appears to be silently rejected on PWEE embed regions — the edit is reported as
        /// succeeding but the document buffer is unchanged. The TextArea path is the same
        /// one EditorService.InsertTextAtCaret uses, which is confirmed to work inside the
        /// PWEE embeditor (it is the known-good workaround: find_embed → go_to_line →
        /// insert_text_at_cursor). We also move the caret into the embed slot before the
        /// edit, mirroring what the interactive path does. PweePart.Data is not touched
        /// directly — IGeneratorDialog.TryClose reads from the document buffer on save.
        /// </summary>
        public string WriteEmbedContentByLine(int lineNumber, string code) { return WriteEmbedContentByLine(lineNumber, code, true); }

        /// <summary>
        /// Writes code into the embed point at the given 1-based line number. When
        /// <paramref name="reindent"/> is true (default, MCP behaviour) each line is prefixed with the
        /// embed point's column indent. When false (Modern Embeditor save), the code is written verbatim —
        /// the caller already supplies buffer-form (indented) lines, so re-indenting would double them.
        /// </summary>
        public string WriteEmbedContentByLine(int lineNumber, string code, bool reindent)
        {
            var editor = GetClaGenEditor();
            if (editor == null) return "Error: No embeditor is currently open.";

            try
            {
                // Route through the TextArea (same path insert_text_at_cursor uses successfully)
                var textControl = GetProp(editor, "TextEditorControl");
                if (textControl == null) return "Error: TextEditorControl not found.";

                var tac = GetProp(textControl, "ActiveTextAreaControl");
                if (tac == null) return "Error: ActiveTextAreaControl not found.";

                var textArea = GetProp(tac, "TextArea");
                if (textArea == null) return "Error: TextArea not found.";

                var document = GetProp(textArea, "Document");
                if (document == null) return "Error: Document not found via TextArea.";

                var caret = GetProp(textArea, "Caret");

                var lineManager = GetProp(document, "CustomLineManager");
                if (lineManager == null) return "Error: Document.CustomLineManager not found.";

                var customLines = GetProp(lineManager, "CustomLines") as System.Collections.IEnumerable;
                if (customLines == null) return "Error: CustomLines not found.";

                // Find the CustomLine whose StartLineNr matches lineNumber-1 (0-based)
                object customLine = null;
                int targetLine0 = lineNumber - 1;
                foreach (var cl in customLines)
                {
                    if (cl == null) continue;
                    var startNr = GetProp(cl, "StartLineNr");
                    if (startNr != null && (int)startNr == targetLine0)
                    {
                        customLine = cl;
                        break;
                    }
                }

                if (customLine == null)
                    return "Error: No embed point found at line " + lineNumber +
                           ". Use get_embeditor_source to get current line numbers.";

                var pweePart = GetProp(customLine, "PweePart");
                if (pweePart == null) return "Error: CustomLine has no PweePart.";
                if (pweePart.GetType().GetInterface("SoftVelocity.Generator.PWEE.IPweeEmbedPoint") == null)
                    return "Error: Line " + lineNumber + " is a read-only generated section, not an embed point.";

                // Normalise input to LF internally
                code = code.Replace("\r\n", "\n").Replace("\r", "\n");

                int startLine0 = (int)GetProp(customLine, "StartLineNr");
                int endLine0   = (int)GetProp(customLine, "EndLineNr");

                // Resolve Insert/Remove/GetLineSegment using default binding flags (public
                // instance only) — matches EditorService.InsertTextAtCaret/ReplaceRange
                var getSegMethod = document.GetType().GetMethod("GetLineSegment", new[] { typeof(int) });
                var insertMethod = document.GetType().GetMethod("Insert",         new[] { typeof(int), typeof(string) });
                var removeMethod = document.GetType().GetMethod("Remove",         new[] { typeof(int), typeof(int) });
                if (getSegMethod == null) return "Error: Document.GetLineSegment not found.";
                if (insertMethod == null) return "Error: Document.Insert not found.";

                var startSeg   = getSegMethod.Invoke(document, new object[] { startLine0 });
                var endSeg     = getSegMethod.Invoke(document, new object[] { endLine0 });
                int startOff   = (int)GetProp(startSeg, "Offset");
                int endOff     = (int)GetProp(endSeg, "Offset") + (int)GetProp(endSeg, "Length");
                int replaceLen = endOff - startOff;

                string indented;
                if (reindent)
                {
                    // Indentation is driven by the embed point's column position
                    var textSection = GetProp(pweePart, "Text");
                    int column = textSection != null ? Convert.ToInt32(GetProp(textSection, "Column") ?? 1) : 1;
                    string indent = column > 1 ? new string(' ', column - 1) : string.Empty;

                    string[] codeLines = code.Split(new[] { "\n" }, StringSplitOptions.None);
                    indented = string.Join("\r\n", System.Array.ConvertAll(codeLines,
                        l => string.IsNullOrEmpty(l) ? l : indent + l));
                }
                else
                {
                    // Verbatim: caller supplies buffer-form lines already; just use CRLF endings.
                    indented = code.Replace("\n", "\r\n");
                }

                int oldLineCount = endLine0 - startLine0 + 1;
                int newLineCount = 1;
                foreach (char c in indented) if (c == '\n') newLineCount++;
                int lineDelta = newLineCount - oldLineCount;

                // Move the caret into the embed slot before mutating. Interactive edits
                // through the PWEE UI only succeed when the caret is positioned inside the
                // slot being edited, and insert_text_at_cursor (the known-good workaround)
                // implicitly relies on this. Doing it explicitly keeps behaviour aligned.
                if (caret != null)
                {
                    var caretOffsetProp = caret.GetType().GetProperty("Offset");
                    if (caretOffsetProp != null && caretOffsetProp.CanWrite)
                        caretOffsetProp.SetValue(caret, startOff, null);
                }

                // Remove existing embed content (if any), then insert the new text.
                // We deliberately avoid Document.Replace here — on PWEE embed regions it
                // reports success but the buffer is unchanged, whereas Insert/Remove via
                // this code path is the same one InsertTextAtCaret uses successfully.
                if (replaceLen > 0 && removeMethod != null)
                    removeMethod.Invoke(document, new object[] { startOff, replaceLen });

                insertMethod.Invoke(document, new object[] { startOff, indented });

                // Move caret to the end of the inserted region
                if (caret != null)
                {
                    var caretOffsetProp = caret.GetType().GetProperty("Offset");
                    if (caretOffsetProp != null && caretOffsetProp.CanWrite)
                        caretOffsetProp.SetValue(caret, startOff + indented.Length, null);
                }

                // Update EndLineNr on the CustomLine to reflect the new line count.
                // CommonGenEditor.Save() reads document content via StartLineNr..EndLineNr
                // (not from PweePart.Data), but PWEE does not refresh these after Document.Insert.
                // Without this fix, Save() slices only the original single-line range and the
                // embed appears empty on next open — even though the buffer showed the correct text.
                var endLineNrProp = customLine.GetType().GetProperty("EndLineNr", AllInstance);
                if (endLineNrProp != null && endLineNrProp.CanWrite)
                    endLineNrProp.SetValue(customLine, startLine0 + newLineCount - 1, null);

                // Mark the CustomLine and the editor view dirty so save-and-close persists the edit
                var dirtyField = customLine.GetType().GetField("Dirty", AllInstance);
                if (dirtyField != null) dirtyField.SetValue(customLine, true);

                SetIsDirty(editor, true);

                try { textArea.GetType().GetMethod("Invalidate", Type.EmptyTypes)?.Invoke(textArea, null); } catch { }

                var log = new StringBuilder();
                log.AppendLine("Wrote to embed at line " + lineNumber + ".");
                if (lineDelta == 0)
                    log.AppendLine("Line count unchanged — get_embeditor_source tokens remain valid.");
                else
                    log.AppendLine("Line count changed by " + (lineDelta > 0 ? "+" : "") + lineDelta
                        + " — call get_embeditor_source again before writing to embeds after line " + lineNumber + ".");
                return log.ToString().Trim();
            }
            catch (Exception ex)
            {
                return "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// Search the annotated embeditor source for lines matching a regex pattern.
        /// Returns matching lines with contextLines of surrounding source for each match.
        /// Overlapping match windows are merged. Output is capped at ~6 KB.
        /// Returns null if no PWEE editor is open.
        /// </summary>
        public string SearchEmbeditorSource(string pattern, int contextLines = 5)
        {
            var source = GetEmbeditorSource();
            if (source == null) return null;

            var lines = source.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            Regex rx;
            try { rx = new Regex(pattern, RegexOptions.IgnoreCase); }
            catch (Exception ex) { return "Error: invalid pattern — " + ex.Message; }

            // Collect [start, end] ranges for each match (with context), then merge overlaps
            var ranges = new List<int[]>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (rx.IsMatch(lines[i]))
                {
                    int from = Math.Max(0, i - contextLines);
                    int to   = Math.Min(lines.Length - 1, i + contextLines);
                    ranges.Add(new[] { from, to });
                }
            }

            if (ranges.Count == 0)
                return "No matches for: " + pattern;

            // Merge overlapping/adjacent ranges
            var merged = new List<int[]> { ranges[0] };
            foreach (var r in ranges)
            {
                var last = merged[merged.Count - 1];
                if (r[0] <= last[1] + 1) last[1] = Math.Max(last[1], r[1]);
                else merged.Add(new[] { r[0], r[1] });
            }

            const int MaxOutputChars = 6000;
            var sb = new StringBuilder();
            sb.AppendLine("Matches for: " + pattern);
            int blocksEmitted = 0;
            foreach (var m in merged)
            {
                var block = new StringBuilder();
                block.AppendLine("--- lines " + (m[0] + 1) + "–" + (m[1] + 1) + " ---");
                for (int i = m[0]; i <= m[1]; i++)
                    block.AppendLine(lines[i]);

                if (sb.Length + block.Length > MaxOutputChars)
                {
                    int remaining = merged.Count - blocksEmitted;
                    sb.AppendLine("... [" + remaining + " more block(s) truncated — use a more specific pattern]");
                    break;
                }
                sb.Append(block);
                blocksEmitted++;
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Read the current content of the embed point at the given 1-based line number.
        /// Returns the raw code lines inside the embed, or an error/status message.
        /// </summary>
        public string GetEmbedContent(int lineNumber)
        {
            var editor = GetClaGenEditor();
            if (editor == null) return "Error: No embeditor is currently open.";

            var textControl = GetProp(editor, "TextEditorControl");
            if (textControl == null) return "Error: TextEditorControl not found.";

            var document = GetProp(textControl, "Document");
            if (document == null) return "Error: Document not found.";

            var lineManager = GetProp(document, "CustomLineManager");
            if (lineManager == null) return "Error: Document.CustomLineManager not found.";

            var customLines = GetProp(lineManager, "CustomLines") as System.Collections.IEnumerable;
            if (customLines == null) return "Error: CustomLines not found.";

            int targetLine0 = lineNumber - 1;
            object customLine = null;
            foreach (var cl in customLines)
            {
                if (cl == null) continue;
                var startNr = GetProp(cl, "StartLineNr");
                if (startNr != null && (int)startNr == targetLine0)
                {
                    customLine = cl;
                    break;
                }
            }

            if (customLine == null)
                return "Error: No embed point found at line " + lineNumber +
                       ". Use get_embeditor_source to get current line numbers.";

            var pweePart = GetProp(customLine, "PweePart");
            if (pweePart == null) return "Error: Line " + lineNumber + " has no PweePart.";
            if (pweePart.GetType().GetInterface("SoftVelocity.Generator.PWEE.IPweeEmbedPoint") == null)
                return "Error: Line " + lineNumber + " is a read-only generated section, not an embed point.";

            int startLine0 = (int)GetProp(customLine, "StartLineNr");
            int endLine0   = (int)GetProp(customLine, "EndLineNr");

            // Read the embed's line range directly from the document buffer rather than
            // trusting (startLine0 == endLine0) as an "empty" signal: PWEE's CustomLine
            // metadata does not get refreshed when we mutate the document via
            // Document.Insert from WriteEmbedContentByLine, so a freshly written slot
            // still reports start==end even though the buffer now contains text. Whether
            // the embed is empty is decided by the actual buffer contents.
            var getSegMethod  = document.GetType().GetMethod("GetLineSegment", AllInstance);
            var getTextMethod = document.GetType().GetMethod("GetText", AllInstance, null,
                new[] { typeof(int), typeof(int) }, null);
            if (getSegMethod == null || getTextMethod == null)
                return "Error: GetLineSegment/GetText not found.";

            int firstLine = Math.Min(startLine0, endLine0);
            int lastLine  = Math.Max(startLine0, endLine0);

            var sb = new StringBuilder();
            bool hasContent = false;
            for (int i = firstLine; i <= lastLine; i++)
            {
                var seg    = getSegMethod.Invoke(document, new object[] { i });
                int offset = (int)GetProp(seg, "Offset");
                int length = (int)GetProp(seg, "Length");
                string line = (string)getTextMethod.Invoke(document, new object[] { offset, length });
                if (line.Trim().Length > 0) hasContent = true;
                sb.AppendLine(line);
            }

            if (!hasContent) return "(empty embed)";
            return sb.ToString().TrimEnd();
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
        /// DIAGNOSTIC reflection explorer (pure MANAGED reflection only — never reinterprets native pointers;
        /// run on the UI thread). Navigate the IDE object graph starting from the App object by a dot-path
        /// (segments are property/field names or no-arg getter methods; "Name[3]" indexes arrays/collections),
        /// then dump the target's type, properties (with values for simple types), fields, and methods.
        /// Used to discover the in-memory dictionary object model. path="" dumps the App object itself.
        /// </summary>
        public string DumpObjectApi(string path)
        {
            var sb = new StringBuilder();
            try
            {
                object cur = GetAppObject();
                if (cur == null) return "Error: no App object — is an .app open?";
                sb.AppendLine("App = " + cur.GetType().FullName);

                if (!string.IsNullOrWhiteSpace(path))
                {
                    foreach (var rawSeg in path.Split('.'))
                    {
                        string seg = rawSeg.Trim();
                        if (seg.Length == 0) continue;

                        int index = -1;
                        string member = seg;
                        var mi = Regex.Match(seg, @"^(\w*)\[(\d+)\]$");
                        if (mi.Success) { member = mi.Groups[1].Value; index = int.Parse(mi.Groups[2].Value); }

                        if (member.Length > 0)
                        {
                            object next = GetProp(cur, member) ?? InvokeNoArg(cur, member);
                            if (next == null) { sb.AppendLine("-> " + seg + " : <null or not found>"); return sb.ToString(); }
                            cur = next;
                        }
                        if (index >= 0)
                        {
                            cur = ElementAt(cur, index);
                            if (cur == null) { sb.AppendLine("-> [" + index + "] : <null / out of range>"); return sb.ToString(); }
                        }
                        sb.AppendLine("-> " + seg + " : " + cur.GetType().FullName);
                    }
                }

                sb.AppendLine();
                DumpReflectNode(cur, sb);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return sb + "\nError: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        private void DumpReflectNode(object obj, StringBuilder sb)
        {
            if (obj == null) { sb.AppendLine("(null)"); return; }
            var t = obj.GetType();
            sb.AppendLine("TYPE: " + t.FullName);

            if (obj is System.Collections.IEnumerable en && !(obj is string))
            {
                int count = 0; object first = null;
                foreach (var e in en) { if (count == 0) first = e; count++; }
                sb.AppendLine("ENUMERABLE count=" + count +
                              (first != null ? "  elem[0] type=" + first.GetType().FullName : ""));
                if (first != null) { sb.AppendLine("--- element[0] members ---"); DumpReflectMembers(first, sb); }
                return;
            }
            DumpReflectMembers(obj, sb);
        }

        private void DumpReflectMembers(object obj, StringBuilder sb)
        {
            var t = obj.GetType();

            sb.AppendLine("-- Properties --");
            foreach (var p in t.GetProperties(AllInstance))
            {
                if (p.GetIndexParameters().Length > 0) { sb.AppendLine("  [indexer] " + p.PropertyType.Name + " " + p.Name); continue; }
                string val;
                try { val = FormatReflectVal(p.GetValue(obj)); } catch { val = "<err>"; }
                sb.AppendLine("  " + p.PropertyType.Name + " " + p.Name + (val != null ? " = " + val : ""));
            }

            sb.AppendLine("-- Fields --");
            foreach (var fld in t.GetFields(AllInstance))
            {
                string val;
                try { val = FormatReflectVal(fld.GetValue(obj)); } catch { val = "<err>"; }
                sb.AppendLine("  " + fld.FieldType.Name + " " + fld.Name + (val != null ? " = " + val : ""));
            }

            sb.AppendLine("-- Methods --");
            foreach (var m in t.GetMethods(AllInstance))
            {
                if (m.IsSpecialName) continue; // skip property get_/set_ accessors
                var psr = string.Join(", ", Array.ConvertAll(m.GetParameters(),
                    x => x.ParameterType.Name + " " + x.Name));
                sb.AppendLine("  " + m.ReturnType.Name + " " + m.Name + "(" + psr + ")");
            }
        }

        // Inline-print only simple values; complex objects are left for a follow-up path dive (returns null).
        private static string FormatReflectVal(object v)
        {
            if (v == null) return "null";
            var t = v.GetType();
            if (t.IsPrimitive || v is string || t.IsEnum) return "\"" + v + "\"";
            return null;
        }

        private object InvokeNoArg(object obj, string method)
        {
            try
            {
                var m = obj.GetType().GetMethod(method, AllInstance, null, Type.EmptyTypes, null);
                if (m != null && m.ReturnType != typeof(void)) return m.Invoke(obj, null);
            }
            catch { }
            return null;
        }

        private object ElementAt(object enumerable, int index)
        {
            if (enumerable is System.Collections.IEnumerable en)
            {
                int i = 0;
                foreach (var e in en) { if (i == index) return e; i++; }
            }
            return null;
        }

        /// <summary>
        /// Read the LIVE in-memory dictionary (App.FileSchema.DataDictionary.Tables) into plain TableDef
        /// DTOs — Name, Prefix, Fields (with TYPE+size, ScreenPicture, GROUP nesting via DDContainerField /
        /// GetContainedColumns), and Key names. The authoritative, always-current source for the Modern Data
        /// pad's Other Files (no .dcv/.txa schema parsing). MANAGED reflection only — never reinterprets
        /// native pointers. MUST run on the UI thread (live IDE object access). Returns [] if no app/dict.
        /// </summary>
        public List<ClarionAppDataReader.TableDef> ReadLiveDictionaryTables()
        {
            var outp = new List<ClarionAppDataReader.TableDef>();
            try
            {
                var app = GetAppObject();
                if (app == null) return outp;
                var dict = GetProp(GetProp(app, "FileSchema"), "DataDictionary");
                if (dict == null) return outp;
                if (!(GetProp(dict, "Tables") is System.Collections.IEnumerable tables)) return outp;

                foreach (var t in tables)
                {
                    if (t == null) continue;
                    var td = new ClarionAppDataReader.TableDef
                    {
                        Name = (GetProp(t, "Name") ?? "").ToString(),
                        Prefix = (GetProp(t, "Prefix") ?? "").ToString(),
                        Driver = (GetProp(t, "FileDriverName") ?? "").ToString(),
                        DriverOptions = (GetProp(t, "DriverOptions") ?? "").ToString(),
                        Owner = StripBang((GetProp(t, "OwnerName") ?? "").ToString()),
                        FullName = (GetProp(t, "FullPathName") ?? "").ToString(),
                        Description = (GetProp(t, "Description") ?? "").ToString(),
                        Bindable = (GetProp(t, "IsBindable") as bool?) ?? false,
                        Threaded = (GetProp(t, "Threaded") as bool?) ?? false
                    };
                    if (GetProp(t, "Fields") is System.Collections.IEnumerable flds)
                        foreach (var f in flds) td.Fields.Add(ReadLiveField(f));
                    if (GetProp(t, "Keys") is System.Collections.IEnumerable keys)
                        foreach (var k in keys) td.KeyDefs.Add(ReadLiveKey(k));
                    ReadLiveRelations(t, td);
                    outp.Add(td);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AppTree] ReadLiveDictionaryTables: " + ex.Message); }
            return outp;
        }

        // One live DDField → FieldDef. Unprefixed Label (callers prepend the table prefix). GROUP fields
        // (DDContainerField, IsContainer=true) recurse via GetContainedColumns(); children carry own picture.
        private ClarionAppDataReader.FieldDef ReadLiveField(object fobj)
        {
            var f = new ClarionAppDataReader.FieldDef
            {
                Name = (GetProp(fobj, "Label") ?? GetProp(fobj, "Name") ?? "").ToString(),
                Type = LiveFieldType(fobj),
                Picture = EmptyToNull((GetProp(fobj, "ScreenPicture") ?? "").ToString()),
                Prompt = EmptyToNull((GetProp(fobj, "PromptText") ?? "").ToString()),
                Header = EmptyToNull((GetProp(fobj, "ColumnHeading") ?? "").ToString()),
                Description = EmptyToNull((GetProp(fobj, "Description") ?? "").ToString()),
                DerivedFrom = EmptyToNull((GetProp(fobj, "DerivedFromFieldName") ?? "").ToString())
            };
            bool isContainer = (GetProp(fobj, "IsContainer") as bool?) ?? false;
            if (isContainer)
            {
                var kids = InvokeNoArg(fobj, "GetContainedColumns") as System.Collections.IEnumerable
                        ?? (GetProp(fobj, "Fields") as System.Collections.IEnumerable);
                if (kids != null)
                {
                    f.Children = new List<ClarionAppDataReader.FieldDef>();
                    foreach (var c in kids) f.Children.Add(ReadLiveField(c));
                }
            }
            return f;
        }

        // Clarion declaration type: string-family types carry their size (CSTRING(61)); others bare (LONG,
        // GROUP, BLOB, DATE…). FieldSize is the declared size (byte length incl. null for CSTRING).
        private string LiveFieldType(object fobj)
        {
            string dt = (GetProp(fobj, "DataType") ?? "").ToString();
            if (string.IsNullOrEmpty(dt)) return "";
            string dtU = dt.ToUpperInvariant();
            if (dtU == "CSTRING" || dtU == "STRING" || dtU == "PSTRING" || dtU == "USTRING")
            {
                string sz = (GetProp(fobj, "FieldSize") ?? "").ToString();
                if (!string.IsNullOrEmpty(sz) && sz != "0") return dt + "(" + sz + ")";
            }
            return dt;
        }

        // One live DDKey → KeyDef. Name unprefixed (Label); components are the member columns' labels.
        private ClarionAppDataReader.KeyDef ReadLiveKey(object kobj)
        {
            var kd = new ClarionAppDataReader.KeyDef
            {
                Name = (GetProp(kobj, "Label") ?? GetProp(kobj, "Name") ?? "").ToString(),
                Primary = (GetProp(kobj, "AttributePrimary") as bool?) ?? false,
                Unique = (GetProp(kobj, "AttributeUnique") as bool?) ?? false,
                CaseSensitive = (GetProp(kobj, "AttributeCase") as bool?) ?? false,
                Description = EmptyToNull((GetProp(kobj, "Description") ?? "").ToString())
            };
            var kt = GetProp(kobj, "KeyType");
            if (kt != null)
                kd.KeyType = kt.ToString().IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0 ? "INDEX" : "KEY";
            // Components carry full field detail (name/type/picture/description) so the UI can show each
            // on its own readable line — far better than Clarion's underscore-mashed key name. Use the
            // CONCRETE KeyComponents list (ComponentsColumn is an explicit-interface member that plain
            // reflection can't reach); each DDKeyComponent.Field is the real DDField.
            if (GetProp(kobj, "KeyComponents") is System.Collections.IEnumerable comps)
                foreach (var comp in comps)
                {
                    var fld = GetProp(comp, "Field");
                    if (fld != null) kd.Components.Add(ReadLiveField(fld));
                }
            return kd;
        }

        // Read a table's relationships from the live dictionary (DDFile.Relations → DDRelation). A DDRelation
        // links a parent (primary-key) table to a child (foreign-key) table; cardinality is inherently
        // parent(1)→child(MANY). We present each from THIS table's perspective: the row is named by the
        // OTHER table, the type is "1:MANY" when this table is the parent (the "1") else "MANY:1", and the
        // mappings list the column pairings on this side. Member names confirmed via dump_object_api — note
        // SoftVelocity's "Foreing" misspelling on the foreign-key labels. MANAGED reflection only; UI thread.
        private void ReadLiveRelations(object tobj, ClarionAppDataReader.TableDef td)
        {
            try
            {
                if (!(GetProp(tobj, "Relations") is System.Collections.IEnumerable rels)) return;
                string thisName = td.Name ?? "";
                foreach (var r in rels)
                {
                    if (r == null) continue;
                    string parentTable = (GetProp(r, "PrimaryKeyTableLabel") ?? "").ToString();
                    string childTable  = (GetProp(r, "ForeingKeyTableLabel") ?? "").ToString();
                    string primaryKey  = (GetProp(r, "PrimaryKeyLabel") ?? "").ToString();
                    string foreignKey  = (GetProp(r, "ForeingKeyLabel") ?? "").ToString();

                    bool thisIsParent = string.Equals(parentTable, thisName, StringComparison.OrdinalIgnoreCase);
                    string related = thisIsParent ? childTable : parentTable;
                    if (string.IsNullOrEmpty(related))
                        related = string.Equals(childTable, thisName, StringComparison.OrdinalIgnoreCase)
                                  ? parentTable : childTable;

                    var rd = new ClarionAppDataReader.RelationDef
                    {
                        Name = related,
                        Type = thisIsParent ? "1:MANY" : "MANY:1",
                        PrimaryKey = primaryKey,
                        ForeignKey = foreignKey
                    };

                    // Column pairings on THIS table's side. Each DDRelationMapping pairs its own .Field with
                    // the .KeyComponent's .Field (the key column on the other table). Prefixed names disambiguate
                    // which table each field belongs to.
                    var maps = GetProp(r, thisIsParent ? "ParentMappings" : "ChildMappings")
                               as System.Collections.IEnumerable;
                    if (maps != null)
                        foreach (var m in maps)
                        {
                            string fromField = PrefixedFieldName(GetProp(m, "Field"));
                            string toField = PrefixedFieldName(GetProp(GetProp(m, "KeyComponent"), "Field"));
                            if (!string.IsNullOrEmpty(fromField) || !string.IsNullOrEmpty(toField))
                                rd.Mappings.Add(new ClarionAppDataReader.FieldMap { From = fromField, To = toField });
                        }

                    td.Relations.Add(rd);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[AppTree] ReadLiveRelations: " + ex.Message); }
        }

        // A DDField's PREFIXED name ("Add:AddressID"). For relation mappings the two fields live on DIFFERENT
        // tables, so the prefix is what tells them apart — prefer it over the bare Label.
        private string PrefixedFieldName(object fobj)
        {
            if (fobj == null) return "";
            return (GetProp(fobj, "Name") ?? GetProp(fobj, "Label") ?? "").ToString();
        }

        // Dictionary OWNER stores "!Glo:Connection" (leading ! = variable, not literal); strip it for display.
        private static string StripBang(string s) => (s != null && s.StartsWith("!")) ? s.Substring(1) : s;

        private static string EmptyToNull(string s) => string.IsNullOrEmpty(s) ? null : s;

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

        private void SetIsDirty(object editor, bool value)
        {
            // IsDirty lives on AbstractViewContent — walk the inheritance chain to find a writable property
            var t = editor.GetType();
            while (t != null)
            {
                var prop = t.GetProperty("IsDirty", AllInstance);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(editor, value, null);
                    return;
                }
                t = t.BaseType;
            }
        }
    }
}
