# Generating Clarion Applications from Text (ClarionCL)

**Purpose:** establish whether an assistant can *create* a Clarion application — not edit an
existing one — and record a replication recipe that survives into more advanced experiments.

**Spike date:** 2026-08-05. **Environment:** Clarion 12.0.0.14000 Enterprise, ClarionCL 12.0.0.14000.
**Status:** core capability **PROVEN**. Productisation **NOT** started.

> This document is a lab notebook, not a feature spec. Everything below was executed and
> observed; claims that were *not* verified are marked **UNVERIFIED**. Keep that discipline as
> the experiments get more ambitious — the failure modes here are quiet, and several early
> conclusions in this spike were wrong for hours before evidence corrected them.

---

## 1. Result

Two distinct capabilities, proven separately. They are not the same claim and should not be
conflated.

| Capability | Meaning | Evidence |
|---|---|---|
| **Transport** | Round-trip an existing app through text and rebuild it | `cacheMemos.app` → TXA → renamed → `CAGENDEMO.exe` (927,612 bytes), full browse UI ran |
| **Authorship** | Write a TXA from nothing; get a running app | 955-byte hand-written TXA → `HELLO3.exe` (239,096 bytes), window ran |
| **Schema authorship** | Write a dictionary from nothing | 1,933-byte hand-written `.dctx` → 32,512-byte `.dct`, round-trip verified |
| **Data-bound authorship** | Hand-written app **over a hand-written schema** | 1,216-byte TXA + 1,933-byte `.dctx` → `BRW2.exe` (533,644 bytes), full ABC browse |
| **Multi-procedure CRUD** | Browse + update form, linked | 2,522-byte TXA → `CRUD3.exe` (556,280 bytes), browse/form/insert/change/delete |
| **Conventional app shape** | APPLICATION frame + menu + MDI children | 3,726-byte TXA → `FRAME.exe` (575,172 bytes) |
| **Relational schema** | Two tables + a relation, authored | 3,227-byte `.dctx` → exact round-trip |

**The whole application — schema included — can be authored as 3,149 bytes of text.** No IDE,
no pre-existing app, no pre-existing dictionary.

The pipeline, entirely without the IDE:

```
TXA text ──/ai──► .app ──/ag──► .clw/.inc ──MSBuild──► .exe
```

**Authorship is the one that matters.** Transport only proves the format moves; authorship
proves an assistant can originate an application.

## 2. ClarionCL switches that matter

`/ag` is Enterprise-only. The rest work in Professional.

| Switch | Params | Effect | Verified |
|---|---|---|---|
| `/ai` | `<app> <txa>` | Import TXA. **Creates the .app if absent** — the key discovery | yes |
| `/ax` | `<app> <txa>` | Export app → TXA | yes |
| `/ag` | `<app or sln>` | Template code generation → `.clw`/`.inc` | yes |
| `/agc` | `on\|off` | Force conditional generation on/off | yes (`off`) |
| `/agd` | `on\|off` | `#DEBUG` generation | no |
| `/dx` | `<dct> <textfile>` | Dictionary → text (`.dctx`) | yes |
| `/di` | `<dct> <textfile>` | **Create dictionary from text** — authored schemas | yes |
| `/aru` | `<app> <utility>` | Run a utility template | no |
| `/tl` | — | List registered templates | no |
| `/rt` | `<file>` | Trace redirection — prints which `.red` was consulted | yes |

CA already shipped `/ag` as `build_solution` / `build_app` / `generate_source`
(`Services/McpToolRegistry.cs`). `/ai` and `/ax` exist as MCP tools but route through the **IDE**
and require an app already open; the ClarionCL path needs neither.

> **State change 2026-08-05 (Charlie, `fix/clarioncl-tool-audit` @ 0fa3783):** several findings
> from this spike landed in CA itself. The three ClarionCL-backed build tools now pass **`/au`**
> (suppressing the dictionary-upgrade dialog class on the MCP path), report the **ClarionCL exit
> code as the error count** on failure (the `": error "` pattern counter was blind to every
> ClarionCL error shape), and the invisible-modal caveat is documented in CLAUDE.md's Build Tools
> section. Warning counts remain unfixed by design — one integer cannot carry both. Modal
> *detection* (child-window enumeration) remains future work.

## 3. Authoring from scratch

### 3.1 Minimal TXA (window app)

955 bytes. CRLF line endings, **plain ASCII, no BOM**. Produced a running windowed app.

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
       STRING('This window was authored as plain text.'),AT(20,30,200,12),USE(?Msg),CENTER
       BUTTON('&Close'),AT(95,85,50,14),USE(?Close),STD(STD:Close)
     END
[END]
[END]
```

#### Grammar rules established

- **Template prompts are optional.** The single most useful finding. The dense `[PROMPTS]`
  blocks that dominate a real export are *not* required — Clarion supplies every default. A
  593-byte input round-tripped back out as **15,545 bytes** of filled-in prompts.
- **`DICTIONARY ''` is invalid** → `error GENE000: Could not load dictionary`. Omit the line
  entirely for a dictionary-less app.
- **The app is still created when that error fires.** Trust the **exit code**, never file
  existence.
- **`[COMMON]` is self-terminating** — no `[END]`.
- **Only `[PROGRAM]` and `[MODULE]` take `[END]`. `[PROCEDURE]` blocks do NOT.** A procedure ends
  where the next `[PROCEDURE]` or the module's `[END]` begins.
  > **Corrected 2026-08-05.** This doc previously claimed module *and* procedure each take one
  > `[END]`. That is wrong, and it is a **silent** error: with one procedure per module the extra
  > `[END]` is tolerated at EOF, so single-procedure apps work by luck. With **two** procedures it
  > desyncs the parser — the second `[MODULE]` is absorbed and its procedure lands in the app as
  > `UpdateContact PROCEDURE !Procedure not yet defined`, with `/ai` and `/ag` both still
  > **exiting 0**. Verify structure by round-tripping with `/ax`, never by exit code alone.
- **`FROM ABC <TemplateName>`** is the binding that does the work:
  `FROM ABC` (application), `FROM ABC ABC` (program), `FROM ABC GENERATED` (module),
  and per-procedure `Window`, `Source`, `Frame`, `Browse`.
- **Control metadata is output-only.** `#ORIG`, `#ORDINAL`, `#SEQ`, `#LINK` appear in exports
  but are **not** required on import; Clarion generates them.
- **`[PROJECT]`** carries the legacy pragma project (`#system`, `#model`, `#compile`, `#pragma
  link`, `#link`) and must name the same modules as `[PROGRAM]`/`[MODULE]`.

#### The discovery loop

The format is *discoverable* rather than requiring reverse-engineering:

> **write minimal → `/ai` → `/ax` → read what Clarion normalised**

Clarion fills in the canonical form of everything omitted. Use this for every new construct
instead of guessing at prompt syntax. It is how the rules above were derived, and it is the
method to carry into the browse/form work.

### 3.2 Dictionary (`.dctx` → `.dct`)

