---
name: clarioncom-validate
# prettier-ignore
description: Validate and remediate existing C# COM controls for Clarion RegFree COM compliance. Auto-applies when reviewing COM controls, fixing COM issues, or migrating from registry-based to manifest-based COM. Uses parallel execution for independent validation checks.
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
- Templates: `$CLARIONCOM_HOME\Templates\`
- Scripts: `$CLARIONCOM_HOME\scripts\`

# Clarion COM Validator Skill

## Purpose
This skill validates existing COM controls against RegFree COM requirements for Clarion and provides remediation steps for non-compliant controls.

## When to Use
- Reviewing an existing COM control for Clarion compatibility
- Migrating from registry-based COM to RegFree COM
- Debugging COM activation or event issues
- Validating a COM control before deployment
- User asks to "check", "validate", "review", or "fix" a COM control

## Execution Strategy

**IMPORTANT:** Use parallel execution for independent validation checks:
- Check AssemblyInfo.cs, interface files, and implementation files in parallel
- Validate all GUIDs simultaneously
- Check project configuration while analyzing source files

## Complete Validation Checklist

### 1. Assembly Configuration (AssemblyInfo.cs)

**REQUIRED Attributes:**
```csharp
[assembly: ComVisible(true)]                    // MUST be true, not false
[assembly: Guid("UNIQUE-TYPELIB-GUID-HERE")]    // REQUIRED for RegFree COM
```

**Common Issues:**
- Missing assembly-level GUID (causes manifest type library registration failure)
- ComVisible(false) at assembly level (breaks entire COM exposure)

**Fix:** Add the missing GUID attribute with a newly generated GUID.

---

### 2. Main Interface (Methods Interface)

**REQUIRED Attributes:**
```csharp
[ComVisible(true)]
[Guid("UNIQUE-INTERFACE-GUID")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]  // MUST be InterfaceIsDual
public interface IYourControlMethods
{
    // Methods here
}
```

**Common Issues:**
- Using `InterfaceIsIDispatch` instead of `InterfaceIsDual`
  - Impact: Prevents early binding, reduces performance
  - Fix: Change to `InterfaceIsDual`

**Why InterfaceIsDual for Methods:**
- Supports both early binding (vtable) and late binding (IDispatch)
- Required for optimal Clarion integration
- Provides type safety and IntelliSense support

---

### 2a. Color Parameter Naming (IDE Integration)

**REQUIRED for Clarion IDE color selector support:**

Check that all color-related methods and properties include "color" in their name:

```csharp
// CORRECT
void SetBackgroundColor(string hexColor);
string TextColor { get; set; }

// WRONG - Needs remediation
void SetBackground(string hex);
string Foreground { get; set; }
```

**Validation checklist:**
- [ ] Methods accepting hex color strings include "color" in name
- [ ] Properties storing color values include "color" in name

**Remediation:** Rename method/property to include "color" (e.g., `SetBackground` → `SetBackgroundColor`)

---

### 3. Event Interface

**REQUIRED Attributes:**
```csharp
[ComVisible(true)]
[Guid("UNIQUE-EVENT-GUID")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]  // MUST be InterfaceIsIDispatch
public interface IYourControlEvents
{
    [DispId(1)]
    void EventName(string param);

    [DispId(2)]
    void AnotherEvent();
    // Sequential DispIds starting from 1
}
```

**Common Issues:**
- Using `InterfaceIsDual` for events (causes event registration failure)
- Missing `[DispId(n)]` attributes on event methods
- Non-sequential DispIds

**Why InterfaceIsIDispatch for Events:**
- COM event sinks use late binding exclusively
- Dual interface causes marshaling problems for events
- Clarion requires IDispatch for event handling

---

### 4. Implementation Class

**REQUIRED Attributes:**
```csharp
[ComVisible(true)]
[Guid("UNIQUE-CLASS-GUID")]
[ProgId("Namespace.ClassName")]
[ClassInterface(ClassInterfaceType.None)]
[ComSourceInterfaces(typeof(IYourControlEvents))]
public class YourControl : UserControl, IYourControlMethods
```

**Common Issues:**
- Missing `ClassInterface(ClassInterfaceType.None)` (creates auto-generated interface)
- Missing `ComSourceInterfaces` (events not exposed)
- Incorrect ProgId format

---

### 5. Project Configuration (.csproj)

**REQUIRED Settings for RegFree COM:**
```xml
<PropertyGroup>
    <TargetFramework>net48</TargetFramework>      <!-- or net48 -->
    <PlatformTarget>x86</PlatformTarget>          <!-- MUST be x86 for Clarion -->
    <ComVisible>true</ComVisible>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>

    <!-- RegFree COM - NO registry registration -->
    <!-- Do NOT include EnableComInterop or RegisterForComInterop -->
