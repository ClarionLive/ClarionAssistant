---
name: clarioncom-create
# prettier-ignore
description: Creates complete C# COM control projects for Clarion from scratch. Generates all required files (interfaces, implementation, manifest) with proper COM attributes and GUIDs. Uses parallel file updates where possible.
version: 1.0.0
---

## Path Resolution - CRITICAL

### Step 1: Get CLARIONCOM_HOME

Use the helper script to avoid shell escaping issues:

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If NOT_INSTALLED**: Stop and tell user:
> ClarionCOM is not installed. Please run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder.

### Step 2: Determine Template Location (IMPORTANT - prevents read errors)

**Before reading any template files**, use the helper script to get the templates path:

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') templates"
```

**Based on the result:**
- **LOCAL**: Use `Template/` for all template file reads (project was copied from COMTemplate)
- **Full path** (e.g., `C:\Users\...\ClarionCOM\Templates`): Use this exact path for template file reads
- **NOT_INSTALLED**: Stop and tell user to install ClarionCOM

**DO NOT attempt to read from local Template/ if it doesn't exist** - this causes unnecessary error messages.

### Resolved Paths Summary
- Templates: `Template/` (if exists) OR `$CLARIONCOM_HOME\Templates\`
- Scripts: `$CLARIONCOM_HOME\scripts\`

# Clarion COM Builder Skill

This skill enables you to create C# COM components (.NET Framework) that can be used in Clarion applications via reg-free COM activation.

## �?��? CRITICAL RULES - READ FIRST �?��?

### NEVER Register the Control
**DO NOT register the COM control using RegAsm or Register.bat**
- These components use **registration-free COM** (manifest-based activation)
- Traditional COM registration is NOT required and should NOT be performed
- Do not run Register.bat or suggest running it
- Do not use RegAsm.exe to register the DLL
- The manifest file provides all necessary COM activation information

### NEVER Run or Offer to Run Tests
**DO NOT execute or suggest running any test scripts**
- Do not run TestCOM.bat, TestManifests.bat, or any other test files
- Do not offer to run tests after building
- Do not suggest testing the component
- Testing is the user's responsibility
- The build batch files and test scripts are provided for user convenience only

**These rules apply throughout the entire workflow - during creation, building, and deployment.**

## ???� MANIFEST FILE REQUIREMENTS - CRITICAL ???�

### The Correct Manifest Structure for .NET Framework COM

**EVERY Clarion COM component MUST use this exact structure:**

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">

  <assemblyIdentity
      name="YourProjectName"
      version="1.0.0.0"
      processorArchitecture="x86"
      type="win32"/>

  <clrClass
      clsid="{YOUR-CLASS-GUID}"
      progid="YourNamespace.YourClassName"
      threadingModel="Apartment"
      name="YourNamespace.YourClassName"
      runtimeVersion="v4.0.30319">
  </clrClass>

  <file name="YourProject.dll">
     <typelib
         tlbid="{YOUR-ASSEMBLY-GUID}"
         version="1.0"
         helpdir=""
         resourceid="0"
         flags="HASDISKIMAGE"/>
  </file>

</assembly>
```

### �?� COMMON MISTAKE - DO NOT USE THIS FORMAT �?�

**NEVER use this structure (native COM format):**

```xml
<!-- WRONG - This is for native C++ COM, not .NET -->
<file name="YourProject.dll">
  <comClass clsid="{...}" ...>
    <!-- This will NOT work for .NET COM! -->
  </comClass>
</file>
```

### Manifest Creation Checklist

**Before saving your manifest file, verify ALL of these:**

- [ ] ✅ Uses `<clrClass>` element (NOT `<comClass>`)
- [ ] ✅ `<clrClass>` is OUTSIDE and BEFORE the `<file>` element
- [ ] ✅ Includes `processorArchitecture="x86"` in `<assemblyIdentity>`
- [ ] ✅ Includes `name="Namespace.ClassName"` attribute (fully qualified)
- [ ] ✅ Includes `runtimeVersion="v4.0.30319"` attribute
- [ ] ✅ Uses `threadingModel="Apartment"` (required for Clarion)
- [ ] ✅ Includes `resourceid="0"` in `<typelib>` element
- [ ] ✅ All GUIDs match the source code exactly
- [ ] ✅ ProgId matches the `[ProgId]` attribute in implementation class
- [ ] ✅ File named as `ProjectName.manifest` (NOT `ProjectName.dll.manifest`)

### Critical Attributes Explained

| Attribute | Required Value | Why It's Critical |
|-----------|---------------|-------------------|
| Element type | `<clrClass>` | .NET Framework COM requires CLR activation, not native COM |
| `name` | `Namespace.ClassName` | CLR needs fully qualified name to instantiate the class |
| `runtimeVersion` | `v4.0.30319` | Specifies .NET Framework 4.x runtime for activation |
| `processorArchitecture` | `x86` | Clarion requires 32-bit architecture |
| `threadingModel` | `Apartment` | Required for UI controls and Clarion compatibility |

### Why `<comClass>` Fails

**Using `<comClass>` instead of `<clrClass>` causes:**
- �?� Windows treats it as native COM component
- �?� Tries to find it in registry (registration-based activation)
- �?� Registration-free activation FAILS completely
- �?� Results in "Could not create COM object" error in Clarion
- �?� Component appears to build successfully but doesn't work

**The fix:** Always use `<clrClass>` for .NET Framework COM components!

## When to Use This Skill

Use this skill when a user requests:
- A COM object/component for Clarion
- A visual control for a Clarion application
- A Windows Forms control that Clarion can use
- Any UI component with methods that Clarion can call

## Template Folder Structure

**CRITICAL:** This project uses a Template/ folder containing READ-ONLY reference files.

### Template Files Location

All template files are located in the `Template/` subfolder:
- `Template/MinimalControl.cs` - Sample UserControl implementation
- `Template/IMinimalControl.cs` - Sample COM interface
- `Template/IMinimalControlEvents.cs` - Sample COM events interface
- `Template/MinimalControl.manifest` - Sample manifest file
- `Template/ClarionCOMTemplate.csproj` - Sample project file
- `Template/Properties/AssemblyInfo.cs` - Sample assembly info

### How to Use Templates

**When creating a new COM component:**

1. **READ** template files from `Template/` folder to understand structure
2. **COPY** content from template files
3. **CUSTOMIZE** the content for the new control (names, GUIDs, methods)
4. **CREATE** new files in project root (NOT in Template/)
5. **NEVER MODIFY** files in Template/ folder

**Example:**
```
READ: Template/IMinimalControl.cs        (template reference)
COPY and customize content
CREATE: NewProjectName/INewControl.cs    (new file in project root)
```

**Template files remain unchanged** - they serve as permanent reference for creating new controls.

## Control Library Support

When creating a COM control, the user selects a UI control library. Add the appropriate NuGet packages to the generated .csproj based on their choice.

### Standard WinForms (default)
No additional packages needed. Uses built-in System.Windows.Forms.

### DevExpress WinForms
Add these packages to the .csproj:
```xml
<ItemGroup>
  <!-- Core DevExpress package - add specific control packages as needed -->
  <PackageReference Include="DevExpress.WindowsForms" Version="24.*" />
</ItemGroup>
```

**Common additional packages** (add based on specific controls used):
- `DevExpress.Data` - Data binding support
- `DevExpress.XtraEditors` - Editor controls (TextEdit, ButtonEdit, etc.)
- `DevExpress.XtraGrid` - Grid and data grid controls
- `DevExpress.XtraCharts` - Chart controls
- `DevExpress.XtraTreeList` - TreeList control
- `DevExpress.XtraScheduler` - Scheduler/calendar controls

**Using statements for DevExpress:**
```csharp
using DevExpress.XtraEditors;
using DevExpress.XtraGrid;
// Add specific namespaces based on controls used
```

