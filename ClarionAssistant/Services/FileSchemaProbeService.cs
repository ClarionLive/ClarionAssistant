using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// SPIKE (ticket 30bb3125) — guarded invoke-test for adding a Local/Global variable from our
    /// Modern Data pad by driving Clarion's OWN managed "Add Column" flow on the FileSchemaPad /
    /// FileSchemaTree. NO native pointers, NO Win32, NO TXA.
    ///
    /// Proven chain (read-only investigation 2026-06-07): the docked "Data / Tables" pad is the managed
    /// SoftVelocity.DataDictionary.FileSchemaEditor.FileSchemaPad. pad.Control = FileSchemaControl, which
    /// hosts a FileSchemaTree (: DataDictionaryTreeView : Aga TreeViewAdv). Each scope node's Tag is a
    /// *DataLabelNode (LocalDataLabelNode / GlobalAppDataLabelNode) carrying its FieldList. Selecting a
    /// node drives tree.CurrentDetails (an EntityBrowserDetails whose AddParent = that node's FieldList).
    /// "Add Column" is a managed ToolStripItem wired to EntityBrowserDetails.AddItemEventHandler, which
    /// does new DDField(AddParent, STRING) + shows the managed FieldForm + persists on OK.
    ///
    /// Three STAGED probes, each reports richly:
    ///   probe_fileschema_resolve            — READ-ONLY: resolve pad/control/tree, dump scope nodes + CurrentDetails/AddParent.
    ///   probe_fileschema_select:global|local — set tree.SelectedNode programmatically, report whether CurrentDetails.AddParent tracked (the one open question).
    ///   probe_fileschema_add:global|local    — GATED MUTATION: select scope, invoke AddItemEventHandler (fires Clarion's FieldForm). Run only on a backed-up app with approval.
    /// </summary>
    public static class FileSchemaProbeService
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // The docked "Data / Tables" pad is SoftVelocity.DataDictionary...FileSchematicPad (note the "tic").
        // "FileSchema" matches both FileSchemaPad and FileSchematicPad; "Schematic" is a belt-and-braces fallback.
        private static readonly string[] PadTypeMarkers = { "FileSchema", "Schematic" };
        private const string TreeTypeMarker = "FileSchemaTree";      // : DataDictionaryTreeView : TreeViewAdv
        private const string LocalNodeMarker = "LocalDataLabelNode";
        private const string GlobalNodeMarker = "GlobalAppDataLabelNode";

        // ---------------------------------------------------------------------------------------------
        // STAGE 1 — resolve (read-only)
        // ---------------------------------------------------------------------------------------------
        public static string Resolve()
        {
            var sb = new StringBuilder();
            try
            {
                var pad = FindFileSchemaPad(sb);
                if (pad == null) return sb.ToString();

                var tree = FindTree(pad, sb);
                if (tree == null) return sb.ToString();

                sb.AppendLine();
                sb.AppendLine("=== SCOPE NODES (Tag types) ===");
                DumpScopeNodes(tree, sb);

                sb.AppendLine();
                sb.AppendLine("=== CURRENT SELECTION ===");
                DumpSelectionState(tree, sb);

                sb.AppendLine();
                sb.AppendLine("=== CONTEXT MENU ===");
                DumpContextMenu(tree, sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message));
                sb.AppendLine(ex.StackTrace);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // STAGE 2 — programmatic select + verify AddParent tracks (mutates selection only)
        // ---------------------------------------------------------------------------------------------
        public static string SelectScope(string scope)
        {
            var sb = new StringBuilder();
            try
            {
                bool wantLocal = IsLocal(scope, sb);
                var pad = FindFileSchemaPad(sb);
                if (pad == null) return sb.ToString();
                var tree = FindTree(pad, sb);
                if (tree == null) return sb.ToString();

                var node = FindScopeNode(tree, wantLocal, sb);
                if (node == null) return sb.ToString();

                sb.AppendLine();
                sb.AppendLine("--- BEFORE programmatic select: CurrentDetails/AddParent ---");
                DumpSelectionState(tree, sb);

                sb.AppendLine();
                sb.AppendLine("--- Setting tree.SelectedNode + CurrentNode programmatically ---");
                bool sel = SetSelectedNode(tree, node, sb);
                if (!sel) { sb.AppendLine("Could not set selection — see above."); return sb.ToString(); }

                // Pump the message queue so any SelectionChanged handler runs (it drives CurrentDetails).
                Application.DoEvents();

                sb.AppendLine();
                sb.AppendLine("--- AFTER programmatic select: CurrentDetails/AddParent ---");
                DumpSelectionState(tree, sb);

                sb.AppendLine();
                var verdict = VerdictAddParentTracks(tree, wantLocal);
                sb.AppendLine("VERDICT: " + verdict);
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message));
                sb.AppendLine(ex.StackTrace);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // STAGE 3 — GATED invoke: fire Clarion's add-variable flow for the scope (mutates the app)
        // ---------------------------------------------------------------------------------------------
        public static string InvokeAdd(string scope)
        {
            var sb = new StringBuilder();
            try
            {
                bool wantLocal = IsLocal(scope, sb);
                var pad = FindFileSchemaPad(sb);
                if (pad == null) return sb.ToString();
                var tree = FindTree(pad, sb);
                if (tree == null) return sb.ToString();

                var node = FindScopeNode(tree, wantLocal, sb);
                if (node == null) return sb.ToString();

                if (!SetSelectedNode(tree, node, sb)) { sb.AppendLine("Abort: selection failed."); return sb.ToString(); }
                Application.DoEvents();

                var details = GetProp(tree, "CurrentDetails");
                if (details == null) { sb.AppendLine("Abort: tree.CurrentDetails is null after select."); return sb.ToString(); }
                var addParent = GetProp(details, "AddParent");
                if (addParent == null) { sb.AppendLine("Abort: CurrentDetails.AddParent is null — refused to fire a blind add."); return sb.ToString(); }

                sb.AppendLine("Pre-invoke: CurrentDetails=" + details.GetType().FullName);
                sb.AppendLine("            AddParent=" + addParent.GetType().FullName + " ToString=\"" + addParent + "\" IsLocal=" + GetProp(addParent, "IsLocal"));

                // Snapshot the FieldList count so we can confirm the new field landed after the form closes.
                int before = CountFields(addParent);
                sb.AppendLine("            AddParent field count BEFORE = " + before);

                // Primary path: invoke EntityBrowserDetails.AddItemEventHandler(sender, EventArgs) — the exact
                // delegate the managed "Add Column" menu item is wired to (per decompile). Deterministic: it
                // reads AddParent from the freshly-resolved CurrentDetails. This pops Clarion's modal FieldForm.
                var handler = FindMethod(details, "AddItemEventHandler", new[] { typeof(object), typeof(EventArgs) });
                if (handler == null)
                {
                    sb.AppendLine("AddItemEventHandler(object,EventArgs) not found on " + details.GetType().FullName + " — trying menu PerformClick fallback.");
                    return sb.ToString() + InvokeViaMenuFallback(tree, addParent, before);
                }

                sb.AppendLine();
                sb.AppendLine(">>> Invoking AddItemEventHandler — Clarion's FieldForm should appear. Fill it and click OK (or Cancel).");
                handler.Invoke(details, new object[] { tree, EventArgs.Empty });

                int after = CountFields(addParent);
                sb.AppendLine("<<< Returned from add flow.");
                sb.AppendLine("            AddParent field count AFTER = " + after + (after > before ? "  ✅ FIELD ADDED" : "  (no change — Cancelled, or persistence deferred)"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message));
                sb.AppendLine(ex.StackTrace);
            }
            return sb.ToString();
        }

        private static string InvokeViaMenuFallback(object tree, object addParent, int before)
        {
            var sb = new StringBuilder();
            var menu = GetProp(tree, "ContextMenuStrip") as ContextMenuStrip;
            if (menu == null) { sb.AppendLine("Fallback failed: tree.ContextMenuStrip is null."); return sb.ToString(); }
            ToolStripItem add = null;
            foreach (ToolStripItem it in menu.Items)
            {
                if (it == null) continue;
                if (string.Equals(it.Name, "addField", StringComparison.OrdinalIgnoreCase)
                    || (it.Text != null && it.Text.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0))
                { add = it; break; }
            }
            if (add == null) { sb.AppendLine("Fallback failed: no 'Add Column' item (Name=addField / Text~Column) in ContextMenuStrip."); return sb.ToString(); }
            sb.AppendLine(">>> PerformClick on menu item \"" + add.Text + "\" (Name=" + add.Name + ") — FieldForm should appear.");
            add.PerformClick();
            int after = CountFields(addParent);
            sb.AppendLine("<<< AddParent field count AFTER = " + after + (after > before ? "  ✅ FIELD ADDED" : "  (no change)"));
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // Resolution helpers
        // ---------------------------------------------------------------------------------------------
        private static object FindFileSchemaPad(StringBuilder sb)
        {
            var workbench = WorkbenchSingleton.Workbench;
            if (workbench == null) { sb.AppendLine("ERROR: Workbench is null."); return null; }
            var pads = GetProp(workbench, "PadContentCollection") as IEnumerable;
            if (pads == null) { sb.AppendLine("ERROR: PadContentCollection not enumerable."); return null; }

            // Entries may be PadDescriptors (lazy wrapper) or the IPadContent itself. Match against the
            // descriptor type, its Title, AND the resolved PadContent type — then return the object that
            // actually exposes .Control (the IPadContent).
            var diag = new StringBuilder();
            foreach (var entry in pads)
            {
                if (entry == null) continue;
                var descType = entry.GetType().FullName ?? "";
                var title = GetProp(entry, "Title")?.ToString() ?? "";
                var content = GetProp(entry, "PadContent");           // null when the entry already IS the content
                var contentType = content?.GetType().FullName ?? "";
                diag.AppendLine("    [" + descType + "] Title=\"" + title + "\" PadContent=" + (content == null ? "(none)" : contentType));

                bool match = PadTypeMarkers.Any(m =>
                    descType.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    contentType.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!match) continue;

                // Prefer the resolved content (has .Control); fall back to the entry itself.
                var padObj = (content != null && GetProp(content, "Control") != null) ? content
                           : (GetProp(entry, "Control") != null ? entry : (content ?? entry));
                sb.AppendLine("FileSchema pad matched.");
                sb.AppendLine("  Descriptor: " + descType + "  Title=\"" + title + "\"");
                sb.AppendLine("  Content:    " + (padObj == null ? "null" : padObj.GetType().FullName)
                    + "  (assembly " + (padObj == null ? "?" : padObj.GetType().Assembly.GetName().Name) + ")");
                return padObj;
            }
            sb.AppendLine("ERROR: No pad matching " + string.Join("/", PadTypeMarkers) + " found in PadContentCollection.");
            sb.AppendLine("       Is the Data/Tables pad open and is an .app loaded? All pads seen:");
            sb.Append(diag);
            return null;
        }

        private static object FindTree(object pad, StringBuilder sb)
        {
            var control = GetProp(pad, "Control") as Control;
            if (control == null) { sb.AppendLine("ERROR: pad.Control is null or not a WinForms Control."); return null; }
            sb.AppendLine("Control: " + control.GetType().FullName);

            var tree = FindControlByTypeMarker(control, TreeTypeMarker);
            if (tree == null)
            {
                sb.AppendLine("ERROR: no child control whose type contains '" + TreeTypeMarker + "'. Dumping control tree:");
                DumpControlTree(control, sb, 1);
                return null;
            }
            sb.AppendLine("Tree:    " + tree.GetType().FullName + "  (assembly " + tree.GetType().Assembly.GetName().Name + ")");
            return tree;
        }

        private static Control FindControlByTypeMarker(Control root, string marker)
        {
            if (root == null) return null;
            // Match the control itself or anything in its type hierarchy name.
            for (var t = root.GetType(); t != null && t != typeof(object); t = t.BaseType)
                if ((t.Name ?? "").IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return root;
            foreach (Control child in root.Controls)
            {
                var hit = FindControlByTypeMarker(child, marker);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void DumpControlTree(Control c, StringBuilder sb, int depth)
        {
            if (c == null || depth > 6) return;
            sb.AppendLine(new string(' ', depth * 2) + c.GetType().FullName + " (Name=" + c.Name + ", Visible=" + c.Visible + ")");
            foreach (Control child in c.Controls) DumpControlTree(child, sb, depth + 1);
        }

        // ---------------------------------------------------------------------------------------------
        // Tree node helpers (Aga.Controls.Tree.TreeViewAdv → Root.Children of TreeNodeAdv, each .Tag)
        // ---------------------------------------------------------------------------------------------
        private static IEnumerable<object> EnumerateNodes(object tree)
        {
            var root = GetProp(tree, "Root");
            if (root == null) yield break;
            var stack = new Stack<object>();
            foreach (var c in Children(root)) stack.Push(c);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n == null) continue;
                yield return n;
                foreach (var c in Children(n)) stack.Push(c);
            }
        }

        private static IEnumerable<object> Children(object node)
        {
            var kids = GetProp(node, "Children") as IEnumerable ?? GetProp(node, "Nodes") as IEnumerable;
            if (kids == null) yield break;
            foreach (var k in kids) yield return k;
        }

        private static void DumpScopeNodes(object tree, StringBuilder sb)
        {
            int n = 0;
            foreach (var node in EnumerateNodes(tree))
            {
                var tag = GetProp(node, "Tag");
                if (tag == null) continue;
                var tagType = tag.GetType().Name;
                // Only surface the data-scope label nodes + their FieldLists to keep output tight.
                if (tagType.IndexOf("DataLabelNode", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var list = GetProp(tag, "List");
                sb.AppendLine("  node Tag=" + tagType
                    + "  Label=\"" + GetProp(tag, "Label") + "\""
                    + "  List=" + (list == null ? "null" : list.GetType().Name + " IsLocal=" + GetProp(list, "IsLocal")));
                n++;
            }
            if (n == 0) sb.AppendLine("  (no *DataLabelNode nodes found — is an .app open and the tree populated?)");
        }

        private static object FindScopeNode(object tree, bool wantLocal, StringBuilder sb)
        {
            string marker = wantLocal ? LocalNodeMarker : GlobalNodeMarker;
            foreach (var node in EnumerateNodes(tree))
            {
                var tag = GetProp(node, "Tag");
                if (tag == null) continue;
                if ((tag.GetType().Name ?? "").IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.AppendLine("Target scope node: Tag=" + tag.GetType().Name + " Label=\"" + GetProp(tag, "Label") + "\"");
                    return node;
                }
            }
            sb.AppendLine("ERROR: no node with Tag '" + marker + "' found"
                + (wantLocal ? " — is a procedure with Local Data shown in the tree?" : " — is Global Data present?"));
            return null;
        }

        private static bool SetSelectedNode(object tree, object node, StringBuilder sb)
        {
            bool ok = false;
            // Aga TreeViewAdv exposes SelectedNode {get;set} + CurrentNode {get;set}.
            if (TrySetProp(tree, "SelectedNode", node)) { sb.AppendLine("  set SelectedNode OK"); ok = true; }
            else sb.AppendLine("  set SelectedNode FAILED");
            if (TrySetProp(tree, "CurrentNode", node)) sb.AppendLine("  set CurrentNode OK");
            // Some builds want the node expanded/visible to realize the details.
            TryInvokeNoArg(node, "ExpandAll");
            return ok;
        }

        private static void DumpSelectionState(object tree, StringBuilder sb)
        {
            var sel = GetProp(tree, "SelectedNode");
            sb.AppendLine("  SelectedNode.Tag = " + DescribeTag(sel));
            var details = GetProp(tree, "CurrentDetails");
            if (details == null) { sb.AppendLine("  CurrentDetails = null"); return; }
            sb.AppendLine("  CurrentDetails = " + details.GetType().FullName);
            var addParent = GetProp(details, "AddParent");
            if (addParent == null) { sb.AppendLine("  CurrentDetails.AddParent = null"); return; }
            sb.AppendLine("  CurrentDetails.AddParent = " + addParent.GetType().Name
                + " ToString=\"" + addParent + "\""
                + " IsLocal=" + GetProp(addParent, "IsLocal")
                + " Fields=" + CountFields(addParent)
                + "  CanHaveAdd=" + GetProp(details, "CanHaveAdd")
                + " AddPosition=" + GetProp(details, "AddPosition"));
        }

        private static string DescribeTag(object node)
        {
            if (node == null) return "(no SelectedNode)";
            var tag = GetProp(node, "Tag");
            if (tag == null) return "(node has null Tag)";
            return tag.GetType().Name + " Label=\"" + GetProp(tag, "Label") + "\"";
        }

        private static void DumpContextMenu(object tree, StringBuilder sb)
        {
            var menu = GetProp(tree, "ContextMenuStrip") as ContextMenuStrip;
            if (menu == null) { sb.AppendLine("  ContextMenuStrip = null"); return; }
            sb.AppendLine("  ContextMenuStrip Items=" + menu.Items.Count);
            foreach (ToolStripItem it in menu.Items)
                sb.AppendLine("    - Name=\"" + (it?.Name) + "\" Text=\"" + (it?.Text) + "\" Type=" + (it?.GetType().Name));
        }

        private static string VerdictAddParentTracks(object tree, bool wantLocal)
        {
            var details = GetProp(tree, "CurrentDetails");
            var addParent = details == null ? null : GetProp(details, "AddParent");
            if (addParent == null)
                return "FAIL — CurrentDetails.AddParent is null after programmatic select. Fallback needed (set AddParent / re-resolve CurrentDetails).";
            var isLocal = GetProp(addParent, "IsLocal");
            bool? il = isLocal as bool?;
            if (il.HasValue && il.Value == wantLocal)
                return "PASS — programmatic select refreshed AddParent to the " + (wantLocal ? "Local" : "Global") + " FieldList (IsLocal=" + il.Value + "). Add invoke is safe.";
            return "PARTIAL — AddParent resolved (" + addParent + ", IsLocal=" + isLocal + ") but IsLocal != requested scope (" + wantLocal + "). Check node match / selection.";
        }

        private static int CountFields(object fieldContainer)
        {
            var fields = GetProp(fieldContainer, "Fields") as IEnumerable;
            if (fields == null) return -1;
            int n = 0; foreach (var _ in fields) n++; return n;
        }

        private static bool IsLocal(string scope, StringBuilder sb)
        {
            bool local = string.Equals((scope ?? "").Trim(), "local", StringComparison.OrdinalIgnoreCase);
            sb.AppendLine("Scope requested: " + (local ? "LOCAL (current procedure)" : "GLOBAL"));
            return local;
        }

        // ---------------------------------------------------------------------------------------------
        // tiny reflection helpers (public + non-public, walk base types)
        // ---------------------------------------------------------------------------------------------
        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var p = t.GetProperty(name, AllInstance | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    try { return p.GetValue(obj, null); } catch { return null; }
                }
            }
            return null;
        }

        private static bool TrySetProp(object obj, string name, object value)
        {
            if (obj == null) return false;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var p = t.GetProperty(name, AllInstance | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite)
                {
                    try { p.SetValue(obj, value, null); return true; } catch { return false; }
                }
            }
            return false;
        }

        private static MethodInfo FindMethod(object obj, string name, Type[] sig)
        {
            if (obj == null) return null;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var m = t.GetMethod(name, AllInstance | BindingFlags.DeclaredOnly, null, sig, null);
                if (m != null) return m;
            }
            return null;
        }

        private static void TryInvokeNoArg(object obj, string method)
        {
            if (obj == null) return;
            try
            {
                var m = obj.GetType().GetMethod(method, AllInstance, null, Type.EmptyTypes, null);
                if (m != null) m.Invoke(obj, null);
            }
            catch { }
        }
    }
}
