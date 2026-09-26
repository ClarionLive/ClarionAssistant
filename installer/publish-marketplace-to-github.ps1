# Publish the repo marketplace/ folder (source of truth) to the standalone GitHub
# marketplace repo that Claude Code consumes:
#     https://github.com/ClarionLive/clarionassistant-marketplace
#
# The repo's marketplace/ folder stays the SOURCE OF TRUTH (skills are developed
# alongside ClarionAssistant). This script mirrors marketplace/ into a working
# clone of the dedicated repo and pushes -- so the GitHub marketplace, which the
# installer/configure.ps1 installs from, always matches the repo.
#
# The dedicated repo's ROOT == the contents of marketplace/ (marketplace.json at
# the repo root), which is what 'claude plugin marketplace add owner/repo'
# expects.
#
# Usage:
#   pwsh installer\publish-marketplace-to-github.ps1                   # publish
#   pwsh installer\publish-marketplace-to-github.ps1 -WhatIf           # dry run (no push)
#   pwsh installer\publish-marketplace-to-github.ps1 -AllowDeletions   # permit removals
#
# DELETIONS ARE REFUSED BY DEFAULT. The mirror step prunes anything absent from
# marketplace/, which is only safe while marketplace/ genuinely IS the source of
# truth -- and once it was not (ticket 69b2f1fb). A run that would remove files
# now stops and prints them instead. -AllowDeletions is how you say "yes, I have
# read that list and I am retiring those skills on purpose".
#
# Requires: git on PATH, and push rights to the repo (the ClarionLive gh/git
# account -- peterparker57 is NOT a member with create/push rights to the org).

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Repo marketplace folder (source of truth). Defaults to ..\marketplace,
    # resolved in the body relative to this script (robust to how it's invoked).
    [string]$Source,
    # GitHub repo (owner/name) to publish to.
    [string]$Repo = 'ClarionLive/clarionassistant-marketplace',
    # Working clone location. Defaults to a temp sibling that is reused across runs.
    [string]$WorkDir = (Join-Path $env:TEMP 'clarionassistant-marketplace-publish'),
    # Commit message for the publish.
    [string]$Message = "Publish marketplace from ClarionAssistant repo",
    # Allow the publish to DELETE files from the GitHub marketplace.
    #
    # Off by default, and that default is the whole point. The mirror below uses
    # robocopy /MIR, which prunes anything absent from $Source. That is correct ONLY
    # while marketplace/ really is the source of truth. It was not: the skill
    # 'clarion-appgen' (9 files, ~41KB, six of them existing in NO source-controlled
    # project anywhere) was published from a laptop-local tree and never landed in
    # this repo. The next routine publish would have deleted it -- silently, as nine
    # more lines in a `git status --short` that already scrolls.
    #
    # So deletions now stop the run and are printed on their own. Pass this switch
    # only when you have LOOKED at that list and every entry is a skill you actually
    # meant to retire. See ticket 69b2f1fb.
    [switch]$AllowDeletions
)

$ErrorActionPreference = 'Stop'

if (-not $Source) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Source = Join-Path (Split-Path $scriptDir -Parent) 'marketplace'
}

if (-not (Test-Path (Join-Path $Source '.claude-plugin\marketplace.json'))) {
    throw "Source does not look like the marketplace folder: $Source"
}

$repoUrl = "https://github.com/$Repo.git"

Write-Host "Publishing marketplace:" -ForegroundColor Cyan
Write-Host "  from (repo)   $Source"
Write-Host "  to   (github) $Repo"
Write-Host "  via (clone)   $WorkDir"

