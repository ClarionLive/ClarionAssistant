# Clarion Windows, Events, and Properties

ACCEPT loop event processing, EVENT constants, PROP:xxx property syntax, and WINDOW definitions.

## ACCEPT Loop (Event Processing)

```clarion
  OPEN(Window)
  ACCEPT
    CASE EVENT()
    OF EVENT:OpenWindow
      ! Initialize controls
    OF EVENT:Accepted
      CASE FIELD()
      OF ?ButtonSave
        ! Save button was clicked
        DO SaveRecord
      OF ?ButtonCancel
        POST(EVENT:CloseWindow)
      END
    OF EVENT:NewSelection
      CASE FIELD()
      OF ?ListBox1
        ! List selection changed
      END
    OF EVENT:CloseWindow
      BREAK
    END
  END
  CLOSE(Window)
```

### Common EVENT Constants
```clarion
EVENT:Accepted         ! Control was accepted (button click, enter)
EVENT:NewSelection     ! List/combo selection changed
EVENT:OpenWindow       ! Window opened
EVENT:CloseWindow      ! Window closing
EVENT:LoseFocus        ! Control lost focus
EVENT:GainFocus        ! Control gained focus
EVENT:Timer            ! Timer fired
EVENT:AlertKey         ! Alert key pressed
EVENT:PreAlertKey      ! Before alert key
EVENT:Dragging         ! Drag in progress
EVENT:Drag             ! Drag started
EVENT:Drop             ! Drop occurred
EVENT:User             ! Base for user-defined events (400h)
```

## PROP:xxx Property Syntax

Access control and object properties with `{PROP:xxx}`:
```clarion
! Control properties
?ListBox{PROP:Selected} = 1              ! Set selected row
Value = ?EditField{PROP:ScreenText}      ! Get displayed text
?Control{PROP:Hide} = TRUE               ! Hide a control
?Control{PROP:Disable} = TRUE            ! Disable a control
?List{PROP:Format} = '80L|80L|40R'       ! Set list format
?List{PROP:VScrollPos}                   ! Get scroll position

! Window properties
SYSTEM{PROP:Timer} = 100                 ! Set timer interval (centiseconds)

! Indexed properties
FieldLabel = File{PROP:Label, idx}       ! Get field label at index
FieldType = File{PROP:Type, idx}         ! Get field type at index
```

## Window/Form Syntax

### Window Definition
One `END` closes the WINDOW and its controls. Container controls (SHEET, TAB, OPTION, GROUP, OLE, MENUBAR, TOOLBAR) take their own `END`. A second `END` after the controls does not compile.

```clarion
Window    WINDOW('Window Title'),AT(,,343,131),FONT('Segoe UI',9),CENTER,SYSTEM
            BUTTON('Click Me'),AT(15,10,64,20),USE(?ButtonID)
            SHEET,AT(4,34,250,90),USE(?Sheet)
              TAB('First'),USE(?Tab1)
              END
            END
            OLE,AT(291,79),USE(?OLE),HIDE      ! COM host control, usually hidden
            END                                ! The OLE control takes its own END
          END                                  ! Closes the WINDOW
```
