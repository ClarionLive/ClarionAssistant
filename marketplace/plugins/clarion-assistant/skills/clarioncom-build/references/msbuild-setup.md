# MSBuild Location and Troubleshooting

## Why `dotnet build` Doesn't Work

**NEVER use `dotnet build` or `dotnet msbuild` for COM projects.**

For RegFree COM projects that compile successfully with the .NET Framework version of MSBuild, the .NET Core/SDK version of MSBuild may have compatibility issues with certain build properties. Always use the Visual Studio MSBuild.exe to ensure proper COM component generation.

**How to recognize you're using the wrong MSBuild:**
- If you see "MSBuild version" followed by ".NET" at the top of the output
- If the build command starts with `dotnet`
- If the build succeeds but you're missing manifest file generation or proper type metadata

## Locating MSBuild.exe

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

**Solution:** Use the full .NET Framework MSBuild.exe from Visual Studio (see above)

### Build succeeds but DLL is missing

**Problem:** Looking in the wrong output directory

**Solution:** Check `bin\Release\net472\` (or your target framework)

### "Access denied" registry error

**Problem:** Not actually a problem! The DLL compiled successfully.

**Solution:** Ignore this error for registration-free COM. Verify the DLL exists.

### Need to run as administrator?

**Problem:** Trying to register the COM component in the registry

**Solution:** Don't! Use registration-free COM with a manifest file instead. No admin rights needed.
