---
name: clarion-appgen
# prettier-ignore
description: Create a complete Clarion application from authored text — hand-write a TXA (and optional .dctx dictionary), then build it to a running .exe headlessly with ClarionCL (/ai, /ag) and MSBuild. Covers TXA section grammar, .dctx dictionary XML, browse/form/frame/report recipes, and the silent-failure traps that let a broken app exit 0. Use whenever creating an .app from scratch, scaffolding a Clarion app without the IDE, authoring or editing a .txa/.dctx file, generating apps in CI, or diagnosing a silently missing feature in an app generated from TXA text.
version: 1.0.0
---

# Clarion App Generation from Text

A complete, conventional Clarion application — dictionary, frame, browses, forms, reports, embeds — can be authored as plain text and built to a running `.exe` with no IDE interaction. This is proven, not theoretical: a 955-byte hand-written TXA compiles to a working 239 KB windowed app; 3,149 bytes of text (TXA + dictionary) produces a 533 KB ABC browse app.

`ClarionCL /ai` **creates** the `.app` if it does not exist. That is the capability everything here rests on.

## Read this first: the failure mode of this whole format family

**Nearly every mistake in TXA and DCTX authoring fails SILENTLY.** Import and generation exit 0, the app compiles, the `.exe` runs — and the feature you asked for is simply absent, or a procedure is a stub, or a file binding is empty.

Three rules follow, and they are not optional:

1. **Never infer success from an exit code.** Exit 0 means "no parser objection", not "you got what you asked for".
2. **Verify by inspecting the artifact.** Round-trip with `/ax` and read the structure back, or grep the *generated source* for the call that implements the feature (`AddRange(`, `ThisReport.Init`, the procedure name). A green build proves nothing about behavior.
3. **A partial spec is worse than no spec.** Template defaults are self-consistent; your half-supplied overrides are not. Omitting a prompt family gets you working defaults. Supplying three of its four members gets you an empty `IF ` and a compile error, or silence. **When unsure, omit.**

## Hard rules — data loss and hangs

**`/ai` REPLACES an existing `.app`. It never merges.** Despite the name, it is create-not-import. Pointed at an existing app it destroys it: a 245 KB six-procedure app with hand-added embeds became a 41 KB two-procedure app, embeds gone, prompts gone, **exit code 0, no warning, no backup**. Only ever point `/ai` at a path where no `.app` exists, and make any script verify that first — a stale `.app` in an output directory turns a routine run into silent data loss. There is no ClarionCL path for adding a procedure to a working app; do that in the IDE.

**The handoff is one-way.** Once a developer takes a generated app into the IDE and edits it, text authoring can no longer contribute to *that* app. Embeds added in the IDE live only in the `.app`; rebuilding from the authored spec discards them.

**ClarionCL is not reliably headless.** It raises modal GUI dialogs mid-run that block until a human clicks, and they are invisible to automation. At least four distinct modal classes are known (solution-association mismatch, dictionary upgrade, TXD-format rejection, template registry). Always invoke it with a timeout guard and kill on expiry. See `references/clarioncl-reference.md`.

**Pass `/au` on every `/ax`, `/ai`, `/dx` and `/di`.** It suppresses the dictionary-upgrade modal that older dictionaries trigger. Without it, headless runs hang on an invisible prompt. It emits a harmless `warning CLCE004: ... ForceUpgrade ...` — ignore that.

**ClarionCL cannot open an `.app` the IDE has loaded** — Win32 sharing violation, `status 32`. No scripting workaround exists. Detect the string `status 32` explicitly and report "the IDE has this app open" rather than a generic failure. Note the error names `<App>.ap~`, the tilde temp, which misreads as a stale-file problem. It is not. (Closing the *solution* is not required; closing the *application* is.) Clarion Assistant's `export_txa` MCP tool routes through the IDE object model instead of spawning ClarionCL, so it exports a loaded app happily.

## The pipeline

```
1.  Author  <App>.dctx   (optional dictionary — XML, far easier than TXA)
2.  Author  <App>.txa    (the application)
3.  ClarionCL /au /di <App>.dct <App>.dctx     -> creates the .dct
4.  ClarionCL /au /ai <App>.app <App>.txa      -> CREATES the .app  (target must not exist)
5.  ClarionCL /au /ag <App>.app                -> template codegen -> .clw/.inc
                                                  (/agc off forces full generation)
6.  MSBuild <App>.cwproj /p:Configuration=Debug /p:Platform=Win32
       env: ClarionBinPath=<clarion>\bin       -> .exe
```

`/ag` requires the **Enterprise** edition. No `.sln` is needed — MSBuild builds the `.cwproj` directly, and keeping no `.sln` in the folder avoids the solution-association modal entirely.

**Give generated apps unique app and module names.** Redirection resolves broadly; a scratch app whose module names collided with a shipped example pulled that example's `.sln` in from the Clarion Examples tree.

## Authoring order matters everywhere

This format family is order-sensitive in at least four places, each silent when wrong:

| Rule | Consequence if violated |
|---|---|
| `TODO ABC ToDo` before `DICTIONARY 'x.dct'` | Import exits 0 and **silently drops every later section** |
| `[ADDITION]` blocks before `[WINDOW]` | Additions not bound |
| `[PROMPTS]` belongs to the `[ADDITION]` it *follows* | Prompts handed to the wrong template, silently dropped |
| `<ForeignMapping>` before `<PrimaryMapping>` in `<Relation>` | ForeignMapping silently dropped, `/di` exits 0 |

