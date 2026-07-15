# CodeGraph parser regression fixture

Contributed by [@geircodes](https://github.com/geircodes) alongside issues #79–#90, extended for
a further `LIKE(...)`/`EQUATE`-alias CLASS-member fix (PR #92) — a single compiling Clarion
solution whose procedures each exercise one historical parser/indexer bug. This is currently the
only regression coverage the CodeGraph parser has; run it after ANY change to
`Parsing/ClarionParser.cs` or `Graph/CodeGraphIndexer.cs` (either synced copy).

## Run

```powershell
indexer\bin\Debug\clarion-indexer.exe index test-fixtures\codegraph-repro\ReproSolution.sln --db %TEMP%\codegraph-repro.db
```

## Expected results (verified 2026-07-15 with all #79–#90 fixes applied, plus #92)

### Callers of `WorkerClass.Sign` — exactly 18 `calls` rows

| Caller | Line | Proves issue |
|---|---|---|
| TestSignatureFlow | 18 | baseline |
| TestSignatureFlow | 19 | #79 (repeat call same proc→target kept) |
| ParameterTest | 31 | #87 (call through PROCEDURE parameter) |
| ReturnTest | 40 | baseline (inline RETURN call shape) |
| OwnerClass.CallViaMember | 51 | #84+#86 (.inc member, cross-file type) |
| MainHelperProc | 58 | #81 (procedure in main PROGRAM file) |
| OwnerClass.CallViaCommentedMember | 67 | #85+#86 (trailing-comment member) |
| CommentedLocalTest | 83 | #85 (trailing-comment DATA local) |
| GroupBugClass.CallViaAfterGroupMember | 101 | #88 (member after inline GROUP END) |
| PeriodBugClass.CallViaAfterPeriodMember | 117 | #88 (member after inline GROUP `.`) |
| OmitTest | 154 | #80 (line after `!label` OMIT terminator) |
| AfterOmitProc | 163 | #80 (procedure after OMIT block) |
| CommentEmbeddedTest | 180 | #80 (terminator embedded in comment) |
| ConditionalOmitTest | 194 | #80 (2-arg conditional OMIT included) |
| GroupQueueLocalTest | 213 | #89 (local after GROUP(Type) two-line) |
| InlineLocalGroupTest | 228 | #89 (local after GROUP(Type) END inline) |
| LocalDerivedClassTest | 254 | #90 (attribution after local CLASS(Parent)) |
| LikeMemberBugClass.CallViaPlainInstanceMember | 271 | #92 (call through a reference CLASS member, unaffected control) |

### Symbols

- 7 `class` symbols: WorkerClass, OwnerClass, DerivableClass, GroupBugClass, PeriodBugClass,
  AfterBugClass (#84: sourced from the `.inc` despite `<None Include>`; #88: the last two
  vanished entirely before the depth-leak fix), LikeMemberBugClass (#92).
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
OR name IN ('LocalDerived','workerRef','LocalGroup','InlineLocalGroup','GenCertData','SomeHandle');
```
