# App-Tree Procedure Selection → Managed Read (FileSchema) — Investigation & Test Guide

**Date:** 2026-06-07
**Context:** Live IDE probe (CA-Terminal-1) supporting Charlie's task **fd5a4607 — "CA Data Pad: populate
from app-tree procedure selection."** Probed against `clbrws.app`
(`C:\Users\Public\Documents\SoftVelocity\Clarion11\Examples\HowToClarion\Browses`), Clarion 12.0.14000.
**Knowledge entries:** #25 (FileSchema read), #26 (no managed select / dispatch channel), #27 (unique proc names).

---

## TL;DR

- ✅ **You CAN read the app-tree's currently-selected procedure from managed code**, with no embeditor open,
  no native pointers: **`ActiveViewContent.FileSchema.ProcedureName`**. It updates on a **single left-click**
  (verified live). This is the channel SoftVelocity's own "Data / Tables" pad uses.
- ✅ **The procedure's data is right there too** — `FileSchema.LocalData / GlobalData / ModuleData` are
  `FieldList`s in the same shape as dictionary tables. Read directly, **skip `.txa`**.
- ✅ **Trigger = poll** `ProcedureName` on the pad's existing ~750 ms tick, string-compare, repopulate on change.
  No event subscription needed (none exists at this level).
- ❌ **You CANNOT *set* the selection from managed code** (no select-by-name anywhere). Selection is driven
  only by the native ClaList locator (keystrokes) or a row click. Proven down to the enum level.
- ❌ **You CANNOT generate the embeditor source headlessly** — the generator detail objects are native-pointer
  -born (prior Path A IL scan). The native embeditor open/close as transport is irreducible.

---

## 1. The managed channel (the win)

```
WorkbenchSingleton.Workbench.ActiveViewContent          // null-check; must be the app view
  → (SoftVelocity.Generator.UI.ApplicationMainWindowControl_ViewContent)
  .FileSchema                                            // public property; SoftVelocity.DataDictionary.Schema.FileSchema
    .ProcedureName    // string — the SELECTED proc name (LIVE; updates on single-click)
    .ModuleName       // string — e.g. "clbrws007.clw"
    .LocalData        // FieldList — the proc's local data (what the Data pad renders)
    .GlobalData       // FieldList
    .ModuleData       // FieldList
    .LocalSimpleData / .GlobalSimpleData   // List
    .Templates        // List
    .Id               // Guid (changes per schema instance)
```

- The ViewContent exposes `FileSchema` as a **direct public property** (no cast needed). It also implements
  `IFileSchemaProvider` + `IFileSchemaPadController` if you prefer binding through the interface.
- Native repopulates it on selection via `FileSchema.ReloadProcedure(IProcedureDataProvider)` / `Init(...)`.
- **App-view guard:** when a non-app tab (editor / Monaco embeditor) is active, `ActiveViewContent` won't have
  `FileSchema`. Guard with `ActiveWindowInterface == UI_AppMainWindowControl`
  (`UIBindingInterfaceKind`) or a type-check for `ApplicationMainWindowControl_ViewContent`. (Note: reading the
  selection should locate the **app** view regardless of which tab is focused — see `FindAppViewContent`.)

### Live verification (this session)

| Step | `FileSchema.ProcedureName` | `FileSchema.ModuleName` |
|------|----------------------------|-------------------------|
| Initial (default selection) | `Main` | `clbrws002.clw` |
| After John single-clicked **BrowseDiscounts** (no open) | `BrowseDiscounts` | `clbrws007.clw` |

→ Single left-click drives it. No embeditor was opened. Value is live, not stale.

### The data shape

`FileSchema.LocalData` = `SoftVelocity.DataDictionary.Schema.FieldList`:
- `.AllFields` (IEnumerable), `.AllFieldsCount` (e.g. 11 for BrowseDiscounts), `.Fields`
  (`UniqueDataDictionaryItemList`), `.StatisticsInformation` ("Columns:   11"), `.IsLocal = true`,
  `.Label / .Name / .CodeName = "Local Data"`.
