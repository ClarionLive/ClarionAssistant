# Command Syntax Rules and Prerequisite Checks

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

# Step 1 Detail: Check GitHub CLI and Authentication

**REMINDER: Do NOT use `powershell -Command`. Use ONLY the helper script commands shown below.**

Before proceeding, verify the GitHub CLI is installed and configured.

## 1a. Check if gh CLI is installed

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

## 1b. Check for GITHUB_TOKEN

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

## 1c. Check GitHub Authentication

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-user'
```

**Output**: GitHub username, `NOT_AUTHENTICATED`, or `NOT_INSTALLED`

If `NOT_AUTHENTICATED`, the user needs to run `gh auth login` first.

Store the returned username — it is needed later for the fork/clone steps (Step 8a).

## 1d. Get gh.exe Path (REQUIRED for later steps)

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-path'
```

**Output**: Full path like `C:\Program Files\GitHub CLI\gh.exe` or `NOT_INSTALLED`

**IMPORTANT:** Store this path! You will need it for all `gh` commands in Steps 7.6 and 8.

---

# Step 2 Detail: Validate Project Structure

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
