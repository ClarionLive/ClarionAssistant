using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

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
        private DiffService _diffService;
        private DocGraphService _docGraph;
        private ClarionTraceService _traceService;
        private KnowledgeService _knowledgeService;
        private InstanceCoordinationService _instanceCoord;

        public McpToolRegistry(EditorService editorService, ClarionClassParser parser)
        {
            _editorService = editorService;
            _parser = parser;
            _appTree = new AppTreeService();
            _docGraph = new DocGraphService();
            _traceService = new ClarionTraceService();
            RegisterAllTools();
        }

        /// <summary>
        /// Set reference to chat control for solution context and indexing.
        /// </summary>
        public void SetChatControl(ClaudeChatControl control)
        {
            _chatControl = control;
        }

        public void SetDiffService(DiffService diffService)
        {
            _diffService = diffService;
        }

        public void SetKnowledgeService(KnowledgeService knowledgeService)
        {
            _knowledgeService = knowledgeService;
        }

        public void SetInstanceCoordination(InstanceCoordinationService instanceCoord)
        {
            _instanceCoord = instanceCoord;
        }

        public int GetToolCount() { return _tools.Count; }
        public AppTreeService GetAppTreeService() { return _appTree; }

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
                Name = "get_lines_range",
                Description = "Get text of multiple lines (1-based) from the active editor buffer in one call. Returns lines prefixed with line numbers. Much faster than calling get_line_text repeatedly.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "start_line", "First line to read (1-based)" },
                        { "end_line", "Last line to read (1-based, inclusive)" }
                    },
                    new[] { "start_line", "end_line" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    int startLine = McpJsonRpc.GetInt(args, "start_line", 1);
                    int endLine = McpJsonRpc.GetInt(args, "end_line", startLine);
                    string result = _editorService.GetLinesRange(startLine, endLine);
                    return result ?? "Error: could not read lines " + startLine + "-" + endLine;
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

            // open_app and close_app removed — opening/closing apps from the assistant
            // can break the IDE. The developer opens and closes apps manually.

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
                Description = "Open the embeditor for a specific procedure in the currently open Clarion app. The app must be loaded first. Automatically checks for conflicts with other IDE instances.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string> { { "procedure_name", "Name of the procedure to open" } },
                    new[] { "procedure_name" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string name = McpJsonRpc.GetString(args, "procedure_name");
                    if (string.IsNullOrEmpty(name)) return "Error: procedure_name required";

                    // Auto-check for conflicts with other IDE instances
                    if (_instanceCoord != null)
                    {
                        try
                        {
                            string appFile = null;
                            var appInfo = _appTree.GetAppInfo();
                            if (appInfo != null && appInfo.ContainsKey("fileName"))
                                appFile = appInfo["fileName"]?.ToString();

                            var conflict = _instanceCoord.CheckProcedureConflict(appFile, name);
                            if (conflict != null)
                            {
                                string warning = string.Format(
                                    "WARNING: Another IDE instance (PID {0}) has procedure '{1}' open in {2}. " +
                                    "Editing here may cause save conflicts. Proceeding anyway.",
                                    conflict.Pid, name, Path.GetFileName(conflict.AppFile ?? ""));
                                string result = _appTree.OpenProcedureEmbed(name);
                                return warning + "\n\n" + result;
                            }
                        }
                        catch { /* conflict check failed — proceed anyway */ }
                    }

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

            Register(new McpTool
            {
                Name = "open_embeditor_source",
                Description = "Open the module .clw source file for the procedure currently displayed in the embeditor. " +
                    "Parses the module filename from the embeditor header and opens it in the text editor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    try
                    {
                        var workbench = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench;
                        if (workbench == null)
                            return new Dictionary<string, object> { { "error", "No workbench" } };

                        var activeWindow = workbench.ActiveWorkbenchWindow;
                        if (activeWindow == null)
                            return new Dictionary<string, object> { { "error", "No active window" } };

                        var viewContent = activeWindow.ViewContent;
                        if (viewContent == null)
                            return new Dictionary<string, object> { { "error", "No view content" } };

                        // Get HeaderTitle: "ProcName - Embeditor - (module001.clw)"
                        var headerProp = viewContent.GetType().GetProperty("HeaderTitle",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        string headerTitle = headerProp?.GetValue(viewContent, null) as string;
                        if (string.IsNullOrEmpty(headerTitle))
                            return new Dictionary<string, object> { { "error", "Not in an embeditor window" } };

                        // Parse .clw filename from parentheses
                        var match = System.Text.RegularExpressions.Regex.Match(headerTitle, @"\(([^)]+\.clw)\)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!match.Success)
                            return new Dictionary<string, object> { { "error", "No module name found in: " + headerTitle } };

                        string clwFileName = match.Groups[1].Value;

                        // Get app directory from ViewContent.FileName
                        string appFilePath = viewContent.FileName;
                        if (string.IsNullOrEmpty(appFilePath))
                            return new Dictionary<string, object> { { "error", "Could not determine app file path" } };

                        string appDir = Path.GetDirectoryName(appFilePath);
                        string clwFullPath = Path.Combine(appDir, clwFileName);

                        // Search subdirectories if not in app dir (e.g. source\ subfolder)
                        if (!File.Exists(clwFullPath))
                        {
                            string[] found = Directory.GetFiles(appDir, clwFileName, SearchOption.AllDirectories);
                            if (found.Length > 0)
                                clwFullPath = found[0];
                            else
                                return new Dictionary<string, object> { { "error", "File not found: " + clwFileName + " in " + appDir } };
                        }

                        // Open in editor
                        _editorService.NavigateToFileAndLine(clwFullPath, 1);

                        return new Dictionary<string, object>
                        {
                            { "success", true },
                            { "file", clwFullPath },
                            { "module", clwFileName },
                            { "header", headerTitle }
                        };
                    }
                    catch (Exception ex)
                    {
                        return new Dictionary<string, object> { { "error", ex.Message } };
                    }
                }
            });

            Register(new McpTool
            {
                Name = "execute_command",
                Description = "Execute a registered SharpDevelop/Clarion IDE addin command by class name. " +
                    "Instantiates the command and calls Run(). Use to invoke toolbar buttons, menu commands, " +
                    "or any AbstractMenuCommand/AbstractCommand class loaded by an addin. " +
                    "Example: execute_command(class_name: 'OpenSourceButton.OpenSourceCommand')",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "class_name", "Fully qualified class name of the command to execute (e.g. 'OpenSourceButton.OpenSourceCommand')" }
                }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string className = args.ContainsKey("class_name") ? args["class_name"]?.ToString() : null;
                    if (string.IsNullOrEmpty(className))
                        return new Dictionary<string, object> { { "error", "class_name is required" } };

                    try
                    {
                        // Search all loaded assemblies for the command type
                        Type cmdType = null;
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                cmdType = asm.GetType(className, false);
                                if (cmdType != null) break;
                            }
                            catch { }
                        }

                        if (cmdType == null)
                            return new Dictionary<string, object> { { "error", "Class not found: " + className } };

                        var cmd = Activator.CreateInstance(cmdType);
                        var runMethod = cmdType.GetMethod("Run");
                        if (runMethod == null)
                            return new Dictionary<string, object> { { "error", "No Run() method on: " + className } };

                        runMethod.Invoke(cmd, null);
                        return new Dictionary<string, object>
                        {
                            { "success", true },
                            { "command", className }
                        };
                    }
                    catch (Exception ex)
                    {
                        return new Dictionary<string, object>
                        {
                            { "error", ex.InnerException != null ? ex.InnerException.Message : ex.Message },
                            { "command", className }
                        };
                    }
                }
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

            Register(new McpTool
            {
                Name = "list_embeds",
                Description = "List all embed sections in the active embeditor with their names and filled status. Use this to see what embed points are available before navigating.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var embeds = _appTree.ListEmbeds();
                    if (embeds == null) return "Error: No embeditor is currently open or no PWEE parts found.";
                    if (embeds.Count == 0) return "No embed sections found.";
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Embed sections (" + embeds.Count + "):");
                    foreach (var e in embeds)
                    {
                        string indent = new string(' ', (int)e["depth"] * 2);
                        string filled = (bool)e["filled"] ? " [FILLED]" : "";
                        sb.AppendLine(indent + "- " + e["name"] + filled);
                    }
                    return sb.ToString();
                }
            });

            Register(new McpTool
            {
                Name = "find_embed",
                Description = "Find an embed section by name and navigate the cursor there. Use a partial name like 'Local Proc' or 'Init' — matches case-insensitively.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "name", "Name or partial name of the embed section to find (e.g. 'Local Procedures', 'Init', 'Data')" }
                    },
                    new[] { "name" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string name = McpJsonRpc.GetString(args, "name");
                    if (string.IsNullOrEmpty(name)) return "Error: name is required";
                    return _appTree.FindEmbed(name, _editorService);
                }
            });

            // === TXA Export/Import Tools ===

            Register(new McpTool
            {
                Name = "export_txa",
                Description = "Export the ENTIRE current Clarion app to a TXA (Text Application) file. This always exports all procedures. To work with individual procedure code, use open_procedure_embed instead.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Absolute path for the output TXA file" }
                    },
                    new[] { "path" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string path = McpJsonRpc.GetString(args, "path");
                    if (string.IsNullOrEmpty(path))
                        return "Error: path is required";

                    return _appTree.ExportTxa(path);
                }
            });

            Register(new McpTool
            {
                Name = "import_txa",
                Description = "Import a TXA (Text Application) file into the currently open Clarion app. Use clash_mode to control what happens when procedure names conflict.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Absolute path to the TXA file to import" },
                        { "clash_mode", "How to handle name conflicts: 'rename' (default) auto-renames clashing procedures, 'replace' overwrites existing procedures" }
                    },
                    new[] { "path" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    string path = McpJsonRpc.GetString(args, "path");
                    if (string.IsNullOrEmpty(path))
                        return "Error: path is required";

                    string clashMode = McpJsonRpc.GetString(args, "clash_mode");
                    return _appTree.ImportTxa(path, clashMode);
                }
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

            // open_solution and close_solution removed — opening/closing solutions from
            // the assistant can break the IDE. The developer manages solutions manually.

            // open_dictionary removed — opening dictionaries from the assistant can
            // interfere with the IDE. The developer opens dictionaries manually.

            Register(new McpTool
            {
                Name = "export_dctx",
                Description = "Export the currently open Clarion data dictionary to a .dctx text file. The dictionary must be open in the IDE (use open_dictionary first). The .dctx format is a human-readable text representation of the dictionary.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Absolute path for the output .dctx file (optional — defaults to same folder as the .dct with .dctx extension)" }
                    }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var dct = FindOpenDictionary();
                    if (dct == null)
                        return "Error: no dictionary is open in the IDE. Use open_dictionary first.";

                    var fileNameProp = dct.GetType().GetProperty("FileName",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    string dctPath = fileNameProp != null ? fileNameProp.GetValue(dct, null) as string : null;
                    string outPath = McpJsonRpc.GetString(args, "path", "");
                    if (string.IsNullOrEmpty(outPath))
                    {
                        if (string.IsNullOrEmpty(dctPath))
                            return "Error: could not determine dictionary path and no output path provided";
                        outPath = Path.ChangeExtension(dctPath, ".dctx");
                    }

                    // Call ExportToText(string destination, bool quiet, out string errorMessage)
                    var method = dct.GetType().GetMethod("ExportToText",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (method == null)
                        return "Error: ExportToText method not found on dictionary object";

                    var methodParams = new object[] { outPath, true, null };
                    bool success = (bool)method.Invoke(dct, methodParams);
                    string errorMsg = methodParams[2] as string;

                    if (success)
                        return "Dictionary exported to: " + outPath;
                    else
                        return "Export failed: " + (errorMsg ?? "unknown error");
                }
            });

            Register(new McpTool
            {
                Name = "import_dctx",
                Description = "Import a .dctx text file into the currently open Clarion data dictionary. The dictionary must be open in the IDE (use open_dictionary first). WARNING: This modifies the dictionary — changes must be saved manually.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "path", "Absolute path to the .dctx file to import" }
                    },
                    new[] { "path" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    var dct = FindOpenDictionary();
                    if (dct == null)
                        return "Error: no dictionary is open in the IDE. Use open_dictionary first.";

                    string importPath = McpJsonRpc.GetString(args, "path");
                    if (string.IsNullOrEmpty(importPath))
                        return "Error: path is required";
                    if (!File.Exists(importPath))
                        return "Error: file not found: " + importPath;

                    // Call ImportFromText(string textFile, out string errorMessage)
                    var method = dct.GetType().GetMethod("ImportFromText",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (method == null)
                        return "Error: ImportFromText method not found on dictionary object";

                    var methodParams = new object[] { importPath, null };
                    bool success = (bool)method.Invoke(dct, methodParams);
                    string errorMsg = methodParams[1] as string;

                    if (success)
                        return "Dictionary imported from: " + importPath + ". Review changes in the Dictionary Editor and save when ready.";
                    else
                        return "Import failed: " + (errorMsg ?? "unknown error");
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
                Name = "index_codegraph",
                Description = "Index a Clarion solution into a CodeGraph database. Parses all .clw/.inc files and builds a symbol/relationship graph for cross-file queries, impact analysis, and dead code detection. Run this when you first open a solution or after code changes.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "sln_path?", "Path to .sln file (auto-detected from current solution if omitted)" },
                    { "incremental?", "Only re-index changed projects (default: true)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string slnPath = McpJsonRpc.GetString(args, "sln_path");
                    if (string.IsNullOrEmpty(slnPath) && _chatControl != null)
                        slnPath = _chatControl.CurrentSolutionPath;
                    if (string.IsNullOrEmpty(slnPath))
                        return "Error: no solution path provided and no solution is open in the IDE.";
                    if (!File.Exists(slnPath))
                        return "Error: solution file not found: " + slnPath;

                    bool incremental = McpJsonRpc.GetString(args, "incremental") != "false";

                    string dbPath = Path.Combine(
                        Path.GetDirectoryName(slnPath),
                        Path.GetFileNameWithoutExtension(slnPath) + ".codegraph.db");

                    try
                    {
                        var db = new ClarionCodeGraph.Graph.CodeGraphDatabase();
                        db.Open(dbPath);

                        var indexer = new ClarionCodeGraph.Graph.CodeGraphIndexer(db);
                        var progress = new System.Text.StringBuilder();
                        indexer.OnProgress += msg => progress.AppendLine(msg);

                        var result = indexer.IndexSolution(slnPath, incremental);
                        db.Close();

                        return string.Format(
                            "CodeGraph indexed successfully:\n" +
                            "  Solution: {0}\n" +
                            "  Projects: {1}\n" +
                            "  Files: {2}\n" +
                            "  Symbols: {3}\n" +
                            "  Duration: {4}ms\n" +
                            "  Database: {5}\n" +
                            "  Mode: {6}",
                            Path.GetFileName(slnPath), result.ProjectCount, result.FileCount,
                            result.SymbolCount, result.DurationMs, dbPath,
                            incremental ? "incremental" : "full");
                    }
                    catch (Exception ex)
                    {
                        return "Error indexing solution: " + ex.Message;
                    }
                }
            });

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

                    // Check the live IDE state first — the cached path may be stale
                    string ideSlnPath = EditorService.GetOpenSolutionPath();
                    string slnPath = !string.IsNullOrEmpty(ideSlnPath) ? ideSlnPath : _chatControl.CurrentSolutionPath;

                    // If nothing is open in the IDE and we only have a cached path, report no solution
                    if (string.IsNullOrEmpty(ideSlnPath) && !string.IsNullOrEmpty(slnPath))
                    {
                        return new Dictionary<string, object>
                        {
                            { "solutionPath", "(none open)" },
                            { "lastSelectedPath", slnPath },
                            { "isIndexed", false },
                            { "lastIndexed", "(never)" },
                            { "note", "No solution is currently open in the IDE. The lastSelectedPath was the most recently selected solution." }
                        };
                    }

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

                    var red = _chatControl.RedFile;
                    if (red != null && red.RedFilePath != null)
                    {
                        result["activeRedFile"] = red.RedFilePath;
                        result["redSections"] = red.Sections.Keys.ToArray();
                        // Include CLW and INC search paths so the AI knows where classes live
                        result["clwSearchPaths"] = red.GetSearchPaths(".clw");
                        result["incSearchPaths"] = red.GetSearchPaths(".inc");
                    }

                    return result;
                }
            });

            Register(new McpTool
            {
                Name = "resolve_red_path",
                Description = "Resolve a Clarion filename (e.g. 'MyClass.inc', 'MyClass.clw') to its full path using the active .red (redirection) file. Searches Common section by default. Returns the first existing file match.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "filename", "The filename to resolve (e.g. 'MyClass.inc', 'StringClass.clw')" },
                        { "section", "Red file section to search (default: 'Common'). Other options: 'Debug32', 'Release32', 'Copy'" }
                    },
                    new[] { "filename" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    if (_chatControl == null)
                        return "Error: chat control not initialized";

                    var red = _chatControl.RedFile;
                    if (red == null || red.RedFilePath == null)
                        return "Error: no .red file loaded. Select a version and solution first.";

                    string fileName = McpJsonRpc.GetString(args, "filename", "");
                    string section = McpJsonRpc.GetString(args, "section", "Common");

                    if (string.IsNullOrEmpty(fileName))
                        return "Error: filename is required";

                    string resolved = red.Resolve(fileName, section);
                    if (resolved != null)
                        return new Dictionary<string, object>
                        {
                            { "filename", fileName },
                            { "resolvedPath", resolved },
                            { "found", true }
                        };

                    // Not found - return the search paths so the user knows where we looked
                    string ext = System.IO.Path.GetExtension(fileName);
                    var searchPaths = red.GetSearchPaths(ext, section);
                    return new Dictionary<string, object>
                    {
                        { "filename", fileName },
                        { "found", false },
                        { "searchedPaths", searchPaths }
                    };
                }
            });

            Register(new McpTool
            {
                Name = "get_red_search_paths",
                Description = "Get all search directories for a file extension from the .red file. Useful for discovering where Clarion source files, includes, and libraries are located.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "extension", "File extension to look up (e.g. 'clw', 'inc', 'lib', 'dll')" },
                        { "section", "Red file section (default: 'Common')" }
                    },
                    new[] { "extension" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    if (_chatControl == null)
                        return "Error: chat control not initialized";

                    var red = _chatControl.RedFile;
                    if (red == null || red.RedFilePath == null)
                        return "Error: no .red file loaded. Select a version and solution first.";

                    string ext = McpJsonRpc.GetString(args, "extension", "");
                    string section = McpJsonRpc.GetString(args, "section", "Common");

                    if (string.IsNullOrEmpty(ext))
                        return "Error: extension is required";

                    return new Dictionary<string, object>
                    {
                        { "extension", ext },
                        { "section", section },
                        { "searchPaths", red.GetSearchPaths(ext, section) },
                        { "redFile", red.RedFilePath }
                    };
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

                    string serverJs = ResolveLspServerPath();
                    if (serverJs == null)
                        return "Error: LSP server not found. Place server.js in the lsp-server subfolder next to the addin DLL, or set the 'Lsp.ServerPath' setting to the full path.";

                    if (_lspClient != null) _lspClient.Dispose();
                    _lspClient = new LspClient();

                    string wsUri = "file:///" + wsPath.Replace("\\", "/");
                    string wsName = Path.GetFileName(wsPath);

                    bool ok = _lspClient.Start(serverJs, wsUri, wsName);
                    if (ok)
                        return "LSP server started for workspace: " + wsPath;

                    // Provide diagnostic info on failure
                    string lspRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(serverJs), "..", "..", ".."));
                    string nodeExe = Path.Combine(lspRoot, "node.exe");
                    string diag = "Error: LSP server failed to start.\n"
                        + "  server.js: " + serverJs + " (exists: " + File.Exists(serverJs) + ")\n"
                        + "  node.exe: " + nodeExe + " (exists: " + File.Exists(nodeExe) + ")\n"
                        + "  workspace: " + wsUri + "\n"
                        + "  Check the IDE Debug Output for [LSP] messages for more detail.";
                    return diag;
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

            // === Diff Viewer Tools ===
            Register(new McpTool
            {
                Name = "show_diff",
                Description = "Open a unified diff viewer in the IDE editor panel. Shows color-coded additions/removals with a changes sidebar and inline code review notes. " +
                    "The developer can add severity-tagged notes (BLOCKER/SUGGESTION/NITPICK/QUESTION) on any line. " +
                    "You can provide text directly via original_text/modified_text, OR provide file paths via original_file/modified_file to load from disk (avoids encoding issues with large files). " +
                    "Use ignore_whitespace to suppress trivial whitespace-only differences. Use get_diff_result to check the outcome.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "title", "Title for the diff tab (e.g. procedure name or file name)" },
                        { "original_text", "The original (before) text. Not needed if original_file is provided." },
                        { "modified_text", "The modified (after) text. Not needed if modified_file is provided." },
                        { "original_file", "Path to a file to load as the original (left) side. Overrides original_text." },
                        { "modified_file", "Path to a file to load as the modified (right) side. Overrides modified_text. Preferred for large files." },
                        { "original_start_line", "First line to include from original_file (1-based, default: 1)" },
                        { "original_end_line", "Last line to include from original_file (1-based, default: end of file)" },
                        { "modified_start_line", "First line to include from modified_file (1-based, default: 1)" },
                        { "modified_end_line", "Last line to include from modified_file (1-based, default: end of file)" },
                        { "ignore_whitespace", "Set to 'true' to ignore leading/trailing whitespace differences (default: false)" },
                        { "language", "Syntax highlighting language (default: clarion). Options: clarion, csharp, javascript, html, css, xml, json, plaintext" }
                    },
                    new[] { "title" }),
                RequiresUiThread = true,
                Handler = args =>
                {
                    if (_diffService == null)
                        return "Error: Diff service not available.";

                    string title = McpJsonRpc.GetString(args, "title");
                    string language = McpJsonRpc.GetString(args, "language") ?? "clarion";
                    bool ignoreWs = McpJsonRpc.GetString(args, "ignore_whitespace") == "true";

                    string originalFile = McpJsonRpc.GetString(args, "original_file");
                    string modifiedFile = McpJsonRpc.GetString(args, "modified_file");

                    // Both files provided — load both from disk (best path, avoids MCP text encoding issues)
                    if (!string.IsNullOrEmpty(originalFile) && !string.IsNullOrEmpty(modifiedFile))
                    {
                        int origStart = McpJsonRpc.GetInt(args, "original_start_line", 1);
                        int origEnd = McpJsonRpc.GetInt(args, "original_end_line", -1);
                        int modStart = McpJsonRpc.GetInt(args, "modified_start_line", 1);
                        int modEnd = McpJsonRpc.GetInt(args, "modified_end_line", -1);
                        return _diffService.ShowDiffFromFiles(title, originalFile, origStart, origEnd,
                            modifiedFile, modStart, modEnd, language, ignoreWs);
                    }

                    // Original from file, modified from text parameter
                    if (!string.IsNullOrEmpty(originalFile))
                    {
                        string modified = McpJsonRpc.GetString(args, "modified_text") ?? "";
                        int startLine = McpJsonRpc.GetInt(args, "original_start_line", 1);
                        int endLine = McpJsonRpc.GetInt(args, "original_end_line", -1);
                        return _diffService.ShowDiffFromFile(title, originalFile, startLine, endLine, modified, language, ignoreWs);
                    }

                    // Both from text parameters
                    string original = McpJsonRpc.GetString(args, "original_text") ?? "";
                    string modifiedText = McpJsonRpc.GetString(args, "modified_text") ?? "";
                    return _diffService.ShowDiff(title, original, modifiedText, language, ignoreWs);
                }
            });

            Register(new McpTool
            {
                Name = "get_diff_result",
                Description = "Check the result of the diff viewer. Returns: 'pending' if the developer hasn't acted yet, " +
                    "'approved' with the modified text if they clicked Approve, " +
                    "'notes' with an array of review notes [{line, lineContent, severity, comment}] if they submitted feedback, " +
                    "or 'cancelled' if they dismissed it.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>(),
                    new string[0]),
                RequiresUiThread = false,
                Handler = args =>
                {
                    if (_diffService == null)
                        return "Error: Diff service not available.";

                    return _diffService.GetResult();
                }
            });

            // === DocGraph Tools ===

            Register(new McpTool
            {
                Name = "query_docs",
                Description = @"Search third-party Clarion template documentation using full-text search.
Returns matching documentation chunks ranked by relevance, including method signatures, descriptions, parameters, and code examples.
Covers CapeSoft (StringTheory, NetTalk, FM3, etc.), Icetips, Noyantis, LANSRAD, Super templates, and other vendors.

EXAMPLES:
- query_docs(query='parse CSV') → finds StringTheory CSV parsing docs
- query_docs(query='Split', library='StringTheory') → StringTheory.Split method docs
- query_docs(query='email send', library='NetTalk') → NetTalk email sending docs
- query_docs(query='encryption', class_name='StringTheory') → all encryption-related methods",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "query", "Search text — method names, topics, or natural language questions" },
                        { "library", "Filter to a specific library name (e.g. 'StringTheory', 'NetTalk', 'fm3'). Optional." },
                        { "class_name", "Filter to a specific class name. Optional." },
                        { "limit", "Max results to return (default 10). Optional." }
                    },
                    new[] { "query" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string query = McpJsonRpc.GetString(args, "query");
                    string library = McpJsonRpc.GetString(args, "library");
                    string className = McpJsonRpc.GetString(args, "class_name");
                    int limit = 10;
                    object limitObj;
                    if (args.TryGetValue("limit", out limitObj) && limitObj != null)
                    {
                        int.TryParse(limitObj.ToString(), out limit);
                        if (limit <= 0) limit = 10;
                    }

                    string personalPath = DocGraphService.GetPersonalDbPath();
                    return _docGraph.QueryDocsMulti(personalPath, query, library, className, limit);
                }
            });

            Register(new McpTool
            {
                Name = "ingest_docs",
                Description = @"Ingest documentation files (HTM, HTML, CHM, PDF) into the DocGraph search database.
Accepts ANY folder path — scans it recursively for doc files and ingests everything found.
Also works with a Clarion installation root (auto-discovers docs/, bin/, accessory/Documents/).
If no path is given, auto-detects the Clarion installation.
Optional vendor parameter sets the vendor name (defaults to the folder name).",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "clarion_root", "Path to any folder containing doc files, or a Clarion installation root. Optional — auto-detects if omitted." },
                        { "vendor", "Vendor name for the ingested docs. Optional — defaults to the folder name." }
                    }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string input = McpJsonRpc.GetString(args, "clarion_root");
                    string vendor = McpJsonRpc.GetString(args, "vendor");

                    // If the path itself is a Clarion root, ingest everything
                    if (!string.IsNullOrEmpty(input) && DocGraphService.IsClarionRoot(input))
                        return _docGraph.IngestAll(input);

                    // If no path given, try auto-detect
                    if (string.IsNullOrEmpty(input))
                    {
                        string root = DocGraphService.ResolveClarionRoot(null);
                        if (root != null)
                            return _docGraph.IngestAll(root);
                    }

                    // Treat as a plain folder and scan it directly
                    if (!string.IsNullOrEmpty(input) && Directory.Exists(input))
                        return _docGraph.IngestFolder(input, vendor);

                    return string.IsNullOrEmpty(input)
                        ? "Error: Could not auto-detect a Clarion installation. Pass a folder path explicitly."
                        : "Error: Folder not found: " + input;
                }
            });

            Register(new McpTool
            {
                Name = "ingest_web_docs",
                Description = @"Ingest documentation from a web URL into the DocGraph search database.
Fetches the start page, discovers all linked HTM pages in the same directory, downloads and parses them.
Works great for CapeSoft online docs — just point it at the index page.

EXAMPLES:
- ingest_web_docs(url='https://capesoft.com/docs/NetTalk14/nettalkindex.htm') → ingests all NetTalk docs
- ingest_web_docs(url='https://capesoft.com/accessories/netsp.htm') → ingests from product page
- ingest_web_docs(url='...', vendor='Capesoft', library='FM3') → explicit vendor/library naming",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "url", "URL to the documentation index page. All linked HTM pages in the same directory will be fetched." },
                        { "vendor", "Vendor name (e.g. 'Capesoft'). Optional — auto-detected from the domain." },
                        { "library", "Library name (e.g. 'NetTalk'). Optional — auto-detected from the URL path." }
                    },
                    new[] { "url" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string url = McpJsonRpc.GetString(args, "url");
                    string vendor = McpJsonRpc.GetString(args, "vendor");
                    string library = McpJsonRpc.GetString(args, "library");

                    if (string.IsNullOrEmpty(url))
                        return "Error: url parameter is required.";

                    return _docGraph.IngestFromWeb(url, vendor, library);
                }
            });

            Register(new McpTool
            {
                Name = "list_doc_libraries",
                Description = "List all third-party libraries that have been ingested into the DocGraph documentation database, with chunk counts.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = false,
                Handler = args => _docGraph.ListLibraries()
            });

            Register(new McpTool
            {
                Name = "discover_docs",
                Description = "Preview what documentation sources would be ingested from a Clarion installation. Lists all discoverable HTM, CHM, and PDF files without ingesting them. The clarion_root parameter is optional — auto-detects if omitted.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "clarion_root", "Path to Clarion installation root or any subfolder. Optional — auto-detects if omitted." }
                    }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string input = McpJsonRpc.GetString(args, "clarion_root");
                    string root = DocGraphService.ResolveClarionRoot(input);
                    if (root == null)
                        return string.IsNullOrEmpty(input)
                            ? "Error: Could not auto-detect a Clarion installation. Pass clarion_root explicitly."
                            : "Error: Could not find a Clarion installation at or above: " + input;

                    var sources = _docGraph.DiscoverDocSources(root);
                    if (sources.Count == 0)
                        return "No documentation sources found in " + root;

                    var sb = new StringBuilder();
                    sb.AppendLine(string.Format("Found {0} documentation sources in {1}:\n", sources.Count, root));
                    sb.AppendLine("Vendor\tLibrary\tFormat\tPath");
                    foreach (var s in sources)
                        sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}", s.Vendor, s.Library, s.Format, s.FilePath));
                    return sb.ToString();
                }
            });

            Register(new McpTool
            {
                Name = "docgraph_stats",
                Description = "Get statistics about the DocGraph documentation database — library count, chunk count, breakdown by topic and vendor.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = false,
                Handler = args => _docGraph.GetStats()
            });

            // === Build Tools ===

            Register(new McpTool
            {
                Name = "build_solution",
                Description = "Build the entire loaded Clarion solution using ClarionCL.exe. Returns build output with errors and warnings. Use build_app instead for multi-DLL solutions when you only need to rebuild one target.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "solution_path", "Path to .sln file. Defaults to the currently loaded solution." },
                        { "timeout", "Timeout in seconds (default: 120)" }
                    }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string slnPath = McpJsonRpc.GetString(args, "solution_path", null);
                    int timeout = McpJsonRpc.GetInt(args, "timeout", 120);

                    if (string.IsNullOrEmpty(slnPath))
                        slnPath = EditorService.GetOpenSolutionPath();

                    if (string.IsNullOrEmpty(slnPath) || !File.Exists(slnPath))
                        return "Error: No solution path provided and no solution is currently loaded in the IDE.";

                    string clarionRoot = EditorService.GetClarionInstallPath();
                    if (string.IsNullOrEmpty(clarionRoot))
                        return "Error: Could not detect Clarion installation path.";

                    string clarionCl = Path.Combine(clarionRoot, "bin", "ClarionCL.exe");
                    if (!File.Exists(clarionCl))
                        return "Error: ClarionCL.exe not found at " + clarionCl;

                    string arguments = "/ag \"" + slnPath + "\"";
                    return RunBuildProcess(clarionCl, arguments, Path.GetDirectoryName(slnPath), timeout, "build_solution", slnPath);
                }
            });

            Register(new McpTool
            {
                Name = "build_app",
                Description = "Build a single Clarion .app file using ClarionCL.exe. Ideal for multi-DLL solutions where you only need to rebuild one target. Defaults to the currently active app in the IDE.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "app_path", "Path to .app file. Defaults to the currently active app in the IDE." },
                        { "timeout", "Timeout in seconds (default: 120)" }
                    }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string appPath = McpJsonRpc.GetString(args, "app_path", null);
                    int timeout = McpJsonRpc.GetInt(args, "timeout", 120);

                    if (string.IsNullOrEmpty(appPath))
                    {
                        var appInfo = _appTree.GetAppInfo();
                        if (appInfo != null && appInfo.ContainsKey("fileName"))
                            appPath = appInfo["fileName"]?.ToString();
                    }

                    if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath))
                        return "Error: No app path provided and no .app file is currently open in the IDE.";

                    string clarionRoot = EditorService.GetClarionInstallPath();
                    if (string.IsNullOrEmpty(clarionRoot))
                        return "Error: Could not detect Clarion installation path.";

                    string clarionCl = Path.Combine(clarionRoot, "bin", "ClarionCL.exe");
                    if (!File.Exists(clarionCl))
                        return "Error: ClarionCL.exe not found at " + clarionCl;

                    string arguments = "/ag \"" + appPath + "\"";
                    return RunBuildProcess(clarionCl, arguments, Path.GetDirectoryName(appPath), timeout, "build_app", appPath);
                }
            });

            Register(new McpTool
            {
                Name = "generate_source",
                Description = "Generate Clarion source code (.clw/.inc files) from an .app file using ClarionCL.exe without a full build. Runs template code generation to produce the source files. Defaults to the currently active app.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "app_path", "Path to .app file. Defaults to the currently active app in the IDE." },
                        { "conditional_generation", "on/off — toggle conditional code generation (default: use app setting)" },
                        { "debug_generation", "on/off — toggle #DEBUG generation (default: use app setting)" },
                        { "timeout", "Timeout in seconds (default: 120)" }
                    }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string appPath = McpJsonRpc.GetString(args, "app_path", null);
                    string condGen = McpJsonRpc.GetString(args, "conditional_generation", null);
                    string debugGen = McpJsonRpc.GetString(args, "debug_generation", null);
                    int timeout = McpJsonRpc.GetInt(args, "timeout", 120);

                    if (string.IsNullOrEmpty(appPath))
                    {
                        var appInfo = _appTree.GetAppInfo();
                        if (appInfo != null && appInfo.ContainsKey("fileName"))
                            appPath = appInfo["fileName"]?.ToString();
                    }

                    if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath))
                        return "Error: No app path provided and no .app file is currently open in the IDE.";

                    string clarionRoot = EditorService.GetClarionInstallPath();
                    if (string.IsNullOrEmpty(clarionRoot))
                        return "Error: Could not detect Clarion installation path.";

                    string clarionCl = Path.Combine(clarionRoot, "bin", "ClarionCL.exe");
                    if (!File.Exists(clarionCl))
                        return "Error: ClarionCL.exe not found at " + clarionCl;

                    var argBuilder = new StringBuilder();
                    argBuilder.Append("/ag \"" + appPath + "\"");
                    if (!string.IsNullOrEmpty(condGen))
                        argBuilder.Append(" /agc " + condGen);
                    if (!string.IsNullOrEmpty(debugGen))
                        argBuilder.Append(" /agd " + debugGen);

                    return RunBuildProcess(clarionCl, argBuilder.ToString(), Path.GetDirectoryName(appPath), timeout, "generate_source", appPath);
                }
            });

            Register(new McpTool
            {
                Name = "build_com_project",
                Description = "Build a C# COM control project (.csproj) using MSBuild. Auto-detects VS2022 MSBuild. Use for building Clarion COM controls written in C#.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "project_path", "Path to .csproj file (required)" },
                        { "configuration", "Build configuration: Debug or Release (default: Debug)" },
                        { "timeout", "Timeout in seconds (default: 120)" }
                    },
                    new[] { "project_path" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string projectPath = McpJsonRpc.GetString(args, "project_path");
                    string config = McpJsonRpc.GetString(args, "configuration", "Debug");
                    int timeout = McpJsonRpc.GetInt(args, "timeout", 120);

                    if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath))
                        return "Error: Project file not found: " + (projectPath ?? "(null)");

                    string msbuild = FindMSBuild();
                    if (msbuild == null)
                        return "Error: MSBuild.exe not found. Searched VS2022, VS2019, and BuildTools paths.";

                    string arguments = string.Format("\"{0}\" /p:Configuration={1} /p:Platform=x86 /t:Build /v:minimal /nologo", projectPath, config);
                    return RunBuildProcess(msbuild, arguments, Path.GetDirectoryName(projectPath), timeout, "build_com_project", projectPath);
                }
            });

            Register(new McpTool
            {
                Name = "run_command",
                Description = "Execute a command-line process and capture its output. Use for general-purpose build tasks, scripts, or tools not covered by the dedicated build tools.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "command", "The executable to run (required)" },
                        { "arguments", "Command-line arguments" },
                        { "working_directory", "Working directory for the process" },
                        { "timeout", "Timeout in seconds (default: 60)" }
                    },
                    new[] { "command" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string command = McpJsonRpc.GetString(args, "command");
                    string arguments = McpJsonRpc.GetString(args, "arguments", "");
                    string workDir = McpJsonRpc.GetString(args, "working_directory", "");
                    int timeout = McpJsonRpc.GetInt(args, "timeout", 60);

                    if (string.IsNullOrEmpty(command))
                        return "Error: command is required";

                    return RunBuildProcess(command, arguments, workDir, timeout);
                }
            });

            // === Trace Tools (Recursive Self-Improvement) ===

            Register(new McpTool
            {
                Name = "query_traces",
                Description = "Query the Clarion code generation trace database. Use SQL to analyze build failures, find recurring error patterns, and identify areas where code generation needs improvement. Table: clarion_traces (id, timestamp, trace_type, target_file, code_snippet, build_result, error_count, warning_count, errors, tool_name, agent).",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "sql", "SQL query to run against the clarion_traces table (required)" }
                    },
                    new[] { "sql" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string sql = McpJsonRpc.GetString(args, "sql");
                    if (string.IsNullOrEmpty(sql))
                        return "Error: sql is required";
                    return _traceService.QueryTraces(sql);
                }
            });

            Register(new McpTool
            {
                Name = "trace_stats",
                Description = "Get summary statistics from the Clarion code generation trace database — total traces, build failures, error counts by type.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                RequiresUiThread = false,
                Handler = args => _traceService.GetStats()
            });

            Register(new McpTool
            {
                Name = "log_skill_update",
                Description = "Log a modification to the /clarion skill for changelog tracking. Call this after adding or modifying a pattern in the skill file. Records what was changed and why, enabling rollback analysis.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "pattern_name", "Short name of the pattern added/modified (required)" },
                        { "action", "What was done: 'added', 'strengthened', 'removed', 'modified' (required)" },
                        { "reason", "Why this change was made — reference trace evidence (required)" },
                        { "occurrence_count", "How many times this pattern was seen in traces" }
                    },
                    new[] { "pattern_name", "action", "reason" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string patternName = McpJsonRpc.GetString(args, "pattern_name");
                    string action = McpJsonRpc.GetString(args, "action");
                    string reason = McpJsonRpc.GetString(args, "reason");
                    int count = McpJsonRpc.GetInt(args, "occurrence_count", 0);

                    string entry = string.Format("[{0}] {1}: {2} (seen {3}x) — {4}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm"), action, patternName, count, reason);

                    _traceService.LogCodeGeneration("skill_update", "clarion/SKILL.md", entry);
                    return "Logged skill update: " + entry;
                }
            });

            // === Knowledge & Memory Tools ===

            Register(new McpTool
            {
                Name = "add_knowledge",
                Description = "Save a reusable insight to the Clarion Assistant's knowledge base. Categories: decision, pattern, gotcha, anti_pattern, debug_insight, preference. Knowledge persists across sessions and is auto-injected at startup ranked by usage.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "title", "Short title summarizing the knowledge entry (required)" },
                        { "content", "Full content of the knowledge entry (required)" },
                        { "category", "Category: decision, pattern, gotcha, anti_pattern, debug_insight, preference (required)" },
                        { "tags", "Comma-separated tags for categorization (optional)" },
                        { "confidence", "Confidence level: confirmed, likely, uncertain (default: confirmed)" }
                    },
                    new[] { "title", "content", "category" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    if (_knowledgeService == null) return "Knowledge service not initialized.";
                    string title = McpJsonRpc.GetString(args, "title");
                    string content = McpJsonRpc.GetString(args, "content");
                    string category = McpJsonRpc.GetString(args, "category");
                    string tags = McpJsonRpc.GetString(args, "tags");
                    string confidence = McpJsonRpc.GetString(args, "confidence") ?? "confirmed";

                    int id = _knowledgeService.AddEntry(title, content, category, tags, confidence);
                    return string.Format("Knowledge entry saved (id: {0}): [{1}] {2}", id, category, title);
                }
            });

            Register(new McpTool
            {
                Name = "query_knowledge",
                Description = "Search the Clarion Assistant's knowledge base for decisions, patterns, gotchas, and insights. Uses full-text search. Returns matching entries ranked by relevance.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "query", "Search text to find within knowledge entries (required)" },
                        { "limit", "Max results to return (default: 20)" }
                    },
                    new[] { "query" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    if (_knowledgeService == null) return "Knowledge service not initialized.";
                    string query = McpJsonRpc.GetString(args, "query");
                    int limit = McpJsonRpc.GetInt(args, "limit", 20);

                    var results = _knowledgeService.Search(query, limit);
                    if (results.Count == 0)
                        return string.Format("No knowledge entries found matching \"{0}\".", query);

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine(string.Format("Knowledge Search: \"{0}\" — {1} result(s)", query, results.Count));
                    sb.AppendLine();
                    foreach (var e in results)
                    {
                        string preview = e.Content != null && e.Content.Length > 300
                            ? e.Content.Substring(0, 300) + "..." : e.Content ?? "";
                        sb.AppendLine(string.Format("- [{0}] {1}", e.Category, e.Title));
                        sb.AppendLine("  " + preview);
                        if (e.Tags != null) sb.AppendLine("  Tags: " + e.Tags);
                        sb.AppendLine();
                    }
                    return sb.ToString().TrimEnd();
                }
            });

            Register(new McpTool
            {
                Name = "save_session_summary",
                Description = "Save a summary of the current session's work for continuity. This summary will be injected at the start of the next session so you can pick up where you left off. Call this before the session ends.",
                InputSchema = McpJsonRpc.BuildSchema(
                    new Dictionary<string, string>
                    {
                        { "summary", "Concise summary of what was accomplished, current state, and what to do next (required)" }
                    },
                    new[] { "summary" }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    if (_knowledgeService == null) return "Knowledge service not initialized.";
                    string summary = McpJsonRpc.GetString(args, "summary");
                    if (string.IsNullOrWhiteSpace(summary)) return "Summary cannot be empty.";

                    // Save as a new session with summary
                    int sessionId = _knowledgeService.StartSession(null);
                    _knowledgeService.EndSession(sessionId, summary);
                    return "Session summary saved. It will be injected at the start of your next session.";
                }
            });

            // === Instance Coordination Tools ===

            Register(new McpTool
            {
                Name = "list_instances",
                Description = "List all running Clarion IDE instances with their open apps, active files, and what they're working on. Use this to see the full picture across a multi-app solution.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                Handler = args =>
                {
                    if (_instanceCoord == null) return "Instance coordination not initialized.";
                    var instances = _instanceCoord.GetAllInstances();
                    if (instances.Count == 0) return "No instances registered.";

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Running Clarion IDE instances:");
                    sb.AppendLine();
                    foreach (var inst in instances)
                    {
                        string label = inst.IsSelf ? " (this instance)" : "";
                        sb.AppendLine(string.Format("PID {0}{1}", inst.Pid, label));
                        if (!string.IsNullOrEmpty(inst.AppFile))
                            sb.AppendLine("  App: " + Path.GetFileName(inst.AppFile));
                        if (!string.IsNullOrEmpty(inst.ActiveFile))
                            sb.AppendLine("  File: " + inst.ActiveFile);
                        if (!string.IsNullOrEmpty(inst.ActiveProcedure))
                            sb.AppendLine("  Procedure: " + inst.ActiveProcedure);
                        if (!string.IsNullOrEmpty(inst.WorkingOn))
                            sb.AppendLine("  Working on: " + inst.WorkingOn);
                        sb.AppendLine("  Since: " + inst.StartedAt);
                        sb.AppendLine();
                    }
                    return sb.ToString();
                }
            });

            Register(new McpTool
            {
                Name = "check_conflicts",
                Description = "Check if any other Clarion IDE instance is editing the same procedure. Call before opening a procedure in the embeditor to avoid conflicts.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "app_file", "App file path (defaults to current app)" },
                    { "procedure_name", "Name of the procedure to check" }
                }, new[] { "procedure_name" }),
                Handler = args =>
                {
                    if (_instanceCoord == null) return "Instance coordination not initialized.";
                    string appFile = args.ContainsKey("app_file") ? args["app_file"]?.ToString() : _instanceCoord.AppFile;
                    string proc = args.ContainsKey("procedure_name") ? args["procedure_name"]?.ToString() : null;
                    if (string.IsNullOrEmpty(proc)) return "procedure_name is required.";

                    var conflict = _instanceCoord.CheckProcedureConflict(appFile, proc);
                    if (conflict == null)
                        return "No conflict. No other instance has " + proc + " open.";

                    return string.Format(
                        "CONFLICT: Instance PID {0} has procedure '{1}' open in {2}. " +
                        "Editing it here may cause save conflicts. Consider coordinating with the other instance first.",
                        conflict.Pid, proc, Path.GetFileName(conflict.AppFile ?? ""));
                }
            });

            Register(new McpTool
            {
                Name = "send_to_instances",
                Description = "Send a message to other Clarion IDE instances. Use to coordinate work across a multi-app solution (e.g., 'I changed the API in ProcX, you may need to update callers').",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "message", "Message text to send" },
                    { "to_pid", "Target instance PID (omit to broadcast to all)" },
                    { "type", "Message type: info, warning, question, conflict" }
                }, new[] { "message" }),
                Handler = args =>
                {
                    if (_instanceCoord == null) return "Instance coordination not initialized.";
                    string message = args.ContainsKey("message") ? args["message"]?.ToString() : null;
                    if (string.IsNullOrEmpty(message)) return "message is required.";

                    int? toPid = null;
                    if (args.ContainsKey("to_pid"))
                    {
                        int pid;
                        if (int.TryParse(args["to_pid"]?.ToString(), out pid))
                            toPid = pid;
                    }
                    string type = args.ContainsKey("type") ? args["type"]?.ToString() : "info";

                    _instanceCoord.SendMessage(toPid, type, null, message);
                    return toPid.HasValue
                        ? "Message sent to instance PID " + toPid.Value + "."
                        : "Message broadcast to all instances.";
                }
            });

            Register(new McpTool
            {
                Name = "get_instance_messages",
                Description = "Get unread messages from other Clarion IDE instances. Check this when starting work or periodically to stay coordinated.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
                Handler = args =>
                {
                    if (_instanceCoord == null) return "Instance coordination not initialized.";
                    var messages = _instanceCoord.GetMessages();
                    if (messages.Count == 0) return "No unread messages from other instances.";

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine(messages.Count + " message(s) from other instances:");
                    sb.AppendLine();
                    foreach (var msg in messages)
                    {
                        sb.AppendLine(string.Format("[{0}] From PID {1} at {2}:",
                            msg.Type.ToUpper(), msg.FromPid, msg.CreatedAt));
                        sb.AppendLine("  " + msg.Payload);
                        sb.AppendLine();
                    }
                    return sb.ToString();
                }
            });

            // ── Everything Search Tools ──────────────────────────────────────

            Register(new McpTool
            {
                Name = "search_files",
                Description = "Instant file search using Everything (voidtools). Searches file/folder names across all indexed drives.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "query", "Search query for file names" },
                    { "max_results?", "Maximum results to return (default: 100, max: 1000)" },
                    { "match_case?", "Enable case-sensitive search (true/false)" },
                    { "match_whole_word?", "Match whole words only (true/false)" },
                    { "regex?", "Enable regular expression search (true/false)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string query = args.ContainsKey("query") ? args["query"]?.ToString() : null;
                    if (string.IsNullOrEmpty(query)) return "Error: query is required.";

                    var opts = new SearchOptions
                    {
                        MaxResults = GetIntArg(args, "max_results", 100),
                        MatchCase = GetBoolArg(args, "match_case"),
                        MatchWholeWord = GetBoolArg(args, "match_whole_word"),
                        Regex = GetBoolArg(args, "regex")
                    };

                    var result = EverythingService.Search(query, opts);
                    if (result.HasError) return "Error: " + result.Error;
                    if (result.Items.Count == 0) return "No files found matching \"" + query + "\"";

                    var sb = new StringBuilder();
                    sb.AppendLine("Found " + result.TotalResults + " results (showing " + result.Items.Count + "):");
                    sb.AppendLine();
                    foreach (var item in result.Items)
                        sb.AppendLine("[" + item.Type + "] " + item.FullPath);
                    return sb.ToString();
                }
            });

            Register(new McpTool
            {
                Name = "search_files_advanced",
                Description = "Advanced file search with path, extension, size, and date filters using Everything.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "query", "Search query for file names" },
                    { "path?", "Limit search to specific path" },
                    { "extension?", "Filter by file extension (e.g., 'txt', 'pdf', 'clw')" },
                    { "size?", "Filter by file size (e.g., '>1mb', '<100kb', '1gb..2gb')" },
                    { "date_modified?", "Filter by date modified (e.g., 'today', 'yesterday', 'thisweek', '2024')" },
                    { "max_results?", "Maximum results to return (default: 100, max: 1000)" },
                    { "match_case?", "Enable case-sensitive search (true/false)" },
                    { "match_whole_word?", "Match whole words only (true/false)" },
                    { "regex?", "Enable regular expression search (true/false)" },
                    { "sort_by?", "Sort results: name_asc, name_desc, path_asc, path_desc, size_asc, size_desc, date_asc, date_desc" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string query = args.ContainsKey("query") ? args["query"]?.ToString() : null;
                    if (string.IsNullOrEmpty(query)) return "Error: query is required.";

                    // Build Everything search query with filters
                    var sb = new StringBuilder();
                    string path = GetStringArg(args, "path");
                    string ext = GetStringArg(args, "extension");
                    string size = GetStringArg(args, "size");
                    string dateMod = GetStringArg(args, "date_modified");

                    if (!string.IsNullOrEmpty(path)) sb.Append("path:\"" + path + "\" ");
                    if (!string.IsNullOrEmpty(ext)) sb.Append("ext:" + ext + " ");
                    if (!string.IsNullOrEmpty(size)) sb.Append("size:" + size + " ");
                    if (!string.IsNullOrEmpty(dateMod)) sb.Append("dm:" + dateMod + " ");
                    sb.Append(query);

                    var opts = new SearchOptions
                    {
                        MaxResults = GetIntArg(args, "max_results", 100),
                        MatchCase = GetBoolArg(args, "match_case"),
                        MatchWholeWord = GetBoolArg(args, "match_whole_word"),
                        Regex = GetBoolArg(args, "regex"),
                        SortBy = GetStringArg(args, "sort_by")
                    };

                    var result = EverythingService.Search(sb.ToString(), opts);
                    if (result.HasError) return "Error: " + result.Error;
                    if (result.Items.Count == 0) return "No files found matching \"" + query + "\"";

                    var filters = new List<string>();
                    if (!string.IsNullOrEmpty(path)) filters.Add("path: \"" + path + "\"");
                    if (!string.IsNullOrEmpty(ext)) filters.Add("extension: " + ext);
                    if (!string.IsNullOrEmpty(size)) filters.Add("size: " + size);
                    if (!string.IsNullOrEmpty(dateMod)) filters.Add("date modified: " + dateMod);

                    var output = new StringBuilder();
                    output.Append("Found " + result.TotalResults + " results");
                    if (filters.Count > 0) output.Append(" (filtered by " + string.Join(", ", filters) + ")");
                    output.AppendLine(" (showing " + result.Items.Count + "):");
                    output.AppendLine();
                    foreach (var item in result.Items)
                        output.AppendLine("[" + item.Type + "] " + item.FullPath);
                    return output.ToString();
                }
            });

            Register(new McpTool
            {
                Name = "find_duplicates",
                Description = "Find duplicate files by filename across all indexed drives using Everything.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "filename", "Filename to search for duplicates" },
                    { "path?", "Limit search to specific path" },
                    { "max_results?", "Maximum results to return (default: 50)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string filename = args.ContainsKey("filename") ? args["filename"]?.ToString() : null;
                    if (string.IsNullOrEmpty(filename)) return "Error: filename is required.";

                    string searchQuery = filename;
                    string path = GetStringArg(args, "path");
                    if (!string.IsNullOrEmpty(path)) searchQuery = "path:\"" + path + "\" " + searchQuery;

                    var opts = new SearchOptions
                    {
                        MaxResults = GetIntArg(args, "max_results", 50),
                        MatchWholeWord = true
                    };

                    var result = EverythingService.Search(searchQuery, opts);
                    if (result.HasError) return "Error: " + result.Error;
                    if (result.Items.Count == 0) return "No files found with name \"" + filename + "\"";

                    if (result.TotalResults == 1)
                        return "Found only 1 instance of \"" + filename + "\":\n\n[" + result.Items[0].Type + "] " + result.Items[0].FullPath;

                    var sb = new StringBuilder();
                    sb.AppendLine("Found " + result.TotalResults + " instances of \"" + filename + "\" (showing " + result.Items.Count + "):");
                    sb.AppendLine();
                    foreach (var item in result.Items)
                        sb.AppendLine("[" + item.Type + "] " + item.FullPath);
                    return sb.ToString();
                }
            });

            Register(new McpTool
            {
                Name = "search_content",
                Description = "Search for text content within files using Everything. Requires Everything content indexing to be enabled.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "content", "Text content to search for within files" },
                    { "file_types?", "Limit to specific file types, semicolon-separated (e.g., 'clw;inc;txt')" },
                    { "path?", "Limit search to specific path" },
                    { "max_results?", "Maximum results to return (default: 50)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string content = args.ContainsKey("content") ? args["content"]?.ToString() : null;
                    if (string.IsNullOrEmpty(content)) return "Error: content is required.";

                    var sb = new StringBuilder();
                    string path = GetStringArg(args, "path");
                    string fileTypes = GetStringArg(args, "file_types");

                    if (!string.IsNullOrEmpty(fileTypes))
                    {
                        var types = fileTypes.Split(';');
                        var parts = new List<string>();
                        foreach (var t in types)
                        {
                            string trimmed = t.Trim();
                            if (!string.IsNullOrEmpty(trimmed)) parts.Add("ext:" + trimmed);
                        }
                        if (parts.Count > 0) sb.Append("(" + string.Join(" | ", parts) + ") ");
                    }

                    if (!string.IsNullOrEmpty(path)) sb.Append("path:\"" + path + "\" ");
                    sb.Append("content:\"" + content + "\"");

                    var opts = new SearchOptions { MaxResults = GetIntArg(args, "max_results", 50) };

                    var result = EverythingService.Search(sb.ToString(), opts);
                    if (result.HasError) return "Error: " + result.Error + " Note: Content search requires Everything to have content indexing enabled.";
                    if (result.Items.Count == 0) return "No files found containing \"" + content + "\". Note: Content search requires Everything to have content indexing enabled.";

                    var output = new StringBuilder();
                    output.AppendLine("Found " + result.TotalResults + " files containing \"" + content + "\" (showing " + result.Items.Count + "):");
                    output.AppendLine();
                    foreach (var item in result.Items)
                        output.AppendLine("[" + item.Type + "] " + item.FullPath);
                    return output.ToString();
                }
            });

            // ── SchemaGraph Tools ─────────────────────────────────────────
            RegisterSchemaGraphTools();
        }

        // ── SchemaGraph Tools ─────────────────────────────────────────

        private SchemaGraphService GetSchemaGraph(Dictionary<string, object> args)
        {
            string dbPath = McpJsonRpc.GetString(args, "db_path");
            if (string.IsNullOrEmpty(dbPath))
                dbPath = FindSchemaGraphDb();
            if (string.IsNullOrEmpty(dbPath))
                return null;
            return new SchemaGraphService(dbPath);
        }

        private string FindSchemaGraphDb()
        {
            // 1. Check Schema Sources registry for the current solution
            if (_chatControl != null)
            {
                // Try both the .sln path and the working directory — source may be linked with either
                var pathsToTry = new List<string>();
                string slnPath = _chatControl.CurrentSolutionPath;
                if (!string.IsNullOrEmpty(slnPath)) pathsToTry.Add(slnPath);
                // Also try the solution directory (in case source was linked with folder path)
                if (!string.IsNullOrEmpty(slnPath))
                {
                    string slnDir = Path.GetDirectoryName(slnPath);
                    if (!string.IsNullOrEmpty(slnDir) && !pathsToTry.Contains(slnDir))
                        pathsToTry.Add(slnDir);
                }

                foreach (string tryPath in pathsToTry)
                {
                    try
                    {
                        var sources = SchemaGraphService.GetSourcesForSolution(tryPath);
                        foreach (var src in sources)
                        {
                            string id = (string)src["id"];
                            string type = (string)src["type"];
                            string connInfo = (string)src["connectionInfo"];
                            string dbPath = SchemaGraphService.GetDbPathForSource(id, type, connInfo);
                            if (File.Exists(dbPath))
                                return dbPath;
                        }
                    }
                    catch { }
                }
            }

            // 2. Check near the currently open file or solution (legacy .dctx path)
            try
            {
                string activePath = _editorService.GetActiveDocumentPath();
                if (!string.IsNullOrEmpty(activePath))
                {
                    string dir = Path.GetDirectoryName(activePath);
                    while (!string.IsNullOrEmpty(dir))
                    {
                        var dbFiles = Directory.GetFiles(dir, "*.schemagraph.db");
                        if (dbFiles.Length > 0) return dbFiles[0];
                        var parent = Directory.GetParent(dir);
                        if (parent == null) break;
                        dir = parent.FullName;
                    }
                }
            }
            catch { }

            // 3. Try solution directory
            if (_chatControl != null)
            {
                string slnPath = _chatControl.CurrentSolutionPath;
                if (!string.IsNullOrEmpty(slnPath))
                {
                    string slnDir = Path.GetDirectoryName(slnPath);
                    try
                    {
                        var dbFiles = Directory.GetFiles(slnDir, "*.schemagraph.db", SearchOption.AllDirectories);
                        if (dbFiles.Length > 0) return dbFiles[0];
                    }
                    catch { }
                }
            }

            return null;
        }

        private void RegisterSchemaGraphTools()
        {
            Register(new McpTool
            {
                Name = "ingest_schema",
                Description = "Ingest a Clarion dictionary (.dctx file) into a SchemaGraph database for fast schema queries. Creates a .schemagraph.db alongside the dictionary.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "dctx_path", "Path to the .dctx file to ingest" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string dctxPath = McpJsonRpc.GetString(args, "dctx_path");
                    if (string.IsNullOrEmpty(dctxPath))
                        return "Error: dctx_path is required";
                    if (!File.Exists(dctxPath))
                        return "Error: File not found: " + dctxPath;

                    string dbPath = SchemaGraphService.GetDbPathForDictionary(dctxPath);
                    var service = new SchemaGraphService(dbPath);
                    service.EnsureDatabase();
                    return service.IngestDctx(dctxPath);
                }
            });

            Register(new McpTool
            {
                Name = "ingest_sql_database",
                Description = "Ingest schema from a SQL Server database into the SchemaGraph. Extracts tables, columns, keys, relationships, stored procedures, functions, and views. Merges with existing .dctx data by default.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "connection_string", "SQL Server connection string (e.g. 'Server=localhost;Database=MyDB;Trusted_Connection=True;')" },
                    { "db_path?", "Path to .schemagraph.db to merge into (auto-detected if omitted)" },
                    { "merge?", "Merge with existing dctx data (default: true). Set to false to replace all data." }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string connStr = McpJsonRpc.GetString(args, "connection_string");
                    if (string.IsNullOrEmpty(connStr))
                        return "Error: connection_string is required";

                    string dbPath = McpJsonRpc.GetString(args, "db_path");
                    if (string.IsNullOrEmpty(dbPath))
                        dbPath = FindSchemaGraphDb();
                    if (string.IsNullOrEmpty(dbPath))
                    {
                        // Create in appdata if no existing DB found
                        string appData = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "ClarionAssistant");
                        Directory.CreateDirectory(appData);
                        dbPath = Path.Combine(appData, "schemagraph.db");
                    }

                    bool merge = McpJsonRpc.GetString(args, "merge") != "false";

                    var service = new SchemaGraphService(dbPath);
                    service.EnsureDatabase();
                    return service.IngestSqlDatabase(connStr, merge);
                }
            });

            Register(new McpTool
            {
                Name = "query_schema",
                Description = "Run a read-only SQL query against a SchemaGraph database. Tables: tables, columns, keys, key_columns, relationships, relationship_mappings, procedures, procedure_params, views, view_references, schema_fts, schema_metadata.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "sql", "SQL SELECT query to run (read-only)" },
                    { "db_path?", "Path to .schemagraph.db (auto-detected if omitted)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string sql = McpJsonRpc.GetString(args, "sql");
                    if (string.IsNullOrEmpty(sql))
                        return "Error: sql parameter is required";

                    var service = GetSchemaGraph(args);
                    if (service == null)
                        return "Error: SchemaGraph database not found. Run ingest_schema first or provide db_path.";

                    return service.ExecuteQuery(sql);
                }
            });

            Register(new McpTool
            {
                Name = "search_tables",
                Description = "Search for database tables by name pattern in the SchemaGraph. Returns table name, prefix, driver, column count, key count.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "pattern", "Table name search pattern (FTS or LIKE with %)" },
                    { "limit?", "Maximum results (default 50)" },
                    { "db_path?", "Path to .schemagraph.db (auto-detected if omitted)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string pattern = McpJsonRpc.GetString(args, "pattern");
                    if (string.IsNullOrEmpty(pattern))
                        return "Error: pattern is required";

                    var service = GetSchemaGraph(args);
                    if (service == null)
                        return "Error: SchemaGraph database not found. Run ingest_schema first or provide db_path.";

                    int limit = McpJsonRpc.GetInt(args, "limit", 50);
                    return service.SearchTables(pattern, limit);
                }
            });

            Register(new McpTool
            {
                Name = "get_table",
                Description = "Get full detail for a database table: all columns (name, type, size, picture), keys with their fields, and relationships to other tables.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "name", "Exact table name (case-insensitive)" },
                    { "db_path?", "Path to .schemagraph.db (auto-detected if omitted)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string name = McpJsonRpc.GetString(args, "name");
                    if (string.IsNullOrEmpty(name))
                        return "Error: name is required";

                    var service = GetSchemaGraph(args);
                    if (service == null)
                        return "Error: SchemaGraph database not found. Run ingest_schema first or provide db_path.";

                    return service.GetTable(name);
                }
            });

            Register(new McpTool
            {
                Name = "search_columns",
                Description = "Search for columns across all tables by name pattern. Returns table name, column name, data type, size, picture.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "pattern", "Column name search pattern (FTS or LIKE with %)" },
                    { "limit?", "Maximum results (default 100)" },
                    { "db_path?", "Path to .schemagraph.db (auto-detected if omitted)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string pattern = McpJsonRpc.GetString(args, "pattern");
                    if (string.IsNullOrEmpty(pattern))
                        return "Error: pattern is required";

                    var service = GetSchemaGraph(args);
                    if (service == null)
                        return "Error: SchemaGraph database not found. Run ingest_schema first or provide db_path.";

                    int limit = McpJsonRpc.GetInt(args, "limit", 100);
                    return service.SearchColumns(pattern, limit);
                }
            });

            Register(new McpTool
            {
                Name = "get_relationships",
                Description = "Get all relationships for a table — both tables it references (parents) and tables that reference it (children).",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "table", "Table name to get relationships for" },
                    { "db_path?", "Path to .schemagraph.db (auto-detected if omitted)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string table = McpJsonRpc.GetString(args, "table");
                    if (string.IsNullOrEmpty(table))
                        return "Error: table is required";

                    var service = GetSchemaGraph(args);
                    if (service == null)
                        return "Error: SchemaGraph database not found. Run ingest_schema first or provide db_path.";

                    return service.GetRelationships(table);
                }
            });

            Register(new McpTool
            {
                Name = "validate_names",
                Description = "Validate table and column names exist in the schema. Supports plain names and Prefix:Column format. Suggests corrections for misspelled names.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "names", "Comma-separated table/column names to validate (e.g. 'Customers, ORD:OrderDate, Products')" },
                    { "db_path?", "Path to .schemagraph.db (auto-detected if omitted)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string names = McpJsonRpc.GetString(args, "names");
                    if (string.IsNullOrEmpty(names))
                        return "Error: names is required";

                    var service = GetSchemaGraph(args);
                    if (service == null)
                        return "Error: SchemaGraph database not found. Run ingest_schema first or provide db_path.";

                    return service.ValidateNames(names);
                }
            });

            Register(new McpTool
            {
                Name = "schema_stats",
                Description = "Get SchemaGraph database statistics: table count, column count, relationships, driver breakdown, database size.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "db_path?", "Path to .schemagraph.db (auto-detected if omitted)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    var service = GetSchemaGraph(args);
                    if (service == null)
                        return "Error: SchemaGraph database not found. Run ingest_schema first or provide db_path.";

                    return service.GetStats();
                }
            });

            // === Project Info Tool ===

            Register(new McpTool
            {
                Name = "get_ca_project_info",
                Description = "Get ClarionAssistant project info for a folder, including linked GitHub account and repository name. Use this to auto-populate marketplace submissions and GitHub operations instead of asking the user.",
                InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
                {
                    { "folder", "Project folder path to look up (e.g. H:\\DevLaptop\\ClarionAssistant\\DatePickerWebviewCOM)" }
                }),
                RequiresUiThread = false,
                Handler = args =>
                {
                    string folder = GetStringArg(args, "folder");
                    if (string.IsNullOrEmpty(folder))
                        return "Error: folder is required";

                    // Normalize path for comparison
                    folder = folder.TrimEnd('\\', '/');

                    // Load projects from %APPDATA%\ClarionAssistant\projects.json
                    string projectsPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ClarionAssistant", "projects.json");

                    if (!File.Exists(projectsPath))
                        return "Error: No projects file found at " + projectsPath;

                    string json = File.ReadAllText(projectsPath);

                    // Find matching project by folder (case-insensitive)
                    string matchedName = null;
                    string matchedType = null;
                    string matchedGhAccountId = null;
                    string matchedRepoName = null;
                    string matchedId = null;

                    int idx = json.IndexOf('[');
                    if (idx < 0)
                        return "Error: Invalid projects.json format";
                    idx++;
                    while (idx < json.Length)
                    {
                        int objStart = json.IndexOf('{', idx);
                        if (objStart < 0) break;
                        int objEnd = FindJsonClosingBrace(json, objStart);
                        if (objEnd < 0) break;
                        string obj = json.Substring(objStart, objEnd - objStart + 1);

                        string projFolder = ExtractJsonField(obj, "folder");
                        if (!string.IsNullOrEmpty(projFolder) &&
                            string.Equals(projFolder.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedId = ExtractJsonField(obj, "id");
                            matchedName = ExtractJsonField(obj, "name");
                            matchedType = ExtractJsonField(obj, "type");
                            matchedGhAccountId = ExtractJsonField(obj, "githubAccountId");
                            matchedRepoName = ExtractJsonField(obj, "repoName");
                            break;
                        }
                        idx = objEnd + 1;
                    }

                    if (matchedName == null)
                        return "Error: No project found for folder: " + folder;

                    // Resolve GitHub account details (without exposing the token)
                    string ghUsername = "";
                    string ghDisplayName = "";
                    string ghProvider = "github";
                    string repoUrl = "";

                    if (!string.IsNullOrEmpty(matchedGhAccountId))
                    {
                        try
                        {
                            var acct = SchemaGraphService.GetGitHubAccount(matchedGhAccountId);
                            if (acct != null)
                            {
                                ghUsername = acct.ContainsKey("username") ? (string)acct["username"] : "";
                                ghDisplayName = acct.ContainsKey("displayName") ? (string)acct["displayName"] : "";
                                ghProvider = acct.ContainsKey("provider") ? (string)acct["provider"] : "github";
                            }
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(ghUsername) && !string.IsNullOrEmpty(matchedRepoName))
                        repoUrl = "https://github.com/" + ghUsername + "/" + matchedRepoName;

                    var result = new Dictionary<string, object>
                    {
                        { "projectId", matchedId ?? "" },
                        { "name", matchedName ?? "" },
                        { "type", matchedType ?? "" },
                        { "folder", folder },
                        { "repoName", matchedRepoName ?? "" },
                        { "githubUsername", ghUsername },
                        { "githubDisplayName", ghDisplayName },
                        { "githubProvider", ghProvider },
                        { "repoUrl", repoUrl }
                    };

                    return result;
                }
            });
        }

        /// <summary>
        /// Find matching closing brace in JSON, handling quoted strings.
        /// </summary>
        private static int FindJsonClosingBrace(string json, int openPos)
        {
            int depth = 0;
            bool inString = false;
            for (int i = openPos; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}') { depth--; if (depth == 0) return i; }
                }
            }
            return -1;
        }

        /// <summary>
        /// Extract a JSON string field value by key (simple parser, no dependencies).
        /// </summary>
        private static string ExtractJsonField(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return null;
            idx += pattern.Length;
            // Skip whitespace and colon
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++; // skip opening quote
            var sb = new StringBuilder();
            while (idx < json.Length && json[idx] != '"')
            {
                if (json[idx] == '\\' && idx + 1 < json.Length)
                {
                    idx++;
                    sb.Append(json[idx]);
                }
                else
                {
                    sb.Append(json[idx]);
                }
                idx++;
            }
            return sb.ToString();
        }

        #region LSP Helpers

        /// <summary>
        /// Resolves the LSP server.js path. Priority:
        /// 1. Settings key "Lsp.ServerPath" (user-configured)
        /// 2. Relative to assembly: {assemblyDir}\lsp-server\server.js
        /// Returns null if not found.
        /// </summary>
        private string ResolveLspServerPath()
        {
            // 1. Check user settings
            if (_chatControl != null)
            {
                string configured = _chatControl.Settings?.Get("Lsp.ServerPath");
                if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                    return configured;
            }

            // 2. Resolve relative to assembly location
            string assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string lspPath = Path.Combine(assemblyDir, "lsp-server", "out", "server", "src", "server.js");
            if (File.Exists(lspPath))
                return lspPath;

            return null;
        }

        private void EnsureLspRunning()
        {
            if (_lspClient != null && _lspClient.IsRunning) return;
            if (_chatControl == null) return;

            string slnPath = _chatControl.CurrentSolutionPath;
            if (string.IsNullOrEmpty(slnPath)) return;

            string wsPath = Path.GetDirectoryName(slnPath);
            string serverJs = ResolveLspServerPath();
            if (serverJs == null) return;

            if (_lspClient != null) _lspClient.Dispose();
            _lspClient = new LspClient();

            // Build clarion/updatePaths for cross-file LSP features (definition, references, etc.)
            try
            {
                var versionConfig = _chatControl.CurrentVersionConfig;
                if (versionConfig != null)
                {
                    var redFile = _chatControl.RedFile;
                    var redirectionPaths = new List<string>();
                    var libsrcPaths = new List<string>();
                    var projectPaths = new List<string>();

                    if (redFile != null)
                    {
                        redirectionPaths = redFile.GetSearchPaths(".clw");
                        libsrcPaths = redFile.GetSearchPaths(".clw", "[*.CLW]");
                    }

                    // Parse project paths from .sln
                    if (File.Exists(slnPath))
                    {
                        foreach (string line in File.ReadAllLines(slnPath))
                        {
                            if (line.TrimStart().StartsWith("Project("))
                            {
                                var parts = line.Split(',');
                                if (parts.Length >= 2)
                                {
                                    string projRelPath = parts[1].Trim().Trim('"');
                                    string projFullPath = Path.Combine(wsPath, projRelPath);
                                    if (File.Exists(projFullPath))
                                        projectPaths.Add(Path.GetDirectoryName(projFullPath));
                                }
                            }
                        }
                    }

                    _lspClient.SetUpdatePaths(new Dictionary<string, object>
                    {
                        { "solutionFilePath", slnPath },
                        { "redirectionFile", versionConfig.RedFilePath ?? "" },
                        { "clarionVersion", versionConfig.Name ?? "" },
                        { "configuration", "Debug" },
                        { "macros", versionConfig.Macros ?? new Dictionary<string, string>() },
                        { "redirectionPaths", redirectionPaths },
                        { "libsrcPaths", libsrcPaths },
                        { "projectPaths", projectPaths },
                        { "defaultLookupExtensions", new[] { ".clw", ".inc", ".equ", ".int" } }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[McpToolRegistry] Failed to build LSP updatePaths: " + ex.Message);
            }

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

        #region Dictionary Helpers

        /// <summary>
        /// Find an open DDDataDictionary object by iterating open ViewContents.
        /// Returns the DCT property of the first DataDictionaryViewContent found.
        /// </summary>
        private object FindOpenDictionary()
        {
            try
            {
                var workbench = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench;
                if (workbench == null) return null;

                var vcProp = workbench.GetType().GetProperty("ViewContentCollection",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (vcProp == null) return null;

                var viewContents = vcProp.GetValue(workbench, null) as System.Collections.IEnumerable;
                if (viewContents == null) return null;

                foreach (var vc in viewContents)
                {
                    if (vc.GetType().Name == "DataDictionaryViewContent")
                    {
                        var dctProp = vc.GetType().GetProperty("DCT",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (dctProp != null)
                            return dctProp.GetValue(vc, null);
                    }
                }
            }
            catch { }
            return null;
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

        #region Build Helpers

        private string RunBuildProcess(string fileName, string arguments, string workingDirectory, int timeoutSeconds, string toolName = null, string targetFile = null)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                    startInfo.WorkingDirectory = workingDirectory;

                var output = new StringBuilder();
                var errors = new StringBuilder();
                int errorCount = 0;
                int warningCount = 0;

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        output.AppendLine(e.Data);
                        string line = e.Data;
                        // Count errors and warnings from build output
                        if (line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            line.IndexOf(": error(", StringComparison.OrdinalIgnoreCase) >= 0)
                            errorCount++;
                        else if (line.IndexOf(": warning ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 line.IndexOf(": warning(", StringComparison.OrdinalIgnoreCase) >= 0)
                            warningCount++;
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            errors.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    bool exited = process.WaitForExit(timeoutSeconds * 1000);

                    if (!exited)
                    {
                        try { process.Kill(); } catch { }
                        return string.Format("Error: Build timed out after {0} seconds.\n\nPartial output:\n{1}", timeoutSeconds, output.ToString());
                    }

                    var result = new StringBuilder();
                    bool success = process.ExitCode == 0;

                    result.AppendLine(success ? "BUILD SUCCEEDED" : "BUILD FAILED");
                    result.AppendLine(string.Format("Exit code: {0} | Errors: {1} | Warnings: {2}", process.ExitCode, errorCount, warningCount));
                    result.AppendLine(string.Format("Command: {0} {1}", fileName, arguments));
                    result.AppendLine();

                    if (output.Length > 0)
                        result.Append(output.ToString());

                    if (errors.Length > 0)
                    {
                        result.AppendLine("\n--- STDERR ---");
                        result.Append(errors.ToString());
                    }

                    // Log trace for recursive self-improvement
                    if (_traceService != null && !string.IsNullOrEmpty(toolName))
                        _traceService.LogBuildResult(toolName, targetFile ?? workingDirectory, result.ToString(), success, errorCount, warningCount);

                    return result.ToString();
                }
            }
            catch (Exception ex)
            {
                return "Error launching build process: " + ex.Message;
            }
        }

        private static string FindMSBuild()
        {
            // Search common MSBuild locations in priority order
            string[] searchPaths = new[]
            {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                    return path;
            }

            // Fallback: try vswhere.exe to find MSBuild
            try
            {
                string vswhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
                if (File.Exists(vswhere))
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = vswhere,
                        Arguments = "-latest -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        string result = process.StandardOutput.ReadLine();
                        process.WaitForExit(5000);
                        if (!string.IsNullOrEmpty(result) && File.Exists(result))
                            return result;
                    }
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region Argument Helpers

        private static string GetStringArg(Dictionary<string, object> args, string key)
        {
            if (args.ContainsKey(key) && args[key] != null)
                return args[key].ToString();
            return null;
        }

        private static int GetIntArg(Dictionary<string, object> args, string key, int defaultValue = 0)
        {
            if (args.ContainsKey(key) && args[key] != null)
            {
                int val;
                if (int.TryParse(args[key].ToString(), out val))
                    return val;
            }
            return defaultValue;
        }

        private static bool GetBoolArg(Dictionary<string, object> args, string key, bool defaultValue = false)
        {
            if (args.ContainsKey(key) && args[key] != null)
            {
                bool val;
                if (bool.TryParse(args[key].ToString(), out val))
                    return val;
            }
            return defaultValue;
        }

        #endregion

        private void Register(McpTool tool)
        {
            _tools[tool.Name] = tool;
        }

        #endregion
    }
}