### Telerik WinForms
Add these packages to the .csproj:
```xml
<ItemGroup>
  <PackageReference Include="Telerik.WinControls.All" Version="2024.*" />
</ItemGroup>
```

**Using statements for Telerik:**
```csharp
using Telerik.WinControls;
using Telerik.WinControls.UI;
```

### Syncfusion WinForms
Add these packages to the .csproj:
```xml
<ItemGroup>
  <PackageReference Include="Syncfusion.WinForms" Version="*" />
</ItemGroup>
```

**Note**: Free community license available for individuals and small businesses (revenue < $1M).

**Using statements for Syncfusion:**
```csharp
using Syncfusion.WinForms;
using Syncfusion.WinForms.Controls;
```

### Infragistics WinForms
Add these packages to the .csproj:
```xml
<ItemGroup>
  <PackageReference Include="Infragistics.WinForms" Version="*" />
</ItemGroup>
```

**Using statements for Infragistics:**
```csharp
using Infragistics.Win;
using Infragistics.Win.UltraWinEditors;
```

### Library Selection Guidelines

When the user selects a third-party library:
1. Add the appropriate NuGet PackageReference(s) to the .csproj file
2. Include the library-specific using statements in the control implementation
3. Use the library's control classes instead of standard WinForms controls
4. Follow the library's patterns for control initialization and event handling

## Project Structure Requirements

### Framework and Project Type
- **Framework:** .NET Framework 4.8 (NOT .NET Core/.NET 5+)
- **Project Type:** Class Library (NOT Windows Forms Application)
- **Platform:** x86 (required for Clarion compatibility)
- **References:** Add `System.Windows.Forms` and `System.Drawing` for UI components

## Critical COM Implementation Rules

### Rule 1: GUID Generation
**ALWAYS generate three unique GUIDs for every COM component:**

1. **Interface GUID** - for the COM interface
2. **Class GUID** - for the COM class/control
3. **Assembly GUID** - for the type library (in AssemblyInfo.cs)

**Format:** Use standard GUID format: `{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`

**Generation:** In Visual Studio: Tools → Create GUID, or use online GUID generator, or use `[Guid(Guid.NewGuid().ToString())]` pattern.

### Rule 2: File Structure
Every COM component requires exactly 3 C# files:

1. **Interface File** (e.g., `IMyControl.cs`)
2. **Implementation File** (e.g., `MyControl.cs`)
3. **AssemblyInfo.cs** (in Properties folder)

### Rule 3: Interface Definition Pattern

```csharp
using System.Runtime.InteropServices;

namespace YourNamespace
{
    [ComVisible(true)]
    [Guid("YOUR-UNIQUE-INTERFACE-GUID")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IYourInterface
    {
        [DispId(1)]
        void MethodOne(string parameter);

        [DispId(2)]
        string MethodTwo();

        // Add more methods with incrementing DispId
    }
}
```

**Key requirements:**
- Mark with `[ComVisible(true)]`
- Assign unique GUID with `[Guid("...")]`
- Use `[InterfaceType(ComInterfaceType.InterfaceIsDual)]` for maximum compatibility
- Number methods sequentially with `[DispId(n)]` starting at 1
- Keep method signatures simple (basic types, strings)

### Rule 4: UserControl Implementation Pattern

```csharp
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace YourNamespace
{
    [ComVisible(true)]
    [Guid("YOUR-UNIQUE-CLASS-GUID")]
    [ProgId("YourNamespace.YourControlName")]
    [ClassInterface(ClassInterfaceType.None)]
    public partial class YourControl : UserControl, IYourInterface
    {
        // UI controls as private fields
        private Label lblExample;
        private Button btnExample;

        public YourControl()
        {
            InitializeControls();
        }

        private void InitializeControls()
        {
            this.Size = new Size(300, 200);

            // Create and configure controls
            lblExample = new Label();
            lblExample.Location = new Point(10, 10);
            lblExample.AutoSize = true;
            lblExample.Text = "Example";
            this.Controls.Add(lblExample);

            // Add more controls as needed
        }

        // Implement interface methods
        public void MethodOne(string parameter)
        {
            // Ensure UI thread safety
            if (InvokeRequired)
            {
                Invoke(new Action<string>(MethodOne), parameter);
                return;
            }

            lblExample.Text = parameter;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Clean up resources
            }
            base.Dispose(disposing);
        }
    }
}
```

**Key requirements:**
- Inherit from `UserControl` AND implement your interface
- Mark with `[ComVisible(true)]`
- Assign unique Class GUID with `[Guid("...")]`
- Set ProgId: `[ProgId("Namespace.ClassName")]` - this is what Clarion uses to create the object
- Use `[ClassInterface(ClassInterfaceType.None)]` to force explicit interface implementation
- Initialize all UI controls in code (not designer)
- Use `InvokeRequired` pattern for thread-safe UI updates
- Set default `this.Size` for the control

### Rule 5: AssemblyInfo.cs Configuration

```csharp
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("YourProjectName")]
[assembly: AssemblyDescription("COM Component for Clarion")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("YourProjectName")]
[assembly: AssemblyCopyright("Copyright A� 2025")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(true)]  // CRITICAL: Make assembly COM-visible
[assembly: Guid("YOUR-UNIQUE-ASSEMBLY-GUID")]  // Type Library GUID

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
```

**Key requirements:**
- Set `[assembly: ComVisible(true)]`
- Assign unique Assembly GUID - this is the Type Library ID

### Rule 6: Project Configuration

**CRITICAL:** The .csproj file must have these settings:

**IMPORTANT:** Do NOT include `RegisterForComInterop=true` or `EnableComInterop=true`. These settings cause the build to attempt registry registration, which is NOT used in registration-free COM. The manifest file provides all necessary COM activation information.

```xml
<PropertyGroup>
  <TargetFramework>net48</TargetFramework>
  <PlatformTarget>x86</PlatformTarget>
  <UseWindowsForms>true</UseWindowsForms>
  <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
</PropertyGroup>

<!-- CRITICAL: Exclude Template folder from compilation -->
<!-- This prevents duplicate assembly attributes when COMTemplate is copied to a project -->
<ItemGroup>
  <Compile Remove="Template\**\*" />
  <None Remove="Template\**\*" />
  <Content Remove="Template\**\*" />
  <EmbeddedResource Remove="Template\**\*" />
</ItemGroup>

<ItemGroup>
  <Reference Include="System.Windows.Forms" />
  <Reference Include="System.Drawing" />
</ItemGroup>
```

**CRITICAL - Template Folder Exclusion:**

The .csproj file MUST include the Template folder exclusion ItemGroup shown above. This is already present in the template .csproj file (`Template/ClarionCOMTemplate.csproj`), but it's important to understand why it's required:

**Why this is needed:**
- When COMTemplate folder is copied to create a new project, the Template/ subfolder comes along as a reference
- Without this exclusion, MSBuild attempts to compile .cs files from Template/ folder
- This causes duplicate assembly attribute errors (duplicate AssemblyInfo.cs, etc.)
- The exclusion ensures only the project's own source files are compiled

**What it does:**
- `<Compile Remove="Template\**\*" />` - Excludes all .cs files in Template/ from compilation
- `<None Remove="Template\**\*" />` - Excludes Template/ files from None item group
- `<Content Remove="Template\**\*" />` - Excludes Template/ files from Content item group
- `<EmbeddedResource Remove="Template\**\*" />` - Excludes Template/ files from embedded resources

**When to use:**
- This exclusion is already in the template .csproj and will be automatically included when copying from Template/
- If creating a .csproj from scratch (not recommended), you must manually add this ItemGroup

