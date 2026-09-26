# Clarion Data Structures and Built-in Functions

FILE/RECORD/KEY declarations, file I/O, QUEUE operations, GROUP declarations, and built-in functions.

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
