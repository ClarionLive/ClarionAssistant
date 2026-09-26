using System;
using System.IO;
using System.Text;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Persists the Modern Data pad's UI state (which sections/nodes are collapsed/expanded, and which "+"
    /// detail panels are open) as a single opaque JSON blob to
    ///   %APPDATA%\ClarionAssistant\modern-data-pad-state.json
    /// The pad's JavaScript owns the shape (sectionCollapsed/localCollapsed/relExpanded/detailOpen); C# treats
    /// it as an opaque string — it never parses it — so the schema can evolve without host changes. The
    /// ctrl-mousewheel font zoom is persisted separately via <see cref="WebViewZoomHelper"/> (WebView2's
    /// built-in ZoomFactor), not here.
    /// </summary>
    internal static class ModernDataPadState
    {
        private static readonly string FilePath;
        internal const int MaxBytes = 512 * 1024; // hard cap so a runaway/crafted blob can't bloat the file

        static ModernDataPadState()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "modern-data-pad-state.json");
        }

        /// <summary>The saved JSON blob, or null if none / unreadable.</summary>
        public static string Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                // Enforce the size cap on READ too (symmetric with Save). A pre-seeded/tampered/corrupt file
                // larger than the cap is treated as absent rather than read into memory and reposted into the
                // WebView (which would JSON.parse it) — avoids an IDE hang/OOM on pad/settings open.
                var info = new FileInfo(FilePath);
                if (info.Length > MaxBytes) return null;
                string s = File.ReadAllText(FilePath, Encoding.UTF8);
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch { return null; }
        }

        /// <summary>Persist the JSON blob (ignored if empty or over the size cap).</summary>
        public static void Save(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return;
                if (Encoding.UTF8.GetByteCount(json) > MaxBytes) return;
                File.WriteAllText(FilePath, json, Services.EncodingHelper.Utf8NoBom);
            }
            catch { }
        }
    }
}
