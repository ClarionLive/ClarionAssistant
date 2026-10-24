using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The ONE place that finds a Claude Code plugin directory to hand to <c>claude --plugin-dir</c>
    /// (GH #214). It replaces three copies that each hard-coded %USERPROFILE%\.claude: the chat control's
    /// and ClaudeProcessManager's GetClarionAssistantPluginPath, and McpServer's MultiTerminal probe. Those
    /// copies also hard-coded the cache folder "1.0.0", a version the plugin left behind long ago, so the
    /// cache fallback never matched anything (ticket 7c36dd75).
    ///
    /// Resolution order, first hit wins:
    ///   1. the SHARED marketplace folder the installer was told to use (registry
    ///      HKCU\Software\ClarionAssistant\PluginMarketplacePath, written by configure.ps1). Missing or
    ///      not a plugin -> fall through, never an error: a deleted shared folder must not cost the
    ///      developer their plugin when a profile copy still exists.
    ///   2. the ACTIVE Claude profile: CLAUDE_CONFIG_DIR when set, else %USERPROFILE%\.claude
    ///        a. plugins\marketplaces\&lt;marketplace&gt;\plugins\&lt;plugin&gt;
    ///        b. plugins\cache\&lt;marketplace&gt;\&lt;plugin&gt;\&lt;newest version&gt;
    ///   3. %USERPROFILE%\.claude, the same two probes, when CLAUDE_CONFIG_DIR named somewhere else - so a
    ///      developer who sets CLAUDE_CONFIG_DIR globally keeps today's plugin rather than losing it.
    ///
    /// A folder counts only when it holds .claude-plugin\plugin.json: "is this a loadable plugin", not "does
    /// some folder exist". The returned path is the PLUGIN folder, never its parent - a --plugin-dir that
    /// names a folder OF plugins loads every child (see McpServer.GetMultiTerminalPluginPath).
    ///
    /// Inputs are explicit (<see cref="Inputs"/>) so the harness can drive every tier without touching the
    /// real profile or registry, and so per-environment profiles (GH #213) can pass their own config dir.
    /// No IDE coupling: compiled straight into tests\ClaudePluginLocator.Test.cs.
    /// </summary>
    public static class ClaudePluginLocator
    {
        public const string RegistryKeyPath = @"Software\ClarionAssistant";
        public const string SharedPathValueName = "PluginMarketplacePath";

        public const string ClarionAssistantMarketplace = "clarionassistant-marketplace";
        public const string ClarionAssistantPlugin = "clarion-assistant";
        public const string MultiTerminalMarketplace = "multiterminal-marketplace";
        public const string MultiTerminalPlugin = "multiterminal";

        /// <summary>Everything the lookup reads from the machine. Null/empty members are simply skipped.</summary>
        public sealed class Inputs
        {
            /// <summary>The shared CLARION ASSISTANT marketplace folder (the registry value), or null.</summary>
            public string SharedMarketplaceDir;
            /// <summary>CLAUDE_CONFIG_DIR, or null when unset.</summary>
            public string ClaudeConfigDir;
            /// <summary>%USERPROFILE%.</summary>
            public string UserProfile;

            public static Inputs FromEnvironment()
            {
                return new Inputs
                {
                    SharedMarketplaceDir = ReadSharedMarketplaceDir(),
                    ClaudeConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
                    UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                };
            }
        }

        /// <summary>What was found and which tier found it ("shared", "profile marketplace", "profile cache").</summary>
        public sealed class Resolution
        {
            public string Path;
            public string Tier;
            public override string ToString() { return Tier + ": " + Path; }
        }

        public static string FindClarionAssistantPlugin()
        {
            return Find(ClarionAssistantMarketplace, ClarionAssistantPlugin, Inputs.FromEnvironment());
        }

        public static string FindMultiTerminalPlugin()
        {
            return Find(MultiTerminalMarketplace, MultiTerminalPlugin, Inputs.FromEnvironment());
        }

        public static string Find(string marketplace, string plugin, Inputs inputs)
        {
            try
            {
                var r = Resolve(marketplace, plugin, inputs);
                return r == null ? null : r.Path;
            }
            catch { return null; }   // a plugin we cannot find must never stop a session launching
        }

        public static Resolution Resolve(string marketplace, string plugin, Inputs inputs)
        {
            if (inputs == null || string.IsNullOrEmpty(marketplace) || string.IsNullOrEmpty(plugin)) return null;

            // 1. Shared folder.
            string sharedMarketplace = SharedMarketplaceFor(marketplace, inputs.SharedMarketplaceDir);
            if (sharedMarketplace != null)
            {
                string p = Path.Combine(sharedMarketplace, "plugins", plugin);
                if (IsPlugin(p)) return new Resolution { Path = p, Tier = "shared" };
            }

            // 2./3. Active profile, then the default one.
            foreach (string profile in ProfileRoots(inputs))
            {
                string mkt = Path.Combine(profile, "plugins", "marketplaces", marketplace, "plugins", plugin);
                if (IsPlugin(mkt)) return new Resolution { Path = mkt, Tier = "profile marketplace" };

                string cached = NewestCachedVersion(Path.Combine(profile, "plugins", "cache", marketplace, plugin));
                if (cached != null) return new Resolution { Path = cached, Tier = "profile cache" };
            }
            return null;
        }

        /// <summary>
        /// The Claude profile folders to probe, in order: CLAUDE_CONFIG_DIR when set, then %USERPROFILE%\.claude
        /// (once - not twice when CLAUDE_CONFIG_DIR already names it).
        /// </summary>
        public static IList<string> ProfileRoots(Inputs inputs)
        {
            var roots = new List<string>();
            string cfg = Normalize(inputs.ClaudeConfigDir);
            if (cfg != null) roots.Add(cfg);
            if (!string.IsNullOrEmpty(inputs.UserProfile))
            {
                string def = Normalize(Path.Combine(inputs.UserProfile, ".claude"));
                if (def != null && (cfg == null || !string.Equals(cfg, def, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(def);
            }
            return roots;
        }

        /// <summary>
        /// The shared folder for <paramref name="marketplace"/>. The registry names the Clarion Assistant
        /// marketplace itself; another marketplace (MultiTerminal) is looked for BESIDE it, so one shared
        /// root - ...\claude-plugins\marketplaces\ - can hold both (GH #214 notes). Only when the configured
        /// folder is actually named after our marketplace, so a custom leaf never sends us to a guessed sibling.
        /// </summary>
        public static string SharedMarketplaceFor(string marketplace, string sharedClarionAssistantDir)
        {
            string shared = Normalize(sharedClarionAssistantDir);
            if (shared == null) return null;
            if (string.Equals(marketplace, ClarionAssistantMarketplace, StringComparison.OrdinalIgnoreCase)) return shared;
            if (!string.Equals(Path.GetFileName(shared), ClarionAssistantMarketplace, StringComparison.OrdinalIgnoreCase)) return null;
            string parent = Path.GetDirectoryName(shared);
            return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, marketplace);
        }

        /// <summary>
        /// The newest version folder under a plugin's cache folder (plugins\cache\&lt;mkt&gt;\&lt;plugin&gt;), or null.
        /// Each candidate must be a loadable plugin; its version is the "version" in its own plugin.json, else
        /// the folder name. Parseable versions outrank unparseable ones (a git-sha folder); ties and the
        /// unparseable go by last write time.
        /// </summary>
        public static string NewestCachedVersion(string pluginCacheDir)
        {
            if (string.IsNullOrEmpty(pluginCacheDir) || !Directory.Exists(pluginCacheDir)) return null;
            string best = null;
            Version bestVer = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string dir in Directory.GetDirectories(pluginCacheDir))
            {
                if (!IsPlugin(dir)) continue;
                Version v = ParseVersion(ReadPluginVersion(dir)) ?? ParseVersion(Path.GetFileName(dir));
                DateTime t = Directory.GetLastWriteTimeUtc(dir);
                bool better;
                if (best == null) better = true;
                else if (v != null && bestVer == null) better = true;
                else if (v == null && bestVer != null) better = false;
                else if (v != null && v != bestVer) better = v > bestVer;
                else better = t > bestTime;
                if (better) { best = dir; bestVer = v; bestTime = t; }
            }
            return best;
        }

        /// <summary>The "version" string from &lt;pluginDir&gt;\.claude-plugin\plugin.json, or null.</summary>
        public static string ReadPluginVersion(string pluginDir)
        {
            try
            {
                string manifest = Path.Combine(pluginDir, ".claude-plugin", "plugin.json");
                if (!File.Exists(manifest)) return null;
                var m = Regex.Match(File.ReadAllText(manifest), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>The configured shared marketplace folder, environment variables expanded, or null.</summary>
        public static string ReadSharedMarketplaceDir()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
                {
                    if (key == null) return null;
                    return Normalize(key.GetValue(SharedPathValueName) as string);
                }
            }
            catch { return null; }
        }

        static bool IsPlugin(string dir)
        {
            try { return File.Exists(Path.Combine(dir, ".claude-plugin", "plugin.json")); }
            catch { return false; }
        }

        static Version ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var m = Regex.Match(s.Trim(), @"^v?(\d+)(\.\d+){0,3}");
            if (!m.Success) return null;
            string core = m.Value.TrimStart('v');
            if (core.IndexOf('.') < 0) core += ".0";
            Version v;
            return Version.TryParse(core, out v) ? v : null;
        }

        /// <summary>Trimmed, quotes stripped, %VARS% expanded, trailing separator removed; null unless rooted.</summary>
        static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
            if (p.Length == 0) return null;
            try
            {
                if (!Path.IsPathRooted(p)) return null;
                p = Path.GetFullPath(p);
            }
            catch { return null; }
            if (p.Length > 3) p = p.TrimEnd('\\', '/');
            return p;
        }
    }
}
