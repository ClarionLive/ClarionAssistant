---
name: jfiles
# prettier-ignore
description: jFiles JSON serialization patterns for Clarion. Queue-to-JSON, NAME attributes for case control, TagCase settings, field trimming, loading queues inside groups (NewPointer), and common gotchas. Auto-applies when working with jFiles or JSON serialization in Clarion code.
version: 1.1.0
---

# jFiles JSON Serialization Skill for Clarion

You are an expert in using CapeSoft jFiles to serialize and deserialize JSON in Clarion applications. jFiles requires StringTheory 3 and CapeSoft Reflection.

## Critical Rules

### 1. Field Name Case - Always Use NAME Attributes

Clarion is case-insensitive and defaults field names to ALL CAPS in JSON output. **Always add `NAME` attributes** to queue/group fields to control the exact JSON key case.

```clarion
! WRONG - jFiles will output "ID", "CATEGORYGUID", "INSURER"
polQ  QUEUE
Id          STRING(20)
CategoryGUID STRING(10)
Insurer     STRING(255)
      END

! CORRECT - JSON keys will match the NAME attribute exactly
polQ  QUEUE
Id          STRING(20),NAME('Id')
CategoryGUID STRING(10),NAME('CategoryGUID')
Insurer     STRING(255),NAME('Insurer')
      END
```

### 2. SetTagCase - Use jf:CaseAsIs for Mixed Case

Always set TagCase before Save when you need mixed-case JSON keys:

```clarion
json.Start()
json.SetTagCase(jf:CaseAsIs)    ! Preserves NAME attribute case exactly
json.Save(myQueue, stBuffer, , jf:noformat)
```

Available options:
- `jf:CaseAsIs` - uses NAME attribute case exactly (use this for APIs/COM controls)
- `jf:CaseLower` - forces all lowercase
- `jf:CaseUpper` - forces all uppercase
- `jf:CaseAny` - case insensitive for loads; saves use NAME case if set, otherwise UPPERCASE

### 3. String Trimming - Always CLIP(LEFT()) Before Storing

jFiles outputs string values as-is from the Clarion field. Clarion strings are fixed-width and right-padded with spaces. Numeric FORMAT() results are right-justified. **Always trim values when populating queue fields.**

```clarion
! WRONG - FORMAT(@n3) produces "  1" (right-justified), STRING(10) pads to "  1       "
! jFiles outputs: "CategoryGUID":"  1"
polQ.CategoryGUID = FORMAT(POl:PolicyCategoryGUID, @n3)

! CORRECT - CLIP(LEFT()) removes leading/trailing spaces
! jFiles outputs: "CategoryGUID":"1"
polQ.CategoryGUID = CLIP(LEFT(FORMAT(POl:PolicyCategoryGUID, @n3)))

! For string fields from file buffers:
polQ.Insurer = CLIP(LEFT(POl:Insurer))
```

This is especially critical when JSON keys must match between different parts of the output (e.g., category ID in records must match category ID keys in a lookup object).

### 4. BLOB Fields Require FromBlob / ToBlob

If the source/target is a Clarion BLOB field, never use `st.SetValue(File:Blob)` / `File:Blob = st.GetValue()`: a BLOB can't be assigned as a whole. Use `st.FromBlob(File:Blob)` to load and `st.ToBlob(File:Blob)` to save. A MEMO is assigned like a STRING: `st.SetValue(File:Memo, st:Clip)`. See the `stringtheory` skill for the full BLOB/MEMO reference.

### 5. Date Formatting

Format dates as strings before storing in the queue. Use STRING fields in the queue, not DATE/LONG:

```clarion
! Queue declaration
polQ  QUEUE
Inception   STRING(20),NAME('Inception')
      END

! Populate with formatted date or empty string
IF POl:Inception <> 0
    polQ.Inception = FORMAT(POl:Inception, @d17)
END
! If 0, the STRING field stays blank - jFiles outputs: "Inception":""
```

### 6. Loading a Queue Inside a Group (or Queue) Requires a Derived NewPointer

A `&QueueType` field inside a GROUP (or inside another QUEUE) is **not allocated by `Load`**. Unless your JSONClass is derived with a `NewPointer` override that NEWs it, the reference stays NULL and the array is silently dropped: `Load` returns no error, there are just zero records.

```clarion
ModelIdQType   QUEUE,TYPE
id               STRING(128), NAME('id')
               END
ModelListG     GROUP                                   ! {"object":"list","data":[{"id":...},...]}
data             &ModelIdQType, NAME('data | queue')
               END

json           CLASS(JSONClass)                        ! DERIVED - a plain JSONClass will not do
NewPointer       PROCEDURE( *CSTRING pGroupName, *CSTRING pColumnName), DERIVED
               END
st             StringTheory                            ! holds the JSON text
i              LONG

  CODE
  json.Start()
  json.SetTagCase(jf:CaseAsIs)
  json.Load( ModelListG, st )                          ! calls json.NewPointer for 'data'
  IF NOT ModelListG.data &= NULL
     LOOP i = 1 TO RECORDS(ModelListG.data)
        GET(ModelListG.data, i)
        ! ... use ModelListG.data.id ...
     END
  END
  json.DisposeGroup( ModelListG )                      ! frees + disposes the loaded queue
  RETURN

json.NewPointer PROCEDURE( *CSTRING pGroupName, *CSTRING pColumnName)
  CODE
  CASE pGroupName
  OF 'group'                                           ! an unnamed GROUP is reported as 'group'
     CASE pColumnName
     OF 'data'                                         ! the NAME() text before the first '|', case-sensitive
        ModelListG.data &= NEW( ModelIdQType )         ! jFiles CLEARs it and loads it after you return
     END
  END
```

