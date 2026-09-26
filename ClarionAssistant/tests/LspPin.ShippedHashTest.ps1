# Harness for the shipped-artifact pin check (PR #191). Discovered and run automatically by
# Run-Tests.ps1.
#
# WHAT IT GUARDS.
#   1. deploy.ps1's Test-LspPin hashes the server.js it is about to ship and compares it with
#      lsp-snapshot.json's resolvedServerSha256: a match ships, a tampered/other-build server.js is
#      refused, and a manifest without the field WARNS that nothing was verified (fails open).
#   2. Sync-LspServer.ps1 -Pure on a NON-GIT pure tree identifies the build by a source fingerprint,
#      so an unchanged tree is not rebuilt on every run (before the fix $headNow was null there, the
#      stamp could never match, and every run rebuilt), while a source edit still forces a rebuild.
#
# HOW. Part 1 runs the REAL functions, extracted from deploy.ps1 through the AST, against a scratch
# git tree whose commit matches the pin -- so the commit check passes and the HASH alone decides
# each case. Part 2 runs a copy of the REAL Sync-LspServer.ps1 (a copy only so it writes a scratch
# manifest instead of the committed one) with a fake npm first on PATH that just counts builds.
#
# Exit 0 = all pass, 1 = a real failure, 2 = could not run (git missing).
$ErrorActionPreference = 'Stop'
$RepoDir  = Split-Path -Parent $PSScriptRoot          # ...\ClarionAssistant
$fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:fail++ }
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Host "git not on PATH" -ForegroundColor Yellow; exit 2 }

