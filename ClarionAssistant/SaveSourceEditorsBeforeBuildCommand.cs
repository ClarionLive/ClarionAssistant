using System;
using System.Reflection;
using ICSharpCode.Core;

namespace ClarionAssistant
{
    /// <summary>
    /// /Workspace/Autostart command — saves every open CA Editor tab (MonacoClarionEditor, the Monaco/
    /// WebView2 overlay over the native source-file editor) that has unsaved edits, just before a build
    /// starts.
    ///
    /// The Monaco overlay saves straight to disk itself and deliberately never touches the native
    /// ClarionEditor's own buffer/IsDirty underneath it (see MonacoClarionEditor's AttachOverlay/OnSave
    /// doc comments — "the native editor underneath stays a clean, untouched shell"). The native IDE's
    /// own save-before-build (AbstractBuildMenuCommand.BeforeBuild -> SaveAllFiles.SaveAll) only reaches
    /// AbstractViewContent.IsDirty, so it never sees an unsaved Monaco edit. Without this hook, "Build
    /// Solution" could silently compile a STALE on-disk version of a file still being edited in the CA
    /// Editor — confirmed directly: an edit left unsaved in Monaco compiled clean until this hook was
    /// added, and correctly surfaced a compiler error afterwards.
    ///
    /// Subscribes to ProjectService.StartBuild (a static, non-cancelable EventHandler) via reflection —
    /// same technique LspAutostartCommand uses for SolutionLoaded/SolutionClosed. Guarded throughout:
    /// this MUST NOT throw at workbench load, and MUST NOT block/slow down a build when nothing is dirty.
    /// </summary>
    public class SaveSourceEditorsBeforeBuildCommand : ICommand
    {
        private object _owner;
        public object Owner
        {
            get { return _owner; }
            set
            {
                _owner = value;
                var h = OwnerChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public event EventHandler OwnerChanged;

        // Rooted so the StartBuild delegate is never GC'd.
        private static Delegate _startBuildHandler;

        public void Run()
        {
            try
            {
                SubscribeStartBuild();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SaveSourceEditorsBeforeBuild] subscribe failed: " + ex.Message); }
        }

        /// <summary>
        /// Subscribes to ICSharpCode.SharpDevelop.Project.ProjectService.StartBuild via reflection
        /// (same assembly/type EditorService/LspAutostartCommand use).
        /// </summary>
        private void SubscribeStartBuild()
        {
            var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
            if (sharpDevelopAsm == null) return;

            var projectServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
            if (projectServiceType == null) return;

            var evt = projectServiceType.GetEvent("StartBuild", BindingFlags.Public | BindingFlags.Static);
            if (evt == null) return;

            MethodInfo handlerMethod = typeof(SaveSourceEditorsBeforeBuildCommand).GetMethod(
                "OnStartBuild", BindingFlags.NonPublic | BindingFlags.Static);
            if (handlerMethod == null) return;

            _startBuildHandler = Delegate.CreateDelegate(evt.EventHandlerType, handlerMethod);
            evt.AddEventHandler(null, _startBuildHandler);
        }

        private static void OnStartBuild(object sender, EventArgs e)
        {
            try { MonacoClarionEditor.SaveAllDirtyBeforeBuild(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SaveSourceEditorsBeforeBuild] OnStartBuild failed: " + ex.Message); }
        }
    }
}