- The method is implemented after the procedure's last ROUTINE. A local derived class's methods can see the procedure's data, so they can NEW `ModelListG.data` directly.
- Which names arrive: `pGroupName` is `'group'` for `json.Load(MyGroup, st)` with no boundary, the boundary name if you pass one (`json.Load(MyGroup, st, 'result')` gives `'result'`), `'queue'` for a top-level queue load, and `parent.column` one level down (a queue inside the records of `data` arrives as `'group.data'`). The GROUP's own `NAME()` does not change it. `pColumnName` is the field's `NAME()` text before the first `|`, not the label. If in doubt, put a debug trace or a breakpoint in `NewPointer`, or call `json.Reflection.Walk()` after the Load.
- The `| queue` suffix is required. It is how jFiles/Reflection classifies the field as a queue reference, both for `Load` (so it asks `NewPointer`) and for `DisposeGroup` (so it finds and disposes the queue). Without it the field is neither loaded nor disposed.
- The CapeSoft `NewPointer` page shows the jFiles 2 one-parameter signature (`String pColumnName`). jFiles 3 (3.03 and 3.08 checked) passes `*CSTRING pGroupName, *CSTRING pColumnName` and, since 3.02, falls back to the one-parameter form if the new one leaves the reference NULL. jFiles 2.x (2.28 ships with Clarion 10) has only the one-parameter form. Declare the override `DERIVED` rather than `VIRTUAL`: a prototype that matches neither parent then fails to compile instead of becoming an unused overload.
- **Save does not need this.** For Save the caller NEWs and fills the queue first (`MyGroup.Q &= NEW(MyQType)`, `ADD(...)`, `json.Save(MyGroup, st)`), then calls `json.DisposeGroup(MyGroup)`.
- `&StringTheory` fields (`NAME('x | StringTheory')`) are allocated by jFiles itself. Only queue references need `NewPointer`.
- When copying a working Load-into-group example from existing code, copy its derived class and `NewPointer` method too. The declarations alone look complete but load nothing.

## Common Patterns

### Queue to JSON Array (via StringTheory buffer)

```clarion
json   &JsonClass
stOut  &StringTheory

  CODE
  json &= NEW JsonClass
  stOut &= NEW StringTheory
  json.Start()
  json.SetTagCase(jf:CaseAsIs)
  json.Save(myQueue, stOut, , jf:noformat)
  ! stOut now contains: [{"Field1":"value1","Field2":"value2"},...]
  DISPOSE(json)
  DISPOSE(stOut)
```

### Save Parameters

```clarion
json.Save(Structure, Buffer, Boundary, Format, Compressed, Loop)
```

- **Structure** - Table, Queue, View, or Group
- **Buffer** - StringTheory object to receive JSON (omit to save to internal object only)
- **Boundary** - Optional wrapper name. Omit for bare array `[...]`, include for named object `{"name":[...]}`
- **Format** - `jf:format` (human readable) or `jf:noformat` (compact)
- **Compressed** - `jf:compressed` for gzip (file output only)
- **Loop** - `jf:loop` (default, iterate all records) or `jf:noloop` (current record only)

### Disable Fields from Output

```clarion
json.Start()
json.SetTagCase(jf:CaseAsIs)
json.DisableField(myQueue, 'InternalId')  ! Exclude from JSON - name is case sensitive
json.Save(myQueue, stOut, , jf:noformat)
```

### Combining Multiple JSON Structures

When the target format requires multiple structures (e.g., categories + records), serialize each part separately and combine with StringTheory:

```clarion
! Serialize records via jFiles
json.Start()
json.SetTagCase(jf:CaseAsIs)
json.Save(polQ, stRec, , jf:noformat)

! Combine with manually-built categories (key-value map not suited for jFiles array output)
stAll.SetValue('{{"categories":' & stCat.GetValue() & ',"records":' & stRec.GetValue() & '}')
```

Note: `{{` in Clarion strings produces a literal `{` character.

### Declaring jFiles in a ROUTINE

Use references with NEW/DISPOSE in ROUTINE DATA sections:

```clarion
MyRoutine  ROUTINE
    DATA
json    &JsonClass
stOut   &StringTheory
myQ     QUEUE
Name        STRING(100),NAME('Name')
Value       LONG,NAME('Value')
        END
    CODE
    json &= NEW JsonClass
    stOut &= NEW StringTheory
    ! ... work ...
    DISPOSE(json)
    DISPOSE(stOut)
    FREE(myQ)
```

## Reflection - Runtime Field Name Control

If you can't add NAME attributes at declaration time, use Reflection at runtime:

```clarion
json.Start()
json.SetTagCase(jf:CaseAsIs)
! Use json.Reflection.Walk after a Save to see what field names jFiles detected
json.Save(myQueue, stOut)
json.Reflection.Walk   ! Outputs field mapping to debug - useful for troubleshooting
```

## Checklist When Using jFiles

1. `NAME` attribute on every queue/group field with the correct case
2. `json.SetTagCase(jf:CaseAsIs)` before Save
3. `CLIP(LEFT())` on all string and formatted numeric values when populating queue fields
4. Pre-format dates as strings in the queue
5. `FREE(queue)` before populating and after done
6. `DISPOSE` all NEW'd references (JsonClass, StringTheory)
7. Loading into a `&Queue` field of a GROUP or QUEUE: a derived JSONClass with a `NewPointer` override that NEWs it (rule 6), then `json.DisposeGroup()` / `json.DisposeQueue()` afterwards