$root = Join-Path $env:TEMP ('lsppin-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$savedLspRoot = $env:CLARIONLSP_ROOT
$savedPath    = $env:PATH
try {
    New-Item -ItemType Directory -Force $root | Out-Null

    # ================================================================ part 1: deploy.ps1 Test-LspPin
    Write-Host "-- deploy.ps1 Test-LspPin (shipped server.js hash)" -ForegroundColor Cyan
    $deployAst = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $RepoDir 'deploy.ps1'), [ref]$null, [ref]$null)
    foreach ($fn in 'Invoke-GitQuiet', 'Get-FileSha256', 'Test-LspPin') {
        $def = $deployAst.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $fn }, $true)
        if (-not $def) { Write-Host "  FAIL  deploy.ps1 no longer defines $fn" -ForegroundColor Red; exit 1 }
        . ([scriptblock]::Create($def.Extent.Text))
    }

    # $ProjectDir is what Test-LspPin reads the manifest from.
    $ProjectDir = Join-Path $root 'project'
    New-Item -ItemType Directory -Force (Join-Path $ProjectDir 'lsp-server-sync') | Out-Null
    $manifestPath = Join-Path $ProjectDir 'lsp-server-sync\lsp-snapshot.json'

    # A git source tree whose HEAD matches the pin, so the secondary (commit) check always passes.
    $src = Join-Path $root 'src'
    New-Item -ItemType Directory -Force (Join-Path $src 'out\server\src') | Out-Null
    'package' | Set-Content (Join-Path $src 'package.json')
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & git -C $src init --quiet 2>&1 | Out-Null
    & git -C $src -c user.email=t@t -c user.name=t add package.json 2>&1 | Out-Null
    & git -C $src -c user.email=t@t -c user.name=t commit --quiet -m base 2>&1 | Out-Null
    $ErrorActionPreference = $prevEap
    $head = (& git -C $src rev-parse --short HEAD).Trim()
    if (-not $head) { Write-Host "could not create the fixture git tree" -ForegroundColor Yellow; exit 2 }

    $serverJs = Join-Path $src 'out\server\src\server.js'
    [System.IO.File]::WriteAllBytes($serverJs, [System.Text.Encoding]::ASCII.GetBytes("console.log('pure build');`n"))
    $goodHash = (Get-FileHash -LiteralPath $serverJs -Algorithm SHA256).Hash.ToLowerInvariant()
    Check "Get-FileSha256 agrees with Get-FileHash" ((Get-FileSha256 $serverJs) -eq $goodHash)

    function Write-Manifest($hash) {
        $m = [ordered]@{ resolvedCommit = $head; resolvedTag = 'vTEST' }
        if ($hash) { $m.resolvedServerSha256 = $hash }
        [System.IO.File]::WriteAllText($manifestPath, (([pscustomobject]$m) | ConvertTo-Json))
    }
    # Runs Test-LspPin, returning @{ ok = <bool>; text = <everything it printed> }.
    function Invoke-Pin {
        $all  = @(& { Test-LspPin $src } 6>&1)
        $bool = @($all | Where-Object { $_ -is [bool] })
        $text = ($all | Where-Object { $_ -isnot [bool] } | ForEach-Object { "$_" }) -join "`n"
        @{ ok = $(if ($bool.Count -eq 1) { $bool[0] } else { $null }); text = $text }
    }

    $env:CLARIONLSP_ROOT = $null

    # A. matching hash -> ships, and says the ARTIFACT (not just the source) was verified.
    Write-Manifest $goodHash
    $r = Invoke-Pin
    Check "A  matching hash: returns True" ($r.ok -eq $true)
    Check "A  matching hash: prints the shipped-server.js OK line" ($r.text -match 'OK\s+shipped server\.js matches pin')

    # B. tampered server.js (one byte appended) -> refused, even though the commit still matches.
    Add-Content -LiteralPath $serverJs -Value '// overlay'
    $r = Invoke-Pin
    Check "B  tampered server.js: returns False" ($r.ok -eq $false)
    Check "B  tampered server.js: prints the FAIL line" ($r.text -match 'FAIL\s+shipped server\.js does NOT match the pin')

    # C. same tampered file, but CLARIONLSP_ROOT override -> warns and ships.
    $env:CLARIONLSP_ROOT = $src
    $r = Invoke-Pin
    Check "C  mismatch under CLARIONLSP_ROOT: returns True with a WARN" (($r.ok -eq $true) -and ($r.text -match 'WARN\s+shipped server\.js hashes'))
    $env:CLARIONLSP_ROOT = $null

    # D. manifest without resolvedServerSha256 -> fails OPEN, but says the artifact is NOT verified.
    Write-Manifest $null
    $r = Invoke-Pin
    Check "D  no resolvedServerSha256: returns True (fails open)" ($r.ok -eq $true)
    Check "D  no resolvedServerSha256: WARNs that server.js is NOT verified" ($r.text -match 'no resolvedServerSha256.*NOT verified')

    # ================================================ part 2: Sync-LspServer.ps1 -Pure, non-git tree
    Write-Host "-- Sync-LspServer.ps1 -Pure on a non-git pure tree (build stamp)" -ForegroundColor Cyan
    $syncDir = Join-Path $root 'sync\lsp-server-sync'
    New-Item -ItemType Directory -Force $syncDir | Out-Null
    Copy-Item (Join-Path $RepoDir 'lsp-server-sync\Sync-LspServer.ps1') $syncDir
    $syncManifest = Join-Path $syncDir 'lsp-snapshot.json'
    [System.IO.File]::WriteAllText($syncManifest, (([pscustomobject]@{
        source           = [pscustomobject]@{ repo = 'https://example.invalid/Clarion-Extension.git'; buildOutput = 'out/server/src/server.js' }
        codeGraphOverlay = [pscustomobject]@{ overlayFiles = @('server/src/codegraph-bridge.ts') }
        targetPin        = [pscustomobject]@{ tag = 'vTEST' }
    }) | ConvertTo-Json -Depth 5))

    # Non-git pure tree (package.json present, no .git) with a build already sitting in out/.
    $tree = Join-Path $root 'tree'
    New-Item -ItemType Directory -Force (Join-Path $tree 'server\src'), (Join-Path $tree 'out\server\src') | Out-Null
    '{ "name": "fixture" }' | Set-Content (Join-Path $tree 'package.json')
    'export const x = 1;' | Set-Content (Join-Path $tree 'server\src\server.ts')
    [System.IO.File]::WriteAllBytes((Join-Path $tree 'out\server\src\server.js'), [System.Text.Encoding]::ASCII.GetBytes("console.log('built');`n"))
    $treeHash = (Get-FileHash -LiteralPath (Join-Path $tree 'out\server\src\server.js') -Algorithm SHA256).Hash.ToLowerInvariant()

    # Fake npm: records each invocation, succeeds, changes nothing.
    $bin = Join-Path $root 'bin'
    New-Item -ItemType Directory -Force $bin | Out-Null
    $marker = Join-Path $root 'npm-calls.txt'
    [System.IO.File]::WriteAllText((Join-Path $bin 'npm.cmd'), "@echo off`r`necho %*>>`"$marker`"`r`nexit /b 0`r`n")
    $env:PATH = "$bin;$savedPath"
    function Get-BuildCount { if (Test-Path $marker) { @(Get-Content $marker | Where-Object { $_ -match '^ci' }).Count } else { 0 } }
    $hostExe = (Get-Process -Id $PID).Path
    function Invoke-Sync {
        $o = & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $syncDir 'Sync-LspServer.ps1') -Pure -PureRoot $tree 2>&1
        @{ code = $LASTEXITCODE; text = ($o | ForEach-Object { "$_" }) -join "`n" }
    }

    # Run 1: out/ exists but has no stamp -> rebuild, stamp, record the hash.
    $r1 = Invoke-Sync
    $stampPath = Join-Path $tree 'out\.pure-build-stamp.json'
    $stamp = if (Test-Path $stampPath) { Get-Content $stampPath -Raw | ConvertFrom-Json } else { $null }
    Check "1  first run exits 0" ($r1.code -eq 0)
    Check "1  first run rebuilds (no stamp)" ((Get-BuildCount) -eq 1)
    Check "1  stamp written with a source-fingerprint build id" ($stamp -and "$($stamp.buildId)" -match '^tree:[0-9a-f]{64}$')
    $m1 = Get-Content $syncManifest -Raw | ConvertFrom-Json
    Check "1  manifest records resolvedServerSha256 of out/server/src/server.js" ($m1.resolvedServerSha256 -eq $treeHash)
    if ($r1.code -ne 0) { Write-Host $r1.text }

    # Run 2: nothing changed -> must NOT rebuild. (Before the fix: rebuilt on every run.)
    $r2 = Invoke-Sync
    Check "2  unchanged non-git tree: exits 0" ($r2.code -eq 0)
    Check "2  unchanged non-git tree: NOT rebuilt" ((Get-BuildCount) -eq 1)
    Check "2  says the build is already present" ($r2.text -match 'pure build already present for tree:')

    # Run 3: touching only out/ is not a source change -> still no rebuild.
    'x' | Set-Content (Join-Path $tree 'out\server\src\unrelated.js')
    $null = Invoke-Sync
    Check "3  a file under out/ does not change the fingerprint" ((Get-BuildCount) -eq 1)

    # Run 4: a source edit -> rebuild.
    'export const x = 2;' | Set-Content (Join-Path $tree 'server\src\server.ts')
    $r4 = Invoke-Sync
    Check "4  edited source: rebuilt" ((Get-BuildCount) -eq 2)
    Check "4  says which build id changed" ($r4.text -match 'was built from tree:\w+ but the source is now tree:\w+')
}
finally {
    $env:CLARIONLSP_ROOT = $savedLspRoot
    $env:PATH = $savedPath
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}

Write-Host ""
if ($fail) { Write-Host "$fail check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed" -ForegroundColor Green
exit 0
