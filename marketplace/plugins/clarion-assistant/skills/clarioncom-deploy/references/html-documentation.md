# HTML Documentation Generation (Step 5 Detail)

Generate `readme_ProjectName.html` in `ProjectName/Clarion/accessory/resources/`.

## CRITICAL: DO NOT GENERATE CLARION CODE EXAMPLES

**NEVER hand-write per-control Clarion code examples in the documentation.**
- Improvised, per-control Clarion code will likely have incorrect syntax
- The UltimateCOM template generates the instantiation and event-handling code
- Focus on COM interface documentation only

**EXCEPTION - the shared "Calling from Clarion" section.** GenerateReadmeHTML.ps1 emits a fixed,
reviewed calling-convention block into every generated readme: OLE string-expression syntax,
parameter-passing rules, and the base64 requirement for JSON payloads. It is verified against the
SoftVelocity Help topics "Parameter Passing to OLE/OCX Methods" and "Calling OLE Object Methods",
and is identical for every control. Do NOT strip it out, and do NOT add improvised examples of
your own alongside it.

Why the exception exists: the UltimateCOM template and class cover the INBOUND half only -
instantiation, placement, and events (Parm1..Parm6 arrive as StringTheory objects). There is no
outbound helper, so every method call and data payload is hand-written by the developer. That is
the gap the shared block fills.

## Required Sections

Generate comprehensive HTML documentation with sections:

1. **Quick Start** - Files included, requirements
2. **Required Files and Dependencies** - List ALL files needed:
   - Main COM DLL and manifest
   - Any additional dependency DLLs (NuGet packages, native DLLs)
   - Sample data files (databases, config files)
   - Note that all files must be in the same directory as the Clarion executable
3. **COM Component Information** - ProgId, CLSID, TypeLib GUID
4. **Integration Options** - Registration-free vs. traditional
5. **Integration Instructions** - How to add OLE control and set ProgID (NO CODE EXAMPLES)
6. **Available Properties and Methods** - List what's available (NO CODE EXAMPLES)
7. **Exposed Methods** - For each method extracted from interface:
   - Method signature
   - Parameter descriptions
   - Return type
   - **NO Clarion usage examples**
8. **COM Events** - For each event extracted:
   - Event signature
   - Parameter descriptions
   - **NO Clarion usage examples**
9. **Date/Data Format** - If applicable
10. **Troubleshooting** - Common errors
11. **Testing** - Batch file descriptions
12. **Quick Reference Card** - Summary table

## What to include instead of code examples

- Property/method names and descriptions
- Parameter types and purposes
- Simple integration steps (add OLE control, set ProgID, copy files)
- File requirements (DLL + manifest in same folder)

## What NOT to include

- Clarion code snippets
- Variable declarations in Clarion syntax
- Clarion-specific code examples
- Method call examples in Clarion

## Extract actual documentation

- Parse XML comments from C# source files
- Use `/// <summary>` tags for method descriptions
- Use `/// <param>` tags for parameter descriptions
- Use `/// <returns>` tags for return value descriptions

## Detect and document dependencies

- Check the `Clarion/accessory/bin/` folder for additional DLL files (beyond the main COM DLL)
- List all `.dll`, `.db`, `.db3`, `.sqlite`, `.json`, `.xml`, `.ini` files found
- Group them by type:
  - .NET dependency DLLs (e.g., Newtonsoft.Json.dll, System.Data.SQLite.dll)
  - Native DLLs (e.g., sqlite3.dll, zlibwapi.dll)
  - Data files (e.g., *.db3, *.sqlite)
  - Configuration files (e.g., *.json, *.ini)
- Include file sizes for reference
- Note in the documentation that ALL these files must be deployed together
- If no additional files are found besides the COM DLL and manifest, state "No additional dependencies required"
