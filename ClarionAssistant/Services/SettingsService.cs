using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Persists user settings in AppData folder.
    /// </summary>
    public class SettingsService
    {
        private readonly string _settingsPath;
        private Dictionary<string, string> _settings;

        public SettingsService()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _settingsPath = Path.Combine(folder, "settings.txt");
            _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Load();
        }

        public string Get(string key) => _settings.TryGetValue(key ?? "", out var v) ? v : null;

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
            _settings[key] = value ?? "";
            Save();
        }

        public void Remove(string key)
        {
            if (_settings.Remove(key ?? "")) Save();
        }

        private void Load()
        {
            _settings.Clear();
            if (!File.Exists(_settingsPath)) return;
            try
            {
                foreach (var line in File.ReadAllLines(_settingsPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) _settings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# ClarionAssistant Settings");
                sb.AppendLine($"# Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                foreach (var kv in _settings) sb.AppendLine($"{kv.Key}={kv.Value}");
                File.WriteAllText(_settingsPath, sb.ToString());
            }
            catch { }
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