The dictionary text format is **XML**, and is far more tractable than TXA. `/di` builds a real
`.dct` from it. This 1,933-byte file produced a 32,512-byte dictionary that round-tripped
through `/dx` with fields and keys intact.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Dictionary Name="CONTACT" Version="1" DctxFormat="4">
	<DictionaryVersion Version="1" Description="Initial version"/>
	<Table Guid="{...}" Ident="2" Name="Contact" Prefix="CON" Driver="TOPSPEED"
	       Path="Contact.tps" Create="true" Thread="true" Bindable="true">
		<Field Guid="{...}" Ident="6" Name="ID" DataType="LONG" Size="4"
		       ScreenPicture="@n_10" ScreenPrompt="ID:" ReportHeading="ID" Justification="RIGHT" Offset="1">
			<WindowControl>
				<Line Text=" PROMPT(&apos;ID:&apos;),USE(?CON:ID:Prompt)"/>
				<Line Text=" ENTRY(@n_10),USE(CON:ID),RIGHT(1)"/>
			</WindowControl>
			<Validity Check="NOCHECKS"/>
		</Field>
		<!-- further <Field> elements -->
		<Key Guid="{...}" Ident="2" Order="1" Name="IDKey" KeyType="KEY"
		     Unique="true" Primary="true" AutoNumber="true" Exclude="true">
			<Component Guid="{...}" FieldId="{GUID of the ID field}" Order="1" Ascend="true"/>
		</Key>
	</Table>
</Dictionary>
```

Rules:
- Every `Table`/`Field`/`Key`/`Component` needs a fresh **GUID** and an `Ident` unique within the
  dictionary. Keys bind to fields by **`FieldId` = the field's GUID**, not by name.
- `<Audit>` elements appear in exports but are **not required** on input.
- `Create="true"` makes the app create the table on first run — no data file needs to ship.

### 3.3 Dictionary-bound browse

A browse is a **`FROM ABC Window`** procedure plus three cooperating parts:

1. **`[FILES]`** naming the table and key:
   ```
   [FILES]
   [PRIMARY]
   Contact
   [INSTANCE]
   1
   [KEY]
   CON:IDKey
   ```
2. **`[ADDITION]`** declaring the control-template instance (no `[END]`; must precede `[WINDOW]`):
   ```
   [ADDITION]
   NAME ABC BrowseBox
   [INSTANCE]
   INSTANCE 1
   PROCPROP
   ```
3. **A `LIST` control** in `[WINDOW]` carrying the linkage metadata:
   ```
   LIST,AT(...),USE(?Browse:1),HVSCROLL,FORMAT('...'),FROM(Queue:Browse:1),IMM,
        #FIELDS(CON:ID,CON:Name,CON:Email),#ORIG(?List),#SEQ(1)
   ```

`#ORIG(?List)` and `#SEQ(1)` are what bind the LIST to BrowseBox `INSTANCE 1`. **Unlike a plain
window, this metadata IS required** — the earlier "control metadata is output-only" rule holds
only for controls with no control template attached.

This generates the full ABC stack: `VIEW(Contact)`, `Queue:Browse:1`, `BRW1 CLASS(BrowseClass)`,
`StepLocatorClass`, `Relate:Contact.Open()`, and a correct `BRW1.Init(...)`.

> **Gotcha — omitting prompts is safer than guessing them.** A first attempt supplied
> `%SortOrder MULTI LONG (1)` + `%SortKey` to set the browse order. That declared a *conditional*
> sort with no matching `%SortCondition`, and the template emitted **`IF ` with an empty
> condition** — a compile error in `BRW1.ResetSort`. Deleting the prompts entirely fixed it: the
> `[FILES]/[KEY]` section already supplies the default sort. **A partial prompt set is worse than
> none**, because template defaults are self-consistent and your partial set may not be.

### 3.4 Update form and browse→form linkage

A form is another `FROM ABC Window` procedure with `CATEGORY 'Form'`, its own `[FILES]` binding,
and two additions — `ABC SaveButton` and `ABC CancelButton` — wired to `?OK` / `?Cancel`.

The browse links to it by adding a second control template that **parents** the BrowseBox:

```
[ADDITION]
NAME ABC BrowseUpdateButtons
[INSTANCE]
INSTANCE 2
PARENT 1
PROCPROP
[PROMPTS]
%UpdateProcedure PROCEDURE  (UpdateContact)
```

`PARENT 1` binds these buttons to BrowseBox `INSTANCE 1`. `%UpdateProcedure` is one of the few
prompts that **must** be supplied — it is the only thing naming the form. The browse window then
carries `?Insert` / `?Change` / `?Delete` buttons with `#ORIG(?Insert)` etc. and `#SEQ(2)`.

**`#SEQ(n)` is the INSTANCE number of the owning control template.** That is the whole linkage
rule: `#SEQ(1)` → the LIST belongs to BrowseBox instance 1; `#SEQ(2)` → the buttons belong to
BrowseUpdateButtons instance 2. `#ORIG(?Name)` is the template's internal control name.

Generates `BRW1.AskProcedure = 1 ! Will call: UpdateContact` on the browse side, and full
`GlobalRequest`/`GlobalResponse` request-mode handling on the form side.

> **Gotcha — `[FILES]`/`[INSTANCE]` is per-procedure, not app-global.** Copying the example's
> pattern (browse = instance 1, form = instance 2) produced a form whose file resolved to
> **nothing**: `Relate:.Open()`, `Access:)`, `SELF.Primary &= Relate:` — an empty file name,
> failing at *compile* time with `Unknown identifier: RELATE:`. The example app had three tables;
> mine has one, so instance 2 did not exist. **Both procedures use `[INSTANCE] 1`** when the
> dictionary has a single table. `/ai` and `/ag` both exit 0 regardless — only the compiler
> catches it.

### 3.5 APPLICATION frame + MDI children

A frame is `FROM ABC Frame` with `CATEGORY 'Frame'`, and an **`APPLICATION`** window instead of
`WINDOW`. It needs **no `[ADDITION]` blocks at all** — menu items call procedures purely through
prompts:

```
[PROMPTS]
%ButtonAction DEPEND %Control DEFAULT TIMES 1
WHEN  ('?BrowseContact') ('Call a Procedure')

%ButtonProcedure DEPEND %Control PROCEDURE TIMES 1
WHEN  ('?BrowseContact') (BrowseContact)

%ButtonThread DEPEND %Control LONG TIMES 1
WHEN  ('?BrowseContact') (1)
```

```
AppFrame APPLICATION('Contact Manager'),AT(,,360,250),FONT('Segoe UI',10),CENTER,MAX,RESIZE, |
          STATUS(-1),SYSTEM
          MENUBAR,USE(?Menubar)
            MENU('&Browse'),USE(?BrowseMenu)
              ITEM('&Contacts'),USE(?BrowseContact),MSG('Browse the Contact file')
            END
          END
        END
```

The `USE` equate on the `ITEM` is the join to the prompt family. Child procedures need **`MDI`**
on their `WINDOW`. Generates the idiomatic thread launch:

```
OF ?BrowseContact
  START(BrowseContact, 25000)
```

Note this is a **blank-line-separated** prompt family, and a rare case where prompts are
**mandatory** — like `%UpdateProcedure` (§3.4), nothing else names the target procedure. Only the
entries you need must be supplied; the other controls fall back to defaults.

Result: `FRAME.exe` (575,172 B) from a 3,726-byte TXA — frame, menu, MDI browse, update form.

### 3.6 Multi-table relational dictionary

`<Relation>` is a **sibling of `<Table>`** at dictionary level, *after* all tables. Everything
binds by **GUID**, never by name:

```xml
<Relation Guid="{...}" PrimaryTable="{Company GUID}" ForeignTable="{Contact GUID}"
                       PrimaryKey="{CompanyIDKey GUID}" ForeignKey="{CompanyKey GUID}">
	<ForeignMapping Guid="{...}" Field="{Contact.CompanyID GUID}"/>
	<PrimaryMapping Guid="{...}" Field="{Company.ID GUID}"/>
</Relation>
```

