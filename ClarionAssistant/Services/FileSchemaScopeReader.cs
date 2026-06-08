using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Reads the per-template-instance FILE SCOPES that Clarion's native "Data / Tables" pad shows
    /// ("File-Browsing List Box", "Update Record on Disk", "Relation Tree Viewing List Box", ...) from the LIVE
    /// schema MODEL on the open app view — <c>AppTreeService.GetAppFileSchema()</c> →
    /// <c>FileSchema.Templates</c> (one <c>FileBasedTemplate</c> per instance). The .txa [FILES] section is flat
    /// (one PRIMARY + a bare OTHERS list) and cannot express this per-instance grouping, so the model is the only
    /// source for it.
    ///
    /// Member names confirmed by a live dump_object_api probe against the open UpdateStores view (Clarion
    /// 12.0.0.14000): FileSchema.ProcedureName (proc gate) and FileSchema.Templates[i] with public properties
    /// <c>.Name</c> (the per-instance DISPLAY label, e.g. "Update Record on Disk") and <c>.Primary</c>
    /// (PrimaryFile, whose <c>.Name</c> is the attached table, e.g. "Stores"; can be null when
    /// MustHavePrimaryFile is false). This reads the schema MODEL — NOT the docked tree control — so it has no
    /// dependency on the native Data pad being open and no WinForms cross-thread concern (the earlier
    /// tree-control approach failed for exactly that reason: the user can have our Modern pad open with the
    /// native pad closed).
    ///
    /// Pure managed reflection — no native pointers, no Win32, no TXA. Bails to an empty result (→ caller falls
    /// back to the flat txa browse) on any reflection surprise or proc mismatch.
    /// </summary>
    public static class FileSchemaScopeReader
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>One template-instance file scope, e.g. "Update Record on Disk" with its attached table(s).</summary>
        public sealed class Scope
        {
            public string Label;          // FileBasedTemplate.Name — "File-Browsing List Box" / "Update Record on Disk" / ...
            public string Instance;       // same as Label (the model has no separate instance name) — used as a stable UI key
            public string PrimaryFile;    // Primary.CodeName — the attached primary table (e.g. Stores)
            public readonly List<FileRef> Files = new List<FileRef>();  // attached files: primary (depth 0) then related, with relation-tree depth
        }

        /// <summary>An attached file plus its depth in the relation tree (primary = 0, its related = 1, theirs = 2, ...).</summary>
        public sealed class FileRef
        {
            public string Name;   // bare dictionary file CodeName (e.g. "Publishers", "Titles")
            public int Depth;     // nesting depth for indented rendering
        }

        /// <summary>
        /// The live per-instance file scopes for the procedure the app view is CURRENTLY showing — but ONLY when
        /// that procedure matches <paramref name="expectedProcedure"/>. Returns an empty list (so the caller falls
        /// back to its flat .txa browse parsing) when no app/FileSchema is available, the FileSchema is showing a
        /// DIFFERENT procedure (FAIL CLOSED — the app view and the Modern pad can diverge, and returning another
        /// proc's schematic would be silently wrong), or any reflection step throws.
        /// </summary>
        public static List<Scope> ReadFileScopes(string expectedProcedure)
        {
            var outp = new List<Scope>();
            try
            {
                if (string.IsNullOrWhiteSpace(expectedProcedure)) return outp;

                var fileSchema = new AppTreeService().GetAppFileSchema();
                if (fileSchema == null) return outp;

                // FAIL CLOSED on procedure mismatch: FileSchema is repopulated on app-tree selection and may be on
                // a different procedure than the one the Modern pad is rendering. Only trust it on an exact match.
                var proc = AsString(GetMember(fileSchema, "ProcedureName"));
                if (!string.Equals(proc?.Trim(), expectedProcedure.Trim(), StringComparison.OrdinalIgnoreCase))
                    return outp;

                var templates = GetMember(fileSchema, "Templates") as IEnumerable;
                if (templates == null) return outp;

                foreach (var t in templates)
                {
                    var scope = BuildScope(t);
                    if (scope != null && scope.Files.Count > 0) outp.Add(scope);
                }
            }
            catch { return new List<Scope>(); }   // any surprise → caller uses the txa fallback
            return outp;
        }

        // Build a Scope from a FileBasedTemplate. The instance LABEL is template.Name (e.g. "Update Record on
        // Disk", "- Relation Tree Viewing List Box"). The attached FILES are resolved by CodeName — NOT by Name:
        // on a PrimaryFile/related node, .Name carries the key suffix (e.g. "Publishers - PUB:PubID_Key") which
        // never matches a dictionary file, whereas .CodeName is the bare file name ("Publishers"). A Form's
        // Primary.Name happens to be bare ("Stores"), which masked this — but relation trees exposed it.
        // Related files (relation-tree secondaries) nest under Primary.Relationships (each child also has
        // .Relationships), so we recurse to flatten Publishers→Titles→Sales into the scope's file list.
        private static Scope BuildScope(object template)
        {
            if (template == null) return null;
            var label = AsString(GetMember(template, "Name"));
            var primary = GetMember(template, "Primary");
            var primaryName = FileName(primary);

            var scope = new Scope
            {
                Label = string.IsNullOrEmpty(label) ? (primaryName ?? "(file scope)") : label,
                Instance = string.IsNullOrEmpty(label) ? (primaryName ?? "") : label,
                PrimaryFile = primaryName ?? ""
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(primaryName) && seen.Add(primaryName))
                scope.Files.Add(new FileRef { Name = primaryName, Depth = 0 });
            CollectRelated(primary, scope.Files, seen, 1);   // related files start one level under the primary
            return scope;
        }

        // Recursively gather related-file names from a node's Relationships (the real tree: direct children only,
        // each child carrying its own Relationships). Flattened into the scope's file list — the File Schematic
        // section renders a flat set of table cards (preserving the visual Publishers→Titles→Sales nesting is a
        // later polish). Depth-bounded against a pathological cyclic schema.
        private static void CollectRelated(object node, List<FileRef> into, HashSet<string> seen, int depth)
        {
            if (node == null || depth > 32) return;
            var rels = GetMember(node, "Relationships") as IEnumerable;
            if (rels == null) return;
            foreach (var r in rels)
            {
                if (r == null) continue;
                var name = FileName(r);
                if (!string.IsNullOrEmpty(name) && seen.Add(name)) into.Add(new FileRef { Name = name, Depth = depth });
                CollectRelated(r, into, seen, depth + 1);
            }
        }

        // The bare dictionary file name for a PrimaryFile / RelatedFile node: prefer .CodeName, then .Label, and
        // only fall back to .Name (which may carry a " - <Key>" suffix) or the resolved DDFile's name.
        private static string FileName(object fileNode)
        {
            if (fileNode == null) return null;
            return AsString(GetMember(fileNode, "CodeName"))
                ?? AsString(GetMember(fileNode, "Label"))
                ?? AsString(GetMember(GetMember(fileNode, "File"), "CodeName"))
                ?? AsString(GetMember(fileNode, "Name"));
        }

        // ---- tiny reflection helper: reads PROPERTIES and FIELDS (public + non-public, walking base types) ----
        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var p = t.GetProperty(name, AllInstance | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                { try { return p.GetValue(obj, null); } catch { return null; } }
                var fld = t.GetField(name, AllInstance | BindingFlags.DeclaredOnly);
                if (fld != null) { try { return fld.GetValue(obj); } catch { return null; } }
            }
            return null;
        }

        private static string AsString(object o)
        {
            var s = o?.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
