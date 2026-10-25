---
name: stringtheory
# prettier-ignore
description: CapeSoft StringTheory gotchas for Clarion (BLOB/MEMO, value-overwriting methods, padded arguments, return values, Base64/UTF-8/URL encoding, memory ownership). Auto-applies whenever StringTheory (st, stringtheory) is referenced in Clarion code or templates.
version: 1.5.0
---

# CapeSoft StringTheory for Clarion

Verified against StringTheory 3.90 (`StringTheory.clw` / `.inc`). Other versions may differ; when in doubt, resolve `StringTheory.clw` through the .red and read the method.

## 1. File fields

| Field | Load | Save |
|---|---|---|
| STRING / CSTRING / MEMO | `st.SetValue(field, st:Clip)` | `field = st.GetValue()` |
| BLOB | `st.FromBlob(field)` | `st.ToBlob(field)` |
| File on disk | `st.LoadFile(path)` | `st.SaveFile(path)` |

- A BLOB can't be assigned as a whole, only sliced, so `SetValue` / `GetValue` don't work on it. `FromBlob` / `ToBlob` take `*blob` and don't accept a MEMO. The file must be open: a BLOB on a closed file is likely to GPF.
- A MEMO is a fixed-length, space-padded string (64K or less). Saving truncates silently if `st.Length() > SIZE(field)`.
- `MEMO,BINARY` may be zero-padded: `st:Clip` neither strips that nor keeps real trailing spaces. Store the length, or use a BLOB.
- Check the field's declaration first. If you can't tell whether it's a BLOB, ask.

## 2. Methods that overwrite the value

`MakeGuid()`, `MakeGuid4()` and `Random()` (they return the result *and* set it), `LoadFile()` (resets the object first, §4), `Base64Encode` / `Decode`, `ToUnicode` / `ToAnsi`, `UrlEncode` / `Decode`, `JsonEncode` / `Decode`, `Join()`. Calling one on an instance that holds a payload destroys the payload without an error.

The GUID methods read like getters. Use a dedicated instance:

```clarion
stJson.SetValue(Payload, st:Clip)
Rec:Guid = stJson.MakeGuid()          ! BUG: stJson now holds the GUID
stJson.ToBlob(Rec:Json)               ! writes the GUID, not the payload
Rec:Guid = stGuid.MakeGuid()          ! RIGHT: stGuid never holds a payload
```

## 3. Clip padded arguments

String arguments are used at full `SIZE()`, trailing spaces included. Literals are fine; STRING variables aren't.

- `SetValue(Var, st:Clip)`, not `SetValue(CLIP(Var))`.
- `Split`, `Join`, `Instring`, `Count`, `Replace`, `Between` and `FindBetween` don't clip the search or separator argument: use `CLIP(Var)`. (`Split`'s `pClip` clips the resulting lines, not the separator.) With `Sep STRING(5)` holding `','`, `st.Split(Sep)` splits on `',    '`.
- Already clip by default: `StartsWith` / `EndsWith`, `Equals(string)`, `FileNameOnly` / `PathOnly` / `ExtensionOnly`.

## 4. Return values: mixed conventions

| Methods | Success | Failure |
|---|---|---|
| `LoadFile`, `SaveFile`, `FromBlob`, `ToBlob`, `ToAnsi`, `ToUnicode` | `TRUE` | `FALSE` |
| `Base64Encode`, `Base64Decode` (declared `bool`) | `st:ok` (0) | `st:notOk` |
| `SetBefore`, `SetAfter` | `st:ok` (0) | `st:notFound` |

- `IF st.LoadFile(f) = st:ok` is the **failure** branch. The reason is in `st.LastError`.
- `SetBefore` / `SetAfter` leave the value unchanged when not found, unless `pClearIfNotFound` (5th parameter) is set.
- `LoadFile` calls `Start()` first: the value, the Lines and `encoding` are reset even when the load fails. A zero-byte file fails, and so does `FromBlob` on an empty BLOB (it also clears the value). A UTF-8 BOM is kept unless `st:removeBOM` is passed: `st.LoadFile(f, , , st:removeBOM)`.

## 5. Base64

- `Base64Decode()` accepts the standard and URL-safe alphabets and missing `=` padding. Converting `-` `_` or adding `=` first is redundant (the CapeSoft docs suggest otherwise).
- Base64URL: `Base64Encode(st:URLSafe + st:NoPadding)`, which also doesn't wrap. Plain `Base64Encode()` wraps with CR/LF; pass `st:NoWrap` for HTTP headers and JSON.
- `Base64Encode` does nothing while the `base64` property is true, and it sets it itself. `SetValue`, `LoadFile`, `Start` and `Base64Decode` reset it; `Append`, `Prepend` and `Replace` don't, so encoding again after `Append` is a silent no-op.

## 6. Split and Lines

- `Split(sep)` fills the Lines queue and leaves the value unchanged. Loop `i = 1 TO st.Records()` with `st.GetLine(i)`: `GetLine` returns `''` both for an empty line and out of range.
- `Split(',', '"')` keeps separators inside quotes. 4th parameter `true` strips the quotes; `st:CSVDecode` also turns `""` into `"`.
- `Split` frees the old Lines. `Free()` keeps the Lines; `Free(true)` clears both.

## 7. UTF-8 / ANSI: silent data loss

