# Recipes

Each recipe below was built from authored text and compiled to a running `.exe`.

## Browse

A browse is a `FROM ABC Window` procedure plus three cooperating parts:

**1. The file binding**

```
[FILES]
[PRIMARY]  <TableName>
[INSTANCE] 1
[KEY]      <PREFIX:KeyName>
```

**2. The control template**

```
[ADDITION]
NAME ABC BrowseBox
[INSTANCE]
INSTANCE 1
PROCPROP
```

`[ADDITION]` takes no `[END]` and **must precede `[WINDOW]`**.

**3. The LIST control, bound back to instance 1**

```
LIST,AT(...),USE(?Browse:1),HVSCROLL,FORMAT('...'),FROM(Queue:Browse:1),IMM,
     #FIELDS(CON:ID,CON:Name,CON:Email),#ORIG(?List),#SEQ(1)
```

`#ORIG(?List)` + `#SEQ(1)` are the binding between the LIST and BrowseBox instance 1, and are **required** for control-template controls.

Generates the full ABC stack: `VIEW(Contact)`, `Queue:Browse:1`, `BRW1 CLASS(BrowseClass)`, `StepLocatorClass`, `Relate:Contact.Open()`, `BRW1.Init(...)`.

**Result:** a 1,216-byte TXA plus a 1,933-byte `.dctx` → a 533,644-byte ABC browse app.

## Browse → Form (CRUD)

Add a second addition to the browse:

```
[ADDITION]
NAME ABC BrowseUpdateButtons
[INSTANCE]
INSTANCE 2
PARENT 1
PROCPROP
[PROMPTS]
%UpdateProcedure PROCEDURE  (UpdateContact)
```

- **`PARENT 1`** binds the buttons to BrowseBox instance 1.
- **`%UpdateProcedure` is one of the few prompts that MUST be supplied** — nothing else names the form.
- The Insert/Change/Delete buttons in `[WINDOW]` carry `#SEQ(2)`, matching this addition's instance.

The form is `FROM ABC Window` with `CATEGORY 'Form'`, its own `[FILES]` (**`[INSTANCE] 1`** — SaveButton is instance 1), and additions `ABC SaveButton` and `ABC CancelButton`.

**Watch the instance numbers.** Copying a shipped three-table example's pattern (browse=1, form=2) onto a single-table dictionary makes the form's file resolve to nothing. Both procedures use `[INSTANCE] 1`; table count is irrelevant.

**Result:** a 2,522-byte TXA → a 556,280-byte `.exe` with working browse, insert, change, delete.

## Application frame + MDI

The frame is `FROM ABC Frame` with `CATEGORY 'Frame'`, and uses an **`APPLICATION`** window, not `WINDOW`.

It needs **no `[ADDITION]` blocks at all**. Menu items call procedures purely through prompts:

```
[PROMPTS]
%ButtonAction DEPEND %Control DEFAULT TIMES 1
WHEN  ('?BrowseContact') ('Call a Procedure')

%ButtonProcedure DEPEND %Control PROCEDURE TIMES 1
WHEN  ('?BrowseContact') (BrowseContact)

%ButtonThread DEPEND %Control LONG TIMES 1
WHEN  ('?BrowseContact') (1)
```

- The ITEM's USE equate (`?BrowseContact`) is the join across the prompt families.
- **Blank lines separate families** — they are significant.
- This is a rare case where **prompts are mandatory**; nothing else names the target procedure. Supply only the entries you need; other controls fall back to defaults.
- Child procedures need **`MDI`** on their `WINDOW`.

Generates `OF ?BrowseContact` / `START(BrowseContact, 25000)`.

**Result:** a 3,726-byte TXA → a 575,172-byte `.exe` with frame, menu, MDI browse and update form.

## Report

`FROM ABC Report`, with sections in this order:

```
[FILES]     [INSTANCE] 0      <- reports have no control template to own the file
[REPORT]
[WINDOW]                      <- the PROGRESS window — MANDATORY
```

**The `[WINDOW]` is not optional.** It is the progress window, and omitting it makes codegen assert `progress controls use variable not found` (`abprocs.tpw:577`). Copy it verbatim from `ABREPORT.TPW`'s `#DEFAULT` block: `PROGRESS,USE(Progress:Thermometer)` plus `?Progress:UserString`, `?Progress:PctText`, `?Progress:Cancel`.

No shipped example app contains a report, so the ABC template `#DEFAULT` block is the only canonical source.

## Parent-child browse (range limit)

Three things, **each silent when wrong**:

**1. Use the FLAT prompt family on the BrowseBox addition:**

```
%RangeField      COMPONENT  (ORD:CustomerID)
%RangeLimitType  DEFAULT    ('Single Value')
%RangeLimit      FIELD      (CUS:ID)
```

Not `%SortRangeField` / `%SortRangeLimitType` / `%SortRangeLimit` — those are the per-sort-order *conditional* variants and stay empty in a normal browse.

**2. List the PARENT file in `[FILES]` under `[OTHERS]`.** Without it the value silently resolves to nothing: `BRW1.AddRange(ORD:CustomerID,)` — which compiles, runs, and never filters. Correct output is `BRW1.AddRange(ORD:CustomerID,CUS:ID)`.

**3. Obey the `[PROMPTS]` placement rule** — they belong to the `[ADDITION]` they follow.

**Verify by grepping the generated source for `AddRange(`.** The missing range limit compiled and ran cleanly for three iterations before anyone noticed.

### Known open issue: `%SortOrder MULTI`

The `%SortOrder` MULTI family is silently dropped when supplied piecemeal — `/ax` returns `%SortOrder MULTI LONG ()` with all ~20 members at `TIMES 0`. Type tokens and blank-line separation have both been ruled out as the cause. Note that an IDE-authored app with a **working** range limit also exports `%SortOrder MULTI LONG ()` with members at `TIMES 0`, so the export is not evidence either way. Use the flat `%Range*` family above.
