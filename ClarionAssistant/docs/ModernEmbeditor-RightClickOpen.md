# Right-Click Procedure → "Open in Modern Embeditor"

> ⚠️ **RETIRED (ticket 4d16b53a, 2026-07-04).** This entire native-menu-injection + `SetWindowsHookEx`
> approach (`RightClickHookService`) has been **removed**. The CA Embeditor overlay is now triggered by a
> lightweight 1.5s poll (`EmbedEditorMonitorService`) that detects when a native PWEE embed opens — via
> Clarion's OWN built-in **"Embeditor Source"** menu — and auto-attaches the live overlay. No injected menu
> item, no thread hooks. Adopted from Mark Sarson's `EmbedEditorMonitor` (CA issue #55). This document is kept
> for historical record of the hook design only.

Ticket **4b82f1de**. Native-menu injection that adds an **"Open in Modern Embeditor"** item to the Clarion app-tree procedure right-click popup, opening the selected procedure in the Monaco/WebView2 Modern Embeditor.

This doc is the **build-ready phase-2 spec** for both fingerprint branches. Phase-1.5 selects the branch; everything else is shared and locked. Authored from the three-way design consensus (Diana / Eve / Charlie) + CC's live probes, 2026-06-04.

---

## 1. Why this is hard (confirmed findings)

| # | Finding | How confirmed | Consequence |
|---|---------|---------------|-------------|
| F1 | The proc right-click menu, the Views button strip, and the app-tab header are all **native Clarion UI**, none SharpDevelop-codon-extensible | CC live | No clean `.addin` route → native hook required |
| F2 | The live tree selection **cannot be read** by any safe managed means (no `Current`/`Selected`; `ClaList` ignores `LB_GETCURSEL` → −1); reading the native `GenIFace*` pointer **crashes the IDE** | CC live + prior burns | Identity must come from elsewhere |
| F3 | **Right-click commits selection** to the clicked row (PROBE A) | John, live, 2/2 | Launch needs no name — act on committed selection |
| F4 | The IDE is **one process with two UI threads**: native `Cla*` windows on a native UI thread, managed SharpDevelop on another. `SetWindowSubclass` is thread-affine → installing from managed **silently no-ops** on native windows | CC, `GetWindowThreadProcessId` | Per-window subclass is dead; use a thread hook |
| F5 | The native UI thread is **recreated per app-view** (dies/reborn on app open/close) | CC, close/reopen test (tid 60596→92520) | Re-resolve tid per app-open (Case B) |
| F6 | Selecting a menu item **POSTs `WM_COMMAND`** (cmdId 9 = Embeditor) — **not** `TPM_RETURNCMD` | CC v2 | `WH_GETMESSAGE` catches the command — path is live |
| F7 | The popup is **owner-drawn** (`MFT_OWNERDRAW`); `GetMenuString` returns empty for all items. `GetMenuItemID` **still works** | CC v2 | Text fingerprint dead; id-based identify/dedup survives |
| F8 | The `WM_INITMENUPOPUP`/`WM_COMMAND` receiver is a generic **`ClaPopup_*`** window | CC v2 | Owner-class may not discriminate → see §6 |
| F9 | Menu cmdIds are **sequential 1..N** (Embeditor = 9) | John, confirmed | Content (cmdId) **cannot** be the primary discriminator |
| F10 | The proc tree is the **only native pad** (`Clarion.Generator.ApplicationPad`); the editor (AvalonEdit) and solution tree (SharpDevelop `ProjectScout`) are **managed** → their context menus live on the **managed** thread | CC | Managed menus never reach our native-tid hook → "did the hook fire" is itself a near-discriminator (Tier 0, pending phase-1.5) |
| F11 | A plain `MF_STRING` item **renders + is clickable** in the owner-drawn `ClaPopup` (count 16→17) | phase-1.5 | No owner-draw fallback needed (§6 retired) |
| F12 | The popup `GW_OWNER == 0`, `GetParent ==` the **shared** `ClaWin`; stateless lineage never reaches the ClaList. **3 ClaLists** exist (1 visible + 2 hidden). HMENU rebuilt per right-click (fresh handle) | phase-1.5 | Forces **tier-4** correlation; arm gate needs `IsWindowVisible`; inject on every popup, no caching |
| F13 | `WM_CONTEXTMENU` can trigger a **modal `TrackPopupMenu` inside the handler** → `WM_INITMENUPOPUP` fires *before* the `WM_CONTEXTMENU` RET hook would | Eve (analysis) | Arm on the **PRE** hook (`WH_CALLWNDPROC`), gated by `recv==wParam` (single deterministic write) — RET-arm would miss |

