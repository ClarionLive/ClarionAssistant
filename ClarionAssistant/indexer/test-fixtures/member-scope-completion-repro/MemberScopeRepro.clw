!--------------------------------------------------------------------------------------------------
! Repro for the buffer-derived member scope overriding the language server's members.
!
! Covers both defects in SharedLspBridge.cs:
!   (1) ParseScopeStructures kept a self-closing declaration OPEN, so it adopted the following
!       declarations as its own fields (CgClosesOnSameLine).
!   (2) CgStruct discarded a declaration's type argument, so its field set was INCOMPLETE and
!       scoping to it dropped every field the server resolved from the type (CgStruct.BaseType).
!
! Every structure below is correctly terminated and this file COMPILES AND RUNS. That matters
! beyond syntax checking: case B uses a field from the type AND a field declared inline on the
! same variable, so a successful build is the compiler itself confirming the semantics the fix
! depends on -- a typed structure carries BOTH sets of fields.
!
! The cases are declared as PROCEDURE LOCALS, not as global PROGRAM data. That is deliberate:
! it is where these declarations actually live in real code, and it keeps the fixture focused.
! Declared globally, a dotted reference outside any procedure takes a different hover path
! (HoverProvider.resolveDottedFieldHover, which splits the single StructureField token at the
! dot) and hovering the STRUCTURE half answers with the FIELD -- pre-existing behaviour, nothing
! to do with the completion defects this fixture is for, but a confusing distraction if a
! reviewer meets it here. Do not "simplify" these declarations back to global scope.
!
! Completion is verified by hand (see README.md); there is no unit-test harness for this code.
!--------------------------------------------------------------------------------------------------

  PROGRAM

  INCLUDE('MemberScopeTypes.inc'),ONCE

  MAP
ReproProc            PROCEDURE()
  END

  CODE
  ReproProc()
  RETURN

ReproProc PROCEDURE()

! --- Case A: a declaration that CLOSES ON ITS OWN LINE, followed by unrelated locals -----------
! Before the fix this stayed "open" and swallowed ReplDate/ReplTime/Counter as its own fields,
! so completion on "Settings." offered those three instead of the type's fields.
Settings      GROUP(SettingsGroupType)  END
ReplDate      LONG
ReplTime      LONG
Counter       LONG

! --- Case B: a typed structure that ALSO declares its own inline fields ------------------------
! The type contributes Code/RefDate/Description; CategoryName/SourceSystem are declared here.
! Clarion gives ItemQ all five. Before the fix, completion on "ItemQ." offered only the two
! inline ones, because the buffer scan never reads the type and the incomplete set was then
! used to filter the server's correct answer.
ItemQ         QUEUE(ItemQueueType)
CategoryName    STRING(16)
SourceSystem    STRING(32)
              END

! --- Case D: a plain structure with NO type argument (negative control) ------------------------
! Nothing changes for this shape: its field set is genuinely complete, so scoping still applies
! and still suppresses the server's keyword dump.
Cfg           GROUP,PRE(CFG)
Host            STRING(64)
Port            LONG
              END

! --- Case E: an attribute whose STRING LITERAL contains "END" ----------------------------------
! Must NOT be read as closing on its own line. CgClosesOnSameLine blanks string literals first.
Appended      QUEUE,NAME('APPEND')
Line            STRING(32)
              END

! --- Case F: an opener with a trailing COMMENT that mentions "end" -----------------------------
! Must NOT be read as closing on its own line. The comment is stripped before the check.
Commented     GROUP(SettingsGroupType)   ! not the end of this group
Extra           LONG
              END

! --- Case G: PRE() only, no type argument (negative control for base-type extraction) ----------
! ",PRE(q)" must NOT be mistaken for a type argument -- the pattern is anchored to the first
! token after the keyword, so a later attribute's parentheses cannot be read as the base type.
Prefixed      QUEUE,PRE(PFX)
Value           LONG
              END

  CODE
  ! Case B, the load-bearing line for this fixture: a field inherited FROM THE TYPE and a field
  ! declared INLINE on the same variable, used together. If a typed structure did not carry both
  ! sets, this would not compile.
  ItemQ.Code         = 1
  ItemQ.RefDate      = TODAY()
  ItemQ.Description  = 'from the type'
  ItemQ.CategoryName = 'inline'
  ItemQ.SourceSystem = 'inline'
  ADD(ItemQ)

  ! Case A: the type's fields are reachable through the self-closing declaration, and the locals
  ! that follow it are ordinary variables -- NOT members of Settings.
  Settings.Status  = 'ok'
  Settings.Retries = 0
  ReplDate = TODAY()
  ReplTime = CLOCK()
  Counter  = Counter + 1

  ! Remaining cases just need to exist and compile.
  CFG:Host      = 'localhost'
  CFG:Port      = 80
  Appended.Line = 'x'
  ADD(Appended)
  Commented.Status = 'ok'
  Commented.Extra  = 1
  PFX:Value = 1
  ADD(Prefixed)

  RETURN
