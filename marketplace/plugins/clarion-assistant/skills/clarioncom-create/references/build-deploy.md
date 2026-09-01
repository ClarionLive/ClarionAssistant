# Build Tools, Deployment, and Verification

## Required Build Tool

**CRITICAL: Use Visual Studio MSBuild.exe** - Located at:
- Visual Studio 2022: `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`
- Other editions: Professional, Enterprise, or earlier versions in similar paths

### Finding MSBuild.exe

If you don't know where MSBuild is installed, use this PowerShell command (searches both 64-bit and 32-bit Program Files directories):

```powershell
powershell -Command "@('C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio') | ForEach-Object { Get-ChildItem -Path $_ -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue } | Select-Object -First 1 -ExpandProperty FullName"
```

### DO NOT Use

- **`dotnet build`** - Will fail with MSB4803 error on RegisterAssembly task
- **`dotnet msbuild`** - Same issue, uses .NET Core MSBuild

**Why:** The .NET Core version of MSBuild does not support the RegisterAssembly task required for COM interop. You will see this error:

```
error MSB4803: The task "RegisterAssembly" is not supported on the .NET Core version of MSBuild.
Please use the .NET Framework version of MSBuild.
```

**Solution:** Always use Visual Studio's MSBuild.exe for COM projects.

For more detailed build procedures, see the `clarioncom-build` skill.

## Build Process

**Build Command:**

```cmd
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" YourProject.csproj -p:Configuration=Release
```

Or if you've found MSBuild with PowerShell, use that path.

**Build in Release mode** for distribution to ensure optimal performance.

**Expected Output:**
- `YourProject.dll` (the COM component)
- `YourProject.tlb` (type library - auto-generated)
- `YourProject.pdb` (optional, for debugging)

**Expected "Error" - This is OK:**

You will likely see a registry access error at the end:

```
error MSB3216: Cannot register assembly - access denied.
Please make sure you're running the application as administrator.
```

**This error is NORMAL for registration-free COM!** If the DLL was created, the build succeeded. The registration error happens afterward because we're using manifest-based COM activation instead of registry registration.

## Deployment Files

For a Clarion application to use the COM component:
1. `YourProject.dll` - Your COM component
2. `YourProject.tlb` - Type library (generated automatically)
3. `YourProject.manifest` (NOT .dll.manifest - Clarion requires this naming)
4. **Additional dependency DLLs** (if any):
   - NuGet package DLLs (e.g., `Newtonsoft.Json.dll`, `System.Data.SQLite.dll`)
   - Native DLLs (e.g., `sqlite3.dll`, `zlibwapi.dll`)
   - Other referenced assemblies
5. **Sample data files** (if applicable):
   - Database files (e.g., `*.db`, `*.db3`, `*.sqlite`)
   - Configuration files (e.g., `*.json`, `*.xml`, `*.ini`)
   - Test data files

Place all files in the same directory as the Clarion executable.

**Important:** If you use the MSBuild targets from references/csproj-msbuild.md, all required files will be automatically copied to the `Clarion/` folder after each build, making deployment straightforward - just copy everything from `ProjectName/Clarion/*` to your Clarion application directory.

## Clarion Usage Pattern

In Clarion, the component is used with:
```clarion
ComObject &IYourInterface
ComObject &= CreateObject('YourNamespace.YourClassName')
IF ComObject &= NULL
    MESSAGE('Failed to create COM object')
ELSE
    ! Use the object
    ComObject.MethodOne('Hello')
    ! Clean up
    ComObject{PROP:Handle} = 0
END
```

## Deployment Artifact Generation (REQUIRED)

**CRITICAL STEP:** After the build succeeds and the DLL is created in the `Clarion/` folder, you MUST generate deployment artifacts. **This step is NOT optional** - it must be completed for every new COM component.

**What to Generate:**
1. **Register.bat** - COM registration script
2. **Unregister.bat** - COM unregistration script
3. **CheckDotNetVersion.bat** - System requirement validation
4. **TestCOM.bat / TestCOM.vbs** - Functional testing scripts
5. **TestManifests.bat** - Registration-free COM validation
6. **README.md** - Complete integration documentation

