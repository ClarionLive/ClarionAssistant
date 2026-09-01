---
name: clarioncom-create
# prettier-ignore
description: Creates complete C# COM control projects for Clarion from scratch. Generates all required files (interfaces, implementation, manifest) with proper COM attributes and GUIDs. Uses parallel file updates where possible.
version: 1.0.0
---

# Clarion COM Builder

Creates C# COM components (.NET Framework) for Clarion via registration-free COM. Use when the user requests a COM object, visual control, or WinForms control Clarion can call. Use parallel execution for independent file creation/updates.

Bulk detail lives in `references/` **in this skill's directory** — read each file only at the step that needs it.

## Critical Rules — never violate

1. **NEVER register the control.** No RegAsm, no running or suggesting Register.bat. Manifest-based (reg-free) activation only.
2. **NEVER run or offer to run tests** (TestCOM.bat, TestManifests.bat, etc.). Testing is the user's responsibility.
3. **Manifest MUST use `<clrClass>`, never `<comClass>`** — `<comClass>` builds fine but activation fails ("Could not create COM object"). File is `ProjectName.manifest`, NOT `ProjectName.dll.manifest`.
4. **NEVER hand-write Clarion code examples** in docs/READMEs. Sole exception: the fixed, reviewed block GenerateReadmeHTML.ps1 emits — don't strip it, don't add improvised examples beside it.
5. **.NET Framework 4.7.2/4.8 Class Library, x86 only.** Never .NET Core/5+, never AnyCPU/x64.
6. **Never set `RegisterForComInterop` or `EnableComInterop`** in .csproj — they force registry registration and break reg-free COM.
7. **Build with Visual Studio MSBuild.exe only** — `dotnet build`/`dotnet msbuild` fail with MSB4803. An MSB3216 "cannot register assembly — access denied" at the end of the build is NORMAL: if the DLL exists, the build succeeded.
8. **Template/ files are READ-ONLY reference.** Read, copy, customize into the new project folder; never modify Template/.

## Workflow

### Step 0: Resolve paths (CRITICAL — do first)

Get CLARIONCOM_HOME:
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```
If NOT_INSTALLED: stop and tell the user to run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder.

Before reading any template file, get the templates path the same way (`... clarioncom-env.ps1') templates`):
- **LOCAL** → use `Template/` for template reads
- **Full path** → use that exact path
- **NOT_INSTALLED** → stop; tell user to install
Do NOT attempt to read a local Template/ that doesn't exist. Scripts live in `$CLARIONCOM_HOME\scripts\`.

### Step 1: Understand requirements
Methods? UI elements? Control library — WinForms (default), DevExpress, Telerik, Syncfusion, or Infragistics? For any third-party library, read `references/control-libraries.md` for the NuGet packages and using statements. Also ask the user's API style: getter/setter methods (recommended for Clarion) vs. properties — apply consistently (details in `references/code-patterns.md`).

### Step 2: Generate 3 unique GUIDs
Interface GUID, Class GUID, Assembly GUID — all different, format `{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`.

### Step 3: Read templates
Read the template files (IMinimalControl.cs, MinimalControl.cs, IMinimalControlEvents.cs, MinimalControl.manifest, ClarionCOMTemplate.csproj, Properties/AssemblyInfo.cs) from the resolved location. New files go in the project root, never in Template/.

### Steps 4–6: Create C# source files (can run in parallel)
Read `references/code-patterns.md` for the full templates and key attribute requirements, then create:
- **Interface file** — `[ComVisible(true)]`, `[Guid]`, `[InterfaceType(ComInterfaceType.InterfaceIsDual)]`, sequential `[DispId(n)]` from 1, simple types only.
- **Implementation file** — UserControl + interface, `[ComVisible(true)]`, `[Guid]`, `[ProgId("Namespace.ClassName")]`, `[ClassInterface(ClassInterfaceType.None)]`; controls initialized in code (no designer); InvokeRequired pattern for UI updates; set `this.Size`.
- **Properties/AssemblyInfo.cs** — `[assembly: ComVisible(true)]` + Assembly GUID.
Gotcha: any color-handling method/property name MUST contain "color" (Clarion IDE color-selector support). Design guidance (UI layout, thread safety, Dispose, simple types): `references/best-practices.md`. If the control does text editing, also read `references/keyboard-input.md` — standard .NET keyboard handling fails under Clarion hosting; direct text manipulation is required.

