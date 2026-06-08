---
name: clarioncom-control
# prettier-ignore
description: Create and validate C# COM controls for Clarion with correct patterns. Auto-applies when user mentions COM control, Clarion control, ActiveX, or debugging COM registration/event issues. Uses parallel execution for independent operations.
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

**Before reading any template files**, check if a local Template/ folder exists:

```bash
powershell -Command "if (Test-Path 'Template') { Write-Output 'LOCAL' } else { Write-Output 'GLOBAL' }"
```

**Based on the result:**
- **LOCAL**: Use `Template/` for all template file reads (project was copied from COMTemplate)
- **GLOBAL**: Use `$CLARIONCOM_HOME\Templates\` for all template file reads

**DO NOT attempt to read from local Template/ if it doesn't exist** - this causes unnecessary error messages.

### Resolved Paths Summary
- Templates: `Template/` (if exists) OR `$CLARIONCOM_HOME\Templates\`
- Scripts: `$CLARIONCOM_HOME\scripts\`

## Execution Strategy

**IMPORTANT:** Use subagents where appropriate, and run subagents in parallel when possible to maximize performance.

Many steps in the COM control creation workflow can be parallelized:
- **File reading operations** - Read multiple template files from Template/ folder simultaneously
- **File creation operations** - Create multiple new control files in project root simultaneously
- **Multiple file updates** - Update interface files, implementation, and manifest in parallel
- **GUID generation** - Generate all 4 GUIDs at once
- **File validation** - Check multiple files for correctness concurrently
- **Code updates** - Update namespaces, class names, and attributes across files in parallel

**Use parallel execution whenever steps are independent and don't depend on each other's results.**

# Clarion COM Control Development Skill

## Purpose
This skill ensures all COM controls for Clarion are created with correct patterns from the start, preventing common issues with COM interop, event registration, and interface visibility.

## When to Use
- Creating a new COM control for Clarion
- User mentions "COM control", "Clarion control", or "ActiveX"
- Reviewing or fixing existing COM control code
- Debugging COM registration or event issues

## CRITICAL WORKFLOW - Read This First!

### Creating a New Control - COPY from Template/ Folder!

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

### Why Copy from Template/ Instead of Create New?

The Template/ folder contains all correct patterns. By copying and modifying:
- You preserve all critical patterns
- You don't accidentally omit required attributes
- Template/ folder remains unchanged as reference
- You can create multiple controls from the same template
- The build system works correctly

### Verification After Creation

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

### CRITICAL: DO NOT Create Clarion Demo Applications!

**IMPORTANT:** Your job is ONLY to create the COM control (.NET C# code).

**DO NOT:**
- ❌ Create Clarion .clw files
- ❌ Create Clarion demo applications
- ❌ Write Clarion source code
- ❌ Generate Clarion project files

**WHY:** The user will create their own Clarion application to test the control. Creating Clarion files is outside your scope and not needed.

**Your scope:**
- ✅ Create the COM control (C# .NET)
- ✅ Build the DLL
- ✅ Generate deployment files (manifest, details, events, methods, README.html)
- ✅ Report success with location of Clarion folder

**After building, simply tell the user:**
"The control is ready in the Clarion folder. You can now add it to your Clarion application."

## Critical Patterns Checklist

### 1. Assembly Configuration (AssemblyInfo.cs)
**CRITICAL:** Must have `[assembly: ComVisible(true)]` - NOT false!

```csharp
[assembly: ComVisible(true)]
[assembly: Guid("UNIQUE-GUID-FOR-TYPELIB")]
```

### 2. Main Interface (IYourControl.cs)
```csharp
[ComVisible(true)]
[Guid("UNIQUE-GUID-FOR-INTERFACE")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]  // Dual for main interface
public interface IYourControl
{
    // Properties
    string PropertyName { get; set; }

    // Methods
    void MethodName(string param);

