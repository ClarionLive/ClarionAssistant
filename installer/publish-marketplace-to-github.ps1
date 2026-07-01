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
#   pwsh installer\publish-marketplace-to-github.ps1            # publish
#   pwsh installer\publish-marketplace-to-github.ps1 -WhatIf    # dry run (no push)
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
    [string]$Message = "Publish marketplace from ClarionAssistant repo"
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

if ($PSCmdlet.ShouldProcess($Repo, "Commit and push marketplace changes")) {
    & git -C $WorkDir commit --quiet -m $Message
    if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)" }
    & git -C $WorkDir push --quiet origin HEAD
    if ($LASTEXITCODE -ne 0) { throw "git push failed (exit $LASTEXITCODE)" }
    Write-Host "Published to $repoUrl" -ForegroundColor Green
} else {
    Write-Host "(-WhatIf) Skipped commit/push." -ForegroundColor DarkGray
}
