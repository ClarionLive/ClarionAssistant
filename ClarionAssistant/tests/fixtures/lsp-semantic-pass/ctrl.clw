  PROGRAM

! The PROGRAM module. Its declaration section is the only place MyGlobalVar and
! AnotherGlobal exist, so a single-file token walk over second.clw cannot see them.
! This file is also the CLEAN control: it must keep reporting zero diagnostics.

MyGlobalVar          LONG
AnotherGlobal        STRING(20)
  INCLUDE('globals.inc')

  MAP
    SecondProc
  END

  CODE
  MyGlobalVar = 1
  SecondProc
  RETURN