</PropertyGroup>
```

**Common Issues:**
- `PlatformTarget` not x86 (Clarion is 32-bit)
- `EnableComInterop` or `RegisterForComInterop` present (conflicts with RegFree)

---

### 6. GUID Uniqueness

**Requirements:**
- Each project needs 4 unique GUIDs:
  1. Assembly TypeLib GUID (AssemblyInfo.cs)
  2. Main Interface GUID (IYourControl.cs)
  3. Event Interface GUID (IYourControlEvents.cs)
  4. Implementation Class GUID (YourControl.cs)

**Validation:**
- All 4 GUIDs must be different from each other
- GUIDs must not be copied from other projects
- Use `[guid]::NewGuid().ToString().ToUpper()` to generate

---

### 7. Manifest File

**REQUIRED for RegFree COM:**

Each COM control needs a manifest file (`ControlName.manifest`):

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
    <assemblyIdentity
        type="win32"
        name="AssemblyName"
        version="1.0.0.0"
        processorArchitecture="x86" />

    <clrClass
        clsid="{CLASS-GUID-HERE}"
        progid="Namespace.ClassName"
        threadingModel="Both"
        name="Namespace.ClassName"
        runtimeVersion="v4.0.30319">
    </clrClass>

    <file name="AssemblyName.dll">
        <typelib
            tlbid="{TYPELIB-GUID-HERE}"
            version="1.0"
            helpdir="" />
    </file>
</assembly>
```

**CRITICAL:** Must use `<clrClass>`, NOT `<comClass>`:
- `<clrClass>` = .NET COM components (correct)
- `<comClass>` = Native COM components (WRONG for .NET)

---

### 8. Constructor Pattern (CRITICAL for OCX Creation)

**REQUIRED Pattern:**
```csharp
public class YourControl : UserControl, IYourControlMethods
{
    private ElementHost _elementHost;
    private YourWpfControl _wpfControl;

    public YourControl()
    {
        // CONSTRUCTOR: Field initialization ONLY
        // DO NOT create child controls here
        // DO NOT call Controls.Add() here

        this.SetStyle(ControlStyles.OptimizedDoubleBuffer |
                      ControlStyles.UserPaint |
                      ControlStyles.AllPaintingInWmPaint, true);
        this.UpdateStyles();

        this.Dock = DockStyle.Fill;
        this.BackColor = System.Drawing.Color.White;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (!DesignMode)
        {
            // SAFE: Windows handle now exists
            _elementHost = new ElementHost { Dock = DockStyle.Fill };
            _wpfControl = new YourWpfControl();
            _elementHost.Child = _wpfControl;

            this.Controls.Add(_elementHost); // Safe here!

            // Wire up events, load data, etc.
        }
    }
}
```

**Common Issues (CRITICAL - Prevents OCX Creation):**
```csharp
// WRONG - This breaks COM/ActiveX control contract!
public YourControl()
{
    _elementHost = new ElementHost { ... };      // ❌ NO!
    _wpfControl = new YourWpfControl();          // ❌ NO!
    this.Controls.Add(_elementHost);             // ❌ CRITICAL VIOLATION!
    _repo.LoadAll();                             // ❌ NO data operations!
}
```

**Why This Matters:**
- COM containers (like Clarion) instantiate the control before the Windows handle exists
- `Controls.Add()` in constructor fails because there's no handle to add to
- This breaks the COM/ActiveX control contract
- **Result:** Clarion cannot recognize it as a valid OCX object

**Impact:** Control will NOT be recognized as an OCX by Clarion. The COM object creation will fail completely.

---

## Migration from Registry-Based to RegFree COM

