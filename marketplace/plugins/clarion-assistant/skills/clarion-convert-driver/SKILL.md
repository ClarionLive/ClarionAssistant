---
name: clarion-convert-driver
# prettier-ignore
description: Convert a Clarion dictionary from one file driver to another (e.g., TopSpeed to SQLite). Exports the dictionary to .dctx, transforms driver settings, regenerates GUIDs, creates a new .dct from blank template, and prepares for import. User must perform the final import manually in the IDE.
version: 1.0.0
triggers:
  - convert driver
  - change driver
  - topspeed to sqlite
  - sqlite to topspeed
  - switch driver
  - convert dictionary driver
  - migrate driver
changelog:
  - version: 1.0.0
    date: 2026-04-02
    changes:
      - Initial release supporting TopSpeed <-> SQLite conversion
---

## Overview

Converts a Clarion data dictionary from one file driver to another by transforming the .dctx export. The process:
1. Exports the source dictionary to .dctx
2. Transforms driver-specific attributes in the XML
3. Regenerates all GUIDs (required to avoid conflicts)
4. Creates a new .dct from a blank template
5. Opens the new dictionary in the IDE
6. **User must manually import the .dctx** (the programmatic import does not trigger the UI — known limitation)

## Prerequisites

- A blank dictionary template must exist at `%APPDATA%\clarionassistant\blank.dct`
- The source dictionary must be loadable in the Clarion IDE
- The source .app file should be **closed** before export (avoids file lock errors on the .dct)

## Supported Driver Conversions

### TopSpeed -> SQLite
- `Driver="TOPSPEED"` -> `Driver="SQLite"`
- `Path="tablename.tps"` -> `Owner="databasename.sqlite"` (single file for all tables)
- Add `Path="TableName"` (Full Path Name = table label)
- `Create="true"` and `Thread="true"` are preserved

### SQLite -> TopSpeed
- `Driver="SQLite"` -> `Driver="TOPSPEED"`
- `Owner="databasename.sqlite"` -> remove Owner
- `Path="TableName"` -> `Path="tablename.tps"` (lowercase table name + .tps extension)
- `Create="true"` and `Thread="true"` are preserved

## Step-by-Step Procedure

### Step 1: Validate inputs

Ask the user (if not already provided):
- **Source driver**: The current driver (e.g., TopSpeed)
- **Target driver**: The desired driver (e.g., SQLite)
- **Output name**: Name for the new dictionary file (default: `{original}_SQLite` or `{original}_TopSpeed`)

### Step 2: Close the .app if open

Warn the user: "Please close the .app file before proceeding — the dictionary export will fail if the .app holds a lock on the .dct."

Wait for confirmation before proceeding.

### Step 3: Open and export the source dictionary

```
open_dictionary  (no path = auto-discover from solution)
export_dctx      (no path = defaults to same folder as .dct with .dctx extension)
```

If export fails with "Error writing text file", the .app is likely still open. Ask the user to close it and retry.

### Step 4: Transform the .dctx

Read the exported .dctx file and make the following changes:

#### 4a: Change the Dictionary Name
On the `<Dictionary>` element, change the `Name` attribute to the output name.

#### 4b: Transform Table elements
For each `<Table>` element, apply the driver-specific changes:

**TopSpeed -> SQLite:**
```xml
<!-- Before -->
<Table ... Driver="TOPSPEED" Path="policy.tps" Create="true" Thread="true">
<!-- After -->
<Table ... Driver="SQLite" Owner="{output_name}.sqlite" Path="Policy" Create="true" Thread="true">
```
- Replace `Driver` value
- Replace `Path="xxx.tps"` with `Owner="{output_name}.sqlite"`
- Add `Path="{TableName}"` where TableName = the `Name` attribute value (this sets the Full Path Name)

**SQLite -> TopSpeed:**
```xml
<!-- Before -->
<Table ... Driver="SQLite" Owner="database.sqlite" Path="Policy" Create="true" Thread="true">
<!-- After -->
<Table ... Driver="TOPSPEED" Path="policy.tps" Create="true" Thread="true">
```
- Replace `Driver` value
- Remove `Owner` attribute
- Replace `Path` with lowercase table name + `.tps`

#### 4c: Regenerate ALL GUIDs

**This is critical.** Every GUID in the file must be replaced with a new unique GUID, but cross-references must remain consistent.

GUIDs appear in these attributes:
- `Guid` on Table, Field, Key, Component, Relation, ForeignMapping, PrimaryMapping elements
- `FieldId` on Component elements (references a Field's Guid)
- `PrimaryTable`, `ForeignTable` on Relation elements (reference Table Guids)
- `PrimaryKey`, `ForeignKey` on Relation elements (reference Key Guids)
- `Field` on ForeignMapping/PrimaryMapping elements (reference Field Guids)

Process:
1. Extract every unique GUID from the file
2. Generate a new GUID for each (use PowerShell: `[guid]::NewGuid().ToString()`)
3. Replace ALL occurrences — each old GUID maps to exactly one new GUID
4. Verify: no old GUIDs remain, same count of unique GUIDs, cross-references intact

Use PowerShell for this transformation:
```powershell
$content = Get-Content "path/to/file.dctx" -Raw
$guids = [regex]::Matches($content, '\{[0-9a-fA-F-]{36}\}') | 
    ForEach-Object { $_.Value } | Select-Object -Unique
$map = @{}
foreach ($g in $guids) { $map[$g] = '{' + [guid]::NewGuid().ToString() + '}' }
foreach ($old in $map.Keys) { $content = $content.Replace($old, $map[$old]) }
$content | Set-Content "path/to/file.dctx" -Encoding utf8 -NoNewline
```

### Step 5: Create the new dictionary

```bash
# Copy blank template
cp "%APPDATA%/clarionassistant/blank.dct" "{solution_folder}/{output_name}.dct"
```

Then open it in the IDE:
```
open_dictionary path="{solution_folder}/{output_name}.dct"
```

### Step 6: Instruct user to import

**IMPORTANT:** The programmatic `import_dctx` call does not reliably trigger in the IDE. The user must import manually.

Tell the user:
> The new dictionary is open and the transformed .dctx is ready. To complete the import:
> 1. In the Dictionary Editor, click the **Import** button (or right-click Tables -> Import)
> 2. Select the file: `{output_name}.dctx`
> 3. You may see a "Creates Duplicate Key" error — this is a known Clarion bug. Click OK to dismiss it. The import will complete successfully.
> 4. Review the tables and save the dictionary.

### Step 7: Verify (after user imports)

After the user confirms the import is done:
1. Export the new dictionary to a verification .dctx
2. Check that all tables have the correct driver settings
3. Check that all tables have the Full Path Name (Path attribute) set
4. Confirm table count, field count, key count, and relation count match the original

## Known Issues

- **"Creates Duplicate Key" error on import**: This is a non-fatal Clarion IDE bug. The import succeeds after dismissing the error. This occurs even with freshly regenerated GUIDs.
- **Export fails with "Error writing text file"**: The .app file has a lock on the .dct. Close the .app first.
- **Programmatic import_dctx appears to succeed but nothing changes**: Known limitation — the MCP tool call returns success but the IDE doesn't update. User must import manually via the Dictionary Editor UI.
- **DCTX import is finicky**: Attribute ordering matters. Always start from a Clarion-exported .dctx to preserve correct structure. Only modify values, never reorder attributes.

## Cleanup

After successful verification, optionally clean up intermediate files:
- `{original}_SQLite_check.dctx` (verification exports)
- `{original}_SQLite_exported.dctx` (comparison exports)

Keep the final `.dctx` and `.dct` files.