# A reused/stale $WorkDir that is a clone of a DIFFERENT repo would be catastrophic:
# the robocopy /MIR below would replace its tree with the marketplace and the push
# would go to the wrong origin. We reclone whenever the clone doesn't match -- but we
# only ever AUTO-DELETE the tool-owned DEFAULT work dir. If the caller passed an
# explicit -WorkDir that isn't already a matching clone, we FAIL CLOSED rather than
# recursively deleting a path we don't own (which could be an SSH clone of this repo,
# a mistakenly-passed checkout, or a dir with uncommitted work).
$workDirIsDefault = -not $PSBoundParameters.ContainsKey('WorkDir')
$needFreshClone = $true
if (Test-Path (Join-Path $WorkDir '.git')) {
    $existingOrigin = (& git -C $WorkDir remote get-url origin 2>$null)
    if ($existingOrigin -eq $repoUrl) {
        $needFreshClone = $false
    } elseif ($workDirIsDefault) {
        Write-Host "Default work dir origin '$existingOrigin' != '$repoUrl' -- recloning." -ForegroundColor Yellow
    } else {
        throw "WorkDir '$WorkDir' is a git clone of '$existingOrigin', not '$repoUrl'. Refusing to delete a custom -WorkDir I don't own. Pass an empty/new path, or a clone of $Repo."
    }
} elseif ((Test-Path $WorkDir) -and -not $workDirIsDefault -and @(Get-ChildItem -LiteralPath $WorkDir -Force).Count -gt 0) {
    throw "WorkDir '$WorkDir' exists, is non-empty, and is not a git clone. Refusing to delete a custom -WorkDir I don't own. Pass an empty/new path."
}

# 1. Clone (first run / wrong-origin default) or refresh (matching clone) the repo.
# The Remove-Item here is now safe: we only reach it for the tool-owned default dir,
# or an empty/absent custom path (custom non-empty/wrong-origin dirs threw above).
if ($needFreshClone) {
    if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
    Write-Host "Cloning $repoUrl ..."
    & git clone --quiet $repoUrl $WorkDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)" }
} else {
    Write-Host "Refreshing existing clone ..."
    & git -C $WorkDir fetch --quiet origin
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed (exit $LASTEXITCODE)" }
    # Determine the default branch and hard-reset to it so the mirror is clean.
    $defaultBranch = (& git -C $WorkDir remote show origin) |
        Select-String 'HEAD branch:' | ForEach-Object { ($_ -split ':')[1].Trim() }
    if (-not $defaultBranch) { $defaultBranch = 'main' }
    & git -C $WorkDir checkout --quiet $defaultBranch
    if ($LASTEXITCODE -ne 0) { throw "git checkout '$defaultBranch' failed (exit $LASTEXITCODE)" }
    & git -C $WorkDir reset --hard --quiet "origin/$defaultBranch"
    if ($LASTEXITCODE -ne 0) { throw "git reset failed (exit $LASTEXITCODE)" }
}

# 2. Mirror marketplace/ -> working clone, but NEVER touch the clone's .git.
#    /MIR prunes files that no longer exist in Source (e.g. a deleted skill).
#    /MIR is KEPT deliberately: it is what makes a real deletion visible at all.
#    The pruning happens here, in a throwaway clone, and step 3a then refuses to
#    COMMIT it unless -AllowDeletions was passed. Dropping /MIR instead would make
#    deletions impossible to publish AND invisible to review -- the quieter bug.
# robocopy uses exit codes 0-7 for SUCCESS (1 = files copied, the normal case) and
# 8+ for failure. Scope EAP to 'Continue' so pwsh 7.3+ (where non-zero native exits
# honor $ErrorActionPreference) does not throw on the benign exit-1 before our
# `-ge 8` guard runs. Zero $LASTEXITCODE afterward so a residual 1 can't poison the
# git checks below.
Write-Host "Mirroring files ..."
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& robocopy $Source $WorkDir /MIR /XD '.git' /NFL /NDL /NJH /NJS /NP | Out-Null
$rc = $LASTEXITCODE
$ErrorActionPreference = $prevEap
$global:LASTEXITCODE = 0
if ($rc -ge 8) { throw "robocopy failed with exit code $rc" }

