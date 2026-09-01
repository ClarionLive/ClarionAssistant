---
name: clarioncom-build
# prettier-ignore
description: Compile C# COM projects for Clarion using MSBuild with correct paths, error handling, and build verification. Supports public releases with changelog management. Auto-applies for building .NET Framework COM components. Verification steps use parallel execution.
version: 1.2.0
changelog:
  - version: 1.2.0
    date: 2026-01-16
    changes:
      - Added Step 5.4 to copy deployment files to marketplace-submission/files/
      - Build now prepares files for marketplace submission automatically
  - version: 1.1.0
    changes:
      - Previous release
---

# Compile Clarion COM Component

Build .NET Framework COM components for Clarion using registration-free (manifest-based) COM.

## Path Resolution - CRITICAL

Get CLARIONCOM_HOME via the helper script (avoids shell escaping — never use `$env:APPDATA` in commands; the `$` gets stripped by Bash. Use `[Environment]::GetFolderPath('ApplicationData')`):

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If NOT_INSTALLED**: Stop and tell user: "ClarionCOM is not installed. Please run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder." Scripts live at the resolved CLARIONCOM_HOME + `\scripts\`.

## CRITICAL RULES

1. **ALWAYS copy files after build** — the task is NOT complete until Step 5 (Copy Files to Clarion) runs. Never stop after verifying build output.
2. **NEVER register the control** — RegFree COM only. No RegAsm.exe, no registry, no .tlb files. The manifest provides all COM activation info.
3. **NEVER run or offer to run tests** — build/test scripts are for the user's convenience only.
4. **NEVER use `dotnet build` / `dotnet msbuild`** — always use Visual Studio's .NET Framework MSBuild.exe. Wrong-MSBuild symptoms and fixes: references/msbuild-setup.md.
5. **Clarion manifest naming**: `ComponentName.manifest` — WITHOUT the `.dll` part (`ComponentName.dll.manifest` will NOT work with Clarion).

## Workflow

### Step 0: Verify Manifest (DO THIS FIRST)

Quick check — must return a `<clrClass` line (NOT `<comClass>`):
```powershell
powershell -Command "Get-Content YourProject.manifest | Select-String -Pattern '<clrClass'"
```
If it fails, STOP — do not build. Read references/manifest-validation.md (in this skill's directory) for the full validation script, required-elements checklist, common manifest errors, and the Clarion manifest naming rule.

### Step 1: Locate MSBuild.exe

Typical path: `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`. If not there, read references/msbuild-setup.md for the search command, common locations, and install options.

### Step 1.5: Version Management

Increment the build number before building:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') increment 'ProjectPath'"
```
If `.env` is missing (NOT_CONFIGURED) or you need init details, read references/versioning-and-releases.md.

### Step 1.6: Public Release (Optional)

Ask the user (AskUserQuestion): "Is this a public release?" If YES: ask bump type (patch/minor/major/custom), ask what changed, update/create CHANGELOG.md, and run the matching version-bump command. Read references/versioning-and-releases.md (in this skill's directory) for the exact prompts, bump commands, and CHANGELOG.md format. If NO: just proceed with Step 1.5's increment.

### Step 2: Build

```cmd
"C:\...\MSBuild.exe" YourProject.csproj -restore -p:Configuration=Release
```
- `-restore` restores NuGet packages (required on first build; do NOT run `dotnet restore` separately).
- Omit `-p:Platform` — the .csproj's PlatformTarget (must be x86) is used.
- Build the .csproj, not the .sln, if you have issues.

Required .csproj settings (net48, x86, Library, no EnableComInterop/RegisterForComInterop): references/regfree-com-config.md.

### Step 3-4: Verify Build Output

Success = DLL created. Check `bin\Release\net48\` for:
- `YourProject.dll` (required), `YourProject.manifest` (required), `YourProject.pdb` (optional)
- Do NOT create .tlb files — not needed for RegFree COM.
- An "Access denied" registry error is NOT a failure — ignore it if the DLL exists.

### Step 4.5: Create Clarion Folder Structure and Copy Output

Create `PROJECT_PATH/Clarion/accessory/bin` and `.../accessory/resources`; copy DLLs to bin, manifest to resources. Exact commands: references/deploy-and-copy.md.

### Step 4.6: Generate Metadata Files (MANDATORY)

Run GenerateClarionMetadata.ps1 to produce `.header` (must contain **[DllsToCopy]**), `ProgID.details`, `ProgID.methods`, `ProgID.events`. Read references/deploy-and-copy.md (in this skill's directory) for the command and verification checks.

### Step 5.0: Verify Clarion Path (BEFORE copying)

Read configured path via `clarioncom-env.ps1 clarion`, validate it exists, and confirm with the user (AskUserQuestion). Handle NOT_FOUND / change-path / skip-copying per references/deploy-and-copy.md.

### Step 5: Copy Files to Clarion

**ALWAYS copy to the Clarion accessory folder** — use the helper script `copy-to-clarion.ps1`, never hand-built copy commands. Then list ALL copied files to the user. Exact invocation and confirmation format: references/deploy-and-copy.md.

### Step 5.4: Copy to Marketplace Submission Folder

Copy accessory bin + resources files flat into `PROJECT_PATH/marketplace-submission/files/`. Commands and confirmation list: references/deploy-and-copy.md.

### Step 5.5: Auto-commit (Optional, git repos only)

If `.git` exists: read version from `.env`, `git add .`, commit as `Build v{version} - {yyyy-MM-dd}`, try push (never fail the build on git/push errors). Full script and reporting rules: references/auto-commit.md (in this skill's directory).

## Key Facts

- Deployment needs only two files next to the Clarion exe: the DLL and `ComponentName.manifest`.
- No registry modifications, no admin rights, xcopy deployment.
- Add a `CopyManifest` MSBuild target to auto-copy the manifest on every build (template in references/regfree-com-config.md).
- Projects with `clarion-com-builder` deployment targets auto-deploy to the `Clarion/accessory` layout; run `clarioncom-deploy` afterwards for HTML docs and batch files.

## Troubleshooting

Read references/msbuild-setup.md (in this skill's directory) for the full catalog: msbuild not found, MSB4803 RegisterAssembly, missing DLL, access-denied registry errors, admin-rights questions.

## References

All in this skill's `references/` directory:

- **manifest-validation.md** — Read before building: full manifest validation script, required-elements checklist, common manifest errors, Clarion manifest naming rule.
- **msbuild-setup.md** — Read when MSBuild can't be found or the build errors: why dotnet build fails, locations, search commands, troubleshooting catalog.
- **versioning-and-releases.md** — Read for Step 1.5/1.6: version script commands, public-release prompts, CHANGELOG.md format and tips.
- **deploy-and-copy.md** — Read for Steps 4.5-5.4: folder structure, metadata generation, Clarion path verification prompts, copy-to-clarion script, marketplace submission.
- **auto-commit.md** — Read for Step 5.5: complete auto-commit script and reporting rules.
- **regfree-com-config.md** — Read for .csproj configuration, RegFree COM rationale, CopyManifest target, automated build script, and new-project auto-deployment behavior.
