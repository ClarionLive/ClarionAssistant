using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ClarionLsp.Contracts;
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
        /// <summary>Settings key — when "true", ignore the shared addin and always use the bundled
        /// LspClient. For debugging / rollback when the shared path misbehaves.</summary>
        public const string ForceLocalSettingKey = "Lsp.ForceLocal";

        /// <summary>True when the developer has pinned us to the bundled LSP via settings.</summary>
        public static bool ForceLocal
        {
            get
            {
                try
                {
                    string v = new SettingsService().Get(ForceLocalSettingKey);
                    return !string.IsNullOrEmpty(v) &&
                           v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
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

        /// <summary>textDocument/hover → raw LSP-response-shaped dict (consumers read ["result"]).</summary>
        public static Dictionary<string, object> GetHover(string filePath, int line, int character, string bufferText = null)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.GetHover(filePath, line, character, bufferText) : null; }
            return SharedGetHover(c, filePath, line, character);
        }

        /// <summary>textDocument/definition → raw LSP-response-shaped dict.</summary>
        public static Dictionary<string, object> GetDefinition(string filePath, int line, int character)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.GetDefinition(filePath, line, character) : null; }
            return SharedGetDefinition(c, filePath, line, character);
        }

        /// <summary>textDocument/references → raw LSP-response-shaped dict.</summary>
        public static Dictionary<string, object> GetReferences(string filePath, int line, int character)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.GetReferences(filePath, line, character) : null; }
            return SharedGetReferences(c, filePath, line, character);
        }

        /// <summary>textDocument/documentSymbol (optionally syncing a live buffer) → raw LSP dict.</summary>
        public static Dictionary<string, object> GetDocumentSymbols(string filePath, string bufferText = null)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.GetDocumentSymbols(filePath, bufferText) : null; }
            return SharedGetDocumentSymbols(c, filePath, bufferText);
        }

        /// <summary>workspace/symbol → raw LSP dict.</summary>
        public static Dictionary<string, object> FindWorkspaceSymbol(string query)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.FindWorkspaceSymbol(query) : null; }
            return SharedFindWorkspaceSymbol(c, query);
        }

        /// <summary>textDocument/rename → WorkspaceEdit-shaped dict (ExtractWorkspaceEditFlat-compatible).</summary>
        public static Dictionary<string, object> Rename(string filePath, int line, int character, string newName)
        {
            var c = Shared;
            if (c == null) { var lsp = LspClient.Active; return lsp != null ? lsp.Rename(filePath, line, character, newName) : null; }
            return SharedRename(c, filePath, line, character, newName);
        }

        /// <summary>textDocument/completion → bundled LspClient.CompletionItemInfo list.</summary>
        public static List<LspClient.CompletionItemInfo> GetCompletion(
            string filePath, int line, int character, int timeoutMs = 2500, string bufferText = null)
        {
            var c = Shared;
            if (c == null)
            {
                var lsp = LspClient.Active;
                return lsp != null
                    ? lsp.GetCompletion(filePath, line, character, timeoutMs, bufferText)
                    : new List<LspClient.CompletionItemInfo>();
            }
            return SharedGetCompletion(c, filePath, line, character, timeoutMs, bufferText);
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
    }
}