### Step 1: Remove Registry Registration

**Remove from build scripts:**
- `regasm.exe /tlb /codebase` commands
- Any `RegisterForComInterop` settings

**Remove from .csproj:**
```xml
<!-- REMOVE these if present -->
<EnableComInterop>true</EnableComInterop>
<RegisterForComInterop>true</RegisterForComInterop>
```

### Step 2: Create Manifest Files

For each COM control:
1. Create `ControlName.manifest` in project root
2. Use the `<clrClass>` template above
3. Substitute GUIDs from source code:
   - `clsid` = Class GUID from implementation
   - `tlbid` = Assembly GUID from AssemblyInfo.cs
   - `progid` = ProgId from class attribute
   - `name` = Full class name (Namespace.ClassName)

### Step 3: Update Build Process

Add MSBuild target to copy manifest to output:
```xml
<Target Name="CopyManifest" AfterTargets="Build">
    <Copy SourceFiles="$(ProjectDir)ControlName.manifest"
          DestinationFolder="$(OutDir)" />
</Target>
```

### Step 4: Update CLAUDE.md

Remove all references to COM registration, regasm, or Administrator requirements.

---

## Validation Report Format

When validating a COM control, provide:

```
## COM Control Validation Report

### Control: [ControlName]

#### 1. Assembly Configuration
- [ ] ComVisible(true) at assembly level
- [ ] Assembly GUID present
Status: [PASS/FAIL]

#### 2. Main Interface ([InterfaceName])
- [ ] ComVisible(true)
- [ ] InterfaceType is InterfaceIsDual
- [ ] Unique GUID
Status: [PASS/FAIL]

#### 3. Event Interface ([EventInterfaceName])
- [ ] ComVisible(true)
- [ ] InterfaceType is InterfaceIsIDispatch
- [ ] All events have DispId
- [ ] Sequential DispIds
Status: [PASS/FAIL]

#### 3a. About Method (Version Display)
- [ ] About() method defined in interface with [DispId]
- [ ] About() method implemented in class with [ComVisible(true)]
- [ ] .env file exists with valid MAJOR_VERSION, MINOR_VERSION, BUILD_NUMBER (for projects being built)
Status: [PASS/FAIL]

#### 4. Implementation Class ([ClassName])
- [ ] ComVisible(true)
- [ ] ClassInterface(None)
- [ ] ComSourceInterfaces present
- [ ] ProgId format correct
Status: [PASS/FAIL]

#### 5. Project Configuration
- [ ] PlatformTarget x86
- [ ] No EnableComInterop
- [ ] No RegisterForComInterop
Status: [PASS/FAIL]

#### 6. Manifest File
- [ ] Manifest exists
- [ ] Uses clrClass (not comClass)
- [ ] GUIDs match source code
Status: [PASS/FAIL]

#### 7. Constructor Pattern (CRITICAL)
- [ ] No Controls.Add() in constructor
- [ ] No child control creation in constructor
- [ ] Uses OnHandleCreated for control initialization
- [ ] No data operations in constructor
Status: [PASS/FAIL]

### Summary
Total Issues: [N]
Critical: [N]
Warnings: [N]

### Remediation Steps
1. [First fix needed]
2. [Second fix needed]
...
```

---

## Common Remediation Patterns

### Fix: Missing Assembly GUID
```csharp
// Add to AssemblyInfo.cs
[assembly: Guid("GENERATE-NEW-GUID-HERE")]
```

### Fix: Wrong Interface Type
```csharp
// Change from:
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]

// To:
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
```

### Fix: Missing Manifest
Create manifest file with correct `<clrClass>` element and all required GUIDs.

### Fix: Registry-Based Build
1. Remove regasm.exe calls from batch files
2. Remove EnableComInterop/RegisterForComInterop from .csproj
3. Add manifest copy target to .csproj

### Fix: Constructor Pattern Violation (CRITICAL)

Move child control creation from constructor to `OnHandleCreated`:

**Before (BROKEN - prevents OCX creation):**
```csharp
public YourControl()
{
    // Basic setup is OK
    this.Dock = DockStyle.Fill;

    // WRONG - These break COM contract!
    _elementHost = new ElementHost { Dock = DockStyle.Fill };
    _wpfControl = new YourWpfControl();
    _elementHost.Child = _wpfControl;
    this.Controls.Add(_elementHost);
    _repo.LoadAll();
    WireUpEvents();
}
```

