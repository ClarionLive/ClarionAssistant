# COM/OLE Controls in Clarion

Using COM/OLE controls from Clarion code. To build a .NET COM control for Clarion (RegFree COM, project settings, interfaces, deployment), use the clarioncom-control skill.

## Creating a COM Control

Host it in an `OLE` control (usually hidden; it takes its own `END`, see windows-events.md) and create the object after `OPEN(Window)`:
```clarion
MyCOMCtrl    SIGNED,STATIC                 ! Usually module level

  OPEN(Window)
  MyCOMCtrl               = ?OLE            ! The OLE control's field number
  MyCOMCtrl{PROP:Create}  = 'ProgId.ClassName'
```

## Properties and Methods

Property names are case-sensitive. The usual pattern sets properties, then calls one action method:
```clarion
MyCOMCtrl{'Init()'}                        ! Parameterless method
MyCOMCtrl{'Title'}       = 'Hello'          ! Set a property
Value = MyCOMCtrl{'Title'}                  ! Get a property
MyCOMCtrl{'Show(param1, param2)'}          ! Parameters go inside the string
```

## Events

```clarion
! Register event handler
OCXREGISTEREVENTPROC(MyCOMCtrl, EventHandlerFunction)

! Event handler function
EventHandlerFunction PROCEDURE(*SHORT Reference, SIGNED OleControl, LONG CurrentEvent)
EventName    STRING(20)
EventParm1   STRING(5000)
  CODE
  EventName  = OleControl{PROP:LastEventName}
  EventParm1 = OCXGETPARAM(Reference, 1)

  IF OleControl = MyCOMCtrl
    CASE EventName
    OF 'EventName1'
      ! Handle event
    OF 'EventName2'
      ! Handle event
    END
  END
  RETURN(TRUE)
```

If the handler never fires, check the .NET side: COM events reach `OCXREGISTEREVENTPROC` only when the .NET class inherits `UserControl` (or another Control). .NET implements the connection points only for Control-derived classes; a plain class with `[ComSourceInterfaces]` has no subscribers.