---

## 2. Mechanism — thread-specific `SetWindowsHookEx`

Install is **not** thread-affine: call `SetWindowsHookEx` from the managed thread, targeting the native tid; the hook **proc runs on the native thread** (same process → managed delegate, no DLL injection). That is exactly where we need to be to `AppendMenu` to a native `HMENU`.

- **`WH_CALLWNDPROC` (4)** — catches **SENT** messages **before** the target wndproc. Used to **arm** on `WM_CONTEXTMENU` (tier 4). Arming on PRE is mandatory: if Clarion shows the menu via a **modal `TrackPopupMenu` inside its `WM_CONTEXTMENU` handler**, the `WM_INITMENUPOPUP` fires *during* that handler — **before** the RET hook would run (F13). PRE guarantees the arm is set first.
- **`WH_CALLWNDPROCRET` (12)** — catches **SENT** messages *after* the target wndproc. Used for `WM_INITMENUPOPUP` (append **after** Clarion builds the menu so our item survives a rebuild-in-handler).
- **`WH_GETMESSAGE` (3)** — catches **POSTED** messages. Used for `WM_COMMAND` (our menu command).

Three hooks per native tid: PRE arms, RET appends, GETMESSAGE handles the command.

**Launch deferral + WebView2 affinity, solved by one hop:** the hook proc runs on the native thread; the actual open (WebView2/Monaco) **must** run on the managed UI thread (STA). So on our command, marshal to the managed thread (`Control.BeginInvoke` on a managed UI control, or `PostMessage` to a managed-thread sink). This is **both** the off-stack deferral (never launch inside the menu/message stack — the WebView2 reentrancy-freeze lesson) **and** the thread-affinity fix, in a single hop.

---

## 3. Shared scaffold — Case B-MULTI lifecycle (locked, both branches)

The native UI thread is per-app-view (F5) and multiple `.app` tabs may yield multiple concurrent native tids, so the general design is a **tid-keyed hook roster**. A 1-entry roster ≡ single-pair, so this is correct whether there are 1 or N native threads.

```csharp
// ---- state (managed-thread-owned unless noted) ----
static readonly HOOKPROC s_cwp    = CwpProc;       // PRE  — arm (GC-rooted for addin lifetime — CARDINAL RULE)
static readonly HOOKPROC s_cwpRet = CwpRetProc;    // RET  — append
static readonly HOOKPROC s_getMsg = GetMsgProc;    // GETMESSAGE — command
static readonly Dictionary<uint,(IntPtr cwp, IntPtr cwpRet, IntPtr getMsg)> _hooked = new();
static volatile bool _shuttingDown;
const int  WH_CALLWNDPROC = 4, WH_CALLWNDPROCRET = 12, WH_GETMESSAGE = 3;
const int  WM_INITMENUPOPUP = 0x0117, WM_COMMAND = 0x0111, WM_CONTEXTMENU = 0x007B, WM_NULL = 0x0000;
const uint OUR_CMD = 0xE001;          // our menu command id (high, collision-safe)
const uint MF_STRING = 0x0000;
```

**Cardinal rule (F4-family crash):** each `HOOKPROC` is a managed delegate marshaled to a native function pointer. If GC collects it while a hook is installed, the next message calls freed memory → AccessViolation → dead IDE. Hold **three** static delegates (`s_cwp`, `s_cwpRet`, `s_getMsg`) for the addin lifetime, reused across **every** tid. The dictionary stores only `HHOOK` handles — **never** per-tid delegates (N lifetimes to track = footgun). `CallNextHookEx` ignores its `hHook` arg → pass `IntPtr.Zero`.

### 3.1 Discovery + re-arm (the `ArmPass`, managed thread only)

The native windows/threads are created **late** (after every managed event) — so a low-frequency timer is the source of truth; events are eager nudges into the same pass.

