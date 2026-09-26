# Clarion Template Authoring Gotchas

These rules apply when writing Clarion templates (`.tpl` / `.tpw`), not when writing Clarion source code. Both gotchas fail silently or in confusing ways — there is no compiler warning that tells you what's wrong.

## `#AT` directives cannot be nested inside `#IF` blocks

`#AT` registers a code-generation point with the template engine; the registration must be unconditional from the parser's view. Conditional logic belongs INSIDE the body the generator emits, not around the `#AT` itself.

❌ **Wrong — parser rejects this with `#ENDIF expected` / `Mismatched End`:**
```
#IF(%MyFlag <> '')
#AT(%SomeEmbed),PRIORITY(500)
   ...code...
#ENDAT
#ENDIF
```

✅ **Right — invert the nesting (`#IF` goes INSIDE the `#AT` body):**
```
#AT(%SomeEmbed),PRIORITY(500)
#IF(%MyFlag <> '')
   ...code...
#ENDIF
#ENDAT
```

When the condition is false, the inner `#IF` skips the body and the `#AT` emits nothing — same net effect as the broken form, but parser-legal.

## `OMITTED()` only works in the scope where the parameter is declared

`OMITTED(name)` resolves the name against the **current method's** parameter list, not against the enclosing procedure's parameter list. Inside ABC class methods declared within a procedure (e.g. `ThisWindow.Init`, `ThisWindow.TakeEvent`), `OMITTED(pSomeParam)` returns 1 (TRUE = omitted) even when the caller passed a real value — because `TakeEvent()` has no parameter named `pSomeParam`. The parameter VALUE is visible from the nested method (procedure-locals are accessible), but the OMITTED bitfield is not.

The compiler accepts the syntax silently and emits code that reads from the wrong place. The only signal is bizarre runtime behavior.

**Fix:** Stash the params at procedure top-level (where `OMITTED` works correctly) into procedure-local data variables, then check those locals from class methods.

For ABC Window procedures, the right embed is `%BeforeWindowManagerRun`. It's declared in `template/win/ABWINDOW.TPW` (HIDE-flagged but `#AT`-targetable), generated inside the procedure's main `CODE` block immediately before `GlobalResponse = ThisWindow.Run()`:

```
#AT(%BeforeWindowManagerRun),PRIORITY(500)
#IF(%FileNameParam <> '')
IF OMITTED(%FileNameParam) = 0; LocalStashFile = %FileNameParam; END
#IF(%STParam <> '')
IF OMITTED(%STParam) = 0;       LocalStashST  &= %STParam;       END
#ENDIF
#ENDIF
#ENDAT
```

Then from class methods (e.g. an event handler):

```clarion
IF CLIP(LocalStashFile) <> ''        ! filename was passed
  ...
  IF NOT (LocalStashST &= NULL)      ! ST ref was passed
    ...
```

**Two embed names that LOOK right but DON'T work for ABC procedures:**

- `%LocalProcedureSetup` — not a real embed at all. Parses silently and emits nothing.
- `%ProcedureSetup` — declared with the `LEGACY` flag, so `#AT` parses but emits nothing for ABC procedures (only fires for the Legacy family).

**Bonus — silence the "Unusual type conversion" warning** by writing `IF OMITTED(x) = 0` instead of `IF NOT OMITTED(x)`. Same logic, no warning.
