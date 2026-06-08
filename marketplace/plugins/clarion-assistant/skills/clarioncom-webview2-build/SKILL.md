---
name: clarioncom-webview2-build
description: Builds WebView2 COM control projects with proper verification of WebView2 dependencies and wwwroot deployment
version: 1.1.0
---

## CRITICAL: Script Paths

All ClarionCOM scripts are located at:
```
%APPDATA%\ClarionCOM\scripts\
```

Or use this to get the path dynamically:
```bash
powershell -ExecutionPolicy Bypass -Command "[Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts'"
```

**NEVER use relative paths like `.claude/scripts/`** - always use the full path to ClarionCOM scripts.

### Get Clarion Installation Path

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

This returns the Clarion installation path like `C:\Clarion12`

# WebView2 COM Build Skill

This skill builds WebView2-based COM control projects with additional verification for WebView2 dependencies and wwwroot content.

## CRITICAL: Use Visual Studio MSBuild

**DO NOT use:**
- `dotnet build` - Will fail with MSB4803 error
- `dotnet msbuild` - Same issue

**USE:**
```cmd
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" ProjectName.csproj -p:Configuration=Release
```

### Finding MSBuild

```powershell
powershell -Command "@('C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio') | ForEach-Object { Get-ChildItem -Path $_ -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue } | Select-Object -First 1 -ExpandProperty FullName"
```

## Pre-Build Verification

Before building, verify:

### 1. Project Structure
- [ ] .csproj file exists
- [ ] WebView2Control.cs (or control implementation) exists
- [ ] IControlName.cs interface exists
- [ ] IControlNameEvents.cs events interface exists
- [ ] Properties/AssemblyInfo.cs exists
- [ ] ControlName.manifest exists

### 2. wwwroot Folder (WebView2-specific)
- [ ] wwwroot folder exists
- [ ] wwwroot/controls/{name}/index.html exists
- [ ] wwwroot/css/ folder exists
- [ ] wwwroot/js/ folder exists

### 3. NuGet Packages in .csproj
- [ ] Microsoft.Web.WebView2 package referenced
- [ ] Newtonsoft.Json package referenced
- [ ] Database package referenced (if using data access)

## Build Process

### Step 0: Check WebView2 Version Compatibility (Optional)

**IMPORTANT:** Before building, check if existing WebView2 controls are deployed to avoid version mismatch errors at runtime.

```bash
powershell -ExecutionPolicy Bypass -File "$env:APPDATA\ClarionCOM\scripts\check-webview2-version.ps1" -CsprojPath "ProjectName.csproj" -ClarionPath "C:\Clarion12"
```

**Results:**
- `NO_EXISTING` - No existing WebView2 DLL found, using project default version
- `VERSION_MATCHED:x.x.x.x` - Current version matches existing, proceed with build
- `VERSION_UPDATED:x.x.x.x` - Updated .csproj to match existing version, proceed with build

**Why this matters:** If multiple WebView2 controls use different WebView2 runtime versions, you'll get runtime errors when both are loaded in the same Clarion application.

### Step 1: Version Management (Optional)

Check and increment version:

```bash
powershell -ExecutionPolicy Bypass -File "$env:APPDATA\ClarionCOM\scripts\increment-build-version.ps1" read "ProjectPath"
```

If script not found or NOT_CONFIGURED, skip this step - version management is optional.

### Step 2: Build Command

```cmd
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" ProjectName.csproj -p:Configuration=Release
```

### Step 3: Expected Output

