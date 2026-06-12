using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ClarionAssistant.Services;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Path B — Modern Embeditor (M1 spike, read-only render).
    /// Hosts a Monaco editor in WebView2 as a SharpDevelop view, showing the assembled
    /// embeditor source. Generation + parse-back + persistence remain Clarion-owned; this
    /// view is a parallel surface (mirror model — see docs/ModernEmbeditor-PathA.md, Path B).
    ///
    /// M1 scope: scaffold + render only. The editable-region map (read-only guard) and the
    /// save round-trip back through WriteEmbedContentByLine / SaveAndCloseEmbeditor are M2.
    ///
    /// Mirrors the proven WebView2-as-view pattern from DiffViewContent.cs: shared environment
    /// cache, virtual-host folder mapping for large-buffer transfer, and a JS to C# message bridge.
    /// </summary>
    public class ModernEmbeditorViewContent : AbstractViewContent
    {
        private Panel _panel;
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;

        private string _title;
        private string _sourceText;
        private string _language;
        private bool _isDark = true;
        private List<int[]> _editableRanges; // 1-based inclusive [start,end] embed-slot ranges
        private readonly string _procedureName;     // set when opened from the picker (enables save)
        private List<string> _originalSlotTexts;     // baseline slot contents for change detection
        private readonly bool _saveEnabled;
        private readonly string _lspFileName;        // synthetic .clw URI for LSP completion/hover requests

        private string _tempDir;
        private const string VIRTUAL_HOST = "clarion-embeditor-data";

        // Find/Replace history scope: per-version (storage layer) + per-solution (folder) + per-procedure
        // (the "This procedure" group). Resolved once from the IDE when the page first asks for source.
        private string _histSolutionPath;
        private string _histProcKey;
        private bool _histScopeResolved;

        // CA Embeditor selection snapshot — pushed by Monaco (onDidChangeCursorSelection), read by the
        // embeditor_get_selection MCP tool. Follows the saveCursor/saveBookmarks push model (no async
        // round-trip → no WebView2 re-entrancy). Written + read on the UI thread for the instance path.
        private string _selText = "";
        private int _selStartLine, _selStartCol, _selEndLine, _selEndCol;
        private bool _selHasSelection;
        private bool _selTruncated;   // JS clipped the text at the cap — surface it so a consumer never treats a partial selection as whole

        // Last selection reported by whichever tab pushed most recently. Lets the read survive a moment of
        // ambiguous focus resolution. Guarded because GetFocusedSelection() may run before focus settles.
        private static readonly object _selSnapLock = new object();
        private static Dictionary<string, object> _lastFocusedSelection;

        private static readonly List<ModernEmbeditorViewContent> _instances = new List<ModernEmbeditorViewContent>();

        public override Control Control { get { return _panel; } }

        /// <summary>The procedure this tab represents (null/empty in mirror mode).</summary>
        public string ProcedureName { get { return _procedureName; } }

        /// <summary>The Modern Embeditor tab that's currently the active document, or null.</summary>
        public static ModernEmbeditorViewContent ActiveModernView()
        {
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                if (wb != null)
                {
                    // Reflect ActiveWorkbenchWindow -> ViewContent (the property is explicit-interface on the
                    // workbench itself, so GetProperty by name there returns null — go via the window).
                    var aw = GetProp(wb, "ActiveWorkbenchWindow");
                    if (aw != null)
                    {
                        var vc = GetProp(aw, "ActiveViewContent") ?? GetProp(aw, "ViewContent");
                        var m = vc as ModernEmbeditorViewContent;
                        if (m != null) return m;
                    }
                }
                // Fallback: if exactly one Modern Embeditor is open, it's unambiguous.
                lock (_instances) { if (_instances.Count == 1) return _instances[0]; }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The Modern Embeditor view that is the FOCUSED active document, or null. Unlike ActiveModernView() this
        /// does NOT fall back to the lone open tab: the Data pad routes by focus, so a Modern tab sitting unfocused
        /// in the background must resolve to null (no editor) rather than silently becoming the action target.
        /// </summary>
        public static ModernEmbeditorViewContent FocusedModernView()
        {
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                if (wb == null) return null;
                var aw = GetProp(wb, "ActiveWorkbenchWindow");
                if (aw == null) return null;
                var vc = GetProp(aw, "ActiveViewContent") ?? GetProp(aw, "ViewContent");
                return vc as ModernEmbeditorViewContent;
            }
            catch { return null; }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var p = obj.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return (p != null && p.GetIndexParameters().Length == 0) ? p.GetValue(obj, null) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Data symbols (locals/globals/structures) for this procedure, from the LSP document-symbol tree
        /// over the opened source. Each entry: { name, kind, detail }. Empty if the LSP isn't running.
        /// </summary>
        public List<Dictionary<string, object>> GetDataSymbols()
        {
            var result = new List<Dictionary<string, object>>();
            try
            {
                var lsp = LspClient.Active;
                if (lsp == null) return result;
                var resp = lsp.GetDocumentSymbols(_lspFileName, _sourceText);
                object res = (resp != null && resp.ContainsKey("result")) ? resp["result"] : null;
                CollectSymbols(res, result);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetDataSymbols: " + ex.Message); }
            return result;
        }

        // LSP documentSymbol returns either DocumentSymbol[] (hierarchical, has children) or
        // SymbolInformation[] (flat). Collect leaf names + kinds from either shape.
        private static void CollectSymbols(object node, List<Dictionary<string, object>> into)
        {
            var list = node as System.Collections.IEnumerable;
            if (list == null) return;
            foreach (var item in list)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                string name = d.ContainsKey("name") ? d["name"] as string : null;
                int kind = 0;
                if (d.ContainsKey("kind")) { try { kind = Convert.ToInt32(d["kind"]); } catch { } }
                string detail = d.ContainsKey("detail") ? d["detail"] as string : null;
                if (!string.IsNullOrEmpty(name))
                    into.Add(new Dictionary<string, object> { { "name", name }, { "kind", kind }, { "detail", detail } });
                if (d.ContainsKey("children")) CollectSymbols(d["children"], into);
            }
        }

        /// <summary>Recursively flatten a parsed FieldDef (with nested QUEUE/GROUP members) into the
        /// JSON-ready dictionary the Data pad consumes. Children are only emitted when present.</summary>
        private static Dictionary<string, object> FieldToDict(ClarionAppDataReader.FieldDef d)
        {
            var dict = new Dictionary<string, object> { { "name", d.Name }, { "type", d.Type } };
            // Display metadata — only present when sourced from the .txa (ParseTxaProcedureData).
            if (!string.IsNullOrEmpty(d.Picture)) dict["picture"] = d.Picture;
            if (!string.IsNullOrEmpty(d.Prompt)) dict["prompt"] = d.Prompt;
            if (!string.IsNullOrEmpty(d.Header)) dict["header"] = d.Header;
            if (d.Children != null && d.Children.Count > 0)
            {
                var kids = new List<object>();
                foreach (var c in d.Children) kids.Add(FieldToDict(c));
                dict["children"] = kids;
            }
            return dict;
        }

        // Whole-app .txa text, exported on the UI thread (open + save) and parsed per-proc on the pad's
        // background refresh. Static so it's shared across all Modern Embeditor tabs for the same app.
        private static readonly object _txaLock = new object();
        private static string _wholeAppTxa;

        // Live dictionary snapshot (master, proc-independent): table name -> TableDef (cols w/ pictures +
        // GROUP nesting, keys). Read from the IDE object model on the UI thread; the Other Files schema
        // source (replaces the .dcv). See reference_clarion_dict_object_model.
        private static readonly object _liveLock = new object();
        private static Dictionary<string, ClarionAppDataReader.TableDef> _liveTables;

        /// <summary>
        /// Refresh the Modern Data pad's IDE-sourced caches: (1) the whole-app .txa text (Local/Global Data),
        /// and (2) a snapshot of the live dictionary tables (Other Files schema). BOTH require the UI thread
        /// (they touch the IDE / drive a silent whole-app export) — never call from GetPadData, which runs on
        /// a background thread (a background export/IDE-poke is the re-entrancy that locks the IDE). Each
        /// source is independent and best-effort: on failure the prior cache is kept and GetPadData falls
        /// back (embeditor-source for locals, .dcv for Other Files).
        /// </summary>
        public static void RefreshPadSources()
        {
            // (1) Whole-app .txa — silent Export(path, all=TRUE), validated. Source for Local/Global Data.
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "ClarionModernData_wholeapp.txa");
                string res = new AppTreeService().ExportTxa(tmp);
                if (!string.IsNullOrEmpty(res) && !res.StartsWith("Error") && File.Exists(tmp))
                {
                    string text = File.ReadAllText(tmp);
                    if (!string.IsNullOrEmpty(text)) lock (_txaLock) { _wholeAppTxa = text; }
                }
            }
            catch { /* keep prior .txa cache */ }

            // (2) Live dictionary snapshot — the master Tables read from App.FileSchema.DataDictionary.
            try
            {
                var tables = new AppTreeService().ReadLiveDictionaryTables();
                if (tables != null && tables.Count > 0)
                {
                    var map = new Dictionary<string, ClarionAppDataReader.TableDef>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in tables)
                        if (!string.IsNullOrEmpty(t.Name)) map[t.Name] = t;
                    lock (_liveLock) { _liveTables = map; }
                }
            }
            catch { /* keep prior dict cache; GetOtherFiles falls back to the .dcv */ }
        }

        // Identity (.app file path) of the app the pad-source caches were last loaded FOR, via the SELECTION
        // path. The caches (_wholeAppTxa/_liveTables) are process-wide static and BuildPadData consumes them by
        // procedure name only — so switching .app must force a re-export, otherwise a same-named proc in the new
        // app would render the previous app's Local/Global/Tables data. Guarded by _txaLock.
        private static string _padSourcesAppKey;

        /// <summary>
        /// Ensure the pad's IDE-sourced caches (whole-app .txa + live dict snapshot) are loaded for the CURRENT
        /// app, re-exporting only when (a) nothing is cached yet, or (b) the active .app changed since the last
        /// selection load. The whole-app .txa carries EVERY procedure, so within one app a selection switch to
        /// any proc reads straight from cache — no per-click export. Used by the app-tree SELECTION path, which
        /// has no open/save hook to populate the caches. UI thread (delegates to RefreshPadSources).
        ///
        /// "Loaded" is keyed on the .txa being present + the app identity — NOT on dictionary non-emptiness: a
        /// dictionary-less app legitimately yields an empty _liveTables, and gating on that would re-export the
        /// whole app on every single click (a multi-second UI stall). The dictionary, when present, is loaded as
        /// a side effect of the same RefreshPadSources call.
        /// </summary>
        public static void EnsurePadSourcesLoaded()
        {
            string appKey = TryGetCurrentAppKey();
            bool appChanged, needLoad;
            lock (_txaLock)
            {
                appChanged = !string.Equals(_padSourcesAppKey, appKey, StringComparison.OrdinalIgnoreCase);
                needLoad = string.IsNullOrEmpty(_wholeAppTxa) || appChanged;
            }
            if (!needLoad) return;

            // APP SWITCH: drop the prior app's caches BEFORE refreshing so a failed OR empty refresh can never
            // serve the previous app's .txa or dictionary tables under the new app's procedure names (cross-app
            // isolation). RefreshPadSources is best-effort (keeps prior cache on error) and only overwrites
            // _liveTables when the dict read returns >0 rows — so without this clear, a failed export or a
            // dictionary-less new app would leave stale data behind. Within the SAME app we keep the prior cache
            // (no clear) so a transient export hiccup falls back gracefully.
            if (appChanged)
            {
                lock (_txaLock) { _wholeAppTxa = null; }
                lock (_liveLock) { _liveTables = null; }
            }

            RefreshPadSources();

            // Commit the app key ONLY when the .txa actually loaded for this app. If the export failed (txa still
            // empty), leave the key stale so the next tick retries — and since we cleared the caches above on an
            // app change, BuildPadData has no prior-app data to fall back on (it shows empty, not wrong-app data).
            //
            // FRESHNESS CONTRACT (selection/populate-only mode): the whole-app .txa + dict snapshot are exported
            // ONCE per app and reused across selection clicks. They are kept fresh by the existing open/save hooks,
            // native proc-change refresh, and the pad's own variable add/edit/delete (ScheduleAddRefresh). Edits
            // made through OTHER IDE surfaces (e.g. Clarion's native dictionary editor) while ONLY browsing tree
            // selections are not reflected until one of those events fires — an accepted trade-off for a read-only
            // quick-view that avoids a multi-second whole-app export on every click.
            lock (_txaLock) { _padSourcesAppKey = string.IsNullOrEmpty(_wholeAppTxa) ? null : appKey; }
        }

        // Current open .app identity (file path, else name) via pure managed reflection; null when no app open.
        private static string TryGetCurrentAppKey()
        {
            try
            {
                var info = new AppTreeService().GetAppInfo();
                if (info != null)
                {
                    object fn;
                    if (info.TryGetValue("fileName", out fn) && fn != null && !string.IsNullOrEmpty(fn.ToString()))
                        return fn.ToString();
                    object nm;
                    if (info.TryGetValue("name", out nm) && nm != null && !string.IsNullOrEmpty(nm.ToString()))
                        return nm.ToString();
                }
            }
            catch { }
            return null;
        }

        // Parsed dictionary (.dcv) tables, cached by path + mtime so we re-parse only when Clarion's
        // Auto Export/Import rewrites the .dcv. Parsing is pure file I/O + XML (safe on the bg thread).
        private static readonly object _dcvLock = new object();
        private static string _dcvPathCached;
        private static DateTime _dcvMtimeCached;
        private static List<ClarionAppDataReader.TableDef> _dcvTablesCached;

        private static List<ClarionAppDataReader.TableDef> GetDcvTablesCached(string dcvPath)
        {
            if (string.IsNullOrEmpty(dcvPath) || !File.Exists(dcvPath)) return null;
            var mtime = File.GetLastWriteTimeUtc(dcvPath);
            lock (_dcvLock)
            {
                if (_dcvTablesCached != null && _dcvPathCached == dcvPath && _dcvMtimeCached == mtime)
                    return _dcvTablesCached;
            }
            var parsed = ClarionAppDataReader.ParseDcvTables(dcvPath);
            lock (_dcvLock) { _dcvPathCached = dcvPath; _dcvMtimeCached = mtime; _dcvTablesCached = parsed; }
            return parsed;
        }

        // The dictionary .dcv text export beside the .dct (Clarion Auto Export/Import). Default ext .dcv;
        // fall back to any *.dcv in the dict folder matching the dict base name (then any) for ext variance.
        private static string ResolveDcvPath(string dctPath)
        {
            if (string.IsNullOrEmpty(dctPath)) return null;
            try
            {
                string dcv = Path.ChangeExtension(dctPath, ".dcv");
                if (File.Exists(dcv)) return dcv;
                string dir = Path.GetDirectoryName(dctPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string baseName = Path.GetFileNameWithoutExtension(dctPath);
                    var found = Directory.GetFiles(dir, "*.dcv");
                    foreach (var x in found)
                        if (string.Equals(Path.GetFileNameWithoutExtension(x), baseName, StringComparison.OrdinalIgnoreCase))
                            return x;
                    if (found.Length > 0) return found[0];
                }
            }
            catch { }
            return null;
        }

        // Field → JSON dict for an Other Files column. Name is UNPREFIXED (the prefix is shown once at the
        // file header; the frontend prepends it for double-click insert). Carries detail for the "+" panel.
        private static Dictionary<string, object> ColToDict(ClarionAppDataReader.FieldDef f)
        {
            var d = new Dictionary<string, object> { { "name", f.Name }, { "type", f.Type ?? "" } };
            if (!string.IsNullOrEmpty(f.Picture)) d["picture"] = f.Picture;
            // Show a description only when it adds info — the dict often defaults it to the field name.
            if (!string.IsNullOrEmpty(f.Description) && !string.Equals(f.Description, f.Name, StringComparison.OrdinalIgnoreCase))
                d["description"] = f.Description;
            if (!string.IsNullOrEmpty(f.DerivedFrom)) d["derivedFrom"] = f.DerivedFrom;
            if (!string.IsNullOrEmpty(f.Prompt)) d["prompt"] = f.Prompt;
            if (!string.IsNullOrEmpty(f.Header)) d["header"] = f.Header;
            if (f.Children != null && f.Children.Count > 0)
            {
                var kids = new List<object>();
                foreach (var c in f.Children) kids.Add(ColToDict(c));
                d["children"] = kids;
            }
            return d;
        }

        // Assemble the FILE attribute line for the table-detail panel, e.g.
        // DRIVER('MSSQL','/TRUSTEDCONNECTION=TRUE'),OWNER(Glo:Connection),NAME('Person.Address'),PRE(Add),BINDABLE,THREAD
        private static string BuildTableAttributes(ClarionAppDataReader.TableDef t)
        {
            var sb = new StringBuilder();
            Action<string> add = s => { if (sb.Length > 0) sb.Append(","); sb.Append(s); };
            if (!string.IsNullOrEmpty(t.Driver))
                add("DRIVER('" + t.Driver + "'" +
                    (!string.IsNullOrEmpty(t.DriverOptions) ? ",'" + t.DriverOptions + "'" : "") + ")");
            if (!string.IsNullOrEmpty(t.Owner)) add("OWNER(" + t.Owner + ")");
            if (!string.IsNullOrEmpty(t.FullName)) add("NAME('" + t.FullName + "')");
            if (!string.IsNullOrEmpty(t.Prefix)) add("PRE(" + t.Prefix + ")");
            if (t.Bindable) add("BINDABLE");
            if (t.Threaded) add("THREAD");
            return sb.ToString();
        }

        // KeyDef list → JSON dicts for the Other Files key rows (rich form). Falls back to legacy name-only.
        private static List<object> KeysToDicts(ClarionAppDataReader.TableDef t)
        {
            var keys = new List<object>();
            if (t.KeyDefs.Count > 0)
                foreach (var k in t.KeyDefs)
                {
                    var comps = new List<object>();
                    foreach (var c in k.Components) comps.Add(ColToDict(c));
                    keys.Add(new Dictionary<string, object>
                    {
                        { "name", k.Name }, { "components", comps }, { "keyType", k.KeyType },
                        { "primary", k.Primary }, { "unique", k.Unique },
                        { "caseSensitive", k.CaseSensitive }, { "description", k.Description ?? "" }
                    });
                }
            else
                foreach (var kn in t.Keys) keys.Add(new Dictionary<string, object> { { "name", kn } });
            return keys;
        }

        // RelationDef list → JSON dicts for the Relations sub-folder. Each row is named by the related table;
        // the "+" detail carries the relation type, primary/foreign keys, and the column mappings.
        private static List<object> RelationsToDicts(ClarionAppDataReader.TableDef t)
        {
            var rels = new List<object>();
            foreach (var r in t.Relations)
            {
                var maps = new List<object>();
                foreach (var m in r.Mappings)
                    maps.Add(new Dictionary<string, object> { { "from", m.From ?? "" }, { "to", m.To ?? "" } });
                rels.Add(new Dictionary<string, object>
                {
                    { "name", r.Name ?? "" }, { "type", r.Type ?? "" },
                    { "primaryKey", r.PrimaryKey ?? "" }, { "foreignKey", r.ForeignKey ?? "" },
                    { "mappings", maps }
                });
            }
            return rels;
        }

        /// <summary>
        /// The procedure's "Other Files": the [FILES][OTHERS] names from the cached whole-app .txa, paired
        /// with their schema (columns w/ pictures + GROUP nesting, keys) from the dictionary .dcv export.
        /// If the .dcv isn't available, the files are still listed by name so the section appears.
        /// </summary>
        private static List<Dictionary<string, object>> GetOtherFiles(string txa, string procedureName)
        {
            var outp = new List<Dictionary<string, object>>();
            try
            {
                if (string.IsNullOrEmpty(txa) || string.IsNullOrEmpty(procedureName)) return outp;
                var names = ClarionAppDataReader.ParseTxaOtherFiles(txa, procedureName);
                if (names.Count == 0) return outp;

                // Schema source: prefer the LIVE dictionary snapshot (always current, no file dependency);
                // fall back to the dictionary .dcv text export only if the live snapshot isn't available.
                Dictionary<string, ClarionAppDataReader.TableDef> live;
                lock (_liveLock) { live = _liveTables; }
                List<ClarionAppDataReader.TableDef> dcvTables = null; // lazily loaded fallback

                foreach (var n in names)
                {
                    ClarionAppDataReader.TableDef t = null;
                    if (live != null) live.TryGetValue(n, out t);
                    if (t == null)
                    {
                        if (dcvTables == null)
                            dcvTables = GetDcvTablesCached(ResolveDcvPath(ClarionAppDataReader.ParseTxaDictionaryPath(txa)))
                                        ?? new List<ClarionAppDataReader.TableDef>();
                        t = dcvTables.Find(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                    }
                    if (t == null)
                    {
                        // Listed but no schema (no live dict / .dcv yet) — still show the file name.
                        outp.Add(new Dictionary<string, object>
                        {
                            { "name", n }, { "prefix", "" }, { "attributes", "" }, { "description", "" },
                            { "columns", new List<object>() }, { "keys", new List<object>() }
                        });
                        continue;
                    }
                    var cols = new List<object>();
                    foreach (var f in t.Fields) cols.Add(ColToDict(f));
                    outp.Add(new Dictionary<string, object>
                    {
                        { "name", t.Name }, { "prefix", t.Prefix },
                        { "attributes", BuildTableAttributes(t) }, { "description", t.Description ?? "" },
                        { "columns", cols }, { "keys", KeysToDicts(t) }, { "relations", RelationsToDicts(t) }
                    });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetOtherFiles: " + ex.Message); }
            return outp;
        }

        /// <summary>
        /// The procedure's PRIMARY browse file (Clarion's "File-Browsing List Box") enriched from the live
        /// dictionary, carrying the browse KEY. Returns 0 or 1 entries (a list keeps the frontend renderer
        /// uniform with Other Files / Declared Tables).
        /// </summary>
        private static List<Dictionary<string, object>> GetBrowseFiles(string txa, string procedureName)
        {
            var outp = new List<Dictionary<string, object>>();
            try
            {
                if (string.IsNullOrEmpty(txa) || string.IsNullOrEmpty(procedureName)) return outp;
                var pf = ClarionAppDataReader.ParseTxaPrimaryFile(txa, procedureName);
                if (pf == null || string.IsNullOrEmpty(pf.File)) return outp;

                Dictionary<string, ClarionAppDataReader.TableDef> live;
                lock (_liveLock) { live = _liveTables; }
                ClarionAppDataReader.TableDef t = null;
                if (live != null) live.TryGetValue(pf.File, out t);

                Dictionary<string, object> d;
                if (t != null)
                {
                    var cols = new List<object>();
                    foreach (var f in t.Fields) cols.Add(ColToDict(f));
                    d = new Dictionary<string, object>
                    {
                        { "name", t.Name }, { "prefix", t.Prefix },
                        { "attributes", BuildTableAttributes(t) }, { "description", t.Description ?? "" },
                        { "columns", cols }, { "keys", KeysToDicts(t) }, { "relations", RelationsToDicts(t) }
                    };
                }
                else
                {
                    // Listed but no live-dict schema yet — still show the file + key.
                    d = new Dictionary<string, object>
                    {
                        { "name", pf.File }, { "prefix", "" }, { "attributes", "" }, { "description", "" },
                        { "columns", new List<object>() }, { "keys", new List<object>() }
                    };
                }
                d["browseKey"] = pf.Key ?? "";
                outp.Add(d);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetBrowseFiles: " + ex.Message); }
            return outp;
        }

        /// <summary>
        /// The procedure's per-template-instance FILE SCOPES exactly as Clarion's native Data/Tables pad groups
        /// them ("File-Browsing List Box", "Update Record on Disk", "Relation Tree Viewing List Box", ...), read
        /// LIVE from the FileSchemaTree (see <see cref="FileSchemaScopeReader"/>). Each scope's attached file(s)
        /// are enriched with full dictionary schema (columns w/ pictures + GROUP nesting, keys, relations) from
        /// the live snapshot, so each renders identically to Other Files / Declared Tables.
        ///
        /// Returns an empty list when the live tree isn't reachable OR is showing a different procedure than
        /// <paramref name="procedureName"/> (the reader fails closed) — the caller then falls back to the flat
        /// .txa browse/other parsing. Only files that resolve in the live dictionary are emitted (a scope whose
        /// files all fail to resolve is dropped), so a stray non-file node never produces an empty table card.
        /// </summary>
        private static List<Dictionary<string, object>> GetFileScopes(string procedureName)
        {
            var outp = new List<Dictionary<string, object>>();
            try
            {
                var scopes = FileSchemaScopeReader.ReadFileScopes(procedureName);
                if (scopes == null || scopes.Count == 0) return outp;

                Dictionary<string, ClarionAppDataReader.TableDef> live;
                lock (_liveLock) { live = _liveTables; }
                if (live == null) return outp;   // no schema to enrich with → let the txa fallback render instead

                foreach (var sc in scopes)
                {
                    var files = new List<object>();
                    foreach (var fr in sc.Files)
                    {
                        ClarionAppDataReader.TableDef t;
                        if (fr == null || string.IsNullOrEmpty(fr.Name) || !live.TryGetValue(fr.Name, out t) || t == null) continue;
                        var cols = new List<object>();
                        foreach (var f in t.Fields) cols.Add(ColToDict(f));
                        files.Add(new Dictionary<string, object>
                        {
                            { "name", t.Name }, { "prefix", t.Prefix },
                            { "attributes", BuildTableAttributes(t) }, { "description", t.Description ?? "" },
                            { "columns", cols }, { "keys", KeysToDicts(t) }, { "relations", RelationsToDicts(t) },
                            { "depth", fr.Depth }   // relation-tree nesting depth → indented rendering in the File Schematic
                        });
                    }
                    if (files.Count == 0) continue;   // no resolvable file → drop the scope (don't show an empty card)
                    outp.Add(new Dictionary<string, object>
                    {
                        { "label", sc.Label }, { "instance", sc.Instance }, { "files", files }
                    });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetFileScopes: " + ex.Message); }
            return outp;
        }

        /// <summary>
        /// Combined data payload for the Modern Data pad: the procedure's local symbols (LSP) plus the
        /// dictionary tables it references (parsed from the generated &lt;app&gt;.clw, filtered to used ones).
        /// </summary>
        public Dictionary<string, object> GetPadData()
        {
            return BuildPadData(_procedureName, _sourceText);
        }

        /// <summary>
        /// Procedure-name-keyed Data pad payload: builds the same data as the instance GetPadData()
        /// from a procedure name + source text, with NO Modern view instance — this is what lets the pad
        /// serve the native (PWEE) embeditor too. Sources: the static whole-app .txa cache (RefreshPadSources)
        /// + the live dictionary snapshot + AppTreeService. <paramref name="sourceText"/> feeds the Routines
        /// list and the Local Data fallback (used only when the .txa isn't cached yet); pass the focused
        /// editor's buffer (the Modern mirror, or the native embeditor source).
        /// </summary>
        public static Dictionary<string, object> BuildPadData(string procedureName, string sourceText)
        {
            var locals = new List<Dictionary<string, object>>();
            var routines = new List<Dictionary<string, object>>();
            var localProcedures = new List<Dictionary<string, object>>();
            var globals = new List<Dictionary<string, object>>();
            var otherFiles = new List<Dictionary<string, object>>();
            var browseFiles = new List<Dictionary<string, object>>();
            var fileScopes = new List<Dictionary<string, object>>();
            try
            {
                // Prefer the AUTHORITATIVE .txa source (declaration order + pictures + exact Clarion item
                // set). Falls back to the embeditor-source parse when the whole-app .txa isn't cached yet.
                List<ClarionAppDataReader.FieldDef> localDefs = null;
                string txa; lock (_txaLock) { txa = _wholeAppTxa; }
                if (!string.IsNullOrEmpty(txa) && !string.IsNullOrEmpty(procedureName))
                {
                    var fromTxa = ClarionAppDataReader.ParseTxaProcedureData(txa, procedureName);
                    if (fromTxa.Count > 0) localDefs = fromTxa;
                }
                if (localDefs == null)
                    localDefs = ClarionAppDataReader.ParseLocalData(sourceText, procedureName);

                foreach (var d in localDefs)
                    locals.Add(FieldToDict(d));

                foreach (var r in ClarionAppDataReader.ParseRoutines(sourceText, procedureName))
                    routines.Add(new Dictionary<string, object> { { "name", r.Name }, { "line", r.Line } });

                foreach (var p in ClarionAppDataReader.ParseLocalProcedures(sourceText, procedureName))
                    localProcedures.Add(new Dictionary<string, object> { { "name", p.Name }, { "line", p.Line } });

                // Global Data: prefer the .txa [PROGRAM][DATA] — the developer-registered globals ONLY
                // (nested + pictures, matching Clarion's pad). When the .txa is cached it's authoritative
                // even if empty (an app with no dev globals shows none). Fall back to the generated
                // <app>.clw globals only when no .txa is available yet.
                List<ClarionAppDataReader.FieldDef> globalDefs;
                if (!string.IsNullOrEmpty(txa))
                {
                    globalDefs = ClarionAppDataReader.ParseTxaGlobalData(txa);
                }
                else
                {
                    string appClw = ClarionAppDataReader.FindAppClwPath();
                    globalDefs = appClw != null
                        ? ClarionAppDataReader.ParseGlobalData(appClw)
                        : new List<ClarionAppDataReader.FieldDef>();
                }
                foreach (var g in globalDefs)
                    globals.Add(FieldToDict(g));

                // Other Files: the proc's [FILES][OTHERS] names paired with dictionary (.dcv) schema.
                otherFiles = GetOtherFiles(txa, procedureName);

                // File scopes: ALL per-template-instance file groups the native Data/Tables pad shows ("File-
                // Browsing List Box", "Update Record on Disk", "Relation Tree...", ...), read LIVE from the
                // FileSchemaTree. When this resolves it SUPERSEDES the flat .txa browse parse below — the browse
                // is itself one of these instance scopes, so emitting both would double-list it. The reader fails
                // closed (empty) when the docked tree isn't reachable or is showing a different procedure, in
                // which case we keep the .txa browse fallback so the section still renders something.
                fileScopes = GetFileScopes(procedureName);
                if (fileScopes.Count == 0)
                {
                    // File-Browsing List Box: the proc's [FILES][PRIMARY] file + [KEY], dict-enriched.
                    browseFiles = GetBrowseFiles(txa, procedureName);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetPadData parse: " + ex.Message); }

            var moduleData = new List<Dictionary<string, object>>();
            try
            {
                string modClw = ClarionAppDataReader.FindModuleClwForProcedure(procedureName);
                foreach (var d in ClarionAppDataReader.ParseModuleData(modClw))
                    moduleData.Add(new Dictionary<string, object> { { "name", d.Name }, { "type", d.Type } });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] ParseModuleData: " + ex.Message); }

            var procedures = new List<Dictionary<string, object>>();
            try
            {
                foreach (var p in new AppTreeService().GetProcedureDetails())
                {
                    string n = (p != null && p.ContainsKey("name")) ? p["name"]?.ToString() : null;
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    string proto = p.ContainsKey("prototype") ? p["prototype"]?.ToString() : null;
                    procedures.Add(new Dictionary<string, object> { { "name", n }, { "params", ExtractParamList(proto) } });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] procedures: " + ex.Message); }

            var data = new Dictionary<string, object>
            {
                { "procedure", procedureName ?? "" },
                { "locals", locals },
                { "routines", routines },
                { "localProcedures", localProcedures },
                { "moduleData", moduleData },
                { "globals", globals },
                { "otherFiles", otherFiles },
                { "browseFiles", browseFiles },
                { "fileScopes", fileScopes },
                { "tables", GetDeclaredTables() },
                { "procedures", procedures }
            };
            return data;
        }

        /// <summary>
        /// Pull the parameter list "(...)" out of a Clarion prototype so the Procedures pad can show it
        /// (e.g. "PROCEDURE(LONG id),LONG" -> "(LONG id)"). Returns "" when the prototype has no parens.
        /// </summary>
        private static string ExtractParamList(string prototype)
        {
            if (string.IsNullOrEmpty(prototype)) return "";
            int open = prototype.IndexOf('(');
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < prototype.Length; i++)
            {
                if (prototype[i] == '(') depth++;
                else if (prototype[i] == ')') { depth--; if (depth == 0) return prototype.Substring(open, i - open + 1); }
            }
            return prototype.Substring(open); // unbalanced — take the rest
        }

        /// <summary>Navigate this editor to a ROUTINE's declaration (Modern Data pad "go to routine" button).</summary>
        public void GotoRoutine(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            Action post = () =>
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                try { _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"gotoRoutine\",\"name\":" + JsonString(name) + "}"); }
                catch { }
            };
            try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(post); else post(); }
            catch { }
        }

        /// <summary>If a Modern Embeditor tab for this procedure is already open, focus it. Returns true if found.</summary>
        public static bool TryFocusExisting(string procName)
        {
            if (string.IsNullOrWhiteSpace(procName)) return false;
            lock (_instances)
            {
                foreach (var inst in _instances)
                {
                    if (string.Equals(inst._procedureName, procName, StringComparison.OrdinalIgnoreCase))
                    {
                        inst.BringToFront();
                        return true;
                    }
                }
            }
            return false;
        }

        // "Declared Tables": the tables DECLARED in the generated <app>.clw File Declaration (the program's
        // global file set). The SET comes from the <app>.clw (authoritative, stable, explainable — not a
        // fuzzy text scan); the SCHEMA is enriched from the LIVE dictionary snapshot (pictures, GROUP
        // nesting, full keys), matched by name, falling back to the <app>.clw-parsed schema when the live
        // snapshot lacks an entry. A standalone whole-dictionary browser is a separate, future addin.
        private static List<Dictionary<string, object>> GetDeclaredTables()
        {
            var outp = new List<Dictionary<string, object>>();
            try
            {
                string appClw = ClarionAppDataReader.FindAppClwPath();
                var declared = appClw != null
                    ? ClarionAppDataReader.ParseTables(appClw)
                    : new List<ClarionAppDataReader.TableDef>();
                if (declared.Count == 0) return outp;

                Dictionary<string, ClarionAppDataReader.TableDef> live;
                lock (_liveLock) { live = _liveTables; }

                foreach (var d in declared)
                {
                    ClarionAppDataReader.TableDef t = null;
                    if (live != null && !string.IsNullOrEmpty(d.Name)) live.TryGetValue(d.Name, out t);
                    if (t == null) t = d; // live snapshot not ready / no match — use the clw-parsed schema
                    var cols = new List<object>();
                    foreach (var f in t.Fields) cols.Add(ColToDict(f));
                    outp.Add(new Dictionary<string, object>
                    {
                        { "name", t.Name }, { "prefix", t.Prefix },
                        { "attributes", BuildTableAttributes(t) }, { "description", t.Description ?? "" },
                        { "columns", cols }, { "keys", KeysToDicts(t) }, { "relations", RelationsToDicts(t) }
                    });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetDeclaredTables: " + ex.Message); }
            return outp;
        }

        /// <summary>Insert text at the editor's cursor (used by the Modern Data pad's double-click-insert).</summary>
        public void InsertAtCursor(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Action post = () =>
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                try
                {
                    string json = "{\"type\":\"insertText\",\"text\":" + JsonString(text) + "}";
                    _webView.CoreWebView2.PostWebMessageAsJson(json);
                    // Bring THIS editor tab to the front so the developer can start typing immediately after a
                    // Data-pad double-click insert (the editor JS already does ed.focus() to place the caret).
                    BringToFront();
                }
                catch { }
            };
            try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(post); else post(); }
            catch { }
        }

        public ModernEmbeditorViewContent(string title, string sourceText, List<int[]> editableRanges,
            string language = "clarion", bool isDark = true, string procedureName = null)
        {
            _title = title ?? "Embeditor";
            _sourceText = sourceText ?? "";
            _editableRanges = editableRanges ?? new List<int[]>();
            _language = language ?? "clarion";
            _isDark = isDark;
            _procedureName = procedureName;
            _saveEnabled = !string.IsNullOrWhiteSpace(procedureName);
            _originalSlotTexts = ModernEmbeditorSaver.ExtractSlotTexts(_sourceText, _editableRanges);
            _lspFileName = MakeLspFileName(procedureName);
            TitleName = "CA: " + _title;

            _panel = new Panel { Dock = DockStyle.Fill, BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245) };
            // Plain WebView2 — Monaco's native mouseWheelZoom handles Ctrl+wheel inside the
            // renderer (a WinForms WndProc override never sees WebView2's inner Chrome wheel msg).
            _webView = new WebView2 { Dock = DockStyle.Fill };
            _panel.Controls.Add(_webView);

            lock (_instances) { _instances.Add(this); }
            _panel.HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                _tempDir = Path.Combine(Path.GetTempPath(), "ClarionEmbeditor_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tempDir);
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    VIRTUAL_HOST, _tempDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false; // let Monaco own Ctrl+S, not the browser

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);

                // On open (UI thread): refresh the pad's IDE-sourced caches (whole-app .txa for Local/Global
                // Data; live dictionary snapshot for Other Files). Silent.
                RefreshPadSources();
            }
            catch (Exception ex)
            {
                _isInitializing = false; // allow retry
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = e.IsSuccess;
            _isInitializing = false;
            // SendSource is triggered by the JS "ready" message, not here — avoids double-send.
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                if (action == "ready")
                    SendSource();
                else if (action == "save")
                    HandleSave(json);
                else if (action == "clipboard")
                    HandleClipboard(json);
                else if (action == "completion")
                    HandleCompletion(json);
                else if (action == "hover")
                    HandleHover(json);
                else if (action == "diagnostics")
                    HandleDiagnostics(json);
                else if (action == "saveSettings")
                    HandleSaveSettings(json);
                else if (action == "saveHistory")
                    HandleSaveHistory(json);
                else if (action == "saveCursor")
                    HandleSaveCursor(json);
                else if (action == "saveBookmarks")
                    HandleSaveBookmarks(json);
                else if (action == "selectionChanged")
                    HandleSelectionChanged(json);
                else if (action == "focusEditor")
                    BringToFront();   // drag-drop from the Data pad: activate this tab so the dev can type immediately
                else if (action == "openDesigner")
                    HandleOpenDesigner(json);
                else if (action == "activateDesigner")
                    StructureDesignerService.ActivateCurrent(_panel);   // 'Show designer' on the modal lock overlay
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] Message error: " + ex.Message);
            }
        }

        /// <summary>Persist the user's edits: parse the per-slot payload and run the save round-trip.</summary>
        private void HandleSave(string json)
        {
            if (!_saveEnabled || string.IsNullOrWhiteSpace(_procedureName))
            {
                PostSaveResult(false, "Save isn't available — this tab was opened in mirror mode, not from the procedure picker.");
                return;
            }

            List<string> current;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(json) as Dictionary<string, object>;
                var arr = (data != null && data.ContainsKey("slots")) ? data["slots"] as object[] : null;
                if (arr == null) { PostSaveResult(false, "Save failed: malformed payload (no slots)."); return; }
                current = arr.Select(o => o == null ? "" : o.ToString()).ToList();
            }
            catch (Exception ex)
            {
                PostSaveResult(false, "Save failed parsing the editor payload: " + ex.Message);
                return;
            }

            // CRITICAL — do NOT run the save round-trip on THIS stack. We're inside OnWebMessageReceived,
            // the WebView2 web-message handler (a reentrant message-loop context). ModernEmbeditorSaver.Save
            // re-opens the native embeditor and drives it with nested Application.DoEvents() pumps; on this
            // reentrant stack that deadlocks the IDE — the same failure mode the deferred ShowView fixed on
            // open. Post it so this handler returns and the round-trip runs on a settled UI turn.
            var captured = current;
            if (_panel != null && _panel.IsHandleCreated)
                _panel.BeginInvoke((Action)(() => RunSaveRoundTrip(captured)));
            else
                RunSaveRoundTrip(captured);
        }

        // The actual save round-trip — re-open native embed, write slots, save+close. Runs deferred (off the
        // WebView2 web-message handler) on a settled UI turn so its nested DoEvents pumps don't reenter the
        // WebView2 message loop and deadlock the IDE.
        private void RunSaveRoundTrip(List<string> current)
        {
            bool ok;
            string msg = ModernEmbeditorSaver.Save(_procedureName, _editableRanges, _originalSlotTexts, current, out ok);
            // On success, the saved content is the new baseline so a follow-up save sees no changes.
            if (ok && current.Count == _originalSlotTexts.Count) _originalSlotTexts = current;
            // The save activated the app tree to drive the embeditor — bring this tab back to the front.
            BringToFront();
            // Refresh the pad's IDE-sourced caches (UI thread) so Local/Global Data + Other Files reflect the save.
            if (ok) RefreshPadSources();
            PostSaveResult(ok, msg);
        }

        /// <summary>Re-select this view's tab (the save round-trip activates the app tree to drive the embeditor).</summary>
        private void BringToFront()
        {
            // DEFER the re-select onto a clean, non-reentrant turn. The save round-trip just pumped DoEvents inside
            // the native TryClose; re-activating this (WebView2) tab synchronously on that same stack risks the very
            // focus deadlock we're fixing on the close side. Post it (same primitive HandleSave uses) so it runs
            // after the close stack fully unwinds. Re-ACTIVATING an existing WebView2 tab on a settled turn is safe
            // — only CREATING / manual SetFocus on a reentrant stack deadlocks. Use SelectWindow (the SharpDevelop
            // view activation), NOT a WebView2-specific focus call.
            try
            {
                Action select = () =>
                {
                    try
                    {
                        var w = WorkbenchWindow;
                        if (w != null) w.GetType().GetMethod("SelectWindow", Type.EmptyTypes)?.Invoke(w, null);
                    }
                    catch { }
                };
                if (_panel != null && _panel.IsHandleCreated)
                    _panel.BeginInvoke(select);
                else
                    select();
            }
            catch { }
        }

        /// <summary>
        /// LSP completion request from Monaco. Uses the context-free language set (keywords, builtins,
        /// datatypes, attributes, controls) — no per-keystroke buffer sync needed. Runs off the UI thread
        /// and posts the result back keyed by reqId.
        /// </summary>
        /// <summary>
        /// Kick the shared LSP self-heal (idempotent, fire-and-forget) when no client is running yet.
        /// Mirrors the native embeditor's completion-time self-heal (EmbeditorCompletionService.LspStarter,
        /// wired to EnsureLspRunningInBackground) so the Modern editor can also recover the language server —
        /// completion, hover, AND the LSP diagnostics pass all depend on it. The first request after a cold
        /// start still returns empty (server warming); the next one succeeds.
        /// </summary>
        private static void EnsureLspStarted()
        {
            try
            {
                var lsp = LspClient.Active;
                if (lsp == null || !lsp.IsRunning)
                    EmbeditorCompletionService.LspStarter?.Invoke();
            }
            catch { }
        }

        private void HandleCompletion(string json)
        {
            int reqId, line, column;
            if (!ParseRequest(json, out reqId, out line, out column, out _)) return;
            Task.Run(() =>
            {
                var items = new List<Dictionary<string, object>>();
                string lspStatus;
                try
                {
                    EnsureLspStarted();
                    var lsp = LspClient.Active;
                    if (lsp == null) lspStatus = "not started";
                    else if (!lsp.IsRunning) lspStatus = "starting";
                    else
                    {
                        // Context-free: pass no buffer; the server returns the language item set.
                        var comps = lsp.GetCompletion(_lspFileName, Math.Max(0, line - 1), Math.Max(0, column - 1), 2500, null);
                        if (comps != null)
                            foreach (var c in comps)
                                items.Add(new Dictionary<string, object>
                                {
                                    { "label", c.Label },
                                    { "kind", c.Kind },
                                    { "detail", c.Detail },
                                    { "documentation", c.Documentation },
                                    { "insertText", c.InsertText }
                                });
                        lspStatus = string.IsNullOrEmpty(lsp.LastCompletionDiagnostic) ? "ok" : lsp.LastCompletionDiagnostic;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] completion: " + ex.Message);
                    lspStatus = "error: " + ex.Message;
                }
                PostResponse(reqId, new Dictionary<string, object> { { "items", items }, { "lsp", lspStatus } });
            });
        }

        /// <summary>LSP hover request from Monaco. Syncs the current buffer (needed to resolve the symbol).</summary>
        private void HandleHover(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            Task.Run(() =>
            {
                string contents = null;
                try
                {
                    EnsureLspStarted();
                    var lsp = LspClient.Active;
                    if (lsp != null && lsp.IsRunning)
                    {
                        var resp = lsp.GetHover(_lspFileName, Math.Max(0, line - 1), Math.Max(0, column - 1), buffer);
                        contents = ExtractHoverString(resp);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] hover: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "contents", contents } });
            });
        }

        /// <summary>
        /// Diagnostics request from Monaco (debounced after edits + once after load). Runs the hybrid
        /// ModernEmbeditorDiagnostics over the LIVE buffer + LIVE editable ranges — Monaco passes its
        /// decoration-tracked ranges because slots grow as the user types, so the load-time
        /// _editableRanges snapshot would be stale. Runs off the UI thread (the LSP sub-pass blocks),
        /// then posts back a unified marker list for setModelMarkers.
        /// </summary>
        private void HandleDiagnostics(string json)
        {
            int reqId; string buffer; List<int[]> ranges;
            if (!ParseDiagnosticsRequest(json, out reqId, out buffer, out ranges)) return;
            Task.Run(() =>
            {
                var markers = new List<Dictionary<string, object>>();
                try
                {
                    markers = ModernEmbeditorDiagnostics.Compute(
                        _lspFileName,
                        buffer ?? _sourceText,
                        (ranges != null && ranges.Count > 0) ? ranges : _editableRanges,
                        _procedureName);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] diagnostics: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "markers", markers } });
            });
        }

        // Parses a diagnostics request: reqId, the live buffer text, and the live editable ranges
        // (an array of [start,end] line pairs from Monaco's tracked decorations).
        private bool ParseDiagnosticsRequest(string json, out int reqId, out string buffer, out List<int[]> ranges)
        {
            reqId = 0; buffer = null; ranges = null;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return false;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                if (data.ContainsKey("buffer")) buffer = data["buffer"] as string;
                if (data.ContainsKey("ranges"))
                {
                    var arr = data["ranges"] as object[];
                    if (arr != null)
                    {
                        ranges = new List<int[]>();
                        foreach (var item in arr)
                        {
                            var pair = item as object[];
                            if (pair != null && pair.Length >= 2)
                                ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) });
                        }
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Ctrl+D from Monaco — open the NATIVE structure designer for the WINDOW/REPORT at the caret
        /// (task 0a2ac0cb, literal-source mode). Validates here (structure detection + editable-slot
        /// guard + edit-vs-create mode), responds immediately so Monaco can arm its tracked splice
        /// target, then defers the designer open off this reentrant WebView2 stack (same rule as save).
        /// The designer's merges stream back as 'designerSplice' messages; tab close ends the session
        /// with 'designerClosed'.
        /// </summary>
        private void HandleOpenDesigner(string json)
        {
            int reqId = 0, line = 0;
            string buffer = null;
            List<int[]> ranges = null;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                if (data.ContainsKey("line")) line = Convert.ToInt32(data["line"]);
                if (data.ContainsKey("buffer")) buffer = data["buffer"] as string;
                if (data.ContainsKey("ranges"))
                {
                    var arr = data["ranges"] as object[];
                    if (arr != null)
                    {
                        ranges = new List<int[]>();
                        foreach (var item in arr)
                        {
                            var pair = item as object[];
                            if (pair != null && pair.Length >= 2)
                                ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) });
                        }
                    }
                }
            }
            catch { return; }
            if (buffer == null || ranges == null) { PostDesignerRefusal(reqId, "Designer request was malformed."); return; }

            if (StructureDesignerService.IsActive)
            {
                StructureDesignerService.ActivateCurrent(_panel);
                PostDesignerRefusal(reqId, "A structure designer is already open — close its tab first.");
                return;
            }

            Func<int, int, bool> editable = (s, e2) =>
            {
                foreach (var r in ranges) if (s >= r[0] && e2 <= r[1]) return true;
                return false;
            };

            var hit = ClarionAppDataReader.FindStructureAtLine(buffer, line);
            var lines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            string structureText, label;
            bool isWindow;
            int startLine, endLine;

            if (hit.Found)
            {
                if (!editable(hit.StartLine, hit.EndLine))
                {
                    PostDesignerRefusal(reqId, "This " + hit.Type + " is in generated code — the designer only works on editable embed code.");
                    return;
                }
                startLine = hit.StartLine; endLine = hit.EndLine;
                structureText = string.Join("\n", lines.Skip(startLine - 1).Take(endLine - startLine + 1));
                label = string.IsNullOrEmpty(hit.Name) ? "CAWindow" : hit.Name;
                isWindow = hit.Type == "WINDOW";
            }
            else
            {
                // Create-new mode: a BLANK editable line becomes a fresh WINDOW.
                bool lineEditable = editable(line, line);
                bool lineBlank = line >= 1 && line <= lines.Length && lines[line - 1].Trim().Length == 0;
                if (!lineEditable || !lineBlank)
                {
                    PostDesignerRefusal(reqId, lineEditable
                        ? "Put the caret inside a WINDOW/REPORT, or on a blank line to create a new window."
                        : "The designer only works in editable embed code.");
                    return;
                }
                startLine = line; endLine = line;
                label = "NewWindow";
                isWindow = true;
                structureText = "NewWindow WINDOW('New Window'),AT(,,200,120),GRAY,SYSTEM\n" +
                                "         \n" +
                                "       END";
            }

            // Tell Monaco we're going ahead — it arms a decoration-tracked splice target for [start..end].
            PostResponse(reqId, new Dictionary<string, object>
            {
                { "ok", true }, { "mode", hit.Found ? "edit" : "insert" },
                { "startLine", startLine }, { "endLine", endLine }, { "type", isWindow ? "WINDOW" : "REPORT" }
            });

            // Defer the open off this reentrant WebView2 message-handler stack (save's hard-won rule).
            Action open = () =>
            {
                string err = StructureDesignerService.Open(structureText, label, isWindow, _panel,
                    onBufferChanged: text => PostDesignerMessage("designerSplice", text, null),
                    onClosed: finalText =>
                    {
                        PostDesignerMessage("designerClosed", finalText, null);
                        BringToFront();   // the scratch tab auto-closed after the merge — hand focus back here
                    });
                if (err != null) PostDesignerMessage("designerClosed", null, err);
            };
            if (_panel != null && _panel.IsHandleCreated) _panel.BeginInvoke(open);
            else open();
        }

        private void PostDesignerRefusal(int reqId, string message)
        {
            PostResponse(reqId, new Dictionary<string, object> { { "ok", false }, { "message", message } });
        }

        /// <summary>Push a designer-session event to Monaco (UI-thread marshalled, same as ApplySettings).</summary>
        private void PostDesignerMessage(string type, string text, string message)
        {
            string json;
            try
            {
                var d = new Dictionary<string, object> { { "type", type } };
                if (text != null) d["text"] = text;
                if (message != null) d["message"] = message;
                json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(d);
            }
            catch { return; }
            Action post = () =>
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                try { _webView.CoreWebView2.PostWebMessageAsJson(json); } catch { }
            };
            try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(post); else post(); }
            catch { }
        }

        /// <summary>
        /// Persist the dev's editor settings (from the gear panel) and broadcast them to every open
        /// Modern Embeditor tab so the change is consistent across tabs. Persist failures are logged but
        /// don't block the broadcast — the live editors still reflect the new options for this session.
        /// </summary>
        // Small fixed-cap parse for the tiny save* bridge payloads (cursor / bookmarks / settings). These
        // are page-supplied (untrusted) and bounded by design — refuse to materialize an oversized payload
        // BEFORE deserializing rather than trimming after. (Security gate finding.)
        private const int MaxBridgeJsonBytes = 65536;   // 64 KB — far above any legit save* message
        private static Dictionary<string, object> ParseBoundedBridgeJson(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxBridgeJsonBytes) return null;
            try { return new JavaScriptSerializer { MaxJsonLength = MaxBridgeJsonBytes }.DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        private void HandleSaveSettings(string json)
        {
            try
            {
                var data = ParseBoundedBridgeJson(json);
                var sd = (data != null && data.ContainsKey("settings")) ? data["settings"] as Dictionary<string, object> : null;
                if (sd == null) return;
                var settings = ModernEmbeditorSettings.FromDict(sd);
                try { settings.Save(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveSettings persist: " + ex.Message); }
                ApplySettingsToAll(settings); // broadcast to every open tab (incl. this one — idempotent)
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveSettings: " + ex.Message); }
        }

        /// <summary>Push the given settings to this tab's Monaco (gear panel + live updateOptions).</summary>
        public void ApplySettings(ModernEmbeditorSettings settings)
        {
            if (settings == null) return;
            string sjson;
            try { sjson = new JavaScriptSerializer().Serialize(settings.ToDict()); }
            catch { return; }
            Action post = () =>
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                try { _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"applySettings\",\"settings\":" + sjson + "}"); }
                catch { }
            };
            try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(post); else post(); }
            catch { }
        }

        /// <summary>Broadcast editor settings to all open Modern Embeditor tabs (mirrors ApplyThemeToAll).</summary>
        public static void ApplySettingsToAll(ModernEmbeditorSettings settings)
        {
            lock (_instances) { foreach (var inst in _instances) inst.ApplySettings(settings); }
        }

        /// <summary>
        /// Persist the Find/Replace dropdown history (sent by JS as full arrays) and broadcast the saved
        /// lists to every open tab so all tabs converge. The incoming list is authoritative, so per-entry
        /// delete and "clear history" stick. Persist failures are logged but never block the broadcast.
        /// </summary>
        private void HandleSaveHistory(string json)
        {
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return;
                var find = ToStringList(data, "find");
                var replace = ToStringList(data, "replace");
                var proc = ToStringList(data, "proc");
                EnsureHistoryScope();
                List<string> savedFind, savedReplace;
                ModernEmbeditorHistory.Save(_histSolutionPath, _histProcKey, find, replace, proc, out savedFind, out savedReplace);
                // Broadcast solution-wide lists only — each tab keeps its own procedure's recent terms.
                ApplyHistoryToAll(savedFind, savedReplace);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveHistory: " + ex.Message); }
        }

        /// <summary>Persist the cursor position (sent on Ctrl+S) per solution+procedure for restore-on-open.</summary>
        private void HandleSaveCursor(string json)
        {
            try
            {
                var data = ParseBoundedBridgeJson(json);
                if (data == null) return;
                int line = data.ContainsKey("line") ? Convert.ToInt32(data["line"]) : 0;
                int column = data.ContainsKey("column") ? Convert.ToInt32(data["column"]) : 0;
                if (line < 1) return;
                EnsureHistoryScope();
                ModernEmbeditorState.SaveCursor(_histSolutionPath, _histProcKey, line, column);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveCursor: " + ex.Message); }
        }

        /// <summary>Persist the bookmark line set (sent whenever it changes) per solution+procedure.</summary>
        private void HandleSaveBookmarks(string json)
        {
            try
            {
                var data = ParseBoundedBridgeJson(json);
                if (data == null) return;
                var lines = new List<int>();
                object o;
                if (data.TryGetValue("bookmarks", out o) && o is object[])
                {
                    // Bound ingestion: stop collecting once we have comfortably more than the persist cap
                    // (200) so a hostile/oversized array from the page can't force a huge allocation before
                    // CleanLines trims it. (Security gate finding.)
                    var arr = (object[])o;
                    for (int i = 0; i < arr.Length && lines.Count < 1000; i++)
                        if (arr[i] != null) { try { lines.Add(Convert.ToInt32(arr[i])); } catch { } }
                }
                EnsureHistoryScope();
                ModernEmbeditorState.SaveBookmarks(_histSolutionPath, _histProcKey, lines);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveBookmarks: " + ex.Message); }
        }

        /// <summary>
        /// Cache the Monaco selection pushed by the page (onDidChangeCursorSelection). Mirrors the
        /// saveCursor/saveBookmarks push model — read by the embeditor_get_selection MCP tool, no round-trip.
        /// The JS side caps the text at 10 KB chars, so even an all-escaped selection stays under
        /// MaxBridgeJsonBytes (64 KB) and the message is never dropped; `truncated` flags any clipping.
        /// </summary>
        private void HandleSelectionChanged(string json)
        {
            try
            {
                var data = ParseBoundedBridgeJson(json);
                if (data == null) return;
                string text = data.ContainsKey("text") ? (data["text"] as string ?? "") : "";
                bool truncated = data.ContainsKey("truncated") && data["truncated"] is bool && (bool)data["truncated"];
                int sl = data.ContainsKey("startLine") ? Convert.ToInt32(data["startLine"]) : 0;
                int sc = data.ContainsKey("startColumn") ? Convert.ToInt32(data["startColumn"]) : 0;
                int el = data.ContainsKey("endLine") ? Convert.ToInt32(data["endLine"]) : 0;
                int ec = data.ContainsKey("endColumn") ? Convert.ToInt32(data["endColumn"]) : 0;
                // A real selection has a non-empty range. An empty range (click/caret move) reports
                // hasSelection=false so the tool can say "nothing highlighted" on click-away.
                bool has = sl > 0 && (sl != el || sc != ec);

                _selText = has ? text : "";
                _selStartLine = sl; _selStartCol = sc; _selEndLine = el; _selEndCol = ec;
                _selHasSelection = has;
                _selTruncated = has && truncated;   // no selection ⇒ nothing to truncate

                var snap = BuildSelectionDict();
                lock (_selSnapLock) { _lastFocusedSelection = snap; }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] selectionChanged: " + ex.Message); }
        }

        private Dictionary<string, object> BuildSelectionDict()
        {
            return new Dictionary<string, object>
            {
                { "procedure", _procedureName ?? "" },
                { "hasSelection", _selHasSelection },
                { "truncated", _selTruncated },
                { "text", _selText ?? "" },
                { "startLine", _selStartLine },
                { "startColumn", _selStartCol },
                { "endLine", _selEndLine },
                { "endColumn", _selEndCol }
            };
        }

        /// <summary>This tab's current Monaco selection snapshot (procedure, text, range, hasSelection).</summary>
        public Dictionary<string, object> GetSelectionSnapshot()
        {
            return BuildSelectionDict();
        }

        /// <summary>
        /// The selection from the FOCUSED Modern Embeditor; if focus is momentarily ambiguous, the last
        /// snapshot any tab reported; null only when no Modern Embeditor has ever reported one. Read by the
        /// embeditor_get_selection MCP tool on the UI thread.
        /// </summary>
        public static Dictionary<string, object> GetFocusedSelection()
        {
            var view = FocusedModernView();
            if (view != null) return view.GetSelectionSnapshot();
            // Fall back to the last snapshot ONLY while a Monaco CA Embeditor is genuinely still open but
            // unfocused (ambiguous-focus case). If no Modern tab is open, there is no CA Embeditor — return
            // null so the tool says so, instead of serving a stale snapshot left behind by a closed tab.
            lock (_instances) { if (_instances.Count == 0) return null; }
            lock (_selSnapLock) { return _lastFocusedSelection; }
        }

        /// <summary>
        /// Resolve (once) the history scope from the IDE: the open solution (folder) and an app::procedure
        /// key (the "This procedure" group). Cached for this tab's lifetime.
        /// </summary>
        private void EnsureHistoryScope()
        {
            if (_histScopeResolved) return;
            _histScopeResolved = true;
            try { _histSolutionPath = EditorService.GetOpenSolutionPath(); } catch { _histSolutionPath = null; }
            string appName = null;
            try
            {
                var info = new AppTreeService().GetAppInfo();
                if (info != null && info.ContainsKey("name") && info["name"] != null) appName = info["name"].ToString();
            }
            catch { }
            string key = ((appName ?? "") + "::" + (_procedureName ?? "")).Trim(':');
            _histProcKey = string.IsNullOrEmpty(key) ? "" : key;
        }

        /// <summary>Coerce a JSON array field (object[] from DeserializeObject) into a string list.</summary>
        private static List<string> ToStringList(Dictionary<string, object> d, string key)
        {
            var res = new List<string>();
            object o;
            if (d != null && d.TryGetValue(key, out o) && o is object[])
            {
                foreach (var item in (object[])o)
                    if (item != null) res.Add(item.ToString());
            }
            return res;
        }

        /// <summary>Push Find/Replace history to this tab's dropdowns.</summary>
        public void ApplyHistory(IList<string> find, IList<string> replace)
        {
            string fj = ModernEmbeditorHistory.ToJson(find);
            string rj = ModernEmbeditorHistory.ToJson(replace);
            Action post = () =>
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                try { _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"applyHistory\",\"find\":" + fj + ",\"replace\":" + rj + "}"); }
                catch { }
            };
            try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(post); else post(); }
            catch { }
        }

        /// <summary>Broadcast Find/Replace history to all open Modern Embeditor tabs.</summary>
        public static void ApplyHistoryToAll(IList<string> find, IList<string> replace)
        {
            lock (_instances) { foreach (var inst in _instances) inst.ApplyHistory(find, replace); }
        }

        private bool ParseRequest(string json, out int reqId, out int line, out int column, out string buffer)
        {
            reqId = 0; line = 0; column = 0; buffer = null;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return false;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                if (data.ContainsKey("line")) line = Convert.ToInt32(data["line"]);
                if (data.ContainsKey("column")) column = Convert.ToInt32(data["column"]);
                if (data.ContainsKey("buffer")) buffer = data["buffer"] as string;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Posts a {type:"response", reqId, data} message back to Monaco (marshaled to the UI thread).</summary>
        private void PostResponse(int reqId, Dictionary<string, object> data)
        {
            Action post = () =>
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                try
                {
                    var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    string json = ser.Serialize(new Dictionary<string, object>
                    {
                        { "type", "response" }, { "reqId", reqId }, { "data", data }
                    });
                    _webView.CoreWebView2.PostWebMessageAsJson(json);
                }
                catch { }
            };
            try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(post); else post(); }
            catch { }
        }

        /// <summary>Pulls a plain string out of an LSP textDocument/hover response (MarkupContent/string/array).</summary>
        private static string ExtractHoverString(Dictionary<string, object> resp)
        {
            if (resp == null) return null;
            object result = resp.ContainsKey("result") ? resp["result"] : null;
            var rd = result as Dictionary<string, object>;
            object contents = rd != null && rd.ContainsKey("contents") ? rd["contents"] : result;
            return HoverPartToString(contents);
        }

        private static string HoverPartToString(object contents)
        {
            if (contents == null) return null;
            var s = contents as string;
            if (s != null) return s;
            var d = contents as Dictionary<string, object>;
            if (d != null && d.ContainsKey("value")) return d["value"] as string;
            var list = contents as System.Collections.IEnumerable;
            if (list != null)
            {
                var sb = new StringBuilder();
                foreach (var part in list)
                {
                    string p = HoverPartToString(part);
                    if (!string.IsNullOrEmpty(p)) { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(p); }
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }
            return null;
        }

        private static string MakeLspFileName(string procName)
        {
            string baseName = string.IsNullOrWhiteSpace(procName) ? "modern_embeditor" : procName;
            var sb = new StringBuilder();
            foreach (char c in baseName) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString() + ".clw";
        }

        /// <summary>Put text on the Windows clipboard (Clarion-style Ctrl+X cut from the editor).</summary>
        private void HandleClipboard(string json)
        {
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(json) as Dictionary<string, object>;
                string text = (data != null && data.ContainsKey("text")) ? (data["text"]?.ToString() ?? "") : null;
                if (text != null) Clipboard.SetText(text.Length == 0 ? " " : text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] Clipboard error: " + ex.Message);
            }
        }

        private void PostSaveResult(bool ok, string message)
        {
            PostSaveResultOnce(ok, message);
            // Backup re-post on the next message-loop turn — delivery of the first post can race with
            // the embeditor open/close churn that just happened during the save.
            try { _panel?.BeginInvoke((Action)(() => PostSaveResultOnce(ok, message))); }
            catch { }
        }

        private void PostSaveResultOnce(bool ok, string message)
        {
            if (_webView == null || _webView.CoreWebView2 == null) return;
            string json = "{\"type\":\"saveResult\",\"ok\":" + (ok ? "true" : "false") +
                          ",\"message\":" + JsonString(message) + "}";
            try { _webView.CoreWebView2.PostWebMessageAsJson(json); } catch { }
        }

        /// <summary>Update the displayed source. Sends immediately if ready, else waits for the JS "ready".</summary>
        public void SetSource(string title, string sourceText, string language = null)
        {
            _title = title ?? _title;
            _sourceText = sourceText ?? "";
            if (language != null) _language = language;
            TitleName = "CA: " + _title;

            if (_isInitialized)
                SendSource();
        }

        private void SendSource()
        {
            if (_webView.CoreWebView2 == null) return;

            // Warm the language server as soon as the editor opens, so completion/hover/LSP-diagnostics
            // are ready by the time the dev uses them (self-heal if eager-start never fired).
            EnsureLspStarted();

            try
            {
                // Transfer source via the virtual host (temp file) to avoid huge postMessage payloads.
                string sourceFile = Path.Combine(_tempDir, "source.txt");
                File.WriteAllText(sourceFile, _sourceText ?? "", Encoding.UTF8);

                string settingsJson;
                try { settingsJson = new JavaScriptSerializer().Serialize(ModernEmbeditorSettings.Load().ToDict()); }
                catch { settingsJson = "null"; }

                string findHistJson = "[]", replHistJson = "[]", procHistJson = "[]";
                int cursorLine = 0, cursorColumn = 0;
                string bookmarksJson = "[]";
                try
                {
                    EnsureHistoryScope();
                    List<string> hf, hr, hp;
                    ModernEmbeditorHistory.Load(_histSolutionPath, _histProcKey, out hf, out hr, out hp);
                    findHistJson = ModernEmbeditorHistory.ToJson(hf);
                    replHistJson = ModernEmbeditorHistory.ToJson(hr);
                    procHistJson = ModernEmbeditorHistory.ToJson(hp);
                    List<int> bms;
                    ModernEmbeditorState.Load(_histSolutionPath, _histProcKey, out cursorLine, out cursorColumn, out bms);
                    bookmarksJson = ModernEmbeditorState.BookmarksJson(bms);
                }
                catch { }

                string json = "{\"type\":\"setSource\"," +
                    "\"title\":" + JsonString(_title) + "," +
                    "\"language\":" + JsonString(_language) + "," +
                    "\"isDark\":" + (_isDark ? "true" : "false") + "," +
                    "\"saveEnabled\":" + (_saveEnabled ? "true" : "false") + "," +
                    "\"editableRanges\":" + RangesJson() + "," +
                    "\"settings\":" + settingsJson + "," +
                    "\"findHistory\":" + findHistJson + "," +
                    "\"replaceHistory\":" + replHistJson + "," +
                    "\"procHistory\":" + procHistJson + "," +
                    "\"cursorLine\":" + cursorLine + "," +
                    "\"cursorColumn\":" + cursorColumn + "," +
                    "\"bookmarks\":" + bookmarksJson + "," +
                    "\"sourceUrl\":\"https://" + VIRTUAL_HOST + "/source.txt\"}";
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] SendSource error: " + ex.Message);
            }
        }

        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (_panel != null)
                _panel.BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"applyTheme\",\"isDark\":" + (isDark ? "true" : "false") + "}");
        }

        public static void ApplyThemeToAll(bool isDark)
        {
            lock (_instances)
            {
                foreach (var inst in _instances)
                    inst.ApplyTheme(isDark);
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "monaco-embeditor.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "monaco-embeditor.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "monaco-embeditor.html");
        }

        /// <summary>Serializes the editable ranges as a JSON array of [start,end] pairs (1-based, inclusive).</summary>
        private string RangesJson()
        {
            if (_editableRanges == null || _editableRanges.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < _editableRanges.Count; i++)
            {
                var r = _editableRanges[i];
                if (r == null || r.Length < 2) continue;
                if (sb.Length > 1) sb.Append(',');
                sb.Append('[').Append(r[0]).Append(',').Append(r[1]).Append(']');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 20);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ')
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string ExtractJsonValue(string json, string key)
        {
            if (json == null) return null;
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return null;
            if (json[idx] == 'n') return null;
            if (json[idx] == '"')
            {
                idx++;
                var sb = new StringBuilder();
                while (idx < json.Length)
                {
                    char c = json[idx];
                    if (c == '\\' && idx + 1 < json.Length)
                    {
                        char next = json[idx + 1];
                        if (next == '"') { sb.Append('"'); idx += 2; continue; }
                        if (next == '\\') { sb.Append('\\'); idx += 2; continue; }
                        if (next == 'n') { sb.Append('\n'); idx += 2; continue; }
                        if (next == 'r') { sb.Append('\r'); idx += 2; continue; }
                        if (next == 't') { sb.Append('\t'); idx += 2; continue; }
                        sb.Append(c); idx++; continue;
                    }
                    if (c == '"') break;
                    sb.Append(c);
                    idx++;
                }
                return sb.ToString();
            }
            int start = idx;
            while (idx < json.Length && json[idx] != ',' && json[idx] != '}') idx++;
            return json.Substring(start, idx - start).Trim();
        }

        public override void Dispose()
        {
            lock (_instances) { _instances.Remove(this); }
            if (_webView != null)
            {
                _webView.Dispose();
                _webView = null;
            }
            if (_panel != null)
            {
                _panel.Dispose();
                _panel = null;
            }
            CleanupTempDir();
            base.Dispose();
        }

        /// <summary>Shutdown hook: dispose every open Modern Embeditor's WebView2 on the UI thread, before
        /// native IDE teardown, to avoid the WebView2 &lt;-&gt; native focus deadlock. Idempotent + best-effort.</summary>
        public static void DisposeAllForShutdown()
        {
            List<ModernEmbeditorViewContent> snapshot;
            lock (_instances) { snapshot = new List<ModernEmbeditorViewContent>(_instances); }
            foreach (var inst in snapshot)
            {
                try { inst.Dispose(); } catch { }
            }
        }

        private void CleanupTempDir()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
                _tempDir = null;
            }
        }
    }
}