> **Gotcha — element order inside `<Relation>` is significant.** `<ForeignMapping>` must come
> **before** `<PrimaryMapping>`. Written the other way round, the **`ForeignMapping` is silently
> dropped** — `/di` still **exits 0**, and the round-trip comes back `PriMap=1 FrnMap=0`. XML is
> normally order-insensitive, which makes this a genuine trap for generated DCTX. Match
> Northwind's canonical order.

With the correct order a hand-authored 3,227-byte two-table dictionary round-tripped exactly:
2 tables / 6 fields / 3 keys / 1 relation / 1 PrimaryMapping / 1 ForeignMapping.

## 4. Environment traps

These cost the majority of the spike. Each one presents as something other than what it is.

### 4.1 ClarionCL is NOT reliably headless

It raises **modal GUI dialogs** that block indefinitely. Observed trigger: a `.sln` whose
project association does not match the app pops
*"Clarion loaded the solution 'X.sln', but it does not contain the project 'X.cwproj'"*.

> **Cause identified later — see §4.4.** The `.sln` involved was one *we* hand-wrote, and it was
> malformed: it omitted the `"Solution Items"` project that associates the `.app` with the
> solution. It was not "having a `.sln`" that caused this.

`/ag` may fail with what looks like a misleading error:

```
error 0: The application <path>.app could not be open.
```

