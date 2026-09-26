# COM Control Creation Workflow

The exact workflow for creating a new COM control from the Template/ folder, naming conventions, scope rules, and post-build expectations.

## Creating a New Control - COPY from Template/ Folder!

**IMPORTANT:** When the user asks you to create a new control, you MUST follow this exact workflow:

1. **Copy Template Folder** (User does this before starting Claude)
   - User copies the COMTemplate folder to their project location
   - Example: Copy to `C:\MyProjects\MyNewControl\`
   - The Template/ subfolder contains READ-ONLY reference files

2. **COPY from Template/ and CREATE New Files** (Claude does this - YOU!)
   - **DO NOT modify files in the Template/ folder**
   - **DO NOT create new files from scratch**
   - **READ files from Template/ subfolder**
   - **CREATE new files in project root** (same level as Template/ folder)
   - **CRITICAL NAMING CONVENTION:**
     - Assembly name MUST end with "COM" (e.g., "ToggleButtonCOM", "ProgressBarCOM")
     - Class name = Assembly name minus "COM" suffix (e.g., "ToggleButton", "ProgressBar")
     - This ensures correct ProgID generation: AssemblyName.ClassName
   - **Color Parameter Naming Convention (REQUIRED for IDE Integration):**
     - Methods/properties that accept or return color values MUST include "color" in their name
     - Examples: `SetBackgroundColor()`, `GetTextColor()`, `BorderColor`
     - Applies to both `System.Drawing.Color` and hex string parameters
     - **Purpose:** Clarion IDE addin detects "color" in names and shows a color selector button
     - **Wrong:** `SetBackground(string hexValue)` - No color selector
     - **Correct:** `SetBackgroundColor(string hexColor)` - Color selector appears
   - **API Style Preference:**
     - Ask user: "Getter/Setter Methods" or "Properties"?
     - Default recommendation: Getter/Setter Methods
     - Apply choice consistently throughout the interface
     - Example (methods): `GetValue()`, `SetValue(x)`
     - Example (properties): `Value { get; set; }`
   - CREATE new files based on template files:
     - Read `Template/MinimalControl.cs` → Create `YourControlName.cs` in project root (e.g., `ProgressBar.cs`)
     - Read `Template/IMinimalControl.cs` → Create `IYourControlName.cs` in project root (e.g., `IProgressBar.cs`)
     - Read `Template/IMinimalControlEvents.cs` → Create `IYourControlNameEvents.cs` in project root (e.g., `IProgressBarEvents.cs`)
     - Read `Template/MinimalControl.manifest` → Create `YourControlName.manifest` in project root (e.g., `ProgressBar.manifest`)
     - Read `Template/ClarionCOMTemplate.csproj` → Create `YourControlNameCOM.csproj` in project root (e.g., `ProgressBarCOM.csproj`)

3. **Update File Contents**
   - Generate 4 new unique GUIDs
   - Update AssemblyInfo.cs with new GUIDs and names
   - Update all new files with new namespaces, class names, GUIDs
   - Update .csproj file with new assembly name and paths
   - Update .manifest file with new GUIDs and names

4. **Build the Project**
   - Use MSBuild to build the control
   - Build process automatically generates Clarion deployment files
   - **Note:** The first build will prompt for major/minor version numbers, which are then stored in a .env file for subsequent builds

5. **Expected Clarion Folder Contents After Build:**

   **File Naming Convention:**
   - DLL and manifest use assembly name
   - Metadata files (.details, .events, .methods, .header, .html) use ProgID

   - `AssemblyName.dll` - The COM control
   - `AssemblyName.manifest` - RegFree COM registration
   - `AssemblyName.header` - Assembly header info (includes ClarionPath, DLL name, ProgIDs)
   - `ProgID.details` - Control metadata (e.g., `MyNamespace.MyControl.details`)
   - `ProgID.events` - Event definitions
   - `ProgID.methods` - Properties and methods
   - `readme_AssemblyName.html` - Usage documentation

## Why Copy from Template/ Instead of Create New?

The Template/ folder contains all correct patterns. By copying and modifying:
- You preserve all critical patterns
- You don't accidentally omit required attributes
- Template/ folder remains unchanged as reference
- You can create multiple controls from the same template
- The build system works correctly

## Verification After Creation

Verify Template/ folder is UNCHANGED:
- ✅ Template/MinimalControl.cs still exists
- ✅ Template/IMinimalControl.cs still exists
- ✅ Template/IMinimalControlEvents.cs still exists
- ✅ Template/ClarionCOMTemplate.csproj still exists

Verify new control files exist in PROJECT ROOT:
- ✅ YourControlName.cs exists in project root
- ✅ IYourControlName.cs exists in project root
- ✅ IYourControlNameEvents.cs exists in project root
- ✅ YourControlName.manifest exists in project root
- ✅ YourControlNameCOM.csproj exists in project root

## CRITICAL: DO NOT Create Clarion Demo Applications!

**IMPORTANT:** Your job is ONLY to create the COM control (.NET C# code).

**DO NOT:**
- ❌ Create Clarion .clw files
- ❌ Create Clarion demo applications
- ❌ Write Clarion source code
- ❌ Generate Clarion project files

**WHY:** The user will create their own Clarion application to test the control. Creating Clarion files is outside your scope and not needed.

**NOT the same thing:** the generated readme's fixed "Calling from Clarion" section is
documentation, not a Clarion source file. It is emitted by GenerateReadmeHTML.ps1, is identical
for every control, and is in scope. This rule bans creating .clw/.app files - it does not ban
documenting how to call the control.

**Your scope:**
- ✅ Create the COM control (C# .NET)
- ✅ Build the DLL
- ✅ Generate deployment files (manifest, details, events, methods, README.html)
- ✅ Report success with location of Clarion folder

**After building, simply tell the user:**
"The control is ready in the Clarion folder. You can now add it to your Clarion application."

## Usage Notes

- **Always COPY from Template/ folder** - never create new files from scratch
- **Template/ folder is READ-ONLY** - never modify files in Template/ folder
- **Create new files in project root** - same level as Template/ folder
- Always generate new GUIDs for new projects (4 unique GUIDs)
- Test build succeeds before testing in Clarion
- **RegFree COM approach:**
  - NO .tlb file generated (EnableComInterop disabled)
  - NO registry registration needed
  - Uses .manifest file for COM registration
- **Manifest file:** Automatically generated and copied to Clarion folder
- **README.html:** Automatically generated with usage documentation
- Events not firing usually means ComVisible(false) at assembly level or wrong InterfaceType on event interface
- After build, Clarion folder should have: .dll, .manifest, .header (assembly name) + .details, .events, .methods (ProgID name) + readme .html
- After build, bin folder should have only: .dll and .pdb (NO .tlb file!)
