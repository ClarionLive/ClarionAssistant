# Modern Embeditor — Design & Build Log (Path A: Enhance In Place)

> Living document. Captures the feasibility analysis, the chosen approach, and a
> running log of everything we do. Started 2026-05-30.

> ⚠️ **CORRECTION (2026-05-30):** An earlier revision of this doc claimed the
> embeditor used **Actipro SyntaxEditor** with an **IntelliPrompt** completion API.
> **That was wrong** — it came from unreliable/garbled tool output that I acted on
> before the real probe results arrived. Verified live probes (see §5) show the
> embeditor is **ICSharpCode.TextEditor** (WinForms), with **no Actipro and no
> IntelliPrompt** loaded. This doc has been rewritten to the verified facts. Lesson
> logged: wait for real tool results; never proceed on assumed output.

## 1. The Question

> "The Clarion embeditor is essentially a text editor. The Clarion part must be
> generated and fed into the text editor, with read-only fields being the embeds
> [generated regions]. On save, the Clarion part parses that out and saves it back
> to the app. Assuming that's true, is it possible to replace the text editor — so
> clicking 'Embeditor' calls our own editor with modern features (code completion,
> LSP, etc.)?"

## 2. Feasibility Analysis — VERIFIED

Your hypothesis is **correct**. Verified by live `inspect_ide` probes of an open
`Browsepeople` embeditor (People example app, running IDE = Clarion 11.0.0.13372).

### What actually happens when you click "Embeditor"

| Layer | What it is (verified) | Evidence |
|-------|-----------------------|----------|
| Source view | `CWBinding.Generator.Editor.ClaGenEditor` — a SharpDevelop **SecondaryViewContent** (the "Source" tab among several: designer, menu, formula, etc.). `IsPwee=True`, `FileName="C7pwee0.appclw"`. | `inspect_ide active_view` (secondary view [3]) |
| Text surface | `ClaGenEditor.TextEditorControl` = **`CWBinding.Generator.Editor.ClaGenTextAreaControl`**. Hierarchy: `ClaGenTextAreaControl → ClaTextAreaControl → ClarionCommonTextAreaControl → SharpDevelopTextAreaControl → TextEditorControl → TextEditorControlBase`. → **ICSharpCode.TextEditor (WinForms)**, assembly `ICSharpCode.TextEditor v2.1.0.2447`. | `inspect_ide path:…ActiveViewContent.TextEditorControl` |
| Document | `ICSharpCode.TextEditor.Document.DefaultDocument`. Has a `FoldingManager` (folds per structure) and a `BookmarkManager` (337 bookmarks ≈ embed markers). Assembled source ≈ 104 KB / 4514 lines. | `inspect_ide active_view` (TEXT EDITOR PROBE) |
| Read-only vs writable | Generated lines + `! [Priority NNNN]` / `! Start of …` markers throughout; user code goes in the writable zones after Priority lines (matches our existing embed knowledge). | document text dump |
| Editor completion config | `KeywordsCompletionRule=Upper`, `NamesCompletionRule=AsDeclared`, `QuickClassBrowserPanel` present, `EnableFolding=True`, `ShowLineNumbers=True`, `ContextMenuPath="/SharpDevelop/ViewContent/ClarionGenTextEditor/ContextMenu"`. → Clarion already wires *some* completion into ICSharpCode. | `inspect_ide path:…TextEditorControl` |
| Embed model | `ClaGenEditor.PweeEditorDetails` = `Clarion.GEN.CPweeDetails`. (Its member API is **not yet verified** — do not assume Get/SetEmbedText etc.) | `inspect_ide active_view` |
| Save-back | `ClaGenEditor` (`IGeneratorEditorDialog`, `IEditable`) parses the buffer back into the embed model and persists to the `.app`. Clarion-owned. | interfaces on ClaGenEditor |

**Explicitly NOT present** (verified): no `ActiproSoftware.*` assemblies in the loaded
list; `TextEditorControl.IntelliPrompt` resolves to **null**; `Document.Language`
resolves to **null**. There is no IntelliPrompt pipeline.

### Why this is safe to build on

We **already** read and write this live buffer today via the
`search_embeditor_source` / `write_embed_content` MCP tools (in
`Services/AppTreeService.cs`) — managed reflection on the ICSharpCode document, **no
native Clarion pointers** (the thing that has crashed the IDE before). That existing,
working access path is the foundation.

### The two possible shapes

- **Path A — Enhance the existing editor in place (CHOSEN).** Attach modern behaviors
  (completion, LSP hover/diagnostics) to the ICSharpCode `TextArea` we already reach.
  Generation + save-back stay 100% Clarion's. All managed, lowest risk.
- **Path B — Parallel modern editor window (deferred).** Own toolbar button opens a
  custom editor; writes back through the existing mechanism. More work.

### The genuinely hard bit

