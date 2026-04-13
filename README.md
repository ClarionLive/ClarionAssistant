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
  <img src="https://img.shields.io/badge/version-3.1-green?style=for-the-badge" alt="v3.1">
</p>

---

## What is Clarion Assistant?

Clarion Assistant is an IDE addin that brings AI-powered code intelligence to [Clarion](https://softvelocity.com) developers. It runs as a docked terminal pane inside the Clarion IDE, giving you a conversational coding assistant that understands your entire codebase.

Ask it to write Clarion code, explain procedures, refactor classes, build COM controls, convert Clarion apps to C#, or navigate your solution &mdash; all without leaving the IDE.

### Key Capabilities

- **Write and edit Clarion code** directly in the IDE editor
- **Multi-tab terminal** &mdash; multiple Claude Code sessions with independent workspaces
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

Clarion Assistant exposes **90+ MCP tools** that Claude uses to interact with the IDE:

### IDE & Editor
| Tool | Description |
|---|---|
| `get_active_file` | Get path and content of the open file |
| `open_file` | Open a file in the editor, optionally at a line |
| `replace_text` / `replace_range` | Find-and-replace or replace a specific code block |
| `toggle_comment` | Toggle Clarion line comments on a range |
| `save_file` / `undo` / `redo` | Standard editor operations |

### Application Tree
| Tool | Description |
|---|---|
| `open_app` | Open a .app file in the IDE |
| `list_procedures` | List all procedures in the open app |
| `open_procedure_embed` | Open the embeditor for a procedure |
| `list_embeds` | List all embed sections with filled status |
| `find_embed` | Find and navigate to an embed section by name |
| `export_txa` / `import_txa` | Export/import TXA files |

### Code Intelligence
| Tool | Description |
|---|---|
| `query_codegraph` | SQL queries over every symbol and relationship in the solution |
| `analyze_class` | Parse CLASS definitions from .inc files |
| `sync_check` | Compare .inc declarations vs .clw implementations |
| `generate_stubs` / `generate_clw` | Generate missing method implementations |

### Schema Intelligence
| Tool | Description |
|---|---|
| `search_tables` | Search database tables by name |
| `get_table` | Full table detail with columns, keys, relationships |
| `search_columns` | Find columns across all tables |
| `get_relationships` | Show parent/child table relationships |
| `query_schema` | Run SQL queries against the schema index |
| `ingest_schema` | Index a Clarion dictionary (.dctx) |
| `ingest_sql_database` | Index schema from SQL Server |

### Documentation Search
| Tool | Description |
|---|---|
| `query_docs` | Full-text search across all indexed documentation |
| `ingest_docs` | Index docs from your Clarion installation |
| `list_doc_libraries` | List all indexed libraries |

### Build Tools
| Tool | Description |
|---|---|
| `build_solution` | Build the entire Clarion solution via ClarionCL.exe |
| `build_app` | Build a single .app file (for multi-DLL solutions) |
| `generate_source` | Generate .clw/.inc source from templates |
| `build_com_project` | Build a C# COM control via MSBuild |
| `run_command` | Execute any command-line tool |

### Project Info
| Tool | Description |
|---|---|
| `get_ca_project_info` | Get linked GitHub account and repo name for a project folder |

### Knowledge & Memory
| Tool | Description |
|---|---|
| `add_knowledge` | Save reusable insights (decisions, patterns, gotchas) across sessions |
| `query_knowledge` | Search past decisions and patterns |

---

## Claude Code Skills

The installer includes 20+ Clarion-specific skills for Claude Code (installed as a plugin):

| Skill | Description |
|---|---|
| `clarion` | Clarion language reference &mdash; syntax, data types, control structures, Windows API patterns |
| `clarion-ide-addin` | IDE addin development with SharpDevelop integration |
| `ClarionCOM` | Interactive COM development assistant |
| `clarioncom-create` | Create new C# COM control projects from scratch |
| `clarioncom-build` | Build COM projects with MSBuild |
| `clarioncom-validate` | Validate RegFree COM compliance |
| `clarioncom-deploy` | Generate deployment artifacts |
| `clarioncom-webview2-*` | WebView2-based COM control suite (create, build, validate, deploy) |
| `clarioncom-config` | Manage ClarionCOM settings |
| `clarioncom-get` | Download controls from the marketplace |
| `clarioncom-github-init` | Initialize GitHub repos for COM projects |
| `evaluate-code` | Evaluate Clarion app code for issues and improvements |
| `clarion-analyze` | Analyze Clarion code generation traces for recurring failure patterns |
| `clarion-benchmark` | Benchmark Clarion code generation quality |
| `clarion-convert-driver` | Convert Clarion dictionaries between file drivers (e.g., TopSpeed to SQLite) |
| `jfiles` | jFiles JSON serialization patterns for Clarion |

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
