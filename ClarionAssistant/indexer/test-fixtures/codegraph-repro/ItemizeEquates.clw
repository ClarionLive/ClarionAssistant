  MEMBER('Worker.clw')

! ITEMIZE member naming in DATA sections (module-level and procedure-local) plus the
! file-level blocks in ItemizeEquates.inc. Before the fix, members were indexed under
! their bare label (Text, None, Red, ... -- names that do not exist) and members without
! a value were not indexed at all. ItemizeTest references every expected name, so this
! module compiling proves the expected names are the real ones.

  INCLUDE('ItemizeEquates.inc'),ONCE

  MAP
ItemizeTest       PROCEDURE( ), LONG
  END

                  ITEMIZE,PRE(FxMod)           ! module-level DATA
ModA                EQUATE                     ! FxMod:ModA
ModB                EQUATE                     ! FxMod:ModB
                  END

ItemizeTest PROCEDURE( )
LocColor          ITEMIZE(1),PRE(LocC)         ! procedure-local DATA, period terminator
Blue                EQUATE                     ! LocC:Blue
Green               EQUATE                     ! LocC:Green
                  .
loc:Sum  LONG
  CODE
  loc:Sum = FixFormat:Text + FixFormat:Base64 + FixFormat:CData
  loc:Sum += FX:RESET:None + FX:RESET:Value
  loc:Sum += FixColor:Red + FixColor:White
  loc:Sum += FixState:Normal + FixState:Hot
  loc:Sum += FixModeA + FixModeB
  loc:Sum += FxP:First + FxP:Second + FixPlain
  loc:Sum += FxMod:ModA + FxMod:ModB + LocC:Blue + LocC:Green
  RETURN loc:Sum
