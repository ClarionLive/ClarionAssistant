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

# Initialize GitHub Repository Skill

Initializes a GitHub repository for a ClarionCOM project (new project needing version control, publishing source, or preparing for marketplace submission).

## ⛔ CRITICAL COMMAND RULES

- **NEVER use `powershell -Command "..."` with variables, and never `where gh`** — the `$` characters get stripped by Bash and the command WILL FAIL. Use ONLY the exact commands in references/commands.md (in this skill's directory): the clarioncom-env.ps1 helper script for checks, bash for file checks/git, and full-path gh.exe invocations.
- **Do NOT use Search or Glob tools** — all file locations are known and fixed. The `.details` file is ALWAYS at `PROJECT_PATH/Clarion/*.details`.

Prerequisites: valid project folder with source files; GitHub CLI (`gh`) installed; `GITHUB_TOKEN` in `~/.clarioncom.env`.

## Step 1: Check Prerequisites

Read references/commands.md for the exact helper-script commands, then run:
1. **gh-check** — if `NOT_INSTALLED`, ask "GitHub CLI is required for repository initialization. Install it now?" If yes: `winget install GitHub.cli` and wait. If no: show the manual-install message from references/error-handling.md and stop.
2. **github-token** — if `NOT_CONFIGURED`, show the token setup message from references/error-handling.md and stop.
3. **gh-user** — if `NOT_AUTHENTICATED`, the user needs `gh auth login` first (token-based auth command is in references/commands.md, step 6).

## Step 2: Check Project Status

Using the bash file-check commands in references/commands.md: check `.git` exists, existing remotes, `.gitignore` and `README.md` (project root), and `Clarion/*.details`. If a .details file exists, read it to extract the project description for the README.

**If git is already initialized with a remote**, ask: "A git repository with remote already exists. Options: (1) Skip - keep existing, (2) Add new remote with different name". Skip → display existing remote URL and exit successfully. Add → continue but use a different remote name (e.g. "github" instead of "origin").

## Step 3: Gather Repository Information

1. **Auto-detect first**: call `get_ca_project_info(folder: "PROJECT_PATH")`. If it returns data, use `repoName` as default repo name and `githubUsername` for the target account; display "Using project settings: repo=REPONAME, account=USERNAME".
2. **Repository name**: default from project record, else `basename "PROJECT_PATH"`; confirm with user, allow override.
3. **Visibility**: Private (default) or Public. Note: private repos can be made public later for marketplace submission.
4. **Description**: default from the `.details` file's `Description:` line if found, else empty; allow override.
5. **License**: MIT (Recommended — simple/permissive, allows commercial use) | Apache 2.0 (permissive with patent protection) | GPL 3.0 (copyleft) | No License (all rights reserved, not recommended for open source).

## Step 4: Generate .gitignore (if missing)

Create with the Write tool using the content in references/templates.md.

## Step 5: Generate README.md and LICENSE (if missing)

**YOU MUST READ** (Read tool — do not skip; README quality depends on it) the files in `PROJECT_PATH/Clarion/`: `*.details`, `*.methods`, and `*.events` (if present). Then write a comprehensive README.md and LICENSE following references/templates.md — it has the README structure, the MIT license text, per-license rules, and how to fill `{YEAR}`/`{AUTHOR}`.

## Step 6: Initialize Git and Create Remote Repository

Use the exact commands in references/commands.md, in order:
1. **ALWAYS first**: `git config --global --add safe.directory "PROJECT_PATH"` (prevents "dubious ownership" errors on Windows).
2. `git init` (only if no .git), then `git add .` + `git status --porcelain`, and `git commit -m "Initial commit"` if there are changes.
3. Get the gh.exe full path via the **gh-path** helper; use that path in all gh commands.
4. Authenticate gh with the token from `.clarioncom.env` if needed (command in references/commands.md).
5. Create the repo and push: `gh repo create REPO_NAME --private|--public --description '...' --source . --push` via PowerShell with the full gh path.

## Step 7: Success Output

Display the success banner from references/templates.md (repo URL, visibility, next steps: make public when ready, then '/ClarionCOM' > "Submit to Marketplace").

## Errors

For GitHub CLI not installed, missing GITHUB_TOKEN, repository name already exists, or not authenticated: read references/error-handling.md for the exact message to display.

## Integration Notes

Invoked from `/ClarionCOM` under "More options..." > "Initialize GitHub Repo". The skill: does NOT require the project to be built first; creates `.gitignore`/`README.md` only if missing; uses `gh repo create --source . --push`; preserves existing git history; requires GitHub CLI and its authentication.

**Related skills:** `clarioncom-build` (build), `clarioncom-deploy` (deployment artifacts), `clarioncom-validate` (compliance), `clarioncom-marketplace-submit` (requires public repo).

## References

All under references/ in this skill's directory:

- **commands.md** — forbidden commands plus the exact helper-script, bash, git, and gh commands. Read before running ANY command in Steps 1, 2, and 6.
- **templates.md** — .gitignore content, README.md structure, LICENSE texts, success banner. Read for Steps 4, 5, and 7.
- **error-handling.md** — exact error/setup messages to display. Read when a prerequisite fails or repo creation errors.
