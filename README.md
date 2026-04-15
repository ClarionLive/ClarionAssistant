<p align="center">
  <img src="installer/clarion-assistant-256.png" alt="Clarion Assistant" width="128" height="128">
</p>

<h1 align="center">Clarion Assistant</h1>

<p align="center">
  <strong>AI-powered coding assistant for the Clarion IDE</strong><br>
  Embeds Claude Code directly into your Clarion development workflow
</p>

<p align="center">
  <a href="https://github.com/peterparker57/ClarionAssistant/releases/latest"><img src="https://img.shields.io/github/v/release/peterparker57/ClarionAssistant?include_prereleases&label=download&style=for-the-badge" alt="Download"></a>
  <img src="https://img.shields.io/badge/Clarion-10%20%7C%2011%20%7C%2012-blue?style=for-the-badge" alt="Clarion 10 | 11 | 12">
  <img src="https://img.shields.io/badge/version-4.1-green?style=for-the-badge" alt="v4.1">
</p>

---

## What is Clarion Assistant?

Clarion Assistant is an IDE addin that brings AI-powered code intelligence to [Clarion](https://softvelocity.com) developers. It runs as a docked terminal pane inside the Clarion IDE, giving you a conversational coding assistant that understands your entire codebase.

Ask it to write Clarion code, explain procedures, refactor classes, build COM controls, convert Clarion apps to C#, or navigate your solution &mdash; all without leaving the IDE.

### Key Capabilities

- **Write and edit Clarion code** directly in the IDE editor
- **Multi-tab terminal** &mdash; multiple Claude Code sessions with independent workspaces
- **Language Server (LSP)** &mdash; real-time code intelligence with go-to-definition, find references, hover info, diagnostics, and rename support
- **CodeGraph** &mdash; solution-wide code intelligence via SQL queries over every symbol, relationship, and call chain
- **DocGraph** &mdash; instant search across 14,000+ indexed documentation chunks (Clarion core, CapeSoft, Icetips, and more)
- **SchemaGraph** &mdash; database schema intelligence from Clarion dictionaries, SQL Server, SQLite, and PostgreSQL
- **Source Control** &mdash; GitHub and Bitbucket integration with per-solution repo linking
- **Build tools** &mdash; build solutions, individual apps, or C# COM controls without leaving the chat
- **Class intelligence** &mdash; parse CLASS definitions, sync .inc/.clw, generate method stubs
- **Application tree** &mdash; open .app files, list procedures, navigate the embeditor
- **Evaluate Code** &mdash; interactive code review for entire apps, procedures, open files, or selected code
- **Diff viewer** &mdash; Monaco-based side-by-side diffs with syntax highlighting
- **Knowledge system** &mdash; persistent cross-session memory for decisions, patterns, and gotchas
- **Zoom persistence** &mdash; Ctrl+mousewheel zoom is saved and restored across sessions

---

## What's New in v4.1

### Auto-Update Claude Code
- **New setting in Launch tab** &mdash; optional toggle to run `claude update` before each terminal session starts
- Ensures you're always on the latest Claude Code version without manual checks

### DocGraph
- **BoxSoft documentation added** &mdash; BoxSoft template documentation now indexed and searchable via `query_docs`

### Bug Fixes
- **Fixed: GitHub icon URL** &mdash; header toolbar GitHub button now correctly links to the [clarionlive/clarionassistant](https://github.com/clarionlive/clarionassistant) repository

---

## What's New in v4.0

### Clarion Language Server (LSP)
- **Bundled in the installer** &mdash; real-time code intelligence with zero setup
- **9 LSP tools** &mdash; go-to-definition, find references, hover info, document symbols, workspace symbol search, diagnostics, and rename support
- **Ships with Node.js runtime** and all required dependencies &mdash; no separate install needed
- **`/lsp-diagnostics` skill** &mdash; run diagnostics across every source file in the open solution with navigate-to-error support
- **LSP Status Bar** &mdash; live language server status displayed on each terminal tab

### Schema Graph Database
- **Per-project SQLite schema index** for Clarion dictionaries and SQL databases
- **.dctx XML parser** &mdash; extracts tables, columns, keys, and relationships from Clarion dictionary exports
- **SQL Server extraction** &mdash; tables, columns, keys, foreign keys, stored procedures, functions, and views
- **Merge logic** &mdash; matches SQL tables to existing dictionary tables by name, adds SQL-only tables
- **FTS5 full-text search** &mdash; fuzzy name search across tables and columns
- **10 MCP tools** &mdash; `ingest_schema`, `ingest_sql_database`, `search_tables`, `get_table`, `search_columns`, `get_relationships`, `query_schema`, `schema_stats`, `export_dctx`, `import_dctx`

### 108 MCP Tools
- Up from ~30 documented tools in v3.1 to **108 fully documented tools** across 12 categories
- New categories: LSP, File System & Search, Multi-Instance Coordination, Validation

### Installer
- **Code-signed** with Sectigo EV certificate (Kennewick Computer Company)
- **22 Clarion development skills** (up from 17)
- **LSP server** distributed as an optional component for Clarion 10, 11, and 12
- Updated prerequisites &mdash; optional [Everything](https://www.voidtools.com) integration for fast file search

---

## What's New in v3.1

### Create Class from Model Templates
- **New "Create Class" tab** &mdash; select a model template, name your class, and generate .inc/.clw files in one step
- **Class Models settings** &mdash; manage model templates in Settings &rarr; Classes, with Edit (opens both .inc and .clw) and Delete
- **Class output folder** &mdash; configurable default output folder for generated class files
- **Syntax-highlighted preview** &mdash; preview .inc and .clw content before creating, with Clarion syntax highlighting

### Status Line
- **Live status bar** on each terminal tab showing model name, usage quota, pacing, and context window fill
- **Polls from Claude Code** via a statusLine hook script (`ca-statusline.js`) that writes JSON to a temp file

### Embeditor Navigation
- **`list_embeds`** &mdash; new MCP tool to list all embed sections in the active embeditor with filled status
- **`find_embed`** &mdash; new MCP tool to search embed sections by partial name and navigate the cursor there

### Project Info
- **`get_ca_project_info`** &mdash; new MCP tool to look up linked GitHub account and repo name for a project folder

### Claude Code Detection Improvements
- **Standalone CLI and WinGet support** &mdash; finds `claude.exe` from standalone install (`~/.claude/local/`), WinGet (`AppData/Local/Microsoft/WinGet/Links/`), npm global, or PATH
- **Installer detection updated** &mdash; checks all install locations before falling back to PATH; install message now suggests `winget install Anthropic.ClaudeCode`

### UI Improvements
- **Removed Settings from home page** &mdash; use the gear icon button instead
- **Light/dark theme** correctly applied to the Create Class tab on creation

---

## What's New in v3.0

### Schema Sources &mdash; Database Intelligence
- **Schema Source Manager** &mdash; collapsible "Solution Settings" panel above the terminal with per-solution database source linking
- **Multi-database support** &mdash; index schemas from Clarion dictionaries (.dctx), SQL Server, SQLite, and PostgreSQL
- **Global source registry** &mdash; define database sources once, link to multiple solutions
- **DPAPI-encrypted credentials** &mdash; connection info stored securely with Windows data protection
- **Test Connection** &mdash; validate database connections before indexing
- **MCP integration** &mdash; Claude automatically finds and queries your indexed schemas via `search_tables`, `get_table`, `get_relationships`, and more

### Source Control Integration
- **GitHub and Bitbucket** accounts with encrypted token/app password storage
- **Per-solution repo linking** &mdash; assign a source control account + repo to each solution
- **Test authentication** &mdash; validates credentials against GitHub/Bitbucket APIs

### Simplified Home Page
- **Quick Actions** &mdash; New Chat, Evaluate Code, Settings, and Work With Open Solution cards
- **Projects table** &mdash; manage COM controls, addins, and other projects with GitHub/Bitbucket repo linking
- **Removed Solutions tab** &mdash; the IDE manages solutions; Clarion Assistant auto-detects the open solution

### Multi-Version Installer
- **Supports Clarion 10, 11, and 12** &mdash; install to one or all versions from a single installer
- **Per-version installation** &mdash; installing for one version won't affect another
- **Auto-detection** &mdash; finds Clarion installations from the Windows registry and common paths
- **Browse buttons** &mdash; pick custom Clarion paths for non-standard installations

### Evaluate Code Improvements
- **5 scope options** &mdash; evaluate the entire app, a specific procedure, embeditor content, text editor file, or selected code
- **Smart file detection** &mdash; correctly identifies text files vs .app files in the IDE
- **No fabricated results** &mdash; always reads real code from the IDE before producing any evaluation

### Bug Fixes
- **Fixed: Empty 404 crashes Claude Code SDK** &mdash; MCP server now returns JSON body on 404 responses during OAuth discovery ([#3](https://github.com/peterparker57/ClarionAssistant/issues/3))
- **Fixed: replace_text destroys embeditor content** &mdash; now uses surgical `Document.Replace()` instead of full-document replacement ([#2](https://github.com/peterparker57/ClarionAssistant/issues/2))
- **Fixed: LSP server path hardcoded** &mdash; resolved relative to assembly location with configurable override ([#1](https://github.com/peterparker57/ClarionAssistant/issues/1))
- **Fixed: Solution not auto-detected** &mdash; 10-second polling detects solution changes in the IDE
- **Fixed: DocGraph personal search crashes** &mdash; FTS5 virtual tables can't use schema-qualified names; queries now run independently and merge results
- **Fixed: Header/action bar text clipping** &mdash; responsive flex-wrap layout

### Other Improvements
- **Zoom persistence** across all WebView2 panels (header, home, settings, schema sources)
- **Responsive header** &mdash; text wraps instead of clipping at narrow widths
- **Import Now button** for personal DocGraph documentation with progress feedback
- **Remove All Personal** button for bulk DocGraph cleanup
- **Delete confirmation** for source control accounts

---

## Also Included: COM for Clarion

The installer bundles **COM for Clarion**, a complete toolkit for creating .NET COM controls that work with Clarion:

- **IDE addin** &mdash; browse, discover, and manage COM controls from inside Clarion
- **UltimateCOM template** &mdash; Clarion template and class for embedding COM controls in your apps
- **ClarionCOM tooling** &mdash; project templates, build scripts, and deployment tools for creating your own C# COM controls
- **COM Marketplace** &mdash; access community-published controls from [clarionlive.com](https://clarionlive.com)

---

## Installation

### Prerequisites

| Requirement | Notes |
|---|---|
| **Clarion IDE** (v10, v11, or v12) | Auto-detected from Windows registry |
| **Claude Code CLI** | [Download from Anthropic](https://claude.ai/download) |
| **WebView2 Runtime** | Pre-installed on Windows 11; [download for Windows 10](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) |

### Install

1. **[Download the latest installer](https://github.com/peterparker57/ClarionAssistant/releases/latest)** (code-signed)
2. Close the Clarion IDE
3. Run the installer &mdash; select which Clarion versions to install for
4. Restart the Clarion IDE

### What Gets Installed

| Component | Location | Description |
|---|---|---|
| Clarion Assistant addin | `{Clarion}\accessory\addins\ClarionAssistant\` | Main addin DLL, WebView2, SQLite, HTML terminal |
| COM for Clarion addin | `{Clarion}\accessory\addins\ComForClarion\` | COM browser addin |
| UltimateCOM template | `{Clarion}\accessory\template\win\` | .tpl, .inc, .clw, and template DLLs |
| Documentation | `{Clarion}\accessory\resources\ComForClarionDocumentation\` | COM for Clarion docs |
| Claude Code plugin | `%USERPROFILE%\.claude\plugins\...\clarion-assistant\` | 20+ Clarion-specific skills, hooks, and docs |
| Code quality agents | `%USERPROFILE%\.claude\agents\` | 6 agents (won't overwrite existing) |
| ClarionCOM tooling | `%APPDATA%\ClarionCOM\` | Project templates and scripts |
| DocGraph database | `%APPDATA%\ClarionAssistant\` | Pre-loaded Clarion 12 documentation index |

Your existing Claude Code settings are preserved &mdash; the installer merges permissions non-destructively.

---

## MCP Tools Reference

Clarion Assistant exposes **108 MCP tools** that Claude uses to interact with the IDE:

### IDE & Editor (23 tools)
| Tool | Description |
|---|---|
| `get_active_file` | Get path and content of the open file |
| `open_file` | Open a file in the editor, optionally at a line |
| `close_file` | Close the active editor tab |
| `save_file` | Save the active file |
| `get_open_files` | List all open editor tabs |
| `go_to_line` | Navigate to a specific line in the open file |
| `get_cursor_position` | Get current line, column, and total line count |
| `get_line_text` | Get text of a specific line from the live buffer |
| `get_lines_range` | Get a range of lines from the editor |
| `get_selected_text` | Get the currently selected text |
| `get_word_under_cursor` | Get the word at the cursor position |
| `select_range` | Select/highlight a range of text in the editor |
| `insert_text_at_cursor` | Insert text at the current cursor position |
| `replace_text` | Find and replace all occurrences in the active editor |
| `replace_range` | Replace text between specific line/column positions |
| `delete_range` | Delete text between specific line/column positions |
| `find_in_file` | Search for text in the active editor buffer |
| `toggle_comment` | Toggle Clarion line comments on a range of lines |
| `is_modified` | Check if the active file has unsaved changes |
| `undo` | Undo the last edit |
| `redo` | Redo the last undone edit |
| `show_diff` | Show a side-by-side diff in the Monaco viewer |
| `get_diff_result` | Get approval/notes from the diff viewer |

### Application Tree & Embeditor (21 tools)
| Tool | Description |
|---|---|
| `get_app_info` | Get info about the currently open app |
| `list_procedures` | List all procedures in the open app |
| `get_procedure_details` | Get detailed procedure info (prototype, module, template) |
| `select_procedure` | Select a procedure in the app tree |
| `open_procedure_embed` | Open the embeditor for a procedure |
| `get_embed_info` | Get info about the active embeditor |
| `list_embeds` | List all embed sections with filled status |
| `find_embed` | Find and navigate to an embed section by name |
| `next_embed` / `prev_embed` | Navigate to the next/previous embed point |
| `next_filled_embed` / `prev_filled_embed` | Navigate to the next/previous filled embed |
| `get_embed_content` | Read code inside a specific embed slot |
| `get_embeditor_source` | Get full annotated embeditor source with embed markers |
| `search_embeditor_source` | Regex search over annotated embeditor source |
| `open_embeditor_source` | Open the embeditor source in the editor |
| `write_embed_content` | Write code into an embed slot by line number |
| `save_and_close_embeditor` | Save changes and close the embeditor |
| `cancel_embeditor` | Discard changes and close the embeditor |
| `export_txa` | Export app or procedures to TXA format |
| `import_txa` | Import a TXA file into the app |

### Code Intelligence (11 tools)
| Tool | Description |
|---|---|
| `get_solution_info` | Get current solution, Clarion version, RED file, and CodeGraph status |
| `index_codegraph` | Index the solution for CodeGraph queries |
| `index_solution` | Index all projects in the solution |
| `list_codegraph_databases` | List available indexed CodeGraph databases |
| `query_codegraph` | SQL queries over every symbol, relationship, and call chain |
| `get_project_source_files` | List all source files (.clw, .inc) with absolute paths |
| `analyze_class` | Parse CLASS definitions from .inc files |
| `sync_check` | Compare .inc declarations vs .clw implementations |
| `generate_stubs` | Generate method stubs for missing implementations |
| `generate_clw` | Generate a complete .clw implementation from a .inc file |
| `generate_source` | Generate .clw/.inc source from templates |

### LSP &mdash; Language Server (9 tools)
| Tool | Description |
|---|---|
| `lsp_start` | Start the Clarion Language Server |
| `lsp_debug_status` | Check LSP server status |
| `lsp_definition` | Go to definition of a symbol (cross-file) |
| `lsp_references` | Find all references to a symbol across the workspace |
| `lsp_hover` | Get type info, signature, and documentation for a symbol |
| `lsp_document_symbols` | Get all symbols in a file |
| `lsp_find_symbol` | Search for symbols across the workspace by name |
| `lsp_diagnostics` | Get errors and warnings for a source file |
| `lsp_rename` | Propose a rename of a symbol (returns edit list for approval) |

### Schema Intelligence (10 tools)
| Tool | Description |
|---|---|
| `search_tables` | Search database tables by name |
| `get_table` | Full table detail with columns, keys, relationships |
| `search_columns` | Find columns across all tables |
| `get_relationships` | Show parent/child table relationships |
| `query_schema` | Run SQL queries against the schema index |
| `schema_stats` | Get schema database statistics |
| `ingest_schema` | Index a Clarion dictionary (.dctx) |
| `ingest_sql_database` | Index schema from SQL Server, SQLite, or PostgreSQL |
| `export_dctx` | Export dictionary to .dctx format |
| `import_dctx` | Import a .dctx dictionary |

### Documentation Search (6 tools)
| Tool | Description |
|---|---|
| `query_docs` | Full-text search across all indexed documentation |
| `ingest_docs` | Index docs from a Clarion installation's accessory/Documents folder |
| `ingest_web_docs` | Ingest documentation from web URLs |
| `list_doc_libraries` | List all indexed libraries with chunk counts |
| `discover_docs` | Preview discoverable doc sources without ingesting |
| `docgraph_stats` | Get DocGraph database statistics |

### Build Tools (5 tools)
| Tool | Description |
|---|---|
| `build_solution` | Build the entire Clarion solution via ClarionCL.exe |
| `build_app` | Build a single .app file (for multi-DLL solutions) |
| `build_com_project` | Build a C# COM control via MSBuild |
| `run_command` | Execute any command-line tool |
| `execute_command` | Execute a shell command |

### File System & Search (7 tools)
| Tool | Description |
|---|---|
| `read_file` | Read file content from disk with optional line range |
| `write_file` | Write content to a file |
| `append_to_file` | Append text to an existing file |
| `list_directory` | List files in a directory with optional pattern filter |
| `search_files` | Search for files by name |
| `search_files_advanced` | Advanced file search with Everything integration (path, extension, size, date filters) |
| `search_content` | Search file contents by text |

### Project & IDE (4 tools)
| Tool | Description |
|---|---|
| `get_ca_project_info` | Get linked GitHub/Bitbucket account and repo for a project |
| `get_red_search_paths` | Get RED file search paths for the active solution |
| `resolve_red_path` | Resolve a filename to an absolute path via RED search paths |
| `inspect_ide` | Inspect Clarion IDE internal state |

### Knowledge & Memory (6 tools)
| Tool | Description |
|---|---|
| `add_knowledge` | Save reusable insights (decisions, patterns, gotchas) across sessions |
| `query_knowledge` | Search past decisions and patterns |
| `save_session_summary` | Save a session summary for next-session continuity |
| `query_traces` | Query code generation traces |
| `trace_stats` | Get trace database statistics |
| `log_skill_update` | Log a skill update event |

### Multi-Instance Coordination (4 tools)
| Tool | Description |
|---|---|
| `list_instances` | List all running Clarion Assistant instances |
| `get_instance_messages` | Get messages from other instances |
| `send_to_instances` | Send a message to other instances |
| `check_conflicts` | Check for file conflicts across instances |

### Validation (2 tools)
| Tool | Description |
|---|---|
| `validate_names` | Validate Clarion naming conventions |
| `find_duplicates` | Find duplicate symbols in the solution |

---

## Claude Code Skills

The installer includes 22 Clarion-specific skills for Claude Code (installed as a plugin):

| Skill | Description |
|---|---|
| `clarion` | Clarion language reference &mdash; syntax, data types, control structures, Windows API patterns |
| `clarion-ide-addin` | IDE addin development with SharpDevelop integration |
| `clarion-analyze` | Analyze Clarion code generation traces for recurring failure patterns |
| `clarion-benchmark` | Benchmark Clarion code generation quality |
| `clarion-convert-driver` | Convert Clarion dictionaries between file drivers (e.g., TopSpeed to SQLite) |
| `evaluate-code` | Evaluate Clarion app code for issues and improvements |
| `jfiles` | jFiles JSON serialization patterns for Clarion |
| `lsp-diagnostics` | Run LSP diagnostics across all source files in the open solution with navigate-to-error support |
| `ClarionCOM` | Interactive COM development assistant |
| `clarioncom-build` | Build COM projects with MSBuild |
| `clarioncom-config` | Manage ClarionCOM settings |
| `clarioncom-control` | Create and validate C# COM controls for Clarion |
| `clarioncom-create` | Create new C# COM control projects from scratch |
| `clarioncom-deploy` | Generate deployment artifacts |
| `clarioncom-get` | Download controls from the marketplace |
| `clarioncom-github-init` | Initialize GitHub repos for COM projects |
| `clarioncom-marketplace-submit` | Submit controls to the COM Marketplace |
| `clarioncom-validate` | Validate RegFree COM compliance |
| `clarioncom-webview2-build` | Build WebView2 COM control projects |
| `clarioncom-webview2-create` | Create WebView2-based COM controls with HTML/CSS/JS |
| `clarioncom-webview2-deploy` | Generate deployment artifacts for WebView2 COM controls |
| `clarioncom-webview2-validate` | Validate WebView2 COM controls for RegFree compliance |

---

## Building from Source

### Requirements

- Visual Studio 2022 (Community or higher)
- .NET Framework 4.8 SDK
- Clarion IDE (for reference assemblies in `{Clarion}\bin\`)
- [Inno Setup 6](https://jrsoftware.org/isdownload.php) (for building the installer)

### Configuring your Clarion path

The build uses `Directory.Build.props` at the repo root to locate your Clarion installation. The defaults assume John's machine layout (`C:\Clarion12`, `C:\Clarion11-13372`, `C:\Clarion10`).

If your Clarion is installed elsewhere, create a `Directory.Build.props.user` file alongside `Directory.Build.props` (it is gitignored — never commit it):

```xml
<Project>
  <!-- Replace with your actual Clarion installation path -->
  <PropertyGroup>
    <ClarionRoot>C:\Clarion\Clarion12</ClarionRoot>
  </PropertyGroup>
</Project>
```

The `.user` file overrides the defaults for all `ClarionVersion` values, so a single path entry is enough if you only build for one version. You can still pass `/p:ClarionVersion=11` on the command line to select the target version.

Alternatively, pass the path directly on the command line without creating a `.user` file:

```powershell
msbuild ClarionAssistant.csproj /p:ClarionVersion=11 /p:ClarionRoot="C:\Clarion\Clarion11.1"
```

### Build

```powershell
# Build for a specific version (uses Directory.Build.props.user if present)
cd ClarionAssistant
msbuild ClarionAssistant.csproj /p:Configuration=Debug /p:ClarionVersion=12

# Build the addin for all Clarion versions via deploy script
.\deploy.ps1 -NoBuild:$false -Version all
```

> **Note:** Use MSBuild directly — do **not** use `dotnet build`. WebView2 NuGet resolution fails with the .NET CLI on this .NET Framework 4.8 project.

### Deploy for Development

```powershell
# Deploy to your local Clarion IDE (builds + copies DLLs)
cd ClarionAssistant
.\deploy.ps1 -Version 12

# Deploy without rebuilding (e.g. HTML-only changes)
.\deploy.ps1 -Version 12 -NoBuild

# Kill the IDE before deploying (when DLLs are locked)
.\deploy.ps1 -Version 12 -Kill
```

---

## Acknowledgments

Clarion Assistant is built with the help of these open-source projects and contributors:

### Contributors

| Name | Contribution |
|---|---|
| [Mark Sarson](https://github.com/msarson/Clarion-Extension) | Clarion Language Server Protocol implementation for VS Code, which the LSP integration in Clarion Assistant is based on |

### Open Source Libraries

| Library | Description | License |
|---|---|---|
| [xterm.js](https://github.com/xtermjs/xterm.js) | Terminal emulator (v6.0.0) | MIT |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) | JSON serialization (v13.0.3) | MIT |
| [System.Data.SQLite](https://system.data.sqlite.org) | SQLite database with FTS5 full-text search | Public Domain |
| [Microsoft WebView2](https://github.com/MicrosoftEdge/WebView2Feedback) | Embedded browser runtime | MIT |
| [Everything SDK](https://www.voidtools.com) | Instant file search by voidtools | Freeware |
| [recursive-improve](https://github.com/kayba-ai/recursive-improve) | Recursive improvement pattern for code generation | MIT |

---

## License

[MIT License](LICENSE) &mdash; &copy; 2026 ClarionLive.
