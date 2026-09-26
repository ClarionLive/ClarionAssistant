# Manifest Validation and Fix Guide

## Before Deployment - Verify Manifest is Correct

**The #1 cause of deployment failure is incorrect manifest format!**

## Quick Check

Run this to verify manifest uses correct format:

```powershell
powershell -Command "Get-Content ProjectName.manifest | Select-String -Pattern '<clrClass'"
```

**Expected result:** Should display a line containing `<clrClass`
**If empty:** Manifest is WRONG - fix it before proceeding!

## What to Look For

**CORRECT Manifest (uses `<clrClass>`):**
```xml
<clrClass
    clsid="{...}"
    progid="..."
    threadingModel="Apartment"
    name="Namespace.ClassName"
    runtimeVersion="v4.0.30319">
</clrClass>
```

**WRONG Manifest (uses `<comClass>`):**
```xml
<comClass
    clsid="{...}"
    threadingModel="Apartment"
    progid="...">
    <!-- This will NOT work! -->
</comClass>
```

## How to Fix an Incorrect Manifest

**If your manifest uses `<comClass>` instead of `<clrClass>`:**

1. **Stop immediately** - do not proceed with deployment
2. **Open the manifest file** (ProjectName.manifest in project root)
3. **Replace the entire content** with the correct template from `clarioncom-create.md`
4. **Substitute the GUIDs** from your source code:
   - `clsid` = Class GUID from `[Guid]` attribute on implementation class
   - `tlbid` = Assembly GUID from AssemblyInfo.cs
   - `progid` = ProgId from `[ProgId]` attribute
   - `name` = Fully qualified class name (Namespace.ClassName)
5. **Rebuild the project** to copy updated manifest to Clarion folder
6. **Re-run deployment** after manifest is fixed

## Required Manifest Attributes Checklist

- [ ] Uses `<clrClass>` element (NOT `<comClass>`)
- [ ] Includes `runtimeVersion="v4.0.30319"`
- [ ] Includes `name="Namespace.ClassName"` (fully qualified)
- [ ] `<clrClass>` is placed BEFORE `<file>` element
- [ ] Includes `processorArchitecture="x86"` in `<assemblyIdentity>`

## Why This Matters

**Impact of using wrong manifest format:**
- Component builds successfully but doesn't activate
- Clarion shows "Could not create COM object" error
- Registration-free COM activation completely fails
- Windows treats it as native COM (tries registry lookup)
- Using `<clrClass>` = Component works perfectly

**Prevention:** Always use the template from `clarioncom-create.md` skill!
