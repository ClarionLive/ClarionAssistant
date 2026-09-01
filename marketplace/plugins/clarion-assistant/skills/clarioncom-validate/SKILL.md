---
name: clarioncom-validate
# prettier-ignore
description: Validate and remediate existing C# COM controls for Clarion RegFree COM compliance. Auto-applies when reviewing COM controls, fixing COM issues, or migrating from registry-based to manifest-based COM. Uses parallel execution for independent validation checks.
version: 1.0.0
---

# Clarion COM Validator

Validates existing COM controls against RegFree COM requirements for Clarion and provides remediation steps for non-compliant controls.

## Path Resolution - CRITICAL

Get CLARIONCOM_HOME via the helper script (avoids shell escaping issues):

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If NOT_INSTALLED**: Stop and tell user: "ClarionCOM is not installed. Please run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder."

**Use resolved paths:** Templates: `$CLARIONCOM_HOME\Templates\` — Scripts: `$CLARIONCOM_HOME\scripts\`

## When to Use

- Reviewing an existing COM control for Clarion compatibility
- Migrating from registry-based COM to RegFree COM
- Debugging COM activation or event issues
- Validating a COM control before deployment
- User asks to "check", "validate", "review", or "fix" a COM control

## Execution Strategy

**IMPORTANT:** Use parallel execution for independent validation checks: check AssemblyInfo.cs, interface files, and implementation files in parallel; validate all GUIDs simultaneously; check project configuration while analyzing source files.

## Validation Workflow

Run all 8 checks below. Read references/validation-checklist.md (in this skill's directory) for the full per-check detail: required attribute code blocks, common issues, and rationale for each rule.

1. **Assembly (AssemblyInfo.cs)** — `[assembly: ComVisible(true)]` and an assembly-level `[assembly: Guid(...)]` are REQUIRED.
2. **Main methods interface** — `[ComVisible(true)]`, unique GUID, and `InterfaceType(ComInterfaceType.InterfaceIsDual)` (NOT InterfaceIsIDispatch — dual supports early + late binding).
   - **2a. Color naming**: methods/properties handling colors MUST include "color" in their name (e.g., `SetBackgroundColor`) for the Clarion IDE color selector.
3. **Event interface** — `[ComVisible(true)]`, unique GUID, `InterfaceIsIDispatch` (NOT dual — event sinks are late-bound only), and sequential `[DispId(n)]` starting at 1.
4. **Implementation class** — `[ComVisible(true)]`, unique GUID, `[ProgId("Namespace.ClassName")]`, `[ClassInterface(ClassInterfaceType.None)]`, `[ComSourceInterfaces(typeof(IYourControlEvents))]`.
5. **.csproj** — net48, `PlatformTarget` x86 (Clarion is 32-bit), `ComVisible` true, `GenerateAssemblyInfo` false; NO `EnableComInterop` or `RegisterForComInterop`.
6. **GUID uniqueness** — 4 distinct GUIDs (assembly typelib, methods interface, event interface, class); never copied from other projects. Generate with `[guid]::NewGuid().ToString().ToUpper()`.
7. **Manifest file** — `ControlName.manifest` must exist and use `<clrClass>` (NOT `<comClass>` — that is for native COM and fails for .NET), with GUIDs matching source. Full manifest template: references/validation-checklist.md section 7.
8. **Constructor pattern (CRITICAL)** — constructor does field/style setup ONLY; NO `Controls.Add()`, no child-control creation, no data operations. Those belong in `OnHandleCreated` (guarded by `!DesignMode`). Violation means Clarion cannot create the OCX at all. Correct/broken code patterns: references/validation-checklist.md section 8.

## Report Results

Present findings using the standard report structure (per-check PASS/FAIL checklists, summary counts, remediation steps). Read references/validation-output-template.md (in this skill's directory) for the exact template — it also includes check 3a (About() method with [DispId] plus a valid .env version file).

## Remediation

For fixes and for migrating registry-based controls to RegFree COM (remove regasm/EnableComInterop/RegisterForComInterop, create manifests, add a CopyManifest MSBuild target, update CLAUDE.md), read references/remediation-patterns.md (in this skill's directory) for the full fix catalog, including the before/after constructor-pattern fix.

## Deployment Check

After remediation, verify the Clarion folder contents and file naming (DLL/manifest/header use assembly name; .details/.events/.methods use ProgID). Read references/deployment-and-metadata.md (in this skill's directory) for the required file list, naming examples, and the tagged .details/.events/.methods file formats.

## Quick Reference: Interface Types

| Interface Type | Use For | Why |
|---------------|---------|-----|
| `InterfaceIsDual` | Methods interface | Supports early + late binding |
| `InterfaceIsIDispatch` | Events interface | Required for COM event sinks |
| `ClassInterfaceType.None` | Implementation class | Prevents auto-generated interface |

## Integration with Other Skills

- **clarioncom-control** - Reference for correct patterns
- **clarioncom-build** - Build after remediation
- **clarioncom-deploy** - Generate deployment artifacts after validation

Typical workflow: user asks to validate/fix → **this skill validates and identifies issues** → apply fixes to source → clarioncom-build builds → clarioncom-deploy generates deployment files.

## References

All in this skill's `references/` directory:

- **validation-checklist.md** — Read when running the checks: full detail for all 8 checks (required attributes, common issues, manifest template, constructor pattern).
- **validation-output-template.md** — Read when writing up results: the standard validation report template.
- **remediation-patterns.md** — Read when fixing issues: common fix snippets and the registry-to-RegFree migration procedure.
- **deployment-and-metadata.md** — Read when verifying deployment: required Clarion folder files, naming conventions, and .details/.events/.methods tagged formats.
