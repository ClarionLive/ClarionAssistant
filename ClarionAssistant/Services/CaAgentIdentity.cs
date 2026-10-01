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
        /// The system-prompt section that tells the MODEL its own MultiTerminal name, or null when
        /// there is no name to state.
        ///
        /// WHY (ticket c175492a): MULTITERMINAL_NAME and -n identify the session to MultiTerminal,
        /// but the model sees neither. The plugin's SessionStart identity block is suppressed for
        /// embedded tabs, a delivered message names only its sender, and send_message wants the
        /// caller's own name as fromTerminalId. With two IDEs open, both models saw CA-Terminal-1-CC
        /// and CA-Terminal-1-CC-2 on the roster and could not tell which was theirs. Stating it in
        /// the --append-system-prompt-file content survives /clear, unlike anything said in chat.
        /// </summary>
        public static string BuildIdentityPrompt(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return null;
            string n = agentName.Trim();
            var sb = new StringBuilder();
            sb.AppendLine("## Your MultiTerminal identity");
            sb.AppendLine();
            sb.AppendLine("Your MultiTerminal name is `" + n + "`. It is the address other agents use to message this terminal, and it is fixed for this session (it does not change on /clear).");
            sb.AppendLine();
            sb.AppendLine("- Whenever a MultiTerminal tool asks for YOUR name or terminal id (`fromTerminalId` in `send_message`, `agentName`, `updatedBy`, `createdBy`, and the like), pass exactly `" + n + "`.");
            sb.AppendLine("- Other Clarion Assistant terminals, including ones in other Clarion IDEs, can have names that differ from yours only by a `-2`/`-3` suffix. Never work out your own name from `list_terminals`; it is the one stated here.");
            sb.AppendLine("- Messages delivered to you are addressed to `" + n + "`; reply as `" + n + "`.");
            sb.AppendLine("- If a MultiTerminal tool reports that `" + n + "` is held by another terminal, or that this session is not registered, do not send messages as `" + n + "`: tell the developer instead. Never use another terminal's name.");
            return sb.ToString();
        }

        /// <summary>
        /// <paramref name="extra"/> (the knowledge/recap text bound for --append-system-prompt-file)
        /// with the identity section for <paramref name="agentName"/> appended. Either may be empty;
        /// with no name the extra comes back unchanged, so a launch without a name loses nothing.
        /// </summary>
        public static string AppendIdentityPrompt(string extra, string agentName)
        {
            string identity = BuildIdentityPrompt(agentName);
            if (identity == null) return extra;
            if (string.IsNullOrEmpty(extra)) return identity;
            return extra.TrimEnd() + Environment.NewLine + Environment.NewLine + identity;
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