```csharp
void ArmPass() {                       // ALWAYS on the managed UI thread
  if (_shuttingDown) return;
  // 1. discover all live native tids hosting Cla* windows
  var live = new HashSet<uint>();
  foreach (var h in EnumClaWindows(GA_ROOT_from_managed_main_handle))  // EnumChildWindows + class-prefix 'ClaWin'/'ClaList'
     live.Add(GetWindowThreadProcessId(h, out _));
  // 2. install on new tids (idempotent) — all THREE hooks together, or none
  foreach (var tid in live)
     if (!_hooked.ContainsKey(tid)) {
        var a = SetWindowsHookEx(WH_CALLWNDPROC,    s_cwp,    IntPtr.Zero, tid);   // PRE — arm
        var b = SetWindowsHookEx(WH_CALLWNDPROCRET, s_cwpRet, IntPtr.Zero, tid);   // RET — append
        var c = SetWindowsHookEx(WH_GETMESSAGE,     s_getMsg, IntPtr.Zero, tid);   // command
        if (a != IntPtr.Zero && b != IntPtr.Zero && c != IntPtr.Zero) _hooked[tid] = (a, b, c);
        else { if (a!=IntPtr.Zero) UnhookWindowsHookEx(a); if (b!=IntPtr.Zero) UnhookWindowsHookEx(b); if (c!=IntPtr.Zero) UnhookWindowsHookEx(c); }
     }
  // 3. prune dead tids
  foreach (var tid in _hooked.Keys.ToArray())
     if (!live.Contains(tid)) { var p=_hooked[tid]; UnhookWindowsHookEx(p.cwp); UnhookWindowsHookEx(p.cwpRet); UnhookWindowsHookEx(p.getMsg); _hooked.Remove(tid); }
}
```

- **Root the enumeration at the managed main-window handle** (`WorkbenchSingleton` main form `Handle`) — plain WinForms, **zero Clarion reflection** — then `EnumChildWindows` + `GetClassName` prefix match. (Beats rooting at the app host, which needs `FindAppViewContent` reflection, and auto-covers a 2nd `.app` tab with no code change.)
- **Triggers:** `/Workspace/Autostart` (start timer + one pass; mirror `LspAutostartCommand`); UI-thread timer ~2–3 s (perpetual, self-healing); eager nudges `ProjectService.SolutionLoaded` (reflection, verbatim from `LspAutostartCommand`) + `WorkbenchSingleton.Workbench.ActiveWorkbenchWindowChanged` → each just calls `ArmPass()`.
- Use the **same UI-thread timer type** `LspAutostartCommand` uses, so `ArmPass` lands on the managed thread natively (no pool-thread / `SetWindowsHookEx` race).

### 3.2 Teardown

- `ProjectService.SolutionClosed` (reflection) → `ArmPass`'s prune step removes dead tids; do **not** stop the timer (next solution must self-arm).
- `/Workspace/Terminate` → set `_shuttingDown = true` **first**, stop+dispose timer, unhook **all three** hooks of every roster entry, clear.
- Dead-thread safety: when a native thread dies the system auto-invalidates its thread-hooks; `UnhookWindowsHookEx` on a stale handle returns false harmlessly. The next `ArmPass` prune self-heals stale entries.

### 3.3 Concurrency contract for the hook procs

The three shared procs can be invoked **concurrently by multiple native tids**. Therefore:
- Proc body is **reentrant** and **read-only** on shared managed state (`_shuttingDown` read; managed-sink handle read). No shared mutable buffers/logs in the hot path.
- Roster mutation happens **only** on the managed thread in `ArmPass` → no lock needed.
- Branch B's correlation state is **`[ThreadStatic]`** → each native tid gets its own slot, lock-free, reentrant by construction.

### 3.4 Hot-path discipline (all three procs)

Fires on **every** sent/retrieved message on each hooked native thread. Therefore: `if (nCode < 0) return CallNextHookEx(...)` immediately; **check message id first**; return `CallNextHookEx` for everything that isn't a target; **zero allocations / logging** on the hot path in production; wrap the whole body in `try/catch` (an exception escaping a native callback corrupts the stack = crash) and fall through to `CallNextHookEx`.

### 3.5 Command handling (both branches, identical)

```csharp
// inside GetMsgProc, on WH_GETMESSAGE, PM_REMOVE only:
if (msg.message == WM_COMMAND && LOWORD(msg.wParam) == OUR_CMD && HIWORD(msg.wParam) == 0) {
   if (!_shuttingDown)
      PostToManaged(/* committed selection per F3 */);   // BeginInvoke → managed/WebView2 thread
   // DO NOT rewrite the MSG by default — see note below.
   return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
}
```

