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

        /// <param name="scope">"local" (current procedure) or "global".</param>
        public static Result AddVariable(string scope)
        {
            bool wantLocal = string.Equals((scope ?? "").Trim(), "local", StringComparison.OrdinalIgnoreCase);
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

                // Guard: confirm we targeted the requested scope (IsLocal flag on the FieldList).
                var isLocal = GetProp(addParent, "IsLocal") as bool?;
                if (isLocal.HasValue && isLocal.Value != wantLocal)
                    return Result.Fail("Resolved the wrong scope (expected " + scopeName + "). Try again from the correct section.");

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
