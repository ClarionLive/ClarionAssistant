  MEMBER('Worker')

  INCLUDE('WorkerLib.inc'),ONCE

WorkerClass.Sign PROCEDURE( LONG pData )
  CODE
  RETURN 0



! Bug C repro: worker is a local DATA-section variable, and Sign() is called on it
! twice from this same procedure. Before the fix, only the line-9 call survived 
! line 10 was silently dropped as a "duplicate" (same fromId/toId, no line in the key).
TestSignatureFlow PROCEDURE()
worker    WorkerClass
result    LONG
  CODE
  result = worker.Sign( 1 )
  result = worker.Sign( 2 )
  Result = ParameterTest( Worker )
  RETURN result

! NOT a Bug C case ? kept here deliberately as a negative control. pWorker is a
! PROCEDURE(...) parameter, not a DATA-section local, so its declared type is never
! captured for the dotted-call lookup. This call is expected to still be invisible to
! CodeGraph after the Bug C fix ? that's the separate, already-documented parameter-
! resolution gap (see "Scope note" in the issue), not something this fix addresses.
ParameterTest PROCEDURE( *WorkerClass pWorker )
Result   LONG
  CODE
  Result = pWorker.Sign( 3 )
  RETURN result

! Single call site, but expressed inline in a RETURN rather than an assignment/IF 
! confirms detection doesn't depend on call-site syntax shape.
ReturnTest PROCEDURE( )
Result   LONG
MyWrk    WorkerClass
  CODE
  RETURN MyWrk.Sign( 4 )

! Bug D repro: MyWorker is a CLASS data member of OwnerClass, declared in
! WorkerLib.inc, not a DATA-section local or a PROCEDURE parameter. SELF.MyWorker.Sign(...)
! is called from inside OwnerClass's own method. This is the class-member resolution gap
! documented in codegraph-call-resolution-issue.md (2026-07-03) -- a different, still-open
! gap from Bug A/B/C above and from the ParameterTest gap. Expected: invisible to
! CodeGraph today (no variable-scoping path resolves SELF.<member>.<Method> today).
OwnerClass.CallViaMember PROCEDURE( )
result   LONG
  CODE
  result = SELF.MyWorker.Sign( 10 )
  RETURN result

! Bug F repro: CommentedWorker is declared with a trailing inline comment in
! WorkerLib.inc ("!Bug F repro: trailing comment on a CLASS data member").
! RefVariableDeclRegex anchors at end-of-line, so the trailing comment broke the match
! and no variable symbol was ever created for CommentedWorker -- meaning even with Bug D
! and Bug E fixed, this call could never resolve, since the parser did not know
! CommentedWorker existed at all, let alone its type. Expected before the fix: no
! CommentedWorker symbol anywhere; SELF.CommentedWorker.Sign(...) invisible. Expected
! after the fix (still blocked by Bug E, since this crosses into the .inc file):
! CommentedWorker exists as a correctly-typed symbol, but the calls relationship still
! does not resolve -- same as MyWorker/CallViaMember above.
OwnerClass.CallViaCommentedMember PROCEDURE( )
result   LONG
  CODE
  result = SELF.CommentedWorker.Sign( 11 )
  RETURN result

! Bug F repro (DATA-section local): workerRef is a local reference variable declared
! with a trailing inline comment, in the SAME file as its use -- unlike CommentedWorker
! above, this is not blocked by Bug E (the cross-file class-member gap), since Bug 1's
! existing same-file variable-type resolution already handles local DATA-section
! variables. So this call is expected to be invisible ONLY because of Bug F -- once
! Bug F alone is fixed, it should fully resolve to WorkerClass.Sign, with no remaining
! blocker. A useful contrast against CallViaCommentedMember above (blocked by Bug F AND
! Bug E) and against MyWorker/CallViaMember (blocked by Bug E alone, comment-free).
CommentedLocalTest PROCEDURE()
workerRef  &WorkerClass    !Bug F repro: trailing comment on a DATA-section local reference
result     LONG
  CODE
  workerRef &= NEW WorkerClass
  result = workerRef.Sign( 12 )
  DISPOSE(workerRef)
  RETURN result

