# Metadata Schemas: Submission Info, Source Files, manifest.yaml, api-docs.json, Validation

## Step 3 Detail: Gather Submission Information

### 3a. Auto-detect from project record (ALWAYS do this first)

Call `get_ca_project_info` with the project folder path to retrieve saved project data:

```
get_ca_project_info(folder: "PROJECT_PATH")
```

This returns:
- `repoUrl` — full GitHub URL (e.g., `https://github.com/peterparker57/DatePickerWebviewCOM`)
- `githubUsername` — GitHub username (e.g., `peterparker57`)
- `githubDisplayName` — display name for the author
- `repoName` — repository name
- `type` — project type (e.g., `COM Control`)

### 3b. Use auto-detected values, only ask for what's missing

**If `repoUrl` is populated:**
- Use it directly — do NOT ask the user for the GitHub URL
- Use `githubUsername` for the author's GitHub profile
- Use `githubDisplayName` for the author name

**If `repoUrl` is empty** (no GitHub account linked):
- Ask the user for the GitHub Repository URL, Author Name, and GitHub Username

**Always ask the user for** (cannot be auto-detected):

1. **Category** (required, select one):
   - UI Controls
   - Data
   - Utility
   - Integration
   - WebView2

2. **Tags** (optional)
   - Comma-separated list of searchable tags
   - Example: "calendar, date-picker, scheduling"

### 3c. Confirm with user

Display the auto-detected + user-provided values for confirmation:

```
Submission Details:
  Repository: https://github.com/peterparker57/DatePickerWebviewCOM (from project)
  Author: PeterParker57 (from project)
  GitHub: peterparker57 (from project)
  Category: [user selected]
  Tags: [user provided]

Proceed with submission?
```

---

## Step 4 Detail: Extract Metadata from Project Files

Parse the existing metadata files to extract control information.

### Read .details File

The `.details` file contains:
- Control name
- Description
- ProgID
- Assembly name

```
# Format of .details file:
Name: Control Display Name
Description: Full description of the control
ProgID: Namespace.ClassName
AssemblyName: AssemblyName
Version: 1.0.0
ControlType: standard|webview2
UILibrary: WinForms|WPF|WebView2|DevExpress|Custom
```

### Read .methods File

The `.methods` file contains method definitions in format:
```
MethodName(paramType paramName, ...): returnType
  Description of what the method does
```

### Read .events File

The `.events` file contains event definitions in format:
```
EventName(sender As Object, e As EventArgs)
  Description of when the event fires
```

---

## Step 5 Detail: manifest.yaml Template

**IMPORTANT - Version Format:**
Version MUST be 4-part format (MAJOR.MINOR.PATCH.BUILD) like `1.0.0.0`.
If the source version is 3-part (e.g., `1.0.0`), append `.0` to make it 4-part.

Create the `manifest.yaml` file for the marketplace registry:

```yaml
name: "{Name from .details}"
version: "{Version from .details - MUST be 4-part like 1.0.0.0}"
description: "{Description from .details}"
short_description: "{First sentence of description}"
prog_id: "{ProgID from .details}"
assembly_name: "{AssemblyName from .details}"
control_type: "{ControlType from .details}"
ui_library: "{UILibrary from .details}"

repository:
  url: "{GitHub URL from user input}"
  branch: "main"

author:
  name: "{Author name from user input}"
  github: "{GitHub username from user input}"

compatibility:
  dotnet: "net472"
  clarion:
    min_version: "11.0"
    tested_versions: ["11.0", "12.0"]

category: "{Category from user input}"
tags: [{Tags from user input as array}]

changelog:
  - version: "{Version}"
    date: "{Today's date YYYY-MM-DD}"
    changes: "Initial marketplace submission"
```

---

## Step 6 Detail: api-docs.json Template

Create the `api-docs.json` file with method/event/property documentation:

