  MEMBER('Worker2.clw')

! MainHelperProc: same NAME as ReproProject's, different project — the scope-ordered
! resolution test subject. Caller2's call below must resolve HERE (same project), and
! ReproProject's program-CODE call to MainHelperProc must keep resolving to ITS copy.
MainHelperProc PROCEDURE
 CODE
    RETURN 202

! Caller2 also carries the fixture's ONLY ROUTINE + DO pair (b7553893 #4): the routine
! label deliberately contains a colon (Tidy:Up2), the shape \w+ used to miss entirely.
! The DO edge must resolve via (file, owning procedure, routine name).
Caller2 PROCEDURE
R2  LONG
 CODE
    R2 = MainHelperProc()
    DO Tidy:Up2
    RETURN R2

Tidy:Up2 ROUTINE
    R2 += 1

! COLON-LABELLED procedure/function definitions (CC's round-2 battery find): the [\w.]+
! definition regexes matched dots (class methods) but not colons, so ANY colon-bearing label —
! 18,877 declarations in v61, the entire template-generated RI layer — yielded zero procedure
! symbols, zero relationship rows from its files, and orphaned every routine inside them.
! These two mirror the field repro shapes exactly: 'RIDelete:AAATemplate FUNCTION'
! (PRMBa_RD.CLW:5) and 'Preview:SelectDisplay PROCEDURE(*LONG,*LONG)' (PRM00_SF.CLW:858).
RIDelete:Fixture FUNCTION
 CODE
    RETURN 1

Preview:SelectFixture PROCEDURE( *LONG pA, *LONG pB )
 CODE
    pA += pB
