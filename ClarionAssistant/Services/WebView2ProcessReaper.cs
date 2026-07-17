using System;
using System.Runtime.InteropServices;
using ClarionAssistant.Terminal;
using Microsoft.Web.WebView2.Core;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Binds the shared WebView2 browser process to a Windows Job Object with KILL_ON_JOB_CLOSE, so the OS
    /// tears the whole WebView2 process tree down when Clarion dies — including a CRASH, which the graceful
    /// ShutdownService path can't cover (ApplicationExit never fires, so nothing disposes the WebViews and the
    /// browser + every renderer are orphaned; GitHub #109).
    ///
    /// Same mechanism ConPtyTerminal already uses for the terminal's child processes
    /// (ConPtyTerminal.CreateJobAndAssignProcess) — the only difference is WebView2 shares ONE browser process
    /// across every surface, and we only have its PID (from the environment's process list), so we OpenProcess
    /// it rather than owning a handle from CreateProcess.
    ///
    /// The job handle is created once and DELIBERATELY never closed: KILL_ON_JOB_CLOSE fires when the last
    /// handle to the job closes, and the only thing holding it is our process. So it fires exactly at Clarion
    /// exit — clean or crash — reaping any WebView2 process still in the job. On a clean exit the graceful path
    /// has already disposed the WebViews (the browser exits normally); this is purely the crash backstop.
    ///
    /// Everything is best-effort: any native failure (down-level OS, a nested-job refusal) is swallowed and we
    /// fall back to the graceful path. It must never throw into WebView2's ProcessInfosChanged event.
    /// </summary>
    internal static class WebView2ProcessReaper
    {
        private static readonly object _gate = new object();
        private static IntPtr _job = IntPtr.Zero;   // held for the process lifetime; never closed (see class remarks)
        private static uint _boundPid;              // the browser PID currently assigned to the job (0 = none)

        /// <summary>
        /// Ensure the CURRENT WebView2 browser process is inside our kill-on-close job. Idempotent and cheap:
        /// re-binds only when the browser PID has changed (WebView2 re-launches the browser after a browser
        /// crash), so it's safe to call on every ProcessInfosChanged fire.
        /// </summary>
        public static void BindBrowser(CoreWebView2Environment env)
        {
            if (env == null) return;
            try
            {
                uint pid = FindBrowserPid(env);
                if (pid == 0) return;   // browser process not spawned yet — a later ProcessInfosChanged will carry it

                lock (_gate)
                {
                    if (pid == _boundPid) return;                 // already in the job
                    if (!EnsureJobLocked()) return;               // job unavailable — graceful path still applies

                    IntPtr h = NativeMethods.OpenProcess(
                        NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_TERMINATE, false, pid);
                    if (h == IntPtr.Zero) return;
                    try
                    {
                        if (NativeMethods.AssignProcessToJobObject(_job, h))
                            _boundPid = pid;
                        // else: leave _boundPid so a later fire retries; assign can fail transiently.
                    }
                    finally
                    {
                        // The job keeps its own association with the process; we don't need this handle.
                        NativeMethods.CloseHandle(h);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WebView2Reaper] BindBrowser: " + ex.Message);
            }
        }

        private static uint FindBrowserPid(CoreWebView2Environment env)
        {
            try
            {
                var infos = env.GetProcessInfos();
                if (infos == null) return 0;
                foreach (var pi in infos)
                {
                    if (pi.Kind == CoreWebView2ProcessKind.Browser)
                        return (uint)pi.ProcessId;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WebView2Reaper] FindBrowserPid: " + ex.Message);
            }
            return 0;
        }

        /// <summary>Create the kill-on-close job once. Mirrors ConPtyTerminal.CreateJobAndAssignProcess. Caller
        /// holds _gate.</summary>
        private static bool EnsureJobLocked()
        {
            if (_job != IntPtr.Zero) return true;
            try
            {
                IntPtr job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return false;

                var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int infoSize = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr infoPtr = Marshal.AllocHGlobal(infoSize);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (!NativeMethods.SetInformationJobObject(
                            job, NativeMethods.JobObjectExtendedLimitInformation, infoPtr, (uint)infoSize))
                    {
                        // Without KILL_ON_JOB_CLOSE the job is pointless (it wouldn't reap on our death) — drop it.
                        NativeMethods.CloseHandle(job);
                        return false;
                    }
                }
                finally { Marshal.FreeHGlobal(infoPtr); }

                _job = job;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WebView2Reaper] EnsureJob: " + ex.Message);
                return false;
            }
        }
    }
}
