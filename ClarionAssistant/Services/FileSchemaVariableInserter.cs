using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Adds a Local (current procedure) or Global variable to the open .app by driving Clarion's OWN managed
    /// add-variable flow on the docked "Data / Tables" pad (SoftVelocity ...FileSchematicPad → FileSchemaControl
    /// → FileSchemaTree). Proven end-to-end by the ticket-30bb3125 spike: select the scope node so
    /// CurrentDetails.AddParent resolves to that scope's FieldList, then invoke EntityBrowserDetails
    /// .AddItemEventHandler — which creates a DDField in that FieldList and shows Clarion's modal FieldForm.
    /// The user fills + OKs it; Clarion persists it; our Modern Data pad picks it up on its 750ms refresh.
    ///
    /// MANAGED reflection only — no native pointers, no Win32, no TXA. MUST be called on the UI thread (the
    /// FieldForm is shown via ShowDialog, and live IDE object access requires it).
    /// </summary>
    public static class FileSchemaVariableInserter
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // "FileSchema" matches both FileSchemaPad and FileSchematicPad; "Schematic" is a fallback.
        private static readonly string[] PadTypeMarkers = { "FileSchema", "Schematic" };
        private const string TreeTypeMarker = "FileSchemaTree";
        private const string LocalNodeMarker = "LocalDataLabelNode";
        private const string GlobalNodeMarker = "GlobalAppDataLabelNode";

        public sealed class Result
        {
            public bool Ok;
            public string Message;
            public static Result Fail(string m) { return new Result { Ok = false, Message = m }; }
            public static Result Done(string m) { return new Result { Ok = true, Message = m }; }
        }

        /// <param name="scope">"local" (current procedure) or "global". Validated — anything else fails closed.</param>
        /// <param name="expectedProcedure">For LOCAL scope: the procedure the caller (Modern Data pad) is
        /// currently showing. The add is REFUSED unless the native tree's Local Data node belongs to this same
        /// procedure — the docked Clarion Data/Tables tree can be showing a different procedure than our pad,
        /// and a blind add would silently land in the wrong procedure. Ignored for global scope.</param>
        public static Result AddVariable(string scope, string expectedProcedure = null)
        {
            // Fail closed on any unexpected scope — never silently coerce an unknown value to Global.
            string s = (scope ?? "").Trim().ToLowerInvariant();
            if (s != "local" && s != "global")
                return Result.Fail("Unknown variable scope '" + scope + "'.");
            bool wantLocal = s == "local";
            string scopeName = wantLocal ? "Local" : "Global";
            try
            {
                var pad = FindFileSchemaPad();
                if (pad == null)
                    return Result.Fail("Clarion's Data / Tables pad isn't available. Open an application first.");

                var tree = FindTree(pad);
                if (tree == null)
                    return Result.Fail("Couldn't locate the Data / Tables tree.");

                var node = FindScopeNode(tree, wantLocal);
                if (node == null)
                    return Result.Fail(wantLocal
                        ? "No Local Data node found — open or focus a procedure first."
                        : "No Global Data node found in the current application.");

                // FAIL CLOSED on procedure mismatch for LOCAL: the native tree's Local Data node belongs to one
                // procedure, which may differ from the one our pad is showing (native + Modern focus diverge).
                // Adding blindly would land the variable in the WRONG procedure with no warning. We also fail
                // closed when the caller can't tell us which procedure it's showing (empty expectedProcedure) —
                // without that anchor we can't prove the target, so refuse rather than risk a wrong-procedure add.
                if (wantLocal)
                {
                    if (string.IsNullOrEmpty(expectedProcedure))
                        return Result.Fail("No active procedure to add a Local variable to — open or focus a procedure first.");
                    string nodeProc = LocalNodeProcedure(node);
                    if (!string.Equals(nodeProc, expectedProcedure, StringComparison.OrdinalIgnoreCase))
                        return Result.Fail("Clarion's Data pad is showing Local Data for '" + (nodeProc ?? "?")
                            + "', not '" + expectedProcedure + "'. Open/focus that procedure in Clarion, then try again.");
                }

                // Select the scope node so the tree builds CurrentDetails / AddParent for that FieldList.
                TrySetProp(tree, "SelectedNode", node);
                TrySetProp(tree, "CurrentNode", node);
                Application.DoEvents();   // let the tree's SelectionChanged drive CurrentDetails (proven required)

                var details = GetProp(tree, "CurrentDetails");
                if (details == null)
                    return Result.Fail("Couldn't open the " + scopeName + " data editor context.");

                var addParent = GetProp(details, "AddParent");
                if (addParent == null)
                    return Result.Fail("Couldn't target the " + scopeName + " data section (AddParent did not resolve).");

                // Guard: read-only dictionary/app.
                var dd = GetProp(addParent, "DataDictionary");
                if (dd != null && (GetProp(dd, "ReadOnly") as bool?) == true)
                    return Result.Fail("The application/dictionary is read-only.");

                // Guard: confirm we targeted the requested scope (IsLocal flag on the FieldList). For LOCAL we
                // require a definite IsLocal==true (a null/indeterminate flag fails closed rather than risk a
                // wrong-scope add); for GLOBAL we only reject a definite IsLocal==true.
                var isLocal = GetProp(addParent, "IsLocal") as bool?;
                if (wantLocal)
                {
                    if (isLocal != true)
                        return Result.Fail("Couldn't confirm the target is Local data — refused to avoid a wrong-scope add.");
                }
                else if (isLocal == true)
                    return Result.Fail("Resolved Local data when Global was requested.");

                // Fire Clarion's own add flow — pops the modal FieldForm; user fills + OKs (or cancels).
                var handler = FindMethod(details, "AddItemEventHandler", new[] { typeof(object), typeof(EventArgs) });
                if (handler != null)
                {
                    handler.Invoke(details, new object[] { tree, EventArgs.Empty });
                    return Result.Done("Opened the Add Variable form for " + DescribeScope(addParent, scopeName) + ".");
                }

                // Fallback: PerformClick the managed "Add Column" menu item.
                var add = FindAddMenuItem(tree);
                if (add != null)
                {
                    add.PerformClick();
                    return Result.Done("Opened the Add Variable form for " + DescribeScope(addParent, scopeName) + ".");
                }

                return Result.Fail("Couldn't find Clarion's add-variable command (AddItemEventHandler / Add Column).");
            }
            catch (Exception ex)
            {
                return Result.Fail("Add Variable failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        /// <summary>
        /// Edit an existing Local/Global variable: open Clarion's FieldForm for the named field. Clarion picks
        /// the mode itself - ChangeRecord (editable) for app-declared fields (DataStorageLocation.Application=0)
        /// or ViewRecord (read-only) for dictionary/template-derived fields (Location != 0) - so we don't gate it.
        /// Proven by the ticket-0aa0ec42 probe: resolve the DDField from the scope's FieldList (the live model,
        /// NOT the virtual tree nodes) and invoke tree.ShowCurrentItem(ddField, indirect:true). indirect:true skips
        /// the passupItemChosen guard so it always opens the form rather than navigating. Mutation only - the caller
        /// (Modern Data pad) refreshes via ScheduleAddRefresh, exactly like Add. UI thread (FieldForm is modal).
        /// </summary>
        /// <param name="path">Structural path to the field through the scope's FieldList ("/"-delimited:
        /// "Member" for a top-level var, "Group/Member" for a nested one). Resolved unambiguously by descending
        /// one container per segment (sibling names are unique); fails closed on a zero/ambiguous match.</param>
        public static Result EditVariable(string scope, string path, string expectedProcedure = null)
        {
            try
            {
                object tree, ddField; Result error;
                if (!ResolveTargetField(scope, path, expectedProcedure, out tree, out ddField, out error)) return error;

                // tree.ShowCurrentItem(ddField, indirect:true): 2 params, 2nd bool, 1st accepts the DDField.
                var show = FindMethodArgs(tree, "ShowCurrentItem",
                    p => p.Length == 2 && p[1].ParameterType == typeof(bool) && p[0].ParameterType.IsInstanceOfType(ddField));
                if (show == null) return Result.Fail("Couldn't find Clarion's edit-field command.");

                show.Invoke(tree, new object[] { ddField, true });   // modal FieldForm; Clarion persists on OK
                return Result.Done("Opened " + LeafName(path) + " for editing.");
            }
            catch (Exception ex)
            {
                return Result.Fail("Edit Variable failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        /// <summary>
        /// Delete an existing Local/Global variable. Resolves the DDField from the scope's FieldList and invokes
        /// tree.GetDetails(ddField).DeleteItem() - GetDetails(DataDictionaryItem) returns an EntityBrowserDetails
        /// whose Item == the field; DeleteItem() honors CanHaveDelete, computes Item.RemoveSideEffects(...) and pops
        /// Clarion's own ConfirmDeletionForm (or a Yes/No MessageBox) before removing. We ref-check details.Item ==
        /// ddField and refuse on mismatch, so a wrong resolution can never delete the wrong field. Mutation only -
        /// the caller refreshes via ScheduleAddRefresh. UI thread (the confirm dialog is modal).
        /// </summary>
        /// <param name="path">Structural path to the field (see EditVariable) — resolved unambiguously, fail
        /// closed on a zero/ambiguous match so a duplicate label inside a GROUP/QUEUE can't delete the wrong field.</param>
        public static Result DeleteVariable(string scope, string path, string expectedProcedure = null)
        {
            try
            {
                object tree, ddField; Result error;
                if (!ResolveTargetField(scope, path, expectedProcedure, out tree, out ddField, out error)) return error;

                // tree.GetDetails(DataDictionaryItem): the 1-arg overload that accepts the DDField (the other 1-arg
                // overload takes a TreeNodeAdv, which a DDField is NOT an instance of).
                var getDetails = FindMethodArgs(tree, "GetDetails",
                    p => p.Length == 1 && p[0].ParameterType.IsInstanceOfType(ddField));
                if (getDetails == null) return Result.Fail("Couldn't resolve the field editor context for " + LeafName(path) + ".");
                var details = getDetails.Invoke(tree, new object[] { ddField });
                if (details == null) return Result.Fail("Couldn't resolve the field editor context for " + LeafName(path) + ".");

                // HARD guard: only delete when the resolved details actually targets our field.
                if (!ReferenceEquals(GetProp(details, "Item"), ddField))
                    return Result.Fail("Internal mismatch resolving '" + LeafName(path) + "' - delete refused to avoid removing the wrong field.");
                if ((GetProp(details, "CanHaveDelete") as bool?) == false)
                    return Result.Fail("'" + LeafName(path) + "' can't be deleted.");

                var deleteItem = FindMethodArgs(details, "DeleteItem", p => p.Length == 0);
                if (deleteItem == null) return Result.Fail("Couldn't find Clarion's delete-field command.");

                // DeleteItem() returns whether or not the user confirmed Clarion's modal, so the honest signal is
                // "did the field actually leave its parent?". Snapshot the parent, invoke, then check membership.
                object parent = GetProp(ddField, "Parent");
                deleteItem.Invoke(details, null);   // modal confirm dialog; Clarion removes on confirm
                bool removed = parent == null || !ContainsField(parent, ddField);
                return removed ? Result.Done("Deleted " + LeafName(path) + ".")
                               : Result.Done("Delete cancelled for " + LeafName(path) + ".");
            }
            catch (Exception ex)
            {
                return Result.Fail("Delete Variable failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        /// <summary>
        /// Shared resolution for Edit/Delete: scope-validate, resolve pad/tree/scope node, apply the LOCAL
        /// wrong-procedure fail-closed guard (same as AddVariable), warm CurrentDetails by selecting the scope
        /// node, reject a read-only app, then resolve the named DDField from the scope's FieldList (the live model).
        /// </summary>
        private static bool ResolveTargetField(string scope, string path, string expectedProcedure,
            out object tree, out object ddField, out Result error)
        {
            tree = null; ddField = null; error = null;

            string s = (scope ?? "").Trim().ToLowerInvariant();
            if (s != "local" && s != "global") { error = Result.Fail("Unknown variable scope '" + scope + "'."); return false; }
            bool wantLocal = s == "local";
            string scopeName = wantLocal ? "Local" : "Global";
            var segments = SplitPath(path);
            if (segments.Length == 0) { error = Result.Fail("No variable supplied."); return false; }

            var pad = FindFileSchemaPad();
            if (pad == null) { error = Result.Fail("Clarion's Data / Tables pad isn't available. Open an application first."); return false; }
            tree = FindTree(pad);
            if (tree == null) { error = Result.Fail("Couldn't locate the Data / Tables tree."); return false; }

            var node = FindScopeNode(tree, wantLocal);
            if (node == null) { error = Result.Fail(wantLocal
                ? "No Local Data node found - open or focus a procedure first."
                : "No Global Data node found in the current application."); return false; }

            // Same fail-closed LOCAL guard as AddVariable: the native tree's Local Data node may belong to a
            // different procedure than the one our pad is showing - editing/deleting blindly would hit the wrong
            // procedure. Refuse on mismatch, and refuse when the caller can't name the on-screen procedure.
            if (wantLocal)
            {
                if (string.IsNullOrEmpty(expectedProcedure))
                { error = Result.Fail("No active procedure for a Local variable - open or focus a procedure first."); return false; }
                string nodeProc = LocalNodeProcedure(node);
                if (!string.Equals(nodeProc, expectedProcedure, StringComparison.OrdinalIgnoreCase))
                { error = Result.Fail("Clarion's Data pad is showing Local Data for '" + (nodeProc ?? "?")
                    + "', not '" + expectedProcedure + "'. Open/focus that procedure in Clarion, then try again."); return false; }
            }

            // Warm tree.CurrentDetails by selecting the scope node (the proven Add spine).
            TrySetProp(tree, "SelectedNode", node);
            TrySetProp(tree, "CurrentNode", node);
            Application.DoEvents();

            var list = GetProp(GetProp(node, "Tag"), "List");
            if (list == null) { error = Result.Fail("Couldn't read the " + scopeName + " data fields."); return false; }

            // Guard: read-only dictionary/app.
            var dd = GetProp(list, "DataDictionary");
            if (dd != null && (GetProp(dd, "ReadOnly") as bool?) == true)
            { error = Result.Fail("The application/dictionary is read-only."); return false; }

            ddField = FindFieldByPath(list, segments);
            if (ddField == null)
            { error = Result.Fail("Couldn't uniquely resolve '" + path + "' in " + scopeName + " data (not found, or an ambiguous name)."); return false; }
            return true;
        }

        // Resolve a DDField by its STRUCTURAL PATH through the FieldList, descending one container per segment.
        // Names are unique among SIBLINGS within a Clarion structure (the compiler enforces it), so each step is
        // unambiguous; we FAIL CLOSED (return null) on a zero or multiple match at any level rather than guessing —
        // critical for the destructive delete path. A flat name match across nested GROUP/QUEUE members would NOT
        // be safe, because members in *different* containers can share a label.
        private static object FindFieldByPath(object fieldList, string[] segments)
        {
            object container = fieldList;
            for (int i = 0; i < segments.Length; i++)
            {
                var fields = GetProp(container, "Fields") as IEnumerable;
                if (fields == null) return null;
                object match = null; int count = 0;
                foreach (var f in fields)
                {
                    if (f == null) continue;
                    if (NameMatches(f, segments[i])) { match = f; count++; }
                }
                if (count != 1) return null;                  // 0 or ambiguous → fail closed
                if (i == segments.Length - 1) return match;   // last segment = the target field
                container = match;                            // descend into the container
            }
            return null;
        }

        private static bool NameMatches(object field, string name)
        {
            return string.Equals(GetProp(field, "Name")?.ToString(), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetProp(field, "CodeName")?.ToString(), name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsField(object container, object field)
        {
            var fields = GetProp(container, "Fields") as IEnumerable;
            if (fields == null) return false;
            foreach (var f in fields) if (ReferenceEquals(f, field)) return true;
            return false;
        }

        // Split a "/"-delimited structural path ("Group/Member") into trimmed, non-empty segments. Clarion
        // identifiers contain no "/", so the delimiter is unambiguous.
        private static string[] SplitPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new string[0];
            var list = new List<string>();
            foreach (var r in path.Split('/')) { var t = (r ?? "").Trim(); if (t.Length > 0) list.Add(t); }
            return list.ToArray();
        }

        private static string LeafName(string path)
        {
            var segs = SplitPath(path);
            return segs.Length == 0 ? "(variable)" : segs[segs.Length - 1];
        }

        // Find a method by name whose parameter list satisfies 'match' (lets us bind to ShowCurrentItem(item,bool)
        // / GetDetails(item) without referencing the SoftVelocity parameter types at compile time).
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

        private static string DescribeScope(object addParent, string scopeName)
        {
            var label = GetProp(addParent, "CodeName") ?? GetProp(addParent, "Name");
            var s = label?.ToString();
            return string.IsNullOrEmpty(s) ? scopeName + " Data" : s;
        }

        // ---- resolution (mirrors the proven spike chain) -------------------------------------------------
        private static object FindFileSchemaPad()
        {
            var workbench = WorkbenchSingleton.Workbench;
            if (workbench == null) return null;
            var pads = GetProp(workbench, "PadContentCollection") as IEnumerable;
            if (pads == null) return null;

            foreach (var entry in pads)
            {
                if (entry == null) continue;
                var descType = entry.GetType().FullName ?? "";
                var title = GetProp(entry, "Title")?.ToString() ?? "";
                var content = GetProp(entry, "PadContent");
                var contentType = content?.GetType().FullName ?? "";

                bool match = PadTypeMarkers.Any(m =>
                    descType.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    contentType.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!match) continue;

                if (content != null && GetProp(content, "Control") != null) return content;
                if (GetProp(entry, "Control") != null) return entry;
                return content ?? entry;
            }
            return null;
        }

        private static object FindTree(object pad)
        {
            var control = GetProp(pad, "Control") as Control;
            return control == null ? null : FindControlByTypeMarker(control, TreeTypeMarker);
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

        private static object FindScopeNode(object tree, bool wantLocal)
        {
            string marker = wantLocal ? LocalNodeMarker : GlobalNodeMarker;
            var root = GetProp(tree, "Root");
            if (root == null) return null;
            var stack = new Stack<object>();
            foreach (var c in Children(root)) stack.Push(c);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n == null) continue;
                var tag = GetProp(n, "Tag");
                if (tag != null && (tag.GetType().Name ?? "").IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return n;
                foreach (var c in Children(n)) stack.Push(c);
            }
            return null;
        }

        private static IEnumerable Children(object node)
        {
            return (GetProp(node, "Children") as IEnumerable) ?? (GetProp(node, "Nodes") as IEnumerable) ?? new object[0];
        }

        // A Local Data node's Tag carries Label = "Local Data &lt;procedure&gt;" (English; the leading text is a
        // localizable caption). Clarion procedure names are single-token identifiers with no spaces and Clarion
        // appends the name last, so we take the TRAILING whitespace-delimited token as the procedure — robust to
        // a different/localized caption rather than depending on the exact English "Local Data " prefix. The
        // caller compares this to the rendered procedure and fails closed on mismatch, so a parse miss is safe.
        private static string LocalNodeProcedure(object node)
        {
            var tag = GetProp(node, "Tag");
            var label = GetProp(tag, "Label")?.ToString();
            if (string.IsNullOrEmpty(label)) return null;
            var parts = label.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : parts[parts.Length - 1];
        }

        private static ToolStripItem FindAddMenuItem(object tree)
        {
            var menu = GetProp(tree, "ContextMenuStrip") as ContextMenuStrip;
            if (menu == null) return null;
            foreach (ToolStripItem it in menu.Items)
            {
                if (it == null) continue;
                if (string.Equals(it.Name, "addField", StringComparison.OrdinalIgnoreCase)
                    || (it.Text != null && it.Text.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0))
                    return it;
            }
            return null;
        }

        // ---- tiny reflection helpers (public + non-public, walk base types) -----------------------------
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
