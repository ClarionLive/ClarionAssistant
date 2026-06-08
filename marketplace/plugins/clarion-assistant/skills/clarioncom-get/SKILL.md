---
name: clarioncom-get
description: Download a ClarionCOM control from the marketplace to your local machine
version: 1.0.0
user-invocable: true
auto-apply:
  match-terms:
    - get control
    - download control
    - install control
    - marketplace download
    - get com control
changelog:
  - version: 1.0.0
    date: 2026-01-17
    changes:
      - Initial release with marketplace browsing and cloning
      - Search by control name with wildcard support
      - Automatic manifest parsing for control descriptions
---

# STOP - READ BEFORE DOING ANYTHING

## FORBIDDEN COMMANDS - THESE WILL FAIL:

```
powershell -Command "..."
powershell -Command "Get-Command gh..."
powershell -Command "$envFile = ..."
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

# Check GitHub authentication
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-user'
```

### For gh commands, use PowerShell with full path:

```bash
# Get gh path first, then use it in PowerShell commands
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' api repos/OWNER/REPO/contents/PATH"
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' repo clone OWNER/REPO"
```

---

# ClarionCOM Get Control Skill

Download a COM control from the ClarionCOM marketplace to your local machine.

## When to Use

Use this skill when:
- You want to download a COM control from the ClarionCOM marketplace
- You found a control on [clarionlive.com/com_for_clarion/marketplace](https://clarionlive.com/com_for_clarion/marketplace) and want to use it
- You need the source code for a control to build and use in your Clarion project

## Prerequisites

Before downloading:
1. GitHub CLI (`gh`) must be installed
2. User must be authenticated with GitHub (`gh auth login`)

---

## Step 1: Check Prerequisites

### 1a. Check if gh CLI is installed

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-check'
```

**Output**: `INSTALLED` or `NOT_INSTALLED`

If `NOT_INSTALLED`, ask the user: **"GitHub CLI is required to download controls. Install it now?"**

- If **yes**: Run `winget install GitHub.cli` and wait for completion
- If **no**: Display the following and stop:
  ```
  GitHub CLI can be installed from: https://cli.github.com/
  After installation, run 'gh auth login' to authenticate.
  ```

### 1b. Check GitHub Authentication

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-user'
```

**Output**: GitHub username, `NOT_AUTHENTICATED`, or `NOT_INSTALLED`

If `NOT_AUTHENTICATED`:
```
You need to authenticate with GitHub first.
Run: gh auth login
Then try again.
```

### 1c. Get gh.exe Path (REQUIRED for later steps)

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-path'
```

**Output**: Full path like `C:\Program Files\GitHub CLI\gh.exe` or `NOT_INSTALLED`

**IMPORTANT:** Store this path! You will need it for all `gh` commands in subsequent steps.

---

## Step 2: Prompt for Search Term

Use AskUserQuestion to get the control name to search for.

**Question:** "Enter the control name to search for (e.g., 'clock', 'document', 'editor') or '*' to list all available controls"

**Header:** "Control search"

**Options:** (This should be a text input, not options. Use the "Other" option mechanism.)

Store the search term for Step 3.

---

## Step 3: Fetch Available Controls

Use the GitHub API to list all controls in the marketplace registry.

```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' api repos/ClarionLive/com-marketplace/contents/controls --jq '.[].name'"
```

Replace `GH_PATH` with the full path from Step 1c.

**Output**: A list of control folder names, one per line (e.g., `DocumentViewerCOM`, `LedClock`, `TextEditorCOM`)

Filter the results by the user's search term (case-insensitive):
- If search term is `*` or `all`: Show all controls
- Otherwise: Filter to controls whose name contains the search term

---

## Step 4: Get Manifest Details for Matches

For each matching control, fetch its manifest.yaml to get the description and repository URL.

```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' api repos/ClarionLive/com-marketplace/contents/controls/CONTROL_NAME/manifest.yaml --jq '.content'"
```

This returns base64-encoded content. Decode it:

```bash
echo "BASE64_CONTENT" | base64 -d
```

From the decoded YAML, extract:
- `name` - Control display name
- `short_description` - Brief description to show user
- `repository.url` - GitHub URL to clone
- `category` - Control category

---

## Step 5: Present Matches and Select

### If NO matches found:

Display:
```
No controls found matching "SEARCH_TERM".

Check the marketplace for available controls:
https://clarionlive.com/com_for_clarion/marketplace

Or run /clarioncom-get again and enter '*' to list all available controls.
```

Stop here.

### If ONE match found:

Display the control details and ask for confirmation:

```
Found: CONTROL_NAME
Description: SHORT_DESCRIPTION
Category: CATEGORY
Repository: REPO_URL

Download this control?
```

Use AskUserQuestion with options:
- **Yes** - "Download this control to the current folder"
- **No** - "Cancel and search again"

### If MULTIPLE matches found:

Display all matches with numbers and use AskUserQuestion to let user select.

Format each option as:
- Label: `CONTROL_NAME`
- Description: `SHORT_DESCRIPTION (CATEGORY)`

Example:
```
Found 3 controls matching "clock":

1. LedClock - LED-style digital clock control (UI Controls)
2. AnalogClock - Analog clock face control (UI Controls)
3. WorldClock - Multi-timezone clock display (UI Controls)

Select a control to download:
```

---

## Step 6: Check for Existing Folder

Before cloning, check if a folder with the repository name already exists in the current directory.

Extract the repo name from the repository URL:
```bash
# From URL like https://github.com/owner/RepoName, extract "RepoName"
basename "REPO_URL"
```

Check if folder exists:
```bash
test -d "./REPO_NAME" && echo "EXISTS" || echo "NOT_EXISTS"
```

### If folder EXISTS:

Use AskUserQuestion:

**Question:** "A folder named 'REPO_NAME' already exists. What would you like to do?"

**Options:**
1. **Overwrite** - "Delete existing folder and download fresh copy"
2. **Skip** - "Cancel the download"
3. **Rename** - "Download to a different folder name"

**If Overwrite:**
```bash
rm -rf "./REPO_NAME"
```

**If Skip:**
Display: "Download cancelled." and stop.

**If Rename:**
Use AskUserQuestion to get a new folder name, then use that for cloning.

---

## Step 7: Clone the Control Repository

Clone the repository to the current directory.

```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' repo clone REPO_URL"
```

Or if user chose a custom folder name:
```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' repo clone REPO_URL CUSTOM_FOLDER_NAME"
```

Replace:
- `GH_PATH` with the full path from Step 1c
- `REPO_URL` with the repository URL from the manifest (e.g., `https://github.com/peterparker57/LedClockCOM`)
- `CUSTOM_FOLDER_NAME` with the user's chosen name (if applicable)

---

## Step 8: Report Success

After successful cloning, display:

```
============================================================
  Control Downloaded Successfully!
============================================================

  Control: CONTROL_NAME
  Location: ./FOLDER_NAME

  Next steps:
  1. cd FOLDER_NAME
  2. Run /clarioncom-build to build the control
  3. Copy the Clarion/ folder contents to your Clarion accessory folder

============================================================
```

---

## Error Handling

### GitHub CLI Not Installed

```
GitHub CLI is required to download controls.

Install with: winget install GitHub.cli
Or download from: https://cli.github.com/

After installation, run: gh auth login
```

### Not Authenticated

```
You need to authenticate with GitHub first.
Run: gh auth login
Then try /clarioncom-get again.
```

### Control Not Found in Marketplace

```
No controls found matching "SEARCH_TERM".

Available controls can be browsed at:
https://clarionlive.com/com_for_clarion/marketplace

Run /clarioncom-get with '*' to list all available controls.
```

### Clone Failed

```
Failed to clone the repository.

Possible causes:
- Network connection issues
- Repository may have been deleted
- Insufficient permissions

Try again or clone manually:
git clone REPO_URL
```

### Folder Already Exists (and user chose not to overwrite)

```
Download cancelled - folder "FOLDER_NAME" already exists.

Options:
- Delete or rename the existing folder and try again
- Run /clarioncom-get and choose "Rename" to use a different folder name
```

---

## Integration Notes

This skill:
1. Does NOT require any local project to exist
2. Can be run from any folder where you want to download controls
3. Clones the full source repository, not just built artifacts
4. Requires user to run `/clarioncom-build` after downloading to build the control

## Related Skills

- `clarioncom-build` - Build the downloaded control
- `clarioncom-validate` - Validate control compliance
- `clarioncom-marketplace-submit` - Submit your own control to the marketplace
