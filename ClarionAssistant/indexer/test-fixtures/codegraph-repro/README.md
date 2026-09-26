# CodeGraph parser regression fixture

Contributed by [@geircodes](https://github.com/geircodes) alongside issues #79–#90, extended for
the `LIKE(...)`/`EQUATE`-alias CLASS-member fix (PR #92), the GROUP-typed CLASS-member fix
(PR #93), the inherited-CLASS-member dotted-call resolution fix (PR #112), the
built-in-name-collision fix (PR #118), issue #97's attrs+same-line-terminator fix, and the
overload-attribution fix (Bug P) — a single compiling Clarion solution whose procedures each
exercise one historical parser/indexer bug. This is currently the only regression coverage the
CodeGraph parser has; run it after ANY change to `Parsing/ClarionParser.cs` or
`Graph/CodeGraphIndexer.cs` (either synced copy).

**Bug Q is the one exception**: `UnreachableLocalRefTest` (bottom of `WorkerLib.clw`) does NOT
compile — deliberately; see its own section below for why that's fine here.

Includes `WorkerClass.Ask` — a method whose name collides with the Clarion built-in `ASK()`
statement — as coverage for Bug N (PR #118). Before that fix its two call sites resolved to
**zero** `calls` rows (the dotted/`SELF.` call-detection loops skipped any built-in-named method);
after it, they resolve to the expected **2**. If that count ever drops back to 0, Bug N has
regressed.

## Run

```powershell
indexer\bin\Debug\clarion-indexer.exe index test-fixtures\codegraph-repro\ReproSolution.sln --db %TEMP%\codegraph-repro.db
```

## Expected results (verified 2026-07-17 with all #79–#90 fixes applied, plus #92, #93, #112, and
#118, then re-verified with #97, with Bug P, and with Bug Q (2026-08-07); line numbers below
reflect the fixture AFTER Bug Q's `WorkerLib.inc`/`WorkerLib.clw` additions — Bug Q added 14 lines
before `OverloadBugClass.Dispatch`, so every line number at or after it shifted +14 from the
Bug-P-era numbers previously documented here, and 2 lines were added to `Worker.clw` before
`MainHelperProc`, shifting it +2)

### Callers of `WorkerClass.Sign` — exactly 22 `calls` rows

| Caller | Line | Proves issue |
|---|---|---|
| TestSignatureFlow | 39 | baseline (direct call) |
| TestSignatureFlow | 45 | baseline (second call shape) |
| ParameterTest | 57 | #87 (call through PROCEDURE parameter) |
| ReturnTest | 66 | baseline (inline RETURN call shape) |
| MainHelperProc | 66 (`Worker.clw`) | #81 (procedure in main PROGRAM file) |
| OwnerClass.CallViaMember | 77 | #84+#86 (.inc member, cross-file type) |
| OwnerClass.CallViaCommentedMember | 99 | #85+#86 (trailing-comment member) |
| CommentedLocalTest | 115 | #85 (trailing-comment DATA local) |
| GroupBugClass.CallViaAfterGroupMember | 133 | #88 (member after inline GROUP END) |
| PeriodBugClass.CallViaAfterPeriodMember | 149 | #88 (member after inline GROUP period) |
| OmitTest | 186 | #79 (call after OMIT block) |
| AfterOmitProc | 195 | #79 (procedure after OMIT block) |
| CommentEmbeddedTest | 212 | #80 (call with embedded comment) |
| ConditionalOmitTest | 226 | #79 (conditional OMIT/COMPILE) |
| GroupQueueLocalTest | 245 | #89 (local after GROUP(Type) two-line) |
| InlineLocalGroupTest | 260 | #89 (local after GROUP(Type) END inline) |
| AttrTermLocalGroupTest | 280 | #97 (local after GROUP(Type),attrs + same-line terminator) |
| LocalDerivedClassTest | 306 | #90 (attribution after local CLASS(Parent)) |
| LikeMemberBugClass.CallViaPlainInstanceMember | 323 | #92 (call through a reference CLASS member, unaffected control) |
| MultiLineGroupBugClass.CallViaAfterMultiLineGroupMember | 341 | #93 (member after multi-line GROUP with its own extra field) |
| DerivedWorkerClass.CallViaInheritedMember | 352 | #112 (member declared on a BASE class, accessed via SELF. from a DERIVED class's own method) |
| OverloadBugClass.Dispatch (LONG overload) | 363 | Bug P (own overload's call, correctly self-attributed — not misattributed to the CSTRING overload) |

### Callers of `WorkerClass.Ask` — exactly 5 `calls` rows (Bug N fixed in #118, +1 from Bug P, +2 from Bug Q)

`Ask` is identical in shape to `Sign` (same class, same signature) — the only difference is its
name, which happens to collide with the Clarion built-in `ASK()` window/UI statement. Both call
sites below sit directly next to an equivalent, resolving `Sign` call on the SAME object, at the
SAME call site, so the two can be compared line-for-line:

| Caller | Line | Same-site `Sign` call (for comparison) | Path proven fixed |
|---|---|---|---|
| TestSignatureFlow | 44 | line 39 (`worker.Sign( 1 )`) | DATA-section local variable (baseline path) |
| OwnerClass.CallViaMember | 83 | line 77 (`SELF.MyWorker.Sign( 10 )`) | cross-file CLASS member (#84/#86, and #112's inheritance walk when applicable) |
| OverloadBugClass.Dispatch (CSTRING overload) | 382 | line 363 (`worker.Sign( pValue )`, LONG overload) | Bug P — sibling overload's call, correctly self-attributed to the CSTRING overload's own line (377), not the LONG overload's (360) |
| UnreachableLocalRefTest | 417 | n/a — calls the QUEUE,TYPE overload directly, no `Sign` call at this site | Bug Q — the call *resolves* correctly (its target, `worker`, is legitimately declared); Bug Q is about whether its bare-word ARGUMENT (`MyValue`) wrongly resolves, not about this relationship |
| UnreachableLocalRefTest | 418 | n/a | Bug Q — same call, second argument (`TestQ`) is the one under test here |

**Root cause (fixed by #118)**: `"ASK"` is in `ClarionBuiltins.cs`'s `_builtins` set (window/UI
statement). Before #118 both the `SELF.Method` and the dotted `ObjectName.Method` call-detection
loops in `CodeGraphIndexer.cs` unconditionally `continue`d past any method name matched by
`IsBuiltInOrKeyword(...)`, before any type resolution was attempted — erasing the call. #118
removed those two guards: a dotted call (`worker.Ask(...)` / `SELF.Ask(...)`) is syntactically
never how a bare built-in statement (`ASK(...)`) is written, so the collision can't actually
occur at these two sites. Independent of #112 (inheritance): the class-member call site above
resolves the member's type correctly (`Sign` proves that at the very same call site) — `Ask` was
skipped purely by name, before that type resolution was ever reached.

Real-world confirmation (not reproduced in this fixture, referred to generically): the same
built-in/keyword collision was confirmed against a production Clarion solution across at least
19 distinct real class-method names beyond `Open`/`Close`/`Ask` (e.g. `Delete`, `Send`, `Post`,
`Empty`, `Reset`, `Get`, `Put`, `Update`, `Destroy` — all real Clarion built-in keywords also used
as ordinary ABC-style class method names), suggesting a substantial number of currently-invisible
calls solution-wide, not a narrow edge case.

### Bug P: overloaded procedure/method names collapsed onto one arbitrary overload

`OverloadBugClass.Dispatch` is declared twice with the exact same name, differing only by
parameter type (`LONG` vs `*CSTRING`) — a real, legal Clarion overload. `ResolveRelationships`
tracked "which procedure is this source line inside" (`currentProcId`) via dictionaries keyed by
name only (`symbolByFile`/`symbolNameToId`), which can hold just one id per name. Before this fix,
every line inside **both** `Dispatch` bodies — every call, every variable reference — collapsed
onto whichever overload happened to be inserted last, regardless of which overload's body the
scanner was actually inside.

The CSTRING overload delegates to the LONG overload via `SELF.Dispatch(...)`, the same general
shape as the real-world case that motivated this fix (an overload calling a sibling overload of
the same name via `SELF.Method(...)`). Each overload calls a different, distinguishable target
(`WorkerClass.Sign` from the LONG overload, `WorkerClass.Ask` from the CSTRING overload) so the two
bodies' own calls can be told apart in the results:

- **Before the fix**: both `worker.Sign(pValue)` (inside the LONG overload) and `worker.Ask(result)`
  (inside the CSTRING overload) would be attributed as coming from the SAME one overload symbol —
  whichever loaded last — never from their own respective overloads.
- **After the fix**: `WorkerClass.Sign` gains exactly one caller row from `OverloadBugClass.Dispatch`
  at line 363, correctly attributed to the LONG overload's own definition line (360). `WorkerClass.Ask`
  gains exactly one caller row from `OverloadBugClass.Dispatch` at line 382, correctly attributed to
  the CSTRING overload's own definition line (377) — a different id than the Sign caller above, even
  though both share the literal name `OverloadBugClass.Dispatch`.

**Deliberately NOT fixed by this change, and not a regression if still true**: the
`SELF.Dispatch(99)` call at line 381 (inside the CSTRING overload, intending to call the LONG
overload) still resolves its call *target* to the CSTRING overload itself (i.e. `to_id`'s
definition line is 377, not 360) — call-target resolution for an overloaded name still isn't
type-aware; it still picks whichever overload loaded last, same as before this fix. This fix only
corrects the *caller* side (`currentProcId`/`currentProcName` — "which procedure is this line
inside"), not the *callee* side (which overload a given call site actually invokes). Per-overload
caller *counts* (grouping by `to_id` for an overloaded name) remain unreliable; "what does this
specific overload call" (grouping by `from_id`) is now correct.

### Bug Q: F12/Ctrl+Click's CodeGraph fallback resolved to an unrelated procedure's local

Not a parser/indexer bug like A–P above — this reproduces the exact real-world trigger for a
runtime bug in `SharedLspBridge.cs` (the ClarionAssistant addin's C#, a separate assembly this
indexer never touches), discovered live: a call argument left undeclared by a typo/omission,
whose name happened to already be a local elsewhere in the class.

`CgDefinitionFromDb` (F12/Ctrl+Click's CodeGraph fallback, reached only once the upstream LSP's
own scope search has already returned nothing) looked symbols up via a flat, unordered
`WHERE LOWER(name)=LOWER(@name) LIMIT 1`, with no scope filter. Its sibling `CgHoverFromDb`
already guards this exact shape with `IsUnreachableLocalVariable` — a "variable" symbol whose
`parent_name` resolves to a `procedure`/`routine` symbol can never legitimately be referenced from
outside that one procedure. `CgDefinitionFromDb` never called it.

`MyValue` (a `LONG`) and `TestQ` (a `QUEUE(TestQtype)`) are declared ONLY as locals of
`WorkerClass.Sign` (`WorkerLib.clw` line 10/11) — nowhere else in the solution.
`UnreachableLocalRefTest` (bottom of `WorkerLib.clw`) references both by bare name as call
arguments to `WorkerClass.Ask`'s new QUEUE-taking overload, without declaring either itself:

```clarion
result = worker.Ask( MyValue )                  ! MyValue undeclared here
result = worker.Ask( 'test', result, TestQ )     ! TestQ undeclared here
```

This file therefore does **not** compile — deliberately, and unlike every other bug in this
fixture. Referencing a genuinely undeclared identifier in real code is a hard Clarion compile
error; that's the whole point (it's literally the compile error that led to this fixture addition
— "no matching procedure", traced to this exact undeclared-argument shape). The indexer parses
text regardless of compile state, and F12/hover/Ctrl+Click work off the live buffer regardless too
— compiling was never a precondition for reproducing or fixing this.

**Live-confirmed 2026-08-07** (`ClarionAssistant-git` = pre-fix, `ClarionAssistant-consolidated` =
post-fix; both freshly re-indexed before each check — a stale `.codegraph.db` from a previous
build gives misleading results, see [[gotcha_reindex_when_switching_ca_builds]]):
- **F12 on `MyValue`**: pre-fix jumped to `WorkerClass.Sign`'s `MyValue` declaration — wrong (the
  guard was missing). Post-fix: no definition found — correct.
- **F12 / Ctrl+Click on `TestQ`**: post-fix, no definition found, no hover — correct. (Not
  independently confirmed pre-fix; `MyValue`'s result already demonstrates the same code path,
  and `TestQ` has the identical single-candidate DB shape.)

**A second, independent bug was found alongside this one, upstream in Clarion-Extension**:
hovering `MyValue` returned a *third* wrong answer — `WorkerLib.inc`'s `TestQtype` QUEUE,TYPE
field declaration — which is neither of the two locations above and cannot have come from
ClarionAssistant's CodeGraph fallback at all (confirmed: **no CodeGraph symbol exists for
`TestQtype` or its `MyValue` field** — see the Symbols section below and
[[gotcha_queue_type_invisible_to_codegraph]]). Root cause: Clarion-Extension's
`MemberLocatorService.isVariableLookupCandidate` guards against resolving into a CLASS/INTERFACE
member (`context.inClass || context.inInterface`, added for a prior bug — bare-word hover
resolving to an unrelated class member) but has no equivalent check for `context.inQueueOrGroupOrRecord`,
even though `StructureContext` already exposes that flag. A QUEUE/GROUP/RECORD field has the same
property a CLASS member does — only reachable via qualified access (`TestQ.MyValue`), never as a
bare word — so the same guard needs the same third condition. This is almost certainly also why
the original "undeclared variable" diagnostic never fired: the resolver believes the name exists.
Tracked separately; not fixed by this change.

### Symbols

- 10 `class` symbols: WorkerClass, OwnerClass, DerivableClass, GroupBugClass, PeriodBugClass,
  AfterBugClass (#84: sourced from the `.inc` despite `<None Include>`; #88: the last two
  vanished entirely before the depth-leak fix), LikeMemberBugClass (#92),
  MultiLineGroupBugClass (#93), BaseWorkerClass and DerivedWorkerClass (this PR).
- `LocalDerived` is a **local variable** of LocalDerivedClassTest typed `DERIVABLECLASS` —
  NOT a global class (#90).
- `pWorker`: `scope='parameter'`, parent `ParameterTest`, params `&WorkerClass` (#87).
- `OwnerClass.MyWorker` + `OwnerClass.CommentedWorker`: `scope='class'`, `&WorkerClass`
  (#84, #85).
- `workerRef` (`&WORKERCLASS`), `LocalGroup` / `InlineLocalGroup` (`GROUP(SmallGroupType)`)
  local variables (#85, #89).
- `LikeMemberBugClass.GenCertData` (`LIKE(SmallGroupType)`) and `LikeMemberBugClass.SomeHandle`
  (`SMALLHANDLETYPE`, a custom `EQUATE`-aliased scalar synonym): both `scope='class'` — #92's
  motivating case (LIKE()-declared / EQUATE-alias-typed CLASS members were never captured at
  all before it). `LikeMemberBugClass.PlainInstanceMember` (`&WorkerClass`) is an
  unrelated reference member, already handled correctly before this fix — kept as a negative
  control. An **earlier** version of this repro tried a plain by-value `WorkerClass` instance
  here instead of the `EQUATE`-alias case, to exercise the same catch-all fallback — that
  construct does NOT compile at all (confirmed directly); replaced with the actual real-world
  trigger.
- `GroupBugClass.InlineGroup` and `PeriodBugClass.InlineGroupPeriod` (both `GROUP(SmallGroupType)`,
  `scope='class'`): previously used only to prove #88's depth-tracking fix, but neither ever
  produced a symbol for its OWN name until #93 — confirmed retroactively fixed by re-running
  this fixture.
- `MultiLineGroupBugClass.MultiLineGroup` (`GROUP(SmallGroupType)`, `scope='class'`): the
  genuine multi-line form (its own extra field, `ExtraField`, before its own separate closing
  `END`) — #93's motivating case (a CLASS member that is itself a GROUP instantiation never got
  a symbol for its own name at all before it, in ANY form: self-closing or multi-line).
  `MultiLineGroupBugClass.HiddenGroupMember` (`PRIVATE`) correctly stays absent, mirroring
  `GroupBugClass.HiddenMember`'s exclusion for the simple-reference-member case.
- `MultiLineGroupBugClass.AttrTermGroup` / `.AttrTermGroupPeriod` (both `GROUP(SmallGroupType)`,
  `scope='class'`): the attrs+same-line-terminator forms (`...,DIM(2) END` / `...,DIM(2).`).
  The declaration regex's `term` group never matches these (the `,.*` attrs alternative swallows
  the terminator), so self-closing detection must ALSO re-check the end of the line — without
  that, `classEndDepth` leaks and every member/class after them vanishes.
  `CallViaAfterMultiLineGroupMember` still appearing in the callers table above proves no leak.

- `BaseWorkerClass.BaseWorker` (`&WorkerClass`, `scope='class'`): declared once, on the BASE
  class. `DerivedWorkerClass` (`parent_name='BaseWorkerClass'`) never redeclares it. This is
  distinct from the earlier `OwnerClass.MyWorker` case (#84/#86, a member accessed from a method
  on the SAME class that declares it) and from the `LocalDerived` case (#90, a class declared
  and overridden entirely inside one procedure's own DATA section) — here the member and the
  calling method live on two different, both top-level, `.inc`-declared classes joined only by
  `CLASS(BaseClass)` inheritance. Proves the dotted-call resolver's class-member fallback walks
  the `inherits` chain instead of only ever checking the calling method's own class name.
- `WorkerClass.Ask` (Bug N, **fixed in #118**): a `procedure` symbol, parsed and stored exactly
  as correctly as `WorkerClass.Sign` right next to it (proving the symbol/parsing side was always
  unaffected) — the bug was entirely in call-site resolution, not symbol capture. Compare against
  the "Callers of `WorkerClass.Ask`" table above.
- `OverloadBugClass.Dispatch` (Bug P): two `procedure` symbols sharing the identical
  name, at lines 360 (`( LONG pValue )`) and 377 (`( *CSTRING pValue )`) — both were ALWAYS
  correctly stored as two distinct symbols (proving, like Bug N, that the bug was entirely in
  relationship resolution, not symbol capture). Compare against the "Bug P" writeup and the
  `WorkerClass.Sign`/`WorkerClass.Ask` caller tables above.
- `MyValue` and `TestQ` (Bug Q): both `type='variable'`, `scope='local'`, `parent_name='WorkerClass.Sign'`
  — the ONE legitimate declaration of each, and (since `FindSymbolByName`'s lookup is unordered
  and unscoped) also the arbitrary wrong answer the pre-fix bug returned. `TestQtype` (the
  `QUEUE,TYPE` in `WorkerLib.inc`) and its `MyValue` field: **no symbol at all** — confirmed by
  direct query, not merely absent from this list — see [[gotcha_queue_type_invisible_to_codegraph]].
  `UnreachableLocalRefTest` (Bug Q): a `procedure` symbol like any other; the point is what it does
  NOT have — no `MyValue`- or `TestQ`-named local of its own.

### Program symbol (#81)

- `Worker` (`type='program'`) has `calls` rows to every procedure invoked from the global
  CODE section (**21 rows** — was 13 before global-data indexing, 12 before Bug Q's
  `UnreachableLocalRefTest()` call was added), and **zero** incoming `calls` — the local variable
  named `worker` must never resolve to the program symbol despite the case-insensitive name
  collision. The 8 rows added by ticket d1a0aea6 are the dotted calls through the global class
  instances (`owner.CallViaMember()` at line 40 → `OwnerClass.CallViaMember`, and likewise for
  `groupBug`/`periodBug`/`afterBug`/`likeMemberBug`/`multiLineGroupBug`/`derivedWorker`): before
  globals were indexed, those instance variables had no symbol, so the dotted-call resolver could
  not type them and the calls were silently absent. If this count drops back to 13, global-data
  indexing has regressed.

### Global data (ticket d1a0aea6)

- Exactly **7** `type='variable', scope='global'` symbols — the class instances declared between
  `Worker.clw`'s MAP and its global CODE: `owner`, `groupBug`, `periodBug`, `afterBug`,
  `likeMemberBug`, `multiLineGroupBug`, `derivedWorker`, each with `params` naming its class type.
  Before d1a0aea6 this count was **0**: the PROGRAM file's declaration section was never scanned.
- Total symbols: **119** (was 112) — the +7 is exactly these globals.
- **Zero** phantom `procedure` symbols in `Worker.clw` for the MAP prototypes
  (`TestSignatureFlow`/`ParameterTest`/...). This fixture's MAP writes prototypes at COLUMN 0,
  which is legal and compiles — the full-file parse must skip the MAP block wholesale (explicit
  MAP depth tracking in ParseMemberFile), or each column-0 prototype is minted as a phantom
  procedure definition and the state machine derails. If phantom `TestSignatureFlow` (etc.) rows
  appear in `Worker.clw`, that tracking has regressed.
- Class symbol count is **11**, not the 10 the list above says — `OverloadBugClass` (Bug P) was
  never added to that list's count when it was introduced; 11 was already the correct pinned
  value before d1a0aea6 (verified against the pre-d1a0aea6 build).

### Scope-ordered call resolution + DO + decl_kind (ticket b7553893)

A SECOND project, `proj2\ReproProject2`, exists purely for these pins. Its `MainHelperProc`
deliberately shares its name with ReproProject's. No pre-existing fixture file was changed, so
every line pin above survives. Totals become **127 symbols / 4 files / 2 projects**.

- `MainHelperProc` has **2** `decl_kind='implementation'` rows (one per project) — and each
  project's call resolves to ITS OWN copy: ReproProject's program-CODE call (line 39) to
  `Worker.clw`'s, `Caller2`'s call to `proj2\Worker2Lib.clw`'s, both `ambiguous=0`. Before
  scope-ordered resolution, both landed on whichever row was inserted last. If either row
  flips file, resolution has regressed.
- Exactly **1** `type='do'` edge: `Caller2 -> Tidy:Up2`, whose routine symbol carries
  `parent_name='Caller2'`. The label deliberately contains a colon — the old `\w+` DO regex
  missed every template-style routine name. If this count is 0, DO capture regressed.
- Exactly **2** `decl_kind='prototype'` rows: `Worker2.clw`'s MAP prototypes at lines 14–15,
  written in the KEYWORD form (`Name PROCEDURE, LONG`) that `MapProcDeclRegex` never matched
  before b7553893. (`Worker.clw`'s own MAP prototypes remain uncaptured — its column-0 style
  fails the regex's leading-whitespace requirement; pre-existing, documented above.)
- **Zero** `calls` rows target a `decl_kind='prototype'` symbol — implementations always win.
- **6** `ambiguous=1` edges, all genuine same-name overload collisions (`WorkerClass.Ask` ×3
  callers can't be type-matched across its 3 overloads; `OverloadBugClass.Dispatch` likewise) —
  the documented Bug-P callee-side limitation, now FLAGGED instead of silent. If this count
  grows, scope resolution is leaking; if it drops to 0 without type-aware overload resolution
  having been built, the flag is broken.

### Colon-labelled procedures (CC's round-2 battery find, b7553893)

The `[\w.]+` PROCEDURE/FUNCTION definition regexes (parser AND relationship scanner) matched
dots but not colons, so ANY colon-bearing label — 18,877 declarations in v61, the entire
template-generated RI layer — yielded ZERO procedure symbols, zero relationship rows from its
files, and orphaned every routine inside them. `Worker2Lib.clw` now ends with the two field
repro shapes: `RIDelete:Fixture FUNCTION` and `Preview:SelectFixture PROCEDURE(*LONG,*LONG)`,
prototyped in `Worker2.clw`'s MAP (which also pins the colon fix in `MapProcDeclRegex`).
Totals become **133 symbols**; prototype count becomes **4**.

- Exactly **4** colon-named `procedure`/`function` symbols: each repro shape twice — MAP
  `prototype` + body `implementation`. If this hits 0, the colon fix regressed.
- The program's bare call `r# = RIDelete:Fixture()` resolves to the IMPLEMENTATION row
  (1 `calls` edge) — colon names survive the whole pipeline, not just symbol capture.
- `source_preview` is non-null for **11/11** classes (was absent for the whole category).

**Definitional pin — "cross-project calls %"**: quoted over edges whose TARGET is
`decl_kind='implementation'`. Edges landing on prototypes (bodiless WinAPI/external
declarations resolve to a prototype INSIDE the calling project by design) are excluded from
both numerator and denominator. Agreed with CA-v61POSitive-CC 2026-08-11 so the number
doesn't get re-litigated per round. The DEFINITION is the pin; the VALUE is not: round 3
measured 17.05% (14,256/83,600; all-edges cut 18.0%), but that graph is missing every call
issued from inside a ROUTINE body (ticket 9a73aa5d — routine bodies are never scanned at
all), and cross-DLL calls disproportionately live in exactly those routines. Expect the
percentage to MOVE when routine scanning lands — that will be the repair, not a regression.

## Verify queries

```sql
SELECT s2.name, r.line_number FROM relationships r
JOIN symbols s1 ON r.to_id=s1.id JOIN symbols s2 ON r.from_id=s2.id
WHERE s1.name='WorkerClass.Sign' AND r.type='calls' ORDER BY r.line_number;

-- Bug N (fixed in #118): expect 5 rows (TestSignatureFlow line 44, OwnerClass.CallViaMember
-- line 83, OverloadBugClass.Dispatch line 382 -- Bug P's addition; UnreachableLocalRefTest lines
-- 417+418 -- Bug Q's addition, unrelated to Bug N's own collision). If the TestSignatureFlow/
-- OwnerClass.CallViaMember/OverloadBugClass.Dispatch rows drop out, Bug N has regressed -- the
-- built-in-named method is being erased at the dotted/SELF. call sites again.
SELECT s2.name, r.line_number FROM relationships r
JOIN symbols s1 ON r.to_id=s1.id JOIN symbols s2 ON r.from_id=s2.id
WHERE s1.name='WorkerClass.Ask' AND r.type='calls' ORDER BY r.line_number;

SELECT name, type, scope, parent_name, params FROM symbols WHERE type='class' OR scope='parameter'
OR name IN ('LocalDerived','workerRef','LocalGroup','InlineLocalGroup','GenCertData','SomeHandle','InlineGroup','InlineGroupPeriod','MultiLineGroup','HiddenGroupMember','AttrTermGroup','AttrTermGroupPeriod','BaseWorker');

-- Bug P: expect the LONG overload (line 360) as the sole caller of WorkerClass.Sign here, and
-- the CSTRING overload (line 377) as the sole caller of WorkerClass.Ask -- never the same
-- overload's line for both.
SELECT s_to.name AS callee, r.line_number, s_from.line_number AS from_overload_def_line
FROM relationships r
JOIN symbols s_to ON r.to_id = s_to.id
JOIN symbols s_from ON r.from_id = s_from.id
WHERE s_to.name IN ('WorkerClass.Sign','WorkerClass.Ask') AND s_from.name = 'OverloadBugClass.Dispatch';

-- Bug Q: MyValue and TestQ each have exactly ONE row -- their sole legitimate declaration in
-- WorkerClass.Sign -- and UnreachableLocalRefTest must not be among the results (it deliberately
-- declares neither as its own local).
SELECT name, type, scope, parent_name FROM symbols WHERE name IN ('MyValue','TestQ');
SELECT * FROM symbols WHERE name IN ('MyValue','TestQ') AND parent_name='UnreachableLocalRefTest'; -- expect 0 rows

-- Bug Q: simulates CodeGraphProvider.FindSymbolByName's exact query shape. Whichever row this
-- picks, its parent must resolve to type='procedure'/'routine' -- confirming
-- IsUnreachableLocalVariable would reject it.
SELECT id, name, type, scope, parent_name FROM symbols WHERE LOWER(name)=LOWER('MyValue') LIMIT 1;
SELECT id, name, type, scope, parent_name FROM symbols WHERE LOWER(name)=LOWER('TestQ') LIMIT 1;

-- Bug Q (related upstream finding, not this fix): TestQtype and its MyValue field produce NO
-- CodeGraph symbol at all -- confirms a wrong hover/F12 on either cannot have come from
-- CgHoverFromDb/CgDefinitionFromDb; it's Clarion-Extension's own resolver.
SELECT * FROM symbols WHERE name='TestQtype'; -- expect 0 rows
```

### Round 4 — routine bodies scanned, scanner wipeout fixed (ticket 9a73aa5d)

- `Tidy:Up2` emits a `references` edge to `R2` — the fixture's first routine-sourced edge of
  any kind. If routine-sourced edges drop to zero, the ROUTINE-label scanning regressed.
- The `\b` after `(PROCEDURE|FUNCTION)` in the scanner's procDefRegex is LOAD-BEARING: without
  it, an indented `DO ProcedureReturn` prefix-matches PROCEDURE (IgnoreCase, trimmed line),
  the scanner believes a procedure named "DO" was defined, and every edge for the rest of the
  body dies until the next literal CODE line. v61 evidence: DateRanger (PRMBase002.clw) edges
  stopped at :327 (the line before its first DO ProcedureReturn) and resumed at :1128; after
  the fix the file scans to :1594 and the canaries (WindowInitialized 4 refs, FilesOpened 2,
  Lcl:TempDate 19) all resolve. Same bug caused the attribution spillover (Defect 4).
- Class data members receive `references` edges via dotted member access (fixture: 12, was 0).
- v61 round-4 scale: 1,106,910 relationships (was 478,427); routine-sourced = 33,983 calls +
  66,706 do + 446,373 references; zero-incoming locals 64%->37%, class 100%->92%, routines
  without incoming DO 60%->9%. Cross-project calls (impl-target definition) = 23.9% — the
  post-repair floor the b7553893 pin anticipated. KNOWN residual: globals stay ~84%
  zero-incoming — the reference scan is per-file and globals are declared in the main file but
  used in member files (cross-file global references = follow-up).

### Round 5 — external rows re-pointed, routine-DATA declarations, dual-MAP member files (ticket 7e44c54c)

Three new fixture files in ReproProject (`ExternalRef.clw`, `RoutineData.clw`,
`DualMapLib.clw` + `DualMapProtos.inc`) and one appended owner declaration in
`proj2\Worker2.clw` (inserted AFTER the MAP's END — the line-14–15 prototype pins are
untouched). No pre-existing fixture line moved. Totals become **147 symbols / 7 files /
2 projects** (+14 symbols over the 133 pinned above). Supersessions of earlier absolute
counts, both from the new files: `type='do'` edges are now **2** (b7553893's "exactly 1"
plus `RoutineDataTest -> TS::MakeCalendar:8`), and `scope='global'` variables are now **8**
(d1a0aea6's "exactly 7" plus the `PTS::ProgPath` owner). Every other pinned count above
(22/5 callers, 21 program calls, 4 prototypes, 6 ambiguous, 11 classes, 0 calls-to-prototype)
re-verified unchanged 2026-08-11.

- **Externals re-pointed to owners** (`ExternalRef.clw` + `proj2\Worker2.clw`):
  `PTS::ProgPath` exists twice — the OWNER (`Worker2.clw`, `scope='global'`,
  `decl_kind` NULL) and the import (`ExternalRef.clw`, `,EXTERNAL` →
  `decl_kind='external'`). The owner has exactly **2** incoming `references`
  (`ExternalRefTest` — a CROSS-PROJECT re-point — and program `Worker2`'s own
  assignment); the external row has exactly **0**. Before round 5 the co-located
  external absorbed the reference and the owner starved (v61: externals held 2,863
  incoming refs vs owners' 1,031). Like Bug Q's file, `ExternalRef.clw` is
  parse-territory only: the fixture never links proj1, so the EXTERNAL is deliberately
  unresolved by any real export.
- **Routine-DATA declarations** (`RoutineData.clw`, the PRMBase002.clw:1119-1126 shape):
  `r:Count BYTE(1)`, `r:Multiplier DECIMAL(14,4)`, `r:Copy LIKE(loc:Total)` are
  `type='variable'`, `scope='local'`, `parent_name='TS::MakeCalendar:8'` — the ROUTINE,
  not the enclosing procedure (parent chain: procedure → routine → variable; the
  enclosing procedure is recoverable via the routine symbol's own `parent_name`). The
  routine's body emits `references` to all three (v61 scale: 9,261 such declarations
  across 890 generated files were invisible, and their references emitted nothing).
  Known accepted limitation, documented at the scanner's scope check: two same-named
  routines in the SAME file each declaring a same-named DATA local would cross-match.
- **Dual-MAP member file scanned** (`DualMapLib.clw`, the NYSCommon.CLW shape): a
  MEMBER() library file with TWO sibling top-level MAP blocks before the first
  implementation, the second holding an INCLUDE plus a nested `MODULE('Win32API')`
  with its own END. The pre-scan used to take the first MAP prototype as the file's
  parent procedure, fail the by-line symbol lookup, and skip the ENTIRE file (v61: 4
  NYS library files with zero body edges). Pinned: `DualMapProcA` has `calls` edges to
  `DualMapHelper` and `DualMapIncProc` (both implementation rows) and 4 `references`
  to `loc:Ticks`. Top-level member-MAP prototypes are deliberately NOT collected into
  `localMapNames` — calls must resolve to the same-file implementations.

```sql
-- Round 5 pin: owner vs external. Expect the scope='global' row (Worker2.clw) with 2
-- incoming references, and the decl_kind='external' row (ExternalRef.clw) with 0.
SELECT s.file_path, s.scope, s.decl_kind,
  (SELECT COUNT(*) FROM relationships r WHERE r.to_id=s.id AND r.type='references') AS incoming
FROM symbols s WHERE s.name='PTS::ProgPath';

-- Round 5 pin: routine-DATA symbols + their references. Expect 3 rows, all
-- parent_name='TS::MakeCalendar:8', and 6 references edges from the routine to them.
SELECT name, scope, parent_name, params FROM symbols WHERE name IN ('r:Count','r:Multiplier','r:Copy');
SELECT COUNT(*) FROM relationships r JOIN symbols v ON r.to_id=v.id
JOIN symbols f ON r.from_id=f.id
WHERE v.name IN ('r:Count','r:Multiplier','r:Copy') AND f.name='TS::MakeCalendar:8' AND r.type='references';

-- Round 5 pin: dual-MAP file has body edges at all (was 0 rows total). Expect 2 calls + 4 references.
SELECT r.type, COUNT(*) FROM relationships r WHERE r.file_path LIKE '%DualMapLib.clw' GROUP BY r.type;
```

**Pipeline run-1 hardening (same ticket, post-review):** the review pipeline's debugger
gate found five latent defects in the round-5 code before it shipped to testing; all
fixed in-place. (1) A ROUTINE attached to a PROGRAM's own global CODE section emitted
its DATA locals as `scope='global'` — pinned: `Worker2.clw`'s `Main:Tally` routine,
whose `r:MainCount` must be `scope='local'`, `parent_name='Main:Tally'`, with 1
`references` edge from the routine (and the program gains a third `do` edge —
`type='do'` count is now **3**). Totals become **149 symbols**. (2) A multi-owner
external re-point now writes `ambiguous=1` (deterministic lowest-id pick, flagged as
the guess it is — no fixture shape; single-owner pins stay `ambiguous=0`). (3) The
pre-scan runs the same unconditional-OMIT skip as the body loop, and a
procedure-shaped line that RESOLVES to a by-line symbol is authoritative regardless of
MAP depth (self-heals any depth desync — member-file MAP prototypes never carry
symbols, real definitions always do). (4) Only depth-1 prototypes of a
procedure-local MAP are collected into `localMapNames`; nested `MODULE('...')`
prototypes name procedures implemented elsewhere and no longer suppress their calls
edges. (5) The indexer's routine DATA-block peek now uses the exact
`DataStatementRegex` shape the parser uses (no line cap, no `DATA <token>` false
positive). Plus: files skipped for want of a parent procedure are now counted and
reported at end of run (`WARNING: N file(s) skipped by the body scan`).

- **Hardened build re-verified on v61** (2026-08-11): a fresh index with the post-hardening
  build is BYTE-IDENTICAL on every headline number to the pre-hardening round-5 run —
  422,971 symbols / 1,191,410 relationships, routine-parented locals 9,021,
  refs-to-externals 0, `ambiguous=1` references 0. All five hardened defect shapes are
  absent from v61 (CC battery, same date, confirmed independently: zero PROGRAM-global
  routines with DATA; 749 multi-owner global names exist but their intersection with
  `,EXTERNAL`-imported names is exactly 0, so the re-point provably never sees one — the
  964 references landing on them are direct same-file matches, not re-points). Battery
  assertion for the ambiguous flag is therefore DOUBLE-SIDED (per CC): (a) references to
  multi-owner names carry ambiguous=0 AND (b) multi-owner ∩ external-imported = 0. If a
  future codebase makes (b) non-zero while (a) stays 0, THAT is the defect — a
  single-sided (a) cannot tell "never armed" from "armed and silent". The hardening is
  pure safety margin on this corpus; the numbers below stand for the shipped build.

- v61 round-5 scale (measured 2026-08-11, round-4 db vs round-5 db, same source): 422,971
  symbols (was 414,240 — the +8,731 is EXACTLY the new routine-parented locals, 290 → 9,021;
  CC's 9,261 text-level estimate included shapes the parser correctly excludes);
  1,191,410 relationships (was 1,106,910). References landing on `decl_kind='external'`
  rows: 2,863 → **0** — the re-point is total. Owner-globals zero-incoming: 6,051/6,446
  (93.9%) → 5,545/6,446 (**86.0%**); `PTS::ProgPath`'s owner 0 → **140** incoming with all
  its external rows at 0. The four NYS library files: 26 relationship rows (uses_type
  only) → **19,808** (2,376 calls + 17,406 references). Remaining owner-global
  zero-incoming is dominated by the documented round-4 residual (cross-file global
  references — declared in the main file, used in member files — are still a follow-up),
  not by external absorption, which is now structurally impossible.
