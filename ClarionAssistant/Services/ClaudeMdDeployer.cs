using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Decides whether Clarion Assistant may write its IDE briefing to
    /// <c>&lt;workDir&gt;\.claude\CLAUDE.md</c>, and writes it when it may (GH #227).
    ///
    /// Before this existed the launch path did an unconditional
    /// <c>File.Copy(prompt, dest, overwrite: true)</c>. With no working directory configured
    /// (New Chat), workDir falls back to %USERPROFILE%, so the destination was
    /// <c>~\.claude\CLAUDE.md</c> - the user's GLOBAL Claude memory - and it was replaced
    /// with our prompt. Any user-authored project CLAUDE.md was replaced the same way.
    ///
    /// The rules now:
    ///   1. Never write into the user's Claude config dir (%USERPROFILE%\.claude, or
    ///      CLAUDE_CONFIG_DIR when set). Not even when the file is absent - CLAUDE.md there
    ///      applies to every session the user ever starts, not just ours.
    ///   2. Elsewhere, create the file when absent, and refresh it only when CA wrote it.
    ///   3. Anything else is the user's and is left alone.
    /// When the file is not written the caller must deliver the prompt another way
    /// (--append-system-prompt-file), so skipping never costs the session its briefing.
    ///
    /// OWNERSHIP MARKER. A file is CA's when it opens with <see cref="Signature"/> - the first
    /// lines of the shipped prompt, unchanged in every revision since it was added. We use the
    /// prompt's own opening rather than an added marker line because the deployed file must stay
    /// byte-identical to the bundled prompt: this repo tracks its own .claude\CLAUDE.md and
    /// Check-PromptSync.ps1 fails on any difference. tests\ClaudeMdDeployer.Test.cs checks the
    /// real shipped prompt still opens with the signature, so the two cannot drift silently.
    /// A user who wants to keep an edited copy only has to change its first lines.
    ///
    /// ZERO IDE coupling on purpose: the test compiles this file straight out of the tree.
    /// Keep it that way.
    /// </summary>
    public static class ClaudeMdDeployer
    {
        /// <summary>How the shipped prompt begins (line endings normalised to LF).</summary>
        public const string Signature =
            "# Clarion IDE Assistant\n\nYou are running INSIDE the Clarion IDE as an embedded assistant.";

        public enum Outcome
        {
            Created,             // file was absent; written
            Refreshed,           // CA-owned file overwritten with the current prompt
            SkippedUserConfig,   // destination is the user's global Claude config dir
            SkippedUserAuthored, // existing file is not CA's
            SourceMissing,
            Failed
        }

        /// <summary>True when the file at this path now carries CA's current briefing.</summary>
        public static bool Delivered(Outcome o)
        {
            return o == Outcome.Created || o == Outcome.Refreshed;
        }

        /// <summary>
        /// Writes the briefing to &lt;workDir&gt;\.claude\CLAUDE.md if the rules allow.
        /// <paramref name="userProfile"/> and <paramref name="claudeConfigDir"/> are passed in
        /// (rather than read here) so a test can point them at a temp folder.
        /// </summary>
        public static Outcome Deploy(string sourcePath, string workDir, string userProfile, string claudeConfigDir)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return Outcome.SourceMissing;
                if (string.IsNullOrEmpty(workDir)) return Outcome.Failed;

                string claudeDir = Path.Combine(workDir, ".claude");
                if (IsUserConfigDir(claudeDir, userProfile, claudeConfigDir))
                    return Outcome.SkippedUserConfig;

                string dest = Path.Combine(claudeDir, "CLAUDE.md");
                bool existed = File.Exists(dest);
                if (existed && !IsCaOwned(File.ReadAllText(dest)))
                    return Outcome.SkippedUserAuthored;

                Directory.CreateDirectory(claudeDir);
                // Refresh a CA-owned file on every launch - the dynamic context from last session
                // needs to be cleared. A byte copy, so no encoding (and no BOM) is introduced.
                File.Copy(sourcePath, dest, true);
                return existed ? Outcome.Refreshed : Outcome.Created;
            }
            catch
            {
                return Outcome.Failed;
            }
        }

        /// <summary>
        /// True when <paramref name="claudeDir"/> is the directory Claude Code reads the user's
        /// global CLAUDE.md from: %USERPROFILE%\.claude, or CLAUDE_CONFIG_DIR when that is set.
        /// Both are checked even when CLAUDE_CONFIG_DIR is set - the profile folder is still where
        /// a user expects "their" .claude to be, and writing there is never ours to do.
        /// </summary>
        public static bool IsUserConfigDir(string claudeDir, string userProfile, string claudeConfigDir)
        {
            string target = Normalize(claudeDir);
            if (target == null) return false;
            if (!string.IsNullOrEmpty(userProfile) &&
                string.Equals(target, Normalize(Path.Combine(userProfile, ".claude")), StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(claudeConfigDir) &&
                string.Equals(target, Normalize(claudeConfigDir), StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>True when an existing CLAUDE.md was written by CA (opens with <see cref="Signature"/>).</summary>
        public static bool IsCaOwned(string content)
        {
            if (content == null) return false;
            string t = content.TrimStart('\uFEFF').Replace("\r\n", "\n");
            return t.StartsWith(Signature, StringComparison.Ordinal);
        }

        // Exactly the one-line file AssistantChatControl.DeployStatusLineConfig writes, and nothing
        // else: a single statusLine object whose command is a JSON string.
        private static readonly Regex CaStatusLineSettings = new Regex(
            @"^\{""statusLine"":\{""type"":""command"",""command"":""(?:[^""\\]|\\.)*""\}\}$",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// True when a .claude\settings.local.json is the statusLine-only file CA writes, so CA may
        /// replace it. Anything else - notably the permission allow-lists Claude Code itself saves
        /// into this file, which it pretty-prints - belongs to the user and must not be replaced.
        /// </summary>
        public static bool IsCaOwnedSettingsLocal(string content)
        {
            if (content == null) return false;
            string t = content.TrimStart('\uFEFF').Trim();
            return t.IndexOf("ca-statusline.js", StringComparison.OrdinalIgnoreCase) >= 0
                && CaStatusLineSettings.IsMatch(t);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                return Path.GetFullPath(path.Trim().Trim('"'))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }
    }
}
