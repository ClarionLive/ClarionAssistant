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
    public class ModernEmbeditorViewContent : AbstractViewContent, IMonacoEditorHost
    {
        // Converge step 3: _panel is now the reusable MonacoEditorControl (which IS a Panel), so every
        // designer/marshal/Control site that treated it as a Panel still compiles. The control owns the
        // WebView2 + page nav + JS<->C# transport + inbound dispatch; this view implements IMonacoEditorHost.
        private MonacoEditorControl _panel;
        private bool _isInitialized;   // mirrored from the control via OnEditorNavigationCompleted

        private string _title;
        private string _sourceText;
        private string _language;
        private bool _isDark = true;
        private List<int[]> _editableRanges; // 1-based inclusive [start,end] embed-slot ranges
        private readonly string _procedureName;     // set when opened from the picker (enables save)
        private List<string> _originalSlotTexts;     // baseline slot contents for change detection
        private readonly bool _saveEnabled;
        private readonly string _lspFileName;        // synthetic .clw URI for LSP completion/hover requests

        // File mode (ticket 564aa142): the tab edits a plain source file on disk (.clw/.inc/...) instead of
        // an embeditor snapshot. Save = encoding-preserving file write; no slot machinery, no Data pad refresh,
        // no designer. _lspFileName is the REAL path so the LSP resolves includes/symbols against the file.
        private readonly string _filePath;
        private string _fileIdentity;                // true file-ID (vol serial + file index) for tab DEDUP; resolves all aliases incl. hard links (item 3)
        private readonly bool _fileMode;
        private Encoding _fileEncoding;              // detected at open, RE-DETECTED on reload (pipeline item 4)
        private string _fileEol = "\r\n";            // detected dominant EOL at open; non-Clarion files keep their style (item 5)
        private string _fileDiskSig;                 // disk fingerprint (mtimeTicks:length) for changed-on-disk detection (item 2)
        private string _fileOverwriteArmedSig;       // the EXACT disk version the user was warned about; null = not armed (item 2)
        // Host mirror of the page's live file-mode buffer + dirty flag, so a tab close can save WITHOUT an async
        // round-trip into the WebView2 (pipeline CRITICAL — silent data loss on close).
        private string _fileLiveText;
        private bool _fileDirty;
        private bool _disposed;

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
        private IDisposable _settingsReg;   // registration in MonacoSettingsBroadcaster (cross-surface gear-settings sync, deac3d16)
        // Set during IDE shutdown (DisposeAllForShutdown) so per-tab Dispose takes a NONINTERACTIVE recovery path —
        // no modal prompt per dirty tab (avoids a shutdown modal storm). (pipeline Run-3 adversary)
        private static volatile bool _shuttingDown;

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
            _panel?.GotoRoutine(name);
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

        // ── Field-drag support (ticket 0bada8de) ───────────────────────────────────────────────────────
        // The Data pad's host-owned DoDragDrop must (a) belt every live Monaco webview's external-drop OFF for
        // the drag duration so the native dictionary payload can't land — and insert the wrong window-control
        // string — inside a code editor, and (b) on a release over a Monaco editor, insert the plain reference
        // at THAT editor's cursor. These expose the live instances to FieldDropService without it taking a
        // WebView2 type dependency (it belts via the Control base + reflection).

        /// <summary>The live Monaco editor webviews (as Controls), for the field-drag external-drop belt.</summary>
        public static List<System.Windows.Forms.Control> LiveMonacoWebViews()
        {
            var list = new List<System.Windows.Forms.Control>();
            lock (_instances)
                foreach (var inst in _instances)
                {
                    var wv = inst._panel != null ? inst._panel.WebView : null;
                    if (wv != null) list.Add(wv);
                }
            return list;
        }

        /// <summary>If a VISIBLE Monaco editor webview covers the given screen point, move its caret to the editor
        /// position under that point and return true; otherwise false. Used during a Data-pad field DRAG so the
        /// caret tracks the mouse (the drop then lands where the pointer is).</summary>
        public static bool TryMoveMonacoCaretAt(int screenX, int screenY)
        {
            lock (_instances)
                foreach (var inst in _instances)
                {
                    var wv = inst._panel != null ? inst._panel.WebView : null;
                    if (wv == null || !wv.IsHandleCreated || !wv.Visible) continue;
                    try
                    {
                        if (wv.RectangleToScreen(wv.ClientRectangle).Contains(screenX, screenY))
                        {
                            inst._panel.MoveCaretToScreenPoint(screenX, screenY);
                            return true;
                        }
                    }
                    catch { }
                }
            return false;
        }

        /// <summary>If a VISIBLE Monaco editor webview covers the given screen point, insert <paramref name="text"/>
        /// at that editor's cursor and return true; otherwise false (the release wasn't over an editor). Mirrors
        /// the designer/pad rect hit-test so z-order/DPI behave consistently.</summary>
        public static bool TryInsertAtMonacoCursor(int screenX, int screenY, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            lock (_instances)
                foreach (var inst in _instances)
                {
                    var wv = inst._panel != null ? inst._panel.WebView : null;
                    if (wv == null || !wv.IsHandleCreated || !wv.Visible) continue;
                    try
                    {
                        if (wv.RectangleToScreen(wv.ClientRectangle).Contains(screenX, screenY))
                        {
                            // Atomic point-based insert: lands exactly where released (the page resolves the
                            // position from these coords), not at a separately-tracked caret that can go stale.
                            inst._panel.InsertTextAtScreenPoint(text, screenX, screenY);
                            inst.BringToFront();
                            inst._panel.FocusEditor();   // keyboard focus so the dev can type right after the drop
                            return true;
                        }
                    }
                    catch { }
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
            // Marshal the insert + tab-activation together (the call may arrive off the UI thread from the
            // Data pad). The control's InsertText also marshals internally, which is harmless here.
            Action post = () =>
            {
                _panel?.InsertText(text);
                // Bring THIS editor tab to the front so the developer can start typing immediately after a
                // Data-pad double-click insert (the editor JS already does ed.focus() to place the caret).
                BringToFront();
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

            // Reusable Monaco surface; we are its host (IMonacoEditorHost). It self-inits on HandleCreated.
            _panel = new MonacoEditorControl(this, isDark, "monaco-embeditor.html", VIRTUAL_HOST);

            lock (_instances) { _instances.Add(this); }
            // Cross-surface gear-settings sync: receive applySettings from any other Monaco surface (another
            // embeditor or a source/default editor). HandleSaveSettings publishes through the same bus. (deac3d16)
            _settingsReg = Services.MonacoSettingsBroadcaster.Register(json => { try { _panel?.PostJson(json); } catch { } });
        }

        /// <summary>
        /// File mode — open a plain source file (.clw/.inc/.equ/...) for whole-buffer editing.
        /// Same Monaco page and language services as the embeditor, but save writes the file
        /// (encoding-preserving) and all embed/slot machinery is bypassed.
        /// </summary>
        public ModernEmbeditorViewContent(string filePath, bool isDark)
        {
            _filePath = Path.GetFullPath(filePath);
            _fileMode = true;
            _title = Path.GetFileName(_filePath);
            _fileIdentity = CanonicalFileId(_filePath);   // stable identity for dedup/state, survives path aliasing (item 3)
            _fileEncoding = DetectFileEncoding(_filePath);
            _sourceText = File.ReadAllText(_filePath, _fileEncoding);
            _fileEol = DetectEol(_sourceText);
            _fileDiskSig = ReadFileSignature(_filePath);
            _fileLiveText = _sourceText;
            _editableRanges = new List<int[]>();
            _language = LanguageForFile(_filePath);
            _isDark = isDark;
            _procedureName = null;
            _saveEnabled = true;
            _originalSlotTexts = new List<string>();
            _lspFileName = _filePath;     // real path → LSP sees the actual file
            TitleName = "CA: " + _title;

            // Reusable Monaco surface; we are its host (IMonacoEditorHost). It self-inits on HandleCreated.
            _panel = new MonacoEditorControl(this, isDark, "monaco-embeditor.html", VIRTUAL_HOST);

            lock (_instances) { _instances.Add(this); }
            // Cross-surface gear-settings sync: receive applySettings from any other Monaco surface (another
            // embeditor or a source/default editor). HandleSaveSettings publishes through the same bus. (deac3d16)
            _settingsReg = Services.MonacoSettingsBroadcaster.Register(json => { try { _panel?.PostJson(json); } catch { } });
        }

        /// <summary>The file this tab edits (file mode), else null.</summary>
        public string FilePath { get { return _filePath; } }

        /// <summary>Find the open file-mode tab for a path, or null. Used by the open command to dedup so the same
        /// file doesn't open in two tabs (→ last-save-wins). Matches on the UNION of two identities:
        ///  • the canonical file ID (vol serial + file index) — collapses path aliases AND hard links;
        ///  • the normalized full path — collapses same-path reopens even when the file ID CHURNS between opens
        ///    (external delete/recreate, atomic replace, branch switch, or an id:↔path: fallback transition).
        /// Either match reuses the existing tab. This covers the realistic cases, NOT every possible one: a reopen
        /// that changes BOTH the path alias AND the file ID at once (external replace + reopen via a different
        /// alias) still escapes dedup — tracked as follow-up 8348435a. (pipeline item 3 + Run-6/7 adversary)</summary>
        public static ModernEmbeditorViewContent FindByFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string id = CanonicalFileId(path);
            string full = null;
            try { full = Path.GetFullPath(path); } catch { }
            lock (_instances)
            {
                foreach (var inst in _instances)
                {
                    if (!inst._fileMode) continue;
                    if (string.Equals(inst._fileIdentity, id, StringComparison.OrdinalIgnoreCase)
                        || (full != null && string.Equals(inst._filePath, full, StringComparison.OrdinalIgnoreCase)))
                        return inst;
                }
            }
            return null;
        }

        /// <summary>Resolve a path to a STABLE physical-file identity so the same file opened via ANY alias —
        /// 8.3 short name, junction/symlink, subst/mapped-drive vs UNC, OR an NTFS HARD LINK — dedups to one tab
        /// (→ no last-save-wins). PRIMARY: the handle's volume serial + file index (a true file ID; the only thing
        /// that collapses hard links, which are distinct directory entries for one file record with no common
        /// pathname). FALLBACK when the file ID is unavailable: the normalized final path, then the plain full path.
        /// The "id:"/"path:" prefixes keep a file-ID identity and a path fallback from ever colliding. (item 3)</summary>
        private static string CanonicalFileId(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    IntPtr h = fs.SafeFileHandle.DangerousGetHandle();
                    NativeMethods.BY_HANDLE_FILE_INFORMATION info;
                    // A 0 file index means the filesystem didn't supply a real per-file ID (some network/virtual FS) —
                    // do NOT use it, or every such file would collapse to one "id:…:0" identity → wrong-file dedup.
                    // Fall through to the path identity instead.
                    if (NativeMethods.GetFileInformationByHandle(h, out info) && (info.FileIndexHigh != 0 || info.FileIndexLow != 0))
                        return "id:" + info.VolumeSerialNumber.ToString("x8") + ":" +
                               info.FileIndexHigh.ToString("x8") + info.FileIndexLow.ToString("x8");
                    // File ID unavailable — fall back to the normalized final path (resolves path aliases, not hard links).
                    var sb = new StringBuilder(512);
                    uint len = NativeMethods.GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);   // 0 = VOLUME_NAME_DOS | FILE_NAME_NORMALIZED
                    if (len > sb.Capacity) { sb.EnsureCapacity((int)len + 1); len = NativeMethods.GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0); }
                    if (len > 0)
                    {
                        string p = sb.ToString();
                        if (p.StartsWith(@"\\?\UNC\")) p = @"\\" + p.Substring(8);   // \\?\UNC\server\share → \\server\share
                        else if (p.StartsWith(@"\\?\")) p = p.Substring(4);          // \\?\C:\... → C:\...
                        return "path:" + p.ToLowerInvariant();
                    }
                }
            }
            catch { }
            try { return "path:" + Path.GetFullPath(path).ToLowerInvariant(); } catch { return "path:" + path.ToLowerInvariant(); }
        }

        /// <summary>Activate this tab (public wrapper over the deferred SelectWindow used elsewhere).</summary>
        public void ActivateTab() { BringToFront(); }

        /// <summary>
        /// BOM-detect, else ANSI. Clarion source is traditionally Windows-ANSI; decoding it as UTF-8
        /// would mangle high-bit characters and a save would then write the mangled text back. ReadAllText
        /// honors a BOM when present, so only the no-BOM default differs from stock behavior.
        /// </summary>
        private static Encoding DetectFileEncoding(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var bom = new byte[4];
                    int n = fs.Read(bom, 0, 4);
                    if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
                    if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
                    if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
                }
            }
            catch { }
            return Encoding.Default;
        }

        private static string LanguageForFile(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".clw": case ".inc": case ".equ": case ".int":
                case ".tpl": case ".tpw": case ".trn": case ".pr":
                    return "clarion";
                default:
                    return "plaintext";
            }
        }

        // ── IMonacoEditorHost (converge step 3) ─────────────────────────────────────────────────
        // The MonacoEditorControl owns the WebView2 lifecycle + nav + inbound action routing; it
        // calls these as page->host messages arrive. Each delegates to this view's existing handler,
        // unchanged. The fileMode designer guards that used to wrap the dispatch cases live here now.
        void IMonacoEditorHost.OnReady(MonacoEditorControl editor)
        {
            // On open: refresh the pad's IDE-sourced caches (whole-app .txa for Local/Global Data; live
            // dictionary snapshot for Other Files). Silent. File mode has no app context, so skip it.
            // (Was in the old OnHandleCreated; the "ready" message is the equivalent open moment.)
            if (!_fileMode) RefreshPadSources();
            SendSource();
        }

        void IMonacoEditorHost.OnSave(MonacoEditorControl editor, string rawJson) { HandleSave(rawJson); }
        void IMonacoEditorHost.OnClipboard(MonacoEditorControl editor, string rawJson) { HandleClipboard(rawJson); }
        void IMonacoEditorHost.OnCompletion(MonacoEditorControl editor, string rawJson) { HandleCompletion(rawJson); }
        void IMonacoEditorHost.OnHover(MonacoEditorControl editor, string rawJson) { HandleHover(rawJson); }
        void IMonacoEditorHost.OnDefinition(MonacoEditorControl editor, string rawJson) { HandleDefinition(rawJson); }
        void IMonacoEditorHost.OnDiagnostics(MonacoEditorControl editor, string rawJson) { HandleDiagnostics(rawJson); }
        void IMonacoEditorHost.OnSaveSettings(MonacoEditorControl editor, string rawJson) { HandleSaveSettings(rawJson); }
        void IMonacoEditorHost.OnSaveHistory(MonacoEditorControl editor, string rawJson) { HandleSaveHistory(rawJson); }
        void IMonacoEditorHost.OnSaveCursor(MonacoEditorControl editor, string rawJson) { HandleSaveCursor(rawJson); }
        void IMonacoEditorHost.OnSaveBookmarks(MonacoEditorControl editor, string rawJson) { HandleSaveBookmarks(rawJson); }
        void IMonacoEditorHost.OnSelectionChanged(MonacoEditorControl editor, string rawJson) { HandleSelectionChanged(rawJson); }
        void IMonacoEditorHost.OnFocusEditor(MonacoEditorControl editor) { BringToFront(); }   // Data-pad drag-drop
        void IMonacoEditorHost.OnReload(MonacoEditorControl editor) { HandleReload(); }        // file mode
        void IMonacoEditorHost.OnFileState(MonacoEditorControl editor, string rawJson) { HandleFileState(rawJson); }  // file mode

        // Designer needs an embeditor-backed procedure → file mode refuses. Guard lives here now.
        void IMonacoEditorHost.OnOpenDesigner(MonacoEditorControl editor, string rawJson) { if (!_fileMode) HandleOpenDesigner(rawJson); }
        void IMonacoEditorHost.OnOpenDesignerCreate(MonacoEditorControl editor, string rawJson) { if (!_fileMode) HandleOpenDesignerCreate(rawJson); }
        void IMonacoEditorHost.OnActivateDesigner(MonacoEditorControl editor) { if (!_fileMode) StructureDesignerService.ActivateCurrent(_panel); }

        void IMonacoEditorHost.OnEditorNavigationCompleted(MonacoEditorControl editor, bool success) { _isInitialized = success; }
        void IMonacoEditorHost.OnUnknownAction(MonacoEditorControl editor, string action, string rawJson) { }

        /// <summary>Persist the user's edits: parse the per-slot payload and run the save round-trip.</summary>
        private void HandleSave(string json)
        {
            if (_fileMode) { HandleFileSave(json); return; }

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

        /// <summary>
        /// File-mode save: write the whole buffer back to disk, preserving the encoding detected at open and
        /// normalizing line endings per language (CRLF for Clarion, else the file's own detected style). Overwrite
        /// consent is bound to the specific on-disk version the user was warned about: if the file changed on disk
        /// it reports the conflict, and a retry overwrites only that same version — if it changed AGAIN since the
        /// warning, it re-warns rather than clobbering a version the user never saw.
        /// Plain file I/O — safe on this stack, no native embeditor round-trip involved.
        /// </summary>
        private void HandleFileSave(string json)
        {
            string text; long seq = 0;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(json) as Dictionary<string, object>;
                text = (data != null && data.ContainsKey("text")) ? data["text"] as string : null;
                if (data != null && data.ContainsKey("seq")) long.TryParse(Convert.ToString(data["seq"]), out seq);
                if (text == null) { PostSaveResult(false, "Save failed: malformed payload (no text)."); return; }
            }
            catch (Exception ex)
            {
                PostSaveResult(false, "Save failed parsing the editor payload: " + ex.Message);
                return;
            }

            try
            {
                // Changed-on-disk guard. The signature (mtime+length) fingerprints the EXACT on-disk version.
                // Overwrite consent is bound to one specific version: a prior arm is honored only if the disk is
                // STILL at the version we warned about — if it changed AGAIN, re-warn (never clobber a version the
                // user never saw). (pipeline item 2 — debugger + adversary + security)
                string diskSig = ReadFileSignature(_filePath);
                if (diskSig != _fileDiskSig)
                {
                    if (_fileOverwriteArmedSig == null || _fileOverwriteArmedSig != diskSig)
                    {
                        _fileOverwriteArmedSig = diskSig;
                        PostSaveResult(false, _title + " changed on disk since it was opened/last saved. Save again to overwrite THIS disk version, or use Reload to discard your edits and pick up the disk version.");
                        return;
                    }
                    // armed for exactly this version — fall through and overwrite.
                }
                _fileOverwriteArmedSig = null;

                // Normalize EOL (Clarion=CRLF, else the file's detected style) + atomic write, bound to the disk
                // version validated above. Shared with the close-save path so the policy lives in one place. (items 2, 5)
                string outText = WriteFileMode(text, diskSig);

                _fileDiskSig = ReadFileSignature(_filePath);
                _sourceText = outText;
                _fileLiveText = outText;
                _fileDirty = false;
                TrySetHostDirty(false);
                PostSaveResult(true, "Saved " + _title, seq);
            }
            catch (Exception ex)
            {
                PostSaveResult(false, "Save failed: " + ex.Message);
            }
        }

        /// <summary>File mode: the page mirrors its live buffer + dirty flag here on each legal edit (and on blur),
        /// so a tab close can offer to save WITHOUT an async round-trip into the WebView2. (pipeline CRITICAL)</summary>
        private void HandleFileState(string json)
        {
            if (!_fileMode) return;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return;
                if (data.ContainsKey("text") && data["text"] is string) _fileLiveText = (string)data["text"];
                if (data.ContainsKey("dirty")) _fileDirty = Convert.ToBoolean(data["dirty"]);
                TrySetHostDirty(_fileDirty);
            }
            catch { }
        }

        /// <summary>Intentionally a NO-OP. We do NOT reflect dirty state onto AbstractViewContent.IsDirty.
        /// LIVE-IDE FINDING (John's test, 2026-06-13): setting IsDirty=true makes SharpDevelop's workbench run its
        /// OWN save-on-close path on tab close — but this WebView2 view has no real file binding, so the framework
        /// treats it as an untitled doc, pops a bogus "Save As" dialog (defaulting to ...\libsrc\win), then calls
        /// AbstractViewContent.Save(fileName) which we don't implement → System.NotImplementedException.
        /// Unsaved-edits-on-close is handled entirely by OUR Dispose() confirm (+ host buffer mirror), which writes
        /// the file directly and never touches the framework save machinery. Kept as a no-op so call sites are stable.</summary>
        private void TrySetHostDirty(bool dirty)
        {
            // deliberately empty — see summary (framework Save(fileName) is NotImplemented for this view)
        }

        /// <summary>A cheap disk-version fingerprint (last-write UTC ticks + byte length) that distinguishes the
        /// exact on-disk version. Returns null when the file is missing — so a delete/recreate reads as a change.</summary>
        private static string ReadFileSignature(string path)
        {
            try { var fi = new FileInfo(path); return fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : null; }
            catch { return null; }
        }

        /// <summary>Detect the file's dominant EOL so non-Clarion files round-trip their own style instead of being
        /// force-converted to CRLF (which would mark every line changed). (pipeline item 5)</summary>
        private static string DetectEol(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\r\n";
            int crlf = 0, lf = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') { if (i > 0 && text[i - 1] == '\r') crlf++; else lf++; }
            return lf > crlf ? "\n" : "\r\n";
        }

        /// <summary>Normalize every EOL to <paramref name="eol"/>. Collapse CRLF and lone CR to LF first (so a
        /// classic-Mac \r isn't left dangling), then expand to the target.</summary>
        private static string NormalizeEol(string text, string eol)
        {
            if (text == null) return "";
            string s = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return eol == "\n" ? s : s.Replace("\n", "\r\n");
        }

        /// <summary>Write atomically: encode to a temp file in the same directory, then replace the target, so a
        /// crash/lock mid-write can't truncate the real file. Re-checks the disk signature immediately before the
        /// replace and throws if the file changed AGAIN since the caller validated it (shrinks TOCTOU; fails closed).
        /// <paramref name="expectedSig"/> is the signature the caller approved (null = brand-new file).</summary>
        private static void WriteFileAtomic(string path, string text, Encoding enc, string expectedSig)
        {
            string dir = Path.GetDirectoryName(path);
            string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, text, enc);
            try
            {
                if (ReadFileSignature(path) != expectedSig)
                    throw new IOException("File changed on disk again during save.");
                if (File.Exists(path)) File.Replace(tmp, path, null);   // atomic on the same volume; preserves the original's ACLs
                else File.Move(tmp, path);                              // brand-new file
                tmp = null;
            }
            finally { if (tmp != null) { try { File.Delete(tmp); } catch { } } }
        }

        /// <summary>The EOL this file is written with: CRLF for Clarion (tooling expects it), else the file's own
        /// detected style. Single home for the language→EOL policy used by every write path.</summary>
        private string TargetEol { get { return _language == "clarion" ? "\r\n" : _fileEol; } }

        /// <summary>Shared file-mode write: normalize EOL per language (Clarion = CRLF, else the file's detected
        /// style) then atomically write, requiring the on-disk version to still match <paramref name="expectedSig"/>.
        /// Returns the normalized text actually written. Single home for the write policy used by BOTH the
        /// interactive save and the close-save path. (pipeline Run-2 dedup)</summary>
        private string WriteFileMode(string text, string expectedSig)
        {
            string outText = NormalizeEol(text, TargetEol);
            WriteFileAtomic(_filePath, outText, _fileEncoding, expectedSig);
            return outText;
        }

        /// <summary>Write the unsaved buffer to a UNIQUE recovery file next to the original, created EXCLUSIVELY
        /// (CreateNew) so it can neither overwrite a real sibling nor stomp an earlier recovery copy. Returns the
        /// path written. (pipeline Run-3: security + adversary flagged the old fixed ".unsaved.bak" name.)</summary>
        private string WriteRecoveryBackup(string text)
        {
            string baseName = _filePath + ".unsaved";
            for (int i = 0; ; i++)
            {
                string candidate = baseName + (i == 0 ? "" : "." + i) + ".bak";
                try
                {
                    using (var fs = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var sw = new StreamWriter(fs, _fileEncoding))
                        sw.Write(text);
                    return candidate;
                }
                catch (IOException) when (i < 1000 && File.Exists(candidate))
                {
                    // name already taken — try the next index
                }
            }
        }

        /// <summary>Close-time save (best-effort, no async round-trip) of the host-mirrored buffer. Uses the SAME
        /// changed-on-disk consent as the interactive save — <see cref="_fileDiskSig"/>, the version the user last
        /// saw — so it never silently clobbers an externally-changed file. On conflict OR write failure it does NOT
        /// drop the edits: it writes a sidecar backup next to the file and tells the user where it went.
        /// (pipeline Run-2: debugger + both Codex gates converged on the old silent-clobber/swallow path.)</summary>
        private void SaveOnClose()
        {
            try
            {
                WriteFileMode(_fileLiveText, _fileDiskSig);   // guarded: throws if the disk moved since the user last saw it
            }
            catch
            {
                // Conflict or write failure — preserve the edits WITHOUT overwriting the external change, to a
                // UNIQUE recovery file (never stomps a sibling or an earlier recovery copy).
                try
                {
                    string backup = WriteRecoveryBackup(NormalizeEol(_fileLiveText, TargetEol));
                    MessageBox.Show(_title + " changed on disk (or could not be written), so your unsaved edits were NOT applied to it.\n\nThey were saved to:\n" + backup,
                        "CA Editor — saved to backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { /* last-ditch at teardown; nothing more we can safely do */ }
            }
        }

        /// <summary>File-mode reload: re-read the file from disk and push it to the page (discards edits).</summary>
        private void HandleReload()
        {
            if (!_fileMode) return;
            try
            {
                // Re-detect encoding + EOL: the file may have been externally rewritten in a different encoding
                // since open. Reusing the stale open-time encoding would misdecode and a later save would write the
                // corrupted text back. (pipeline item 4 — adversary)
                _fileEncoding = DetectFileEncoding(_filePath);
                _sourceText = File.ReadAllText(_filePath, _fileEncoding);
                _fileEol = DetectEol(_sourceText);
                _fileDiskSig = ReadFileSignature(_filePath);
                _fileOverwriteArmedSig = null;
                _fileLiveText = _sourceText;
                _fileDirty = false;
                TrySetHostDirty(false);
                SendSource();
            }
            catch (Exception ex)
            {
                PostSaveResult(false, "Reload failed: " + ex.Message);
            }
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
                // Only kick the bundled-server self-heal when NO LSP is available at all. When the shared
                // ClarionLsp addin is active, SharedLspBridge.IsRunning is already true and the bundled
                // starter is a deliberate no-op — checking LspClient.Active alone would loop forever
                // ("server starting…") because we intentionally never start the bundled server in that case.
                if (!SharedLspBridge.IsRunning)
                    EmbeditorCompletionService.LspStarter?.Invoke();
            }
            catch { }
        }

        private void HandleCompletion(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            Task.Run(() =>
            {
                var items = new List<Dictionary<string, object>>();
                string lspStatus;
                try
                {
                    EnsureLspStarted();
                    // Route through SharedLspBridge: shared ClarionLsp when active, else bundled LspClient.
                    if (!SharedLspBridge.IsRunning) lspStatus = "starting";
                    else
                    {
                        // Pass the LIVE buffer (mirror HandleHover). Passing null made the shared server complete
                        // against an empty document → always "no suggestions" (John's test; root-caused with Bob).
                        var comps = SharedLspBridge.GetCompletion(_lspFileName, Math.Max(0, line - 1), Math.Max(0, column - 1), 2500, buffer);
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
                        // LastCompletionDiagnostic is bundled-only; surface it on the local path, else "ok".
                        var local = LspClient.Active;
                        lspStatus = (local != null && !string.IsNullOrEmpty(local.LastCompletionDiagnostic))
                            ? local.LastCompletionDiagnostic : "ok";
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

        /// <summary>F12 go-to-definition from the embed editor — resolve via the LSP (+ the C# CodeGraph
        /// fallback for cross-project) against the generated-source file, then open/position the target
        /// with MonacoSourceNavigator (same- or cross-file). (#40 / 2ba0ee17)</summary>
        private void HandleDefinition(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            Task.Run(() =>
            {
                bool navigated = false;
                try
                {
                    EnsureLspStarted();
                    if (SharedLspBridge.IsRunning)
                    {
                        var def = SharedLspBridge.GetDefinition(_lspFileName, Math.Max(0, line - 1), Math.Max(0, column - 1), buffer);
                        string targetPath; int targetLine0, targetChar0;
                        if (SharedLspBridge.TryGetFirstLocation(def, out targetPath, out targetLine0, out targetChar0))
                            navigated = MonacoSourceNavigator.NavigateToFileAndLine(targetPath, targetLine0 + 1, 1);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] definition: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "navigated", navigated } });
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
                    if (SharedLspBridge.IsRunning)
                    {
                        var resp = SharedLspBridge.GetHover(_lspFileName, Math.Max(0, line - 1), Math.Max(0, column - 1), buffer);
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
                        _procedureName,
                        embedSlotChecks: !_fileMode);   // file mode: LSP only, skip embed-slot heuristics
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
            int reqId, line; string buffer, templateTitle; List<int[]> ranges;
            if (!ParseDesignerRequest(json, out reqId, out line, out buffer, out ranges, out templateTitle)) return;
            if (buffer == null || ranges == null) { PostDesignerRefusal(reqId, "Designer request was malformed."); return; }

            if (StructureDesignerService.IsActive)
            {
                StructureDesignerService.ActivateCurrent(_panel);
                PostDesignerRefusal(reqId, "A structure designer is already open — close its tab first.");
                return;
            }

            var hit = ClarionAppDataReader.FindStructureAtLine(buffer, line);
            var lines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            if (hit.Found)
            {
                if (!RangeEditable(ranges, hit.StartLine, hit.EndLine))
                {
                    PostDesignerRefusal(reqId, "This " + hit.Type + " is in generated code — the designer only works on editable embed code.");
                    return;
                }
                string structureText = string.Join("\n", lines.Skip(hit.StartLine - 1).Take(hit.EndLine - hit.StartLine + 1));
                string label = string.IsNullOrEmpty(hit.Name) ? "CAWindow" : hit.Name;
                bool isWindow = hit.Type == "WINDOW";
                PostResponse(reqId, new Dictionary<string, object>
                {
                    { "ok", true }, { "mode", "edit" },
                    { "startLine", hit.StartLine }, { "endLine", hit.EndLine }, { "type", hit.Type }
                });
                OpenDesignerDeferred(structureText, label, isWindow, isWindow);
                return;
            }

            // Create-new mode: a BLANK editable line becomes a fresh structure.
            string refusal = ValidateCreateLine(ranges, lines, line);
            if (refusal != null) { PostDesignerRefusal(reqId, refusal); return; }

            // Native parity (task 1f10aa51): offer the New Structure templates from DEFAULTS.CLW —
            // the same file the native Ctrl+D picker reads. Monaco shows the picker and comes back via
            // 'openDesignerCreate'. No templates (file missing) -> legacy hardcoded seed, no picker.
            var templates = DefaultStructuresReader.Load();
            if (templates.Count > 0)
            {
                var list = templates.Select(t => (object)new Dictionary<string, object> { { "title", t.Title }, { "type", t.Kind } }).ToList();
                PostResponse(reqId, new Dictionary<string, object> { { "ok", true }, { "mode", "pickTemplate" }, { "templates", list } });
                return;
            }

            PostResponse(reqId, new Dictionary<string, object>
            {
                { "ok", true }, { "mode", "insert" },
                { "startLine", line }, { "endLine", line }, { "type", "WINDOW" }
            });
            OpenDesignerDeferred(FallbackSeed, "NewWindow", true, true);
        }

        /// <summary>
        /// Second leg of create-new: Monaco's template picker chose an entry — re-validate the line
        /// (the user may have typed while the picker was up), seed from the chosen DEFAULTS.CLW block,
        /// and open with the designer flags the block's kind dictates (WINDOW / APPLICATION / REPORT).
        /// </summary>
        private void HandleOpenDesignerCreate(string json)
        {
            int reqId, line; string buffer, templateTitle; List<int[]> ranges;
            if (!ParseDesignerRequest(json, out reqId, out line, out buffer, out ranges, out templateTitle)) return;
            if (buffer == null || ranges == null || string.IsNullOrEmpty(templateTitle))
            {
                PostDesignerRefusal(reqId, "Designer request was malformed.");
                return;
            }
            if (StructureDesignerService.IsActive)
            {
                StructureDesignerService.ActivateCurrent(_panel);
                PostDesignerRefusal(reqId, "A structure designer is already open — close its tab first.");
                return;
            }

            var lines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            string refusal = ValidateCreateLine(ranges, lines, line);
            if (refusal != null) { PostDesignerRefusal(reqId, refusal); return; }

            var template = DefaultStructuresReader.Load()
                .FirstOrDefault(t => string.Equals(t.Title, templateTitle, StringComparison.Ordinal));
            string structureText = template != null ? template.Source : FallbackSeed;
            string kind = template != null ? template.Kind : "WINDOW";

            // Scratch tab name = the template block's own label (e.g. Window / ProgressWindow / Report).
            string label = "NewStructure";
            var m = System.Text.RegularExpressions.Regex.Match(structureText, @"^\s*(\w+)");
            if (m.Success) label = m.Groups[1].Value;

            bool isWindowDesigner = kind != "REPORT";
            bool isWindowWindow = kind == "WINDOW";

            PostResponse(reqId, new Dictionary<string, object>
            {
                { "ok", true }, { "mode", "insert" },
                { "startLine", line }, { "endLine", line }, { "type", kind }
            });
            OpenDesignerDeferred(structureText, label, isWindowDesigner, isWindowWindow);
        }

        private const string FallbackSeed =
            "NewWindow WINDOW('New Window'),AT(,,200,120),GRAY,SYSTEM\n" +
            "         \n" +
            "       END";

        private static bool RangeEditable(List<int[]> ranges, int start, int end)
        {
            foreach (var r in ranges) if (start >= r[0] && end <= r[1]) return true;
            return false;
        }

        // null = OK; else the refusal message.
        private static string ValidateCreateLine(List<int[]> ranges, string[] lines, int line)
        {
            bool lineEditable = RangeEditable(ranges, line, line);
            bool lineBlank = line >= 1 && line <= lines.Length && lines[line - 1].Trim().Length == 0;
            if (lineEditable && lineBlank) return null;
            return lineEditable
                ? "Put the caret inside a WINDOW/REPORT, or on a blank line to create a new structure."
                : "The designer only works in editable embed code.";
        }

        /// <summary>Run the designer open off this reentrant WebView2 message-handler stack (save's rule).</summary>
        private void OpenDesignerDeferred(string structureText, string label, bool isWindowDesigner, bool isWindowWindow)
        {
            Action open = () =>
            {
                string err = StructureDesignerService.Open(structureText, label, isWindowDesigner, isWindowWindow, _panel,
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

        private bool ParseDesignerRequest(string json, out int reqId, out int line, out string buffer,
            out List<int[]> ranges, out string templateTitle)
        {
            reqId = 0; line = 0; buffer = null; ranges = null; templateTitle = null;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return false;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                if (data.ContainsKey("line")) line = Convert.ToInt32(data["line"]);
                if (data.ContainsKey("buffer")) buffer = data["buffer"] as string;
                if (data.ContainsKey("templateTitle")) templateTitle = data["templateTitle"] as string;
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

        private void PostDesignerRefusal(int reqId, string message)
        {
            PostResponse(reqId, new Dictionary<string, object> { { "ok", false }, { "message", message } });
        }

        /// <summary>Push a designer-session event to Monaco (UI-thread marshalled by the control).</summary>
        private void PostDesignerMessage(string type, string text, string message)
        {
            _panel?.PostDesignerMessage(type, text, message);
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
                // Persist + broadcast through the shared bus so the change reaches EVERY Monaco surface — other
                // embeditors AND source/default editors — not just ModernEmbeditorViewContent tabs. (deac3d16)
                Services.MonacoSettingsBroadcaster.SaveAndBroadcastFromBridge(json);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveSettings: " + ex.Message); }
        }

        // ApplySettings/ApplySettingsToAll were replaced by Services.MonacoSettingsBroadcaster (deac3d16): this
        // tab now receives applySettings as a registered bus sink (see ctor), so the broadcast reaches source
        // editors too — not just Modern Embeditor tabs.

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
            if (_fileMode)
            {
                // File tabs scope history/cursor/bookmarks by path — no app::procedure identity exists.
                // State (cursor/bookmarks/history) stays keyed on the PATH, not the file-ID dedup identity: a path
                // key is stable across external delete/recreate (a file-ID changes then, orphaning state) AND it
                // matches the key used before item 3, so existing users' saved state is NOT stranded on upgrade.
                _histProcKey = "file::" + _filePath.ToLowerInvariant();
                return;
            }
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
            _panel?.PostJson("{\"type\":\"applyHistory\",\"find\":" + fj + ",\"replace\":" + rj + "}");
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

        /// <summary>Posts a {type:"response", reqId, data} message back to Monaco (marshaled by the control).</summary>
        private void PostResponse(int reqId, Dictionary<string, object> data)
        {
            _panel?.PostResponse(reqId, data);
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

        private void PostSaveResult(bool ok, string message) { PostSaveResult(ok, message, 0); }

        private void PostSaveResult(bool ok, string message, long savedSeq)
        {
            PostSaveResultOnce(ok, message, savedSeq);
            // Backup re-post ONLY in embed mode — the embeditor open/close churn during a slot save can drop the
            // first post. A file save has no such churn, so the double-post is suppressed here: it widened the
            // dirty-clear race (a clean saveResult could land after an edit and wrongly clear the ●). (pipeline item 6)
            if (!_fileMode)
            {
                try { _panel?.BeginInvoke((Action)(() => PostSaveResultOnce(ok, message, savedSeq))); }
                catch { }
            }
        }

        private void PostSaveResultOnce(bool ok, string message, long savedSeq)
        {
            _panel?.PostSaveResult(ok, message, savedSeq);
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
            if (_panel == null || _panel.TempDir == null) return;

            // Warm the language server as soon as the editor opens, so completion/hover/LSP-diagnostics
            // are ready by the time the dev uses them (self-heal if eager-start never fired).
            EnsureLspStarted();

            try
            {
                // Transfer source via the virtual host (temp file) to avoid huge postMessage payloads.
                string sourceFile = Path.Combine(_panel.TempDir, "source.txt");
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
                    "\"fileMode\":" + (_fileMode ? "true" : "false") + "," +
                    "\"filePath\":" + JsonString(_filePath ?? "") + "," +
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
                _panel.PostJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] SendSource error: " + ex.Message);
            }
        }

        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            // The control recolors its own backdrop and posts {applyTheme} once it's live.
            _panel?.ApplyTheme(isDark);
        }

        public static void ApplyThemeToAll(bool isDark)
        {
            lock (_instances)
            {
                foreach (var inst in _instances)
                    inst.ApplyTheme(isDark);
            }
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
            // CRITICAL (pipeline): file mode edits a REAL file on disk. If the tab is closing with unsaved edits,
            // offer to save before teardown — otherwise the buffer is silently lost (the IDE tab close does not
            // prompt for our WebView2-hosted view). We hold the live buffer (_fileLiveText, mirrored from the page),
            // so the save is a synchronous file write — no async round-trip. Dispose the WebView2 FIRST so the
            // confirm MessageBox can't get stuck behind the live WebView2 (the documented native<->WebView2 deadlock).
            bool promptSave = _fileMode && _fileDirty && _fileLiveText != null && !_disposed;
            _disposed = true;

            lock (_instances) { _instances.Remove(this); }
            try { _settingsReg?.Dispose(); } catch { }
            _settingsReg = null;
            // Dispose the editor control (its WebView2 + temp dir) FIRST so the confirm MessageBox below
            // can't get stuck behind a live WebView2 (the documented native<->WebView2 focus deadlock).
            if (_panel != null)
            {
                _panel.Dispose();
                _panel = null;
            }

            if (promptSave)
            {
                try
                {
                    if (_shuttingDown)
                    {
                        // IDE shutdown: NO modal prompt (a Yes/No per dirty tab = modal storm). Preserve the edits to
                        // a unique recovery file without overwriting the real file unprompted; the user recovers on
                        // next open. This also backstops the residual close-race on the shutdown path. (Run-3 adversary)
                        WriteRecoveryBackup(NormalizeEol(_fileLiveText, TargetEol));
                    }
                    else
                    {
                        var r = MessageBox.Show("Save changes to " + _title + " before closing?",
                            "CA Editor — unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (r == DialogResult.Yes)
                            SaveOnClose();   // guarded by _fileDiskSig; recovery-preserves on conflict (never silent-clobbers)
                    }
                }
                catch { /* best-effort save-on-close; the tab is closing regardless */ }
            }

            base.Dispose();
        }

        /// <summary>Shutdown hook: dispose every open Modern Embeditor's WebView2 on the UI thread, before
        /// native IDE teardown, to avoid the WebView2 &lt;-&gt; native focus deadlock. Idempotent + best-effort.</summary>
        public static void DisposeAllForShutdown()
        {
            _shuttingDown = true;   // per-tab Dispose takes the noninteractive recovery path (no modal storm)
            List<ModernEmbeditorViewContent> snapshot;
            lock (_instances) { snapshot = new List<ModernEmbeditorViewContent>(_instances); }
            foreach (var inst in snapshot)
            {
                try { inst.Dispose(); } catch { }
            }
        }

    }
}
