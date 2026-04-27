using System;
using System.Diagnostics;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Minimal helper for resolving the OpenAI Codex CLI executable.
    /// Intended for terminal-based launch (ConPTY) where we want a concrete path
    /// when the user configured a bare "codex" command.
    ///
    /// Mirrors <see cref="CopilotProcessManager"/>: pure path discovery, no settings
    /// dependency. User overrides flow through <c>Codex.Commands</c> in settings.txt
    /// and are honored by the launch builder, not here.
    /// </summary>
    public static class CodexProcessManager
    {
        /// <summary>
        /// Pinned mcp-remote version. Bumped manually when CA is tested against a
        /// new release. Keep in sync with the install instructions in the Codex
        /// settings panel and any upgrade notes.
        /// </summary>
        public const string McpRemoteVersion = "0.1.38";

        public static string FindCodexPathStatic() => FindCodexPath();

        /// <summary>
        /// Locate a globally-installed <c>mcp-remote</c> shim. Returns the absolute
        /// path to <c>mcp-remote.cmd</c> when found in a trusted install root, else
        /// null. Used by the Codex launcher to fail-closed (don't fetch from the
        /// registry at launch time); the user must
        /// <c>npm install -g mcp-remote@&lt;version&gt;</c> before CA wires it into
        /// Codex.
        ///
        /// SECURITY: We deliberately do NOT consult <c>where mcp-remote</c> or PATH —
        /// <c>where</c> searches the current directory before PATH, and PATH itself
        /// can include user-writable folders. A repo-local or path-planted
        /// <c>mcp-remote.cmd</c> would otherwise receive the live MCP bearer token
        /// that BuildManagedTomlBlock embeds in its args (OWASP A08 — codex
        /// security-auditor Run 2 finding). Resolving only inside the npm global
        /// prefix (Windows: <c>%APPDATA%\npm</c>) treats "installed via npm i -g"
        /// as the trust signal.
        /// </summary>
        public static string FindMcpRemotePath()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData)) return null;

                // npm global prefix on Windows is %APPDATA%\npm (cmd shim resides
                // there directly, package itself under node_modules).
                string globalCmd = Path.Combine(appData, "npm", "mcp-remote.cmd");
                if (File.Exists(globalCmd)) return Path.GetFullPath(globalCmd);
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Read the installed mcp-remote package version from its package.json.
        /// Returns the version string or null if it can't be determined.
        ///
        /// We read the JSON directly rather than running <c>mcp-remote --version</c>
        /// because the latter spawns a Node process every Codex tab launch (slow,
        /// and surfaces any errors in package init). package.json is the
        /// authoritative manifest — fast, side-effect free.
        /// </summary>
        public static string GetInstalledMcpRemoteVersion()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData)) return null;

                string packageJson = Path.Combine(
                    appData, "npm", "node_modules", "mcp-remote", "package.json");
                if (!File.Exists(packageJson)) return null;

                string text = File.ReadAllText(packageJson);
                // Cheap regex-free parse: package.json always contains "version": "X.Y.Z"
                // near the top. Avoids pulling in a JSON dependency for one field.
                const string key = "\"version\"";
                int kIdx = text.IndexOf(key, StringComparison.Ordinal);
                if (kIdx < 0) return null;
                int colonIdx = text.IndexOf(':', kIdx + key.Length);
                if (colonIdx < 0) return null;
                int q1 = text.IndexOf('"', colonIdx + 1);
                if (q1 < 0) return null;
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) return null;
                return text.Substring(q1 + 1, q2 - q1 - 1).Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string FindCodexPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 1. WinGet link (common Windows install path).
            try
            {
                string wingetLink = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "codex.exe");
                if (File.Exists(wingetLink)) return wingetLink;
            }
            catch { }

            // 2. npm global install (codex CLI ships as an npm package).
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string npmGlobal = Path.Combine(appData, "npm", "codex.cmd");
                if (File.Exists(npmGlobal)) return npmGlobal;
            }
            catch { }

            // 3. PATH via where.exe.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "codex",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadLine();
                    proc.WaitForExit(3000);
                    if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        return output;
                }
            }
            catch { }

            return null;
        }
    }
}
