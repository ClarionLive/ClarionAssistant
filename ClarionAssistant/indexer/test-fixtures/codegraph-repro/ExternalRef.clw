  MEMBER('Worker.clw')

! Round 5 (globals rescoped): PTS::ProgPath is OWNED by ReproProject2 — the plain
! declaration in proj2\Worker2.clw's global data section. This module IMPORTS it with
! ,EXTERNAL (decl_kind='external'). Before round 5, the reference below resolved to
! this file's own external row (the co-located declaration always won the per-file
! match) and the owner starved — v61: externals absorbed 2,863 incoming references vs
! the owners' 1,031. The edge must land on the OWNING declaration (scope='global',
! decl_kind NULL); the external row must have ZERO incoming references.
!
! NOTE: like Bug Q's UnreachableLocalRefTest, this file is parse-territory only — the
! fixture never links proj1, so the EXTERNAL is deliberately not resolved by a real
! export. The indexer parses text regardless of link state.

  MAP
ExternalRefTest   PROCEDURE( ), LONG
  END

PTS::ProgPath   CSTRING(256),EXTERNAL

ExternalRefTest PROCEDURE( )
loc:Len  LONG
  CODE
  loc:Len = LEN(CLIP(PTS::ProgPath))
  RETURN loc:Len
