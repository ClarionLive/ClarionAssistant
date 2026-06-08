---
name: ClarionCOM
description: Interactive COM development assistant - guides you through creating, building, validating, or deploying Clarion COM components
---

## Installation Check - MUST DO FIRST

Before proceeding with any task, verify ClarionCOM is installed using the helper script:

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If output is `NOT_INSTALLED`:**
Stop immediately and inform the user:

> ⚠️ **ClarionCOM is not installed.**
>
> To install ClarionCOM:
> 1. Download the ClarionCOM distribution ZIP
> 2. Extract to any folder
> 3. Open PowerShell in that folder
> 4. Run: `.\Install-ClarionCOM.ps1`
>
> After installation, run `/ClarionCOM` again.

**If installed:** Store the CLARIONCOM_HOME path for use in subsequent script references. All scripts are located at `$CLARIONCOM_HOME\scripts\`.

---

## Environment Note

**This runs in Git Bash on Windows.** Use the helper script for Clarion path management to avoid escaping issues.

---

# Clarion COM Development Assistant

You are helping the user with Clarion COM development. Follow this workflow:

## Step 0: Check Clarion Path Configuration

Before proceeding with any task, check if the Clarion installation path is configured AND exists.

**Use the helper script** located at `$CLARIONCOM_HOME\scripts\clarioncom-env.ps1` (where CLARIONCOM_HOME was resolved in the installation check):

```bash
# Read existing path (outputs path or 'NOT_CONFIGURED')
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"

# Save new path (outputs 'OK' on success)
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion-write 'C:\Clarion12'"
```

**Note:** Use the CLARIONCOM_HOME path resolved from the installation check for all script references.

**Workflow:**

**0.1 Read current path:**
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

**0.2 If output is `NOT_CONFIGURED`:**

Use AskUserQuestion to prompt the user:
- **Question**: "Clarion path is not configured. What is your Clarion installation path?"
- **Header**: "Clarion Path"
- **Options**:
  1. **C:\Clarion12** - "Standard Clarion 12 installation"
  2. **C:\Clarion11** - "Standard Clarion 11 installation"
- User can also select "Other" to provide a custom path

Continue to step 0.3 to validate before saving.

**0.3 Validate path exists:**

```powershell
powershell -Command "if (Test-Path '{ClarionPath}') { 'EXISTS' } else { 'NOT_FOUND' }"
```

**0.4 If NOT_FOUND:**

Warn the user:
- "The path '{ClarionPath}' does not exist on this system."

Use AskUserQuestion:
- **Question**: "The configured Clarion path does not exist. What would you like to do?"
- **Header**: "Path Issue"
- **Options**:
  1. **Enter different path** - "Provide a different Clarion installation path"
  2. **Continue anyway** - "Path might be on a different drive or created later"
  3. **Use /clarioncom-config** - "Open configuration manager to fix settings"

If user chooses to enter a different path, repeat from step 0.2.

**0.5 Save the path (if new or changed):**

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion-write '{ClarionPath}'"
```

**0.6 Confirm:**

If path was configured or changed:
- "Using Clarion path: {ClarionPath}"

If path already existed and was valid:
- Display briefly or skip confirmation to avoid unnecessary prompts

## Step 0.5: Control Type Selection

Use the AskUserQuestion tool to determine the type of COM control:

**Question**: "What type of COM control are you working with?"
**Header**: "Control Type"
**Options**:
1. **Standard C# COM** - "Windows Forms-based control using .NET Framework"
2. **WebView2 COM** - "Browser-based control using HTML/CSS/JavaScript with WebView2"

Store the selection for use in Step 3 skill routing.

## Step 1: Determine the Task

Use the AskUserQuestion tool to present these options:

**Question**: "What would you like to do with Clarion COM?"
**Header**: "Task"
**Options**:
1. **Create new COM control** - "Start a new C# COM control project from scratch"
2. **Build existing project** - "Compile an existing COM project using MSBuild"
3. **Validate COM control** - "Check an existing control for RegFree COM compliance"
4. **More options...** - "Deploy, GitHub, Marketplace, and other tools"

### If "More options..." selected:

Use AskUserQuestion again to present additional options:

**Question**: "What would you like to do?"
**Header**: "More Tasks"
**Options**:
1. **Deploy COM component** - "Generate deployment artifacts (batch files, documentation)"
2. **Initialize GitHub Repo** - "Create a GitHub repository for an existing project"
3. **Submit to Marketplace** - "Share your control with the Clarion community on clarionlive.com"

## Step 2: Gather Project Details

Based on the user's selection, ask follow-up questions:

### If "Create new COM control":
Ask these questions (can be combined into one AskUserQuestion call or asked conversationally):
- **Control name**: What should the control be named? **Default suggestion should be the current folder name** (e.g., if in folder "WebCalendarCOM", suggest "WebCalendarCOM")
- **Control type**: UserControl (visual) or Component (non-visual)?
- **Control library**: Which UI control library will you use? (AskUserQuestion with these options)
  - **Standard WinForms** - "Built-in Windows Forms controls (System.Windows.Forms) - no additional packages needed"
  - **DevExpress WinForms** - "DevExpress UI suite for rich controls (requires license)"
  - **Telerik WinForms** - "Telerik UI for WinForms (requires license)"
  - **Syncfusion WinForms** - "Syncfusion Essential Studio (free community license available)"
  - **Infragistics WinForms** - "Infragistics Windows Forms controls (requires license)"
