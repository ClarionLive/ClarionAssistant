# GROUP/RECORD keyword-as-label diagnostics regression fixture

Repro for `ModernEmbeditorDiagnostics.cs`'s per-slot structure-balance checker (`Compute()`,
Pass 2). Covers the `GROUP`-keyword regression found while reviewing the
`fix/structure-keyword-as-label-embed-diagnostics` branch (the same fix that resolved the original
`REPORT`/`WINDOW`/`QUEUE`/`RECORD` false positives): `GROUP` was added to `DeclarationStructKeywords`
on the same "always requires a label" assumption as those nine keywords, but unlike them `GROUP` is
also legitimately written **bare**, nested inside a `WINDOW`/`APPLICATION`/`REPORT` body as a screen
control (identified by `USE()`, not a leading label) — the exact ambiguity already carved out for
`TOOLBAR`/`MENU`/`MENUBAR`/`SHEET`/`TAB`/`OPTION`, just missed for `GROUP`.

Confirmed against real production Clarion source: two bare `GROUP('...'),AT(...),USE(?X),BOXED`
screen controls nested in the same `WINDOW` desync the per-slot balance stack, making the second
`GROUP`'s own, genuinely-terminated `END` misreport as
`"END has no matching structure in this embed slot."`. This fixture reproduces that shape without
any project-specific names, paths, or control layout.

## Why this fixture exists

The blunt fix (just deleting `"GROUP"` from `DeclarationStructKeywords`) resolves the regression
above but **reopens** the original bug for `GROUP` specifically: a plain identifier declared bare
as `Group           &STRING` (mirroring the confirmed `Report &STRING` / `Window &STRING` pattern
that motivated the original fix) would then get treated as an unterminated structure opener. The
validated fix instead gives `GROUP` its own tight-lookahead regex (`GroupOpen`, modeled on
`ToolbarOpen`/`NestedBandOpen` but keeping `StructOpen`'s optional leading-label group, since unlike
Toolbar/Option, `GROUP` **can** legitimately be labeled) and removes it from both `StructOpen`'s
alternation and `DeclarationStructKeywords`.

Due-diligence during the same review found `RECORD` has the identical class of gap — an
anonymous `RECORD, PRE()` inside a `FILE`. **Confirmed and fixed** (2026-07-28): the Clarion
Language Reference lists the `RECORD` label as optional, the fixture (restructured into a full
compiling `PROGRAM`) was built and run successfully confirming cases E/E2 are valid syntax, and the
live IDE reproduced the exact predicted symptom — an anonymous `RECORD` inside a `FILE` desyncing
the balance stack the same way a bare screen `GROUP` did, making the `FILE`'s own closing `END`
misreport as unmatched. Fixed with the same shape as `GROUP`: a dedicated `RecordOpen` regex
(`^\s*(?:[A-Za-z_][A-Za-z0-9_:]*\s+)?(RECORD)(?=\s*(?:[(,!]|$))`), `RECORD` removed from both
`StructOpen`'s alternation and `DeclarationStructKeywords`.

The fixture is now a complete, compiling, runnable Clarion `PROGRAM` (not just an isolated
procedure fragment) — build and run it directly to sanity-check any candidate fix against the real
compiler, not just the diagnostics checker.

### Related finding: `Group PROCEDURE()` as a global procedure label (different component)

Case G exercises a **separate component entirely** — not `ModernEmbeditorDiagnostics.cs` in
`ClarionAssistant`, but `LabelDiagnostics.ts` (`validateReservedKeywordLabels`) in the
**`Clarion-Extension`** language server. A top-level, non-nested `Group PROCEDURE()` (declared in
`MAP` and defined normally, called as `Group()`) **compiles and runs correctly** in real Clarion —
confirmed by building and running this fixture — but the LSP flags it as an error:

> `'Group' cannot be the label of a PROCEDURE or FUNCTION declaration.`

Root cause: `LabelDiagnostics.ts`'s `STRUCTURE_ONLY` set includes `GROUP` (alongside 27 other
keywords: `APPLICATION, CLASS, CODE, DATA, DETAIL, FILE, FOOTER, FORM, HEADER, ITEM, ITEMIZE, JOIN,
MAP, MENU, MENUBAR, MODULE, OLE, OPTION, QUEUE, PARENT, RECORD, REPORT, SELF, SHEET, TAB, TOOLBAR,
VIEW, WINDOW`), and only suppresses the "cannot be the label of a PROCEDURE or FUNCTION
declaration" diagnostic when the label sits **inside an enclosing structure** (e.g. a method
declared inside a `CLASS`/`INTERFACE`). It doesn't account for the label being legal at **global,
non-nested** scope too — the same underlying misconception as the `ModernEmbeditorDiagnostics.cs`
bug (treating a context-disambiguated identifier as reserved-except-in-one-specific-context), just
manifesting in a different checker, in a different repo.

