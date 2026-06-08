---
name: lsp-diagnostics
description: Run LSP diagnostics across all source files in the open Clarion solution. Reports errors, warnings, and info grouped by file with click-to-navigate support.
version: 1.0.0
user_invocable: true
---

# LSP Diagnostics

Validate all source files in the open Clarion solution using the Language Server. Reports compilation errors, warnings, and informational diagnostics across every .clw and .inc file.

## Trigger

- `/lsp-diagnostics` command
- "Check my solution for errors"
- "Run diagnostics on the solution"
- "Are there any compile errors?"

## Workflow

### Phase 1: Verify Prerequisites

1. Use `get_solution_info` to confirm a solution is open. If not, tell the developer:
   > No solution is open. Please open a Clarion solution first.
2. Use `lsp_debug_status` to confirm the LSP server is running. If not:
   - Use `lsp_start` to start it
   - If it fails, tell the developer the LSP server couldn't be started

### Phase 2: Gather Source Files

1. Use `get_project_source_files` to get all source files (.clw, .inc) with their absolute paths, grouped by project
2. Count total files and report:
   > Scanning **N** source files across **M** projects...

### Phase 3: Run Diagnostics

For each source file returned by `get_project_source_files`:

1. Call `lsp_diagnostics` with the file's absolute path
2. Collect the results:
   - If `pending: true` — the server timed out on this file. Mark it as "pending" and move on
   - If `count: 0` — file is clean, no issues
   - If `count > 0` — collect all diagnostics with severity, line, character, and message

**Batch efficiently**: call `lsp_diagnostics` on multiple files without waiting between calls where possible.

### Phase 4: Report Results

Present a summary report organized by severity, then by file:

**Summary line:**
> **Solution diagnostics**: X errors, Y warnings, Z info across N files (P files clean)

If there are **no issues at all**:
> Solution is clean — no errors or warnings found across all N files.

If there are issues, group by severity then by file:

**Errors** (severity 1) — list first, these block compilation:
```
FILE: path\to\file.clw
  Line NN: error message
  Line NN: error message
```

**Warnings** (severity 2):
```
FILE: path\to\file.clw
  Line NN: warning message
```

**Info** (severity 3) and **Hints** (severity 4) — summarize count only unless the developer asks for details.

For each file with errors or warnings, use the **short path** (project-relative) for readability but keep the absolute path available for navigation.

### Phase 5: Navigate (on request)

After presenting the report, if the developer asks to look at a specific error or file:

1. Use `open_file` with the absolute path and the error's line number to navigate them directly there
2. Offer to help fix the issue if they want

## Handling Pending Files

If any files returned `pending: true`:
- Report them separately at the end:
  > **Pending** (LSP still analyzing): file1.clw, file2.clw
- Offer to retry: "Want me to re-check the pending files?"
- On retry, call `lsp_diagnostics` again for just those files

## Critical Rules

1. **ALWAYS use `get_project_source_files`** to resolve file paths. Never guess or construct paths manually.
2. **ALWAYS check `get_solution_info` first.** Don't run diagnostics without an open solution.
3. **Treat `pending: true` as "unknown", not "clean".** Always report pending files separately.
4. **Show short paths in the report** for readability, but use absolute paths with `open_file` for navigation.
5. **Don't auto-fix anything.** This skill reports only. If the developer wants fixes, they'll ask — then follow rule #9 from CLAUDE.md (show code, get approval, then apply).
6. **If `get_project_source_files` returns empty**, the solution may not have source generated yet. Tell the developer:
   > No source files found. You may need to generate source first (Build > Generate All Source).
