# Clarion Syntax Basics

Core syntax rules, file structure, parameter passing, routines, directives, and coding conventions.

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

## Reserved Words and Keywords

Clarion has reserved words that cannot be used as identifiers (variable names, column names, table names, procedure parameters, etc.). Using reserved words as identifiers will cause compilation errors or unexpected behavior.

### Strictly Reserved Keywords
**These keywords are reserved and may NOT be used as labels for any purpose:**

`ACCEPT`, `AND`, `ASSERT`, `BEGIN`, `BREAK`, `BY`, `CASE`, `CATCH`, `CHOOSE`, `CODE`, `COMPILE`, `CONST`, `CYCLE`, `DATA`, `DO`, `ELSE`, `ELSIF`, `END`, `EXECUTE`, `EXIT`, `FINALLY`, `FUNCTION`, `GOTO`, `IF`, `INCLUDE`, `LOOP`, `MEMBER`, `NEW`, `NOT`, `NULL`, `OF`, `OMIT`, `OR`, `OROF`, `PRAGMA`, `PROCEDURE`, `PROGRAM`, `RETURN`, `ROUTINE`, `SECTION`, `THEN`, `THROW`, `TIMES`, `TO`, `TRY`, `UNTIL`, `WHILE`, `XOR`

### Data Structure Keywords
**These keywords may be used as labels of data structures or executable statements, but may NOT be the label of any PROCEDURE statement:**

`APPLICATION`, `CLASS`, `DETAIL`, `FILE`, `FOOTER`, `FORM`, `GROUP`, `HEADER`, `ITEM`, `ITEMIZE`, `JOIN`, `MAP`, `MENU`, `MENUBAR`, `MODULE`, `OLE`, `OPTION`, `QUEUE`, `PARENT`, `RECORD`, `REPORT`, `SELF`, `SHEET`, `TAB`, `TOOLBAR`, `VIEW`, `WINDOW`

**IMPORTANT:** `SELF` and `PARENT` cannot name local variables or parameters of any class or interface method.

### Best Practices
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

## When Generating Clarion Examples

1. **Always use correct syntax**: Single quotes for strings, `!` for comments
2. **Align assignments** for readability when showing property settings
3. **Use realistic variable names** following Clarion conventions
4. **Show complete context** - don't assume variables are declared elsewhere
5. **Comment complex sections** using `!` prefix
6. **Use proper indentation** (consistent spacing)
7. **For COM examples**: Show the full pattern including control creation
8. **Property names are case-sensitive** in COM calls - use exact names from interface
