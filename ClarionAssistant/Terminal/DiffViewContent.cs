using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// ViewContent that hosts a unified diff viewer with inline code review notes.
    /// Generates unified diff via git or LCS fallback, renders in WebView2.
    /// </summary>
    public class DiffViewContent : AbstractViewContent
    {
        private Panel _panel;
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;

        private string _title;
        private string _originalText;
        private string _modifiedText;
        private string _language;
        private bool _ignoreWhitespace;

        private string _tempDir;
        private const string VIRTUAL_HOST = "clarion-diff-data";

        private static readonly List<DiffViewContent> _instances = new List<DiffViewContent>();

        /// <summary>Fires when the user clicks Approve (modified text is passed through).</summary>
        public event Action<string> Applied;

        /// <summary>Fires when the user clicks Cancel.</summary>
        public event Action Cancelled;

        /// <summary>Fires when the user submits review notes (JSON array string).</summary>
        public event Action<string> NotesSubmitted;

        public override Control Control { get { return _panel; } }

        private bool _isDark = true;

        public DiffViewContent(string title, string originalText, string modifiedText, string language = "clarion",
            bool ignoreWhitespace = false, bool isDark = true)
        {
            _title = title ?? "Diff";
            _originalText = originalText ?? "";
            _modifiedText = modifiedText ?? "";
            _language = language ?? "clarion";
            _ignoreWhitespace = ignoreWhitespace;
            _isDark = isDark;
            TitleName = "Diff: " + _title;

            _panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 46) };
            _webView = new ZoomableWebView2 { Dock = DockStyle.Fill };
            _panel.Controls.Add(_webView);

            lock (_instances) { _instances.Add(this); }
            _panel.HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                // Set up temp directory and virtual host mapping for large file transfer
                _tempDir = Path.Combine(Path.GetTempPath(), "ClarionDiff_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tempDir);
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    VIRTUAL_HOST, _tempDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsZoomControlEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
            }
            catch (Exception ex)
            {
                _isInitializing = false; // Reset so retry is possible
                System.Diagnostics.Debug.WriteLine("[DiffViewContent] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = e.IsSuccess;
            _isInitializing = false;
            // Don't call SendDiffData here — the JS "ready" message is the trigger.
            // This avoids double-invocation (NavigationCompleted + "ready" both fire on every load).
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");

                if (action == "ready")
                {
                    SendDiffData();
                }
                else if (action == "approve")
                {
                    // Read-only viewer — return the stored modified text
                    Applied?.Invoke(_modifiedText);
                }
                else if (action == "notes")
                {
                    string notesJson = ExtractJsonValue(json, "notes");
                    NotesSubmitted?.Invoke(notesJson ?? "[]");
                }
                else if (action == "cancel")
                {
                    Cancelled?.Invoke();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DiffViewContent] Message error: " + ex.Message);
            }
        }

        /// <summary>Update the diff content. If already initialized, sends immediately; otherwise waits for "ready" message.</summary>
        public void SetDiff(string title, string originalText, string modifiedText, string language = null)
        {
            _title = title ?? _title;
            _originalText = originalText ?? "";
            _modifiedText = modifiedText ?? "";
            if (language != null) _language = language;
            TitleName = "Diff: " + _title;

            if (_isInitialized)
                SendDiffData();
            // Otherwise the JS "ready" message will trigger SendDiffData when the page loads
        }

        private void SendDiffData()
        {
            if (_webView.CoreWebView2 == null) return;

            try
            {
                // Write original and modified to temp files for git diff
                string origFile = Path.Combine(_tempDir, "original.txt");
                string modFile = Path.Combine(_tempDir, "modified.txt");
                File.WriteAllText(origFile, _originalText ?? "", Encoding.UTF8);
                File.WriteAllText(modFile, _modifiedText ?? "", Encoding.UTF8);

                // Generate unified diff
                string diffText = GenerateUnifiedDiff(origFile, modFile);

                // Write diff to temp file for virtual host serving
                string diffFile = Path.Combine(_tempDir, "diff.txt");
                File.WriteAllText(diffFile, diffText, Encoding.UTF8);

                // Send metadata — JavaScript fetches the diff via virtual host URL
                string json = "{\"type\":\"setDiff\"," +
                    "\"title\":" + JsonString(_title) + "," +
                    "\"language\":" + JsonString(_language) + "," +
                    "\"isDark\":" + (_isDark ? "true" : "false") + "," +
                    "\"diffUrl\":\"https://" + VIRTUAL_HOST + "/diff.txt\"}";
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DiffViewContent] SendDiffData error: " + ex.Message);
            }
        }

        #region Unified Diff Generation

        private string GenerateUnifiedDiff(string origFile, string modFile)
        {
            // Primary: use git diff --no-index
            try
            {
                return RunGitDiff(origFile, modFile);
            }
            catch (Win32Exception)
            {
                // git not on PATH — fall through to LCS
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DiffViewContent] git diff failed: " + ex.Message);
            }

            // Fallback: LCS-based unified diff
            return GenerateUnifiedDiffLcs(_originalText ?? "", _modifiedText ?? "", _ignoreWhitespace);
        }

        private string RunGitDiff(string origFile, string modFile)
        {
            var args = "diff --no-index --no-color";
            if (_ignoreWhitespace) args += " -w";
            args += " -U3 -- \"" + origFile + "\" \"" + modFile + "\"";

            var psi = new ProcessStartInfo("git", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using (var proc = Process.Start(psi))
            {
                // Read both streams on the thread pool to prevent pipe deadlock
                // and ensure the timeout actually fires even if git hangs before writing.
                var stdoutTask = System.Threading.Tasks.Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = System.Threading.Tasks.Task.Run(() => proc.StandardError.ReadToEnd());

                bool streamsRead = System.Threading.Tasks.Task.WaitAll(
                    new System.Threading.Tasks.Task[] { stdoutTask, stderrTask }, 10000);
                if (!streamsRead)
                {
                    try { proc.Kill(); } catch { }
                    throw new Exception("git diff timed out after 10 seconds");
                }

                string output = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();

                // Ensure process has exited after streams are drained
                if (!proc.WaitForExit(2000))
                {
                    try { proc.Kill(); } catch { }
                }

                // git diff --no-index exits with 1 when differences found, 0 when identical
                // Only exit code >= 2 is an actual error
                if (proc.ExitCode >= 2)
                    throw new Exception("git diff exit " + proc.ExitCode + ": " + (stderr ?? ""));

                // Clean up temp file paths from the ---/+++ header lines
                if (!string.IsNullOrEmpty(output))
                {
                    output = output.Replace("a/" + origFile.Replace('\\', '/'), "a/original");
                    output = output.Replace("b/" + modFile.Replace('\\', '/'), "b/modified");
                    // Also handle the raw paths git sometimes uses
                    output = output.Replace(origFile.Replace('\\', '/'), "original");
                    output = output.Replace(modFile.Replace('\\', '/'), "modified");
                    output = output.Replace(origFile, "original");
                    output = output.Replace(modFile, "modified");
                }

                return output ?? "";
            }
        }

        /// <summary>
        /// LCS-based fallback for generating unified diff format when git is not available.
        /// </summary>
        private static string GenerateUnifiedDiffLcs(string originalText, string modifiedText, bool ignoreWhitespace)
        {
            string[] oLines = SplitLines(originalText);
            string[] mLines = SplitLines(modifiedText);

            // Compute edit script (list of Equal/Deleted/Added operations)
            var ops = ComputeEditScript(oLines, mLines, ignoreWhitespace);

            // Group into hunks with 3 lines of context
            var hunks = GroupIntoHunks(ops, 3);

            if (hunks.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("--- a/original");
            sb.AppendLine("+++ b/modified");

            foreach (var hunk in hunks)
            {
                sb.AppendLine(string.Format("@@ -{0},{1} +{2},{3} @@",
                    hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));

                foreach (var op in hunk.Ops)
                {
                    switch (op.Type)
                    {
                        case EditType.Equal:
                            sb.Append(' ');
                            sb.AppendLine(op.Text);
                            break;
                        case EditType.Delete:
                            sb.Append('-');
                            sb.AppendLine(op.Text);
                            break;
                        case EditType.Insert:
                            sb.Append('+');
                            sb.AppendLine(op.Text);
                            break;
                    }
                }
            }

            return sb.ToString();
        }

        private enum EditType { Equal, Delete, Insert }

        private struct EditOp
        {
            public EditType Type;
            public string Text;
            public int OldLine; // 1-based original line (0 if insert)
            public int NewLine; // 1-based modified line (0 if delete)
        }

        private class Hunk
        {
            public int OldStart, OldCount, NewStart, NewCount;
            public List<EditOp> Ops = new List<EditOp>();
        }

        private static List<EditOp> ComputeEditScript(string[] oLines, string[] mLines, bool ignoreWs)
        {
            int n = oLines.Length, m = mLines.Length;

            // Trim common prefix
            int pre = 0;
            while (pre < n && pre < m && Eq(oLines[pre], mLines[pre], ignoreWs)) pre++;

            // Trim common suffix
            int suf = 0;
            while (suf < n - pre && suf < m - pre && Eq(oLines[n - 1 - suf], mLines[m - 1 - suf], ignoreWs)) suf++;

            var result = new List<EditOp>();

            // Prefix — all equal
            for (int i = 0; i < pre; i++)
                result.Add(new EditOp { Type = EditType.Equal, Text = oLines[i], OldLine = i + 1, NewLine = i + 1 });

            // Middle section
            int os = pre, oe = n - suf;
            int ms = pre, me = m - suf;
            int oc = oe - os, mc = me - ms;

            if (oc > 0 || mc > 0)
            {
                if ((long)oc * mc > 10_000_000)
                {
                    // Too large for LCS — treat as full replacement
                    for (int i = os; i < oe; i++)
                        result.Add(new EditOp { Type = EditType.Delete, Text = oLines[i], OldLine = i + 1 });
                    for (int i = ms; i < me; i++)
                        result.Add(new EditOp { Type = EditType.Insert, Text = mLines[i], NewLine = i + 1 });
                }
                else
                {
                    // LCS DP
                    int[,] dp = new int[oc + 1, mc + 1];
                    for (int i = 1; i <= oc; i++)
                        for (int j = 1; j <= mc; j++)
                            dp[i, j] = Eq(oLines[os + i - 1], mLines[ms + j - 1], ignoreWs)
                                ? dp[i - 1, j - 1] + 1
                                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

                    // Backtrack
                    var mid = new List<EditOp>();
                    int oi = oc, mi = mc;
                    while (oi > 0 || mi > 0)
                    {
                        if (oi > 0 && mi > 0 && Eq(oLines[os + oi - 1], mLines[ms + mi - 1], ignoreWs))
                        {
                            mid.Add(new EditOp { Type = EditType.Equal, Text = oLines[os + oi - 1],
                                OldLine = os + oi, NewLine = ms + mi });
                            oi--; mi--;
                        }
                        else if (mi > 0 && (oi == 0 || dp[oi, mi - 1] >= dp[oi - 1, mi]))
                        {
                            mid.Add(new EditOp { Type = EditType.Insert, Text = mLines[ms + mi - 1], NewLine = ms + mi });
                            mi--;
                        }
                        else
                        {
                            mid.Add(new EditOp { Type = EditType.Delete, Text = oLines[os + oi - 1], OldLine = os + oi });
                            oi--;
                        }
                    }
                    mid.Reverse();
                    result.AddRange(mid);
                }
            }

            // Suffix — all equal
            for (int i = 0; i < suf; i++)
            {
                int oIdx = n - suf + i, mIdx = m - suf + i;
                result.Add(new EditOp { Type = EditType.Equal, Text = oLines[oIdx], OldLine = oIdx + 1, NewLine = mIdx + 1 });
            }

            return result;
        }

        private static List<Hunk> GroupIntoHunks(List<EditOp> ops, int context)
        {
            var hunks = new List<Hunk>();
            // Find ranges of changes with context
            var changeIndices = new List<int>();
            for (int i = 0; i < ops.Count; i++)
                if (ops[i].Type != EditType.Equal)
                    changeIndices.Add(i);

            if (changeIndices.Count == 0) return hunks;

            // Group changes that are within 2*context of each other
            var groups = new List<int[]>(); // [startChangeIdx, endChangeIdx]
            int gs = 0;
            for (int i = 1; i < changeIndices.Count; i++)
            {
                if (changeIndices[i] - changeIndices[i - 1] > 2 * context)
                {
                    groups.Add(new[] { changeIndices[gs], changeIndices[i - 1] });
                    gs = i;
                }
            }
            groups.Add(new[] { changeIndices[gs], changeIndices[changeIndices.Count - 1] });

            // Build hunks
            foreach (var grp in groups)
            {
                int start = Math.Max(0, grp[0] - context);
                int end = Math.Min(ops.Count - 1, grp[1] + context);

                var hunk = new Hunk();
                hunk.Ops = new List<EditOp>();

                for (int i = start; i <= end; i++)
                {
                    hunk.Ops.Add(ops[i]);
                }

                // Compute old/new start and count
                // Find first old/new line number from ops in this hunk
                int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;
                bool foundOldStart = false, foundNewStart = false;
                foreach (var op in hunk.Ops)
                {
                    if (op.Type == EditType.Equal || op.Type == EditType.Delete)
                    {
                        if (!foundOldStart && op.OldLine > 0) { oldStart = op.OldLine; foundOldStart = true; }
                        oldCount++;
                    }
                    if (op.Type == EditType.Equal || op.Type == EditType.Insert)
                    {
                        if (!foundNewStart && op.NewLine > 0) { newStart = op.NewLine; foundNewStart = true; }
                        newCount++;
                    }
                }

                hunk.OldStart = oldStart > 0 ? oldStart : 1;
                hunk.OldCount = oldCount;
                hunk.NewStart = newStart > 0 ? newStart : 1;
                hunk.NewCount = newCount;
                hunks.Add(hunk);
            }

            return hunks;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
                Array.Resize(ref lines, lines.Length - 1);
            return lines;
        }

        private static bool Eq(string a, string b, bool ignoreWs)
        {
            if (ignoreWs)
                return string.Equals(NormalizeWs(a), NormalizeWs(b), StringComparison.Ordinal);
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static string NormalizeWs(string s)
        {
            if (s == null) return null;
            var sb = new StringBuilder(s.Length);
            bool lastWasSpace = true;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t')
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                sb.Length--;
            return sb.ToString();
        }

        #endregion

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "diff.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "diff.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "diff.html");
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 20);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    default:
                        if (c < ' ')
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string ExtractJsonValue(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return null;
            if (json[idx] == 'n') return null;
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
                        if (next == 't') { sb.Append('\t'); idx += 2; continue; }
                        sb.Append(c); idx++; continue;
                    }
                    if (c == '"') break;
                    sb.Append(c);
                    idx++;
                }
                return sb.ToString();
            }
            int start = idx;
            while (idx < json.Length && json[idx] != ',' && json[idx] != '}') idx++;
            return json.Substring(start, idx - start).Trim();
        }

        /// <summary>Apply light or dark theme to this diff viewer.</summary>
        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (_panel != null)
                _panel.BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"applyTheme\",\"isDark\":" + (isDark ? "true" : "false") + "}");
        }

        /// <summary>Apply theme to all open diff viewers.</summary>
        public static void ApplyThemeToAll(bool isDark)
        {
            lock (_instances)
            {
                foreach (var inst in _instances)
                    inst.ApplyTheme(isDark);
            }
        }

        public override void Dispose()
        {
            lock (_instances) { _instances.Remove(this); }
            if (_webView != null)
            {
                _webView.Dispose();
                _webView = null;
            }
            if (_panel != null)
            {
                _panel.Dispose();
                _panel = null;
            }
            CleanupTempDir();
            base.Dispose();
        }

        /// <summary>
        /// WebView2 subclass that intercepts Ctrl+MouseWheel at the Win32 message level
        /// and forwards it to JavaScript for font size changes.
        /// WebView2 swallows WM_MOUSEWHEEL+Ctrl internally even when IsZoomControlEnabled=false,
        /// so the wheel event never reaches JavaScript. This override catches it first.
        /// </summary>
        private class ZoomableWebView2 : WebView2
        {
            private const int WM_MOUSEWHEEL = 0x020A;
            private const int MK_CONTROL = 0x0008;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEWHEEL)
                {
                    int wParam = m.WParam.ToInt32();
                    int keys = wParam & 0xFFFF;
                    if ((keys & MK_CONTROL) != 0 && CoreWebView2 != null)
                    {
                        short delta = (short)(wParam >> 16);
                        int change = delta > 0 ? 1 : -1;
                        CoreWebView2.ExecuteScriptAsync(
                            "if(typeof applyFontSize==='function')applyFontSize(savedFontSize+(" + change + "))");
                        return; // Consume — don't let WebView2 swallow it
                    }
                }
                base.WndProc(ref m);
            }
        }

        private void CleanupTempDir()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
                _tempDir = null;
            }
        }
    }
}