```xml
<!-- Automatic deployment to Clarion folder -->
<Target Name="CreateClarionFolder" BeforeTargets="CopyToClarion">
  <MakeDir Directories="$(ProjectDir)Clarion" Condition="!Exists('$(ProjectDir)Clarion')" />
</Target>

<Target Name="CopyManifest" AfterTargets="Build">
  <Copy SourceFiles="$(ProjectDir)$(AssemblyName).manifest"
        DestinationFiles="$(OutputPath)$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)$(AssemblyName).manifest')" />
</Target>

<Target Name="CopyToClarion" AfterTargets="CopyManifest">
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll"
        DestinationFiles="$(ProjectDir)Clarion\$(AssemblyName).dll"
        SkipUnchangedFiles="true" />
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).manifest"
        DestinationFiles="$(ProjectDir)Clarion\$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(OutputPath)$(AssemblyName).manifest')" />
  <Message Text="Deployed to Clarion folder: $(ProjectDir)Clarion\" Importance="high" />
</Target>
```

**What these targets do:**
- `CreateClarionFolder` - Creates `Clarion/` folder if it doesn't exist
- `CopyManifest` - Copies manifest from project root to output folder after build
- `CopyToClarion` - Automatically copies DLL and manifest to `Clarion/` folder after every successful build

This means DLL and manifest files are **automatically deployed** after each build!

### Rule 7: Clarion Template Metadata File Generation

Clarion templates require three metadata files that describe the COM component's interface for automatic code generation. These files use an INI-style format and should be auto-generated during the build process.

#### Required Metadata Files

1. **`.methods` file** - Lists all methods and properties with descriptions
2. **`.events` file** - Lists all COM events with parameters
3. **`.details` file** - Contains component metadata (ProgID, friendly name, description)

#### File Format Specifications

##### .methods File Format

```
[Method]
MethodName
[MethodDescription]
Description of what this method does
[Parameter]
parameterName
[ParameterType]
STRING|LONG|BYTE|REAL
[ParameterDescription]
Description of the parameter

[Properties]
[Property]
PropertyName
[PropertyType]
STRING|LONG|BYTE|REAL
[PropertyDescription]
Description of the property
```

##### .events File Format

```
[Event]
EventName
[EventDescription]
Description of when this event fires
[Parameter1]
parameterName
[Parameter1Type]
STRING|LONG|BYTE|REAL
[Parameter1Description]
Description of the parameter
[Parameter2]
secondParameter
[Parameter2Type]
STRING
[Parameter2Description]
Description of second parameter
```

##### .details File Format

```
[FriendlyName]
User-Friendly Component Name
[ProgID]
Namespace.ClassName
[FilenameNoExtenstion]
ProjectName
[Description]
Brief description of the component's purpose and features
[ObjectName]
suggestedVariableName
```

#### MSBuild Targets for Auto-Generation

Add these targets to your `.csproj` file after the `CopyToClarion` target to automatically generate metadata files during build:

##### Generate .details File

```xml
<Target Name="GenerateDetailsFile" AfterTargets="CopyToClarion">
  <ItemGroup>
    <DetailsContent Include="[FriendlyName]" />
    <DetailsContent Include="Component Friendly Name" />
    <DetailsContent Include="[ProgID]" />
    <DetailsContent Include="$(AssemblyName).ClassName" />
    <DetailsContent Include="[FilenameNoExtenstion]" />
    <DetailsContent Include="$(AssemblyName)" />
    <DetailsContent Include="[Description]" />
    <DetailsContent Include="Brief description of component functionality" />
    <DetailsContent Include="[ObjectName]" />
    <DetailsContent Include="suggestedVarName" />
  </ItemGroup>
  <WriteLinesToFile File="$(ProjectDir)Clarion\$(AssemblyName).details"
                    Lines="@(DetailsContent)"
                    Overwrite="true" />
</Target>
```

##### Generate .events File

```xml
<Target Name="GenerateEventsFile" AfterTargets="GenerateDetailsFile">
  <ItemGroup>
    <!-- First Event -->
    <EventsContent Include="[Event]" />
    <EventsContent Include="EventName1" />
    <EventsContent Include="[EventDescription]" />
    <EventsContent Include="Fired when something happens" />
    <EventsContent Include="[Parameter1]" />
    <EventsContent Include="param1Name" />
    <EventsContent Include="[Parameter1Type]" />
    <EventsContent Include="STRING" />
    <EventsContent Include="[Parameter1Description]" />
    <EventsContent Include="Description of parameter 1" />
    <EventsContent Include="" />

    <!-- Second Event -->
    <EventsContent Include="[Event]" />
    <EventsContent Include="EventName2" />
    <EventsContent Include="[EventDescription]" />
    <EventsContent Include="Fired when another thing happens" />
    <EventsContent Include="[Parameter1]" />
    <EventsContent Include="param1Name" />
    <EventsContent Include="[Parameter1Type]" />
    <EventsContent Include="LONG" />
    <EventsContent Include="[Parameter1Description]" />
    <EventsContent Include="Description of parameter 1" />
  </ItemGroup>
  <WriteLinesToFile File="$(ProjectDir)Clarion\$(AssemblyName).events"
                    Lines="@(EventsContent)"
                    Overwrite="true" />
</Target>
```

##### Generate .methods File

```xml
<Target Name="GenerateMethodsFile" AfterTargets="GenerateEventsFile">
  <ItemGroup>
    <!-- First Method -->
    <MethodsContent Include="[Method]" />
    <MethodsContent Include="MethodName1" />
    <MethodsContent Include="[MethodDescription]" />
    <MethodsContent Include="Description of what this method does" />
    <MethodsContent Include="[Parameter]" />
    <MethodsContent Include="param1" />
    <MethodsContent Include="[ParameterType]" />
    <MethodsContent Include="STRING" />
    <MethodsContent Include="[ParameterDescription]" />
    <MethodsContent Include="Description of the parameter" />
    <MethodsContent Include="" />

    <!-- Second Method -->
    <MethodsContent Include="[Method]" />
    <MethodsContent Include="MethodName2" />
    <MethodsContent Include="[MethodDescription]" />
    <MethodsContent Include="Description of second method" />
    <MethodsContent Include="" />

    <!-- Properties Section -->
    <MethodsContent Include="[Properties]" />
    <MethodsContent Include="[Property]" />
    <MethodsContent Include="PropertyName1" />
    <MethodsContent Include="[PropertyType]" />
    <MethodsContent Include="STRING" />
    <MethodsContent Include="[PropertyDescription]" />
    <MethodsContent Include="Description of the property" />
    <MethodsContent Include="" />

    <MethodsContent Include="[Property]" />
    <MethodsContent Include="PropertyName2" />
    <MethodsContent Include="[PropertyType]" />
    <MethodsContent Include="LONG" />
    <MethodsContent Include="[PropertyDescription]" />
    <MethodsContent Include="Description of second property" />
  </ItemGroup>
  <WriteLinesToFile File="$(ProjectDir)Clarion\$(AssemblyName).methods"
                    Lines="@(MethodsContent)"
                    Overwrite="true" />
</Target>
```

#### Data Type Mapping

Map C# types to Clarion types in metadata files:

| C# Type | Clarion Type | Notes |
|---------|--------------|-------|
| `string` | `STRING` | Text data |
| `int`, `long` | `LONG` | 32-bit integer |
| `short` | `SHORT` | 16-bit integer |
| `byte` | `BYTE` | 8-bit unsigned |
| `bool` | `BYTE` | Use 0/1 for false/true |
| `float`, `double` | `REAL` | Floating point |
| `decimal` | `DECIMAL` | Fixed precision decimal |

#### Extraction Guidelines

When generating metadata files:

1. **Extract from XML comments** - Use `/// <summary>` tags from C# source for descriptions
2. **Method signatures** - Get from COM interface definitions
3. **Event signatures** - Get from event interface (`IYourComponentEvents`)
4. **Property types** - Extract from property declarations
5. **Parameter names** - Use exact names from method signatures (case matters)