! Bug H repro: InlineGroup is a self-closing, single-line GROUP instantiated from a named
! GROUP TYPE (SmallGroupType) declared entirely on one line: "InlineGroup GROUP(SmallGroupType) END".
! ParseIncFile's "Nested END for inner GROUP/QUEUE etc." check increments classEndDepth for
! this line (it matches "^\w+\s+(GROUP|QUEUE|RECORD)\b"), but the line's own trailing END is
! never separately recognized (EndRegex requires the line to START with END), so classEndDepth
! is never decremented back down. Every data member declared after this point in the class --
! AfterGroupMember here -- is silently dropped from member capture for the rest of the file,
! since the member-capture branch requires classEndDepth==1 exactly. HiddenMember (PRIVATE,
! declared BEFORE InlineGroup) is a separate, deliberate, by-design exclusion -- kept here to
! prove it is independent of this bug, not the cause of it. Expected before the fix:
! AfterGroupMember does not exist as a symbol; SELF.AfterGroupMember.Sign(...) is invisible.
GroupBugClass.CallViaAfterGroupMember PROCEDURE( )
result   LONG
  CODE
  result = SELF.AfterGroupMember.Sign( 20 )
  RETURN result

! Bug H repro (period variant): same defect as InlineGroup above, but the inline group is
! closed with a bare period instead of the word END. Per the language reference, END
! "terminates a data declaration structure or a compound executable statement. It is
! functionally equivalent to a period" -- this is not limited to executable statements like IF,
! it applies to structural declarations too. Neither of the two checks that would normally
! recognize a closing terminator matches this line: the one for a bare END requires the line
! to START with END, and the one for a bare period requires the ENTIRE line to be just a
! period -- "InlineGroupPeriod GROUP(SmallGroupType) ." matches neither, since both have
! other content before the terminator. Expected before the fix: identical failure mode to the
! GROUP variant above -- AfterPeriodMember does not exist as a symbol.
PeriodBugClass.CallViaAfterPeriodMember PROCEDURE( )
result   LONG
  CODE
  result = SELF.AfterPeriodMember.Sign( 22 )
  RETURN result

! Bug H repro (severity check): AfterBugClass is declared last in WorkerLib.inc, after
! GroupBugClass/PeriodBugClass. Because GroupBugClass's classEndDepth never
! reaches 0 (its own real closing END only decrements the leaked depth from 2 to 1, not to 0),
! inClassBody never resets to false -- and the check that starts a NEW class (ClassDefRegex)
! sits entirely outside the "if (inClassBody)" block, so it becomes unreachable for the rest of
! the file. Confirmed empirically: not only AfterBugClass but PeriodBugClass
! ALSO fails to exist as a "class" symbol at all (verified via direct query against the indexed
! database) -- the corruption from GroupBugClass's single inline-group line silently deletes
! every class declared after it in the file, compounding rather than resetting. Far more severe
! than the missing-member case above: entire classes vanish, not just some properties, and the
! effect is cumulative across the rest of the file, not limited to the next class.
AfterBugClass.CallSomethingElse PROCEDURE( )
result   LONG
  CODE
  result = 1
  RETURN result


! Bug B repro: the OMIT('...') block below uses the standard Clarion convention of a
! comment-prefixed terminator line ("!omitDebugCode"). SkipConditionalBlock's terminator
! search does not account for the leading "!" when matching the terminator line, so it
! never finds it and skips all the way to end-of-file. Everything parsed after this
! point in the FILE (not just this procedure) is silently dropped ? including the
! worker.Sign() call right after the OMIT block, and the entire AfterOmitProc
! procedure below, even though both compile and run normally. Placed after
! WorkerClass.Sign's implementation deliberately, so the Bug C/parameter-gap results
! above are captured before this truncation kicks in.
OmitTest PROCEDURE()
worker    WorkerClass
result    LONG
  CODE
  OMIT('omitDebugCode')
     result = 999
  !omitDebugCode
  result = worker.Sign( 6 )
  RETURN result