    [DispId(7)]
    void About();
}
```

### 3. Event Interface (IYourControlEvents.cs)
**CRITICAL:** Must use `InterfaceIsIDispatch` for events - NOT Dual!

```csharp
[ComVisible(true)]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]  // IDispatch for events!
[Guid("UNIQUE-GUID-FOR-EVENTS")]
public interface IYourControlEvents
{
    [DispId(1)]  // Sequential IDs starting from 1
    void EventName(string param);
}
```

### 4. Implementation Class (YourControl.cs)
```csharp
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]  // Prevents auto-generated interface
[Guid("UNIQUE-GUID-FOR-CLASS")]
[ComSourceInterfaces(typeof(IYourControlEvents))]  // Links event interface
[ProgId("Namespace.ClassName")]
public class YourControl : UserControl, IYourControl
{
    // Event delegates
    public delegate void EventNameDelegate(string param);
    public event EventNameDelegate EventName;

    // Event raising with null check and try-catch
    private void RaiseEventName(string param)
    {
        if (EventName != null)
        {
            try
            {
                EventName(param);
            }
            catch { }
        }
    }

    // About method implementation
    [ComVisible(true)]
    [Description("Display control information and version")]
    public void About()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetName().Name;
            var version = assembly.GetName().Version.ToString();
            MessageBox.Show($"{name}\nVersion: {version}", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error displaying about information: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
```

### 5. Project Configuration (.csproj)
```xml
<PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <PlatformTarget>x86</PlatformTarget>
    <OutputType>Library</OutputType>
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>
    <!-- NO EnableComInterop - we use RegFree COM with manifest instead -->
    <!-- NO RegisterForComInterop - no automatic registry registration -->
    <ComVisible>true</ComVisible>
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
```

**IMPORTANT:** We use Registration-Free COM (RegFree COM):
- **NO** `EnableComInterop` - would generate unwanted .tlb files
- **NO** `RegisterForComInterop` - no automatic registry registration
- **NO** .tlb file generated or needed
- Uses .manifest file for COM registration instead
- Manifest is automatically generated and copied to Clarion folder during build
- Simpler, cleaner approach with no registry pollution

**CRITICAL - Template Folder Exclusion:**
The .csproj file MUST include an ItemGroup that excludes the Template/ folder from compilation:
- When COMTemplate folder is copied to create a new project, the Template/ subfolder comes along
- Without exclusion, MSBuild picks up .cs files from Template/ causing duplicate assembly attribute errors
- This exclusion ensures only the project's own source files are compiled
- The exclusion pattern `Template\**\*` removes all files in Template/ from all build actions

## GUID Requirements
Each project needs **4 unique GUIDs**:
1. Assembly TypeLib GUID (AssemblyInfo.cs)
2. Main Interface GUID (IYourControl.cs)
3. Event Interface GUID (IYourControlEvents.cs)
4. Implementation Class GUID (YourControl.cs)

Generate with: `[guid]::NewGuid().ToString().ToUpper()`

## CRITICAL: OnHandleCreated() Pattern for Child Controls

**This is the #1 most critical pattern for COM controls that contain child controls.**

### Why This Matters

When a COM control is instantiated from Clarion:
1. The control is created by COM interop
2. The constructor runs immediately
3. **The window handle does NOT exist yet**
4. Any attempt to add child controls or manipulate the window fails silently or crashes

You MUST wait for the window handle to be created before adding child controls.

### The Wrong Way (WILL FAIL IN CLARION)

```csharp
// ❌ FATAL - Constructor called before window handle exists
public MyControl()
{
    InitializeComponent();

    _button = new Button();
    Controls.Add(_button);  // FAILS - no window handle yet!

    _textBox = new TextBox();
    Controls.Add(_textBox);  // FAILS
}
```

**What happens:** When instantiated from Clarion, the child controls are never properly created. They may appear to initialize but won't work correctly, cause exceptions, or cause the control to malfunction completely.

### The Correct Way (WORKS IN CLARION)

```csharp
// ✅ CORRECT - Initialize in OnHandleCreated
private Button _button;
private TextBox _textBox;
private bool _controlsInitialized = false;

public MyControl()
{
    InitializeComponent();
    // Field initialization ONLY - no Controls.Add() here!
}

protected override void OnHandleCreated(EventArgs e)
{
    base.OnHandleCreated(e);

    // Guard against double initialization
    if (_controlsInitialized)
        return;

    try
    {
        _button = new Button();
        _button.Text = "Click Me";
        _button.Dock = DockStyle.Top;
        Controls.Add(_button);  // SAFE - handle exists now

        _textBox = new TextBox();
        _textBox.Dock = DockStyle.Fill;
        Controls.Add(_textBox);  // SAFE

        _controlsInitialized = true;
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error initializing controls: {ex.Message}");
    }
}
```

### Critical Rules for Child Controls

1. **NEVER create child controls in the constructor**
   ```csharp
   // ❌ WRONG
   public MyControl()
   {
       var button = new Button();
       Controls.Add(button);  // NO!
   }
   ```

2. **NEVER call Controls.Add() in the constructor**
   ```csharp
   // ❌ WRONG
   public MyControl()
   {
       Controls.Add(new Button());  // NO!
   }
   ```

3. **ALWAYS create child controls in OnHandleCreated()**
   ```csharp
   // ✅ CORRECT
   protected override void OnHandleCreated(EventArgs e)
   {
       base.OnHandleCreated(e);
       Controls.Add(new Button());  // YES!
   }
   ```

4. **ALWAYS guard against double initialization**
   ```csharp
   private bool _initialized = false;

   protected override void OnHandleCreated(EventArgs e)
   {
       base.OnHandleCreated(e);

       if (_initialized)
           return;

       // Initialize child controls here
       _initialized = true;
   }
   ```

### Proper Null Checking Pattern for Child Controls

When you need to access child controls from properties or methods:

```csharp
// ✅ CORRECT - Check if initialized AND not disposed
public string ButtonText
{
    get
    {
        if (_button != null && !_button.IsDisposed)
            return _button.Text;
        return string.Empty;
    }
    set
    {
        if (_button != null && !_button.IsDisposed)
            _button.Text = value ?? string.Empty;
    }
}

// ✅ CORRECT - Safe method that works before/after initialization
public void SetContent(string text)
{
    if (_textBox != null && !_textBox.IsDisposed)
        _textBox.Text = text ?? string.Empty;
}
```

### CRITICAL: Do NOT Use 'new' to Hide Members

When creating child controls, never use the `new` keyword to shadow UserControl members:

```csharp
// ❌ WRONG - Member shadowing breaks COM interop
public class MyControl : UserControl
{
    public new Button Controls { get; set; }  // FATAL! Hides UserControl.Controls
}

// ✅ CORRECT - Use descriptive names instead
public class MyControl : UserControl
{
    private Button _primaryButton;  // Clear name, doesn't shadow
}
```

**Why this matters:** The `new` keyword hides the base class member from COM interop. This breaks the control when used from Clarion.

### Example: Complete Control with Child Controls

```csharp
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("12345678-1234-1234-1234-123456789012")]
[ComSourceInterfaces(typeof(IMyControlEvents))]
[ProgId("MyNamespace.MyControl")]
public class MyControl : UserControl, IMyControl
{
    // Child control references
    private Button _okButton;
    private Button _cancelButton;
    private TextBox _inputBox;
    private bool _controlsInitialized = false;

