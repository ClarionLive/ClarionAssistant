# Remediation Patterns and Registry-to-RegFree Migration

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
2. Use the `<clrClass>` template (see references/validation-checklist.md, section 7)
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
