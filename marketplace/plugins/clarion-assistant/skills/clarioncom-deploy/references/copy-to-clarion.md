# Completion Report and Copy to Clarion (Steps 7, 8.0, 8 Detail)

## Step 7 Detail: Report Completion

Display summary:
```
Deployment artifacts created in: ProjectName/Clarion/accessory/

To accessory/bin:
  - ProjectName.dll
  - [dependency DLLs if any]

To accessory/resources:
  - ProjectName.manifest
  - ProjectName.header
  - ProgID.details
  - ProgID.events
  - ProgID.methods
  - readme_ProjectName.html
  - CheckDotNetVersion.bat  (project folder only)
  - TestManifests.bat       (project folder only)

Key Information:
- ProgId: {ProgId}
- CLSID: {ClassGuid}
- TypeLib: {TypeLibGuid}
- Methods: {MethodCount}
- Events: {EventCount}
- DLLs to deploy: {DllCount} file(s) - listed in [DllsToCopy] section of .header

REGISTRATION-FREE COM DEPLOYMENT:
All files in ProjectName/Clarion/accessory/ are for registration-free deployment.
NO registration of the DLL should be performed.

To deploy to Clarion:
1. Drag & drop the entire accessory folder to your Clarion installation
   OR copy files manually:
   - DLLs from accessory/bin/ -> C:\Clarion12\accessory\bin\
   - Resources from accessory/resources/ -> C:\Clarion12\accessory\resources\
2. Use ProgId '{ProgId}' to create COM object
3. See readme_ProjectName.html for complete integration instructions
4. NEVER register the DLL - registration-free COM only
```

## Step 8.0 Detail: Verify Clarion Path (BEFORE COPYING)

Before copying files, verify the Clarion installation path exists and confirm with the user.

**8.0.1 Read current Clarion path:**

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

**8.0.2 Validate the path exists:**

```powershell
powershell -Command "if (Test-Path '{ClarionPath}') { 'EXISTS' } else { 'NOT_FOUND' }"
```

**8.0.3 If NOT_FOUND or NOT_CONFIGURED:**

Warn the user and prompt for correction:

Use AskUserQuestion:
- **Question**: "Clarion path '{ClarionPath}' does not exist or is not configured. Please select the correct path:"
- **Header**: "Clarion Path"
- **Options**:
  1. **C:\Clarion12** - "Standard Clarion 12 installation"
  2. **C:\Clarion11** - "Standard Clarion 11 installation"
  3. **Skip copying** - "Don't copy to Clarion installation now"

If user provides a path:
- Validate it exists
- Save to config: `powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion-write '{NewPath}'"`

**8.0.4 If path EXISTS - Confirm with user:**

Use AskUserQuestion:
- **Question**: "Files will be copied to: {ClarionPath}. Is this correct?"
- **Header**: "Confirm path"
- **Options**:
  1. **Yes, copy to {ClarionPath}** - "Proceed with copying files"
  2. **Change path** - "Use a different Clarion installation"
  3. **Skip copying** - "Don't copy to Clarion installation"

**8.0.5 If user selects "Change path":**

- Ask for new path (same as 8.0.3)
- Validate new path exists
- Save to .clarioncom.env
- Continue to Step 8

**8.0.6 If user selects "Skip copying":**

- Report: "Skipping copy to Clarion. Files are available in ProjectName/Clarion/accessory/"
- End the workflow

## Step 8 Detail: Copy Files to Clarion

After deployment artifacts are generated in the `ProjectName/Clarion/` folder, copy files to the user's Clarion installation.

**8.1 Get Paths:**

Get CLARIONCOM_HOME for the copy script:
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

Get Clarion path (already validated in Step 8.0):
```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') clarion"
```

**8.2 Ask User Where to Copy:**

Use AskUserQuestion to present options:

**Question**: "Where would you like to copy the deployment files?"
**Header**: "Copy files"
**Options**:
1. **Clarion accessory folder** - "Copy DLLs to accessory\bin, others to accessory\resources"
2. **App folder** - "Copy all files to a specific application folder"
3. **Skip** - "Don't copy files now"

**8.3 Execute Copy:**

**USE THE HELPER SCRIPT - Do not construct copy commands manually!**

Replace `{CLARIONCOM_HOME}` and `{ClarionPath}` with the actual values obtained from step 8.1.

**If "Clarion accessory folder":**
```powershell
powershell -ExecutionPolicy Bypass -File "{CLARIONCOM_HOME}\scripts\copy-to-clarion.ps1" -ProjectFolder "ProjectName\Clarion\accessory" -ClarionPath "{ClarionPath}" -Target "accessory"
```

Note: The script reads from the project's `accessory/bin` and `accessory/resources` subfolders.

**If "App folder":**
- Ask user: "Enter the full path to your application folder:"
```powershell
powershell -ExecutionPolicy Bypass -File "{CLARIONCOM_HOME}\scripts\copy-to-clarion.ps1" -ProjectFolder "ProjectName\Clarion\accessory" -Target "appfolder" -AppFolder "{AppFolder}"
```

**The script automatically copies ALL files - DLLs to bin, everything else to resources.**

**8.4 Confirm Copy:**

List ALL files that were copied (check the actual folder contents):
```
Copied to Clarion accessory folders:

To accessory\bin:
  - ProjectName.dll

To accessory\resources:
  - ProjectName.manifest        <- CRITICAL for RegFree COM!
  - ProjectName.header
  - readme_ProjectName.html     <- Note: filename starts with readme_
  - ProgID.details
  - ProgID.events
  - ProgID.methods
```
