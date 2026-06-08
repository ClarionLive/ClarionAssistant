---
name: clarioncom-build
# prettier-ignore
description: Compile C# COM projects for Clarion using MSBuild with correct paths, error handling, and build verification. Supports public releases with changelog management. Auto-applies for building .NET Framework COM components. Verification steps use parallel execution.
version: 1.2.0
changelog:
  - version: 1.2.0
    date: 2026-01-16
    changes:
      - Added Step 5.4 to copy deployment files to marketplace-submission/files/
      - Build now prepares files for marketplace submission automatically
  - version: 1.1.0
    changes:
      - Previous release
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

# Compile Clarion COM Component Skill

This skill provides the correct procedures for compiling C# COM components (.NET Framework) for use with Clarion applications, particularly focusing on registration-free COM activation.

## ⚠️ CRITICAL RULES - READ FIRST ⚠️

### ALWAYS Copy Files After Build
**After a successful build, you MUST proceed to Step 5 (Copy Files to Clarion)**
- Do NOT stop after verifying build output
- Do NOT consider the task complete until files are copied
- The copy step is MANDATORY - always copy to the Clarion accessory folder

### NEVER Register the Control
- These components use **registration-free COM** (manifest-based activation)
- Traditional COM registration is NOT required and should NOT be performed
- Do not use RegAsm.exe to register the DLL
- The manifest file provides all necessary COM activation information

### NEVER Run or Offer to Run Tests
**DO NOT execute or suggest running any test scripts**
- Do not offer to run tests after building
- Do not suggest testing the component
- Testing is the user's responsibility
- The build batch files and test scripts are provided for user convenience only

**These rules apply throughout the entire workflow - during creation, building, and deployment.**

## When to Use This Skill

Use this skill when:
- Building a .NET Framework COM component for Clarion
- You encounter build errors related to COM compilation
- You're setting up a build process for Clarion COM components (RegFree COM only)
- You need to verify manifest-based COM activation without registry dependencies

## Critical Build Requirements

### Why `dotnet build` Doesn't Work

**NEVER use `dotnet build` or `dotnet msbuild` for COM projects.**

For RegFree COM projects that compile successfully with the .NET Framework version of MSBuild, the .NET Core/SDK version of MSBuild may have compatibility issues with certain build properties. Always use the Visual Studio MSBuild.exe to ensure proper COM component generation.

**How to recognize you're using the wrong MSBuild:**
- If you see "MSBuild version" followed by ".NET" at the top of the output
- If the build command starts with `dotnet`
- If the build succeeds but you're missing manifest file generation or proper type metadata

### The Correct Build Tool

**ALWAYS use the .NET Framework version of MSBuild from Visual Studio.**

Location (typical):
```
C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe
```

## Build Procedure

### Step 0: Verify Manifest File (CRITICAL - DO THIS FIRST!)

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

### Step 1: Locate MSBuild.exe

Use PowerShell to find MSBuild if you don't know the exact path:

```powershell
powershell -Command "@('C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio') | ForEach-Object { Get-ChildItem -Path $_ -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue } | Select-Object -First 1 -ExpandProperty FullName"
```

This searches both 64-bit and 32-bit Program Files directories to find MSBuild in any Visual Studio installation.

Common locations:
- Visual Studio 2022: `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`
- Visual Studio 2022 Professional: `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe`
- Visual Studio 2022 Enterprise: `C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe`
- Visual Studio 2019: `C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe`

### Step 1.5: Version Management

Before building, check and increment the project version using the version management script.

**IMPORTANT: Shell escaping issue with `$env:APPDATA`**

When running PowerShell commands through Bash, the `$` character gets stripped. Use this pattern instead:

```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') increment 'ProjectPath'"
```

**What this does:**
- Reads the current version from the project's `.env` file
- Increments the build number (e.g., 1.0.4 → 1.0.5)
- Updates `AssemblyInfo.cs` with the new version
- Displays the new version number

**If .env doesn't exist:**
- The version script will output an error or `NOT_CONFIGURED`
- This indicates the project has not been initialized with version tracking
- The `/ClarionCOM` workflow handles initial version setup by prompting the user
- If running the skill directly, you can manually initialize:
  ```powershell
  powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') init 'ProjectPath' '1' '0'"
  ```