**Assume order matters everywhere and mirror what `/ax` or `/dx` emits.** XML being normally order-insensitive makes the DCTX one a genuine trap.

## Where to get canonical syntax

In priority order:

1. **The ABC templates' `#DEFAULT` blocks** (`ABREPORT.TPW`, `ABBROWSE.TPW`, …) are complete canonical TXA fragments with every prompt name, type and default. **This is the best source** — it shows what the template *requires*, rather than what one app happened to contain.
2. **Write minimal, import, then `/ax`.** Clarion fills in defaults and normalizes; the export shows you correct syntax. A 593-byte input round-tripped out as 15,545 bytes of filled-in prompts.
3. **The reference-app trick.** When a feature will not wire and the templates do not say why: generate the app from text, set the feature **by hand in the IDE**, save, `/ax`, and diff the export against your input. One human interaction routinely replaces several failed hypotheses.

## Two TXA files, two jobs

Keep them distinct — an authored TXA and an exported one are not the same kind of object:

- **`<App>.txa`** — the authored bootstrap. Small, readable, hand-maintained, frozen once the `.app` exists.
- **`<App>.export.txa`** — a machine snapshot of the live app, refreshed on demand. Its diff is a drift detector.

An export is roughly **20× the size** of an authored TXA (9,197 → 188,306 bytes for the same six-procedure app) because every prompt is written out at its default. It is machine-editable, not hand-authorable.

**Before any export, warm the ABC cache** — call `warmup_abc` or open any procedure in the embeditor. The IDE loads ABC class metadata lazily, so a cold export is quietly missing `%ClassLines` data. The same app exported twice by the same tool produces different text. Any drift-detection tooling built without this will report changes that are only cache temperature.

## Use the harness, don't hand-drive ClarionCL

Building an app from authored text is **one command**. Do not re-derive the `/di` → `/ai` → `/ag` → MSBuild sequence by hand:

```powershell
scripts\New-ClarionApp.ps1 -SpecPath <folder> -Clean -Run
```

`SpecPath` is a folder holding exactly one `<App>.txa`, optionally one `<App>.dctx`, plus any hand-coded `.clw`/`.inc`. The harness copies the spec to `<spec>\.build` (**the spec is never written to**), derives the `.red`, runs every ClarionCL stage through `Invoke-ClarionCL.ps1` so an invisible modal is *reported* rather than hung on, writes the `.cwproj`, builds, and optionally launches the `.exe` and watches it for error dialogs. It returns `{Ok, FailedStep, Steps[]}` with each step carrying the whole unfiltered tool output.

| Script | Purpose |
|---|---|
| `scripts\New-ClarionApp.ps1` | Full spec → running `.exe` pipeline. Start here. |
| `scripts\Invoke-ClarionCL.ps1` | ClarionCL wrapper: modal capture, timeout, child-tree kill, artifact freshness. |
| `scripts\Sync-SpecFromApp.ps1` | Capture a live `.app` back to spec text (drift detection). |

All three take `-ClarionRoot` (default `C:\Clarion12`) — **set it if your install differs.**

`New-ClarionApp.ps1 -Force` guards against rebuilding when the `.app` is newer than its spec, which is the guard that stops you discarding IDE-added embeds.

## Reference files

- **`references/txa-grammar.md`** — section structure, the minimal working skeleton, which blocks take `[END]`, `FROM ABC <Template>` bindings, `[FILES]`/`[INSTANCE]` semantics, `#SEQ`/`#ORIG` linkage.
- **`references/dctx-authoring.md`** — dictionary XML: GUIDs, keys, overlays, relations, and why `.dctx` rather than `.txd`.
- **`references/recipes.md`** — working recipes for browse, browse→form CRUD, application frame + MDI, report, and parent-child range limiting.
- **`references/failure-signatures.md`** — symptom-to-cause table. **Start here when something built cleanly but is wrong.**
- **`references/clarioncl-reference.md`** — switches, modal-dialog classes, redirection and `.cwproj` traps, and the wrapper design that makes headless runs honest.

Two traps the harness already encodes, worth knowing if you ever modify it:

1. **Redirection.** Derive the local `.red` from the global one and localize **only** entries that escape the build directory (those starting `..\` or a dot-dir). Absolute paths, `%MACRO%` paths (ABC libsrc lives behind `%ROOT%`) and project-local `.\x` search paths must survive verbatim. An early version flattened `*.gif = .\images` to `.`, which is exactly the search-path damage that breaks app opening. The file must keep the version name (`Clarion120.red`) or it is silently ignored.
2. **`.cwproj`.** Compile items need real absolute paths — the CW task resolves neither bare includes via redirection nor `subst` drives that outlive the build. `ProjectGuid` is derived deterministically from the app name so re-runs and any `-ForIde` `.sln` agree.

## Related skills

- **`clarion`** — the language itself (syntax, types, embeds, CRLF file conventions).
- **`clarion-template`** — authoring `.tpl`/`.tpw` templates and the `/tr` registration traps. Registering a template only *parses* it; only a generate proves it works.

---

*Derived from a verified spike series (August 2026). Findings marked PROVEN were measured end to end. Credit to Mark Sarson, whose independent headless ClarionCL work confirmed several findings and contributed the modal-capture wrapper concept.*