**Neutralize is OPTIONAL / default-OFF.** `OUR_CMD` (0xE001) is an unknown id to Clarion's `ClaPopup` wndproc, which ignores it. Mutating a message inside a shared thread-wide `WH_GETMESSAGE` hook is slightly invasive, so by default we **only observe** 0xE001 + `BeginInvoke` and leave the `MSG` untouched. Add the `msg.message = WM_NULL` rewrite **only if** phase-2 shows Clarion mishandles the unknown id. Less-invasive hook = safer.

**The launch (phase 3) — be explicit so we don't reintroduce the type-to-select step:** "launch on committed selection" means **`BM_CLICK` the Embeditor BUTTON on the Views panel** while the right-click-committed selection (F3) stands — **not** posting `WM_COMMAND(9)` back to the popup (it's already closed by command time). `OpenProcedureEmbed` today **types the proc name to select first**; phase 3 needs a variant that **skips type-to-select** and clicks Embeditor on the CURRENT selection. The managed handler then derives the **authoritative proc name via source-regex** (not `GetEmbedInfo.fileName`, the temp pwee name) and mirrors to Monaco via `ModernEmbeditorLauncher`.

---

## 4. The pluggable discriminator — `IsProcTreePopup`

The **only** part phase-1.5 fills in is one predicate. Everything else (§3) is tier-independent. The hook proc always calls it at `WM_INITMENUPOPUP`; its body is whichever tier phase-1.5 proves discriminates first.

> **PHASE-1.5 VERDICT (2026-06-05): `IsProcTreePopup` = TIER 4 (correlation).** Stateless tiers 1–3 all failed on live data: receiver = generic `ClaPopup_*` (tier 1 dead); `GW_OWNER == 0` and `GetParent ==` the **shared** `ClaWin` — the popup's lineage stops at the shared parent and never reaches the ClaList (tier 2 chain-walk dead); cmdIds sequential (tier 3 confirm-only). The **only** discriminating signal is `WM_CONTEXTMENU.clickedChild == the proc-tree ClaList` → the `[ThreadStatic]` tier-4 arm. Render confirmed (drop §6 fallback). Build phase 2 with the predicate = tier-4 below.

```csharp
// inside CwpRetProc, on WH_CALLWNDPROCRET, CWPRETSTRUCT* p:
if (p->message == WM_INITMENUPOPUP && HIWORD(p->lParam) == 0 /* not system menu */) {
   if (IsProcTreePopup(p->hwnd, p->wParam)   // <-- the one pluggable predicate (tier 1-4)
       && MenuHasSignature(p->wParam)         // secondary confirm: cmdId 9 present (GetMenuItemID) - never primary (F9)
       && !MenuHasOurItem(p->wParam))         // dedup: OUR_CMD not already present (GetMenuItemID)
      AppendMenu(p->wParam, MF_STRING, (UIntPtr)OUR_CMD, "Open in Modern Embeditor");
}
```

**4-tier discriminator order (locked; first that discriminates wins; stateless preferred):**

| Tier | `IsProcTreePopup` body | State? | Phase-1.5 outcome |
|------|------------------------|--------|-------------------|
| 0 | **the hook fired at all** — being on the native tid means the popup is native (F10) | stateless | ✅ confirmed (editor + solution right-clicks fire our hook EMPTY — managed menus never reach the native tid); but other native popups can share the tid, so tier-4 is still the precise gate |
| 1 | `GetClassName(hwnd)` matches a proc-tree-specific class | stateless | ❌ FAILED — receiver is generic `ClaPopup_*` (F8) |
| 2 | **chain-walk** `GW_OWNER`/`GetParent` to the originating **ClaList** | stateless | ❌ FAILED — `GW_OWNER==0`, `GetParent==`shared `ClaWin`; lineage never reaches the ClaList |
| 3 | cmdId signature in `hMenu` | stateless | ❌ confirm-only — sequential ids (F9) |
| **4** | read `[ThreadStatic] s_armNextPopup` (set when a prior `WM_CONTEXTMENU` hit the proc-tree ClaList) | per-tid state | ✅ **SELECTED** — the only discriminating signal is the clicked child |

**Tiers 1-3 are stateless** (`IsProcTreePopup` pure over `(hwnd, hMenu)`). **Tier 4 (SELECTED) adds one `[ThreadStatic]` flag**, armed on the **PRE hook** and consumed on the RET hook:

