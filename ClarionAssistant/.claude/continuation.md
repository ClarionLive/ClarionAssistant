# Continuation: ClarionAssistant

## Current Status

### Library CodeGraph — TESTED, WORKING
Deployed and verified. 1,082 symbols indexed from Clarion LibSrc equate files. Confirmed EVENT:, PROP:, COLOR: lookups all work via `query_codegraph` with `db_path` pointing to `C:\Clarion12\Accessory\AddIns\ClarionAssistant\ClarionLib.codegraph.db`. Shows up in `list_codegraph_databases`.

### select_procedure — TESTED, WORKING
- `select_procedure` MCP tool selects a procedure in the ClaList by name
- Uses `PostMessage` + `WM_CHAR` only
- VK_DOWN + VK_UP after typing clears the locator buffer
- Tested 2026-03-22: successfully selected "ScanClass" in the app tree
- Now includes embeditor-open guard (returns error if embeditor is active)

### open_procedure_embed — TESTED, WORKING
- Timing fix deployed and verified 2026-03-22
- 100ms per-char delay + `DoEvents()` after each character
- Tested: ScanClass opened 3 times, FillCheckBoxes opened 3 times — all correct
- Now includes embeditor-open guard (returns error if embeditor is active, tells user to close it first)

### save_and_close_embeditor — TESTED, WORKING
- Calls `IGeneratorDialog.TryClose()` on the ClaGenEditor via reflection
- Tested 2026-03-22: SolutionForm embeditor — prompted to save changes, closed successfully
- `TryClose()` triggers the save dialog then closes

### cancel_embeditor — TESTED, WORKING
- Calls `IGeneratorDialog.Discard()` then `IGeneratorDialog.TryClose()` via reflection
- Tested 2026-03-22: SolutionForm embeditor — closed silently without save prompt
- Discards changes then closes

### Embeditor lifecycle — COMPLETE
Full cycle verified: `open_procedure_embed` → edit → `save_and_close_embeditor` or `cancel_embeditor` → `open_procedure_embed` another procedure

### Embed navigation tools — BUILT, AWAITING DEPLOY + TEST
Four new MCP tools added to navigate between embed points inside the embeditor:

- `next_embed` — go to next embed point
- `prev_embed` — go to previous embed point
- `next_filled_embed` — go to next filled embed (one containing user code)
- `prev_filled_embed` — go to previous filled embed (one containing user code)

**Implementation**: Finds and instantiates the SharpDevelop command classes from CommonSources.dll via reflection, then calls `Run()`:
- `SoftVelocity.Generator.Editor.Commands.GotoNextEmbed`
- `SoftVelocity.Generator.Editor.Commands.GotoPrevEmbed`
- `SoftVelocity.Generator.Editor.Commands.GotoNextFilledEmbed`
- `SoftVelocity.Generator.Editor.Commands.GotoPrevFilledEmbed`

These inherit from `AbstractGenEditorCommand` (which extends `AbstractMenuCommand`). The `Run()` method performs the navigation.

**What to do after deploy:**
1. Open a procedure embeditor (e.g., `open_procedure_embed` for "SolutionForm")
2. Call `next_filled_embed` — cursor should jump to the next embed that has user code
3. Call `next_filled_embed` again — should advance to the following filled embed
4. Call `prev_filled_embed` — should go back
5. Try `next_embed` / `prev_embed` — these navigate ALL embed points, not just filled ones
6. If `Run()` throws because it needs an `Owner` property set (common with AbstractMenuCommand), we may need to set `cmd.Owner = editor` before calling `Run()`. Check the error message.
7. Related ClaGenEditor properties for context: `IsOnFirstEmbed`, `IsOnLastEmbed`, `IsOnFirstFilledEmbed`, `IsOnLastFilledEmbed`

**Code locations:**
- `AppTreeService.cs` — `NavigateEmbed(string direction, bool filledOnly)` method (near end of file)
- `McpToolRegistry.cs` — four Register blocks after `cancel_embeditor`, before `open_file`

## Build notes
- ONLY build with MSBuild: `MSYS_NO_PATHCONV=1 "C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" ClarionAssistant.csproj /p:Configuration=Debug /v:minimal`
- Need `MSYS_NO_PATHCONV=1` prefix in bash
- Do NOT use `dotnet build` (WebView2 resolution fails) or `deploy.ps1` (DLLs locked)
- User deploys manually (copies DLL to `C:\Clarion12\accessory\addins\ClarionAssistant`)
- Pre-existing warning in LspClient.cs (CS0414) — ignore

## Known issues / TODO
- **`currentEditorDialog` is unreliable** — always reports null even when embeditor is open. Don't use it as a success indicator.
- **IntPtr overflow pattern**: Any hex constant with bit 31 set (>=0x80000000) will overflow `IntPtr` cast on 32-bit. Always use `new IntPtr(unchecked((int)0x...))`.
- **Library CodeGraph**: Consider adding builtins.clw function/procedure declarations (not just EQUATE lines) in a future iteration.
- **ClaList is confirmed NOT a standard listbox** — LB_GETCOUNT, LB_FINDSTRINGEXACT, LB_GETTEXTLEN all return 0. May be a treeview or fully custom control. Keystroke approach is the only viable method for now.

## Locator clearing approach (important pattern)
- **VK_ESCAPE**: DO NOT USE — triggers IDE exit dialog
- **VK_DOWN + VK_UP**: Clears the locator buffer. Send after typing + settling. The selection moves down one item then back up, ending on the same item but with a clean locator.
- This must happen AFTER the WM_CHAR typing and 500ms settle, BEFORE the Embeditor button click (in OpenProcedureEmbed) or thread detach (in SelectProcedure).

## Keystroke timing (current values as of 2026-03-22)
- **100ms** between WM_CHAR posts + `DoEvents()` after each char
- **500ms** settle after all chars typed
- **100ms + DoEvents** between VK_DOWN and VK_UP for locator clear
- **300ms** final settle after locator clear
- If still too fast, try 150-200ms per char next
