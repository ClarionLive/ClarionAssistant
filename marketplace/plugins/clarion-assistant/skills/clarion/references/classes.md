# Clarion CLASS Methods, Inheritance, and References

CLASS declaration/implementation, inheritance, and reference (pointer) variables.

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