The build should produce in `bin\Release\net48\`:

**Main files:**
- ProjectName.dll
- ProjectName.manifest (if copied by MSBuild target)

**WebView2 dependencies:**
- Microsoft.Web.WebView2.Core.dll
- Microsoft.Web.WebView2.WinForms.dll
- Microsoft.Web.WebView2.Wpf.dll
- WebView2Loader.dll

**JSON library:**
- Newtonsoft.Json.dll

**Database provider (if using):**
- System.Data.SqlClient.dll (SQL Server)
- Npgsql.dll (PostgreSQL)
- MySql.Data.dll (MySQL)
- System.Data.SQLite.dll (SQLite)

**Content:**
- wwwroot/ folder (copied by MSBuild target)

## Post-Build Verification

**Use the verification script to check all build outputs:**

```bash
powershell -ExecutionPolicy Bypass -File "$env:APPDATA\ClarionCOM\scripts\verify-webview2-build.ps1" "ProjectPath"
```

This script automatically verifies:
- Required DLLs (WebView2, Newtonsoft.Json, etc.)
- wwwroot folder deployment
- Control HTML files
- Metadata files (.header, .details, .events, .methods)

**Example:**
```bash
powershell -ExecutionPolicy Bypass -File "$env:APPDATA\ClarionCOM\scripts\verify-webview2-build.ps1" "H:\DevLaptop\WebKanBanCOM"
```

**Expected output:**
```
============================================================
 WebView2 Build Verification: WebKanBanCOM
============================================================

>> Checking required files...
[OK] WebKanBanCOM.dll (45.2 KB)
[OK] WebKanBanCOM.manifest (2.1 KB)
[OK] Microsoft.Web.WebView2.Core.dll (156.8 KB)
...

>> Checking wwwroot deployment...
[OK] wwwroot folder deployed
[OK] wwwroot\controls\webkanban\index.html
...

>> Checking metadata files...
[OK] WebKanBanCOM.header
...

============================================================
 BUILD VERIFICATION: PASSED
============================================================
```

### 4. DevExtreme Files (if using DevExtreme)

If the control uses DevExtreme, ensure the following files are in your project's `wwwroot\js` and `wwwroot\css` folders:
- `wwwroot\js\dx.all.js`
- `wwwroot\js\jquery.min.js`
- `wwwroot\css\dx.light.css`

**Note:** DevExtreme requires a license. These files must be manually obtained from your DevExpress installation and placed in the wwwroot folder. Trial mode will display a watermark.

## Common Build Errors

### MSB4803: RegisterAssembly not supported
**Cause:** Using .NET Core MSBuild instead of Visual Studio MSBuild
**Solution:** Use full path to Visual Studio's MSBuild.exe

### MSB3216: Cannot register assembly - access denied
**Cause:** Expected for RegFree COM - NOT an error
**Solution:** Ignore if DLL was created successfully

### WebView2Loader.dll not copied
**Cause:** Missing NuGet package or wrong target framework
**Solution:** Ensure Microsoft.Web.WebView2 package is referenced and target is net48

### wwwroot not deployed
**Cause:** Missing MSBuild target or Content include
**Solution:** Verify .csproj has:
```xml
<ItemGroup>
  <Content Include="wwwroot\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

## Build Output Summary

After successful build, report:
- Version built
- Files deployed to Clarion folder
- wwwroot deployment status
- Any warnings or issues

## Rebuild Workflow

1. Increment version
2. Run MSBuild
3. Verify DLL created
4. Verify WebView2 dependencies present
5. Verify wwwroot deployed (including DevExtreme files if used)
6. Verify metadata files generated
7. Report success/failure

## Deployment Folder Structure

When deploying to Clarion using `copy-to-clarion.ps1`:

```
C:\Clarion12\accessory\
  bin\
    ProjectName.dll
    Microsoft.Web.WebView2.Core.dll
    Microsoft.Web.WebView2.WinForms.dll
    WebView2Loader.dll
    Newtonsoft.Json.dll
    [Database provider DLL if using]
  resources\
    ProjectName.manifest
    ProjectName.header
    *.details, *.events, *.methods
    wwwroot\
      css\
        styles.css
        dx.light.css (if DevExtreme)
      js\
        dx-config.js
        dx.all.js (if DevExtreme)
        jquery.min.js (if DevExtreme)
      controls\
        {controlname}\
          index.html
          app.js
```
