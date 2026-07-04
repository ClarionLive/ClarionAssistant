<p align="center">
  <img src="installer/clarion-assistant-256.png" alt="Clarion Assistant" width="128" height="128">
</p>

<h1 align="center">Clarion Assistant</h1>

<p align="center">
  <strong>AI-powered coding assistant for the Clarion IDE</strong><br>
  Embeds Claude Code directly into your Clarion development workflow
</p>

<p align="center">
  <a href="https://github.com/ClarionLive/ClarionAssistant/releases/tag/v5.2.0"><img src="https://img.shields.io/github/v/release/ClarionLive/ClarionAssistant?include_prereleases&label=download&style=for-the-badge" alt="Download"></a>
  <img src="https://img.shields.io/badge/Clarion-10%20%7C%2011%20%7C%2012-blue?style=for-the-badge" alt="Clarion 10 | 11 | 12">
  <img src="https://img.shields.io/badge/version-5.2-blue?style=for-the-badge" alt="v5.2">
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
- **Monaco source editor** &mdash; the default editor for Clarion `.clw`/`.inc` source: syntax highlighting, folding, F12/Ctrl+Click go-to-definition, inline diagnostics, and completion (toggleable under Options &rarr; Clarion Assistant &rarr; Editor Surfaces)
- **Smart Formatter (Ctrl+I)** &mdash; reformats Clarion code with structure indentation and aligned declarations; configurable
- **CA Embeditor** &mdash; use Clarion's own **Embeditor Source** (right-click a procedure, or the Views toolbar button) and a fast Monaco/WebView2 editor overlays the native embeditor automatically; edits save straight back with Clarion-native Save &amp; Exit
- **Code Snippets (Ctrl+J)** &mdash; classic Clarion template-picker parity: insert reusable code with tab-stops and a `${SELECTED}` placeholder, managed from Settings &rarr; Snippets
- **CA Explorer** &mdash; docked pad showing the active CA Embeditor tab's Local, Module &amp; Global Data, Declared Tables, Other Files, and their Keys, Columns, and Relations; drag a field to the editor or Window designer, copy/paste variables native-style, and a Cheat Sheet tab of editor shortcuts
- **Evaluate Code** &mdash; interactive code review for entire apps, procedures, open files, or selected code
- **Diff viewer** &mdash; Monaco-based side-by-side diffs with syntax highlighting
- **Knowledge system** &mdash; persistent cross-session memory for decisions, patterns, and gotchas
- **Zoom persistence** &mdash; Ctrl+mousewheel zoom is saved and restored across sessions

---

## What's New in v5.2

v5.2 makes the CA Embeditor feel completely native: it now rides Clarion's **own** "Embeditor Source" command instead of a separate menu item, and it appears with no flicker.

### CA Embeditor rides Clarion's native "Embeditor Source"

There is no longer a separate **Open in CA Embeditor** popup item. Just open an embed the way you always have &mdash; right-click a procedure &rarr; **Embeditor Source**, or the **Embeditor** button on the Views toolbar &mdash; and the Monaco CA Embeditor overlays the native embeditor automatically. One embed at a time, exactly like the native editor; edits save straight back with **Save &amp; Exit** (no re-open).

- **Nothing new to learn** &mdash; the trigger is Clarion's existing command, so it works from every place that opens an embed.
- **Toggle** the overlay under **Options &rarr; Clarion Assistant &rarr; Editor Surfaces** (&ldquo;Use the Monaco embeditor&rdquo;, on by default).
- **Flicker-free** &mdash; the overlay is placed the instant the embed view opens, before the native editor paints, so you don't see the native embeditor flash first.