- Derives `DDBaseFile` and implements `IFieldContainer` — **the same FieldList shape the dictionary TABLES
  use**, so existing `DDField` / `DDContainerField` rendering (Name, Type, picture, prefix, GROUP nesting)
  works verbatim. `GlobalData` / `ModuleData` are the same type.

---

## 2. How Charlie should test (fd5a4607)

**Goal:** clicking a procedure in the app tree repopulates the CA Data pad with that proc's data.

**Suggested flow under test:**
1. Open an `.app` (any — `clbrws.app` is a good sample; procs incl. `BrowseDiscounts`, `Main`, etc.).
2. CA Data pad reads `ActiveViewContent.FileSchema.ProcedureName` on its ~750 ms tick; compares to last-seen.
3. On change → read `FileSchema.LocalData` (+ Global/Module as desired) and rebuild the pad rows.

**Things to verify:**
- [ ] **Single left-click** (NOT double-click / open) on a proc updates the pad within one tick.
- [ ] Clicking several procs in a row tracks each (`Main` → `BrowseDiscounts` → `BrowseAuthors` …).
- [ ] **Focus-wins precedence** — pad doesn't fight the user; populate-only, no selection side-effects.
- [ ] **App-view guard** — switching to a non-app tab (editor / Monaco embeditor) doesn't crash or show stale
      junk (FileSchema may be absent on that ActiveViewContent).
- [ ] **Busy interlock** — pad does NOT poll/repopulate while an embeditor open/save is driving the IDE
      (`ModernEmbeditorLauncher.IsBusy` is the existing flag).
- [ ] Data correctness — `AllFieldsCount` matches the proc's actual local data column count.

**Manual managed read (for ad-hoc verification via the IDE inspector / MCP):**
`inspect_ide path:ActiveWorkbenchWindow.ViewContent.FileSchema.ProcedureName`

> ⚠️ **Shared reader — avoid duplication.** Both the CA Data pad (this task) and the Modern Embeditor Save
> simplification (§4) need the exact same `FileSchema.ProcedureName` read. Make it **one** method, e.g.
> `AppTreeService.GetAppTreeSelectedProcedureName()`, and have both call it.

```csharp
/// <summary>
/// The procedure currently committed/selected in the app tree, from the live managed FileSchema
/// mirror (updates on single-click, no embeditor open). Null if no app view / no selection.
/// </summary>
public string GetAppTreeSelectedProcedureName()
{
    var vc = FindAppViewContent();
    if (vc == null) return null;
    var schema = GetProp(vc, "FileSchema");
    if (schema == null) return null;
    return GetProp(schema, "ProcedureName") as string;
}
```

---

## 3. Dead ends (do NOT re-investigate — confirmed)

### 3a. No managed "select procedure by name"
Checked every surface — none can SET the selection:
- `Application`, `Win32App`, `Win32GeneratorInstance` — app-level only; the current proc lives behind the
  native `GenIFace* generator` pointer (forbidden to reinterpret).
- `Procedure` node — only `SelectAll(bool)` = TXA **export** selection, not UI selection.
- `AppTreeService.SelectProcedure(name)` — itself just types `WM_CHAR` into the ClaList locator.
- `FileSchema.ProcedureName` is a **read-only downstream mirror**; writing it would not move the generator's
  current-proc context that the Embeds/Properties buttons act on.

### 3b. The managed→native dispatch channel can't select either
The host (`ApplicationMainWindowControl`) has `Dispatch(UIControlEvents)`, `DispatchString(UIControlEvents,
String)`, `CommandInvoke(CommandID)`, `ExecuteCommand(Int32)`, `SetRequest(RequestType)`. Enumerated via Cecil:
- **`UIControlEvents`** = `Clarion.ASL.UIControlEvents` (in `Clarion.asl.dll`). All 24 members are
  window-lifecycle / gen-message plumbing — **no select / navigate / row member**:
  ```
  EvWindowEvent, EvConnect, EvDisconnect, EvWindowOpened, EvCloseWindow, EvHideWindow,
  EvDisableWindow, EvCaptionChanged, EvNotifyNewSize, EvDesignModeSelectParent,
  EvIsDirtyChanged, EvIsValidChanged, EvSelectParent, EvActivateParent, EvShowHelp,
  EvWriteToOutput, EvStartGenMsgWindow, EvSetGenMsgText, EvSetGenMsgTitle,
  EvCloseGenMsgWindow, EvHideGenMsgWindow, EvUnknown, EvFirst, EvLast
  ```
  (`EvSelectParent` = focus the parent WINDOW, not a tree row.)
