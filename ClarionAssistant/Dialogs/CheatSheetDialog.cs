using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ClarionAssistant.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Dialogs
{
    public class CheatSheetDialog : Form
    {
        private readonly bool _isDark;
        private WebView2 _webView;

        public CheatSheetDialog(bool isDarkTheme)
        {
            _isDark = isDarkTheme;
            InitializeForm();
        }

        private void InitializeForm()
        {
            Text = "Clarion Assistant \u2014 Cheat Sheet";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(560, 620);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = _isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
            Controls.Add(_webView);

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
                System.Diagnostics.Debug.WriteLine("[CheatSheet] WebView2 init error: " + ex.Message);
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess) return;

            var settings = _webView.CoreWebView2.Settings;
            settings.IsScriptEnabled = true;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            string htmlPath = GetHtmlPath();
            if (File.Exists(htmlPath))
            {
                string url = new Uri(htmlPath).AbsoluteUri + "?theme=" + (_isDark ? "dark" : "light");
                _webView.CoreWebView2.Navigate(url);
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "cheatsheet.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "cheatsheet.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "cheatsheet.html");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
