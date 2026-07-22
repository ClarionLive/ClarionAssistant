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
            // Suffixed per-process (PID) so each Clarion IDE instance gets its OWN WebView2 browser
            // process instead of sharing one across every open instance. Sharing a userDataFolder makes
            // WebView2 share a single msedgewebview2.exe browser process across host processes, and
            // WebView2ProcessReaper (below) binds THAT browser PID to a per-process kill-on-close job —
            // so closing one Clarion instance was killing the shared browser and blacking out every
            // other open instance's WebView2 pads. Isolating the folder per PID isolates the browser
            // process too, so each instance's reaper can only ever kill its own.
            string pidSuffix = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClarionAssistant", "WebView2Data_" + pidSuffix);

            try
            {
                if (!Directory.Exists(userDataFolder))
                    Directory.CreateDirectory(userDataFolder);
            }
            catch
            {
                userDataFolder = Path.Combine(Path.GetTempPath(), "ClarionAssistant", "WebView2Data_" + pidSuffix);
                Directory.CreateDirectory(userDataFolder);
            }

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
    }
}
