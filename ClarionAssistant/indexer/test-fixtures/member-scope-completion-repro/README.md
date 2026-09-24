# Member-scope completion regression fixture

Repro for `SharedLspBridge.cs`'s buffer-derived member scope overriding the language server's
members — `MergeMemberAccessCompletions` (the GROUP/QUEUE branch) and the `ParseScopeStructures`
scan that feeds it.

`MergeMemberAccessCompletions` resolves a member access's owner from the **live editor buffer**,
adds the members it knows about, and returns that name set as `memberScope`. The caller then keeps
only completion items whose bare identifier is in that set — so the set is treated as
**authoritative**, and anything the server resolved that is missing from it is dropped.

Two defects made that set wrong, and both present as "`Instance.` lists the wrong members, or
almost none, while the server had already returned the right ones":

1. **A declaration that closes on its own line stayed "open".** `CgEndLine` (`^\s*END\b`) and
   `CgPeriodEnd` (`^\s*\.\s*$`) are both start-anchored, so neither can see a terminator at the
   *end* of the declaration line. The structure stayed open for the rest of the scope and collected
   every following declaration as one of **its** fields — so the scope became the surrounding
   procedure's locals. Fixed with `CgClosesOnSameLine()`.
2. **A declaration's type argument was discarded.** `CgStruct` recorded only `Name`/`Pre`/`Fields`,
   so for `Q QUEUE(SomeType)` the scope held the inline fields alone and dropped everything the
   server resolved from the type. Fixed with `CgStruct.BaseType` + `CgExtractBaseType`, and the
   GROUP/QUEUE branch now declines to scope at all when a base type is present.

## Why the types live in a separate file

`MemberScopeTypes.inc` is not a tidiness choice. The defect is in a scan of the **live editor
buffer**, which only ever sees the `.clw` it was invoked on. A type declared in another file is
precisely what that scan cannot resolve while the language server resolves it normally. Moving
these declarations into the `.clw` would make the repro pass for the wrong reason.

## Why the cases are procedure-locals

Equally deliberate. It is where these declarations live in real code, and it keeps the fixture
focused on the completion defects.

Declared as global `PROGRAM` data instead, a dotted reference outside any procedure takes a
different hover path — `HoverProvider.resolveDottedFieldHover` splits the single `StructureField`
token at the dot — and hovering the **structure** half then answers with the **field**'s hover
(`ItemQ` reporting `CategoryName`). That is pre-existing behaviour with nothing to do with these
defects — confirmed against a pristine checkout — but it is a confusing thing to meet while
working through this fixture. Do not "simplify" the declarations back to global scope.

## The fixture compiles, and that is part of the evidence

`MemberScopeRepro.clw` is a complete, compiling, runnable `PROGRAM`, not a fragment. Case B's
`CODE` section uses a field inherited **from the type** and a field declared **inline** on the same
variable:

```clarion
ItemQ.Code         = 1          ! from ItemQueueType
ItemQ.CategoryName = 'inline'   ! declared on ItemQ itself
```

A successful build is therefore the compiler confirming the semantics the fix depends on — a typed
structure carries **both** sets of fields — rather than that being asserted from documentation.
Verified: builds clean (exit code 0) against Clarion 11.

```
msbuild MemberScopeRepro.cwproj /p:Configuration=Debug /p:ClarionBinPath=<clarion>\bin
```

## Contents (`MemberScopeRepro.clw`)

| Case | What it exercises | Expected after the fix |
|---|---|---|
| A | `Settings GROUP(SettingsGroupType) END` closing on its own line, followed by three unrelated locals | `Settings.` offers the **type's** fields. It must NOT offer `ReplDate`/`ReplTime`/`Counter` — that was defect 1 |
| B | `ItemQ QUEUE(ItemQueueType)` **plus** two inline fields | `ItemQ.` offers **all five** — `Code`/`RefDate`/`Description` from the type and `CategoryName`/`SourceSystem` from the block. Only the two inline ones appeared before — that was defect 2 |
| B (partial) | Type `ItemQ.C` rather than a bare dot | Same set, filtered. **Before the fix this differed from case B** — with a partial, the addin's own additions were filtered out before its scoping pass, the scope matched nothing, and the caller's "only scope when matches remain" guard left the server's list alone. The list was correct only when our own contribution failed to match; that asymmetry is what made the cause findable |
| D | `Cfg GROUP,PRE(CFG)` — a plain structure, no type argument | Negative control. Its field set is genuinely complete, so scoping still applies and still suppresses the server's keyword dump. Behaviour must be unchanged |
| E | `Appended QUEUE,NAME('APPEND')` | Must NOT be read as closing on its own line. `CgClosesOnSameLine` blanks string literals before looking for a terminator, so the `END` inside `'APPEND'` cannot false-close it |
| F | Opener with a trailing `! not the end of this group` comment | Must NOT be read as closing on its own line. The comment is stripped before the check |
| G | `Prefixed QUEUE,PRE(PFX)` | Negative control for base-type extraction: `,PRE(PFX)` must NOT be mistaken for a type argument. The pattern is anchored to the first token after the keyword, so a later attribute's parentheses cannot be read as one |

## Verify

Open `MemberScopeRepro.clw` in the Clarion IDE and use completion at each case above.

- **Before the fix**: case A offers the three following locals instead of the type's fields; case B
  offers only `CategoryName`/`SourceSystem`, while typing `ItemQ.C` offers the type's `Code` —
  the same request answering differently depending on the typed partial.
- **After the fix**: case A offers the type's fields; case B offers all five; the bare dot and the
  partial agree; cases D–G are unchanged.

The regex-level behaviour behind cases E, F and G is also checkable without the IDE — both helpers
(`CgExtractBaseType`, `CgClosesOnSameLine`) are plain .NET regex logic and were exercised
standalone across 14 cases covering exactly these guards.