```csharp
[ThreadStatic] static bool s_armNextPopup;          // per native tid, lock-free, reentrant by construction

// --- ARM: in CwpProc (WH_CALLWNDPROC, PRE), CWPSTRUCT* p ---
if (p->message == WM_CONTEXTMENU) {
   // recv==wParam = the ORIGINAL delivery (recipient IS the clicked window). WM_CONTEXTMENU then
   // BUBBLES via DefWindowProc (recv becomes the parent, wParam stays the clicked window), so
   // gating on recv==wParam is a SINGLE deterministic write at the click target — no last-writer-wins.
   // PRE (not RET) so the arm is set BEFORE any modal TrackPopupMenu shown inside the handler (F13).
   if (p->hwnd == p->wParam)
      s_armNextPopup = IsProcTreeClaList(p->wParam);    // arm ONLY for the proc-tree ClaList (gate, Eve's edge)
   // (no else — a fresh non-matching original click leaves prior arm to be consumed/reset normally)
}

// --- APPEND: in CwpRetProc (WH_CALLWNDPROCRET, RET), CWPRETSTRUCT* p ---
// IsProcTreePopup(...) for tier 4 = { bool a = s_armNextPopup; s_armNextPopup = false; return a; }
// consume+clear on the very NEXT INITMENUPOPUP, armed or not.
```

