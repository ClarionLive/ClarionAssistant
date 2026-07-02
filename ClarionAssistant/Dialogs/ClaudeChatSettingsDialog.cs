using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
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
        private readonly McpServer _mcpServer;
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
            : this(settings, null, isDarkTheme) { }

        /// <summary>
        /// Construct the settings dialog. <paramref name="mcpServer"/> is optional;
        /// when supplied, the External MCP Clients panel can display the live
        /// endpoint URL (port is dynamically chosen at server start).
        /// </summary>
        public ClaudeChatSettingsDialog(SettingsService settings, McpServer mcpServer, bool isDarkTheme = true)
        {
            _settings = settings;
            _mcpServer = mcpServer;
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
            _webView.ZoomFactorChanged += (s2, ev) => Terminal.WebViewZoomHelper.SetZoom("settings", _webView.ZoomFactor);
            _webView.ZoomFactor = Terminal.WebViewZoomHelper.GetZoom("settings");

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
                        // Per-backend field on the Models tab. Browse result is applied to
                        // whichever backend is currently in view on the HTML side; the hint
                        // here is just the saved default backend's value for convenience.
                        BrowseFolder("workingDir", "Select Working Directory",
                            _settings.Get("Claude.WorkingDirectory"));
                        break;
                    case "browseProjectsDir":
                        BrowseFolder("projectsDir", "Select Projects Directory",
                            _settings.Get("Claude.ProjectsFolder") ?? _settings.Get("COM.ProjectsFolder"));
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
                    case "importDocPaths":
                        HandleImportDocPaths(json);
                        break;
                    case "buildLib":
                        HandleBuildLib();
                        break;
                    case "addGitHubAccount":
                        HandleAddGitHubAccount(json);
                        break;
                    case "editGitHubAccount":
                        HandleEditGitHubAccount(json);
                        break;
                    case "deleteGitHubAccount":
                        HandleDeleteGitHubAccount(json);
                        break;
                    case "testGitHubToken":
                        HandleTestGitHubToken(json);
                        break;
                    case "addSnippet":
                        HandleAddSnippet(json);
                        break;
                    case "editSnippet":
                        HandleEditSnippet(json);
                        break;
                    case "deleteSnippet":
                        HandleDeleteSnippet(json);
                        break;
                    case "browseClassFolder":
                        BrowseFolder("classFolder", "Select Class Output Folder", _settings.Get("Class.OutputFolder"));
                        break;
                    case "editClassModel":
                        HandleEditClassModel(json);
                        break;
                    case "deleteClassModel":
                        HandleDeleteClassModel(json);
                        break;
                    case "openModelsFolder":
                        HandleOpenModelsFolder();
                        break;
                    case "addClassModel":
                        HandleAddClassModel();
                        break;
                    case "openUrl":
                        string url = ExtractJsonValue(json, "data");
                        if (!string.IsNullOrEmpty(url))
                            System.Diagnostics.Process.Start(url);
                        break;
                    case "generateMcpExternalToken":
                        HandleGenerateMcpExternalToken();
                        break;
                    case "addToClaudeDesktop":
                        HandleAddToClaudeDesktop();
                        break;
                    case "openMcpExtraConfig":
                        HandleOpenMcpExtraConfig();
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
            // Per-backend paths. Legacy keys (Claude.WorkingDirectory, COM.ProjectsFolder)
            // act as fallback so existing users don't lose their values on upgrade.
            string legacyWorkingDir = _settings.Get("Claude.WorkingDirectory")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string legacyProjectsDir = _settings.Get("COM.ProjectsFolder") ?? "";
            string claudeWorkingDir   = _settings.Get("Claude.WorkingDirectory")  ?? legacyWorkingDir;
            string copilotWorkingDir  = _settings.Get("Copilot.WorkingDirectory") ?? legacyWorkingDir;
            string codexWorkingDir    = _settings.Get("Codex.WorkingDirectory")   ?? legacyWorkingDir;
            string claudeProjectsDir  = _settings.Get("Claude.ProjectsFolder")    ?? legacyProjectsDir;
            string copilotProjectsDir = _settings.Get("Copilot.ProjectsFolder")   ?? legacyProjectsDir;
            string codexProjectsDir   = _settings.Get("Codex.ProjectsFolder")     ?? legacyProjectsDir;
            string theme = _isDark ? "dark" : "light";

            bool autoUpdate = (_settings.Get("Claude.AutoUpdate") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

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

            // Copilot commands
            var copilotCommands = _settings.GetCopilotCommands();
            var copSb = new StringBuilder("[");
            for (int ci = 0; ci < copilotCommands.Count; ci++)
            {
                if (ci > 0) copSb.Append(",");
                copSb.AppendFormat("{{\"command\":\"{0}\",\"isDefault\":{1}}}",
                    EscapeJson(copilotCommands[ci].Key), copilotCommands[ci].Value ? "true" : "false");
            }
            copSb.Append("]");

            // Codex commands (mirrors the Copilot list shape).
            var codexCommands = _settings.GetCodexCommands();
            var codSb = new StringBuilder("[");
            for (int ci = 0; ci < codexCommands.Count; ci++)
            {
                if (ci > 0) codSb.Append(",");
                codSb.AppendFormat("{{\"command\":\"{0}\",\"isDefault\":{1}}}",
                    EscapeJson(codexCommands[ci].Key), codexCommands[ci].Value ? "true" : "false");
            }
            codSb.Append("]");

            string backend = _settings.Get("Assistant.Backend") ?? "Claude";
            string copilotModel = _settings.Get("Copilot.Model") ?? "";
            string copilotPermMode = _settings.Get("Copilot.PermissionMode") ?? "prompt";
            string copilotExtraFlags = _settings.Get("Copilot.ExtraFlags") ?? "";

            // Plan selections per backend (defaults applied JS-side from registry)
            string claudePlan  = _settings.Get("Claude.Plan")  ?? "";
            string copilotPlan = _settings.Get("Copilot.Plan") ?? "";
            string codexPlan   = _settings.Get("Codex.Plan")   ?? "";
            string codexModel  = _settings.Get("Codex.Model")  ?? "";
            string codexEffort = _settings.Get("Codex.Effort") ?? "";

            // External MCP client access (issue #24)
            bool mcpExternalEnabled = _settings.GetMcpExternalAccessEnabled();
            string mcpExternalToken = _settings.GetMcpExternalToken();
            int mcpPort = (_mcpServer != null && _mcpServer.IsRunning) ? _mcpServer.Port : 0;

            string modelRegistryJson = _settings.GetModelRegistryJson();

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            string displayVersion = ver.Major + "." + ver.Minor;

            string json = "{\"type\":\"setSettings\",\"settings\":{"
                + "\"version\":\"" + displayVersion + "\""
                + ",\"theme\":\"" + theme + "\""
                + ",\"fontFamily\":\"" + EscapeJson(fontFamily) + "\""
                + ",\"fontSize\":" + fontSize
                + ",\"model\":\"" + EscapeJson(model) + "\""
                + ",\"claudeWorkingDir\":\""   + EscapeJson(claudeWorkingDir)   + "\""
                + ",\"copilotWorkingDir\":\""  + EscapeJson(copilotWorkingDir)  + "\""
                + ",\"codexWorkingDir\":\""    + EscapeJson(codexWorkingDir)    + "\""
                + ",\"claudeProjectsDir\":\""  + EscapeJson(claudeProjectsDir)  + "\""
                + ",\"copilotProjectsDir\":\"" + EscapeJson(copilotProjectsDir) + "\""
                + ",\"codexProjectsDir\":\""   + EscapeJson(codexProjectsDir)   + "\""
                + ",\"assistantBackend\":\"" + EscapeJson(backend) + "\""
                + ",\"autoUpdate\":" + (autoUpdate ? "true" : "false")
                + ",\"mtAvailable\":" + (mtAvailable ? "true" : "false")
                + ",\"mtEnabled\":" + (mtOn ? "true" : "false")
                + ",\"agentName\":\"" + EscapeJson(agentName) + "\""
                + ",\"libStatus\":\"" + EscapeJson(libStatus) + "\""
                + ",\"docPaths\":" + docPathsSb
                + ",\"commands\":" + cmdsSb
                + ",\"claudeCommands\":" + cmdsSb
                + ",\"copilotCommands\":" + copSb
                + ",\"codexCommands\":"   + codSb
                + ",\"copilotModel\":\"" + EscapeJson(copilotModel) + "\""
                + ",\"copilotPermissionMode\":\"" + EscapeJson(copilotPermMode) + "\""
                + ",\"copilotExtraFlags\":\"" + EscapeJson(copilotExtraFlags) + "\""
                + ",\"codexEffort\":\"" + EscapeJson(codexEffort) + "\""
                + ",\"mcpExternalEnabled\":" + (mcpExternalEnabled ? "true" : "false")
                + ",\"mcpExternalToken\":\"" + EscapeJson(mcpExternalToken) + "\""
                + ",\"mcpPort\":" + mcpPort
                + ",\"claudePlan\":\""  + EscapeJson(claudePlan)  + "\""
                + ",\"copilotPlan\":\"" + EscapeJson(copilotPlan) + "\""
                + ",\"codexPlan\":\""   + EscapeJson(codexPlan)   + "\""
                + ",\"codexModel\":\""  + EscapeJson(codexModel)  + "\""
                + ",\"modelRegistry\":" + modelRegistryJson
                + ",\"classOutputFolder\":\"" + EscapeJson(_settings.Get("Class.OutputFolder") ?? "") + "\""
                + ",\"classModels\":" + BuildClassModelsJson()
                + "}}";
            _webView.CoreWebView2.PostWebMessageAsString(json);

            // Send GitHub accounts (separate message — keeps setSettings clean)
            SendGitHubAccounts();
            SendSnippets();
        }

        private void SendSnippets()
        {
            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(
                    "{\"type\":\"setSnippets\",\"snippets\":" + SnippetStore.ToJson(SnippetStore.Load()) + "}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] SendSnippets error: " + ex.Message);
            }
        }

        /// <summary>
        /// Snippet payloads carry a multi-line Clarion code body (arbitrary quotes/backslashes/tabs), so
        /// unlike the GitHub-account handlers above (which use the hand-rolled ExtractJsonValue on a
        /// double-stringified "data" field), the snippet JS side sends "data" as a native nested object
        /// and this parses the WHOLE envelope with JavaScriptSerializer — one clean JSON round-trip
        /// instead of two layers of string-escaping that ExtractJsonValue isn't built to survive
        /// (it doesn't decode \t or \uXXXX).
        /// </summary>
        private static Dictionary<string, object> ExtractSnippetData(string json)
        {
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as Dictionary<string, object>;
                object data;
                return (d != null && d.TryGetValue("data", out data)) ? data as Dictionary<string, object> : null;
            }
            catch { return null; }
        }

        private static string SnippetStr(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v) && v != null) ? v.ToString() : null;
        }

        private static List<string> SnippetStrList(Dictionary<string, object> d, string key)
        {
            var list = new List<string>();
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is object[])
                foreach (var item in (object[])v) if (item != null) list.Add(item.ToString());
            return list;
        }

        private void HandleAddSnippet(string json)
        {
            try
            {
                var data = ExtractSnippetData(json);
                if (data == null) return;
                var updated = SnippetStore.Add(
                    SnippetStr(data, "trigger"), SnippetStr(data, "description"),
                    SnippetStrList(data, "extensions"), SnippetStr(data, "body"));
                SendSnippets();
                Terminal.ModernEmbeditorViewContent.ApplySnippetsToAll(updated);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] AddSnippet error: " + ex.Message);
            }
        }

        private void HandleEditSnippet(string json)
        {
            try
            {
                var data = ExtractSnippetData(json);
                if (data == null) return;
                var updated = SnippetStore.Update(
                    SnippetStr(data, "id"), SnippetStr(data, "trigger"), SnippetStr(data, "description"),
                    SnippetStrList(data, "extensions"), SnippetStr(data, "body"));
                SendSnippets();
                Terminal.ModernEmbeditorViewContent.ApplySnippetsToAll(updated);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] EditSnippet error: " + ex.Message);
            }
        }

        private void HandleDeleteSnippet(string json)
        {
            try
            {
                string id = ExtractJsonValue(json, "data");
                if (string.IsNullOrEmpty(id)) return;
                var updated = SnippetStore.Delete(id);
                SendSnippets();
                Terminal.ModernEmbeditorViewContent.ApplySnippetsToAll(updated);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] DeleteSnippet error: " + ex.Message);
            }
        }

        private void SendGitHubAccounts()
        {
            try
            {
                var accounts = Services.SchemaGraphService.GetAllGitHubAccounts();
                var sb = new StringBuilder("[");
                for (int i = 0; i < accounts.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = accounts[i];
                    string hasToken = (string)a["token"] ?? "";
                    string provider = a.ContainsKey("provider") ? (string)a["provider"] : "github";
                    sb.AppendFormat("{{\"id\":\"{0}\",\"displayName\":\"{1}\",\"username\":\"{2}\",\"token\":\"{3}\",\"provider\":\"{4}\"}}",
                        EscapeJson((string)a["id"]), EscapeJson((string)a["displayName"]),
                        EscapeJson((string)a["username"]), EscapeJson(hasToken), EscapeJson(provider));
                }
                sb.Append("]");
                _webView.CoreWebView2.PostWebMessageAsString(
                    "{\"type\":\"setGitHubAccounts\",\"accounts\":" + sb + "}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] SendGitHubAccounts error: " + ex.Message);
            }
        }

        private void HandleAddGitHubAccount(string json)
        {
            try
            {
                string data = ExtractJsonValue(json, "data");
                if (data == null) return;
                // data is a JSON string that was stringified — parse the inner object
                string displayName = ExtractJsonValue(data, "displayName");
                string username = ExtractJsonValue(data, "username");
                string token = ExtractJsonValue(data, "token");
                string provider = ExtractJsonValue(data, "provider") ?? "github";
                Services.SchemaGraphService.AddGitHubAccount(displayName, username, token, provider);
                SendGitHubAccounts();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] AddGitHubAccount error: " + ex.Message);
            }
        }

        private void HandleEditGitHubAccount(string json)
        {
            try
            {
                string data = ExtractJsonValue(json, "data");
                if (data == null) return;
                string id = ExtractJsonValue(data, "id");
                string displayName = ExtractJsonValue(data, "displayName");
                string username = ExtractJsonValue(data, "username");
                string token = ExtractJsonValue(data, "token");

                // If token is the placeholder, preserve existing
                if (token == "\u2022\u2022\u2022\u2022" || string.IsNullOrEmpty(token))
                {
                    var existing = Services.SchemaGraphService.GetGitHubAccount(id);
                    if (existing != null) token = (string)existing["token"];
                }
                string provider = ExtractJsonValue(data, "provider");
                Services.SchemaGraphService.UpdateGitHubAccount(id, displayName, username, token, provider);
                SendGitHubAccounts();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] EditGitHubAccount error: " + ex.Message);
            }
        }

        private void HandleDeleteGitHubAccount(string json)
        {
            try
            {
                string id = ExtractJsonValue(json, "data");
                if (!string.IsNullOrEmpty(id))
                {
                    Services.SchemaGraphService.DeleteGitHubAccount(id);
                    SendGitHubAccounts();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] DeleteGitHubAccount error: " + ex.Message);
            }
        }

        private void HandleTestGitHubToken(string json)
        {
            string token;
            string provider;
            string username;
            try
            {
                string data = ExtractJsonValue(json, "data");
                token = ExtractJsonValue(data ?? "", "token");
                string id = ExtractJsonValue(data ?? "", "id");
                provider = ExtractJsonValue(data ?? "", "provider") ?? "github";
                username = ExtractJsonValue(data ?? "", "username") ?? "";

                // If token is placeholder or empty, get from stored account
                if ((string.IsNullOrEmpty(token) || token == "\u2022\u2022\u2022\u2022") && !string.IsNullOrEmpty(id))
                {
                    var existing = Services.SchemaGraphService.GetGitHubAccount(id);
                    if (existing != null)
                    {
                        token = (string)existing["token"];
                        if (string.IsNullOrEmpty(provider) || provider == "github")
                            provider = existing.ContainsKey("provider") ? (string)existing["provider"] : "github";
                        if (string.IsNullOrEmpty(username))
                            username = (string)existing["username"];
                    }
                }
            }
            catch (Exception ex)
            {
                PostTestResult(false, "Error: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(token))
            {
                PostTestResult(false, "No token provided");
                return;
            }

            string finalToken = token;
            string finalProvider = provider;
            string finalUsername = username;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool success = false;
                string message;
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                    System.Net.HttpWebRequest request;

                    if (finalProvider == "bitbucket")
                    {
                        // Bitbucket uses Basic Auth: username + app password
                        request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://api.bitbucket.org/2.0/user");
                        string credentials = Convert.ToBase64String(
                            Encoding.ASCII.GetBytes(finalUsername + ":" + finalToken));
                        request.Headers["Authorization"] = "Basic " + credentials;
                    }
                    else
                    {
                        // GitHub uses Bearer token
                        request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://api.github.com/user");
                        request.Headers["Authorization"] = "Bearer " + finalToken;
                    }

                    request.UserAgent = "ClarionAssistant";
                    request.Timeout = 10000;

                    using (var response = (System.Net.HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        string body = reader.ReadToEnd();
                        string login = ExtractJsonValue(body, finalProvider == "bitbucket" ? "display_name" : "login") ?? "unknown";
                        message = "Authenticated as " + login;
                        success = true;
                    }
                }
                catch (System.Net.WebException wex)
                {
                    if (wex.Response is System.Net.HttpWebResponse hr && (int)hr.StatusCode == 401)
                        message = "Authentication failed — invalid credentials";
                    else
                        message = wex.Message;
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                }

                PostTestResult(success, message);
            });
        }

        private void PostTestResult(bool success, string message)
        {
            try
            {
                if (IsDisposed || _webView == null || _webView.CoreWebView2 == null) return;
                bool s = success;
                string m = message;
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_webView != null && _webView.CoreWebView2 != null)
                            _webView.CoreWebView2.PostWebMessageAsString(
                                "{\"type\":\"testGitHubResult\",\"success\":" + (s ? "true" : "false") +
                                ",\"message\":\"" + EscapeJson(m) + "\"}");
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void HandleSave(string json)
        {
            try
            {
                HandleSaveInner(json);
            }
            catch (SettingsLockedException ex)
            {
                // Cross-process contention: another ClarionAssistant process is
                // mid-save and didn't release the named mutex within our budget.
                // Surface this so the user can retry rather than have the dialog
                // freeze with a half-persisted batch. (Codex Run 5 + Debugger
                // Run 5 — HIGH: HandleSave previously swallowed this in the
                // outer catch-all, leaving partial save + no UI feedback.)
                try
                {
                    MessageBox.Show(this, ex.Message, "Settings Locked",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
                // Do NOT close the dialog — let the user retry.
            }
        }

        private void HandleSaveInner(string json)
        {
            // Extract nested data object values
            string data = ExtractJsonObject(json, "data");
            if (data == null) data = json;

            string theme = ExtractJsonValue(data, "theme") ?? "dark";
            string fontFamily = ExtractJsonValue(data, "fontFamily") ?? "Cascadia Mono";
            string fontSize = ExtractJsonValue(data, "fontSize") ?? "14";
            string model = ExtractJsonValue(data, "model") ?? "sonnet";
            string claudeWorkingDir   = ExtractJsonValue(data, "claudeWorkingDir")   ?? "";
            string copilotWorkingDir  = ExtractJsonValue(data, "copilotWorkingDir")  ?? "";
            string codexWorkingDir    = ExtractJsonValue(data, "codexWorkingDir")    ?? "";
            string claudeProjectsDir  = ExtractJsonValue(data, "claudeProjectsDir")  ?? "";
            string copilotProjectsDir = ExtractJsonValue(data, "copilotProjectsDir") ?? "";
            string codexProjectsDir   = ExtractJsonValue(data, "codexProjectsDir")   ?? "";
            bool autoUpdate = ExtractJsonValue(data, "autoUpdate") == "true";
            bool mtEnabled = ExtractJsonValue(data, "mtEnabled") == "true";
            string agentName = ExtractJsonValue(data, "agentName") ?? "ClarionIDE";

            string backend = ExtractJsonValue(data, "assistantBackend") ?? (_settings.Get("Assistant.Backend") ?? "Claude");
            string copilotModel = ExtractJsonValue(data, "copilotModel") ?? (_settings.Get("Copilot.Model") ?? "");
            string copilotPermMode = ExtractJsonValue(data, "copilotPermissionMode") ?? (_settings.Get("Copilot.PermissionMode") ?? "prompt");
            string copilotExtraFlags = ExtractJsonValue(data, "copilotExtraFlags") ?? (_settings.Get("Copilot.ExtraFlags") ?? "");

            // Save
            _settings.Set("Theme", theme);
            _settings.Set("Claude.FontFamily", fontFamily);
            _settings.Set("Claude.FontSize", fontSize);
            _settings.Set("Claude.Model", model);
            _settings.Set("Claude.WorkingDirectory",  claudeWorkingDir);
            _settings.Set("Copilot.WorkingDirectory", copilotWorkingDir);
            _settings.Set("Codex.WorkingDirectory",   codexWorkingDir);
            _settings.Set("Claude.ProjectsFolder",    claudeProjectsDir);
            _settings.Set("Copilot.ProjectsFolder",   copilotProjectsDir);
            _settings.Set("Codex.ProjectsFolder",     codexProjectsDir);
            // Keep legacy COM.ProjectsFolder in sync with the saved default backend's
            // value so home.html and the COM-project-creation flow (which still read
            // COM.ProjectsFolder) continue to work without per-backend awareness.
            string defaultBackendForLegacy = ExtractJsonValue(data, "assistantBackend") ?? "Claude";
            string legacyTarget = claudeProjectsDir;
            if (string.Equals(defaultBackendForLegacy, "Copilot", StringComparison.OrdinalIgnoreCase))
                legacyTarget = copilotProjectsDir;
            else if (string.Equals(defaultBackendForLegacy, "Codex", StringComparison.OrdinalIgnoreCase))
                legacyTarget = codexProjectsDir;
            _settings.Set("COM.ProjectsFolder", legacyTarget);
            _settings.Set("Claude.AutoUpdate", autoUpdate.ToString().ToLower());
            _settings.Set("MultiTerminal.Enabled", mtEnabled.ToString().ToLower());
            _settings.Set("MultiTerminal.AgentName", agentName);

            _settings.Set("Assistant.Backend", backend);
            _settings.Set("Copilot.Model", copilotModel);
            _settings.Set("Copilot.PermissionMode", copilotPermMode);
            _settings.Set("Copilot.ExtraFlags", copilotExtraFlags);

            // Plans + Codex model (subscription tiers; filters available models in the dialog)
            string claudePlan  = ExtractJsonValue(data, "claudePlan")  ?? (_settings.Get("Claude.Plan")  ?? "");
            string copilotPlan = ExtractJsonValue(data, "copilotPlan") ?? (_settings.Get("Copilot.Plan") ?? "");
            string codexPlan   = ExtractJsonValue(data, "codexPlan")   ?? (_settings.Get("Codex.Plan")   ?? "");
            string codexModel  = ExtractJsonValue(data, "codexModel")  ?? (_settings.Get("Codex.Model")  ?? "");
            string codexEffort = ExtractJsonValue(data, "codexEffort") ?? (_settings.Get("Codex.Effort") ?? "");
            _settings.Set("Claude.Plan",  claudePlan);
            _settings.Set("Copilot.Plan", copilotPlan);
            _settings.Set("Codex.Plan",   codexPlan);
            _settings.Set("Codex.Model",  codexModel);
            _settings.Set("Codex.Effort", codexEffort);

            // External MCP client access (issue #24).
            // Only the toggle is persisted here. The token is persisted by
            // HandleGenerateMcpExternalToken at click time so the snippet the
            // user copies is immediately the live token (lets them paste +
            // test before clicking Save). Re-writing it here would be a
            // round-trip no-op at best and could clobber a token minted in
            // another window if two dialogs were ever open at once.
            string mcpExternalEnabledStr = ExtractJsonValue(data, "mcpExternalEnabled") ?? "false";
            bool mcpExternalEnabled = string.Equals(mcpExternalEnabledStr, "true", StringComparison.OrdinalIgnoreCase);
            _settings.SetMcpExternalAccessEnabled(mcpExternalEnabled);

            // Class output folder
            string classOutputFolder = ExtractJsonValue(data, "classOutputFolder") ?? "";
            if (!string.IsNullOrEmpty(classOutputFolder))
                _settings.Set("Class.OutputFolder", classOutputFolder);

            // Claude commands
            // Commands (both backends)
            var claudeCmds = ParseCommandsArray(ExtractJsonArray(data, "claudeCommands") ?? ExtractJsonArray(data, "commands"));
            if (claudeCmds != null && claudeCmds.Count > 0)
                _settings.SetClaudeCommands(claudeCmds);

            var copilotCmds = ParseCommandsArray(ExtractJsonArray(data, "copilotCommands"));
            if (copilotCmds != null && copilotCmds.Count > 0)
                _settings.SetCopilotCommands(copilotCmds);

            var codexCmds = ParseCommandsArray(ExtractJsonArray(data, "codexCommands"));
            if (codexCmds != null && codexCmds.Count > 0)
                _settings.SetCodexCommands(codexCmds);

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

        private static List<KeyValuePair<string, bool>> ParseCommandsArray(string commandsJson)
        {
            if (string.IsNullOrEmpty(commandsJson)) return null;
            var cmds = new List<KeyValuePair<string, bool>>();
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
            return cmds;
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
                dlg.Filter = "Documentation files (*.htm;*.html;*.chm;*.pdf;*.md)|*.htm;*.html;*.chm;*.pdf;*.md|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string msg = "{\"type\":\"browseResult\",\"target\":\"docPath\",\"path\":\"" + EscapeJson(dlg.FileName) + "\"}";
                    _webView.CoreWebView2.PostWebMessageAsString(msg);
                }
            }
        }

        /// <summary>
        /// One-click integration: write CA's MCP server entry into Claude Desktop's
        /// config (<c>%APPDATA%\Claude\claude_desktop_config.json</c>) and back up
        /// the original. Idempotent — repeated clicks update the entry in place
        /// (e.g. after rotating the token). Avoids the manual copy-paste-into-JSON
        /// step the snippet textarea otherwise requires.
        /// </summary>
        private void HandleAddToClaudeDesktop()
        {
            try
            {
                if (!_settings.GetMcpExternalAccessEnabled())
                {
                    PostClaudeDesktopStatus(false, "Enable external MCP access first.");
                    return;
                }
                string token = _settings.GetMcpExternalToken();
                if (string.IsNullOrEmpty(token))
                {
                    PostClaudeDesktopStatus(false, "Generate a token first.");
                    return;
                }
                if (_mcpServer == null || !_mcpServer.IsRunning)
                {
                    PostClaudeDesktopStatus(false, "MCP server is not running.");
                    return;
                }

                // Resolve mcp-remote (npm global install per CodexProcessManager pattern).
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string mcpRemoteJs = Path.Combine(appData, "npm", "node_modules", "mcp-remote", "dist", "proxy.js");
                if (!File.Exists(mcpRemoteJs))
                {
                    PostClaudeDesktopStatus(false,
                        "mcp-remote not installed. Run: npm install -g mcp-remote@" +
                        Services.CodexProcessManager.McpRemoteVersion);
                    return;
                }

                // Resolve node.exe — Claude Desktop wraps `npx` in cmd /C which breaks
                // on the space in `C:\Program Files\nodejs\npx.cmd`. Calling node.exe
                // directly with the proxy.js path matches the project's other MCP
                // server entries and avoids the cmd-quoting issue.
                string nodeExe = ResolveNodeExeForClaudeDesktop();
                if (nodeExe == null)
                {
                    PostClaudeDesktopStatus(false, "node.exe not found. Install Node.js first.");
                    return;
                }

                string configDir = Path.Combine(appData, "Claude");
                string configPath = Path.Combine(configDir, "claude_desktop_config.json");
                if (!Directory.Exists(configDir))
                {
                    PostClaudeDesktopStatus(false,
                        "Claude Desktop directory not found at " + configDir + ". Install Claude Desktop first.");
                    return;
                }

                string original = File.Exists(configPath) ? File.ReadAllText(configPath) : "{}";

                // Backup once on first mutation. Subsequent clicks reuse the same
                // backup (rotating tokens shouldn't keep replacing the .bak with
                // already-modified state).
                if (File.Exists(configPath))
                {
                    string bak = configPath + ".clarionassistant.bak";
                    if (!File.Exists(bak))
                    {
                        try { File.Copy(configPath, bak, overwrite: false); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("[SettingsDialog] Claude Desktop config backup failed: " + ex.Message);
                        }
                    }
                }

                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                Dictionary<string, object> root;
                try { root = serializer.Deserialize<Dictionary<string, object>>(original); }
                catch (Exception ex)
                {
                    PostClaudeDesktopStatus(false, "Could not parse claude_desktop_config.json: " + ex.Message);
                    return;
                }
                if (root == null) root = new Dictionary<string, object>();

                Dictionary<string, object> servers;
                if (root.ContainsKey("mcpServers") && root["mcpServers"] is Dictionary<string, object>)
                    servers = (Dictionary<string, object>)root["mcpServers"];
                else
                {
                    servers = new Dictionary<string, object>();
                    root["mcpServers"] = servers;
                }

                servers["clarion-assistant"] = new Dictionary<string, object>
                {
                    { "command", nodeExe },
                    { "args", new object[]
                        {
                            mcpRemoteJs,
                            _mcpServer.McpUrl,
                            "--header",
                            "Authorization:Bearer " + token
                        }
                    }
                };

                string serialized = serializer.Serialize(root);
                string pretty = PrettyPrintJson(serialized);

                // Atomic write via temp + Move (no ReplaceFile here — first-ever
                // write is also possible, and the file isn't security-critical
                // enough to need cross-process coordination).
                string tmpPath = configPath + ".ca-new";
                File.WriteAllText(tmpPath, pretty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                try
                {
                    if (File.Exists(configPath)) File.Delete(configPath);
                    File.Move(tmpPath, configPath);
                }
                catch
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    throw;
                }

                PostClaudeDesktopStatus(true,
                    "Added clarion-assistant to Claude Desktop config. Restart Claude Desktop to load it. (Backup: " +
                    Path.GetFileName(configPath) + ".clarionassistant.bak)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] HandleAddToClaudeDesktop failed: " + ex);
                PostClaudeDesktopStatus(false, "Failed: " + ex.Message);
            }
        }

        private void PostClaudeDesktopStatus(bool ok, string message)
        {
            try
            {
                string msg = "{\"type\":\"mcpClaudeDesktopStatus\",\"ok\":" + (ok ? "true" : "false") +
                             ",\"message\":\"" + EscapeJson(message ?? "") + "\"}";
                _webView.CoreWebView2.PostWebMessageAsString(msg);
            }
            catch { }
        }

        private static string ResolveNodeExeForClaudeDesktop()
        {
            try
            {
                string defaultPath = @"C:\Program Files\nodejs\node.exe";
                if (File.Exists(defaultPath)) return defaultPath;
            }
            catch { }
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (string dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(dir.Trim(), "node.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        // Minimal JSON pretty-printer. Re-formats the single-line output of
        // JavaScriptSerializer with 2-space indentation. Not strict — assumes
        // valid JSON in, which our serializer guarantees.
        private static string PrettyPrintJson(string json)
        {
            var sb = new StringBuilder(json.Length + 256);
            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (escape) { sb.Append(c); escape = false; continue; }
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { sb.Append(c); inString = true; continue; }
                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c);
                        // Empty object / array inline — emit closing on same line
                        // and advance the index so the main loop doesn't see the
                        // closing brace again (which would push depth negative).
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                        {
                            sb.Append(json[i + 1]);
                            i++;
                            break;
                        }
                        depth++;
                        sb.AppendLine();
                        sb.Append(new string(' ', depth * 2));
                        break;
                    case '}':
                    case ']':
                        if (depth > 0) depth--;
                        sb.AppendLine();
                        sb.Append(new string(' ', depth * 2));
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        sb.AppendLine();
                        sb.Append(new string(' ', depth * 2));
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Mint a fresh external MCP token, persist it, and notify the webview so
        /// the input field updates without a full settings reload. Issue #24.
        /// </summary>
        private void HandleGenerateMcpExternalToken()
        {
            string token = SettingsService.GenerateMcpExternalToken();
            _settings.SetMcpExternalToken(token);
            try
            {
                string msg = "{\"type\":\"setMcpExternalToken\",\"token\":\"" + EscapeJson(token) + "\"}";
                _webView.CoreWebView2.PostWebMessageAsString(msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] generateMcpExternalToken post failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Open the user-owned mcp-extra.json sidecar in the default editor for .json.
        /// Creates the file with a commented template if missing. Issue #26.
        /// </summary>
        private void HandleOpenMcpExtraConfig()
        {
            string path = Services.McpServer.GetMcpExtraConfigPath();
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                if (!System.IO.File.Exists(path))
                {
                    string template = "{\r\n"
                        + "  \"_comment\": \"Add MCP servers here that should load in Clarion Assistant's IDE-pane Claude session. Format matches Claude Desktop's mcpServers schema. Restart the IDE pane (or open a new tab) for changes to take effect. Addin-supplied servers (clarion-assistant, multiterminal, multiterminal-channel) cannot be overridden — those keys are reserved.\",\r\n"
                        + "  \"mcpServers\": {\r\n"
                        + "    \"_example_stdio\": {\r\n"
                        + "      \"type\": \"stdio\",\r\n"
                        + "      \"command\": \"C:\\\\Path\\\\To\\\\YourMcp.exe\",\r\n"
                        + "      \"args\": []\r\n"
                        + "    }\r\n"
                        + "  }\r\n"
                        + "}\r\n";
                    System.IO.File.WriteAllText(path, template);
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] openMcpExtraConfig failed: " + ex.Message);
                System.Windows.Forms.MessageBox.Show(
                    "Could not open " + path + "\r\n\r\n" + ex.Message,
                    "Open mcp-extra.json",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
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
                                ? "SELECT l.id, l.name, l.vendor, l.tags, COUNT(c.id) as cnt, l.source_path FROM libraries l LEFT JOIN doc_chunks c ON c.library_id = l.id GROUP BY l.id ORDER BY cnt DESC"
                                : "SELECT l.id, l.name, l.vendor, NULL as tags, COUNT(c.id) as cnt, l.source_path FROM libraries l LEFT JOIN doc_chunks c ON c.library_id = l.id GROUP BY l.id ORDER BY cnt DESC";
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    long id = reader.GetInt64(0);
                                    string name = reader.GetString(1);
                                    string vendor = reader.IsDBNull(2) ? "" : reader.GetString(2);
                                    string tags = reader.IsDBNull(3) ? "" : reader.GetString(3);
                                    int cnt = reader.GetInt32(4);
                                    string sourcePath = reader.IsDBNull(5) ? "" : reader.GetString(5);
                                    totalLibs++;
                                    totalChunks += cnt;
                                    if (!first) items.Append(",");
                                    items.AppendFormat("{{\"id\":{0},\"name\":\"{1}\",\"vendor\":\"{2}\",\"chunks\":{3},\"source\":\"{4}\",\"tags\":\"{5}\",\"path\":\"{6}\"}}",
                                        id, EscapeJson(name), EscapeJson(vendor), cnt, label, EscapeJson(tags), EscapeJson(sourcePath));
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

        private void HandleImportDocPaths(string json)
        {
            string data = ExtractJsonValue(json, "data");
            if (string.IsNullOrEmpty(data)) return;

            // Parse paths from JSON array
            var paths = new System.Collections.Generic.List<string>();
            int pos = 0;
            while (pos < data.Length)
            {
                int q1 = data.IndexOf('"', pos);
                if (q1 < 0) break;
                int q2 = data.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                paths.Add(data.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\"));
                pos = q2 + 1;
            }

            if (paths.Count == 0) return;

            // Save paths to settings
            _settings.Set("DocGraph.Paths", string.Join("|", paths));

            // Ingest on background thread with UI feedback
            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                var svc = new DocGraphService(DocGraphService.GetPersonalDbPath());
                svc.EnsureDatabase();
                int totalChunks = 0;

                foreach (string path in paths)
                {
                    System.Diagnostics.Debug.WriteLine("[SettingsDialog] Importing: " + path);
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            string folderVendor = Path.GetFileName(path.TrimEnd('\\', '/'));
                            string result = svc.IngestFolder(path, folderVendor);
                            System.Diagnostics.Debug.WriteLine("[SettingsDialog] IngestFolder result: " + result);
                            // Parse "Ingested N chunks" from result
                            if (result != null)
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(result, @"Ingested\s+(\d+)\s+chunks");
                                if (match.Success) totalChunks += int.Parse(match.Groups[1].Value);
                            }
                        }
                        else if (File.Exists(path))
                        {
                            var source = new DocSource
                            {
                                Vendor = "Personal",
                                Library = Path.GetFileNameWithoutExtension(path),
                                FilePath = path,
                                Format = Path.GetExtension(path).TrimStart('.').ToLower()
                            };
                            int chunks = svc.IngestSource(source);
                            System.Diagnostics.Debug.WriteLine("[SettingsDialog] IngestSource: " + path + " -> " + chunks + " chunks");
                            totalChunks += chunks;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[SettingsDialog] Path not found: " + path);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[SettingsDialog] Import error for " + path + ": " + ex.Message);
                    }
                }

                // Only rebuild FTS if single files were added (IngestFolder already rebuilds internally)
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] Import complete. Total chunks: " + totalChunks);
                e.Result = totalChunks;
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                try
                {
                    if (_webView != null && _webView.CoreWebView2 != null)
                    {
                        int chunks = e.Result is int ? (int)e.Result : 0;
                        string resultMsg = chunks > 0
                            ? "Imported " + chunks + " chunks"
                            : "No documentation files found (htm, html, chm, pdf, md)";
                        string msg = "{\"type\":\"importDocResult\",\"success\":" + (chunks > 0 ? "true" : "false")
                            + ",\"message\":\"" + EscapeJson(resultMsg) + "\"}";
                        _webView.CoreWebView2.PostWebMessageAsString(msg);

                        // Auto-refresh the Info panel
                        HandleDocGraphInfo();
                    }
                }
                catch { }
            };
            worker.RunWorkerAsync();
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

        #region Class Models

        private static string GetClassModelsFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant", "ClassModels");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                // Copy bundled models on first run
                try
                {
                    string assemblyDir = Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    string bundled = Path.Combine(assemblyDir, "Terminal", "ClassModels");
                    if (Directory.Exists(bundled))
                    {
                        foreach (var f in Directory.GetFiles(bundled))
                            File.Copy(f, Path.Combine(folder, Path.GetFileName(f)), false);
                    }
                }
                catch { }
            }
            return folder;
        }

        private string BuildClassModelsJson()
        {
            var sb = new StringBuilder("[");
            try
            {
                string folder = GetClassModelsFolder();
                var incFiles = Directory.GetFiles(folder, "*.inc");
                bool first = true;
                foreach (var incPath in incFiles)
                {
                    string baseName = Path.GetFileNameWithoutExtension(incPath);
                    string clwPath = Path.Combine(folder, baseName + ".clw");
                    if (!File.Exists(clwPath)) continue;

                    if (!first) sb.Append(",");
                    sb.Append("{\"name\":\"" + EscapeJson(baseName)
                        + "\",\"incFile\":\"" + EscapeJson(baseName + ".inc")
                        + "\",\"clwFile\":\"" + EscapeJson(baseName + ".clw") + "\"}");
                    first = false;
                }
            }
            catch { }
            sb.Append("]");
            return sb.ToString();
        }

        private void SendClassModelsUpdate()
        {
            if (_webView == null || _webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsString(
                "{\"type\":\"setClassModels\",\"models\":" + BuildClassModelsJson() + "}");
        }

        private void HandleEditClassModel(string json)
        {
            try
            {
                string modelName = ExtractJsonValue(json, "data");
                if (string.IsNullOrEmpty(modelName)) return;
                string folder = GetClassModelsFolder();
                string incPath = Path.Combine(folder, modelName + ".inc");
                string clwPath = Path.Combine(folder, modelName + ".clw");
                if (File.Exists(incPath))
                    System.Diagnostics.Process.Start(incPath);
                if (File.Exists(clwPath))
                    System.Diagnostics.Process.Start(clwPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] EditClassModel error: " + ex.Message);
            }
        }

        private void HandleDeleteClassModel(string json)
        {
            try
            {
                string modelName = ExtractJsonValue(json, "data");
                if (string.IsNullOrEmpty(modelName)) return;
                string folder = GetClassModelsFolder();
                string incPath = Path.Combine(folder, modelName + ".inc");
                string clwPath = Path.Combine(folder, modelName + ".clw");
                if (File.Exists(incPath)) File.Delete(incPath);
                if (File.Exists(clwPath)) File.Delete(clwPath);
                SendClassModelsUpdate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] DeleteClassModel error: " + ex.Message);
            }
        }

        private void HandleOpenModelsFolder()
        {
            try
            {
                string folder = GetClassModelsFolder();
                System.Diagnostics.Process.Start("explorer.exe", folder);
            }
            catch { }
        }

        private void HandleAddClassModel()
        {
            try
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Select a Class Model (.inc file)";
                    dlg.Filter = "Clarion Include (*.inc)|*.inc";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    string srcInc = dlg.FileName;
                    string baseName = Path.GetFileNameWithoutExtension(srcInc);
                    string srcDir = Path.GetDirectoryName(srcInc);
                    string srcClw = Path.Combine(srcDir, baseName + ".clw");

                    if (!File.Exists(srcClw))
                    {
                        MessageBox.Show("Matching .clw file not found:\n" + srcClw,
                            "Missing File", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string folder = GetClassModelsFolder();
                    File.Copy(srcInc, Path.Combine(folder, baseName + ".inc"), true);
                    File.Copy(srcClw, Path.Combine(folder, baseName + ".clw"), true);
                    SendClassModelsUpdate();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SettingsDialog] AddClassModel error: " + ex.Message);
            }
        }

        #endregion

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

        // Static helpers used by AssistantChatControl
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
