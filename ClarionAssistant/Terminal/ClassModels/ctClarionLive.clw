  MEMBER()

!--- these are usually set as project Defines
!omit('***',_c55_)
!_ABCDllMode_  EQUATE(0)
!_ABCLinkMode_ EQUATE(1)
!***
!
!--------------------------
!ClarionLive Skeleton Class
!--------------------------

  INCLUDE('EQUATES.CLW')  !for ICON: and BEEP: etc.
! INCLUDE('KeyCodes.CLW')

  INCLUDE('ctClarionLive.INC')

  MAP
  END

!----------------------------------------
ctClarionLive.Construct    PROCEDURE()
!----------------------------------------
  CODE
  SELF.Q &= NEW qtIDDescr

  RETURN

!---------------------------------------
ctClarionLive.Destruct    PROCEDURE()
!---------------------------------------
  CODE
  IF NOT(SELF.Q &= NULL)
     SELF.QFree()
     DISPOSE(SELF.Q)
  END

  RETURN


!-----------------------------------
ctClarionLive.Init    PROCEDURE()
!-----------------------------------

  CODE

  SELF.InDebug = FALSE

  RETURN

!-----------------------------------
ctClarionLive.Kill    PROCEDURE()
!-----------------------------------

  CODE

  RETURN

!-----------------------------------------
ctClarionLive.HelloWorld    PROCEDURE()
!-----------------------------------------

  CODE

  BEEP(BEEP:SystemAsterisk)
  MESSAGE('Hello World From Clarion Live', 'Hello', ICON:ASTERISK)

  SELF.RaiseError('Display some kind of Error (only in Debug Mode)')

  RETURN

!---------------------------------------------------------
ctClarionLive.RaiseError    PROCEDURE(STRING pErrorMsg)
!---------------------------------------------------------

  CODE

  IF SELF.InDebug = TRUE
    BEEP(BEEP:SystemExclamation)
    MESSAGE(CLIP(pErrorMsg), 'ClarionLive Error', ICON:EXCLAMATION)
  END

  RETURN

!---------------------------------------
ctClarionLive.QFree           PROCEDURE()
!---------------------------------------
  CODE
  LOOP
    GET(SELF.Q, 1)
    IF ERRORCODE() THEN BREAK END
    SELF.QDel()
  END

!---------------------------------------
ctClarionLive.QDel            PROCEDURE()
!---------------------------------------
!This method gives you an opportunity to DISPOSE of References in this Q (If are any)
  CODE
  DELETE(SELF.Q)