- **`RequestType`** = `{None, InsertRecord, ChangeRecord, DeleteRecord, SelectRecord, ProcessRecord,
  ViewRecord, SaveRecord}` — browse RECORD verbs, not proc-tree selection.
- **`ResponseType`** = `{None, RequestCompleted, RequestCancelled}` — status only, no payload.

**Conclusion:** procedure-row selection lives entirely inside the hosted native AppGen Clarion program;
reachable ONLY by feeding its incremental locator (keystrokes) or clicking a row. **Locator typing is
irreducible.**

### 3c. The native ClaList is custom-drawn
`LB_GETCURSEL` / `LB_GETTEXT` / `WM_GETTEXT` and UIA/MSAA all return empty on the procedure list (class
`ClaList_…`). It is not a Win32 listbox. (Earlier confirmed by Charlie + this session.)

### 3d. Headless embeditor-source generation is blocked (prior Path A, 2026-05-31)
IL/Cecil scan: every generator detail-object ctor takes a **native pointer**
(`CPweeDetails.ctor(IPWEERequester*)`, `CEmbedEditorDetails.ctor(IEmbedEditorRequester*)`, …). Nothing managed
**produces** an `IPweeDetails` / `IEmbedEditorDetails`; `OpenPwee` / `OpenEmbedEditor` only **consume** one.
The details object is born only inside native C++/CLI glue. So the native embeditor opening **is** the
generator — there is no "just give me the assembled source" seam. (See `docs/ModernEmbeditor-PathA.md`.)

---

## 4. Constructive payoff — kill the locator trick on Modern Embeditor **Save**

We can't *set* selection, but we can now *read* it — so use it as an oracle. The common save flow
(open → edit → save without clicking away) leaves the target proc **still selected**, so Save can take the
**no-type** open (`OpenProcedureEmbedCurrentSelection` → `BM_CLICK` Embeds, which the right-click feature
already proves works) instead of typing the name into the locator.

**Three additive changes (sketch — not yet applied):**

1. `AppTreeService.GetAppTreeSelectedProcedureName()` — the shared reader from §2.

2. New `ModernEmbeditorLauncher.OpenAndMirrorForSave(...)`:
```csharp
internal static bool OpenAndMirrorForSave(AppTreeService appTree, string procName,
    out string source, out List<int[]> ranges, out string error)
{
    source = null; ranges = null; error = null;

    string sel = null;
    try { sel = appTree.GetAppTreeSelectedProcedureName(); } catch { }

    if (!string.IsNullOrEmpty(sel) &&
        string.Equals(sel, procName, StringComparison.OrdinalIgnoreCase))
    {
        // Fast path — no keystrokes. Open whatever is committed (== procName per the oracle).
        if (OpenAndMirrorCurrentSelection(appTree, out source, out ranges, out error))
        {
            // Race guard (a click between read and open): mirrored source must be this proc.
            if (SourceMentionsProcedure(source, procName))
                return true;                       // locator trick avoided
            try { appTree.CancelEmbeditor(); } catch { }
            WaitForEmbedClosed(appTree, 3000);
        }
        source = null; ranges = null; error = null;
    }

    // Fallback: verified locator typing (unchanged behavior).
    return OpenAndMirror(appTree, procName, out source, out ranges, out error);
}
```

3. `ModernEmbeditorSaver.Save` — one-line swap `OpenAndMirror` → `OpenAndMirrorForSave`. Everything downstream
   (range-match guard, per-slot conflict check, bottom-to-top writes, `SaveAndCloseEmbeditor`) untouched.

