# CodeGraph parser regression fixture

Contributed by [@geircodes](https://github.com/geircodes) alongside issues #79–#90, extended for
the `LIKE(...)`/`EQUATE`-alias CLASS-member fix (PR #92), the GROUP-typed CLASS-member fix
(PR #93), the inherited-CLASS-member dotted-call resolution fix (PR #112), the
built-in-name-collision fix (PR #118), issue #97's attrs+same-line-terminator fix, the
overload-attribution fix (Bug P), and Bug Q (pins the DB shape behind
`SharedLspBridge.cs`'s F12-definition local-variable-guard fix — see its own section below; unlike
A–P this isn't a parser/indexer bug, it documents a precondition for a runtime bug in the addin's
C#) — a single compiling Clarion solution whose procedures each exercise one historical
parser/indexer bug (plus, for Bug Q, a DB-shape precondition for a runtime bug). This is currently
the only regression coverage the CodeGraph parser has; run it after ANY change to
`Parsing/ClarionParser.cs` or `Graph/CodeGraphIndexer.cs` (either synced copy).

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
#118, then re-verified with #97 and with Bug P (this PR); line numbers below reflect the fixture
AFTER Bug P's `OverloadBugClass.Dispatch` addition)

### Callers of `WorkerClass.Sign` — exactly 22 `calls` rows

| Caller | Line | Proves issue |
|---|---|---|
| TestSignatureFlow | 25 | baseline (direct call) |
| TestSignatureFlow | 31 | baseline (second call shape) |
| ParameterTest | 43 | #87 (call through PROCEDURE parameter) |
| ReturnTest | 52 | baseline (inline RETURN call shape) |
| MainHelperProc | 64 | #81 (procedure in main PROGRAM file) |
| OwnerClass.CallViaMember | 63 | #84+#86 (.inc member, cross-file type) |
| OwnerClass.CallViaCommentedMember | 85 | #85+#86 (trailing-comment member) |
| CommentedLocalTest | 101 | #85 (trailing-comment DATA local) |
| GroupBugClass.CallViaAfterGroupMember | 119 | #88 (member after inline GROUP END) |
| PeriodBugClass.CallViaAfterPeriodMember | 135 | #88 (member after inline GROUP period) |
| OmitTest | 172 | #79 (call after OMIT block) |
| AfterOmitProc | 181 | #79 (procedure after OMIT block) |
| CommentEmbeddedTest | 198 | #80 (call with embedded comment) |
| ConditionalOmitTest | 212 | #79 (conditional OMIT/COMPILE) |
| GroupQueueLocalTest | 231 | #89 (local after GROUP(Type) two-line) |
| InlineLocalGroupTest | 246 | #89 (local after GROUP(Type) END inline) |
| AttrTermLocalGroupTest | 266 | #97 (local after GROUP(Type),attrs + same-line terminator) |
| LocalDerivedClassTest | 292 | #90 (attribution after local CLASS(Parent)) |
| LikeMemberBugClass.CallViaPlainInstanceMember | 309 | #92 (call through a reference CLASS member, unaffected control) |
| MultiLineGroupBugClass.CallViaAfterMultiLineGroupMember | 327 | #93 (member after multi-line GROUP with its own extra field) |
| DerivedWorkerClass.CallViaInheritedMember | 338 | #112 (member declared on a BASE class, accessed via SELF. from a DERIVED class's own method) |
| OverloadBugClass.Dispatch (LONG overload) | 349 | Bug P (own overload's call, correctly self-attributed — not misattributed to the CSTRING overload) |

### Callers of `WorkerClass.Ask` — exactly 3 `calls` rows (Bug N fixed in #118, +1 from Bug P)

`Ask` is identical in shape to `Sign` (same class, same signature) — the only difference is its
name, which happens to collide with the Clarion built-in `ASK()` window/UI statement. Both call
sites below sit directly next to an equivalent, resolving `Sign` call on the SAME object, at the
SAME call site, so the two can be compared line-for-line:

| Caller | Line | Same-site `Sign` call (for comparison) | Path proven fixed |
|---|---|---|---|
| TestSignatureFlow | 30 | line 25 (`worker.Sign( 1 )`) | DATA-section local variable (baseline path) |
| OwnerClass.CallViaMember | 69 | line 63 (`SELF.MyWorker.Sign( 10 )`) | cross-file CLASS member (#84/#86, and #112's inheritance walk when applicable) |
| OverloadBugClass.Dispatch (CSTRING overload) | 368 | line 349 (`worker.Sign( pValue )`, LONG overload) | Bug P — sibling overload's call, correctly self-attributed to the CSTRING overload's own line (363), not the LONG overload's (346) |

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
  at line 349, correctly attributed to the LONG overload's own definition line (346). `WorkerClass.Ask`
  gains exactly one caller row from `OverloadBugClass.Dispatch` at line 368, correctly attributed to
  the CSTRING overload's own definition line (363) — a different id than the Sign caller above, even
  though both share the literal name `OverloadBugClass.Dispatch`.

**Deliberately NOT fixed by this change, and not a regression if still true**: the
`SELF.Dispatch(99)` call at line 367 (inside the CSTRING overload, intending to call the LONG
overload) still resolves its call *target* to the CSTRING overload itself (i.e. `to_id`'s
definition line is 363, not 346) — call-target resolution for an overloaded name still isn't
type-aware; it still picks whichever overload loaded last, same as before this fix. This fix only
corrects the *caller* side (`currentProcId`/`currentProcName` — "which procedure is this line
inside"), not the *callee* side (which overload a given call site actually invokes). Per-overload
caller *counts* (grouping by `to_id` for an overloaded name) remain unreliable; "what does this
specific overload call" (grouping by `from_id`) is now correct.

### Bug Q: F12/go-to-definition's CodeGraph fallback resolved to an unrelated procedure's local

Not a parser/indexer bug like A–P above — this documents a DB-*shape* precondition for a bug in
`SharedLspBridge.cs` (the ClarionAssistant addin's runtime C#, a separate assembly from this
indexer). `CgDefinitionFromDb` (F12's CodeGraph fallback, reached only once the upstream LSP's own
scope search has already returned nothing) looked symbols up via a flat, unordered
`WHERE LOWER(name)=LOWER(@name) LIMIT 1` with no scope filter. Its sibling `CgHoverFromDb` already
guards this exact shape with `IsUnreachableLocalVariable` — a "variable" symbol whose
`parent_name` resolves to a `procedure`/`routine` symbol can never legitimately be referenced from
outside that one procedure, so a match like that is always wrong. `CgDefinitionFromDb` never
called it.

This repro doesn't need a *new* bug pattern — `result` is already declared as a separate local in
20 different procedures throughout this fixture (`TestSignatureFlow`, `OmitTest`,
`OverloadBugClass.Dispatch`, etc.), which is exactly the "many wrong candidates, arbitrary pick"
pool the unguarded lookup drew from. `UnreachableLocalRefTest` (`WorkerLib.clw`, near the end)
deliberately declares no local of its own named `result`, so a definition lookup on that bare word
from inside it has nothing legitimate to resolve to. `ProbeGlobalCounter` (`WorkerLib.clw` line 5,
module scope, no owning procedure) is the negative control — a genuine global must keep resolving
after the fix, since `IsUnreachableLocalVariable` short-circuits to `false` on an empty
`parent_name`.

**Live-IDE check** (not exercisable via the indexer alone — this only pins the DB shape the
runtime bug depends on): open this solution in the IDE with a build of ClarionAssistant carrying
the fix, place the caret on the commented-out `result` inside `UnreachableLocalRefTest`, press
F12.
- **Before the fix**: jumps to one of the 20 unrelated procedures' own `result` declaration —
  which one is arbitrary and can change across re-indexes (SQLite's `LIMIT 1` with no `ORDER BY`).
- **After the fix**: no definition found.
- **Regression check**: F12 on `ProbeGlobalCounter` from the same spot must still resolve.

The probe word lives in a comment rather than a live statement so this file keeps compiling —
referencing a genuinely undeclared identifier in real code is a hard Clarion compile error, and
`CgDefinitionFromDb` has no string/comment awareness on the definition path today (a related,
still-open gap — see PR #166's write-up), so a commented-out word exercises the identical lookup
as one in live code.

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
- `OverloadBugClass.Dispatch` (Bug P, this PR): two `procedure` symbols sharing the identical
  name, at lines 346 (`( LONG pValue )`) and 363 (`( *CSTRING pValue )`) — both were ALWAYS
  correctly stored as two distinct symbols (proving, like Bug N, that the bug was entirely in
  relationship resolution, not symbol capture). Compare against the "Bug P" writeup and the
  `WorkerClass.Sign`/`WorkerClass.Ask` caller tables above.
- `ProbeGlobalCounter` (Bug Q): `type='variable'`, `scope='module'`, `parent_name=NULL` — a
  genuine module-scope global declared at the top of `WorkerLib.clw`, outside any procedure.
  `UnreachableLocalRefTest` (Bug Q): a `procedure` symbol like any other; the point is what it
  does NOT have — no `result`-named local of its own (compare against the 20 procedures above
  that each declare one).

### Program symbol (#81)

- `Worker` (`type='program'`) has `calls` rows to every procedure invoked from the global
  CODE section (11 rows), and **zero** incoming `calls` — the local variable named `worker`
  must never resolve to the program symbol despite the case-insensitive name collision.

## Verify queries

```sql
SELECT s2.name, r.line_number FROM relationships r
JOIN symbols s1 ON r.to_id=s1.id JOIN symbols s2 ON r.from_id=s2.id
WHERE s1.name='WorkerClass.Sign' AND r.type='calls' ORDER BY r.line_number;

-- Bug N (fixed in #118): expect 3 rows (TestSignatureFlow line 30, OwnerClass.CallViaMember
-- line 69, OverloadBugClass.Dispatch line 368 -- the last one is Bug P's addition, not Bug N's).
-- If this drops back to 0, Bug N has regressed — the built-in-named method is being erased at
-- the dotted/SELF. call sites again.
SELECT s2.name, r.line_number FROM relationships r
JOIN symbols s1 ON r.to_id=s1.id JOIN symbols s2 ON r.from_id=s2.id
WHERE s1.name='WorkerClass.Ask' AND r.type='calls' ORDER BY r.line_number;

SELECT name, type, scope, parent_name, params FROM symbols WHERE type='class' OR scope='parameter'
OR name IN ('LocalDerived','workerRef','LocalGroup','InlineLocalGroup','GenCertData','SomeHandle','InlineGroup','InlineGroupPeriod','MultiLineGroup','HiddenGroupMember','AttrTermGroup','AttrTermGroupPeriod','BaseWorker');

-- Bug P: expect the LONG overload (line 346) as the sole caller of WorkerClass.Sign here, and
-- the CSTRING overload (line 363) as the sole caller of WorkerClass.Ask -- never the same
-- overload's line for both.
SELECT s_to.name AS callee, r.line_number, s_from.line_number AS from_overload_def_line
FROM relationships r
JOIN symbols s_to ON r.to_id = s_to.id
JOIN symbols s_from ON r.from_id = s_from.id
WHERE s_to.name IN ('WorkerClass.Sign','WorkerClass.Ask') AND s_from.name = 'OverloadBugClass.Dispatch';

-- Bug Q: 20 unrelated "result" locals, one per owning procedure -- the wrong-candidate pool
-- FindSymbolByName's unordered LIMIT-1 draws from. UnreachableLocalRefTest must NOT be among them.
SELECT COUNT(*) AS result_local_count FROM symbols WHERE name='result' AND type='variable';
SELECT * FROM symbols WHERE name='result' AND parent_name='UnreachableLocalRefTest'; -- expect 0 rows

-- Bug Q: simulates CodeGraphProvider.FindSymbolByName's exact query shape. Whichever row this
-- picks, its parent must resolve to type='procedure'/'routine' -- confirming
-- IsUnreachableLocalVariable would reject it. ProbeGlobalCounter (parent_name IS NULL) must
-- never be filtered this way -- that's the over-rejection/negative-control check.
SELECT id, name, type, scope, parent_name FROM symbols WHERE LOWER(name)=LOWER('result') LIMIT 1;
SELECT name, type, scope, parent_name FROM symbols WHERE name='ProbeGlobalCounter';
```
