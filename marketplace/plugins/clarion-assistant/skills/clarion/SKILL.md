---
name: clarion
# prettier-ignore
description: Clarion language programming reference with syntax rules, data types, control structures, Windows API integration patterns, and template-authoring gotchas (#AT, #IF, OMITTED scope). Auto-applies when working with Clarion source code or .tpl/.tpw template files. Uses parallel operations where applicable.
version: 1.0.0
---

# Clarion Language Programming Skill

You are an expert Clarion language programmer. Clarion is a Windows application development language with unique syntax and conventions.

## File Conventions

**Always write Clarion source files (`.clw`, `.inc`, `.equ`, `.int`, `.tpl`, `.tpw`, `.txa`) with CRLF line endings — actual carriage-return (0x0D) + line-feed (0x0A) bytes, NOT the literal two-character escape sequence `\r\n`.** The Clarion compiler and IDE expect Windows line endings. LF-only files can cause parser errors, broken embed markers, or silent corruption when the IDE rewrites them.

**Critical gotcha:** `mcp__clarion-assistant__write_file` does **not** interpret JSON escape sequences in its `content` parameter — passing a string containing `\r\n` writes the literal four characters `\`, `r`, `\`, `n` to disk. You must embed real newline bytes (a real CR and LF) in the content string itself. Alternatively, use Claude Code's built-in `Write` tool, which writes native Windows line endings on Windows. After writing, sanity-check by reading the file back and confirming there are no literal `\r\n` sequences in the output.

## Core Syntax Rules

### Comments
- Single-line comments start with `!`
- Example: `! This is a comment`

### Variable Declaration
Variables are declared with type after name:
```clarion
VariableName    TYPE
MyString        STRING(100)
MyNumber        LONG
MyByte          BYTE
MyReal          REAL
```

Common types:
- `STRING(size)` - Fixed or variable length string
- `LONG` - 32-bit signed integer
- `SHORT` - 16-bit signed integer
- `BYTE` - 8-bit unsigned integer
- `REAL` - 4-byte floating point
- `DECIMAL(digits,decimals)` - Decimal number

### Procedures
```clarion
ProcedureName PROCEDURE
! Local variables here
LocalVar    LONG
  CODE
  ! Procedure code here
  RETURN
```

### Control Structures

**IF statement:**
```clarion
IF condition
  ! code
END
```

**CASE statement:**
```clarion
CASE variable
OF value1
  ! code
OF value2
  ! code
END
```

**LOOP:**
```clarion
LOOP
  IF condition THEN BREAK.
  ! code
END
```

### String Literals
- Single quotes for strings: `'Hello World'`
- Double single quotes for embedded quote: `'Don''t'`

### Reserved Words and Keywords

Clarion has reserved words that cannot be used as identifiers (variable names, column names, table names, procedure parameters, etc.). Using reserved words as identifiers will cause compilation errors or unexpected behavior.

#### Strictly Reserved Keywords
**These keywords are reserved and may NOT be used as labels for any purpose:**

`ACCEPT`, `AND`, `ASSERT`, `BEGIN`, `BREAK`, `BY`, `CASE`, `CATCH`, `CHOOSE`, `CODE`, `COMPILE`, `CONST`, `CYCLE`, `DATA`, `DO`, `ELSE`, `ELSIF`, `END`, `EXECUTE`, `EXIT`, `FINALLY`, `FUNCTION`, `GOTO`, `IF`, `INCLUDE`, `LOOP`, `MEMBER`, `NEW`, `NOT`, `NULL`, `OF`, `OMIT`, `OR`, `OROF`, `PRAGMA`, `PROCEDURE`, `PROGRAM`, `RETURN`, `ROUTINE`, `SECTION`, `THEN`, `THROW`, `TIMES`, `TO`, `TRY`, `UNTIL`, `WHILE`, `XOR`

#### Data Structure Keywords
**These keywords may be used as labels of data structures or executable statements, but may NOT be the label of any PROCEDURE statement:**

`APPLICATION`, `CLASS`, `DETAIL`, `FILE`, `FOOTER`, `FORM`, `GROUP`, `HEADER`, `ITEM`, `ITEMIZE`, `JOIN`, `MAP`, `MENU`, `MENUBAR`, `MODULE`, `OLE`, `OPTION`, `QUEUE`, `PARENT`, `RECORD`, `REPORT`, `SELF`, `SHEET`, `TAB`, `TOOLBAR`, `VIEW`, `WINDOW`

**IMPORTANT:** `SELF` and `PARENT` cannot name local variables or parameters of any class or interface method.

#### Best Practices
- ✅ Use descriptive names that don't conflict with reserved words
- ✅ Prefix variables to avoid conflicts (e.g., `MyData` instead of `DATA`)
- ❌ Never use reserved keywords as column names in database tables
- ❌ Never use reserved keywords as procedure names
- ❌ Never use `SELF` or `PARENT` as local variables in class methods

## File Structure

### .clw File Structure (Implementation)
Every .clw implementation file follows this structure:
```clarion
                     MEMBER
                     MAP
                         MODULE('API')
                             SomeApiCall(*CSTRING),PASCAL,RAW,NAME('SomeWindowsApi')
                         END
                     END
    INCLUDE('MyClass.inc'),ONCE

MyClass.Init    PROCEDURE
  CODE
  ! implementation here

MyClass.Kill    PROCEDURE
  CODE
  ! implementation here
```

**Rules:** `MEMBER` must be first. Then optional `MAP/END` block. Then `INCLUDE` statements. Then procedure implementations.

### .inc File Structure (Declarations)
```clarion
MyClass    CLASS,TYPE,MODULE('MyClass.clw'),LINK('MyClass.clw')
Q                &MyQueue
Init             PROCEDURE
Kill             PROCEDURE
Process          PROCEDURE(STRING xParam),STRING,PROC
           END
```

**CLASS attributes:** `TYPE` (can be used as a type), `MODULE()` (implementation file), `LINK()` (link this file), `IMPLEMENTS()`, `PROTECTED`, `PRIVATE`, `VIRTUAL`

### Label Column Rules
**Labels MUST start in column 1.** Code statements are indented.
```clarion
MyVariable    LONG              ! Label at column 1
MyProc        PROCEDURE         ! Label at column 1
                CODE            ! CODE is indented
                RETURN          ! Statements are indented
```

### Statement Termination
Clarion does NOT use periods to end statements. Statements are terminated by newlines. `END` closes block structures.
```clarion
CLEAR(SELF.Q)                   ! No period
SELF.Q.Field &= xOrigField     ! No period
ADD(SELF.Q)                     ! No period
IF NOT ERRORCODE()              ! No period
   BREAK                        ! No period
END                             ! No period — END closes the IF
```

**Exception:** Single-line IF uses period: `IF condition THEN statement.`

## FILE/RECORD/KEY Declarations

```clarion
Customers        FILE,DRIVER('TOPSPEED'),PRE(CUS),CREATE,BINDABLE,THREAD
KeyId                KEY(CUS:Id),NOCASE,OPT,PRIMARY
KeyLastName          KEY(CUS:LastName),DUP,NOCASE
Record               RECORD,PRE()
Id                       LONG
FirstName                STRING(30)
LastName                 STRING(30)
Email                    STRING(100)
                     END
                 END
```

**Attributes:** `DRIVER()` (database driver), `PRE()` (field prefix), `CREATE`, `BINDABLE`, `THREAD`. Keys use `KEY()`, `NOCASE`, `OPT`, `PRIMARY`, `DUP`.

### OPEN/CLOSE Files
```clarion
OPEN(Customers)
IF ERRORCODE()
   MESSAGE('Cannot open file: ' & ERROR())
   RETURN
END
! ... work with file ...
CLOSE(Customers)
```

## QUEUE Operations

### Declaration
```clarion
MyQueue      QUEUE
Name             STRING(50)
Value            LONG
             END
```

### Operations
```clarion
! Add a record
CLEAR(MyQueue)
MyQueue.Name = 'Test'
MyQueue.Value = 42
ADD(MyQueue)                          ! Append to end
ADD(MyQueue, 1)                       ! Insert at position 1

! Get a record by position
GET(MyQueue, 1)                       ! Get first record
IF NOT ERRORCODE()
   ! record is now in the queue buffer
END

! Get by key value
MyQueue.Name = 'Test'
GET(MyQueue, MyQueue.Name)            ! Get by key field

! Update current record
MyQueue.Value = 99
PUT(MyQueue)

! Delete current record
DELETE(MyQueue)

! Other operations
RECORDS(MyQueue)                      ! Count of records
FREE(MyQueue)                         ! Delete all records
SORT(MyQueue, +MyQueue.Name, -MyQueue.Value)  ! Sort (+ ascending, - descending)
POINTER(MyQueue)                      ! Current position
```

## CLASS Methods and Inheritance

### Declaration (.inc)
```clarion
BaseClass      CLASS,TYPE,MODULE('BaseClass.clw'),LINK('BaseClass.clw')
Init              PROCEDURE
Kill              PROCEDURE
Process           PROCEDURE(STRING xParam),STRING,VIRTUAL
               END

DerivedClass   CLASS(BaseClass),TYPE,MODULE('DerivedClass.clw'),LINK('DerivedClass.clw')
Process           PROCEDURE(STRING xParam),STRING,VIRTUAL  ! Override
NewMethod         PROCEDURE
               END
```

### Implementation (.clw)
```clarion
                     MEMBER
    INCLUDE('DerivedClass.inc'),ONCE

DerivedClass.Process   PROCEDURE(STRING xParam)
RetVal    STRING(255)
  CODE
  RetVal = PARENT.Process(xParam)    ! Call parent method
  ! additional logic
  RETURN RetVal

DerivedClass.NewMethod PROCEDURE
  CODE
  SELF.Init()                         ! Call own method
```

### Reference Variables
```clarion
MyObj    &BaseClass                    ! Reference (pointer) variable
  CODE
  MyObj &= NEW DerivedClass           ! Allocate
  MyObj.Init()                        ! Call method
  DISPOSE(MyObj)                      ! Deallocate
```

**Pointer syntax:** `&=` assigns a reference. `&= NULL` checks/clears. `NEW` allocates. `DISPOSE` deallocates.

## ROUTINE and DO

ROUTINEs are named code blocks within a procedure. Called with `DO`.
```clarion
MyProc    PROCEDURE
Counter       LONG
  CODE
  DO InitializeData
  DO ProcessRecords
  RETURN

InitializeData   ROUTINE
  Counter = 0
  CLEAR(MyQueue)

ProcessRecords   ROUTINE
  LOOP Counter = 1 TO RECORDS(MyQueue)
    GET(MyQueue, Counter)
    ! process record
  END
```

**Rules:** ROUTINEs have access to the procedure's local variables. They cannot accept parameters or return values. Always called with `DO RoutineName`.

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

## Parameter Passing

### By Value (default)
```clarion
MyProc    PROCEDURE(STRING xName, LONG xCount)
```

### By Reference
```clarion
MyProc    PROCEDURE(*STRING xName, *LONG xCount)   ! * = by reference
  CODE
  xName = 'Modified'     ! Modifies caller's variable
```

### Omittable Parameters
```clarion
MyProc    PROCEDURE(STRING xName, <STRING xOptional>, <LONG xCount>)
  CODE
  IF NOT OMITTED(2)       ! Check if parameter 2 was passed (1-based)
    ! use xOptional
  END
  IF NOT OMITTED(3)
    ! use xCount
  END
```

**Angle brackets** `<>` denote omittable parameters. Check with `OMITTED(n)`.

### Return Values
```clarion
MyFunc    PROCEDURE(LONG xInput),STRING   ! Return type after parameters
  CODE
  RETURN 'Result: ' & xInput
```

**PROC attribute:** Add `,PROC` to allow calling a function and ignoring the return value.

## Built-in Functions

### String Functions
```clarion
CLIP(string)                    ! Remove trailing spaces
LEFT(string)                    ! Left-justify (remove leading spaces)
RIGHT(string)                   ! Right-justify
UPPER(string)                   ! Uppercase
LOWER(string)                   ! Lowercase
LEN(string)                     ! Length (excluding trailing spaces)
SIZE(variable)                  ! Size in bytes
INSTRING(find, source, start, count)  ! Find substring (returns position, 0 if not found)
SUB(string, start, length)      ! Substring
FORMAT(value, picture)          ! Format number/date with picture
DEFORMAT(string, picture)       ! Remove formatting
CHR(code)                       ! ASCII code to character
VAL(char)                       ! Character to ASCII code
```

### Numeric Functions
```clarion
INT(real)                       ! Truncate to integer
ROUND(real, decimals)           ! Round
ABS(number)                     ! Absolute value
RANDOM(low, high)               ! Random number in range
```

### System Functions
```clarion
ERRORCODE()                     ! Last error code (0 = success)
ERROR()                         ! Last error message
RECORDS(queue_or_file)          ! Record count
POINTER(queue_or_file)          ! Current position
ADDRESS(variable)               ! Memory address
WHAT(group, n)                  ! Field reference by index
WHO(group, n)                   ! Field name by index
WHERE(group, n)                 ! Field offset by index
CLOCK()                         ! Current time (centiseconds since midnight)
TODAY()                         ! Current date (Clarion standard date)
```

### File/Queue Functions
```clarion
SET(file)                       ! Set file to beginning
SET(key)                        ! Set to beginning of key order
SET(key, key_value)             ! Position at key value
NEXT(file)                      ! Read next record
PREVIOUS(file)                  ! Read previous record
ADD(file)                       ! Add record
PUT(file)                       ! Update current record
DELETE(file)                    ! Delete current record
```

## GROUP Declarations

```clarion
AddressGroup     GROUP,TYPE
Street               STRING(50)
City                 STRING(30)
State                STRING(2)
Zip                  STRING(10)
                 END

! Use with LIKE
CustomerAddress  LIKE(AddressGroup)
```

## INCLUDE and OMIT Directives

```clarion
INCLUDE('MyHeader.inc'),ONCE         ! Include once (header guard)
INCLUDE('equates.clw'),ONCE

OMIT('_EndOfInclude_',_MySymbol_)    ! Omit block if symbol defined
! ... code to conditionally omit ...
_EndOfInclude_

COMPILE('_EndCompile_',_MySymbol_)   ! Compile block if symbol defined
! ... code to conditionally compile ...
_EndCompile_
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

## Best Practices

### Naming Conventions
- PascalCase for procedures: `CalculateTotal`
- Local variables often start with lowercase: `counter`, `index`
- Module/global variables often PascalCase: `GlobalErrors`, `INIMgr`
- Control IDs prefixed with `?`: `?ButtonSave`, `?OLE`

### Code Organization
- Use `MAP/END` for procedure declarations
- Use `CODE` section for executable code
- Use proper indentation (2-4 spaces)

### String Concatenation
```clarion
Result = 'String1' & 'String2' & Variable
```

### Alignment for Readability
Clarion developers often align assignments:
```clarion
MyCOMCtrl{'Title'}      = 'Meeting Invitation'
MyCOMCtrl{'Subtitle'}   = 'Tomorrow 2:00 PM'
MyCOMCtrl{'Message'}    = 'Please RSVP'
MyCOMCtrl{'Type'}       = 0
```

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

## Template Authoring Gotchas

These rules apply when writing Clarion templates (`.tpl` / `.tpw`), not when writing Clarion source code. Both gotchas fail silently or in confusing ways — there is no compiler warning that tells you what's wrong.

### `#AT` directives cannot be nested inside `#IF` blocks

`#AT` registers a code-generation point with the template engine; the registration must be unconditional from the parser's view. Conditional logic belongs INSIDE the body the generator emits, not around the `#AT` itself.

❌ **Wrong — parser rejects this with `#ENDIF expected` / `Mismatched End`:**
```
#IF(%MyFlag <> '')
#AT(%SomeEmbed),PRIORITY(500)
   ...code...
#ENDAT
#ENDIF
```

✅ **Right — invert the nesting (`#IF` goes INSIDE the `#AT` body):**
```
#AT(%SomeEmbed),PRIORITY(500)
#IF(%MyFlag <> '')
   ...code...
#ENDIF
#ENDAT
```

When the condition is false, the inner `#IF` skips the body and the `#AT` emits nothing — same net effect as the broken form, but parser-legal.

### `OMITTED()` only works in the scope where the parameter is declared

`OMITTED(name)` resolves the name against the **current method's** parameter list, not against the enclosing procedure's parameter list. Inside ABC class methods declared within a procedure (e.g. `ThisWindow.Init`, `ThisWindow.TakeEvent`), `OMITTED(pSomeParam)` returns 1 (TRUE = omitted) even when the caller passed a real value — because `TakeEvent()` has no parameter named `pSomeParam`. The parameter VALUE is visible from the nested method (procedure-locals are accessible), but the OMITTED bitfield is not.

The compiler accepts the syntax silently and emits code that reads from the wrong place. The only signal is bizarre runtime behavior.

**Fix:** Stash the params at procedure top-level (where `OMITTED` works correctly) into procedure-local data variables, then check those locals from class methods.

For ABC Window procedures, the right embed is `%BeforeWindowManagerRun`. It's declared in `template/win/ABWINDOW.TPW` (HIDE-flagged but `#AT`-targetable), generated inside the procedure's main `CODE` block immediately before `GlobalResponse = ThisWindow.Run()`:

```
#AT(%BeforeWindowManagerRun),PRIORITY(500)
#IF(%FileNameParam <> '')
IF OMITTED(%FileNameParam) = 0; LocalStashFile = %FileNameParam; END
#IF(%STParam <> '')
IF OMITTED(%STParam) = 0;       LocalStashST  &= %STParam;       END
#ENDIF
#ENDIF
#ENDAT
```

Then from class methods (e.g. an event handler):

```clarion
IF CLIP(LocalStashFile) <> ''        ! filename was passed
  ...
  IF NOT (LocalStashST &= NULL)      ! ST ref was passed
    ...
```

**Two embed names that LOOK right but DON'T work for ABC procedures:**

- `%LocalProcedureSetup` — not a real embed at all. Parses silently and emits nothing.
- `%ProcedureSetup` — declared with the `LEGACY` flag, so `#AT` parses but emits nothing for ABC procedures (only fires for the Legacy family).

**Bonus — silence the "Unusual type conversion" warning** by writing `IF OMITTED(x) = 0` instead of `IF NOT OMITTED(x)`. Same logic, no warning.

## When Generating Clarion Examples

1. **Always use correct syntax**: Single quotes for strings, `!` for comments
2. **Align assignments** for readability when showing property settings
3. **Use realistic variable names** following Clarion conventions
4. **Show complete context** - don't assume variables are declared elsewhere
5. **Comment complex sections** using `!` prefix
6. **Use proper indentation** (consistent spacing)
7. **For COM examples**: Show the full pattern including control creation
8. **Property names are case-sensitive** in COM calls - use exact names from interface

## Common Mistakes to Avoid

### Strings
❌ Using double quotes: `Message = "Hello"`
✅ Single quotes only: `Message = 'Hello'`

❌ Missing embedded quote doubling: `Message = 'Don't do this'`
✅ Double the quote: `Message = 'Don''t do this'`

### Statement Termination
❌ Adding periods to end statements (this is NOT C/Pascal):
```clarion
Message = 'Hello'.
OPEN(Window).
```
✅ No periods — statements end at newline:
```clarion
Message = 'Hello'
OPEN(Window)
```
**Only exception:** Single-line IF: `IF x > 0 THEN RETURN.`

### Labels and Indentation
❌ Indenting labels (procedure names, variable declarations):
```clarion
  MyVariable    LONG              ! WRONG — label indented
  MyProc        PROCEDURE         ! WRONG — label indented
```
✅ Labels start in column 1:
```clarion
MyVariable    LONG                ! CORRECT — column 1
MyProc        PROCEDURE           ! CORRECT — column 1
                CODE              ! CODE is indented
```

### .clw File Structure
❌ Missing MEMBER or wrong order:
```clarion
INCLUDE('MyClass.inc'),ONCE
MEMBER                            ! WRONG — MEMBER must be FIRST
```
✅ MEMBER first, then MAP, then INCLUDE:
```clarion
                     MEMBER
                     MAP
                     END
    INCLUDE('MyClass.inc'),ONCE
```

### PROCEDURE Declarations
❌ Using parentheses for parameterless procedures:
```clarion
MyProc    PROCEDURE()             ! WRONG — no empty parens
```
✅ No parentheses when no parameters:
```clarion
MyProc    PROCEDURE               ! CORRECT
```

❌ Putting CODE on the same line as PROCEDURE:
```clarion
MyProc    PROCEDURE CODE          ! WRONG
```
✅ CODE on its own indented line, after local variable declarations:
```clarion
MyProc    PROCEDURE
LocalVar      LONG
  CODE
  ! code here
```

### END Statements
❌ Missing END for block structures:
```clarion
IF condition
  DoSomething()
                                  ! WRONG — no END
```
✅ Every IF, LOOP, CASE, ACCEPT, etc. needs END:
```clarion
IF condition
  DoSomething()
END
```

❌ Single END for WINDOW:
```clarion
Window    WINDOW('Title'),AT(,,400,300)
            BUTTON('Click'),USE(?Button1)
          END                     ! WRONG — only one END
```
✅ Two ENDs — first closes controls, second closes window:
```clarion
Window    WINDOW('Title'),AT(,,400,300)
            BUTTON('Click'),USE(?Button1)
            END                   ! Closes control list
          END                     ! Closes WINDOW structure
```

### Reference vs Assignment
❌ Using = for reference assignment:
```clarion
MyRef = MyObject                  ! WRONG — copies value, doesn't assign reference
MyRef = NULL                      ! WRONG — can't assign NULL with =
```
✅ Use &= for references:
```clarion
MyRef &= MyObject                 ! CORRECT — assigns reference
MyRef &= NULL                     ! CORRECT — clears reference
```

### QUEUE Operations
❌ Forgetting to CLEAR before ADD:
```clarion
MyQueue.Name = 'Test'
ADD(MyQueue)                      ! WRONG — other fields have garbage
```
✅ CLEAR the buffer first:
```clarion
CLEAR(MyQueue)
MyQueue.Name = 'Test'
ADD(MyQueue)                      ! CORRECT — clean record
```

❌ Using SORT with wrong syntax:
```clarion
SORT(MyQueue, 'Name')             ! WRONG — string field name
```
✅ Use field references with +/- prefix:
```clarion
SORT(MyQueue, +MyQueue.Name)      ! CORRECT — ascending by Name
SORT(MyQueue, -MyQueue.Value, +MyQueue.Name)  ! Multiple sort keys
```

### Parameter Syntax
❌ Using & for reference parameters in declaration:
```clarion
MyProc    PROCEDURE(&STRING xName)  ! WRONG — & is not for params
```
✅ Use * for reference parameters:
```clarion
MyProc    PROCEDURE(*STRING xName)  ! CORRECT — * means by reference
```

❌ Using ? for omittable parameters:
```clarion
MyProc    PROCEDURE(?STRING xOpt)   ! WRONG
```
✅ Use angle brackets:
```clarion
MyProc    PROCEDURE(<STRING xOpt>)  ! CORRECT — omittable
```

### ACCEPT Loop
❌ Using LOOP for event processing:
```clarion
LOOP
  CASE ACCEPTED()                 ! WRONG — old/simplified pattern
  END
END
```
✅ Use ACCEPT for window event processing:
```clarion
ACCEPT
  CASE EVENT()
  OF EVENT:Accepted
    CASE FIELD()
    OF ?MyButton
      ! handle
    END
  END
END
```

### ROUTINE Calls
❌ Calling ROUTINE like a procedure:
```clarion
MyRoutine()                       ! WRONG — routines aren't procedures
MyRoutine                         ! WRONG — this calls a PROCEDURE
```
✅ Use DO:
```clarion
DO MyRoutine                      ! CORRECT
```

### CLASS Method Implementation
❌ Missing class prefix in .clw:
```clarion
Init    PROCEDURE                 ! WRONG — which class?
  CODE
```
✅ Always prefix with ClassName:
```clarion
MyClass.Init    PROCEDURE         ! CORRECT
  CODE
```

### COM/OLE Property Syntax
❌ Using PROP:OLE assignment for methods:
```clarion
ctrl{PROP:OLE} = 'MethodName(param)'   ! WRONG — unreliable
```
✅ Use direct brace syntax:
```clarion
ctrl{'MethodName("' & param & '")'} ! CORRECT
```

### RETURN in Procedures
❌ Missing RETURN at end of procedure (for procedures with return type):
```clarion
MyFunc    PROCEDURE,STRING
  CODE
  IF condition
    RETURN 'yes'
  END
  ! Falls through with no return — WRONG
```
✅ Always have a RETURN path:
```clarion
MyFunc    PROCEDURE,STRING
  CODE
  IF condition
    RETURN 'yes'
  END
  RETURN ''                       ! CORRECT — always returns
```

### NEW/DISPOSE
❌ Forgetting DISPOSE (memory leak):
```clarion
MyObj &= NEW MyClass
MyObj.DoWork()
! WRONG — never disposed
```
✅ Always DISPOSE what you NEW:
```clarion
MyObj &= NEW MyClass
MyObj.DoWork()
DISPOSE(MyObj)                    ! CORRECT
```