**`IsProcTreeClaList(hwnd)` — the arm gate (Eve's edge, now concrete from phase-1.5):** phase-1.5 found **3 ClaLists** exist (1 visible pivot + 2 hidden), so a bare "class is ClaList" test would wrongly fire. The confirmed gate is **all three**: `GetClassName(hwnd)` starts with `ClaList` **AND** the parent chain is `ClaChildClient -> ClaWin` **AND** `IsWindowVisible(hwnd)`. Arm only on that.

**Tier-4 safety (false-negative, not false-positive):** arm only on the proc-tree ClaList -> non-proc right-clicks never arm -> never a wrong-menu append; works across the ClaList->ClaPopup boundary because the hook is **thread-level** (both on the same native tid, both SENT, both seen by `CwpRetProc`). Worst case = a missing item, never a wrong-menu item.

**Stale-arm bounding (Eve flag C):** clear the arm in BOTH places — (1) the **PRE original-delivery write** (`recv==wParam`) re-evaluates on every genuine right-click (a non-ClaList click writes `false`, resetting any stale `true`), and (2) **consume+clear on the next `INITMENUPOPUP`** regardless of armed state. (1) covers the no-popup case (a ClaList right-click that yields no menu can't leave a `true` that rides to a later popup, since the next right-click resets it); (2) bounds the arm to the immediately-next popup. Optional **generation counter** (stamp on arm, honor only if the consuming popup's stamp matches) hardens the pathological "armed, then a programmatic popup with no intervening `WM_CONTEXTMENU`" case.

**cmdId-9 is a REQUIRED secondary confirm (Eve flag B), not optional:** append iff `armed AND MenuHasSignature (cmdId 9 present)`. It's the orthogonal backstop for the 3-ClaList edge — even if the arm logic is ever fooled, a popup lacking the Embeditor command won't get our item.

**Ephemeral append:** the popup `HMENU` is transient (rebuilt per show), so our appended item vanishes with it - **no `DeleteMenu` management**. Dedup by `OUR_CMD` presence handles the rebuilt-vs-reused case; the `MenuHasOurItem` guard is the only bookkeeping.

---

## 5. Selection criterion — the phase-1.5 dataset

For the **proc popup**, the **editor right-click menu**, and the **solution-tree menu**, log at `WM_INITMENUPOPUP`:
1. `CWPRETSTRUCT.hwnd` **class** (the receiver — expected `ClaPopup_*`),
2. that window's `GW_OWNER` **class** and `GetParent` **class** (the lineage — possible stateless discriminator),
3. the full **cmdId layout** (confirm sequential 1..N; pick any usable secondary-confirm id),
4. the `WM_CONTEXTMENU` target/wParam **class** (the clicked child — for tier 4),
5. **MF_STRING render check:** append one harmless `MF_STRING@0xE001` → does it **appear**, is it **clickable**, no popup corruption?
6. **cross-proc stability:** re-open a different proc → ids/classes unchanged?

**Decision order (first that discriminates wins; prefer stateless):**
owner-class → owner/parent-chain class → cmdId-signature *(dead per F9, confirm only)* → **correlation**.

Given F8 (`ClaPopup_*` receiver) + F9 (sequential ids), **tier 2 (chain-walk) is the likely primary**, with **tier 4 (correlation) as fallback** if the popup's `GW_OWNER`/`GetParent` lineage never reaches a proc-tree ClaList.

---

## 6. Render fallback — ❌ NOT NEEDED (phase-1.5)

~~If a plain `MF_STRING` item does not render in the owner-drawn popup, owner-draw our item.~~ **Resolved:** phase-1.5 confirmed a plain `MF_STRING` item appended into the owner-drawn `ClaPopup` **renders and is clickable** (item count 16→17). No owner-draw fallback required. Retained for record only.

---

## 7. Build-1 / build-2 split

- **Build 1 (logging-only probe, phase-1 + phase-1.5):** confirm receiver/owner classes, cmdId layout, render, command POST vs RETURNCMD (done — POST), thread stability (done — Case B), instance count (1 today). No menu mutation beyond the single test append.
- **Build 2 (gate, PASSED 6/7):** the §3 scaffold + the selected discriminator tier (§4) + the §3.5 command → managed launch, **launch body a logged no-op** (`BeginInvoke` round-trip proven; zero freeze). The 7th criterion (survives-reopen) is a bounded ≤2.5s re-hook latency, not a logic fault — see §7.1.
- **Build 3 / phase 3 (the real launch):** swap the no-op body for `ModernEmbeditorLauncher` — see §7.2.

### 7.1 Re-hook latency one-shot (the 7th criterion)

`ActiveWorkbenchWindowChanged` fires **before** the new app-view's native windows/thread exist, so the immediate `ArmPass()` on that event enumerates nothing; the perpetual ~2.5 s timer then backstops → a one-time post-reopen miss. Fix: on `ActiveWorkbenchWindowChanged`, in addition to the immediate `ArmPass()`, start a **one-shot `System.Windows.Forms.Timer` (~300 ms)** whose `Tick` calls `ArmPass()` once then `Stop()`+`Dispose()`. UI-thread, `_shuttingDown`-guarded, idempotent (the roster `ContainsKey` guard). Shrinks worst-case arm latency ~2.5 s → ~300 ms. Keep the perpetual timer as the ultimate backstop. (Optional: stagger two one-shots, e.g. 150 ms + 400 ms.)

### 7.2 Phase-3 launch wiring — entry points

**Finding: there is NO existing "open on the current committed selection" entry.** Both `ModernEmbeditorLauncher.OpenProcedure(procName, isDark)` and `AppTreeService.OpenProcedureEmbed(procName, charDelay)` require the name and **type it in via `WM_CHAR`** to select (ClaList ignores `LB_` — `LB_GETCOUNT == 0` → `claListSupportsLB == false`, so it falls to the keystroke locator at `OpenProcedureEmbed` ~line 783). That path also runs a **retry-on-wrong-proc loop** (`SourceMentionsProcedure`) because fast typing can miss.

**But the name is recoverable post-open via source-regex** (cardinal rule #7), so phase 3 doesn't need it up front. ⚠️ **Do NOT use `TryGetActiveEmbeditorSource`'s `title` out-param** — it is `Path.GetFileName(control.FileName)` = the **temp pwee token** (e.g. `C7pwee0.appclw`), not the proc name; using it would label every tab `C7pwee0.appclw` and violate rule #7. Instead, regex the proc name out of the **mirrored `source`** that `OpenAndMirror` already returns: the proc's own declaration is the **first** `(?m)^(\w+)\s+PROCEDURE` match (MAP / local procs come later). CC verified `BrowseAuthors` → `"BrowseAuthors PROCEDURE"` in column 1. The right-click already committed the selection (F3), so:

1. **`AppTreeService.OpenProcedureEmbedCurrentSelection()`** (new) = `OpenProcedureEmbed` **minus Phase 2** (no `LB_*` select, no `WM_CHAR` typing) **and minus Phase 1's `AttachThreadInput`/`SetFocus`** — see E8. Just the Phase-3 block: find the Embeditor `ClaButton` by "beditor" text + cross-thread `SendMessage(btn, BM_CLICK)`. Refactor the existing Phase-3 block into a private helper both entries call (the existing named path keeps its Phase 1+2 unchanged).
2. **`ModernEmbeditorLauncher.OpenCommittedSelection(bool isDark)`** (new) = `OpenProcedure` but: call an `OpenAndMirror` variant that uses `OpenProcedureEmbedCurrentSelection()` (no name in), derive `capProc` via **source-regex** (first `(?m)^(\w+)\s+PROCEDURE` in the mirrored `source` — NOT the `title` temp-pwee out-param), and **drop the `SourceMentionsProcedure` retry loop entirely** (no name to verify — the committed selection is ground truth; this also removes the type-miss failure mode and is faster). If the regex finds no match → E6 abort (no `ShowView`). Then the identical freeze-safe tail: `CancelEmbeditor` + `WaitForEmbedClosed` + the deferred `ctx.Post(ShowView)` with `capProc` = the regex'd proc name.
3. **The hook's `BeginInvoke` body calls `OpenCommittedSelection(isDark)`** — the existing freeze-fixed path, NOT a hand-rolled open (preserves the off-stack `ShowView` that fixes the WebView2 reentrancy freeze).

> ⚠️ **CRITICAL — the freeze risk phase 2 did NOT test (Eve).** Phase 2's "zero freeze" was a logged **no-op** — it never `BM_CLICK`'d or opened anything. The actual open is untested. `SendMessage` is itself **synchronous**; the trigger is non-blocking **only because Clarion's *specific* Embeditor-button handler DEFERS the open** (posts it / toggles + lets a later pumped message do the work) rather than opening inline. **That deferral is a property of THAT button's handler, not of our call.** Therefore the requirement is precise: phase 3 must drive the **same Embeditor `ClaButton`** (found by "beditor" text) whose handler `OpenProcedureEmbed` already uses, **via the shared helper** — not a re-implemented trigger that might hit a different button/code path where the defer property doesn't transfer (→ synchronous inline open on the managed stack = IDE freeze). The named `OpenProcedure` runs this freeze-free today; phase 3 inherits it **only if** it reuses the same button through the shared helper. Pattern: `SendMessage(btn, BM_CLICK); DoEvents(); return;` then `OpenAndMirror`'s `WaitForEmbedOpen` pumps. **Top of the diff review: same button, same deferring handler, shared helper — not re-implemented.**

Net: phase 3 is small — one `AppTreeService` method (Phase-3-only), one launcher method (open-current-selection + source-regex name, no retry), and the one-line hook-body swap. The committed-selection path is **more robust** than the named path (no keystroke select → no miss → no retry).

### 7.3 Phase-3 launch guards (canonical — Eve E1–E7 + C1/C2 + hygiene)

**E1 — Double-open serialization.** Single static `_openInProgress` (volatile bool, managed UI thread), set at the **START of the `BeginInvoke`'d launch delegate** (managed side, not the hook proc); 2nd click while in-flight → **bail**. Cannot key by proc identity (no name pre-open). *Why:* single-instance native embed + `OpenCommittedSelection` pumps internally (`WaitForEmbedOpen`/`DoEvents`) → a 2nd delegate re-enters mid-pump → concurrent opens conflict on the single-instance lock.

**C1 (critical ordering).** The flag **check→set must occur BEFORE the first internal pump**, with NO pump/await between the check and the set — else a 2nd delegate slips through the gap. (Check-and-set is effectively atomic on the single UI thread *only* if nothing pumps between them.)

**E2 / C2 — Timeout-clear sizing.** Primary clear = completion/failure in `finally`. The backstop watchdog clearing `_openInProgress` must be **> the COLD-open ceiling, not just the warm ~2.6 s**: `WaitForEmbedOpen` allows up to **45 s** for the first (ABC-loading) open, so a 10 s watchdog would fire mid-legit-cold-open → 2nd delegate slips in → concurrent conflict. **Implemented: 60 s** (`OpenWatchdogMs`, > the 45 s ceiling). Timeout is pure backstop, never the primary clear. *(Original "10 s+" guidance was too low — corrected after review against `WaitForEmbedOpen(45000)`.)*

**E3 — Already-open → focus, BOTH surfaces.** (a) existing **Monaco tab** for that proc → activate it (post-open, keyed by the **source-regex proc name**), abort the duplicate; (b) **native embeditor** already open for that proc → prefer mirroring the existing open over a 2nd `BM_CLICK` (define the BM_CLICK-when-already-open behavior).

**E4 — Target-still-valid + `_shuttingDown` re-check INSIDE the delegate.** The app/solution may have **switched in the async gap** between click and delegate run → re-validate the native ClaWin / selection / Embeditor-button targets still exist; abort cleanly if torn-down or swapped. Re-check `_shuttingDown` here too, not just at hook time.

**E5 — Exception isolation (CRITICAL).** Wrap the ENTIRE marshalled delegate in `try/catch` + log + swallow. It is hook-originated with **NO error boundary above it** → an unhandled exception on the UI thread = **IDE CRASH**. The `finally` also clears `_openInProgress`.

**E6 — Name-resolution failure → ABORT.** If the **source-regex** finds no `(?m)^(\w+)\s+PROCEDURE` match in the mirrored source → `CancelEmbeditor` + **NO Monaco tab** + log. Hard rule: **no confident identity → no launch.** Never open a blank/wrong-proc tab.

**E7 — Stale-selection / sequential read.** Keep `BM_CLICK` → source-read → regex **tightly sequential on the managed thread, no awaits between**. The mirrored source's first `PROCEDURE` decl IS the ground-truth identity (there's no pre-captured name to validate against), so nothing may change between click and read.

**E8 — `AttachThreadInput` × per-app-view thread mortality (Eve).** The existing `OpenProcedureEmbed` attaches managed→native for Phase-1 focus/typing and holds it across Phase-2's pumps. Because the native thread is **per-app-view and destroyed on app/solution close** (F5), a leaked/dangling attach to a since-dead native tid is not just a leak — it corrupts the managed UI thread's shared input-queue association → input hang / main-thread deadlock.
- **PREFERRED: take NO attach.** Phase 3 drops typing; the only native op is `SendMessage(btn, BM_CLICK)`, a cross-thread message **send** that needs neither focus nor the merged input queue. No `AttachThreadInput`, no `SetFocus` → the whole hazard class is moot. (Verify in the phase-3 test that BM_CLICK fires on the committed selection without focus — expected, since the button acts on current selection.)
- **FALLBACK (only if BM_CLICK needs focus): detach BEFORE the pump.** `attach → SetFocus → BM_CLICK → detach`, all synchronous with **no pump/await between**, detach completing **before `WaitForEmbedOpen`**. This single discipline closes all three hazards: (1) no attach spans native-thread teardown; (2) inner `try/finally` for the detach, **nested inside** the E5 catch, so detach runs before E5 swallows; (3) the reconcile timer's `ArmPass` only fires re-entrantly *during a pump* (it's on the managed thread), so if the attach never spans a pump the timer can never evict/unhook a tid we're attached to — the race is structurally impossible, not merely unlikely.

