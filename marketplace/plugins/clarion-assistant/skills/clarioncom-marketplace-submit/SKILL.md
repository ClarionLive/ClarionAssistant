---
name: clarioncom-marketplace-submit
description: Submit a ClarionCOM control to the COM Marketplace for discovery by the Clarion community
version: 1.7.0
changelog:
  - version: 1.7.0
    date: 2026-01-17
    changes:
      - Added fork cleanup step to remove old control folders before submission
      - Added validation feedback - waits for workflow and reports pass/fail
      - Prevents conflicts from previous submission attempts
  - version: 1.6.0
    date: 2026-01-17
    changes:
      - Added local manifest validation before PR submission
      - Auto-fixes 3-part versions to 4-part format (1.0.0 → 1.0.0.0)
      - Validates required fields, ProgID format, and category
      - Prevents failed PRs by catching validation errors early
  - version: 1.5.0
    date: 2026-01-16
    changes:
      - Include Clarion deployment files in marketplace submissions
      - Submissions now include files/ subfolder with DLL, manifest, and documentation
      - Enables direct download from marketplace and documentation display on website
  - version: 1.4.0
    date: 2026-01-13
    changes:
      - Fixed gh CLI path resolution using clarioncom-env.ps1 helper script
      - Fixed Bash variable escaping issues (same pattern as github-init skill)
  - version: 1.3.0
    date: 2026-01-13
    changes:
      - Added repository sync check before submission (uncommitted/unpushed changes)
      - Added repository visibility check (prompts to make private repos public)
  - version: 1.2.0
    date: 2026-01-11
    changes:
      - Added support for updating existing controls (not just new submissions)
      - Detection of whether control already exists in registry
      - Different branch naming and PR templates for new vs update submissions
      - Automatic CHANGELOG.md parsing to populate PR description for updates
      - Version-aware branch naming for updates (e.g., update-calendar-picker-v1.1.0)
  - version: 1.1.0
    date: 2025-12-01
    changes:
      - Added automated GitHub PR submission
      - Token-based authentication via .clarioncom.env
  - version: 1.0.0
    date: 2025-11-15
    changes:
      - Initial release with manifest.yaml and api-docs.json generation
---

# STOP - READ BEFORE DOING ANYTHING

## FORBIDDEN COMMANDS - THESE WILL FAIL:

```
powershell -Command "..."
powershell -Command "Get-Command gh..."
powershell -Command "$envFile = ..."
powershell -Command "if (Test-Path..."
where gh
```

**ANY command using `powershell -Command` with variables WILL FAIL.** The `$` characters get stripped by Bash.

---

## ALLOWED COMMANDS - USE ONLY THESE:

### For prerequisite checks, use the helper script:

```bash
# Check gh CLI installed
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-check'

# Get full path to gh.exe (use this for all gh commands!)
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-path'

# Check GitHub token configured
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") github-token'

# Check GitHub authentication
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-user'
```

### For gh commands, use PowerShell with full path:

**IMPORTANT:** After getting the gh path from `gh-path`, use PowerShell to run gh commands:

```bash
# Get gh path first, then use it in PowerShell commands
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' repo view OWNER/REPO --json visibility"
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' repo edit OWNER/REPO --visibility public"
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' repo fork OWNER/REPO --clone=false"
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' pr create --repo OWNER/REPO --title 'Title' --body 'Body'"
```

**DO NOT USE bare `gh` commands** - they will fail to find the executable.

---

# CRITICAL VALIDATION GATE - READ BEFORE PROCEEDING

## STOP - Before Creating ANY Pull Request:

You MUST validate the manifest.yaml version format. This is MANDATORY.

### Version Validation Rules:

1. **Version field MUST be 4-part** (e.g., `1.0.0.0`)
   - Valid: `1.0.0.0`, `2.1.3.0`, `1.0.0.0-beta`
   - Invalid: `1.0.0`, `1.0`, `v1.0.0.0`
   - Pattern: `^\d+\.\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$`

2. **If version is 3-part, AUTO-FIX IT:**
   - `1.0.0` → `1.0.0.0` (append `.0`)
   - Update BOTH the `version:` field AND `changelog[0].version:`

3. **Changelog version must also be 4-part**

### Validation Checkpoint:

After generating manifest.yaml (Step 5), IMMEDIATELY check:
```
Is version 4-part? (e.g., 1.0.0.0)
  YES → Proceed to Step 6
  NO  → Auto-fix by appending .0, then proceed
```