**Version format:**
- Major.Minor.Build (e.g., 1.0.5)
- Build number auto-increments on each build
- Major and Minor versions are set manually during initialization

### Step 1.6: Public Release (Optional)

Before building, determine if this is a public release that requires version bumping and changelog updates.

**1.6.1 Ask if this is a public release:**

Use AskUserQuestion to prompt:

**Question**: "Is this a public release?"
**Options**:
1. **Yes - Update version and changelog** - "Bump version and add changelog entry"
2. **No - Just build (development)** - "Development build, increment build number only"

**1.6.2 If YES (public release):**

**a. Ask for version bump type:**

Use AskUserQuestion to prompt:

**Question**: "What type of version bump?"
**Options**:
1. **Patch (1.0.0 → 1.0.1) - Bug fixes** - "Backwards-compatible bug fixes"
2. **Minor (1.0.0 → 1.1.0) - New features** - "New functionality, backwards-compatible"
3. **Major (1.0.0 → 2.0.0) - Breaking changes** - "Incompatible API changes"
4. **Custom - Enter specific version** - "Specify exact version number"

If "Custom" is selected, ask: "Enter the new version (e.g., 2.1.0):"

**b. Ask what changed:**

Use AskUserQuestion with free-text input:

**Question**: "What changed in this release? (Brief description)"

This description will be added to the changelog.

**c. Update/Create CHANGELOG.md:**

Check if `{ProjectRoot}/CHANGELOG.md` exists.

**If CHANGELOG.md does NOT exist**, create it with this header:

```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

```

**Then prepend the new entry** after the header (before any existing entries):

```markdown
## [{version}] - {YYYY-MM-DD}

{User's description of changes}

```

Where:
- `{version}` is the new version number (e.g., 1.1.0)
- `{YYYY-MM-DD}` is today's date
- `{User's description of changes}` is the text entered by the user

**d. Update version:**

Use the existing increment-build-version.ps1 script with the appropriate parameters based on the bump type selected.

**Note:** Use `[Environment]::GetFolderPath('ApplicationData')` to avoid shell escaping issues with `$env:APPDATA`.

For **Patch** bump:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') increment 'ProjectPath'"
```

For **Minor** bump:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') bump-minor 'ProjectPath'"
```

For **Major** bump:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') bump-major 'ProjectPath'"
```

For **Custom** version:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') set 'ProjectPath' '{Major}' '{Minor}' '{Build}'"
```

**e. Continue with normal build** (Step 2)

**1.6.3 If NO (development build):**

- Skip changelog updates
- Continue with Step 1.5 (increment build number as usual)
- Proceed to Step 2 (Build the Project)

### Step 2: Build the Project

Build the .csproj file (not the .sln file if you have issues):

```cmd
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" YourProject.csproj -restore -p:Configuration=Release
```

Or navigate to the project directory first:

```cmd
cd YourProjectFolder
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" YourProject.csproj -restore -p:Configuration=Release
```

**Important Parameters:**
- `-restore` - Restores NuGet packages before building (required for first build)
- `-p:Configuration=Release` - Build in Release mode
- You can omit `-p:Platform` - it will use the PlatformTarget from the .csproj (should be x86)

**Note:** The `-restore` flag ensures NuGet packages are restored automatically. This is required on first build or when packages change. Do NOT use `dotnet restore` separately - MSBuild handles it with this flag.

### Step 3: Verify Build Success

The build is successful when the DLL is created in the output directory:

```
ColorPickerCOM -> C:\Dev\...\bin\Release\net48\ColorPickerCOM.dll
```

**For RegFree COM:**
- Registry errors do NOT occur because we don't attempt registry registration
- The manifest file (generated or provided) contains all necessary COM activation information
- No admin rights needed for build or deployment

### Step 4: Check Build Output

After a successful build, you should have:

```cmd
ls bin/Release/net48/YourProject.dll
```

Expected files:
- `YourProject.dll` - Required (compiled COM component)
- `YourProject.pdb` - Optional (debug symbols)
- `YourProject.manifest` - Required (reg-free COM activation)

