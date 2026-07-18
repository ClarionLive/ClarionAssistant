<#
.SYNOPSIS
    Re-pin the bundled Clarion LSP server to a tagged release of msarson/Clarion-Extension
    and rebuild it with our CodeGraph overlay, instead of hand-maintaining a drifting snapshot.
    GitHub #40 (LSP snapshot drift).

.DESCRIPTION
    The bundled LSP server that deploy.ps1 copies out of $CLARIONLSP_ROOT is a build artifact
    of the public Clarion VS Code extension (msarson/Clarion-Extension) with our CodeGraph
    integration applied on top. Because that snapshot is hand-maintained it drifts from upstream.

    This script turns the pin into a repeatable, git-anchored operation:

      1. Fetch tags from origin (msarson/Clarion-Extension).
      2. Report the drift between the currently checked-out commit and the target tag.
      3. (with -Apply) Re-pin to the target tag, re-apply the server.ts CodeGraph wiring,
         rebuild out/, and verify the CodeGraph markers survived the rebuild.

    KEY FACT that makes this safe: the CodeGraph overlay source files
    (server/src/codegraph-bridge.ts, codegraph-indexer.ts) are UNTRACKED in the upstream
    clone and out/ is gitignored, so `git checkout <tag>` leaves them in place. The ONLY
    tracked artifact that needs re-application is the wiring inside server/src/server.ts.

    Without -Apply the script is READ-ONLY (fetch + report only).

.PARAMETER LspRoot
    The upstream clone (a git checkout of msarson/Clarion-Extension with our overlay).
    Defaults to $env:CLARIONLSP_ROOT, else H:\DevLaptop\ClarionLSP (legacy dev path).

.PARAMETER Tag
    Target release tag to pin to. Defaults to targetPin.tag from lsp-snapshot.json.

.PARAMETER Apply
    Actually perform the checkout/rebuild. Omit for a dry run (fetch + drift report only).

.PARAMETER SkipBuild
    Skip `npm ci && npm run compile` after checkout (report + re-pin only).

.PARAMETER Pure
    #40 disposition (2026-07-03): build STOCK upstream at the tag with NO CodeGraph overlay. The overlay
    is retired — CodeGraph go-to-def / references / completion are served C#-side (SharedLspBridge +
    CodeGraphProvider), so the bundled LSP is pure msarson/Clarion-Extension. Produces a clean tag build
    under $PureRoot (default <repo>\.lsp-build\<Tag>) which deploy.ps1 sources instead of the overlay clone.
    Independent of -Apply (which is the legacy overlay-keeping path). Idempotent: skips the rebuild if a
    pure build is already present. VERIFIES the built server.js contains ZERO codegraph refs.

.PARAMETER PureRoot
    Where the pure build lives / is created. Defaults to <repo>\.lsp-build\<Tag>.

.EXAMPLE
    # Dry run — show how far the bundled server has drifted from v0.9.6
    .\Sync-LspServer.ps1

.EXAMPLE
    # Re-pin to v0.9.6 and rebuild WITH the CodeGraph overlay (legacy path)
    .\Sync-LspServer.ps1 -Apply

.EXAMPLE
    # Build PURE upstream v0.9.6 (no overlay) for the #40 default bundled server
    .\Sync-LspServer.ps1 -Pure -Tag v0.9.6
#>
[CmdletBinding()]
param(
    [string]$LspRoot,
    [string]$Tag,
    [switch]$Apply,
    [switch]$SkipBuild,
    [switch]$Pure,
    [string]$PureRoot
)

$ErrorActionPreference = 'Stop'
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$ManifestPath = Join-Path $ScriptDir 'lsp-snapshot.json'

function Line($status, $msg, $color) { Write-Host ("  {0,-5} {1}" -f $status, $msg) -ForegroundColor $color }
function OK($m)   { Line 'OK'   $m 'Green' }
function Info($m) { Line 'INFO' $m 'Cyan' }
function Warn($m) { Line 'WARN' $m 'Yellow' }
function Fail($m) { Line 'FAIL' $m 'Red' }