| Case | Result |
|------|--------|
| Common save (no click-away) | **zero locator typing**, zero mis-select |
| Navigated to another proc | falls back to today's typing path — no regression |
| Click during save | `SourceMentionsProcedure` guard catches it, falls back |
| Native open/close (~2.6 s, ABC) | **still happens** — irreducible |

> Still removes typing only, not the native open cost (proven irreducible in §3d).

---

## 5. Domain invariant relied upon

**Procedure names are unique within a single Clarion `.app`** (confirmed by John; knowledge #27). Therefore:
- `SourceMentionsProcedure` is a **safe** identity guard (a unique name can't match the wrong proc).
- `FileSchema.ProcedureName` can be compared by exact equality — no module disambiguation.
- The dup-name hedges in `OpenCommittedSelection` / `ProcNameFromSource` are dead concerns in practice.
- Uniqueness is **app-level**; across different apps in a solution a name can recur (needs app context).

---

## 6. Reference: handles & discovery (per-session — values WILL differ)

Captured live this session (illustrative only — re-discover each run):
- IDE process: **`Clarion.exe`**
- Top-level IDE window: title `Clarion [Clarion 12.0.14000] - [<solution dir>]`
- Host control (`ApplicationMainWindowControl`): `Handle` (WinForms host of the native proc-view)
- Native procedure list: class prefix **`ClaList_…`**, found via `EnumChildWindows` on the host `Handle`
- `HostedWindowCaption` = `"<app>.app - Procedure view"`

**Discovery methods:**
- Managed object graph: `inspect_ide path:<dotpath>` (from Workbench) / `dump_object_api <dotpath>` (from App).
- Native child HWNDs: `EnumChildWindows` on the host control `Handle` (match `ClaList_` by class prefix —
  the `…071290000H` suffix is a per-session runtime tag, never match it literally).

---

## 7. Tooling gotchas (cost us time — record for next time)

- **`inspect_ide` path navigator CANNOT index collections** — `Foo[0]` / `List[0]` resolve to `null`. It only
  follows property/field names. This is a tool limitation, NOT absence of the member. (Blocked dumping the
  host's private `Hosted` (UINetBinding) / `Invoker` (CWEventInvoker) and indexing pad collections.)
- **Bundled Cecil is v0.6.9** (`C:\Clarion12\bin\Mono.Cecil.dll`) — it has **no** `AssemblyDefinition.ReadAssembly`
  (that's Cecil 0.9+). Use `[Mono.Cecil.AssemblyFactory]::GetAssembly(path)`; iterate `asm.MainModule.Types`
  (NOT `GetTypes()`); recurse `.NestedTypes`; enum members = `Fields` where `IsStatic && IsLiteral`.
  ⚠️ A `try/catch` around the wrong `ReadAssembly` call **silently returns false "not found"** — verify the API
  before trusting a negative (this produced bogus "UIControlEvents not found" results until corrected).
- **PowerShell 7 dropped `ReflectionOnlyLoad`** ("not supported on this platform") — use Cecil for metadata.
- Reflection windows run **in-process** (the addin hosts them): `inspect_ide` navigates from `Workbench`,
  `dump_object_api` from the `App` object. Neither can index collections; neither does `Control.FromHandle`.

---

## 8. Defining assemblies (for future spelunking)

- `SoftVelocity.Generator.UI.ApplicationMainWindowControl(_ViewContent)`, `Application`, `Win32App`,
  `Win32GeneratorInstance` → **`Generator.dll`**
- `SoftVelocity.DataDictionary.Schema.FileSchema` / `FieldList` → **`DataDictionary.dll`**
- `Clarion.GEN.Procedure`, `CPweeDetails` / `CEmbedEditorDetails` (native-pointer ctors) → **`clarion.gen.dll`** (C++/CLI)
- `SoftVelocity.CWPInvoke.{RequestType, ResponseType, InvokeKind, CWEventInvoker}` → **`CommonSources.dll`**
- `Clarion.ASL.UIControlEvents` → **`Clarion.asl.dll`**
- Clarion binding dirs: `…\bin\Addins\BackendBindings\ClarionBinding\Common` and `…\ClarionWin`