**DO NOT create .tlb files:**
For registration-free COM, type library (.tlb) files are NOT needed. The manifest file provides all necessary COM interface information. Type libraries are only created during registry registration, which we do not use in RegFree COM.

### Step 4.5: Create Clarion Folder Structure and Copy Build Output

After a successful build, create the Clarion folder structure and copy the build output files.

**Create folder structure:**
```bash
mkdir -p "PROJECT_PATH/Clarion/accessory/bin"
mkdir -p "PROJECT_PATH/Clarion/accessory/resources"
```

**Copy DLLs to accessory/bin:**
```bash
cp "PROJECT_PATH/bin/Release/net48/"*.dll "PROJECT_PATH/Clarion/accessory/bin/"
```

**Copy manifest to accessory/resources:**
```bash
cp "PROJECT_PATH/bin/Release/net48/"*.manifest "PROJECT_PATH/Clarion/accessory/resources/"
```

**Verify files are in place:**
```
Clarion/accessory/bin/
  ✓ ProjectName.dll
  ✓ [dependency DLLs if any]

Clarion/accessory/resources/
  ✓ ProjectName.manifest
```

### Step 4.6: Generate Metadata Files (REQUIRED)

**This step is MANDATORY** - always generate metadata files after copying build output.

Use the GenerateClarionMetadata.ps1 script to generate all metadata files including the .header with [DllsToCopy]:

```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\GenerateClarionMetadata.ps1') -ProjectDir 'PROJECT_PATH' -AssemblyName 'ProjectName' -ClarionDeployPath 'PROJECT_PATH\Clarion\accessory\resources'"
```

**What this generates:**
- `ProjectName.header` - Assembly info with **[DllsToCopy] section** listing all DLLs
- `ProgID.details` - Control metadata for each COM class
- `ProgID.methods` - Method and property definitions
- `ProgID.events` - Event definitions (if control has events)

**Verify metadata files are in place:**
```
Clarion/accessory/resources/
  ✓ ProjectName.manifest
  ✓ ProjectName.header      ← Must contain [DllsToCopy] section!
  ✓ ProgID.details
  ✓ ProgID.methods
  ✓ ProgID.events
```

**Check the .header file contains [DllsToCopy]:**
```powershell
powershell -Command "Get-Content 'PROJECT_PATH\Clarion\accessory\resources\ProjectName.header' | Select-String 'DllsToCopy'"
```

If it shows `[DllsToCopy]`, the metadata generation was successful.

**Optional: Generate HTML documentation and batch files**

For complete deployment with HTML docs and test batch files, run the `clarioncom-deploy` skill. However, the essential metadata files (.header with [DllsToCopy], .details, .methods, .events) are already generated by Step 4.6.

### Step 5.0: Verify Clarion Path (BEFORE COPYING)

Before copying files, verify the Clarion installation path exists and confirm with the user.

**5.0.1 Read current Clarion path:**

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

**5.0.2 Validate the path exists:**

```powershell
powershell -Command "if (Test-Path '{ClarionPath}') { 'EXISTS' } else { 'NOT_FOUND' }"
```

**5.0.3 If NOT_FOUND or NOT_CONFIGURED:**

Warn the user and prompt for correction:

Use AskUserQuestion:
- **Question**: "Clarion path '{ClarionPath}' does not exist or is not configured. Please select the correct path:"
- **Header**: "Clarion Path"
- **Options**:
  1. **C:\Clarion12** - "Standard Clarion 12 installation"
  2. **C:\Clarion11** - "Standard Clarion 11 installation"
  3. **Skip copying** - "Deploy to project folder only, don't copy to Clarion"

If user provides a path:
- Validate it exists
- Save to config: `powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion-write '{NewPath}'"`

**5.0.4 If path EXISTS - Confirm with user:**

Use AskUserQuestion:
- **Question**: "Files will be copied to: {ClarionPath}. Is this correct?"
- **Header**: "Confirm path"
- **Options**:
  1. **Yes, copy to {ClarionPath}** - "Proceed with copying files"
  2. **Change path** - "Use a different Clarion installation"
  3. **Skip copying** - "Don't copy to Clarion installation"

**5.0.5 If user selects "Change path":**

- Ask for new path (same as 5.0.3)
- Validate new path exists
- Save to .clarioncom.env
- Continue to Step 5

