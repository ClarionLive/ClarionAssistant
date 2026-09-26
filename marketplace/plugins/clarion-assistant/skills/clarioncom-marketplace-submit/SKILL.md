---
name: clarioncom-marketplace-submit
description: Submit a ClarionCOM control to the COM Marketplace for discovery by the Clarion community
version: 1.7.0
changelog:
  - version: 1.7.0
    date: 2026-01-17
    changes:
      - Added fork cleanup step to remove old control folders before submission
      - Added validation feedback - waits for workflow and reports pass/fail
      - Prevents conflicts from previous submission attempts
  - version: 1.6.0
    date: 2026-01-17
    changes:
      - Added local manifest validation before PR submission
      - Auto-fixes 3-part versions to 4-part format (1.0.0 → 1.0.0.0)
      - Validates required fields, ProgID format, and category
      - Prevents failed PRs by catching validation errors early
  - version: 1.5.0
    date: 2026-01-16
    changes:
      - Include Clarion deployment files in marketplace submissions
      - Submissions now include files/ subfolder with DLL, manifest, and documentation
      - Enables direct download from marketplace and documentation display on website
  - version: 1.4.0
    date: 2026-01-13
    changes:
      - Fixed gh CLI path resolution using clarioncom-env.ps1 helper script
      - Fixed Bash variable escaping issues (same pattern as github-init skill)
  - version: 1.3.0
    date: 2026-01-13
    changes:
      - Added repository sync check before submission (uncommitted/unpushed changes)
      - Added repository visibility check (prompts to make private repos public)
  - version: 1.2.0
    date: 2026-01-11
    changes:
      - Added support for updating existing controls (not just new submissions)
      - Detection of whether control already exists in registry
      - Different branch naming and PR templates for new vs update submissions
      - Automatic CHANGELOG.md parsing to populate PR description for updates
      - Version-aware branch naming for updates (e.g., update-calendar-picker-v1.1.0)
  - version: 1.1.0
    date: 2025-12-01
    changes:
      - Added automated GitHub PR submission
      - Token-based authentication via .clarioncom.env
  - version: 1.0.0
    date: 2025-11-15
    changes:
      - Initial release with manifest.yaml and api-docs.json generation
---

# ClarionCOM Marketplace Submission Skill

Generates the files to submit a COM control to the ClarionCOM Marketplace registry and automatically creates a GitHub Pull Request to ClarionLive/com-marketplace.

## CRITICAL RULES (apply throughout)

1. **NEVER use `powershell -Command` with `$` variables or bare `gh`** — Bash strips `$` and bare `gh` won't be found. Use ONLY the clarioncom-env.ps1 helper script and the full gh.exe path. Read references/command-syntax.md (in this skill's directory) for the exact allowed/forbidden commands.
2. **manifest.yaml version MUST be 4-part** (e.g., `1.0.0.0`, never `1.0.0`). If 3-part, auto-fix by appending `.0` to BOTH `version:` and `changelog[0].version`. **DO NOT create a PR with a 3-part version — the PR will fail validation.**

## When to Use

Use when a built COM control with a public GitHub repository is ready to share on clarionlive.com/com_for_clarion/marketplace. Requires a `Clarion/accessory/` folder with deployment artifacts (bin DLLs, manifest, .details/.methods/.events) and a successful build (`/ClarionCOM` -> Build first if needed). Invoked from `/ClarionCOM` "Submit to Marketplace". Does NOT build or modify source; creates files only in `marketplace-submission/`.

## Workflow

**Step 1 — Check GitHub CLI and auth.** Run helper-script checks: `gh-check` (install via winget if missing, with user consent), `github-token` (stop if NOT_CONFIGURED), `gh-user` (store username), `gh-path` (store full gh.exe path — needed for Steps 7.6 and 8). Read references/command-syntax.md for the exact commands, outputs, and stop/remediation messages.

