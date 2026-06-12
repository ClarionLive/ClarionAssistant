using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>
    /// Opens the NATIVE Clarion Window/Report structure designer on arbitrary WINDOW/REPORT source and
    /// streams the designer's write-backs to the caller — the engine behind the CA Embeditor's Ctrl+D
    /// (task 0a2ac0cb). The mechanism is the spike-proven recipe (DesignerSpikeCommand, v8 = 5.0.362):
    ///
    ///   1. Write the structure to a scratch .clw and open it via FileService.OpenFile — the IDE's normal
    ///      display-binding pipeline auto-attaches the ClarionDesignerView secondary (works with NO project).
    ///   2. On a DEFERRED UI turn (never the caller's reentrant stack): reset the designer view's stale
    ///      guards/caches, parse via the EDITOR's IStructureDesignerCompatible.ParseStructure (header binds,
    ///      correct window/report context), then call the INSTANCE
    ///      CommonClarionDesignerView.ShowDesigner(rcd, cr, isWindowDesigner, isWindowWindow, IsTemplate).
    ///   3. NEVER call SwitchView afterwards — ShowDesigner presents the Design view itself; a programmatic
    ///      switch (sync or deferred) disposes DesignSurface+IDesignerHost (three spike iterations died there).
    ///   4. The designer merges edits into the scratch EDITOR buffer when its Design view deselects
    ///      (MergeFormChanges). A poll timer turns each merge into an onBufferChanged callback; the
    ///      workbench window's close event ends the session.
    ///
    /// One session at a time (the scratch tab is modal-ish UX anyway). All reflection, every step guarded;
    /// diagnostics to %TEMP%\ca-structure-designer.log.
    /// </summary>
    public static class StructureDesignerService
    {
        private const string CommonDesignerViewSimpleName = "CommonClarionDesignerView";

        private static Session _current;

        private sealed class Session
        {
            public object EditorView;          // scratch CWBinding ClarionEditor (primary view content)
            public Type EditorType;
            public object WorkbenchWin;        // its SdiWorkspaceWindow
            public object DesignerView;        // the attached ClarionDesignerView secondary
            public Timer Poll;
            public string LastText;
            public string TempPath;
            public Action<string> OnChanged;
            public Action<string> OnClosed;    // arg = closing message for the user ("" = clean)
            public object CloseSinkKeepAlive;  // delegate target kept alive for the event subscription
            public bool SawDesignerActive;     // designer view has been the active view at least once
            public bool Ended;
        }

        public static bool IsActive { get { return _current != null && !_current.Ended; } }

        /// <summary>Bring the current scratch designer tab to front (deferred, never on a reentrant stack).</summary>
        public static void ActivateCurrent(Control uiInvoker)
        {
            var s = _current;
            if (s == null || s.Ended || s.WorkbenchWin == null) return;
            Action select = () =>
            {
                try { s.WorkbenchWin.GetType().GetMethod("SelectWindow", Type.EmptyTypes)?.Invoke(s.WorkbenchWin, null); }
                catch { }
            };
            try
            {
                if (uiInvoker != null && uiInvoker.IsHandleCreated) uiInvoker.BeginInvoke(select);
                else select();
            }
            catch { }
        }

        /// <summary>
        /// Open the native designer on <paramref name="structureText"/>. Returns null on success, else a
        /// user-facing error message. MUST be called on a settled UI turn (callers BeginInvoke off the
        /// WebView2 message handler — same rule as the embeditor save round-trip).
        /// <paramref name="onBufferChanged"/> fires (UI thread) with the FULL scratch buffer every time the
        /// designer merges; <paramref name="onClosed"/> fires once when the scratch tab closes (arg = final
        /// buffer, or null if nothing changed since the last onBufferChanged).
        /// </summary>
        public static string Open(string structureText, string label, bool isWindow, Control uiInvoker,
            Action<string> onBufferChanged, Action<string> onClosed)
        {
            if (IsActive) return "A structure designer is already open — close its tab first.";
            var log = new StringBuilder();
            void L(string s) { log.AppendLine(s); }
            L("=== StructureDesignerService.Open — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  label=" + label + " isWindow=" + isWindow + " ===");
            try
            {
                // 1) Scratch .clw. The file name becomes the tab title — use the structure's label.
                string safe = Regex.Replace(string.IsNullOrEmpty(label) ? "CAWindow" : label, @"[^\w]", "_");
                string dir = Path.Combine(Path.GetTempPath(), "CAStructureDesigner");
                Directory.CreateDirectory(dir);
                string clwPath = Path.Combine(dir, safe + ".clw");
                string normalized = structureText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                if (!normalized.EndsWith("\r\n")) normalized += "\r\n";
                File.WriteAllText(clwPath, normalized, Encoding.UTF8);
                L("Scratch .clw: " + clwPath + " (" + normalized.Length + " chars)");

                // 2) Open through the IDE pipeline — attaches the designer secondary.
                object view = OpenFileResolveView(clwPath, L);
                if (view == null) { Flush(log); return "Couldn't open the scratch editor (see " + LogPath() + ")."; }
                object designerView = FindDesignerSecondary(view);
                if (designerView == null) { Flush(log); return "The designer view didn't attach to the scratch editor."; }
                L("Editor: " + view.GetType().FullName + "  Designer: " + designerView.GetType().FullName);

                var session = new Session
                {
                    EditorView = view,
                    EditorType = view.GetType(),
                    WorkbenchWin = GetPropAny(view, "WorkbenchWindow"),
                    DesignerView = designerView,
                    TempPath = clwPath,
                    OnChanged = onBufferChanged,
                    OnClosed = onClosed
                };
                _current = session;

                // 3) Deferred race-free build (the caller's turn is settled, but file-open/attach work from
                //    step 2 may still have queued messages — same deferral the spike proved).
                Control invoker = (GetTextEditorControl(view) as Control) ?? uiInvoker;
                Action build = () => DeferredBuild(session, designerView, isWindow, L, log);
                if (invoker != null && invoker.IsHandleCreated) invoker.BeginInvoke(build);
                else build();
                return null;
            }
            catch (Exception ex)
            {
                L("Open UNHANDLED: " + Unwrap(ex));
                Flush(log);
                _current = null;
                return "Designer open failed: " + Unwrap(ex);
            }
        }

        private static void DeferredBuild(Session session, object designerView, bool isWindow, Action<string> L, StringBuilder log)
        {
            try
            {
                string src = TryGetText(session.EditorView, session.EditorType) ?? "";
                L("--- DeferredBuild ---  buffer=" + src.Length + " chars");

                // Reset stale guards + cached controls so ShowDesigner builds fresh.
                ResetField(designerView, "alreadyShown", false, L);
                ResetField(designerView, "m_wddesignerControl", null, L);
                ResetField(designerView, "m_windowManager", null, L);
                ResetField(designerView, "m_largeDesignAreaPanel", null, L);
                ResetField(designerView, "m_rcd", null, L);
                ResetField(designerView, "m_cr", null, L);
                ResetField(designerView, "m_isWindowWindow", isWindow, L);

                // Parse ladder (spike v6): editor ParseStructure → CommonIDEParser isWin pin → static raw.
                string wantType = isWindow ? "WindowDeclaration" : "ReportDeclaration";
                object crObj;
                object rcd = ParseStructureViaEditor(session.EditorView, src, 1, 10, out crObj, L);
                if (rcd != null && rcd.GetType().Name != wantType)
                {
                    L("rcd is " + rcd.GetType().Name + " (need " + wantType + ") — re-parsing with isWin pin.");
                    object cr2; var rcd2 = ParseStructureViaIdeParser(src, 1, 10, isWindow, out cr2, L);
                    if (rcd2 != null) { rcd = rcd2; crObj = cr2; }
                }
                if (rcd == null)
                {
                    object cr2;
                    rcd = ParseStructureViaIdeParser(src, 1, 10, isWindow, out cr2, L);
                    if (rcd != null) crObj = cr2;
                }
                if (rcd == null)
                {
                    Type common = WalkToType(designerView, CommonDesignerViewSimpleName) ?? designerView.GetType();
                    var stat = common.GetMethod("ParseControlString", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string), typeof(bool) }, null);
                    if (stat != null) rcd = stat.Invoke(null, new object[] { src, !isWindow });
                    L("static ParseControlString fallback -> " + (rcd == null ? "NULL" : rcd.GetType().Name));
                }
                if (rcd == null) { EndSession(session, "The structure couldn't be parsed for the designer.", L, log); return; }
                L("rcd = " + rcd.GetType().FullName);
                CompilerResults cr = crObj as CompilerResults ?? new CompilerResults(new TempFileCollection());

                // The real builder — instance ShowDesigner (5th arg BOOL overload). It PRESENTS the Design
                // view itself; do NOT SwitchView afterwards.
                MethodInfo build = FindMethodByArity(designerView.GetType(), "ShowDesigner", 5);
                if (build == null) { EndSession(session, "Designer build entry not found (IDE version mismatch?).", L, log); return; }
                object ret = build.Invoke(designerView, new object[] { rcd, cr, isWindow, isWindow, false });
                L("instance ShowDesigner -> " + (ret ?? "void"));

                object wdc = GetField(designerView, "m_wddesignerControl");
                bool healthy = wdc != null
                    && !(GetPropAny(wdc, "IsDisposed") is bool b1 && b1)
                    && GetPropAny(wdc, "Parent") != null;
                L("health: wdc=" + (wdc == null ? "null" : wdc.GetType().Name)
                    + " IsDisposed=" + (GetPropAny(wdc, "IsDisposed") ?? "n/a")
                    + " Parent=" + (GetPropAny(wdc, "Parent")?.GetType().Name ?? "null"));
                if (!healthy) { EndSession(session, "The designer surface didn't build (see " + LogPath() + ").", L, log); return; }

                StartWatching(session, L);
                L("Session live.");
                Flush(log);
            }
            catch (Exception ex)
            {
                L("DeferredBuild UNHANDLED: " + Unwrap(ex));
                EndSession(session, "Designer build failed: " + Unwrap(ex), L, log);
            }
        }

        // ---- live sync: poll the scratch buffer for MergeFormChanges write-backs + watch for close ----

        private static void StartWatching(Session session, Action<string> L)
        {
            session.LastText = TryGetText(session.EditorView, session.EditorType) ?? "";

            session.Poll = new Timer { Interval = 400 };
            session.Poll.Tick += (s, e) =>
            {
                if (session.Ended) { session.Poll.Stop(); return; }
                try
                {
                    string cur = TryGetText(session.EditorView, session.EditorType);
                    if (cur == null) return;
                    if (!string.Equals(cur, session.LastText, StringComparison.Ordinal))
                    {
                        session.LastText = cur;
                        Log("[poll] merge detected (" + cur.Length + " chars) -> onBufferChanged");
                        try { session.OnChanged?.Invoke(cur); } catch (Exception ex) { Log("onChanged threw: " + Unwrap(ex)); }
                        // Save in the designer = done designing: the merge just synced to the caller, so
                        // auto-close the scratch tab (the caller's onClosed then restores its own focus).
                        CloseScratch(session);
                        return;
                    }

                    // CANCEL detection: the designer's exit paths (save OR cancel) flip the scratch window
                    // back to its Source view. Save produces a merge (handled above, same tick — the merge
                    // check runs first); a flip with NO merge is a cancel — close the scratch and end the
                    // session so the caller can unlock.
                    object active = GetPropAny(session.WorkbenchWin, "ActiveViewContent");
                    bool designerActive = active != null && ReferenceEquals(active, session.DesignerView);
                    if (designerActive) session.SawDesignerActive = true;
                    else if (session.SawDesignerActive)
                    {
                        Log("[poll] designer view deselected with no merge -> cancel, closing scratch");
                        CloseScratch(session);
                    }
                }
                catch (Exception ex) { Log("[poll] " + Unwrap(ex)); }
            };
            session.Poll.Start();

            // Close detection: the scratch tab's workbench window Closed event (spike event log confirmed
            // CloseEvent/Closed fire on tab close).
            var wb = session.WorkbenchWin;
            if (wb != null)
            {
                var sink = new CloseSink { A = () => OnScratchClosed(session) };
                foreach (string evName in new[] { "Closed", "CloseEvent" })
                {
                    try
                    {
                        var ev = wb.GetType().GetEvent(evName, BindingFlags.Public | BindingFlags.Instance);
                        if (ev == null) continue;
                        var d = Delegate.CreateDelegate(ev.EventHandlerType, sink, "Handle", false, false);
                        if (d == null) continue;
                        ev.AddEventHandler(wb, d);
                        session.CloseSinkKeepAlive = sink;
                        L("close hook: wbWindow." + evName);
                        break;
                    }
                    catch { }
                }
            }
            if (session.CloseSinkKeepAlive == null)
                L("WARNING: no close hook attached — session ends only via next Open/explicit end.");
        }

        private sealed class CloseSink
        {
            public Action A;
            public void Handle(object s, EventArgs e) { try { A?.Invoke(); } catch { } }
        }

        /// <summary>
        /// Close the scratch tab programmatically — CloseWindow(force:true) skips the dirty-save prompt
        /// (the buffer is a temp file; the merge already synced to the caller). Deferred off the poll tick;
        /// the Closed event then runs the normal OnScratchClosed cleanup.
        /// </summary>
        private static void CloseScratch(Session session)
        {
            if (session.Ended) return;
            try { session.Poll?.Stop(); } catch { }
            var wb = session.WorkbenchWin;
            Action close = () =>
            {
                try
                {
                    var m = wb != null ? wb.GetType().GetMethod("CloseWindow", new[] { typeof(bool) }) : null;
                    if (m != null) { m.Invoke(wb, new object[] { true }); Log("CloseWindow(force) invoked"); }
                    else Log("CloseWindow not found — ending session directly");
                }
                catch (Exception ex) { Log("CloseWindow threw: " + Unwrap(ex)); }
                // A FORCED programmatic close does NOT raise the Closed event we hooked (a manual X-close
                // does) — finalize directly. The Ended guard makes a double-fire harmless.
                OnScratchClosed(session);
            };
            var ctl = GetTextEditorControl(session.EditorView) as Control;
            try
            {
                if (ctl != null && ctl.IsHandleCreated) ctl.BeginInvoke(close);
                else close();
            }
            catch { close(); }
        }

        private static void OnScratchClosed(Session session)
        {
            if (session.Ended) return;
            session.Ended = true;
            try { session.Poll?.Stop(); session.Poll?.Dispose(); } catch { }
            Log("scratch tab closed — ending session");

            // Final read: if the buffer changed after the last poll tick, deliver it with the close.
            string final = null;
            try
            {
                string cur = TryGetText(session.EditorView, session.EditorType);
                if (cur != null && !string.Equals(cur, session.LastText, StringComparison.Ordinal)) final = cur;
            }
            catch { }

            try { session.OnClosed?.Invoke(final); } catch (Exception ex) { Log("onClosed threw: " + Unwrap(ex)); }
            TryDeleteTemp(session);
            if (ReferenceEquals(_current, session)) _current = null;
        }

        private static void EndSession(Session session, string error, Action<string> L, StringBuilder log)
        {
            session.Ended = true;
            try { session.Poll?.Stop(); session.Poll?.Dispose(); } catch { }
            L("SESSION END: " + error);
            Flush(log);
            try { session.OnClosed?.Invoke(null); } catch { }
            TryDeleteTemp(session);
            if (ReferenceEquals(_current, session)) _current = null;
            // Surface the error to the user — the open request already returned ok, so this is the channel.
            try { MessageBoxShowSafe(error); } catch { }
        }

        private static void MessageBoxShowSafe(string msg)
        {
            // Non-blocking surface: log only. (A modal here could pump a nested loop inside designer
            // teardown — the exact failure mode the spike spent three iterations killing.)
            Log("USER-ERROR: " + msg);
        }

        private static void TryDeleteTemp(Session session)
        {
            try { if (session.TempPath != null && File.Exists(session.TempPath)) File.Delete(session.TempPath); }
            catch { /* still open in the editor — harmless, %TEMP% */ }
        }

        // ---- parse ladder (spike-proven) ----

        private static object ParseStructureViaEditor(object editorView, string src, int line, int col, out object cr, Action<string> L)
        {
            cr = null;
            try
            {
                var iface = editorView.GetType().GetInterface("IStructureDesignerCompatible");
                var ps = iface != null ? iface.GetMethod("ParseStructure") : null;
                if (ps == null) { L("ParseStructure not found."); return null; }
                var pars = ps.GetParameters();
                if (pars.Length != 6) { L("ParseStructure arity " + pars.Length + " unexpected."); return null; }
                Type ctType = pars[4].ParameterType.IsByRef ? pars[4].ParameterType.GetElementType() : pars[4].ParameterType;
                object ctDefault = (ctType != null && ctType.IsEnum) ? Enum.ToObject(ctType, 0) : null;
                var args = new object[] { "CAScratch.clw", src, line, col, ctDefault, null };
                object rcd = ps.Invoke(editorView, args);
                cr = args[5];
                L("ParseStructure(" + line + "," + col + ") -> " + (rcd == null ? "NULL" : rcd.GetType().Name)
                    + " ClarionType=" + (args[4] ?? "?"));
                return rcd;
            }
            catch (Exception ex) { L("ParseStructure threw: " + Unwrap(ex)); return null; }
        }

        private static object ParseStructureViaIdeParser(string src, int line, int col, bool isWin, out object cr, Action<string> L)
        {
            cr = null;
            try
            {
                Type t = FindTypeBySimpleName("CommonIDEParser");
                if (t == null) { L("CommonIDEParser not found."); return null; }
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "ParseStructure" || m.GetParameters().Length != 8) continue;
                    var ps = m.GetParameters();
                    Type ctType = ps[6].ParameterType.IsByRef ? ps[6].ParameterType.GetElementType() : ps[6].ParameterType;
                    object ctDefault = (ctType != null && ctType.IsEnum) ? Enum.ToObject(ctType, 0) : null;
                    var args = new object[] { "CAScratch.clw", src, line, col, true, isWin, ctDefault, null };
                    object rcd = m.Invoke(null, args);
                    cr = args[7];
                    L("CommonIDEParser.ParseStructure(isWin=" + isWin + ") -> " + (rcd == null ? "NULL" : rcd.GetType().Name));
                    return rcd;
                }
                L("CommonIDEParser.ParseStructure 8-param overload not found.");
                return null;
            }
            catch (Exception ex) { L("CommonIDEParser threw: " + Unwrap(ex)); return null; }
        }

        // ---- IDE plumbing (mirrors the spike) ----

        private static object OpenFileResolveView(string path, Action<string> L)
        {
            try
            {
                var fs = FindType("ICSharpCode.SharpDevelop.FileService");
                var m = fs != null ? fs.GetMethod("OpenFile", new[] { typeof(string) }) : null;
                if (m == null) { L("FileService.OpenFile not found."); return null; }
                object result = m.Invoke(null, new object[] { path });
                L("FileService.OpenFile -> " + (result == null ? "null" : result.GetType().Name));
                if (result == null) return null;
                if (result is IViewContent) return result;
                return GetPropAny(result, "ActiveViewContent") ?? GetPropAny(result, "ViewContent") ?? result;
            }
            catch (Exception ex) { L("OpenFile threw: " + Unwrap(ex)); return null; }
        }

        private static object FindDesignerSecondary(object view)
        {
            var seq = GetPropAny(view, "SecondaryViewContents") as System.Collections.IEnumerable;
            if (seq == null) return null;
            foreach (var sv in seq)
                if (sv != null && sv.GetType().Name.IndexOf("Designer", StringComparison.OrdinalIgnoreCase) >= 0) return sv;
            return null;
        }

        private static object GetTextEditorControl(object view)
        {
            var tec = GetPropAny(view, "TextEditorControl");
            if (tec != null) return tec;
            try
            {
                var iface = view.GetType().GetInterface("ITextEditorControlProvider");
                var p = iface != null ? iface.GetProperty("TextEditorControl") : null;
                if (p != null) return p.GetValue(view, null);
            }
            catch { }
            return null;
        }

        private static string TryGetText(object view, Type type)
        {
            try
            {
                var p = type.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanRead) { var v = p.GetValue(view, null); if (v != null) return v.ToString(); }
            }
            catch { }
            try
            {
                var m = type.GetMethod("GetText", Type.EmptyTypes);
                if (m != null) { var v = m.Invoke(view, null); if (v != null) return v.ToString(); }
            }
            catch { }
            return null;
        }

        // ---- reflection helpers ----

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try { var t = asm.GetType(fullName, false); if (t != null) return t; } catch { }
            }
            return null;
        }

        private static Type FindTypeBySimpleName(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (string.Equals(t.Name, simpleName, StringComparison.Ordinal)) return t;
                }
                catch { }
            }
            return null;
        }

        private static Type WalkToType(object obj, string simpleName)
        {
            for (Type t = obj?.GetType(); t != null; t = t.BaseType)
                if (string.Equals(t.Name, simpleName, StringComparison.Ordinal)) return t;
            return null;
        }

        private static MethodInfo FindMethodByArity(Type t, string name, int arity)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
                foreach (var m in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (m.Name == name && m.GetParameters().Length == arity) return m;
            return null;
        }

        private static FieldInfo FindField(object obj, string name)
        {
            for (Type t = obj?.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static object GetField(object obj, string name)
        {
            try { return FindField(obj, name)?.GetValue(obj); } catch { return null; }
        }

        private static void ResetField(object obj, string name, object val, Action<string> L)
        {
            var f = FindField(obj, name);
            if (f == null) { L("  reset " + name + ": not found"); return; }
            try { f.SetValue(obj, val); }
            catch (Exception ex) { L("  reset " + name + " threw: " + Unwrap(ex)); }
        }

        private static object GetPropAny(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return (p != null && p.GetIndexParameters().Length == 0) ? p.GetValue(obj, null) : null;
            }
            catch { return null; }
        }

        private static string Unwrap(Exception ex)
        {
            var e = (ex is TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
            return e.GetType().Name + ": " + e.Message;
        }

        // ---- logging ----

        private static string LogPath() { return Path.Combine(Path.GetTempPath(), "ca-structure-designer.log"); }

        private static void Log(string s)
        {
            try { File.AppendAllText(LogPath(), "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        private static void Flush(StringBuilder log)
        {
            try { File.AppendAllText(LogPath(), log.ToString() + Environment.NewLine, Encoding.UTF8); } catch { }
            log.Length = 0;
        }
    }
}