**5.0.6 If user selects "Skip copying":**

- Report: "Skipping copy to Clarion. Files are available in ProjectName/Clarion/accessory/"
- Skip to Step 5.4 (marketplace submission folder)

### Step 5: Copy Files to Clarion

After creating the Clarion folder structure with build output files, offer to copy files to the user's Clarion installation.

**5.1 Check for Clarion Path:**

Use the helper script to read the configured path (avoids shell escaping issues):
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

If output is `NOT_CONFIGURED`, inform the user and skip copying. The `/ClarionCOM` command will prompt for the path on first use.

**5.2 Copy to Clarion Accessory Folder:**

**ALWAYS copy to the Clarion accessory folder.** Do not offer other options - application folder deployment is handled on the Clarion side.

**USE THE HELPER SCRIPT - Do not construct copy commands manually!**

First, get CLARIONCOM_HOME and ClarionPath using:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

Then use those paths (replace `{CLARIONCOM_HOME}` and `{ClarionPath}` with the actual values):
```powershell
powershell -ExecutionPolicy Bypass -File "{CLARIONCOM_HOME}\scripts\copy-to-clarion.ps1" -ProjectFolder "ProjectName\Clarion\accessory" -ClarionPath "{ClarionPath}" -Target "accessory"
```

Note: The script reads from the project's `accessory/bin` and `accessory/resources` subfolders.

**The script automatically copies:**
- DLLs → `accessory\bin\`
- Support files (.manifest, .header, .details, .methods, .events, etc.) → `accessory\resources\`

**5.3 Confirm Copy:**

List ALL files that were copied:
```
To accessory\bin:
  ✓ ProjectName.dll

To accessory\resources:
  ✓ ProjectName.manifest    ← CRITICAL for RegFree COM!
  ✓ ProjectName.header
  ✓ readme_ProjectName.html
  ✓ ProgID.details
  ✓ ProgID.events
  ✓ ProgID.methods
```

### Step 5.4: Copy to Marketplace Submission Folder

Copy deployment files to `marketplace-submission/files/` for marketplace readiness. This creates a flat structure ready for submission.

```bash
# Create marketplace-submission/files folder
mkdir -p "PROJECT_PATH/marketplace-submission/files"

# Copy DLLs from bin folder
cp "PROJECT_PATH/Clarion/accessory/bin/"* "PROJECT_PATH/marketplace-submission/files/"

# Copy resources (manifest, header, details, methods, events, html)
cp "PROJECT_PATH/Clarion/accessory/resources/"* "PROJECT_PATH/marketplace-submission/files/"
```

**5.4 Confirm Marketplace Files:**

```
To marketplace-submission/files:
  ✓ ProjectName.dll
  ✓ ProjectName.manifest
  ✓ ProjectName.header
  ✓ readme_ProjectName.html
  ✓ ProgID.details
  ✓ ProgID.events
  ✓ ProgID.methods
```

These files are now ready for marketplace submission without additional preparation.

### Step 5.5: Auto-commit Changes (If Git Repository)

After a successful build and file copy, automatically commit changes if the project is a git repository. This step is **optional** and only runs if a `.git` folder exists.

**5.5.1 Check if Git Repository Exists:**

```powershell
if (Test-Path ".git") {
    # Git repo exists, proceed with auto-commit
}
```

If `.git` folder does not exist, skip this step entirely and report:
```
Skipped auto-commit (not a git repository)
```

**5.5.2 Get Version Information:**

Read the current version from the project's `.env` file or `AssemblyInfo.cs`:

```powershell
# Get version from .env file
$envFile = ".env"
if (Test-Path $envFile) {
    $envContent = Get-Content $envFile
    $major = ($envContent | Where-Object { $_ -match "^MAJOR_VERSION=" }) -replace "MAJOR_VERSION=", ""
    $minor = ($envContent | Where-Object { $_ -match "^MINOR_VERSION=" }) -replace "MINOR_VERSION=", ""
    $build = ($envContent | Where-Object { $_ -match "^BUILD_NUMBER=" }) -replace "BUILD_NUMBER=", ""
    $version = "$major.$minor.$build"
}
```

**5.5.3 Check for Uncommitted Changes:**

```powershell
$changes = git status --porcelain
```

If no changes exist, report:
```
No changes to commit
```

**5.5.4 Stage and Commit Changes:**

If changes exist, stage all changes and create a commit:

```powershell
$date = Get-Date -Format "yyyy-MM-dd"
git add .
git commit -m "Build v$version - $date"
```

Commit message format: `Build v1.0.5 - 2026-01-13`

**5.5.5 Push to Remote (Optional - Don't Fail on Error):**

Try to push changes, but never fail the build if push fails:

```powershell
try {
    git push 2>$null
    Write-Host "Changes pushed to remote"
} catch {
    Write-Host "Note: Could not push to remote (no remote configured or auth issue)"
}
```

**5.5.6 Report Results:**

Report what was done:
- Success: `Auto-committed build changes: Build v1.0.5 - 2026-01-13`
- No changes: `No changes to commit`
- Not a repo: `Skipped auto-commit (not a git repository)`
- Push failed: `Auto-committed locally (push failed - no remote or auth issue)`

**Complete Auto-commit Script:**

```powershell
# Auto-commit after successful build (only if git repo exists)
$projectPath = "ProjectPath"  # Replace with actual project path

