---
name: clarioncom-deploy
# prettier-ignore
description: Generate deployment artifacts for Clarion COM components including batch scripts (validation and test scripts), HTML documentation, and metadata files. Auto-applies after successful COM builds. Registration-free COM only. Generates deployment files in parallel where possible. Uses simple file naming (all files share same base name as DLL).
version: 1.0.0
---

## Path Resolution - CRITICAL

Use the helper script to get CLARIONCOM_HOME (avoids shell escaping issues):

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If NOT_INSTALLED**: Stop and tell user:
> ClarionCOM is not installed. Please run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder.

**Use resolved paths:**
- Scripts: Use the resolved CLARIONCOM_HOME path + `\scripts\`
- **Note:** Avoid using `$env:APPDATA` in commands as the `$` gets stripped by Bash. Use `[Environment]::GetFolderPath('ApplicationData')` instead.

# Deploy Clarion COM Skill

This skill automates the deployment setup for Clarion COM components by generating batch files and documentation with project-specific details.

## �?��? CRITICAL RULES - READ FIRST �?��?

### ALWAYS Offer to Copy Files After Deployment
**After generating deployment artifacts, you MUST proceed to Step 8 (Copy Files to Clarion)**
- Do NOT stop after reporting completion
- Do NOT consider the task complete until you've offered to copy files
- The copy step is MANDATORY - always ask where to copy files

### NEVER Register the Control - Registration-Free COM ONLY
**DO NOT register the COM control using RegAsm, RegSvcs, or any registration method**
- These components use **registration-free COM** (manifest-based activation) EXCLUSIVELY
- Traditional COM registration is NEVER used and MUST NOT be performed
- Do not suggest, offer, or provide registration options
- Do not use RegAsm.exe, RegSvcs.exe, or any registry-based registration
- Do not create or maintain any registration-based deployment workflows
- The manifest file provides ALL necessary COM activation information
- Registration interferes with manifest-based activation and will cause failures
- Only deployment method: Copy DLL + manifest to the same directory as the Clarion executable

### NEVER Run or Offer to Run Tests
**DO NOT execute or suggest running any test scripts**
- Do not run CheckDotNetVersion.bat, TestManifests.bat, or any other test files
- Do not offer to run tests after building
- Do not suggest testing the component
- Testing is the user's responsibility
- The batch files are for manual debugging and stay in the project folder (not copied to Clarion)

**These rules apply throughout the entire workflow - during creation, building, and deployment.**

## When to Use This Skill

Use this skill:
- After creating a new COM component with `clarioncom-create`
- After building a COM project with `clarioncom-build`
- When COM interface changes (new methods, events, or properties)
- To regenerate deployment artifacts with updated information

## What This Skill Does

Automatically generates deployment artifacts in the `ProjectName/Clarion/accessory/` folder structure:

```
ProjectName/Clarion/
??? accessory/
    ??? bin/                    ? DLLs go here
    ?   ??? ProjectName.dll
    ?   ??? [dependency DLLs]
    ??? resources/              ? All other files go here
        ??? ProjectName.manifest
        ??? ProjectName.header
        ??? ProgID.details
        ??? ProgID.events
        ??? ProgID.methods
        ??? readme_ProjectName.html
        ??? CheckDotNetVersion.bat  (not copied to Clarion)
        ??? TestManifests.bat       (not copied to Clarion)
```

This structure mirrors the Clarion installation's `accessory` folder, enabling drag & drop deployment.

**Generated Files:**
1. **2 Batch Files** - Validation scripts for registration-free COM (stay in project folder, not copied to Clarion)
2. **readme_ProjectName.html** - Complete integration documentation
3. **Metadata files** - .header (assembly name), .details/.events/.methods (ProgID)
4. **Extracts project details** - GUIDs, ProgId, methods, events from source code

**Note:** Batch files (CheckDotNetVersion.bat, TestManifests.bat) remain in the project folder for debugging. They are NOT copied to the Clarion installation to avoid naming collisions between projects.

**File Naming Convention:**
- DLL, manifest, header use **assembly name**
- Metadata files (.details, .events, .methods) use **ProgID**
- Example: `ProgID.details`, `ProgID.events`, `ProgID.methods`

## Prerequisites

Before running this skill:
- COM project must be created and built
- DLL and manifest must exist in `bin/Release/net48/` or `bin/x86/Release/net48/`
- Source files must exist (IInterface.cs, ImplementationControl.cs, AssemblyInfo.cs)

## ??�? MANIFEST VALIDATION AND FIX GUIDE

### Before Deployment - Verify Manifest is Correct

**The #1 cause of deployment failure is incorrect manifest format!**

### Quick Check

Run this to verify manifest uses correct format:

```powershell
powershell -Command "Get-Content ProjectName.manifest | Select-String -Pattern '<clrClass'"
```

**Expected result:** Should display a line containing `<clrClass`
**If empty:** Manifest is WRONG - fix it before proceeding!

### What to Look For

**✅ CORRECT Manifest (uses `<clrClass>`):**
```xml
<clrClass
    clsid="{...}"
    progid="..."
    threadingModel="Apartment"
    name="Namespace.ClassName"
    runtimeVersion="v4.0.30319">
