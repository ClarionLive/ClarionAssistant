!--------------------------------------------------------------------------------------------------
! Repro/test source for the GROUP (and RECORD) keyword-as-label ambiguity in
! ModernEmbeditorDiagnostics.cs's per-slot structure-balance checker.
!
! Every structure below is CORRECTLY terminated and this file compiles. With the bug present
! (GROUP still listed in DeclarationStructKeywords, no dedicated GroupOpen regex), cases B/B2/C/D
! are wrongly treated as "used as a label" and their openers are never pushed -- this desyncs the
! balance stack and makes a later, genuinely-terminated END misreport as:
!     "END has no matching structure in this embed slot."
! Case F (the ORIGINAL bug this whole thing started from) must NOT push an opener -- "Group" here
! is a plain reference variable, not a structure. With the naive blunt fix (just deleting "GROUP"
! from DeclarationStructKeywords, no dedicated regex), case F regresses and gets falsely flagged
! as an unterminated GROUP.
!
! Expected result once the GroupOpen-regex fix is correctly applied: ZERO diagnostics anywhere in
! this file.
! - Before the fix (current committed state): the WINDOW's screen GROUP controls (cases A/A2)
!   desync the stack and produce a phantom "END has no matching structure" warning further down.
! - With only the blunt fix (GROUP deleted from DeclarationStructKeywords, no GroupOpen regex):
!   case F starts falsely flagging instead.
! - With the full GroupOpen-regex fix: zero diagnostics.
!--------------------------------------------------------------------------------------------------

 PROGRAM

 MAP 
Group PROCEDURE()    
 END   



! --- Case E: anonymous RECORD inside a FILE, no label at all ---
! FILE is always labeled (safe, unaffected). The RECORD immediately inside it can legitimately be
! bare -- same class of ambiguity as GROUP, flagged as a follow-up (not yet fixed).
ReproFile          FILE,DRIVER('TOPSPEED'),PRE(RPF),CREATE,BINDABLE
                       RECORD, PRE()
Field1                    STRING(20)
Field2                    LONG
                       END
                   END
 
! --- Case E2: labeled RECORD, for contrast (already worked before, must keep working) ---
ReproFile2         FILE,DRIVER('TOPSPEED'),PRE(RP2),CREATE,BINDABLE
ReproRecord            RECORD
Field1                    STRING(20)
                       END
                   END

! --- Case H: CLASS method named Group() -- exercises LabelDiagnostics.ts's EXISTING exception
!     (a structure-only keyword used as a method label INSIDE an enclosing CLASS/INTERFACE is
!     already allowed by findEnclosingStructure()). Unlike case G (a global, non-nested
!     Group PROCEDURE()), this is assumed but NOT YET independently confirmed to compile/run.
GroupTestClass     CLASS
Group                  PROCEDURE()
                   END

GTC                GroupTestClass

! --- main ---
  CODE
    !ReproGroupRecordTest()
    Group()
    GTC.Group()
  RETURN

GroupTestClass.Group PROCEDURE()
  CODE
  RETURN


Group PROCEDURE()


! --- Case B: labeled DATA group, keyword immediately followed by ',' (no space before punctuation) ---
MyGroup            GROUP,PRE(GRP)
GroupField            LONG
                   END

! --- Case B2: labeled DATA group, SPACE before the paren -- a real syntax variant that breaks a
!     too-tight candidate regex if it doesn't allow whitespace before the punctuation ---
InfoGroupType      GROUP, TYPE
Field                 LONG
                   END 


InfoGroup          GROUP (InfoGroupType), NAME('InfoGroup')
                   END

! --- Case C: bare, attribute-less, labeled group ---
BareLabeledGroup   GROUP
BareField             STRING(4)
                   END

! --- Case D: labeled group with a trailing inline comment after the opener ---
CommentedGroup     GROUP,PRE(CMT)              !CommentedGroup instance
CommentField          LONG
                   END

! --- Case F: THE ORIGINAL BUG -- "Group" used purely as a plain identifier/label, no structure at
!     all (mirrors the confirmed "Report &STRING" / "Window &STRING" / "Group &STRING" pattern,
!     three unrelated reference variables declared back-to-back). Must NOT be treated as an
!     opener -- there is no matching END for this line, and there must not be one.

Window             &STRING
Group              &STRING
Report             &STRING

Result             LONG(0)



! --- Case A / A2: bare screen GROUP controls nested in a WINDOW -- unlabeled, identified by
!     USE(), the actual regression pattern (confirmed against real production Clarion source).
ReproWindow WINDOW('Repro'),AT(,,300,200),GRAY,FONT('MS Sans Serif',8,,FONT:regular)
      GROUP('Group One'),AT(3,18,153,28),USE(?Group1),BOXED
        STRING('Id:'),AT(10,30),USE(?String1)
         ENTRY(@s6),AT(21,29,,10),USE(Result)
      END
      GROUP('Group Two'),AT(160,20,120,26),USE(?Group2),BOXED
         STRING('Start'),AT(163,32),USE(?String2)
         BUTTON('Refresh'),AT(230,29),USE(?RefreshButton)
      END
      BUTTON('Close'),AT(230,170,52,17),USE(?CloseButton)
    END


 CODE 

  OPEN(ReproWindow)
  ACCEPT
    CASE FIELD()
    OF ?CloseButton
      IF EVENT() = EVENT:Accepted THEN POST(EVENT:CloseWindow). 
    END
  END
  CLOSE(ReproWindow)
  RETURN
