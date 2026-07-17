# CodeGraph parser regression fixture

Contributed by [@geircodes](https://github.com/geircodes) alongside issues #79–#90, extended for
the `LIKE(...)`/`EQUATE`-alias CLASS-member fix (PR #92), the GROUP-typed CLASS-member fix
(PR #93), the inherited-CLASS-member dotted-call resolution fix (PR #112), and the
built-in-name-collision fix (PR #118) — a single compiling Clarion solution whose procedures each
exercise one historical parser/indexer bug. This is currently the only regression coverage the
CodeGraph parser has; run it after ANY change to `Parsing/ClarionParser.cs` or
`Graph/CodeGraphIndexer.cs` (either synced copy).

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
#118; line numbers below reflect the fixture AFTER Bug N's `Ask()` additions)

### Callers of `WorkerClass.Sign` — exactly 20 `calls` rows

| Caller | Line | Proves issue |
|---|---|---|
| TestSignatureFlow | 25 | baseline (direct call) |
| TestSignatureFlow | 31 | baseline (second call shape) |
| ParameterTest | 43 | #87 (call through PROCEDURE parameter) |
| ReturnTest | 52 | baseline (inline RETURN call shape) |
| MainHelperProc | 62 | #81 (procedure in main PROGRAM file) |
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
| LocalDerivedClassTest | 272 | #90 (attribution after local CLASS(Parent)) |
| LikeMemberBugClass.CallViaPlainInstanceMember | 289 | #92 (call through a reference CLASS member, unaffected control) |
| MultiLineGroupBugClass.CallViaAfterMultiLineGroupMember | 307 | #93 (member after multi-line GROUP with its own extra field) |
| DerivedWorkerClass.CallViaInheritedMember | 318 | #112 (member declared on a BASE class, accessed via SELF. from a DERIVED class's own method) |

### Callers of `WorkerClass.Ask` — exactly 2 `calls` rows (Bug N, **fixed in #118**)

`Ask` is identical in shape to `Sign` (same class, same signature) — the only difference is its
name, which happens to collide with the Clarion built-in `ASK()` window/UI statement. Both call
sites below sit directly next to an equivalent, resolving `Sign` call on the SAME object, at the
SAME call site, so the two can be compared line-for-line:

| Caller | Line | Same-site `Sign` call (for comparison) | Path proven fixed |
|---|---|---|---|
| TestSignatureFlow | 30 | line 25 (`worker.Sign( 1 )`) | DATA-section local variable (baseline path) |
| OwnerClass.CallViaMember | 69 | line 63 (`SELF.MyWorker.Sign( 10 )`) | cross-file CLASS member (#84/#86, and #112's inheritance walk when applicable) |

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

### Program symbol (#81)

- `Worker` (`type='program'`) has `calls` rows to every procedure invoked from the global
  CODE section (11 rows), and **zero** incoming `calls` — the local variable named `worker`
  must never resolve to the program symbol despite the case-insensitive name collision.

## Verify queries

```sql
SELECT s2.name, r.line_number FROM relationships r
JOIN symbols s1 ON r.to_id=s1.id JOIN symbols s2 ON r.from_id=s2.id
WHERE s1.name='WorkerClass.Sign' AND r.type='calls' ORDER BY r.line_number;

-- Bug N (fixed in #118): expect 2 rows (TestSignatureFlow line 30, OwnerClass.CallViaMember
-- line 69). If this drops back to 0, Bug N has regressed — the built-in-named method is being
-- erased at the dotted/SELF. call sites again.
SELECT s2.name, r.line_number FROM relationships r
JOIN symbols s1 ON r.to_id=s1.id JOIN symbols s2 ON r.from_id=s2.id
WHERE s1.name='WorkerClass.Ask' AND r.type='calls' ORDER BY r.line_number;

SELECT name, type, scope, parent_name, params FROM symbols WHERE type='class' OR scope='parameter'
OR name IN ('LocalDerived','workerRef','LocalGroup','InlineLocalGroup','GenCertData','SomeHandle','InlineGroup','InlineGroupPeriod','MultiLineGroup','HiddenGroupMember','AttrTermGroup','AttrTermGroupPeriod','BaseWorker');
```
