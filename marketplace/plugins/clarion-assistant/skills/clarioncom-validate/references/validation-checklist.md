# Complete Validation Checklist (full detail)

## 1. Assembly Configuration (AssemblyInfo.cs)

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

## 2. Main Interface (Methods Interface)

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

## 2a. Color Parameter Naming (IDE Integration)

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

## 3. Event Interface

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

## 4. Implementation Class

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

## 5. Project Configuration (.csproj)

**REQUIRED Settings for RegFree COM:**
```xml
<PropertyGroup>
    <TargetFramework>net472</TargetFramework>      <!-- or net48 -->
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

## 6. GUID Uniqueness

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

## 7. Manifest File

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

## 8. Constructor Pattern (CRITICAL for OCX Creation)

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

## Quick Reference: Interface Types

| Interface Type | Use For | Why |
|---------------|---------|-----|
| `InterfaceIsDual` | Methods interface | Supports early + late binding |
| `InterfaceIsIDispatch` | Events interface | Required for COM event sinks |
| `ClassInterfaceType.None` | Implementation class | Prevents auto-generated interface |