Push-Location $projectPath
try {
    if (Test-Path ".git") {
        # Get version from .env
        $version = "unknown"
        $envFile = ".env"
        if (Test-Path $envFile) {
            $envContent = Get-Content $envFile
            $major = ($envContent | Where-Object { $_ -match "^MAJOR_VERSION=" }) -replace "MAJOR_VERSION=", ""
            $minor = ($envContent | Where-Object { $_ -match "^MINOR_VERSION=" }) -replace "MINOR_VERSION=", ""
            $build = ($envContent | Where-Object { $_ -match "^BUILD_NUMBER=" }) -replace "BUILD_NUMBER=", ""
            if ($major -and $minor -and $build) {
                $version = "$major.$minor.$build"
            }
        }

        # Check for changes
        $changes = git status --porcelain
        if ($changes) {
            $date = Get-Date -Format "yyyy-MM-dd"
            $commitMessage = "Build v$version - $date"

            git add .
            git commit -m $commitMessage

            # Try to push (don't fail if it doesn't work)
            try {
                git push 2>$null
                Write-Host "Auto-committed and pushed: $commitMessage"
            } catch {
                Write-Host "Auto-committed locally: $commitMessage"
                Write-Host "Note: Could not push to remote (no remote configured or auth issue)"
            }
        } else {
            Write-Host "No changes to commit"
        }
    } else {
        Write-Host "Skipped auto-commit (not a git repository)"
    }
} finally {
    Pop-Location
}
```

**Important Notes:**
- This step is OPTIONAL - only runs if `.git` folder exists
- NEVER fail the build because of git issues
- Git errors should be logged as warnings, not errors
- The commit happens AFTER Step 5 (Copy Files to Clarion) completes successfully

## Registration-Free COM Approach

For Clarion applications, we use **registration-free COM** which means:

1. **No registry modifications** - The component is not registered in HKEY_CLASSES_ROOT
2. **Manifest-based activation** - A .manifest file describes the COM classes
3. **Xcopy deployment** - Just copy DLL + manifest to the application directory
4. **No admin rights needed** - Neither for deployment nor runtime

### ⚠️ CRITICAL: Clarion Manifest Naming Convention

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

### What You Need to Deploy

Only two files:
1. `YourProject.dll` - The compiled component
2. `YourProject.manifest` - The manifest file (WITHOUT `.dll` in the name!)

Both files must be in the same directory as the Clarion executable.

## Complete Build Workflow

Here's the complete workflow for building a Clarion COM component:

```bash
# 1. Find MSBuild.exe
powershell -Command "Get-ChildItem -Path 'C:\Program Files\Microsoft Visual Studio' -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName"

# 2. Build the project (use the path from step 1)
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" ColorPickerCOM\ColorPickerCOM.csproj -restore -p:Configuration=Release

# 3. Verify DLL was created (ignore registration error)
ls ColorPickerCOM\bin\Release\net48\ColorPickerCOM.dll

