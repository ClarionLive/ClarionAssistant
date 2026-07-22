using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace ClarionAssistant.Terminal
{
    public static class WebView2EnvironmentCache
    {
        private static CoreWebView2Environment _environment;
        private static readonly object _lock = new object();
        private static Task<CoreWebView2Environment> _initTask;

        // Holds this instance's profile-slot lock for the life of the process. Never disposed on
        // purpose: the OS releases the handle at process exit (including a crash), which is what
        // frees the slot for the next Clarion instance.
        private static FileStream _slotLock;

        private const int MaxProfileSlots = 16;
        private const string LockFileName = ".ca-instance.lock";

        public static async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_environment != null)
                return _environment;

            Task<CoreWebView2Environment> task;
            lock (_lock)
            {
                if (_initTask == null)
                    _initTask = CreateEnvironmentAsync();
                task = _initTask;
            }

            try
            {
                return await task;
            }
            catch
            {
                // Reset so the next caller can retry initialization
                lock (_lock)
                {
                    if (_initTask == task)
                        _initTask = null;
                }
                throw;
            }
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            // Per-instance profile so each Clarion IDE instance gets its OWN WebView2 browser process
            // instead of sharing one across every open instance. Sharing a userDataFolder makes
            // WebView2 share a single msedgewebview2.exe browser process across host processes, and
            // WebView2ProcessReaper (below) binds THAT browser PID to a per-process kill-on-close job —
            // so closing one Clarion instance was killing the shared browser and blacking out every
            // other open instance's WebView2 pads. Isolating the folder isolates the browser process
            // too, so each instance's reaper can only ever kill its own.
            //
            // Isolation uses stable SLOTS, not a per-PID suffix: slot 1 is the pre-existing
            // "WebView2Data" folder (so localStorage prefs — diff/terminal/embeditor fonts and themes —
            // survive the upgrade and persist across runs), and a concurrent second instance probes on
            // to "WebView2Data_2", etc. A slot is owned by holding an exclusive lock on its
            // ".ca-instance.lock"; the OS drops the lock at process exit (or crash), so slots recycle
            // and the profile-folder count stays bounded instead of growing per launch.
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClarionAssistant");
            string userDataFolder;
            try
            {
                userDataFolder = AcquireProfileFolder(baseDir);
            }
            catch
            {
                baseDir = Path.Combine(Path.GetTempPath(), "ClarionAssistant");
                userDataFolder = AcquireProfileFolder(baseDir);
            }

            CleanupStaleProfiles(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions();
            // Disable GPU rasterization to prevent progressive text rendering
            // corruption in Monaco editor (characters render as empty squares)
            options.AdditionalBrowserArguments = "--disable-gpu-rasterization";

            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options);

            // #109: bind the shared browser process to a kill-on-close job so the OS reaps the whole WebView2
            // tree if Clarion crashes (the graceful ShutdownService path can't cover a crash). The browser
            // process spawns lazily with the first CoreWebView2, so it usually isn't up yet here — the live
            // subscription binds it when ProcessInfosChanged fires, and re-binds if WebView2 relaunches it after
            // a browser crash. The immediate call covers the case where a browser is already present. Best-effort.
            try
            {
                _environment.ProcessInfosChanged += (s, e) => Services.WebView2ProcessReaper.BindBrowser(_environment);
                Services.WebView2ProcessReaper.BindBrowser(_environment);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[WebView2Cache] reaper wire: " + ex.Message); }

            return _environment;
        }

        /// <summary>
        /// Claim the first free profile slot under <paramref name="baseDir"/> by taking an exclusive
        /// lock on its lock file. Slot 1 is the legacy "WebView2Data" folder; slots 2..N are
        /// "WebView2Data_2".."WebView2Data_N". Throws only if the base dir itself is unusable.
        /// </summary>
        private static string AcquireProfileFolder(string baseDir)
        {
            for (int slot = 1; slot <= MaxProfileSlots; slot++)
            {
                string folder = Path.Combine(baseDir, slot == 1 ? "WebView2Data" : "WebView2Data_" + slot);
                try
                {
                    Directory.CreateDirectory(folder);
                    _slotLock = new FileStream(Path.Combine(folder, LockFileName),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return folder;
                }
                catch
                {
                    // Slot owned by another live instance (lock held) or inaccessible — probe the next.
                }
            }

            // More than MaxProfileSlots concurrent instances (or every slot inaccessible): last-resort
            // per-PID folder. No prefs persistence here; swept by CleanupStaleProfiles on a later run.
            string fallback = Path.Combine(baseDir, "WebView2Data_pid" +
                System.Diagnostics.Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        /// <summary>
        /// Background best-effort sweep of leftover per-PID profile folders ("WebView2Data_pid1234"
        /// slot-overflow fallbacks, and plain "WebView2Data_1234" from interim per-PID builds).
        /// Stable slot folders are kept — they hold each slot's persisted prefs.
        /// </summary>
        private static void CleanupStaleProfiles(string activeFolder)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string[] baseDirs =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClarionAssistant"),
                    Path.Combine(Path.GetTempPath(), "ClarionAssistant")
                };
                foreach (string baseDir in baseDirs)
                {
                    try
                    {
                        if (!Directory.Exists(baseDir)) continue;
                        foreach (string dir in Directory.GetDirectories(baseDir, "WebView2Data_*"))
                        {
                            try
                            {
                                if (string.Equals(dir, activeFolder, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string suffix = Path.GetFileName(dir).Substring("WebView2Data_".Length);

                                int slot;
                                if (int.TryParse(suffix, out slot) && slot >= 2 && slot <= MaxProfileSlots)
                                    continue; // stable slot — keeps its prefs; ownership is decided by its lock

                                // Per-PID leftover. Leave it alone while that process is still alive.
                                string pidText = suffix.StartsWith("pid", StringComparison.OrdinalIgnoreCase)
                                    ? suffix.Substring(3) : suffix;
                                int pid;
                                if (int.TryParse(pidText, out pid) && IsProcessAlive(pid))
                                    continue;

                                // Prove no CA instance owns it (exclusive lock), then delete. If a lingering
                                // browser still holds files the delete throws and a later run retries.
                                using (new FileStream(Path.Combine(dir, LockFileName),
                                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                                Directory.Delete(dir, true);
                            }
                            catch { /* locked or in use — leave it for a future sweep */ }
                        }
                    }
                    catch { /* base dir unreadable — nothing to do */ }
                }
            });
        }

        private static bool IsProcessAlive(int pid)
        {
            try { System.Diagnostics.Process.GetProcessById(pid); return true; }
            catch { return false; }
        }
    }
}