</clrClass>
```

**�?� WRONG Manifest (uses `<comClass>`):**
```xml
<comClass
    clsid="{...}"
    threadingModel="Apartment"
    progid="...">
    <!-- This will NOT work! -->
</comClass>
```

### How to Fix an Incorrect Manifest

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

### Required Manifest Attributes Checklist

- [ ] Uses `<clrClass>` element (NOT `<comClass>`)
- [ ] Includes `runtimeVersion="v4.0.30319"`
- [ ] Includes `name="Namespace.ClassName"` (fully qualified)
- [ ] `<clrClass>` is placed BEFORE `<file>` element
- [ ] Includes `processorArchitecture="x86"` in `<assemblyIdentity>`

### Why This Matters

**Impact of using wrong manifest format:**
- �?� Component builds successfully but doesn't activate
- �?� Clarion shows "Could not create COM object" error
- �?� Registration-free COM activation completely fails
- �?� Windows treats it as native COM (tries registry lookup)
- ✅ Using `<clrClass>` = Component works perfectly

**Prevention:** Always use the template from `clarioncom-create.md` skill!

## Step-by-Step Process

### Step 1: Identify Project

Find the COM project to deploy:
- Look for .csproj file in current directory or subdirectories
- Extract project name from .csproj filename (e.g., `CalendarPickerCOM.csproj` → `CalendarPickerCOM`)

### Step 2: Extract COM Details from Source Code

**From Implementation File (e.g., `CalendarPickerControl.cs`):**
- Find the class with `[ComVisible(true)]` attribute
- Extract CLSID from `[Guid("...")]` attribute
- Extract ProgId from `[ProgId("...")]` attribute
- Extract fully qualified class name from namespace and class declaration
- Find all public methods (from interface implementation)
- Find event delegate declarations (e.g., `public event SomethingChangedDelegate SomethingChanged`)

**From Interface File (e.g., `ICalendarPicker.cs`):**
- Find interface with `[ComVisible(true)]` attribute
- Extract Interface GUID from `[Guid("...")]` attribute
- Extract all method signatures with `[DispId(...)]` attributes
- Extract XML documentation comments for each method

**From AssemblyInfo.cs:**
- Extract Assembly TypeLib GUID from `[assembly: Guid("...")]` attribute

**From Event Interface File (if exists, e.g., `ICalendarPickerEvents.cs`):**
- Extract event interface GUID
- Extract event method signatures

### Step 3: Create/Verify Clarion Folder Structure

Create the accessory folder structure if it doesn't exist:

```bash
mkdir -p "ProjectName/Clarion/accessory/bin"
mkdir -p "ProjectName/Clarion/accessory/resources"
```

**Copy files to correct locations:**

1. **Copy DLLs to `accessory/bin/`:**
   ```bash
   cp bin/Release/net48/*.dll "ProjectName/Clarion/accessory/bin/"
   ```

2. **Copy manifest to `accessory/resources/`:**
   ```bash
   cp bin/Release/net48/*.manifest "ProjectName/Clarion/accessory/resources/"
   ```

**Verify files are present:**
- `accessory/bin/ProjectName.dll` - Main COM DLL (required)
- `accessory/bin/*.dll` - Any dependency DLLs
- `accessory/resources/ProjectName.manifest` - RegFree COM manifest (required)

### Step 4: Generate Batch Files

Generate 3 batch files in `ProjectName/Clarion/accessory/resources/` with project-specific substitutions:

#### CheckDotNetVersion.bat Template

Use the exact same content as CalendarPickerCOM, just replace:
- `CalendarPickerCOM` → `{ProjectName}`

#### TestManifests.bat Template

```batch
@echo off
REM Test registration-free COM with manifests (Manual Testing Version)

echo ============================================
echo Testing Registration-Free COM Setup
echo ============================================
echo.
echo This script validates the DLL and manifest files
echo for registration-free COM deployment.
echo.

REM Get current directory (where DLL and manifest should be)
set DEPLOY_DIR=%~dp0

echo Checking required files...
echo.

set ALL_FILES_EXIST=1

if not exist "%DEPLOY_DIR%{ProjectName}.dll" (
    echo ERROR: {ProjectName}.dll not found in current directory
    set ALL_FILES_EXIST=0
) else (
    echo [OK] {ProjectName}.dll found
)

if not exist "%DEPLOY_DIR%{ProjectName}.manifest" (
    echo ERROR: {ProjectName}.manifest not found in current directory
    set ALL_FILES_EXIST=0
) else (
    echo [OK] {ProjectName}.manifest found
)

REM Also check if the WRONG filename exists
if exist "%DEPLOY_DIR%{ProjectName}.dll.manifest" (
    echo.
    echo WARNING: Found {ProjectName}.dll.manifest - this is WRONG for Clarion!
    echo Clarion requires {ProjectName}.manifest (without .dll)
    echo Please rename {ProjectName}.dll.manifest to {ProjectName}.manifest
    echo.
    set ALL_FILES_EXIST=0
)

if %ALL_FILES_EXIST%==0 (
    echo.
    echo Missing or incorrectly named files for registration-free COM
    echo.
    echo The following files must be in: %DEPLOY_DIR%
    echo   - {ProjectName}.dll
    echo   - {ProjectName}.manifest (NOT {ProjectName}.dll.manifest!)
    echo.
    pause
    exit /b 1
)

echo.
echo All required files found.
echo.

REM Check if DLL is registered
echo Checking if DLL is currently registered with COM...
reg query "HKEY_CLASSES_ROOT\{ProgId}" >nul 2>&1
if %errorLevel% equ 0 (
    echo.
    echo WARNING: {ProjectName}.dll is currently REGISTERED with COM
    echo.
    echo WARNING: Registration interferes with registration-free COM activation!
    echo This component is designed for registration-free deployment only.
    echo For correct operation, the DLL must NOT be registered.
    echo.
    echo Press any key to continue testing anyway...
    pause >nul
) else (
    echo [OK] DLL is NOT registered (correct for registration-free COM)
)

echo.
echo ============================================
echo Manifest Validation
echo ============================================
echo.

REM Parse and validate manifest file
echo Checking {ProjectName}.manifest...

findstr /C:"{ClassGuidNoBraces}" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Contains correct CLSID
) else (
    echo   [ERROR] CLSID not found
    echo   Expected: {{{ClassGuid}}}
)

findstr /C:"{ProgId}" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Contains correct ProgID
) else (
    echo   [ERROR] ProgID not found
    echo   Expected: {ProgId}
)

findstr /C:"clrClass" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Uses clrClass element (correct for .NET COM)
) else (
    echo   [WARNING] clrClass element not found - may use comClass instead
    findstr /C:"comClass" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
    if %errorLevel% equ 0 (
        echo   [ERROR] Uses comClass - this is WRONG for .NET COM components!
        echo   Should use clrClass element with runtimeVersion
    )
)

findstr /C:"runtimeVersion" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Runtime version specified
) else (
    echo   [WARNING] Runtime version not specified in manifest
)

findstr /C:"processorArchitecture=\"x86\"" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Processor architecture set to x86
) else (
    echo   [ERROR] Processor architecture not x86
)

echo.
echo ============================================
echo File Timestamps
echo ============================================
echo.
echo Checking file dates...
dir "%DEPLOY_DIR%{ProjectName}.*" /T:W

echo.
echo ============================================
echo Next Steps for Integration
echo ============================================
echo.
echo To use this COM component in your Clarion application:
echo.
echo 1. Copy these files to your Clarion application directory:
echo      - {ProjectName}.dll
echo      - {ProjectName}.manifest
echo.
echo 2. In your Clarion app, use this ProgId:
echo      {ProgId}
echo.
{MethodsList}
echo.
echo If COM creation fails, check:
echo   - .NET Framework 4.8+ is installed
echo   - Manifest file is correctly named (no .dll in the name)
echo   - Both DLL and manifest are in the same folder as your executable
echo   - The DLL is NOT registered (registration-free COM requirement)
echo.

pause
```

**Substitutions:**
- `{ProjectName}` → Actual project name
- `{ProgId}` → Extracted ProgId
- `{ClassGuid}` → Class GUID with braces
- `{ClassGuidNoBraces}` → Class GUID without braces (for findstr)
- `{MethodsList}` → Generated list like:
  ```
  echo 4. Available COM methods:
  echo      - GetSelectedDate() - Returns selected date
  echo      - SetDate(string)   - Sets calendar date
  ```

### Step 5: Generate ProjectName.html Documentation

Generate `readme_ProjectName.html` in `ProjectName/Clarion/accessory/resources/`.

�?��? **CRITICAL: DO NOT GENERATE CLARION CODE EXAMPLES**

**NEVER include Clarion code examples in the documentation.**
- The user has Clarion templates that generate correct code
- Writing Clarion code examples will likely be incorrect
- Focus on COM interface documentation only

Generate comprehensive HTML documentation with sections:

1. **Quick Start** - Files included, requirements
2. **Required Files and Dependencies** - List ALL files needed:
   - Main COM DLL and manifest
   - Any additional dependency DLLs (NuGet packages, native DLLs)
   - Sample data files (databases, config files)
   - Note that all files must be in the same directory as the Clarion executable
3. **COM Component Information** - ProgId, CLSID, TypeLib GUID
4. **Integration Options** - Registration-free vs. traditional
5. **Integration Instructions** - How to add OLE control and set ProgID (NO CODE EXAMPLES)
6. **Available Properties and Methods** - List what's available (NO CODE EXAMPLES)
7. **Exposed Methods** - For each method extracted from interface:
   - Method signature
   - Parameter descriptions
   - Return type
   - **NO Clarion usage examples**
8. **COM Events** - For each event extracted:
   - Event signature
   - Parameter descriptions
   - **NO Clarion usage examples**
9. **Date/Data Format** - If applicable
10. **Troubleshooting** - Common errors
11. **Testing** - Batch file descriptions
12. **Quick Reference Card** - Summary table

**What to include instead of code examples:**
- Property/method names and descriptions
- Parameter types and purposes
- Simple integration steps (add OLE control, set ProgID, copy files)
- File requirements (DLL + manifest in same folder)

**What NOT to include:**
- �?� Clarion code snippets
- �?� Variable declarations in Clarion syntax
- �?� Clarion-specific code examples
- �?� Method call examples in Clarion

**Extract actual documentation:**
- Parse XML comments from C# source files
- Use `/// <summary>` tags for method descriptions
- Use `/// <param>` tags for parameter descriptions
- Use `/// <returns>` tags for return value descriptions

**Detect and document dependencies:**
- Check the `Clarion/accessory/bin/` folder for additional DLL files (beyond the main COM DLL)
- List all `.dll`, `.db`, `.db3`, `.sqlite`, `.json`, `.xml`, `.ini` files found
- Group them by type:
  - .NET dependency DLLs (e.g., Newtonsoft.Json.dll, System.Data.SQLite.dll)
  - Native DLLs (e.g., sqlite3.dll, zlibwapi.dll)
  - Data files (e.g., *.db3, *.sqlite)
  - Configuration files (e.g., *.json, *.ini)
- Include file sizes for reference
- Note in the documentation that ALL these files must be deployed together
- If no additional files are found besides the COM DLL and manifest, state "No additional dependencies required"

### Step 6: Generate Metadata Files with ProgID Naming

**First, read the Clarion path** using the helper script:
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

Generate metadata files in `ProjectName/Clarion/accessory/resources/` using the ProgID:
- `ProjectName.header` - Assembly, ProgID info, and Clarion path (assembly name)
- `ProgID.details` - Control metadata (ProgID name)
- `ProgID.events` - Event definitions (ProgID name)
- `ProgID.methods` - Property and method definitions (ProgID name)

**Header file format** (must include all sections for Clarion template compatibility):
```
[ClarionPath]
C:\Clarion12
[ControlType]
CSharp
[Description]
Short description of the control from AssemblyDescription attribute
[DLL]
ProjectName
[Version]
1.0.0.0
[DllsToCopy]
ProjectName.dll
DependencyOne.dll
DependencyTwo.dll
[ProgID]
Namespace.ClassName
```

**Important:** The `[DllsToCopy]` section lists ALL DLL files that need to be deployed with this control. This section is critical for the Clarion template to know which files to copy during application deployment.

**To generate `[DllsToCopy]` section:**

1. Scan the build output folder (`bin/Release/net48/` or `bin/x86/Release/net48/`) for all `.dll` files
2. List each DLL file name (with extension) on a separate line
3. Always include the main COM DLL first
4. Include all dependency DLLs (NuGet packages, native DLLs, etc.)

**Example scanning command:**
```powershell
Get-ChildItem -Path "bin/Release/net48/" -Filter "*.dll" | ForEach-Object { $_.Name }
```

**Example output for header:**
```
[DllsToCopy]
GridControlCOM.dll
System.Data.SqlClient.dll
Newtonsoft.Json.dll
```

**Where to get other header values:**
- `[ControlType]` ? Always `CSharp` for .NET COM controls
- `[Description]` ? Extract from `AssemblyDescription` attribute in AssemblyInfo.cs
- `[Version]` ? Extract from `AssemblyVersion` or `AssemblyFileVersion` in AssemblyInfo.cs or from the `.env` file

### Step 7: Report Completion

Display summary:
```
Deployment artifacts created in: ProjectName/Clarion/accessory/

To accessory/bin:
  ? ProjectName.dll
  ? [dependency DLLs if any]

To accessory/resources:
  ? ProjectName.manifest
  ? ProjectName.header
  ? ProgID.details
  ? ProgID.events
  ? ProgID.methods
  ? readme_ProjectName.html
  ? CheckDotNetVersion.bat  (project folder only)
  ? TestManifests.bat       (project folder only)

Key Information:
- ProgId: {ProgId}
- CLSID: {ClassGuid}
- TypeLib: {TypeLibGuid}
- Methods: {MethodCount}
- Events: {EventCount}
- DLLs to deploy: {DllCount} file(s) - listed in [DllsToCopy] section of .header

REGISTRATION-FREE COM DEPLOYMENT:
All files in ProjectName/Clarion/accessory/ are for registration-free deployment.
NO registration of the DLL should be performed.

To deploy to Clarion:
1. Drag & drop the entire accessory folder to your Clarion installation
   OR copy files manually:
   - DLLs from accessory/bin/ ? C:\Clarion12\accessory\bin\
   - Resources from accessory/resources/ ? C:\Clarion12\accessory\resources\
2. Use ProgId '{ProgId}' to create COM object
3. See readme_ProjectName.html for complete integration instructions
4. NEVER register the DLL - registration-free COM only
```

### Step 8.0: Verify Clarion Path (BEFORE COPYING)

Before copying files, verify the Clarion installation path exists and confirm with the user.

**8.0.1 Read current Clarion path:**

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

**8.0.2 Validate the path exists:**

```powershell
powershell -Command "if (Test-Path '{ClarionPath}') { 'EXISTS' } else { 'NOT_FOUND' }"
```

**8.0.3 If NOT_FOUND or NOT_CONFIGURED:**

Warn the user and prompt for correction:

Use AskUserQuestion:
- **Question**: "Clarion path '{ClarionPath}' does not exist or is not configured. Please select the correct path:"
- **Header**: "Clarion Path"
- **Options**:
  1. **C:\Clarion12** - "Standard Clarion 12 installation"
  2. **C:\Clarion11** - "Standard Clarion 11 installation"
  3. **Skip copying** - "Don't copy to Clarion installation now"

If user provides a path:
- Validate it exists
- Save to config: `powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion-write '{NewPath}'"`

**8.0.4 If path EXISTS - Confirm with user:**

Use AskUserQuestion:
- **Question**: "Files will be copied to: {ClarionPath}. Is this correct?"
- **Header**: "Confirm path"
- **Options**:
  1. **Yes, copy to {ClarionPath}** - "Proceed with copying files"
  2. **Change path** - "Use a different Clarion installation"
  3. **Skip copying** - "Don't copy to Clarion installation"

**8.0.5 If user selects "Change path":**

- Ask for new path (same as 8.0.3)
- Validate new path exists
- Save to .clarioncom.env
- Continue to Step 8

**8.0.6 If user selects "Skip copying":**

- Report: "Skipping copy to Clarion. Files are available in ProjectName/Clarion/accessory/"
- End the workflow

### Step 8: Copy Files to Clarion

After deployment artifacts are generated in the `ProjectName/Clarion/` folder, copy files to the user's Clarion installation.

**8.1 Get Paths:**

Get CLARIONCOM_HOME for the copy script:
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

Get Clarion path (already validated in Step 8.0):
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

**8.2 Ask User Where to Copy:**

Use AskUserQuestion to present options:

**Question**: "Where would you like to copy the deployment files?"
**Header**: "Copy files"
**Options**:
1. **Clarion accessory folder** - "Copy DLLs to accessory\bin, others to accessory\resources"
2. **App folder** - "Copy all files to a specific application folder"
3. **Skip** - "Don't copy files now"

**8.3 Execute Copy:**

**USE THE HELPER SCRIPT - Do not construct copy commands manually!**

Replace `{CLARIONCOM_HOME}` and `{ClarionPath}` with the actual values obtained from step 8.1.

**If "Clarion accessory folder":**
```powershell
powershell -ExecutionPolicy Bypass -File "{CLARIONCOM_HOME}\scripts\copy-to-clarion.ps1" -ProjectFolder "ProjectName\Clarion\accessory" -ClarionPath "{ClarionPath}" -Target "accessory"
```

Note: The script reads from the project's `accessory/bin` and `accessory/resources` subfolders.

**If "App folder":**
- Ask user: "Enter the full path to your application folder:"
```powershell
powershell -ExecutionPolicy Bypass -File "{CLARIONCOM_HOME}\scripts\copy-to-clarion.ps1" -ProjectFolder "ProjectName\Clarion\accessory" -Target "appfolder" -AppFolder "{AppFolder}"
```

**The script automatically copies ALL files - DLLs to bin, everything else to resources.**

**8.4 Confirm Copy:**

List ALL files that were copied (check the actual folder contents):
```
? Copied to Clarion accessory folders:

To accessory\bin:
  - ProjectName.dll

To accessory\resources:
  - ProjectName.manifest        ? CRITICAL for RegFree COM!
  - ProjectName.header
  - readme_ProjectName.html     ? Note: filename starts with readme_
  - ProgID.details
  - ProgID.events
  - ProgID.methods
```

## Error Handling

**If project not found:**
- List available .csproj files in current directory
- Ask user which project to deploy

**If source files not found:**
- Report which files are missing
- Suggest running clarioncom-create first

**If GUIDs not found:**
- Report which GUIDs are missing
- Cannot proceed without proper COM attributes

**If DLL/manifest not in output folder:**
- Suggest running clarioncom-build first
- Check both `bin/Release/net48/` and `bin/x86/Release/net48/`

## Integration with Other Skills

This skill works alongside:
- **clarioncom-create** - Creates the COM component
- **clarioncom-build** - Builds the component
- **MSBuild targets** - Auto-copies DLL and manifest

Typical workflow:
1. User asks for new COM control
2. clarioncom-create creates C# files
3. clarioncom-build builds DLL
4. MSBuild auto-copies DLL + manifest to Clarion/
5. **This skill generates batch files + HTML documentation + metadata files**

## Example Usage

```
User: "Set up deployment for CalendarPickerCOM"

Assistant actions:
1. Find CalendarPickerCOM.csproj
2. Parse CalendarPickerControl.cs for CLSID, ProgId, methods
3. Parse ICalendarPicker.cs for Interface GUID, method signatures
4. Parse AssemblyInfo.cs for TypeLib GUID
5. Parse ICalendarPickerEvents.cs for event signatures
6. Generate 5 batch files with extracted details
7. Generate ProjectName.html with method documentation
8. Report completion
```

## Notes

- Batch files use `%~dp0` for relative paths (works from any location)
- HTML documentation format follows established structure
- **File naming**: DLL/manifest/header use assembly name; .details/.events/.methods use ProgID
- GUID format handling:
  - Stored with braces: `{GUID}`
  - Stored without braces for findstr: `GUID`
- Method list generation includes all public interface methods
- Event list generation includes all event delegate declarations
- Date format documentation only included if methods use date/time parameters