    // Events
    public delegate void OKClickedDelegate();
    public event OKClickedDelegate OKClicked;

    public MyControl()
    {
        // Field initialization only - no Controls.Add() here!
        InitializeComponent();
        this.Size = new Size(300, 200);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_controlsInitialized)
            return;

        try
        {
            // Create button 1
            _okButton = new Button();
            _okButton.Text = "OK";
            _okButton.Location = new Point(10, 10);
            _okButton.Click += (s, e2) => RaiseOKClicked();
            Controls.Add(_okButton);

            // Create button 2
            _cancelButton = new Button();
            _cancelButton.Text = "Cancel";
            _cancelButton.Location = new Point(100, 10);
            Controls.Add(_cancelButton);

            // Create text box
            _inputBox = new TextBox();
            _inputBox.Location = new Point(10, 50);
            _inputBox.Size = new Size(200, 30);
            Controls.Add(_inputBox);

            _controlsInitialized = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
        }
    }

    // Property with proper null checking
    public string InputText
    {
        get
        {
            if (_inputBox != null && !_inputBox.IsDisposed)
                return _inputBox.Text;
            return string.Empty;
        }
        set
        {
            if (_inputBox != null && !_inputBox.IsDisposed)
                _inputBox.Text = value ?? string.Empty;
        }
    }

