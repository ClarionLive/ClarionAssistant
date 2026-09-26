# Manifest Validation (Step 0 detail)

**Before building, ALWAYS verify the manifest file is correct:**

**Quick Validation Command:**
```powershell
powershell -Command "Get-Content YourProject.manifest | Select-String -Pattern '<clrClass'"
```

**What to look for:**
- ✅ Should return a line containing `<clrClass`
- ❌ If it returns nothing, the manifest is WRONG

**Detailed Verification:**

Run this PowerShell validation script:

```powershell
powershell -Command "$manifest = 'YourProject.manifest'; Write-Host 'Checking manifest...' -ForegroundColor Cyan; if (Test-Path $manifest) { $content = Get-Content $manifest -Raw; if ($content -match '<clrClass') { Write-Host '[OK] Uses <clrClass> element' -ForegroundColor Green } else { Write-Host '[ERROR] Missing <clrClass> - manifest is WRONG!' -ForegroundColor Red; if ($content -match '<comClass') { Write-Host '[ERROR] Found <comClass> instead - this will NOT work!' -ForegroundColor Red } }; if ($content -match 'runtimeVersion') { Write-Host '[OK] Has runtimeVersion attribute' -ForegroundColor Green } else { Write-Host '[ERROR] Missing runtimeVersion attribute!' -ForegroundColor Red }; if ($content -match 'processorArchitecture') { Write-Host '[OK] Has processorArchitecture' -ForegroundColor Green } else { Write-Host '[WARNING] Missing processorArchitecture!' -ForegroundColor Yellow } } else { Write-Host '[ERROR] Manifest file not found!' -ForegroundColor Red }"
```

**Required Elements Checklist:**

Before proceeding to build, verify your manifest contains:

1. ✅ `<clrClass` element (NOT `<comClass>`)
2. ✅ `runtimeVersion="v4.0.30319"` attribute
3. ✅ `name="Namespace.ClassName"` attribute (fully qualified)
4. ✅ `processorArchitecture="x86"` in assemblyIdentity
5. ✅ `<clrClass>` appears BEFORE `<file>` element

**If validation fails:**
- Stop immediately - do NOT build
- Fix the manifest first (see clarioncom-create.md for correct template)
- Building with incorrect manifest wastes time - fix it now!

**Common errors caught by this validation:**
- Using `<comClass>` instead of `<clrClass>` → Complete activation failure
- Missing `runtimeVersion` → CLR can't load the component
- Missing `name` attribute → CLR can't find the class
- `<clrClass>` inside `<file>` → Wrong structure, won't work

## Clarion Manifest Naming Convention (CRITICAL)

**For Clarion applications, the manifest file naming is DIFFERENT from standard Windows:**

✅ **CORRECT for Clarion:**
```
ColorPickerCOM.dll       → Component DLL
ColorPickerCOM.manifest  → Manifest file (without .dll extension!)
```

❌ **WRONG (standard Windows, but doesn't work with Clarion):**
```
ColorPickerCOM.dll.manifest  ← This will NOT work with Clarion!
```

**Rule:** If your DLL is named `ComponentName.dll`, the manifest MUST be named `ComponentName.manifest` (remove the `.dll` part from the manifest filename).

This is a **Clarion-specific requirement**. Standard Windows registration-free COM typically uses `Component.dll.manifest`, but Clarion requires the simpler naming without the `.dll` extension.