**After (FIXED - Clarion can create OCX):**
```csharp
public YourControl()
{
    // Constructor: ONLY basic setup
    this.SetStyle(ControlStyles.OptimizedDoubleBuffer |
                  ControlStyles.UserPaint |
                  ControlStyles.AllPaintingInWmPaint, true);
    this.UpdateStyles();
    this.Dock = DockStyle.Fill;
    this.BackColor = System.Drawing.Color.White;
}

protected override void OnHandleCreated(EventArgs e)
{
    base.OnHandleCreated(e);

    if (!DesignMode)
    {
        // NOW safe - handle exists
        _elementHost = new ElementHost { Dock = DockStyle.Fill };
        _wpfControl = new YourWpfControl();
        _elementHost.Child = _wpfControl;
        this.Controls.Add(_elementHost);
        _repo.LoadAll();
        WireUpEvents();
    }
}
```

---

## Integration with Other Skills

This skill works with:
- **clarioncom-control** - Reference for correct patterns
- **clarioncom-build** - Build after remediation
- **clarioncom-deploy** - Generate deployment artifacts after validation

Typical workflow:
1. User asks to validate/fix existing COM control
2. **This skill validates and identifies issues**
3. Apply fixes to source code
4. clarioncom-build builds the fixed control
5. clarioncom-deploy generates deployment files

---

## Quick Reference: Interface Types

| Interface Type | Use For | Why |
|---------------|---------|-----|
| `InterfaceIsDual` | Methods interface | Supports early + late binding |
| `InterfaceIsIDispatch` | Events interface | Required for COM event sinks |
| `ClassInterfaceType.None` | Implementation class | Prevents auto-generated interface |

---

## Deployment Requirements

After validation and remediation, the Clarion folder should contain:
- `AssemblyName.dll` - The COM control DLL
- `AssemblyName.manifest` - RegFree COM registration
- `AssemblyName.header` - Assembly header info (includes ClarionPath, DLL name, ProgIDs)
- `ProgID.details` - Control metadata
- `ProgID.methods` - Method definitions
- `ProgID.events` - Event definitions
- `readme_AssemblyName.html` - Usage documentation

**File Naming Convention:**
- DLL, manifest, header use **assembly name**
- Metadata files (.details, .events, .methods) use **ProgID**

**Example for `InventoryGridControl.dll` with ProgID `InventoryGridControl.InventoryGridControl`:**
- `InventoryGridControl.dll`
- `InventoryGridControl.manifest`
- `InventoryGridControl.header`
- `InventoryGridControl.InventoryGridControl.details`
- `InventoryGridControl.InventoryGridControl.events`
- `InventoryGridControl.InventoryGridControl.methods`
- `readme_InventoryGridControl.html`

---

## Metadata File Format (Tagged Structure)

These files use a specific tagged format for Clarion template compatibility.

### .details File Format
```
[FriendlyName]
ControlName
[ProgID]
AssemblyName.ClassName
[FilenameNoExtenstion]
AssemblyName
[Description]
Human-readable description of the control
[ObjectName]
shortname
```

### .events File Format
```
[Event]
EventName
[EventDescription]
Description of when this event fires
[Parameter1]
paramName
[Parameter1Type]
STRING
[Parameter1Description]
Description of the parameter
[Parameter2]
secondParam
[Parameter2Type]
LONG
[Parameter2Description]
Description of second parameter
```

Repeat the `[Event]` block for each event. Parameter types: `STRING`, `LONG`, `BYTE`, `SHORT`

### .methods File Format
```
[Properties]
[Methods]
[Method]
MethodName
[MethodDescription]
Description of what the method does
[ReturnType]
STRING
[Parameter]
paramName
[ParameterType]
STRING
[ParameterDescription]
Description of the parameter
```

- Start with `[Properties]` then `[Methods]`
- Use `[ReturnType]` only for methods that return values
- Repeat `[Parameter]`/`[ParameterType]`/`[ParameterDescription]` for each parameter