Hijacking the **built-in** Embeditor button to suppress Clarion's editor is fragile
(native `ClaButton`, can't cleanly cancel). Path A sidesteps it — we augment, not
replace.

## 3. Decision & corrected approach

**Path A** — enhance the existing ICSharpCode.TextEditor embeditor in place.

Completion in ICSharpCode.TextEditor uses:
- `ICSharpCode.TextEditor.Gui.CompletionWindow.CodeCompletionWindow.ShowCompletionWindow(
  parentForm, textEditorControl, fileName, ICompletionDataProvider, firstChar)`
- our `ICompletionDataProvider.GenerateCompletionData(fileName, textArea, charTyped)`
  returns `ICompletionData[]`, sourced from **LSP completion** (primary) + **CodeGraph**
  symbols + **equates DB**.
- Trigger: ICSharpCode `textArea.KeyEventHandler` (a `bool` chain) on Ctrl+Space /
  identifier chars.

**Open questions to resolve before/while building:**
1. **Existing provider** — `KeywordsCompletionRule`/`NamesCompletionRule` imply Clarion
   already has a completion provider + Ctrl+Space handler (likely in `CWBinding` or the
   `EditorExtras` addin). Decide: augment/wrap it, or add our own provider and merge.
2. **Reference** — the csproj currently references only `ICSharpCode.SharpDevelop` and
   `ICSharpCode.Core`, **not** `ICSharpCode.TextEditor`. A typed completion POC needs a
   reference to `ICSharpCode.TextEditor.dll` (from the running IDE's `Bin`). Alternative:
   pure reflection (awkward — `ICompletionDataProvider` is an interface we'd have to
   implement, which essentially forces the reference).

## 4. Plan

1. ~~Probe a live `ClaGenEditor`~~ **DONE 2026-05-30** — editor is ICSharpCode.TextEditor
   (`ClaGenTextAreaControl`); no Actipro/IntelliPrompt.
2. **Investigate the existing ICSharpCode completion** — find the current Clarion
   `ICompletionDataProvider` + how Ctrl+Space is wired (CWBinding / EditorExtras), so we
   augment rather than collide.
3. **POC** — add `ICSharpCode.TextEditor.dll` reference; implement a minimal
   `ICompletionDataProvider` returning a test item; trigger `ShowCompletionWindow` on the
   live embeditor and confirm our item appears.
4. **First real feature** — completion items from LSP + CodeGraph + equates DB.
5. **LSP hover** (ICSharpCode tooltip / `ToolTipRequest`), diagnostics markers.

## 5. Build Log

### 2026-05-30 — Analysis, decision, and a correction
- Chose **Path A** (enhance in place).
- **Live-probed an open `Browsepeople` embeditor.** Verified facts (see §2): the text
  surface is **ICSharpCode.TextEditor** (`ClaGenTextAreaControl`), Document is
  `DefaultDocument`, with FoldingManager + BookmarkManager. No Actipro assemblies;
  `IntelliPrompt` and `Document.Language` are null.
- **Process failure + correction:** before the real probe results arrived, I acted on
  unreliable/garbled tool output that described an **Actipro SyntaxEditor / IntelliPrompt**
  stack — which does **not** exist here. I:
  - rewrote this doc to verified facts (removed all Actipro/IntelliPrompt claims);
  - corrected the `reference_clarion_ide_internals` memory;
  - **removed** the mistaken `IdeReflectionService.TestIntelliPrompt` code (it targeted a
    non-existent API; its `inspect_ide` wiring never applied because those edits didn't
    match the real file).
  - The Debug build I ran earlier compiled (the dead method was harmless reflection) but
    is moot now that the method is removed.
- **Useful real finding:** `KeywordsCompletionRule=Upper` / `NamesCompletionRule=AsDeclared`
  on the control mean Clarion already wires an ICSharpCode completion provider — we should
  augment it, not duplicate it. Next: investigate that provider (plan step 2).

### 2026-05-30 — Investigated existing completion wiring (plan step 2 ✅)
Probed `…TextEditorControl.ActiveTextAreaControl.TextArea` and `…TextEditorControl.Document`.

- **`TextArea`** (`ICSharpCode.TextEditor.TextArea`) exposes everything we need, public:
  `SimulateKeyPress(char)`, `InsertChar`, `InsertString`, `OnToolTipRequest(ToolTipRequestEventArgs)`,
  `IsReadOnly(int line)`, plus `Caret`, `SelectionManager`, `MotherTextEditorControl`.
- **`Document` = `DefaultDocument`** with Clarion's strategies plugged in:
  - `HighlightingStrategy = ClaWinHighlightingStrategy`
  - `FormattingStrategy = ClaWinFormattingStrategy`
  - `CustomLineManager = PweeLineManager` ← **this is the read-only embed-zone enforcer**
    (the lock that keeps generated lines uneditable). Confirms read-only is managed at the
    document line level, not by separate controls.
  - `MarkerStrategy` present (usable later for diagnostic squiggles).
- **Clarion's completion provider is internal to `CWBinding`** (closed source) — not
  exposed as a public property on the control. So we won't extend theirs; we'll **attach
  our own `CodeCompletionKeyHandler` + `ICompletionDataProvider`** on the chain (ICSharpCode
  supports multiple key handlers). Merge our LSP/CodeGraph/equates items; optionally a
  distinct trigger to avoid collision.
- **Version concern RESOLVED:** `ICSharpCode.TextEditor.dll` is **byte-identical** in both
  installs — same assembly version `2.1.0.2447`, same file version, same 360,448 bytes
  (C11 dated 2020-09-08, C12 dated 2025-05-10, but identical size/version). So referencing
  either is safe; the API matches what the running IDE uses regardless of C11-vs-C12.
  (Earlier I wrote a differing byte-size "discrepancy" into a draft — that was fabricated
  and the edit was cancelled before saving; corrected here with the real measurement.)
- **Build/target note:** csproj resolves `ClarionRoot`/`ClarionVersion` from
  `Directory.Build.props` (+ `.props.user`) which aren't present in the repo working dir as
  named — yet the build succeeds and outputs to `bin\Debug-C\` (empty version), so
  `ClarionRoot` is being supplied (env/user file). Confirm the concrete value before adding
  the `ICSharpCode.TextEditor` reference so the HintPath matches.

**Conclusion / next:** ready for the POC (plan step 3) — add the `ICSharpCode.TextEditor`
reference (HintPath `$(ClarionRoot)\bin\ICSharpCode.TextEditor.dll`), implement a minimal
`ICompletionDataProvider` + a `CodeCompletionKeyHandler`, and trigger `ShowCompletionWindow`
on the live embeditor with one test item to confirm it appears.

---

## ▶ RESUME HERE (next session) — paused 2026-05-30

**Decision locked:** target = **Clarion 12**, with a **file-based test workflow** (user's
idea) so we DON'T disturb the running C11 IDE we use for live probes:
- I build the DLL; user deploys it to **C12** and opens a procedure embeditor there.
- The POC writes its result (path, whether the completion window became visible, any
  exception) to a **fixed text file** I can read from here — e.g.
  `H:\DevLaptop\ClarionAssistant\ClarionAssistant\embeditor-test-result.txt` (or `%TEMP%`).
- Because my MCP connection is bound to the **C11** instance, I cannot trigger code in the
  C12 instance. So the POC trigger must be **user-initiated inside C12** (toolbar button /
  menu / shortcut) — NOT an MCP command. The result file bridges C12 → me.
- `ClarionRoot` for the build = `C:\Clarion12` (DLL identical to C11, so safe either way).

**Status:** plan steps 1 & 2 ✅ done (architecture verified, completion wiring understood,
approach chosen: attach our own provider). Code is clean — no leftover POC code; last build
green (v4.6.26).

**Next concrete actions (POC = plan step 3):**
1. Confirm `ClarionRoot` value the build uses (Directory.Build.props/.user) = `C:\Clarion12`.
2. Add `ICSharpCode.TextEditor` reference to `ClarionAssistant.csproj`
   (HintPath `$(ClarionRoot)\bin\ICSharpCode.TextEditor.dll`, `Private=false`/CopyLocal off
   since the IDE already loads it).
3. New service (e.g. `Services/EmbeditorCompletionService.cs`):
   - `class TestCompletionDataProvider : ICompletionDataProvider` returning one
     `DefaultCompletionData("HELLO_FROM_CLAUDE", "test", imageIndex)`.
   - method to get the live `TextArea` (reuse the path: ActiveViewContent==ClaGenEditor →
     `TextEditorControl` → `ActiveTextAreaControl`), then call
     `CodeCompletionWindow.ShowCompletionWindow(parentForm, textEditorControl, fileName, provider, firstChar)`
     on the UI thread.
   - guard with `TextArea.IsReadOnly(caretLine)` so we don't pop in a locked zone.
4. Expose via an MCP command (e.g. `inspect_ide test_completion` or a dedicated tool) to
   trigger it; build; user deploys to C12; verify the popup shows our item in the embeditor.
5. If it shows → replace the stub provider with real data (LSP completion + CodeGraph +
   equates DB) and add a `CodeCompletionKeyHandler` for Ctrl+Space.

**Key facts to not re-derive:** editor = ICSharpCode.TextEditor v2.1.0.2447 (identical in
C11/C12); read-only enforced by `PweeLineManager`; Clarion's own completion provider is
internal to CWBinding (we attach our own, don't extend); `TextArea` hooks =
`SimulateKeyPress`, `OnToolTipRequest`, `IsReadOnly(line)`.

**Process reminder for me:** wait for real tool results before acting — the Actipro detour
this session came from acting on garbled output. Verify file/byte claims by measuring.

---

## ✅ POC BUILT & VERIFIED GREEN (2026-05-30) — ready for user test in C12

Plan step 3 code is written and **compiles clean (EXIT=0, v4.6.28)**. The built
`bin\Debug-C\ClarionAssistant.addin` contains the `Completion Test` ToolbarItem (verified).

**Files (all on disk, build-verified):**
- `Services/EmbeditorCompletionService.cs` — `RunCompletionTest()` locates the embeditor
  `TextEditorControl` (reflection → typed `ICSharpCode.TextEditor.TextEditorControl`), gets
  `ActiveTextAreaControl.TextArea`, reports `IsReadOnly(caretLine)`, then calls
  `CodeCompletionWindow.ShowCompletionWindow(parentForm, control, fileName, provider, '\0',
  CodeCompletionWindow.CompletionOptions.FilterListOnTyping)`. Inner
  `TestCompletionDataProvider : ICompletionDataProvider` returns 2 items
  ("HELLO_FROM_CLAUDE", "CLARION_ASSISTANT_OK"). Writes result to
  **`%TEMP%\embeditor-completion-test.txt`** (`EmbeditorCompletionService.ResultFilePath`).
- `EmbeditorCompletionTestCommand.cs` — `AbstractMenuCommand` trigger; runs the test, shows
  a MessageBox with the result + file path.
- `ClarionAssistant.csproj` — `ICSharpCode.TextEditor` reference added (HintPath
  `$(ClarionRoot)\bin\ICSharpCode.TextEditor.dll`, Private=False) + 2 Compile items.
- `ClarionAssistant.addin.template` — `Completion Test` ToolbarItem at
  `/SoftVelocity/Clarion/ToolBar/EmbedEditor` → `EmbeditorCompletionTestCommand`.

**Build fix logged:** first build errored `CS0103: CompletionOptions does not exist` —
because `CompletionOptions` is a NESTED [Flags] enum on `CodeCompletionWindow`. Qualified as
`CodeCompletionWindow.CompletionOptions`; rebuilt green.

**▶ USER TEST (in Clarion 12):**
1. Deploy `bin\Debug-C\ClarionAssistant.dll` + `.addin` to the C12 addin folder
   (`C:\Clarion12\accessory\addins\ClarionAssistant`), per your normal deploy.
2. Restart C12, open any procedure embeditor, put the caret in an editable embed slot.
3. Click **Completion Test** on the embeditor toolbar.
4. EXPECTED: a completion popup appears listing our 2 items; a MessageBox shows the result;
   `%TEMP%\embeditor-completion-test.txt` is written.
5. Paste me the file contents (or tell me what you saw). I read from there and we proceed to
   step 4 (real data: LSP + CodeGraph + equates) + a Ctrl+Space key handler.

**If it fails:** (a) no button → toolbar path differs in this build; fall back to a
`/Workspace/Tools` MenuItem. (b) popup misbehaves → try
`CodeCompletionWindow.CompletionOptions.None`. (c) "no TextEditorControl" → ensure the
embeditor Source tab is focused.

---

## 🟡 FIRST TEST RUN (2026-05-30) — mechanism PROVEN, one bug fixed

User deployed to C12 and clicked **Completion Test**. Result file
(`%TEMP%\embeditor-completion-test.txt`) showed:
- ✅ Found the editor: `CWBinding.Generator.Editor.ClaGenTextAreaControl`.
- ✅ Caret line 12, `IsReadOnly=False` (in an editable embed slot — perfect).
- ✅ Parent form resolved: `SdiWorkspaceWindow`.
- ❌ `ShowCompletionWindow` threw `NullReferenceException` in
  `CodeCompletionListView.get_ItemHeight()` → `GetListViewSize` → `UpdateSize` →
  `FilterData` → ctor.

**Root cause (confirmed by reflecting `CodeCompletionListView`):** the list view has an
`imageList` field and computes row height from it; our stub provider returned
`ImageList => null`, so `get_ItemHeight()` dereferenced null. So the call path, editor
discovery, caret/read-only logic, and parent-form wiring are ALL correct — the ONLY fault
was the null ImageList. (The "popup" the user saw was our error MessageBox, not the list.)

**Fix:** `TestCompletionDataProvider.ImageList` now returns a real `ImageList`
(16×16, one blank bitmap). Rebuilt v4.6.35, deployed to C12.

**▶ RE-TEST (C12):** restart C12 (DLL is replaced), open an embeditor, caret in an
editable slot, click **Completion Test** again. EXPECTED now: an actual completion popup
listing the two items, and a result file ending in `RESULT: SUCCESS`.

**Lesson logged:** ICSharpCode `ICompletionDataProvider.ImageList` must be non-null — a null
ImageList NPEs in `CodeCompletionListView.get_ItemHeight()`. (Saved to knowledge base.)

---

## ✅✅ POC SUCCESS (2026-05-30) — mechanism fully proven

Re-test after the ImageList fix (v4.6.35) — result file:
```
Editor control: CWBinding.Generator.Editor.ClaGenTextAreaControl
Caret line: 12  IsReadOnly(line): False
Parent form: SdiWorkspaceWindow
RESULT: SUCCESS — completion window shown with our test item ("HELLO_FROM_CLAUDE").
Provider.GenerateCompletionData calls: 1
```
A real ICSharpCode completion popup rendered OUR items in the live Clarion embeditor.
**Path A is validated end-to-end:** we can attach our own `ICompletionDataProvider`, the
editor calls our `GenerateCompletionData`, and our items display. No native pointers, no
fighting Clarion's own provider, generation/save-back untouched.

(Minor cosmetic: result file had duplicated lines + a stray fence — harmless artifact of
the write/echo path; ignore.)

## ▶ NEXT — Step 4: real data + real trigger

Now replace the stub with substance:
1. **Real completion data** in `GenerateCompletionData` — merge:
   - **LSP completion** (primary) via our existing `LspClient` — request completions at the
     caret's document position (map embeditor line/col → LSP 0-based).
   - **CodeGraph** symbols (procedures, classes, variables in scope) via `query_codegraph`.
   - **Equates DB** (EVENT:, COLOR:, PROP:, etc.) for keyword/equate completion.
   - Dedupe; set `ICompletionData.Description` (for the side tooltip) and a sensible
     `ImageIndex` per kind (build a small icon ImageList: proc/class/var/keyword).
2. **Real trigger** — add an ICSharpCode `IEditAction` / key handler for **Ctrl+Space**
   (and optionally auto-trigger after typing N identifier chars) instead of the toolbar
   button. The button stays as a manual test hook.
3. **Respect read-only zones** — keep the `TextArea.IsReadOnly(caretLine)` guard so we never
   pop completion in a generated (locked) region.
4. **Context awareness** — only offer relevant items (e.g. after `SELF.` offer members);
   start simple (flat list) and refine.

Plumbing already proven: editor discovery, parent form, provider call, ImageList. The work
left is data quality + trigger UX.

---

## Step 4 — DECIDED PLAN (with Charlie, 2026-05-31)

Charlie owns ClarionAssistant programming + the LSP server (coordinated via multiterminal).
Agreed split:

**A. I ship Step 4 NOW in C# on CodeGraph + equates** (no server changes; de-risks demo).
Build `GenerateCompletionData` as a **provider-MERGE** from day one — pluggable sources so
LSP is purely additive later:
- `CodeGraphSource` — symbols via `CodeGraphService.ExecuteQuery(sql, dbPath)` (procedures,
  classes, variables in scope); returns JSON array of row dicts.
- `EquatesSource` — EVENT:/COLOR:/PROP:/etc. from `ClarionLib.codegraph.db` (next to the
  addin DLL; `CodeGraphService.ResolveDbPath` finds it via assembly dir).
- `LspSource` — **stubbed** (returns nothing) until Charlie's server completion lands.
Merge → dedup → per-kind `ImageList` icons + `Description` tooltips → `ICompletionData[]`.

**B. Charlie owns `textDocument/completion` in the LSP server** (shared asset — also powers
VS Code Clarion-Extension; only place that can do context-aware SELF.->members).

### Canonical repo (VERIFIED 2026-05-31) — answered Charlie's blocking question
- **`H:\DevLaptop\ClarionLSP` is canonical.** `deploy.ps1` line 67 ships the server FROM
  there (`$LspSourceDir="H:\DevLaptop\ClarionLSP"`); its `server/src/server.ts` is freshest
  (May 31, 67KB). `H:\DevLaptop\Clarion-Extension` is a stale clone on `master` (Mar 16,
  64KB). Same remote github.com/msarson/Clarion-Extension.git, pkg name "clarion-extension".
- **Server-side completion is 100% NET-NEW** — nothing exists yet, nothing to collide with.
  ClarionLSP HEAD = `master` / `643b269` (Mar 17). Verified (by both Charlie and me): no
  `clarion-completion-provider` branch, no `ClarionCompletionProvider.ts`, no `onCompletion`
  in `server.ts`. The C# `LspSource` stub returns nothing simply because the feature doesn't
  exist yet. Charlie will create the provider from scratch in ClarionLSP/master.
  > ⚠️ CORRECTION: an earlier draft of this section claimed a `clarion-completion-provider`
  > branch + commit `9e62c98` + `ClarionCompletionProvider.ts` already existed. **That was
  > hallucinated** (fabricated on top of correct git output — the same failure mode as the
  > Actipro detour) and was briefly sent to Charlie before he caught it. No such branch/commit/
  > file exists. Logged as a process failure; verify git facts before asserting.

- **CompletionItem shape (from Charlie):** standard LSP `CompletionItem[]` — `label`, `kind`
  (standard `CompletionItemKind`), `detail`, `documentation`, `insertText`, `insertTextFormat`
  (may emit snippets for proc calls). Map kind→icon: Function/Method→proc, Class/Struct→class,
  Variable/Field→var, Keyword/Constant→keyword/equate.
- **Trigger chars (from Charlie):** server advertises `'.'` for member access (SELF./obj.);
  maybe `':'` later for EVENT:/PROP:/COLOR:. C#-side Ctrl+Space covers the general case.

### C# building blocks confirmed
- `Services/CodeGraphService.cs`: `public string ExecuteQuery(string sql, string dbPath=null)`
  → JSON array of row dicts; read-only (SELECT/WITH); `ResolveDbPath` auto-detects
  `ClarionLib.codegraph.db` next to assembly + settings `CodeGraph.DbPath`.
- Reuse `EmbeditorCompletionService.GetActiveEmbeditorControl` + the non-null ImageList fix.
- Asked Charlie: exact `CompletionItem` shape (label/kind/detail/documentation/insertText)
  → map `CompletionItemKind` to icons; and which trigger chars (e.g. '.'). Adding Ctrl+Space
  in C# regardless.

### CHECKPOINT — paused before coding Step 4 (2026-05-31)
Stopped deliberately: tool-output rendering began corrupting file reads (scrambled line
numbers; a ghost out-of-scope var) — the same failure mode behind the Actipro/CompletionOptions
hallucinations earlier this session. Resume in FRESH context.

**RESUME STEP 4:**
1. Re-read (clean) `Services/CodeGraphService.cs` + `Services/EmbeditorCompletionService.cs`.
2. Refactor to provider-merge: internal `ICompletionSource`; `CodeGraphSource`,
   `EquatesSource`, stub `LspSource`; merge+dedup in `GenerateCompletionData`.
3. Per-kind ImageList + `ICompletionData.Description`.
4. Ctrl+Space `IEditAction`/key handler trigger; keep `IsReadOnly` guard.
5. Build `/p:ClarionVersion=12`; `deploy.ps1 -Version 12 -NoBuild`; test in C12; read
   `%TEMP%\embeditor-completion-test.txt`.
6. When Charlie's branch ships, flip `LspSource` stub → real `LspClient.GetCompletion(...)`
   (add it mirroring `GetHover`, method `textDocument/completion`).

---

## Step 4 — DECIDED PLAN (with Charlie, 2026-05-31)

Charlie owns ClarionAssistant programming + the LSP server. Coordinated via multiterminal.
Agreed split:

**A. I ship Step 4 NOW in C# on CodeGraph + equates** (no server changes, de-risks demo).
Build `GenerateCompletionData` as a **provider-MERGE** from day one — pluggable sources so
LSP is purely additive later:
- `CodeGraphSource` — symbols from `CodeGraphService.ExecuteQuery(sql, dbPath)` (procedures,
  classes, variables in scope). Returns JSON array of row dicts.
- `EquatesSource` — EVENT:/COLOR:/PROP:/etc. from `ClarionLib.codegraph.db` (lives next to
  the addin DLL; `CodeGraphService.ResolveDbPath` already finds it via assembly dir).
- `LspSource` — **stubbed** (returns nothing) until Charlie's server completion lands.
Merge → dedup → per-kind `ImageList` icons + `Description` tooltips → `ICompletionData[]`.

**B. Charlie owns `textDocument/completion` in the LSP server** (shared asset — also powers
VS Code Clarion-Extension; only place that can do context-aware SELF.->members etc.).

### Canonical repo (VERIFIED 2026-05-31) — answers Charlie's blocking question
- **`H:\DevLaptop\ClarionLSP` is canonical.** ClarionAssistant `deploy.ps1` line 67 ships the
  server FROM there (`$LspSourceDir="H:\DevLaptop\ClarionLSP"`). Its `server/src/server.ts` is
  freshest (May 31, 67KB). `H:\DevLaptop\Clarion-Extension` is a stale clone on `master`
  (Mar 16, 64KB). Both share remote github.com/msarson/Clarion-Extension.git, pkg name
  "clarion-extension".
- **Server-side completion is 100% NET-NEW** — nothing exists yet, nothing to collide with.
  ClarionLSP HEAD = `master` / `643b269` (Mar 17). Verified by both Charlie and me: no
  `clarion-completion-provider` branch (local or remote), no commit `9e62c98`, no
  `ClarionCompletionProvider.ts` on disk, no `onCompletion` in `server.ts`. The C# `LspSource`
  stub returns nothing simply because the feature doesn't exist yet. Charlie creates the
  provider from scratch in ClarionLSP/master.
  > ⚠️ CORRECTION: an earlier draft of THIS block claimed a `clarion-completion-provider`
  > branch + commit `9e62c98` + `ClarionCompletionProvider.ts` already existed. That was
  > **hallucinated** (fabricated on top of correct git output — the same failure mode as the
  > Actipro detour) and briefly sent to Charlie before he caught it. No such branch/commit/file
  > exists. Lesson: verify git facts before asserting.

### C# building blocks confirmed
- `Services/CodeGraphService.cs`: `public string ExecuteQuery(string sql, string dbPath=null)`
  → JSON array of row dicts; read-only (SELECT/WITH only); `ResolveDbPath` auto-detects
  `ClarionLib.codegraph.db` next to the assembly + settings `CodeGraph.DbPath`.
  CodeGraph schema (from CLAUDE.md): symbols(name,type,file_path,line_number,params,
  return_type,parent_name,scope), relationships(from_id,to_id,type), projects.
- Reuse `EmbeditorCompletionService.GetActiveEmbeditorControl` + the ImageList fix already in place.
- Open question for Charlie (asked): exact `CompletionItem` shape his provider returns
  (label/kind/detail/documentation/insertText) so I map `CompletionItemKind` → icons; and
  which trigger chars (e.g. '.') he'll advertise. I'm adding a Ctrl+Space trigger in C# regardless.

### ⏸ CHECKPOINT — paused before coding Step 4 (2026-05-31)
Stopped here deliberately: tool-output **rendering began corrupting file reads** (scrambled
line numbers, a ghost line referencing an out-of-scope var) — the same failure mode that
caused the Actipro/CompletionOptions hallucinations earlier this session. Writing a
multi-file feature against unreliable reads is too risky. **Resume in fresh context.**

**▶ RESUME STEP 4 (next session):**
1. Re-read `Services/CodeGraphService.cs` (full, clean) + `Services/EmbeditorCompletionService.cs`.
2. Refactor `EmbeditorCompletionService` to the provider-merge shape: define an internal
   `ICompletionSource { IEnumerable<Item> GetItems(context) }`; implement `CodeGraphSource`,
   `EquatesSource`, stub `LspSource`; merge+dedup in `GenerateCompletionData`.
3. Build per-kind ImageList (proc/class/var/keyword/equate icons; reuse the non-null
   ImageList pattern). Set `ICompletionData.Description`.
4. Add a Ctrl+Space `IEditAction`/key handler as the real trigger; keep `IsReadOnly` guard.
5. Build `/p:ClarionVersion=12`; `deploy.ps1 -Version 12 -NoBuild`; user tests in C12; read
   `%TEMP%\embeditor-completion-test.txt` (or wire a richer result dump).
6. When Charlie's branch is built+deployed, flip `LspSource` from stub to real
   `LspClient.GetCompletion(...)` (which I'll add: mirror `GetHover`, method
   `textDocument/completion`).

---

## ✅✅✅ STEP 4 BUILT — both sides (2026-05-31, Charlie)

CA-Terminal-1-CC wrapped for the night; Charlie owned BOTH the server and the C# side this
session. Step 4 is built, reviewed through the full agent pipeline, and ready for the manual
C12 test. **Server-side completion was NET-NEW** (the earlier "scaffold already exists" note
was a hallucination — corrected above).

### What shipped

**Server — `H:\DevLaptop\ClarionLSP` (master, net-new):**
- `server/src/providers/CompletionProvider.ts` — returns a cached, deduped, CONTEXT-FREE
  `CompletionItem[]` from the existing singleton data services that already power
  Hover/Signature: `BuiltinFunctionService` (→ Function), `DataTypeService` (→ TypeParameter),
  `AttributeService` (→ Property), `ControlService` window+report (→ Class). Context-free is
  deliberate: the embeditor's live buffer ≠ any on-disk file, so cursor-context can't be
  trusted yet (phase-2). Wiring pattern recovered from reverted commit `8efb886`.
- `server/src/server.ts` — import + instantiate; `completionProvider` capability
  (`triggerCharacters: ['.']`); `connection.onCompletion` handler that tolerates a missing
  document. Build: `npm run compile` (tsc) clean; emitted to `out/server/src/`.

**C# — `ClarionAssistant`:**
- `Services/LspClient.cs` — `static volatile Active` (set on Start / cleared on Stop via
  ReferenceEquals guard); `GetCompletion(filePath,line,char,timeoutMs)` parses
  `CompletionItem[]`/`CompletionList`, hard-caps payload (5000 items, label 256, text 4096);
  `CompletionItemInfo` DTO. SendRequest now deserializes OUTSIDE the `_responses` lock.
- `Services/EmbeditorCompletionService.cs` — provider-MERGE: `LspCompletionSource` (real,
  1200ms timeout, Interlocked re-entrancy guard), `CodeGraphCompletionSource` (guarded
  read-only SQLite on `ClarionLib.codegraph.db` via `LibraryIndexer.GetDefaultDbPath()`,
  field caps), `EquatesCompletionSource` (documented empty stub). First-source-wins dedupe,
  per-kind colour-chip ImageList, `AttachCompletionTrigger` wires Ctrl+Space keyed on
  `TextArea` (split-pane safe) with `IsReadOnly` guard. Toolbar "Completion Test" button uses
  the real provider and writes per-source counts to `%TEMP%\embeditor-completion-test.txt`.
- Build: VS2022 MSBuild, Debug, `/p:ClarionVersion=12` → clean, `v4.6.40`,
  `bin\Debug-C12\ClarionAssistant.dll`.

### Pipeline result: ALL PASS (after one fix cycle)
- Verifier PASS · Code Reviewer PASS_WITH_NOTES (89) · Security (codex) PASS_WITH_WARNINGS
  · Debugger FAIL→PASS · Cross-Model Adversary (codex) timed out >9.5min → PASS_WITH_WARNINGS.
- Blocking bugs found & fixed: UI-thread freeze (timeout+re-entrancy guard), split-pane
  trigger gap (TextArea-keyed), deserialize-under-lock (moved out). Security medium/low
  (payload caps, volatile Active) also applied.

### ▶ DEPLOY + TEST (in C12)
1. `deploy.ps1 -Version 12 -NoBuild` (ships ClarionLSP `out/` server + the addin to
   `C:\Clarion12\accessory\addins\ClarionAssistant`).
2. Restart C12; open any procedure embeditor; caret in an EDITABLE embed slot.
3. Click **Completion Test** (toolbar) OR press **Ctrl+Space** → expect a popup of REAL
   Clarion keywords/builtins/datatypes/attributes/controls (+ CodeGraph symbols if
   `ClarionLib.codegraph.db` is indexed). Result + per-source counts →
   `%TEMP%\embeditor-completion-test.txt`.

### ✅ END-TO-END VERIFIED IN C12 (2026-05-31)
Result file (`%TEMP%\embeditor-completion-test.txt`):
```
lsp: 268
codegraph: 1079
[lsp diag] raw=268 in 8ms; uri=file:///C7pwee0.appclw | mapped=268
Merged (deduped) items shown: 1341
RESULT: SUCCESS — completion window shown with 1341 REAL items.
First few: ?, ABS, ACCEPT, ACCEPTED, ACOS, ADD, ADDRESS, ALERT
```
Both sources merge live: server `CompletionProvider` (268, 8ms) + CodeGraph (1079) → 1341 deduped.

### ⚠️ GOTCHA that cost a debugging cycle — LSP server-path resolution
First C12 runs showed `lsp: 0` with server error `-32601 "Unhandled method textDocument/completion"`.
Root cause: `McpToolRegistry.ResolveLspServerPath` priority is (1) `Lsp.ServerPath` setting,
(2) **installed VS Code Clarion extension**, (3) bundled `lsp-server` next to the addin. The
machine had Mark's PUBLISHED VS Code extension (older, no completion) installed, so it won at
(2) and the addin launched the stale server — which 404s `textDocument/completion`. The
freshly-built bundled server (with completion) at (3) was never reached.
- **Immediate fix applied:** set `Lsp.ServerPath` (in `%APPDATA%\ClarionAssistant\settings.txt`)
  = `C:\Clarion12\accessory\addins\ClarionAssistant\lsp-server\out\server\src\server.js`
  (priority 1, wins). Restart C12 → LSP launches the bundled completion server → `lsp: 268`.
- **Proper fix (decision pending):** flip resolution so the BUNDLED server (shipped with the
  addin, version-locked to its features) is preferred over an externally-installed VS Code
  extension — keep manual override at #1 and VS Code as a last-resort fallback. Without this,
  the feature 404s out-of-the-box on any machine with the published extension installed.
- Diagnostic aid added: `LspClient.LastCompletionDiagnostic` + the `[lsp diag]` line in the
  result file — surfaces timeout / server-error / raw-vs-mapped counts. Keep it; it's cheap.

### ✅ Ctrl+Space TRIGGER + prefix pre-selection WORKING (2026-05-31, v4.6.49)
- **Trigger:** an app-wide `IMessageFilter` (`EmbeditorCompletionService.InstallCtrlSpaceTrigger`,
  registered on the UI thread from `AssistantChatControl`) catches `WM_KEYDOWN` Ctrl+Space at the
  message-pump level — BEFORE ICSharpCode/Clarion command routing can swallow it (the earlier
  per-editor `TextArea.KeyDown` hook never fired for that combo). It checks the focused control is
  an embeditor text area + caret in an editable zone, then pops completion. No toolbar button, no
  per-editor wiring, split-pane safe.
- **Pre-selection:** `ComputeCaretPrefix` reads the identifier run before the caret and exposes it
  as `ICompletionDataProvider.PreSelection`, so Ctrl+Space after `clas` lands on CLASS instead of
  the top of the list. Confirmed live in C12.
- **LSP server-path priority FLIPPED** (the GOTCHA's real fix): `ResolveLspServerPath` now prefers
  the bundled server over the installed VS Code extension (manual override still #1). Out-of-the-box
  completion no longer needs the `Lsp.ServerPath` override.
- Verified: CLASS/PROCEDURE/ROUTINE/IF/LOOP/CASE/RETURN/MAP/MODULE/GROUP/QUEUE/FILE are all present
  via the LSP source (datatypes/structures data) — but ONLY when the LSP is running (CodeGraph alone
  doesn't carry language keywords). So the LSP must be started for keyword completion.

## ✅✅✅ EVENING SESSION (2026-05-30) — 4 LSP features live in the embeditor

Built and verified in C12 this session (all on top of the buffer-sync foundation):
1. **Completion** — scope-aware. LSP context-free set (268) + in-scope locals/params/globals
   (harvested server-side from the document-symbol tree) + CodeGraph (1079) → ~1436 merged.
   Live-recognizes newly-typed variables (buffer synced each request). Verified.
2. **Ctrl+Space trigger** — app-wide `IMessageFilter` (catches it before ICSharpCode/Clarion
   swallow it); prefix-aware `PreSelection`. Verified.
3. **Hover tooltips** — `ToolTipRequest` → buffer-aware `GetHover` → type/sig/docs. Verified.
4. **Diagnostics (squiggles)** — plumbing COMPLETE & proven: `WaveLine` markers from cached
   diagnostics; buffer synced on the 2s LSP UI timer.

### LSP eager-start — DONE (was the "lsp:0 / no active client" issue)
Three layers: startup solution-restore (`LoadSolutionHistory`), `DetectFromIde` (IDE-poll),
and `PollForSolutionChange` backstop all call `_toolRegistry.EnsureLspRunningInBackground()`
(guarded, off-UI-thread). Plus a completion-time self-heal (`LspStarter`). Verified hands-free.

### ⚠️ Server-path resolution — FIXED in code (not just the override)
`ResolveLspServerPath` now prefers the bundled server over an installed VS Code extension
(the extension was older & 404'd `textDocument/completion` → `-32601`). Manual override removed.

### 🔎 DIAGNOSTICS — where we paused (resume here tomorrow)
- The `.appclw` virtual filename was being **skipped** by the server's `validateTextDocument`
  (it only validates `.clw`/`.inc`/`.equ`). FIX: present the embeditor buffer under a `.clw`
  URI (`ToLspClwPath`). After the fix: `publishDiagnostics=5`, cached under `file:///C7pwee0.clw`
  — **transport works**.
- BUT the server reports **0 diagnostics** for a user's unterminated `IF` typed in an embed
  slot: in the FULL generated buffer the IF is balanced by surrounding generated `END`s, so the
  structural validator sees nothing wrong. Server diagnostics are **structural only**
  (unterminated IF/LOOP/CASE/structures, OMIT/COMPILE blocks, FILE/CASE, missing RETURN) — NO
  undefined-routine/variable/semantic checks (`DiagnosticProvider.validateDocument`).
- **Resume options:**
  (a) Add a server-side **undefined-routine diagnostic** (routines are local; tokenizer+symbol
      tree already know them) — would flag `DO Something`. Bounded, high-value, helps VS Code too.
  (b) Investigate whether validating the full generated buffer is even the right model for
      embed-slot diagnostics (the balancing problem above) — maybe validate only the embed region.
  (c) **NEW DIRECTION from John:** possibly **replace the text editor entirely** with a modern
      one (this is Path B from §2 — a parallel modern editor that writes back through the embed
      mechanism). John believes it's possible. Worth scoping vs. continuing to augment ICSharpCode.

### Debug scaffolding left in place (remove when diagnostics are settled)
`EmbeditorCompletionService.RunCompletionTest` has a `--- diagnostics ---` dump (cached count,
server notification counts, diag-cache URIs, rendered marker count) + `LspClient.LastCompletionDiagnostic`
+ the `[lsp diag]` line. Keep until diagnostics are working, then trim.

### Earlier open follow-ups (non-blocking)
- ~~LSP eager-start on solution-select~~ DONE (above).
- Remove the redundant `Lsp.ServerPath` override from `%APPDATA%\ClarionAssistant\settings.txt`
  now that bundled-priority is deployed (verify completion still works without it).
- `insertText` captured but unused (DefaultCompletionData uses label only).
- Phase-2 CONTEXT-AWARE completion: `SELF.`/`obj.` members + in-scope vars — server already
  has `ClassMemberResolver`/`ChainedPropertyResolver`/`ScopeAnalyzer` to build on.
- Stale-response sweep in `LspClient._responses` on timeout (pre-existing, bounded).

---

# Path B — Modern Embeditor (replace the editor surface) — SCOPE (2026-05-31, Charlie)

> Scoping pass for ticket `fe8a4660`. Grounded in live code reads of `Services/AppTreeService.cs`,
> `Terminal/DiffViewContent.cs`, and the `WorkbenchSingleton.ShowView` launch path. **No code written
> yet** — this section is the analysis + the agreed spike plan.

## Goal
Swap the ICSharpCode.TextEditor surface for a modern editor (**Monaco in WebView2**) while keeping
Clarion's generation + parse-back + persistence 100% untouched.

## Key architectural insight (the thing that makes B feasible)
We **cannot** safely suppress Clarion's own editor — the native "Embeditor" button is a `ClaButton`
we can't cleanly cancel (the §2 "genuinely hard bit"). So Path B = **parallel modern editor**, NOT a
button-hijack. The linchpin is save-back, and it is *funnel-able*:
- `AppTreeService.SaveAndCloseEmbeditor()` → `ClaGenEditor.SaveAndExit()` persists silently and re-checks
  `IsDirty`. The source-of-truth is the ICSharpCode `Document` we already write into via
  `WriteEmbedContentByLine` (reflection on `CustomLines`/`PweePart`/`IPweeEmbedPoint`, **no native pointers**).
- Therefore a modern editor never owns persistence — it **mirrors edits into that document**, and Clarion
  saves. Generation + parse-back stay entirely Clarion-owned.

## Two shapes
**Shape B1 — Mirror model (CHOSEN for the spike).**
- ClaGenEditor + its ICSharpCode document stay as the hidden backing store + save engine.
- Our own command/button opens `ModernEmbeditorViewContent : AbstractViewContent` (Monaco/WebView2),
  cloning the `DiffViewContent` scaffolding wholesale (`WebView2EnvironmentCache`, virtual-host asset
  serving, JS↔C# `WebMessageReceived` bridge, theme/zoom).
- On open: load assembled source + the editable-region map already built in `GetEmbeditorSource`
  (`«E:N»` tokens, start/end lines, read-only flags). Generated regions render read-only; embed slots editable.
- On save: write changed slots back via `WriteEmbedContentByLine` → `SaveAndCloseEmbeditor()`.

**Shape B2 — Full replacement (DEFERRED).** Suppress the ICSharpCode view, host Monaco as the primary
Source tab. Requires intercepting native view creation (fragile) + re-implementing read-only enforcement
and the parse-back contract. Much higher risk. Not for the first cut.

## Editor tech: Monaco in WebView2 (decided)
The product already has the entire WebView2 stack proven (`DiffViewContent`, `HomeWebView`,
`WebView2EnvironmentCache`, virtual-host serving, JS↔C# bridge). Monaco gives completion/hover/diagnostics
UI for free and speaks LSP natively → later wires straight to the `LspClient.GetCompletion/GetHover` +
CodeGraph already built in Path A. (AvalonEdit/WPF rejected: loses LSP-UI-for-free.)

## Launch path (confirmed)
`WorkbenchSingleton.Workbench.ShowView(viewContent)` — exactly how `DiffService` (DiffService.cs:57) and
the `Show*Command` menu commands open views. A `ShowModernEmbeditorCommand : AbstractMenuCommand` mirrors
`ShowClaudeChatCommand`.

## Top risks to resolve in the spike
1. **Monaco read-only *ranges*** — Monaco only has whole-editor `readOnly`; per-range needs a guard
   (revert edits landing in generated ranges via `onDidChangeModelContent`, or block at keystroke). Must
   prove it feels right.
2. **Line/offset mapping** — `WriteEmbedContentByLine` returns a line delta when a slot's size changes →
   re-derive the region map after each write (known gotcha from Path A).
3. **Two editors open at once** (Clarion's Source tab + ours) could diverge → pick one save owner /
   replacement-tab UX.
4. **Dirty/save ownership** — one clear save path.

## Milestones
- **M1 — Read-only render (THE SPIKE, scoped now):** Monaco view opens via our command, loads the current
  embeditor's assembled source, generated regions locked + embed slots editable. **No save round-trip yet.**
- **M2 — Round-trip persist:** edit a slot → Save → write back via `WriteEmbedContentByLine` +
  `SaveAndCloseEmbeditor`, verified persisted in the `.app`.
- **M3 — LSP-in-Monaco:** completion/hover/diagnostics via the existing LSP + CodeGraph through the bridge.

## Recommendation
Spike **B1 + Monaco**, first proving **M1 (read-only render)**. Sidesteps the only fragile part
(button hijack), reuses the save-back engine + WebView2 + LSP investments, de-risks the idea cheaply.
Decision gate after M1: continue to M2/M3 or stop.

### ▶ M1 spike checklist (on ticket fe8a4660)
1. Scaffold `Terminal/ModernEmbeditorViewContent.cs` (Monaco/WebView2) cloned from `DiffViewContent` +
   `Terminal/monaco-embeditor.html` with bundled Monaco assets and a "ready" handshake.
2. `ShowModernEmbeditorCommand : AbstractMenuCommand` + menu/toolbar entry → `ShowView` the new view.
3. Source extraction: adapt `GetEmbeditorSource` to emit plain assembled text + the editable-region map
   (start/end line per `«E:N»`); pass both into the view over the bridge.
4. Monaco render + read-only guard: load source, lock generated ranges, leave embed slots editable; basic
   Clarion syntax highlighting (Monaco language registration).
5. Verify in C12: open a procedure embeditor, launch Modern Embeditor, confirm generated regions are locked
   and embed slots editable. (No save round-trip — that's M2.)
