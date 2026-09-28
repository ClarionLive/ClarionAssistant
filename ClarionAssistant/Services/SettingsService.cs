using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Thrown by <see cref="SettingsService.Set"/> / <see cref="SettingsService.Remove"/>
    /// when the cross-process named mutex is held by another ClarionAssistant
    /// process and doesn't release within the budget. Surface this to the user
    /// (settings dialog, etc.) so they can wait + retry rather than have the
    /// save silently degrade to last-writer-wins.
    /// </summary>
    public class SettingsLockedException : Exception
    {
        public SettingsLockedException(string message) : base(message) { }
    }

    /// <summary>
    /// Persists user settings in AppData folder.
    /// </summary>
    public class SettingsService
    {
        private readonly string _settingsPath;
        private readonly Dictionary<string, string> _settings;

        // In-process serialization. STATIC so all SettingsService instances
        // share it — the addin constructs multiple (AssistantChatControl,
        // ClassHelperControl, TaskLifecycleBoardForm). Per-instance locks
        // would let two instances both reload from disk, both compute updates
        // from the same baseline, then race File.WriteAllText (last-writer-wins).
        private static readonly object _lock = new object();

        // Cross-process serialization. The project supports multi-IDE
        // operation (see InstanceCoordinationService), so two ClarionAssistant
        // processes can run side-by-side and both want to mutate settings.txt.
        // A named Mutex coordinates the read-modify-write across processes
        // belonging to the same Windows user — settings.txt is per-user
        // (%APPDATA%\ClarionAssistant\settings.txt). The mutex is acquired
        // INSIDE the static lock so in-process callers serialize cheaply
        // before paying the cross-process acquire cost. (Codex Run 3 — HIGH.)
        private const string CrossProcessMutexName = @"Local\ClarionAssistant.SettingsService.v1";

        public SettingsService()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _settingsPath = Path.Combine(folder, "settings.txt");
            _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Load();
        }

        public string Get(string key)
        {
            lock (_lock)
            {
                return _settings.TryGetValue(key ?? "", out var v) ? v : null;
            }
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            // Reject CR/LF in values: the file format is line-based (key=value\n),
            // so a value containing a newline would smuggle an additional key on
            // reload (e.g. sneaking Copilot.PermissionMode=allow onto disk via a
            // crafted ExtraFlags value).
            if (value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0))
                throw new ArgumentException(
                    "Setting values cannot contain newline characters.", "value");
            // Reject CR/LF in keys too, for symmetry and to prevent a crafted
            // key from escaping onto its own line.
            if (key.IndexOf('\r') >= 0 || key.IndexOf('\n') >= 0 || key.IndexOf('=') >= 0)
                throw new ArgumentException(
                    "Setting keys cannot contain newline or '=' characters.", "key");
            lock (_lock)
            {
                WithCrossProcessMutex(() =>
                {
                    // Reload before write so concurrent SettingsService instances
                    // (multiple controls in this addin each construct their own:
                    // AssistantChatControl, ClassHelperControl, TaskLifecycleBoardForm)
                    // don't silently overwrite each other's keys with stale state.
                    ReloadFromDisk();
                    _settings[key] = value ?? "";
                    Save();
                });
            }
        }

        public void Remove(string key)
        {
            lock (_lock)
            {
                WithCrossProcessMutex(() =>
                {
                    // Same multi-instance merge rationale as Set — see comment there.
                    ReloadFromDisk();
                    if (_settings.Remove(key ?? "")) Save();
                });
            }
        }

        // Acquire the cross-process mutex around the supplied action. Caller
        // already holds the in-process static lock, so contention here is
        // strictly cross-process.
        //
        // Two failure modes, two policies:
        //
        // 1. The OS won't give us a Mutex at all (constructor or WaitOne
        //    throws — sandboxed environment, ACL denial, etc.): fall back to
        //    in-process-only correctness. Better to keep the user's settings
        //    save working with reduced safety than to refuse on a system that
        //    can't even create a named kernel object.
        //
        // 2. The mutex exists but another process is holding it (WaitOne
        //    returns false within the 5s budget): fail closed. Throw
        //    SettingsLockedException so the caller (and any UI surface
        //    catching it) can present an actionable message instead of
        //    silently corrupting cross-process state. (Codex security
        //    Run 4 — HIGH.)
        //
        // AbandonedMutexException is benign — the prior holder crashed and
        // ownership transfers cleanly to us.
        private static void WithCrossProcessMutex(Action action)
        {
            Mutex mutex = null;
            bool acquired = false;
            bool fallbackUnsafe = false;
            try
            {
                try { mutex = new Mutex(initiallyOwned: false, name: CrossProcessMutexName); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[SettingsService] cross-process mutex create failed; in-process only: " + ex.Message);
                    fallbackUnsafe = true;
                }

                if (!fallbackUnsafe)
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[SettingsService] cross-process mutex wait failed; in-process only: " + ex.Message);
                        fallbackUnsafe = true;
                    }
                }

                if (!fallbackUnsafe && !acquired)
                {
                    // Active contention: another process is holding the mutex
                    // and didn't release within our budget. Fail closed.
                    throw new SettingsLockedException(
                        "Settings file is locked by another ClarionAssistant process. " +
                        "Wait a moment and try again, or close the other instance.");
                }

                action();
            }
            finally
            {
                try { if (acquired && mutex != null) mutex.ReleaseMutex(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[SettingsService] mutex release failed: " + ex.Message);
                }
                try { if (mutex != null) mutex.Dispose(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[SettingsService] mutex dispose failed: " + ex.Message);
                }
            }
        }

        // Caller must hold _lock. Re-reads settings.txt into the in-memory
        // dictionary so cross-instance writes are merged before our write.
        //
        // Read-then-swap pattern: parse into a local dictionary first, only
        // replace _settings when the read succeeds. This prevents a transient
        // I/O failure (file briefly locked by AV scanner, OneDrive sync, search
        // indexer, or another IDE instance mid-WriteAllText) from leaving
        // _settings empty — which would cause the next Save in Set() to persist
        // an almost-empty settings.txt and silently wipe every other key
        // (commands lists, plans, models, working dirs, MCP token, etc.).
        // A subsequent Save() with stale state is far better than persisting an
        // empty file with no user-visible error. (Debugger Run 2 — HIGH.)
        //
        // Cross-process safety (two IDE instances writing settings.txt at the
        // exact same instant) still relies on the OS file-write being atomic
        // enough that a reader sees one or the other; we don't take an OS
        // file lock here — multi-IDE simultaneous Save is a much rarer race
        // than multi-instance-same-process.
        private void ReloadFromDisk()
        {
            // Both the not-exists and read-failure branches preserve the in-memory
            // dictionary rather than wiping it. A missing file is just as likely to
            // be a transient state from an editor or sync tool doing
            // delete-then-rename as it is to be a fresh install — and on the
            // mutation path, treating "missing" as authoritative would let a single
            // Set silently overwrite settings.txt with only the just-mutated key.
            // Codex adversary Run 2 — HIGH. The clear-on-truly-empty path is left
            // to the constructor's initial Load (which executes before any Sets
            // are possible from outside).
            if (!File.Exists(_settingsPath))
            {
                System.Diagnostics.Debug.WriteLine("[SettingsService] ReloadFromDisk: settings.txt missing; keeping in-memory state");
                return;
            }

            long fileSize = 0;
            try { fileSize = new FileInfo(_settingsPath).Length; } catch { }

            var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadAllLines(_settingsPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) fresh[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[SettingsService] ReloadFromDisk read failed; keeping in-memory state");
                return;
            }

            // Partial-read guard: only refuse a fully-empty parse from a
            // non-empty file. That catches catastrophic corruption (file
            // ransomware-encrypted, all line endings stripped, etc.) but lets
            // legitimate cross-instance deletes through.
            //
            // The earlier `fresh.Count < _settings.Count` heuristic refused
            // ANY shrinkage, which conflated truncated reads with valid mass
            // deletes by another instance — instance A would resurrect keys
            // instance B legitimately removed (Codex security Run 5 — HIGH).
            //
            // We can afford to relax this because Save() now writes via
            // temp-file + File.Replace (atomic ReplaceFileW on NTFS), so
            // concurrent readers see either the old file or the new file,
            // never a partial truncated write. The reader-side guard is now
            // defense-in-depth for the catastrophic-corruption case only.
            if (fresh.Count == 0 && _settings.Count > 0 && fileSize > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[SettingsService] ReloadFromDisk refused: file has " + fileSize +
                    " bytes but parsed 0 entries while in-memory has " + _settings.Count +
                    " — likely catastrophic corruption; keeping in-memory state");
                return;
            }

            _settings.Clear();
            foreach (var kv in fresh) _settings[kv.Key] = kv.Value;
        }

        // Constructor entry point — wraps ReloadFromDisk under the lock.
        // Subsequent reloads happen inside Set/Remove which already hold the lock.
        private void Load()
        {
            lock (_lock)
            {
                ReloadFromDisk();
            }
        }

        // Caller MUST hold _lock — Save iterates _settings and any concurrent
        // mutation would throw. The current callers (Set, Remove) take the
        // lock before calling.
        //
        // Atomic write: stage to <settings>.tmp then File.Replace into the
        // target, which on NTFS becomes a single ReplaceFileW syscall —
        // concurrent readers see either the old file or the new file, never
        // a half-written one. This closes the partial-read window the
        // ReloadFromDisk heuristic guards against from the WRITER side, so
        // even an abandoned-mutex / crash-mid-write scenario can't leave a
        // truncated settings.txt for another process to misread.
        // (Codex adversary Run 4 — HIGH: defense-in-depth at the source.)
        // First-ever write falls back to plain WriteAllText since File.Replace
        // requires the destination to exist.
        private void Save()
        {
            // Exceptions PROPAGATE to the caller. Earlier this method swallowed
            // all errors to Debug.WriteLine, which meant the dialog could believe
            // a Set succeeded even when the disk write failed (e.g., File.Replace
            // unsupported on a non-NTFS volume, transient I/O error, ACL denial).
            // The in-memory dictionary would advance but disk would not, leaving
            // a security-relevant token "saved" in-memory only — gone on next
            // launch. (Codex security Run 5 — MEDIUM.)
            //
            // Now Set/Remove propagate IOException, UnauthorizedAccessException,
            // etc. up to the caller. The settings dialog's HandleSave handles
            // SettingsLockedException specifically; other exceptions bubble to
            // the form-level catch in OnWebMessageReceived which already logs.
            var sb = new StringBuilder();
            sb.AppendLine("# ClarionAssistant Settings");
            sb.AppendLine($"# Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            foreach (var kv in _settings) sb.AppendLine($"{kv.Key}={kv.Value}");

            if (File.Exists(_settingsPath))
            {
                // Per-invocation unique tmp name. Normally the static lock +
                // cross-process mutex serialize writers, but the fallbackUnsafe
                // path in WithCrossProcessMutex runs without the mutex when
                // the OS can't provide a named Mutex. PID+GUID matches the
                // CodexConfigService precedent.
                string tmpPath = _settingsPath + "." +
                    System.Diagnostics.Process.GetCurrentProcess().Id + "." +
                    Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmpPath, sb.ToString());
                try
                {
                    File.Replace(tmpPath, _settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    throw;
                }
            }
            else
            {
                // First-ever write: nothing to replace.
                File.WriteAllText(_settingsPath, sb.ToString());
            }
        }

        // ── Claude Commands ──────────────────────────────────────

        /// <summary>
        /// Returns the list of configured Claude launch commands.
        /// Format in settings.txt: Claude.Commands = command1|0;command2|1;command3|0
        /// where |1 = default.
        /// </summary>
        public List<KeyValuePair<string, bool>> GetClaudeCommands()
        {
            var result = new List<KeyValuePair<string, bool>>();
            string raw = Get("Claude.Commands");
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (string entry in raw.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    int pipe = entry.LastIndexOf('|');
                    if (pipe > 0)
                    {
                        string cmd = entry.Substring(0, pipe);
                        bool isDefault = entry.Substring(pipe + 1) == "1";
                        result.Add(new KeyValuePair<string, bool>(cmd, isDefault));
                    }
                    else
                    {
                        result.Add(new KeyValuePair<string, bool>(entry.Trim(), false));
                    }
                }
            }
            if (result.Count == 0)
            {
                // Defaults
                result.Add(new KeyValuePair<string, bool>("claude", true));
                result.Add(new KeyValuePair<string, bool>("claude -c", false));
                result.Add(new KeyValuePair<string, bool>("claude --dangerously-skip-permissions", false));
            }
            return result;
        }

        public void SetClaudeCommands(List<KeyValuePair<string, bool>> commands)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < commands.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(commands[i].Key);
                sb.Append('|');
                sb.Append(commands[i].Value ? "1" : "0");
            }
            Set("Claude.Commands", sb.ToString());
        }

        /// <summary>
        /// Returns the default Claude launch command (the base command, e.g. "claude -c").
        /// Falls back to "claude" if none marked as default.
        /// </summary>
        public string GetDefaultClaudeCommand()
        {
            var commands = GetClaudeCommands();
            foreach (var kv in commands)
                if (kv.Value) return kv.Key;
            return commands.Count > 0 ? commands[0].Key : "claude";
        }

        // ── Copilot Commands ─────────────────────────────────────

        /// <summary>
        /// Returns the list of configured Copilot launch commands.
        /// Format in settings.txt: Copilot.Commands = command1|0;command2|1
        /// where |1 = default.
        /// </summary>
        public List<KeyValuePair<string, bool>> GetCopilotCommands()
        {
            var result = new List<KeyValuePair<string, bool>>();
            string raw = Get("Copilot.Commands");
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (string entry in raw.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    int pipe = entry.LastIndexOf('|');
                    if (pipe > 0)
                    {
                        string cmd = entry.Substring(0, pipe);
                        bool isDefault = entry.Substring(pipe + 1) == "1";
                        result.Add(new KeyValuePair<string, bool>(cmd, isDefault));
                    }
                    else
                    {
                        result.Add(new KeyValuePair<string, bool>(entry.Trim(), false));
                    }
                }
            }

            if (result.Count == 0)
            {
                result.Add(new KeyValuePair<string, bool>("copilot", true));
                result.Add(new KeyValuePair<string, bool>("gh copilot", false));
                result.Add(new KeyValuePair<string, bool>("copilot --allow-all-tools", false));
            }

            return result;
        }

        public void SetCopilotCommands(List<KeyValuePair<string, bool>> commands)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < commands.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(commands[i].Key);
                sb.Append('|');
                sb.Append(commands[i].Value ? "1" : "0");
            }
            Set("Copilot.Commands", sb.ToString());
        }

        public string GetDefaultCopilotCommand()
        {
            var commands = GetCopilotCommands();
            foreach (var kv in commands)
                if (kv.Value) return kv.Key;
            return commands.Count > 0 ? commands[0].Key : "copilot";
        }

        // ── Codex Commands ───────────────────────────────────────

        /// <summary>
        /// Returns the list of configured Codex launch commands.
        /// Format in settings.txt: Codex.Commands = command1|0;command2|1
        /// where |1 = default. Mirrors the Claude / Copilot pattern.
        /// </summary>
        public List<KeyValuePair<string, bool>> GetCodexCommands()
        {
            var result = new List<KeyValuePair<string, bool>>();
            string raw = Get("Codex.Commands");
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (string entry in raw.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    int pipe = entry.LastIndexOf('|');
                    if (pipe > 0)
                    {
                        string cmd = entry.Substring(0, pipe);
                        bool isDefault = entry.Substring(pipe + 1) == "1";
                        result.Add(new KeyValuePair<string, bool>(cmd, isDefault));
                    }
                    else
                    {
                        result.Add(new KeyValuePair<string, bool>(entry.Trim(), false));
                    }
                }
            }

            if (result.Count == 0)
            {
                result.Add(new KeyValuePair<string, bool>("codex", true));
                result.Add(new KeyValuePair<string, bool>("codex --full-auto", false));
            }

            return result;
        }

        public void SetCodexCommands(List<KeyValuePair<string, bool>> commands)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < commands.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(commands[i].Key);
                sb.Append('|');
                sb.Append(commands[i].Value ? "1" : "0");
            }
            Set("Codex.Commands", sb.ToString());
        }

        public string GetDefaultCodexCommand()
        {
            var commands = GetCodexCommands();
            foreach (var kv in commands)
                if (kv.Value) return kv.Key;
            return commands.Count > 0 ? commands[0].Key : "codex";
        }

        // ── External MCP client access (issue #24) ───────────────

        /// <summary>
        /// True when the user has opted into letting external MCP clients
        /// (Claude Desktop, Cline, custom mcp-remote setups) authenticate
        /// against CA's local MCP server using the static
        /// <see cref="GetMcpExternalToken"/>. Off by default — only the
        /// per-session token (used by in-IDE Claude/Copilot/Codex tabs)
        /// authenticates when this is false.
        /// </summary>
        public bool GetMcpExternalAccessEnabled()
        {
            return string.Equals(Get("Mcp.ExternalAccessEnabled"), "true", StringComparison.OrdinalIgnoreCase);
        }

        public void SetMcpExternalAccessEnabled(bool enabled)
        {
            Set("Mcp.ExternalAccessEnabled", enabled ? "true" : "false");
        }

        /// <summary>
        /// User-managed static MCP bearer token (64-char hex). Persists across
        /// IDE sessions so an external tool's mcp-remote config keeps working
        /// after a CA restart. Empty by default — the user generates one from
        /// the Settings → MCP panel and copies the matching mcp-remote config
        /// snippet.
        /// </summary>
        public string GetMcpExternalToken()
        {
            return Get("Mcp.ExternalToken") ?? string.Empty;
        }

        public void SetMcpExternalToken(string token)
        {
            Set("Mcp.ExternalToken", token ?? string.Empty);
        }

        /// <summary>
        /// Cryptographically random 32-byte hex token (64 chars). Same shape as
        /// <c>McpServer.GenerateSessionToken</c>; intentionally duplicated here
        /// so the settings dialog can mint a token without taking a dependency
        /// on the running McpServer instance.
        /// </summary>
        public static string GenerateMcpExternalToken()
        {
            byte[] bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(64);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        // ── Backend model registry ───────────────────────────────

        /// <summary>
        /// Returns the backend model registry as a raw JSON string. The webview
        /// parses it directly to populate Plan and Model dropdowns. Three-tier
        /// loader: deployed file (user-editable), embedded resource (shipped
        /// with the assembly), then a minimal hardcoded fallback so the
        /// settings dialog can still open if both fail.
        /// </summary>
        public string GetModelRegistryJson()
        {
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string path = Path.Combine(asmDir, "Terminal", "models.json");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch { }

            try
            {
                using (Stream s = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("ClarionAssistant.Terminal.models.json"))
                {
                    if (s != null)
                        using (var reader = new StreamReader(s))
                            return reader.ReadToEnd();
                }
            }
            catch { }

            return MinimalRegistryJson;
        }

        // Minimal registry used only when both the deployed file and the embedded
        // resource are unavailable — keeps the settings dialog functional.
        private const string MinimalRegistryJson =
            "{\"version\":\"1\",\"backends\":{" +
            "\"Claude\":{\"plans\":[{\"id\":\"Pro\",\"label\":\"Pro\"}],\"defaultPlan\":\"Pro\",\"models\":[{\"id\":\"sonnet\",\"label\":\"Sonnet\",\"plans\":[\"Pro\"]}]}," +
            "\"Copilot\":{\"plans\":[{\"id\":\"Pro\",\"label\":\"Pro\"}],\"defaultPlan\":\"Pro\",\"models\":[{\"id\":\"\",\"label\":\"(default)\",\"plans\":[\"Pro\"]}]}," +
            "\"Codex\":{\"plans\":[{\"id\":\"Plus\",\"label\":\"ChatGPT Plus\"}],\"defaultPlan\":\"Plus\",\"models\":[{\"id\":\"\",\"label\":\"Auto\",\"plans\":[\"Plus\"]}]}" +
            "}}";
    }
}
