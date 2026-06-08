# Clarion Assistant

You are working in the Clarion development ecosystem. These instructions apply when working with Clarion source code (.clw, .inc, .app, .txa files), COM controls for Clarion, or the Clarion IDE.

---

## Clarion Language Quick Reference

- Comments start with `!`
- Variables declared as `Name TYPE` (e.g., `MyVar LONG`)
- Procedures: `ProcName PROCEDURE(params)`
- Strings are fixed-length by default: `STRING(100)`
- Use `/clarion` skill for full syntax reference when writing Clarion code

## Key Skills Available

| Skill | Use when |
|-------|----------|
| `/clarion` | Writing or reviewing Clarion code — syntax, types, control structures |
| `/jfiles` | Working with jFiles JSON serialization in Clarion |
| `/evaluate-code` | Analyzing Clarion application code for issues |
| `/ClarionCOM` | Interactive COM development workflow |
| `/clarioncom-create` | Creating new C# COM controls for Clarion |
| `/clarioncom-build` | Building COM projects with MSBuild |
| `/clarioncom-validate` | Validating RegFree COM compliance |
| `/clarioncom-deploy` | Generating deployment artifacts |
| `/clarioncom-webview2-create` | Creating WebView2-based COM controls |
| `/clarioncom-webview2-build` | Building WebView2 COM projects |
| `/clarion-ide-addin` | Creating Clarion IDE addins (SharpDevelop) |
| `/clarion-analyze` | Analyzing code generation traces for patterns |
| `/clarion-benchmark` | Benchmarking Clarion code generation quality |
| `/clarion-template-dll` | Creating Windows DLLs callable from Clarion templates |

## COM Control Development

When creating COM controls for Clarion:
- Always use **Registration-Free COM** (manifest-based, no regsvr32)
- Target **.NET Framework 4.8** (not .NET Core/.NET 5+)
- Use `[ComVisible(true)]`, `[ClassInterface(ClassInterfaceType.None)]`
- Define explicit interfaces (IMyControl) and event interfaces (_IMyControlEvents)
- Use `/clarioncom-create` for the full scaffold
- See the COM patterns reference for proven patterns from successful implementations

## Clarion IDE Integration

The Clarion IDE is built on SharpDevelop. When the `clarion-ide` MCP server is available:
- `mcp__clarion-ide__get_active_file` — get the currently open file
- `mcp__clarion-ide__get_selected_text` — get selected text
- `mcp__clarion-ide__read_file` / `write_file` — read/write files through the IDE
- `mcp__clarion-ide__analyze_class` — analyze Clarion class structure
- `mcp__clarion-ide__generate_clw` / `generate_stubs` — code generation

## File Conventions

| Extension | Purpose |
|-----------|---------|
| `.clw` | Clarion source code (implementation) |
| `.inc` | Clarion include files (declarations) |
| `.app` | Clarion application dictionary |
| `.txa` | Text export of .app (editable) |
| `.dct` | Data dictionary |
| `.tpl` | Template files |
| `.trn` | Translation files |
