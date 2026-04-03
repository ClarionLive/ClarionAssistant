using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ClarionAssistant.Services;
using ClarionAssistant.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Dialogs
{
    public class ClaudeChatSettingsDialog : Form
    {
        private readonly SettingsService _settings;
        private readonly bool _isDark;

        private WebView2 _webView;

        private static readonly string MultiTerminalMcpPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "multiterminal", "mcp", "index.js");

        // Result properties
        public string FontFamily { get; private set; }
        public float FontSize { get; private set; }
        public bool ThemeChanged { get; private set; }
        public bool IsDarkTheme { get; private set; }

        /// <summary>Fires when user clicks OK with saved settings.</summary>
        public event Action<ClaudeChatSettingsDialog> SettingsSaved;

        public ClaudeChatSettingsDialog(SettingsService settings, bool isDarkTheme = true)
        {
            _settings = settings;
            _isDark = isDarkTheme;
            IsDarkTheme = isDarkTheme;

            // Defaults in case dialog is closed before WebView2 loads
            FontFamily = settings.Get("Claude.FontFamily") ?? "Cascadia Mono";
            float fs;
            string fsStr = settings.Get("Claude.FontSize");
            FontSize = (!string.IsNullOrEmpty(fsStr) && float.TryParse(fsStr, out fs)) ? fs : 14f;

            InitializeForm();
        }

        private void InitializeForm()
        {
            Text = "Clarion Assistant Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(720, 640);
            BackColor = _isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);

            // Restore saved position, or center on parent/screen
            RestorePosition();

            _webView = new WebView2 { Dock = DockStyle.Fill };
            _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
            Controls.Add(_webView);

            // Fire-and-forget from constructor — works because this dialog uses Show() (non-modal),
            // same pattern as TaskLifecycleBoardForm. WebView2 cannot init inside ShowDialog().
            _ = InitWebViewAsync();
        }

        private async System.Threading.Tasks.Task InitWebViewAsync()
        {
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] WebView2 init error: " + ex.Message);
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[SettingsDialog] OnWebViewInitialized: success=" + e.IsSuccess);
            if (!e.IsSuccess)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] Init FAILED: " + e.InitializationException?.GetType().Name + ": " + e.InitializationException?.Message);
                return;
            }



            var settings = _webView.CoreWebView2.Settings;
            settings.IsScriptEnabled = true;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            string htmlPath = GetHtmlPath();
            System.Diagnostics.Debug.WriteLine("[SettingsDialog] HTML path: " + htmlPath + " exists=" + File.Exists(htmlPath));
            if (File.Exists(htmlPath))
            {
                string url = new Uri(htmlPath).AbsoluteUri + "?theme=" + (_isDark ? "dark" : "light");
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] Navigating to: " + url);
                _webView.CoreWebView2.Navigate(url);
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");

                switch (action)
                {
                    case "ready":
                        SendCurrentSettings();
                        break;
                    case "save":
                        HandleSave(json);
                        break;
                    case "cancel":
                        DialogResult = DialogResult.Cancel;
                        Close();
                        break;
                    case "browseWorkingDir":
                        BrowseFolder("workingDir", "Select Working Directory", _settings.Get("Claude.WorkingDirectory"));
                        break;
                    case "browseComFolder":
                        BrowseFolder("comFolder", "Select COM Projects Folder", _settings.Get("COM.ProjectsFolder"));
                        break;
                    case "browseDocPath":
                        BrowseFolder("docPath", "Select Documentation Folder", null);
                        break;
                    case "browseDocFile":
                        BrowseFile();
                        break;
                    case "docGraphInfo":
                        HandleDocGraphInfo();
                        break;
                    case "removeDocLibraries":
                        HandleRemoveDocLibraries(json);
                        break;
                    case "updateLibraryTags":
                        HandleUpdateLibraryTags(json);
                        break;
                    case "buildLib":
                        HandleBuildLib();
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] Message error: " + ex.Message);
            }
        }

        private void SendCurrentSettings()
        {
            string fontFamily = _settings.Get("Claude.FontFamily") ?? "Cascadia Mono";
            string fontSize = _settings.Get("Claude.FontSize") ?? "14";
            string model = _settings.Get("Claude.Model") ?? "sonnet";
            string workingDir = _settings.Get("Claude.WorkingDirectory")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string comFolder = _settings.Get("COM.ProjectsFolder") ?? "";
            string theme = _isDark ? "dark" : "light";

            bool mtAvailable = File.Exists(MultiTerminalMcpPath);
            string mtEnabled = _settings.Get("MultiTerminal.Enabled");
            bool mtOn = mtEnabled == null ? mtAvailable : mtEnabled.Equals("true", StringComparison.OrdinalIgnoreCase);
            string agentName = _settings.Get("MultiTerminal.AgentName") ?? "ClarionIDE";

            string libStatus = LibraryIndexer.GetStatus();

            // Doc paths
            string docPathsRaw = _settings.Get("DocGraph.Paths") ?? "";
            var docPathsSb = new StringBuilder("[");
            bool first = true;
            foreach (string p in docPathsRaw.Split('|'))
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!first) docPathsSb.Append(",");
                docPathsSb.Append("\"" + EscapeJson(p) + "\"");
                first = false;
            }
            docPathsSb.Append("]");

            // Claude commands
            var commands = _settings.GetClaudeCommands();
            var cmdsSb = new StringBuilder("[");
            for (int ci = 0; ci < commands.Count; ci++)
            {
                if (ci > 0) cmdsSb.Append(",");
                cmdsSb.AppendFormat("{{\"command\":\"{0}\",\"isDefault\":{1}}}",
                    EscapeJson(commands[ci].Key), commands[ci].Value ? "true" : "false");
            }
            cmdsSb.Append("]");

            string json = "{\"type\":\"setSettings\",\"settings\":{"
                + "\"theme\":\"" + theme + "\""
                + ",\"fontFamily\":\"" + EscapeJson(fontFamily) + "\""
                + ",\"fontSize\":" + fontSize
                + ",\"model\":\"" + EscapeJson(model) + "\""
                + ",\"workingDir\":\"" + EscapeJson(workingDir) + "\""
                + ",\"comFolder\":\"" + EscapeJson(comFolder) + "\""
                + ",\"mtAvailable\":" + (mtAvailable ? "true" : "false")
                + ",\"mtEnabled\":" + (mtOn ? "true" : "false")
                + ",\"agentName\":\"" + EscapeJson(agentName) + "\""
                + ",\"libStatus\":\"" + EscapeJson(libStatus) + "\""
                + ",\"docPaths\":" + docPathsSb
                + ",\"commands\":" + cmdsSb
                + "}}";
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }

        private void HandleSave(string json)
        {
            // Extract nested data object values
            string data = ExtractJsonObject(json, "data");
            if (data == null) data = json;

            string theme = ExtractJsonValue(data, "theme") ?? "dark";
            string fontFamily = ExtractJsonValue(data, "fontFamily") ?? "Cascadia Mono";
            string fontSize = ExtractJsonValue(data, "fontSize") ?? "14";
            string model = ExtractJsonValue(data, "model") ?? "sonnet";
            string workingDir = ExtractJsonValue(data, "workingDir") ?? "";
            string comFolder = ExtractJsonValue(data, "comFolder") ?? "";
            bool mtEnabled = ExtractJsonValue(data, "mtEnabled") == "true";
            string agentName = ExtractJsonValue(data, "agentName") ?? "ClarionIDE";

            // Save
            _settings.Set("Theme", theme);
            _settings.Set("Claude.FontFamily", fontFamily);
            _settings.Set("Claude.FontSize", fontSize);
            _settings.Set("Claude.Model", model);
            _settings.Set("Claude.WorkingDirectory", workingDir);
            _settings.Set("COM.ProjectsFolder", comFolder);
            _settings.Set("MultiTerminal.Enabled", mtEnabled.ToString().ToLower());
            _settings.Set("MultiTerminal.AgentName", agentName);

            // Claude commands
            string commandsJson = ExtractJsonArray(data, "commands");
            if (commandsJson != null)
            {
                var cmds = new List<KeyValuePair<string, bool>>();
                // Parse array of {command:"...", isDefault:true/false}
                int cpos = 0;
                while (cpos < commandsJson.Length)
                {
                    int objStart = commandsJson.IndexOf('{', cpos);
                    if (objStart < 0) break;
                    int objEnd = commandsJson.IndexOf('}', objStart);
                    if (objEnd < 0) break;
                    string obj = commandsJson.Substring(objStart, objEnd - objStart + 1);
                    string cmd = ExtractJsonValue(obj, "command");
                    bool isDef = ExtractJsonValue(obj, "isDefault") == "true";
                    if (!string.IsNullOrEmpty(cmd))
                        cmds.Add(new KeyValuePair<string, bool>(cmd, isDef));
                    cpos = objEnd + 1;
                }
                if (cmds.Count > 0)
                    _settings.SetClaudeCommands(cmds);
            }

            // Doc paths — extract JSON array, detect new paths, trigger ingestion
            string oldPathsStr = _settings.Get("DocGraph.Paths") ?? "";
            var oldPathSet = new HashSet<string>(
                oldPathsStr.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            string docPathsJson = ExtractJsonArray(data, "docPaths");
            if (docPathsJson != null)
            {
                var paths = new System.Collections.Generic.List<string>();
                int pos = 0;
                while (pos < docPathsJson.Length)
                {
                    int q1 = docPathsJson.IndexOf('"', pos);
                    if (q1 < 0) break;
                    int q2 = docPathsJson.IndexOf('"', q1 + 1);
                    if (q2 < 0) break;
                    paths.Add(docPathsJson.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\"));
                    pos = q2 + 1;
                }
                _settings.Set("DocGraph.Paths", string.Join("|", paths));

                // Find newly added paths and ingest into personal docgraph
                var newPaths = paths.FindAll(p => !oldPathSet.Contains(p));
                if (newPaths.Count > 0)
                    IngestPersonalPaths(newPaths);
            }

            // Set result properties
            FontFamily = fontFamily;
            float fs;
            FontSize = float.TryParse(fontSize, out fs) ? Math.Max(6f, Math.Min(32f, fs)) : 14f;

            bool newIsDark = theme != "light";
            ThemeChanged = newIsDark != _isDark;
            IsDarkTheme = newIsDark;

            SettingsSaved?.Invoke(this);
            Close();
        }

        private void BrowseFolder(string target, string description, string initialPath)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = description;
                if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                    dlg.SelectedPath = initialPath;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string msg = "{\"type\":\"browseResult\",\"target\":\"" + target
                        + "\",\"path\":\"" + EscapeJson(dlg.SelectedPath) + "\"}";
                    _webView.CoreWebView2.PostWebMessageAsString(msg);
                }
            }
        }

        private void BrowseFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select Documentation File";
                dlg.Filter = "Documentation files (*.htm;*.html;*.chm;*.pdf)|*.htm;*.html;*.chm;*.pdf|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string msg = "{\"type\":\"browseResult\",\"target\":\"docPath\",\"path\":\"" + EscapeJson(dlg.FileName) + "\"}";
                    _webView.CoreWebView2.PostWebMessageAsString(msg);
                }
            }
        }

        private void HandleDocGraphInfo()
        {
            try
            {
                string bundledPath = DocGraphService.GetDefaultDbPath();
                string personalPath = DocGraphService.GetPersonalDbPath();

                var items = new StringBuilder("[");
                int totalLibs = 0, totalChunks = 0;
                long totalSize = 0;
                bool first = true;

                // Query each database that exists
                string[] dbPaths = { bundledPath, personalPath };
                string[] dbLabels = { "bundled", "personal" };

                for (int i = 0; i < dbPaths.Length; i++)
                {
                    if (!File.Exists(dbPaths[i])) continue;
                    totalSize += new FileInfo(dbPaths[i]).Length;
                    string label = dbLabels[i];

                    string connStr = "Data Source=" + dbPaths[i] + ";Version=3;Read Only=True;Journal Mode=WAL;";
                    using (var conn = new System.Data.SQLite.SQLiteConnection(connStr))
                    {
                        conn.Open();

                        // Check if tags column exists (bundled DB may not have it)
                        bool hasTags = false;
                        using (var pragma = conn.CreateCommand())
                        {
                            pragma.CommandText = "PRAGMA table_info(libraries)";
                            using (var pr = pragma.ExecuteReader())
                                while (pr.Read())
                                    if (pr.GetString(1) == "tags") { hasTags = true; break; }
                        }

                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = hasTags
                                ? "SELECT l.id, l.name, l.vendor, l.tags, COUNT(c.id) as cnt FROM libraries l LEFT JOIN doc_chunks c ON c.library_id = l.id GROUP BY l.id ORDER BY cnt DESC"
                                : "SELECT l.id, l.name, l.vendor, NULL as tags, COUNT(c.id) as cnt FROM libraries l LEFT JOIN doc_chunks c ON c.library_id = l.id GROUP BY l.id ORDER BY cnt DESC";
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    long id = reader.GetInt64(0);
                                    string name = reader.GetString(1);
                                    string vendor = reader.IsDBNull(2) ? "" : reader.GetString(2);
                                    string tags = reader.IsDBNull(3) ? "" : reader.GetString(3);
                                    int cnt = reader.GetInt32(4);
                                    totalLibs++;
                                    totalChunks += cnt;
                                    if (!first) items.Append(",");
                                    items.AppendFormat("{{\"id\":{0},\"name\":\"{1}\",\"vendor\":\"{2}\",\"chunks\":{3},\"source\":\"{4}\",\"tags\":\"{5}\"}}",
                                        id, EscapeJson(name), EscapeJson(vendor), cnt, label, EscapeJson(tags));
                                    first = false;
                                }
                            }
                        }
                    }
                }
                items.Append("]");

                string dbSizeMb = (totalSize / (1024.0 * 1024.0)).ToString("F1");
                string json = "{\"type\":\"docGraphInfo\",\"libraries\":" + totalLibs
                    + ",\"chunks\":" + totalChunks
                    + ",\"dbSizeMb\":\"" + dbSizeMb + "\""
                    + ",\"items\":" + items + "}";
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] DocGraph info error: " + ex.Message);
                _webView.CoreWebView2.PostWebMessageAsString(
                    "{\"type\":\"docGraphInfo\",\"libraries\":0,\"chunks\":0,\"dbSizeMb\":\"0\",\"items\":[]}");
            }
        }

        private void HandleRemoveDocLibraries(string json)
        {
            try
            {
                string personalPath = DocGraphService.GetPersonalDbPath();
                if (!File.Exists(personalPath)) return;

                // Extract the ids array from "data":{"ids":[1,2,3]}
                string data = ExtractJsonObject(json, "data");
                if (data == null) return;
                string idsArr = ExtractJsonArray(data, "ids");
                if (idsArr == null) return;

                // Parse numeric IDs
                var ids = new List<long>();
                foreach (var token in idsArr.Trim('[', ']').Split(','))
                {
                    long id;
                    if (long.TryParse(token.Trim(), out id))
                        ids.Add(id);
                }
                if (ids.Count == 0) return;

                // Run on background thread to avoid blocking UI
                var worker = new System.ComponentModel.BackgroundWorker();
                worker.DoWork += (s, e) =>
                {
                    var svc = new DocGraphService(personalPath);
                    svc.EnsureDatabase();
                    svc.DeleteLibraries(ids);
                };
                worker.RunWorkerCompleted += (s, e) =>
                {
                    if (_webView?.CoreWebView2 == null) return;
                    if (e.Error != null)
                        System.Diagnostics.Debug.WriteLine("[SettingsDialog] Remove libraries error: " + e.Error.Message);
                    // Refresh the info overlay on UI thread
                    HandleDocGraphInfo();
                };
                worker.RunWorkerAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] Remove libraries error: " + ex.Message);
            }
        }

        private void HandleUpdateLibraryTags(string json)
        {
            try
            {
                string personalPath = DocGraphService.GetPersonalDbPath();
                if (!File.Exists(personalPath)) return;

                string data = ExtractJsonObject(json, "data");
                if (data == null) return;
                string idStr = ExtractJsonValue(data, "id");
                string tags = ExtractJsonValue(data, "tags") ?? "";

                long id;
                if (!long.TryParse(idStr, out id)) return;

                var svc = new DocGraphService(personalPath);
                svc.EnsureDatabase();
                svc.UpdateLibraryTags(id, tags);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] Update tags error: " + ex.Message);
            }
        }

        private void IngestPersonalPaths(System.Collections.Generic.List<string> paths)
        {
            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                var svc = new DocGraphService(DocGraphService.GetPersonalDbPath());
                svc.EnsureDatabase();
                int totalChunks = 0;

                foreach (string path in paths)
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            svc.IngestFolder(path, "Personal");
                        }
                        else if (File.Exists(path))
                        {
                            string ext = Path.GetExtension(path).TrimStart('.').ToLower();
                            var source = new DocSource
                            {
                                Vendor = "Personal",
                                Library = Path.GetFileNameWithoutExtension(path),
                                FilePath = path,
                                Format = ext
                            };
                            totalChunks += svc.IngestSource(source);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[SettingsDialog] Ingest error for " + path + ": " + ex.Message);
                    }
                }

                svc.RebuildFtsIndex();
                e.Result = totalChunks;
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                // Dialog may already be closed — that's OK, ingestion still completed
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] Personal docgraph ingestion complete");
            };
            worker.RunWorkerAsync();
        }

        private void HandleBuildLib()
        {
            var info = ClarionVersionService.Detect();
            var config = info?.GetCurrentConfig();
            string clarionRoot = config?.RootPath;

            if (string.IsNullOrEmpty(clarionRoot))
            {
                string msg = "{\"type\":\"buildResult\",\"success\":false,\"message\":\"Clarion not detected\"}";
                _webView.CoreWebView2.PostWebMessageAsString(msg);
                return;
            }

            // Update UI to show building
            _webView.CoreWebView2.PostWebMessageAsString(
                "{\"type\":\"buildResult\",\"success\":true,\"message\":\"Building...\"}");

            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, e) => { e.Result = LibraryIndexer.Build(clarionRoot); };
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (_webView?.CoreWebView2 == null) return;
                string resultMsg;
                if (e.Error != null)
                    resultMsg = "{\"type\":\"buildResult\",\"success\":false,\"message\":\"" + EscapeJson(e.Error.Message) + "\"}";
                else
                {
                    var result = (LibraryIndexResult)e.Result;
                    if (result.Success)
                        resultMsg = "{\"type\":\"buildResult\",\"success\":true,\"message\":\"" + result.SymbolCount + " symbols indexed\"}";
                    else
                        resultMsg = "{\"type\":\"buildResult\",\"success\":false,\"message\":\"" + EscapeJson(result.Error) + "\"}";
                }
                _webView.CoreWebView2.PostWebMessageAsString(resultMsg);
            };
            worker.RunWorkerAsync();
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "settings.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "settings.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "settings.html");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SavePosition();
                if (_webView != null)
                {
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }

        private void RestorePosition()
        {
            string saved = _settings.Get("Settings.WindowPosition");
            if (!string.IsNullOrEmpty(saved))
            {
                var parts = saved.Split(',');
                int x, y;
                if (parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y))
                {
                    // Ensure the title bar (top 40px) is within a screen's working area
                    var titleBar = new Rectangle(x, y, Width, 40);
                    foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                    {
                        var wa = screen.WorkingArea;
                        if (titleBar.Top >= wa.Top && titleBar.Top <= wa.Bottom - 40
                            && titleBar.Right > wa.Left && titleBar.Left < wa.Right)
                        {
                            StartPosition = FormStartPosition.Manual;
                            Location = new Point(x, y);
                            return;
                        }
                    }
                }
                // Bad saved position — clear it
                _settings.Set("Settings.WindowPosition", "");
            }

            // No saved position or off-screen — center on screen
            StartPosition = FormStartPosition.CenterScreen;
        }

        private void SavePosition()
        {
            if (WindowState == FormWindowState.Normal)
                _settings.Set("Settings.WindowPosition", Location.X + "," + Location.Y);
        }

        #region JSON Helpers

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string ExtractJsonValue(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return null;

            // Boolean
            if (json[idx] == 't') return "true";
            if (json[idx] == 'f') return "false";
            if (json[idx] == 'n') return null;

            // String
            if (json[idx] == '"')
            {
                idx++;
                var sb = new StringBuilder();
                while (idx < json.Length)
                {
                    char c = json[idx];
                    if (c == '\\' && idx + 1 < json.Length)
                    {
                        char next = json[idx + 1];
                        if (next == '"') { sb.Append('"'); idx += 2; continue; }
                        if (next == '\\') { sb.Append('\\'); idx += 2; continue; }
                        if (next == 'n') { sb.Append('\n'); idx += 2; continue; }
                        if (next == 'r') { sb.Append('\r'); idx += 2; continue; }
                        sb.Append(c); idx++; continue;
                    }
                    if (c == '"') break;
                    sb.Append(c);
                    idx++;
                }
                return sb.ToString();
            }

            // Number
            int start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '.')) idx++;
            return json.Substring(start, idx - start);
        }

        private static string ExtractJsonObject(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length || json[idx] != '{') return null;
            int depth = 0;
            int start = idx;
            for (; idx < json.Length; idx++)
            {
                if (json[idx] == '{') depth++;
                else if (json[idx] == '}') { depth--; if (depth == 0) return json.Substring(start, idx - start + 1); }
            }
            return null;
        }

        private static string ExtractJsonArray(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length || json[idx] != '[') return null;
            int depth = 0;
            int start = idx;
            for (; idx < json.Length; idx++)
            {
                if (json[idx] == '[') depth++;
                else if (json[idx] == ']') { depth--; if (depth == 0) return json.Substring(start, idx - start + 1); }
            }
            return null;
        }

        #endregion

        // Static helpers used by ClaudeChatControl
        public static bool IsMultiTerminalAvailable()
        {
            return File.Exists(MultiTerminalMcpPath);
        }

        public static string GetMultiTerminalMcpPath()
        {
            return MultiTerminalMcpPath;
        }
    }
}
