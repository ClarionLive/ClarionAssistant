using System;
using System.Collections.Generic;
using System.IO;
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
    }
}
