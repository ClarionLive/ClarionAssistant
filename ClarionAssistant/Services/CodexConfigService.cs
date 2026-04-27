using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Registers ClarionAssistant's local MCP server in <c>~/.codex/config.toml</c>
    /// so a Codex CLI terminal launched from CA can reach the IDE-driving tools.
    ///
    /// Codex CLI does not accept an <c>--mcp-config</c> flag (unlike Claude / Copilot);
    /// MCP servers must be declared in the user-global config.toml. CA's MCP server
    /// runs HTTP on localhost with a per-session bearer token, so the TOML block
    /// uses a stdio bridge via a globally-installed <c>mcp-remote</c> shim
    /// (Codex CLI's native HTTP transport is unreliable against Streamable-HTTP).
    ///
    /// SECURITY: We require a pre-installed, locally-resolved <c>mcp-remote.cmd</c>
    /// rather than letting <c>npx</c> auto-fetch from the public registry at launch
    /// time. The bridge process receives the live MCP bearer token, so a typosquat
    /// or registry compromise of <c>mcp-remote</c> would get authenticated access
    /// to the IDE bridge. Fail-closed if the shim isn't installed; surface an
    /// install instruction to the user instead.
    ///
    /// CONCURRENCY: A static lock serializes all in-process writers to the same
    /// config file (multiple Codex tabs launching simultaneously would otherwise
    /// race the read-modify-write cycle and clobber each other). Cross-process
    /// safety relies on <see cref="File.Replace(string,string,string)"/>'s atomic
    /// ReplaceFileW semantics on NTFS.
    ///
    /// MARKER COEXISTENCE: Codex CLI itself sometimes appends its own state tables
    /// (e.g. <c>[tui.model_availability_nux]</c>) at the end of the file, which
    /// can land inside our marker block when we wrote earlier. Before each
    /// rewrite, we lift any foreign top-level table out of the managed block so
    /// it survives.
    /// </summary>
    public static class CodexConfigService
    {
        private const string ManagedMarkerBegin = "# >>> CLARIONASSISTANT MANAGED — do not edit (begin) <<<";
        private const string ManagedMarkerEnd   = "# <<< CLARIONASSISTANT MANAGED — do not edit (end) >>>";
        private const string ServerName         = "clarion-assistant";
        private const string ManagedTablePrefix = "mcp_servers." + ServerName;

        // Serializes in-process writers. Cross-process is handled by File.Replace's
        // atomic ReplaceFileW semantics; this lock prevents two CA tabs in the same
        // process from racing the read-modify-write cycle.
        private static readonly object _writeLock = new object();

        public static string GetCodexConfigPath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".codex", "config.toml");
        }

        /// <summary>
        /// Refresh the managed MCP block in <c>~/.codex/config.toml</c>. Idempotent
        /// and content-diffing — no write if the existing block already matches.
        /// Returns the config path on success, or null on any failure (caller
        /// surfaces the error to the user).
        ///
        /// On failure, sets <paramref name="failureReason"/> with a single-line
        /// human-readable cause so the launcher can put it in the terminal banner.
        /// </summary>
        public static string EnsureMcpRegistration(string mcpUrl, string bearerToken, out string failureReason)
        {
            failureReason = null;

            if (string.IsNullOrWhiteSpace(mcpUrl))
            {
                failureReason = "MCP server URL is empty (server may have stopped before launch).";
                return null;
            }

            string mcpRemotePath = CodexProcessManager.FindMcpRemotePath();
            if (string.IsNullOrEmpty(mcpRemotePath))
            {
                failureReason = "mcp-remote not installed. Run: npm install -g mcp-remote@" + CodexProcessManager.McpRemoteVersion;
                return null;
            }

            // Version pin enforcement. Without this, an older mcp-remote satisfies
            // the presence check and CA writes a path that may behave differently
            // than the version we tested. Hard-fail with a distinct reason so the
            // user can tell version-skew apart from missing-install.
            string installedVersion = CodexProcessManager.GetInstalledMcpRemoteVersion();
            if (!string.Equals(installedVersion, CodexProcessManager.McpRemoteVersion, StringComparison.Ordinal))
            {
                failureReason = "mcp-remote version mismatch (installed: "
                    + (installedVersion ?? "unknown")
                    + ", expected: " + CodexProcessManager.McpRemoteVersion
                    + "). Run: npm install -g mcp-remote@" + CodexProcessManager.McpRemoteVersion;
                return null;
            }

            lock (_writeLock)
            {
                try
                {
                    string managedBlock = BuildManagedTomlBlock(mcpUrl, bearerToken, mcpRemotePath);
                    string configPath = GetCodexConfigPath();
                    string configDir = Path.GetDirectoryName(configPath);
                    if (!string.IsNullOrEmpty(configDir))
                        Directory.CreateDirectory(configDir);

                    bool fileExisted = File.Exists(configPath);
                    string existing = fileExisted ? File.ReadAllText(configPath) : string.Empty;
                    string updated = ReplaceOrAppendManagedBlock(existing, managedBlock);

                    if (string.Equals(existing, updated, StringComparison.Ordinal))
                        return configPath;

                    if (fileExisted)
                    {
                        string bak = configPath + ".clarionassistant.bak";
                        if (!File.Exists(bak))
                        {
                            try { File.Copy(configPath, bak, overwrite: false); }
                            catch { }
                        }
                    }

                    // Atomic write. File.Replace uses ReplaceFileW on NTFS — single
                    // syscall, concurrent readers see either old or new, never a
                    // half-written file. Falls back to first-write Move when the
                    // destination doesn't exist yet (Replace requires it to exist).
                    //
                    // Per-invocation unique tmp name: cross-process safety. Two CA
                    // processes (e.g. two IDE instances) share ~/.codex/config.toml
                    // but NOT _writeLock. A fixed ".ca-new" name would let process A
                    // overwrite process B's staged content. PID + GUID in the
                    // filename eliminates the collision entirely.
                    string tmpPath = configPath + ".ca-new-"
                        + System.Diagnostics.Process.GetCurrentProcess().Id + "-"
                        + Guid.NewGuid().ToString("N");
                    File.WriteAllText(tmpPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    try
                    {
                        if (fileExisted)
                        {
                            File.Replace(tmpPath, configPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                        }
                        else
                        {
                            // First-write cross-process race: another process may have
                            // created the file after our File.Exists check but before
                            // we Move. If Move fails because dest now exists, retry as
                            // Replace so the latest writer wins atomically rather than
                            // dropping the registration with an exception.
                            try
                            {
                                File.Move(tmpPath, configPath);
                            }
                            catch (IOException) when (File.Exists(configPath))
                            {
                                File.Replace(tmpPath, configPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                            }
                        }
                    }
                    catch
                    {
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                        throw;
                    }

                    return configPath;
                }
                catch (Exception ex)
                {
                    failureReason = "Writing ~/.codex/config.toml failed: " + ex.Message;
                    System.Diagnostics.Debug.WriteLine("[CodexConfigService] EnsureMcpRegistration failed: " + ex);
                    return null;
                }
            }
        }

        private static string BuildManagedTomlBlock(string mcpUrl, string bearerToken, string mcpRemotePath)
        {
            // Resolved local path for mcp-remote.cmd — no npx, no registry fetch.
            // The user installs it once with `npm install -g mcp-remote@<version>`
            // and CA then invokes it directly. This eliminates the supply-chain
            // surface that runtime-`npx` would expose to the bearer token.
            var sb = new StringBuilder();
            sb.AppendLine(ManagedMarkerBegin);
            sb.AppendLine("# Generated by ClarionAssistant — points Codex CLI at the IDE's local MCP server.");
            sb.AppendLine("# Content between the markers is replaced on every Codex terminal launch.");
            sb.AppendLine("# CA rewrites the bearer token each launch, so do not hand-edit it here.");
            sb.AppendLine("# To change the bridge: npm install -g mcp-remote@" + CodexProcessManager.McpRemoteVersion);
            sb.AppendLine();
            sb.AppendLine("[mcp_servers." + ServerName + "]");
            sb.AppendLine("command = " + TomlQuote(mcpRemotePath));

            var args = new List<string> { mcpUrl };
            if (!string.IsNullOrEmpty(bearerToken))
            {
                args.Add("--header");
                args.Add("Authorization:Bearer " + bearerToken);
            }
            sb.Append("args = [");
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TomlQuote(args[i]));
            }
            sb.AppendLine("]");
            sb.AppendLine("startup_timeout_sec = 30");
            sb.AppendLine();
            sb.Append(ManagedMarkerEnd);
            return sb.ToString();
        }

        /// <summary>
        /// Replace the existing managed block (or append a new one), but FIRST
        /// lift any foreign top-level TOML table out of the existing block to
        /// preserve content Codex CLI may have written inside our markers
        /// (e.g. <c>[tui.model_availability_nux]</c>).
        ///
        /// "Foreign" = any <c>[...]</c> section header whose name does NOT start
        /// with <c>mcp_servers.clarion-assistant</c>. Lifted sections are appended
        /// after the end marker so the next rewrite leaves them alone.
        /// </summary>
        private static string ReplaceOrAppendManagedBlock(string existing, string managedBlock)
        {
            int beginIdx = existing.IndexOf(ManagedMarkerBegin, StringComparison.Ordinal);
            int endIdx   = existing.IndexOf(ManagedMarkerEnd,   StringComparison.Ordinal);

            if (beginIdx >= 0 && endIdx > beginIdx)
            {
                // Extract the body between markers (excluding the marker lines themselves).
                int bodyStart = beginIdx + ManagedMarkerBegin.Length;
                if (bodyStart < existing.Length && existing[bodyStart] == '\r') bodyStart++;
                if (bodyStart < existing.Length && existing[bodyStart] == '\n') bodyStart++;
                string body = existing.Substring(bodyStart, endIdx - bodyStart);

                string foreignTables = ExtractForeignTables(body);

                int after = endIdx + ManagedMarkerEnd.Length;
                if (after < existing.Length && existing[after] == '\r') after++;
                if (after < existing.Length && existing[after] == '\n') after++;

                var sb = new StringBuilder();
                sb.Append(existing, 0, beginIdx);
                sb.Append(managedBlock);
                sb.Append(Environment.NewLine);
                if (!string.IsNullOrEmpty(foreignTables))
                {
                    sb.Append(Environment.NewLine);
                    sb.Append(foreignTables);
                    if (!foreignTables.EndsWith("\n", StringComparison.Ordinal))
                        sb.Append(Environment.NewLine);
                }
                sb.Append(existing, after, existing.Length - after);
                return sb.ToString();
            }

            // Append. Separate from any existing content with a blank line.
            var append = new StringBuilder(existing);
            if (append.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal))
                append.Append(Environment.NewLine);
            if (append.Length > 0)
                append.Append(Environment.NewLine);
            append.Append(managedBlock);
            append.Append(Environment.NewLine);
            return append.ToString();
        }

        /// <summary>
        /// Walk the supplied TOML body line-by-line and return only the sections
        /// (header + body lines up to the next header) whose section name does NOT
        /// start with <see cref="ManagedTablePrefix"/>. Returns an empty string if
        /// no foreign tables are present.
        ///
        /// NOTE: This is a line scanner, not a full TOML parser. It intentionally
        /// tracks multi-line basic / literal string state (<c>"""..."""</c> /
        /// <c>'''...'''</c>) so a bracketed token at column 0 inside a multi-line
        /// string isn't misclassified as a section header. Any "bare" key=value
        /// lines that appeared between the begin marker and the first section
        /// header are intentionally discarded — CA only emits comments and a blank
        /// line in that region, so anything else is presumed to be a transient
        /// artifact (Codex CLI's known appended content uses proper section
        /// headers like <c>[tui.model_availability_nux]</c>).
        /// </summary>
        private static string ExtractForeignTables(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;

            var lines = body.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var foreign = new StringBuilder();
            bool inForeignSection = false;
            bool inBasicMultiline = false;   // inside """..."""
            bool inLiteralMultiline = false; // inside '''...'''

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Update multi-line string toggle BEFORE deciding whether to
                // interpret leading [ as a section header.
                int basicCount = CountOccurrences(line, "\"\"\"");
                int literalCount = CountOccurrences(line, "'''");

                bool wasInMultiline = inBasicMultiline || inLiteralMultiline;

                if ((basicCount % 2) != 0) inBasicMultiline = !inBasicMultiline;
                if ((literalCount % 2) != 0) inLiteralMultiline = !inLiteralMultiline;

                bool nowInMultiline = inBasicMultiline || inLiteralMultiline;

                // If this line is part of a multi-line string body (open before
                // and after, or just closing it), preserve it under whichever
                // section flag was active when the string started.
                if (wasInMultiline)
                {
                    if (inForeignSection)
                    {
                        foreign.Append(Environment.NewLine);
                        foreign.Append(line);
                    }
                    continue;
                }

                string trimmed = line.TrimStart();

                // Detect section headers: [name] or [[name]] at line start.
                // Only when not currently inside a multi-line string value.
                if (!nowInMultiline && trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    int closeBracket = trimmed.IndexOf(']');
                    if (closeBracket > 0)
                    {
                        // Strip leading [ and any [[ for array-of-tables.
                        string name = trimmed.Substring(1, closeBracket - 1);
                        if (name.StartsWith("[", StringComparison.Ordinal)) name = name.Substring(1);
                        name = name.Trim();

                        bool isManaged = name.Equals(ManagedTablePrefix, StringComparison.Ordinal)
                            || name.StartsWith(ManagedTablePrefix + ".", StringComparison.Ordinal);

                        inForeignSection = !isManaged;
                        if (inForeignSection)
                        {
                            if (foreign.Length > 0) foreign.Append(Environment.NewLine);
                            foreign.Append(line);
                        }
                        continue;
                    }
                }

                if (inForeignSection)
                {
                    foreign.Append(Environment.NewLine);
                    foreign.Append(line);
                }
            }

            return foreign.ToString();
        }

        private static int CountOccurrences(string s, string needle)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(needle)) return 0;
            int n = 0;
            int idx = 0;
            while ((idx = s.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                n++;
                idx += needle.Length;
            }
            return n;
        }

        private static string TomlQuote(string value)
        {
            if (value == null) value = string.Empty;
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
