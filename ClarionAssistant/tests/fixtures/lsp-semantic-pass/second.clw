  MEMBER('ctrl.clw')

! Aimed at each layer SEPARATELY, so no single behaviour can make the whole file pass:
!
!   MyGlobalVar / AnotherGlobal   cross-file globals from the PROGRAM module
!   IncGlobalOne / IncGlobalTwo   cross-file globals reached via INCLUDE
!       -> must NOT be flagged. If they are, either the server regressed or our
!          undeclared-identifier filter has stopped suppressing what it should.
!
!   TotallyUndeclaredXyz / AlsoNotDeclaredAbc   declared nowhere at all
!       -> must ALWAYS be flagged, and they arrive ONLY in the server's second,
!          async semantic publish. They are the regression guard for b7505691:
!          before the fix the first query returned zero of them, with pending:false.

SecondProc PROCEDURE

LocalOne             LONG

  CODE
  LocalOne = 1
  MyGlobalVar = 2
  AnotherGlobal = 'x'
  IncGlobalOne = 5
  IncGlobalTwo = 'y'
  TotallyUndeclaredXyz = 3
  AlsoNotDeclaredAbc = 4
  RETURN
