---
name: clarioncom-control
# prettier-ignore
description: Create and validate C# COM controls for Clarion with correct patterns. Auto-applies when user mentions COM control, Clarion control, ActiveX, or debugging COM registration/event issues. Uses parallel execution for independent operations.
version: 1.0.0
---

# Clarion COM Control Development Skill

Ensures all COM controls for Clarion are created with correct patterns from the start. Use when creating a new COM control, reviewing/fixing COM control code, or debugging COM registration or event issues. Details live in `references/` (in this skill's directory) — read the relevant file before doing the work.

## Path Resolution - CRITICAL

### Step 1: Get CLARIONCOM_HOME

Use the helper script to avoid shell escaping issues:

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If NOT_INSTALLED**: Stop and tell user:
> ClarionCOM is not installed. Please run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder.

### Step 2: Determine Template Location

**Before reading any template files**, check if a local Template/ folder exists:

```bash
powershell -Command "if (Test-Path 'Template') { Write-Output 'LOCAL' } else { Write-Output 'GLOBAL' }"
```

- **LOCAL**: Use `Template/` for all template file reads (project was copied from COMTemplate)
- **GLOBAL**: Use `$CLARIONCOM_HOME\Templates\` for all template file reads

**DO NOT attempt to read from local Template/ if it doesn't exist** — this causes unnecessary error messages. Scripts live at `$CLARIONCOM_HOME\scripts\`.

## Execution Strategy

Use subagents where appropriate and run them in parallel whenever steps are independent: reading multiple template files, creating/updating multiple control files, generating all 4 GUIDs at once, and validating multiple files concurrently.

## Non-Negotiable Rules

1. **Creating a new control: COPY from Template/, never create from scratch.** Template/ is READ-ONLY; new files go in the project root. Full step-by-step workflow, file mapping, and naming conventions in references/creation-workflow.md.
2. **Naming:** Assembly name MUST end with "COM" (e.g., `ProgressBarCOM`); class name = assembly name minus "COM" (`ProgressBar`) → ProgID `AssemblyName.ClassName`. Color-valued methods/properties MUST include "color" in the name (enables the IDE color selector). Ask the user: getter/setter methods or properties (default: methods).
3. **RegFree COM only:** never `EnableComInterop` or `RegisterForComInterop` (they generate .tlb files / registry entries that break manifest-based activation). Only `<ComVisible>true</ComVisible>`. No .tlb should exist after build.
4. **`[assembly: ComVisible(true)]`** — ComVisible(false) at assembly level breaks everything.
5. **Event interface uses `InterfaceIsIDispatch`** (NOT Dual); main interface uses `InterfaceIsDual`. Every event method needs a sequential `[DispId(n)]` from 1.
6. **Implementation class:** inherits `UserControl`, `ClassInterface(ClassInterfaceType.None)`, `[ComSourceInterfaces(typeof(IYourControlEvents))]`, `[ProgId("Namespace.ClassName")]`. Event raising with null check + try-catch; never throw exceptions to COM; never return null strings (use `?? string.Empty`).
7. **4 unique GUIDs per project** (assembly TypeLib, main interface, event interface, class) — never copy GUIDs from another project. Generate with `[guid]::NewGuid().ToString().ToUpper()`.
8. **csproj:** net472, `PlatformTarget` x86 (Clarion is 32-bit — x64 is wrong), `UseWindowsForms`, `GenerateAssemblyInfo` false, and the ItemGroup that excludes `Template\**\*` from compilation (prevents duplicate assembly attribute errors).
9. **Child controls MUST be created in `OnHandleCreated()`, never in the constructor** — the window handle doesn't exist yet when COM instantiates the control. Guard against double initialization; null-check + IsDisposed-check before access; never use `new` to shadow UserControl members. Read references/child-controls.md before writing any control with child controls.
10. **DO NOT create Clarion demo applications** (.clw/.app files) — the control (C#), build, and deployment files are your scope. (The generated readme's fixed "Calling from Clarion" documentation section is in scope.) After building, tell the user: "The control is ready in the Clarion folder. You can now add it to your Clarion application."

## Build Expectations (quick check)

First build prompts for major/minor version (stored in .env). After build, the Clarion folder must contain: `AssemblyName.dll`, `.manifest`, `.header` (assembly-named) + `ProgID.details`, `.events`, `.methods` (ProgID-named) + `readme_AssemblyName.html`. The bin folder holds only .dll and .pdb — NO .tlb. Events not firing usually means ComVisible(false) at assembly level or wrong InterfaceType on the event interface.

## References

All paths relative to this skill's directory:

- **references/creation-workflow.md** — The exact copy-from-Template workflow (5 steps), file mapping, naming/color/API-style conventions, why-copy rationale, post-creation verification, scope rules (no Clarion demo apps), and usage notes. Read FIRST when creating a new control.
- **references/com-patterns.md** — Required code patterns per file (AssemblyInfo, main interface, event interface, implementation class incl. About(), .csproj with Template exclusion), GUID requirements, and the common-mistakes catalog. Read when writing or fixing control source files.
- **references/child-controls.md** — The OnHandleCreated pattern in full: wrong vs. correct code, the four critical rules, null-checking patterns, member-shadowing trap, and a complete working example. Read when the control contains child controls.
- **references/validation-checklist.md** — The 8-part validation checklist (assembly, interfaces, class, csproj, GUIDs, manifest, build output). Read when validating or reviewing a control, and after builds.

- **Full Documentation:** `.claude/docs/clarion-com-control-patterns.md` - Complete technical reference
