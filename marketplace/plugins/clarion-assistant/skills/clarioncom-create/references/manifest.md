# Manifest File Requirements - CRITICAL

## The Correct Manifest Structure for .NET Framework COM

**EVERY Clarion COM component MUST use this exact structure.** Create the file as `YourProject.manifest` (NOT `YourProject.dll.manifest` - Clarion requires this naming) in the project root after building:

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">

  <assemblyIdentity
      name="YourProjectName"
      version="1.0.0.0"
      processorArchitecture="x86"
      type="win32"/>

  <clrClass
      clsid="{YOUR-CLASS-GUID}"
      progid="YourNamespace.YourClassName"
      threadingModel="Apartment"
      name="YourNamespace.YourClassName"
      runtimeVersion="v4.0.30319">
  </clrClass>

  <file name="YourProject.dll">
     <typelib
         tlbid="{YOUR-ASSEMBLY-GUID}"
         version="1.0"
         helpdir=""
         resourceid="0"
         flags="HASDISKIMAGE"/>
  </file>

</assembly>
```

## COMMON MISTAKE - DO NOT USE THIS FORMAT

**NEVER use this structure (native COM format):**

```xml
<!-- WRONG - This is for native C++ COM, not .NET -->
<file name="YourProject.dll">
  <comClass clsid="{...}" ...>
    <!-- This will NOT work for .NET COM! -->
  </comClass>
</file>
```

## GUID Mapping

- `clsid=` -> Class GUID from implementation file
- `progid=` -> Must match ProgId attribute exactly
- `name=` -> Fully qualified class name (Namespace.ClassName) - must match exactly
- `runtimeVersion=` -> Use `v4.0.30319` for .NET Framework 4.x
- `threadingModel=` -> Use `Apartment` for Clarion compatibility
- `tlbid=` -> Assembly GUID from AssemblyInfo.cs

**Critical for .NET COM:**
- Use `<clrClass>` element (NOT `<comClass>`)
- Place `<clrClass>` OUTSIDE and BEFORE the `<file>` element
- Must include `name` and `runtimeVersion` attributes
- Threading model must be `Apartment` for Clarion

## Manifest Creation Checklist

**Before saving your manifest file, verify ALL of these:**

- [ ] Uses `<clrClass>` element (NOT `<comClass>`)
- [ ] `<clrClass>` is OUTSIDE and BEFORE the `<file>` element
- [ ] Includes `processorArchitecture="x86"` in `<assemblyIdentity>`
- [ ] Includes `name="Namespace.ClassName"` attribute (fully qualified)
- [ ] Includes `runtimeVersion="v4.0.30319"` attribute
- [ ] Uses `threadingModel="Apartment"` (required for Clarion)
- [ ] Includes `resourceid="0"` in `<typelib>` element
- [ ] All GUIDs match the source code exactly
- [ ] ProgId matches the `[ProgId]` attribute in implementation class
- [ ] File named as `ProjectName.manifest` (NOT `ProjectName.dll.manifest`)

## Critical Attributes Explained

| Attribute | Required Value | Why It's Critical |
|-----------|---------------|-------------------|
| Element type | `<clrClass>` | .NET Framework COM requires CLR activation, not native COM |
| `name` | `Namespace.ClassName` | CLR needs fully qualified name to instantiate the class |
| `runtimeVersion` | `v4.0.30319` | Specifies .NET Framework 4.x runtime for activation |
| `processorArchitecture` | `x86` | Clarion requires 32-bit architecture |
| `threadingModel` | `Apartment` | Required for UI controls and Clarion compatibility |

## Why `<comClass>` Fails

**Using `<comClass>` instead of `<clrClass>` causes:**
- Windows treats it as native COM component
- Tries to find it in registry (registration-based activation)
- Registration-free activation FAILS completely
- Results in "Could not create COM object" error in Clarion
- Component appears to build successfully but doesn't work

**The fix:** Always use `<clrClass>` for .NET Framework COM components!

## Automating Manifest Copy

To avoid manually copying the manifest file after each build, the .csproj includes a `CopyManifest` MSBuild target (see references/csproj-msbuild.md). Keep your manifest file in the project root (same directory as .csproj); it is automatically copied to the output folder after each build (only if the file exists and has changed).