! Only reachable if Bug B is fixed ? proves the truncation is file-wide, not just
! scoped to the procedure containing the OMIT block.
AfterOmitProc PROCEDURE()
worker    WorkerClass
result    LONG
  CODE
  result = worker.Sign( 7 )
  RETURN result 

! Bug B repro (comment-embedded terminator): per the official Clarion language reference,
! an OMIT block "ends with the line that contains the same string constant as the
! terminator" ? a substring match anywhere in the line, not a prefix match. Real-world
! Clarion code (including the documentation's own nested-OMIT/COMPILE example) sometimes
! spells the terminator out mid-comment, e.g. "!end- OMIT('embeddedTerm') closing", rather
! than a bare "!embeddedTerm" prefix. An earlier fix attempt (strip a leading "!", then
! StartsWith) still fails on this legal form; only a substring Contains match is correct.
CommentEmbeddedTest PROCEDURE()
worker    WorkerClass
result    LONG
  CODE
  OMIT('embeddedTerm')
     result = 999
  !end- OMIT('embeddedTerm') closing comment, not a bare "!embeddedTerm" prefix
  result = worker.Sign( 8 )
  RETURN result

! Conditional OMIT repro: OMIT('condTerm', SomeEquate) depends on a project-specific
! EQUATE/Conditional Switch value that CodeGraph has no way to evaluate. Design decision:
! treat conditional OMIT the same as COMPILE ? always included ? rather than assuming it's
! always omitted (which would wrongly hide a call that might be very real in some build
! configurations). Only a bare, unconditional OMIT('term') (no second argument, see
! OmitTest above) is unambiguously dead code in every build and stays skipped.
ConditionalOmitTest PROCEDURE()
worker    WorkerClass
result    LONG
  CODE
  OMIT('condTerm', SomeEquate)
  result = worker.Sign( 9 )
  !condTerm
  RETURN result

! Bug I repro: a procedure-local DATA-section variable declared "Name GROUP(NamedType)" (a
! named GROUP,TYPE instantiated inline, its fields inherited entirely from SmallGroupType) never
! matched any of ParseMemberFile's declaration regexes. GroupQueueDeclRegex required GROUP/QUEUE
! to be followed immediately by end-of-line or a comma-attribute list; the parenthesized
! inherited-type form matched none of the other five regexes either. Unlike Bug H (ParseIncFile's
! classEndDepth), this was NOT a cascading leak: since the line simply matched nothing at all,
! dataGroupDepth was never touched, so AfterLocalGroup below was already captured correctly even
! before this fix -- only LocalGroup itself silently never existed as a symbol. Expected before
! the fix: LocalGroup does not exist as a symbol at all; AfterLocalGroup does.
GroupQueueLocalTest PROCEDURE( )
result          LONG
LocalGroup      GROUP(SmallGroupType)
                END
AfterLocalGroup &WorkerClass
  CODE
  result = AfterLocalGroup.Sign( 30 )
  RETURN result

! Bug I repro (self-closing local variant): "InlineLocalGroup GROUP(SmallGroupType) END" opens
! and closes on the same line. Same missing-match root cause as above, but this variant also
! specifically proves the fix doesn't introduce a NEW Bug-H-style depth leak of its own: widening
! GroupQueueDeclRegex to match this line means it now increments dataGroupDepth unless the
! self-closing form is detected and excluded -- AfterInlineLocalGroup below must still be
! captured correctly, proving the self-closing check works for local variables too, not just for
! CLASS bodies in ParseIncFile.
InlineLocalGroupTest PROCEDURE( )
result                LONG
InlineLocalGroup      GROUP(SmallGroupType) END
AfterInlineLocalGroup &WorkerClass
  CODE
  result = AfterInlineLocalGroup.Sign( 31 )
  RETURN result

DerivableClass.Compute PROCEDURE( LONG pValue )
  CODE
  RETURN pValue

! Bug J repro: a CLASS declared inside a procedure's own DATA section, deriving from and
! overriding a method of a real, repro-owned class (not a third-party one, e.g. CapeSoft's
! jsonClass, which surfaced the real production case -- this proves the bug isn't specific to
! third-party classes). "LocalDerived CLASS(DerivableClass)" with Compute overridden inline
! (,DERIVED) and implemented later in the same procedure via the standard
! "LocalDerived.Compute PROCEDURE(...)" syntax. Before this fix, ClassDefRegex fired
! unconditionally regardless of whether the parser was inside a procedure's own DATA section, so
! LocalDerived was indexed as a phantom global class (parent_name=DerivableClass, scope=global)
! instead of a local variable of LocalDerivedClassTest -- completely absent from the procedure's
! own local-variable list. AfterLocalDerived, declared immediately after it, was already
! captured correctly even before this fix -- proving (like Bug I) there's no cascading
! depth-leak here, just a misclassification.
LocalDerivedClassTest PROCEDURE( )
result            LONG
LocalDerived      CLASS(DerivableClass)
Compute              PROCEDURE( LONG pValue ), LONG, DERIVED
                  END
AfterLocalDerived &WorkerClass
  CODE
  result = AfterLocalDerived.Sign( 40 )
  RETURN result

LocalDerived.Compute PROCEDURE( LONG pValue )
  CODE
  RETURN pValue * 2

! Bug K repro: GenCertData (LIKE(SmallGroupType)) and SomeHandle (typed via the EQUATE-aliased
! SmallHandleType synonym) never matched RefVariableDeclRegex/VariableDeclRegex, the only two
! regexes ParseIncFile's CLASS-member cascade tried -- silently absent from the index. Expected
! before the fix: neither GenCertData nor SomeHandle exists as a symbol. PlainInstanceMember
! (&WorkerClass) is unrelated to this fix -- a reference member, already handled correctly
! before Bug K -- kept only because a call through it was already wired up here.
LikeMemberBugClass.CallViaPlainInstanceMember PROCEDURE( )
result   LONG
  CODE
  SELF.PlainInstanceMember &= NEW(WorkerClass)
  result = SELF.PlainInstanceMember.Sign( 50 )
  DISPOSE( SELF.PlainInstanceMember)
  RETURN result

! Bug L repro: MultiLineGroup (a genuine multi-line GROUP(NamedType) CLASS member, with its own
! extra field before its own separate closing END) never got a symbol for its own name -- only
! GroupBugClass.InlineGroup/PeriodBugClass.InlineGroupPeriod above (the self-closing single-line
! forms) were previously used to prove Bug H's depth-tracking fix, but none of the three ever
! produced a symbol for the member's OWN name until this fix. HiddenGroupMember (PRIVATE,
! declared before MultiLineGroup) proves PRIVATE-exclusion also applies to this construct,
! mirroring HiddenMember for the simple-reference-member case. Expected before the fix: neither
! MultiLineGroup nor HiddenGroupMember exists as a symbol (the former because no symbol was ever
! created for this construct at all; the latter for the same reason, so its PRIVATE-exclusion was
! never actually exercised prior to this fix -- confirmed absent for the right reason, not the
! wrong one, by inspecting the pre-fix state directly).
MultiLineGroupBugClass.CallViaAfterMultiLineGroupMember PROCEDURE( )
result   LONG
  CODE
  result = SELF.AfterMultiLineGroupMember.Sign( 60 )
  RETURN result

