using System;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Identity helpers for CA-spawned Claude Code terminals that register
    /// with the MultiTerminal broker. Each tab is known by a CA-prefixed agent name
    /// derived from its display name - by name alone, with no docId: a docId identifies
    /// a pane MultiTerminal itself launched (ticket b24bcaf4).
    /// </summary>
    public static class CaAgentIdentity
    {
        /// <summary>
        /// Normalize a tab name into a CA-prefixed agent name safe for the messaging system.
        /// - Already starts with "CA-"? Use as-is after sanitize.
        /// - Otherwise prefix with "CA-".
        /// - Sanitize: runs of non-[A-Za-z0-9] collapse to single dash, trim, cap length.
        /// </summary>
        public static string NormalizeAgentName(string tabName, int fallbackIndex)
        {
            string baseName = (tabName ?? "").Trim();
            if (string.IsNullOrEmpty(baseName))
                baseName = "Tab" + fallbackIndex;

            bool hasPrefix = baseName.StartsWith("CA-", StringComparison.OrdinalIgnoreCase);
            string rest = hasPrefix ? baseName.Substring(3) : baseName;

            var sb = new StringBuilder();
            bool lastWasDash = false;
            foreach (char c in rest)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastWasDash = false;
                }
                else
                {
                    if (!lastWasDash && sb.Length > 0)
                    {
                        sb.Append('-');
                        lastWasDash = true;
                    }
                }
            }
            string cleaned = sb.ToString().TrimEnd('-');
            if (cleaned.Length == 0) cleaned = "Tab" + fallbackIndex;
            if (cleaned.Length > 40) cleaned = cleaned.Substring(0, 40);
            return "CA-" + cleaned;
        }

        /// <summary>
        /// The first of <paramref name="baseName"/>, baseName-2, baseName-3, ... that
        /// <paramref name="isTaken"/> rejects (case-insensitively, as the caller decides).
        ///
        /// WHY (ticket b24bcaf4): the agent name is the session's native messaging ADDRESS
        /// (-n) and its only MultiTerminal identity, and NormalizeAgentName is deterministic,
        /// so two tabs with the same name - in one IDE, or in two - would otherwise share one
        /// address and could receive each other's messages. Uniqueness is decided at launch
        /// against the names this IDE already holds and MultiTerminal's live roster; the broker
        /// rejecting a duplicate registration is the backstop for two IDEs racing the same name.
        /// </summary>
        public static string MakeUnique(string baseName, Func<string, bool> isTaken)
        {
            if (isTaken == null || !isTaken(baseName)) return baseName;
            for (int n = 2; n < 1000; n++)
            {
                string candidate = baseName + "-" + n;
                if (!isTaken(candidate)) return candidate;
            }
            return baseName + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        /// <summary>
        /// Escape a string for single-quoted PowerShell literal (' → '').
        /// </summary>
        public static string EscapeForPowerShellSingleQuote(string s)
        {
            return (s ?? "").Replace("'", "''");
        }
    }
}
