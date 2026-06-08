---
name: clarioncom-config
description: View and manage ClarionCOM configuration settings including Clarion installation path and default project folder
version: 1.0.0
---

## Path Resolution - CRITICAL

Use the helper script to get CLARIONCOM_HOME (avoids shell escaping issues):

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If NOT_INSTALLED**: Stop and tell user:
> ClarionCOM is not installed. Please run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder.

# ClarionCOM Configuration Manager

This skill helps you view and update ClarionCOM settings stored in `~/.clarioncom.env`.

## When to Use

- View current configuration settings
- Change the default Clarion installation path
- Change the default project folder
- Troubleshoot path-related deployment issues
- Check ClarionCOM version and installation status

## Workflow

### Step 1: Read and Display Current Settings

Read the configuration file directly:

```bash
powershell -ExecutionPolicy Bypass -Command "Get-Content ([Environment]::GetFolderPath('UserProfile') + '\.clarioncom.env')"
```

Parse and display the settings in a formatted way:

```
ClarionCOM Configuration
========================
Config file: C:\Users\{username}\.clarioncom.env

CLARION_PATH:           C:\Clarion12
CLARIONCOM_HOME:        C:\Users\{username}\AppData\Roaming\ClarionCOM
DEFAULT_PROJECT_FOLDER: H:\DevLaptop
CLARIONCOM_VERSION:     2.5.0
```

Also validate that CLARION_PATH exists:

```powershell
powershell -Command "if (Test-Path 'C:\Clarion12') { Write-Host '[OK] Path exists' -ForegroundColor Green } else { Write-Host '[WARNING] Path does not exist!' -ForegroundColor Red }"
```

### Step 2: Ask What to Do

Use AskUserQuestion to present options:

**Question**: "What would you like to do?"
**Header**: "Config"
**Options**:
1. **Done viewing** - "No changes needed"
2. **Change Clarion path** - "Update CLARION_PATH for deployments"
3. **Change project folder** - "Update DEFAULT_PROJECT_FOLDER"

### Step 3: Handle Selection

#### If "Change Clarion path":

**3.1 Ask for new path:**

Use AskUserQuestion:
- **Question**: "Select or enter your Clarion installation path:"
- **Header**: "Clarion Path"
- **Options**:
  1. **C:\Clarion12** - "Standard Clarion 12 installation"
  2. **C:\Clarion11** - "Standard Clarion 11 installation"
  3. **C:\Clarion12d** - "Clarion 12d installation"

User can also select "Other" to provide a custom path.

**3.2 Validate path exists:**

```powershell
powershell -Command "if (Test-Path '{NewPath}') { 'EXISTS' } else { 'NOT_FOUND' }"
```

**3.3 If NOT_FOUND:**
- Warn: "Path '{NewPath}' does not exist. Please verify the path is correct."
- Ask if they want to:
  - Try a different path
  - Save anyway (path might be created later)
  - Cancel

**3.4 If EXISTS (or user chose to save anyway), update the config:**

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion-write '{NewPath}'"
```

**3.5 Confirm the change:**

Read back the value to confirm:
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

Display: "Clarion path updated to: {NewPath}"

#### If "Change project folder":

**3.1 Ask for new folder:**

Use AskUserQuestion:
- **Question**: "Enter your default project folder path:"
- **Header**: "Project Folder"
- Let user provide custom path via "Other" option

**3.2 Validate folder exists:**

```powershell
powershell -Command "if (Test-Path '{NewFolder}') { 'EXISTS' } else { 'NOT_FOUND' }"
```

**3.3 If NOT_FOUND:**
- Warn and ask if they want to create it or try another path

**3.4 Update the config file:**

Read current content, update the line, and write back:

```powershell
powershell -ExecutionPolicy Bypass -Command "$envPath = [Environment]::GetFolderPath('UserProfile') + '\.clarioncom.env'; $content = Get-Content $envPath; $content = $content -replace '^DEFAULT_PROJECT_FOLDER=.*', 'DEFAULT_PROJECT_FOLDER={NewFolder}'; Set-Content $envPath $content"
```

**3.5 Confirm the change:**

Display: "Default project folder updated to: {NewFolder}"

### Step 4: Report Summary

After any changes, display the updated configuration:

```
Configuration Updated
=====================
CLARION_PATH:           C:\Clarion12 [OK - exists]
DEFAULT_PROJECT_FOLDER: H:\DevLaptop [OK - exists]

These settings will be used for future /clarioncom-build and /clarioncom-deploy operations.
```

## Quick Commands

For users who want to quickly check or set values without the interactive workflow:

**View current Clarion path:**
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

**Set Clarion path directly:**
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion-write 'C:\Clarion12'"
```

**View all settings:**
```bash
powershell -ExecutionPolicy Bypass -Command "Get-Content ([Environment]::GetFolderPath('UserProfile') + '\.clarioncom.env')"
```

## Error Handling

**If config file doesn't exist:**
- Report: "Configuration file not found at ~/.clarioncom.env"
- Suggest: "Run the ClarionCOM installer or create the file manually"

**If CLARIONCOM_HOME is not set:**
- Report: "ClarionCOM is not installed"
- Provide installation instructions

## Integration Notes

This skill is standalone and does not invoke other skills. It modifies the same configuration file used by:
- `/clarioncom-build` - Uses CLARION_PATH for file copying
- `/clarioncom-deploy` - Uses CLARION_PATH for file copying
- `/ClarionCOM` - Reads CLARION_PATH during initialization

Changes made here take effect immediately for subsequent skill invocations.
