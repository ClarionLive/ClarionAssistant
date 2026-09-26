# Exact Commands (helper script, file checks, git/gh)

Copy these commands exactly, substituting PROJECT_PATH, REPO_NAME, GH_PATH, etc.

## FORBIDDEN COMMANDS - THESE WILL FAIL

```
❌ powershell -Command "..."
❌ powershell -Command "Get-Command gh..."
❌ powershell -Command "$envFile = ..."
❌ powershell -Command "if (Test-Path..."
❌ where gh
```

**ANY command using `powershell -Command` with variables WILL FAIL.** The `$` characters get stripped by Bash. Use only `powershell -ExecutionPolicy Bypass -Command '...'` invocations of the helper script (single quotes) or the full-path gh commands shown below.

**DO NOT USE Search or Glob tools** — all file locations are known and fixed.

## Prerequisite checks (helper script)

```bash
# Check gh CLI installed — output: INSTALLED or NOT_INSTALLED
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-check'

# Get full path to gh.exe (use this for all gh commands!) — e.g. C:\Program Files\GitHub CLI\gh.exe
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-path'

# Check GitHub token configured — output: the token value or NOT_CONFIGURED
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") github-token'

# Check GitHub authentication — output: GitHub username, NOT_AUTHENTICATED, or NOT_INSTALLED
powershell -ExecutionPolicy Bypass -Command '& (Join-Path $env:APPDATA "ClarionCOM\scripts\clarioncom-env.ps1") gh-user'
```

## File checks (bash only — no PowerShell)

```bash
# Check if .git exists
test -d "PROJECT_PATH/.git" && echo "EXISTS" || echo "NOT_EXISTS"

# Check for existing remotes (if git exists)
git -C "PROJECT_PATH" remote -v 2>/dev/null || echo "NO_REMOTES"

# Check for .gitignore and README.md — they live in the project root
test -f "PROJECT_PATH/.gitignore" && echo "GITIGNORE_EXISTS" || echo "GITIGNORE_MISSING"
test -f "PROJECT_PATH/README.md" && echo "README_EXISTS" || echo "README_MISSING"

# Check for .details file — ALWAYS in Clarion subfolder, never search for it
ls "PROJECT_PATH/Clarion/"*.details 2>/dev/null || echo "NO_DETAILS"

# List the Clarion folder (documentation files for README)
ls "PROJECT_PATH/Clarion/"

# Default repo name from folder
basename "PROJECT_PATH"

# Current year (for LICENSE)
date +%Y

# Author name (for LICENSE)
git config user.name 2>/dev/null || echo "UNKNOWN"
```

## Git commands (bash)

```bash
# ALWAYS run first on Windows! Prevents "dubious ownership" errors
git config --global --add safe.directory "PROJECT_PATH"

# Initialize local repository (if no .git folder exists)
cd "PROJECT_PATH" && git init

# Stage and inspect changes
cd "PROJECT_PATH" && git add . && git status --porcelain

# Commit (if there are changes)
cd "PROJECT_PATH" && git commit -m "Initial commit"
```

## gh commands (PowerShell with full path from gh-path)

First get GH_PATH via the `gh-path` helper command above, then:

```bash
# Check auth status
powershell -ExecutionPolicy Bypass -Command "& 'C:\Program Files\GitHub CLI\gh.exe' auth status"

# Authenticate with token from ~/.clarioncom.env (if gh-user returned NOT_AUTHENTICATED)
powershell -ExecutionPolicy Bypass -Command "& 'GH_PATH' auth login --with-token <<< (Get-Content $env:USERPROFILE\.clarioncom.env | Select-String 'GITHUB_TOKEN=' | ForEach-Object { $_ -replace 'GITHUB_TOKEN=','' })"
# ...or have the user run `gh auth login` interactively.

# Create GitHub repository and push
powershell -ExecutionPolicy Bypass -Command "Set-Location 'PROJECT_PATH'; & 'GH_PATH' repo create REPO_NAME --VISIBILITY --description 'DESCRIPTION' --source . --push"
```

Where:
- `GH_PATH` — full path from the gh-path helper (e.g. `C:\Program Files\GitHub CLI\gh.exe`)
- `PROJECT_PATH` — the actual project folder path
- `REPO_NAME` — the repository name gathered in Step 3
- `VISIBILITY` — either `--private` or `--public`
- `DESCRIPTION` — the description gathered in Step 3
