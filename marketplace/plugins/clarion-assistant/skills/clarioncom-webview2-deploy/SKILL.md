---
name: clarioncom-webview2-deploy
description: Generates deployment artifacts for WebView2 COM controls including wwwroot handling and WebView2 runtime documentation
version: 1.0.0
---

## Path Resolution

### Get CLARIONCOM_HOME

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

# WebView2 COM Deployment Skill

This skill generates deployment artifacts for WebView2-based COM controls, with special handling for the wwwroot folder and WebView2 runtime requirements.

## NEVER Run Tests
- Do NOT execute CheckDotNetVersion.bat, TestManifests.bat, or any test scripts
- Testing is the user's responsibility
- Batch files stay in project folder for debugging (not copied to Clarion)

## NEVER Generate Clarion Code
- Do NOT write Clarion code examples in README
- Only document COM interface (ProgID, methods, properties, events)

## Deployment Artifacts to Generate

### 1. README.md

Generate comprehensive documentation including:

```markdown
# [ProjectName] - WebView2 COM Control

## IMPORTANT: WebView2 Runtime Required

This control requires the **Microsoft Edge WebView2 Runtime** to be installed on the target machine.

**Download:** https://developer.microsoft.com/microsoft-edge/webview2/

Choose the **Evergreen Bootstrapper** for automatic updates.

## COM Information

- **ProgID:** `Namespace.ControlName`
- **CLSID:** `{class-guid}`

## Files Required for Deployment

Copy ALL of the following to your application folder:

### DLL Files
- `ProjectName.dll` - Main control
- `ProjectName.manifest` - COM manifest (REQUIRED)
- `Microsoft.Web.WebView2.Core.dll` - WebView2 core
- `Microsoft.Web.WebView2.WinForms.dll` - WebView2 WinForms integration
- `WebView2Loader.dll` - WebView2 native loader
- `Newtonsoft.Json.dll` - JSON serialization
- [Database DLL if applicable]

### wwwroot Folder
The entire `wwwroot/` folder must be copied to your application folder.

## Properties

[List properties from interface]

## Methods

[List methods from interface]

## Events

[List events from events interface]

## Integration Steps

1. Copy all DLL files to your application folder
2. Copy the `wwwroot` folder to your application folder
3. Ensure WebView2 Runtime is installed on target machines
4. Add an OLE control to your Clarion window
5. Set the ProgID to: `Namespace.ControlName`
```

### 2. TestManifests.bat

Generate with wwwroot verification:

```batch
@echo off
REM WebView2 COM Control Manifest and Dependency Test

echo.
echo ========================================
echo WebView2 COM Control Validation
echo ========================================
echo.

set ERRORS=0

REM Check DLL
if exist "ProjectName.dll" (
    echo [OK] ProjectName.dll
) else (
    echo [MISSING] ProjectName.dll
    set /a ERRORS+=1
)

REM Check Manifest
if exist "ProjectName.manifest" (
    echo [OK] ProjectName.manifest
) else (
    echo [MISSING] ProjectName.manifest
    set /a ERRORS+=1
)

REM Check WebView2 Dependencies
if exist "Microsoft.Web.WebView2.Core.dll" (
    echo [OK] Microsoft.Web.WebView2.Core.dll
) else (
    echo [MISSING] Microsoft.Web.WebView2.Core.dll
    set /a ERRORS+=1
)

if exist "WebView2Loader.dll" (
    echo [OK] WebView2Loader.dll
) else (
    echo [MISSING] WebView2Loader.dll
    set /a ERRORS+=1
)

REM Check wwwroot folder
echo.
echo --- wwwroot Folder ---
if exist "wwwroot" (
    echo [OK] wwwroot folder exists
) else (
    echo [MISSING] wwwroot folder
    set /a ERRORS+=1
)

echo.
echo ========================================
if %ERRORS%==0 (
    echo All checks passed!
) else (
    echo %ERRORS% issue(s) found.
)
echo ========================================

pause
```

### 3. CheckDotNetVersion.bat

Same as standard COM, checks for .NET Framework 4.8+

### 4. TestManifests.bat

Same as standard COM - validates manifest and DLL files for RegFree COM setup.

**Note:** Batch files stay in project folder for debugging, they are not copied to Clarion installation.

## Deployment Package Structure

### Project Clarion Folder (Source)

The project uses the `accessory/bin/resources` layout that mirrors the Clarion installation structure:

```
ProjectName/Clarion/
└── accessory/                   ← Drag & drop this entire folder!
    ├── bin/
    │   ├── ProjectName.dll
    │   ├── Microsoft.Web.WebView2.Core.dll
    │   ├── Microsoft.Web.WebView2.WinForms.dll
    │   ├── WebView2Loader.dll
    │   └── Newtonsoft.Json.dll
    └── resources/
        ├── ProjectName.manifest
        ├── ProjectName.header
        ├── *.details, *.events, *.methods
        ├── readme_ProjectName.html
        ├── TestManifests.bat
        ├── CheckDotNetVersion.bat
        └── wwwroot/
            ├── css/
            │   ├── styles.css
            │   └── dx.light.css (if DevExtreme)
            ├── js/
            │   ├── dx-config.js
            │   ├── dx.all.js (if DevExtreme)
            │   └── jquery.min.js (if DevExtreme)
            └── controls/
                └── {controlname}/
                    ├── index.html
                    └── app.js
```

### Clarion Installation (Destination)

When deployed using `copy-to-clarion.ps1`:

```
C:\Clarion12\accessory\
  bin\
    ProjectName.dll
    Microsoft.Web.WebView2.Core.dll
    Microsoft.Web.WebView2.WinForms.dll
    WebView2Loader.dll
    Newtonsoft.Json.dll
  resources\
    ProjectName.manifest
    ProjectName.header
    *.details, *.events, *.methods
    wwwroot\
      (entire folder structure)
```

## Workflow

1. **Extract Information**
   - Read interface file for methods/properties
   - Read events interface for events
   - Read manifest for GUIDs and ProgID
   - Check for database provider

2. **Generate Files**
   - README.md with WebView2-specific documentation
   - TestManifests.bat with wwwroot checks
   - CheckDotNetVersion.bat
   (batch files stay in project folder, not copied to Clarion)

3. **Verify wwwroot**
   - Ensure wwwroot folder exists in Clarion folder
   - Verify control HTML/JS files present

4. **Verify DevExtreme Files (if using DevExtreme)**
   - Check for dx.all.js in wwwroot/js/
   - Check for jquery.min.js in wwwroot/js/
   - Check for dx.light.css in wwwroot/css/
   - If missing, run copy-devextreme.ps1:
     ```bash
     powershell -ExecutionPolicy Bypass -File "$CLARIONCOM_HOME\scripts\copy-devextreme.ps1" -DestinationFolder "ProjectPath\Clarion\wwwroot"
     ```

5. **Report**
   - List all generated files
   - Note WebView2 runtime requirement
   - Provide deployment instructions

## Key Differences from Standard COM Deploy

1. **WebView2 Runtime Warning** - Prominent in README
2. **wwwroot Folder** - Must be included in deployment
3. **Additional DLLs** - WebView2 dependencies listed
4. **TestManifests.bat** - Includes wwwroot verification
5. **DevExtreme Files** - If using DevExtreme, dx.all.js, jquery.min.js, dx.light.css must be present
6. **Deployment Structure** - DLLs to accessory\bin, wwwroot to accessory\resources\wwwroot
7. **Troubleshooting** - WebView2-specific issues documented
