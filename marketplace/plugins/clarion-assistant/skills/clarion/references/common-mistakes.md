# Common Clarion Mistakes to Avoid

Full catalog of wrong/right patterns. The most frequent ones are summarized in SKILL.md; this is the complete list with examples.

## Strings
❌ Using double quotes: `Message = "Hello"`
✅ Single quotes only: `Message = 'Hello'`

❌ Missing embedded quote doubling: `Message = 'Don't do this'`
✅ Double the quote: `Message = 'Don''t do this'`

## Statement Termination
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

## Labels and Indentation
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

## .clw File Structure
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

## PROCEDURE Declarations
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

## END Statements
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

## Reference vs Assignment
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

## QUEUE Operations
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

## Parameter Syntax
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

## ACCEPT Loop
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

## ROUTINE Calls
❌ Calling ROUTINE like a procedure:
```clarion
MyRoutine()                       ! WRONG — routines aren't procedures
MyRoutine                         ! WRONG — this calls a PROCEDURE
```
✅ Use DO:
```clarion
DO MyRoutine                      ! CORRECT
```

## CLASS Method Implementation
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

## DECIMAL Parameters and Returns
❌ `MyProc PROCEDURE(DECIMAL pV)` → `Invalid data type for value parameter`
❌ `MyProc PROCEDURE(),DECIMAL` → `Illegal return type or attribute`
✅ `MyProc PROCEDURE(*DECIMAL pV),REAL` — by reference, and return REAL/LONG (assign to the DECIMAL at the call site).

## Indented Data Declarations
❌ `  MyVar LONG` (indented) → `Illegal data type: MYVAR`
✅ `MyVar LONG` in column 1; only `CODE` and executable statements are indented.

## MODULE Placement and Naming
❌ `MODULE('M1')` outside a `MAP` → `Expected: <ID> <LINEBREAK> ; CODE INCLUDE OMIT ...`
✅ Put every `MODULE` inside `MAP ... END`, and make its name match the .clw that defines the procedure (a mismatch gives `Procedure doesn't belong to module`).

## Returning Procedures Called as Statements
❌ `GetVal('a')` as a statement when `GetVal PROCEDURE(STRING),LONG` has no `,PROC`.
This compiles, but with the warning `Calling function as procedure`.
✅ Add `,PROC` to the prototype, or assign the result. Assign `GETINI(...)`'s result (it has no `,PROC`).

## No POWER()
❌ `D = POWER(2, n)` → `Unknown function label`
✅ `D = (2) ^ (n)`

## Redeclaring Globals in a MEMBER
❌ `Flag BYTE,EXTERNAL` in a MEMBER file for a PROGRAM global → `Label duplicated` warning, then `Unresolved External` at link time.
✅ Just use `Flag` — MEMBER modules see the PROGRAM's global data.

## Class Construct
❌ `Construct PROCEDURE(LONG p)` or `Construct PROCEDURE,VIRTUAL` → `Illegal return type or attribute`
✅ `Construct`/`Destruct` take no parameters and are not VIRTUAL.

## COM/OLE Property Syntax
❌ Using PROP:OLE assignment for methods:
```clarion
ctrl{PROP:OLE} = 'MethodName(param)'   ! WRONG — unreliable
```
✅ Use direct brace syntax:
```clarion
ctrl{'MethodName("' & param & '")'} ! CORRECT
```

## RETURN in Procedures
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

## NEW/DISPOSE
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

## MEMBER Without the Parent
❌ Bare `MEMBER` in a module implementing procedures prototyped in the parent program's MAP:
```clarion
                     MEMBER                  ! WRONG here — "No matching prototype available"
```
✅ Name the parent:
```clarion
                     MEMBER('MyApp.clw')
```
(Bare `MEMBER` remains correct in a class-implementation .clw.)