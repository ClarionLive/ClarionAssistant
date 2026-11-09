---
name: clarion
# prettier-ignore
description: Clarion language reference - critical syntax rules, compile-verified pitfalls, data structures, classes, windows and events, COM controls from Clarion, and template-authoring gotchas (#AT/#IF nesting, OMITTED scope). Auto-applies when working with Clarion source code or .tpl/.tpw template files.
version: 1.0.0
---

# Clarion Language Programming Skill

You are an expert Clarion language programmer. This file holds the most critical rules; detailed syntax, examples, and full gotcha catalogs live in `references/` (in this skill's directory) — read the relevant file before writing non-trivial code in that area.

## File Conventions (CRITICAL)

**Always write Clarion source files (`.clw`, `.inc`, `.equ`, `.int`, `.tpl`, `.tpw`, `.txa`) with CRLF line endings** — actual CR (0x0D) + LF (0x0A) bytes, NOT the literal two-character sequence `\r\n`. LF-only files can cause parser errors, broken embed markers, or silent corruption when the IDE rewrites them.

**Which write paths give you CRLF:**
- **Already CRLF:**
  - `mcp__clarion-assistant__write_file` and `append_to_file` convert every line ending to CRLF (no BOM) for `.clw`, `.inc`, `.equ`, `.int`, `.trn`, `.tpl` and `.tpw`. They do NOT interpret escape sequences, so write real line breaks: `\r\n` typed as text lands on disk as four literal characters.
  - The editor tools (`insert_text_at_cursor`, `replace_text`, `replace_range`).
  - `write_embed_content` and `apply_embed_edits`.
  - Saves from the CA Editor and the CA Embeditor.
- **NOT CRLF:**
  - `write_file` on a `.txa` (it is not in that list).
  - Claude Code's built-in `Write` and `Edit` tools, which emit **LF only** on Windows.

  After writing a Clarion file with one of these, convert LF to CRLF before compiling or importing: `unix2dos`, or read the text, replace LF with CRLF and write it back.

**Gotcha (verified on Clarion 12):** LF-only files do not compile:
- the whole file is reported as line 1 with a huge column number;
- `Illegal character` appears at every line break;
- the compiler expects `<ID> <LINEBREAK> ; CODE INCLUDE OMIT SECTION COMPILE PRAGMA GROUP ITEMIZE MAP`.

LF-only template code breaks the same way.

## Working With Apps (ALWAYS)

- **One app open at a time.** Before you `open_file` an `.app`, call `get_open_files`. If another `.app` is open, ask the developer to save and close it first, and do not open the second one until they have. The Clarion IDE is a 32-bit program limited to 2 GB of memory (4 GB with the LargeAddressAware `ClarionLAA.exe`). Two large apps can use all of it, and an out-of-memory halt can damage the app being edited and the template registry.
- **Closing an app is the developer's job, and it is always saved.** Never close an `.app` tab yourself. `close_file` closes the active tab without the IDE's save prompt. Ask the developer to close it and to answer **Yes** to saving. If you changed the app (embeds, `import_txa`), say so, so that they know there is something to save. A procedure's embeditor is different: close it with `save_and_close_embeditor`, never `cancel_embeditor`, unless the developer asks to discard.
- **If Clarion offers to recover an app** (after a crash it may offer the last save or a recovery copy), that is the developer's decision. Tell them the dialog is up, and wait.

## Finding Clarion Files (ALWAYS)

**ALWAYS find Clarion files through the active .red** (`resolve_red_path`; `get_red_search_paths` to search inside them). Never use a path from memory or an earlier answer, and never search drives: files move, and the .red is the only source of truth.

- The build's Clarion comes from `get_solution_info` (`versionName`, `clarionRoot`, `activeRedFile`) — not from memory or the IDE's install folder; Build > Set Clarion Version can point the solution at a different Clarion.
- `resolve_red_path` finds ANY file the compiler or template engine would: `.tpl`, `.tpw`, `.inc`, `.clw`, `.equ`, `.trn`, `.lib`, `.dll`, `.ico`, ...
- To search inside them (e.g. which template writes X), Grep only `get_red_search_paths`'s `absoluteSearchPaths` for that extension, in order — earlier folders take precedence. Stale copies elsewhere on disk give wrong answers.
- In a multi-project solution pass `project_name`. An answer with `alsoFoundIn` means the file also resolves under another build configuration's section: say which one you used.

## Top Critical Syntax Rules

1. **Strings use single quotes** (`'Hello'`); embedded quote is doubled (`'Don''t'`). Never double quotes. Comments start with `!`. Concatenate with `&`.
2. **No statement-terminating periods** — statements end at newline. Only exception: single-line IF (`IF x > 0 THEN RETURN.`).
3. **Labels (variables, procedures, structures) MUST start in column 1**; executable code is indented.
4. **Every block structure (IF, LOOP, CASE, ACCEPT, GROUP, QUEUE, WINDOW...) needs END.** One END closes a WINDOW together with its controls; a second END does not compile. Container controls (SHEET, TAB, OPTION, GROUP, OLE, MENUBAR, MENU, TOOLBAR) take their own END.
5. **.clw file order:** `MEMBER` first, then optional `MAP/END`, then `INCLUDE` statements, then implementations. Class method implementations MUST be prefixed: `MyClass.Init PROCEDURE`. Use `MEMBER('Parent.clw')` (never bare) in a module whose procedures are prototyped in the parent's global MAP; bare `MEMBER` is correct only in a class-implementation .clw.
6. **PROCEDURE with no parameters takes NO parentheses** (`MyProc PROCEDURE`, not `PROCEDURE()`). `CODE` goes on its own indented line after local declarations, never on the PROCEDURE line.
7. **References use `&=`**, not `=` (`MyRef &= MyObject`, `MyRef &= NULL`). `NEW` allocates, and every NEW needs a matching `DISPOSE`. `DISPOSE(MyRef)` also sets `MyRef` to NULL: no `MyRef &= NULL` afterwards, and a second `DISPOSE(MyRef)` does nothing. Only that variable is cleared — any other reference to the same object now points to freed memory, so only the owner of a shared object disposes it.
8. **Parameters:** `*TYPE` = by reference (not `&`), `<TYPE x>` = omittable (not `?`), checked with `OMITTED(n)`. Return type follows the parameter list: `PROCEDURE(LONG x),STRING`; add `,PROC` to allow ignoring the return value.
9. **ROUTINEs are called with `DO RoutineName`** — never with parentheses or as a bare name. They share the procedure's locals; no parameters, no return values.
10. **CLEAR the queue buffer before ADD** (other fields keep garbage). SORT takes field references with +/- prefixes: `SORT(Q, +Q.Name, -Q.Value)` — never a string field name.
11. **Reserved words cannot be identifiers** (ACCEPT, CASE, CODE, DATA, END, LOOP, NEW...). `SELF`/`PARENT` cannot name locals or parameters in class methods. Full lists in references/syntax-basics.md.
12. **Use ACCEPT (not plain LOOP/ACCEPTED) for window event processing:** `ACCEPT / CASE EVENT() / OF EVENT:Accepted / CASE FIELD()...`.
13. **Functions with a return type must return on every path** — no falling off the end.
14. **COM methods use direct brace syntax** `ctrl{'MethodName()'}` — never `ctrl{PROP:OLE} = '...'` (unreliable). COM property names are case-sensitive.
15. **Template files (.tpl/.tpw): `#AT` cannot be nested inside `#IF`** — put the `#IF` INSIDE the `#AT` body. And `OMITTED()` only works in the scope where the parameter is declared (fails silently inside ABC class methods) — stash params into procedure-locals at top level. Details and working embeds in references/templates.md.

16. **`DECIMAL` cannot be a value parameter or a return type** (compile errors). Use `*DECIMAL` by reference, or `REAL`/`LONG`. There is no `POWER()` — use `(2) ^ (n)`. Calling a returning procedure as a statement without `,PROC` (including `GETINI`) draws the warning `Calling function as procedure` — add `,PROC` to the prototype or assign the result.
17. **`OMIT('token')` ends at the first occurrence of that exact text anywhere, even inside a `!`/`!!` comment.** `MODULE` blocks belong inside a `MAP`, and the `MODULE('name')` must match the .clw that defines the procedure.

## When Generating Clarion Examples

Use realistic Clarion-convention names, show complete context (declare all variables), align property assignments, comment with `!`, and follow proper indentation.

## References

All paths are relative to this skill's directory. Read the file whose topic matches the task before writing code in that area:

- **references/syntax-basics.md** — Data types, procedure and control-structure shapes, reserved-word lists, .clw/.inc layout, parameters, ROUTINE, INCLUDE/OMIT/COMPILE, naming. Read for general source authoring.
- **references/data-structures.md** — FILE/RECORD/KEY, OPEN/CLOSE, QUEUE operations, GROUP/LIKE, built-in functions. Read when working with files, queues, groups or built-ins.
- **references/classes.md** — CLASS declaration and implementation, inheritance, VIRTUAL/PARENT, reference variables. Read when writing or deriving classes.
- **references/windows-events.md** — ACCEPT loop, EVENT: constants, PROP: syntax, WINDOW definitions and their END rules. Read when building windows or handling events.
- **references/com-controls.md** — Using COM/OLE controls from Clarion: PROP:Create, properties, methods, OCXREGISTEREVENTPROC. Building a .NET COM control is the clarioncom-control skill.
- **references/templates.md** — #AT/#IF nesting, the OMITTED() scope trap, embeds that silently do nothing. Read before writing or editing .tpl/.tpw files.
- **references/ai-pitfalls.md** — Compile-verified rules with the exact compiler messages, plus plausible-sounding rules that are false. Read before writing or reviewing non-trivial code.
- **references/common-mistakes.md** — Wrong-vs-right examples for the rules above. Read when reviewing or debugging Clarion code.
