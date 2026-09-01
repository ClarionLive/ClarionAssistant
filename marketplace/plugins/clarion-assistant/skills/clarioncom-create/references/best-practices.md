# Best Practices

## Documentation and README Generation

**CRITICAL: NEVER Generate Clarion Code Examples**

**DO NOT hand-write per-control Clarion code examples in documentation or README files:**
- Do not write Clarion code snippets
- Do not create Clarion variable declarations
- Do not show method calls in Clarion syntax
- Do not provide Clarion integration examples

**Why:**
- Improvised, per-control Clarion code will likely have incorrect syntax
- Clarion has specific syntax requirements that are easy to get wrong
- The UltimateCOM template generates the instantiation and event-handling code

**EXCEPTION:** GenerateReadmeHTML.ps1 emits a fixed, reviewed "Calling from Clarion" block into every generated readme (OLE string-expression syntax, parameter-passing rules, base64 for JSON payloads). It is verified against the SoftVelocity Help and is identical for every control. Do NOT strip it out and do NOT add improvised examples alongside it. The template covers instantiation and events only - outbound method calls are hand-written by the developer, which is why the shared block exists.

**What to include instead:**
- List of available properties and methods
- Property/method descriptions (from C# XML comments)
- Parameter types and purposes
- Integration steps (add OLE control, set ProgID, copy files)
- COM identifiers (ProgID, CLSID, etc.)

**Example of correct documentation (NO CODE):**

> **Integration Instructions:**
> 1. Add an OLE control to your Clarion window
> 2. Set the ProgID to: `ComponentName.ClassName`
> 3. Copy DLL and manifest to your application directory
>
> **Available Methods:**
> - `SetDateToToday()` - Sets the selected date to today's date
> - `GetFormattedDate()` - Returns the date as a formatted string

## UI Design
1. **Set default sizes:** Always set `this.Size` in constructor
2. **Use AutoSize:** For labels, set `AutoSize = true` for automatic sizing
3. **Manual layout:** Position controls with `Location = new Point(x, y)`
4. **Fonts:** Specify fonts explicitly: `new Font("Microsoft Sans Serif", 10F, FontStyle.Bold)`

## Thread Safety
1. **Always check InvokeRequired** for methods that update UI
2. **Use Invoke pattern:**
   ```csharp
   if (InvokeRequired)
   {
       Invoke(new Action<ParamType>(MethodName), param);
       return;
   }
   // Safe to update UI here
   ```

## Memory Management
1. **Dispose timers and resources** in `Dispose(bool disposing)`
2. **Stop timers** before disposing
3. **Call base.Dispose(disposing)** at the end

## Method Design
1. **Keep it simple:** Use basic types (string, int, bool, double)
2. **Avoid complex types:** No custom classes, collections, or delegates in interface
3. **Return values:** Prefer void methods or simple types
4. **Error handling:** Use try-catch in all interface methods
