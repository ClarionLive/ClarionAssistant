using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using ClarionLsp.Contracts;
using ClarionCodeGraph.Graph;
using ClarionCodeGraph.Parsing;
using LspModels = ClarionLsp.Contracts.Models;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Single entry point every LSP consumer in this addin goes through. It routes each request to
    /// the SHARED Clarion language server (the msarson/clarion-lsp addin, reached via
    /// <see cref="ClarionLspLocator.Current"/>) when that addin is installed and running, and
    /// otherwise FALLS BACK to our bundled <see cref="LspClient"/>.
    ///
    /// The shared client exposes typed DTOs (HoverResult, LocationResult[], …) while our existing
    /// consumers were written against the bundled LspClient's shapes — raw LSP-JSON
    /// Dictionary&lt;string,object&gt; for the MCP tools, and the LspClient.CompletionItemInfo /
    /// DiagnosticEntry / DiagnosticWaitResult value types for the embeditor. So the SHARED path here
    /// converts the DTOs back into those exact legacy shapes; consumers don't change shape, only their
    /// accessor (LspClient.Active → SharedLspBridge).
    ///
    /// ── JIT-safety / version robustness (see HasRequiredCapabilities) ──────────────────────────────
    /// Our shipped ClarionLsp.Contracts.dll and the ClarionLsp addin's own copy are BOTH
    /// AssemblyVersion 1.0.0.0, so under "first-load-wins" the CLR may bind a STALE (≤1.0) contract
    /// that lacks the v1.1.0 methods we use. A method that statically references such a member throws
    /// (MissingMethod/TypeLoad) when it is JIT-compiled — even on the local-fallback branch. To stay
    /// crash-safe we keep every v1.1 member/DTO reference inside the private Shared* worker methods,
    /// which are ONLY invoked (hence only JIT-compiled) once <see cref="Shared"/> has confirmed, via
    /// reflection, that the live client actually exposes those methods. The public dispatchers and the
    /// local-fallback branches reference only the interface type and LspClient, so they always JIT.
    /// </summary>
    public static class SharedLspBridge
    {
        /// <summary>Settings key controlling shared-vs-bundled LSP selection. As of #40 the BUNDLED
        /// pure-v0.9.6 server is the DEFAULT — the shared-addin preference was dropped once pure upstream
        /// was proven (member-access + hover work with no solution handshake; CodeGraph nav/completion is
        /// served C#-side). So an ABSENT setting now means BUNDLED. To opt BACK IN to the shared ClarionLsp
        /// addin when it's installed, set "Lsp.ForceLocal=false".</summary>
        public const string ForceLocalSettingKey = "Lsp.ForceLocal";

        /// <summary>True → use the bundled LSP (the #40 default). Only an explicit "false" opts into the
        /// shared ClarionLsp addin; absent/anything-else → bundled.</summary>
        public static bool ForceLocal
        {
            get
            {
                try
                {
                    string v = new SettingsService().Get(ForceLocalSettingKey);
                    if (string.IsNullOrEmpty(v)) return true;   // #40: bundled pure-v0.9.6 is the default
                    return !v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
                }
                catch { return true; }
            }
        }

        /// <summary>
        /// The live shared client when present, running, NOT force-disabled, AND verified to expose the
        /// v1.1.0 method surface; otherwise null. Null is the universal "use the local fallback" signal
        /// across this class. References only the interface type + locator (both exist in ≤1.0), so this
        /// getter always JIT-compiles safely.
        /// </summary>
        private static IClarionLanguageClient Shared
        {
            get
            {
                if (ForceLocal) return null;
                IClarionLanguageClient c;
                try { c = ClarionLspLocator.Current; } catch { return null; }
                if (c == null) return null;
                try { if (!c.IsRunning) return null; } catch { return null; }
                return HasRequiredCapabilities(c) ? c : null;
            }
        }

        // D2: one-time (per client instance) reflection probe. Guards against a stale ClarionLsp whose
        // runtime IClarionLanguageClient lacks the v1.1.0 methods we depend on. Uses reflection only —
        // no static binding to the v1.1 members — so it is safe to JIT even under the old contract.
        private static readonly object _probeLock = new object();
        private static IClarionLanguageClient _probedClient;
        private static bool _probedOk;

        private static bool HasRequiredCapabilities(IClarionLanguageClient c)
        {
            lock (_probeLock)
            {
                if (ReferenceEquals(c, _probedClient)) return _probedOk;

                bool ok = false;
                try
                {
                    var t = c.GetType();
                    ok = t.GetMethod("GetCompletionAsync") != null
                      && t.GetMethod("GetDiagnosticsAsync") != null
                      && t.GetMethod("NotifyBufferChangedAsync") != null;
                }
                catch { ok = false; }

                _probedClient = c;
                _probedOk = ok;
                if (!ok)
                    Debug.WriteLine("[SharedLspBridge] The installed ClarionLsp addin's client lacks the "
                        + "v1.1.0 methods (GetCompletionAsync/GetDiagnosticsAsync/NotifyBufferChangedAsync) — "
                        + "treating shared LSP as unavailable and using the bundled LspClient. "
                        + "Install ClarionLsp >= 1.1.0 for the shared single-process path.");
                return ok;
            }
        }

        /// <summary>True when the shared ClarionLsp addin is the active (capability-verified) server.</summary>
        public static bool IsSharedActive { get { return Shared != null; } }

        /// <summary>"shared" | "local" | "none" — surfaced in diagnostics/status (the #37 resolver tag).</summary>
        public static string Resolver
        {
            get
            {
                if (Shared != null) return "shared";
                return (LspClient.Active != null && LspClient.Active.IsRunning) ? "local" : "none";
            }
        }

        /// <summary>True when SOME LSP (shared or bundled) is available to serve requests.</summary>
        public static bool IsRunning
        {
            get { return Shared != null || (LspClient.Active != null && LspClient.Active.IsRunning); }
        }

        // ===========================================================================================
        // Public dispatchers. Each references ONLY the interface type + LspClient, so it always JITs.
        // The v1.1 member/DTO references live exclusively in the Shared* workers below.
        // ===========================================================================================

        /// <summary>textDocument/hover → raw LSP-response-shaped dict (consumers read ["result"]). Falls back
        /// to the C# CodeGraph/ClarionGraph providers (library + cross-project symbols) when the LSP returns
        /// nothing — ticket 6e8f2439 item 6, so hovering an ABC/library symbol shows its signature.</summary>
        public static Dictionary<string, object> GetHover(string filePath, int line, int character, string bufferText = null)
        {
            // Library/ABC member access ("oInstance.Member") → PREEMPT the LSP with our precise member hover.
            // The LSP can't resolve libsrc member access (it returns the containing class at best), so ours is
            // strictly more useful here. Project-local member access is left to the LSP (returns null below).
            var lm = LibraryMemberAccessSymbol(filePath, line, character, bufferText);
            if (lm != null)
            {
                string mc = CgHoverText(lm, "ClarionGraph");
                if (!string.IsNullOrEmpty(mc))
                    return WrapResult(new Dictionary<string, object> { { "contents", mc } });
            }

            var c = Shared;
            Dictionary<string, object> primary = (c == null)
                ? (LspClient.Active != null ? LspClient.Active.GetHover(filePath, line, character, bufferText) : null)
                : SharedGetHover(c, filePath, line, character);
            if (!IsHoverEmpty(primary)) return primary;
            return CodeGraphHover(filePath, line, character, bufferText) ?? primary;
        }

        /// <summary>A member-access symbol ("oInstance.Member") resolved from the ClarionGraph LIBRARY DB,
        /// or null. Used to PREEMPT the LSP for member access on ABC/library types — the LSP only resolves to
        /// the containing CLASS there (so F12 lands on the class's first member, not the target). Member access
        /// on a PROJECT-LOCAL type (resolved from the project CodeGraph) returns null → left to the LSP, which
        /// resolves it precisely and from fresher state. Never throws.</summary>
        private static CodeGraphSymbol LibraryMemberAccessSymbol(string filePath, int line, int character, string bufferText)
        {
            try
            {
                string mDb, mLabel;
                var sym = ResolveMemberAccessSymbol(bufferText, filePath, line, character, out mDb, out mLabel);
                return (sym != null && mLabel == "ClarionGraph") ? sym : null;
            }
            catch { return null; }
        }

        /// <summary>textDocument/definition → raw LSP-response-shaped dict. Falls back to the C#
        /// CodeGraph provider (cross-project + ABC/library member access) when the LSP returns nothing.
        /// <paramref name="bufferText"/> (when supplied) lets the fallback resolve member access
        /// ("oInstance.Method" → the method's libsrc declaration) from the live buffer.</summary>
        public static Dictionary<string, object> GetDefinition(string filePath, int line, int character, string bufferText = null)
        {
            // Library/ABC member access → PREEMPT the LSP with our precise member definition. The LSP resolves
            // libsrc member access only to the containing CLASS, so F12/Ctrl+Click lands on the class's first
            // member (e.g. AutoRefresh) instead of the target member. Project-local member access is left to
            // the LSP (LibraryMemberAccessSymbol returns null for it).
            var lm = LibraryMemberAccessSymbol(filePath, line, character, bufferText);
            if (lm != null && !string.IsNullOrEmpty(lm.FilePath))
                return WrapResult(new System.Collections.ArrayList { CgLocation(lm.FilePath, lm.LineNumber) });

            var c = Shared;
            Dictionary<string, object> primary = (c == null)
                ? (LspClient.Active != null ? LspClient.Active.GetDefinition(filePath, line, character) : null)
                : SharedGetDefinition(c, filePath, line, character);
            if (!IsEmptyResult(primary)) return primary;
            return CodeGraphDefinition(filePath, line, character, bufferText) ?? primary;
        }

        /// <summary>textDocument/references → raw LSP-response-shaped dict. CodeGraph fallback when empty.</summary>
        public static Dictionary<string, object> GetReferences(string filePath, int line, int character)
        {
            var c = Shared;
            Dictionary<string, object> primary = (c == null)
                ? (LspClient.Active != null ? LspClient.Active.GetReferences(filePath, line, character) : null)
                : SharedGetReferences(c, filePath, line, character);
            if (!IsEmptyResult(primary)) return primary;
            return CodeGraphReferences(filePath, line, character) ?? primary;
        }

        /// <summary>textDocument/documentSymbol (optionally syncing a live buffer) → raw LSP dict.</summary>
        public static Dictionary<string, object> GetDocumentSymbols(string filePath, string bufferText = null)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.GetDocumentSymbols(filePath, bufferText) : null; }
            return SharedGetDocumentSymbols(c, filePath, bufferText);
        }

        /// <summary>workspace/symbol → raw LSP dict. CodeGraph fallback (cross-project) when empty.</summary>
        public static Dictionary<string, object> FindWorkspaceSymbol(string query)
        {
            var c = Shared;
            Dictionary<string, object> primary = (c == null)
                ? (LspClient.Active != null ? LspClient.Active.FindWorkspaceSymbol(query) : null)
                : SharedFindWorkspaceSymbol(c, query);
            if (!IsEmptyResult(primary)) return primary;
            return CodeGraphWorkspaceSymbol(query) ?? primary;
        }

        /// <summary>textDocument/rename → WorkspaceEdit-shaped dict (ExtractWorkspaceEditFlat-compatible).</summary>
        public static Dictionary<string, object> Rename(string filePath, int line, int character, string newName)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.Rename(filePath, line, character, newName) : null; }
            return SharedRename(c, filePath, line, character, newName);
        }

        /// <summary>textDocument/completion → bundled LspClient.CompletionItemInfo list, augmented with
        /// CodeGraph prefix completion (task a47a6cac Phase 1).</summary>
        public static List<LspClient.CompletionItemInfo> GetCompletion(
            string filePath, int line, int character, int timeoutMs = 2500, string bufferText = null)
        {
            // Primary completion from the LSP (shared addin or bundled client).
            List<LspClient.CompletionItemInfo> primary;
            var c = Shared;
            if (c == null)
            {
                var lsp = LspClient.Active;
                primary = lsp != null
                    ? lsp.GetCompletion(filePath, line, character, timeoutMs, bufferText)
                    : new List<LspClient.CompletionItemInfo>();
            }
            else
            {
                primary = SharedGetCompletion(c, filePath, line, character, timeoutMs, bufferText);
            }
            if (primary == null) primary = new List<LspClient.CompletionItemInfo>();

            // CodeGraph prefix-completion augmentation (task a47a6cac Phase 1). Mark's pure upstream
            // server does MEMBER-ACCESS-ONLY completion; for a BARE PREFIX (line not ending in '.') it
            // returns nothing. We merge in global symbols (procedures/functions/classes/vars) from the
            // .codegraph.db so typing the first letters of a symbol + Ctrl+Space completes it.
            // Member-access stays LSP-only — the server resolves type-scoped members, CodeGraph can't.
            // Defensive: never throws (completion must not break), never overrides a real LSP item.
            try { MergeBarePrefixCompletions(primary, filePath, line, character, bufferText); }
            catch (Exception ex) { Debug.WriteLine("[SharedLspBridge] bare-prefix completion merge failed: " + ex.Message); }

            // Qualified group/queue FIELD completion (task a47a6cac Phase 2 refinement): PRE: prefix
            // ("Cus:" → fields of GROUP,PRE(Cus)) and dotted access ("Group." → its fields). Separate from
            // the bare path (which returns early in a qualified context). Never overrides a real LSP item.
            try { MergeQualifiedFieldCompletions(primary, filePath, line, character, bufferText); }
            catch (Exception ex) { Debug.WriteLine("[SharedLspBridge] qualified field completion merge failed: " + ex.Message); }

            // Class member-access (ticket 6e8f2439, item 5b): "oInstance." → that instance's ABC/library
            // methods from ClarionGraph (+ project CodeGraph), resolved by the instance's declared class
            // type. Mark's LSP answers member access for project-local types; this SUPPLEMENTS it for
            // library/ABC types it may not index. Returns the owner's full member/field name set (for the
            // scoping pass below). Additive + deduped + never blanks the LSP's members.
            HashSet<string> memberScope = null;
            try { memberScope = MergeMemberAccessCompletions(primary, filePath, line, character, bufferText); }
            catch (Exception ex) { Debug.WriteLine("[SharedLspBridge] member-access completion merge failed: " + ex.Message); }

            // Member/field-access scoping (mirror of the colon-qualifier fix). When '.' doesn't resolve to a
            // class server-side, Mark's LSP falls back to a global keyword/builtin dump (ABS, ACCEPT, END,
            // ENTRY, ...) and Monaco filters it by the typed partial, so language keywords leak in beside the
            // real members. Once we've resolved the owner, scope the list to its members/fields. Genuine LSP
            // project-local members survive — a resolved class's members are also in the project CodeGraph
            // set. Guard: only scope when matches remain, so it can never blank an otherwise-working list.
            try
            {
                if (memberScope != null && memberScope.Count > 0)
                {
                    var scoped = primary.FindAll(it =>
                        it != null && !string.IsNullOrEmpty(it.Label) && memberScope.Contains(it.Label));
                    if (scoped.Count > 0) primary = scoped;
                }
            }
            catch (Exception ex) { Debug.WriteLine("[SharedLspBridge] member-access scoping failed: " + ex.Message); }

            // Colon-qualifier scoping. When the cursor sits right after an "IDENT:" qualifier (PROP:/EVENT:/
            // PROPLIST:/group-PRE like Cus:...), the Monaco replace-range breaks on the ':' and is EMPTY, so
            // the client does NO prefix filtering — Mark's LSP also returns its global built-in/keyword set
            // (ACOS, ABS, ...) which then shows alongside the relevant IDENT:* items. Scope the list to labels
            // starting with the qualifier (those carry the prefix: "PROP:Bevel", "Cus:Field"). Defensive:
            // only apply when matches remain, so it can never blank out an otherwise-working list. Member
            // access ('.') has no colon → unaffected.
            // Colon-qualified completion (PROP:/EVENT:/PROPLIST:/group-PRE Cus:...). In this context the LSP
            // returns a broad in-scope symbol dump (locals, globals, builtins like ACOS) — NOT IDENT:* members
            // — and because the Monaco replace-range breaks on ':' (empty range) the client shows them
            // unfiltered. Fix in two moves: (1) supply the IDENT:* members from ClarionGraph/CodeGraph (e.g.
            // every PROP:* property equate from property.clw), then (2) scope the list to labels starting with
            // the qualifier, which drops the LSP noise. Guard: only scope when matches remain.
            try
            {
                string qualifier = ColonQualifierAt(filePath, line, character, bufferText);
                if (qualifier != null)
                {
                    MergeColonQualifierCompletions(primary, qualifier, filePath);
                    if (primary.Count > 0)
                    {
                        var scoped = primary.FindAll(it =>
                            it != null && !string.IsNullOrEmpty(it.Label) &&
                            it.Label.StartsWith(qualifier, StringComparison.OrdinalIgnoreCase));
                        if (scoped.Count > 0) primary = scoped;
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[SharedLspBridge] colon-qualifier completion failed: " + ex.Message); }

            return primary;
        }

        // Matches an "IDENT:" qualifier (with the trailing ':') immediately left of the cursor, allowing a
        // partial suffix after it (PROP: , PROP:Be , Cus:Na). Group 1 includes the colon.
        private static readonly Regex ColonQualifierPattern =
            new Regex(@"([A-Za-z_][A-Za-z0-9_]*:)[A-Za-z0-9_]*$", RegexOptions.Compiled);

        /// <summary>In a colon-qualified context ("PROP:", "EVENT:", "PROPLIST:", ...), add IDENT:* members
        /// (property/event equates, etc.) from the project CodeGraph and the ClarionGraph library DB. Labels
        /// carry the full "IDENT:Name"; the insert text is the suffix after the ':' (the typed "IDENT:" stays
        /// put because the Monaco replace-range breaks on the colon). Deduped against existing items. The LSP
        /// itself returns only a broad scope dump here, so this is what actually populates PROP:/EVENT:
        /// completion. Never throws.</summary>
        private static void MergeColonQualifierCompletions(
            List<LspClient.CompletionItemInfo> primary, string qualifier, string filePath)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in primary)
                if (it != null && !string.IsNullOrEmpty(it.Label)) seen.Add(it.Label);

            string[] dbs = { ResolveCodeGraphDb(filePath), ClarionGraphService.ResolveDbPath() };
            foreach (string db in dbs)
            {
                try
                {
                    if (string.IsNullOrEmpty(db) || !File.Exists(db)) continue;
                    using (var p = new CodeGraphProvider())
                    {
                        if (!p.Open(db)) continue;
                        var syms = p.FindSymbols(qualifier, 2000);   // LIKE %IDENT:% — narrowed to true prefix below
                        if (syms == null) continue;
                        foreach (var s in syms)
                        {
                            if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                            if (!s.Name.StartsWith(qualifier, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!seen.Add(s.Name)) continue;
                            int ci = s.Name.IndexOf(':');
                            string insert = (ci >= 0 && ci < s.Name.Length - 1) ? s.Name.Substring(ci + 1) : s.Name;
                            primary.Add(new LspClient.CompletionItemInfo
                            {
                                Label = s.Name,
                                Kind = 21,   // Constant (equates)
                                Detail = CgCompletionDetail(s),
                                InsertText = insert
                            });
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>The "IDENT:" qualifier (e.g. "PROP:") immediately before the cursor, or null when the
        /// cursor is not in a colon-qualified context. Uses the same line/column slicing as the merges.</summary>
        private static string ColonQualifierAt(string filePath, int line, int character, string bufferText)
        {
            try
            {
                string lineText = CgLineAt(bufferText, filePath, line);
                if (lineText == null) return null;
                int col = character < 0 ? 0 : (character > lineText.Length ? lineText.Length : character);
                string upToCursor = lineText.Substring(0, col);
                if (upToCursor.IndexOf('!') >= 0) return null; // comment line — no completion scoping
                var m = ColonQualifierPattern.Match(upToCursor);
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>Push the live embeditor buffer to the server (completion/diagnostics see current text).</summary>
        public static void EnsureBufferSynced(string filePath, string bufferText)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; if (lsp != null) lsp.EnsureBufferSynced(filePath, bufferText); return; }
            SharedEnsureBufferSynced(c, filePath, bufferText);
        }

        /// <summary>Diagnostics for a live buffer (embeditor squiggles).</summary>
        public static LspClient.DiagnosticWaitResult WaitForDiagnostics(string filePath, int timeoutMs, bool forceRefresh)
        {
            var c = Shared;
            if (c == null)
            {
                var lsp = LspClient.Active;
                return lsp != null
                    ? lsp.WaitForDiagnostics(filePath, timeoutMs, forceRefresh)
                    : new LspClient.DiagnosticWaitResult { Entries = new List<LspClient.DiagnosticEntry>(), Pending = true };
            }
            return SharedGetDiagnostics(c, filePath, timeoutMs, true);
        }

        /// <summary>Diagnostics for an on-disk file (MCP lsp_diagnostics tool).</summary>
        public static LspClient.DiagnosticWaitResult GetDiagnostics(string filePath, int timeoutMs = 3000)
        {
            var c = Shared;
            if (c == null)
            {
                var lsp = LspClient.Active;
                return lsp != null
                    ? lsp.GetDiagnostics(filePath, timeoutMs)
                    : new LspClient.DiagnosticWaitResult { Entries = new List<LspClient.DiagnosticEntry>(), Pending = true };
            }
            return SharedGetDiagnostics(c, filePath, timeoutMs, false);
        }

        /// <summary>Last diagnostics computed for a file (no re-query).</summary>
        public static List<LspClient.DiagnosticEntry> GetCachedDiagnostics(string filePath)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.GetCachedDiagnostics(filePath) : null; }
            lock (_sharedDiagLock)
            {
                List<LspClient.DiagnosticEntry> entries;
                return _sharedDiagCache.TryGetValue(filePath, out entries) ? new List<LspClient.DiagnosticEntry>(entries) : null;
            }
        }

        // ===========================================================================================
        // Shared-path workers. These reference the v1.1 methods + Models DTOs and are invoked ONLY
        // when Shared != null (capabilities verified), so they never JIT under a stale contract.
        // ===========================================================================================

        private static Dictionary<string, object> SharedGetHover(IClarionLanguageClient c, string filePath, int line, int character)
        {
            try
            {
                LspModels.HoverResult h = c.GetHoverAsync(filePath, line, character, 1500).GetAwaiter().GetResult();
                object result = null;
                if (h != null)
                {
                    var hov = new Dictionary<string, object> { { "contents", h.Contents ?? "" } };
                    var rng = RangeToDict(h.Range);
                    if (rng != null) hov["range"] = rng;
                    result = hov;
                }
                return WrapResult(result);
            }
            catch (Exception ex) { return SharedError("hover", ex); }
        }

        private static Dictionary<string, object> SharedGetDefinition(IClarionLanguageClient c, string filePath, int line, int character)
        {
            try { return WrapResult(LocationsToList(c.GetDefinitionAsync(filePath, line, character).GetAwaiter().GetResult())); }
            catch (Exception ex) { return SharedError("definition", ex); }
        }

        private static Dictionary<string, object> SharedGetReferences(IClarionLanguageClient c, string filePath, int line, int character)
        {
            try { return WrapResult(LocationsToList(c.GetReferencesAsync(filePath, line, character, true).GetAwaiter().GetResult())); }
            catch (Exception ex) { return SharedError("references", ex); }
        }

        private static Dictionary<string, object> SharedGetDocumentSymbols(IClarionLanguageClient c, string filePath, string bufferText)
        {
            try
            {
                if (!string.IsNullOrEmpty(bufferText))
                {
                    try { c.NotifyBufferChangedAsync(filePath, bufferText).GetAwaiter().GetResult(); } catch { }
                }
                return WrapResult(SymbolsToList(c.GetDocumentSymbolsAsync(filePath).GetAwaiter().GetResult()));
            }
            catch (Exception ex) { return SharedError("documentSymbol", ex); }
        }

        private static Dictionary<string, object> SharedFindWorkspaceSymbol(IClarionLanguageClient c, string query)
        {
            try { return WrapResult(SymbolsToList(c.FindWorkspaceSymbolAsync(query).GetAwaiter().GetResult())); }
            catch (Exception ex) { return SharedError("workspaceSymbol", ex); }
        }

        private static Dictionary<string, object> SharedRename(IClarionLanguageClient c, string filePath, int line, int character, string newName)
        {
            try
            {
                LspModels.RenameEdit[] edits = c.RenameAsync(filePath, line, character, newName).GetAwaiter().GetResult();
                if (edits == null || edits.Length == 0) return WrapResult(null);

                // Fold into an LSP WorkspaceEdit.changes = { uri: [ {range, newText}, ... ] }.
                var changes = new Dictionary<string, object>();
                foreach (var e in edits)
                {
                    if (e == null || string.IsNullOrEmpty(e.FilePath)) continue;
                    string uri = e.FilePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                        ? e.FilePath
                        : FilePathToUri(e.FilePath);
                    System.Collections.ArrayList bucket;
                    object existing;
                    if (changes.TryGetValue(uri, out existing)) bucket = (System.Collections.ArrayList)existing;
                    else { bucket = new System.Collections.ArrayList(); changes[uri] = bucket; }

                    var te = new Dictionary<string, object> { { "newText", e.NewText ?? "" } };
                    var rng = RangeToDict(e.Range);
                    if (rng != null) te["range"] = rng;
                    bucket.Add(te);
                }
                return WrapResult(new Dictionary<string, object> { { "changes", changes } });
            }
            catch (Exception ex) { return SharedError("rename", ex); }
        }

        private static List<LspClient.CompletionItemInfo> SharedGetCompletion(
            IClarionLanguageClient c, string filePath, int line, int character, int timeoutMs, string bufferText)
        {
            var items = new List<LspClient.CompletionItemInfo>();
            // The shared GetCompletionAsync completes against the document text we hand it — a null/empty buffer =
            // empty document = no items. When the caller didn't pass a live buffer (the file-mode "context-free"
            // call site), fall back to the on-disk text so the server has the document, mirroring
            // SharedGetDiagnostics and the bundled LspClient.EnsureDocumentOpen. (Bob, ticket 3d9a6ec9)
            if (string.IsNullOrEmpty(bufferText) && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try { bufferText = File.ReadAllText(filePath); } catch { }
            }
            try
            {
                LspModels.CompletionResult[] comps =
                    c.GetCompletionAsync(filePath, line, character, bufferText, timeoutMs).GetAwaiter().GetResult();
                if (comps != null)
                {
                    foreach (var item in comps)
                    {
                        if (item == null || string.IsNullOrEmpty(item.Label)) continue;
                        items.Add(new LspClient.CompletionItemInfo
                        {
                            Label = item.Label,
                            Kind = CompletionKindToInt(item.Kind),
                            Detail = item.Detail,
                            Documentation = item.Documentation,
                            InsertText = item.InsertText
                        });
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[SharedLspBridge] completion (shared) failed: " + ex.Message); }
            return items;
        }

        private static void SharedEnsureBufferSynced(IClarionLanguageClient c, string filePath, string bufferText)
        {
            if (string.IsNullOrEmpty(filePath) || bufferText == null) return;
            lock (_sharedBufLock) { _sharedBuffers[filePath] = bufferText; }
            try { c.NotifyBufferChangedAsync(filePath, bufferText).GetAwaiter().GetResult(); }
            catch (Exception ex) { Debug.WriteLine("[SharedLspBridge] NotifyBufferChanged failed: " + ex.Message); }
        }

        /// <summary>Shared diagnostics. <paramref name="liveBuffer"/> true → use the last synced embeditor
        /// buffer; false → read the file from disk (MCP tool). Single request/response (no publish/wait).</summary>
        private static LspClient.DiagnosticWaitResult SharedGetDiagnostics(IClarionLanguageClient c, string filePath, int timeoutMs, bool liveBuffer)
        {
            string buffer = null;
            if (liveBuffer) { lock (_sharedBufLock) { _sharedBuffers.TryGetValue(filePath, out buffer); } }
            if (buffer == null && File.Exists(filePath)) { try { buffer = File.ReadAllText(filePath); } catch { } }

            var result = new LspClient.DiagnosticWaitResult { Entries = new List<LspClient.DiagnosticEntry>(), Pending = true };
            try
            {
                LspModels.DiagnosticResult[] diags =
                    c.GetDiagnosticsAsync(filePath, buffer ?? "", timeoutMs).GetAwaiter().GetResult();
                result.Entries = DiagnosticsToEntries(diags);
                result.Pending = false;
                lock (_sharedDiagLock) { _sharedDiagCache[filePath] = result.Entries; }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SharedLspBridge] GetDiagnostics (shared) failed: " + ex.Message);
            }
            return result;
        }

        // ── Shared-path caches (the shared contract is request/response, so we retain the last buffer
        //    and diagnostics per file the way the publish-based bundled client did) ──────────────────
        private static readonly object _sharedBufLock = new object();
        private static readonly Dictionary<string, string> _sharedBuffers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _sharedDiagLock = new object();
        private static readonly Dictionary<string, List<LspClient.DiagnosticEntry>> _sharedDiagCache =
            new Dictionary<string, List<LspClient.DiagnosticEntry>>(StringComparer.OrdinalIgnoreCase);

        // ===========================================================================================
        // DTO → legacy-shape converters (LspModels.* references — only reached from Shared* workers).
        // ===========================================================================================

        private static Dictionary<string, object> WrapResult(object result)
        {
            return new Dictionary<string, object> { { "result", result } };
        }

        private static Dictionary<string, object> SharedError(string op, Exception ex)
        {
            Debug.WriteLine("[SharedLspBridge] " + op + " (shared) failed: " + ex.Message);
            return new Dictionary<string, object>
            {
                { "error", new Dictionary<string, object> { { "message", "shared LSP " + op + " failed: " + ex.Message } } }
            };
        }

        private static System.Collections.ArrayList LocationsToList(LspModels.LocationResult[] locs)
        {
            var list = new System.Collections.ArrayList();
            if (locs == null) return list;
            foreach (var l in locs)
            {
                if (l == null || string.IsNullOrEmpty(l.FilePath)) continue;
                var loc = new Dictionary<string, object> { { "uri", FilePathToUri(l.FilePath) } };
                var rng = RangeToDict(l.Range);
                if (rng != null) loc["range"] = rng;
                list.Add(loc);
            }
            return list;
        }

        private static System.Collections.ArrayList SymbolsToList(LspModels.SymbolResult[] syms)
        {
            var list = new System.Collections.ArrayList();
            if (syms == null) return list;
            foreach (var s in syms)
            {
                if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                var sym = new Dictionary<string, object>
                {
                    { "name", s.Name },
                    { "kind", SymbolKindToInt(s.Kind) }
                };
                if (!string.IsNullOrEmpty(s.ContainerName)) sym["containerName"] = s.ContainerName;
                if (!string.IsNullOrEmpty(s.FilePath))
                {
                    var loc = new Dictionary<string, object> { { "uri", FilePathToUri(s.FilePath) } };
                    var rng = RangeToDict(s.Range);
                    if (rng != null) loc["range"] = rng;
                    sym["location"] = loc;
                }
                list.Add(sym);
            }
            return list;
        }

        private static List<LspClient.DiagnosticEntry> DiagnosticsToEntries(LspModels.DiagnosticResult[] diags)
        {
            var entries = new List<LspClient.DiagnosticEntry>();
            if (diags == null) return entries;
            foreach (var d in diags)
            {
                if (d == null) continue;
                var e = new LspClient.DiagnosticEntry
                {
                    Severity = SeverityToInt(d.Severity),
                    Message = d.Message,
                    Source = d.Source
                };
                if (d.Range != null)
                {
                    if (d.Range.Start != null) { e.Line = d.Range.Start.Line; e.Character = d.Range.Start.Character; }
                    if (d.Range.End != null) { e.EndLine = d.Range.End.Line; e.EndCharacter = d.Range.End.Character; }
                }
                entries.Add(e);
            }
            return entries;
        }

        private static Dictionary<string, object> RangeToDict(LspModels.Range r)
        {
            if (r == null) return null;
            return new Dictionary<string, object>
            {
                { "start", PositionToDict(r.Start) },
                { "end", PositionToDict(r.End) }
            };
        }

        private static Dictionary<string, object> PositionToDict(LspModels.Position p)
        {
            int line = p != null ? p.Line : 0;
            int ch = p != null ? p.Character : 0;
            return new Dictionary<string, object> { { "line", line }, { "character", ch } };
        }

        /// <summary>Shared DiagnosticResult.Severity is a string; map to the LSP int the rest of our code
        /// uses (1=Error, 2=Warning, 3=Information, 4=Hint). Tolerates numeric strings too.</summary>
        private static int SeverityToInt(string severity)
        {
            if (string.IsNullOrEmpty(severity)) return 1;
            int n;
            if (int.TryParse(severity, out n) && n >= 1 && n <= 4) return n;
            switch (severity.Trim().ToLowerInvariant())
            {
                case "error": return 1;
                case "warning": case "warn": return 2;
                case "information": case "info": return 3;
                case "hint": return 4;
                default:
                    // Mark's server defaults null/unknown to "Error" on its side, so this is belt-and-
                    // suspenders — but log so a future non-spec severity string is visible, not silent.
                    Debug.WriteLine("[SharedLspBridge] unmapped diagnostic severity '" + severity + "' -> Error(1)");
                    return 1;
            }
        }

        /// <summary>Map the shared CompletionResult.Kind string to the LSP CompletionItemKind int the
        /// embeditor passes to Monaco. Unknown → 0 (Monaco shows a default icon).</summary>
        private static int CompletionKindToInt(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return 0;
            int n;
            if (int.TryParse(kind, out n)) return (n >= 1 && n <= 25) ? n : 0;
            switch (kind.Trim().ToLowerInvariant())
            {
                case "text": return 1;
                case "method": return 2;
                case "function": return 3;
                case "constructor": return 4;
                case "field": return 5;
                case "variable": return 6;
                case "class": return 7;
                case "interface": return 8;
                case "module": return 9;
                case "property": return 10;
                case "unit": return 11;
                case "value": return 12;
                case "enum": return 13;
                case "keyword": return 14;
                case "snippet": return 15;
                case "color": return 16;
                case "file": return 17;
                case "reference": return 18;
                case "folder": return 19;
                case "enummember": return 20;
                case "constant": return 21;
                case "struct": return 22;
                case "event": return 23;
                case "operator": return 24;
                case "typeparameter": return 25;
                default:
                    Debug.WriteLine("[SharedLspBridge] unmapped completion kind '" + kind + "' -> 0 (Monaco default icon)");
                    return 0;
            }
        }

        /// <summary>Map the shared SymbolResult.Kind string to the LSP SymbolKind int. Unknown → 0.</summary>
        private static int SymbolKindToInt(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return 0;
            int n;
            if (int.TryParse(kind, out n)) return (n >= 1 && n <= 26) ? n : 0;
            switch (kind.Trim().ToLowerInvariant())
            {
                case "file": return 1;
                case "module": return 2;
                case "namespace": return 3;
                case "package": return 4;
                case "class": return 5;
                case "method": return 6;
                case "property": return 7;
                case "field": return 8;
                case "constructor": return 9;
                case "enum": return 10;
                case "interface": return 11;
                case "function": return 12;
                case "variable": return 13;
                case "constant": return 14;
                case "string": return 15;
                // Mark's shared server collapses SymbolKind 16..26 to the literal "Symbol", and maps
                // null to "Unknown" (ClarionLspService SymbolKindName) — both are expected sentinels
                // with no valid LSP int, so map them to 0 explicitly rather than via the default.
                case "unknown": case "symbol": return 0;
                // The 16..26 names below are defensive only: the shared server never emits them (it
                // sends "Symbol"); retained for a future server that doesn't collapse the high kinds.
                case "number": return 16;
                case "boolean": return 17;
                case "array": return 18;
                case "object": return 19;
                case "key": return 20;
                case "null": return 21;
                case "enummember": return 22;
                case "struct": return 23;
                case "event": return 24;
                case "operator": return 25;
                case "typeparameter": return 26;
                default:
                    Debug.WriteLine("[SharedLspBridge] unmapped symbol kind '" + kind + "' -> 0");
                    return 0;
            }
        }

        // Mirrors LspClient.FilePathToUri (private there) so bridge-built locations match the canonical
        // URI shape the rest of the code already produces.
        private static string FilePathToUri(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath;
            if (filePath.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) return filePath;
            return "file:///" + filePath.Replace("\\", "/").Replace(" ", "%20");
        }

        /// <summary>Inverse of <see cref="FilePathToUri"/>: file:///H:/dir/f.clw -&gt; H:\dir\f.clw.</summary>
        public static string UriToFilePath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return uri;
            string p = uri;
            if (p.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) p = p.Substring(8);
            else if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) p = p.Substring(7);
            return p.Replace("/", "\\").Replace("%20", " ");
        }

        /// <summary>Extract the FIRST Location from a definition/references result dict
        /// (<c>{ "result": [ { uri, range:{ start:{ line, character } } } ] }</c>) as an on-disk path +
        /// 0-based line/character. Returns false on any shape mismatch. Shared by the F12 hosts (#40).</summary>
        public static bool TryGetFirstLocation(Dictionary<string, object> result, out string filePath, out int line, out int character)
        {
            filePath = null; line = 0; character = 0;
            try
            {
                object res;
                if (result == null || !result.TryGetValue("result", out res) || res == null) return false;

                object loc = res;
                if (!(res is System.Collections.IDictionary) && res is System.Collections.IEnumerable)
                {
                    loc = null;
                    foreach (var item in (System.Collections.IEnumerable)res) { loc = item; break; } // first location
                }
                var locDict = loc as System.Collections.IDictionary;
                if (locDict == null) return false;

                object uriObj = locDict.Contains("uri") ? locDict["uri"] : null;
                if (uriObj == null) return false;
                filePath = UriToFilePath(uriObj.ToString());

                var range = (locDict.Contains("range") ? locDict["range"] : null) as System.Collections.IDictionary;
                if (range != null)
                {
                    var start = (range.Contains("start") ? range["start"] : null) as System.Collections.IDictionary;
                    if (start != null)
                    {
                        if (start.Contains("line")) line = Convert.ToInt32(start["line"]);
                        if (start.Contains("character")) character = Convert.ToInt32(start["character"]);
                    }
                }
                return !string.IsNullOrEmpty(filePath);
            }
            catch { filePath = null; line = 0; character = 0; return false; }
        }

        // ===========================================================================================
        // CodeGraph fallback (GitHub #40, ticket 2ba0ee17) — answer cross-project definition /
        // references / workspace-symbol from the .codegraph.db in C# when the LSP (shared OR bundled)
        // returns nothing. This is what lets us consume Mark's PURE upstream server.js (no CodeGraph
        // baked in) without losing CA's cross-project navigation: the addin merges in C#, LSP-first.
        //
        // Port of server.ts's codegraph-bridge call sites: same word extraction, same 1-based->0-based
        // line conversion, same "fallback only when the primary result is empty" semantics. Fallback
        // never overrides a real LSP answer, and every step is defensive (never throws — nav must not
        // break). It only does work when the LSP came back empty, so with today's codegraph-bearing
        // bundled server it effectively never fires; once we adopt pure v0.9.6 it supplies the gap.
        // ===========================================================================================

        /// <summary>Set at startup to return the active solution's .codegraph.db path. When null / no
        /// db, the fallback walks up from the request's file path instead. (Static-hook pattern, like
        /// <c>EmbeditorCompletionService.LspStarter</c>.)</summary>
        public static Func<string> CodeGraphDbPathProvider;

        // Clarion identifier under the cursor — mirrors server.ts getWordAtPosition.
        private static readonly Regex CgWordPattern = new Regex(@"[A-Za-z_][A-Za-z0-9_:.]*");

        /// <summary>True when a dispatcher result carries no usable payload (null, an error, or an
        /// empty result collection) — i.e. the LSP had no answer and we should try CodeGraph.</summary>
        private static bool IsEmptyResult(Dictionary<string, object> r)
        {
            if (r == null) return true;
            if (r.ContainsKey("error")) return true;   // LSP errored → a CodeGraph answer beats an error
            object res;
            if (!r.TryGetValue("result", out res) || res == null) return true;
            if (res is string) return false;
            var seq = res as System.Collections.IEnumerable;
            if (seq != null)
            {
                foreach (var _ in seq) return false;   // at least one element
                return true;                            // empty collection
            }
            return false;                               // non-null single object = a real answer
        }

        private static string ResolveCodeGraphDb(string filePathHint)
        {
            try
            {
                var hook = CodeGraphDbPathProvider;
                if (hook != null)
                {
                    string p = hook();
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                }
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(filePathHint))
                    return CodeGraphProvider.FindDatabase(Path.GetDirectoryName(filePathHint));
            }
            catch { }
            return null;
        }

        // Word under (0-based line, character). PREFERS the live buffer when supplied — the CA Embeditor's
        // _lspFileName is a SYNTHETIC .clw path with no file on disk, so a disk read there returns null and
        // the whole CodeGraph definition/hover fallback silently yields nothing (this is what made F12 do
        // nothing in the embeditor once we moved to the pure-upstream LSP, whose definition provider returns
        // empty for bare symbols → the fallback must carry it). Falls back to reading filePath from disk for
        // on-disk callers that pass no buffer. (task 37e2079f)
        private static string CgWordAt(string filePath, int line, int character, string bufferText = null)
        {
            try
            {
                string[] lines;
                if (!string.IsNullOrEmpty(bufferText))
                    lines = bufferText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                else if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    lines = File.ReadAllLines(filePath);
                else
                    return null;
                if (line < 0 || line >= lines.Length) return null;
                string text = lines[line];
                foreach (Match m in CgWordPattern.Matches(text))
                {
                    int start = m.Index, end = m.Index + m.Length;
                    if (character >= start && character <= end) return m.Value;
                }
            }
            catch { }
            return null;
        }

        // Build an LSP Location dict. DB line_number is 1-based (Clarion source line); LSP is 0-based.
        private static Dictionary<string, object> CgLocation(string filePath, int dbLine1Based)
        {
            int lspLine = dbLine1Based > 0 ? dbLine1Based - 1 : 0;
            var pos = new Dictionary<string, object> { { "line", lspLine }, { "character", 0 } };
            return new Dictionary<string, object>
            {
                { "uri", FilePathToUri(filePath) },
                { "range", new Dictionary<string, object> { { "start", pos }, { "end", pos } } }
            };
        }

        private static Dictionary<string, object> CodeGraphDefinition(string filePath, int line, int character, string bufferText)
        {
            try
            {
                // Member access first ("oInstance.Member" → the member's libsrc declaration), resolved from
                // the live buffer. F12 / Ctrl+Click on an ABC member jumps to where it's declared in libsrc.
                string mDb, mLabel;
                var mSym = ResolveMemberAccessSymbol(bufferText, filePath, line, character, out mDb, out mLabel);
                if (mSym != null && !string.IsNullOrEmpty(mSym.FilePath))
                    return WrapResult(new System.Collections.ArrayList { CgLocation(mSym.FilePath, mSym.LineNumber) });

                // Bare word (class name, equate). Project CodeGraph first (most specific), then ClarionGraph.
                string word = CgWordAt(filePath, line, character, bufferText);
                if (string.IsNullOrEmpty(word)) return null;
                return CgDefinitionFromDb(word, ResolveCodeGraphDb(filePath))
                    ?? CgDefinitionFromDb(word, ClarionGraphService.ResolveDbPath());
            }
            catch { return null; }
        }

        /// <summary>Resolve a member-access expression at the cursor ("oInstance.Member") to its CodeGraph
        /// symbol: take the dotted token, split at the last '.', resolve oInstance's class from the live
        /// buffer (reusing completion's type-inference), then look up "Class.Member" in the project CodeGraph
        /// then the ClarionGraph library DB. <paramref name="foundDb"/>/<paramref name="sourceLabel"/> name
        /// the DB that resolved it. Returns null when not a member-access context or unresolved. Needs the
        /// live buffer (member access references possibly-unsaved declarations). Never throws.</summary>
        private static CodeGraphSymbol ResolveMemberAccessSymbol(
            string bufferText, string filePath, int line, int character, out string foundDb, out string sourceLabel)
        {
            foundDb = null; sourceLabel = null;
            try
            {
                string[] lines = CgGetLines(bufferText, filePath);
                string lineText = (lines != null && line >= 0 && line < lines.Length)
                    ? lines[line] : CgLineAt(bufferText, filePath, line);
                if (string.IsNullOrEmpty(lineText)) return null;
                int col = character < 0 ? 0 : (character > lineText.Length ? lineText.Length : character);

                // The dotted token spanning the cursor (+ its position).
                Match tok = null;
                foreach (Match mm in CgWordPattern.Matches(lineText))
                    if (col >= mm.Index && col <= mm.Index + mm.Length) { tok = mm; break; }
                if (tok == null) return null;

                string token = tok.Value;
                int dot = token.LastIndexOf('.');
                if (dot <= 0 || dot >= token.Length - 1) return null;   // not "instance.member"
                // Cursor must be on the MEMBER side (after the dot). On the INSTANCE side ("loc:wm"), this is
                // NOT member access — return null so the LSP resolves the instance's own declaration/hover.
                if (col <= tok.Index + dot) return null;
                string instance = token.Substring(0, dot);
                string member = token.Substring(dot + 1);
                if (instance.IndexOf('.') >= 0) return null;            // multi-level chain — single-level only

                bool inlineIgnored;
                string className = ResolveInstanceType(lines, line, instance, filePath, out inlineIgnored);
                if (string.IsNullOrEmpty(className)) return null;

                string fullName = className + "." + member;
                string[] dbs = { ResolveCodeGraphDb(filePath), ClarionGraphService.ResolveDbPath() };
                string[] labels = { "CodeGraph", "ClarionGraph" };
                for (int k = 0; k < dbs.Length; k++)
                {
                    string d = dbs[k];
                    if (string.IsNullOrEmpty(d) || !File.Exists(d)) continue;
                    using (var p = new CodeGraphProvider())
                        if (p.Open(d))
                        {
                            var sym = p.FindSymbolByName(fullName);
                            if (sym != null) { foundDb = d; sourceLabel = labels[k]; return sym; }
                        }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Resolve an exact-name definition from one CodeGraph-schema DB → an LSP location list,
        /// or null when the DB is missing/unopenable or the symbol isn't found. Never throws.</summary>
        private static Dictionary<string, object> CgDefinitionFromDb(string word, string db)
        {
            try
            {
                if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(db) || !File.Exists(db)) return null;
                using (var p = new CodeGraphProvider())
                {
                    if (!p.Open(db)) return null;
                    var def = p.GetDefinition(word);
                    if (def == null || string.IsNullOrEmpty(def.FilePath)) return null;
                    return WrapResult(new System.Collections.ArrayList { CgLocation(def.FilePath, def.LineNumber) });
                }
            }
            catch { return null; }
        }

        /// <summary>True when an LSP hover result has no usable contents (null, error, empty collection, or a
        /// blank/whitespace contents string) — the signal to try the CodeGraph/ClarionGraph hover fallback.</summary>
        private static bool IsHoverEmpty(Dictionary<string, object> r)
        {
            if (IsEmptyResult(r)) return true;
            try
            {
                object res;
                if (r.TryGetValue("result", out res) && res is System.Collections.IDictionary)
                {
                    var d = (System.Collections.IDictionary)res;
                    object cont = d.Contains("contents") ? d["contents"] : null;
                    // 'contents' may be a string, a MarkupContent/MarkedString dict ({value:...}), or a list
                    // of those — extract the actual text and whitespace-check THAT (a {kind,value:""} dict
                    // would otherwise stringify to its type name and read as non-empty, suppressing fallback).
                    if (string.IsNullOrWhiteSpace(HoverContentsText(cont))) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Best-effort extraction of the displayable text from an LSP hover "contents" value (plain
        /// string, MarkupContent/MarkedString dict {value:...}, or a list of those). Empty/null when there's
        /// no real content — the signal that the hover is empty and the CodeGraph fallback should fire.</summary>
        private static string HoverContentsText(object contents)
        {
            if (contents == null) return null;
            if (contents is string) return (string)contents;
            var dict = contents as System.Collections.IDictionary;
            if (dict != null)
            {
                object v = dict.Contains("value") ? dict["value"]
                         : (dict.Contains("contents") ? dict["contents"] : null);
                return v == null ? null : HoverContentsText(v);
            }
            var seq = contents as System.Collections.IEnumerable;
            if (seq != null)
            {
                string acc = "";
                foreach (var item in seq) acc += HoverContentsText(item);
                return acc;
            }
            return null;   // unknown shape → treat as empty (fallback fires; LSP hover still kept if no DB hit)
        }

        /// <summary>Hover fallback: build hover contents for the word under the cursor from the project
        /// CodeGraph, then the ClarionGraph library DB (ABC/library symbols). Null when neither resolves.</summary>
        private static Dictionary<string, object> CodeGraphHover(string filePath, int line, int character, string bufferText)
        {
            try
            {
                // Member access first ("oInstance.Member" → that member's signature), resolved via the buffer.
                string mDb, mLabel;
                var mSym = ResolveMemberAccessSymbol(bufferText, filePath, line, character, out mDb, out mLabel);
                if (mSym != null)
                {
                    string c = CgHoverText(mSym, mLabel);
                    if (!string.IsNullOrEmpty(c))
                        return WrapResult(new Dictionary<string, object> { { "contents", c } });
                }

                // Bare word (class name, equate) — exact-name lookup.
                string word = CgWordAt(filePath, line, character, bufferText);
                if (string.IsNullOrEmpty(word)) return null;
                var hov = CgHoverFromDb(word, ResolveCodeGraphDb(filePath), "CodeGraph")
                    ?? CgHoverFromDb(word, ClarionGraphService.ResolveDbPath(), "ClarionGraph");
                if (hov != null) return hov;
                // Template-generated ABC globals (GlobalRequest/Response, VCRRequest, GlobalErrors …) live in
                // no libsrc file, so no DB has them — resolve their hover from the curated built-in list. This
                // also covers the request equates before the ABFILE.EQU rebuild lands. (task 37e2079f, CC probe)
                return AbcGlobalHover(word);
            }
            catch { return null; }
        }

        /// <summary>Hover for a well-known ABC standard global/equate (GlobalRequest, GlobalResponse,
        /// VCRRequest, RequestCancelled …) that is template-generated or otherwise absent from every indexed
        /// DB. Returns null when the word isn't one of them. Markdown mirrors CgHoverText. (task 37e2079f)</summary>
        private static Dictionary<string, object> AbcGlobalHover(string word)
        {
            try
            {
                var g = ClarionCodeGraph.Parsing.ClarionBuiltins.AbcStandardGlobalExact(word);
                if (g == null) return null;
                string contents = "```clarion\n" + g.Name + "\n```\n\n" + g.Detail + " · ABC";
                return WrapResult(new Dictionary<string, object> { { "contents", contents } });
            }
            catch { return null; }
        }

        /// <summary>Exact-name hover from one CodeGraph-schema DB → an LSP hover dict ({contents}), or null
        /// when the DB is missing/unopenable or the symbol isn't found. <paramref name="sourceLabel"/> names
        /// the DB (e.g. "ClarionGraph") for the detail line when the symbol has no project name. Never throws.</summary>
        private static Dictionary<string, object> CgHoverFromDb(string word, string db, string sourceLabel)
        {
            try
            {
                if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(db) || !File.Exists(db)) return null;
                using (var p = new CodeGraphProvider())
                {
                    if (!p.Open(db)) return null;
                    var sym = p.FindSymbolByName(word);
                    if (sym == null) return null;
                    string contents = CgHoverText(sym, sourceLabel);
                    if (string.IsNullOrEmpty(contents)) return null;
                    return WrapResult(new Dictionary<string, object> { { "contents", contents } });
                }
            }
            catch { return null; }
        }

        /// <summary>Markdown hover text for a CodeGraph symbol: a fenced signature line (name + params +
        /// return type) followed by a detail line (type · member-of-parent · source). The source is the
        /// symbol's project name when set, else <paramref name="sourceLabel"/>. Null when empty.</summary>
        private static string CgHoverText(CodeGraphSymbol s, string sourceLabel)
        {
            if (s == null || string.IsNullOrEmpty(s.Name)) return null;

            string sig = s.Name;
            bool isData = string.Equals(s.Type, "variable", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(s.Params))
            {
                if (isData) sig += "  " + s.Params;   // data member: "Class.Field  BYTE" (Params holds the type)
                else sig += s.Params.TrimStart().StartsWith("(") ? s.Params : "(" + s.Params + ")";  // method
            }
            if (!string.IsNullOrEmpty(s.ReturnType)) sig += " → " + s.ReturnType;

            var bits = new List<string>();
            if (!string.IsNullOrEmpty(s.Type)) bits.Add(s.Type);
            if (!string.IsNullOrEmpty(s.ParentName)) bits.Add("member of " + s.ParentName);
            bits.Add(!string.IsNullOrEmpty(s.ProjectName) ? s.ProjectName : sourceLabel);

            return "```clarion\n" + sig + "\n```\n\n" + string.Join(" · ", bits);
        }

        private static Dictionary<string, object> CodeGraphReferences(string filePath, int line, int character)
        {
            try
            {
                string word = CgWordAt(filePath, line, character);
                if (string.IsNullOrEmpty(word)) return null;
                string db = ResolveCodeGraphDb(filePath);
                if (string.IsNullOrEmpty(db)) return null;
                using (var p = new CodeGraphProvider())
                {
                    if (!p.Open(db)) return null;
                    var refs = p.GetReferences(word);
                    if (refs == null || refs.Count == 0) return null;
                    var list = new System.Collections.ArrayList();
                    foreach (var r in refs) list.Add(CgLocation(r.FilePath, r.LineNumber));
                    return WrapResult(list);
                }
            }
            catch { return null; }
        }

        private static Dictionary<string, object> CodeGraphWorkspaceSymbol(string query)
        {
            try
            {
                if (string.IsNullOrEmpty(query)) return null;
                string db = ResolveCodeGraphDb(null);
                if (string.IsNullOrEmpty(db)) return null;
                using (var p = new CodeGraphProvider())
                {
                    if (!p.Open(db)) return null;
                    var syms = p.FindSymbols(query, 100);
                    if (syms == null || syms.Count == 0) return null;
                    var list = new System.Collections.ArrayList();
                    foreach (var s in syms)
                    {
                        var sym = new Dictionary<string, object>
                        {
                            { "name", s.Name },
                            { "kind", CgSymbolKind(s.Type) },
                            { "location", CgLocation(s.FilePath, s.LineNumber) }
                        };
                        if (!string.IsNullOrEmpty(s.ParentName)) sym["containerName"] = s.ParentName;
                        list.Add(sym);
                    }
                    return WrapResult(list);
                }
            }
            catch { return null; }
        }

        // CodeGraph symbol type → LSP SymbolKind int (icon only; approximate is fine).
        private static int CgSymbolKind(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "class": return 5;       // Class
                case "interface": return 11;  // Interface
                case "procedure": return 12;  // Function
                case "function": return 12;   // Function
                case "routine": return 6;     // Method
                case "variable": return 13;   // Variable
                default: return 13;           // Variable (reasonable default)
            }
        }

        // === CodeGraph prefix completion (task a47a6cac Phase 1) ===
        // Mark's pure upstream server completes members only (after '.'); these helpers merge global
        // symbols from the .codegraph.db into a BARE-PREFIX completion so typing the first letters of a
        // symbol + Ctrl+Space completes it. Member-access / qualified contexts are left LSP-only.

        // Identifier prefix immediately left of the cursor.
        private static readonly Regex CgPrefixPattern = new Regex(@"[A-Za-z_][A-Za-z0-9_]*$");

        // Min prefix length before hitting the CodeGraph — avoids dumping the symbol table on 1 keystroke.
        private const int CgCompletionMinPrefix = 2;

        /// <summary>Merge bare-prefix completions into <paramref name="primary"/> when the cursor is in a
        /// bare-prefix context: ABC standard globals (task a47a6cac built-ins) + CodeGraph global symbols.
        /// Member-access / qualified contexts are left LSP-only. Mutates the list in place. Never throws.</summary>
        private static void MergeBarePrefixCompletions(
            List<LspClient.CompletionItemInfo> primary, string filePath, int line, int character, string bufferText)
        {
            string lineText = CgLineAt(bufferText, filePath, line);
            if (lineText == null) return;

            int col = character < 0 ? 0 : (character > lineText.Length ? lineText.Length : character);
            string upToCursor = lineText.Substring(0, col);

            // In a Clarion comment ('!') → no completion.
            if (upToCursor.IndexOf('!') >= 0) return;
            // Member-access ('.' immediately before cursor, ignoring trailing spaces) → LSP-only.
            if (upToCursor.TrimEnd().EndsWith(".")) return;

            var m = CgPrefixPattern.Match(upToCursor);
            if (!m.Success) return;
            string prefix = m.Value;
            if (prefix.Length < CgCompletionMinPrefix) return;
            // Qualified access (Class.Member, PROP:/EVENT:/prefixed field) → LSP-only, no global noise.
            if (m.Index > 0) { char before = upToCursor[m.Index - 1]; if (before == '.' || before == ':') return; }

            // Labels already returned by the LSP — don't double-list (case-insensitive).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in primary)
                if (it != null && !string.IsNullOrEmpty(it.Label)) seen.Add(it.Label);

            string[] lines = CgGetLines(bufferText, filePath);

            // (1) Local variables (depth-aware) from the enclosing routine + procedure DATA. Never in the
            // CodeGraph (not cross-project) + must reflect unsaved edits, so parse the live buffer. Phase 2.
            if (lines != null) MergeLocalVarCompletions(primary, seen, prefix, lines, line);

            // (2) No-PRE group/queue FIELDS in scope (bare-accessible). PRE'd group fields require their
            // prefix and are offered via the ':' path (MergeQualifiedFieldCompletions) instead.
            if (lines != null)
                foreach (var s in ParseScopeStructures(lines, GetScopeDataRanges(lines, line)))
                {
                    if (s.Pre != null) continue;
                    foreach (var f in s.Fields)
                    {
                        if (!f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(f.Name)) continue;
                        primary.Add(new LspClient.CompletionItemInfo
                        {
                            Label = f.Name, Kind = 5 /*Field*/,
                            Detail = string.IsNullOrEmpty(f.Type) ? "(field)" : f.Type + "  (field)",
                            InsertText = f.Name
                        });
                    }
                }

            // (3) ABC standard globals / request-response equates. These are ABC-template-generated, not
            // user-declared (CodeGraph never indexes them) and not language built-ins — so they need this
            // curated source. No DB required, so this fires even when no .codegraph.db is present.
            foreach (var b in ClarionBuiltins.AbcStandardGlobalsByPrefix(prefix))
            {
                if (!seen.Add(b.Name)) continue;
                primary.Add(new LspClient.CompletionItemInfo
                {
                    Label = b.Name,
                    Kind = b.Kind,
                    Detail = b.Detail,
                    InsertText = b.Name
                });
            }

            // (4) CodeGraph global symbols (procedures/functions/classes/vars) — project .codegraph.db.
            MergeDbBarePrefix(primary, seen, prefix, ResolveCodeGraphDb(filePath), bareNamesOnly: false);

            // (5) ClarionGraph static LIBRARY symbols (ABC + library classes, equates) — version-keyed
            // cache (ticket 6e8f2439). Bare-prefix offers class/interface NAMES + equates; ClassName.Method
            // entries are skipped here (they belong to member-access completion). No-op until the version
            // DB is built. Additive + defensive: only ADDS, never overrides an LSP item.
            MergeDbBarePrefix(primary, seen, prefix, ClarionGraphService.ResolveDbPath(), bareNamesOnly: true);
        }

        /// <summary>
        /// Merge true-prefix global symbols from a CodeGraph-schema DB into the bare-prefix completion
        /// list. <paramref name="bareNamesOnly"/> skips dotted ClassName.Method entries (used for the
        /// ClarionGraph library DB, whose methods belong to member-access, not bare-prefix). Dedupes via
        /// <paramref name="seen"/>; no-op when the DB is missing/unopenable. Never throws.
        /// </summary>
        private static void MergeDbBarePrefix(
            List<LspClient.CompletionItemInfo> primary, HashSet<string> seen, string prefix,
            string db, bool bareNamesOnly)
        {
            try
            {
                if (string.IsNullOrEmpty(db) || !File.Exists(db)) return;
                using (var p = new CodeGraphProvider())
                {
                    if (!p.Open(db)) return;
                    var syms = p.FindSymbols(prefix, 100);   // substring match, prefix-ordered first
                    if (syms == null) return;
                    foreach (var s in syms)
                    {
                        if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                        if (!s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue; // true prefix only
                        if (bareNamesOnly && s.Name.IndexOf('.') >= 0) continue; // ClassName.Method → member-access only
                        if (!seen.Add(s.Name)) continue;
                        primary.Add(new LspClient.CompletionItemInfo
                        {
                            Label = s.Name,
                            Kind = CgCompletionKind(s.Type),
                            Detail = CgCompletionDetail(s),
                            InsertText = s.Name
                        });
                    }
                }
            }
            catch { }
        }

        private static string CgLineAt(string bufferText, string filePath, int line)
        {
            try
            {
                if (line < 0) return null;
                if (!string.IsNullOrEmpty(bufferText))
                {
                    var arr = bufferText.Split('\n');
                    return line < arr.Length ? arr[line].TrimEnd('\r') : null;
                }
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    var lines = File.ReadAllLines(filePath);
                    if (line < lines.Length) return lines[line];
                }
            }
            catch { }
            return null;
        }

        // CodeGraph symbol type → LSP CompletionItemKind int (Monaco icon).
        private static int CgCompletionKind(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "class": return 7;       // Class
                case "interface": return 8;   // Interface
                case "procedure": return 3;   // Function
                case "function": return 3;    // Function
                case "routine": return 2;     // Method
                case "variable": return 6;    // Variable
                default: return 6;            // Variable
            }
        }

        private static string CgCompletionDetail(CodeGraphSymbol s)
        {
            string t = string.IsNullOrEmpty(s.Type) ? "" : s.Type;
            if (!string.IsNullOrEmpty(s.ReturnType)) t += " : " + s.ReturnType;
            if (!string.IsNullOrEmpty(s.ProjectName)) t += "  (" + s.ProjectName + ")";
            return string.IsNullOrEmpty(t) ? null : t;
        }

        // === Local-variable prefix completion (task a47a6cac Phase 2) ===
        // Procedure header: a column-1 label followed by the PROCEDURE keyword (e.g. "ThisWindow.Init PROCEDURE").
        private static readonly Regex CgProcHeaderPattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_.:]*\s+PROCEDURE\b", RegexOptions.IgnoreCase);
        // Routine header: a column-1 label followed by the ROUTINE keyword (e.g. "TestRoutine ROUTINE").
        private static readonly Regex CgRoutineHeaderPattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_.:]*\s+ROUTINE\b", RegexOptions.IgnoreCase);
        // The CODE statement that ends a procedure's DATA section.
        private static readonly Regex CgCodeLinePattern = new Regex(@"^\s*CODE\b", RegexOptions.IgnoreCase);
        // A data declaration: a column-1 label (group 1) followed by its type/rest-of-line (group 2).
        private static readonly Regex CgDataLabelPattern = new Regex(@"^([A-Za-z_][A-Za-z0-9_:]*)\s+(\S.*)$");
        // Trailing Clarion line comment (' ! ...') — stripped from the type shown in the detail column.
        private static readonly Regex CgTrailingComment = new Regex(@"\s+!.*$");

        /// <summary>Phase 2: merge in-scope local variables from the live buffer. Two scopes, most-specific
        /// first: (a) the enclosing ROUTINE's private DATA (visible only inside that routine), then (b) the
        /// enclosing PROCEDURE's main DATA (visible everywhere in the proc, including its routines). Parsed
        /// from the live buffer so it reflects unsaved edits. Never throws.</summary>
        private static void MergeLocalVarCompletions(
            List<LspClient.CompletionItemInfo> primary, HashSet<string> seen, string prefix,
            string[] lines, int line)
        {
            if (lines == null || line < 0 || line >= lines.Length) return;
            int from = Math.Min(line, lines.Length - 1);

            // (a) Enclosing ROUTINE (most specific). Scanning up, an enclosing routine is one whose header
            // we reach BEFORE any PROCEDURE header — otherwise the cursor is in the procedure's main body.
            // Added first so a routine-private var shadows a same-named procedure local (via `seen`).
            for (int i = from; i >= 0; i--)
            {
                if (CgRoutineHeaderPattern.IsMatch(lines[i])) { CollectDataLabels(lines, i, prefix, seen, primary, "(routine var)"); break; }
                if (CgProcHeaderPattern.IsMatch(lines[i])) break;   // in proc main body → no enclosing routine
            }

            // (b) Enclosing PROCEDURE main locals — in scope everywhere in the proc, incl. its routines.
            for (int i = from; i >= 0; i--)
                if (CgProcHeaderPattern.IsMatch(lines[i])) { CollectDataLabels(lines, i, prefix, seen, primary, "(local)"); break; }
        }

        /// <summary>Collect column-1 data declarations from <paramref name="headerIdx"/>+1 down to the next
        /// CODE statement (the end of that PROCEDURE/ROUTINE's DATA section), offering labels that match
        /// <paramref name="prefix"/> and aren't already in <paramref name="seen"/>. Shows the declared type
        /// in the detail column with <paramref name="scopeMarker"/>.</summary>
        private static void CollectDataLabels(
            string[] lines, int headerIdx, string prefix, HashSet<string> seen,
            List<LspClient.CompletionItemInfo> primary, string scopeMarker)
        {
            int depth = 0;   // GROUP/QUEUE nesting — fields inside structures are NOT plain locals
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (CgCodeLinePattern.IsMatch(ln)) break;                                   // end of DATA section
                if (CgProcHeaderPattern.IsMatch(ln) || CgRoutineHeaderPattern.IsMatch(ln)) break;  // next proc/routine
                bool isEnd = CgEndLine.IsMatch(ln) || CgPeriodEnd.IsMatch(ln);
                // Emit only depth-0 declarations: plain locals + the GROUP/QUEUE container's own label.
                if (depth == 0 && !isEnd)
                {
                    var lm = CgDataLabelPattern.Match(ln);
                    if (lm.Success)
                    {
                        string label = lm.Groups[1].Value;
                        if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(label))
                        {
                            // Show the declared type (e.g. "STRING(12)", "LONG", "GROUP") in the detail
                            // column, stripping any trailing line comment. Fall back to the scope marker.
                            string typeText = CgTrailingComment.Replace(lm.Groups[2].Value, "").Trim();
                            string detail = string.IsNullOrEmpty(typeText) ? scopeMarker : typeText + "  " + scopeMarker;
                            primary.Add(new LspClient.CompletionItemInfo
                            { Label = label, Kind = 6 /*Variable*/, Detail = detail, InsertText = label });
                        }
                    }
                }
                if (CgGroupQueueOpen.IsMatch(ln)) depth++;
                else if (isEnd && depth > 0) depth--;
            }
        }

        // === Group/Queue FIELD completion (task a47a6cac Phase 2 refinement) ===
        // Parses GROUP/QUEUE structures (with PRE() + nesting) from the in-scope DATA sections, then offers
        // their fields via PRE prefix ("Cus:"), dotted access ("Group."), and bare labels (no-PRE groups, in
        // MergeBarePrefixCompletions). Proc + routine scope for now (module-level + global are follow-ups).

        private static readonly Regex CgGroupQueueOpen = new Regex(@"^([A-Za-z_][A-Za-z0-9_:]*)\s+(GROUP|QUEUE)\b(.*)$", RegexOptions.IgnoreCase);
        private static readonly Regex CgEndLine   = new Regex(@"^\s*END\b", RegexOptions.IgnoreCase);
        private static readonly Regex CgPeriodEnd = new Regex(@"^\s*\.\s*$");
        private static readonly Regex CgPreAttr   = new Regex(@",\s*PRE\(\s*([A-Za-z_][A-Za-z0-9_]*)?\s*\)", RegexOptions.IgnoreCase);
        // Qualifier immediately before the cursor: <identifier><':' or '.'><partial>. The identifier may
        // contain ':' so a colon-named container (template queues like "Queue:Browse:1") is matched for
        // dotted access — mirrors CgGroupQueueOpen / CgMemberAccess / CgDataLabelPattern, which all allow ':'.
        // Greedy backtracking keeps PRE ("Cus:Name"→"Cus") and plain-dotted ("Group."→"Group") intact.
        private static readonly Regex CgQualifier = new Regex(@"([A-Za-z_][A-Za-z0-9_:]*)([:.])([A-Za-z0-9_]*)$");

        private sealed class CgStructField { public string Name; public string Type; }
        private sealed class CgStruct { public string Name; public string Pre; public readonly List<CgStructField> Fields = new List<CgStructField>(); }

        /// <summary>Full buffer (live text preferred, else disk) split into lines, or null.</summary>
        private static string[] CgGetLines(string bufferText, string filePath)
        {
            if (!string.IsNullOrEmpty(bufferText)) return bufferText.Replace("\r\n", "\n").Split('\n');
            try { if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) return File.ReadAllLines(filePath); } catch { }
            return null;
        }

        /// <summary>The enclosing routine + procedure DATA-section line ranges [start, endExclusive).</summary>
        private static List<int[]> GetScopeDataRanges(string[] lines, int line)
        {
            var ranges = new List<int[]>();
            int from = Math.Min(line, lines.Length - 1);
            for (int i = from; i >= 0; i--)   // enclosing routine (only if reached before any procedure header)
            {
                if (CgRoutineHeaderPattern.IsMatch(lines[i])) { ranges.Add(new[] { i + 1, FindCodeAfter(lines, i) }); break; }
                if (CgProcHeaderPattern.IsMatch(lines[i])) break;
            }
            for (int i = from; i >= 0; i--)   // enclosing procedure
                if (CgProcHeaderPattern.IsMatch(lines[i])) { ranges.Add(new[] { i + 1, FindCodeAfter(lines, i) }); break; }
            return ranges;
        }

        // End a DATA region at its CODE statement, OR at the next procedure/routine header — the latter
        // matters because a routine without its own DATA/CODE (or one being edited) has no CODE of its own,
        // and without the routine-header stop its range would over-extend into the NEXT routine's DATA and
        // leak that routine's groups/fields out of scope.
        private static int FindCodeAfter(string[] lines, int headerIdx)
        {
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                if (CgCodeLinePattern.IsMatch(lines[i])) return i;
                if (CgProcHeaderPattern.IsMatch(lines[i]) || CgRoutineHeaderPattern.IsMatch(lines[i])) return i;
            }
            return lines.Length;
        }

        /// <summary>Parse GROUP/QUEUE structures (nesting + PRE inheritance) from the given line ranges.</summary>
        private static List<CgStruct> ParseScopeStructures(string[] lines, List<int[]> ranges)
        {
            var all = new List<CgStruct>();
            foreach (var rg in ranges)
            {
                var stack = new List<CgStruct>();
                for (int i = rg[0]; i < rg[1] && i < lines.Length; i++)
                {
                    string ln = lines[i];
                    var gq = CgGroupQueueOpen.Match(ln);
                    if (gq.Success)
                    {
                        string pre = CgExtractPre(gq.Groups[3].Value)
                                     ?? (stack.Count > 0 ? stack[stack.Count - 1].Pre : null);
                        var s = new CgStruct { Name = gq.Groups[1].Value, Pre = pre };
                        if (stack.Count > 0)   // a nested group is also a field of its parent
                            stack[stack.Count - 1].Fields.Add(new CgStructField { Name = s.Name, Type = gq.Groups[2].Value });
                        all.Add(s);
                        stack.Add(s);
                        continue;
                    }
                    if (stack.Count > 0 && (CgEndLine.IsMatch(ln) || CgPeriodEnd.IsMatch(ln)))
                    {
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }
                    if (stack.Count > 0)
                    {
                        var fm = CgDataLabelPattern.Match(ln);
                        if (fm.Success)
                            stack[stack.Count - 1].Fields.Add(new CgStructField
                            {
                                Name = fm.Groups[1].Value,
                                Type = CgTrailingComment.Replace(fm.Groups[2].Value, "").Trim()
                            });
                    }
                }
            }
            return all;
        }

        private static string CgExtractPre(string attrs)
        {
            var m = CgPreAttr.Match(attrs ?? "");
            return (m.Success && m.Groups[1].Success && m.Groups[1].Value.Length > 0) ? m.Groups[1].Value : null;
        }

        /// <summary>Group/queue FIELD completion for qualified contexts: PRE prefix ("Cus:partial" → fields
        /// of GROUP,PRE(Cus)) and dotted access ("Group.partial" → direct fields). Only injects for groups/
        /// queues visible in scope — class member-access ('.') stays the LSP's job. Never throws.</summary>
        private static void MergeQualifiedFieldCompletions(
            List<LspClient.CompletionItemInfo> primary, string filePath, int line, int character, string bufferText)
        {
            string lineText = CgLineAt(bufferText, filePath, line);
            if (lineText == null) return;
            int col = character < 0 ? 0 : (character > lineText.Length ? lineText.Length : character);
            string upToCursor = lineText.Substring(0, col);
            if (upToCursor.IndexOf('!') >= 0) return;   // comment

            var q = CgQualifier.Match(upToCursor);
            if (!q.Success) return;                       // not a "<ident>:partial" / "<ident>.partial" context
            string qualifier = q.Groups[1].Value;
            char sep = q.Groups[2].Value[0];
            string partial = q.Groups[3].Value;

            string[] lines = CgGetLines(bufferText, filePath);
            if (lines == null) return;
            var structs = ParseScopeStructures(lines, GetScopeDataRanges(lines, line));
            if (structs.Count == 0) return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in primary)
                if (it != null && !string.IsNullOrEmpty(it.Label)) seen.Add(it.Label);

            foreach (var s in structs)
            {
                bool match = sep == ':'
                    ? (s.Pre != null && string.Equals(s.Pre, qualifier, StringComparison.OrdinalIgnoreCase))
                    : string.Equals(s.Name, qualifier, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;

                foreach (var f in s.Fields)
                {
                    if (partial.Length > 0 && !f.Name.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                    // PRE label shows the full "Cus:Field"; insert just the field name (the range breaks on
                    // ':'/'.', so the qualifier + separator already typed stays put).
                    string label = sep == ':' ? qualifier + ":" + f.Name : f.Name;
                    if (!seen.Add(label)) continue;
                    primary.Add(new LspClient.CompletionItemInfo
                    {
                        Label = label, Kind = 5 /*Field*/,
                        Detail = string.IsNullOrEmpty(f.Type) ? "(field)" : f.Type + "  (field)",
                        InsertText = f.Name
                    });
                }
            }
        }

        // === Class member-access completion (ticket 6e8f2439, item 5b) ===
        // "oInstance." (optionally "oInstance.partial") → the methods/properties of oInstance's declared
        // CLASS, sourced from ClarionGraph (ABC + library classes) and the project CodeGraph (parent_name).
        // SUPPLEMENTS Mark's LSP, which resolves project-local member access but may not index libsrc/ABC.

        // "<identifier>.<partial>" at end of line. The instance label may contain ':' (e.g. Access:Customer).
        private static readonly Regex CgMemberAccess =
            new Regex(@"([A-Za-z_][A-Za-z0-9_:]*)\.([A-Za-z0-9_]*)$");
        // "CLASS(Parent)" — the instance is a derived class; member access resolves to the parent's members.
        private static readonly Regex CgClassParen =
            new Regex(@"^\s*CLASS\s*\(\s*([A-Za-z_][A-Za-z0-9_:]*)\s*\)", RegexOptions.IgnoreCase);
        // Leading type token in a declaration's rest-of-line, stripping an optional reference '&'.
        private static readonly Regex CgTypeToken =
            new Regex(@"^\s*&?\s*([A-Za-z_][A-Za-z0-9_:]*)");

        /// <summary>Member-access completion: when the cursor sits after "oInstance." resolve the instance's
        /// declared class and offer that class's methods from ClarionGraph + the project CodeGraph. For a
        /// GROUP/QUEUE in scope it adds nothing (field access is owned by MergeQualifiedFieldCompletions) but
        /// still returns that struct's field names. Additive for the class case: only ADDS members (deduped
        /// against the LSP's items), never blanks the list, never throws. Returns the owner's full member/
        /// field name set (case-insensitive) for the caller's scoping pass, or null when not a resolvable
        /// member/field-access context.</summary>
        private static HashSet<string> MergeMemberAccessCompletions(
            List<LspClient.CompletionItemInfo> primary, string filePath, int line, int character, string bufferText)
        {
            string lineText = CgLineAt(bufferText, filePath, line);
            if (lineText == null) return null;
            int col = character < 0 ? 0 : (character > lineText.Length ? lineText.Length : character);
            string upToCursor = lineText.Substring(0, col);
            if (upToCursor.IndexOf('!') >= 0) return null;   // comment

            var m = CgMemberAccess.Match(upToCursor);
            if (!m.Success) return null;
            // Skip multi-level chains ("a.b.") for now — single-level member access only (follow-up).
            if (m.Index > 0 && upToCursor[m.Index - 1] == '.') return null;
            string instance = m.Groups[1].Value;
            string partial  = m.Groups[2].Value;

            string[] lines = CgGetLines(bufferText, filePath);

            // GROUP/QUEUE in scope → field access (MergeQualifiedFieldCompletions adds the fields). Return its
            // field-name set so the scoping pass keeps the fields and drops the LSP keyword dump.
            if (lines != null)
                foreach (var s in ParseScopeStructures(lines, GetScopeDataRanges(lines, line)))
                    if (string.Equals(s.Name, instance, StringComparison.OrdinalIgnoreCase))
                    {
                        var fset = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in s.Fields)
                            if (f != null && !string.IsNullOrEmpty(f.Name)) fset.Add(f.Name);
                        return fset.Count > 0 ? fset : null;
                    }

            bool isInlineClass;
            string className = ResolveInstanceType(lines, line, instance, filePath, out isInlineClass);
            if (string.IsNullOrEmpty(className)) return null;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in primary)
                if (it != null && !string.IsNullOrEmpty(it.Label)) seen.Add(it.Label);

            // Add members from the project CodeGraph (most specific) then the ClarionGraph library DB; dedupe
            // across both. Collect them (unfiltered by partial) into two sets for the scoping decision below.
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // project-DB members
            var libMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // library-DB members only
            MergeDbMembers(primary, seen, className, partial, ResolveCodeGraphDb(filePath), added);
            MergeDbMembers(primary, seen, className, partial, ClarionGraphService.ResolveDbPath(), libMembers);
            added.UnionWith(libMembers);   // full set of members WE know about (so our additions survive scoping)

            // SCOPE ONLY for a DIRECT instance of a library/ABC class (review HIGH + MEDIUM, 3 gates). Rationale:
            // the LSP keyword-dump leak we're suppressing only happens for library types the LSP can't resolve
            // server-side (libsrc isn't indexed) — there ARE no real LSP members to protect there, so scoping
            // to our authoritative library member set is safe. For an inline "CLASS(Parent)" declaration
            // (project-local derived class) or a purely project-local type, the LSP resolves member access
            // itself — including derived-own methods and just-added members a stale .codegraph.db lacks — so
            // scoping would silently DROP those valid completions. Leave those to the LSP (return null).
            bool libraryBacked = libMembers.Count > 0;
            return (!isInlineClass && libraryBacked && added.Count > 0) ? added : null;
        }

        /// <summary>Resolve the declared CLASS type of an instance so member-access knows which class's
        /// methods to offer. (1) Scan the live buffer for a column-1 declaration "instance &Type" /
        /// "instance Type" / "instance CLASS(Parent)", nearest above the cursor first, then anywhere.
        /// (2) Fall back to the project CodeGraph — the variable's declared type lives in its params/return
        /// (e.g. "&FILEMANAGER"), which covers globally-declared ABC objects absent from the buffer. Returns
        /// the bare class name (no '&'), or null. <paramref name="isInlineClass"/> is true when the type was
        /// an inline "CLASS(Parent)" declaration (project-local derived class — caller must not scope). Never
        /// throws.</summary>
        private static string ResolveInstanceType(string[] lines, int line, string instance, string filePath, out bool isInlineClass)
        {
            isInlineClass = false;
            try
            {
                if (lines != null && lines.Length > 0)
                {
                    int from = Math.Max(0, Math.Min(line, lines.Length - 1));   // clamp (line may be <0)
                    for (int i = from; i >= 0; i--)
                    {
                        string t = TypeFromDecl(lines[i], instance, out isInlineClass);
                        if (t != null) return t;
                    }
                    for (int i = from + 1; i < lines.Length; i++)   // declarations below the cursor
                    {
                        string t = TypeFromDecl(lines[i], instance, out isInlineClass);
                        if (t != null) return t;
                    }
                    isInlineClass = false;   // reset: no buffer decl matched
                }

                string db = ResolveCodeGraphDb(filePath);
                if (!string.IsNullOrEmpty(db) && File.Exists(db))
                    using (var p = new CodeGraphProvider())
                        if (p.Open(db))
                        {
                            var sym = p.FindSymbolByName(instance);
                            if (sym != null)
                            {
                                // A global object var's type ref (e.g. "&FILEMANAGER") — a DIRECT reference,
                                // not an inline class, so leave isInlineClass false.
                                bool _ignore;
                                string t = ClassTypeFromText(sym.Params, out _ignore);
                                if (t == null) t = ClassTypeFromText(sym.ReturnType, out _ignore);
                                if (t != null) return t;
                            }
                        }
            }
            catch { }
            return null;
        }

        /// <summary>If <paramref name="lineText"/> is a column-1 declaration of <paramref name="instance"/>,
        /// return the class name from its type (else null). <paramref name="isInlineClass"/> is set true when
        /// the type is an inline "CLASS(Parent)..." form (a project-local derived class) — the caller must NOT
        /// scope those (the LSP resolves their own + inherited members; scoping would drop the own ones).</summary>
        private static string TypeFromDecl(string lineText, string instance, out bool isInlineClass)
        {
            isInlineClass = false;
            if (string.IsNullOrEmpty(lineText)) return null;
            var m = CgDataLabelPattern.Match(lineText);   // ^(label)\s+(rest)
            if (!m.Success) return null;
            if (!string.Equals(m.Groups[1].Value, instance, StringComparison.OrdinalIgnoreCase)) return null;
            return ClassTypeFromText(m.Groups[2].Value, out isInlineClass);
        }

        /// <summary>Extract a class name from a declaration's type text: "CLASS(Parent)" → Parent (and sets
        /// <paramref name="isInlineClass"/>=true); otherwise the leading identifier with an optional reference
        /// '&' stripped. Null when none.</summary>
        private static string ClassTypeFromText(string typeText, out bool isInlineClass)
        {
            isInlineClass = false;
            if (string.IsNullOrEmpty(typeText)) return null;
            var cp = CgClassParen.Match(typeText);
            if (cp.Success) { isInlineClass = true; return cp.Groups[1].Value; }
            var tk = CgTypeToken.Match(typeText);
            return tk.Success ? tk.Groups[1].Value : null;
        }

        /// <summary>Merge a class's members (by parent_name) from one CodeGraph-schema DB into the completion
        /// list. Names are stored "Parent.Member"; the inserted text is the bare member (the "oInstance."
        /// already typed stays put). Added items are filtered by <paramref name="partial"/> and deduped via
        /// <paramref name="seen"/>; <paramref name="collectInto"/> (optional) receives EVERY member name
        /// unfiltered, for the caller's scoping pass. No-op when the DB is missing/unopenable. Never throws.</summary>
        private static void MergeDbMembers(
            List<LspClient.CompletionItemInfo> primary, HashSet<string> seen,
            string className, string partial, string db, HashSet<string> collectInto = null)
        {
            try
            {
                if (string.IsNullOrEmpty(db) || !File.Exists(db)) return;
                using (var p = new CodeGraphProvider())
                {
                    if (!p.Open(db)) return;
                    var syms = p.FindMembersOfParent(className, 500);
                    if (syms == null) return;
                    foreach (var s in syms)
                    {
                        if (s == null || string.IsNullOrEmpty(s.Name)) continue;
                        int dot = s.Name.LastIndexOf('.');
                        if (dot == s.Name.Length - 1) continue;   // malformed "Parent." row — no member suffix
                        string member = dot >= 0 ? s.Name.Substring(dot + 1) : s.Name;
                        if (collectInto != null) collectInto.Add(member);   // full set (unfiltered) for scoping
                        if (partial.Length > 0 && !member.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(member)) continue;
                        // Members are a mix of methods (type=procedure) and class-typed data members
                        // (type=class) — map each to its real icon rather than labelling all "method".
                        primary.Add(new LspClient.CompletionItemInfo
                        {
                            Label = member,
                            Kind = s.Type == "procedure" || s.Type == "function" ? 2 /*Method*/ : CgCompletionKind(s.Type),
                            Detail = CgCompletionDetail(s),
                            InsertText = member
                        });
                    }
                }
            }
            catch { }
        }
    }
}
