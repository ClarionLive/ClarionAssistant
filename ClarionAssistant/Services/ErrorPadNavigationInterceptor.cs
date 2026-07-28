using System;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Task d3ab083a: stop Errors-pane clicks from closing/re-opening the native embeditor (the "reload")
    /// while a CA overlay is live.
    ///
    /// Mechanics (CA-Terminal-1-CC's IL probes, 2026-07-28; CA knowledge #89 + memory
    /// reference_errors_pane_navigation_routes): every activation route (double-click, Enter, F4,
    /// next/prev toolbar) funnels through TaskView.OnItemActivate → SelectedTask.JumpToPosition(), which
    /// is public VIRTUAL and dispatched off ListViewItem.Tag. For .app builds the row object is
    /// SoftVelocity.Generator.ApplicationBuildErrorTask, whose JumpToPosition is
    ///     if (!app.EditError(error)) FileService.JumpToFilePosition(...);
    /// — app.EditError drives the NATIVE mapper (Win32App.EditError) which repositions the open
    /// embeditor for embed-mappable lines but re-hosts (closes/reopens) it otherwise: the reload.
    ///
    /// Interception = substitute the Task object:
    ///  • ApplicationBuildErrorTask is constructed directly (never routes through Task.NewTask), so we
    ///    WRAP it at the TaskService level: on TaskService.Added, Remove the original and Add a
    ///    CaErrorTask carrying it as inner — TaskService then genuinely contains our wrapper, so pad
    ///    rebuilds (filter toggles) and F4 all use it. The wrapper preserves the inner's JumpToPosition
    ///    (and with it app.EditError) for every row we don't handle.
    ///  • Task.NewTaskEvent is also hooked (official substitution point) for plain source-compile rows.
    ///    Nobody else subscribes it in this codebase (CC-verified); += only, never assignment.
    ///
    /// Dispatch: rows whose file IS the live overlay's generated module are revealed INSIDE the open
    /// overlay (module line → pwee document line via CommonGenEditor.BackgroundLineNumOffset, validated
    /// against the composed BackgroundPWEEText before trusting — see
    /// ModernEmbeditorViewContent.TryRevealErrorInLiveOverlay). The embeditor is never touched → no
    /// reload, and the row works whether it's embed-mappable or not (generated context shows read-only,
    /// embed slots editable). Everything else — no live overlay, other files, mapping unavailable or
    /// unvalidated — falls through to the ORIGINAL navigation: 100% native behavior.
    /// </summary>
    internal static class ErrorPadNavigationInterceptor
    {
        private static readonly object _gate = new object();
        private static bool _hooked;
        private static int _wrapped;

        /// <summary>Idempotent; cheap; safe to call from any Monaco surface spin-up path.</summary>
        public static void EnsureInstalled()
        {
            lock (_gate)
            {
                if (_hooked) return;
                try
                {
                    Task.NewTaskEvent += OnNewTask;
                    TaskService.Added += OnTaskAdded;
                    _hooked = true;
                    ClarionAssistant.MonacoSpikeLog.Write("error-task interception installed (TaskService.Added wrap + Task.NewTaskEvent)");
                }
                catch (Exception ex)
                {
                    ClarionAssistant.MonacoSpikeLog.Write("error-task interception NOT installed: " + ex.Message);
                }
            }
        }

        public static void Uninstall()
        {
            lock (_gate)
            {
                if (!_hooked) return;
                try { Task.NewTaskEvent -= OnNewTask; } catch { }
                try { TaskService.Added -= OnTaskAdded; } catch { }
                _hooked = false;
            }
        }

        // Plain source-compile rows (AbstractBuildMenuCommand.ShowResults → Task.NewTask): substitute at
        // creation — cleaner than the Remove/Add churn, and e.Task wins over the default construction.
        private static void OnNewTask(object sender, NewTaskEventArgs e)
        {
            try
            {
                if (e == null || e.Task != null || e.BuildError == null) return;   // someone else substituted — don't fight
                e.Task = new CaErrorTask(e.BuildError);
            }
            catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("error-task NewTask substitution error: " + ex.Message); }
        }

        // App-generation rows (ApplicationBuildErrorTask, constructed directly): wrap after the fact.
        // The pad's own Added handler ran first (it subscribed at pad creation), so the Remove/Add pair
        // just replaces the freshly-appended row — order is preserved because it was last anyway.
        private static void OnTaskAdded(object sender, TaskEventArgs e)
        {
            try
            {
                var t = e?.Task;
                if (t == null || t is CaErrorTask) return;   // our own Add re-entering — done
                var w = new CaErrorTask(t);
                TaskService.Remove(t);
                TaskService.Add(w);
                int n = ++_wrapped;
                if (n == 1 || n % 50 == 0)
                    ClarionAssistant.MonacoSpikeLog.Write("error-task wrap count: " + n + " (last inner: " + t.GetType().Name + ")");
            }
            catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("error-task wrap error: " + ex.Message); }
        }

        /// <summary>A Task whose navigation CA owns. Runs on the pad's UI thread for every activation
        /// route. Line/Column are 0-BASED throughout (Task(BuildError) already did the -1).</summary>
        private sealed class CaErrorTask : Task
        {
            private readonly Task _inner;   // original row object (ApplicationBuildErrorTask etc.); null when born from NewTaskEvent

            public CaErrorTask(Task inner)
                : base(inner.FileName, inner.Description, inner.Column, inner.Line, inner.TaskType)   // ctor order: column BEFORE line
            {
                _inner = inner;
            }

            public CaErrorTask(BuildError error) : base(error) { }

            public override void JumpToPosition()
            {
                try
                {
                    if (Terminal.ModernEmbeditorViewContent.TryRevealErrorInLiveOverlay(FileName, Line, Column))
                        return;   // shown inside the live overlay — embeditor untouched, no reload
                }
                catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("error-task reveal error: " + ex.Message); }

                // Native behavior — the inner ApplicationBuildErrorTask keeps its app.EditError embed
                // routing; a NewTask-born row has no inner and the stock base jump is the native path.
                if (_inner != null) _inner.JumpToPosition();
                else base.JumpToPosition();
            }
        }
    }
}
