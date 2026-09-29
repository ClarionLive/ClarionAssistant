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
        /// Escape a string for single-quoted PowerShell literal (' → '').
        /// </summary>
        public static string EscapeForPowerShellSingleQuote(string s)
        {
            return (s ?? "").Replace("'", "''");
        }
    }
}