**Scope note**: only `GROUP` has been confirmed here via an actual compile+run. The other 27
`STRUCTURE_ONLY` keywords have NOT been individually re-verified for this same global-PROCEDURE-
label behavior — do not assume they share it without testing each one; some (e.g. `SELF`, `PARENT`)
seem unlikely to. Not yet fixed; likely warrants its own `Clarion-Extension` PR rather than folding
into the `ClarionAssistant` `GroupOpen` fix, since it's a different repo and a different checker.

Case H (`GroupTestClass.Group`, a `CLASS` method literally named `Group`) exercises the checker's
OWN existing, already-documented exception for this ambiguity (`findEnclosingStructure` — the code
comments already give `Join PROCEDURE()` inside a `CLASS` as an example of allowed usage). **Confirmed**
(2026-07-28, compiled + zero diagnostics on the method label): this path already works correctly —
the bug is specifically the global/non-nested case (G), not the in-structure case (H).

## Contents (`GroupRecordRepro.clw`)

Every structure in the fixture is correctly terminated — the checker must produce **zero**
diagnostics once the fix is correctly applied.

| Case | What it exercises | Must NOT be flagged because |
|---|---|---|
| A / A2 | Bare screen `GROUP('Title'),AT(...),USE(?X),BOXED` nested in a `WINDOW` | The actual regression — opener must be pushed |
| B | Labeled group, `GROUP` immediately followed by `,` | Already worked pre-regression — must keep working |
| B2 | Labeled group, space before the paren (`GROUP (Type), NAME(...)`) | Real syntax variant that breaks a too-tight candidate regex if `\s*` isn't allowed before the punctuation |
| C | Bare, attribute-less labeled group (`Label GROUP` then fields) | Trailing lookahead must accept end-of-line, not just punctuation |
| D | Labeled group with a trailing inline comment on the opener line | `Sanitize()` must blank the comment before the regex runs |
| E | Anonymous `RECORD, PRE()` inside a `FILE` | Same bare-structure ambiguity as GROUP — **confirmed and fixed** via a dedicated `RecordOpen` regex, same shape as `GroupOpen` |
| E2 | Labeled `RECORD` inside a `FILE` | Contrast/negative control — already correct |
| F | `Window &STRING` / `Group &STRING` / `Report &STRING` — plain identifiers, no structure | **The original bug.** Must NOT push an opener. Regresses under the blunt "just delete from the set" fix |
| G | `Group PROCEDURE()` — a global, non-nested procedure named `Group` | **Different bug, different component** (`Clarion-Extension`'s `LabelDiagnostics.ts`, not `ModernEmbeditorDiagnostics.cs`). Compiles and runs; the LSP incorrectly flags it as an error — see below |
| H | `Group PROCEDURE()` declared/implemented as a `CLASS` method (`GroupTestClass.Group`) | Exercises `LabelDiagnostics.ts`'s EXISTING `findEnclosingStructure` exception — **confirmed**: compiles cleanly, zero diagnostics on the method label (unlike case G) |

## Verify

Open `GroupRecordRepro.clw` in the Clarion IDE (native Monaco source-code editor runs the same
per-slot heuristic as the Modern Embeditor for plain `.clw`/`.inc` files) and confirm there are no
"END has no matching structure in this embed slot" (or any other structure-balance) diagnostics
anywhere in the file.

- **Before either fix**: cases A/A2 desync the stack (phantom diagnostic on the `END` closing the
  second `GROUP` control, confirmed live), and case E's anonymous `RECORD` desyncs it too (phantom
  diagnostic on the `FILE`'s own closing `END`, also confirmed live).
- **With only the blunt fix** (keyword deleted from `DeclarationStructKeywords`, no dedicated
  regex): case F (`Group &STRING`) starts falsely flagging as an unterminated structure.
- **With the full `GroupOpen` + `RecordOpen` fix**: zero diagnostics (confirmed live, 2026-07-28).

Case G is verified separately, against `Clarion-Extension`'s language server (e.g. its VS Code
extension host) rather than the ClarionAssistant Modern Embeditor/native editor: open the fixture
where that LSP is active and confirm whether `Group PROCEDURE()`'s label is flagged as
`'Group' cannot be the label of a PROCEDURE or FUNCTION declaration.` — a real compiler build+run
(as done for this fixture) confirms it should NOT be an error.
