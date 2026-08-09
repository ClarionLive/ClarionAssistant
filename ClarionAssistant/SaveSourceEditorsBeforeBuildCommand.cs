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
            catch (Exception ex) { MonacoSpikeLog.Write("[save-before-build] subscribe threw: " + ex.Message); }
        }

        /// <summary>
        /// Subscribes to ICSharpCode.SharpDevelop.Project.ProjectService.StartBuild via reflection
        /// (same assembly/type EditorService/LspAutostartCommand use).
        ///
        /// Every bail-out below is LOGGED, deliberately. This is a reflection probe into a SharpDevelop
        /// FORK, where window/service shapes differ from stock and a wrong guess fails by returning null
        /// rather than throwing (see the SD-fork memory). The original version returned silently on all
        /// five, and logged only via Debug.WriteLine — which goes to an attached debugger, i.e. nowhere
        /// on a deployed addin. The net effect was that "the hook is not wired" and "the hook is wired
        /// and working" produced byte-identical evidence: nothing. That ambiguity is what made a user
        /// report of "it does not save before build" impossible to confirm or refute.
        /// </summary>
        private void SubscribeStartBuild()
        {
            var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
            if (sharpDevelopAsm == null)
            { MonacoSpikeLog.Write("[save-before-build] NOT WIRED: ICSharpCode.SharpDevelop did not load"); return; }

            var projectServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
            if (projectServiceType == null)
            { MonacoSpikeLog.Write("[save-before-build] NOT WIRED: ProjectService type not found"); return; }

            var evt = projectServiceType.GetEvent("StartBuild", BindingFlags.Public | BindingFlags.Static);
            if (evt == null)
            { MonacoSpikeLog.Write("[save-before-build] NOT WIRED: ProjectService.StartBuild (public static) not found on this fork"); return; }

            MethodInfo handlerMethod = typeof(SaveSourceEditorsBeforeBuildCommand).GetMethod(
                "OnStartBuild", BindingFlags.NonPublic | BindingFlags.Static);
            if (handlerMethod == null)
            { MonacoSpikeLog.Write("[save-before-build] NOT WIRED: OnStartBuild handler method not found"); return; }

            _startBuildHandler = Delegate.CreateDelegate(evt.EventHandlerType, handlerMethod);
            evt.AddEventHandler(null, _startBuildHandler);
            MonacoSpikeLog.Write("[save-before-build] WIRED: subscribed to ProjectService.StartBuild");
        }

        /// <summary>
        /// Fires when a build starts. Logs on ENTRY, before doing any work — so the log distinguishes
        /// "the build never raised StartBuild" (no line at all) from "it fired and found nothing dirty"
        /// (a line saying so). Without the entry line those two look the same from the log, and they
        /// need completely different fixes: the first means this event is the wrong chokepoint for the
        /// Clarion build button, the second means the dirty-state mirror is not being updated.
        /// </summary>
        private static void OnStartBuild(object sender, EventArgs e)
        {
            try
            {
                MonacoSpikeLog.Write("[save-before-build] StartBuild fired -> flushing dirty CA Editor tabs");
                MonacoClarionEditor.SaveAllDirtyBeforeBuild();
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[save-before-build] OnStartBuild failed: " + ex.Message); }
        }
    }
}
