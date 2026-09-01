---
name: clarioncom-deploy
# prettier-ignore
description: Generate deployment artifacts for Clarion COM components including batch scripts (validation and test scripts), HTML documentation, and metadata files. Auto-applies after successful COM builds. Registration-free COM only. Generates deployment files in parallel where possible. Uses simple file naming (all files share same base name as DLL).
version: 1.0.0
---

# Deploy Clarion COM Skill

Automates deployment setup for Clarion COM components: generates batch files, HTML documentation, and metadata files with project-specific details into `ProjectName/Clarion/accessory/` (bin/ for DLLs, resources/ for everything else), then offers to copy them to the Clarion installation.

## Path Resolution - CRITICAL

Get CLARIONCOM_HOME via the helper script (avoids shell escaping issues):

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If NOT_INSTALLED**: Stop and tell user: "ClarionCOM is not installed. Please run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder."

- Scripts live at the resolved CLARIONCOM_HOME path + `\scripts\`
- **Never use `$env:APPDATA` in commands** — the `$` gets stripped by Bash. Use `[Environment]::GetFolderPath('ApplicationData')` instead.

## CRITICAL RULES - apply throughout creation, building, and deployment

1. **ALWAYS offer to copy files after deployment.** After generating artifacts you MUST proceed to Step 8 (Copy Files to Clarion). Do NOT stop after reporting completion; the copy step is MANDATORY - always ask where to copy files.
2. **NEVER register the control - Registration-Free COM ONLY.** No RegAsm.exe, RegSvcs.exe, or any registry-based registration; do not suggest or offer it. The manifest provides ALL COM activation information; registration interferes with manifest-based activation and causes failures. Only deployment method: copy DLL + manifest to the same directory as the Clarion executable.
3. **NEVER run or offer to run tests.** Do not execute or suggest CheckDotNetVersion.bat, TestManifests.bat, or any test files. Testing is the user's responsibility; batch files are for manual debugging and stay in the project folder (not copied to Clarion).

## When to Use

- After `clarioncom-create` creates a component or `clarioncom-build` builds it
- When the COM interface changes (new methods, events, or properties)
- To regenerate deployment artifacts with updated information

Prerequisites: project created and built; DLL + manifest exist in `bin/Release/net472/` or `bin/x86/Release/net472/`; source files exist (IInterface.cs, ImplementationControl.cs, AssemblyInfo.cs).

## Pre-flight: Manifest Validation

**The #1 cause of deployment failure is a manifest using `<comClass>` instead of `<clrClass>`.** Before deploying, verify the manifest contains `<clrClass>` with `runtimeVersion="v4.0.30319"`, fully qualified `name=`, and `processorArchitecture="x86"`. If it uses `<comClass>`, STOP and fix it first. Read references/manifest-validation.md (in this skill's directory) for the quick check command, correct/wrong examples, the fix procedure, and the attribute checklist.

## Workflow

**Step 1 - Identify project.** Find the .csproj; project name = filename base. Detail in references/project-extraction.md.

**Step 2 - Extract COM details from source.** CLSID/ProgId/class name from the implementation file, interface GUID + method signatures + XML docs from the interface file, TypeLib GUID from AssemblyInfo.cs, event signatures from the events interface. Read references/project-extraction.md (in this skill's directory) for exactly what to extract from each file.

**Step 3 - Create/verify Clarion folder structure.** `mkdir` accessory/bin and accessory/resources; copy DLLs to bin/, manifest to resources/; verify required files present. Commands and the full output-structure diagram are in references/project-extraction.md.

**Step 4 - Generate batch files** in accessory/resources/: CheckDotNetVersion.bat and TestManifests.bat with project-specific substitutions ({ProjectName}, {ProgId}, {ClassGuid}, {MethodsList}). Read references/batch-templates.md (in this skill's directory) for the full templates and substitution list.

**Step 5 - Generate readme_ProjectName.html.** CRITICAL: NEVER hand-write per-control Clarion code examples (the UltimateCOM template generates that code); the ONLY Clarion content allowed is the fixed shared "Calling from Clarion" block emitted by GenerateReadmeHTML.ps1 - do not strip it or add examples alongside it. Read references/html-documentation.md (in this skill's directory) for the 12 required sections, the code-example rules, XML-doc extraction, and dependency detection.

**Step 6 - Generate metadata files** using ProgID naming: `ProjectName.header` (assembly name) plus `ProgID.details`, `ProgID.events`, `ProgID.methods`. The `.header` must include [ClarionPath], [ControlType], [Description], [DLL], [Version], [DllsToCopy] (ALL DLLs from the build output - critical for the Clarion template), and [ProgID]. Read references/metadata-files.md (in this skill's directory) for the header format and how to populate each section.

**Step 7 - Report completion.** Display the summary of generated files, key info (ProgId, CLSID, TypeLib, counts), and registration-free deployment instructions. Full report template in references/copy-to-clarion.md.

**Step 8.0 - Verify Clarion path BEFORE copying.** Read the path via helper script, validate it exists, and confirm with the user (AskUserQuestion: confirm / change path / skip). If NOT_FOUND, prompt for correction and save via `clarion-write`. Read references/copy-to-clarion.md (in this skill's directory) for the exact commands and question flows.

**Step 8 - Copy files to Clarion.** Ask where to copy (Clarion accessory folder / app folder / skip), then run `copy-to-clarion.ps1` from CLARIONCOM_HOME - **use the helper script; do not construct copy commands manually**. Afterwards list ALL files actually copied. Full commands and confirmation template in references/copy-to-clarion.md.

## Error Handling

- **Project not found**: list available .csproj files; ask user which to deploy
- **Source files not found**: report missing files; suggest running clarioncom-create first
- **GUIDs not found**: report which are missing; cannot proceed without proper COM attributes
- **DLL/manifest not in output folder**: suggest clarioncom-build first; check both `bin/Release/net472/` and `bin/x86/Release/net472/`

## Integration with Other Skills

Works alongside **clarioncom-create** (creates the component), **clarioncom-build** (builds it), and **MSBuild targets** (auto-copies DLL + manifest). Typical flow: create C# files -> build DLL -> MSBuild auto-copies DLL + manifest to Clarion/ -> **this skill generates batch files + HTML documentation + metadata files**.

## References

All files are in this skill's `references/` directory:

- **manifest-validation.md** - Read before deployment: clrClass vs comClass check, fix procedure, required-attribute checklist.
- **project-extraction.md** - Read at Steps 1-3: output structure diagram, per-file COM detail extraction, folder setup commands, example usage, GUID/naming notes.
- **batch-templates.md** - Read at Step 4: full CheckDotNetVersion.bat/TestManifests.bat templates and substitutions.
- **html-documentation.md** - Read at Step 5: required HTML sections, no-Clarion-code rules and the shared-block exception, dependency documentation.
- **metadata-files.md** - Read at Step 6: .header file format, [DllsToCopy] generation, value sources.
- **copy-to-clarion.md** - Read at Steps 7-8: completion report template, Clarion path verification flow, copy script usage, copy confirmation.
