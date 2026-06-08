---
name: stringtheory
# prettier-ignore
description: CapeSoft StringTheory patterns and gotchas for Clarion. Covers BLOB/MEMO field handling, common method confusions, and storage-aware serialization. Auto-applies whenever StringTheory (st, stringtheory) is referenced in Clarion code or templates.
version: 1.0.0
---

# CapeSoft StringTheory Skill for Clarion

You are an expert in CapeSoft StringTheory. StringTheory is a Clarion class that wraps a string buffer and provides parsing, formatting, and I/O methods. Many of its methods only operate on its own internal buffer — they do NOT understand Clarion field storage indirection (BLOB / MEMO).

## Critical Rule: BLOB and MEMO fields require FromBlob / ToBlob

Clarion BLOB fields use storage indirection — the field label is a handle, not the data. StringTheory's `SetValue` / `GetValue` only touch the StringTheory object's internal buffer, so when used against a BLOB they silently no-op: code compiles, runtime throws nothing, but data never lands in (or comes out of) the blob backing store.

```clarion
! WRONG — silent no-op for BLOB / MEMO fields
st.SetValue(File:BlobField)         ! does NOT load from blob
File:BlobField = st.GetValue()      ! does NOT save to blob

! CORRECT — blob-aware methods
st.FromBlob(File:BlobField)         ! load FROM blob into st
st.ToBlob(File:BlobField)           ! save st INTO blob
```

**When to use which:**

| Field type           | Load into st            | Save from st            |
|----------------------|-------------------------|-------------------------|
| STRING / CSTRING     | `st.SetValue(field)`    | `field = st.GetValue()` |
| BLOB                 | `st.FromBlob(field)`    | `st.ToBlob(field)`      |
| MEMO                 | `st.FromBlob(field)`    | `st.ToBlob(field)`      |
| File on disk         | `st.LoadFile('path')`   | `st.SaveFile('path')`   |

If the target is a BLOB or MEMO declared in a FILE structure, you MUST use `FromBlob` / `ToBlob`. Never emit `SetValue` / `GetValue` for those targets — it's a silent-fail anti-pattern.

## Critical Rule: Methods that overwrite the receiver's value are destructive

Several StringTheory methods replace the object's internal value as a side effect — even though their names read like pure "produce a value" calls. Calling one of these on an ST instance that's holding a payload destroys the payload silently. Code review misses it because the call sites look reasonable in isolation.

### `MakeGuid()` is destructive — this is the surprising one

`st.MakeGuid()` generates a GUID, **assigns it to `st`'s value**, AND returns it. Any prior content (PDF binary, JSON, file text) in `st` is gone after the call.

❌ **Wrong — `stanno` holds JSON that's about to be destroyed:**
```clarion
stanno.SetValue(PDFViewerCOM.Parm1.GetValue())    ! stanno = JSON payload
stpdf.SetValue(PDFViewerCOM_Ctrl{'SourceBase64'})
stpdf.Base64Decode()

Att:Guid = stanno.MakeGuid()                      ! BUG: stanno value is now the GUID — JSON destroyed
stpdf.ToBlob(Att:FBlob)
Ano:Guid   = stanno.MakeGuid()                    ! second MakeGuid, still on stanno
Ano:Father = Att:Guid
stanno.ToBlob(Ano:AnnotJsonField)                 ! writes the GUID string into the JSON blob field
```

Symptom: the database column contains a GUID string instead of the JSON. Downstream load fails silently or crashes the importer.

✅ **Right — use a dedicated ST instance just for GUID generation:**
```clarion
stguid    StringTheory                            ! dedicated ST, never holds payloads
...
Att:Guid = stguid.MakeGuid()                      ! safe — stguid has no payload to lose
stpdf.ToBlob(Att:FBlob)
Ano:Guid   = stguid.MakeGuid()
Ano:Father = Att:Guid
stanno.ToBlob(Ano:AnnotJsonField)                 ! stanno still holds the JSON
```

### Detection heuristic

In a single procedure or routine, if `<x>.MakeGuid()` appears and is followed by any of these on the same instance without an intervening re-assignment, it's almost certainly a bug:

- `<x>.ToBlob(...)`
- `<x>.GetValue()`
- `<x>.Base64Encode()` / `<x>.Base64Decode()`
- `<x>.Length()`
- `<x>.SaveFile(...)`

A "re-assignment" means one of: `<x>.SetValue(...)`, `<x>.LoadFile(...)`, `<x>.FromBlob(...)`. If those don't appear between the `MakeGuid` and the read, the read is fetching the GUID string, not the payload the surrounding code thinks is there.

### Other receiver-mutating methods to watch

The same destructive pattern applies to anything that writes the instance's value:

- `Random()` — fills with random bytes
- `LoadFile(path)` — replaces value with file contents
- `Base64Decode()` / `Base64Encode()` — replaces value with the decoded/encoded form
- `Decrypt(key)` / `Encrypt(key)` — replaces value with the plaintext/ciphertext

`MakeGuid` is the most surprising because the name suggests "produce a GUID" without hinting at the side effect on `self`. Treat it as a setter, not a getter.

## Quick Reference: Common Method Pairings

- `SetValue(string)` / `GetValue()` — string buffer ↔ Clarion STRING
- `FromBlob(blobField)` / `ToBlob(blobField)` — Clarion BLOB/MEMO ↔ string buffer
- `LoadFile(path)` / `SaveFile(path)` — disk file ↔ string buffer
- `Append(s)` / `Prepend(s)` — concat without reassign
- `Clip()` / `Trim()` — strip trailing / leading+trailing spaces
- `Length()` — current buffer length

## When generating templates or code that touches a Clarion file field

Before emitting StringTheory I/O against any field, check the field's declaration:

1. STRING / CSTRING → `SetValue` / `GetValue`
2. BLOB / MEMO → `FromBlob` / `ToBlob`

If you can't determine the type, ask — don't guess. A wrong guess on a BLOB compiles cleanly and breaks at runtime with no error.
