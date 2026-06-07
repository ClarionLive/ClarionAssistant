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
    /// SPIKE (ticket 0aa0ec42) - guarded invoke-tests for EDITING and DELETING a Local/Global variable from
    /// our Modern Data pad by driving Clarion's OWN managed flows on the FileSchemaPad / FileSchemaTree.
    /// NO native pointers, NO Win32, NO TXA. Diagnostic only - invoked via the inspect_ide MCP command, run
    /// on a backed-up app with the developer driving the modal/confirm dialog. This mirrors how the ADD path
    /// (ticket 30bb3125) was de-risked before it became Services.FileSchemaVariableInserter.AddVariable.
    ///
    /// Verified chain (decompiled DataDictionaryEditor.dll, SoftVelocity.DataDictionary.{Editor,FileSchemaEditor}):
    ///   EDIT   - select the FIELD node (Tag is a DDField), then tree.ShowCurrentItem(false). The override
    ///            BaseFileSchemaTree.ShowCurrentItem builds a FieldForm with RequestType.ChangeRecord, or
    ///            RequestType.ViewRecord when DDField.Location != 0 (read-only/inherited), and persists on OK.
    ///            Same path the tree takes on a double-click (ProcessMouseDoubleClick -> ShowCurrentItem).
    ///   DELETE - select the FIELD node, then tree.Delete(): it walks SelectedNodes, resolves each node's
    ///            EntityBrowserDetails (GetDetails) and calls DeleteItem(), which honors CanHaveDelete,
    ///            computes Item.RemoveSideEffects(...) and pops ConfirmDeletionForm (or a Yes/No MessageBox)
    ///            before DoRemoveFromParent(Item). Fallback if SelectedNodes is empty: CurrentDetails.DeleteItem()
    ///            (operates on details.Item directly).
    ///
    /// Two STAGED probes plus a read-only resolver:
    ///   probe_fileschema_resolve                       - READ-ONLY: resolve pad/control/tree, dump scope nodes.
    ///   probe_fileschema_edit:&lt;scope&gt;:&lt;FieldName&gt;     - GATED: select the named field, invoke ShowCurrentItem(false).
    ///   probe_fileschema_delete:&lt;scope&gt;:&lt;FieldName&gt;   - GATED: select the named field, invoke tree.Delete().
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
        // READ-ONLY resolver (shared diagnostics)
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
            }
            catch (Exception ex) { AppendException(sb, ex); }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // STAGE A - GATED invoke: edit the named field (pops FieldForm in Change/View mode)
        // arg is the inspect_ide remainder after the first colon, i.e. "&lt;scope&gt;:&lt;FieldName&gt;".
        // ---------------------------------------------------------------------------------------------
        public static string EditField(string arg)
        {
            var sb = new StringBuilder();
            try
            {
                string scope, name;
                ParseScopeName(arg, out scope, out name, sb);
                if (string.IsNullOrEmpty(name))
                {
                    sb.AppendLine("ERROR: no field name. Use probe_fileschema_edit:<scope>:<FieldName>.");
                    return sb.ToString();
                }
                bool wantLocal = IsLocal(scope, sb);

                object tree, scopeNode, fieldNode, ddField;
                if (!ResolveFieldNode(wantLocal, name, sb, out tree, out scopeNode, out fieldNode, out ddField))
                    return sb.ToString();

                // Report the field's edit mode BEFORE invoking: Location != 0 => Clarion opens read-only (ViewRecord).
                object locBefore = GetProp(ddField, "Location");
                string typeBefore = Describe(GetProp(ddField, "Type"));
                bool readOnly = !IsZero(locBefore);
                sb.AppendLine();
                sb.AppendLine("Field BEFORE: Name=\"" + DescribeFieldName(ddField) + "\" Type=" + typeBefore
                    + " Location=" + Describe(locBefore) + "  => expected form mode: "
                    + (readOnly ? "ViewRecord (READ-ONLY, Location!=0)" : "ChangeRecord (editable)"));

                if (!SetSelectedNode(tree, fieldNode, sb)) { sb.AppendLine("Abort: selection failed."); return sb.ToString(); }
                Application.DoEvents();

                // Invoke the same path double-click takes: tree.ShowCurrentItem(indirect:false) on the selected node.
                var show = FindMethod(tree, "ShowCurrentItem", new[] { typeof(bool) });
                if (show == null)
                {
                    sb.AppendLine("ERROR: ShowCurrentItem(bool) not found on " + tree.GetType().FullName + ".");
                    return sb.ToString();
                }
                sb.AppendLine();
                sb.AppendLine(">>> Invoking tree.ShowCurrentItem(false) - Clarion's FieldForm should appear ("
                    + (readOnly ? "ViewRecord/read-only" : "ChangeRecord/editable") + "). Change something and OK, or Cancel.");
                show.Invoke(tree, new object[] { false });

                string typeAfter = Describe(GetProp(ddField, "Type"));
                sb.AppendLine("<<< Returned from the form.");
                sb.AppendLine("Field AFTER:  Name=\"" + DescribeFieldName(ddField) + "\" Type=" + typeAfter
                    + (typeAfter != typeBefore ? "  (TYPE CHANGED)" : "  (unchanged - Cancelled, or no edit made)"));
                sb.AppendLine();
                sb.AppendLine("VERDICT: if the FieldForm appeared above, the edit path is proven for "
                    + (wantLocal ? "Local" : "Global") + " scope.");
            }
            catch (Exception ex) { AppendException(sb, ex); }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // STAGE B - GATED invoke: delete the named field (pops ConfirmDeletionForm / Yes-No, then removes)
        // ---------------------------------------------------------------------------------------------
        public static string DeleteField(string arg)
        {
            var sb = new StringBuilder();
            try
            {
                string scope, name;
                ParseScopeName(arg, out scope, out name, sb);
                if (string.IsNullOrEmpty(name))
                {
                    sb.AppendLine("ERROR: no field name. Use probe_fileschema_delete:<scope>:<FieldName>.");
                    return sb.ToString();
                }
                bool wantLocal = IsLocal(scope, sb);

                object tree, scopeNode, fieldNode, ddField;
                if (!ResolveFieldNode(wantLocal, name, sb, out tree, out scopeNode, out fieldNode, out ddField))
                    return sb.ToString();

                // The scope label node's Tag.List is the FieldList we count before/after to confirm removal.
                object fieldList = GetProp(GetProp(scopeNode, "Tag"), "List");
                int before = CountFields(fieldList);

                if (!SetSelectedNode(tree, fieldNode, sb)) { sb.AppendLine("Abort: selection failed."); return sb.ToString(); }
                Application.DoEvents();

                var details = GetProp(tree, "CurrentDetails");
                object canDelete = GetProp(details, "CanHaveDelete");
                int selCount = CountEnumerable(GetProp(tree, "SelectedNodes"));
                sb.AppendLine();
                sb.AppendLine("Pre-delete: CurrentDetails=" + (details == null ? "null" : details.GetType().Name)
                    + " CanHaveDelete=" + Describe(canDelete)
                    + " SelectedNodes=" + selCount
                    + "  FieldList count BEFORE=" + before);

                if (details != null && (canDelete as bool?) == false)
                {
                    sb.AppendLine("NOTE: CanHaveDelete=false - DeleteItem() will no-op. Reporting only.");
                }

                // Canonical path: tree.Delete() (walks SelectedNodes -> GetDetails(node).DeleteItem()). If selecting
                // the node did not populate SelectedNodes (selCount==0), Delete() would do nothing, so fall back to
                // CurrentDetails.DeleteItem() which operates on details.Item directly.
                if (selCount > 0)
                {
                    var del = FindMethod(tree, "Delete", Type.EmptyTypes);
                    if (del == null) { sb.AppendLine("ERROR: tree.Delete() not found."); return sb.ToString(); }
                    sb.AppendLine(">>> Invoking tree.Delete() - Clarion's delete confirmation should appear. Confirm or cancel.");
                    del.Invoke(tree, null);
                }
                else
                {
                    var di = FindMethod(details, "DeleteItem", Type.EmptyTypes);
                    if (di == null) { sb.AppendLine("ERROR: SelectedNodes empty and CurrentDetails.DeleteItem() not found."); return sb.ToString(); }
                    sb.AppendLine(">>> SelectedNodes was empty; invoking CurrentDetails.DeleteItem() - confirmation should appear. Confirm or cancel.");
                    di.Invoke(details, null);
                }

                int after = CountFields(fieldList);
                sb.AppendLine("<<< Returned from delete flow.");
                sb.AppendLine("FieldList count AFTER=" + after
                    + (after >= 0 && before >= 0 && after < before ? "  FIELD DELETED" : "  (no change - cancelled, or CanHaveDelete=false)"));
                sb.AppendLine();
                sb.AppendLine("VERDICT: if the confirmation appeared and the count dropped, the delete path is proven for "
                    + (wantLocal ? "Local" : "Global") + " scope.");
            }
            catch (Exception ex) { AppendException(sb, ex); }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // Shared resolution: pad -> tree -> scope label node -> named field node (+ its DDField Tag)
        // ---------------------------------------------------------------------------------------------
        private static bool ResolveFieldNode(bool wantLocal, string name, StringBuilder sb,
            out object tree, out object scopeNode, out object fieldNode, out object ddField)
        {
            tree = scopeNode = fieldNode = ddField = null;
            var pad = FindFileSchemaPad(sb);
            if (pad == null) return false;
            tree = FindTree(pad, sb);
            if (tree == null) return false;
            scopeNode = FindScopeNode(tree, wantLocal, sb);
            if (scopeNode == null) return false;
            fieldNode = FindFieldNode(scopeNode, name, sb);
            if (fieldNode == null) return false;
            ddField = GetProp(fieldNode, "Tag");
            if (ddField == null) { sb.AppendLine("ERROR: matched field node has a null Tag."); return false; }
            return true;
        }

        private static object FindFileSchemaPad(StringBuilder sb)
        {
            var workbench = WorkbenchSingleton.Workbench;
            if (workbench == null) { sb.AppendLine("ERROR: Workbench is null."); return null; }
            var pads = GetProp(workbench, "PadContentCollection") as IEnumerable;
            if (pads == null) { sb.AppendLine("ERROR: PadContentCollection not enumerable."); return null; }

            var diag = new StringBuilder();
            foreach (var entry in pads)
            {
                if (entry == null) continue;
                var descType = entry.GetType().FullName ?? "";
                var title = GetProp(entry, "Title")?.ToString() ?? "";
                var content = GetProp(entry, "PadContent");
                var contentType = content?.GetType().FullName ?? "";
                diag.AppendLine("    [" + descType + "] Title=\"" + title + "\" PadContent=" + (content == null ? "(none)" : contentType));

                bool match = PadTypeMarkers.Any(m =>
                    descType.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    contentType.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!match) continue;

                var padObj = (content != null && GetProp(content, "Control") != null) ? content
                           : (GetProp(entry, "Control") != null ? entry : (content ?? entry));
                sb.AppendLine("FileSchema pad matched: " + (padObj == null ? "null" : padObj.GetType().FullName));
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
            var tree = FindControlByTypeMarker(control, TreeTypeMarker);
            if (tree == null)
            {
                sb.AppendLine("ERROR: no child control whose type contains '" + TreeTypeMarker + "'.");
                return null;
            }
            sb.AppendLine("Tree: " + tree.GetType().FullName);
            return tree;
        }

        private static Control FindControlByTypeMarker(Control root, string marker)
        {
            if (root == null) return null;
            for (var t = root.GetType(); t != null && t != typeof(object); t = t.BaseType)
                if ((t.Name ?? "").IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return root;
            foreach (Control child in root.Controls)
            {
                var hit = FindControlByTypeMarker(child, marker);
                if (hit != null) return hit;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------
        // Tree node helpers (Aga TreeViewAdv: Root.Children of TreeNodeAdv, each .Tag)
        // ---------------------------------------------------------------------------------------------
        private static IEnumerable<object> EnumerateNodes(object tree)
        {
            var root = GetProp(tree, "Root");
            if (root == null) yield break;
            foreach (var n in EnumerateDescendants(root)) yield return n;
        }

        private static IEnumerable<object> EnumerateDescendants(object node)
        {
            var stack = new Stack<object>();
            foreach (var c in Children(node)) stack.Push(c);
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
                if (tagType.IndexOf("DataLabelNode", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var list = GetProp(tag, "List");
                sb.AppendLine("  node Tag=" + tagType
                    + "  Label=\"" + GetProp(tag, "Label") + "\""
                    + "  List=" + (list == null ? "null" : list.GetType().Name + " IsLocal=" + GetProp(list, "IsLocal")
                        + " Fields=" + CountFields(list)));
                n++;
            }
            if (n == 0) sb.AppendLine("  (no *DataLabelNode nodes found - is an .app open and the tree populated?)");
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
                    sb.AppendLine("Scope node: Tag=" + tag.GetType().Name + " Label=\"" + GetProp(tag, "Label") + "\"");
                    return node;
                }
            }
            sb.AppendLine("ERROR: no node with Tag '" + marker + "' found"
                + (wantLocal ? " - is a procedure with Local Data shown in the tree?" : " - is Global Data present?"));
            return null;
        }

        // Find the FIELD node (Tag is a DDField whose Name/CodeName matches) under the given scope label node.
        private static object FindFieldNode(object scopeNode, string name, StringBuilder sb)
        {
            var matches = new List<object>();
            var available = new List<string>();
            foreach (var n in EnumerateDescendants(scopeNode))
            {
                var tag = GetProp(n, "Tag");
                if (tag == null) continue;
                if ((tag.GetType().Name ?? "").IndexOf("DDField", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string fn = GetProp(tag, "Name")?.ToString();
                string cn = GetProp(tag, "CodeName")?.ToString();
                if (!string.IsNullOrEmpty(fn)) available.Add(fn);
                if (string.Equals(fn, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cn, name, StringComparison.OrdinalIgnoreCase))
                    matches.Add(n);
            }
            if (matches.Count == 0)
            {
                sb.AppendLine("ERROR: no field named \"" + name + "\" under this scope.");
                sb.AppendLine("       Fields present: " + (available.Count == 0 ? "(none)" : string.Join(", ", available)));
                return null;
            }
            if (matches.Count > 1)
                sb.AppendLine("WARN: " + matches.Count + " fields named \"" + name
                    + "\" (GROUP/QUEUE nesting); using the first. Production will disambiguate by nesting path.");
            var node = matches[0];
            sb.AppendLine("Field node: Tag=" + GetProp(node, "Tag").GetType().Name + " Name=\"" + DescribeFieldName(GetProp(node, "Tag")) + "\"");
            return node;
        }

        private static bool SetSelectedNode(object tree, object node, StringBuilder sb)
        {
            bool ok = false;
            if (TrySetProp(tree, "SelectedNode", node)) { sb.AppendLine("  set SelectedNode OK"); ok = true; }
            else sb.AppendLine("  set SelectedNode FAILED");
            TrySetProp(tree, "CurrentNode", node);
            return ok;
        }

        // ---------------------------------------------------------------------------------------------
        // value helpers
        // ---------------------------------------------------------------------------------------------
        private static int CountFields(object fieldContainer)
        {
            var fields = GetProp(fieldContainer, "Fields") as IEnumerable;
            return CountEnumerable(fields);
        }

        private static int CountEnumerable(object maybeEnumerable)
        {
            var e = maybeEnumerable as IEnumerable;
            if (e == null) return -1;
            int n = 0; foreach (var _ in e) n++; return n;
        }

        private static string DescribeFieldName(object ddField)
        {
            return (GetProp(ddField, "Name") ?? GetProp(ddField, "CodeName") ?? "?").ToString();
        }

        private static string Describe(object v) { return v == null ? "null" : v.ToString(); }

        private static bool IsZero(object v)
        {
            if (v == null) return true;
            string s = v.ToString();
            return string.IsNullOrEmpty(s) || s == "0";
        }

        private static bool IsLocal(string scope, StringBuilder sb)
        {
            bool local = string.Equals((scope ?? "").Trim(), "local", StringComparison.OrdinalIgnoreCase);
            sb.AppendLine("Scope requested: " + (local ? "LOCAL (current procedure)" : "GLOBAL"));
            return local;
        }

        // "<scope>:<name>" -> scope + name (split on the FIRST colon; Clarion identifiers carry no colons).
        private static void ParseScopeName(string arg, out string scope, out string name, StringBuilder sb)
        {
            arg = (arg ?? "").Trim();
            int colon = arg.IndexOf(':');
            if (colon < 0) { scope = arg; name = ""; }
            else { scope = arg.Substring(0, colon).Trim(); name = arg.Substring(colon + 1).Trim(); }
            sb.AppendLine("Probe arg: scope=\"" + scope + "\" name=\"" + name + "\"");
        }

        private static void AppendException(StringBuilder sb, Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message));
            sb.AppendLine(ex.StackTrace);
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
    }
}
