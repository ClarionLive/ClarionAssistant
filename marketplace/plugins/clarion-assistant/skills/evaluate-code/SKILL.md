---
name: evaluate-code
description: Evaluate Clarion application code for issues, improvements, and optimization. Exports fresh TXA, analyzes code, shows diffs for any changes, and applies via embeditor.
version: 1.0.0
user_invocable: true
---

# Evaluate Code

Interactive skill for evaluating Clarion application code. Guides the developer through analysis, review, and optional code improvements applied through the embeditor.

## Trigger

- `/evaluate-code` command
- "Evaluate Code" button on the Home page

## Workflow

### Phase 1: Scope

Ask the developer what they want to evaluate:

> **What would you like to evaluate?**
> 1. **Entire application** — all procedures in the open app
> 2. **Specific procedure** — name a procedure to focus on
> 3. **Embeditor code** — evaluate what's currently open in the embeditor
> 4. **Text editor file** — evaluate the file currently open in the text editor
> 5. **Selected code** — evaluate only the currently selected text

Wait for the developer's response before proceeding.

### Phase 2: Gather Code

Depending on the scope chosen:

**Option 1 (Entire app) or Option 2 (Specific procedure):**
1. Use `get_app_info` to get the app name and file path
2. Determine the TXA output path: same directory as the .app file, named `{AppName}_evaluate.txa`
3. Use `export_txa` to export:
   - If evaluating the entire app: export without specifying procedures
   - If evaluating a specific procedure: export with that procedure name (and its parent if it's a local procedure)
4. Confirm the export completed

**Option 3 (Embeditor code):**
1. Use `get_embed_info` to check if an embeditor is open and get context (procedure name, embed point)
2. Use `get_active_file` to read the current embed content
3. If no embeditor is open, tell the developer and ask them to open one first

**Option 4 (Text editor file):**
1. Use `get_active_file` first — this returns the focused text editor content
2. If the result is a source file (.clw, .inc, .equ, .int, .trn), use it directly
3. If the result is a .app or .dct file (not a text source), fall back: use `get_open_files` to list all open tabs, filter to text source files, and ask the developer which one to evaluate
4. If NO text source files are found anywhere, tell the developer: "No text source files are open. Please open a .clw or .inc file first, or choose a different scope."
5. If it's a .clw or .inc file, also use `analyze_class` for deeper class-level analysis

**Option 5 (Selected code):**
1. Use `get_selected_text` to get the currently selected text
2. If no text is selected or the result is empty, tell the developer and ask them to select some code first
3. Use `get_active_file` to get the file context (path, language) — if this returns a .app file, still use the selected text as-is (the selection might be from a different panel)
4. Use `get_cursor_position` to know where in the file the selection is

### Phase 3: Analyze

Read and analyze the code based on scope:

1. For options 1-2: **Read the TXA** to understand embed structure, procedure relationships, and template context, then **read the source files** (.clw/.inc) using `read_file`
2. For options 3-5: analyze the gathered code directly
3. For class-based code, use `analyze_class` and `sync_check` for deeper analysis

Evaluate for:
- **Bugs**: Logic errors, off-by-one, null/empty checks, resource leaks
- **Performance**: Unnecessary loops, repeated file access, missing indexes, N+1 patterns
- **Clarity**: Confusing variable names, overly complex logic, dead code
- **Clarion idioms**: Proper use of QUEUE operations, FILE handling, string functions, GROUP overlays
- **Thread safety**: THREAD variables, shared file access patterns
- **Error handling**: Missing error checks after file operations, ERRORCODE() usage

Present findings organized by severity:
- **BLOCKER** - Will cause crashes, data corruption, or wrong results
- **WARNING** - Likely problems or significant inefficiencies
- **SUGGESTION** - Improvements for readability, maintainability, or performance
- **INFO** - Observations, not necessarily problems

### Phase 4: Improve (if requested)

If the developer asks you to fix or improve any findings:

1. **Make a working copy** of the code being changed. Save it alongside the original with a descriptive suffix (e.g., `ProcedureName_optimized.clw`)
2. **Apply your changes** to the working copy
3. **ALWAYS show the diff** using `show_diff` with:
   - `original_file` pointing to the original source
   - `modified_file` pointing to your working copy
   - `ignore_whitespace` set to `true`
   - Descriptive `title` (e.g., "CreateTheClass - Performance Optimization")
4. Wait for the developer to review the diff. They may add notes via the diff viewer.
5. Use `get_diff_result` to check if they approved, submitted notes, or cancelled.

### Phase 5: Apply via Embeditor

After the developer approves changes (or you've addressed their review notes):

1. **Ask** the developer if they want to apply the changes to the app:
   > Would you like me to apply these changes to the app via the embeditor?
2. If yes:
   - Use the TXA (from Phase 2) to identify which embed points contain the code being changed
   - Parse the TXA to find the exact embed location: procedure name, embed point (e.g., `EMBED %ProcedureRoutines`), and any parent procedure
   - Use `open_procedure_embed` to open the embeditor for the target procedure
   - Use `get_embed_info` to confirm you're in the right embed
   - Navigate embeds with `next_embed`/`prev_embed` or `next_filled_embed`/`prev_filled_embed` to reach the correct embed point
   - Apply the changes using the editor operations (`replace_range`, `replace_text`, etc.)
   - Use `save_and_close_embeditor` to save
3. Confirm the changes were applied successfully

## Critical Rules

1. **For app/procedure scope, ALWAYS export a fresh TXA** at the start. Never rely on a stale export. For embeditor/editor/selection scopes, read from the IDE directly.
2. **ALWAYS show a diff** before applying any code changes. The developer must see what's changing.
3. **NEVER apply changes to the embeditor without asking first.** The developer decides.
4. **Use `ignore_whitespace: true`** in all diff views to avoid noisy whitespace-only differences.
5. **Keep the working copy files** so the developer has a reference. Don't delete them.
6. **If the developer has review notes**, address them and show a new diff before applying.
7. **One procedure at a time** when applying via embeditor. Don't try to batch multiple procedures.

## TXA Embed Structure Reference

In a TXA file, embeds appear as:
```
[EMBED]
  EMBED %EmbedPointName
    [INSTANCES]
      WHEN ''
        [DEFINITION]
          ! Actual embed code here
        [END]
    [END]
  [END]
[END]
```

Local procedures appear nested under their parent procedure. To find which embed contains specific code, search the TXA for the code text and look at the surrounding EMBED structure.

## Important

NEVER fabricate findings or use placeholder data. ALWAYS use the MCP tools to read real code from the IDE before producing any evaluation. If a tool call fails, tell the developer — do not make up results.