**Hygiene:** (h1) `CancelEmbeditor` must FULLY reverse the native open (no orphaned single-instance lock) and not disturb the focused tab. (h2) Dropped/different-proc → small "Embeditor busy — try again" hint + log; log the redundant ~2.6 s open cost on a same-proc re-click.

## 8. Cardinal rules (carry across every variant)

1. **Three** static GC-rooted `HOOKPROC` delegates (PRE arm / RET append / GETMESSAGE command) for the addin lifetime; roster stores `HHOOK` handles only (3 per tid).
2. Launch always marshaled off the message stack onto the managed/WebView2 thread (off-stack deferral + STA affinity in one hop).
3. Fingerprint gate is **load-bearing** (the thread hook sees every popup on the native tid) — it is the only filter.
4. `GetMenuItemID` for identify + dedup (owner-draw-safe); `GetMenuString`/text is dead.
5. Hot-path early-out + `try/catch` + always `CallNextHookEx`.
6. `_shuttingDown` latch gates append + launch during teardown; `UnhookWindowsHookEx` on every teardown path.
7. Authoritative proc name = **source-regex**, never `GetEmbedInfo.fileName` / `TryGetActiveEmbeditorSource.title` (both are the temp pwee token).
8. **All timers (perpetual reconcile + one-shot) are UI-thread timers** (`System.Windows.Forms.Timer` / the `LspAutostartCommand` type), NEVER `System.Threading.Timer`/threadpool. Load-bearing for the whole roster: `SetWindowsHookEx`/`UnhookWindowsHookEx` bookkeeping is thread-affine and `_hooked` is mutated lock-free on the assumption of single-threaded UI access — a pool-thread `ArmPass` corrupts the roster (and breaks E8-3). Mutate `_hooked` / install / unhook ONLY on the UI thread.
9. **The `BM_CLICK` trigger is non-blocking** — `SendMessage(BM_CLICK)` + `DoEvents` + return, then `WaitForEmbedOpen` pumps; never a blocking synchronous open on the managed stack (§7.2 ⚠️).