# Keep ALL pin fields honest on a successful sync (#77 housekeeping): the sync used to update only
# resolvedTag/resolvedCommit/lastSync, leaving targetPin/currentPin frozen at whatever hand-written
# audit they last held -- after the v1.0.0 re-pin the manifest still reported targetPin v0.9.8 and a
# June currentPin, so a first read said "behind" when the bundle was current. One writer, all fields.
function Update-PinFields($manifest, $Tag, $repoRoot) {
    $commit     = (git -C $repoRoot rev-parse --short HEAD)
    $commitDate = (git -C $repoRoot show -s --format=%cs HEAD)
    $manifest | Add-Member -NotePropertyName 'targetPin' -NotePropertyValue ([pscustomobject]@{
        note = "Tag to sync toward; -Tag overrides. AUTO-UPDATED to the last successfully synced tag by Sync-LspServer.ps1 -- edit by hand only to stage a pin to a NEWER release before running the sync."
        tag  = $Tag
    }) -Force
    $manifest | Add-Member -NotePropertyName 'currentPin' -NotePropertyValue ([pscustomobject]@{
        note       = "AUTO-UPDATED by Sync-LspServer.ps1 on each successful sync -- the observed state of the just-synced checkout. Historical hand-audit notes live in git history (pre-#77 revisions of this file)."
        tag        = $Tag
        commit     = $commit
        commitDate = $commitDate
    }) -Force
}