# 4. Create/verify manifest exists (NOTE: without .dll in filename!)
ls ColorPickerCOM\bin\Release\net48\ColorPickerCOM.manifest

# 5. Done! Deploy these two files to your Clarion app directory
```

## Automatic Manifest Deployment

To avoid manually copying the manifest file to the output directory after each build, you can add an MSBuild target to your .csproj file that automates this process.

### Add to Your .csproj File

Add this target anywhere inside the `<Project>` tag (typically at the end, before the closing `</Project>`):

```xml
<Target Name="CopyManifest" AfterTargets="Build">
  <Copy SourceFiles="$(ProjectDir)$(AssemblyName).manifest"
        DestinationFiles="$(OutputPath)$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)$(AssemblyName).manifest')" />
</Target>
```

### What This Does

- **Runs automatically** after each build
- **Copies** `YourProject.manifest` from the project root to the output folder (e.g., `bin\Release\net48\`)
- **Only copies** if the source file exists
- **Skips** if the file hasn't changed (improves build performance)

### File Location

Keep your `YourProject.manifest` file in the **project root directory** (same level as the .csproj file), and this target will automatically copy it to the output folder during every build.

**Example project structure:**
```
CalendarPickerCOM/
├── CalendarPickerCOM.csproj
├── CalendarPickerCOM.manifest  ← Source (in project root)
├── ICalendarPicker.cs
├── CalendarPickerControl.cs
└── bin/
    └── Release/
        └── net48/
            ├── CalendarPickerCOM.dll
            └── CalendarPickerCOM.manifest  ← Auto-copied here
```

**Note:** Remember that Clarion requires the manifest to be named `ComponentName.manifest` (without the `.dll` extension), so name your source file accordingly.

## Troubleshooting

### "msbuild: command not found" or "msbuild is not recognized"

**Problem:** Trying to use `msbuild` without full path, or Visual Studio is not installed.

**Solution:**
1. Install Visual Studio 2022 (Community edition is free)
2. Always use the full path to MSBuild.exe
3. Use the PowerShell command above to find it

### MSBuild not found in any standard location

**Problem:** PowerShell search returns nothing, or MSBuild.exe doesn't exist in any expected location.

**Solutions:**

**Option 1: Install Visual Studio** (Recommended)
1. Download Visual Studio 2022 Community (free) from https://visualstudio.microsoft.com/
2. During installation, select the "**.NET desktop development**" workload
3. This installs MSBuild along with all necessary components
4. After installation, run the PowerShell search command again

**Option 2: Comprehensive search**
If Visual Studio is installed but MSBuild wasn't found, try a more thorough search:
```powershell
Get-ChildItem -Path C:\ -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*Visual Studio*" } | Select-Object -First 1 -ExpandProperty FullName
```
⚠️ Warning: This searches the entire C: drive and may take several minutes.

**Option 3: Install Build Tools** (Lightweight alternative)
If you don't need the full Visual Studio IDE:
1. Download "Build Tools for Visual Studio 2022" from https://visualstudio.microsoft.com/downloads/
2. Scroll to "Tools for Visual Studio" section
3. Run the installer and select "**.NET desktop build tools**"
4. This provides MSBuild without the full IDE (~2GB vs ~10GB)

**Option 4: Check installed Visual Studio versions**
```powershell
Get-ChildItem -Path "C:\Program Files\Microsoft Visual Studio" -Directory | Select-Object Name
Get-ChildItem -Path "C:\Program Files (x86)\Microsoft Visual Studio" -Directory -ErrorAction SilentlyContinue | Select-Object Name
```
This lists all installed Visual Studio versions to help locate MSBuild manually.

### "error MSB4803: The task 'RegisterAssembly' is not supported"

**Problem:** Using `dotnet build` or `dotnet msbuild`

**Solution:** Use the full .NET Framework MSBuild.exe from Visual Studio (see Step 1 above)

### Build succeeds but DLL is missing

**Problem:** Looking in the wrong output directory

**Solution:** Check `bin\Release\net48\` (or your target framework)

### "Access denied" registry error

**Problem:** Not actually a problem! The DLL compiled successfully.

**Solution:** Ignore this error for registration-free COM. Verify the DLL exists.

### Need to run as administrator?

**Problem:** Trying to register the COM component in the registry

**Solution:** Don't! Use registration-free COM with a manifest file instead. No admin rights needed.

## Project Configuration Checklist

Ensure your .csproj has these settings:

```xml
<PropertyGroup>
  <TargetFramework>net48</TargetFramework>
  <PlatformTarget>x86</PlatformTarget>
  <UseWindowsForms>true</UseWindowsForms>
  <OutputType>Library</OutputType>
  <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