> **CORRECTED 2026-08-05 (this was our mistake, not ClarionCL's).** An earlier draft said this
> message means *"a modal is waiting"* or *"redirection is broken"*. The first half is **wrong**.
> Reading the captured stdout afterwards, those runs never blocked at all — they exited in
> **0.1–0.2 seconds**, and the line **directly above** stated the cause plainly:
>
> ```
> Cannot create an application because no templates have been registered.
>  error 0: The application ...\CAGENDEMO.app could not be open.
>  finish at 10:55 AM , elapsed time: 00:00:00.1907310
> ```
>
> The real cause was the hand-written local `.red` of §4.2 — its `*.tp?` rule also matched `.tpl`
> and severed the app from the template folder. The output said so the whole time.
>
> **We never saw it because our own log filter was `Select-String 'successfully|error'`, and that
> sentence contains neither word.** We discarded the diagnosis in our own tooling and then blamed
> the tool. **Never filter ClarionCL's output** — the explanatory line is frequently *adjacent to*
> rather than *inside* the line matching `error`.

So: `could not be open` means the app genuinely could not be opened, and **the reason is on the
preceding line**. Read the whole log before theorising.

**Mitigations, all required for unattended use:**
- Invoke via `Start-Process` + `WaitForExit(timeout)`; kill and report on timeout. Never assume
  it returns.
- **Capture stdout and stderr whole; never grep them down.** See the corrected box above — our
  filter hid the one line that explained a failure we then misdiagnosed for hours.
- **A timeout is not proof of a modal.** When ClarionCL genuinely blocks it emits *nothing* —
  stdout carried only the unrelated `CLCE004` warning and stderr was empty. Conversely, when it
  prints prompt-shaped text (*"…Do you want to open the backup file?"*) it **does not** block and
  exits normally. In our data the two never co-occur, so prompt-looking output is evidence
  *against* a hang. Detecting a real modal requires enumerating child windows — output parsing
  cannot do it.
- ClarionCL does emit structured codes on non-modal failures (`GENE000`, `CLCE001`, `CLCE004`,
  `DCTE004`), so there is real material to parse — it is specifically the modal path that goes
  dark.
- For a **headless** build, keep **no `.sln`** in the folder — MSBuild builds a `.cwproj` directly,
  so a solution buys nothing and a *malformed* one is what triggers the dialog. If a human will
  later open the app in the IDE, write a **correct** one (§4.4) rather than none.
- Give generated apps **unique app/module names**. A scratch app whose modules were still named
  `cacheMemos*` resolved the *shipped example's* `.sln` out of `C:\Clarion12\Examples` via
  redirection.
- Clicking "Add" on that dialog appends a **duplicate** project entry with a fresh GUID each time
  (three were observed for one cwproj), which compounds the mess rather than fixing it.

### 4.2 Redirection

- A **local `.red` overrides the global one only if version-named** — `Clarion120.red`, not
  `MyApp.red`. A differently-named file is silently ignored. Confirm with `ClarionCL /rt <file>`
  run from the app folder; it prints which `.red` it consulted.
- **Never hand-write a minimal `.red`.** Derive it from the global file and change only the
  *output* directories, leaving every search path intact. A hand-written minimal red broke app
  opening entirely. Specific killer: the pattern `*.tp?` **also matches `.tpl`**, severing the
  app from Clarion's template folder.
- `.` in a redirection rule resolves against the **process working directory**, not the project
  folder. Always set the working directory to the app folder.
- Generated output silently follows the global red. On this machine `*.clw = ..\v8Source` sent
  generated modules *one directory above* the app — they appeared "missing" for some time.

### 4.3 MSBuild

- The CW task did **not** resolve bare `<Compile Include="X.CLW">` via redirection under any
  `RedFile` setting tried (relative, absolute, omitted). **Use full paths** in `Compile` items.
- The error names only the **first** unresolved item, so *"one file not found"* usually means
  **none** resolved. Check the item order before diagnosing.
- Requires `ClarionBinPath` in the environment; `.NET Framework v4.0.30319 MSBuild` works.
- **No `.sln` needed** — build the `.cwproj` directly. (A solution is only needed for the IDE; see
  §4.4 for the shape it must have.)

### 4.4 The `.sln` shape (only needed if a human will open the app)

A headless build never needs a solution. But the moment someone opens the generated app in the
IDE, it does — and **the shape matters**. This is the file the IDE generated for itself, and it is
the shape to copy:

```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio 2012
# Clarion 2.1.0.2447
Project("{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}") = "FRAME", "FRAME.cwproj", "{<cwproj ProjectGuid>}"
EndProject
Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{2150E333-8FDC-42A3-9474-1A3956D46DE8}"
	ProjectSection(SolutionItems) = postProject
		FRAME.app = FRAME.app
	EndProjectSection
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Win32 = Debug|Win32
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{<cwproj ProjectGuid>}.Debug|Win32.Build.0 = Debug|Win32
		{<cwproj ProjectGuid>}.Debug|Win32.ActiveCfg = Debug|Win32
	EndGlobalSection
EndGlobal
```

Rules:

- **The second `Project(...)` block — `"Solution Items"` — is what associates the `.app` with the
  solution, and omitting it is very likely what caused the §4.1 modal.** Our first hand-written
  `.sln` had only the `.cwproj` entry; both the IDE-generated file and the shipped
  `cacheMemos.sln` carry this block. *Likely cause, not proven* — we did not isolate it by adding
  only that section.
- The first `Project(...)` GUID must equal the `.cwproj`'s own `<ProjectGuid>`, and the same GUID
  repeats in `ProjectConfigurationPlatforms`. Read it out of the `.cwproj` rather than minting a
  new one.
- `{12B76EC0-…}` is the Clarion **project-type** GUID and `{2150E333-…}` the standard VS
  solution-folder GUID — both are constants, not per-project values.
- The file starts with a **leading blank line** and uses tab indentation, as the IDE writes it.

> **Retracted:** an earlier draft said *"do not hand-author a `.cwproj` GUID and expect a match —
> the `.app` carries its own project identity."* That is wrong. The IDE's own solution simply
> reuses the `.cwproj`'s `ProjectGuid`, and a hand-written `.sln` doing the same opens fine. The
> original failure was the missing `"Solution Items"` block, not GUID identity.

> **`subst` leaks into artifacts.** The MSBuild workaround in §4.3 puts `Q:\` paths into the
> `.cwproj`'s `Compile` items. That is invisible headlessly but meaningless to the IDE and to
> anyone else opening the project. If a `.cwproj` is going to outlive the build, write **real
> absolute paths** into it.

## 5. Replication recipe

```powershell
$env:ClarionBinPath = "C:\Clarion12\bin"
$app = "MYAPP"            # unique — must not collide with anything under C:\Clarion12
$dir = "<scratch folder>" # working directory for every step

# 0. local redirection: derive from global, redirect ONLY output dirs, keep the version name
#    (replace ..\v8Source -> . , .\Source -> . , ..\v8Obj -> .\obj) into $dir\Clarion120.red

# 1. text -> app   (creates the .app; for a headless build keep NO .sln in $dir.
#    If a human will open this in the IDE afterwards, write a CORRECT .sln - see 4.4)
ClarionCL /ai "$dir\$app.app" "$dir\$app.txa"

# 2. app -> source
ClarionCL /agc off /ag "$dir\$app.app"

# 3. source -> exe   (Compile items must use FULL paths; working dir = $dir)
MSBuild "$dir\$app.cwproj" /p:Configuration=Debug /p:Platform=Win32
```

Wrap steps 1–2 in a `WaitForExit` guard. Verify each step by **exit code**.

## 6. Evidence

Artifacts lived in the session scratchpad (ephemeral — regenerate via the recipe):

| Artifact | Size | Note |
|---|---|---|
| `CAGENDEMO.exe` | 927,612 B | Transport. Ran: MDI frame, menu, toolbar, tabbed browse, Insert/Change/Delete. Created its own `Department.tps` on first run |
| `HELLO3.exe` | 239,096 B | **Authorship.** Ran: window, two strings, working Close |
| `HELLO3.txa` | 955 B | The hand-written input |
| `BRW2.exe` | 533,644 B | **Data-bound authorship.** Compiled clean; TopSpeed driver linked |
| `BRW2.txa` + `CONTACT.dctx` | 1,216 + 1,933 B | Total hand-authored input for the browse app |

Both binaries verified as valid Win32 PE (`0x014C`); `CAGENDEMO.exe` imports `ClaRUN.dll`,
`ClaTPS.dll`, `ClaASC.dll`, `claDF.dll`.

**Side effect, accepted and left in place:** the first `/ax` against
`C:\Clarion12\Examples\DFD\Memos\cacheMemos.app` silently **upgraded that example's dictionary**
(`cache.dct` rewritten, `cache.old_dct` backup created, `.app` re-saved). *Merely opening an app
headlessly upgrades its dictionary.* Copy examples out before touching them.

## 7. Open questions

Ordered by how much each would unlock.

1. ~~Dictionary-bound procedure~~ — **RESOLVED 2026-08-05.** `/di` builds a `.dct` from authored
   XML; a browse over it compiles and links. See §3.2, §3.3.
2. ~~Browse~~ — **RESOLVED 2026-08-05.** Read-only browse proven end to end. Note the prediction
   that *"prompts are optional will break down here"* was **half right**: prompts remained
   optional, but control-template **linkage metadata** (`#ORIG`/`#SEQ`) became mandatory, and a
   *partial* prompt set actively broke codegen.
3. ~~Update form + BrowseUpdateButtons~~ — **RESOLVED 2026-08-05.** Two-procedure CRUD app
   compiled clean. See §3.4.
4. ~~APPLICATION frame + MDI~~ — **RESOLVED 2026-08-05.** Frame + menu + MDI browse + form
   compiled to `FRAME.exe`. See §3.5.
5. ~~Relational dictionary~~ — **RESOLVED 2026-08-05.** Two-table dictionary with a relation
   round-trips exactly. See §3.6.
6. ~~Parent–child browse~~ — **RESOLVED 2026-08-05.** `BRW1.AddRange(ORD:CustomerID,CUS:ID)` from
   authored text, verified against an IDE-authored reference app. Needs three things that were each
   individually silent when wrong: `%RangeField`/`%RangeLimitType`/`%RangeLimit` in the **BrowseBox
   `[ADDITION]`'s own `[PROMPTS]`**, the parent file under `[FILES]`/`[OTHERS]`, and the positional
   prompt-ownership rule. See §10.3.
6b. **Reports** — **RESOLVED 2026-08-05.** `FROM ABC Report` with `[FILES]` instance **0**,
   `[REPORT]`, and the canonical progress `[WINDOW]`. See §10.2.
7. **Reproducing Mark's TXD construct bugs** — needs a hand-authored *dictionary* TXD, not the
   Report-Writer TXD `/dx` emits. **UNVERIFIED.**
4. Does `/aru` allow running utility templates against a generated app? **UNVERIFIED.**
5. Correct parameter order for `/up_createapp` / `/up_createappVC` — accepted
   `(Solution, App, TXA)` with exit 0 but produced **nothing**. Not needed once `/ai` is used.

## 8. What this is not

A spike, not a feature. Before anything runs unattended it needs the modal guard, unique-name
isolation, and redirection handling made robust. Nothing here is wired into CA.

The plausible product is a generator — *describe an app, get a compiled `.exe`* — but that claim
depends on open question 2, which is untested and is the one most likely to fail.

## 9. Independent confirmation + external intel (Mark Sarson's agent, 2026-08-05)

Mark Sarson has been working the same ground for about a week — headless ClarionCL as an entire
build-and-test loop, no IDE. His agent replied to John's Discord post. **Everything in this
section is second-hand and NOT verified by us**; it is recorded because it is high-value and
because several items are things we would otherwise hit blind.

### Independently confirmed (both sides, arrived at separately)

- `/ai` **creates the `.app` when it does not exist.**
- **Template prompts are optional** — Clarion fills every default; minimal TXAs are tiny.
- **A partial/malformed spec is worse than none**, because it fails *quietly*.

Three separate discoveries matching ours is strong evidence these are real properties of
ClarionCL rather than artefacts of our environment.

### His variant of the "fails quietly" trap — TXA section ORDER matters

If `DICTIONARY 'x.dct'` appears **before** `TODO ABC ToDo`, the import returns **exit 0** and
then **silently drops every later section** — extensions, procedures, everything. A half-imported
app that compiles into nothing.

> Our working TXAs happen to have `TODO ABC ToDo` before `DICTIONARY` (§3.1, §3.3), so we never
> hit this. Treat the header order in this doc as **load-bearing, not stylistic**.

### Switches we have not used

| Switch | Note | Status |
|---|---|---|
| `/tr` / `/tu` | Register/unregister a template. **Machine-global state.** | **UNVERIFIED (ours)** |
| `/win` | Forces the Win32 redirection file. He uses `/win /ag` and `/win /aru` as standard. | **UNVERIFIED (ours)** |
| `/au` | Always-upgrade; he pairs it as `/au /ai`. We saw it warn about a missing runbefore switch. | partially seen |
| `/aru` | Run a `#UTILITY` headless. **Only** `/win /aru App.app "Utility(Template)"` works — any other form **exits 0 having done nothing.** | **UNVERIFIED (ours)** |

### His gotchas (all UNVERIFIED by us)

- **`GENE000: could not open include file` is almost never the file — it's the cwd.** ClarionCL
  resolves relative `.tpl` paths against the working directory at the moment it runs, and a
  chained `Set-Location` may not have taken effect. *Using an absolute path does not fix it.*
- **`/tr` is global machine state and registering from a throwaway directory is a footgun.** An
  agent registered a template from a git worktree; the worktree was later pruned; the registry
  then pointed at a deleted directory and **every** template operation on the machine — including
  read-only `/tl` — spun at 100% CPU forever. Heal: `/tu <ChainName>` (chain name only, not a
  path), then `/tr` from a stable checkout.
- **The `/di` TXD importer mangles things:** `FM3IGNORE` truncates to `FM` (a digit inside a bare
  flag trips it); `GROUP,OVER(...)` overlay components get dropped or inherited from the preceding
  key; `ORDER(...)` key components get dropped entirely. Editor-authored DCTs are fine —
  hand-authored **TXD**s need workarounds.
- **`%KeyOrder` ≠ `%Key`.** Orders (the 4th key type) appear only in the `%KeyOrder` symbol family
  and surface as `%KeyOrderIndex = 'ORDER'` — an undocumented value; the docs list only
  KEY/INDEX/DYNAMIC.

### The engineering we should copy

He confirms **ClarionCL has no quiet/unattended switch** (checked the whole `/?`) — matching §4.1.
His team built a PowerShell wrapper that is the reusable core:

1. **cwd guard** — verify working directory *and* that the target file resolves before launch
   (kills the `GENE000` class of failure).
2. **timeout** on the process.
3. **Dialog capture** — on timeout, walk the child process tree for a stray modal, capture its
   **title + text** so the agent finally learns what went wrong, dismiss it, return an honest exit
   code. *This is the missing half of our §4.1 mitigation: we detect the hang, he diagnoses it.*
4. **`-ExpectArtifact`** — assert the output file exists **and is fresh**, turning silent no-ops
   (like the `/aru` form trap) into real failures.

His methodology on top — *"round-trip to zero"* — is worth stealing for template work: hand-author
the TXD + TXA **and** a hand-coded reference of exactly what the generated output should be, then
assert byte-identical, line by line. Template correctness becomes a green/red exit code instead of
eyeballed source.

### RESOLVED: `.dctx` round-trips perfectly — and `/dx`→`.txd` is a format trap

> **Read the CORRECTED CONCLUSION at the end of this section before quoting any of it.** The
> first pass concluded "the TXD importer is broken"; that was wrong, and the correction is the
> more useful finding.

**Tested 2026-08-05 (ours).** Subject: `Examples\IMDD\IMDD-SQLcache\Northwind.dct` — 32 tables,
261 fields, 63 keys, 14 GROUP fields, MSSQL driver, BLOBs. Copied out before touching.

Method: one source dictionary → `/dx` to **both** formats → `/di` each back into a fresh `.dct` →
`/dx` both results to `.dctx` → compare.

| Path | Exit | Resulting `.dct` | Fidelity |
|---|---|---|---|
| `.dctx` (XML) | **0** | 227,328 B | **32 / 261 / 63 / 14 — identical to source.** Re-export 233,827 B vs original 233,825 B |
| `.txd` | **hung on a modal** (first run: no error reported) | 26,368 B | **Corrupt.** Clarion refuses to open it: *"The Dictionary … closed prematurely. Do you want to open the backup file?"* A subsequent `/dx` crashes with `System.NullReferenceException` in `ExportToText.DoCommand` |

**The exported `NW.txd` itself was fine** — 89,161 bytes, all 261 field control blocks present. So
the fault is in the **TXD importer**, not the exporter: *ClarionCL cannot re-import its own TXD
output for this dictionary.*

This generalises Mark's three construct-level bugs into something larger: **the TXD import path is
unsafe at production scale**, and `.dctx` is a drop-in replacement that round-trips perfectly.
`/di` accepts either format; `/dx` emits either by file extension. **Author XML.**

**Correction to an earlier draft of this section:** it claimed Northwind contained no `OVER(` and
no relations. Wrong — that was a bad grep, searching for *TXD* syntax inside an *XML* file. In
DCTX an overlay is the attribute `Over="BirthDate"` on a `DataType="GROUP"` field with **nested
`<Field>` children**, and relations are `<Relation>`, not `<Relationship>`. Northwind has **14
overlays and 13 relations**, and the DCTX path preserved all of them (14 `Over=`, 14 GROUPs, 13
relations, 13 `<PrimaryMapping>`, 13 `<ForeignMapping>`, 28 nested fields — exact match).

#### Edge-case test: all three of Mark's constructs, both formats

A purpose-built 2,021-byte dictionary (`EDGE.dctx`) carrying **`DriverOption="FM3IGNORE"`**, a
**`GROUP` + `Over=`** overlay with two nested fields, and a **`KeyType="ORDER"`** key with a
component:

| path | DriverOption | `Over=` | GROUPs | nested | KeyTypes | Components |
|---|---|---|---|---|---|---|
| via **DCTX** | `FM3IGNORE` | 1 | 1 | 2 | `KEY,ORDER` | 2 |
| via **TXD** | *(empty)* | 0 | 0 | 0 | *(none)* | 0 |

`/di` from TXD returned **exit 1** and produced a **26,368-byte husk — the same size as the failed
Northwind import**, i.e. an empty dictionary regardless of input size.

**The TXD export is correct.** It renders all three constructs properly:

```
Edge FILE,DRIVER('TOPSPEED','FM3IGNORE'),NAME('Edge.tps'),PRE(EDG),BINDABLE,CREATE,THREAD
IDKey KEY(EDG:ID),NOCASE,PRIMARY
BirthOrder ORDER(EDG:BirthDate),NOCASE
BirthDate_GROUP GROUP,OVER(BirthDate)
```

So `FM3IGNORE` is *not* truncated on the way out and `ORDER(...)` *is* emitted — the failure is
entirely in the **importer**.

#### CORRECTED CONCLUSION — the two `.txd` formats are not the same format

The blocking modal was finally read off John's screen:

> **TXD Import** — *"This TXD file is a Report Writer only format"*

**`/dx <dct> file.txd` emits a Report Writer TXD, not a dictionary-import TXD.** Two different
formats sharing one extension. `/di` did not fail — it **correctly refused** the file, and the
26,368-byte husk is what you get when it declines and writes an empty dictionary anyway.

This retracts the overstated claim in the draft above ("the TXD importer is unsafe / cannot
re-import its own output"). What actually holds:

1. **`/dx` → `.txd` is a trap.** It silently produces a Report-Writer-only artefact. The `/?` text
   for `/di` — *"Creates a dictionary from a textfile (either dctx or txd)"* — is misleading,
   because the `txd` it means is **not** the one `/dx` produces.
2. **My A/B therefore never tested Mark's bugs.** His three construct-level failures are against
   *hand-authored* dictionary TXDs and remain **unreproduced by us**. My "wholesale vs partial"
   discrepancy was the symptom of this, and it resolved to *"you were feeding it the wrong
   format,"* not a version difference.
3. **The `.dctx` results stand unaffected** — they never depended on the TXD leg. `.dctx`
   round-tripped a 32-table production dictionary exactly, and carried `FM3IGNORE`, a `GROUP`+
   `Over=` overlay with nested fields, and a `KeyType="ORDER"` key with its component. *Author
   `.dctx`* remains sound advice; it simply is not evidence that TXD import is broken.

Failure reporting across these runs was inconsistent — exit 1 once, a modal hang once, no message
at all once — all from the same cause. **Never infer import success from an exit code.**

#### Methodological lesson

This is the **third distinct modal class** we have hit (solution-mismatch §4.1, dictionary-upgrade
above, TXD-format here) — and the only reason the conclusion got corrected is that a *human*
read the dialog. Our guard kills the process and reports a hang; the dialog text carrying the
actual diagnosis is lost. **Mark's dialog-capture wrapper (§9) would have surfaced
`"This TXD file is a Report Writer only format"` automatically and prevented a wrong conclusion
from being written down.** That moves it from *nice engineering* to *correctness-critical*.

### Also verified by us: `/au` suppresses the dictionary-upgrade modal

Two of three Examples dictionaries (`Northwind`, `TrigExam`) **hung on a modal** under a plain
`/dx`. Adding **`/au`** cleared it — exit 0, full export. Older (C6/C7-era) dictionaries prompt to
upgrade, and that prompt is invisible to a headless agent. This is why Mark pairs `/au` as
standard. **Use `/au` on every `/dx`, `/di`, and `/ai`.** It emits a harmless
`warning CLCE004: … ForceUpgrade …` which can be ignored.

## 10. Cold test: authoring from a description (2026-08-05, later the same day)

Everything in §3 was *grown* — the TXA was iterated against compiler errors while the format was
still being discovered. This section records the first app authored from a **description** instead,
against the written grammar, as a measurement of whether that grammar is sufficient.

**Spec:** "Customers → Orders, MDI frame, parent–child browse, update forms, orders-by-customer
report." Chosen to hit the two things §7 listed as unverified.

**Result: green in 3 iterations.** `ORDERS.dctx` (2.1 KB) + `ORDERS.txa` (9.4 KB) → `ORDERS.exe`
(797 KB), running. Six procedures: frame + menu, two browses, two update forms, one report.
Artifacts: `ClarionAppGen/specs/orders`. The whole run is one command now — see §11.

Iteration 1 → 29 compile errors, all in the three `Orders` procedures.
Iteration 2 → 4 errors, all in the report.
Iteration 3 → green.

### 10.1 CORRECTION: `[INSTANCE]` binds the file to a CONTROL TEMPLATE, not to a table ordinal

This supersedes the §3.4 rule *"both procedures use `[INSTANCE] 1` when the dictionary has a single
table."* That rule produces working output but the reason it gives is wrong, and the wrong reason
does not generalize — it predicts that a two-table dictionary needs instance 2 somewhere, which is
what iteration 1 tried, and every `Orders` procedure failed.

**The actual rule:** `[FILES]`'s `[INSTANCE] N` binds the file to **control-template instance `N`**
— the same numbering space as `[ADDITION]`/`INSTANCE n` and `#SEQ(n)`. **`0` means the procedure
itself.**

| Procedure kind | Instance | Why |
|---|---|---|
| Browse | `1` | BrowseBox is instance 1 and owns the file |
| Form | `1` | SaveButton is instance 1 (this is why instance 2 — CancelButton — broke in §3.4) |
| Report / Process | `0` | no control templates exist to own the file |

Table count is irrelevant. `[INSTANCE]` itself is **mandatory** — omitting the block fails import
with exit 1.

**Failure signature when it is wrong** — identical to §3.4, and it survives both `/ai` and `/ag`
with exit 0, failing only at *compile*:

```
Process:View         VIEW()          ! empty file name
ThisReport.Init(Process:View, Relate:, ?Progress:PctText, Progress:Thermometer)
```

```
error : Must be FILE or KEY label
error : Unknown identifier: RELATE:
error : Field not found: SETQUICKSCAN
```

Read it as *"the file did not bind"*, and check the instance number first.

### 10.2 Reports: `FROM ABC Report`, and the progress `[WINDOW]` is mandatory

No shipped example app under `C:\Clarion12\Examples` contains a report, so there was nothing to
copy. A report procedure needs, in this order: `[FILES]` (instance **0**), `[REPORT]`, `[WINDOW]`.

The `[WINDOW]` is the **progress window**, and omitting it fails at *generation*:

```
error GENE000: ASSERT: Main: progress controls use variable not found!
error GENE000: Main Error: No Window Defined!
```

The assert is `abprocs.tpw:577` (`%ThermometerUseVariable <> ''`). Copy the window verbatim from
`ABREPORT.TPW`'s `#DEFAULT` block — `PROGRESS,USE(Progress:Thermometer)` plus
`?Progress:UserString`, `?Progress:PctText`, `?Progress:Cancel`. Report prompts are all optional
(`%ReportDataSource` defaults to `'File'`, `%EnablePrintPreview` to 1); the load-bearing parts are
the file binding and that window.

Given a correct binding the template does the rest — `VIEW(Orders)` with a `PROJECT` per band
control, `Relate:Orders`, `AddSortOrder(ORD:CustomerKey)`, and `ORD:CustomerID` passed to
`ThisReport.Init` as the break field.

> **The templates' `#DEFAULT` blocks are canonical TXA fragments.** `ABREPORT.TPW` and
> `abprocs.tpw` carry complete `[COMMON]/[DATA]/[PROMPTS]/[REPORT]/[WINDOW]` skeletons per
> procedure type, plus every prompt name, type and default. This is a **better authoring source
> than reverse-engineering an export**, because it shows what the template *requires* rather than
> what one app happened to contain. Read the `#PROCEDURE(...)` line for the template name and
> `#DEFAULT` for the shape.

### 10.3 RESOLVED: `[PROMPTS]` ownership is POSITIONAL — a prompt block binds to the `[ADDITION]` it follows

Settled by the reference-app experiment described below. This is the most load-bearing rule in the
whole document after §10.1, because it silently mis-files *any* prompt, not just range limits.

**The structure, from an IDE-authored browse:**

```
[FILES]
[PRIMARY] / Orders
[INSTANCE] / 1
[KEY] / ORD:CustomerKey
[OTHERS] / Customer
[PROMPTS]                 <-- the PROCEDURE's own prompts
[ADDITION]
NAME ABC BrowseBox
[INSTANCE] / INSTANCE 1 / PROCPROP
[PROMPTS]                 <-- BrowseBox's prompts (the range limit lives HERE)
[ADDITION]
NAME ABC BrowseUpdateButtons
[INSTANCE] / INSTANCE 2 / PARENT 1 / PROCPROP
[PROMPTS]                 <-- BrowseUpdateButtons' prompts (%UpdateProcedure lives HERE)
[WINDOW]
```

**A `[PROMPTS]` block belongs to the `[ADDITION]` it follows. Procedure-level prompts must come
BEFORE the first `[ADDITION]`.** Anything placed after the additions is handed to the *last*
addition; if that template does not define the symbol, it is **silently dropped** — exit 0 from
`/ai` and `/ag`, compiles, runs, feature simply absent.

This explains two things at once in the §3.4/§3.5 material:

- `%UpdateProcedure` "worked" after the additions **by luck** — it genuinely belongs to
  BrowseUpdateButtons, which is the last addition.
- A `%ButtonAction`/`%ButtonProcedure` family placed in the same spot did **not** work; moved
  before the first `[ADDITION]`, the button emits `BrowseOrders()` correctly. In §3.5 the frame has
  no additions at all, which is why its prompts worked wherever they were put.

**The range limit is the FLAT family on the BrowseBox — not `%SortRange*`:**

```
[ADDITION]
NAME ABC BrowseBox
[INSTANCE]
INSTANCE 1
PROCPROP
[PROMPTS]
%RangeField COMPONENT  (ORD:CustomerID)
%RangeLimitType DEFAULT  ('Single Value')
%RangeLimit FIELD  (CUS:ID)
```

`%SortRangeField`/`%SortRangeLimitType`/`%SortRangeLimit` are the **per-sort-order conditional**
variants; in a normal browse they stay empty. The IDE-authored reference exports
`%SortOrder MULTI LONG ()` with all ~20 members at `TIMES 0` *even with a working range limit* —
which retires the earlier "the MULTI family gets dropped" theory. The family was never the
mechanism; the wrong family was being populated, in the wrong place.

**The parent file must appear in `[FILES]` under `[OTHERS]`.** Without it the field reference in
`%RangeLimit` resolves to nothing and you get a range limit with an empty value — again compiling
and running, and never filtering:

```
BRW1.AddRange(ORD:CustomerID,)          ! [OTHERS] missing - silently unfiltered
BRW1.AddRange(ORD:CustomerID,CUS:ID)    ! correct
```

**Verification — authored text vs the IDE.** Generating from the IDE-authored app and diffing
against our generated source:

| Module | Difference |
|---|---|
| `ORDERS004` parent–child browse | 2 lines, both `? DEBUGHOOK` |
| `ORDERS006` report | 1 line, `? DEBUGHOOK` |
| `ORDERS002` customer browse | 27 lines, **all ours only** — the working `?ShowOrders` handler the reference app lacks |

`DEBUGHOOK` is a debug-generation artifact. **Hand-authored text now produces the same source the
IDE produces.**

> **Method worth reusing: the reference app.** When a feature will not wire and the templates do
> not say why, generate the app from text, set the feature **by hand in the IDE**, save, `/ax`, and
> diff the export against your input. One human interaction converted an open-ended guessing
> problem — four failed hypotheses across the wrong prompt family — into an exact answer in one
> step. Keep the `.app` as ground truth (`ClarionAppGen/reference/orders-rangelimit`).

> **A green harness run does not prove the app does what the spec asked.** The missing range limit
> compiled and ran cleanly for three iterations. Inspect the generated source for the call that
> *implements* the feature — `AddRange` here — not just the exit code. This is the one class of
> failure the harness structurally cannot catch.

### 10.4 Methodological: `/ax` echoing your input proves ACCEPTED, not COMPLETE

The report `[REPORT]` section round-tripped byte-identical on the first try, which was read as
"the grammar is right". It was accepted and stored — and still failed generation for a missing
`[WINDOW]` and failed compilation for a wrong `[INSTANCE]`. A clean round-trip is necessary, not
sufficient. Same family of error as §9's "partial beats absent because it fails quietly": the
verification has to be *build and run*, not *re-export*.

A second instance of the same lesson, from the tooling side: a probe matrix of four `[FILES]`
variants all "agreed", which looked like strong evidence. The substitution had silently not
applied — a `\r\n` pattern against an LF file — so all four runs used identical input. **When
variants agree suspiciously well, verify the variants actually differ** (byte sizes did, once
fixed). Incidentally: `/ai` accepts LF-only TXA, so line endings are not load-bearing for import.

## 11. Harness

`ClarionAppGen/tools/New-ClarionApp.ps1` — spec folder in, running `.exe` out, one command:

```powershell
.\New-ClarionApp.ps1 -SpecPath .\specs\orders -Clean -Run
```

A spec folder is one `<App>.txa`, optionally one `<App>.dctx`, plus any hand-coded `.clw`/`.inc`.
It stages a copy (the spec is never written to), derives the `.red`, runs `/di` → `/ai` → `/ag`
through `Invoke-ClarionCL.ps1` (so a modal is captured, not a hang), writes the `.cwproj` with real
absolute paths, builds, and optionally launches the exe and watches for error dialogs. Returns
`{Ok, FailedStep, Steps[]}` with each step's **whole** unfiltered output. This replaces the §5
recipe as the way to run the pipeline; §5 remains the explanation of what it does.

Two things it encodes that were previously hand-done:

- The `.red` is derived from the global one, localizing **only** entries that *escape* the build
  dir (`..\`-rooted or dot-dir). Absolute, `%MACRO%` and project-local `.\x` paths survive
  verbatim — flattening `*.gif = .\images` to `.` is the §4.2 search-path damage. (It also fixes a
  stray `..\v8obj` the hand-edited example `.red` had missed.)
- The `.cwproj` gets real absolute `Compile` paths — no `subst` drive leaking into an artifact —
  and a deterministic `ProjectGuid` so re-runs and the optional `-ForIde` `.sln` always agree.

Validated by two positives (`specs/hello`, `specs/crud` — build *and* run) and one negative
(`specs/_negative`, a TXA bound to a nonexistent template) that goes RED at `import /ai` carrying
`GENE000 … Unknown template type NoSuchTemplateXYZ(ABC)` with file/line/column intact.

## 12. Related knowledge entries

`add_knowledge` ids **91** (pipeline), **92** (modal dialogs), **93** (redirection / cwproj),
**94** (minimal TXA grammar), **104** (the harness), **107** (the `status 32` IDE lock),
**108** (export is a complete serialization — §13).

## 13. The `.app` as source of truth — export round-trip (2026-08-07)

§1–§11 treat the authored text as the source of truth and the `.app` as a disposable build
artifact. That breaks the first time someone opens the generated app in the IDE, because **an
embed lives only in the `.app`** — the authored `.txa` has no record of it and the next build
silently discards it. This section records whether the reverse direction (`app → text`) is good
enough to make the `.app` authoritative instead.

**It is.** The export is a *complete* serialization, not merely a readable one.

### 13.1 The test

Take a built app carrying edits the authored spec never had — an embed added by hand in the IDE,
plus four `WindowResize` instances with per-instance `%AppStrategy` prompts — export it, and
rebuild from that export alone in a *different directory*:

```
.app --/ax--> .txa --/ai--> new .app --/ag--> .clw --MSBuild--> .exe --> runs
```

| Carried across | Result |
|---|---|
| Hand-added embed (`MESSAGE` in `ThisWindow.Init`, priority 2500) | survived to generated source |
| Per-instance `%AppStrategy` (`Surface`×2, `Resize`×2) | all four correct |
| `BRW1.AddRange(ORD:CustomerID,CUS:ID)` — the §10.3 fragile one | intact |
| All six `.INC` module maps | byte-identical |
| Runs | yes |

Embeds export with full addressing, so the round-trip is not lossy about *where* code sits:

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

### 13.2 Functionally exact, NOT byte-exact

One systematic difference across all five window procedures — `SELF.AddItem(Toolbar)` generates
two lines later, after the `CLEAR(GlobalRequest)`/`CLEAR(GlobalResponse)` pair instead of before
it. The exes are the same size but differ in bytes. Cause is addition ordering: the export writes
the app's internal order, which is not the order the additions were authored in.

Benign — registering the toolbar with the window manager does not interact with clearing the
globals — but it matters for tooling: **the first export after adopting this workflow shows a diff
in every window procedure.** Anything that treats "export differs from last export" as "the app
changed" needs to absorb that one-off.

### 13.3 `/ax` cannot read an app the IDE has open

ClarionCL fails with `Could not gain access … after 50 attempts` / `error GENE000: Cannot open
application … (status 32)`. This hits `/ax`, `/ag` and `/di` alike — anything that opens the
`.app`. There is no PowerShell-side workaround.

The only exporter that works on a *loaded* app is the IDE itself, via CA's `export_txa` MCP tool
(which routes through the IDE object model rather than spawning ClarionCL). So the capture step is
IDE-dependent: **it cannot run headless or in CI while the app is open.** Close the application in
the IDE — the solution may stay open — or export through CA.

### 13.4 The 20× problem, and why there are two files

An exported TXA is not a substitute for an authored one:

| | bytes | lines |
|---|---|---|
| Authored `ORDERS.txa` | 9,197 | 316 |
| Exported from the same app | 188,306 | 5,890 |

Section *counts* barely move (12 `[ADDITION]`, 6 `[PROCEDURE]` either way) — the bloat is inside
them: every prompt written out at its default, nothing inferred. Machine-editable, not
hand-authorable.

So keep both, with distinct jobs:

- **`<App>.txa`** — the authored bootstrap. Small, readable, frozen once the `.app` exists. This
  is the artifact that proves authorship-from-nothing (§1), and regenerating it from an export
  would destroy that claim.
- **`<App>.export.txa`** — machine snapshot of the live app, refreshed on demand and committed.
  Its diff is the drift detector.

### 13.5 Tooling

`tools/Sync-SpecFromApp.ps1` — `.app → <App>.export.txa` in the spec folder. Exports via a temp
file so a failed `/ax` cannot leave a truncated snapshot that looks like a good one, refuses any
export under 1 KB (§10.4 again: check the artifact, not the exit code), reports changed vs
unchanged against the previous capture, and detects the `status 32` lock specifically so the
message names the IDE instead of blaming ClarionCL.

`New-ClarionApp.ps1` gained a matching guard: **if the `.app` is newer than the spec `.txa`, the
run stops.** That mismatch is the signature of IDE-side edits the spec does not have, and `-Clean`
would delete them. `-Force` declares the spec authoritative and accepts the loss.

> **Caveat — an export is only as complete as the IDE's ABC cache.** See §13.7. An earlier draft
> of this section guessed that the IDE and ClarionCL exporters disagree; that guess was wrong, and
> the real cause is worse for tooling.

> The export step deliberately did **not** go into `New-ClarionApp.ps1`. That script's contract is
> "spec in, exe out, the spec is never written to", and it runs *after* the spec has been edited —
> an export stage inside it would clobber the very edit that triggered the run. Capture is a
> separate, deliberate action taken *before* editing.

### 13.6 RESOLVED: `/ai` into an existing app REPLACES it — silently, exit 0

Tested 2026-08-07. A six-procedure app carrying a hand-added embed and four per-instance
`WindowResize` prompts; a TXA fragment declaring one new procedure imported into it with
`/ai`. **The entire application was destroyed and rebuilt from the fragment.**

| | before | after |
|---|---|---|
| Procedures | Main, BrowseCustomers, BrowseOrders, UpdateCustomer, UpdateOrders, OrdersReport | Main, AboutBox |
| `MESSAGE` embed | present | **gone** |
| `%AppStrategy` prompts | 4 | 0 |
| `%RangeField` (range limit) | 1 | 0 |
| `[ADDITION]` blocks (BrowseBox etc.) | many | 0 |
| `.app` on disk | 245 KB | 41 KB |

`Main` survived as a **name only** — declared in the fragment's `[APPLICATION]` header, with no
window, no menubar, no additions. Everything the fragment did not mention was discarded.

**Exit code 0. No warning, no prompt, no backup.** Nothing in stdout but the usual unrelated
`CLCE004`. This is the §10.4 lesson in its most expensive form: the exit code says the operation
succeeded, and it did — the operation is simply *replace*.

Re-tested with a fragment carrying **no `[APPLICATION]` header** (bare `[MODULE]`/`[PROCEDURE]`),
in case the header was the "this is the whole app" signal. Identical outcome: app 245 KB → 38 KB,
embed gone. **The header is not the trigger; `/ai` has no merge mode.**

Consequences:

- `/ai` is only ever safe against a **non-existent** app — i.e. bootstrapping, which is all §1–§11
  ever did. Treat it as `CREATE`, never `IMPORT`, whatever the switch is named.
- **The handoff to the IDE is one-way on the ClarionCL path.** Once a developer has put work into
  the `.app`, text can no longer contribute to it through `/ai`. Structural additions (a new browse
  over a new table) must be made in the IDE, or the whole app re-bootstrapped from a spec that
  already contains them.
- Any harness or script that runs `/ai` against a path where an `.app` might already exist is one
  stale file away from silent data loss. `New-ClarionApp.ps1` is safe by construction (`-Clean`
  into a build dir, plus the app-newer-than-spec guard), but nothing else should call `/ai`
  without checking first.

**Still untested:** CA's `import_txa` MCP tool, which routes through the IDE and exposes
`clash_mode` (`rename`/`replace`). Having an explicit clash parameter implies it merges rather than
replaces, but that is an inference, not a measurement, and it must be tested on a scratch app —
never on one holding real work.

### 13.7 An export is only as complete as the IDE's ABC cache

The IDE loads ABC class metadata **lazily**. Export before that load has happened and the TXA is
quietly missing class information; export after, and it is present. **The same app, exported twice
by the same tool, produces different text.**

Measured 2026-08-07 on ORDERS, both via `export_txa`:

| | bytes | `BrowseCustomers` slice |
|---|---|---|
| cold (10:21) | 188,306 | 1,469 lines |
| warm (12:40, after an embeditor open) | 189,484 | 1,483 lines |

The difference is entirely `%ClassLines`. Cold, the browse class lists two methods
(`Q`, `Init`). Warm, it lists the full derivable surface — `AppendOrder`, `ApplyRange`, `InitSort`,
`Kill`, `Next`, `Open`, `Previous`, `ReplaceSort`, `ResetSort`, `RestoreBuffers`, `SetSort`,
`SetUseMRP`, `TakeKey`, `TakeLocate`, `ValidateRecord` — plus an entire `Locator0` class item that
is **absent** from the cold export. Same for `WindowResize`: `GetParentControl`, `Resize`,
`RestoreWindow` appear only warm.

**This was found by accident**, chasing what looked like a difference between per-procedure and
whole-app export. It is not: a single-procedure export and the corresponding slice of a whole-app
export taken *in the same IDE state* are byte-identical apart from a trailing `[END]` (1 differing
line). Against the cold whole-app export the same file differed by 21 lines. **The variable was
time, not export mode.**

Consequences:

- **A naive drift detector is unusable.** "The export differs from last capture" means "the app
  changed" *or* "the IDE was colder last time". The second is not a change and there is nothing in
  the diff that distinguishes it.
- **Warm the cache before exporting.** CA ships `warmup_abc` precisely to force the lazy ABC load
  (it exists for a Modern Embeditor timing problem, but it is the same load). Call it, or open any
  procedure in the embeditor, before `export_txa`. An export taken cold is not wrong so much as
  *incomplete*, and incompleteness that varies run to run is worse than a stable omission.
- It is unknown whether a cold export **re-imports** to an equivalent app, since the missing
  `%ClassLines` are the derivable-method lists the class prompts depend on. The §13.1 round-trip
  used a cold export and produced a working, functionally-correct app — so the omission appears
  benign for generation — but that is one data point and it was not the property under test.
- This likely explains the 188,306 vs 191,228 gap noted earlier between an IDE export and a
  ClarionCL export of the rebuilt app. Attributing that to two disagreeing exporters was a guess;
  cache warmth is the simpler explanation and the measured one. **Do not assume the exporters
  differ** — that has never been demonstrated.

Same family as §10.4: what looked like a property of the *format* was a property of the *moment
the measurement was taken*.