# 3. Commit + push only if something changed.
& git -C $WorkDir add -A
$pending = (& git -C $WorkDir status --porcelain)
if (-not $pending) {
    Write-Host "No changes to publish - GitHub marketplace already matches the repo." -ForegroundColor Green
    exit 0
}

Write-Host "Changes to publish:" -ForegroundColor Yellow
& git -C $WorkDir status --short

# A bare 'M' hides magnitude, and magnitude is the whole signal when the published
# side has been edited directly. Four clarioncom skills were found in exactly that
# state (69b2f1fb): publishing them would have silently removed 48 lines that exist
# only on GitHub -- including a reviewed EXCEPTION clause -- while the status line
# said nothing more alarming than 'M'. The diffstat makes an overwrite look like an
# overwrite. Deletions get a hard gate below; modifications get visibility, because
# modifying is the normal reason to publish and must not require a flag.
$modified = @($pending | Where-Object { $_ -match '^M[ ACDMRTU]\s' })
if ($modified.Count -gt 0) {
    Write-Host ""
    Write-Host "Content changes (lines the published copy will GAIN/LOSE):" -ForegroundColor Yellow
    & git -C $WorkDir diff --cached --stat
    Write-Host "Review any large removal -- it may be content edited directly on GitHub" -ForegroundColor DarkGray
    Write-Host "and never brought back into this repo." -ForegroundColor DarkGray
}

# 3a. DELETION GATE. Everything is staged by the `add -A` above, so a pruned file
#     shows as 'D ' in the FIRST (index) column. Match on that column only -- a
#     rename shows as 'R ' and its old path is not a deletion we need to block.
$deleted = @(
    $pending | Where-Object { $_ -match '^D[ ACDMRTU]\s' } | ForEach-Object { ($_ -replace '^..\s+', '').Trim('"') }
)

if ($deleted.Count -gt 0 -and -not $AllowDeletions) {
    Write-Host ""
    Write-Host "REFUSING TO PUBLISH: this run would DELETE $($deleted.Count) file(s) from $Repo" -ForegroundColor Red
    $deleted | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "robocopy /MIR prunes anything missing from the source folder:" -ForegroundColor Yellow
    Write-Host "    $Source" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "If those files SHOULD still be published, they are missing from this repo --" -ForegroundColor Yellow
    Write-Host "add them to marketplace/ and re-run. That is the usual cause: a skill authored" -ForegroundColor Yellow
    Write-Host "directly into the marketplace and never committed here (see ticket 69b2f1fb)." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "If you really are retiring them, re-run with -AllowDeletions." -ForegroundColor Yellow

    # Leave the clone exactly as we found it. Without this the pruned files sit
    # deleted-and-staged in $WorkDir, so a later run reusing this dir starts from a
    # tree that is already missing them -- and the next `git status` would show
    # nothing to block, quietly converting a refusal into a successful deletion.
    & git -C $WorkDir reset --quiet --hard HEAD
    & git -C $WorkDir clean --quiet -fd
    throw "Publish aborted: $($deleted.Count) deletion(s) not confirmed. Re-run with -AllowDeletions to proceed."
}

if ($deleted.Count -gt 0) {
    Write-Host "-AllowDeletions given: $($deleted.Count) file(s) will be REMOVED from $Repo." -ForegroundColor Yellow
}

if ($PSCmdlet.ShouldProcess($Repo, "Commit and push marketplace changes")) {
    & git -C $WorkDir commit --quiet -m $Message
    if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)" }
    & git -C $WorkDir push --quiet origin HEAD
    if ($LASTEXITCODE -ne 0) { throw "git push failed (exit $LASTEXITCODE)" }
    Write-Host "Published to $repoUrl" -ForegroundColor Green
} else {
    Write-Host "(-WhatIf) Skipped commit/push." -ForegroundColor DarkGray
}