#### Complete Target Chain

The complete MSBuild target execution order should be:

```
Build
  ↓
CopyManifest
  ↓
CreateClarionFolder
  ↓
CopyToClarion
  ↓
CopyDependenciesToClarion (if needed)
  ↓
GenerateDetailsFile
  ↓
GenerateEventsFile
  ↓
GenerateMethodsFile
```

This ensures all files are deployed before metadata generation begins.

#### Benefits

- **Automatic updates** - Metadata files regenerated on every build
- **Always current** - No manual synchronization needed
- **Template integration** - Clarion templates can parse these files for code generation
- **Documentation** - Serves as human-readable API documentation
- **Consistency** - Eliminates manual errors in metadata

#### Example Output Files

After build, your `Clarion/` folder uses the `accessory/bin/resources` layout:
- **DLLs** go to `accessory/bin/`
- **Resources** (manifest, metadata, docs, batch files) go to `accessory/resources/`
- **wwwroot** (WebView2 only) goes to `accessory/resources/wwwroot/`

This layout mirrors the Clarion installation `accessory` folder structure, enabling drag & drop deployment.

Here's the folder structure (accessory layout):

```
ProjectName/Clarion/
??? accessory/
    ??? bin/                        ? DLLs
    ?   ??? ProjectName.dll
    ?   ??? [dependency DLLs]
    ??? resources/                  ? Metadata and docs
        ??? ProjectName.manifest
        ??? ProjectName.header
        ??? ProgID.details
        ??? ProgID.events
        ??? ProgID.methods
        ??? readme_ProjectName.html
```

**IGNORE the orphaned text below** - it's a rendering artifact from a previous edit and will be cleaned up.
  ├── ProjectName.dll
  ├── ProjectName.manifest
  ├── ProjectName.details      �? Component metadata
  ├── ProjectName.events       �? COM events list
  ├── ProjectName.methods      �? Methods and properties list
  ├── [dependency DLLs]
  └── [batch files and README]
```

### Rule 8: Managing Additional Dependencies

If your COM component uses NuGet packages or additional DLLs (SQLite, JSON libraries, etc.), these dependencies must also be deployed to the Clarion folder.

#### Adding NuGet Packages

Use standard NuGet package management:
```xml
<ItemGroup>
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  <PackageReference Include="System.Data.SQLite.Core" Version="1.0.118" />
</ItemGroup>
```

#### Auto-Copying Dependencies to Clarion Folder

Add this MSBuild target after the `CopyToClarion` target to automatically copy all dependency DLLs:

```xml
<Target Name="CopyDependenciesToClarion" AfterTargets="CopyToClarion">
  <!-- Copy all DLLs from output folder except the main assembly -->
  <ItemGroup>
    <DependencyDlls Include="$(OutputPath)*.dll" Exclude="$(OutputPath)$(AssemblyName).dll" />
  </ItemGroup>
  <Copy SourceFiles="@(DependencyDlls)"
        DestinationFolder="$(ProjectDir)Clarion"
        SkipUnchangedFiles="true" />
  <Message Text="Copied $(DependencyDlls->Count()) dependency DLL(s) to Clarion folder" Importance="high" />
</Target>
```

**What this does:**
- Runs automatically after `CopyToClarion` target
- Copies ALL DLL files from the output folder to `Clarion/` folder
- Excludes your main COM DLL (already copied by `CopyToClarion`)
- Only copies changed files for faster builds
- Includes NuGet package DLLs, native DLLs, and any referenced assemblies

#### Including Sample Data Files

For database files, config files, or other data files needed by your component:

**Option 1: Manual placement** (recommended for large files)
- Place sample files directly in `ProjectName/Clarion/` folder
- They will be preserved across builds

**Option 2: Auto-copy from project**
Create a `SampleData/` folder in your project and add this target:

```xml
<Target Name="CopySampleDataToClarion" AfterTargets="CopyDependenciesToClarion">
  <ItemGroup>
    <SampleFiles Include="$(ProjectDir)SampleData\**\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(SampleFiles)"
        DestinationFolder="$(ProjectDir)Clarion\%(RecursiveDir)"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)SampleData')" />
  <Message Text="Copied sample data files to Clarion folder" Importance="high" Condition="Exists('$(ProjectDir)SampleData')" />
</Target>
```

#### Complete MSBuild Targets Example

Here's a complete set of targets that handles everything. Uses `accessory/bin` and `accessory/resources` structure to mirror Clarion installation:

```xml
<!-- Automatic deployment to Clarion folder with accessory structure -->
<Target Name="CreateClarionFolders" BeforeTargets="CopyToClarion">
  <MakeDir Directories="$(ProjectDir)Clarion\accessory\bin" Condition="!Exists('$(ProjectDir)Clarion\accessory\bin')" />
  <MakeDir Directories="$(ProjectDir)Clarion\accessory\resources" Condition="!Exists('$(ProjectDir)Clarion\accessory\resources')" />
</Target>

<Target Name="CopyManifest" AfterTargets="Build">
  <Copy SourceFiles="$(ProjectDir)$(AssemblyName).manifest"
        DestinationFiles="$(OutputPath)$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)$(AssemblyName).manifest')" />
</Target>

<Target Name="CopyToClarion" AfterTargets="CopyManifest">
  <!-- Copy main DLL to accessory/bin -->
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll"
        DestinationFiles="$(ProjectDir)Clarion\accessory\bin\$(AssemblyName).dll"
        SkipUnchangedFiles="true" />
  <!-- Copy manifest to accessory/resources -->
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).manifest"
        DestinationFiles="$(ProjectDir)Clarion\accessory\resources\$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(OutputPath)$(AssemblyName).manifest')" />
  <Message Text="Deployed to Clarion folder: $(ProjectDir)Clarion\accessory\" Importance="high" />
</Target>

<Target Name="CopyDependenciesToClarion" AfterTargets="CopyToClarion">
  <!-- Copy all dependency DLLs to accessory/bin -->
  <ItemGroup>
    <DependencyDlls Include="$(OutputPath)*.dll" Exclude="$(OutputPath)$(AssemblyName).dll" />
  </ItemGroup>
  <Copy SourceFiles="@(DependencyDlls)"
        DestinationFolder="$(ProjectDir)Clarion\accessory\bin"
        SkipUnchangedFiles="true" />
  <Message Text="Copied dependency DLLs to Clarion\accessory\bin" Importance="high" Condition="@(DependencyDlls->Count()) > 0" />
</Target>

<Target Name="CopySampleDataToClarion" AfterTargets="CopyDependenciesToClarion">
  <!-- Copy sample data to accessory/resources -->
  <ItemGroup>
    <SampleFiles Include="$(ProjectDir)SampleData\**\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(SampleFiles)"
        DestinationFolder="$(ProjectDir)Clarion\accessory\resources\%(RecursiveDir)"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)SampleData')" />
  <Message Text="Copied sample data files to Clarion\accessory\resources" Importance="high" Condition="Exists('$(ProjectDir)SampleData')" />
</Target>
```

**Folder structure after build:**
```
ProjectName/Clarion/
??? accessory/
    ??? bin/                    ? DLLs go here
    ?   ??? ProjectName.dll
    ?   ??? [dependency DLLs]
    ??? resources/              ? All other files go here
        ??? ProjectName.manifest
        ??? [sample data files]
