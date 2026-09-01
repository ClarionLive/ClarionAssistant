# Project Identification, COM Detail Extraction, and Folder Setup (Steps 1-3 Detail)

## Output Structure Overview

The skill generates deployment artifacts in the `ProjectName/Clarion/accessory/` folder structure:

```
ProjectName/Clarion/
└── accessory/
    ├── bin/                    <- DLLs go here
    │   ├── ProjectName.dll
    │   └── [dependency DLLs]
    └── resources/              <- All other files go here
        ├── ProjectName.manifest
        ├── ProjectName.header
        ├── ProgID.details
        ├── ProgID.events
        ├── ProgID.methods
        ├── readme_ProjectName.html
        ├── CheckDotNetVersion.bat  (not copied to Clarion)
        └── TestManifests.bat       (not copied to Clarion)
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

## Step 1 Detail: Identify Project

Find the COM project to deploy:
- Look for .csproj file in current directory or subdirectories
- Extract project name from .csproj filename (e.g., `CalendarPickerCOM.csproj` → `CalendarPickerCOM`)

## Step 2 Detail: Extract COM Details from Source Code

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

## Step 3 Detail: Create/Verify Clarion Folder Structure

Create the accessory folder structure if it doesn't exist:

```bash
mkdir -p "ProjectName/Clarion/accessory/bin"
mkdir -p "ProjectName/Clarion/accessory/resources"
```

**Copy files to correct locations:**

1. **Copy DLLs to `accessory/bin/`:**
   ```bash
   cp bin/Release/net472/*.dll "ProjectName/Clarion/accessory/bin/"
   ```

2. **Copy manifest to `accessory/resources/`:**
   ```bash
   cp bin/Release/net472/*.manifest "ProjectName/Clarion/accessory/resources/"
   ```

**Verify files are present:**
- `accessory/bin/ProjectName.dll` - Main COM DLL (required)
- `accessory/bin/*.dll` - Any dependency DLLs
- `accessory/resources/ProjectName.manifest` - RegFree COM manifest (required)

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