- **Description** (REQUIRED): Ask the user to describe what they want the control to do. This is critical - do NOT assume functionality based on the name alone. Ask: "Please describe what you want this control to do - what features, behaviors, or functionality should it have?"
- **API Style** (AskUserQuestion with these options):
  - **Getter/Setter Methods (Recommended)** - "Use explicit GetValue() and SetValue() methods - better Clarion IDE tooling support"
  - **Properties** - "Use C# property syntax Value { get; set; } - more idiomatic C#"

**Namespace**: Auto-derived from control name. Do NOT ask the user for this.
- Example: Control name "DigitalClock" → Namespace "DigitalClock" → ProgID "DigitalClock.DigitalClockControl"

**IMPORTANT**: Do NOT proceed to implementation until the user has provided a description of what they want. The control name alone is not enough information.

#### Template Files Structure

When creating a new COM control, reference files are located in the `Template/` subfolder:
- **Template/MinimalControl.cs** - Example control implementation
- **Template/IMinimalControl.cs** - Example COM interface
- **Template/ClarionCOMTemplate.csproj** - Example project configuration
- **Template/MinimalControl.manifest** - Example manifest file
- **Template/Properties/AssemblyInfo.cs** - Example assembly info

**Workflow:**
1. Skills will **READ** from `Template/` folder to understand the structure and patterns
2. Skills will **CREATE** new files in the project root with the user's control name
3. Template files in `Template/` folder remain unchanged as reference examples
4. New project files use the control name (e.g., `ToggleButton.cs`, `IToggleButton.cs`, `ToggleButton.csproj`)

**Note:** The `Template/` folder contains reference files only. When creating a new control, these files are used as templates but are not modified.

### If "Build existing project":
- **Project path**: Ask for the path to the .csproj file, or offer to detect it from the current directory

### Version Management (Before Build)

Before building, check and manage the version using the ClarionCOM scripts:

1. **Check version status:**
   ```bash
   powershell -ExecutionPolicy Bypass -File "$env:APPDATA\ClarionCOM\scripts\increment-build-version.ps1" read "ProjectPath"
   ```

2. **If output is `NOT_CONFIGURED`:**
   - Use AskUserQuestion to prompt:
     - **Question**: "This project doesn't have version tracking configured. Please select the initial version."
     - **Header**: "Initialize Version"
     - **Options**:
       - Major version: 1, 2, or 3
       - Minor version: 0, 1, or 2

3. **Initialize version with user's choices:**
   ```bash
   powershell -ExecutionPolicy Bypass -File "$env:APPDATA\ClarionCOM\scripts\increment-build-version.ps1" init "ProjectPath" "MajorVersion" "MinorVersion"
   ```

4. **Increment the build number:**
   ```bash
   powershell -ExecutionPolicy Bypass -File "$env:APPDATA\ClarionCOM\scripts\increment-build-version.ps1" increment "ProjectPath"
   ```

5. **Report the new version:**
   - Display output like "Building version 1.0.5"
   - Continue to MSBuild step

### If "Validate COM control":
- **Project path**: Ask for the path to the project or control files
- **Scope**: Validate everything or focus on specific areas?

### If "Deploy COM component":
- **Project path**: Path to the built COM project
- **Target directory**: Where should deployment artifacts be generated?

### If "Initialize GitHub Repo":
- **Skip Step 0.5 (Control Type)**: GitHub initialization works with any control type
- **Project path**: Ask for the path to the project, or offer to detect it from the current directory
- The skill will check for existing git setup and guide through repository creation

### If "Submit to Marketplace":
- **Skip Step 0.5 (Control Type)**: The marketplace submission skill works with any control type
- The skill will validate the project structure and gather required information
- User will be asked for GitHub repository URL, author name, and category

## Step 3: Invoke the Appropriate Skill

After gathering the required information, invoke the matching skill based on the control type selected in Step 0.5:

### Standard C# COM Controls

| Task | Skill to Invoke |
|------|-----------------|
| Create new COM control | `clarioncom-create` |
| Build existing project | `clarioncom-build` |
| Validate COM control | `clarioncom-validate` |
| Deploy COM component | `clarioncom-deploy` |

### WebView2 COM Controls

| Task | Skill to Invoke |
|------|-----------------|
| Create new COM control | `clarioncom-webview2-create` |
| Build existing project | `clarioncom-webview2-build` |
| Validate COM control | `clarioncom-webview2-validate` |
| Deploy COM component | `clarioncom-webview2-deploy` |

### GitHub and Marketplace (Any Control Type)

| Task | Skill to Invoke |
|------|-----------------|
| Initialize GitHub Repo | `clarioncom-github-init` |
| Submit to Marketplace | `clarioncom-marketplace-submit` |

**Note:** GitHub initialization and marketplace submission skills work with both Standard and WebView2 controls. Skip the control type selection in Step 0.5 when the user chooses either of these options.

Use the Skill tool to invoke the skill, then provide the gathered context to guide the skill's execution.

## Important Notes

- All COM controls use **Registration-Free COM** (manifest-based, no registry)
- The `clarioncom-create` skill has critical patterns that MUST be followed
- Never suggest running RegAsm or Register.bat - these are NOT needed
- The OnHandleCreated pattern is critical for controls with child components
