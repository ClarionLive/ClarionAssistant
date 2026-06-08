---
name: clarioncom-github-init
description: Initialize a GitHub repository for a ClarionCOM project with proper .gitignore, README, and remote setup
version: 1.1.0
changelog:
  - version: 1.1.0
    date: 2026-01-13
    changes:
      - Rewrote to use clarioncom-env.ps1 helper script
      - Fixed Bash variable escaping issues
      - Added gh-check and gh-user actions to helper script
  - version: 1.0.0
    date: 2026-01-13
    changes:
      - Initial release with GitHub repository initialization
      - Automatic .gitignore generation for ClarionCOM projects
      - README.md generation with project details
      - GitHub CLI integration for remote repository creation
---

# ⛔ STOP - READ BEFORE DOING ANYTHING ⛔

## FORBIDDEN COMMANDS - THESE WILL FAIL:

```
❌ powershell -Command "..."
❌ powershell -Command "Get-Command gh..."
❌ powershell -Command "$envFile = ..."
❌ powershell -Command "if (Test-Path..."
❌ where gh
```

**ANY command using `powershell -Command` with variables WILL FAIL.** The `$` characters get stripped by Bash.

---

# Initialize GitHub Repository Skill

This skill initializes a GitHub repository for a ClarionCOM project.

---

## ✅ ALLOWED COMMANDS - USE ONLY THESE:

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

### For file checks, use bash:

```bash
# Check if .git exists
test -d "PATH/.git" && echo "EXISTS" || echo "NOT_EXISTS"

# Check if file exists - .gitignore and README.md are in project root
test -f "PATH/.gitignore" && echo "EXISTS" || echo "NOT_EXISTS"
test -f "PATH/README.md" && echo "EXISTS" || echo "NOT_EXISTS"

# Check for .details file - ALWAYS in Clarion subfolder, never search for it
ls "PATH/Clarion/"*.details 2>/dev/null || echo "NO_DETAILS"
```

### For git/gh commands, use PowerShell with full path:

**IMPORTANT:** After getting the gh path from `gh-path`, use PowerShell to run gh commands:

```bash
# Get gh path first, then use it in PowerShell commands
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' auth status"
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' repo create NAME --private --source . --push"
```

```bash
# Git commands (use bash)
git config --global --add safe.directory "PATH"  # ALWAYS run first on Windows!
git -C "PATH" remote -v
git init
git add .
git commit -m "message"
```

**DO NOT USE Search or Glob tools** - all file locations are known and fixed.

---

## When to Use

Use this skill when:
- You have a new ClarionCOM project that needs version control
- You want to publish your control source to GitHub for the community
- You need to prepare a project for eventual marketplace submission
- You want to set up proper .gitignore and README files

## Prerequisites

Before initialization, ensure:
1. Your project has a valid folder structure with source files
2. GitHub CLI (`gh`) is installed
3. A `GITHUB_TOKEN` is configured in `~/.clarioncom.env`

---

## Step 1: Check Prerequisites

**⚠️ REMINDER: Do NOT use `powershell -Command`. Use ONLY the helper script commands shown below.**

### 1a. Check if gh CLI is installed

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-check'
```

**Output**: `INSTALLED` or `NOT_INSTALLED`

If `NOT_INSTALLED`, ask the user: **"GitHub CLI is required for repository initialization. Install it now?"**

- If **yes**: Run `winget install GitHub.cli` and wait for completion
- If **no**: Display the following and stop:
  ```
  GitHub CLI can be installed manually from: https://cli.github.com/
  After installation, run 'gh auth login' to authenticate.
  ```

### 1b. Check for GITHUB_TOKEN

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") github-token'
```

**Output**: The token value or `NOT_CONFIGURED`

If `NOT_CONFIGURED`, display the following and stop:
```
GitHub token required for repository creation.

To set up your token:
1. Visit https://github.com/settings/tokens
2. Click "Generate new token (classic)"
3. Select scopes: repo, read:org
4. Copy the generated token
5. Add to ~/.clarioncom.env: GITHUB_TOKEN=ghp_your_token_here

For detailed instructions: https://clarionlive.com/com_for_clarion/marketplace/setup
```

### 1c. Check GitHub Authentication