```

This structure mirrors the Clarion installation's `accessory` folder, enabling drag & drop deployment.

#### Dependencies Checklist

When adding dependencies to your COM component:
- [ ] Add NuGet packages via PackageReference in .csproj
- [ ] Add `CopyDependenciesToClarion` MSBuild target to auto-copy DLLs
- [ ] If using sample data, create `SampleData/` folder in project
- [ ] Add `CopySampleDataToClarion` target if needed
- [ ] Document all dependencies in README.md (handled by clarioncom-deploy skill)
- [ ] Test that all DLLs are present in `Clarion/accessory/bin/` folder after build
- [ ] Verify Clarion application can find all required DLLs

**Important:** All dependency DLLs must be placed in the same directory as your COM DLL for Clarion to load them correctly.

### Rule 9: Manifest File Generation

After building, create `YourProject.manifest` (NOT `YourProject.dll.manifest`) with this template:

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">

  <assemblyIdentity
      name="YourProjectName"
      version="1.0.0.0"
      processorArchitecture="x86"
      type="win32"/>

  <clrClass
      clsid="{YOUR-CLASS-GUID}"
      progid="YourNamespace.YourClassName"
      threadingModel="Apartment"
      name="YourNamespace.YourClassName"
      runtimeVersion="v4.0.30319">
  </clrClass>

  <file name="YourProject.dll">
     <typelib
         tlbid="{YOUR-ASSEMBLY-GUID}"
         version="1.0"
         helpdir=""
         resourceid="0"
         flags="HASDISKIMAGE"/>
  </file>

</assembly>
```

**GUID Mapping:**
- `clsid=` → Class GUID from implementation file
- `progid=` → Must match ProgId attribute exactly
- `name=` → Fully qualified class name (Namespace.ClassName) - must match exactly
- `runtimeVersion=` → Use `v4.0.30319` for .NET Framework 4.x
- `threadingModel=` → Use `Apartment` for Clarion compatibility
- `tlbid=` → Assembly GUID from AssemblyInfo.cs

**Critical for .NET COM:**
- Use `<clrClass>` element (NOT `<comClass>`)
- Place `<clrClass>` OUTSIDE and BEFORE the `<file>` element
- Must include `name` and `runtimeVersion` attributes
- Threading model must be `Apartment` for Clarion

## Best Practices

### Documentation and README Generation

�?��? **CRITICAL: NEVER Generate Clarion Code Examples**

**DO NOT include Clarion code examples in documentation or README files:**
- �?� Do not write Clarion code snippets
- �?� Do not create Clarion variable declarations
- �?� Do not show method calls in Clarion syntax
- �?� Do not provide Clarion integration examples

**Why:**
- The user has Clarion templates that generate correct code
- AI-generated Clarion code will likely have incorrect syntax
- Clarion has specific syntax requirements that are easy to get wrong

