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
     END
   END

 CODE
    r# = Caller2()
