---
name: clarioncom-webview2-validate
description: Validates WebView2 COM controls for RegFree COM compliance, WebView2 configuration, and wwwroot structure
version: 1.0.0
---

## Path Resolution

### Get CLARIONCOM_HOME

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

# WebView2 COM Validation Skill

This skill validates WebView2-based COM controls for correct configuration, dependencies, and deployment readiness.

## Validation Categories

1. **Standard COM Validation** - Same as clarioncom-validate
2. **WebView2-Specific Validation** - Additional checks for WebView2

## Standard COM Validation Checklist

### Source Code Validation

- [ ] **Interface File** (IControlName.cs)
  - [ ] `[ComVisible(true)]` attribute present
  - [ ] `[Guid("...")]` attribute with valid GUID
  - [ ] `[InterfaceType(ComInterfaceType.InterfaceIsDual)]` attribute
  - [ ] All methods have `[DispId(n)]` with sequential numbers

- [ ] **Events Interface File** (IControlNameEvents.cs)
  - [ ] `[ComVisible(true)]` attribute present
  - [ ] `[Guid("...")]` attribute with valid GUID (different from main interface)
  - [ ] `[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]` attribute
  - [ ] All events have `[DispId(n)]` with sequential numbers

- [ ] **Implementation File** (ControlName.cs)
  - [ ] `[ComVisible(true)]` attribute present
  - [ ] `[Guid("...")]` attribute with valid GUID (different from interfaces)
  - [ ] `[ProgId("Namespace.ClassName")]` attribute
  - [ ] `[ClassInterface(ClassInterfaceType.None)]` attribute
  - [ ] `[ComSourceInterfaces(typeof(IControlNameEvents))]` attribute
  - [ ] Implements the interface

- [ ] **AssemblyInfo.cs**
  - [ ] `[assembly: ComVisible(true)]` attribute
  - [ ] `[assembly: Guid("...")]` with valid TypeLib GUID

### GUID Validation

- [ ] All 4 GUIDs are unique:
  - Interface GUID
  - Events GUID
  - Class GUID
  - TypeLib GUID
- [ ] GUIDs are properly formatted: `{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`
- [ ] No duplicate GUIDs across files

### Manifest Validation

- [ ] Manifest file exists (ControlName.manifest, NOT .dll.manifest)
- [ ] Uses `<clrClass>` element (NOT `<comClass>`)
- [ ] `clsid` matches Class GUID
- [ ] `progid` matches ProgId attribute
- [ ] `name` is fully qualified (Namespace.ClassName)
- [ ] `runtimeVersion="v4.0.30319"` present
- [ ] `threadingModel="Apartment"` present
- [ ] `processorArchitecture="x86"` in assemblyIdentity
- [ ] `tlbid` matches TypeLib GUID

## WebView2-Specific Validation

### NuGet Package Validation

- [ ] **Microsoft.Web.WebView2** package referenced in .csproj
- [ ] **Newtonsoft.Json** package referenced in .csproj
- [ ] Database package referenced (if data access used):
  - SQL Server: System.Data.SqlClient
  - PostgreSQL: Npgsql
  - MySQL: MySql.Data
  - SQLite: System.Data.SQLite.Core

### wwwroot Folder Structure

- [ ] `wwwroot/` folder exists in project root
- [ ] `wwwroot/css/` folder exists
- [ ] `wwwroot/js/` folder exists
- [ ] `wwwroot/controls/` folder exists
- [ ] `wwwroot/controls/{name}/index.html` exists
- [ ] `wwwroot/controls/{name}/app.js` exists (or equivalent)

### wwwroot Content Validation

Check index.html contains:
- [ ] Reference to CSS files
- [ ] Reference to JS files
- [ ] `sendToCSharp` function or equivalent
- [ ] WebView2 message handler setup

### Virtual Host Mapping

Check control implementation for:
- [ ] `SetVirtualHostNameToFolderMapping` call
- [ ] Host name is `localapp.clarioncontrols` (or project-specific)
- [ ] Maps to wwwroot folder

### Host Object Implementation

- [ ] Host object class exists (e.g., WebViewHostObject)
- [ ] Host object marked `[ComVisible(true)]`
- [ ] Host object marked `[ClassInterface(ClassInterfaceType.AutoDual)]`
- [ ] `AddHostObjectToScript` call in WebView2 initialization

### WebView2 Initialization Pattern

Check for proper async initialization:
- [ ] `OnHandleCreated` override for initialization
- [ ] `EnsureCoreWebView2Async` call
- [ ] Async/await pattern used correctly
- [ ] Navigation occurs after initialization complete

### Event Raising Pattern

- [ ] All events have null check before invoking
- [ ] Events wrapped in try-catch

## Deployment Validation

### Output Folder Contents

After build, verify in output folder:
- [ ] ProjectName.dll
- [ ] Microsoft.Web.WebView2.Core.dll
- [ ] Microsoft.Web.WebView2.WinForms.dll
- [ ] WebView2Loader.dll
- [ ] Newtonsoft.Json.dll
- [ ] wwwroot/ folder with all content
- [ ] Database provider DLL (if applicable)

### Clarion Folder Contents

After deployment, verify in Clarion folder:
- [ ] All DLLs from output folder
- [ ] ProjectName.manifest
- [ ] wwwroot/ folder with all content
- [ ] Metadata files (.header, .details, .events, .methods)

## Validation Report Format

```
=== WebView2 COM Control Validation Report ===

Project: [ProjectName]
Path: [ProjectPath]

--- Standard COM Validation ---
[PASS] Interface GUID valid: {guid}
[PASS] Events GUID valid: {guid}
[PASS] Class GUID valid: {guid}
[PASS] TypeLib GUID valid: {guid}
[PASS] All GUIDs are unique
[PASS] Manifest uses clrClass
[PASS] ProgId matches: Namespace.ClassName

--- WebView2 Validation ---
[PASS] WebView2 NuGet package referenced
[PASS] wwwroot folder exists
[PASS] wwwroot/controls/index.html exists
[PASS] Virtual host mapping configured
[PASS] Host object implemented
[PASS] WebView2 initialization pattern correct

--- Deployment Validation ---
[PASS] DLL built successfully
[PASS] WebView2Loader.dll present
[PASS] wwwroot deployed to output
[PASS] Files copied to Clarion folder

=== Validation Complete ===
Issues Found: 0
Status: READY FOR DEPLOYMENT
```

## Common Issues and Remediation

### Issue: WebView2Loader.dll missing
**Remediation:** Ensure Microsoft.Web.WebView2 NuGet package is installed and build target copies native DLLs

### Issue: wwwroot not copied to output
**Remediation:** Add to .csproj:
```xml
<ItemGroup>
  <Content Include="wwwroot\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

### Issue: Virtual host mapping not found
**Remediation:** Add in WebView2 initialization:
```csharp
_webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
    "localapp.clarioncontrols",
    wwwrootPath,
    CoreWebView2HostResourceAccessKind.Allow);
```

### Issue: Host object not accessible from JavaScript
**Remediation:** Ensure host object class has `[ComVisible(true)]` and `[ClassInterface(ClassInterfaceType.AutoDual)]` attributes

### Issue: Manifest uses comClass instead of clrClass
**Remediation:** Replace `<comClass>` with `<clrClass>` and add required attributes (name, runtimeVersion)