</PropertyGroup>
```

**Key settings explained:**
- `TargetFramework`: net48 or net48 (NOT netcoreapp or net5.0+)
- `PlatformTarget`: x86 (required for Clarion compatibility)
- `UseWindowsForms`: true (if using Windows Forms controls)
- `OutputType`: Library (creates .dll)
- `GenerateAssemblyInfo`: false (if you have manual AssemblyInfo.cs)

**DO NOT USE:**
- `RegisterForComInterop` - NOT needed for RegFree COM
- `EnableComInterop` - NOT needed for RegFree COM
- Any registry-related settings - RegFree COM doesn't use the registry

## Build Settings for RegFree COM

For registration-free COM components used with Clarion:

**Recommended approach:**
- Do NOT add `RegisterForComInterop` or `EnableComInterop` to your .csproj
- The manifest file (XML-based) provides all necessary COM activation information
- No registry interaction occurs, no admin rights needed

**Why we avoid registry-related settings:**
- `RegisterForComInterop` attempts to write to HKEY_CLASSES_ROOT (requires admin)
- `EnableComInterop` generates .tlb files we don't need
- RegFree COM uses manifest files, not registry entries
- Simpler, more portable, no admin rights required

## Automated Build Script

Here's a complete PowerShell script for automated builds:

```powershell
# find-and-build-com.ps1

# Find MSBuild (search both Program Files locations)
$searchPaths = @(
    "C:\Program Files\Microsoft Visual Studio",
    "C:\Program Files (x86)\Microsoft Visual Studio"
)

Write-Host "Searching for MSBuild.exe..." -ForegroundColor Cyan
$msbuild = $searchPaths | ForEach-Object {
    Get-ChildItem -Path $_ -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue
} | Select-Object -First 1 -ExpandProperty FullName

if (-not $msbuild) {
    Write-Error "MSBuild.exe not found. Please install Visual Studio."
    Write-Host "Searched locations:" -ForegroundColor Yellow
    $searchPaths | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host "✓ Found MSBuild at: $msbuild" -ForegroundColor Green

# Build project
$projectName = "ColorPickerCOM"
$projectPath = "$projectName\$projectName.csproj"
$outputDir = "$projectName\bin\Release\net48"

Write-Host "`nBuilding $projectPath..." -ForegroundColor Cyan
& $msbuild $projectPath -p:Configuration=Release -v:minimal

Write-Host "`nVerifying build output..." -ForegroundColor Cyan

# Check if DLL exists (regardless of registration error)
$dllPath = "$outputDir\$projectName.dll"
if (Test-Path $dllPath) {
    Write-Host "✓ Build successful! DLL created at: $dllPath" -ForegroundColor Green

    # Check for manifest
    $manifestPath = "$outputDir\$projectName.manifest"
    if (Test-Path $manifestPath) {
        Write-Host "✓ Manifest file found: $manifestPath" -ForegroundColor Green
    } else {
        # Try to copy from project root
        $sourceManifest = "$projectName\$projectName.manifest"
        if (Test-Path $sourceManifest) {
            Copy-Item $sourceManifest $manifestPath
            Write-Host "✓ Manifest copied from project root to output" -ForegroundColor Green
        } else {
            Write-Host "⚠ Warning: Manifest file not found." -ForegroundColor Yellow
            Write-Host "  Create $sourceManifest for reg-free COM." -ForegroundColor Yellow
            Write-Host "  Remember: Clarion requires 'ComponentName.manifest' (without .dll extension!)" -ForegroundColor Yellow
        }
    }

    # Show deployment files (RegFree COM - manifest-based)
    Write-Host "`nRegFree COM files ready for deployment:" -ForegroundColor Cyan
    Write-Host "  - $dllPath" -ForegroundColor White
    if (Test-Path $manifestPath) {
        Write-Host "  - $manifestPath" -ForegroundColor White
    }
    Write-Host "  (No registry files needed - this is registration-free COM)" -ForegroundColor Gray

    # Show file sizes
    $dllSize = (Get-Item $dllPath).Length
    Write-Host "`nDLL size: $dllSize bytes" -ForegroundColor Gray

} else {
    Write-Host "✗ Build failed - DLL not created" -ForegroundColor Red
    Write-Host "Check build output above for errors." -ForegroundColor Yellow
    exit 1
}
```

## Summary

**Key Takeaways:**
1. Use Visual Studio's MSBuild.exe, NOT `dotnet build`
2. RegFree COM uses manifest files, not registry entries
3. Only the DLL and manifest files are needed for deployment
4. No registry modifications or admin rights required
5. Build success is indicated by DLL creation
6. Do NOT use RegisterForComInterop or EnableComInterop settings

**Quick Command:**
```cmd
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" YourProject.csproj -restore -p:Configuration=Release
```

**Verify Success:**
```cmd
ls bin\Release\net48\YourProject.dll
ls bin\Release\net48\YourProject.manifest
```

If both the DLL and manifest files exist, you're done! Build is complete for RegFree COM deployment.

## Changelog Format

When making public releases, the skill maintains a CHANGELOG.md file in your project root.

**Location:** `{ProjectRoot}/CHANGELOG.md`

**Format:**
```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.1.0] - 2026-01-11

