# Critical COM Patterns and Common Mistakes

Required attributes/configuration for each file in a Clarion COM control project, GUID requirements, and the common-mistakes catalog.

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
    <TargetFramework>net472</TargetFramework>
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
