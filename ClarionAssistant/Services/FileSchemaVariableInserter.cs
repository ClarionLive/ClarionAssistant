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
            // True when the operation actually mutated the app (so the caller should refresh). Defaults true so
            // Add/Edit keep their proven refresh-on-return behavior; only a no-op (e.g. a cancelled delete) sets
            // it false so the host skips the whole-app .txa export on an operation that changed nothing.
            public bool Committed = true;
            public string Message;
            public static Result Fail(string m) { return new Result { Ok = false, Message = m }; }
            public static Result Done(string m) { return new Result { Ok = true, Message = m }; }
            public static Result NoOp(string m) { return new Result { Ok = true, Committed = false, Message = m }; }
        }

        /// <param name="scope">"local" (current procedure) or "global". Validated — anything else fails closed.</param>
        /// <param name="expectedProcedure">For LOCAL scope: the procedure the caller (Modern Data pad) is
        /// currently showing. The add is REFUSED unless the native tree's Local Data node belongs to this same
        /// procedure — the docked Clarion Data/Tables tree can be showing a different procedure than our pad,
        /// and a blind add would silently land in the wrong procedure. Ignored for global scope.</param>
        public static Result AddVariable(string scope, string expectedProcedure = null)
        {
            string scopeName = ScopeName(scope);
            try
            {
                object tree, details, addParent; Result error;
                if (!ResolveAddTarget(scope, expectedProcedure, out tree, out details, out addParent, out error))
                    return error;

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
        /// Paste/drop one or more Clarion variable DECLARATIONS (text) into Local/Global data, creating the
        /// fields WITHOUT Clarion's modal FieldForm. Parses the text (simple single-line MVP), constructs a
        /// DDField per parsed line, and commits each through Clarion's OWN native paste-one-item flow
        /// (details.PasteItem → item.Copy(AddParent) → AddItem → OnItemAdded) so persistence, uniqueness
        /// auto-rename, and tree refresh are Clarion's job — the same managed spine the proven Add path uses.
        /// Adds every line it CAN; reports the lines it skipped (parse errors / build failures) in the message.
        /// UI thread (live IDE object access; a duplicate-label rename can pop Clarion's own form). Same LOCAL
        /// wrong-procedure fail-closed guard as AddVariable.
        /// </summary>
        // Hard caps on a single paste — untrusted clipboard/drop text runs synchronously on the UI thread and
        // each accepted line is a live IDE mutation, so bound both the parse allocation and the mutation blast
        // radius. Above these we fail CLOSED (reject the whole paste) rather than freeze the IDE or commit a mass
        // of unintended fields. Generous enough for any realistic hand-paste; bulk generation isn't this feature.
        private const int MaxPasteChars = 256 * 1024;
        private const int MaxPasteFields = 200;

        public static Result PasteVariableDefinitions(string scope, string declarationText, string expectedProcedure = null)
        {
            if (string.IsNullOrWhiteSpace(declarationText))
                return Result.Fail("Nothing to paste — the clipboard/drop had no text.");
            if (declarationText.Length > MaxPasteChars)
                return Result.Fail("That paste is too large (" + declarationText.Length + " chars). Paste fewer declarations at once.");

            var specs = ClarionDeclarationParser.Parse(declarationText);
            var good = specs.Where(p => p.Ok).ToList();
            var badParses = specs.Where(p => !p.Ok).ToList();
            if (good.Count == 0)
            {
                if (specs.Count == 0)
                    return Result.Fail("No variable declarations found in the pasted text.");
                return Result.Fail("Couldn't parse any variable. " + badParses[0].Error
                    + (badParses.Count > 1 ? " (+" + (badParses.Count - 1) + " more)" : ""));
            }
            if (good.Count > MaxPasteFields)
                return Result.Fail("Too many variables in one paste (" + good.Count + "). The limit is "
                    + MaxPasteFields + " — paste fewer at once.");

            string scopeName = ScopeName(scope);
            try
            {
                object tree, details, addParent; Result error;
                if (!ResolveAddTarget(scope, expectedProcedure, out tree, out details, out addParent, out error))
                    return error;

                // Resolve the SoftVelocity types we need to construct a field, from the AddParent's own assembly
                // (DataDictionary.dll) — no compile-time reference to SoftVelocity types.
                var asm = addParent.GetType().Assembly;
                var ddFieldType = asm.GetType("SoftVelocity.DataDictionary.DDField");
                if (ddFieldType == null) return Result.Fail("Couldn't locate the Clarion DDField type.");
                var dataTypeProp = ddFieldType.GetProperty("DataType", AllInstance);
                var fieldTypeEnum = dataTypeProp != null ? dataTypeProp.PropertyType : null;
                if (fieldTypeEnum == null || !fieldTypeEnum.IsEnum)
                    return Result.Fail("Couldn't locate the Clarion FieldType enum.");
                var ctor = FindFieldCtor(ddFieldType, fieldTypeEnum, addParent.GetType());
                if (ctor == null) return Result.Fail("Couldn't locate the DDField(parent, type) constructor.");

                int added = 0;
                var failed = new List<string>();
                var renamed = new List<string>();
                object curDetails = details;
                bool first = true;
                foreach (var spec in good)
                {
                    string buildErr;
                    var field = BuildField(ctor, fieldTypeEnum, addParent, spec, out buildErr);
                    if (field == null) { failed.Add(spec.Label + " (" + buildErr + ")"); continue; }

                    string pasteErr;
                    var newItem = PasteOneField(tree, curDetails, field, first, out pasteErr);
                    if (newItem == null) { failed.Add(spec.Label + " (" + pasteErr + ")"); continue; }
                    // Only count it added if it's actually attached to the target FieldList — PasteItem returning
                    // a non-null object isn't proof of commit. (Persistence ACROSS a save is still the live test.)
                    if (!VerifyAttached(addParent, newItem)) { failed.Add(spec.Label + " (not attached after paste)"); continue; }

                    added++;
                    first = false;
                    // Clarion's AddItem auto-renames a duplicate label (appends _Copy). Surface that so "Added N"
                    // doesn't silently imply exact-name fidelity when a clash forced a rename.
                    var finalLabel = (GetProp(newItem, "Label") ?? GetProp(newItem, "Name"));
                    var finalStr = finalLabel == null ? null : finalLabel.ToString();
                    if (!string.IsNullOrEmpty(finalStr) && !string.Equals(finalStr, spec.Label, StringComparison.OrdinalIgnoreCase))
                        renamed.Add(spec.Label + " → " + finalStr);
                    // Re-resolve details against the just-added field so the NEXT paste lands After it — preserves
                    // the developer's declaration order (mirrors Clarion's own PasteItems loop, minus its sort).
                    var nd = ReResolveDetails(tree, newItem);
                    if (nd != null) curDetails = nd;
                }

                if (added == 0)
                    return Result.Fail("Couldn't add any variable. " + string.Join("; ", failed));

                string msg = "Added " + added + " variable" + (added == 1 ? "" : "s") + " to " + scopeName + " data.";
                if (renamed.Count > 0)
                    msg += " (" + renamed.Count + " renamed to avoid a name clash: " + string.Join(", ", renamed) + ")";
                var skips = new List<string>();
                foreach (var b in badParses) skips.Add("L" + b.LineNumber + ": " + b.Error);
                skips.AddRange(failed);
                if (skips.Count > 0)
                    msg += " Skipped " + skips.Count + ": " + string.Join("; ", skips);
                return Result.Done(msg);
            }
            catch (Exception ex)
            {
                return Result.Fail("Paste variables failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        /// <summary>
        /// Resolve the managed add target for a scope: pad → tree → scope node (Local/Global), apply the LOCAL
        /// wrong-procedure fail-closed guard, select the node so the tree builds CurrentDetails/AddParent, then
        /// reject a read-only app and confirm the resolved FieldList's IsLocal flag matches the requested scope.
        /// Shared by AddVariable (native form) and PasteVariableDefinitions (form-less). On success returns the
        /// tree, the EntityBrowserDetails (CurrentDetails), and the AddParent FieldList; on failure sets error.
        /// </summary>
        private static bool ResolveAddTarget(string scope, string expectedProcedure,
            out object tree, out object details, out object addParent, out Result error)
        {
            tree = null; details = null; addParent = null; error = null;

            string s = (scope ?? "").Trim().ToLowerInvariant();
            if (s != "local" && s != "global") { error = Result.Fail("Unknown variable scope '" + scope + "'."); return false; }
            bool wantLocal = s == "local";
            string scopeName = wantLocal ? "Local" : "Global";

            var pad = FindFileSchemaPad();
            if (pad == null) { error = Result.Fail("Clarion's Data / Tables pad isn't available. Open an application first."); return false; }

            tree = FindTree(pad);
            if (tree == null) { error = Result.Fail("Couldn't locate the Data / Tables tree."); return false; }

            var node = FindScopeNode(tree, wantLocal);

            // Repopulate fallback: the docked Data pad is ACTIVE-DOCUMENT-driven (it rebuilds on
            // ActiveWorkbenchWindowChanged from the active doc's IFileSchemaProvider). When a CA/Modern
            // (WebView) embeditor is the active document it isn't a provider, so the docked tree empties —
            // Local paste/add then can't find the scope node even though the developer is editing that
            // procedure. Re-push the app's SHARED FileSchema (still pinned to the procedure that was the
            // app-tree selection when the embeditor opened) via FileSchemaPad.SetSchema(fs, null, false): a
            // SYNCHRONOUS in-place tree rebuild with NO active-document change, NO native re-pull, and NO
            // SelectProcedure. It persists exactly like the native path (same FileSchema instance the
            // generator flushes on save). A procedure that isn't the current shared selection still fails
            // closed via the guard below (multi-embeditor support is a separate phase). UI-thread only.
            if (ScopeNeedsRepopulate(node, wantLocal, expectedProcedure)
                && TryRepopulatePadFromAppSchema(pad, wantLocal, expectedProcedure))
            {
                tree = FindTree(pad) ?? tree;
                node = FindScopeNode(tree, wantLocal);
            }

            if (node == null) { error = Result.Fail(wantLocal
                ? "No Local Data node found — open or focus a procedure first."
                : "No Global Data node found in the current application."); return false; }

            // FAIL CLOSED on procedure mismatch for LOCAL (native tree's Local node may belong to a different
            // procedure than the one our pad shows). Also refuse when the caller can't name its on-screen proc.
            if (wantLocal)
            {
                if (string.IsNullOrEmpty(expectedProcedure))
                { error = Result.Fail("No active procedure to add a Local variable to — open or focus a procedure first."); return false; }
                string nodeProc = LocalNodeProcedure(node);
                if (!string.Equals(nodeProc, expectedProcedure, StringComparison.OrdinalIgnoreCase))
                { error = Result.Fail("Clarion's Data pad is showing Local Data for '" + (nodeProc ?? "?")
                    + "', not '" + expectedProcedure + "'. Open/focus that procedure in Clarion, then try again."); return false; }
            }

            // Select the scope node so the tree builds CurrentDetails / AddParent for that FieldList.
            TrySetProp(tree, "SelectedNode", node);
            TrySetProp(tree, "CurrentNode", node);
            Application.DoEvents();   // let the tree's SelectionChanged drive CurrentDetails (proven required)

            details = GetProp(tree, "CurrentDetails");
            if (details == null) { error = Result.Fail("Couldn't open the " + scopeName + " data editor context."); return false; }

            addParent = GetProp(details, "AddParent");
            if (addParent == null) { error = Result.Fail("Couldn't target the " + scopeName + " data section (AddParent did not resolve)."); return false; }

            // Guard: read-only dictionary/app.
            var dd = GetProp(addParent, "DataDictionary");
            if (dd != null && (GetProp(dd, "ReadOnly") as bool?) == true)
            { error = Result.Fail("The application/dictionary is read-only."); return false; }

            // Guard: confirm we targeted the requested scope (IsLocal flag on the FieldList). For LOCAL require a
            // definite IsLocal==true (null/indeterminate fails closed); for GLOBAL reject a definite IsLocal==true.
            var isLocal = GetProp(addParent, "IsLocal") as bool?;
            if (wantLocal)
            {
                if (isLocal != true) { error = Result.Fail("Couldn't confirm the target is Local data — refused to avoid a wrong-scope add."); return false; }
            }
            else if (isLocal == true) { error = Result.Fail("Resolved Local data when Global was requested."); return false; }

            return true;
        }

        // Find the DDField(parent, FieldType) constructor: 2 params, 2nd the FieldType enum, 1st a base type the
        // AddParent FieldList is assignable to (DataDictionaryItem).
        private static ConstructorInfo FindFieldCtor(Type ddFieldType, Type fieldTypeEnum, Type parentType)
        {
            foreach (var c in ddFieldType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var ps = c.GetParameters();
                if (ps.Length == 2 && ps[1].ParameterType == fieldTypeEnum && ps[0].ParameterType.IsAssignableFrom(parentType))
                    return c;
            }
            return null;
        }

        // Construct a DDField from a parsed spec and set its declarable properties. Returns null + reason on a
        // bad type token. Sizing maps: sized strings → Characters; DECIMAL/PDECIMAL → Characters(+Places);
        // fixed types carry no size; DIM → Dimension1; NAME('x') → ExternalName. The field is NOT yet inserted.
        private static object BuildField(ConstructorInfo ctor, Type fieldTypeEnum, object addParent,
            ClarionDeclarationParser.ParsedFieldSpec spec, out string error)
        {
            error = null;
            object typeVal;
            try { typeVal = Enum.Parse(fieldTypeEnum, spec.ClarionType, true); }
            catch { error = "unknown type " + spec.ClarionType; return null; }

            object field;
            try { field = ctor.Invoke(new[] { addParent, typeVal }); }
            catch (Exception ex) { error = "couldn't create field: " + (ex.InnerException?.Message ?? ex.Message); return null; }

            // Label is the settable identifier (DDLabeledItem.Label) — REQUIRED. A nameless field must never be
            // pasted (it would commit malformed or silently auto-named), so fail the spec here if we can't set it.
            // Clarion's Label setter THROWS on a name that already exists in the scope (the column-copy path
            // dodges this because Clarion's AddItem auto-renames a clash to "<label>_Copy" — but the text path
            // sets the Label up front, before AddItem runs). Mirror that convention here: if the requested label
            // is taken, pre-compute the same "<label>_Copy" name so the setter accepts it; the paste loop's
            // rename-tracking then surfaces the rename in the summary.
            string label = MakeUniqueLabel(addParent, spec.Label);
            if (!TrySetProp(field, "Label", label) && !TrySetProp(field, "Name", label))
            {
                error = "couldn't set the variable name";
                return null;
            }
            TrySetProp(field, "DataType", typeVal);   // ctor already set this; re-assert (authoritative either way)
            // Every REQUESTED shape property must ACTUALLY apply — otherwise we'd paste, attach, and count a
            // wrong-shape field. Fail the spec on any setter that returns false, so "added N" means N fields with
            // the requested type/size/dim/name, not just N attached objects. TrySetNumeric coerces width (DDField
            // is uint Characters/Dimensions, ushort Places/Dimension1), so these fail only on a genuine API
            // mismatch — which SHOULD fail loudly (reported as skipped) rather than silently mis-size the field.
            if (spec.Characters.HasValue && !TrySetNumeric(field, "Characters", spec.Characters.Value))
            { error = "couldn't set length " + spec.Characters.Value; return null; }
            if (spec.Places.HasValue && !TrySetNumeric(field, "Places", spec.Places.Value))
            { error = "couldn't set decimal places"; return null; }
            if (spec.Dim.HasValue
                && !TrySetNumeric(field, "Dimension1", spec.Dim.Value)
                && !TrySetNumeric(field, "Dimensions", spec.Dim.Value))
            { error = "couldn't set DIM(" + spec.Dim.Value + ")"; return null; }
            if (!string.IsNullOrEmpty(spec.ExternalName) && !TrySetProp(field, "ExternalName", spec.ExternalName))
            { error = "couldn't set NAME()"; return null; }
            return field;
        }

        // Commit ONE constructed field through Clarion's native paste-one-item: details.PasteItem(field, pos) →
        // item.Copy(AddParent) → AddItem(copy) (auto-renames a duplicate label) → OnItemAdded(copy). Returns the
        // newly-added DataDictionaryItem (or null + reason). first==true uses Inside (into the scope), else After.
        private static object PasteOneField(object tree, object details, object field, bool first, out string error)
        {
            error = null;
            var pasteItem = FindMethodArgs(details, "PasteItem",
                p => p.Length == 2 && p[1].ParameterType.IsEnum && p[0].ParameterType.IsInstanceOfType(field));
            if (pasteItem == null) { error = "no PasteItem command"; return null; }

            var nodePosEnum = pasteItem.GetParameters()[1].ParameterType;
            object pos;
            try { pos = Enum.Parse(nodePosEnum, first ? "Inside" : "After"); }
            catch { try { pos = Enum.Parse(nodePosEnum, "Inside"); } catch { error = "no NodePosition value"; return null; } }

            try
            {
                var result = pasteItem.Invoke(details, new[] { field, pos });
                if (result == null) { error = "paste returned nothing"; return null; }
                return result;
            }
            catch (Exception ex) { error = ex.InnerException?.Message ?? ex.Message; return null; }
        }

        // Confirm a just-pasted item is actually attached to the target FieldList (its Parent is the list, or the
        // list's Fields contains it by reference). PasteItem returning a non-null object is NOT proof of commit —
        // this turns "added" into "verified in the live model". (Persistence ACROSS an app save remains a live test.)
        private static bool VerifyAttached(object addParent, object item)
        {
            if (item == null || addParent == null) return false;
            if (ReferenceEquals(GetProp(item, "Parent"), addParent)) return true;
            var fields = GetProp(addParent, "Fields") as IEnumerable;
            if (fields != null)
                foreach (var f in fields)
                    if (ReferenceEquals(f, item)) return true;
            return false;
        }

        // Re-resolve the EntityBrowserDetails for a just-added item so the next paste can land After it (Tree
        // exposes a 1-arg GetDetails(DataDictionaryItem); the other 1-arg overload takes a TreeNodeAdv).
        private static object ReResolveDetails(object tree, object item)
        {
            var m = FindMethodArgs(tree, "GetDetails", p => p.Length == 1 && p[0].ParameterType.IsInstanceOfType(item));
            if (m == null) return null;
            try { return m.Invoke(tree, new[] { item }); } catch { return null; }
        }

        /// <summary>
        /// Copy an EXISTING dictionary column into Local/Global data as a variable (the in-pad drag-a-column
        /// path). Resolves the live source DDField from the dictionary (addParent.DataDictionary.Tables[table]
        /// → AllFields[column]) and commits it through Clarion's OWN native paste-one-item flow (PasteItem →
        /// source.Copy(AddParent) → AddItem → OnItemAdded) — a lossless copy that carries the column's full type,
        /// picture, dimensions, etc., with Clarion handling persistence + uniqueness auto-rename. UI thread.
        /// Same LOCAL wrong-procedure fail-closed guard as the other entry points.
        /// </summary>
        public static Result CopyColumnToScope(string scope, string table, string column, string expectedProcedure = null)
        {
            return CopyColumnsToScope(scope, new List<string[]> { new[] { table, column } }, expectedProcedure);
        }

        /// <summary>
        /// Copy one OR MORE existing dictionary columns into Local/Global data, IN ORDER. Resolves the add target
        /// ONCE, then pastes each resolved source DDField through Clarion's native paste-one-item flow — first
        /// Inside the scope, subsequent ones After the previous field (re-resolving details each time) so a
        /// multi-column drag lands in drag order rather than all stacking at the same Inside point. Lossless
        /// (each source.Copy(AddParent) carries the column's full type/picture/dims). UI thread; same LOCAL
        /// wrong-procedure fail-closed guard. tableColumns: each entry is [table, column].
        /// </summary>
        public static Result CopyColumnsToScope(string scope, IList<string[]> tableColumns, string expectedProcedure = null)
        {
            if (tableColumns == null || tableColumns.Count == 0)
                return Result.Fail("No columns to copy.");
            if (tableColumns.Count > MaxPasteFields)
                return Result.Fail("Too many columns in one drop (" + tableColumns.Count + "). The limit is " + MaxPasteFields + ".");

            string scopeName = ScopeName(scope);
            try
            {
                object tree, details, addParent; Result error;
                if (!ResolveAddTarget(scope, expectedProcedure, out tree, out details, out addParent, out error))
                    return error;

                var dd = GetProp(addParent, "DataDictionary");
                if (dd == null) return Result.Fail("No dictionary is loaded for this application.");

                int copied = 0;
                var failed = new List<string>();
                object curDetails = details;
                bool first = true;
                foreach (var tc in tableColumns)
                {
                    string table = tc != null && tc.Length > 0 ? tc[0] : null;
                    string column = tc != null && tc.Length > 1 ? tc[1] : null;
                    if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(column))
                    { failed.Add((column ?? "?") + " (missing table/column)"); continue; }

                    var source = FindDictionaryField(dd, table, column);
                    if (source == null) { failed.Add(table + "." + column + " (not found)"); continue; }

                    string pasteErr;
                    var newItem = PasteOneField(tree, curDetails, source, first, out pasteErr);
                    if (newItem == null) { failed.Add(table + "." + column + " (" + pasteErr + ")"); continue; }
                    if (!VerifyAttached(addParent, newItem)) { failed.Add(table + "." + column + " (not attached)"); continue; }

                    copied++;
                    first = false;
                    var nd = ReResolveDetails(tree, newItem);   // next column lands After this one (preserve order)
                    if (nd != null) curDetails = nd;
                }

                if (copied == 0)
                    return Result.Fail("Couldn't copy any column. " + string.Join("; ", failed));

                string msg = "Copied " + copied + " column" + (copied == 1 ? "" : "s") + " into " + scopeName + " data.";
                if (failed.Count > 0) msg += " Skipped " + failed.Count + ": " + string.Join("; ", failed);
                return Result.Done(msg);
            }
            catch (Exception ex)
            {
                return Result.Fail("Copy column failed: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        // Resolve a live DDField from the loaded dictionary by table + column name. Prefix-tolerant: matches the
        // column against the field's Name/CodeName/Label, and as a fallback against the un-prefixed tail (the
        // part after ':') so a pad row carrying "PRE:Field" still resolves to the field whose label is "Field".
        private static object FindDictionaryField(object dd, string table, string column)
        {
            var tables = GetProp(dd, "Tables") as IEnumerable;
            if (tables == null) return null;

            object file = null;
            foreach (var f in tables)
            {
                if (f == null) continue;
                if (NameEquals(f, table)) { file = f; break; }
            }
            if (file == null) return null;

            var fields = (GetProp(file, "AllFields") ?? GetProp(file, "Fields")) as IEnumerable;
            if (fields == null) return null;

            string wantBare = AfterColon(column);
            foreach (var fld in fields)
            {
                if (fld == null) continue;
                if (NameEquals(fld, column)) return fld;
                // prefix-insensitive fallback
                var fldName = GetProp(fld, "Name") ?? GetProp(fld, "Label");
                if (fldName != null && string.Equals(AfterColon(fldName.ToString()), wantBare, StringComparison.OrdinalIgnoreCase))
                    return fld;
            }
            return null;
        }

        private static bool NameEquals(object item, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return StrEq(GetProp(item, "Name"), name)
                || StrEq(GetProp(item, "CodeName"), name)
                || StrEq(GetProp(item, "Label"), name);
        }

        private static bool StrEq(object value, string name)
        {
            return value != null && string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);
        }

        // The part of a Clarion name after a ':' prefix separator (e.g. "PRE:Field" → "Field"); unchanged if none.
        private static string AfterColon(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.LastIndexOf(':');
            return i >= 0 && i < s.Length - 1 ? s.Substring(i + 1) : s;
        }

        // Display name for a scope ("local" → "Local", anything else → "Global"). Centralizes the repeated
        // normalization used across AddVariable / PasteVariableDefinitions / CopyColumnsToScope messages.
        private static string ScopeName(string scope)
        {
            return (scope ?? "").Trim().ToLowerInvariant() == "local" ? "Local" : "Global";
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
                if (!ResolveTargetField(scope, path, expectedProcedure, out tree, out ddField, out _, out error)) return error;

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
                object tree, ddField, fieldList; Result error;
                if (!ResolveTargetField(scope, path, expectedProcedure, out tree, out ddField, out fieldList, out error)) return error;

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

                deleteItem.Invoke(details, null);   // modal confirm dialog; Clarion removes on confirm

                // Honest postcondition: DeleteItem() returns whether or not the user confirmed Clarion's modal, so
                // re-resolve the same path against the live model — no longer resolvable == actually deleted. This
                // avoids depending on whether a removed field's Parent is nulled (which we can't assume).
                bool removed = FindFieldByPath(fieldList, SplitPath(path)) == null;
                return removed ? Result.Done("Deleted " + LeafName(path) + ".")
                               : Result.NoOp("Delete cancelled for " + LeafName(path) + ".");   // no mutation → no refresh
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
            out object tree, out object ddField, out object fieldList, out Result error)
        {
            tree = null; ddField = null; fieldList = null; error = null;

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
            fieldList = list;

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

        // Case-insensitive check for an existing field LABEL (or NAME) in the target FieldList. Used to mirror
        // Clarion's duplicate-label rename for the text-paste path (the Label setter throws on a clash).
        private static bool LabelExists(object addParent, string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            var fields = GetProp(addParent, "Fields") as IEnumerable;
            if (fields == null) return false;
            foreach (var f in fields)
            {
                var n = (GetProp(f, "Label") ?? GetProp(f, "Name"))?.ToString();
                if (!string.IsNullOrEmpty(n) && string.Equals(n, label, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Clarion-style unique label: the requested name if free, else "<label>_Copy" (matching Clarion's own
        // AddItem auto-rename, per the image John shared), then "_Copy2", "_Copy3", … for further clashes. Keeps
        // the text-paste path's behavior consistent with the column-copy path, which gets Clarion's rename free.
        private static string MakeUniqueLabel(object addParent, string baseLabel)
        {
            if (string.IsNullOrEmpty(baseLabel) || !LabelExists(addParent, baseLabel)) return baseLabel;
            string first = baseLabel + "_Copy";
            if (!LabelExists(addParent, first)) return first;
            for (int i = 2; i < 1000; i++)
            {
                string c = baseLabel + "_Copy" + i;
                if (!LabelExists(addParent, c)) return c;
            }
            return first; // pathological (1000 clashes) — let the setter surface it
        }

        // True when the docked tree isn't usable for the requested scope: no node at all, or (LOCAL) the node
        // belongs to a different procedure than the one the caller is editing. Drives the SetSchema re-push.
        private static bool ScopeNeedsRepopulate(object node, bool wantLocal, string expectedProcedure)
        {
            if (node == null) return true;
            if (!wantLocal || string.IsNullOrEmpty(expectedProcedure)) return false;
            return !string.Equals(LocalNodeProcedure(node), expectedProcedure, StringComparison.OrdinalIgnoreCase);
        }

        // Re-push the app's shared FileSchema into the docked FileSchemaPad so its tree rebuilds in place for
        // the currently-selected procedure — WITHOUT changing the active document. The IDE's own
        // FileSchemaControl.DisplayApp uses this exact SetSchema(fs, null, displayData:false) call; on the UI
        // thread it runs synchronously (FileSchemaPad.Refresh → _Refresh_Threaded inline), so the tree is fully
        // rebuilt before this returns and the caller can re-read nodes immediately. For LOCAL we re-push ONLY
        // when the shared schema is already pinned to the expected procedure, so we never disrupt the pad to
        // show a procedure we can't satisfy (that case fails closed downstream). Returns true if a re-push was
        // attempted. No native pointers, no SelectProcedure.
        private static bool TryRepopulatePadFromAppSchema(object pad, bool wantLocal, string expectedProcedure)
        {
            if (pad == null) return false;
            var appSchema = new AppTreeService().GetAppFileSchema();
            if (appSchema == null) return false;
            if (wantLocal)
            {
                var schemaProc = GetProp(appSchema, "ProcedureName") as string;
                if (string.IsNullOrEmpty(expectedProcedure)
                    || !string.Equals(schemaProc, expectedProcedure, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            // FileSchemaPad.SetSchema(FileSchema value, object selected, bool displayData) — the 3-arg overload
            // (the 2-arg one delegates to it). displayData:false rebuilds the tree without fronting the pad or
            // touching the active editor document.
            var setSchema = FindMethodArgs(pad, "SetSchema",
                p => p.Length == 3 && p[1].ParameterType == typeof(object) && p[2].ParameterType == typeof(bool));
            if (setSchema == null) return false;
            try { setSchema.Invoke(pad, new object[] { appSchema, null, false }); return true; }
            catch { return false; }
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

        // Set a NUMERIC property, COERCING the value to the property's actual CLR width first. DDField is uint
        // Characters/Dimensions + ushort Places/Dimension1 (matches the spec types today), but reflection
        // SetValue does NOT widen uint→int etc. — a mismatch would throw and be swallowed, silently dropping the
        // field's size. Convert.ChangeType makes that a non-event. Kept separate from TrySetProp, which also sets
        // object-reference props (e.g. SelectedNode) that must NOT pass through Convert.ChangeType.
        private static bool TrySetNumeric(object obj, string name, object value)
        {
            if (obj == null || value == null) return false;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var p = t.GetProperty(name, AllInstance | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite)
                {
                    try
                    {
                        var target = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                        object coerced = target.IsInstanceOfType(value) ? value : Convert.ChangeType(value, target);
                        p.SetValue(obj, coerced, null);
                        return true;
                    }
                    catch { return false; }
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
