using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Persists WebView2 zoom factors per view name to %APPDATA%\ClarionAssistant\zoom.ini.
    /// </summary>
    internal static class WebViewZoomHelper
    {
        private static readonly string ZoomFilePath;
        private static readonly Dictionary<string, double> _cache = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        static WebViewZoomHelper()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            ZoomFilePath = Path.Combine(dir, "zoom.ini");
        }

        public static double GetZoom(string viewName)
        {
            EnsureLoaded();
            double val;
            return _cache.TryGetValue(viewName, out val) ? val : 1.0;
        }

        public static void SetZoom(string viewName, double factor)
        {
            EnsureLoaded();
            if (Math.Abs(factor - 1.0) < 0.01)
                _cache.Remove(viewName);
            else
                _cache[viewName] = factor;
            Save();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            if (!File.Exists(ZoomFilePath)) return;
            try
            {
                foreach (string line in File.ReadAllLines(ZoomFilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    double val;
                    if (double.TryParse(line.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out val))
                        _cache[key] = val;
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kvp in _cache)
                    sb.AppendLine(kvp.Key + "=" + kvp.Value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllText(ZoomFilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