**How to Generate:**

Immediately after build completion, AUTOMATICALLY run deployment artifact generation by following the clarioncom-deploy skill steps:
1. Extract COM details (GUIDs, ProgId, methods) from source files
2. Generate all 5 batch files with project-specific substitutions
3. Generate comprehensive README.md with method documentation (NO Clarion code examples - see references/best-practices.md)
4. Verify all files created in ProjectName/Clarion/ folder

**Validation:** After generation, verify these files exist in `ProjectName/Clarion/`:
- ProjectName.dll (from MSBuild)
- ProjectName.manifest (from MSBuild)
- Register.bat, Unregister.bat, CheckDotNetVersion.bat, TestCOM.bat, TestCOM.vbs, TestManifests.bat, README.md (generated)

**When to Skip:** ONLY if:
- You are explicitly asked to create ONLY the C# source files (no build)
- You are updating existing source code (deployment docs already exist and don't need updating)

**When to Regenerate:** Whenever:
- COM interface changes (new methods, properties, or events)
- Documentation needs updating
- Testing procedures change

Reminder: NEVER run Register.bat or any of the test scripts - they are generated for the user's convenience only.

## Testing Checklist

Before delivering a COM component:
1. All three GUIDs are unique and different
2. ProgId matches in both code and manifest
3. Project builds without errors
4. .tlb file is generated in output
5. Manifest file created with correct GUIDs
6. All interface methods are implemented
7. Thread safety (InvokeRequired) is handled
8. Resources are properly disposed

## Common Mistakes to Avoid

1. **Wrong Framework:** Don't use .NET Core/.NET 5+ (Clarion needs .NET Framework)
2. **Missing ComVisible:** All three files need `[ComVisible(true)]`
3. **Duplicate GUIDs:** Each GUID must be unique
4. **Wrong Platform:** Must be x86, not AnyCPU or x64
5. **Using EnableComInterop or RegisterForComInterop:** Do NOT use these for RegFree COM - they cause registry registration which breaks manifest-based activation
6. **Wrong ProgId:** Must match exactly between code and manifest
7. **Designer dependency:** Don't use Visual Studio designer - initialize controls in code
8. **Thread unsafe:** Always use InvokeRequired pattern for UI updates
9. **Using dotnet CLI:** Don't use `dotnet build` or `dotnet msbuild` - use Visual Studio's MSBuild.exe
10. **Missing manifest in output:** Manually copy or add MSBuild target to auto-copy manifest file
11. **Panicking at registration errors:** MSB3216 "access denied" errors are expected for reg-free COM - verify DLL was created

## Step 14: Offer GitHub Repository Creation

After deployment completes successfully, offer to create a GitHub repository for the new project.

### 14.1 Prompt User

Use AskUserQuestion to offer GitHub repository creation:

**Question**: "Would you like to create a GitHub repository for this project?"
**Header**: "GitHub Repo"
**Options**:
1. **Create private repo** - "Create a private GitHub repository (recommended for development)"
2. **Create public repo** - "Create a public GitHub repository (visible to everyone)"
3. **Skip** - "Don't create a repository now"

### 14.2 If User Selects Create (Private or Public)

1. **Check GITHUB_TOKEN exists** in `~/.clarioncom.env`:
   ```powershell
   $envFile = "$env:USERPROFILE\.clarioncom.env"
   $hasToken = $false
   if (Test-Path $envFile) {
       $tokenLine = Get-Content $envFile | Where-Object { $_ -match '^GITHUB_TOKEN=' }
       $hasToken = [bool]$tokenLine
   }
   ```

2. **If no token**, display setup instructions and skip:
   ```
   GitHub token required for repository creation.
   Please visit https://clarionlive.com/com_for_clarion/marketplace/setup for setup instructions.
   Skipping GitHub repository creation.
   ```

3. **If token exists**, invoke the `clarioncom-github-init` skill with:
   - **Repo name**: Default to project name (e.g., "TimePickerCOM")
   - **Visibility**: "private" or "public" based on user selection
   - **Description**: Extract from .details file if available

### 14.3 If User Selects Skip

Display a note:
```
Skipping GitHub repository creation.
You can create a repository later using /ClarionCOM -> "Initialize GitHub Repo"
```

### 14.4 Workflow Integration

This step runs **after** step 13 (clarioncom-deploy) completes successfully.

**Timeline:**
```
Step 12: MSBuild deploys to Clarion/
Step 13: clarioncom-deploy generates docs
Step 14: Offer GitHub repo creation
Complete: Project ready for use!
```

## Automatic Deployment Summary

### What Happens Automatically

When you create a COM component with this skill, the `.csproj` file includes MSBuild targets that:

1. **Create `Clarion/` folder** - Automatically created in project directory
2. **Copy manifest** - From project root to build output folder
3. **Deploy to Clarion folder** - DLL and manifest automatically copied after every build

**Result:** After building, your `ProjectName/Clarion/` folder contains `ProjectName.dll` and `ProjectName.manifest` (auto-copied).

### What Needs Manual Setup (One Time)

After the first build, run the **clarioncom-deploy** skill to generate Register.bat, Unregister.bat, CheckDotNetVersion.bat, TestCOM.bat, TestManifests.bat, and README.md.

**Command:** `"Set up deployment for ProjectName"`

The clarioncom-deploy skill will:
- Extract GUIDs, ProgId, and method signatures from your source code
- Generate batch files with project-specific details
- Create README with method documentation (NO Clarion code examples)

### Complete Automated Workflow Example

**User Request:** `"Create a TimePickerCOM control that lets users select a time"`

**What Happens Automatically:**
1. Template files read from `Template/` folder for structure reference
2. C# source files created in new project folder (Interface, Implementation, AssemblyInfo)
3. Manifest file created in new project folder with correct GUIDs
4. .csproj created in new project folder with MSBuild deployment targets
5. Project built (DLL generated)
6. DLL and manifest auto-deployed to `TimePickerCOM/Clarion/`
7. clarioncom-deploy runs (generates batch files + README)

**Final Result** (Template files in `Template/` remain unchanged as READ-ONLY references; the new project `TimePickerCOM/` is created as a sibling folder to `Template/`):

```
TimePickerCOM/
  +-- ITimePicker.cs
  +-- TimePickerControl.cs
  +-- Properties/
  |   +-- AssemblyInfo.cs
  +-- TimePickerCOM.csproj
  +-- TimePickerCOM.manifest
  +-- Clarion/                    <- Ready for deployment!
      +-- TimePickerCOM.dll       <- Auto-copied
      +-- TimePickerCOM.manifest  <- Auto-copied
      +-- Register.bat            <- Auto-generated
      +-- Unregister.bat          <- Auto-generated
      +-- CheckDotNetVersion.bat  <- Auto-generated
      +-- TestCOM.bat             <- Auto-generated
      +-- TestManifests.bat       <- Auto-generated
      +-- README.md               <- Auto-generated
```

### Subsequent Builds

**What Updates Automatically:**
- `ProjectName.dll` - Re-copied to Clarion/ on every build
- `ProjectName.manifest` - Re-copied to Clarion/ on every build

**What Stays Current:**
- Batch files (only regenerate if COM interface changes)
- README.md (only regenerate if methods/events change)

**To Regenerate Documentation:** `"Update deployment for ProjectName"` - re-runs clarioncom-deploy to refresh batch files and README with latest code changes.

### For Clarion Developers

As a Clarion developer, you get:
1. **One-step creation** - Just describe the control you need
2. **Automatic deployment** - DLL and manifest always current
3. **Complete documentation** - README with COM interface details
4. **Testing tools** - Batch files for validation
5. **Property/method reference** - API documentation without code examples

**To use in your Clarion app:**
1. Copy `ProjectName/Clarion/*` to your Clarion app directory
2. Use ProgId from README to create COM object via OLE control
3. Reference the property/method list in README for available features
4. Run `TestManifests.bat` to validate setup
