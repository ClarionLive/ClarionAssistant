# Metadata Files Generation (Step 6 Detail)

**First, read the Clarion path** using the helper script:
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

Generate metadata files in `ProjectName/Clarion/accessory/resources/` using the ProgID:
- `ProjectName.header` - Assembly, ProgID info, and Clarion path (assembly name)
- `ProgID.details` - Control metadata (ProgID name)
- `ProgID.events` - Event definitions (ProgID name)
- `ProgID.methods` - Property and method definitions (ProgID name)

## Header file format

Must include all sections for Clarion template compatibility:
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

## To generate the [DllsToCopy] section

1. Scan the build output folder (`bin/Release/net472/` or `bin/x86/Release/net472/`) for all `.dll` files
2. List each DLL file name (with extension) on a separate line
3. Always include the main COM DLL first
4. Include all dependency DLLs (NuGet packages, native DLLs, etc.)

**Example scanning command:**
```powershell
Get-ChildItem -Path "bin/Release/net472/" -Filter "*.dll" | ForEach-Object { $_.Name }
```

**Example output for header:**
```
[DllsToCopy]
GridControlCOM.dll
System.Data.SqlClient.dll
Newtonsoft.Json.dll
```

## Where to get other header values

- `[ControlType]` → Always `CSharp` for .NET COM controls
- `[Description]` → Extract from `AssemblyDescription` attribute in AssemblyInfo.cs
- `[Version]` → Extract from `AssemblyVersion` or `AssemblyFileVersion` in AssemblyInfo.cs or from the `.env` file
