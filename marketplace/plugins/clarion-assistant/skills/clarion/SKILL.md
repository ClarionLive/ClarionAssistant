---
name: clarion
# prettier-ignore
description: Clarion language programming reference with syntax rules, data types, control structures, Windows API integration patterns, and template-authoring gotchas (#AT, #IF, OMITTED scope). Auto-applies when working with Clarion source code or .tpl/.tpw template files. Uses parallel operations where applicable.
version: 1.0.0
---

# Clarion Language Programming Skill

You are an expert Clarion language programmer. This file holds the most critical rules; detailed syntax, examples, and full gotcha catalogs live in `references/` (in this skill's directory) — read the relevant file before writing non-trivial code in that area.

## File Conventions (CRITICAL)

**Always write Clarion source files (`.clw`, `.inc`, `.equ`, `.int`, `.tpl`, `.tpw`, `.txa`) with CRLF line endings** — actual CR (0x0D) + LF (0x0A) bytes, NOT the literal two-character sequence `\r\n`. LF-only files can cause parser errors, broken embed markers, or silent corruption when the IDE rewrites them.

**Gotcha:** `mcp__clarion-assistant__write_file` does NOT interpret escape sequences — passing `\r\n` in content writes those four literal characters to disk. Embed real newline bytes, or use Claude Code's built-in `Write` tool (native Windows line endings). After writing, read the file back and confirm no literal `\r\n` sequences.

## Top Critical Syntax Rules

1. **Strings use single quotes** (`'Hello'`); embedded quote is doubled (`'Don''t'`). Never double quotes. Comments start with `!`. Concatenate with `&`.
2. **No statement-terminating periods** — statements end at newline. Only exception: single-line IF (`IF x > 0 THEN RETURN.`).
3. **Labels (variables, procedures, structures) MUST start in column 1**; executable code is indented.
4. **Every block structure (IF, LOOP, CASE, ACCEPT, GROUP, QUEUE...) needs END.** A WINDOW needs TWO ENDs: first closes the control list, second closes the WINDOW.
5. **.clw file order:** `MEMBER` first, then optional `MAP/END`, then `INCLUDE` statements, then implementations. Class method implementations MUST be prefixed: `MyClass.Init PROCEDURE`.
6. **PROCEDURE with no parameters takes NO parentheses** (`MyProc PROCEDURE`, not `PROCEDURE()`). `CODE` goes on its own indented line after local declarations, never on the PROCEDURE line.
7. **References use `&=`**, not `=` (`MyRef &= MyObject`, `MyRef &= NULL`). `NEW` allocates, and every NEW needs a matching `DISPOSE`.
8. **Parameters:** `*TYPE` = by reference (not `&`), `<TYPE x>` = omittable (not `?`), checked with `OMITTED(n)`. Return type follows the parameter list: `PROCEDURE(LONG x),STRING`; add `,PROC` to allow ignoring the return value.
9. **ROUTINEs are called with `DO RoutineName`** — never with parentheses or as a bare name. They share the procedure's locals; no parameters, no return values.
10. **CLEAR the queue buffer before ADD** (other fields keep garbage). SORT takes field references with +/- prefixes: `SORT(Q, +Q.Name, -Q.Value)` — never a string field name.
11. **Reserved words cannot be identifiers** (ACCEPT, CASE, CODE, DATA, END, LOOP, NEW...). `SELF`/`PARENT` cannot name locals or parameters in class methods. Full lists in references/syntax-basics.md.
12. **Use ACCEPT (not plain LOOP/ACCEPTED) for window event processing:** `ACCEPT / CASE EVENT() / OF EVENT:Accepted / CASE FIELD()...`.
13. **Functions with a return type must return on every path** — no falling off the end.
14. **COM methods use direct brace syntax** `ctrl{'MethodName()'}` — never `ctrl{PROP:OLE} = '...'` (unreliable). COM property names are case-sensitive.
15. **Template files (.tpl/.tpw): `#AT` cannot be nested inside `#IF`** — put the `#IF` INSIDE the `#AT` body. And `OMITTED()` only works in the scope where the parameter is declared (fails silently inside ABC class methods) — stash params into procedure-locals at top level. Details and working embeds in references/templates.md.

## When Generating Clarion Examples

Use realistic Clarion-convention names, show complete context (declare all variables), align property assignments, comment with `!`, and follow proper indentation. Full guidelines in references/syntax-basics.md.

## References

All paths are relative to this skill's directory. Read the file whose topic matches the task before writing code in that area:

- **references/syntax-basics.md** — Core syntax (types, procedures, IF/CASE/LOOP, strings), full reserved-word lists, .clw/.inc file structure, label/termination rules, parameter passing, ROUTINE/DO, INCLUDE/OMIT/COMPILE, naming conventions, example-generation guidelines. Read for any general Clarion source authoring.
- **references/data-structures.md** — FILE/RECORD/KEY declarations, OPEN/CLOSE with error checks, QUEUE operations (ADD/GET/PUT/DELETE/SORT/FREE), GROUP/LIKE, and the built-in function catalog (string, numeric, system, file/queue). Read when working with files, queues, groups, or built-in functions.
- **references/classes.md** — CLASS declaration (.inc) and implementation (.clw), inheritance/VIRTUAL/PARENT, reference variables, NEW/DISPOSE. Read when writing or deriving classes.
- **references/windows-events.md** — Full ACCEPT loop pattern, EVENT: constants, PROP:xxx property syntax, WINDOW definitions (two-END rule, hidden OLE controls). Read when building windows or handling UI events.
- **references/com-controls.md** — Using COM/OLE from Clarion (PROP:Create, property/method calls, OCXREGISTEREVENTPROC event handling) AND building .NET COM controls for Clarion (RegFree COM only, UserControl inheritance requirement, interfaces/GUIDs/csproj, checklist, complete working example). Read for anything COM-related.
- **references/templates.md** — Template authoring gotchas in depth: #AT/#IF nesting, OMITTED() scope trap with the %BeforeWindowManagerRun fix, embeds that silently don't work (%LocalProcedureSetup, %ProcedureSetup). Read before writing/editing .tpl/.tpw files.
- **references/common-mistakes.md** — Full wrong-vs-right catalog (strings, periods, labels, MEMBER order, END counts, &=, queue CLEAR, parameter markers, ACCEPT vs LOOP, ROUTINE calls, class prefixes, RETURN paths, NEW/DISPOSE). Read when reviewing or debugging Clarion code.
