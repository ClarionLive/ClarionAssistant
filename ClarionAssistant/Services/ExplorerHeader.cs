using System;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The pure half of the CA Explorer's environment header (ticket 16d140e9): which values the
    /// APP / VERSION / ROOT lines show, and whether a path may be handed to Windows Explorer.
    ///
    /// No IDE references on purpose, so tests\ExplorerHeader.Test.cs can compile this file on its own.
    /// ModernDataPad gathers the live values (open .app, solution, current version entry) and asks here.
    /// </summary>
    public static class ExplorerHeader
    {
        /// <summary>What the header shows. A null value means "unknown": the page renders an em dash
        /// and does not make that line clickable.</summary>
        public sealed class Model
        {
            /// <summary>"APP" when an .app is open, else "SOLUTION" when only a solution is known,
            /// else "APP" (with no value) so the empty state still reads the way the Owner asked for.</summary>
            public string AppLabel;
            public string AppPath;
            public string VersionName;
            public string RootPath;
        }

        public const string LabelApp = "APP";
        public const string LabelSolution = "SOLUTION";

        /// <summary>
        /// Compose the header. The open .app wins; the solution is the fallback, labelled SOLUTION so
        /// the line never claims a .sln is an app. An .app known only by a bare file name is placed in
        /// the solution's folder when one is known (the IDE keeps the two together), otherwise dropped
        /// rather than shown as a path that is not one.
        /// </summary>
        public static Model Compose(string appPath, string solutionPath, string versionName, string rootPath)
        {
            var m = new Model();
            string app = Clean(appPath);
            string sol = Clean(solutionPath);

            if (app != null && !IsRooted(app))
            {
                string solDir = null;
                if (sol != null && IsRooted(sol)) { try { solDir = Path.GetDirectoryName(sol); } catch { solDir = null; } }
                try { app = string.IsNullOrEmpty(solDir) ? null : Path.Combine(solDir, app); } catch { app = null; }
            }

            if (app != null) { m.AppLabel = LabelApp; m.AppPath = app; }
            else if (sol != null) { m.AppLabel = LabelSolution; m.AppPath = sol; }
            else { m.AppLabel = LabelApp; m.AppPath = null; }

            m.VersionName = Clean(versionName);
            string root = Clean(rootPath);
            m.RootPath = root == null ? null : TrimTrailingSeparator(root);
            return m;
        }

        /// <summary>
        /// Build the explorer.exe argument string for opening <paramref name="path"/>, or return false.
        /// <paramref name="selectFile"/> = true opens the containing folder with the file selected
        /// (<c>/select,"path"</c>); false opens the folder itself.
        ///
        /// Accepts only a fully qualified drive path (C:\...) or a UNC share path (\\server\share\...),
        /// after normalisation. Refused: relative paths, device / long-path namespaces (\\?\, \\.\),
        /// URLs and other schemes, and any character that could end the quoted argument or mean
        /// something to explorer's own parser (quote, control chars, wildcards, pipes, redirects). The target must
        /// exist as a file (select) or a directory (open). The argument is always quoted, except a bare
        /// drive root, whose trailing backslash would otherwise escape the closing quote.
        /// </summary>
        public static bool TryBuildExplorerArgs(string path, bool selectFile,
            Func<string, bool> fileExists, Func<string, bool> dirExists, out string args)
        {
            args = null;
            string p = Clean(path);
            if (p == null || p.Length > 32000) return false;
            if (!HasSafeChars(p)) return false;
            if (!IsDriveOrUncForm(p)) return false;

            string full;
            try { full = Path.GetFullPath(p); }
            catch { return false; }
            if (!HasSafeChars(full) || !IsDriveOrUncForm(full)) return false;

            if (selectFile)
            {
                if (fileExists == null || !SafeCall(fileExists, full)) return false;
                args = "/select,\"" + full + "\"";
                return true;
            }

            if (IsDriveRoot(full))
            {
                if (dirExists == null || !SafeCall(dirExists, full)) return false;
                args = full.Substring(0, 2) + "\\";
                return true;
            }
            full = TrimTrailingSeparator(full);
            if (dirExists == null || !SafeCall(dirExists, full)) return false;
            args = "\"" + full + "\"";
            return true;
        }

        /// <summary>
        /// Host-side debounce for the APP / ROOT clicks: a double-click (or a page that posts in a loop - the
        /// page is untrusted) must not open a stack of Explorer windows. Per line: a request is refused while
        /// one for the same line is in flight, and for <see cref="CooldownMs"/> after it finished. The clock
        /// is passed in (milliseconds, any monotonic origin) so the harness can drive it. Thread-safe.
        /// </summary>
        public sealed class OpenGate
        {
            public const long CooldownMs = 1000;
            private readonly object _lock = new object();
            private readonly System.Collections.Generic.HashSet<string> _inFlight =
                new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            private readonly System.Collections.Generic.Dictionary<string, long> _endedAt =
                new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);

            /// <summary>True = go ahead (and the line is now in flight; call <see cref="End"/> when done).</summary>
            public bool TryBegin(string which, long nowMs)
            {
                if (which == null) return false;
                lock (_lock)
                {
                    if (_inFlight.Contains(which)) return false;
                    long ended;
                    if (_endedAt.TryGetValue(which, out ended) && nowMs - ended < CooldownMs) return false;
                    _inFlight.Add(which);
                    return true;
                }
            }

            /// <summary>The request for <paramref name="which"/> finished (launched, refused or failed).</summary>
            public void End(string which, long nowMs)
            {
                if (which == null) return;
                lock (_lock)
                {
                    _inFlight.Remove(which);
                    _endedAt[which] = nowMs;
                }
            }
        }

        // ---------------------------------------------------------------- helpers

        private static string Clean(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        private static bool SafeCall(Func<string, bool> f, string arg)
        {
            try { return f(arg); } catch { return false; }
        }

        private static bool IsRooted(string p)
        {
            return IsDriveOrUncForm(p);
        }

        private static bool IsDriveRoot(string p)
        {
            return p.Length == 3 && IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }

        /// <summary>C:\... or \\server\share[\...]; never \\?\ or \\.\ and never a bare "C:" or "C:foo".</summary>
        private static bool IsDriveOrUncForm(string p)
        {
            if (p.Length >= 3 && IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/'))
                return true;
            if (p.Length >= 5 && (p[0] == '\\' || p[0] == '/') && (p[1] == '\\' || p[1] == '/'))
            {
                char c = p[2];
                if (c == '?' || c == '.' || c == '\\' || c == '/') return false;
                // Need a server AND a share segment.
                string rest = p.Substring(2).Replace('/', '\\');
                int sep = rest.IndexOf('\\');
                if (sep <= 0 || sep == rest.Length - 1) return false;
                string share = rest.Substring(sep + 1);
                int sep2 = share.IndexOf('\\');
                string shareName = sep2 < 0 ? share : share.Substring(0, sep2);
                return shareName.Length > 0;
            }
            return false;
        }

        private static bool HasSafeChars(string p)
        {
            foreach (char c in p)
            {
                if (c < 0x20 || c == 0x7F) return false;
                switch (c)
                {
                    // Not '&', '%' or '^': legal in real folder names ("R&D") and inert here, because
                    // explorer.exe is started directly - no cmd.exe ever parses this string.
                    case '"': case '*': case '?': case '<': case '>': case '|':
                        return false;
                }
            }
            // A colon anywhere but the drive letter's is a stream name or a scheme (file:, http:).
            int colon = p.IndexOf(':');
            if (colon >= 0 && colon != 1) return false;
            if (colon == 1 && p.IndexOf(':', 2) >= 0) return false;
            return true;
        }

        private static string TrimTrailingSeparator(string p)
        {
            while (p.Length > 3 && (p.EndsWith("\\") || p.EndsWith("/")))
                p = p.Substring(0, p.Length - 1);
            // "C:\" stays; a UNC "\\server\share\" loses its trailing separator.
            return p;
        }
    }
}
