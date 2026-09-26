# TXA Grammar

Write TXA as **CRLF, plain ASCII, no BOM**.

## Minimal working skeleton (PROVEN)

This 955-byte file, written from scratch with no export involved, imported via `/ai`, generated, and compiled to a working 239 KB `.exe` with a visible ABC window.

```
[APPLICATION]
VERSION 34
TODO ABC ToDo
PROCEDURE Main
[COMMON]
FROM ABC
MODIFIED '2026/08/05' '11:00:00'
[PROJECT]
#noedit
#system win32 exe
#model clarion dll
#compile "HELLO3.clw" -- GENERATED
#compile "HELLO3001.clw" -- GENERATED
#pragma link("C%V%DF%X%.LIB") -- GENERATED
#link "HELLO3.exe"
[PROGRAM]
NAME 'HELLO3.clw'
[COMMON]
FROM ABC ABC
MODIFIED '2026/08/05' '11:00:00'
[END]
[MODULE]
NAME 'HELLO3001.clw'
[COMMON]
FROM ABC GENERATED
MODIFIED '2026/08/05' '11:00:00'
[PROCEDURE]
NAME Main
[COMMON]
FROM ABC Window
MODIFIED '2026/08/05' '11:00:00'
[WINDOW]
MainWindow WINDOW('Hand-written TXA'),AT(,,240,120),FONT('Segoe UI',10),CENTER,SYSTEM,GRAY,DOUBLE
       STRING('text'),AT(20,30,200,12),USE(?Msg),CENTER
       BUTTON('&Close'),AT(95,85,50,14),USE(?Close),STD(STD:Close)
     END
[END]
[END]
```

## Which blocks take `[END]`

**Only `[PROGRAM]` and `[MODULE]`.**

- `[PROCEDURE]` takes **NO** `[END]` — a procedure ends where the next `[PROCEDURE]`, or the module's `[END]`, begins.
- `[COMMON]` is a self-terminating property block — no `[END]`.
- `[ADDITION]` takes **NO** `[END]`.

**Why the `[PROCEDURE]` rule hides:** with one procedure per module the stray `[END]` is tolerated at EOF, so single-procedure apps work by luck. With two procedures the extra `[END]` desyncs the parser — the second `[MODULE]` is absorbed, its procedure lands in the app as `UpdateContact PROCEDURE !Procedure not yet defined`, and its `.clw` is never generated. **Exit codes stay 0 through both `/ai` and `/ag`.**

## Header rules

- **`TODO ABC ToDo` must come BEFORE `DICTIONARY 'x.dct'`.** Reversed, import returns exit 0 and then silently drops every later section — extensions, procedures, everything.
- **`DICTIONARY ''` (empty) is INVALID** → `error GENE000: Could not load dictionary`. For a dictionary-less app, **omit the `DICTIONARY` line entirely**. Note the app is still created even when this error fires, so check the exit code, not just file existence.

## `FROM ABC <TemplateName>` bindings

`FROM ABC <Name>` binds an object to a template:

| Context | Binding |
|---|---|
| `[APPLICATION]` | `FROM ABC` |
| `[PROGRAM]` | `FROM ABC ABC` |
| `[MODULE]` | `FROM ABC GENERATED` |
| Procedure — window | `FROM ABC Window` |
| Procedure — frame | `FROM ABC Frame` |
| Procedure — report | `FROM ABC Report` |
| Procedure — source | `FROM ABC Source` |
| Procedure — browse | `FROM ABC Browse` |

## `[PROJECT]`

Holds the legacy pragma project definition (`#system`, `#model`, `#compile`, `#pragma link`, `#link`). It drives the compile/link list and **must name the same modules** as `[PROGRAM]`/`[MODULE]`. It is also the source of truth for authoring the `.cwproj`.

## Template prompts are optional — mostly

Prompts can be **omitted entirely** and Clarion supplies defaults. Prefer that: a partial prompt set is worse than none (see `failure-signatures.md`).

The known exceptions, where nothing else can supply the information:

- **`%UpdateProcedure`** on `ABC BrowseUpdateButtons` — names the form procedure.
- **`%ButtonAction` / `%ButtonProcedure` / `%ButtonThread`** on a frame — name the target of each menu item.