### Step 7: Create .csproj
Read `references/csproj-msbuild.md` for the required settings, the CRITICAL Template-folder exclusion ItemGroup (prevents duplicate-attribute build errors), deployment targets (Clarion/ accessory layout), NuGet packages, and dependency/sample-data copying. No RegisterForComInterop/EnableComInterop.

### Step 8: Create CHANGELOG.md
Copy from Template/CHANGELOG.md, replace {DATE} with today's date.

### Step 9: Build
Read `references/build-deploy.md` for the MSBuild path/finder command, Release build command, expected outputs, and the expected MSB3216 "error". For detailed procedures see the `clarioncom-build` skill.

### Step 10: Create manifest
Read `references/manifest.md` for the exact XML template, GUID mapping, checklist, and attributes table. Key points: `<clrClass>` outside and before `<file>`; `name` = fully qualified class; `runtimeVersion="v4.0.30319"`; `threadingModel="Apartment"`; `processorArchitecture="x86"`; GUIDs/ProgId must match source exactly.

### Step 11: Metadata files (.methods / .events / .details)
Read `references/metadata-files.md` for the INI-style file formats, MSBuild auto-generation targets, C#-to-Clarion type mapping, and target chain. These drive Clarion template code generation.

### Step 12: Verify
MSBuild targets auto-deploy DLL + manifest to `Clarion/`. Run the Testing Checklist and review Common Mistakes in `references/build-deploy.md` (unique GUIDs, ProgId match, .tlb present, thread safety, disposal).

### Step 13: Deployment artifacts (REQUIRED — not optional)
Run the **clarioncom-deploy** skill to generate Register.bat, Unregister.bat, CheckDotNetVersion.bat, TestCOM.bat/.vbs, TestManifests.bat, and README.md. Skip only if user asked for source-only, or when updating existing code with current docs. When/how details in `references/build-deploy.md`. README content rules (no Clarion code) in `references/best-practices.md`.

### Step 14: Offer GitHub repository creation
AskUserQuestion: private repo / public repo / skip. Full prompt wording, GITHUB_TOKEN check, and `clarioncom-github-init` invocation details in `references/build-deploy.md`.

## References

All in this skill's `references/` directory:

- **manifest.md** — Step 10: exact manifest XML, clrClass-vs-comClass, GUID mapping, checklist, attribute table.
- **code-patterns.md** — Steps 3–6: template folder usage, GUID rules, interface/implementation/AssemblyInfo templates, API style options, color naming, common patterns, ColorPicker example.
- **csproj-msbuild.md** — Step 7: .csproj settings, Template exclusion, deployment/dependency/sample-data targets, accessory layout.
- **metadata-files.md** — Step 11: .methods/.events/.details formats, generation targets, type mapping.
- **control-libraries.md** — Step 1, when a third-party UI library is chosen: NuGet packages and using statements per vendor.
- **keyboard-input.md** — when the control accepts typed text: why keyboard input breaks under Clarion hosting and the direct-manipulation fix.
- **build-deploy.md** — Steps 9, 12–14: MSBuild usage, expected errors, deployment files, Clarion usage pattern, artifact generation, testing checklist, common mistakes, GitHub repo offer, automatic-deployment summary.
- **best-practices.md** — when designing the API/docs: documentation rules, UI design, thread safety, memory, method design.

This skill ensures Clarion programmers can request COM components without knowing C# or COM internals.
