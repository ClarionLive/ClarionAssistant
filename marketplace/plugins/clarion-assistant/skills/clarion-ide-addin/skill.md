---
name: clarion-ide-addin
description: Create Clarion IDE addins with proper project structure, templates, and SharpDevelop integration. Use when creating new IDE tools, pads, embeditor toolbar buttons, or menu commands for the Clarion IDE.
version: 1.0.0
---

# Clarion IDE Addin Generator

Creates a new Clarion IDE addin project with proper structure, templates, and IDE integration based on the ClarionCOMBrowser addin patterns. Use when creating an IDE addin/plugin, dockable pad, main-window tool, menu command, or embeditor toolbar button.

## Workflow

### Step 1: Gather Information

Use `AskUserQuestion` to collect:

1. **Addin name** (e.g. "ClarionCodeFormatter") — used for solution, project, namespace, and assembly name.
2. **What does it do?** (description).
3. **Hosting type**: Pad (dockable tool panel, like Solution Explorer) [Recommended] | Window (main document area, like source files) | Both (window + pad with View > Tools menu entries) | Embeditor Button (toolbar button on the embed editor, like Find/Replace).
4. **UI approach** (skip for Embeditor Button): Windows Forms [Recommended] | WebBrowser/HTML (HTML/JS UI with ScriptBridge).
5. **Keyboard shortcut** (optional, e.g. "Control|Alt|F").

**Naming convention:** `{AddinName}` = full name; `{ShortName}` = AddinName without "Clarion" prefix (e.g. "CodeFormatter"); `{DisplayName}` = human-readable (e.g. "Code Formatter"). Full placeholder table: read references/project-files.md (in this skill's directory).

### Step 2: Generate GUID

Use `mcp__GUID-Generator__generate_guid` for the project GUID.

### Step 3: Generate Project Structure (in current working directory)

Standard layout: `{AddinName}/{AddinName}.sln` plus project folder `{AddinName}/{AddinName}/` containing `{AddinName}.csproj`, `{AddinName}.addin`, `Properties/AssemblyInfo.cs`, the source files from the matrix below, `Services/` (EditorService.cs, SettingsService.cs), empty `Models/` and `Dialogs/` folders, and `ScriptBridge.cs` if HTML UI.

**Embeditor Button layout is simplified**: only `.sln`, `.csproj`, `.addin`, `Properties/AssemblyInfo.cs`, and `{ShortName}Command.cs` — no UI control or service files.

### Step 4: Create All Files

Create each file from the verbatim templates in the reference files, replacing placeholders:

- Read references/project-files.md (in this skill's directory) for the .sln, .csproj, .addin, and AssemblyInfo.cs templates plus the placeholder table. The .addin paths and suffixes vary by hosting type — follow the INCLUDE IF comments and the {PadSuffix}/{WindowSuffix} rules there.
- Read references/ui-templates.md for {ShortName}Pad.cs, {ShortName}ViewContent.cs, Show{ShortName}Command.cs, Show{ShortName}WindowCommand.cs, {ShortName}Control.cs, and {ShortName}Control.Designer.cs.
- Read references/services-templates.md for Services/EditorService.cs (includes its public API summary), Services/SettingsService.cs, and Services/ScriptBridge.cs (HTML UI only).
- Read references/embeditor-button.md for the {ShortName}Command.cs template, the embeditor ViewContent context (HeaderTitle/FileName/App properties and common patterns), and the simplified Embeditor Button .csproj ItemGroups.

**File generation by hosting type:**

| File | Pad | Window | Both | Embeditor Button |
|------|-----|--------|------|------------------|
| `{ShortName}Pad.cs` | Yes | No | Yes | No |
| `{ShortName}ViewContent.cs` | No | Yes | Yes | No |
| `{ShortName}Control.cs` + `.Designer.cs` | Yes | Yes | Yes | No |
| `Show{ShortName}Command.cs` | Yes | No | Yes | No |
| `Show{ShortName}WindowCommand.cs` | No | Yes | Yes | No |
| `{ShortName}Command.cs` | No | No | No | Yes |
| `Services/EditorService.cs` + `SettingsService.cs` | Yes | Yes | Yes | No |
| `.addin` Pads path | Yes | No | Yes | No |
| `.addin` View/Tools path | No | Yes | Yes | No |
| `.addin` Workspace/Tools path | Yes | No | No | No |
| `.addin` EmbedEditor toolbar | No | No | No | Yes |

## Build & Deploy

1. Build: `msbuild {AddinName}.sln /p:Configuration=Release`
2. Deploy — each addin goes in its own subfolder under `accessory\addins`:
   ```powershell
   $dest = "C:\Clarion12\accessory\addins\{AddinName}"
   New-Item -ItemType Directory -Path $dest -Force
   Copy-Item "{AddinName}\bin\Release\{AddinName}.dll" $dest -Force
   Copy-Item "{AddinName}\bin\Release\{AddinName}.addin" $dest -Force
   ```
3. Restart the Clarion IDE to load the addin. Access via Tools menu or keyboard shortcut.

## Key Gotchas

- Target .NET Framework 4.8, PlatformTarget x86; ICSharpCode references use HintPath to `{ClarionRoot}\bin` with `<Private>False</Private>`.
- All IDE interaction (workbench, pads, text editor) uses reflection for IDE version compatibility — the templates already do this; keep that pattern.
- The "Both" hosting type appends " (Pad)" / " (Window)" suffixes to menu titles; single hosting types use empty suffixes.

## References

All under references/ in this skill's directory:

- **project-files.md** — .sln, .csproj, .addin, AssemblyInfo.cs templates and the full placeholder table. Read when creating the project skeleton (Step 4).
- **ui-templates.md** — Pad, ViewContent, Show*Command, and Control class templates. Read when generating Pad/Window/Both UI files.
- **services-templates.md** — EditorService (with API summary), SettingsService, ScriptBridge templates. Read when generating the Services folder or HTML UI bridge.
- **embeditor-button.md** — Embeditor command template, embeditor ViewContent context/patterns, simplified .csproj. Read whenever hosting type is Embeditor Button.
