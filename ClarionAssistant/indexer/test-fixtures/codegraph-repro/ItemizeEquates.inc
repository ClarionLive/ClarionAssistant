! ITEMIZE member naming (fixture for the ITEMIZE/PRE indexer fix). Each block's members
! are named the way the compiler names them; ItemizeEquates.clw references every expected
! name, so this pair compiling proves the names below are real.

FixFormat           EQUATE(UNSIGNED)
                  ITEMIZE, PRE(FixFormat)      ! blank label, space after the comma
Text                EQUATE(0)                  ! FixFormat:Text
Base64              EQUATE                     ! FixFormat:Base64 (no value: auto-numbered)
CData               EQUATE                     ! FixFormat:CData
                  END

                  ITEMIZE,PRE(FX:RESET)        ! colon-qualified prefix
None                EQUATE(-1)                 ! FX:RESET:None
Value               EQUATE(01H)                ! FX:RESET:Value
                  END

FixColor          ITEMIZE(0),PRE               ! empty prefix: the ITEMIZE label is used
Red                 EQUATE                     ! FixColor:Red
White               EQUATE                     ! FixColor:White
                  END

FixState          ITEMIZE,PRE()                ! empty prefix, members already qualified
FixState:Normal     EQUATE(1)                  ! FixState:Normal (not FixState:FixState:Normal)
FixState:Hot        EQUATE                     ! FixState:Hot
                  END

FixMode           ITEMIZE                      ! no PRE: members keep their own labels
FixModeA            EQUATE                     ! FixModeA
FixModeB            EQUATE                     ! FixModeB
                  END

                  ITEMIZE(10),PRE(FxP)         ! period terminator
First               EQUATE                     ! FxP:First
Second              EQUATE                     ! FxP:Second
                  .

FixPlain            EQUATE(7)                  ! control: ordinary file-level EQUATE after the blocks
