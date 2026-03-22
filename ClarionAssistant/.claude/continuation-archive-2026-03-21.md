# Continuation: ClarionAssistant

## Current Status

### OpenProcedureEmbed — COMPLETE (Iteration 21)
VK_DOWN+VK_UP after incremental search typing commits the ClaList highlight as a real selection without triggering the Properties dialog. Tested with ScanClass and Favorites — both opened correctly in the embeditor. Minor cosmetic: highlight position may differ from actual selection after exiting embeditor (non-functional).

### Library CodeGraph — BUILT, awaiting deploy + test
New feature: indexes Clarion LibSrc equate files (equates.clw, property.clw, builtins.clw, winerr.inc) into a standalone `ClarionLib.codegraph.db` that ships with the addin. Enables quick lookup of EVENT:, PROP:, BUTTON:, ICON:, COLOR: and other constants via `query_codegraph`.

**Files created/modified:**
- `Services/LibraryIndexer.cs` — NEW. Parses EQUATE definitions, creates CodeGraph-compatible SQLite db.
- `Dialogs/ClaudeChatSettingsDialog.cs` — Added "Library CodeGraph" section with Build button + status label.
- `ClarionAssistant.csproj` — Added LibraryIndexer.cs compile include.
- `Services/McpToolRegistry.cs` — Updated `list_codegraph_databases` to include library db.

**What to test:**
1. Open Settings dialog — verify Library CodeGraph section appears with "Not built" status
2. Click Build — should detect Clarion root, index equates, show "N symbols indexed" in green
3. Run `list_codegraph_databases` — library db should appear at top of list
4. Run `query_codegraph` with db_path pointing to ClarionLib.codegraph.db — verify EVENT: lookups work

**DB location:** `C:\Clarion12\accessory\addins\ClarionAssistant\ClarionLib.codegraph.db`

## Build notes
- ONLY build with MSBuild: `MSYS_NO_PATHCONV=1 "C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" ClarionAssistant.csproj /p:Configuration=Debug /v:minimal`
- Need `MSYS_NO_PATHCONV=1` prefix in bash
- Do NOT use `dotnet build` (WebView2 resolution fails) or `deploy.ps1` (DLLs locked)
- User deploys manually (copies DLL to `C:\Clarion12\accessory\addins\ClarionAssistant`)
- Pre-existing warning in LspClient.cs (CS0414) — ignore

## Known issues / TODO
- **`currentEditorDialog` is unreliable** — always reports null even when embeditor is open. Don't use it as a success indicator.
- **No close embeditor function** — `close_file` doesn't work on embeditor. Would need Escape key or close button approach. Low priority.
- **IntPtr overflow pattern**: Any hex constant with bit 31 set (≥0x80000000) will overflow `IntPtr` cast on 32-bit. Always use `new IntPtr(unchecked((int)0x...))` for these values.
- **Library CodeGraph**: Consider adding builtins.clw function/procedure declarations (not just EQUATE lines) in a future iteration.
