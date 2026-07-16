using System;
using System.IO;
using System.Text;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Persists the CA Find/Replace pad's state (per-editor search sessions, global find/replace
    /// history, options, font size, theme) as a single opaque JSON blob to
    ///   %APPDATA%\ClarionAssistant\ca-find-pad-state.json
    /// The pad's JavaScript owns the shape; C# treats it as an opaque string — it never parses it —
    /// so the schema can evolve without host changes. Same pattern (and size caps, both directions)
    /// as <see cref="ModernDataPadState"/>. (GitHub #66, ticket 91e6ecac)
    /// </summary>
    internal static class CaFindPadState
    {
        private static readonly string FilePath;
        internal const int MaxBytes = 512 * 1024; // hard cap so a runaway/crafted blob can't bloat the file

        static CaFindPadState()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "ca-find-pad-state.json");
        }

        /// <summary>The saved JSON blob, or null if none / unreadable / over the size cap.</summary>
        public static string Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
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
                File.WriteAllText(FilePath, json, Encoding.UTF8);
            }
            catch { }
        }
    }
}