# --- Resolve inputs -------------------------------------------------------------------
if (-not (Test-Path $ManifestPath)) { throw "Manifest not found: $ManifestPath" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

if (-not $LspRoot) { $LspRoot = if ($env:CLARIONLSP_ROOT) { $env:CLARIONLSP_ROOT } else { 'H:\DevLaptop\ClarionLSP' } }
if (-not $Tag)     { $Tag = $manifest.targetPin.tag }

Write-Host ""
Write-Host "Clarion LSP server sync (GitHub #40)" -ForegroundColor White
Write-Host "  LspRoot : $LspRoot"
Write-Host "  Tag     : $Tag"
Write-Host "  Mode    : $(if ($Apply) { 'APPLY' } else { 'dry run (read-only)' })"
Write-Host ""

if (-not (Test-Path (Join-Path $LspRoot '.git'))) { Fail "Not a git clone: $LspRoot"; exit 2 }

Push-Location $LspRoot
try {
    # Assert this is the expected upstream repo
    $origin = (git remote get-url origin 2>$null)
    if ($origin -notmatch 'Clarion-Extension') {
        Fail "origin is '$origin' — expected msarson/Clarion-Extension. Aborting to avoid touching the wrong repo."
        exit 2
    }
    OK "origin: $origin"

    # 1. Fetch tags
    Info "Fetching tags from origin..."
    git fetch --tags --prune origin | Out-Null

    # Verify the target tag exists
    $tagExists = (git tag --list $Tag)
    if (-not $tagExists) {
        Fail "Tag '$Tag' does not exist upstream. Available recent tags:"
        git tag --sort=-creatordate | Select-Object -First 8 | ForEach-Object { Write-Host "         $_" }
        exit 2
    }
    OK "target tag exists: $Tag ($(git show -s --format='%ci' $Tag 2>$null | Select-Object -First 1))"

    # --- PURE mode (#40) -------------------------------------------------------------
    # Build STOCK upstream at $Tag with NO CodeGraph overlay, into $PureRoot. The overlay is retired;
    # CodeGraph is C#-side now. Self-contained: creates/refreshes a clean tag checkout (untracked overlay
    # .ts files in $LspRoot do NOT propagate into a fresh worktree), builds, verifies purity, records pin.
    if ($Pure) {
        if (-not $PureRoot) {
            $RepoRoot = Split-Path -Parent $ScriptDir      # ...\ClarionAssistant
            $PureRoot = Join-Path $RepoRoot (".lsp-build\" + $Tag)
        }
        $builtServer = Join-Path $PureRoot $manifest.source.buildOutput   # out/server/src/server.js
        Write-Host ""
        Info "PURE build target: $PureRoot (tag $Tag, NO overlay)"

        # Ensure a clean tag checkout at $PureRoot
        if (Test-Path (Join-Path $PureRoot '.git')) {
            Info "Refreshing existing checkout -> $Tag"
            git -C $PureRoot fetch --tags --quiet origin 2>$null
            git -C $PureRoot checkout --quiet --force $Tag
        } elseif (Test-Path (Join-Path $PureRoot 'package.json')) {
            Info "Using existing (non-git) pure tree as-is"
        } else {
            Info "Creating clean worktree at $PureRoot ..."
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $PureRoot) | Out-Null
            git worktree add --force $PureRoot $Tag
        }

        # Assert PURE source: the overlay .ts must NOT be present in this tree.
        foreach ($f in $manifest.codeGraphOverlay.overlayFiles) {
            if (Test-Path (Join-Path $PureRoot $f)) {
                Fail "PURE tree contains overlay file '$f' — not pure. Use a clean checkout."; exit 6
            }
        }
        OK "no CodeGraph overlay in source (pure)"

        # Build (idempotent: skip if a pure build is already present)
        if ($SkipBuild) {
            Warn "Skipping build (-SkipBuild)."
        } elseif ((Test-Path $builtServer) -and ((Get-Content $builtServer -Raw) -notmatch 'codegraph|CodeGraph')) {
            OK "pure build already present (skipping rebuild; delete out/ to force)"
        } else {
            Info "Building (npm ci && npm run compile) — can take a minute..."
            Push-Location $PureRoot
            try {
                npm ci;          if ($LASTEXITCODE) { throw "npm ci failed ($LASTEXITCODE)" }
                npm run compile; if ($LASTEXITCODE) { throw "npm run compile failed ($LASTEXITCODE)" }
            } finally { Pop-Location }
            OK "build complete"
        }

        # Verify PURE: built server.js must contain ZERO codegraph refs.
        if (Test-Path $builtServer) {
            if ((Get-Content $builtServer -Raw) -match 'codegraph|CodeGraph') {
                Fail "Built server.js STILL contains CodeGraph refs — not pure. Aborting."; exit 6
            }
            OK "verified PURE: no CodeGraph refs in built server.js"
        } elseif (-not $SkipBuild) {
            Fail "Expected build output not found: $builtServer"; exit 6
        }

        # Record the pure pin (targetPin/currentPin included -- see Update-PinFields)
        $resolved = (git -C $PureRoot rev-parse --short HEAD 2>$null)
        Update-PinFields $manifest $Tag $PureRoot
        $manifest | Add-Member -NotePropertyName 'pure'           -NotePropertyValue $true   -Force
        $manifest | Add-Member -NotePropertyName 'resolvedCommit' -NotePropertyValue $resolved -Force
        $manifest | Add-Member -NotePropertyName 'resolvedTag'    -NotePropertyValue $Tag      -Force
        $manifest | Add-Member -NotePropertyName 'lastSync'       -NotePropertyValue (Get-Date -Format 'yyyy-MM-dd') -Force
        ($manifest | ConvertTo-Json -Depth 12) | Set-Content -Path $ManifestPath -Encoding UTF8
        OK "manifest updated: pure=true tag=$Tag resolvedCommit=$resolved"
        Write-Host ""
        OK "PURE sync complete. deploy.ps1 sources the pure build from $PureRoot."
        exit 0
    }

    # 2. Drift report — current HEAD vs target tag
    $head       = (git rev-parse --short HEAD)
    $headBranch = (git rev-parse --abbrev-ref HEAD)
    $counts     = (git rev-list --left-right --count "$Tag...HEAD") -split '\s+'
    $behind     = [int]$counts[0]   # on tag, not on HEAD
    $ahead      = [int]$counts[1]   # on HEAD, not on tag
    Write-Host ""
    Info "Current bundle pin : $head ($headBranch)"
    Info "Target tag         : $Tag"
    if ($behind -gt 0) { Warn "HEAD is $behind commit(s) BEHIND $Tag (missing upstream work)" }
    if ($ahead  -gt 0) { Warn "HEAD is $ahead commit(s) AHEAD of $Tag (would be dropped by re-pinning)" }
    if ($behind -eq 0 -and $ahead -eq 0) { OK "Already at $Tag — no drift." }
    Write-Host ""

    if (-not $Apply) {
        Info "Dry run complete. Re-run with -Apply to re-pin to $Tag and rebuild."
        exit 0
    }

    # --- APPLY path -------------------------------------------------------------------
    # Guard: the working tree must be clean EXCEPT for our server.ts wiring (which we
    # deliberately carry across the checkout). Any other tracked change is unexpected and
    # could be lost — refuse rather than clobber the user's work.
    $wiringFile = $manifest.codeGraphOverlay.wiring.file    # server/src/server.ts
    $dirtyTracked = @(git status --porcelain --untracked-files=no) |
        Where-Object { $_ -and ($_.Substring(3) -ne $wiringFile) }
    if ($dirtyTracked.Count -gt 0) {
        Fail "Working tree has tracked changes beyond '$wiringFile'. Commit/stash them first:"
        $dirtyTracked | ForEach-Object { Write-Host "         $_" }
        exit 3
    }

    # Confirm the overlay .ts files are present (they should survive checkout as untracked files)
    foreach ($f in $manifest.codeGraphOverlay.overlayFiles) {
        if (Test-Path (Join-Path $LspRoot $f)) { OK "overlay present: $f" }
        else { Fail "overlay MISSING: $f — the CodeGraph patch source is not in this clone. Aborting."; exit 3 }
    }

    # Carry the server.ts wiring across the checkout by stashing just that path, then popping
    # it back onto the tag's server.ts. A pop conflict is the one genuine manual merge point.
    $hasWiring = @(git status --porcelain -- $wiringFile).Count -gt 0
    if ($hasWiring) {
        Info "Stashing CodeGraph wiring in $wiringFile ..."
        git stash push --quiet -- $wiringFile
    } else {
        Warn "$wiringFile has no uncommitted wiring — assuming it's already committed or applied via patch."
    }

    Info "Checking out $Tag ..."
    git checkout --quiet $Tag

    if ($hasWiring) {
        Info "Re-applying CodeGraph wiring onto ${Tag}'s $wiringFile ..."
        $popOk = $true
        try { git stash pop --quiet } catch { $popOk = $false }
        $conflict = @(git status --porcelain -- $wiringFile) -match '^(UU|AA|U|.U)'
        if (-not $popOk -or $conflict) {
            Fail "MERGE CONFLICT re-applying wiring onto $wiringFile."
            Warn "Resolve the conflict in $wiringFile by hand, then re-run with -SkipBuild to finish (rebuild + verify)."
            Warn "This is the one step #40 cannot fully automate — the server.ts wiring must merge onto the new tag."
            exit 4
        }
        OK "wiring re-applied cleanly onto $Tag"
    }

    # 3. Rebuild
    if ($SkipBuild) {
        Warn "Skipping rebuild (-SkipBuild). Remember to `npm ci && npm run compile` before deploying."
    } else {
        Info "Rebuilding (npm ci && npm run compile) — this can take a minute..."
        npm ci
        npm run compile
        OK "rebuild complete"
    }

    # 4. Verify CodeGraph markers survived into the rebuilt server.js
    $builtServer = Join-Path $LspRoot $manifest.source.buildOutput
    if (Test-Path $builtServer) {
        $body = Get-Content $builtServer -Raw
        $missing = @()
        foreach ($m in $manifest.codeGraphOverlay.builtMarkers) {
            if ($body -notmatch [regex]::Escape($m)) { $missing += $m }
        }
        if ($missing.Count -eq 0) { OK "CodeGraph markers present in rebuilt server.js" }
        else { Fail ("CodeGraph markers MISSING from server.js: {0}" -f ($missing -join ', ')) ; exit 5 }
    } elseif (-not $SkipBuild) {
        Fail "Expected build output not found: $builtServer"; exit 5
    }

    # 5. Record the resolved pin back into the manifest (targetPin/currentPin included)
    $resolved = (git rev-parse --short HEAD)
    Update-PinFields $manifest $Tag '.'
    $manifest.resolvedCommit = $resolved
    $manifest | Add-Member -NotePropertyName 'resolvedTag' -NotePropertyValue $Tag -Force
    $manifest.lastSync = (Get-Date -Format 'yyyy-MM-dd')
    ($manifest | ConvertTo-Json -Depth 12) | Set-Content -Path $ManifestPath -Encoding UTF8
    OK "manifest updated: resolvedCommit=$resolved lastSync=$($manifest.lastSync)"

    Write-Host ""
    OK "Sync complete. The bundle is now pinned to $Tag ($resolved). Run deploy.ps1 to redeploy."
}
finally {
    Pop-Location
}
