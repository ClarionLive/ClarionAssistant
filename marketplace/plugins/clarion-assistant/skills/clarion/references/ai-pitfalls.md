# Compile-Verified Clarion Pitfalls

Every rule below was reproduced with a standalone compile (or, where stated, a run) against **Clarion 12.0.13999** — generic MSBuild plus `SoftVelocity.Build.Clarion.targets`, one tiny `.cwproj` per test. The error text is the real compiler output. MSBuild often prints a second `Internal Compiler Error (...)` line after a real error; the first line is the diagnostic.

Read this before writing or reviewing non-trivial Clarion code. The last section lists plausible-sounding rules that are **false** on Clarion 12, so they are not reintroduced.

## Types, prototypes and calls

| Rule | Compiler evidence |
|---|---|
| `DECIMAL` cannot be a **value parameter**: `Dv PROCEDURE(DECIMAL pV)`. Use `*DECIMAL`, `LONG` or `REAL`. | `Invalid data type for value parameter` |
| `DECIMAL` cannot be a **return type**: `Dr PROCEDURE(),DECIMAL`. Return `REAL`/`LONG` and assign to a DECIMAL variable. | `Illegal return type or attribute` |
| `Dp PROCEDURE(*DECIMAL pV),REAL` compiles; a local `D DECIMAL(15,2)` and `D = GetR()` (REAL return) compile. | compiles clean |
| A returning procedure called as a statement should have `,PROC` in its prototype (`GetVal PROCEDURE(STRING pK),LONG,PROC`), or have its result assigned. | without `,PROC` it compiles with a warning: `Calling function as procedure` |
| `GETINI(...)` is a function — assign the result (`S = GETINI('a','b','c','x.ini')`). | as a statement it compiles with a warning: `Calling function as procedure` |
| There is no `POWER()`. Use the `^` operator: `D = (2) ^ (N)`. | `Unknown function label` |
| A class `Construct` cannot take parameters and cannot be `VIRTUAL`. | `Illegal return type or attribute` (both) |
| Parameter **names** in prototypes are legal: `MyApi PROCEDURE(LONG hWnd, *CSTRING text)`. | compiles clean |
| A `*CSTRING` parameter needs a real `CSTRING` variable — a string literal is rejected. | `No matching prototype available` |
| An unindexed array field passed to a `*BYTE` parameter (`TakeB(G.B)` with `B BYTE,DIM(16)`) is rejected; index it (`G.B[1]`). | `No matching prototype available` |

## MAP, MODULE and MEMBER

- **`MODULE` must be inside a `MAP`.** Outside one: `Expected: <ID> <LINEBREAK> ; CODE INCLUDE OMIT SECTION COMPILE PRAGMA GROUP ITEMIZE MAP`.
- **The `MODULE('name')` must match the `.clw` that contains the procedure.** With `MODULE('WrongName')` while `M1.clw` defines the procedure: `Procedure doesn't belong to module: WORK`.
- **`MEMBER('Parent.clw')` is required** in a module implementing procedures prototyped in the parent's global MAP; bare `MEMBER` gives `No matching prototype available`. In a class-implementation `.clw` (`CLASS,MODULE(...),LINK(...)`) both bare `MEMBER` and `MEMBER('Parent.clw')` compile.
- **A MEMBER module sees the PROGRAM file's global data and program-level EQUATEs automatically.** Do not redeclare a global with `,EXTERNAL` in the member file — the build fails: a `Label duplicated, second used` warning, then `Unresolved External <MODULE>$<LABEL>` at link time.

## Layout, strings and dates

- **Data declarations start in column 1.** `  MyVar LONG` (indented) → `Illegal data type: MYVAR`. Only `CODE` and executable statements are indented.
- **LF-only source files do not compile** (see the File Conventions note in `SKILL.md`): `Illegal character` reported on line 1, and on larger files `Expected: <ID> <LINEBREAK> ; ...` as well.
- **Trailing blanks are ignored in comparisons.** A `STRING(10)` holding `'Hello'` *equals* `'Hello'` (run-tested), and `CLIP(S1) = 'Hello'` and `S1 = S2` are true as well. `CLIP()` matters when *concatenating* (`CLIP(First) & ' ' & CLIP(Last)`), not for comparing to a literal.
- **`DATE`/`TIME` are real types**: `D DATE`, `T TIME`, `D = TODAY()`, `T = CLOCK()`, `S = FORMAT(D,@D17)`, `D = DEFORMAT(S,@D17)` all compile.
- `PUSHERRORS` / `POPERRORS` exist and compile.

## OMIT

`OMIT('token')` is closed by the **first occurrence of that exact text anywhere in the source — including inside a comment.** `!` and `!!` are both ordinary comments, so a comment line containing the token closes the block:

```clarion
  OMIT('disable old 2026')
  X = 1
  this line is not valid Clarion and is never parsed
!! disable old 2026
  X = 2
```

This compiles clean. An `OMIT` whose token never reappears fails with `OMIT not terminated: <token>`. (Some developers use `!!` for hand-written comments and `!` for IDE-generated ones and deliberately close an OMIT that way after disabling generated code — preserve that convention when editing their code.)

## Claims that are false on Clarion 12

These appear in other Clarion AI-guidance documents. Each was tested and does **not** hold:

| Claim | Result |
|---|---|
| "A value-returning routine must be declared `FUNCTION`, not `PROCEDURE`, in a MAP." | `PROCEDURE(STRING),LONG` and `FUNCTION(STRING),LONG` both compile. `PROCEDURE ...,TYPE` is the normal form. |
| "`OMIT` guards don't work in `.inc` files." | Both `OMIT('_G_',_G_)` and the unconditional `OMIT('_MYFILE_INC_')` closed by a `!_MYFILE_INC_` line compile clean. (`INCLUDE(...),ONCE` is still simpler.) |
| "MEMBER files can't see EQUATEs from the parent's includes." | They can, when the PROGRAM file includes them. |
| "A QUEUE cannot be passed by value." | `PROCEDURE(QUEUE pQ)` compiles. (`*QUEUE` is still preferable — no copy.) |
| "`KEY`, `FILE`, `RECORD`, `WINDOW`, `QUEUE`, `GROUP`, `INDEX` cannot be parameter names." | Each compiles as a `STRING` parameter name. (Real reserved words such as `ACCEPT`, `CASE`, `CODE`, `SELF`, `PARENT` remain off-limits.) |
| "`STRING(10)` = `'Hello'` is false because of padding." | It is true (trailing blanks ignored). |
| "LF-only files report `Expected: PROGRAM PRAGMA`." | The real signature is in the Layout section above. |