**COPY THIS EXACT COMMAND:**
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-user'
```

**Output**: GitHub username, `NOT_AUTHENTICATED`, or `NOT_INSTALLED`

If `NOT_AUTHENTICATED`, the user needs to run `gh auth login` first.

---

## Step 2: Check Project Status

**USE BASH COMMANDS ONLY - no PowerShell for file checks. DO NOT use Search/Glob tools.**

### 2a. Check if git exists
```bash
test -d "PROJECT_PATH/.git" && echo "EXISTS" || echo "NOT_EXISTS"
```

### 2b. Check for existing remotes (if git exists)
```bash
git -C "PROJECT_PATH" remote -v 2>/dev/null || echo "NO_REMOTES"
```

### 2c. Check for existing .gitignore and README.md (in project root)
```bash
test -f "PROJECT_PATH/.gitignore" && echo "GITIGNORE_EXISTS" || echo "GITIGNORE_MISSING"
test -f "PROJECT_PATH/README.md" && echo "README_EXISTS" || echo "README_MISSING"
```

### 2d. Check for .details file (ALWAYS in Clarion subfolder)

The `.details` file is **ALWAYS** located at `PROJECT_PATH/Clarion/*.details`. Do NOT search for it - check directly:
```bash
ls "PROJECT_PATH/Clarion/"*.details 2>/dev/null || echo "NO_DETAILS"
```

If a .details file exists, read it to extract project description for the README.

If git is already initialized with a remote:
- Ask user: **"A git repository with remote already exists. Options: (1) Skip - keep existing, (2) Add new remote with different name"**
- If "Skip": Display existing remote URL and exit successfully
- If "Add new remote": Continue with Step 3 but use a different remote name (e.g., "github" instead of "origin")

---

## Step 3: Gather Repository Information

### 3a. Auto-detect from project record (ALWAYS do this first)

Call `get_ca_project_info` with the project folder path:

```
get_ca_project_info(folder: "PROJECT_PATH")
```

If the tool returns data:
- Use `repoName` as the default repository name
- Use `githubUsername` to know which account to create under
- Display: "Using project settings: repo=REPONAME, account=USERNAME"

### 3b. Collect remaining info

### Repository Name

**If `repoName` was returned by `get_ca_project_info`:**
- Use it as the default — confirm with user but don't ask from scratch

**If not available**, extract default from folder name:
```bash
basename "PROJECT_PATH"
```

Ask user: **"Repository name?"**
- Default: from project record or folder name
- Allow override with any valid repo name

### Visibility

Ask user: **"Repository visibility?"**
- Options: **Private** (default), **Public**
- Note: "Private repos can be made public later for marketplace submission"

### Description

If a .details file was found, extract the description from it (look for `Description:` line).

Ask user: **"Repository description?"**
- Default: description from .details file if available, otherwise empty
- Allow override

### License

Ask user: **"Which license would you like to use?"**
- Options:
  - **MIT License** (Recommended) - "Simple and permissive, allows commercial use"
  - **Apache 2.0** - "Permissive with patent protection"
  - **GPL 3.0** - "Copyleft, requires derivative works to be open source"
  - **No License** - "All rights reserved (not recommended for open source)"

---

## Step 4: Generate .gitignore (if missing)

If .gitignore does NOT exist (already checked in Step 2c), create it using the Write tool with this content:

```
# Build outputs
bin/
obj/

# Clarion deployment folder (generated artifacts)
Clarion/

# Visual Studio files
*.user
*.suo
.vs/

# NuGet packages
packages/

# IDE and editor files
*.swp
*.swo
*~

# OS files
Thumbs.db
.DS_Store
```

## Step 5: Generate README.md and LICENSE (if missing)

If README.md does NOT exist (already checked in Step 2c), generate it using documentation from the Clarion folder.

### 5a. Read Documentation Files from Clarion Folder

**YOU MUST READ THESE FILES** using the Read tool to extract their content for the README:

1. **List the Clarion folder first:**
```bash
ls "PROJECT_PATH/Clarion/"
```

2. **USE THE READ TOOL to read each documentation file:**
   - `PROJECT_PATH/Clarion/*.details` - Contains: Description, ControlType, ProgId, Version
   - `PROJECT_PATH/Clarion/*.methods` - Contains: All properties and methods with signatures
   - `PROJECT_PATH/Clarion/*.events` - Contains: All events with signatures (if exists)

**IMPORTANT:** Do not skip this step! The README quality depends on reading these files and extracting their content.

### 5b. Generate README.md

After reading the documentation files, use the Write tool to create a **comprehensive** README.md.

**Extract and include:**
- From `.details`: Project description, version, control type
- From `.methods`: List ALL properties with their types, list ALL methods with parameters
- From `.events`: List ALL events with their signatures

Use this structure:

```markdown
# {PROJECT_NAME}

{DESCRIPTION from .details file}

## Overview

This is a ClarionCOM control for use with Clarion for Windows applications.

## Requirements

- .NET Framework 4.7.2 or later
- Clarion 11.0 or later

## Features

{List key features based on the methods/properties from .methods file}

## Installation

Copy the contents of the `Clarion/` folder to your Clarion accessory folder:
- `{ControlName}.dll` - The compiled control
- `{ControlName}.manifest` - Registration-free COM manifest
- Other supporting files

## API Reference

### Properties

{Extract properties from .methods file - format as a table or list}

### Methods

{Extract methods from .methods file - include parameters and return types}

### Events

{If .events file exists, list the events with their signatures}

## Building from Source

1. Clone this repository
2. Open in Visual Studio or use Claude Code
3. Build in Release mode

Or use Claude Code:
```
/ClarionCOM
```
Then select "Build existing project".

## License

{LICENSE TEXT based on user selection - see Step 5c}

## Links

- [COM for Clarion Documentation](https://clarionlive.com/com_for_clarion)
- [COM Marketplace](https://clarionlive.com/com_for_clarion/marketplace)
```

### 5c. Generate LICENSE File

Based on the license selected in Step 3, create a LICENSE file in the project root.

**MIT License:**
```
MIT License

Copyright (c) {YEAR} {AUTHOR}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Apache 2.0:** Use standard Apache 2.0 license text.

**GPL 3.0:** Use standard GPL 3.0 license text.

**No License:** Do not create LICENSE file, put "All Rights Reserved" in README.

Get the current year with:
```bash
date +%Y
```

Get author name from git config or ask user:
```bash
git config user.name 2>/dev/null || echo "UNKNOWN"
```

## Step 6: Initialize Git and Create Remote Repository

**USE THESE EXACT BASH COMMANDS - substitute PROJECT_PATH, REPO_NAME, etc. with actual values:**

### 6a. Add Safe Directory (REQUIRED on Windows)

**ALWAYS run this first** to prevent "dubious ownership" errors on Windows drives:
```bash
git config --global --add safe.directory "PROJECT_PATH"
```

### 6b. Initialize Local Repository (if needed)

If no .git folder exists:
```bash
cd "PROJECT_PATH" && git init
```

### 6c. Create Initial Commit

```bash
cd "PROJECT_PATH" && git add . && git status --porcelain
```

If there are changes to commit:
```bash
cd "PROJECT_PATH" && git commit -m "Initial commit"
```

### 6d. Get gh.exe Path

First, get the full path to gh.exe:
```bash
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-path'
```

This will return something like `C:\Program Files\GitHub CLI\gh.exe`. Store this path for use in gh commands.

### 6e. Authenticate gh CLI (if needed)

If gh-user returned NOT_AUTHENTICATED, authenticate using the token from .clarioncom.env:
```bash
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' auth login --with-token <<< (Get-Content $env:USERPROFILE\.clarioncom.env | Select-String 'GITHUB_TOKEN=' | ForEach-Object { $_ -replace 'GITHUB_TOKEN=','' })"
```

Or have the user run `gh auth login` interactively.

### 6f. Create GitHub Repository and Push

Use PowerShell with the full gh path to create repo and push:

```bash
powershell -ExecutionPolicy Bypass -Command "Set-Location 'PROJECT_PATH'; & 'GH_PATH' repo create REPO_NAME --VISIBILITY --description 'DESCRIPTION' --source . --push"
```

Where:
- `GH_PATH` - the full path from step 6d (e.g., `C:\Program Files\GitHub CLI\gh.exe`)
- `PROJECT_PATH` - the actual project folder path
- `REPO_NAME` - the repository name from Step 3
- `VISIBILITY` - either `--private` or `--public`
- `DESCRIPTION` - the description from Step 3

## Step 7: Success Output

After successful initialization, display:

```
============================================================
  GitHub Repository Created!
============================================================

  Repository: https://github.com/{username}/{repoName}
  Visibility: {visibility}

  Your project is now on GitHub!

  Next steps:
  - Make the repository public when ready to share
  - Run '/ClarionCOM' and select "Submit to Marketplace"

============================================================
```

## Error Handling

### GitHub CLI Not Installed

```
GitHub CLI is required for repository initialization.
Install with: winget install GitHub.cli

Or download from: https://cli.github.com/
After installation, run: gh auth login
```

### Missing GITHUB_TOKEN

```
Error: No GITHUB_TOKEN found in ~/.clarioncom.env

To set up your token:
1. Visit https://github.com/settings/tokens
2. Click "Generate new token (classic)"
3. Select scopes: repo, read:org
4. Copy the generated token
5. Add to ~/.clarioncom.env: GITHUB_TOKEN=ghp_your_token_here
```

### Repository Name Already Exists

```
Error: Repository '{repoName}' already exists on GitHub.

Options:
  1. Run skill again with a different name
  2. Delete the existing repository
  3. Manually add existing repo as remote:
     git remote add origin https://github.com/{username}/{repoName}.git
     git push -u origin main
```

### Not Authenticated

```
Error: Not authenticated with GitHub.

Run: gh auth login
Then retry this operation.
```

## Integration Notes

This skill can be invoked:
1. From the main `/ClarionCOM` command under "More options..." > "Initialize GitHub Repo"

The skill:
1. Does NOT require the project to be built first
2. Creates `.gitignore` and `README.md` only if they don't exist
3. Uses `gh repo create` with `--source .` and `--push` for streamlined setup
4. Preserves any existing git history if the repo was already initialized
5. Requires GitHub CLI (`gh`) and uses its authentication

## Related Skills

- `clarioncom-build` - Build the control
- `clarioncom-deploy` - Generate deployment artifacts
- `clarioncom-validate` - Validate control compliance
- `clarioncom-marketplace-submit` - Submit control to marketplace (requires public repo)
