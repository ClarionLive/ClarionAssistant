   PROGRAM

! Project 2 of the fixture (ticket b7553893): exists to prove SCOPE-ORDERED call resolution.
! MainHelperProc below deliberately shares its name with ReproProject's MainHelperProc —
! before scope-ordered resolution, every call to that name solution-wide collapsed onto
! whichever copy was inserted last. Each project's calls must resolve to ITS OWN copy.
!
! Unlike Worker.clw (whose MAP prototypes sit at column 0 and are therefore invisible to
! MapProcDeclRegex — a separate, pre-existing limitation), this MAP is conventionally
! indented, so its prototypes ARE captured and must carry decl_kind='prototype'.

   MAP
     MODULE('Worker2Lib.clw')
       MainHelperProc PROCEDURE, LONG
       Caller2        PROCEDURE, LONG
       RIDelete:Fixture PROCEDURE, LONG
       Preview:SelectFixture PROCEDURE( *LONG pA, *LONG pB )
     END
   END

! Round 5 (globals rescoped): the OWNING declaration of PTS::ProgPath — scope='global',
! decl_kind NULL. ReproProject's ExternalRef.clw imports it with ,EXTERNAL; that file's
! reference must re-point HERE (cross-project), and the program-CODE assignment below
! pins the owner's own direct reference edge.
PTS::ProgPath  CSTRING(256)

 CODE
    PTS::ProgPath = 'C:\FIXTURE'
    r# = Caller2()
    r# = RIDelete:Fixture()
    DO Main:Tally

! Round 5 / pipeline run-1: a ROUTINE attached to the PROGRAM's own global CODE section
! (legal, hand-written mains have these). Its DATA-block local must be scope='local'
! with parent_name='Main:Tally' — NOT scope='global' (currentProcedure is null here;
! before the fix that leaked it into the global namespace and even made it a candidate
! external re-point owner).
Main:Tally ROUTINE
  DATA
r:MainCount  LONG
  CODE
  r:MainCount += 1
