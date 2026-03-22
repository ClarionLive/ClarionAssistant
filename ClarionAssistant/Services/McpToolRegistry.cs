using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Defines a single MCP tool with its metadata and handler.
    /// </summary>
    public class McpTool
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> InputSchema { get; set; }
        public Func<Dictionary<string, object>, object> Handler { get; set; }
        public bool RequiresUiThread { get; set; }
    }

    /// <summary>
    /// Registry of MCP tools that expose Clarion IDE operations.
    /// Each tool maps to methods on EditorService and ClarionClassParser.
    /// </summary>
    public class McpToolRegistry
    {
        private readonly Dictionary<string, McpTool> _tools = new Dictionary<string, McpTool>(StringComparer.OrdinalIgnoreCase);
        private readonly EditorService _editorService;
        private readonly ClarionClassParser _parser;
        private readonly AppTreeService _appTree;
        private ClaudeChatControl _chatControl;
        private LspClient _lspClient;

        public McpToolRegistry(EditorService editorService, ClarionClassParser parser)
        {
            _editorService = editorService;
            _parser = parser;
            _appTree = new AppTreeService();
            RegisterAllTools();
        }

        /// <summary>
        /// Set reference to chat control for solution context and indexing.
        /// </summary>
        public void SetChatControl(ClaudeChatControl control)
        {
            _chatControl = control;
        }

        public int GetToolCount() { return _tools.Count; }

        public bool RequiresUiThread(string toolName)
        {
            McpTool tool;
            return _tools.TryGetValue(toolName, out tool) && tool.RequiresUiThread;
        }

        public object ExecuteTool(string name, Dictionary<string, object> arguments)
        {
            McpTool tool;
            if (!_tools.TryGetValue(name, out tool))
                throw new ArgumentException("Unknown tool: " + name);

            return tool.Handler(arguments ?? new Dictionary<string, object>());
        }

        public List<Dictionary<string, object>> GetToolDefinitions()
        {
            return _tools.Values.Select(t => McpJsonRpc.BuildToolDefinition(
                t.Name, t.Description, t.InputSchema
            )).ToList();
        }

        #region Tool Registration

        private void RegisterAllTools()
        {
            // === IDE Context Tools ===

            Register(new McpTool
            {
                Name = "get_active_file",
                Description = "Get the path and full content of the file currently open in the Clarion IDE editor",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string path = _editorService.GetActiveDocumentPath();
                    string content = _editorService.GetActiveDocumentContent();
                    return new Dictionary<string, object>
                    {
                        { "path", path ?? "(no file open)" },
                        { "content", content ?? "(unable to read)" }
                    };
                }
            });

            Register(new McpTool
            {
                Name = "get_selected_text",
                Description = "Get the currently selected text in the Clarion IDE editor. Returns null if nothing selected.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    return _editorService.GetSelectedText() ?? "(no selection)";
                }
            });

            Register(new McpTool
            {
                Name = "get_word_under_cursor",
                Description = "Get the word at the current cursor position in the editor. Useful for identifying what symbol the developer is looking at.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    return _editorService.GetWordUnderCursor() ?? "(no word at cursor)";
                }
            });

            Register(new McpTool
            {
                Name = "get_cursor_position",
                Description = "Get the current cursor position (line and column, 1-based) and total line count in the active editor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var pos = _editorService.GetCursorPosition();
                    int lineCount = _editorService.GetLineCount();
                    if (pos == null)
                        return "(no active editor)";
                    return new Dictionary<string, object>
                    {
                        { "line", pos[0] },
                        { "column", pos[1] },
                        { "totalLines", lineCount }
                    };
                }
            });

            // === Editor Operation Tools ===

            Register(new McpTool
            {
                Name = "go_to_line",
                Description = "Navigate to a specific line number in the currently open file in the Clarion IDE editor. Scrolls the view to show the line.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string> { { "line", "Line number to go to (1-based)" } },
                    new[] { "line" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    int line = McpJsonRpc.GetInt(args, "line", 1);
                    if (_editorService.GoToLine(line))
                        return "Moved to line " + line;
                    return "Error: could not navigate to line " + line;
                }
            });

            Register(new McpTool
            {
                Name = "insert_text_at_cursor",
                Description = "Insert text at the current cursor position in the Clarion IDE editor",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string> { { "text", "The text to insert" } },
                    new[] { "text" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string text = McpJsonRpc.GetString(args, "text");
                    if (string.IsNullOrEmpty(text))
                        return "Error: text parameter is required";
                    var result = _editorService.InsertTextAtCaret(text);
                    return result.Success ? "Text inserted successfully" : "Error: " + result.ErrorMessage;
                }
            });

            Register(new McpTool
            {
                Name = "replace_text",
                Description = "Find and replace text in the active editor. Replaces ALL occurrences of old_text with new_text.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "old_text", "The exact text to find and replace" },
                        { "new_text", "The replacement text" }
                    },
                    new[] { "old_text", "new_text" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string oldText = McpJsonRpc.GetString(args, "old_text");
                    string newText = McpJsonRpc.GetString(args, "new_text", "");
                    if (string.IsNullOrEmpty(oldText))
                        return "Error: old_text is required";
                    var result = _editorService.ReplaceText(oldText, newText);
                    return result.Success ? "Text replaced successfully" : "Error: " + result.ErrorMessage;
                }
            });

            Register(new McpTool
            {
                Name = "replace_range",
                Description = "Replace text between two positions (line/column, 1-based) in the active editor. Use to replace a specific region of code.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "start_line", "Start line (1-based)" },
                        { "start_col", "Start column (1-based)" },
                        { "end_line", "End line (1-based)" },
                        { "end_col", "End column (1-based)" },
                        { "new_text", "Replacement text (empty string to delete)" }
                    },
                    new[] { "start_line", "end_line", "new_text" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    int startLine = McpJsonRpc.GetInt(args, "start_line");
                    int startCol = McpJsonRpc.GetInt(args, "start_col", 1);
                    int endLine = McpJsonRpc.GetInt(args, "end_line");
                    int endCol = McpJsonRpc.GetInt(args, "end_col", 999);
                    string newText = McpJsonRpc.GetString(args, "new_text", "");
                    var result = _editorService.ReplaceRange(startLine, startCol, endLine, endCol, newText);
                    return result.Success ? "Range replaced successfully" : "Error: " + result.ErrorMessage;
                }
            });

            Register(new McpTool
            {
                Name = "select_range",
                Description = "Select a range of text in the active editor (line/column, 1-based). The selected text will be highlighted.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "start_line", "Start line (1-based)" },
                        { "start_col", "Start column (1-based)" },
                        { "end_line", "End line (1-based)" },
                        { "end_col", "End column (1-based)" }
                    },
                    new[] { "start_line", "end_line" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    int startLine = McpJsonRpc.GetInt(args, "start_line");
                    int startCol = McpJsonRpc.GetInt(args, "start_col", 1);
                    int endLine = McpJsonRpc.GetInt(args, "end_line");
                    int endCol = McpJsonRpc.GetInt(args, "end_col", 999);
                    var result = _editorService.SelectRange(startLine, startCol, endLine, endCol);
                    return result.Success ? "Text selected" : "Error: " + result.ErrorMessage;
                }
            });

            Register(new McpTool
            {
                Name = "delete_range",
                Description = "Delete text between two positions (line/column, 1-based) in the active editor.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "start_line", "Start line (1-based)" },
                        { "start_col", "Start column (1-based)" },
                        { "end_line", "End line (1-based)" },
                        { "end_col", "End column (1-based)" }
                    },
                    new[] { "start_line", "end_line" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    int startLine = McpJsonRpc.GetInt(args, "start_line");
                    int startCol = McpJsonRpc.GetInt(args, "start_col", 1);
                    int endLine = McpJsonRpc.GetInt(args, "end_line");
                    int endCol = McpJsonRpc.GetInt(args, "end_col", 999);
                    var result = _editorService.DeleteRange(startLine, startCol, endLine, endCol);
                    return result.Success ? "Text deleted" : "Error: " + result.ErrorMessage;
                }
            });

            Register(new McpTool
            {
                Name = "undo",
                Description = "Undo the last edit in the active editor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _editorService.Undo() ? "Undo successful" : "Nothing to undo"
            });

            Register(new McpTool
            {
                Name = "redo",
                Description = "Redo the last undone edit in the active editor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _editorService.Redo() ? "Redo successful" : "Nothing to redo"
            });

            Register(new McpTool
            {
                Name = "save_file",
                Description = "Save the currently active file in the Clarion IDE editor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _editorService.SaveActiveDocument() ? "File saved" : "Error: could not save"
            });

            Register(new McpTool
            {
                Name = "close_file",
                Description = "Close the currently active editor tab.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _editorService.CloseActiveDocument() ? "File closed" : "Error: could not close"
            });

            Register(new McpTool
            {
                Name = "get_open_files",
                Description = "List all files currently open in the Clarion IDE editor tabs.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var files = _editorService.GetOpenFiles();
                    return files.Count > 0 ? string.Join("\n", files) : "(no files open)";
                }
            });

            Register(new McpTool
            {
                Name = "get_line_text",
                Description = "Get the text of a specific line (1-based) from the active editor buffer. Reflects unsaved changes.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string> { { "line", "Line number (1-based)" } },
                    new[] { "line" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    int line = McpJsonRpc.GetInt(args, "line", 1);
                    string text = _editorService.GetLineText(line);
                    return text ?? "Error: could not read line " + line;
                }
            });

            Register(new McpTool
            {
                Name = "find_in_file",
                Description = "Search for text in the active editor buffer (includes unsaved changes). Returns matching line numbers and columns.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "search", "Text to search for" },
                        { "case_sensitive", "true for case-sensitive search (default: false)" }
                    },
                    new[] { "search" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string search = McpJsonRpc.GetString(args, "search");
                    if (string.IsNullOrEmpty(search)) return "Error: search parameter required";
                    bool caseSensitive = McpJsonRpc.GetString(args, "case_sensitive", "false")
                        .Equals("true", StringComparison.OrdinalIgnoreCase);

                    var results = _editorService.FindInFile(search, caseSensitive);
                    if (results.Count == 0) return "No matches found for: " + search;

                    var sb = new StringBuilder();
                    sb.AppendLine(results.Count + " match(es) found:");
                    foreach (var match in results)
                        sb.AppendLine("  Line " + match[0] + ", Col " + match[1]);
                    return sb.ToString();
                }
            });

            Register(new McpTool
            {
                Name = "is_modified",
                Description = "Check if the active file has unsaved changes.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _editorService.IsModified() ? "Yes - file has unsaved changes" : "No - file is saved"
            });

            Register(new McpTool
            {
                Name = "toggle_comment",
                Description = "Toggle Clarion line comments (!) on the specified line range (1-based). If all lines are commented, uncomments them; otherwise comments them.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "start_line", "First line to toggle (1-based)" },
                        { "end_line", "Last line to toggle (1-based, inclusive)" }
                    },
                    new[] { "start_line", "end_line" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    int startLine = McpJsonRpc.GetInt(args, "start_line");
                    int endLine = McpJsonRpc.GetInt(args, "end_line");
                    var result = _editorService.ToggleComment(startLine, endLine);
                    return result.Success ? "Comment toggled on lines " + startLine + "-" + endLine : "Error: " + result.ErrorMessage;
                }
            });

            // === IDE Inspector Tools ===

            Register(new McpTool
            {
                Name = "inspect_ide",
                Description = @"Inspect the Clarion IDE state using reflection. Available commands:
- 'active_view' - Full inspection of the active workbench window (type, properties, methods, control tree, text editor, secondary views, application object)
- 'editor_text' - Read the full text content of the active editor (text editor or embeditor, includes unsaved changes)
- 'all_windows' - List all open workbench windows with their types and filenames
- 'all_pads' - List all docked pads (tool windows) with their types and visibility
- 'app_details' - Deep inspect the Application object (procedures with all properties, modules)
- 'embed_details' - Inspect the embeditor state (ClaGenEditor, PweeEditorDetails, embed points)
- 'path:<dotpath>' - Inspect a specific property path starting from Workbench (e.g. 'path:ActiveWorkbenchWindow.ViewContent.App')
- 'types' - Discover automation-related types in loaded assemblies (AppGen, Embed, Generator)
- 'assemblies' - List all loaded assemblies

Use this tool to discover IDE APIs and understand what's available for automation.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "command", "Inspection command: active_view, editor_text, all_windows, all_pads, app_details, embed_details, path:<dotpath>, types, assemblies" }
                    },
                    new[] { "command" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string command = McpJsonRpc.GetString(args, "command", "active_view");

                    switch (command.ToLower())
                    {
                        case "active_view": return IdeReflectionService.InspectActiveView();
                        case "editor_text": return IdeReflectionService.ReadActiveEditorText();
                        case "all_windows": return IdeReflectionService.ListAllWindows();
                        case "all_pads": return IdeReflectionService.ListAllPads();
                        case "app_details": return IdeReflectionService.InspectApplicationDetails();
                        case "embed_details": return IdeReflectionService.InspectEmbedDetails();
                        case "types": return IdeReflectionService.DiscoverAutomationTypes();
                        case "assemblies": return IdeReflectionService.ListLoadedAssemblies();
                        default:
                            if (command.StartsWith("path:"))
                                return IdeReflectionService.InspectPath(command.Substring(5));
                            return "Unknown command: " + command + ". Use: active_view, editor_text, all_windows, all_pads, app_details, embed_details, path:<dotpath>, types, assemblies";
                    }
                }
            });

            // === Application Tree Tools ===

            Register(new McpTool
            {
                Name = "open_app",
                Description = "Open a Clarion .app file in the IDE. The app must be loaded before listing procedures or opening embeds.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string> { { "path", "Absolute path to the .app file" } },
                    new[] { "path" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string path = McpJsonRpc.GetString(args, "path");
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        return "Error: .app file not found: " + path;
                    return _appTree.OpenApp(path) ? "App opened: " + path : "Error: could not open app";
                }
            });

            Register(new McpTool
            {
                Name = "get_app_info",
                Description = "Get info about the currently open Clarion application (.app) - name, filename, target type, language.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var info = _appTree.GetAppInfo();
                    return info != null ? (object)info : "No .app file is currently open";
                }
            });

            Register(new McpTool
            {
                Name = "list_procedures",
                Description = "List all procedure names in the currently open Clarion application.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var names = _appTree.GetProcedureNames();
                    if (names.Count == 0) return "No procedures found (is an .app open?)";
                    return names.Count + " procedures:\n" + string.Join("\n", names);
                }
            });

            Register(new McpTool
            {
                Name = "get_procedure_details",
                Description = "Get detailed info about all procedures in the open app - name, prototype, module, parent, template.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var details = _appTree.GetProcedureDetails();
                    if (details.Count == 0) return "No procedures found (is an .app open?)";
                    return details;
                }
            });

            Register(new McpTool
            {
                Name = "open_procedure_embed",
                Description = "Open the embeditor for a specific procedure in the currently open Clarion app. The app must be loaded first.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string> { { "procedure_name", "Name of the procedure to open" } },
                    new[] { "procedure_name" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string name = McpJsonRpc.GetString(args, "procedure_name");
                    if (string.IsNullOrEmpty(name)) return "Error: procedure_name required";
                    return _appTree.OpenProcedureEmbed(name);
                }
            });

            Register(new McpTool
            {
                Name = "select_procedure",
                Description = "Select a procedure in the ClaList without opening the embeditor. For testing procedure selection.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string> { { "procedure_name", "Name of the procedure to select" } },
                    new[] { "procedure_name" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string name = McpJsonRpc.GetString(args, "procedure_name");
                    if (string.IsNullOrEmpty(name)) return "Error: procedure_name required";
                    return _appTree.SelectProcedure(name);
                }
            });

            Register(new McpTool
            {
                Name = "get_embed_info",
                Description = "Get info about the currently active embeditor - app name, file, embed position.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var info = _appTree.GetEmbedInfo();
                    return info != null ? (object)info : "No embeditor active";
                }
            });

            Register(new McpTool
            {
                Name = "save_and_close_embeditor",
                Description = "Save changes and close the currently open embeditor. Use this when done editing embed code.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _appTree.SaveAndCloseEmbeditor()
            });

            Register(new McpTool
            {
                Name = "cancel_embeditor",
                Description = "Discard changes and close the currently open embeditor. Use this to abandon edits without saving.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _appTree.CancelEmbeditor()
            });

            // === Embed Navigation Tools ===

            Register(new McpTool
            {
                Name = "next_embed",
                Description = "Navigate to the next embed point in the embeditor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _appTree.NavigateEmbed("next", false)
            });

            Register(new McpTool
            {
                Name = "prev_embed",
                Description = "Navigate to the previous embed point in the embeditor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _appTree.NavigateEmbed("prev", false)
            });

            Register(new McpTool
            {
                Name = "next_filled_embed",
                Description = "Navigate to the next filled embed point (one that contains user code) in the embeditor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _appTree.NavigateEmbed("next", true)
            });

            Register(new McpTool
            {
                Name = "prev_filled_embed",
                Description = "Navigate to the previous filled embed point (one that contains user code) in the embeditor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args => _appTree.NavigateEmbed("prev", true)
            });

            // === File System Tools ===

            Register(new McpTool
            {
                Name = "open_file",
                Description = "Open a file in the Clarion IDE editor and optionally navigate to a specific line number",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Absolute path to the file to open" },
                        { "line", "Line number to navigate to (optional, 1-based)" }
                    },
                    new[] { "path" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string path = McpJsonRpc.GetString(args, "path");
                    int line = McpJsonRpc.GetInt(args, "line", 1);
                    if (!File.Exists(path))
                        return "Error: file not found: " + path;
                    _editorService.NavigateToFileAndLine(path, line);
                    return "Opened " + path + " at line " + line;
                }
            });

            // === File System Tools ===

            Register(new McpTool
            {
                Name = "read_file",
                Description = "Read content of a file from disk. Optionally specify start_line and end_line to read a specific line range (1-based, inclusive).",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Absolute path to the file" },
                        { "start_line", "First line to read, 1-based (optional — reads from start if omitted)" },
                        { "end_line", "Last line to read, 1-based inclusive (optional — reads to end if omitted)" }
                    },
                    new[] { "path" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string path = McpJsonRpc.GetString(args, "path");
                    if (!File.Exists(path))
                        return "Error: file not found: " + path;

                    int startLine = McpJsonRpc.GetInt(args, "start_line", 0);
                    int endLine = McpJsonRpc.GetInt(args, "end_line", 0);

                    if (startLine > 0 || endLine > 0)
                    {
                        var allLines = File.ReadAllLines(path);
                        int from = Math.Max(1, startLine) - 1; // convert to 0-based
                        int to = endLine > 0 ? Math.Min(endLine, allLines.Length) : allLines.Length;
                        var sb = new System.Text.StringBuilder();
                        for (int i = from; i < to; i++)
                        {
                            sb.AppendLine((i + 1).ToString().PadLeft(5) + "  " + allLines[i]);
                        }
                        return sb.ToString();
                    }

                    return File.ReadAllText(path);
                }
            });

            Register(new McpTool
            {
                Name = "write_file",
                Description = "Write content to a file on disk. Creates the file if it doesn't exist, overwrites if it does.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Absolute path to write to" },
                        { "content", "The content to write" }
                    },
                    new[] { "path", "content" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string path = McpJsonRpc.GetString(args, "path");
                    string content = McpJsonRpc.GetString(args, "content", "");
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(path, content);
                    return "File written: " + path + " (" + content.Length + " chars)";
                }
            });

            Register(new McpTool
            {
                Name = "append_to_file",
                Description = "Append text to the end of an existing file",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Absolute path to the file" },
                        { "text", "Text to append" }
                    },
                    new[] { "path", "text" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string path = McpJsonRpc.GetString(args, "path");
                    string text = McpJsonRpc.GetString(args, "text", "");
                    if (!File.Exists(path))
                        return "Error: file not found: " + path;
                    var result = _editorService.AppendTextToFile(path, text);
                    return result.Success ? "Text appended to " + path : "Error: " + result.ErrorMessage;
                }
            });

            Register(new McpTool
            {
                Name = "list_directory",
                Description = "List files in a directory with an optional search pattern (e.g., '*.inc')",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Directory path to list" },
                        { "pattern", "Search pattern (optional, default: *)" }
                    },
                    new[] { "path" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string path = McpJsonRpc.GetString(args, "path");
                    string pattern = McpJsonRpc.GetString(args, "pattern", "*");
                    if (!Directory.Exists(path))
                        return "Error: directory not found: " + path;
                    var files = Directory.GetFiles(path, pattern);
                    return string.Join("\n", files.Select(f => Path.GetFileName(f)));
                }
            });

            // === Clarion Class Intelligence Tools ===

            Register(new McpTool
            {
                Name = "analyze_class",
                Description = "Parse CLASS definitions from a Clarion .inc file. Returns class names, methods (with signatures), data members, and module file references.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "file_path", "Path to the .inc file to analyze" }
                    },
                    new[] { "file_path" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string filePath = McpJsonRpc.GetString(args, "file_path");
                    if (!File.Exists(filePath))
                        return "Error: file not found: " + filePath;

                    var classes = _parser.ParseIncFile(filePath);
                    if (classes.Count == 0)
                        return "No CLASS definitions found in " + filePath;

                    var results = new List<Dictionary<string, object>>();
                    foreach (var cls in classes)
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "className", cls.ClassName },
                            { "parentClass", cls.ParentClass ?? "" },
                            { "moduleFile", cls.ModuleFile },
                            { "methodCount", cls.Methods.Count },
                            { "methods", cls.Methods.Select(m => new Dictionary<string, object>
                                {
                                    { "name", m.Name },
                                    { "signature", m.FullSignature },
                                    { "params", m.Params },
                                    { "returnType", m.ReturnType },
                                    { "attributes", m.Attributes },
                                    { "line", m.LineNumber }
                                }).ToList()
                            },
                            { "dataMembers", cls.DataMembers }
                        });
                    }
                    return results;
                }
            });

            Register(new McpTool
            {
                Name = "sync_check",
                Description = "Compare method declarations in a .inc file with implementations in the paired .clw file. Reports missing implementations and orphaned methods.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "inc_path", "Path to the .inc file" },
                        { "clw_path", "Path to the .clw file (optional — auto-detected from .inc if omitted)" },
                        { "class_name", "Specific class name to check (optional — checks first class if omitted)" }
                    },
                    new[] { "inc_path" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string incPath = McpJsonRpc.GetString(args, "inc_path");
                    string clwPath = McpJsonRpc.GetString(args, "clw_path");
                    string className = McpJsonRpc.GetString(args, "class_name");

                    if (!File.Exists(incPath))
                        return "Error: .inc file not found: " + incPath;

                    if (string.IsNullOrEmpty(clwPath))
                    {
                        var classes = _parser.ParseIncFile(incPath);
                        if (classes.Count > 0)
                            clwPath = _parser.ResolveClwPath(incPath, classes[0]);
                    }

                    if (string.IsNullOrEmpty(clwPath) || !File.Exists(clwPath))
                        return "Error: .clw file not found. Specify clw_path explicitly.";

                    var result = _parser.CompareIncWithClw(incPath, clwPath, className);

                    return new Dictionary<string, object>
                    {
                        { "className", result.ClassName },
                        { "isInSync", result.IsInSync },
                        { "implementedCount", result.ImplementedMethods.Count },
                        { "missingCount", result.MissingImplementations.Count },
                        { "orphanedCount", result.OrphanedImplementations.Count },
                        { "missing", result.MissingImplementations.Select(m => m.Name + " " + m.FullSignature).ToList() },
                        { "orphaned", result.OrphanedImplementations.Select(m => m.ClassName + "." + m.MethodName + " (line " + (m.LineNumber + 1) + ")").ToList() }
                    };
                }
            });

            Register(new McpTool
            {
                Name = "generate_stubs",
                Description = "Generate method implementation stubs for methods declared in .inc but missing from .clw. Returns the stub text (does NOT write to file — use write_file or append_to_file for that).",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "inc_path", "Path to the .inc file" },
                        { "clw_path", "Path to the .clw file (optional — auto-detected)" },
                        { "class_name", "Specific class name (optional)" }
                    },
                    new[] { "inc_path" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string incPath = McpJsonRpc.GetString(args, "inc_path");
                    string clwPath = McpJsonRpc.GetString(args, "clw_path");
                    string className = McpJsonRpc.GetString(args, "class_name");

                    if (!File.Exists(incPath))
                        return "Error: .inc file not found: " + incPath;

                    var classes = _parser.ParseIncFile(incPath);
                    if (classes.Count == 0)
                        return "Error: no CLASS found in " + incPath;

                    if (string.IsNullOrEmpty(clwPath))
                        clwPath = _parser.ResolveClwPath(incPath, classes[0]);

                    if (string.IsNullOrEmpty(clwPath))
                        return "Error: cannot resolve .clw path";

                    var syncResult = _parser.CompareIncWithClw(incPath, clwPath, className);
                    if (syncResult.MissingImplementations.Count == 0)
                        return "All methods are already implemented. Nothing to generate.";

                    string stubs = _parser.GenerateAllMissingStubs(syncResult);
                    return new Dictionary<string, object>
                    {
                        { "className", syncResult.ClassName },
                        { "missingCount", syncResult.MissingImplementations.Count },
                        { "clwPath", clwPath },
                        { "stubs", stubs }
                    };
                }
            });

            Register(new McpTool
            {
                Name = "generate_clw",
                Description = "Generate a complete .clw implementation file for a class defined in a .inc file. Returns the full file content with MEMBER, INCLUDE, MAP, and all method stubs.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "inc_path", "Path to the .inc file" },
                        { "class_name", "Specific class name (optional — uses first class if omitted)" }
                    },
                    new[] { "inc_path" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string incPath = McpJsonRpc.GetString(args, "inc_path");
                    string className = McpJsonRpc.GetString(args, "class_name");

                    if (!File.Exists(incPath))
                        return "Error: .inc file not found: " + incPath;

                    var classes = _parser.ParseIncFile(incPath);
                    if (classes.Count == 0)
                        return "Error: no CLASS found in " + incPath;

                    var classDef = string.IsNullOrEmpty(className)
                        ? classes[0]
                        : classes.FirstOrDefault(c => c.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase)) ?? classes[0];

                    string content = _parser.GenerateClwFile(classDef);
                    string suggestedPath = _parser.ResolveClwPath(incPath, classDef);

                    return new Dictionary<string, object>
                    {
                        { "className", classDef.ClassName },
                        { "suggestedPath", suggestedPath },
                        { "methodCount", classDef.Methods.Count },
                        { "content", content }
                    };
                }
            });

            // === CodeGraph Database Tools ===

            Register(new McpTool
            {
                Name = "query_codegraph",
                Description = @"Run a read-only SQL query against the Clarion CodeGraph database. The database indexes an entire Clarion solution with these tables:

TABLES:
- projects (id, name, guid, cwproj_path, output_type, sln_path)
- symbols (id, name, type, file_path, line_number, project_id, params, return_type, parent_name, member_of, scope, source_preview)
  - type values: 'procedure', 'function', 'class', 'interface', 'routine', 'variable', 'include'
  - scope values: 'global', 'local'
- relationships (id, from_id, to_id, type, file_path, line_number)
  - type values: 'calls', 'do', 'inherits', 'implements', 'references'
- project_dependencies (project_id, depends_on_id)
- index_metadata (key, value)

COMMON QUERIES:
- Find symbol: SELECT * FROM symbols WHERE name LIKE '%search%' AND type IN ('procedure','function','class')
- Who calls X: SELECT s.name, r.file_path, r.line_number FROM relationships r JOIN symbols s ON r.from_id = s.id WHERE r.to_id = (SELECT id FROM symbols WHERE name = 'X') AND r.type = 'calls'
- What does X call: SELECT s.name FROM relationships r JOIN symbols s ON r.to_id = s.id WHERE r.from_id = (SELECT id FROM symbols WHERE name = 'X') AND r.type = 'calls'
- Dead code: SELECT name, file_path FROM symbols WHERE type IN ('procedure','function') AND scope = 'global' AND id NOT IN (SELECT to_id FROM relationships WHERE type IN ('calls','do'))
- Class hierarchy: SELECT name, parent_name, file_path FROM symbols WHERE type = 'class'",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "sql", "SQL SELECT query to run (read-only)" },
                        { "db_path", "Path to .codegraph.db file (optional - auto-detected from open solution if omitted)" }
                    },
                    new[] { "sql" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string sql = McpJsonRpc.GetString(args, "sql");
                    string dbPath = McpJsonRpc.GetString(args, "db_path");

                    if (string.IsNullOrEmpty(sql))
                        return "Error: sql parameter is required";

                    // Safety: only allow SELECT queries
                    string trimmed = sql.TrimStart();
                    if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
                        return "Error: only SELECT/WITH/PRAGMA queries are allowed (read-only)";

                    // Find the database
                    if (string.IsNullOrEmpty(dbPath))
                        dbPath = FindCodeGraphDb();

                    if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                        return "Error: CodeGraph database not found. Specify db_path or ensure a .codegraph.db exists next to the open solution.";

                    return ExecuteCodeGraphQuery(dbPath, sql);
                }
            });

            Register(new McpTool
            {
                Name = "list_codegraph_databases",
                Description = "List available CodeGraph databases (.codegraph.db files) that have been indexed.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = false,
                Handler = args =>
                {
                    var results = new List<string>();
                    string[] searchRoots = { @"H:\Dev", @"H:\DevLaptop" };
                    foreach (string root in searchRoots)
                    {
                        if (Directory.Exists(root))
                        {
                            try
                            {
                                foreach (string file in Directory.GetFiles(root, "*.codegraph.db", SearchOption.AllDirectories))
                                    results.Add(file);
                            }
                            catch { }
                        }
                    }
                    string libDb = LibraryIndexer.GetDefaultDbPath();
                    if (File.Exists(libDb) && !results.Contains(libDb))
                        results.Insert(0, libDb);

                    if (results.Count == 0)
                        return "No CodeGraph databases found. Run the CodeGraph indexer on a Clarion solution first.";
                    return string.Join("\n", results);
                }
            });

            Register(new McpTool
            {
                Name = "get_solution_info",
                Description = "Get the currently selected Clarion solution, version/build, .red file path, and CodeGraph database status.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    if (_chatControl == null)
                        return "Error: chat control not initialized";

                    string slnPath = _chatControl.CurrentSolutionPath;
                    string dbPath = _chatControl.CurrentDbPath;
                    bool hasDb = !string.IsNullOrEmpty(dbPath) && File.Exists(dbPath);
                    var vConfig = _chatControl.CurrentVersionConfig;

                    var result = new Dictionary<string, object>
                    {
                        { "solutionPath", slnPath ?? "(none selected)" },
                        { "databasePath", dbPath ?? "(none)" },
                        { "isIndexed", hasDb },
                        { "lastIndexed", hasDb ? File.GetLastWriteTime(dbPath).ToString("yyyy-MM-dd HH:mm:ss") : "(never)" }
                    };

                    if (vConfig != null)
                    {
                        result["versionName"] = vConfig.Name ?? "";
                        result["clarionRoot"] = vConfig.RootPath ?? "";
                        result["binPath"] = vConfig.BinPath ?? "";
                        result["redFilePath"] = vConfig.RedFilePath ?? "";
                        result["redFileName"] = vConfig.RedFileName ?? "";
                        if (vConfig.Macros != null && vConfig.Macros.Count > 0)
                            result["macros"] = vConfig.Macros;
                    }

                    return result;
                }
            });

            Register(new McpTool
            {
                Name = "index_solution",
                Description = "Index or re-index the currently selected Clarion solution. Creates/updates the CodeGraph database for cross-project code intelligence.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "incremental", "Set to 'true' for incremental update (only changed files), 'false' for full re-index (default: false)" }
                    }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    if (_chatControl == null)
                        return "Error: chat control not initialized";

                    string incremental = McpJsonRpc.GetString(args, "incremental", "false");
                    bool isIncremental = incremental.Equals("true", StringComparison.OrdinalIgnoreCase);

                    _chatControl.RunIndex(isIncremental);
                    return isIncremental
                        ? "Incremental index started for: " + (_chatControl.CurrentSolutionPath ?? "(none)")
                        : "Full index started for: " + (_chatControl.CurrentSolutionPath ?? "(none)");
                }
            });

            // === LSP Tools ===

            Register(new McpTool
            {
                Name = "lsp_start",
                Description = "Start the Clarion Language Server for advanced code intelligence. Must be called before using other lsp_ tools. Provide the workspace folder path (the directory containing the .sln file).",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "workspace_path", "Path to the workspace folder (directory containing .sln file). Optional - auto-detected from current solution." }
                    }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string wsPath = McpJsonRpc.GetString(args, "workspace_path");
                    if (string.IsNullOrEmpty(wsPath) && _chatControl != null)
                    {
                        string slnPath = _chatControl.CurrentSolutionPath;
                        if (!string.IsNullOrEmpty(slnPath))
                            wsPath = Path.GetDirectoryName(slnPath);
                    }

                    if (string.IsNullOrEmpty(wsPath) || !Directory.Exists(wsPath))
                        return "Error: workspace_path required (directory containing .sln)";

                    string serverJs = @"H:\DevLaptop\ClarionLSP\out\server\src\server.js";
                    if (!File.Exists(serverJs))
                        return "Error: LSP server not found at " + serverJs;

                    if (_lspClient != null) _lspClient.Dispose();
                    _lspClient = new LspClient();

                    string wsUri = "file:///" + wsPath.Replace("\\", "/");
                    string wsName = Path.GetFileName(wsPath);

                    bool ok = _lspClient.Start(serverJs, wsUri, wsName);
                    return ok
                        ? "LSP server started for workspace: " + wsPath
                        : "Error: LSP server failed to start";
                }
            });

            Register(new McpTool
            {
                Name = "lsp_definition",
                Description = "Go to definition: find where a symbol is defined. Provide the file path and 0-based line/character position. Returns the definition location (file + line). Starts LSP automatically if needed.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "file_path", "Absolute path to the source file" },
                        { "line", "0-based line number" },
                        { "character", "0-based character offset in the line" }
                    },
                    new[] { "file_path", "line", "character" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    EnsureLspRunning();
                    if (_lspClient == null || !_lspClient.IsRunning)
                        return "Error: LSP not running. Call lsp_start first or set a solution.";

                    string filePath = McpJsonRpc.GetString(args, "file_path");
                    int line = McpJsonRpc.GetInt(args, "line");
                    int character = McpJsonRpc.GetInt(args, "character");

                    var result = _lspClient.GetDefinition(filePath, line, character);
                    return FormatLspResult(result);
                }
            });

            Register(new McpTool
            {
                Name = "lsp_references",
                Description = "Find all references to a symbol at the given position. Returns a list of locations (file + line) where the symbol is used.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "file_path", "Absolute path to the source file" },
                        { "line", "0-based line number" },
                        { "character", "0-based character offset in the line" }
                    },
                    new[] { "file_path", "line", "character" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    EnsureLspRunning();
                    if (_lspClient == null || !_lspClient.IsRunning)
                        return "Error: LSP not running.";

                    string filePath = McpJsonRpc.GetString(args, "file_path");
                    int line = McpJsonRpc.GetInt(args, "line");
                    int character = McpJsonRpc.GetInt(args, "character");

                    var result = _lspClient.GetReferences(filePath, line, character);
                    return FormatLspResult(result);
                }
            });

            Register(new McpTool
            {
                Name = "lsp_hover",
                Description = "Get hover information (type, signature, documentation) for a symbol at the given position.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "file_path", "Absolute path to the source file" },
                        { "line", "0-based line number" },
                        { "character", "0-based character offset in the line" }
                    },
                    new[] { "file_path", "line", "character" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    EnsureLspRunning();
                    if (_lspClient == null || !_lspClient.IsRunning)
                        return "Error: LSP not running.";

                    string filePath = McpJsonRpc.GetString(args, "file_path");
                    int line = McpJsonRpc.GetInt(args, "line");
                    int character = McpJsonRpc.GetInt(args, "character");

                    var result = _lspClient.GetHover(filePath, line, character);
                    return FormatLspResult(result);
                }
            });

            Register(new McpTool
            {
                Name = "lsp_document_symbols",
                Description = "Get all symbols (procedures, classes, variables) defined in a file. Returns name, type, and line number for each symbol.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "file_path", "Absolute path to the source file" }
                    },
                    new[] { "file_path" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    EnsureLspRunning();
                    if (_lspClient == null || !_lspClient.IsRunning)
                        return "Error: LSP not running.";

                    string filePath = McpJsonRpc.GetString(args, "file_path");
                    var result = _lspClient.GetDocumentSymbols(filePath);
                    return FormatLspResult(result);
                }
            });

            Register(new McpTool
            {
                Name = "lsp_find_symbol",
                Description = "Search for symbols across the entire workspace by name. Returns matching symbols with their file and line number.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "query", "Symbol name or partial name to search for" }
                    },
                    new[] { "query" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    EnsureLspRunning();
                    if (_lspClient == null || !_lspClient.IsRunning)
                        return "Error: LSP not running.";

                    string query = McpJsonRpc.GetString(args, "query");
                    var result = _lspClient.FindWorkspaceSymbol(query);
                    return FormatLspResult(result);
                }
            });
        }

        #region LSP Helpers

        private void EnsureLspRunning()
        {
            if (_lspClient != null && _lspClient.IsRunning) return;
            if (_chatControl == null) return;

            string slnPath = _chatControl.CurrentSolutionPath;
            if (string.IsNullOrEmpty(slnPath)) return;

            string wsPath = Path.GetDirectoryName(slnPath);
            string serverJs = @"H:\DevLaptop\ClarionLSP\out\server\src\server.js";
            if (!File.Exists(serverJs)) return;

            if (_lspClient != null) _lspClient.Dispose();
            _lspClient = new LspClient();

            string wsUri = "file:///" + wsPath.Replace("\\", "/");
            string wsName = Path.GetFileName(wsPath);
            _lspClient.Start(serverJs, wsUri, wsName);
        }

        private string FormatLspResult(Dictionary<string, object> response)
        {
            if (response == null) return "Error: no response from LSP (timeout)";

            if (response.ContainsKey("error"))
            {
                var error = response["error"];
                return "LSP Error: " + McpJsonRpc.Serialize(error);
            }

            if (response.ContainsKey("result"))
            {
                var result = response["result"];
                if (result == null) return "(no result)";
                return McpJsonRpc.Serialize(result);
            }

            return McpJsonRpc.Serialize(response);
        }

        #endregion

        #region CodeGraph Helpers

        private string FindCodeGraphDb()
        {
            // First: check the solution selected in the solution bar
            if (_chatControl != null)
            {
                string dbPath = _chatControl.CurrentDbPath;
                if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                    return dbPath;
            }

            // Fallback: find .codegraph.db near the currently open file
            try
            {
                string activePath = _editorService.GetActiveDocumentPath();
                if (!string.IsNullOrEmpty(activePath))
                {
                    string dir = Path.GetDirectoryName(activePath);
                    while (!string.IsNullOrEmpty(dir))
                    {
                        var dbFiles = Directory.GetFiles(dir, "*.codegraph.db");
                        if (dbFiles.Length > 0) return dbFiles[0];

                        // Check for .sln in this dir
                        var slnFiles = Directory.GetFiles(dir, "*.sln");
                        if (slnFiles.Length > 0)
                        {
                            string slnName = Path.GetFileNameWithoutExtension(slnFiles[0]);
                            string dbPath = Path.Combine(dir, slnName + ".codegraph.db");
                            if (File.Exists(dbPath)) return dbPath;
                        }

                        dir = Path.GetDirectoryName(dir);
                    }
                }
            }
            catch { }

            return null;
        }

        private object ExecuteCodeGraphQuery(string dbPath, string sql)
        {
            try
            {
                string connStr = "Data Source=" + dbPath + ";Version=3;Read Only=True;Journal Mode=WAL;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        using (var reader = cmd.ExecuteReader())
                        {
                            var sb = new StringBuilder();
                            int colCount = reader.FieldCount;

                            // Header
                            for (int i = 0; i < colCount; i++)
                            {
                                if (i > 0) sb.Append("\t");
                                sb.Append(reader.GetName(i));
                            }
                            sb.AppendLine();

                            // Rows
                            int rowCount = 0;
                            while (reader.Read() && rowCount < 500)
                            {
                                for (int i = 0; i < colCount; i++)
                                {
                                    if (i > 0) sb.Append("\t");
                                    sb.Append(reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString());
                                }
                                sb.AppendLine();
                                rowCount++;
                            }

                            if (rowCount == 0)
                                return "Query returned 0 rows.";

                            sb.AppendLine("(" + rowCount + " rows)");
                            return sb.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return "SQL Error: " + ex.Message;
            }
        }

        #endregion

        private void Register(McpTool tool)
        {
            _tools[tool.Name] = tool;
        }

        #endregion
    }
}
