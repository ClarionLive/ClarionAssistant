# RegFree COM Approach, Project Configuration, and Automation

## Registration-Free COM Approach

For Clarion applications, we use **registration-free COM** which means:

1. **No registry modifications** - The component is not registered in HKEY_CLASSES_ROOT
2. **Manifest-based activation** - A .manifest file describes the COM classes
3. **Xcopy deployment** - Just copy DLL + manifest to the application directory
4. **No admin rights needed** - Neither for deployment nor runtime

### What You Need to Deploy

Only two files:
1. `YourProject.dll` - The compiled component
2. `YourProject.manifest` - The manifest file (WITHOUT `.dll` in the name!)

Both files must be in the same directory as the Clarion executable.

(See references/manifest-validation.md for the Clarion-specific manifest naming rule.)

## Complete Build Workflow (Quick Reference)

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
- `TargetFramework`: net48 (NOT netcoreapp or net5.0+)
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
