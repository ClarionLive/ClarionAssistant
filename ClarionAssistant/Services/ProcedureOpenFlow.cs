using System;
using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>The IDE operations the open/select-procedure flow drives (seam for tests). AppTreeService implements
    /// it; tests\ProcedureOpenFlow.Test.cs fakes it. UI thread only, like everything that touches the app tree.</summary>
    public interface IProcedureOpenOps
    {
        /// <summary>The open embeditor's file name, or null when none is open.</summary>
        string OpenEmbeditorFile();
        /// <summary>True when an .app view exists at all.</summary>
        bool HasApp();
        /// <summary>App.IsLoaded: false while the IDE is still loading the app, null when unreadable.</summary>
        bool? IsAppLoaded();
        /// <summary>App.ProcedureNames, or null/empty while the app is loading.</summary>
        IList<string> ProcedureNames();
        /// <summary>Bring the app's window forward on its app-tree view. False when it could not be selected.</summary>
        bool ActivateAppTree();
        /// <summary>Pump until the app tree's ClaList is visible. False on timeout.</summary>
        bool WaitForClaList(int timeoutMs);
        /// <summary>Type the name into the ClaList locator and commit the highlight. Null on success, else why not.</summary>
        string TypeLocator(string name, int charDelayMs);
        /// <summary>Pump until the app tree's selected procedure equals <paramref name="name"/> or the timeout passes;
        /// returns the last selection read (null when the selection cannot be read).</summary>
        string WaitForSelected(string name, int timeoutMs);
        /// <summary>Click the app tree's Embeditor button. Null on success, else why not.</summary>
        string ClickEmbeditor();
        /// <summary>Pump until an embeditor is open. False on timeout.</summary>
        bool WaitForOpen(int timeoutMs);
        /// <summary>The OPEN embeditor's procedure, once its identity has settled (see
        /// <see cref="ProcedureOpenFlow.DecideOpenedName"/>): its name, a description that cannot equal
        /// <paramref name="requested"/> when the evidence contradicts itself, or null when it cannot be read.</summary>
        string OpenProcedureName(string requested);
        /// <summary>Cancel the open embeditor WITHOUT saving and wait for it to close. Null on success, else why not.</summary>
        string CancelAndWaitClosed();
    }

    public sealed class ProcedureOpenResult
    {
        public bool Ok;
        /// <summary>The procedure as App.ProcedureNames spells it, once the name gate has passed.</summary>
        public string Procedure;
        public string Message;
    }

    /// <summary>
    /// Opens (or only selects) a procedure from the app tree, refusing at each step rather than guessing
    /// (ticket a964cde3). The old path typed the name into the ClaList locator and clicked Embeditor whatever the
    /// locator had selected, and never checked what opened: with a CA Editor tab in front ("ClaList: NOT FOUND"),
    /// or keystrokes dropped while the IDE was loading, it opened VerifyInventoryFiles for CheckComma.
    ///
    /// The steps: no embeditor already open; the app has finished loading; the name is an exact procedure of
    /// the app; the app tree is brought forward and its list is visible; the locator's selection reads back as
    /// the name BEFORE the click; and the opened embeditor's procedure equals the name AFTER it. A mismatch after
    /// the open is cancelled WITHOUT saving: a wrong embeditor is never left open for edits to land in. The
    /// post-open check is the hard guarantee; the pre-click check only saves a wasted open. Each attempt uses the
    /// next, slower, locator speed (ClaList drops keystrokes typed too fast).
    /// </summary>
    public static class ProcedureOpenFlow
    {
        public const int ClaListTimeoutMs = 3000;
        public const int SelectionTimeoutMs = 2000;
        public const int OpenTimeoutMs = 45000;
        /// <summary>After the open times out, how long to keep watching for it to arrive late (to cancel it).</summary>
        public const int LateOpenDrainMs = 5000;
        /// <summary>How long the post-open identity may take to settle before the last reading is taken as final.</summary>
        public const int IdentitySettleMs = 3000;

        public static ProcedureOpenResult Open(IProcedureOpenOps ops, string requested, int[] charDelaysMs)
        {
            return Run(ops, requested, charDelaysMs, true);
        }

        public static ProcedureOpenResult Select(IProcedureOpenOps ops, string requested, int[] charDelaysMs)
        {
            return Run(ops, requested, charDelaysMs, false);
        }

        private static ProcedureOpenResult Run(IProcedureOpenOps ops, string requested, int[] charDelaysMs, bool open)
        {
            requested = (requested ?? "").Trim();
            if (requested.Length == 0) return Fail(null, "Error: procedure_name required");

            string openFile = ops.OpenEmbeditorFile();
            if (openFile != null)
                return Fail(null, "Error: An embeditor is already open (" + openFile + "). Please close it before " +
                    (open ? "opening" : "selecting") + " another procedure.");

            if (!ops.HasApp()) return Fail(null, "Error: no .app is open. Open the app with open_file first.");

            // Loading gate: while the IDE is still loading the app, the locator drops keystrokes and the tree
            // selection is unreliable. Refuse at once with a retryable error rather than wait or guess.
            var names = ops.ProcedureNames();
            if (ops.IsAppLoaded() == false || names == null || names.Count == 0)
                return Fail(null, "Error: the app is still loading. Retry in a few seconds, once the IDE has finished loading it.");

            // Exact-name gate: the locator is a PREFIX search, so a name that isn't a procedure would select its
            // nearest neighbour. Only exact names proceed, spelled as the app spells them.
            string canonical = FindExact(names, requested);
            if (canonical == null)
            {
                string near = Suggest(names, requested);
                return Fail(null, "Error: the app has no procedure named '" + requested + "'." +
                    (near.Length > 0 ? " Did you mean: " + near + "?" : "") + " Nothing was opened.");
            }

            if (charDelaysMs == null || charDelaysMs.Length == 0) charDelaysMs = new[] { 100 };
            string lastError = null;
            for (int attempt = 0; attempt < charDelaysMs.Length; attempt++)
            {
                if (!ops.ActivateAppTree() || !ops.WaitForClaList(ClaListTimeoutMs))
                    return Fail(canonical, "Error: could not bring the app tree forward (its procedure list is not visible). " +
                        "Click the app's tab and retry. Nothing was opened.");

                string typeErr = ops.TypeLocator(canonical, charDelaysMs[attempt]);
                if (typeErr != null) return Fail(canonical, "Error: " + typeErr + ". Nothing was opened.");

                // Pre-click check. Null means the selection can't be read here: carry on, the post-open check
                // still guards. A definite different name means the locator missed: don't click, retry slower.
                string selected = ops.WaitForSelected(canonical, SelectionTimeoutMs);
                if (selected != null && !Same(selected, canonical))
                {
                    lastError = "Error: the app tree selected '" + selected + "', not '" + canonical + "'. Nothing was " +
                        (open ? "opened" : "selected") + ".";
                    continue;
                }

                if (!open)
                    return new ProcedureOpenResult
                    {
                        Ok = true, Procedure = canonical,
                        Message = selected != null
                            ? "Selected '" + canonical + "' in the app tree."
                            : "Typed '" + canonical + "' into the app tree locator; the selection could not be read back, so check the app tree."
                    };

                string clickErr = ops.ClickEmbeditor();
                if (clickErr != null) return Fail(canonical, "Error: " + clickErr + ". Nothing was opened.");

                if (!ops.WaitForOpen(OpenTimeoutMs))
                {
                    // The click is still outstanding: the IDE may finish the open after we give up. Keep watching a
                    // little longer and cancel anything that arrives, unsaved, rather than leave it unverified.
                    string msg = "Error: the embeditor did not open for '" + canonical + "' within " + (OpenTimeoutMs / 1000) + "s.";
                    bool arrived = ops.OpenEmbeditorFile() != null || ops.WaitForOpen(LateOpenDrainMs);
                    if (!arrived)
                        return Fail(canonical, msg + " If an embeditor still appears later it was NOT verified: check it with " +
                            "get_embed_info and cancel it with cancel_embeditor unless it shows '" + canonical + "'.");
                    string lateErr = ops.CancelAndWaitClosed();
                    return Fail(canonical, msg + (lateErr == null
                        ? " It opened late and was cancelled without saving. Nothing is open."
                        : " It opened late. " + CouldNotClose(lateErr)));
                }

                // Post-open check: the hard guarantee. An unreadable name is treated as a mismatch.
                string opened = ops.OpenProcedureName(canonical);
                if (Same(opened, canonical))
                    return new ProcedureOpenResult { Ok = true, Procedure = canonical, Message = "Embeditor opened for '" + canonical + "'." };

                string cancelErr = ops.CancelAndWaitClosed();
                if (cancelErr != null)
                    return Fail(canonical, "Error: opened " + Describe(opened) + " instead of '" + canonical + "'. " + CouldNotClose(cancelErr));
                lastError = "Error: opened " + Describe(opened) + " instead of '" + canonical +
                    "'; it was cancelled without saving. Nothing is open.";
            }
            return Fail(canonical, lastError);
        }

        /// <summary>
        /// Decide which procedure an embeditor that has just opened is showing, from one reading of its evidence
        /// (pipeline run 1, a964cde3). The native ClaGenEditor is REUSED across opens and its header and document
        /// are updated asynchronously, so right after the open is detected they can still describe the PREVIOUS
        /// procedure. That is dangerous exactly when the previous procedure is the one requested and the locator
        /// picked another. So a reading is only trusted when its parts agree:
        ///   * <paramref name="module"/> (from PweeEditorDetails, which exists only while an embed is open, so it
        ///     belongs to THIS open) must match <paramref name="expectedModule"/>, the requested procedure's module
        ///     in the app, when both are known. A mismatch settles at once as a description that can never equal
        ///     the requested name.
        ///   * the header's name and the document's col-0 PROCEDURE name must agree when both are readable.
        /// Until then the reading is unsettled (null) and the caller reads again. On the <paramref name="final"/>
        /// reading (the settle window ran out): a single readable name is taken; a disagreement is described as
        /// such (so it fails the comparison); nothing readable is null. Pure; the caller pumps between readings.
        /// </summary>
        public static string DecideOpenedName(string header, string document, string module, string expectedModule,
            bool final, out bool settled)
        {
            settled = false;
            header = Clean(header); document = Clean(document);
            string mod = ModuleKey(module), expected = ModuleKey(expectedModule);

            if (mod != null && expected != null && !string.Equals(mod, expected, StringComparison.OrdinalIgnoreCase))
            {
                settled = true;
                return (header ?? document ?? "a procedure") + " (in module " + module.Trim() + ", not " + expectedModule.Trim() + ")";
            }
            // The open's own details are not readable yet: its module is the evidence tied to THIS open, so wait.
            if (mod == null && expected != null && !final) return null;

            if (header != null && document != null)
            {
                if (Same(header, document)) { settled = true; return header; }
                if (!final) return null;
                settled = true;
                return "'" + header + "' by its header but '" + document + "' by its source";
            }
            if (!final) return null;
            settled = true;
            return header ?? document;
        }

        /// <summary>
        /// The module NAME behind an app-model module value, or null. App.Procedures[i].Module is a Clarion.GEN.Module
        /// OBJECT (its file name is its Name property: "PRM002081.clw"), while PweeEditorDetails.Module is already a
        /// string. Never ToString() an unknown object: Clarion.GEN.Module doesn't override it, so that yields the type
        /// name "Clarion.GEN.Module" - which is how CA1's live test saw every correct open refused as "in module
        /// PRM002081.clw, not Clarion.GEN.Module" (a964cde3, live run on f7a637f).
        /// </summary>
        public static string ModuleNameOf(object module)
        {
            if (module == null) return null;
            var s = module as string;
            if (s != null) return Clean(s);
            foreach (var prop in new[] { "Name", "FileName", "ModuleName" })
            {
                try
                {
                    var pi = module.GetType().GetProperty(prop, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var v = pi == null ? null : pi.GetValue(module, null) as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
                catch { }
            }
            return null;
        }

        private static string Clean(string s) { return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }

        // "C:\App\PRM002022.clw" / "prm002022.CLW" / "PRM002022" -> "PRM002022": the comparison is on the module's
        // name, since the app's procedure list and the open's details need not spell the path the same way.
        private static string ModuleKey(string module)
        {
            module = Clean(module);
            if (module == null) return null;
            int slash = Math.Max(module.LastIndexOf('\\'), module.LastIndexOf('/'));
            if (slash >= 0) module = module.Substring(slash + 1);
            if (module.EndsWith(".clw", StringComparison.OrdinalIgnoreCase)) module = module.Substring(0, module.Length - 4);
            return module.Length == 0 ? null : module;
        }

        private static string Describe(string opened)
        {
            return opened == null ? "a procedure whose name could not be read" : "'" + opened + "'";
        }

        private static string CouldNotClose(string why)
        {
            return "WARNING: that embeditor could not be closed (" + why + "). It is still open: cancel it in the IDE WITHOUT saving.";
        }

        private static ProcedureOpenResult Fail(string canonical, string message)
        {
            return new ProcedureOpenResult { Ok = false, Procedure = canonical, Message = message };
        }

        private static bool Same(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string FindExact(IList<string> names, string name)
        {
            foreach (var n in names)
                if (Same(n, name)) return n.Trim();
            return null;
        }

        // Up to five names that start with, or contain, the request: enough to spot a typo, never a guess.
        private static string Suggest(IList<string> names, string name)
        {
            var hits = new List<string>();
            foreach (var n in names)
                if (n != null && n.StartsWith(name, StringComparison.OrdinalIgnoreCase) && hits.Count < 5) hits.Add(n.Trim());
            foreach (var n in names)
                if (n != null && hits.Count < 5 && !hits.Contains(n.Trim()) &&
                    n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(n.Trim());
            return string.Join(", ", hits.ToArray());
        }
    }
}