    // Event raising
    private void RaiseOKClicked()
    {
        if (OKClicked != null)
        {
            try
            {
                OKClicked();
            }
            catch { }
        }
    }
}
```

## Common Mistakes to Avoid

### ❌ ComVisible(false) at Assembly Level
```csharp
[assembly: ComVisible(false)]  // BREAKS EVERYTHING!
```

### ❌ Wrong InterfaceType for Events
```csharp
[InterfaceType(ComInterfaceType.InterfaceIsDual)]  // WRONG for events!
// Should be InterfaceIsIDispatch for event interfaces
```

### ❌ Missing DispId on Events
```csharp
void EventName();  // Missing [DispId(n)]
```

### ❌ Duplicate GUIDs
Never copy GUIDs from another project - each must be unique!

### ❌ Wrong Platform Target
```xml
<PlatformTarget>x64</PlatformTarget>  <!-- WRONG - Clarion is 32-bit -->
<PlatformTarget>x86</PlatformTarget>  <!-- CORRECT -->
```

### ❌ Throwing Exceptions to COM
```csharp
// WRONG
public void Method()
{
    throw new Exception();
}

// CORRECT
public void Method()
{
    try { /* ... */ }
    catch (Exception ex)
    {
        MessageBox.Show(ex.Message);
    }
}
```

### ❌ Returning Null Strings
```csharp
// WRONG
return _value;  // Could be null

// CORRECT
return _value ?? string.Empty;
```

## Validation Process

When creating or reviewing a COM control:

1. **Check AssemblyInfo.cs**
   - [ ] ComVisible(true) at assembly level
   - [ ] Unique GUID for type library

2. **Check Main Interface**
   - [ ] ComVisible(true)
   - [ ] InterfaceIsDual
   - [ ] Unique GUID
   - [ ] All members defined

3. **Check Event Interface**
   - [ ] ComVisible(true)
   - [ ] InterfaceIsIDispatch (NOT Dual!)
   - [ ] Unique GUID
   - [ ] DispId on all event methods (sequential from 1)

4. **Check Implementation Class**
   - [ ] ComVisible(true)
   - [ ] ClassInterface(None)
   - [ ] Unique GUID
   - [ ] ComSourceInterfaces links to event interface
   - [ ] ProgId matches namespace.classname
   - [ ] Delegates and events declared
   - [ ] Event raising methods have null checks and try-catch
   - [ ] Methods have error handling (never throw to COM)
   - [ ] Strings never return null
   - [ ] About() method defined in interface with [DispId]
   - [ ] About() method implemented in class
   - [ ] .env file exists for version management (if building)

5. **Check Project File**
   - [ ] TargetFramework: net48
   - [ ] PlatformTarget: x86
   - [ ] NO EnableComInterop (using RegFree COM)
   - [ ] NO RegisterForComInterop (using RegFree COM)
   - [ ] ComVisible: true

6. **Check GUID Uniqueness**
   - [ ] All 4 GUIDs are different
   - [ ] No GUIDs copied from other projects

7. **Check Manifest File** (YourControl.manifest)
   - [ ] clsid matches Class GUID
   - [ ] tlbid matches TypeLib GUID
   - [ ] progid matches namespace.classname
   - [ ] File is in project root (gets copied to Clarion folder on build)

8. **Check Build Output** (Clarion folder after build)
   - [ ] AssemblyName.dll exists
   - [ ] AssemblyName.manifest exists
   - [ ] AssemblyName.header exists
   - [ ] ProgID.details exists
   - [ ] ProgID.events exists
   - [ ] ProgID.methods exists
   - [ ] readme_AssemblyName.html exists

## Reference Files

The template contains all correct patterns and examples needed for successful COM control creation.

- **Full Documentation:** `.claude/docs/clarion-com-control-patterns.md` - Complete technical reference

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