**Step 2 — Validate project structure.** Verify `Clarion/` folder exists with *.dll, *.manifest, *.details, *.methods. PowerShell snippet in references/command-syntax.md (Step 2 Detail).

**Step 3 — Gather submission info.** ALWAYS call `get_ca_project_info(folder: "PROJECT_PATH")` first and use its values (repoUrl, githubUsername, githubDisplayName); only ask the user for what's missing. Always ask for **Category** and **Tags**, then display all values and confirm. Read references/metadata-schemas.md (Step 3 Detail) for returned fields, allowed categories, and the confirmation display.

**Step 4 — Extract metadata.** Parse the project's `.details`, `.methods`, and `.events` files. Read references/metadata-schemas.md (in this skill's directory) for the formats.

**Step 5 — Generate manifest.yaml.** Use the template in references/metadata-schemas.md (Step 5 Detail). Version must be 4-part.

**Step 6 — Generate api-docs.json.** Methods/events/properties documentation. JSON template in references/metadata-schemas.md (Step 6 Detail).

**Step 6.5 — Validate manifest.yaml.** MANDATORY gate before any PR: 4-part version (auto-fix 3-part), ProgID pattern `Namespace.ClassName`, all required fields, valid category, 4-part changelog version. STOP on non-fixable failures. Read references/metadata-schemas.md (Step 6.5 Detail) for full rules and pass/fail messages.

**Step 7 — Create submission files.** FIRST (7.0): if `marketplace-submission/manifest.yaml` already exists, re-validate its version and fix 3-part versions before anything else (see metadata-schemas.md, Step 7.0 Detail). Then write manifest.yaml + api-docs.json to `marketplace-submission/` and copy the full Clarion deployment package into `marketplace-submission/files/`. Read references/submission-package.md (in this skill's directory) for copy commands and the final folder structure.

**Step 7.5 — Repository sync check.** If uncommitted/unpushed changes exist, ask user: push or cancel. Commands and AskUserQuestion wording in references/pr-workflow.md.

**Step 7.6 — Repository visibility check.** Submissions require a public repo. If PRIVATE, ask user: make public or cancel. Commands in references/pr-workflow.md.

**Step 8 — Automated PR.** Fork ClarionLive/com-marketplace, clone the fork to /tmp, **clean any old control folders matching this control's base name (CRITICAL — stale 3-part-version manifests fail validation)**, detect NEW vs UPDATE, read CHANGELOG.md for updates, create branch, copy files, commit, push, create the PR and capture its URL. Read references/pr-workflow.md (in this skill's directory) for every command, branch/commit naming, and both PR body templates.

**Step 8.5 — Wait for validation.** Sleep 30s, then poll the workflow run every 15s (max 2 min) and report PASSED / FAILED / still-running to the user. Exact commands and report blocks in references/pr-workflow.md.

**Step 9 — Success output.** Display the success banner with the PR URL as a clickable link (template in references/pr-workflow.md).

## Error Handling

On any failure (missing gh/token/Clarion files, bad URL, private repo, fork/clone/PR failure), read references/error-handling.md (in this skill's directory) for the exact message and solution to display.

## References

All files are in this skill's `references/` directory:

- **command-syntax.md** — Read FIRST before any shell command: forbidden/allowed forms, Step 1 prerequisite checks, Step 2 structure validation.
- **metadata-schemas.md** — Read at Steps 3–6.5/7.0: submission-info gathering, metadata file formats, manifest.yaml and api-docs.json templates, validation rules.
- **submission-package.md** — Read at Step 7: marketplace-submission/ layout and copy commands.
- **pr-workflow.md** — Read at Steps 7.5–9: sync/visibility checks, fork/clone/clean/branch commands, PR templates, validation polling, success banner.
- **error-handling.md** — Read when a step fails: exact error/solution messages.

Related skills: `clarioncom-build` (build the control before submission), `clarioncom-deploy` (generate deployment artifacts), `clarioncom-validate` (validate control compliance).
