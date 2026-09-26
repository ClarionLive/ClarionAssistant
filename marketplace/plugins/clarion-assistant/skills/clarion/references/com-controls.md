# COM/OLE Controls in Clarion and Building .NET COM Controls

Using COM/OLE controls from Clarion code, and building .NET COM controls that work with Clarion (RegFree COM, UserControl inheritance, event wiring).

## COM/OLE Control Usage

### Creating COM Controls
1. Add OLE control to window
2. Assign control reference to variable
3. Create COM object

```clarion
! Variable declaration (usually module level)
MyCOMCtrl    SIGNED,STATIC

! In window open procedure
MyCOMCtrl               = ?OLE                      ! ?OLE is control reference
MyCOMCtrl{PROP:Create}  = 'ProgId.ClassName'       ! Create COM object
```

### Accessing COM Properties
```clarion
! Setting property
MyCOMCtrl{'PropertyName'} = Value

! Getting property
Value = MyCOMCtrl{'PropertyName'}
```

### Calling COM Methods
```clarion
! Parameterless method
MyCOMCtrl{'MethodName()'}

! Method with parameters (old style, pass as single string)
MyCOMCtrl{'MethodName(param1, param2, param3)'}
```

### Property-Based COM Pattern
Modern pattern: Set properties, then call action method:
```clarion
MyCOMCtrl{'Init()'}
MyCOMCtrl{'Property1'} = 'Value1'
MyCOMCtrl{'Property2'} = 123
MyCOMCtrl{'Property3'} = 0
MyCOMCtrl{'Execute()'}
```

### COM Event Handling
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

## Building .NET COM Controls for Clarion

### CRITICAL WARNING: RegFree COM Only

**You MUST use RegFree COM deployment with manifest files. DO NOT use EnableComInterop or RegisterForComInterop.**

- ❌ **NEVER** set `<EnableComInterop>true</EnableComInterop>` - This generates .tlb files that conflict with manifest-based activation
- ❌ **NEVER** set `<RegisterForComInterop>true</RegisterForComInterop>` - This attempts registry registration that breaks RegFree COM
- ✅ **ALWAYS** use manifest files for deployment
- ✅ **ALWAYS** set only `<ComVisible>true</ComVisible>` in your .csproj
- ✅ **ALWAYS** use `Microsoft.NET.Sdk` (not WindowsDesktop)

Registry-based COM registration conflicts with Clarion's manifest-based activation and will cause:
- Events not firing in Clarion
- Registration failures
- Deployment issues
- Resource conflicts

### Critical Requirement: UserControl Inheritance

**IMPORTANT:** For COM events to work with Clarion's `OCXREGISTEREVENTPROC`, your .NET COM class **MUST inherit from UserControl** (or another Control-derived class).

#### Why This Matters

.NET's COM interop provides automatic COM event infrastructure (connection points) **ONLY** for Control-derived classes:

- ✅ **Control-derived class**: .NET automatically implements `IConnectionPointContainer`, `IConnectionPoint`, and all COM event plumbing
- ❌ **Plain class**: The `[ComSourceInterfaces]` attribute is just metadata; no automatic connection point implementation occurs

This is the difference between `subscribers=0` (events never work) and properly functioning COM events that Clarion can receive.

### Proper COM Control Structure

#### 1. Project Configuration (.csproj)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <PlatformTarget>x86</PlatformTarget>
    <OutputType>Library</OutputType>
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>

    <!-- COM Interop Settings - RegFree COM ONLY -->
    <ComVisible>true</ComVisible>
  </PropertyGroup>
