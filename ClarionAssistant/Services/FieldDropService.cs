using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClarionAssistant.Services
{
    /// <summary>Where a host-owned field drag was released (so the caller can finish routing).</summary>
    public enum FieldDropTarget { None, Designer, Pad, Monaco, Editor }

    /// <summary>Outcome of <see cref="FieldDropService.DoFieldDrag"/>: the classified target + the screen
    /// release point. DESIGNER (native bound-control create) and MONACO (cursor insert into a CA-embeditor
    /// webview) are handled INSIDE DoFieldDrag. PAD is returned so the Data pad posts the drop back to its page
    /// for the in-pad copy. EDITOR (native Clarion editor / standalone Monaco / any other active editor) is
    /// returned so the caller inserts the plain reference at the active editor's caret (the double-click path).</summary>
    public struct FieldDropResult
    {
        public FieldDropTarget Target;
        public int ScreenX;
        public int ScreenY;
    }

    /// <summary>
    /// Drag a column/var row out of the Modern Data pad (WebView2) and drop it on the right target — a bound
    /// ENTRY+PROMPT on the native Window designer, or just the plain reference (var name / PRE:Field) into ANY
    /// editor. (Ticket ed2ccb84 / 0bada8de.)
    ///
    /// HIT-TEST, NO OLE PAYLOAD. The earlier version ran a native DoDragDrop carrying Clarion's own dictionary
    /// item (DataDictionaryItemDataObject). That made the Window designer accept the drop cleanly — but ANY native
    /// OLE drop target that recognizes the format (the native Clarion editor) also grabbed it and inserted the
    /// WHOLE field, not the reference; and the "belt" that suppressed this only reached Monaco webviews we own, not
    /// the native editor or a standalone Monaco. So we DON'T carry the payload at all. Instead we run the gesture
    /// ourselves: a low-level mouse hook (WH_MOUSE_LL) tracks the cursor until the button is released, THEN we
    /// hit-test the release point against the windows we know and route explicitly:
    ///   • DESIGNER (panel rect)  → resolve the live DDField and call ContainerTemplateCreationSupportImp
    ///                              .CreateIfDDField(surface, field, screenX, screenY) — the same create the
    ///                              native drop runs. No rejected OLE drag, so no orphan DesignerTransaction.
    ///   • MONACO  (CA-embeditor webview rect) → insert the reference at that editor's cursor.
    ///   • PAD     (the Data pad's own webview)  → returned so the page hit-tests the section and copies.
    ///   • EDITOR  (the active editor's control rect) → returned so the caller inserts the reference at its caret.
    ///   • otherwise → no-op (released over nothing we own).
    /// Because nothing rides on the OS clipboard/drag channel, no editor can swallow a full-field payload, and the
    /// insert is uniform across the native Clarion editor, the CA embeditor and a standalone Monaco editor.
    ///
    /// FIELD IDENTITY (designer only): we resolve the REAL, parented DDField (live dictionary column or live
    /// local/global scope field) so the created control's binding survives the designer's reconcile; we forge a
    /// transient field only when the live model isn't loaded.
    ///
    /// UI thread (called from the Data pad's WebMessageReceived turn with the button still down). All-managed,
    /// every step try/caught, never throws into the IDE.
    /// </summary>
    public static class FieldDropService
    {
        // ---- drag descriptor (what's being dragged) ----
        private sealed class DragDesc
        {
            public string Kind;     // "column" | "var"
            public string Name;     // field/var label (USE)
            public string Type;     // raw Clarion type token, e.g. "STRING(30)", "LONG", "DECIMAL(10,2)"
            public string Picture;  // display picture, e.g. "@n10.2" (optional)
            public string Table;    // owning table (column drags only) — resolves the LIVE dictionary DDField
            public string Scope;    // "local" | "global" (var drags) — resolves the LIVE scope FieldList DDField
            public string Procedure;// pad's rendered procedure (wrong-proc fail-closed guard for scope resolve)
        }

        /// <summary>
        /// Run the host-owned field drag to completion. Called on the UI thread from the Data pad's
        /// WebMessageReceived turn, with the left mouse button still down (the page posted this on a small
        /// pointer-move). We track the cursor with a low-level mouse hook until release, then hit-test + route.
        /// <paramref name="text"/> is the plain reference (var name / PRE:Field) used for a Monaco/editor insert.
        /// Returns the classified target + release point — the caller handles the PAD and EDITOR cases.
        /// </summary>
        public static FieldDropResult DoFieldDrag(string kind, string name, string type, string picture,
                                                  string table, string scope, string procedure,
                                                  Control source, Control padWebView, string text)
        {
            var res = new FieldDropResult { Target = FieldDropTarget.None };
            try
            {
                var desc = new DragDesc
                {
                    Kind = string.IsNullOrEmpty(kind) ? "var" : kind,
                    Name = name, Type = type, Picture = picture,
                    Table = table, Scope = scope, Procedure = procedure
                };

                Log("DoFieldDrag ENTER kind=" + desc.Kind + " name='" + desc.Name + "' text='" + text + "'");

                // The active document doesn't change during the drag, so resolve it once and reuse.
                object activeVcAtStart = GetActiveViewContent();

                // Visual feedback (we have no OS drag cursor without OLE): a small click-through, top-most "ghost"
                // showing the reference, following the mouse for the whole drag — works over ANY window/monitor,
                // unlike a cursor override the OS won't let us paint over a foreign window.
                DragGhost ghost = TryCreateGhost(text);

                // Caret-follow: nudge the Monaco caret under the pointer (visual only — the atomic insert at release
                // owns the real landing spot). THROTTLED so the per-move flood of async webview messages doesn't
                // back up and trail the mouse; the ghost itself repositions every move (cheap, local) for smoothness.
                var caretClock = Stopwatch.StartNew();
                long lastCaretMs = -1000;
                Action<POINT> onMove = (p) =>
                {
                    try
                    {
                        if (ghost != null) ghost.MoveTo(p.X, p.Y);
                        if (string.IsNullOrEmpty(text)) return;
                        long now = caretClock.ElapsedMilliseconds;
                        if (now - lastCaretMs < 50) return;          // ~20 caret updates/sec — keeps the async queue from backing up
                        lastCaretMs = now;
                        if (Terminal.ModernEmbeditorViewContent.TryMoveMonacoCaretAt(p.X, p.Y)) return;  // CA embeditor(s)
                        TryMoveCaretViaViewContent(activeVcAtStart, p.X, p.Y);                            // Monaco-default-editor
                    }
                    catch { }
                };

                // Track the cursor ourselves until the button comes up (or a right/middle click cancels).
                POINT pt;
                bool cancelled;
                bool tracked = TrackUntilRelease(onMove, out pt, out cancelled);
                if (ghost != null) { try { ghost.Close(); ghost.Dispose(); } catch { } }   // ghost gone before any insert/route
                if (!tracked) { Log("drag track failed -> abort"); return res; }
                res.ScreenX = pt.X; res.ScreenY = pt.Y;
                if (cancelled) { Log("drag cancelled"); return res; }
                Log("released @ (" + pt.X + "," + pt.Y + ")");

                // ---- classify + route the release point ----

                // 1) DESIGNER — release inside the Window designer's surface → create the bound control natively.
                object view = FindActiveWindowDesignerView();
                var panel = GetProp(view, "Control") as Control;
                bool overDesigner = panel != null && panel.IsHandleCreated
                    && panel.RectangleToScreen(panel.ClientRectangle).Contains(pt.X, pt.Y);
                Log("  designerView=" + (view == null ? "null" : view.GetType().Name) + " overDesigner=" + overDesigner);
                if (overDesigner)
                {
                    res.Target = FieldDropTarget.Designer;
                    CreateOnDesigner(view, desc, pt);
                    return res;
                }

                // 2) MONACO — a CA-embeditor / CA file-mode Monaco webview is under the point → insert at its cursor.
                bool monaco = !string.IsNullOrEmpty(text)
                    && Terminal.ModernEmbeditorViewContent.TryInsertAtMonacoCursor(pt.X, pt.Y, text);
                Log("  tryMonaco=" + monaco);
                if (monaco)
                {
                    res.Target = FieldDropTarget.Monaco;
                    Log("release => MONACO  inserted '" + text + "'");
                    return res;
                }

                // 3) PAD — released back over the Data pad's own webview → caller posts the drop to its page.
                bool overPad = PointOverControl(padWebView, pt.X, pt.Y);
                Log("  overPad=" + overPad);
                if (overPad)
                {
                    res.Target = FieldDropTarget.Pad;
                    Log("release => PAD @ (" + pt.X + "," + pt.Y + ")");
                    return res;
                }

                // 4) EDITOR — released over the active editor's surface (native Clarion editor / standalone Monaco
                //    / any other active editor) → caller inserts the reference at its caret (double-click parity).
                object activeVc = GetActiveViewContent();
                var editor = GetActiveEditorControl(view);
                bool overEditor = !string.IsNullOrEmpty(text) && PointOverControl(editor, pt.X, pt.Y);
                Log("  activeVC=" + (activeVc == null ? "null" : activeVc.GetType().Name)
                    + " editorCtrl=" + (editor == null ? "null" : editor.GetType().Name)
                    + " editorVisible=" + (editor != null && editor.IsHandleCreated && editor.Visible)
                    + " overEditor=" + overEditor);
                if (overEditor)
                {
                    res.Target = FieldDropTarget.Editor;
                    Log("release => EDITOR @ (" + pt.X + "," + pt.Y + ")  text='" + text + "'");
                    // A Monaco-overlay editor (e.g. MonacoClarionEditor, the Monaco-default-editor) keeps Monaco as
                    // the authoritative editable buffer over an inert native shell — inserting the native text area
                    // is invisible. Drive its Monaco overlay first; fall back to the native text area otherwise.
                    if (!TryInsertViaViewContentMonaco(activeVc, text, pt.X, pt.Y))
                        InsertIntoEditor(editor, text);
                    return res;
                }

                Log("release => OTHER (no-op) @ (" + pt.X + "," + pt.Y + ")");
                return res;
            }
            catch (Exception ex) { Log("DoFieldDrag threw: " + Unwrap(ex)); return res; }
        }

        // ───────────────────────────── cursor gesture (WH_MOUSE_LL) ─────────────────────────────
        // We're entered with the left button already down (the page fired on a small pointer-move). Install a
        // low-level mouse hook and pump messages until the button is released; the hook records the release point.
        // A right/middle-button-down cancels. Returns false only if the hook couldn't be installed.
        [ThreadStatic] private static bool _trackDone;
        [ThreadStatic] private static bool _trackCancelled;
        [ThreadStatic] private static POINT _trackPt;
        [ThreadStatic] private static IntPtr _hook;
        private static LowLevelMouseProc _hookProc;   // kept alive for the hook's lifetime

        private static bool TrackUntilRelease(Action<POINT> onMove, out POINT releasePt, out bool cancelled)
        {
            releasePt = new POINT(); cancelled = false;
            _trackDone = false; _trackCancelled = false;
            GetCursorPos(out _trackPt);                       // sensible default if no further move arrives

            _hookProc = HookCallback;
            using (var proc = Process.GetCurrentProcess())
            using (var mod = proc.MainModule)
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(mod.ModuleName), 0);
            if (_hook == IntPtr.Zero) { _hookProc = null; Log("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error()); return false; }

            var prevCursor = Cursor.Current;
            POINT lastFollow = new POINT { X = int.MinValue, Y = int.MinValue };
            try
            {
                try { Cursor.Current = Cursors.Hand; } catch { }   // best-effort feedback over our own windows
                var sw = Stopwatch.StartNew();
                while (!_trackDone && sw.Elapsed.TotalSeconds < 30)  // safety cap so a lost button-up can't wedge
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(8);
                    // Caret-follow: nudge the editor caret to the pointer when it has moved (debounced on position).
                    if (onMove != null)
                    {
                        POINT cur = _trackPt;
                        if (cur.X != lastFollow.X || cur.Y != lastFollow.Y) { lastFollow = cur; onMove(cur); }
                    }
                    // Belt: if the button is already up and we somehow missed the hook event, finish.
                    if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
                    {
                        GetCursorPos(out _trackPt);
                        _trackDone = true;
                    }
                }
            }
            finally
            {
                if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                _hookProc = null;
                try { Cursor.Current = prevCursor; } catch { }
            }

            releasePt = _trackPt;
            cancelled = _trackCancelled;
            return true;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_LBUTTONUP)
                    {
                        var ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        _trackPt = ms.pt; _trackDone = true;
                    }
                    else if (msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                    {
                        _trackCancelled = true; _trackDone = true;
                    }
                    else if (msg == WM_MOUSEMOVE)
                    {
                        var ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        _trackPt = ms.pt;
                    }
                }
            }
            catch (Exception ex) { Log("hook cb threw: " + Unwrap(ex)); }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // ───────────────────────────── designer create ─────────────────────────────
        // Resolve the real (or forged) DDField and run the designer's own creation method at the drop point.
        // Screen coords (CreateIfDDField hit-tests the container internally). No OLE drag means no rejected-drop
        // orphan DesignerTransaction to neutralize — the create stands on its own.
        private static void CreateOnDesigner(object view, DragDesc desc, POINT pt)
        {
            try
            {
                object control = GetProp(view, "WindowDesignerControl");
                object imp = control != null ? GetProp(control, "ContainerTemplateCreationSupportImp") : null;
                if (imp == null) { Notify("The Window designer isn't ready for a field drop. Click into the window design, then try again."); return; }

                MethodInfo cm = FindCreateIfDDField(imp.GetType());
                if (cm == null) { Log("CreateIfDDField not found on " + imp.GetType().FullName); Notify("Couldn't reach the designer's field-create API."); return; }

                // Live, parented field preferred (binding survives reconcile); forge from the descriptor only when
                // the live model isn't loaded (e.g. the pad was fed from a .txa snapshot).
                object field = ResolveLiveField(desc);
                if (field == null)
                {
                    Type ddt = cm.GetParameters()[1].ParameterType;
                    Type ft = GetPropertyType(ddt, "DataType");
                    field = ForgeField(desc, ddt, ft);
                }
                if (field == null)
                {
                    Notify("Couldn't resolve \"" + (desc.Name ?? "") + "\" to a dictionary/scope field, so it can't be dropped on the designer. "
                           + "Open the app's dictionary (or the procedure) so the live field is available, then try again.");
                    return;
                }

                // parent = the design surface Control (CreateIfDDField resolves the real container at x,y itself).
                Control parent = (control as Control) ?? (GetProp(view, "Control") as Control);
                cm.Invoke(imp, new object[] { parent, field, pt.X, pt.Y });
                Log("designer create OK: " + desc.Name + " @ (" + pt.X + "," + pt.Y + ")");
            }
            catch (Exception ex) { Log("CreateOnDesigner threw: " + Unwrap(ex)); Notify("Dropping \"" + (desc.Name ?? "") + "\" on the designer failed: " + Unwrap(ex)); }
        }

        // Caret-follow during the drag: ask a Monaco-overlay view content to move its caret to the pointer. Called
        // by name (no compile-time dependency on the editor view-content type), mirroring TryInsertViaViewContentMonaco.
        private static void TryMoveCaretViaViewContent(object activeVc, int x, int y)
        {
            try
            {
                if (activeVc == null) return;
                var mi = activeVc.GetType().GetMethod("TryMoveCaretToScreenPoint",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
                if (mi != null) mi.Invoke(activeVc, new object[] { x, y });
            }
            catch (Exception ex) { Log("TryMoveCaretViaViewContent threw: " + Unwrap(ex)); }
        }

        // If the active view content is a Monaco-overlay editor (its Monaco surface is the authoritative buffer),
        // insert through THAT overlay so the text both shows and saves. Called by name so FieldDropService keeps no
        // compile-time dependency on the editor view-content type. Returns false when the VC has no such hook.
        private static bool TryInsertViaViewContentMonaco(object activeVc, string text, int screenX, int screenY)
        {
            try
            {
                if (activeVc == null) return false;
                var mi = activeVc.GetType().GetMethod("TryInsertReferenceAtPoint",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(int), typeof(int) }, null);
                if (mi == null) return false;
                object r = mi.Invoke(activeVc, new object[] { text, screenX, screenY });
                bool ok = (r is bool) && (bool)r;
                Log("  monaco-vc TryInsertReferenceAtPoint ok=" + ok);
                return ok;
            }
            catch (Exception ex) { Log("TryInsertViaViewContentMonaco threw: " + Unwrap(ex)); return false; }
        }

        // Insert the reference into the editor under the cursor. We target the hit-tested text area DIRECTLY so the
        // drop lands in the editor the developer released over — including the Monaco-default-editor whose Control
        // is a native ClaTextAreaControl that the pad's _renderCtx resolver doesn't recognize (the resolver only
        // knows the native PWEE embeditor + the CA Modern view, so _renderCtx.Insert was a no-op for it). The
        // generic EditorService.InsertTextAtCaret reflects over Document/Caret, so it works for any ICSharpCode
        // surface. Falls back to the active text area if the hit-tested control isn't itself an editable surface.
        private static void InsertIntoEditor(Control editor, string text)
        {
            try
            {
                var es = new EditorService();
                var r = es.InsertTextAtCaret(editor, text);
                if (r == null || !r.Success)
                {
                    Log("editor insert (hit-tested) failed: " + (r == null ? "null" : r.ErrorMessage) + " -> active-text-area fallback");
                    r = es.InsertTextAtCaret(text);
                }
                Log("editor insert ok=" + (r != null && r.Success) + (r != null && !r.Success ? " err=" + r.ErrorMessage : ""));
                if (r != null && r.Success) { try { es.FocusTextArea(editor); } catch { } }
            }
            catch (Exception ex) { Log("InsertIntoEditor threw: " + Unwrap(ex)); }
        }

        // Resolve the REAL, parented DDField the dropped field refers to — the live dictionary column or the live
        // local/global scope field. Reuses the proven FileSchemaVariableInserter resolvers (UI thread).
        private static object ResolveLiveField(DragDesc desc)
        {
            try
            {
                bool isColumn = string.Equals(desc.Kind, "column", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(desc.Table);
                return isColumn
                    ? FileSchemaVariableInserter.ResolveColumnDDField(desc.Scope, desc.Table, desc.Name, desc.Procedure)
                    : FileSchemaVariableInserter.ResolveScopeDDField(desc.Scope, desc.Name, desc.Procedure);
            }
            catch (Exception ex) { Log("ResolveLiveField threw: " + Unwrap(ex)); return null; }
        }

        // Forge a transient DDField from name/type/picture (fallback path). The 3-arg ctor (parent,FieldType,Guid)
        // is null-parent-safe; we never insert it, only hand it to CreateIfDDField. Type parsed via the shared
        // ClarionDeclarationParser so STRING(30)/DECIMAL(10,2)/LONG/etc. map to FieldType + width.
        private static object ForgeField(DragDesc desc, Type ddt, Type ft)
        {
            if (ddt == null || ft == null) return null;

            ClarionDeclarationParser.ParsedFieldSpec spec = null;
            try { spec = ClarionDeclarationParser.ParseLine((desc.Name ?? "Field") + " " + (desc.Type ?? "STRING"), 1); }
            catch (Exception ex) { Log("forge ParseLine threw: " + Unwrap(ex)); }
            string clarionType = (spec != null && !string.IsNullOrEmpty(spec.ClarionType)) ? spec.ClarionType : "STRING";

            object typeVal;
            try { typeVal = Enum.Parse(ft, clarionType, true); }
            catch { Log("forge: unmapped Clarion type '" + clarionType + "'"); return null; }

            object field = null;
            try
            {
                ConstructorInfo ctor3 = null, ctor2 = null;
                foreach (var c in ddt.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType.IsClass && ps[1].ParameterType == ft && ps[2].ParameterType == typeof(Guid)) ctor3 = c;
                    else if (ps.Length == 2 && ps[0].ParameterType.IsClass && ps[1].ParameterType == ft) ctor2 = c;
                }
                if (ctor3 != null) field = ctor3.Invoke(new object[] { null, typeVal, Guid.NewGuid() });
                else if (ctor2 != null) field = ctor2.Invoke(new object[] { null, typeVal });
            }
            catch (Exception ex) { Log("forge ctor threw: " + Unwrap(ex)); return null; }
            if (field == null) { Log("forge: no usable DDField ctor (parent,FieldType[,Guid])"); return null; }

            TrySet(field, "Label", desc.Name);
            TrySet(field, "DataType", typeVal);
            if (spec != null && spec.Characters.HasValue) TrySetNum(field, "Characters", (int)spec.Characters.Value);
            if (spec != null && spec.Places.HasValue) TrySetNum(field, "Places", (int)spec.Places.Value);
            if (!string.IsNullOrEmpty(desc.Picture)) TrySet(field, "ScreenPicture", desc.Picture);   // best-effort
            return field;
        }

        // ---- locate the live, active window designer view ----
        private static object FindActiveWindowDesignerView()
        {
            object active = GetActiveViewContent();
            if (IsWindowDesignerView(active)) return active;

            // Active view is the app/source primary — scan its secondaries for the design view.
            var secs = GetProp(active, "SecondaryViewContents") as IEnumerable;
            if (secs != null)
                foreach (var sv in secs)
                    if (IsWindowDesignerView(sv)) return sv;
            return null;
        }

        // A CommonClarionDesignerView whose WindowDesignerControl is live (a built WINDOW designer, not the
        // dormant report sibling whose WindowDesignerControl is null).
        private static bool IsWindowDesignerView(object v)
        {
            if (v == null) return false;
            if (v.GetType().Name.IndexOf("DesignerView", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return GetProp(v, "WindowDesignerControl") != null;
        }

        // The Control surface of the ACTIVE editor for the EDITOR hit-test. Skips the designer view (handled
        // separately) so a designer never doubles as an "editor" target.
        private static Control GetActiveEditorControl(object designerView)
        {
            try
            {
                object active = GetActiveViewContent();
                if (active != null && ReferenceEquals(active, designerView)) return null;
                return GetProp(active, "Control") as Control;
            }
            catch (Exception ex) { Log("GetActiveEditorControl threw: " + Unwrap(ex)); return null; }
        }

        private static object GetActiveViewContent()
        {
            try
            {
                var wbType = FindType("ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton");
                var wb = wbType?.GetProperty("Workbench", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                var aww = GetProp(wb, "ActiveWorkbenchWindow");
                return GetProp(aww, "ActiveViewContent");
            }
            catch (Exception ex) { Log("GetActiveViewContent threw: " + Unwrap(ex)); return null; }
        }

        // CreateIfDDField(Control parent, DDField f, int x, int y). Located by name+arity so we can read the
        // DDField type off param[1] for forging — robust to which DataDictionary assembly is loaded.
        private static MethodInfo FindCreateIfDDField(Type impType)
        {
            foreach (var mi in impType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (mi.Name != "CreateIfDDField") continue;
                var ps = mi.GetParameters();
                if (ps.Length == 4
                    && typeof(Control).IsAssignableFrom(ps[0].ParameterType)
                    && ps[1].ParameterType.IsClass
                    && ps[2].ParameterType == typeof(int)
                    && ps[3].ParameterType == typeof(int))
                    return mi;
            }
            return null;
        }

        private static Type GetPropertyType(Type t, string name)
        {
            var p = t?.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p?.PropertyType;
        }

        // ---- reflection helpers (self-contained, mirrors StructureDesignerService) ----
        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try { var t = asm.GetType(fullName, false); if (t != null) return t; } catch { }
            }
            return null;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return (p != null && p.GetIndexParameters().Length == 0) ? p.GetValue(obj, null) : null;
            }
            catch { return null; }
        }

        private static bool TrySet(object obj, string name, object val)
        {
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return false;
                p.SetValue(obj, val, null);
                return true;
            }
            catch (Exception ex) { Log("set " + name + " threw: " + Unwrap(ex)); return false; }
        }

        // DDField widths are uint Characters/ushort Places — coerce the int to the property's actual type.
        private static bool TrySetNum(object obj, string name, int val)
        {
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return false;
                object coerced = Convert.ChangeType(val, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
                p.SetValue(obj, coerced, null);
                return true;
            }
            catch (Exception ex) { Log("setnum " + name + " threw: " + Unwrap(ex)); return false; }
        }

        // True if the screen point is inside the control's client rect (PAD / EDITOR release tests).
        private static bool PointOverControl(Control c, int screenX, int screenY)
        {
            try { return c != null && c.IsHandleCreated && c.Visible && c.RectangleToScreen(c.ClientRectangle).Contains(screenX, screenY); }
            catch { return false; }
        }

        private static string Unwrap(Exception ex)
        {
            var e = (ex is TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
            return e.GetType().Name + ": " + e.Message;
        }

        // User-visible heads-up for the rare case a designer drop can't resolve a field (replaces a silent abort).
        private static void Notify(string message)
        {
            try { MessageBox.Show(message, "Drop field", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception ex) { Log("Notify threw: " + Unwrap(ex)); }
        }

        // In-IDE Debug.WriteLine goes to no discoverable file (SharpDevelop LoggingService gotcha), so also append
        // to our own log so a single drag can be diagnosed. %TEMP%\ca-fielddrop.log.
        private static readonly object _logLock = new object();
        private static string _logPath;
        private static void Log(string s)
        {
            try { Debug.WriteLine("[FieldDrop] " + s); } catch { }
            try
            {
                if (_logPath == null) _logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ca-fielddrop.log");
                lock (_logLock)
                    System.IO.File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
            }
            catch { }
        }

        // ---- drag ghost (follow-the-mouse reference label) ----
        private static DragGhost TryCreateGhost(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return null;
                string label = text;
                int nl = label.IndexOfAny(new[] { '\r', '\n' });
                if (nl >= 0) label = label.Substring(0, nl) + " …";   // multi-select: first line + ellipsis
                if (label.Length > 48) label = label.Substring(0, 47) + "…";
                POINT p; GetCursorPos(out p);
                var g = new DragGhost(label);
                g.Show();              // create the handle first; MoveTo positions via SetWindowPos (needs a handle)
                g.MoveTo(p.X, p.Y);
                return g;
            }
            catch (Exception ex) { Log("ghost create threw: " + Unwrap(ex)); return null; }
        }

        // A borderless, click-through (WS_EX_TRANSPARENT), non-activating, top-most label that trails the cursor
        // during the drag. Click-through + no-activate so it never steals focus or intercepts the gesture/hit-test.
        private sealed class DragGhost : Form
        {
            private const int WS_EX_TRANSPARENT = 0x00000020;
            private const int WS_EX_TOOLWINDOW = 0x00000080;
            private const int WS_EX_TOPMOST = 0x00000008;
            private const int WS_EX_NOACTIVATE = 0x08000000;

            private readonly string _text;
            public DragGhost(string text)
            {
                _text = text ?? "";
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = System.Drawing.Color.FromArgb(255, 252, 222);   // pale tooltip
                var sz = TextRenderer.MeasureText(_text, Font);
                ClientSize = new System.Drawing.Size(sz.Width + 14, sz.Height + 8);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                    return cp;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(170, 170, 130)))
                    e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                TextRenderer.DrawText(e.Graphics, _text, Font,
                    new System.Drawing.Rectangle(6, 3, ClientSize.Width - 8, ClientSize.Height - 6),
                    System.Drawing.Color.FromArgb(40, 40, 40),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            // Offset down-right of the pointer so the label is visible and never under the drop point. We position
            // from Cursor.Position (NOT the low-level hook's physical point): the hook reports SYSTEM-physical
            // pixels, but this WinForms window + SetWindowPos live in the host process's (possibly virtualized,
            // mixed-DPI) coordinate space — feeding physical coords there scaled the X up and threw the ghost far
            // to the right on a scaled secondary monitor. Cursor.Position is read in the SAME space as SetWindowPos,
            // so they agree; it's also always current, so the ghost has no lag.
            public void MoveTo(int screenX, int screenY)
            {
                try
                {
                    if (!IsHandleCreated) return;
                    var cp = Cursor.Position;
                    SetWindowPos(Handle, HWND_TOPMOST, cp.X + 16, cp.Y + 10, 0, 0,
                        SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                catch { }
            }

            private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            private const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        }

        // ---- Win32 ----
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int VK_LBUTTON = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}
