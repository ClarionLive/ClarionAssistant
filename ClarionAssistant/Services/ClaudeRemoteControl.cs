using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The "Remote Control" launch setting (ticket b17d528b, ported from MultiTerminal d096464f). With it on,
    /// every new Claude tab starts with Claude Code's own <c>--remote-control</c>, so the session can also be
    /// continued from claude.ai/code and the Claude app. The remote session's title comes from the tab's
    /// <c>-n</c> (CA&lt;n&gt;), which is why the flag is BARE: the name is stated once and cannot drift.
    ///
    /// <para>WHERE THE FLAG GOES. <c>--remote-control [name]</c> takes an OPTIONAL name, so a bare word after it
    /// would be swallowed as the title. MultiTerminal guards that by scanning the command. CA does not need to:
    /// the launch puts the flag in its OWN part of the command, straight after <c>-n</c> (or straight after the
    /// developer's command when a resumed session keeps its title), and that part always continues with a
    /// <c>-</c> flag (<c>--session-id</c>/<c>--resume</c>, then <c>--strict-mcp-config</c>). The developer's
    /// command words all come BEFORE it, so none can be taken as the name.</para>
    ///
    /// <para>Read at each launch: only tabs opened after the setting is saved pick it up.</para>
    /// </summary>
    public static class ClaudeRemoteControl
    {
        public const string SettingKey = "Claude.RemoteControl";

        internal const string FlagText = " --remote-control";

        /// <summary>On only for a stored "true" (any case, trimmed). Anything else, including missing, is off.</summary>
        public static bool IsOn(string raw)
        {
            return raw != null && raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The text to add to the launch: <c>" --remote-control"</c>, or empty when the setting is off or the
        /// developer's own Claude command already asks for Remote Control (<c>--remote-control</c> or
        /// <c>--rc</c>, with or without <c>=name</c>), so the flag is never given twice.
        /// </summary>
        /// <param name="enabled">The setting, from <see cref="IsOn"/>.</param>
        /// <param name="userCommand">The developer's Claude command from the Commands list, e.g. <c>claude -c</c>.</param>
        public static string Flag(bool enabled, string userCommand)
        {
            if (!enabled) return string.Empty;
            return AlreadyAsks(userCommand) ? string.Empty : FlagText;
        }

        /// <summary>True when a token of the command is <c>--remote-control</c> or <c>--rc</c> (alone or <c>=value</c>).</summary>
        internal static bool AlreadyAsks(string userCommand)
        {
            foreach (string token in PwshCommandQuoter.Tokenize(userCommand))
            {
                if (IsFlag(token, "--remote-control") || IsFlag(token, "--rc"))
                    return true;
            }
            return false;
        }

        private static bool IsFlag(string token, string flag)
        {
            return token.Equals(flag, StringComparison.Ordinal)
                || token.StartsWith(flag + "=", StringComparison.Ordinal);
        }
    }
}
