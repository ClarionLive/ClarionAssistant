# Failure Signatures

**Start here when the build was green but the app is wrong.**

Every entry below was observed with **exit code 0** from `/ai` and `/ag` unless stated otherwise. Nothing in the tool output distinguishes them from success.

## Symptom → cause

| What you see | Cause |
|---|---|
| A procedure appears as `Name PROCEDURE !Procedure not yet defined`, and its `.clw` is never generated | A stray `[END]` after a `[PROCEDURE]` block desynced the parser and the following `[MODULE]` was absorbed. `[PROCEDURE]` takes no `[END]`. |
| Generated `Relate:.Open()`, `SELF.AddUpdateFile(Access:)`, `SELF.Primary &= Relate:` — **empty file name**. Compile errors: `Unknown identifier: RELATE:`, `Field not found: OPEN` | Wrong `[FILES]`/`[INSTANCE]` number. Read any empty-binding symptom as "the file did not bind" and check the instance first. |
| `Process:View VIEW()` empty, `ThisReport.Init(Process:View, Relate:, ...)`. Compile errors: `Must be FILE or KEY label`, `Unknown identifier: RELATE:`, `Field not found: SETQUICKSCAN` | Same cause — a report needs `[INSTANCE] 0`. |
| Compile error `Expected: <operand>`, from an `IF ` with an **empty condition** inside `BRW1.ResetSort` | A partial prompt set: `%SortOrder` + `%SortKey` supplied without a matching `%SortCondition`. **Remove the prompts entirely** — `[FILES]`/`[KEY]` already supplies the default sort. |
| Browse sorts correctly but **never filters**. Generated `BRW1.AddRange(ORD:CustomerID,)` — trailing comma, nothing after it | The parent file is missing from `[FILES]` under `[OTHERS]`. Correct output is `BRW1.AddRange(ORD:CustomerID,CUS:ID)`. |
| A prompt family you supplied has **no effect at all** | `[PROMPTS]` placed after the `[ADDITION]` blocks. It was handed to the *last* addition, which does not define your symbol, and dropped. Procedure-level prompts go before the first `[ADDITION]`. |
| Import exits 0 but **extensions, procedures — everything after the header — is missing** | `DICTIONARY 'x.dct'` placed before `TODO ABC ToDo`. Header order is load-bearing. |
| Round-trip returns `PriMap=1 FrnMap=0` — a relation lost its foreign mapping | `<PrimaryMapping>` written before `<ForeignMapping>` in the `.dctx`. Element order inside `<Relation>` is significant despite XML normally being order-insensitive. |
| Codegen asserts `progress controls use variable not found` (`abprocs.tpw:577`) | A report is missing its `[WINDOW]` — the progress window is **mandatory**. Copy it verbatim from `ABREPORT.TPW`'s `#DEFAULT` block. |
| `error GENE000: Could not load dictionary` | `DICTIONARY ''` — an empty dictionary line is invalid. Omit the line entirely. The app is still created despite the error, so check the exit code. |
| Import fails with exit 1 | A `[FILES]` block is missing its `[INSTANCE]` — that one is mandatory and does *not* fail silently. |
| App shrank dramatically (e.g. 245 KB → 41 KB), embeds and prompts gone | `/ai` was pointed at an **existing** `.app`. It replaces, never merges. Not recoverable — there is no backup. |

## Errors that mean something other than what they say

| Message | What it actually means |
|---|---|
| `error 0: The application <path>.app could not be open.` | Either **a modal dialog is waiting** (invisible to you), **or** redirection is broken. It does *not* mean the app is missing or corrupt. |
| `Could not gain access to <App>.ap~ after 50 attempts` / `GENE000: Cannot open application <App>.app (status 32)` | The **Clarion IDE has that app loaded**. `status 32` is a Win32 sharing violation. Note it names the `.ap~` tilde temp, which misreads as a stale-temp problem. It is not; no scripting workaround exists. |
| `GENE000: could not open include file <name>.tpl` | Almost never the file. It is the **working directory** — ClarionCL resolves relative `.tpl` paths against the CWD at launch, and a chained `Set-Location` may not have taken effect. Using an absolute path does **not** fix it. |
| `Cannot create an application because no templates have been registered.` | Often a **broken redirection file** rather than a genuinely empty registry — a bad `*.tp?` pattern cuts Clarion off from its template folder, and it then creates an empty local `TemplateRegistry12.trf` in the CWD. |
| `warning CLCE004: ... ForceUpgrade ...` | Harmless. It is what `/au` emits when it suppresses the dictionary-upgrade modal. Ignore it. |
| `This TXD file is a Report Writer only format` (modal) | `/dx <dct> file.txd` emits a **Report Writer** TXD, not a dictionary-import TXD — two formats sharing one extension. `/di` correctly refuses it, then writes an empty ~26 KB husk anyway. |

## Diagnostic invariants

- **A 26,368-byte `.dct`** is the empty-husk size. It appears regardless of source dictionary size — a 32-table Northwind and a four-field toy produce the same husk. Seeing that size means the import produced nothing.
- **A killed or refused `/di` leaves temp husks** next to the target: `X.dc~` and `X.dct.cl.temp`, both the same 26,368 bytes.
- **Failure reporting is inconsistent across identical causes** — the same fault was observed as exit 1 once, a modal hang once, and silence once. This is the single strongest argument for verifying artifacts rather than trusting the tool's own report.

## How to verify properly

- **Structure:** round-trip with `/ax` and inspect the `[MODULE]`/`[PROCEDURE]` nesting.
- **Features:** grep the **generated source** for the call that implements the feature — `AddRange(`, `ThisReport.Init`, `START(ProcName,`, the procedure's own name. A green build and a running window proved nothing for three iterations of a missing range limit.
- **Dictionaries:** `/dx` the result back to `.dctx` and compare counts — tables, fields, keys, relations, PrimaryMapping, ForeignMapping.
- **Artifacts:** assert the output file exists **and is fresh**. Staleness turns a silent no-op into an apparent success.
