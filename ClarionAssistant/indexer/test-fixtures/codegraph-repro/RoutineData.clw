  MEMBER('Worker.clw')

! Round 5 (Defect A): a ROUTINE's explicit DATA block. The parser's ROUTINE handler
! closed inCode but nothing ever re-opened declaration scanning, so every routine-DATA
! declaration was invisible — v61: 9,261 declarations across 890 generated files, and
! their references then emitted nothing either. Mirrors PRMBase002.clw:1119-1126
! (TS::MakeCalendar:8 ROUTINE / DATA / r:Count BYTE(1) / CODE), plus the DECIMAL and
! LIKE() probe shapes CC confirmed absent. Expected: r:Count / r:Multiplier / r:Copy
! are type='variable', scope='local', parent_name='TS::MakeCalendar:8' (the ROUTINE,
! not the enclosing procedure — parent chain: procedure -> routine -> variable), and
! the routine's body emits references edges to all three.

  MAP
RoutineDataTest   PROCEDURE( ), LONG
  END

RoutineDataTest PROCEDURE( )
loc:Total  LONG
  CODE
  DO TS::MakeCalendar:8
  RETURN loc:Total

TS::MakeCalendar:8 ROUTINE
  DATA
r:Count      BYTE(1)
r:Multiplier DECIMAL(14,4)
r:Copy       LIKE(loc:Total)
  CODE
  r:Count = r:Count + 1
  r:Multiplier = r:Count * 2
  r:Copy = loc:Total + r:Multiplier
  loc:Total = r:Copy
