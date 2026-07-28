using System;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Task d3ab083a: intercept Errors-pane navigation so Clarion's error routing can stop closing /
    /// re-opening the native embeditor (the "reload") while a CA overlay is live.
    ///
    /// Mechanism (CA-Terminal-1-CC's live+IL probe, 2026-07-28; also CA knowledge #89): the pad's
    /// ItemActivate EVENT cannot suppress navigation — TaskView.OnItemActivate raises the event and then
    /// unconditionally calls SelectedTask.JumpToPosition(), which is public VIRTUAL and dispatched off
    /// ListViewItem.Tag. So the interception point is the TASK OBJECT itself: SD's official creation hook
    /// `Task.NewTaskEvent` (public static field; `Task.NewTask` returns `e.Task ?? new Task(error)`) lets
    /// us substitute a CaErrorTask whose JumpToPosition we own. Covers double-click, Enter, F4 and the
    /// next/previous-error toolbar (all funnel through the same OnItemActivate override).
    ///
    /// CURRENT MODE — INSTRUMENTATION ONLY: the override logs the route decision inputs (file, 0-based
    /// line, overlay state) and then ALWAYS defers to base.JumpToPosition(), i.e. zero behavior change.
    /// Two things must land before the dispatch goes active:
    ///   1. Coverage proof: CC could not verify statically that the Clarion build pipeline routes its
    ///      errors through Task.NewTask (a SoftVelocity assembly could construct Tasks directly). The
    ///      substitution counter vs. the rows John sees in the pane answers that after one build.
    ///   2. The dispatch UX decision + the pwee mapper probe (can gen-line→embed be queried without the
    ///      close side effect?) — see the ticket plan.
    /// </summary>
    internal static class ErrorPadNavigationInterceptor
    {
        private static readonly object _gate = new object();
        private static bool _hooked;
        private static int _substituted;

        /// <summary>Idempotent. Subscribes the Task-substitution hook. Cheap and side-effect-free, so it
        /// is safe to call from any Monaco surface spin-up path.</summary>
        public static void EnsureInstalled()
        {
            lock (_gate)
            {
                if (_hooked) return;
                try
                {
                    Task.NewTaskEvent += OnNewTask;
                    _hooked = true;
                    ClarionAssistant.MonacoSpikeLog.Write("error-task hook installed (Task.NewTaskEvent substitution)");
                }
                catch (Exception ex)
                {
                    ClarionAssistant.MonacoSpikeLog.Write("error-task hook NOT installed: " + ex.Message);
                }
            }
        }

        public static void Uninstall()
        {
            lock (_gate)
            {
                if (!_hooked) return;
                try { Task.NewTaskEvent -= OnNewTask; } catch { }
                _hooked = false;
            }
        }

        private static void OnNewTask(object sender, NewTaskEventArgs e)
        {
            try
            {
                if (e == null || e.Task != null || e.BuildError == null) return;   // already substituted elsewhere — don't fight
                e.Task = new CaErrorTask(e.BuildError);
                int n = ++_substituted;
                if (n == 1 || n % 25 == 0)
                    ClarionAssistant.MonacoSpikeLog.Write("error-task substitution count: " + n);
            }
            catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("error-task substitution error: " + ex.Message); }
        }

        /// <summary>A Task whose navigation CA owns. JumpToPosition runs on the pad's UI thread for every
        /// activation route (mouse, Enter, F4, next/prev toolbar).</summary>
        private sealed class CaErrorTask : Task
        {
            public CaErrorTask(BuildError error) : base(error) { }

            public override void JumpToPosition()
            {
                try
                {
                    // INSTRUMENTATION (see class banner): observe, never divert — base always runs below.
                    // Line/Column here are 0-BASED (Task(BuildError) does Line-1/Column-1).
                    ClarionAssistant.MonacoSpikeLog.Write("error-task activate: " +
                        System.IO.Path.GetFileName(FileName ?? "?") + " line0=" + Line +
                        " type=" + TaskType +
                        " liveOverlay=" + Terminal.ModernEmbeditorViewContent.HasLiveOverlay);
                }
                catch { }
                base.JumpToPosition();
            }
        }
    }
}