```json
{
  "methods": [
    {
      "name": "MethodName",
      "description": "Method description",
      "parameters": [
        {
          "name": "paramName",
          "type": "paramType",
          "description": "Parameter description"
        }
      ],
      "returns": {
        "type": "returnType",
        "description": "Return value description"
      }
    }
  ],
  "events": [
    {
      "name": "EventName",
      "description": "Event description",
      "parameters": [
        {
          "name": "sender",
          "type": "object"
        },
        {
          "name": "e",
          "type": "EventArgs"
        }
      ]
    }
  ],
  "properties": [
    {
      "name": "PropertyName",
      "type": "PropertyType",
      "description": "Property description",
      "readonly": false
    }
  ]
}
```

---

## Step 6.5 Detail: Validate manifest.yaml Before Submission

Before proceeding with the PR, validate the generated manifest.yaml against the marketplace requirements.

### Key Validations

1. **Version format**: Must be 4-part (MAJOR.MINOR.PATCH.BUILD like `1.0.0.0`)
   - Valid: `1.0.0.0`, `2.1.3.0`, `1.0.0.0-beta`
   - Invalid: `1.0.0`, `1.0`, `v1.0.0.0`
   - Pattern: `^\d+\.\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$`
   - **Auto-fix**: If version is 3-part, append `.0` (e.g., `1.0.0` → `1.0.0.0`)

2. **ProgID format**: Must be `Namespace.ClassName`
   - Pattern: `^[A-Za-z][A-Za-z0-9]*\.[A-Za-z][A-Za-z0-9]*$`

3. **Required fields**: name, version, description, short_description, prog_id, assembly_name, control_type, repository, author, compatibility, category

4. **Category**: Must be one of: `UI Controls`, `Data`, `Utility`, `Integration`, `WebView2`

5. **Changelog version**: Must also be 4-part format

### Validation Logic

```
IF version matches ^\d+\.\d+\.\d+$ (3-part):
  - Append ".0" to make it 4-part
  - Update both `version` field and `changelog[0].version`
  - Display: "Auto-fixed version: 1.0.0 → 1.0.0.0"

IF any required field is missing:
  - Display error and STOP

IF category not in allowed list:
  - Display error and STOP
```

### If Validation Fails

Display clear error message and STOP submission:

```
ERROR: Manifest validation failed

- version: "1.0.0" does not match 4-part format (auto-fixing to "1.0.0.0")
- category: "Other" is not valid (must be: UI Controls, Data, Utility, Integration, WebView2)

Fix the issues above and try again.
```

### If Validation Passes

Display confirmation and proceed:

```
✓ Manifest validation passed
  - Version: 1.0.0.0
  - ProgID: Namespace.ClassName
  - Category: UI Controls
```

---

## Step 7.0 Detail: Check and Validate Existing Manifest

**BEFORE creating or updating files**, check if `marketplace-submission/manifest.yaml` already exists:

```bash
test -f "PROJECT_PATH/marketplace-submission/manifest.yaml" && echo "EXISTS" || echo "NEW"
```

**If EXISTS: Read and IMMEDIATELY validate the version:**

1. Read the existing manifest.yaml
2. Check the `version:` field format:
   - **4-part (e.g., `1.0.2.0`)**: Valid - continue
   - **3-part (e.g., `1.0.2`)**: INVALID - must fix!

3. **If version is 3-part, FIX IT NOW:**
   ```
   WARNING: Version '1.0.2' is 3-part format.
   Fixing to '1.0.2.0' (appending .0)
   ```
   - Edit the manifest.yaml to change `version: "1.0.2"` to `version: "1.0.2.0"`
   - Also fix any changelog entries with 3-part versions

4. **Display validation result:**
   ```
   ✓ Manifest validation passed:
     - Version: 1.0.2.0 ✓
     - ProgID: LedClock.LedClockControl ✓
     - Category: UI Controls ✓
   ```

**DO NOT proceed to PR creation if version is still 3-part.**
