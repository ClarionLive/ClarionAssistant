using System;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Identity helpers for CA-spawned Claude Code terminals that register
    /// with the MultiTerminal broker. Each tab is known by a short numbered agent name
    /// (CA1, CA2, ...) - by name alone, with no docId: a docId identifies a pane
    /// MultiTerminal itself launched (ticket b24bcaf4).
    /// </summary>
    public static class CaAgentIdentity
    {
        private const string NamePrefix = "CA";

        /// <summary>
        /// The lowest-numbered CA1, CA2, CA3, ... that <paramref name="isTaken"/> does not
        /// reject (case-insensitively, as the caller decides).
        ///
        /// WHY NUMBERED (ticket 7792e3e0): the name used to be derived from the tab's display
        /// name, which gave CA-Terminal-2-CC - too long for the tab and the prompt-box label, and
        /// too much to type when asking for a message to be sent to it. "Terminal" was filler,
        /// "CC" named a backend that every registered tab shares, and a per-IDE tab counter made
        /// a second IDE collide into CA-Terminal-1-CC-2. Numbering from the names actually in use
        /// - every tab in this IDE plus MultiTerminal's live roster - gives each IDE its own
        /// number without a suffix.
        ///
        /// WHY UNIQUE (ticket b24bcaf4): the agent name is the session's native messaging
        /// ADDRESS (-n) and its only MultiTerminal identity, so two sessions sharing one could
        /// receive each other's messages. Two IDEs launching in the same instant can still pick
        /// the same number; the broker rejecting a duplicate registration is the backstop.
        /// </summary>
        public static string NextFreeName(Func<string, bool> isTaken)
        {
            for (int n = 1; n < 1000; n++)
            {
                string candidate = NamePrefix + n;
                if (isTaken == null || !isTaken(candidate)) return candidate;
            }
            return NamePrefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        /// <summary>
        /// The tab-strip label for a tab whose assistant is known as <paramref name="agentName"/>:
        /// the name alone for a plain "Terminal N" tab, or "CA2 · MySolution" when the tab was
        /// opened on something (a solution, project or class) worth keeping in view. The name
        /// leads so the label shows exactly what to type to message the tab.
        /// </summary>
        public static string TabLabel(string agentName, string baseName)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return baseName;
            string context = (baseName ?? "").Trim();
            if (context.Length == 0 || IsDefaultTabName(context)) return agentName;
            return agentName + " · " + context;
        }

        /// <summary>True for the "Terminal N" name TabManager gives a tab nobody named.</summary>
        public static bool IsDefaultTabName(string name)
        {
            const string word = "Terminal ";
            if (name == null || !name.StartsWith(word, StringComparison.Ordinal) || name.Length == word.Length)
                return false;
            for (int i = word.Length; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9') return false;
            return true;
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
            sb.AppendLine("- Other Clarion Assistant terminals, including ones in other Clarion IDEs, have names that differ from yours only by their number (`CA1`, `CA2`, ...). Never work out your own name from `list_terminals`; it is the one stated here.");            sb.AppendLine("- Messages delivered to you are addressed to `" + n + "`; reply as `" + n + "`.");
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
