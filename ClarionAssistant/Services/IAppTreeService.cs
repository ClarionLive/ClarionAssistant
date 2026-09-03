using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The Clarion application tree and embeditor, as McpToolRegistry needs them (ticket d051fbd1).
    ///
    /// EVERY MEMBER HERE IS PERMANENTLY IDE-ONLY, and unlike IEditorService that is not a matter
    /// of convenience. These drive Clarion's own 32-bit assemblies — ClaGenEditor,
    /// CommonGenEditor, CWBinding — against the proprietary binary .app format. They are not
    /// "IDE integration" another editor could reimplement: they ARE Clarion. Hosting them out of
    /// process would mean building a headless Clarion, which is roughly what ClarionCL already is,
    /// and ClarionCL cannot drive the interactive embeditor.
    ///
    /// SO WHY AN INTERFACE AT ALL, if nothing else will ever implement it? Purely so the ONE
    /// shared McpToolRegistry.cs compiles in both builds. The alternative was carving a
    /// 3,400-line registration method in two, or duplicating tool definitions — and duplication
    /// is precisely the failure the indexer's header warns about, where three of six shared files
    /// silently diverged. One source of truth, gated at registration, beats two that drift.
    ///
    /// The standalone server does NOT implement this and does not register the tools that need
    /// it. Nothing here should ever grow a "portable" implementation; if a member turns out to
    /// have a non-IDE answer, it belongs on IWorkspaceContext instead.
    /// </summary>
    public interface IAppTreeService
    {
        // --- app tree ---
        Dictionary<string, object> GetAppInfo();
        /// <summary>
        /// Full path of the dictionary the OPEN APP is bound to (its Global Properties "Dictionary
        /// File"), read off the live App.FileSchema.DataDictionary.FileName. Null when no app is
        /// open or the app has no dictionary. This is a different thing from the dictionary open in
        /// the IDE's dictionary EDITOR (which export_dctx resolves) - GitHub #210 was the assistant
        /// having no way to ask for this one, so it guessed from files on disk and guessed wrong.
        /// UI thread (live IDE object access).
        /// </summary>
        string GetAppDictionaryPath();
        /// <summary>
        /// The open app's dictionary read LIVE off App.FileSchema.DataDictionary.Tables - name, prefix,
        /// driver, fields, keys, relations - always current, no .dctx export, no .schemagraph.db.
        /// Existed on AppTreeService since the Modern Data pad; surfaced here for get_app_dictionary
        /// (GitHub #210). Empty list when no app or no dictionary. UI thread.
        /// </summary>
        List<ClarionAppDataReader.TableDef> ReadLiveDictionaryTables();
        /// <summary>
        /// The multi-app ambiguity guard, for tools that answer "the open app": distinct open app
        /// views, whether the focused window is itself one of them, and their .app file names for
        /// the error message. With 2+ apps and focus elsewhere, FindAppViewContent's answer is
        /// "whichever the IDE listed first" - fail closed instead (GitHub #210, pipeline run 1).
        /// </summary>
        int CountOpenAppViews();
        bool IsActiveWindowAppView();
        List<string> GetOpenAppFileNames();
        List<string> GetProcedureNames();
        List<Dictionary<string, object>> GetProcedureDetails();
        string SelectProcedure(string procedureName);

        // --- embeditor lifecycle ---
        string OpenProcedureEmbed(string procedureName);
        string OpenProcedureEmbed(string procedureName, int charDelayMs);
        Dictionary<string, object> GetEmbedInfo();
        string SaveAndCloseEmbeditor();
        string CancelEmbeditor();

        // --- embed slots ---
        string GetEmbeditorSource();
        string SearchEmbeditorSource(string pattern, int contextLines = 5);
        string GetEmbedContent(int lineNumber);
        string WriteEmbedContentByLine(int lineNumber, string code);
        string WriteEmbedContentByLine(int lineNumber, string code, bool reindent);
        List<Dictionary<string, object>> ListEmbeds();
        string NavigateEmbed(string direction, bool filledOnly);

        /// <summary>
        /// Takes IEditorService rather than the concrete EditorService. Widening the parameter is
        /// what keeps this interface free of the IDE-importing class; EditorService implements
        /// IEditorService, so every existing caller is unaffected.
        /// </summary>
        string FindEmbed(string searchName, IEditorService editorService);

        /// <summary>
        /// Block until the embeditor has actually opened, or the timeout elapses. Returns false on
        /// timeout. The registry called ModernEmbeditorLauncher.WaitForEmbedOpen(appTree, ms)
        /// statically; that class is IDE-coupled, so the call moved behind this interface. The open
        /// is asynchronous, and without this the tool reports success before the editor is ready.
        /// </summary>
        bool WaitForEmbedOpen(int timeoutMs);

        /// <summary>
        /// Force the IDE's lazy ABC class load now. Was ModernEmbeditorLauncher.WarmupAbc().
        /// </summary>
        string WarmupAbc();

        /// <summary>
        /// Apply several embed-slot edits in ONE transient open/write/save/close round-trip,
        /// leaving no interactive session open. Was ModernEmbeditorSaver.ApplyLineEdits().
        /// Preferred over repeated write_embed_content for large procedures, where driving the
        /// live PWEE editor repeatedly is unstable - which is why that method exists at all.
        /// </summary>
        string ApplyEmbedLineEdits(string procName, IList<KeyValuePair<int, string>> edits, out bool ok);

        // --- TXA exchange ---
        string ExportTxa(string txaPath);
        string ImportTxa(string txaPath, string clashMode);

        // --- diagnostics ---
        string DumpObjectApi(string path);
        string DumpAppMainControlApi();
    }
}
