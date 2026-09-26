  MEMBER()

! Round 5 (Defect B): the NYSCommon.CLW shape — a MEMBER library file with TWO sibling
! top-level MAP blocks before the first implementation, the second carrying an INCLUDE
! and a nested MODULE('Win32API') block with its own END. The relationship pre-scan
! used to take the first MAP prototype line as the file's parent procedure (no symbol
! exists at that line — ParseMemberFile skips MAPs wholesale), the by-line lookup
! failed, parentProcId stayed -1, and the ENTIRE file was skipped: zero calls / do /
! references from any body in it (v61: NYSReportControl, NYSCalendarPro,
! NYSTemplateHelper, NYSCommon). Expected after round 5: DualMapProcA emits calls
! edges to DualMapHelper and DualMapIncProc (implementation rows) and references
! edges to loc:Ticks.

  MAP
DualMapProcA      PROCEDURE( ), LONG
DualMapHelper     PROCEDURE( ), LONG
  END

  MAP
    INCLUDE('DualMapProtos.inc'),ONCE
    MODULE('Win32API')
      DMGetTickCount PROCEDURE( ),LONG,PASCAL,RAW,NAME('GetTickCount')
    END
  END

DualMapProcA PROCEDURE( )
loc:Ticks  LONG
  CODE
  loc:Ticks = DMGetTickCount()
  loc:Ticks += DualMapHelper()
  loc:Ticks += DualMapIncProc()
  RETURN loc:Ticks

DualMapHelper PROCEDURE( )
  CODE
  RETURN 42

DualMapIncProc PROCEDURE( )
  CODE
  RETURN 7
