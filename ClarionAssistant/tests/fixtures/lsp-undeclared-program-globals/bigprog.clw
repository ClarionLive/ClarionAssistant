  PROGRAM

! e2f87efb fixture: the shape of a template-generated PROGRAM whose global FILE labels CA's 'not declared'
! filter missed. Synthetic; no customer code.

  MAP
    MODULE('bigmodule.clw')
UseGlobals    PROCEDURE()
    END
  END

GlobBefore           LONG(0)

! A CLASS,TYPE whose method prototype sits at column 1 - what template-generated PROGRAMs emit. It used to
! end the scanned declaration range here, before every FILE below.
TranExternal         CLASS,TYPE
TranslateString     PROCEDURE(STRING InputString),STRING,VIRTUAL
                     END

! An unconditional OMIT block holding a bare CODE line is dead text, not the global CODE.
  OMIT('**FxOmit**')
OmittedProc   PROCEDURE()
  CODE
  **FxOmit**

ADDONS               FILE,DRIVER('TOPSPEED'),PRE(ADD),THREAD
KeyCode                KEY(ADD:CODE),NOCASE
Record                 RECORD,PRE()
CODE                        STRING(16)
Qty                         LONG
                         END
                     END

Pinvdet              FILE,DRIVER('TOPSPEED'),PRE(PND),THREAD
Record                 RECORD,PRE()
Id                          LONG
                         END
                     END

  CODE
  UseGlobals()

TailProc             PROCEDURE()
FxTailLocal          LONG
  CODE
  FxTailLocal = 1
