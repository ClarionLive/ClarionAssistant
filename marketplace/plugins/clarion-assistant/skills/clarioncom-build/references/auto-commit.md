# Auto-commit Changes (Step 5.5 detail)

After a successful build and file copy, automatically commit changes if the project is a git repository. This step is **optional** and only runs if a `.git` folder exists.

**5.5.1 Check if Git Repository Exists:**

```powershell
if (Test-Path ".git") {
    # Git repo exists, proceed with auto-commit
}
```

If `.git` folder does not exist, skip this step entirely and report:
```
Skipped auto-commit (not a git repository)
```

**5.5.2 Get Version Information:**

Read the current version from the project's `.env` file or `AssemblyInfo.cs`:

```powershell
# Get version from .env file
$envFile = ".env"
if (Test-Path $envFile) {
    $envContent = Get-Content $envFile
    $major = ($envContent | Where-Object { $_ -match "^MAJOR_VERSION=" }) -replace "MAJOR_VERSION=", ""
    $minor = ($envContent | Where-Object { $_ -match "^MINOR_VERSION=" }) -replace "MINOR_VERSION=", ""
    $build = ($envContent | Where-Object { $_ -match "^BUILD_NUMBER=" }) -replace "BUILD_NUMBER=", ""
    $version = "$major.$minor.$build"
}
```

**5.5.3 Check for Uncommitted Changes:**

```powershell
$changes = git status --porcelain
```

If no changes exist, report:
```
No changes to commit
```

**5.5.4 Stage and Commit Changes:**

If changes exist, stage all changes and create a commit:

```powershell
$date = Get-Date -Format "yyyy-MM-dd"
git add .
git commit -m "Build v$version - $date"
```

Commit message format: `Build v1.0.5 - 2026-01-13`

**5.5.5 Push to Remote (Optional - Don't Fail on Error):**

Try to push changes, but never fail the build if push fails:

```powershell
try {
    git push 2>$null
    Write-Host "Changes pushed to remote"
} catch {
    Write-Host "Note: Could not push to remote (no remote configured or auth issue)"
}
```

**5.5.6 Report Results:**

Report what was done:
- Success: `Auto-committed build changes: Build v1.0.5 - 2026-01-13`
- No changes: `No changes to commit`
- Not a repo: `Skipped auto-commit (not a git repository)`
- Push failed: `Auto-committed locally (push failed - no remote or auth issue)`

**Complete Auto-commit Script:**

```powershell
# Auto-commit after successful build (only if git repo exists)
$projectPath = "ProjectPath"  # Replace with actual project path

Push-Location $projectPath
try {
    if (Test-Path ".git") {
        # Get version from .env
        $version = "unknown"
        $envFile = ".env"
        if (Test-Path $envFile) {
            $envContent = Get-Content $envFile
            $major = ($envContent | Where-Object { $_ -match "^MAJOR_VERSION=" }) -replace "MAJOR_VERSION=", ""
            $minor = ($envContent | Where-Object { $_ -match "^MINOR_VERSION=" }) -replace "MINOR_VERSION=", ""
            $build = ($envContent | Where-Object { $_ -match "^BUILD_NUMBER=" }) -replace "BUILD_NUMBER=", ""
            if ($major -and $minor -and $build) {
                $version = "$major.$minor.$build"
            }
        }

        # Check for changes
        $changes = git status --porcelain
        if ($changes) {
            $date = Get-Date -Format "yyyy-MM-dd"
            $commitMessage = "Build v$version - $date"

            git add .
            git commit -m $commitMessage

            # Try to push (don't fail if it doesn't work)
            try {
                git push 2>$null
                Write-Host "Auto-committed and pushed: $commitMessage"
            } catch {
                Write-Host "Auto-committed locally: $commitMessage"
                Write-Host "Note: Could not push to remote (no remote configured or auth issue)"
            }
        } else {
            Write-Host "No changes to commit"
        }
    } else {
        Write-Host "Skipped auto-commit (not a git repository)"
    }
} finally {
    Pop-Location
}
```

**Important Notes:**
- This step is OPTIONAL - only runs if `.git` folder exists
- NEVER fail the build because of git issues
- Git errors should be logged as warnings, not errors
- The commit happens AFTER Step 5 (Copy Files to Clarion) completes successfully
