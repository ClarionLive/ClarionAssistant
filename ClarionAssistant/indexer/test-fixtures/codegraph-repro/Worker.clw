PROGRAM

  MAP
    MODULE('WorkerLib.clw')
TestSignatureFlow PROCEDURE, LONG
ParameterTest      PROCEDURE( *WorkerClass pWorker ), LONG
ReturnTest         PROCEDURE( ), LONG
OmitTest           PROCEDURE, LONG
AfterOmitProc      PROCEDURE, LONG
CommentEmbeddedTest PROCEDURE, LONG
ConditionalOmitTest PROCEDURE, LONG
CommentedLocalTest PROCEDURE, LONG
GroupQueueLocalTest PROCEDURE, LONG
InlineLocalGroupTest PROCEDURE, LONG
LocalDerivedClassTest PROCEDURE, LONG
    END
MainHelperProc     PROCEDURE, LONG
  END

  INCLUDE('WorkerLib.inc'),ONCE

owner        OwnerClass
groupBug     GroupBugClass
periodBug    PeriodBugClass
afterBug     AfterBugClass
multiLineGroupBug MultiLineGroupBugClass

 CODE
    r# = TestSignatureFlow()
    r# = ReturnTest()
    r# = OmitTest()
    r# = AfterOmitProc()
    r# = CommentEmbeddedTest()
    r# = ConditionalOmitTest()
    r# = MainHelperProc()
    r# = owner.CallViaMember()
    r# = owner.CallViaCommentedMember()
    r# = CommentedLocalTest()
    r# = groupBug.CallViaAfterGroupMember()
    r# = periodBug.CallViaAfterPeriodMember()
    r# = afterBug.CallSomethingElse()
    r# = GroupQueueLocalTest()
    r# = InlineLocalGroupTest()
    r# = LocalDerivedClassTest()
    r# = multiLineGroupBug.CallViaAfterMultiLineGroupMember()

! Bug A repro: this procedure is implemented directly in the PROGRAM file itself,
! rather than in a MEMBER file. ParseMainFile only scans the file's PROGRAM marker,
! MAP/MODULE(...) structure, and INCLUDE lines ? it has no handling at all for a
! hand-written procedure body placed directly here, so this entire procedure (and
! its worker.Sign call) is invisible to CodeGraph, even though it compiles and runs
! fine. Compare with WorkerLib.clw, a MEMBER file, where the equivalent code IS seen.

MainHelperProc PROCEDURE()
worker    WorkerClass
result    LONG
  CODE
  result = worker.Sign( 5 )
  RETURN result