**DO NOT create a PR with 3-part version. The PR will fail validation.**

---

# ClarionCOM Marketplace Submission Skill

This skill generates the files needed to submit your COM control to the ClarionCOM Marketplace registry and automatically creates a GitHub Pull Request.

## When to Use

Use this skill when:
- You have a built COM control ready to share with the Clarion community
- You want to make your control discoverable on [clarionlive.com/com_for_clarion/marketplace](https://clarionlive.com/com_for_clarion/marketplace)
- Your control has a public GitHub repository

## Prerequisites

Before submission, ensure your project has:
1. A **Clarion/accessory/** folder with deployment artifacts:
   - `accessory/bin/*.dll` - Compiled control and dependencies
   - `accessory/resources/*.manifest` - Registration-free COM manifest
   - `*.details` - Control metadata file
   - `*.methods` - Method definitions
   - `*.events` - Event definitions (if applicable)
2. A **public GitHub repository** with the source code
3. A successful build (run `/ClarionCOM` -> "Build" first if needed)

## Step 1: Check GitHub CLI and Authentication

**REMINDER: Do NOT use `powershell -Command`. Use ONLY the helper script commands shown below.**

Before proceeding, verify the GitHub CLI is installed and configured.

### 1a. Check if gh CLI is installed

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-check'
```

**Output**: `INSTALLED` or `NOT_INSTALLED`

If `NOT_INSTALLED`, ask the user: **"GitHub CLI is required for automatic submission. Install it now?"**

- If **yes**: Run `winget install GitHub.cli` and wait for completion
- If **no**: Display the following and stop:
  ```
  For manual setup instructions, visit: https://clarionlive.com/com_for_clarion/marketplace/setup
  ```

### 1b. Check for GITHUB_TOKEN

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") github-token'
```

**Output**: The token value or `NOT_CONFIGURED`

If `NOT_CONFIGURED`, display the following and stop:
```
GitHub token required for automatic submission.
Please visit https://clarionlive.com/com_for_clarion/marketplace/setup for setup instructions.
```

### 1c. Check GitHub Authentication

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-user'
```

**Output**: GitHub username, `NOT_AUTHENTICATED`, or `NOT_INSTALLED`

If `NOT_AUTHENTICATED`, the user needs to run `gh auth login` first.

### 1d. Get gh.exe Path (REQUIRED for later steps)

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-path'
```

**Output**: Full path like `C:\Program Files\GitHub CLI\gh.exe` or `NOT_INSTALLED`

**IMPORTANT:** Store this path! You will need it for all `gh` commands in Steps 7.6 and 8.

## Step 2: Validate Project Structure

Verify the project has all required files.

```powershell
# Check for Clarion folder
$clarionFolder = Get-ChildItem -Path "." -Directory -Filter "Clarion" | Select-Object -First 1
if (-not $clarionFolder) {
    Write-Error "No Clarion/ folder found. Build your project first with /ClarionCOM -> Build"
    exit 1
}

# Check for required files
$requiredExtensions = @("*.dll", "*.manifest", "*.details", "*.methods")
foreach ($ext in $requiredExtensions) {
    $file = Get-ChildItem -Path $clarionFolder.FullName -Filter $ext | Select-Object -First 1
    if (-not $file) {
        Write-Warning "Missing $ext file in Clarion/ folder"
    } else {
        Write-Host "Found: $($file.Name)"
    }
}
```

## Step 3: Gather Submission Information

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

## Step 4: Extract Metadata from Project Files

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

## Step 5: Generate manifest.yaml

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

## Step 6: Generate api-docs.json

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

## Step 6.5: Validate manifest.yaml Before Submission

Before proceeding with the PR, validate the generated manifest.yaml against the marketplace requirements.

### Key Validations

1. **Version format**: Must be 4-part (MAJOR.MINOR.PATCH.BUILD like `1.0.0.0`)
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

## Step 7: Create Submission Files

### 7.0 CRITICAL: Check and Validate Existing Manifest

**BEFORE creating or updating files**, check if `marketplace-submission/manifest.yaml` already exists:

```bash
test -f "PROJECT_PATH/marketplace-submission/manifest.yaml" && echo "EXISTS" || echo "NEW"
```

**If EXISTS: Read and IMMEDIATELY validate the version:**

1. Read the existing manifest.yaml
2. Check the `version:` field format:
   - **4-part (e.g., `1.0.2.0`)**: ✓ Valid - continue
   - **3-part (e.g., `1.0.2`)**: ⚠️ INVALID - must fix!

3. **If version is 3-part, FIX IT NOW:**
   ```
   ⚠️ Version '1.0.2' is 3-part format.
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

---

Save the generated files to a `marketplace-submission/` folder in the project.

### 7a. Create the manifest and API docs

```
marketplace-submission/
├── manifest.yaml
└── api-docs.json
```

### 7b. Copy Clarion deployment files

Copy files from `Clarion/accessory/` to `marketplace-submission/files/` (flat structure for marketplace):

```bash
mkdir -p "PROJECT_PATH/marketplace-submission/files"

# Copy DLLs from bin folder
cp "PROJECT_PATH/Clarion/accessory/bin/"*.dll "PROJECT_PATH/marketplace-submission/files/"

# Copy resources (manifest, header, details, methods, events, html)
cp "PROJECT_PATH/Clarion/accessory/resources/"* "PROJECT_PATH/marketplace-submission/files/"
```

This includes the complete deployment package:
- `*.dll` - Compiled control and dependencies
- `*.manifest` - Registration-free COM manifest
- `*.header` - Assembly info and ClarionPath
- `*.details` - Control metadata
- `*.methods` - Method documentation (for marketplace display)
- `*.events` - Event documentation (for marketplace display)
- `*.html` - Readme documentation
- `*.bat` - Test scripts (CheckDotNetVersion, TestCOM, TestManifests)
- Any additional data files (.db, .sqlite, .json, .ini, etc.)

Final structure:
```
marketplace-submission/
├── manifest.yaml
├── api-docs.json
└── files/
    ├── ControlName.dll
    ├── ControlName.manifest
    ├── ControlName.header
    ├── {ProgID}.details
    ├── {ProgID}.methods
    ├── {ProgID}.events
    ├── readme_ControlName.html
    ├── CheckDotNetVersion.bat
    ├── TestCOM.bat
    ├── TestManifests.bat
    └── (any additional dependencies)
```

## Step 7.5: Check Repository Sync Status

Before submitting, ensure all local changes are pushed to the remote repository.

### Check for uncommitted changes

```bash
git -C "PROJECT_PATH" status --porcelain
```

### Check for unpushed commits

```bash
git -C "PROJECT_PATH" log origin/main..HEAD --oneline 2>/dev/null || echo "NO_REMOTE"
```

If there are uncommitted or unpushed changes, use AskUserQuestion:
- **Question**: "Your local repository has uncommitted or unpushed changes. Push all changes before submitting?"
- **Options**:
  1. "Push Changes" - "Commit and push all local changes"
  2. "Cancel Submission" - "Stop and review changes first"

If **"Push Changes"**:
```bash
cd "PROJECT_PATH" && git add . && git commit -m "Pre-submission sync" && git push
```

If **"Cancel Submission"**:
Display: "Submission cancelled. Please review your changes and run again when ready." and exit skill.

## Step 7.6: Check Repository Visibility

Marketplace submissions require a public repository so users can access the source code.

**IMPORTANT:** Use the full gh path obtained in Step 1d (e.g., `C:\Program Files\GitHub CLI\gh.exe`).

### Extract repo name from GitHub URL

The GitHub URL format is `https://github.com/owner/repo`. Extract `owner/repo` portion.

### Check repository visibility

```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' repo view OWNER/REPO --json visibility"
```

Replace `GH_PATH` with the full path from Step 1d and `OWNER/REPO` with the actual repo.

**Output**: JSON like `{"visibility":"PRIVATE"}` or `{"visibility":"PUBLIC"}`

### If repository is PRIVATE

Use AskUserQuestion:
- **Question**: "This repository is private. Marketplace submissions require a public repository. Would you like to make it public?"
- **Options**:
  1. "Make Public" - "Change repository visibility to public"
  2. "Cancel Submission" - "Keep private and cancel submission"

If **"Make Public"**:
```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' repo edit OWNER/REPO --visibility public"
```

If **"Cancel Submission"**:
Display: "Submission cancelled. Your repository remains private." and exit skill.

## Step 8: Automated GitHub Pull Request Submission

After generating the submission files, automatically create a Pull Request to the COM Marketplace registry.

**IMPORTANT:** Use the full gh path obtained in Step 1d (e.g., `C:\Program Files\GitHub CLI\gh.exe`) for ALL gh commands.

### 8a. Get GitHub Username

The username was already retrieved in Step 1c using:
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-user'
```

Store the returned username for use in subsequent steps.

### 8b. Fork the Registry

Fork the ClarionLive/com-marketplace repository (silently succeeds if already forked):

```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' repo fork ClarionLive/com-marketplace --clone=false 2>&1 | Out-Null"
```

### 8c. Clone the Fork

Clone the user's fork to a temp directory:

```bash
# Remove existing temp dir if present
rm -rf /tmp/com-marketplace-submission

# Clone using full gh path
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' repo clone USERNAME/com-marketplace /tmp/com-marketplace-submission"
```

Replace `USERNAME` with the GitHub username from Step 8a.

### 8c.1 CRITICAL: Clean Existing Control Folders

After cloning, check for and remove any existing folders that might conflict with this submission:

```bash
# List any existing control folders that match this control name pattern
ls /tmp/com-marketplace-submission/controls/ 2>/dev/null | grep -i "CONTROL_NAME_BASE"
```

Where `CONTROL_NAME_BASE` is the base name of your control (e.g., for `LedClockCOM`, check for `LedClock`).

**If any matching folders exist:**

1. Display to user:
   ```
   ⚠️ Found existing folder(s) in fork: [folder names]
   Removing to ensure clean submission...
   ```

2. Remove them:
   ```bash
   rm -rf /tmp/com-marketplace-submission/controls/MATCHING_FOLDER
   ```

3. This ensures only ONE version of the control exists in the PR.

**Why this matters:** Old folders from previous submission attempts cause validation to fail because they may have outdated manifests (e.g., 3-part versions).

### 8d. Detect New vs Update Submission

Check if the control folder already exists in the registry:

```bash
test -d "/tmp/com-marketplace-submission/controls/CONTROL_NAME" && echo "UPDATE" || echo "NEW"
```

Replace `CONTROL_NAME` with the actual control name.

### 8e. Read CHANGELOG.md for Updates (if update submission)

For update submissions, check if `PROJECT_PATH/CHANGELOG.md` exists and use the Read tool to extract the latest version entry.

If no CHANGELOG.md exists for an update submission, use AskUserQuestion:
- **Question**: "What changed in version VERSION? (This will be included in the PR description)"

### 8f. Create Branch and Copy Files

```bash
# Navigate to cloned repo and create branch
cd /tmp/com-marketplace-submission

# For NEW submissions:
git checkout -b add-CONTROL_NAME_LOWERCASE

# For UPDATE submissions:
git checkout -b update-CONTROL_NAME_LOWERCASE-vVERSION

# Create control folder with files subfolder
mkdir -p controls/CONTROL_NAME/files

# Copy marketplace-submission files (recursive to include files/ subfolder)
cp -r PROJECT_PATH/marketplace-submission/* controls/CONTROL_NAME/
```

### 8g. Commit and Push

```bash
cd /tmp/com-marketplace-submission && git add . && git commit -m "COMMIT_MESSAGE" && git push -u origin BRANCH_NAME
```

**Commit message templates:**
- **NEW**: `Add CONTROL_NAME to COM Marketplace`
- **UPDATE**: `Update CONTROL_NAME to vVERSION`

### 8h. Create Pull Request

**For NEW control submissions:**

```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' pr create --repo ClarionLive/com-marketplace --title 'Add CONTROL_NAME' --body 'PR_BODY'"
```

Where `PR_BODY` is:
```
## New Control Submission

**Control Name:** CONTROL_NAME
**Author:** AUTHOR_NAME (@GITHUB_USERNAME)
**Category:** CATEGORY

### Description
DESCRIPTION

### Checklist
- [x] manifest.yaml included
- [x] api-docs.json included
- [x] Clarion deployment files included (DLL, manifest, documentation)
- [x] Public GitHub repository
- [x] Control builds successfully

---
*Submitted via ClarionCOM Marketplace Submission Skill*
```

**For UPDATE submissions:**

```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' pr create --repo ClarionLive/com-marketplace --title 'Update CONTROL_NAME to vVERSION' --body 'PR_BODY'"
```

Where `PR_BODY` is:
```
## Control Update

**Control:** CONTROL_NAME
**New Version:** VERSION
**Author:** AUTHOR_NAME (@GITHUB_USERNAME)

### What's Changed
CHANGELOG_CONTENT

### Checklist
- [x] manifest.yaml updated
- [x] api-docs.json updated
- [x] Clarion deployment files updated (DLL, manifest, documentation)
- [x] Version incremented
- [x] Control builds successfully

---
*Submitted via ClarionCOM Marketplace Submission Skill*
```

**NOTE:** The gh pr create command returns the PR URL. Capture this for the success output.

## Step 8.5: Wait for Validation and Report Result

After creating the PR, wait for the validation workflow to complete and report the result.

### 1. Wait for workflow to start (30 seconds)

```bash
sleep 30
```

### 2. Check workflow status

```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' run list --repo ClarionLive/com-marketplace --branch BRANCH_NAME --limit 1 --json status,conclusion,name"
```

### 3. If workflow is still running, wait and check again

Poll every 15 seconds for up to 2 minutes total.

### 4. Report result to user

**If validation PASSED (conclusion: success):**
```
============================================================
  ✓ PR VALIDATION PASSED!
============================================================

  Your control passed all validation checks.
  It will be auto-merged shortly.

  PR URL: {$prUrl}
============================================================
```

**If validation FAILED (conclusion: failure):**
```
============================================================
  ✗ PR VALIDATION FAILED
============================================================

  The validation workflow found issues with your submission.

  To see the error details, run:
  gh run view RUN_ID --repo ClarionLive/com-marketplace --log-failed

  Common issues:
  - Version must be 4-part format (e.g., 1.0.0.0)
  - Missing required fields in manifest.yaml
  - Invalid ProgID format

  PR URL: {$prUrl} (validation failed - needs fixes)
============================================================
```

**If workflow didn't complete in time:**
```
  ⏳ Validation still running...
  Check status at: {$prUrl}
```

## Step 9: Success Output

After successful submission, display:

```
============================================================
  SUCCESS: Pull Request Created!
============================================================

  PR URL: {$prUrl}

  Your control has been submitted!
  A maintainer will review it shortly.

  Once approved, it will appear on:
  https://clarionlive.com/com_for_clarion/marketplace

============================================================
```

The PR URL should be displayed as a clickable link.

## Error Handling

### GitHub CLI Not Installed
```
GitHub CLI is required for automatic submission.
Install it now? [Y/N]

If yes: winget install GitHub.cli
If no: Visit https://clarionlive.com/com_for_clarion/marketplace/setup for manual instructions.
```

### Missing GITHUB_TOKEN
```
Error: No GITHUB_TOKEN found in ~/.clarioncom.env
Solution: Visit https://clarionlive.com/com_for_clarion/marketplace/setup for setup instructions.
```

### Missing Clarion/ Folder
```
Error: No Clarion/accessory/ folder found.
Solution: Build your project first using /ClarionCOM -> "Build existing project"
```

### Missing Required Files
```
Warning: Missing {file} in Clarion/accessory/ folder.
Solution: Ensure your project was built successfully with all deployment artifacts.
```

### Invalid GitHub URL
```
Error: Invalid GitHub repository URL.
Solution: Provide a URL in format: https://github.com/username/repository
```

### Private Repository
```
Warning: Repository appears to be private or inaccessible.
Solution: Make the repository public before submission, or users won't be able to clone it.
```

### Fork/Clone Failure
```
Error: Failed to fork or clone com-marketplace repository.
Solution: Check your GitHub token permissions and internet connection.
```

### PR Creation Failure
```
Error: Failed to create Pull Request.
Solution: Ensure you have push access to your fork and try again.
```

## Integration Notes

This skill is invoked from the main `/ClarionCOM` command when the user selects "Submit to Marketplace" option.

The skill:
1. Does NOT require building the project (assumes already built)
2. Does NOT modify any source code
3. Creates new files in a `marketplace-submission/` folder
4. Automatically creates a GitHub Pull Request to ClarionLive/com-marketplace
5. Requires GitHub CLI (`gh`) and a valid GITHUB_TOKEN in `~/.clarioncom.env`

## Related Skills

- `clarioncom-build` - Build the control before submission
- `clarioncom-deploy` - Generate deployment artifacts
- `clarioncom-validate` - Validate control compliance
