# Clarion Windows, Events, and Properties

ACCEPT loop event processing, EVENT constants, PROP:xxx property syntax, and WINDOW definitions.

## ACCEPT Loop (Event Processing)

The ACCEPT loop is the core event processing structure for windows:
```clarion
  OPEN(Window)
  ACCEPT
    CASE EVENT()
    OF EVENT:OpenWindow
      ! Window just opened — initialize controls
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
      IF SELF.Request = InsertRecord OR SELF.Request = ChangeRecord
        ! Prompt to save changes
      END
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
Windows require TWO `END` statements:
1. First `END` - closes the control list
2. Second `END` - closes the window structure

```clarion
Window    WINDOW('Window Title'),AT(,,Width,Height),FONT('Segoe UI',9)
            BUTTON('Click Me'),AT(X,Y,W,H),USE(?ButtonID)
            OLE,AT(X,Y),USE(?OLE),HIDE
            END                    ! Closes control list
          END                      ! Closes window structure
```

**Important:** The OLE control for COM objects is typically positioned off-screen or hidden:
```clarion
Window    WINDOW('Toast Notifications'),AT(,,343,131),FONT('Segoe UI',9),CENTER,SYSTEM
            BUTTON('Show Toast'),AT(15,10,64,20),USE(?BUTTONShowToast)
            OLE,AT(291,79),USE(?OLE),HIDE
            END
          END
```

### Accept Loop Pattern
```clarion
LOOP
  CASE ACCEPTED()
  OF ?ButtonID
    ! Button was clicked
  END

  CASE EVENT()
  OF EVENT:CloseWindow
    BREAK
  END
END
```

**Note:** For window event processing, prefer the ACCEPT structure (above) over a plain LOOP with ACCEPTED() — the LOOP/ACCEPTED() form is an old/simplified pattern.
