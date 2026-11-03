# Clarion Syntax Basics

Data types, procedure and control-structure shapes, reserved words, file layout, parameters, routines and directives. The critical rules in SKILL.md (quotes, no periods, column 1, END, `&=`, parameter markers, `DO`) are not repeated here.

## Data Types

```clarion
MyString        STRING(100)                ! Fixed length, space-padded
MyCString       CSTRING(101)               ! Null-terminated: 100 characters + the null
MyNumber        LONG
```

- `BYTE` (8-bit unsigned), `SHORT` (16-bit signed), `LONG` (32-bit signed)
- `REAL` (8-byte floating point), `SREAL` (4-byte)
- `DECIMAL(digits,decimals)` - packed decimal; see ai-pitfalls.md for its parameter and return limits
- `DATE`, `TIME` - date and time types

## Procedures and Control Structures

```clarion
MyProc    PROCEDURE
Counter       LONG                         ! Local data before CODE
  CODE
  IF Counter > 0
    ! ...
  ELSIF Counter < 0
    ! ...
  ELSE
    ! ...
  END
  CASE Counter
  OF 1
  OROF 2                                   ! Either value
    ! ...
  ELSE
    ! ...
  END
  LOOP Counter = 1 TO 10
    IF Counter = 5 THEN CYCLE.
    IF Counter = 8 THEN BREAK.
  END
  RETURN
```

## Reserved Words

**Never usable as a label:**

`ACCEPT`, `AND`, `ASSERT`, `BEGIN`, `BREAK`, `BY`, `CASE`, `CATCH`, `CHOOSE`, `CODE`, `COMPILE`, `CONST`, `CYCLE`, `DATA`, `DO`, `ELSE`, `ELSIF`, `END`, `EXECUTE`, `EXIT`, `FINALLY`, `FUNCTION`, `GOTO`, `IF`, `INCLUDE`, `LOOP`, `MEMBER`, `NEW`, `NOT`, `NULL`, `OF`, `OMIT`, `OR`, `OROF`, `PRAGMA`, `PROCEDURE`, `PROGRAM`, `RETURN`, `ROUTINE`, `SECTION`, `THEN`, `THROW`, `TIMES`, `TO`, `TRY`, `UNTIL`, `WHILE`, `XOR`

**Usable as the label of data or a statement, but not of a PROCEDURE:**

`APPLICATION`, `CLASS`, `DETAIL`, `FILE`, `FOOTER`, `FORM`, `GROUP`, `HEADER`, `ITEM`, `ITEMIZE`, `JOIN`, `MAP`, `MENU`, `MENUBAR`, `MODULE`, `OLE`, `OPTION`, `QUEUE`, `PARENT`, `RECORD`, `REPORT`, `SELF`, `SHEET`, `TAB`, `TOOLBAR`, `VIEW`, `WINDOW`

## File Layout

### .clw (implementation)
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

### .inc (declarations)
```clarion
MyClass    CLASS,TYPE,MODULE('MyClass.clw'),LINK('MyClass.clw')
Q                &MyQueue
Init             PROCEDURE
Kill             PROCEDURE
Process          PROCEDURE(STRING xParam),STRING,PROC
           END
```

**CLASS attributes:** `TYPE` (can be used as a type), `MODULE()` (implementation file), `LINK()` (link this file), `IMPLEMENTS()`, `PROTECTED`, `PRIVATE`, `VIRTUAL`

## Parameters

```clarion
! Prototype (in a MAP or CLASS)
MyProc    PROCEDURE(STRING xName, *LONG xCount, <STRING xOpt>),STRING,PROC

! Implementation
MyProc    PROCEDURE(STRING xName, *LONG xCount, <STRING xOpt>)
  CODE
  xCount += 1                              ! *LONG changes the caller's variable
  IF OMITTED(xOpt) = 0                     ! The omittable parameter was passed
    RETURN CLIP(xName) & ' ' & CLIP(xOpt)
  END
  RETURN xName
```

## ROUTINE

```clarion
MyProc    PROCEDURE
Counter       LONG
  CODE
  DO InitData
  RETURN

InitData  ROUTINE
  Counter = 0
```

## INCLUDE, OMIT and COMPILE

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

## Naming

- PascalCase procedures (`CalculateTotal`) and globals (`GlobalErrors`); control IDs start with `?` (`?ButtonSave`).
- Align runs of assignments:
```clarion
MyCOMCtrl{'Title'}      = 'Meeting Invitation'
MyCOMCtrl{'Message'}    = 'Please RSVP'
```