### Added
- New SetDateRange method for selecting date spans

### Fixed
- Calendar not displaying correctly in dark mode

## [1.0.0] - 2026-01-05

- Initial release
```

**Tips for good changelog entries:**
- Start with a verb: Added, Fixed, Changed, Removed, Deprecated
- Be specific but concise
- Group related changes together
- This changelog is used when submitting to the COM Marketplace

**Section headers (optional but recommended):**
- `### Added` - New features
- `### Changed` - Changes to existing functionality
- `### Deprecated` - Features to be removed in future
- `### Removed` - Features removed in this release
- `### Fixed` - Bug fixes
- `### Security` - Security vulnerability fixes

## Automatic Deployment (New Projects)

If your project includes MSBuild deployment targets (added by `clarion-com-builder`), the build will also:

**Automatically copy to Clarion folder using accessory/bin/resources layout:**
- DLLs go to `accessory/bin/`
- Resources (manifest, metadata, docs) go to `accessory/resources/`
- wwwroot (WebView2 only) goes to `accessory/resources/wwwroot/`

This mirrors the Clarion installation accessory folder, enabling drag & drop deployment.

**Old documentation (structure now updated):**
```
YourProject/Clarion/
  ├── YourProject.dll        ← Auto-copied from build output
  └── YourProject.manifest   ← Auto-copied from project root
```

**MSBuild output will show:**
```
Deployed to Clarion folder: C:\...\YourProject\Clarion\
```

**What happens after build:**
1. ✅ MSBuild creates `Clarion/` folder (if doesn't exist)
2. ✅ MSBuild copies manifest to build output folder
3. ✅ MSBuild copies DLL and manifest to `Clarion/` folder
4. ✅ Files ready for deployment to Clarion applications

**To complete deployment setup:**

Run the `clarioncom-deploy` skill to generate testing and documentation files:
```
"Set up deployment for YourProject"
```

This generates:
- `CheckDotNetVersion.bat` - .NET checker
- `TestManifests.bat` - Manifest validation
- `README.md` - Integration documentation


**Result:**
```
YourProject/Clarion/           ← RegFree COM deployment package
  ├── YourProject.dll          ← Auto-updated on each build
  ├── YourProject.manifest     ← Auto-updated on each build
  ├── CheckDotNetVersion.bat   ← Generated once
  ├── TestManifests.bat        ← Generated once
  └── README.md                ← Generated once
```

**For older projects without MSBuild targets:**

You can manually copy files or add the deployment targets to your `.csproj`. See the `clarioncom-create` skill for the complete MSBuild target configuration.

**Benefits of RegFree COM approach:**
- DLL and manifest always current after builds
- No manual copy steps needed
- No registry interaction required
- No admin rights needed for deployment
- Simple xcopy deployment model