</Project>
```

**CRITICAL: RegFree COM (No Registry Registration)**

You MUST NOT use the following settings:
- ❌ `<EnableComInterop>true</EnableComInterop>` - Generates unwanted .tlb files and conflicts with RegFree
- ❌ `<RegisterForComInterop>true</RegisterForComInterop>` - Attempts registry registration that conflicts with manifest-based activation

These settings will break RegFree COM deployment and cause Clarion integration issues.

**Key Points:**
- Use `Microsoft.NET.Sdk` (NOT `Microsoft.NET.Sdk.WindowsDesktop`)
- Include `<UseWindowsForms>true</UseWindowsForms>`
- Set `<RuntimeIdentifier>win-x86</RuntimeIdentifier>` for x86 builds
- Target .NET Framework 4.7.2 or 4.8 (Clarion compatibility)
- Only set `<ComVisible>true</ComVisible>` - no registry interop settings
- Use manifest files for RegFree COM deployment

#### 2. Event Interface (IYourControlEvents.cs)

```csharp
using System;
using System.Runtime.InteropServices;

namespace YourNamespace
{
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    [Guid("YOUR-GUID-HERE")]
    public interface IYourControlEvents
    {
        [DispId(1)]
        void ActionClicked(int actionId);

        [DispId(2)]
        void DataChanged(string data);
    }
}
```

**Key Points:**
- Use `InterfaceType.InterfaceIsIDispatch` for event interfaces
- Each event method needs a unique `[DispId(n)]`
- Keep event signatures simple (basic types: int, string, bool)

#### 3. Methods Interface (IYourControl.cs)

```csharp
using System;
using System.Runtime.InteropServices;

namespace YourNamespace
{
    [ComVisible(true)]
    [Guid("YOUR-GUID-HERE")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IYourControl
    {
        // Properties
        string Title { get; set; }
        int Type { get; set; }

        // Methods
        void Init();
        void Execute();
    }
}
```

#### 4. Main COM Class - THE CRITICAL PART

```csharp
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YourNamespace
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("YOUR-GUID-HERE")]
    [ComSourceInterfaces(typeof(IYourControlEvents))]  // Specifies event interface
    [ProgId("YourNamespace.YourControl")]
    public class YourControl : UserControl, IYourControl  // MUST inherit UserControl!
    {
        #region Event Delegates

        [ComVisible(false)]
        public delegate void ActionClickedDelegate(int actionId);

        [ComVisible(false)]
        public delegate void DataChangedDelegate(string data);

        #endregion

        #region COM Events

        // These events automatically get COM connection point infrastructure
        public event ActionClickedDelegate ActionClicked;
        public event DataChangedDelegate DataChanged;

        #endregion

        #region Properties

        private string _title;
        public string Title
        {
            get { return _title; }
            set { _title = value; }
        }

        #endregion

        #region Methods

        public void Init()
        {
            _title = null;
        }

        public void Execute()
        {
            // Your logic here
            RaiseActionClicked(1);
        }

        #endregion

        #region Event Raising

        protected virtual void RaiseActionClicked(int actionId)
        {
            try
            {
                // Simple standard .NET event raising
                ActionClicked?.Invoke(actionId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error raising event: {ex.Message}");
            }
        }

        #endregion
    }
}
```

**Critical Points:**
- ✅ **MUST inherit from UserControl**: `public class YourControl : UserControl, IYourControl`
- ✅ **Use [ComSourceInterfaces]**: Tells COM which interface defines events
- ✅ **Declare event delegates**: Mark with `[ComVisible(false)]`
- ✅ **Declare public events**: These become COM events automatically
- ✅ **Simple event raising**: Just use `EventName?.Invoke(...)`

### What NOT to Do

❌ **WRONG - Plain Class (Events Won't Work):**
```csharp
public class YourControl : IYourControl  // Plain class - NO UserControl!
{
    public event ActionClickedDelegate ActionClicked;  // Won't work with Clarion!
}
```

Result: `OCXREGISTEREVENTPROC` never subscribes, `subscribers=0`, events never reach Clarion.

❌ **WRONG - Manual Connection Point Implementation:**
Don't try to manually implement `IConnectionPointContainer` - it's complex and unnecessary.

❌ **WRONG - Callback Pattern:**
Don't create custom callback interfaces - standard COM events work perfectly with UserControl.

### Complete Working Example

See the ToastNotificationCOM project for a complete, working implementation that successfully passes events to Clarion applications.

Key files to reference:
- `ToastNotificationCOM.csproj` - Project configuration
- `IToastNotifierEvents.cs` - Event interface
- `IToastNotifier.cs` - Methods interface
- `ToastNotifier.cs` - Main class (inherits UserControl)

### Testing Your COM Control

After building your COM control, verify events work:

1. **Check DebugView** for event firing messages
2. **Verify Clarion receives events** in `OCXREGISTEREVENTPROC` handler
3. **Use OCXGETPARAM** to retrieve event parameters

```clarion
EventHandlerFunction PROCEDURE(*SHORT Reference, SIGNED OleControl, LONG CurrentEvent)
EventName    STRING(20)
Param1       LONG
  CODE
  EventName = OleControl{PROP:LastEventName}
  Param1    = OCXGETPARAM(Reference, 1)

  CASE EventName
  OF 'ActionClicked'
    MESSAGE('Button ' & Param1 & ' was clicked!')
  END

  RETURN(TRUE)