1. The default code page is `st:CP_US_ASCII`: accented letters (é ü ø) are lost. Always pass one (`st:CP_WINDOWS_1252`, `st:CP_ACP`).
2. Conversion depends on the `encoding` property, which `SetValue` doesn't change. `ToAnsi()` on a fresh object does nothing and returns `FALSE`: pass `st:EncodeUtf8`. On a reused object a second `ToUnicode` does nothing and returns `TRUE`. Reset with `st.encoding = st:EncodeAnsi`, or use a fresh instance.
3. `JsonDecode` writes `\uXXXX` escapes in the current encoding. Convert UTF-8 to ANSI **before** `JsonDecode`, not after.
4. `ToAnsi` from UTF-8 applies NFKC normalization (`²` → `2`) unless `st.Normalize = false` is set first: not safe where text must round-trip.

```clarion
st.ToAnsi(st:EncodeUtf8, st:CP_WINDOWS_1252)       ! UTF-8 -> Windows-1252
st.encoding = st:EncodeAnsi                        ! if the instance is reused
st.ToUnicode(st:EncodeUtf8, st:CP_WINDOWS_1252)    ! Windows-1252 -> UTF-8
```

## 8. Searching and comparing

```clarion
pos = st.Instring(CLIP(s), 1, 1, 0, st:noCase)          ! step, start, end (0 = end), nocase [, wholeword]
n   = st.Count(CLIP(s), 1, 1, 0, st:noCase)
n   = st.Replace(CLIP(a), CLIP(b), 0, 1, 0, st:noCase)   ! count (0 = all), start, end, nocase
```

- `Replace` returns the number of replacements and doesn't replace text it inserted.
- `Count` counts overlapping matches (`'aa'` in `'aaaa'` is 3); pass `false` as the 7th parameter for non-overlapping. The 6th, `softClip`, lets a match extend past `pEnd`; it doesn't clip.
- `Equals(string)` ignores trailing spaces on both sides by default (`ST:Clip`) and is case-sensitive; add `st:NoCaseCompare` to ignore case. For binary data: `st.Equals(Buf[1 : Len], ST:SimpleCompare)`. `Equals(StringTheory)` compares exact lengths unless `ST:Clip` is passed.
- `StartsWith(s, FALSE)` / `EndsWith(s, FALSE)` are case-insensitive (the default is case-sensitive). An empty `s` always matches: `IF Prefix <> '' AND st.StartsWith(Prefix, FALSE)`.

## 9. Extracting

- `Sub(start, length)` (length defaults to 1) vs `Slice(start, end)` (defaults to the end; a negative end counts back). Both return `''` out of range.
- `Between(left, right)` excludes the delimiters and returns `''` if either is missing. For several occurrences loop with `FindBetween(left, right, start, end)`.
- `Clip()` removes trailing spaces, `Trim()` both sides. `UnQuote()` removes the first and last character only if both are the quote; `Quote()` adds `"` unless already quoted.
- `FileNameOnly(p [, includeExtension])`, `PathOnly(p)`, `ExtensionOnly(p)` return a string, don't change the value, and handle `\` and `/`. `PathOnly` has no trailing `\` (`''` when there is no path); `ExtensionOnly` has no dot.

## 10. Random and MakeGuid are predictable

`Random()`, `SetRandom()`, `MakeGuid()` and `MakeGuid4()` use the C runtime `rand()`, seeded from `CLOCK()` and the process ID, whatever the flags (`st:Binary` included). Fine for test data and non-secret identifiers. **Not** for anything that must be unguessable (passwords, access tokens, secret values): use a secure random source from the operating system and encode the bytes with `Base64Encode(st:URLSafe + st:NoPadding)`.

## 11. UrlEncode and JsonEncode

- **`st.UrlEncode(Text)` encodes the object's value, not `Text`.** The overload that takes text has no defaults, so a single string binds to `UrlEncode(long flags = 0)`. Use `st.SetValue(Text, st:Clip)` first.
- By default `-` `_` `.` `~` are encoded; `st:php` keeps them (RFC 3986). Space becomes `+`; `st:BigPlus` gives `%20`. `&` `=` `?` `:` `/` `\` `$` are encoded unless `st:Parameters`, `st:NoColon` or `st:dos` is passed.
- Bytes are encoded as they are: convert non-ASCII text with `ToUnicode(st:EncodeUtf8, cp)` first.
- `JsonEncode()` only escapes the value for use inside a JSON string; it adds no quotes. `st:min` escapes only `\` and `"`. For JSON documents use jFiles (see the `jfiles` skill).

## 12. Pointers and memory ownership

- `GetValuePtr()` returns a reference without copying: use it instead of `GetValue()` for large values passed to `*STRING`. It is null when the value is empty and invalid after any change.
- Output buffer for an API: `st.SetLength(n)`, then pass `st.GetValuePtr()`. Growing pads with spaces. `SetLength(0)` frees the value, so the pointer is null: guard `n > 0`.
- `ToCstring()` and `Utf16To8()` return memory allocated with NEW: the caller must `DISPOSE` it. `ToCstring` clips trailing spaces and returns `''` without allocating when the value is empty.
- `_StealValue(otherSt)` moves the value without copying: `otherSt` is left empty and its `encoding` / `base64` flags come along. `_StealValue(strRef)` takes ownership of a NEW'd `&STRING`: don't `DISPOSE` it afterwards.
