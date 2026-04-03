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
  <img src="https://img.shields.io/badge/Clarion-11%20%7C%2012-blue?style=for-the-badge" alt="Clarion 11 | 12">
  <img src="https://img.shields.io/badge/status-beta-orange?style=for-the-badge" alt="Beta">
</p>

---

## What is Clarion Assistant?

Clarion Assistant is an IDE addin that brings AI-powered code intelligence to [Clarion](https://softvelocity.com) developers. It runs as a docked terminal pane inside the Clarion IDE, giving you a conversational coding assistant that understands your entire codebase.

Ask it to write Clarion code, explain procedures, refactor classes, build COM controls, convert Clarion apps to C#, or navigate your solution &mdash; all without leaving the IDE.

### Key Capabilities

- **Write and edit Clarion code** directly in the IDE editor
- **CodeGraph** &mdash; solution-wide code intelligence via SQL queries over every symbol, relationship, and call chain
- **DocGraph** &mdash; instant search across 14,000+ indexed documentation chunks (Clarion core, CapeSoft, Icetips, and more)
- **LSP integration** &mdash; real-time go-to-definition, find references, hover info, and symbol search
- **Build tools** &mdash; build solutions, individual apps, or C# COM controls without leaving the chat
- **Class intelligence** &mdash; parse CLASS definitions, sync .inc/.clw, generate method stubs
- **Application tree** &mdash; open .app files, list procedures, navigate the embeditor
- **Diff viewer** &mdash; Monaco-based side-by-side diffs with syntax highlighting
- **Tabbed terminal** &mdash; multiple Claude Code sessions with a Home page, settings, and cheat sheet
- **Knowledge system** &mdash; persistent cross-session memory for decisions, patterns, and gotchas

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
| **Clarion IDE** (v11 or v12) | Auto-detected from Windows registry |
| **Claude Code CLI** | [Download from Anthropic](https://claude.ai/download) |
| **WebView2 Runtime** | Pre-installed on Windows 11; [download for Windows 10](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) |

### Install

1. **[Download the latest installer](https://github.com/peterparker57/ClarionAssistant/releases/latest)** (17 MB, code-signed)
2. Close the Clarion IDE
3. Run the installer &mdash; it auto-detects your Clarion installation
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
| Reference prompt | `%USERPROFILE%\.claude\` | `clarion-assistant-reference.md` |

Your existing Claude Code settings are preserved &mdash; the installer merges permissions non-destructively. The `CLAUDE_CODE_NO_FLICKER` environment variable is enabled automatically for a smoother terminal experience.

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
| `export_txa` / `import_txa` | Export/import TXA files |

### Code Intelligence
| Tool | Description |
|---|---|
| `query_codegraph` | SQL queries over every symbol and relationship in the solution |
| `analyze_class` | Parse CLASS definitions from .inc files |
| `sync_check` | Compare .inc declarations vs .clw implementations |
| `generate_stubs` / `generate_clw` | Generate missing method implementations |

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

### Language Server
| Tool | Description |
|---|---|
| `lsp_definition` | Go to definition (cross-file) |
| `lsp_references` | Find all references across the workspace |
| `lsp_hover` | Get type info and documentation |
| `lsp_find_symbol` | Search for symbols by name |

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

### Build

```powershell
# Build the addin
cd ClarionAssistant
msbuild ClarionAssistant.csproj /p:Configuration=Debug /p:Platform=AnyCPU

# Build the installer (builds all projects + compiles Inno Setup)
cd ..\installer
.\build-installer.ps1

# Build and sign
.\build-installer.ps1 -Sign
```

### Deploy for Development

```powershell
# Deploy to your local Clarion IDE
cd ClarionAssistant
.\deploy.ps1
```

---

## Project Structure

```
ClarionAssistant/
├── ClarionAssistant/           # Main addin source (C#, .NET Framework 4.8)
│   ├── Services/               # Core services
│   │   ├── McpToolRegistry.cs  # 90+ MCP tool definitions
│   │   ├── EditorService.cs    # IDE editor integration
│   │   ├── DocGraphService.cs  # Documentation search (FTS5)
│   │   ├── AppTreeService.cs   # .app file operations
│   │   ├── ClarionClassParser.cs # CLASS intelligence
│   │   ├── KnowledgeService.cs # Persistent cross-session knowledge
│   │   └── LspClient.cs       # Language Server Protocol
│   ├── Terminal/               # WebView2 terminal UI (tabbed)
│   │   ├── TabManager.cs      # Multi-tab terminal management
│   │   ├── HomeWebView.cs     # Home page with recent solutions
│   │   └── *.html             # terminal, home, settings, cheatsheet, diff, header
│   └── ClaudeChatControl.cs    # Main chat control
├── docs/                       # User guide
└── installer/                  # Inno Setup installer
    ├── ClarionAssistant.iss    # Installer script
    ├── configure.ps1           # Post-install settings merger
    └── build-installer.ps1     # Build orchestrator
```

---

## License

Copyright (c) 2025-2026 ClarionLive. All rights reserved.

This software is provided as a beta release for evaluation purposes. See [LICENSE.txt](installer/LICENSE.txt) for details.

Clarion Assistant requires a separate [Claude Code](https://claude.ai/download) subscription from Anthropic.

---

## Acknowledgments

- **[Mark Sarson](https://github.com/msarson/Clarion-Extension)** &mdash; pioneered Language Server Protocol support for Clarion, directly inspiring the LSP integration in this project
- **[SoftVelocity](https://softvelocity.com)** &mdash; Clarion IDE and compiler
- **[Anthropic](https://anthropic.com)** &mdash; Claude Code CLI
- **[Microsoft](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)** &mdash; WebView2 runtime

---

<p align="center">
  Built for the Clarion community by <a href="https://clarionlive.com">ClarionLive</a>
</p>
