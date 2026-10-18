using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// GH #205 pipeline run 2: an IWorkspaceContext read straight from the IDE, with no chat pane, for IdeCallbackHost's
    /// registry. editor_tab_sync's check is read-gated by the #266 path policy (McpPathGate.BuildPolicy), which takes its
    /// allowed roots from the workspace; the call-back host had none, so every solution file was refused and every write
    /// went ahead as "could not be checked".
    ///
    /// The same sources the chat pane's own workspace ends up with, minus the chat pane:
    ///   - solution: the IDE's open solution (EditorService.GetOpenSolutionPath, read on every call: a switch is seen at once);
    ///   - version: the IDE's effective Clarion version (EffectiveClarionVersion.CurrentConfig, the resolution the chat
    ///     pane, the LSP and the embeditor share), re-resolved at most every 10 s (it is an XML parse);
    ///   - .red: RedFileService.Active, which ModernEmbeditorLauncher.EnsureRedirectionLoaded keeps loaded for the
    ///     effective version and the open solution with or without a chat pane (IdeCallbackHost calls it on its tick);
    ///   - Clarion root: the running IDE's install folder.
    /// The indexing members are not used by the call-back tools and do nothing here.
    /// </summary>
    internal sealed class IdeWorkspace : IWorkspaceContext
    {
        private const int VersionTtlMs = 10000;
        private readonly object _lock = new object();
        private ClarionVersionConfig _version;
        private int _versionTick;
        private bool _versionRead;

        public string CurrentSolutionPath
        {
            get { try { return EditorService.GetOpenSolutionPath(); } catch { return null; } }
        }

        public ClarionVersionConfig CurrentVersionConfig
        {
            get
            {
                lock (_lock)
                {
                    if (!_versionRead || unchecked(Environment.TickCount - _versionTick) > VersionTtlMs)
                    {
                        try { _version = EffectiveClarionVersion.CurrentConfig(); } catch { _version = null; }
                        _versionTick = Environment.TickCount;
                        _versionRead = true;
                    }
                    return _version;
                }
            }
        }

        public RedFileService RedFile { get { return RedFileService.Active; } }
        public RedFileService ActiveRedFileService { get { return RedFileService.Active; } }
        public string CurrentDbPath { get { return null; } }
        public List<string> BuildIndexLibraryPaths() { return new List<string>(); }
        public void RunIndex(bool incremental) { }
        public void RunIndex(bool incremental, Action<ClarionCodeGraph.Graph.IndexProgressEvent> onProgress, Action<string> onCompleted)
        {
            if (onCompleted != null) onCompleted("indexing is not available on the IDE call-back endpoint");
        }
        public string GetHostOpenSolutionPath() { return CurrentSolutionPath; }
        public string GetClarionInstallPath()
        {
            try { return EditorService.GetClarionInstallPath(); } catch { return null; }
        }
    }
}
