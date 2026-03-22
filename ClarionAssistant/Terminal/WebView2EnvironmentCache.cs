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
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClarionAssistant", "WebView2Data");

            try
            {
                if (!Directory.Exists(userDataFolder))
                    Directory.CreateDirectory(userDataFolder);
            }
            catch
            {
                userDataFolder = Path.Combine(Path.GetTempPath(), "ClarionAssistant", "WebView2Data");
                Directory.CreateDirectory(userDataFolder);
            }

            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions());

            return _environment;
        }
    }
}
