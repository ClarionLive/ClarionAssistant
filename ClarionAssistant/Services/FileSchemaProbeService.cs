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
    /// on a backed-up app with the developer driving the modal/confirm dialog.
    ///
    /// IMPORTANT (v2, after CA-Terminal-1-CC live run on v5.0.276): field discovery is done from the DATA MODEL,
    /// not from tree nodes. The Aga TreeViewAdv is a virtual model - the scope label node's child TreeNodeAdvs
    /// don't materialize the field rows - so walking node.Children finds nothing. The authoritative source is
    /// scopeNode.Tag.List.Fields (a FieldList of DDField), which matches the resolve counts (e.g. Global=5,
    /// Local=13). We get the DDField from there and invoke Clarion's flows BY ITEM (no tree node needed):
    ///   EDIT   - tree.ShowCurrentItem(ddField, indirect:true). The override BaseFileSchemaTree.ShowCurrentItem
    ///            builds a FieldForm with RequestType.ChangeRecord (or ViewRecord when DDField.Location != 0)
    ///            and persists on OK. indirect:true skips the passupItemChosen guard so it always opens the form
    ///            (never navigates).
    ///   DELETE - tree.GetDetails(ddField).DeleteItem(). GetDetails(DataDictionaryItem) returns an
    ///            EntityBrowserDetails whose Item == ddField; DeleteItem() honors CanHaveDelete, computes
    ///            Item.RemoveSideEffects(...) and pops ConfirmDeletionForm (or a Yes/No MessageBox) before
    ///            DoRemoveFromParent(Item). No SelectedNodes / tree.Delete() dependency.
    ///
    ///   probe_fileschema_resolve                       - READ-ONLY: resolve pad/tree, dump scope nodes + field names.
    ///   probe_fileschema_edit:&lt;scope&gt;:&lt;FieldName&gt;     - GATED: ShowCurrentItem(ddField, true).
    ///   probe_fileschema_delete:&lt;scope&gt;:&lt;FieldName&gt;   - GATED: GetDetails(ddField).DeleteItem().
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
        // READ-ONLY resolver (shared diagnostics) - now also lists field names per scope.
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
                sb.AppendLine("=== SCOPE NODES + FIELDS ===");
                foreach (var node in EnumerateNodes(tree))
                {
                    var tag = GetProp(node, "Tag");
                    if (tag == null) continue;
                    var tagType = tag.GetType().Name ?? "";
                    if (tagType.IndexOf("DataLabelNode", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var list = GetProp(tag, "List");
                    sb.AppendLine("  " + tagType + "  Label=\"" + GetProp(tag, "Label") + "\""
                        + "  List=" + (list == null ? "null" : list.GetType().Name + " IsLocal=" + GetProp(list, "IsLocal")
                            + " Fields=" + CountFieldsDeep(list)));
                    if (list != null)
                        foreach (var f in EnumerateFields(list))
                            sb.AppendLine("      - " + DescribeField(f));
                }
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
                object tree, ddField;
                if (!ResolveField(arg, "edit", sb, out tree, out ddField)) return sb.ToString();

                object locBefore = GetProp(ddField, "Location");
                string typeBefore = Describe(GetProp(ddField, "Type"));
                bool readOnly = !IsZero(locBefore);
                sb.AppendLine();
                sb.AppendLine("Field BEFORE: " + DescribeField(ddField) + " Location=" + Describe(locBefore)
                    + "  => expected form mode: " + (readOnly ? "ViewRecord (READ-ONLY, Location!=0)" : "ChangeRecord (editable)"));

                // tree.ShowCurrentItem(ddField, indirect:true): 2 params, 2nd is bool, 1st accepts the DDField.
                var show = FindMethodArgs(tree, "ShowCurrentItem",
                    p => p.Length == 2 && p[1].ParameterType == typeof(bool) && p[0].ParameterType.IsInstanceOfType(ddField));
                if (show == null)
                {
                    sb.AppendLine("ERROR: ShowCurrentItem(item, bool) not found on " + tree.GetType().FullName + ".");
                    return sb.ToString();
                }
                sb.AppendLine();
                sb.AppendLine(">>> Invoking tree.ShowCurrentItem(ddField, true) - Clarion's FieldForm should appear ("
                    + (readOnly ? "ViewRecord/read-only" : "ChangeRecord/editable") + "). Change something and OK, or Cancel.");
                show.Invoke(tree, new object[] { ddField, true });

                string typeAfter = Describe(GetProp(ddField, "Type"));
                sb.AppendLine("<<< Returned from the form.");
                sb.AppendLine("Field AFTER:  " + DescribeField(ddField)
                    + (typeAfter != typeBefore ? "  (TYPE CHANGED " + typeBefore + " -> " + typeAfter + ")" : "  (unchanged - Cancelled, or no edit made)"));
                sb.AppendLine();
                sb.AppendLine("VERDICT: if the FieldForm appeared above, the edit path is proven (invoke-by-item, no tree node).");
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
                object tree, ddField;
                if (!ResolveField(arg, "delete", sb, out tree, out ddField)) return sb.ToString();

                // tree.GetDetails(DataDictionaryItem): the 1-arg overload whose param accepts the DDField
                // (the other 1-arg overload takes a TreeNodeAdv, which a DDField is NOT an instance of).
                var getDetails = FindMethodArgs(tree, "GetDetails",
                    p => p.Length == 1 && p[0].ParameterType.IsInstanceOfType(ddField));
                if (getDetails == null) { sb.AppendLine("ERROR: GetDetails(DataDictionaryItem) not found."); return sb.ToString(); }
                var details = getDetails.Invoke(tree, new object[] { ddField });
                if (details == null) { sb.AppendLine("ERROR: GetDetails(ddField) returned null."); return sb.ToString(); }

                object canDelete = GetProp(details, "CanHaveDelete");
                object detailsItem = GetProp(details, "Item");
                bool itemMatches = ReferenceEquals(detailsItem, ddField);
                // Count via the field's own parent container so nested fields are reflected too.
                object parentList = GetProp(ddField, "Parent");
                int before = CountFieldsDeep(parentList);
                sb.AppendLine();
                sb.AppendLine("Pre-delete: details=" + details.GetType().Name
                    + " CanHaveDelete=" + Describe(canDelete)
                    + " details.Item==ddField? " + itemMatches
                    + "  parent field count BEFORE=" + before);

                if (!itemMatches)
                    sb.AppendLine("WARN: details.Item is not the target field - aborting to avoid deleting the wrong item.");
                if (!itemMatches) return sb.ToString();
                if ((canDelete as bool?) == false)
                {
                    sb.AppendLine("NOTE: CanHaveDelete=false - DeleteItem() will no-op. Reporting only.");
                }

                var deleteItem = FindMethodArgs(details, "DeleteItem", p => p.Length == 0);
                if (deleteItem == null) { sb.AppendLine("ERROR: details.DeleteItem() not found."); return sb.ToString(); }
                sb.AppendLine(">>> Invoking details.DeleteItem() - Clarion's delete confirmation should appear. Confirm or cancel.");
                deleteItem.Invoke(details, null);

                int after = CountFieldsDeep(parentList);
                sb.AppendLine("<<< Returned from delete flow.");
                sb.AppendLine("Parent field count AFTER=" + after
                    + (after >= 0 && before >= 0 && after < before ? "  FIELD DELETED" : "  (no change - cancelled, or CanHaveDelete=false)"));
                sb.AppendLine();
                sb.AppendLine("VERDICT: if the confirmation appeared and the count dropped, the delete path is proven (invoke-by-item).");
            }
            catch (Exception ex) { AppendException(sb, ex); }
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // Shared resolution: pad -> tree -> scope label node -> named DDField (from scopeNode.Tag.List.Fields)
        // ---------------------------------------------------------------------------------------------
        private static bool ResolveField(string arg, string verb, StringBuilder sb, out object tree, out object ddField)
        {
            tree = ddField = null;
            string scope, name;
            ParseScopeName(arg, out scope, out name, sb);
            if (string.IsNullOrEmpty(name))
            {
                sb.AppendLine("ERROR: no field name. Use probe_fileschema_" + verb + ":<scope>:<FieldName>.");
                return false;
            }
            bool wantLocal = IsLocal(scope, sb);

            var pad = FindFileSchemaPad(sb);
            if (pad == null) return false;
            tree = FindTree(pad, sb);
            if (tree == null) return false;
            var scopeNode = FindScopeNode(tree, wantLocal, sb);
            if (scopeNode == null) return false;

            // Warm tree.CurrentDetails by selecting the scope node first - same spine the proven Add path uses
            // (FileSchemaVariableInserter.AddVariable). The actual edit/delete invoke is by-item below, but this
            // makes sure the tree's ContentManager / details model is realized for the scope before we invoke.
            TrySetProp(tree, "SelectedNode", scopeNode);
            TrySetProp(tree, "CurrentNode", scopeNode);
            Application.DoEvents();
            var curDetails = GetProp(tree, "CurrentDetails");
            sb.AppendLine("Scope selected. tree.CurrentDetails=" + (curDetails == null ? "null" : curDetails.GetType().Name));

            var list = GetProp(GetProp(scopeNode, "Tag"), "List");
            if (list == null) { sb.AppendLine("ERROR: scope node Tag.List (FieldList) is null."); return false; }

            ddField = FindField(list, name, sb);
            return ddField != null;
        }

        // Find a DDField in the FieldList (recursively into GROUP/QUEUE containers) by Name/CodeName.
        private static object FindField(object fieldList, string name, StringBuilder sb)
        {
            var matches = new List<object>();
            var available = new List<string>();
            foreach (var f in EnumerateFields(fieldList))
            {
                string fn = GetProp(f, "Name")?.ToString();
                string cn = GetProp(f, "CodeName")?.ToString();
                if (!string.IsNullOrEmpty(fn)) available.Add(fn);
                if (string.Equals(fn, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cn, name, StringComparison.OrdinalIgnoreCase))
                    matches.Add(f);
            }
            if (matches.Count == 0)
            {
                sb.AppendLine("ERROR: no field named \"" + name + "\" in this scope.");
                sb.AppendLine("       Fields present: " + (available.Count == 0 ? "(none)" : string.Join(", ", available)));
                return null;
            }
            if (matches.Count > 1)
                sb.AppendLine("WARN: " + matches.Count + " fields named \"" + name
                    + "\" (GROUP/QUEUE nesting); using the first. Production will disambiguate by nesting path.");
            sb.AppendLine("Field resolved: " + DescribeField(matches[0]));
            return matches[0];
        }

        // ---------------------------------------------------------------------------------------------
        // FieldList enumeration (data model - authoritative, unlike the virtual tree nodes)
        // ---------------------------------------------------------------------------------------------
        private static IEnumerable<object> EnumerateFields(object fieldListOrContainer)
        {
            var fields = GetProp(fieldListOrContainer, "Fields") as IEnumerable;
            if (fields == null) yield break;
            foreach (var f in fields)
            {
                if (f == null) continue;
                yield return f;
                // GROUP/QUEUE containers carry nested Fields - recurse so nested vars are found/counted.
                var nested = GetProp(f, "Fields") as IEnumerable;
                if (nested != null)
                    foreach (var c in EnumerateFields(f))
                        yield return c;
            }
        }

        private static int CountFieldsDeep(object fieldListOrContainer)
        {
            if (fieldListOrContainer == null) return -1;
            int n = 0;
            foreach (var _ in EnumerateFields(fieldListOrContainer)) n++;
            return n;
        }

        private static string DescribeField(object ddField)
        {
            string name = (GetProp(ddField, "Name") ?? GetProp(ddField, "CodeName") ?? "?").ToString();
            return "Name=\"" + name + "\" Type=" + Describe(GetProp(ddField, "Type")) + " (" + ddField.GetType().Name + ")";
        }

        // ---------------------------------------------------------------------------------------------
        // pad / tree / scope-node resolution (proven 30bb3125 chain)
        // ---------------------------------------------------------------------------------------------
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
            if (tree == null) { sb.AppendLine("ERROR: no child control whose type contains '" + TreeTypeMarker + "'."); return null; }
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

        // ---------------------------------------------------------------------------------------------
        // value helpers
        // ---------------------------------------------------------------------------------------------
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

        // Find a method by name whose parameter list satisfies 'match' (avoids naming SoftVelocity types).
        private static MethodInfo FindMethodArgs(object obj, string name, Func<ParameterInfo[], bool> match)
        {
            if (obj == null) return null;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(AllInstance | BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                    if (match(m.GetParameters())) return m;
                }
            }
            return null;
        }
    }
}
