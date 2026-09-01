# Service Templates (EditorService, SettingsService, ScriptBridge)

C# templates for the shared services. Replace placeholders per the Placeholder Reference in `project-files.md`.

## {AddinName}/Services/EditorService.cs

**Public API:**
- `HasActiveTextEditor()` — returns true if a text editor is active
- `GetActiveDocumentContent()` — returns full text of active document
- `GetActiveDocumentPath()` — returns file path of active document
- `InsertTextAtCaret(string text)` — inserts text at cursor position
- `GetSelectedText()` — returns highlighted text from active editor (via SelectionManager)
- `GetWordUnderCursor()` — returns word at cursor position (falls back to selection)
- `NavigateToFileAndLine(string filePath, int lineNumber)` — opens file and jumps to line (via FileService.OpenFile)
- `GetClarionInstallPath()` — static, derives Clarion root from running assembly (e.g. `C:\Clarion12`)

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace {AddinName}.Services
{
    public class InsertResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        public static InsertResult Succeeded() => new InsertResult { Success = true };
        public static InsertResult Failed(string message) => new InsertResult { Success = false, ErrorMessage = message };
    }

    /// <summary>
    /// Service to interact with the active text editor in the Clarion IDE.
    /// Uses reflection for version compatibility.
    /// </summary>
    public class EditorService
    {
        public bool HasActiveTextEditor()
        {
            try { return GetActiveTextArea() != null; }
            catch { return false; }
        }

        public string GetActiveDocumentContent()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var document = GetProperty(textArea, "Document");
                if (document == null) return null;

                return (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
            }
            catch { return null; }
        }

        public string GetActiveDocumentPath()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return null;

                var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow") ?? GetProperty(workbench, "ActiveContent");
                if (activeWindow == null) return null;

                var viewContent = GetProperty(activeWindow, "ViewContent") ?? GetProperty(activeWindow, "ActiveViewContent") ?? activeWindow;
                return (GetProperty(viewContent, "FileName") ?? GetProperty(viewContent, "PrimaryFileName")) as string;
            }
            catch { return null; }
        }

        public InsertResult InsertTextAtCaret(string text)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return InsertResult.Failed("No active text editor");

                var document = GetProperty(textArea, "Document");
                var caret = GetProperty(textArea, "Caret");
                if (document == null || caret == null) return InsertResult.Failed("Cannot access editor");

                var offset = (int)GetProperty(caret, "Offset");
                var insertMethod = document.GetType().GetMethod("Insert", new[] { typeof(int), typeof(string) });
                if (insertMethod == null) return InsertResult.Failed("Insert method not found");

                insertMethod.Invoke(document, new object[] { offset, text });
                SetProperty(caret, "Offset", offset + text.Length);

                try { textArea.GetType().GetMethod("Invalidate", Type.EmptyTypes)?.Invoke(textArea, null); } catch { }
                return InsertResult.Succeeded();
            }
            catch (Exception ex) { return InsertResult.Failed(ex.Message); }
        }

        /// <summary>
        /// Gets the currently selected text in the active editor.
        /// Returns null if no selection or no active editor.
        /// </summary>
        public string GetSelectedText()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var selMgr = GetProperty(textArea, "SelectionManager");
                if (selMgr != null)
                {
                    var hasSelection = GetProperty(selMgr, "HasSomethingSelected");
                    if (hasSelection is bool && (bool)hasSelection)
                    {
                        var selectedText = GetProperty(selMgr, "SelectedText");
                        if (selectedText is string s && !string.IsNullOrEmpty(s))
                            return s.Trim();
                    }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Gets the word under the cursor in the active editor.
        /// Falls back to selected text if available.
        /// Useful for "look up this symbol" features.
        /// </summary>
        public string GetWordUnderCursor()
        {
            try
            {
                string selected = GetSelectedText();
                if (!string.IsNullOrEmpty(selected)) return selected;

                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var document = GetProperty(textArea, "Document");
                if (document == null) return null;

                var caret = GetProperty(textArea, "Caret");
                if (caret == null) return null;

                var offsetObj = GetProperty(caret, "Offset");
                if (offsetObj == null) return null;
                int offset = (int)offsetObj;

                var textObj = GetProperty(document, "TextContent") ?? GetProperty(document, "Text");
                if (textObj == null) return null;
                string fullText = textObj.ToString();

                if (offset < 0 || offset > fullText.Length) return null;

                int start = offset;
                while (start > 0 && IsWordChar(fullText[start - 1])) start--;
                int end = offset;
                while (end < fullText.Length && IsWordChar(fullText[end])) end++;

                return start < end ? fullText.Substring(start, end - start) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Opens a file in the IDE editor and navigates to a specific line.
        /// Uses FileService.OpenFile then sets Caret.Line.
        /// </summary>
        public void NavigateToFileAndLine(string filePath, int lineNumber)
        {
            try
            {
                var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
                if (sharpDevelopAsm == null) return;

                var fileServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.FileService");
                if (fileServiceType == null) return;

                var openFileMethod = fileServiceType.GetMethod("OpenFile",
                    BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(string) }, null);
                if (openFileMethod != null)
                {
                    openFileMethod.Invoke(null, new object[] { filePath });
                    if (lineNumber > 0)
                    {
                        var textArea = GetActiveTextArea();
                        if (textArea != null)
                        {
                            var caret = GetProperty(textArea, "Caret");
                            SetProperty(caret, "Line", lineNumber - 1);
                            SetProperty(caret, "Column", 0);
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Gets the Clarion installation root directory by deriving from the running IDE assembly location.
        /// Returns e.g. "C:\Clarion12" or null if not found.
        /// </summary>
        public static string GetClarionInstallPath()
        {
            try
            {
                // The IDE loads from {ClarionRoot}\bin\ICSharpCode.SharpDevelop.dll
                var asm = typeof(WorkbenchSingleton).Assembly;
                string binPath = Path.GetDirectoryName(asm.Location);       // {ClarionRoot}\bin
                string clarionRoot = Path.GetDirectoryName(binPath);         // {ClarionRoot}
                if (Directory.Exists(Path.Combine(clarionRoot, "accessory")))
                    return clarionRoot;

                // Fallback: AppDomain base directory
                string appBase = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                clarionRoot = Path.GetDirectoryName(appBase);
                if (Directory.Exists(Path.Combine(clarionRoot, "accessory")))
                    return clarionRoot;

                return null;
            }
            catch { return null; }
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == ':';
        }

        private object GetActiveTextArea()
        {
            var workbench = WorkbenchSingleton.Workbench;
            if (workbench == null) return null;

            var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow") ?? GetProperty(workbench, "ActiveContent");
            if (activeWindow == null) return null;

            var viewContent = GetProperty(activeWindow, "ViewContent") ?? GetProperty(activeWindow, "ActiveViewContent") ?? activeWindow;

            // Try TextEditorControl
            var textEditor = GetProperty(viewContent, "TextEditorControl");
            if (textEditor != null)
            {
                var result = GetTextAreaFromEditor(textEditor);
                if (result != null) return result;
            }

            // Try Control property
            var control = GetProperty(viewContent, "Control");
            if (control != null)
            {
                var result = GetTextAreaFromEditor(control);
                if (result != null) return result;
                if (control is Control wc)
                {
                    result = FindTextAreaInControls(wc);
                    if (result != null) return result;
                }
            }

            // Try SecondaryViewContents (Clarion Embeditor)
            var secondary = GetProperty(viewContent, "SecondaryViewContents") as System.Collections.IEnumerable;
            if (secondary != null)
            {
                foreach (var svc in secondary)
                {
                    if (GetProperty(svc, "Control") is Control wc)
                    {
                        var result = FindTextAreaInControls(wc);
                        if (result != null) return result;
                    }
                }
            }
            return null;
        }

        private object GetTextAreaFromEditor(object editor)
        {
            if (editor == null) return null;
            var tac = GetProperty(editor, "ActiveTextAreaControl");
            if (tac != null)
            {
                var ta = GetProperty(tac, "TextArea");
                if (ta != null && GetProperty(ta, "Document") != null && GetProperty(ta, "Caret") != null) return ta;
            }
            if (GetProperty(editor, "Document") != null && GetProperty(editor, "Caret") != null) return editor;
            return null;
        }

        private object FindTextAreaInControls(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                var result = GetTextAreaFromEditor(child) ?? FindTextAreaInControls(child);
                if (result != null) return result;
            }
            return null;
        }

        private object GetProperty(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(obj, null);
                var field = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                return field?.GetValue(obj);
            }
            catch { return null; }
        }

        private void SetProperty(object obj, string name, object value)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop?.CanWrite == true) prop.SetValue(obj, value, null);
            }
            catch { }
        }
    }
}
```

## {AddinName}/Services/SettingsService.cs

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace {AddinName}.Services
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
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "{AddinName}");
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
                sb.AppendLine("# {AddinName} Settings");
                sb.AppendLine($"# Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                foreach (var kv in _settings) sb.AppendLine($"{kv.Key}={kv.Value}");
                File.WriteAllText(_settingsPath, sb.ToString());
            }
            catch { }
        }
    }
}
```

## {AddinName}/Services/ScriptBridge.cs (if HTML UI)

```csharp
using System;
using System.Runtime.InteropServices;

namespace {AddinName}.Services
{
    /// <summary>
    /// Bridge for JavaScript to C# communication via WebBrowser.ObjectForScripting.
    /// Call from JS: window.external.PerformAction(jsonData)
    /// </summary>
    [ComVisible(true)]
    public class ScriptBridge
    {
        private readonly Action<string> _onAction;

        public ScriptBridge(Action<string> onAction) => _onAction = onAction;

        public void PerformAction(string data) => _onAction?.Invoke(data);
    }
}
```