### `[PROMPTS]` ownership is POSITIONAL

**A `[PROMPTS]` block belongs to the `[ADDITION]` it FOLLOWS.** A procedure's own prompts must come *before* the first `[ADDITION]`. Canonical order:

```
[FILES]        [PRIMARY] / [INSTANCE] / [KEY] / [OTHERS]
[PROMPTS]      <- the PROCEDURE's own prompts
[ADDITION]     NAME ABC BrowseBox / [INSTANCE] INSTANCE 1 / PROCPROP
[PROMPTS]      <- BrowseBox's prompts (range limit lives HERE)
[ADDITION]     NAME ABC BrowseUpdateButtons / INSTANCE 2 / PARENT 1 / PROCPROP
[PROMPTS]      <- BrowseUpdateButtons' prompts (%UpdateProcedure lives HERE)
[WINDOW]
```

Prompts placed after the additions are handed to the **last** addition; if that template does not define the symbol they are **silently dropped** — `/ai` and `/ag` exit 0, it compiles, it runs, the feature is absent. This is why `%UpdateProcedure` appears to "work" after the additions (it genuinely belongs to `BrowseUpdateButtons`, by luck) while a `%ButtonAction` family in the same position does nothing.

A **frame has no additions**, so its prompts work anywhere.

## `[FILES]` and `[INSTANCE]`

`[INSTANCE]` inside a procedure's `[FILES]` binds the file to **control-template instance N** — the same numbering space as `[ADDITION]`/`INSTANCE n` and `#SEQ(n)`. **0 means the procedure itself.**

| Procedure kind | `[INSTANCE]` |
|---|---|
| Browse | 1 (BrowseBox is instance 1 and owns the file) |
| Form | 1 (SaveButton is instance 1; instance 2 = CancelButton, owns no file) |
| Report / Process | 0 (no control templates exist to own the file) |

**Table count is irrelevant.** (An older rule — "use instance 1 when the dictionary has a single table" — produces working output for the wrong reason.) `[INSTANCE]` is mandatory; omitting the block fails import with exit 1.

`[FILES]` is **per-procedure**, not app-global.

## Control metadata: `#ORIG`, `#SEQ`, `#FIELDS`, `#LINK`

For controls with **no control template attached**, this metadata is output-only — Clarion adds it on export and it is not required on import.

**For control-template controls it is REQUIRED.** It is the binding between the control and its template instance:

```
LIST,AT(...),USE(?Browse:1),HVSCROLL,FORMAT('...'),FROM(Queue:Browse:1),IMM,
     #FIELDS(CON:ID,CON:Name,CON:Email),#ORIG(?List),#SEQ(1)
```

- **`#SEQ(n)` is the INSTANCE number of the owning control template.** That is the entire linkage rule. `#SEQ(1)` = this LIST belongs to BrowseBox instance 1; `#SEQ(2)` = these Insert/Change/Delete buttons belong to BrowseUpdateButtons instance 2.
- **`#ORIG(?Name)`** is the template's internal control name.

## Embeds round-trip

Embeds export with full addressing, so placement is preserved, not just the code:

```
WHEN 'Init'
[INSTANCES]
WHEN '(),BYTE'
[DEFINITION]
[SOURCE]
PROPERTY:BEGIN
PRIORITY 2500
PROPERTY:END
MESSAGE('I am here')
```

An app exported to TXA and re-imported into an **empty** directory rebuilds functionally identical: hand-added embeds, per-instance prompts (four WindowResize instances each preserved individually), range limits, and all `.INC` module maps byte-identical.

**Functionally exact, not byte-exact.** One systematic difference: `SELF.AddItem(Toolbar)` generates two lines later on the round trip, because the export writes the app's *internal* addition order rather than the authored order. Same `.exe` size, different bytes — benign, but drift-detection tooling sees a one-off diff in every window procedure the first time.

## A single-procedure export is not a fragment

It starts at `[PROCEDURE]` with no `[APPLICATION]` header, no `[MODULE]` wrapper, no `[PROJECT]`, and no trailing `[END]`. It cannot be dropped into another TXA as-is.