**What to include instead:**
- ✅ List of available properties and methods
- ✅ Property/method descriptions (from C# XML comments)
- ✅ Parameter types and purposes
- ✅ Integration steps (add OLE control, set ProgID, copy files)
- ✅ COM identifiers (ProgID, CLSID, etc.)

**Example of correct documentation (NO CODE):**

> **Integration Instructions:**
> 1. Add an OLE control to your Clarion window
> 2. Set the ProgID to: `ComponentName.ClassName`
> 3. Copy DLL and manifest to your application directory
>
> **Available Methods:**
> - `SetDateToToday()` - Sets the selected date to today's date
> - `GetFormattedDate()` - Returns the date as a formatted string

### UI Design
1. **Set default sizes:** Always set `this.Size` in constructor
2. **Use AutoSize:** For labels, set `AutoSize = true` for automatic sizing
3. **Manual layout:** Position controls with `Location = new Point(x, y)`
4. **Fonts:** Specify fonts explicitly: `new Font("Microsoft Sans Serif", 10F, FontStyle.Bold)`

### Thread Safety
1. **Always check InvokeRequired** for methods that update UI
2. **Use Invoke pattern:**
   ```csharp
   if (InvokeRequired)
   {
       Invoke(new Action<ParamType>(MethodName), param);
       return;
   }
   // Safe to update UI here
   ```

### Memory Management
1. **Dispose timers and resources** in `Dispose(bool disposing)`
2. **Stop timers** before disposing
3. **Call base.Dispose(disposing)** at the end

### Method Design
1. **Keep it simple:** Use basic types (string, int, bool, double)
2. **Avoid complex types:** No custom classes, collections, or delegates in interface
3. **Return values:** Prefer void methods or simple types
4. **Error handling:** Use try-catch in all interface methods

### API Style: Properties vs Getter/Setter Methods

**Ask the user** for their preference when designing the interface:

**Option 1: Getter/Setter Methods (Recommended for Clarion integration)**
- More explicit and predictable
- Each operation has its own DispId
- Better tooling support in Clarion IDE
- Example: `GetBackgroundColor()`, `SetBackgroundColor(string hexColor)`

```csharp
// Interface
[DispId(1)]
string GetControlText();

[DispId(2)]
void SetControlText(string value);

// Implementation
public string GetControlText() { return _text; }
public void SetControlText(string value) { _text = value; Invalidate(); }
```

**Option 2: Properties**
- More idiomatic C#
- Single DispId for get/set
- Shorter interface definitions
- Example: `string BackgroundColor { get; set; }`

```csharp
// Interface
[DispId(1)]
string ControlText { get; set; }

// Implementation
public string ControlText
{
    get { return _text; }
    set { _text = value; Invalidate(); }
}
```

**Apply the user's preference consistently** throughout the interface.

### Color Parameter Naming Convention

**REQUIRED for Clarion IDE Integration:**

When a method or property handles color values, the name MUST include "color" (case-insensitive). This enables the Clarion IDE addin to display a color selector button.

**Correct:**
```csharp
void SetBackgroundColor(string hexColor);
void SetTextColor(string hexColor);
string GetSelectedColor();
string BorderColor { get; set; }
```

**Wrong (IDE won't show color selector):**
```csharp
void SetBackground(string hexValue);
void SetForeground(string hex);
string BorderHex { get; set; }
```

**Why:** The Clarion IDE addin reads `.methods` metadata files. When it finds "color" in a method/property name, it adds a color selector button instead of requiring manual hex entry.

## Common Patterns

### Pattern: Timer-Based Updates
```csharp
private Timer updateTimer;

private void InitializeTimer()
{
    updateTimer = new Timer();
    updateTimer.Interval = 1000; // milliseconds
    updateTimer.Tick += UpdateTimer_Tick;
    updateTimer.Start();
}

private void UpdateTimer_Tick(object sender, EventArgs e)
{
    // Update UI
}

protected override void Dispose(bool disposing)
{
    if (disposing && updateTimer != null)
    {
        updateTimer.Stop();
        updateTimer.Dispose();
        updateTimer = null;
    }
    base.Dispose(disposing);
}
```

### Pattern: Configurable Visual Elements (Color Methods)

**Note:** Method names MUST include "color" for Clarion IDE color selector support.

```csharp
public void SetBackgroundColor(string hexColor)  // "Color" in name = IDE shows color selector
{
    if (InvokeRequired)
    {
        Invoke(new Action<string>(SetBackgroundColor), hexColor);
        return;
    }

    try
    {
        this.BackColor = ColorTranslator.FromHtml(hexColor);
    }
    catch
    {
        // Handle invalid color
    }
}
```

### Pattern: Status/State Tracking
```csharp
private string currentStatus = "Ready";

public string GetStatus()
{
    return currentStatus;
}

public void SetStatus(string status)
{
    if (InvokeRequired)
    {
        Invoke(new Action<string>(SetStatus), status);
        return;
    }

    currentStatus = status;
    lblStatus.Text = status;
}
```

## Keyboard Input Handling in COM Hosting Scenarios

### The Problem: Keyboard Input Doesn't Work in Clarion

When hosting .NET Windows Forms controls (especially text editing controls) in native Win32 applications like Clarion via COM interop, you may encounter a critical issue: **keyboard input doesn't work** even though the control loads and displays correctly.

**Symptoms:**
- Control loads and initializes successfully
- Mouse input works (paste, button clicks)
- Keyboard input fails completely or only the first character appears
- KeyDown events fire but characters don't appear
- No error messages or exceptions

**Root Cause:**

Native Win32 hosts like Clarion don't participate in the .NET Windows Forms message pump. When your control receives keyboard input, the standard .NET message processing doesn't occur because:

1. KeyPress events don't fire naturally (Clarion's message loop doesn't trigger them)
2. WM_CHAR messages generated by the host may not be processed correctly
3. Controls expecting normal Windows Forms input processing enter invalid states

### The Solution: Direct Text Manipulation

Instead of relying on Windows message forwarding (WM_CHAR, WM_KEYDOWN), **directly manipulate the text control's properties** in response to KeyDown events.

#### Implementation Pattern

```csharp
private void TextBox_KeyDown(object sender, KeyEventArgs e)
{
    try
    {
        // Handle special keys first
        if (HandleSpecialKey(e))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Convert key to character
        char? character = ConvertKeyToChar(e);

        if (character.HasValue && textBox != null && !textBox.IsDisposed)
        {
            // Get current state
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;
            string currentText = textBox.Text;

            // Build new text (replace selection or insert at caret)
            string newText = currentText.Substring(0, selectionStart) +
                           character.Value +
                           currentText.Substring(selectionStart + selectionLength);

            // Update text and caret position
            textBox.Text = newText;
            textBox.SelectionStart = selectionStart + 1;
            textBox.SelectionLength = 0;

            // Prevent further processing
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Trace.WriteLine($"Error in KeyDown handler: {ex.Message}");
    }
}
```

#### Special Key Handling

Handle non-printable keys (Backspace, Delete, Enter, Tab) separately:

```csharp
private bool HandleSpecialKey(KeyEventArgs e)
{
    if (textBox == null || textBox.IsDisposed)
        return false;

    int selectionStart = textBox.SelectionStart;
    int selectionLength = textBox.SelectionLength;
    string currentText = textBox.Text;

    switch (e.KeyCode)
    {
        case Keys.Back:  // Backspace
            if (selectionLength > 0)
            {
                // Delete selection
                textBox.Text = currentText.Substring(0, selectionStart) +
                             currentText.Substring(selectionStart + selectionLength);
                textBox.SelectionStart = selectionStart;
            }
            else if (selectionStart > 0)
            {
                // Delete character before caret
                textBox.Text = currentText.Substring(0, selectionStart - 1) +
                             currentText.Substring(selectionStart);
                textBox.SelectionStart = selectionStart - 1;
            }
            return true;

        case Keys.Delete:
            if (selectionLength > 0)
            {
                textBox.Text = currentText.Substring(0, selectionStart) +
                             currentText.Substring(selectionStart + selectionLength);
                textBox.SelectionStart = selectionStart;
            }
            else if (selectionStart < currentText.Length)
            {
                textBox.Text = currentText.Substring(0, selectionStart) +
                             currentText.Substring(selectionStart + 1);
                textBox.SelectionStart = selectionStart;
            }
            return true;

        case Keys.Enter:
            textBox.Text = currentText.Substring(0, selectionStart) +
                         "\r\n" +
                         currentText.Substring(selectionStart + selectionLength);
            textBox.SelectionStart = selectionStart + 2;
            return true;

        case Keys.Tab:
            textBox.Text = currentText.Substring(0, selectionStart) +
                         "\t" +
                         currentText.Substring(selectionStart + selectionLength);
            textBox.SelectionStart = selectionStart + 1;
            return true;
    }

    return false;
}
```

#### Key-to-Character Conversion

Convert KeyDown events to characters (handle shift, letter/number keys, symbols):

```csharp
private char? ConvertKeyToChar(KeyEventArgs e)
{
    bool shift = e.Shift;
    bool ctrl = e.Control;
    bool alt = e.Alt;

    // Don't process control characters
    if (ctrl || alt)
        return null;

    // Letters
    if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
    {
        char baseChar = (char)('a' + (e.KeyCode - Keys.A));
        return shift ? char.ToUpper(baseChar) : baseChar;
    }

    // Numbers and symbols
    if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
    {
        int digit = e.KeyCode - Keys.D0;
        if (shift)
        {
            string shiftedSymbols = ")!@#$%^&*(";
            return shiftedSymbols[digit];
        }
        return (char)('0' + digit);
    }

    // Common symbols
    switch (e.KeyCode)
    {
        case Keys.Space: return ' ';
        case Keys.OemPeriod: return shift ? '>' : '.';
        case Keys.Oemcomma: return shift ? '<' : ',';
        case Keys.OemQuestion: return shift ? '?' : '/';
        case Keys.OemSemicolon: return shift ? ':' : ';';
        case Keys.OemQuotes: return shift ? '"' : '\'';
        case Keys.OemOpenBrackets: return shift ? '{' : '[';
        case Keys.OemCloseBrackets: return shift ? '}' : ']';
        case Keys.OemPipe: return shift ? '|' : '\\';
        case Keys.Oemtilde: return shift ? '~' : '`';
        case Keys.OemMinus: return shift ? '_' : '-';
        case Keys.Oemplus: return shift ? '+' : '=';
    }

    return null;
}
```

#### Required Overrides for Enter Key

To ensure the Enter key reaches your KeyDown handler (instead of being intercepted by the dialog system):

```csharp
protected override bool IsInputKey(Keys keyData)
{
    switch (keyData)
    {
        case Keys.Up:
        case Keys.Down:
        case Keys.Left:
        case Keys.Right:
        case Keys.Tab:
        case Keys.Enter:  // CRITICAL: Required for Enter to work in COM/Clarion
        case Keys.Home:
        case Keys.End:
        case Keys.PageUp:
        case Keys.PageDown:
            return true;
    }

    return base.IsInputKey(keyData);
}
```

#### WndProc Override for Complete Keyboard Control

Tell the native host (Clarion) that your control wants to handle all keyboard input:

```csharp
private const int WM_GETDLGCODE = 0x0087;
private const int DLGC_WANTALLKEYS = 0x0004;
private const int DLGC_WANTARROWS = 0x0001;
private const int DLGC_WANTTAB = 0x0002;
private const int DLGC_WANTCHARS = 0x0080;

