# Deployment Requirements and Metadata File Formats

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
