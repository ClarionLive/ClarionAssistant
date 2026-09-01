# PR Workflow Detail (Steps 7.5 through 9)

**IMPORTANT:** Use the full gh path obtained in Step 1d (e.g., `C:\Program Files\GitHub CLI\gh.exe`) for ALL gh commands. Replace `GH_PATH` below with that path.

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
   WARNING: Found existing folder(s) in fork: [folder names]
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