protected override void WndProc(ref Message m)
{
    if (m.Msg == WM_GETDLGCODE)
    {
        // Tell Clarion we want all keyboard input
        m.Result = (IntPtr)(DLGC_WANTALLKEYS | DLGC_WANTARROWS |
                           DLGC_WANTTAB | DLGC_WANTCHARS);
        return;
    }

    base.WndProc(ref m);
}
```

### Why This Approach Works

**Bypasses broken message flow:**
- Doesn't rely on KeyPress events (which don't fire in COM hosting)
- Doesn't rely on WM_CHAR processing (which may enter invalid state)
- Direct property manipulation always works regardless of message pump state

**Maintains full control:**
- Handles selections correctly (replace selected text when typing)
- Advances caret position properly
- Supports all keyboard operations (typing, backspace, delete, enter)

**Thread-safe:**
- All operations happen on the UI thread (called from KeyDown event)
- No invoke/cross-thread concerns for keyboard input

### What Doesn't Work

**�?� Sending WM_CHAR messages:**
```csharp
// This approach FAILS - control enters bad state after first character
SendMessage(textBox.Handle, WM_CHAR, new IntPtr(character), IntPtr.Zero);
```

**�?� Relying on KeyPress events:**
```csharp
// KeyPress events don't fire naturally in Clarion hosting
private void TextBox_KeyPress(object sender, KeyPressEventArgs e)
{
    // This will never be called!
}
```

**�?� Using IMessageFilter:**
```csharp
// Message filter doesn't work - Clarion bypasses .NET message pump
Application.AddMessageFilter(this);  // Has no effect
```

### Complete Integration Checklist

When implementing text editing controls for Clarion:

- [ ] Attach KeyDown event handler to text control
- [ ] Implement `ConvertKeyToChar()` for printable characters
- [ ] Implement `HandleSpecialKey()` for Backspace/Delete/Enter/Tab
- [ ] Override `IsInputKey()` to include `Keys.Enter`
- [ ] Override `WndProc()` to handle `WM_GETDLGCODE`
- [ ] Set `e.Handled = true` and `e.SuppressKeyPress = true` in KeyDown
- [ ] Test all keyboard operations: typing, backspace, delete, enter, tab
- [ ] Test selection operations: type to replace selection, delete selection
- [ ] Verify caret position advances correctly

### Debugging Tips

**Enable trace logging to diagnose keyboard issues:**

```csharp
private void TextBox_KeyDown(object sender, KeyEventArgs e)
{
    System.Diagnostics.Trace.WriteLine($"KeyDown: {e.KeyCode}");

    // Before modification
    System.Diagnostics.Trace.WriteLine(
        $"BEFORE: TextLen={textBox.Text.Length}, " +
        $"CaretPos={textBox.SelectionStart}, " +
        $"Text=\"{textBox.Text}\"");

    // ... perform text manipulation ...

    // After modification
    System.Diagnostics.Trace.WriteLine(
        $"AFTER: TextLen={textBox.Text.Length}, " +
        $"CaretPos={textBox.SelectionStart}, " +
        $"Text=\"{textBox.Text}\"");
}
```

**Use DebugView++ to monitor output:**
- Download DebugView++ (captures Trace output)
- Watch for TextLen and CaretPos changes
- If TextLen doesn't increase → text insertion failed
- If CaretPos doesn't advance → caret update failed

### Performance Considerations

**Direct text manipulation is efficient:**
- String concatenation for single characters is fast
- TextBox.Text assignment triggers single repaint
- No message marshaling overhead
- No cross-thread invoke delays

**For large documents:**
- Consider using StringBuilder for building new text
- Batch multiple character insertions if implementing paste
- Use SuspendLayout/ResumeLayout for multiple UI updates

This approach enables fully functional text editing controls in Clarion COM hosting scenarios where standard .NET keyboard handling fails.

## Build Tools and Requirements

### Required Build Tool

**CRITICAL: Use Visual Studio MSBuild.exe** - Located at:
- Visual Studio 2022: `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`
- Other editions: Professional, Enterprise, or earlier versions in similar paths

### Finding MSBuild.exe

If you don't know where MSBuild is installed, use this PowerShell command:

```powershell
powershell -Command "@('C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio') | ForEach-Object { Get-ChildItem -Path $_ -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue } | Select-Object -First 1 -ExpandProperty FullName"
```

This searches both 64-bit and 32-bit Program Files directories.

### DO NOT Use

�?� **`dotnet build`** - Will fail with MSB4803 error on RegisterAssembly task
�?� **`dotnet msbuild`** - Same issue, uses .NET Core MSBuild

**Why:** The .NET Core version of MSBuild does not support the RegisterAssembly task required for COM interop. You will see this error:

```
error MSB4803: The task "RegisterAssembly" is not supported on the .NET Core version of MSBuild.
Please use the .NET Framework version of MSBuild.
```

**Solution:** Always use Visual Studio's MSBuild.exe for COM projects.

For more detailed build procedures, see the `clarioncom-build` skill.

## Build and Deployment

### Build Process

**Build Command:**

```cmd
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" YourProject.csproj -p:Configuration=Release
```

Or if you've found MSBuild with PowerShell, use that path.

**Build in Release mode** for distribution to ensure optimal performance.

**Expected Output:**
- `YourProject.dll` (the COM component) ✓
- `YourProject.tlb` (type library - auto-generated)
- `YourProject.pdb` (optional, for debugging)

**Expected "Error" - This is OK:**

You will likely see a registry access error at the end:

```
error MSB3216: Cannot register assembly - access denied.
Please make sure you're running the application as administrator.
```

**This error is NORMAL for registration-free COM!** If the DLL was created, the build succeeded. The registration error happens afterward because we're using manifest-based COM activation instead of registry registration.

### Deployment Files
For Clarion application to use the COM component:
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

**Important:** If you use the MSBuild targets from Rule 7, all required files will be automatically copied to the `Clarion/` folder after each build, making deployment straightforward - just copy everything from `ProjectName/Clarion/*` to your Clarion application directory.

### Automating Manifest Creation

To avoid manually copying the manifest file after each build, add this MSBuild target to your .csproj:

```xml
<Target Name="CopyManifest" AfterTargets="Build">
  <Copy SourceFiles="$(ProjectDir)$(AssemblyName).manifest"
        DestinationFiles="$(OutputPath)$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)$(AssemblyName).manifest')" />
</Target>
```

**What this does:**
- Runs automatically after each build
- Copies `YourProject.manifest` from the project root to `bin\Release\net48\`
- Only copies if the file exists and has changed

**File placement:**
Keep your manifest file in the project root (same directory as .csproj), and it will be automatically copied to the output folder.

### Clarion Usage Pattern
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

### Deployment Artifact Generation (REQUIRED)

**CRITICAL STEP:** After the build succeeds and the DLL is created in the `Clarion/` folder, you MUST generate deployment artifacts.

**This step is NOT optional** - it must be completed for every new COM component.

**What to Generate:**
1. **Register.bat** - COM registration script
2. **Unregister.bat** - COM unregistration script
3. **CheckDotNetVersion.bat** - System requirement validation
4. **TestCOM.bat / TestCOM.vbs** - Functional testing scripts
5. **TestManifests.bat** - Registration-free COM validation
6. **README.md** - Complete integration documentation

**How to Generate:**

Immediately after build completion, execute the deployment artifact generation:

```
AUTOMATICALLY run deployment artifact generation by following the clarioncom-deploy skill steps:
1. Extract COM details (GUIDs, ProgId, methods) from source files
2. Generate all 5 batch files with project-specific substitutions
3. Generate comprehensive README.md with method documentation
4. Verify all files created in ProjectName/Clarion/ folder
```

**Validation:**
After generation, verify these files exist in `ProjectName/Clarion/`:
- ✓ ProjectName.dll (from MSBuild)
- ✓ ProjectName.manifest (from MSBuild)
- ✓ Register.bat (generated)
- ✓ Unregister.bat (generated)
- ✓ CheckDotNetVersion.bat (generated)
- ✓ TestCOM.bat (generated)
- ✓ TestCOM.vbs (generated)
- ✓ TestManifests.bat (generated)
- ✓ README.md (generated)

**When to Skip:**
This step should ONLY be skipped if:
- You are explicitly asked to create ONLY the C# source files (no build)
- You are updating existing source code (deployment docs already exist and don't need updating)

**When to Regenerate:**
Regenerate deployment artifacts whenever:
- COM interface changes (new methods, properties, or events)
- Documentation needs updating
- Testing procedures change

## Testing Checklist

Before delivering a COM component:
1. ✓ All three GUIDs are unique and different
2. ✓ ProgId matches in both code and manifest
3. ✓ Project builds without errors
4. ✓ .tlb file is generated in output
5. ✓ Manifest file created with correct GUIDs
6. ✓ All interface methods are implemented
7. ✓ Thread safety (InvokeRequired) is handled
8. ✓ Resources are properly disposed

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

## Example Component: Simple Color Picker

This demonstrates a complete working example:

**IColorPicker.cs:**
```csharp
using System.Runtime.InteropServices;

namespace ColorPickerCOM
{
    [ComVisible(true)]
    [Guid("A1B2C3D4-E5F6-4789-ABCD-123456789ABC")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IColorPicker
    {
        [DispId(1)]
        void SetColors(string colorList);

        [DispId(2)]
        string GetSelectedColor();

        [DispId(3)]
        void SetTitle(string title);
    }
}
```

**ColorPickerControl.cs:**
```csharp
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace ColorPickerCOM
{
    [ComVisible(true)]
    [Guid("B2C3D4E5-F6A7-4890-BCDE-234567890BCD")]
    [ProgId("ColorPickerCOM.ColorPicker")]
    [ClassInterface(ClassInterfaceType.None)]
    public partial class ColorPickerControl : UserControl, IColorPicker
    {
        private Label lblTitle;
        private FlowLayoutPanel flowColors;
        private string selectedColor = "";

        public ColorPickerControl()
        {
            InitializeControls();
            SetColors("#FF0000,#00FF00,#0000FF,#FFFF00,#FF00FF,#00FFFF");
        }

        private void InitializeControls()
        {
            this.Size = new Size(320, 200);
            this.BackColor = Color.White;

            lblTitle = new Label();
            lblTitle.Text = "Select a Color:";
            lblTitle.Font = new Font("Arial", 10F, FontStyle.Bold);
            lblTitle.Location = new Point(10, 10);
            lblTitle.AutoSize = true;
            this.Controls.Add(lblTitle);

            flowColors = new FlowLayoutPanel();
            flowColors.Location = new Point(10, 40);
            flowColors.Size = new Size(300, 150);
            flowColors.FlowDirection = FlowDirection.LeftToRight;
            this.Controls.Add(flowColors);
        }

        public void SetColors(string colorList)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(SetColors), colorList);
                return;
            }

            flowColors.Controls.Clear();
            string[] colors = colorList.Split(',');

            foreach (string colorHex in colors)
            {
                try
                {
                    Button colorButton = new Button();
                    colorButton.Size = new Size(50, 50);
                    colorButton.BackColor = ColorTranslator.FromHtml(colorHex.Trim());
                    colorButton.FlatStyle = FlatStyle.Flat;
                    colorButton.Tag = colorHex.Trim();
                    colorButton.Click += ColorButton_Click;
                    flowColors.Controls.Add(colorButton);
                }
                catch { }
            }
        }

        private void ColorButton_Click(object sender, EventArgs e)
        {
            Button btn = sender as Button;
            if (btn != null)
            {
                selectedColor = btn.Tag.ToString();
            }
        }

        public string GetSelectedColor()
        {
            return selectedColor;
        }

        public void SetTitle(string title)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(SetTitle), title);
                return;
            }

            lblTitle.Text = title;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                flowColors?.Controls.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
```

## Summary Workflow

**Execution:** This skill uses parallel execution for independent file creation and updates to maximize performance.

When a user requests a COM component:

1. **Understand requirements:** What methods? What UI elements? What control library (WinForms, DevExpress, Telerik, Syncfusion, Infragistics)?
2. **Generate 3 unique GUIDs:** Interface, Class, Assembly
3. **Read template files** from `Template/` folder for structure reference
4. **Create interface file** in project root with ComVisible, Guid, InterfaceType, and DispId
5. **Create implementation file** in project root with UserControl, ComVisible, Guid, ProgId, ClassInterface (use library-specific controls if selected)
6. **Create AssemblyInfo.cs** in project root `Properties/` folder with ComVisible and Assembly GUID
7. **Create .csproj** in project root with correct framework, platform, MSBuild targets, and NuGet packages for selected library (NO RegisterForComInterop)
8. **Create CHANGELOG.md** in project root (copy from Template/CHANGELOG.md, replace {DATE} with today's date)
9. **Build project** to generate DLL
10. **Create manifest file** in project root with correct GUID mappings (RegFree COM activation)
11. **Verify:** Test checklist, ensure all GUIDs are unique
12. **MSBuild targets auto-deploy** DLL and manifest to `Clarion/` folder
13. **Run clarioncom-deploy skill** to generate batch files and README
14. **Offer GitHub repository creation** - Ask user if they want to create a GitHub repo for the project

**Key Principle:** Template/ contains READ-ONLY reference files. New projects are created in their own folders with files copied and customized from templates.

This skill ensures Clarion programmers can request COM components without knowing C# or COM internals!

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

If the user chooses to create a repository:

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
You can create a repository later using /ClarionCOM ? "Initialize GitHub Repo"
```

### 14.4 Workflow Integration

This step runs **after** step 13 (clarioncom-deploy) completes successfully.

**Timeline:**
```
Step 12: MSBuild deploys to Clarion/
Step 13: clarioncom-deploy generates docs
Step 14: Offer GitHub repo creation ? NEW
         ?
Complete: Project ready for use!
```

## Automatic Deployment

### What Happens Automatically

When you create a COM component with this skill, the `.csproj` file includes MSBuild targets that:

1. **Create `Clarion/` folder** - Automatically created in project directory
2. **Copy manifest** - From project root to build output folder
3. **Deploy to Clarion folder** - DLL and manifest automatically copied after every build

**Result:** After building, your `ProjectName/Clarion/` folder contains:
- `ProjectName.dll` ✓ (auto-copied)
- `ProjectName.manifest` ✓ (auto-copied)

### What Needs Manual Setup (One Time)

After the first build, run the **clarioncom-deploy** skill to generate:
- `Register.bat` - COM registration script
- `Unregister.bat` - COM unregistration script
- `CheckDotNetVersion.bat` - .NET version checker
- `TestCOM.bat` - COM test script
- `TestManifests.bat` - Manifest validation script
- `README.md` - Complete integration documentation

**Command:**
```
"Set up deployment for ProjectName"
```

The clarioncom-deploy skill will:
- Extract GUIDs, ProgId, and method signatures from your source code
- Generate batch files with project-specific details
- Create README with method documentation (NO Clarion code examples)

### Complete Automated Workflow

**User Request:**
```
"Create a TimePickerCOM control that lets users select a time"
```

**What Happens Automatically:**

1. ✅ **Template files read** from `Template/` folder for structure reference
2. ✅ **C# source files created** in new project folder (Interface, Implementation, AssemblyInfo)
3. ✅ **Manifest file created** in new project folder with correct GUIDs
4. ✅ **.csproj created** in new project folder with MSBuild deployment targets
5. ✅ **Project built** (DLL generated)
6. ✅ **DLL and manifest auto-deployed** to `TimePickerCOM/Clarion/`
7. ✅ **clarioncom-deploy runs** (generates batch files + README)

**Final Result:**

**Note:** Template files in `Template/` folder remain unchanged as READ-ONLY references. New project (`TimePickerCOM/`) is created as a sibling folder to `Template/`.

```
TimePickerCOM/
  ├── ITimePicker.cs
  ├── TimePickerControl.cs
  ├── Properties/
  │   └── AssemblyInfo.cs
  ├── TimePickerCOM.csproj
  ├── TimePickerCOM.manifest
  └── Clarion/                    �? Ready for deployment!
      ├── TimePickerCOM.dll       �? Auto-copied
      ├── TimePickerCOM.manifest  �? Auto-copied
      ├── Register.bat            �? Auto-generated
      ├── Unregister.bat          �? Auto-generated
      ├── CheckDotNetVersion.bat  �? Auto-generated
      ├── TestCOM.bat             �? Auto-generated
      ├── TestManifests.bat       �? Auto-generated
      └── README.md               �? Auto-generated
```

### Subsequent Builds

**What Updates Automatically:**
- `ProjectName.dll` - Re-copied to Clarion/ on every build
- `ProjectName.manifest` - Re-copied to Clarion/ on every build

**What Stays Current:**
- Batch files (only regenerate if COM interface changes)
- README.md (only regenerate if methods/events change)

**To Regenerate Documentation:**
```
"Update deployment for ProjectName"
```
This re-runs clarioncom-deploy to refresh batch files and README with latest code changes.

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