```

### Memory and Performance Impact

UserControl inheritance adds minimal overhead:
- **Memory**: ~1-2KB per instance
- **Performance**: No UI rendering if you never add visual elements
- **Thread Safety**: UserControl handles cross-thread marshaling automatically
- **Proven Pattern**: Used successfully in production COM controls

### Summary Checklist

When building .NET COM controls for Clarion:

- [ ] Use `Microsoft.NET.Sdk` SDK (NOT WindowsDesktop)
- [ ] Enable `<UseWindowsForms>true</UseWindowsForms>`
- [ ] Set `<ComVisible>true</ComVisible>` ONLY - no EnableComInterop or RegisterForComInterop
- [ ] Inherit from `UserControl` (or another Control class)
- [ ] Use `[ComSourceInterfaces(typeof(IYourEvents))]`
- [ ] Declare event delegates marked `[ComVisible(false)]`
- [ ] Declare public events (they become COM events automatically)
- [ ] Use simple event raising: `EventName?.Invoke(...)`
- [ ] Target .NET Framework 4.7.2 or 4.8
- [ ] Build as x86 for Clarion compatibility
- [ ] Deploy using RegFree COM with manifest files (no registry registration)

**NEVER USE:**
- ❌ `<EnableComInterop>true</EnableComInterop>`
- ❌ `<RegisterForComInterop>true</RegisterForComInterop>`

These settings generate .tlb files and attempt registry registration that breaks RegFree COM.

✅ **With UserControl inheritance + RegFree COM**: Events work perfectly with Clarion!

## Example: Complete COM Control Usage

```clarion
PROGRAM

  MAP
    MODULE('MyModule.CLW')
      MainWindow PROCEDURE
    END
  END

  CODE
  MainWindow

MainWindow PROCEDURE

Window    WINDOW('My Application'),AT(,,400,300),FONT('Segoe UI',9)
            BUTTON('Show Toast'),AT(10,10,100,30),USE(?ButtonShow)
            OLE,AT(0,0),USE(?OLE),HIDE
            END
          END

toast_COMCtrl    SIGNED,STATIC

  CODE
  OPEN(Window)

  ! Initialize COM control
  toast_COMCtrl               = ?OLE
  toast_COMCtrl{PROP:Create}  = 'ToastNotificationCOM.ToastNotifier'

  LOOP
    CASE ACCEPTED()
    OF ?ButtonShow
      ! Use property-based API
      toast_COMCtrl{'Init()'}
      toast_COMCtrl{'Title'}      = 'Hello World'
      toast_COMCtrl{'Message'}    = 'This is a test'
      toast_COMCtrl{'Type'}       = 1  ! Success
      toast_COMCtrl{'DurationMs'} = 5000
      toast_COMCtrl{'ShowToast()'}
    END

    CASE EVENT()
    OF EVENT:CloseWindow
      BREAK
    END
  END

  RETURN
```