*(Thanks to Mark Sarson &mdash; this adopts the auto-detect approach from his [ClarionMonacoEditor](https://github.com/msarson/ClarionMonacoEditor).)*

### Code Snippets in the editor

A full snippet workflow lands in the Monaco source editor and CA Embeditor:

- **Ctrl+J picker** &mdash; theme-aware, with a **live expansion preview** so you see the resulting code (tab-stops visible) before inserting.
- **Snippets in completion** &mdash; type a trigger and press **Enter** to expand (Clarion Ctrl+J parity), or use **trigger + space** for Clarion-style expansion.
- **Manage them in the gear panel** &mdash; edit snippets with examples/help, tab-stop mirroring, and case-insensitive extension scoping.

### Pure upstream language server (v0.9.8)

The bundled LSP is now **stock upstream** [msarson/Clarion-Extension](https://github.com/msarson/Clarion-Extension) **v0.9.8** &mdash; CodeGraph intelligence runs entirely on the CA (C#) side, so there is no patched server to drift out of date, and future upstream releases are a clean one-step re-pin.

### Removed

- The old **Open in CA Embeditor** right-click menu injection and its underlying native Windows-hook machinery have been retired &mdash; the native-command overlay replaces them entirely, with far less moving machinery in the IDE.

---

## What's New in v5.1

v5.1 brings a modern editing experience to the Clarion IDE: Monaco is now the default source editor, a Smart Formatter cleans up your code on demand, and the CA Embeditor and CA Explorer give you a fast, keyboard-friendly way to work with embeds and their data.

### Monaco is now the default Clarion source editor

Clarion source files (`.clw`, `.inc`) open by default in a Monaco-powered editor &mdash; syntax highlighting, code folding, **F12 / Ctrl+Click go-to-definition**, inline diagnostics, and completion &mdash; while keeping the same save and navigation behavior as the native editor.

- **Toggleable per surface** &mdash; turn the Monaco source editor or the Monaco embeditor off under **Options &rarr; Clarion Assistant &rarr; Editor Surfaces**, with an optional file-type filter. Changes apply the next time you open a file.

### Smart Formatter (Ctrl+I)

Press **Ctrl+I** in the Monaco source editor or CA Embeditor to reformat Clarion code &mdash; consistent indentation for structures (IF/LOOP/CASE/ACCEPT) and aligned data declarations. As-you-type aids apply light formatting while you write, and the formatter's behavior is configurable.

### Code Snippets (Ctrl+J)

Press **Ctrl+J** in the CA Embeditor to open a code-snippet picker &mdash; the same shortcut as the classic Clarion text editor's template picker, filterable by trigger/description and scoped to the active file's extension.

- **Managed from Settings &rarr; Snippets** &mdash; add/edit/delete snippets (trigger, description, extensions, body); stored globally in `%APPDATA%\ClarionAssistant\snippets.json`.
- **Tab-stops** &mdash; bodies use VS Code-style snippet syntax (`$1`, `${1:default}`, `$0`); **Tab**/**Shift+Tab** cycles between stops after insertion.
- **`${SELECTED}`** &mdash; substituted with the text selected when Ctrl+J was pressed, or left as a fillable tab-stop if nothing was selected.
- **Auto-indent** &mdash; continuation lines match the indentation of the line Ctrl+J was triggered on.

### CA Embeditor &mdash; right-click a procedure to open it instantly

Opening a procedure's embed code is a single right-click. In the Clarion application tree, right-click any procedure and choose **Open in CA Embeditor** &mdash; the procedure opens directly in a fast Monaco/WebView2 editor, with no Tools-menu or picker round-trip.

- **One-click from the app tree** &mdash; right-click the procedure &rarr; **Open in CA Embeditor**. The procedure you clicked is the one that opens.
- **Multiple procedures as tabs** &mdash; open several procedures side by side; each gets its own CA Embeditor tab.
- **Also available from the toolbar** &mdash; the **CA Embeditor** button on the native embed-editor toolbar opens the currently selected procedure.

### CA Explorer

A docked pad that shows the data behind the procedure you're editing. Open it from **Tools &rarr; CA Explorer** or with **Ctrl+Alt+D** (its docked title bar reads **CA Explorer**); it follows the active CA Embeditor tab.

- **Local, Module &amp; Global Data** &mdash; the variables in scope for the active procedure.
- **Declared Tables &amp; Other Files** &mdash; with their **Keys**, **Columns**, and **Relations**.
- **Follows the active tab** &mdash; switch CA Embeditor tabs and the pad updates to match.
- **Drag a field to the editor or designer** &mdash; drop a column or variable onto the Monaco editor or the native Window designer to create a bound control.
- **Copy / paste variables** native-style, plus a **Cheat Sheet tab** of Monaco editor shortcuts.

### Richer LSP in the Monaco surfaces

The Monaco source editor and CA Embeditor surface live error/warning squiggles and code completion directly as you type &mdash; including `PROP:`/`EVENT:` equates and member-access (`oInstance.Method`) suggestions &mdash; alongside F12 / Ctrl+Click go-to-definition.

### Fixed

- **Clean-install editor load on Clarion 12** &mdash; a strong-name assembly version-lock caused a `Cannot create object: MonacoClarionEditorDisplayBinding` error on C12 builds other than the dev build. The editor now loads on any Clarion 12 build, and installer packaging (Monaco assets and LSP contracts) has been corrected.

### Renamed: "Modern Embeditor / Modern Data" &rarr; "CA Embeditor / CA Explorer"

The feature previously shown as *Modern Embeditor* and *Modern Data* is now **CA Embeditor** and **CA Explorer** (Clarion Assistant) across the menus and UI. Same feature, clearer name.

---

## What's New in v4.6

### Custom MCP servers in the IDE pane (issue #26)

Users can now expose their own MCP servers to the in-IDE Claude tabs without modifying the addin, alongside the three built-ins (clarion-assistant, project-hub, browser).

- **`%APPDATA%\ClarionAssistant\mcp-extra.json` sidecar** &mdash; standard `mcpServers` JSON. Entries are merged into the generated mcp-config.json each time a Claude tab launches, so edits take effect on the *next* tab spawn without restarting the IDE.
- **Settings &rarr; MCP page** has a new "Custom MCP Servers in the IDE Pane" section with an **Open** button that creates the file from a commented template if missing, then shell-opens it. Always visible regardless of the External Access toggle.
- **Auto-approve** &mdash; `mcp__<server>__*` is appended to `--allowedTools` for every sidecar entry, so user tools don't trigger permission prompts inside the IDE pane.
- **Reserved-key protection** &mdash; an mcp-extra.json entry that collides with a built-in key (`clarion-assistant`, `project-hub`, `browser`) is silently skipped; the addin's real entry always wins.
- **Malformed-JSON resilience** &mdash; a corrupted sidecar (trailing comma, stray brace, etc.) is logged and ignored. The tab still launches with the three built-ins.
- **Empty-state safe** &mdash; if the file doesn't exist, behavior is identical to v4.5: just the three built-ins, no errors.

Smoke-tested end-to-end with `@modelcontextprotocol/server-everything` against all four corners: happy path, collision, malformed JSON, missing file.

### Codex config self-heal

Fixes a corruption mode where launching a Codex tab would error with `duplicate key [mcp_servers.clarion-assistant]` and refuse to start.

Codex CLI's TOML serializer silently drops trailing comments when it appends state tables (e.g. `[tui.model_availability_nux]`), which removed our `<<< end >>>` marker. The next CA launch then saw a begin marker with no matching end and appended a second managed block, producing duplicate `[mcp_servers.clarion-assistant]` tables.

- **Self-healing rewrite** &mdash; on every Codex launch CA now strips all CA markers (paired or orphan) and all `[mcp_servers.clarion-assistant.*]` sections anywhere in the file, lifts foreign tables out of any broken managed region, and appends one fresh canonical block. Even a config that's already broken (two begin markers, one end, duplicate tables) is cleaned up automatically on the next launch.
- **No user action required** &mdash; just relaunch a Codex tab and the file is repaired in place.

---

## What's New in v4.5

### External MCP client access (issue #24)

Lets external tools (Claude Desktop, Cline, custom mcp-remote setups) authenticate against ClarionAssistant's local MCP server using a stable user-managed token instead of the per-session token that rotates each IDE start.

- **New "MCP Server" Settings page** with a "Most users can skip this page" intro &mdash; the toggle is opt-in for users who want CA's IDE tools surfaced in tools outside the IDE.
- **Allow external MCP clients toggle** persists across IDE restarts. Off by default.
- **Generate button** mints a 64-character hex bearer token, stored in `settings.txt`. Idempotent: regenerating invalidates any external configs already using the previous token.
- **Live endpoint URL** display shows `http://localhost:<port>/mcp` (port is dynamically chosen at startup).
- **Pre-filled `mcp-remote` config snippet** with **Copy config** button &mdash; ready to paste into the external tool's MCP config.
- **Add to Claude Desktop button** &mdash; one-click integration: writes the `clarion-assistant` entry directly into `%APPDATA%\Claude\claude_desktop_config.json`, backs up the original to `.clarionassistant.bak`, atomic write. Idempotent (re-click after rotating token to push the update).
- **Dual-token authentication in `RequireAuth`** &mdash; the per-session token still authenticates in-IDE Claude Code / Copilot / Codex tabs; the static external token is checked additionally when external access is enabled. Both compared in constant time. Settings re-read on every request so toggle/rotation takes effect immediately.

### SettingsService cross-process hardening (collateral)

Strengthening the settings store to support the External MCP feature surfaced multiple latent issues that affect every settings save, not just MCP tokens:

- **Static lock + named mutex** (`Local\ClarionAssistant.SettingsService.v1`) serializes settings writes across all SettingsService instances within a process AND across multiple ClarionAssistant processes (the addin supports multi-IDE).
- **Reload-before-write merge** &mdash; rotating Mcp.ExternalToken in one place no longer gets reverted when an unrelated control later saves an unrelated setting.
- **Atomic file replace** via temp-file + `File.Replace` (ReplaceFileW on NTFS) so concurrent readers always see either the old file or the new file, never a half-written one.
- **Reload preserves in-memory state** when the file is missing or the read fails &mdash; transient delete-then-rename from sync tools / antivirus no longer wipes settings on the next save.
- **`SettingsLockedException`** is thrown when another CA process holds the cross-process mutex past the 5s budget; the settings dialog catches it and surfaces an actionable message instead of silently degrading.

### Pre-existing security hardening still applies

The MCP server still binds to loopback only (Windows OS blocks remote connections), validates Host and Origin headers (DNS-rebinding and browser-drive-by defense), and uses constant-time token comparison.

---

## What's New in v4.4

### OpenAI Codex CLI backend

Codex joins Claude Code and Copilot as a third launchable terminal backend.

- **Backend selection on the dashboard** &mdash; Codex is now a third option alongside Claude Code and Copilot. Tab labels show **CC** (Claude Code) / **CP** (Copilot) / **CO** (Codex).
- **MCP wiring via `mcp-remote` stdio bridge** &mdash; Codex CLI's native HTTP/SSE transport timed out against CA's Streamable-HTTP `/mcp` endpoint, so CA writes a marker-delimited block in `~/.codex/config.toml` that bridges through `mcp-remote`. Foreign tables Codex CLI itself appends inside the markers (e.g. `[tui.model_availability_nux]`) are lifted out and preserved on rewrite.
- **Pinned `mcp-remote` install** &mdash; CA refuses to write the Codex MCP config if `mcp-remote` isn't installed at the expected npm-global location, and refuses to wire it if the installed version doesn't match the pinned version. Closes a supply-chain risk where `npx -y mcp-remote` would auto-fetch from the public registry at launch with the live MCP bearer token.
- **Codex settings panel** &mdash; reasoning effort dropdown (Auto / low / medium / high), one-time install instruction for `mcp-remote`. Models populated from Codex CLI's actual `/models` list: GPT-5.5 (default), GPT-5.4, GPT-5.4 mini, GPT-5.3 Codex, GPT-5.2.
- **AGENTS.md preserved** &mdash; CA only writes the briefing file when one doesn't already exist in the working directory. Won't clobber a repo-tracked `AGENTS.md`.
- **Robust exception handling in the launch banner** &mdash; failure-reason text is sanitized (CR/LF, double quotes, single quotes, truncated to 240 chars) before splicing into the PowerShell `Write-Host` payload.

### Bundled installer ships with Codex support
Settings → MCP Server panel and the new sidebar entry are part of the standard install.

---

## What's New in v4.3

### Backend selection UX
- **Dashboard backend dropdown** &mdash; pick Claude Code or Copilot per click; overrides the saved default without rewriting it. "Reset to default" link appears whenever the dropdown differs from the saved default.
- **Per-card backend badges** on the quick-action cards (CLAUDE CODE / COPILOT) update live with the dropdown so you can see at a glance which backend each click will launch.
- **Tab-strip labels show the backend** &mdash; new tabs read "Terminal 1 CC" (Claude Code), "Terminal 2 CP" (Copilot), or "CO" (Codex placeholder, reserved for future). Custom-named tabs get the same suffix: "MyProject CC".

### Settings dialog redesign
- **Launch page is now first in the sidebar** and uses per-backend tabs (Launch / Models) under a dropdown
- **"Set as Default" button** replaces the implicit "saved when you change the dropdown" behavior &mdash; now you can inspect any backend's settings without accidentally changing the default
- **Working Directory and Projects Directory are per-backend** &mdash; live on the Models tab; each backend remembers its own paths
- **General page removed** &mdash; its global fields moved: Working Directory to Launch (Models tab, per backend), COM Projects Folder to Classes

### Unified assistant instructions
- **Copilot sessions now get the full 220-line Clarion IDE prompt** previously only Claude received. Both backends share a single authoritative `clarion-assistant-prompt.md`, deployed as `AGENTS.md` for Copilot.

### Security hardening
- **MCP HTTP server requires a per-session bearer token** &mdash; generated fresh each addin start and embedded in the MCP config file each CLI client reads. Drive-by browser fetches to `localhost:19372` no longer reach the tools surface.
- **CORS permissiveness removed** &mdash; `Access-Control-Allow-Origin: *` is gone; browser drive-by can't read responses.
- **Host and Origin header validation** blocks DNS-rebinding attacks and cross-origin browser requests.
- **pwsh launch payload now tokenizes and quotes user settings** &mdash; `Copilot.ExtraFlags`, Claude/Copilot command entries, and Copilot.Model go through a new `PwshCommandQuoter` helper. A crafted settings value can no longer chain arbitrary PowerShell commands.
- **Settings file newline rejection** &mdash; `SettingsService.Set` now throws on CR/LF in values (previously a `\n` could smuggle a second key on reload, e.g. stealth-enabling `Copilot.PermissionMode=allow`).

### Reliability
- **DocGraph FTS5 load fixed** &mdash; `query_docs` no longer intermittently returns "database disk image is malformed" on valid data. The extension is now loaded from an absolute path (no more `LoadLibrary` search-order roulette), and a smoke probe at first use surfaces a clear diagnostic error if the FTS5 module fails to register.
- **New `rebuild_docgraph_fts` MCP tool** &mdash; in-place recovery if someone lands on a corrupted FTS shadow index. Drops and recreates the FTS table from the intact `doc_chunks` content.

### Internals (visible in debug logs)
- **Renamed `ClaudeChatControl` &rarr; `AssistantChatControl`** to reflect its dual-backend role
- **Shared launch scaffolding** &mdash; `LaunchClaudeForTab` and `LaunchCopilotForTab` now share `PrepareBackendLaunch`, `StartTabTerminal`, `AbortLaunch` helpers so adding a 3rd backend (Codex etc.) is a matter of writing one command-body builder, not re-copying ~60 lines of scaffolding

---

## What's New in v4.2

### GitHub Copilot CLI Backend
- **New Assistant Backend setting** (Settings → Launch) — switch between Claude Code and GitHub Copilot CLI per terminal session
- **Copilot Commands** — configurable launch commands for Copilot (same Add/Edit/Delete/Set Default UI as Claude commands), with defaults: `copilot`, `gh copilot`, `copilot --allow-all-tools`
- **Copilot Model selector** (Settings → General) — dropdown with all available Copilot models grouped by provider (GPT-5.x, Claude 4.x); model is passed via `--model` flag at launch
- **Permission Mode** — choose between `Prompt` (default) or `Allow Tools` (`--allow-all-tools`)
- **Extra Flags** — optional additional flags appended to the Copilot command
- **MCP integration** — Clarion Assistant MCP server auto-configured for Copilot with `tools: ["*"]` format; custom instructions deployed via `COPILOT_CUSTOM_INSTRUCTIONS_DIRS`
- **Per-tab backend tracking** — each tab remembers its backend (`AssistantBackend` property) so status bar and exit handling work correctly even if the setting changes mid-session

### Build improvements
- Explicit WebView2 assembly references in `.csproj` to fix compile-time resolution under MSBuild
- `ValidateClarionReferences` MSBuild target for clearer error messages when `ClarionRoot` is not set
- `deploy.ps1` now skips the indexer build gracefully when the project is not present

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

1. **[Download the latest installer](https://github.com/ClarionLive/ClarionAssistant/releases/tag/v5.2.0)** (code-signed)
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
