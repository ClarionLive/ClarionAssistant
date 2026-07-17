# CodeGraph parser regression fixture

Contributed by [@geircodes](https://github.com/geircodes) alongside issues #79–#90, extended for
the `LIKE(...)`/`EQUATE`-alias CLASS-member fix (PR #92), the GROUP-typed CLASS-member fix
(PR #93), and the inherited-CLASS-member dotted-call resolution fix (PR TBD) — a single
compiling Clarion solution whose procedures each exercise one historical parser/indexer bug.
This is currently the only regression coverage the CodeGraph parser has; run it after ANY change
to `Parsing/ClarionParser.cs` or `Graph/CodeGraphIndexer.cs` (either synced copy).

## Run

```powershell
indexer\bin\Debug\clarion-indexer.exe index test-fixtures\codegraph-repro\ReproSolution.sln --db %TEMP%\codegraph-repro.db
```

## Expected results (verified 2026-07-17 with all #79–#90 fixes applied, plus #92, #93, and the
inherited-CLASS-member dotted-call resolution fix)

### Callers of `WorkerClass.Sign` — exactly 20 `calls` rows

| Caller | Line | Proves issue |
|---|---|---|
| TestSignatureFlow | 18 | baseline (direct call) |
| TestSignatureFlow | 19 | baseline (second call shape) |
| ParameterTest | 31 | #87 (call through PROCEDURE parameter) |
| ReturnTest | 40 | baseline (inline RETURN call shape) |
| OwnerClass.CallViaMember | 51 | #84+#86 (.inc member, cross-file type) |
| MainHelperProc | 62 | #81 (procedure in main PROGRAM file) |
| OwnerClass.CallViaCommentedMember | 67 | #85+#86 (trailing-comment member) |
| CommentedLocalTest | 83 | #85 (trailing-comment DATA local) |
| GroupBugClass.CallViaAfterGroupMember | 101 | #88 (member after inline GROUP END) |
| PeriodBugClass.CallViaAfterPeriodMember | 117 | #88 (member after inline GROUP period) |
| OmitTest | 154 | #79 (call after OMIT block) |
| AfterOmitProc | 163 | #79 (procedure after OMIT block) |
| CommentEmbeddedTest | 180 | #80 (call with embedded comment) |
| ConditionalOmitTest | 194 | #79 (conditional OMIT/COMPILE) |
| GroupQueueLocalTest | 213 | #89 (local after GROUP(Type) two-line) |
| InlineLocalGroupTest | 228 | #89 (local after GROUP(Type) END inline) |
| LocalDerivedClassTest | 254 | #90 (attribution after local CLASS(Parent)) |
| LikeMemberBugClass.CallViaPlainInstanceMember | 271 | #92 (call through a reference CLASS member, unaffected control) |
| MultiLineGroupBugClass.CallViaAfterMultiLineGroupMember | 289 | #93 (member after multi-line GROUP with its own extra field) |
| DerivedWorkerClass.CallViaInheritedMember | 300 | this PR (member declared on a BASE class, accessed via SELF. from a DERIVED class's own method) |

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

### Program symbol (#81)

- `Worker` (`type='program'`) has `calls` rows to every procedure invoked from the global
  CODE section (11 rows), and **zero** incoming `calls` — the local variable named `worker`
  must never resolve to the program symbol despite the case-insensitive name collision.

## Verify queries

```sql
SELECT s2.name, r.line_number FROM relationships r
JOIN symbols s1 ON r.to_id=s1.id JOIN symbols s2 ON r.from_id=s2.id
WHERE s1.name='WorkerClass.Sign' AND r.type='calls' ORDER BY r.line_number;

SELECT name, type, scope, parent_name, params FROM symbols WHERE type='class' OR scope='parameter'
OR name IN ('LocalDerived','workerRef','LocalGroup','InlineLocalGroup','GenCertData','SomeHandle','InlineGroup','InlineGroupPeriod','MultiLineGroup','HiddenGroupMember','AttrTermGroup','AttrTermGroupPeriod','BaseWorker');
```
