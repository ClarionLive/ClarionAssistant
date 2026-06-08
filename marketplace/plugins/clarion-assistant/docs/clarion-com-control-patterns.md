# Clarion COM Control Development - Complete Patterns Reference

> **Last Updated:** Based on successful AddressLookup and GanttChart implementations
>
> **Purpose:** Comprehensive reference for creating COM controls that work reliably with Clarion

---

## Table of Contents

1. [Assembly Configuration](#1-assembly-configuration)
2. [Interface Patterns](#2-interface-patterns)
3. [Event Interface Patterns](#3-event-interface-patterns)
4. [Class Implementation](#4-class-implementation)
5. [Event Declaration & Raising](#5-event-declaration--raising)
6. [Property Patterns](#6-property-patterns)
7. [Method Patterns](#7-method-patterns)
8. [GUID Management](#8-guid-management)
9. [Project Configuration](#9-project-configuration)
10. [Error Handling](#10-error-handling)
11. [Async Operations](#11-async-operations)
12. [Complete Checklist](#12-complete-checklist)
13. [Common Issues & Solutions](#13-common-issues--solutions)

---

## 1. Assembly Configuration

### Critical: AssemblyInfo.cs Settings

**Location:** `Properties/AssemblyInfo.cs`

```csharp
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("YourControlName")]
[assembly: AssemblyDescription("Description for Clarion")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Your Company")]
[assembly: AssemblyProduct("YourControlName")]
[assembly: AssemblyCopyright("Copyright © 2025")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// CRITICAL: Must be true for COM visibility
[assembly: ComVisible(true)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("UNIQUE-TYPELIB-GUID")]

// Version information
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
```

### Why ComVisible(true) is Critical

**The Problem:**
If `ComVisible(false)`, the entire assembly is invisible to COM clients, regardless of individual type attributes.

**What Happens:**
- Type library export fails
- Events don't register
- Clarion can't see the control

**The Fix:**
Always use `[assembly: ComVisible(true)]`

---

## 2. Interface Patterns

### Main Interface Structure

**File:** `IYourControl.cs`

```csharp
using System;
using System.Runtime.InteropServices;

namespace YourNamespace
{
    [ComVisible(true)]
    [Guid("UNIQUE-INTERFACE-GUID")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IYourControl
    {
        // Properties
        [DispId(1)]
        string PropertyName { get; set; }

        [DispId(2)]
        int NumericProperty { get; set; }

        // Methods
        [DispId(3)]
        void DoSomething(string parameter);

        [DispId(4)]
        string GetSomething();
    }
}
```

### Attribute Breakdown

| Attribute | Value | Purpose |
|-----------|-------|---------|
| `ComVisible` | `true` | Makes interface visible to COM |
| `Guid` | Unique GUID | Identifies interface in COM registry |
| `InterfaceType` | `InterfaceIsDual` | Supports both early and late binding |
| `DispId` | Sequential integers | Dispatch IDs for COM method/property lookup |

### InterfaceIsDual vs Other Types

```csharp
// CORRECT for main interface
[InterfaceType(ComInterfaceType.InterfaceIsDual)]

// WRONG for main interface (use for event interface only)
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]

// AVOID - causes versioning issues
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
```

---

## 3. Event Interface Patterns

### Event Interface Structure

**File:** `IYourControlEvents.cs`

```csharp
using System;
using System.Runtime.InteropServices;

namespace YourNamespace
{
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]  // NOTE: IDispatch!
    [Guid("UNIQUE-EVENTS-GUID")]
    public interface IYourControlEvents
    {
        [DispId(1)]
        void FirstEvent(string data);

        [DispId(2)]
        void SecondEvent(int value, string message);

        [DispId(3)]
        void ErrorOccurred(string errorMessage);
    }
}
```

### Critical Difference: IDispatch for Events

**Why IDispatch and not IDual?**
- COM event sinking requires IDispatch interface
- Clarion's event handling mechanism expects IDispatch
- Using IDual for events prevents Clarion from receiving them

```csharp
// CORRECT for event interface
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]

// WRONG - Events won't fire to Clarion
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
```

### DispId Requirements for Events

```csharp
// CORRECT - Sequential IDs starting from 1
[DispId(1)]
void Event1();

[DispId(2)]
void Event2();

[DispId(3)]
void Event3();

// WRONG - Missing DispId
void Event4();  // Will not be callable from Clarion

// WRONG - Duplicate IDs
[DispId(1)]
void Event5();  // Conflicts with Event1!
```

---

## 4. Class Implementation

### Complete Class Attributes

**File:** `YourControl.cs`

```csharp
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YourNamespace
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("UNIQUE-CLASS-GUID")]
    [ComSourceInterfaces(typeof(IYourControlEvents))]
    [ProgId("YourNamespace.YourControl")]
    public partial class YourControl : UserControl, IYourControl
    {
        // Implementation
    }
}
```

### Attribute Explanations

#### ComVisible(true)
Makes the class visible to COM clients.

#### ClassInterface(ClassInterfaceType.None)
**Critical:** Prevents auto-generated class interface.

```csharp
// CORRECT - Use explicit interface only
[ClassInterface(ClassInterfaceType.None)]
public class MyControl : UserControl, IMyControl

// WRONG - Auto-generated interface causes versioning problems
[ClassInterface(ClassInterfaceType.AutoDual)]
// Or missing ClassInterface entirely
```

**Why?** Auto-generated interfaces change when you add members, breaking existing Clarion code.

#### ComSourceInterfaces
Links the event interface to this class.

```csharp
[ComSourceInterfaces(typeof(IYourControlEvents))]
```

Without this, events won't be exposed to COM.

#### ProgId
Human-readable identifier for COM activation.

```csharp
// Format: Namespace.ClassName
[ProgId("AddressLookupCOM.AddressLookup")]
[ProgId("GanttChartCOM.GanttChart")]
[ProgId("SliderButton.SliderButton")]
```

**Must match** the actual namespace and class name (case-sensitive).

---

## 5. Event Declaration & Raising

### Event Delegate and Declaration

```csharp
public class YourControl : UserControl, IYourControl
{
    // Step 1: Define delegate
    public delegate void DataChangedDelegate(string newData);

    // Step 2: Declare event
    public event DataChangedDelegate DataChanged;

    // Step 3: Create raising method
    private void RaiseDataChanged(string newData)
    {
        if (DataChanged != null)
        {
            try
            {
                DataChanged(newData);
            }
            catch
            {
                // Swallow exceptions from Clarion event handlers
            }
        }
    }

    // Step 4: Call from your code
    private void SomeInternalMethod()
    {
        // ... do work ...
        RaiseDataChanged("Updated data");
    }
}
```

### Pattern: Safe Event Raising

**Always include:**
1. **Null check** - Event might have no subscribers
2. **Try-catch** - Clarion exceptions shouldn't crash control
3. **Separate method** - Keeps event logic isolated

```csharp
// TEMPLATE for event raising
private void RaiseEventName(/* parameters */)
{
    if (EventName != null)  // 1. Null check
    {
        try  // 2. Exception safety
        {
            EventName(/* parameters */);
        }
        catch { }  // Ignore Clarion-side errors
    }
}
```

### Multiple Events Example

```csharp
// Multiple event delegates
public delegate void SearchStartedDelegate(string query);
public delegate void SearchCompletedDelegate(int resultCount);
public delegate void SearchFailedDelegate(string errorMessage);

// Event declarations
public event SearchStartedDelegate SearchStarted;
public event SearchCompletedDelegate SearchCompleted;
public event SearchFailedDelegate SearchFailed;

// Raising methods
private void RaiseSearchStarted(string query)
{
    if (SearchStarted != null)
    {
        try { SearchStarted(query); }
        catch { }
    }
}

private void RaiseSearchCompleted(int resultCount)
{
    if (SearchCompleted != null)
    {
        try { SearchCompleted(resultCount); }
        catch { }
    }
}

private void RaiseSearchFailed(string errorMessage)
{
    if (SearchFailed != null)
    {
        try { SearchFailed(errorMessage); }
        catch { }
    }
}
```

---

## 6. Property Patterns

### Simple Properties

```csharp
// Interface definition
public interface IYourControl
{
    string APIKey { get; set; }
    int MaxResults { get; set; }
    bool IsEnabled { get; set; }
}

// Implementation with backing fields
private string _apiKey = "";
private int _maxResults = 10;
private bool _isEnabled = true;

public string APIKey
{
    get { return _apiKey; }
    set { _apiKey = value; }
}

public int MaxResults
{
    get { return _maxResults; }
    set { _maxResults = value; }
}

public bool IsEnabled
{
    get { return _isEnabled; }
    set { _isEnabled = value; }
}
```

### Properties with Logic

```csharp
public int DebounceMs
{
    get { return _debounceMs; }
    set
    {
        _debounceMs = value;

        // Apply side effects
        if (_debounceTimer != null)
        {
            _debounceTimer.Interval = _debounceMs;
        }
    }
}
```

### Properties That Trigger Events

```csharp
public bool Value
{
    get { return _value; }
    set
    {
        if (_value != value)
        {
            _value = value;

            // Notify Clarion of change
            RaiseValueChanged(_value);

            // Update UI
            Invalidate();
        }
    }
}
```

### Color Properties (OLE Color Conversion)

```csharp
// Clarion uses OLE colors (int)
public int OnColor
{
    get { return ColorTranslator.ToOle(_onColor); }
    set
    {
        _onColor = ColorTranslator.FromOle(value);
        Invalidate();
    }
}
```

---

## 7. Method Patterns

### Methods with Error Handling

```csharp
public int AddTask(string name, string startDate, string endDate, int progress)
{
    try
    {
        // Parse and validate
        DateTime start = DateTime.Parse(startDate);
        DateTime end = DateTime.Parse(endDate);

        // Do work
        int newId = _dataManager.AddTask(name, start, end, progress);

        // Update UI
        _control.SetTasks(_dataManager.Tasks);

        // Return success
        return newId;
    }
    catch (Exception ex)
    {
        // Show error to user
        MessageBox.Show($"Error adding task: {ex.Message}",
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

        // Return error indicator
        return -1;
    }
}
```

### String Return Methods

```csharp
public string GetSelectedAddress()
{
    // Never return null - use ?? operator
    return _selectedAddress?.FullAddress ?? "";
}

public string GetTaskName(int taskId)
{
    try
    {
        var task = _dataManager.GetTask(taskId);
        return task?.Name ?? string.Empty;
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
        return string.Empty;  // Default on error
    }
}
```

### Date Formatting for COM

```csharp
public string GetTaskStart(int taskId)
{
    try
    {
        var task = _dataManager.GetTask(taskId);

        // Use ISO 8601 format (YYYY-MM-DD)
        return task?.StartDate.ToString("yyyy-MM-dd") ?? string.Empty;
    }
    catch
    {
        return string.Empty;
    }
}
```

### Void Methods

```csharp
public void Initialize()
{
    try
    {
        _cache.Clear();
        _searchTextBox.Text = "";
        _resultsListBox.Visible = false;
        _selectedAddress = null;
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Initialization error: {ex.Message}",
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
    }
}
```

---

## 8. GUID Management

### GUID Requirements

Every COM project needs **4 unique GUIDs**:

1. **Assembly/TypeLib GUID** - `AssemblyInfo.cs`
2. **Main Interface GUID** - `IYourControl.cs`
3. **Event Interface GUID** - `IYourControlEvents.cs`
4. **Implementation Class GUID** - `YourControl.cs`

### Generating GUIDs

#### PowerShell
```powershell
[guid]::NewGuid().ToString().ToUpper()
```

#### Visual Studio
1. Tools → Create GUID
2. Select "Registry Format"
3. Click "New GUID"
4. Click "Copy"

### GUID Format

```
XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX
```

Example:
```
1A49AFF2-26BC-43FF-B36D-55807FCECF84
```

### GUID Documentation Example

```csharp
// AssemblyInfo.cs
[assembly: Guid("26A18B83-FFC0-45D4-8752-9BDB99FEE805")]

// GUIDs used in this assembly:
// Interface GUID: 185BF253-869F-4A9E-97B4-53C702EEC667
// Events GUID:    5E224245-7440-4F5C-9781-95165097F4A6
// Class GUID:     1A49AFF2-26BC-43FF-B36D-55807FCECF84
// TypeLib GUID:   26A18B83-FFC0-45D4-8752-9BDB99FEE805
```

### GUID Conflicts

**Problem:** Duplicate GUIDs cause "Element not found" errors during type library export.

**Solution:** Ensure all 4 GUIDs are unique.

```csharp
// WRONG - TypeLib and Class GUIDs are the same!
[assembly: Guid("1A49AFF2-26BC-43FF-B36D-55807FCECF84")]
// ...
[Guid("1A49AFF2-26BC-43FF-B36D-55807FCECF84")]
public class MyControl

// CORRECT - All GUIDs are unique
[assembly: Guid("26A18B83-FFC0-45D4-8752-9BDB99FEE805")]
// ...
[Guid("1A49AFF2-26BC-43FF-B36D-55807FCECF84")]
public class MyControl
```

---

## 9. Project Configuration

### Required .csproj Settings

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Target Framework - must be .NET Framework 4.7.2 -->
    <TargetFramework>net472</TargetFramework>

    <!-- Windows Forms support -->
    <UseWindowsForms>true</UseWindowsForms>

    <!-- Platform - MUST be x86 for 32-bit Clarion -->
    <PlatformTarget>x86</PlatformTarget>

    <!-- Output type -->
    <OutputType>Library</OutputType>

    <!-- Runtime identifier -->
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>

    <!-- COM Interop settings -->
    <EnableComInterop>true</EnableComInterop>
    <RegisterForComInterop>true</RegisterForComInterop>
    <ComVisible>true</ComVisible>

    <!-- Assembly info -->
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
</Project>
```

### Critical Settings Explained

#### TargetFramework: net472
```xml
<TargetFramework>net472</TargetFramework>
```
- Must be .NET Framework 4.7.2 or 4.8
- .NET Core/5+/6+ don't support COM properly
- Required for Windows Forms controls

#### PlatformTarget: x86
```xml
<PlatformTarget>x86</PlatformTarget>
```
- **Critical:** Clarion is 32-bit
- x64 builds won't work with Clarion
- Must match Clarion's architecture

#### EnableComInterop & RegisterForComInterop
```xml
<EnableComInterop>true</EnableComInterop>
<RegisterForComInterop>true</RegisterForComInterop>
```
- Generates type library (.tlb) file
- Auto-registers during build
- Required for COM visibility

---

## 10. Error Handling

### Never Throw to COM

**Rule:** Never let exceptions propagate to COM clients (Clarion).

```csharp
// WRONG - Exception propagates to Clarion
public void DoWork(string data)
{
    var result = ParseData(data);  // Might throw
    ProcessResult(result);  // Might throw
}

// CORRECT - Exceptions handled
public void DoWork(string data)
{
    try
    {
        var result = ParseData(data);
        ProcessResult(result);
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
```

### Return Default Values on Error

| Type | Default Value |
|------|---------------|
| `string` | `string.Empty` or `""` |
| `int` | `-1` or `0` (use -1 for "not found") |
| `double` | `0.0` |
| `bool` | `false` |
| `DateTime` | `DateTime.MinValue` (format as string) |

```csharp
public string GetData()
{
    try
    {
        return _data.Value;
    }
    catch
    {
        return string.Empty;  // Not null!
    }
}

public int GetCount()
{
    try
    {
        return _items.Count;
    }
    catch
    {
        return 0;
    }
}
```

### Null-Safe String Returns

```csharp
// Using null-coalescing operator
public string GetName()
{
    return _name ?? string.Empty;
}

// Using null-conditional operator
public string GetDescription()
{
    return _item?.Description ?? "";
}

// Combining both
public string GetFullAddress()
{
    return _selectedAddress?.FullAddress ?? string.Empty;
}
```

---

## 11. Async Operations

### Fire-and-Forget Pattern

**Problem:** COM interfaces cannot return `Task` (async/await).

**Solution:** Use "fire-and-forget" with synchronous interface signature.

```csharp
// Interface - synchronous signature
public interface IYourControl
{
    void Search(string query);  // Not: Task SearchAsync()
}

// Implementation
public void Search(string query)
{
    if (string.IsNullOrWhiteSpace(query))
        return;

    _searchTextBox.Text = query;

    // Fire and forget - intentionally not awaited
    _ = PerformSearchAsync(query);
}

// Internal async method
private async Task PerformSearchAsync(string query)
{
    try
    {
        // Raise event - search started
        RaiseSearchStarted(query);

        // Async work
        var results = await CallApiAsync(query);

        // Update UI on main thread
        if (InvokeRequired)
        {
            Invoke(new Action(() => UpdateResults(results)));
        }
        else
        {
            UpdateResults(results);
        }

        // Raise event - search completed
        RaiseSearchCompleted(results.Count);
    }
    catch (Exception ex)
    {
        // Raise event - search failed
        RaiseSearchFailed(ex.Message);
    }
}
```

### Thread-Safe UI Updates

```csharp
private async Task ProcessDataAsync()
{
    var data = await FetchDataAsync();

    // Check if we're on UI thread
    if (InvokeRequired)
    {
        // Marshal to UI thread
        Invoke(new Action(() => UpdateUI(data)));
    }
    else
    {
        // Already on UI thread
        UpdateUI(data);
    }
}
```

---

## 12. Complete Checklist

### Before Building

#### Assembly Level (AssemblyInfo.cs)
- [ ] `[assembly: ComVisible(true)]` - **NOT false!**
- [ ] `[assembly: Guid("...")]` - Unique GUID
- [ ] Standard assembly attributes filled in

#### Main Interface (IYourControl.cs)
- [ ] `[ComVisible(true)]`
- [ ] `[Guid("...")]` - Unique GUID
- [ ] `[InterfaceType(ComInterfaceType.InterfaceIsDual)]`
- [ ] All properties have get/set
- [ ] All methods have return types
- [ ] DispId attributes (optional but recommended)

#### Event Interface (IYourControlEvents.cs)
- [ ] `[ComVisible(true)]`
- [ ] `[Guid("...")]` - Unique GUID
- [ ] `[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]` - **IDispatch!**
- [ ] `[DispId(n)]` on every event method (sequential from 1)
- [ ] Event methods defined (typically void)

#### Implementation Class (YourControl.cs)
- [ ] `[ComVisible(true)]`
- [ ] `[ClassInterface(ClassInterfaceType.None)]`
- [ ] `[Guid("...")]` - Unique GUID
- [ ] `[ComSourceInterfaces(typeof(IYourControlEvents))]`
- [ ] `[ProgId("Namespace.ClassName")]` - matches actual names
- [ ] Inherits `UserControl` (or appropriate base)
- [ ] Implements interface: `: UserControl, IYourControl`
- [ ] Delegate definitions for all events
- [ ] Public event declarations for all events
- [ ] Event raising methods with null checks and try-catch
- [ ] Property implementations with backing fields
- [ ] Method implementations with error handling
- [ ] Parameterless constructor
- [ ] No methods throw exceptions to COM

#### Project File (.csproj)
- [ ] `<TargetFramework>net472</TargetFramework>`
- [ ] `<UseWindowsForms>true</UseWindowsForms>`
- [ ] `<PlatformTarget>x86</PlatformTarget>`
- [ ] `<OutputType>Library</OutputType>`
- [ ] `<RuntimeIdentifier>win-x86</RuntimeIdentifier>`
- [ ] `<EnableComInterop>true</EnableComInterop>`
- [ ] `<RegisterForComInterop>true</RegisterForComInterop>`
- [ ] `<ComVisible>true</ComVisible>`
- [ ] `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>`

#### GUID Verification
- [ ] All 4 GUIDs are different (Assembly, Interface, Events, Class)
- [ ] No GUIDs copied from other projects
- [ ] GUIDs documented in comments

### After Building

- [ ] Build succeeds without errors
- [ ] Type library (.tlb) file generated in output folder
- [ ] No "type library exporter" errors
- [ ] DLL registered in Windows registry (if RegisterForComInterop=true)

### Testing in Clarion

- [ ] Control appears in Clarion's COM control list
- [ ] Can add control to window/form
- [ ] Properties are visible and settable
- [ ] Methods can be called
- [ ] Events fire and are received in Clarion

---

## 13. Common Issues & Solutions

### Issue: "Type library exporter encountered an error"

**Causes:**
1. Duplicate GUIDs (Assembly GUID = Class GUID)
2. Missing required attributes
3. ComVisible(false) at assembly level

**Solution:**
- Generate unique GUID for each: Assembly, Interface, Events, Class
- Verify all attributes present
- Set ComVisible(true) in AssemblyInfo.cs

---

### Issue: Events Don't Fire in Clarion

**Causes:**
1. `[assembly: ComVisible(false)]` in AssemblyInfo.cs
2. Event interface using `InterfaceIsDual` instead of `InterfaceIsIDispatch`
3. Missing `[ComSourceInterfaces]` attribute
4. Missing `[DispId]` on event methods

**Solution:**
```csharp
// AssemblyInfo.cs
[assembly: ComVisible(true)]  // Must be true!

// Event interface
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]  // Must be IDispatch!

// Implementation class
[ComSourceInterfaces(typeof(IYourControlEvents))]  // Links events

// Event methods
[DispId(1)]  // Must have DispId
void EventName();
```

---

### Issue: Control Not Visible in Clarion

**Causes:**
1. Wrong platform target (x64 instead of x86)
2. ComVisible(false) at assembly level
3. Not registered properly

**Solution:**
```xml
<!-- .csproj -->
<PlatformTarget>x86</PlatformTarget>
<RegisterForComInterop>true</RegisterForComInterop>
```

```csharp
// AssemblyInfo.cs
[assembly: ComVisible(true)]
```

---

### Issue: Properties Not Accessible

**Causes:**
1. Property not in interface definition
2. Interface not marked as IsDual
3. Missing DispId (sometimes)

**Solution:**
```csharp
// Interface
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IYourControl
{
    [DispId(1)]
    string PropertyName { get; set; }
}

// Implementation must match
public string PropertyName { get; set; }
```

---

### Issue: Methods Fail with Errors

**Cause:** Exception thrown from method to COM client.

**Solution:** Wrap all interface methods in try-catch:

```csharp
public void Method()
{
    try
    {
        // Implementation
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error: {ex.Message}");
        // Return default value if method has return type
    }
}
```

---

### Issue: Clarion Crashes When Calling Control

**Causes:**
1. Returning null strings
2. Unhandled exceptions
3. Platform mismatch (x64/x86)

**Solution:**
```csharp
// Never return null
public string GetData()
{
    return _data ?? string.Empty;  // Not null!
}

// Platform must be x86
<PlatformTarget>x86</PlatformTarget>
```

---

## Quick Reference: The Big Three

### 1. Assembly ComVisible(true)
```csharp
[assembly: ComVisible(true)]  // In AssemblyInfo.cs
```

### 2. Correct InterfaceTypes
```csharp
// Main interface
[InterfaceType(ComInterfaceType.InterfaceIsDual)]

// Event interface
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
```

### 3. Complete Class Attributes
```csharp
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("UNIQUE-GUID")]
[ComSourceInterfaces(typeof(IYourControlEvents))]
[ProgId("Namespace.ClassName")]
public class YourControl : UserControl, IYourControl
```

---

## Working Examples

**Reference these proven implementations:**

- **AddressLookup:** `C:\Dev\AddressLookup\`
  - Multiple events (4 different events)
  - Async operations (API calls)
  - String handling
  - Property configuration

- **GanttChart:** `C:\Dev\GanttChartDemo\`
  - Complex data structures
  - Task manipulation
  - Event-driven architecture
  - Multiple method patterns

- **Template:** `C:\Dev\.templates\ClarionCOMTemplate\`
  - Minimal working example
  - All patterns demonstrated
  - Ready to copy/modify

---

## Version History

- **v1.0** - Initial documentation based on AddressLookup and GanttChart analysis
- Patterns extracted from successful production controls
- Validated against multiple Clarion integration scenarios

---

## Support

For issues or questions:
1. Check this documentation
2. Review working examples
3. Verify against complete checklist
4. Compare with template project
