# Harness for the bundled-LSP pin checks (PR #191). Discovered and run automatically by
# Run-Tests.ps1.
#
# WHAT IT GUARDS.
#   1. deploy.ps1 Test-LspPin: a server.js that hashes to resolvedServerSha256 ships; a tampered one
#      is refused, INCLUDING under CLARIONLSP_ROOT unless -AllowUnpinnedLsp is passed; a manifest
#      without the hash warns and ships (local dev only); a targetPin bump that was never synced
#      (targetPin.tag != resolvedTag) is refused unless -AllowUnpinnedLsp.
#   2. Sync-LspServer.ps1 -Pure, non-git tree: npm is refused without -TrustNonGitTree; with it, a
#      source fingerprint identifies the build, so an unchanged tree is not rebuilt (and its stamp not
#      rewritten), an out/-only change is not a rebuild, and a source edit is.
#   3. Sync-LspServer.ps1 -Pure, git tree: npm never runs unless origin is EXACTLY source.repo and
#      HEAD is the tag's commit and (for the already-pinned tag) resolvedCommit.
#   4. installer\build-installer.ps1 Test-InstallerLspPin: the release gate -- matching hash passes;
#      tampered server.js, missing hash, unsynced pin bump, and an .iss SrcLsp pointing at another tag
#      all fail.
#
# HOW. Parts 1 and 4 run the REAL functions, extracted through the AST. Parts 2 and 3 run a copy of
# the REAL Sync-LspServer.ps1 (a copy only so it writes a scratch manifest, not the committed one),
# with a fake npm first on PATH that just records its calls. All fixtures live under %TEMP%; the git
# "upstream" in part 3 is a local repo, so nothing touches the network.
#
# Exit 0 = all pass, 1 = a real failure, 2 = could not run (git missing).
$ErrorActionPreference = 'Stop'
$RepoDir = Split-Path -Parent $PSScriptRoot          # ...\ClarionAssistant
$RootDir = Split-Path -Parent $RepoDir               # repository root (holds installer\)
$fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:fail++ }
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Host "git not on PATH" -ForegroundColor Yellow; exit 2 }

# Runs git quietly (stderr folded in, never terminating) and returns trimmed stdout.
function G {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & git -c user.email=t@t -c user.name=t @args 2>&1 | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } }
    finally { $ErrorActionPreference = $prev }
    (($o | ForEach-Object { "$_" }) -join "`n").Trim()
}
# Runs a scriptblock, returning @{ ok = <the single [bool] it returned>; text = <all it printed> }.
function Invoke-Captured([scriptblock]$sb) {
    $all  = @(& $sb 6>&1)
    $bool = @($all | Where-Object { $_ -is [bool] })
    $text = ($all | Where-Object { $_ -isnot [bool] } | ForEach-Object { "$_" }) -join "`n"
    @{ ok = $(if ($bool.Count -eq 1) { $bool[0] } else { $null }); text = $text }
}
function Import-Functions($file, [string[]]$names) {
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$null)
    foreach ($fn in $names) {
        $def = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $fn }, $true)
        if (-not $def) { Write-Host "  FAIL  $(Split-Path $file -Leaf) no longer defines $fn" -ForegroundColor Red; exit 1 }
        $def.Extent.Text
    }
}
function Get-Sha([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

$root = Join-Path $env:TEMP ('lsppin-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$savedLspRoot = $env:CLARIONLSP_ROOT
$savedPath    = $env:PATH
try {
    New-Item -ItemType Directory -Force $root | Out-Null

    # ================================================================ part 1: deploy.ps1 Test-LspPin
    Write-Host "-- 1. deploy.ps1 Test-LspPin" -ForegroundColor Cyan
    foreach ($t in (Import-Functions (Join-Path $RepoDir 'deploy.ps1') 'Invoke-GitQuiet', 'Get-FileSha256', 'Test-LspPin')) {
        . ([scriptblock]::Create($t))
    }
    $AllowUnpinnedLsp = $false                       # deploy.ps1's switch, read by Test-LspPin

    $ProjectDir = Join-Path $root 'project'           # Test-LspPin reads the manifest from here
    New-Item -ItemType Directory -Force (Join-Path $ProjectDir 'lsp-server-sync') | Out-Null
    $manifestPath = Join-Path $ProjectDir 'lsp-server-sync\lsp-snapshot.json'

    # A git source tree whose HEAD matches the pin, so the commit check passes and the HASH (or the
    # tag check) alone decides each case.
    $src = Join-Path $root 'src'
    New-Item -ItemType Directory -Force (Join-Path $src 'out\server\src') | Out-Null
    'package' | Set-Content (Join-Path $src 'package.json')
    $null = G -C $src init --quiet
    $null = G -C $src add package.json
    $null = G -C $src commit --quiet -m base
    $head = G -C $src rev-parse --short HEAD
    if (-not $head) { Write-Host "could not create the fixture git tree" -ForegroundColor Yellow; exit 2 }

    $serverJs = Join-Path $src 'out\server\src\server.js'
    [System.IO.File]::WriteAllBytes($serverJs, [System.Text.Encoding]::ASCII.GetBytes("console.log('pure build');`n"))
    $goodHash = Get-Sha $serverJs
    Check "Get-FileSha256 agrees with Get-FileHash" ((Get-FileSha256 $serverJs) -eq $goodHash)

    function Write-DeployManifest($hash, $target = 'vTEST') {
        $m = [ordered]@{ targetPin = [pscustomobject]@{ tag = $target }; resolvedCommit = $head; resolvedTag = 'vTEST' }
        if ($hash) { $m.resolvedServerSha256 = $hash }
        [System.IO.File]::WriteAllText($manifestPath, (([pscustomobject]$m) | ConvertTo-Json))
    }
    $env:CLARIONLSP_ROOT = $null

    Write-DeployManifest $goodHash
    $r = Invoke-Captured { Test-LspPin $src }
    Check "A  matching hash: ships" ($r.ok -eq $true)
    Check "A  matching hash: prints the shipped-server.js OK line" ($r.text -match 'OK\s+shipped server\.js matches pin')

    Add-Content -LiteralPath $serverJs -Value '// overlay'
    $r = Invoke-Captured { Test-LspPin $src }
    Check "B  tampered server.js: refused" ($r.ok -eq $false)
    Check "B  tampered server.js: FAIL line + esbuild-bundle / re-pin guidance" (($r.text -match 'FAIL\s+shipped server\.js does NOT match the pin') -and ($r.text -match 'esbuild bundle') -and ($r.text -match 'commit the new resolvedServerSha256'))

    $env:CLARIONLSP_ROOT = $src
    $r = Invoke-Captured { Test-LspPin $src }
    Check "C  mismatch under CLARIONLSP_ROOT, no switch: STILL refused" ($r.ok -eq $false)
    Check "C  ... and says how to opt in (-AllowUnpinnedLsp)" ($r.text -match 'CLARIONLSP_ROOT is set.*\s+.*-AllowUnpinnedLsp')
    $AllowUnpinnedLsp = $true
    $r = Invoke-Captured { Test-LspPin $src }
    Check "C2 mismatch with -AllowUnpinnedLsp: ships with a WARN" (($r.ok -eq $true) -and ($r.text -match 'WARN\s+shipped server\.js hashes.*-AllowUnpinnedLsp'))
    $AllowUnpinnedLsp = $false
    $env:CLARIONLSP_ROOT = $null

    Write-DeployManifest $null
    $r = Invoke-Captured { Test-LspPin $src }
    Check "D  no resolvedServerSha256: ships (fails open for local dev)" ($r.ok -eq $true)
    Check "D  ... WARNs it is NOT verified and that the installer refuses it" (($r.text -match 'no resolvedServerSha256.*NOT verified') -and ($r.text -match 'build-installer\.ps1 REFUSES'))

    [System.IO.File]::WriteAllBytes($serverJs, [System.Text.Encoding]::ASCII.GetBytes("console.log('pure build');`n"))
    Write-DeployManifest $goodHash 'vNEXT'
    $r = Invoke-Captured { Test-LspPin $src }
    Check "E  targetPin.tag != resolvedTag (unsynced bump): refused" ($r.ok -eq $false)
    Check "E  ... says to run -Pure and commit the manifest" ($r.text -match 'never synced' -and $r.text -match 'Sync-LspServer\.ps1 -Pure and commit lsp-snapshot\.json')
    $AllowUnpinnedLsp = $true
    $r = Invoke-Captured { Test-LspPin $src }
    Check "E2 unsynced bump with -AllowUnpinnedLsp: ships with a WARN" (($r.ok -eq $true) -and ($r.text -match 'WARN\s+lsp-snapshot\.json targets vNEXT'))
    $AllowUnpinnedLsp = $false

    # ======================================================== shared setup for parts 2 and 3 (Sync)
    $syncDir = Join-Path $root 'sync\lsp-server-sync'
    New-Item -ItemType Directory -Force $syncDir | Out-Null
    Copy-Item (Join-Path $RepoDir 'lsp-server-sync\Sync-LspServer.ps1') $syncDir
    $syncManifest = Join-Path $syncDir 'lsp-snapshot.json'
    function Write-SyncManifest($repo, $resolvedCommit) {
        $m = [ordered]@{
            source           = [pscustomobject]@{ repo = $repo; buildOutput = 'out/server/src/server.js' }
            codeGraphOverlay = [pscustomobject]@{ overlayFiles = @('server/src/codegraph-bridge.ts') }
            targetPin        = [pscustomobject]@{ tag = 'vTEST' }
        }
        if ($resolvedCommit) { $m.resolvedTag = 'vTEST'; $m.resolvedCommit = $resolvedCommit }
        [System.IO.File]::WriteAllText($syncManifest, (([pscustomobject]$m) | ConvertTo-Json -Depth 5))
    }
    # Fake npm: records each invocation, succeeds, changes nothing.
    $bin = Join-Path $root 'bin'
    New-Item -ItemType Directory -Force $bin | Out-Null
    $marker = Join-Path $root 'npm-calls.txt'
    [System.IO.File]::WriteAllText((Join-Path $bin 'npm.cmd'), "@echo off`r`necho %*>>`"$marker`"`r`nexit /b 0`r`n")
    $env:PATH = "$bin;$savedPath"
    function Get-BuildCount { if (Test-Path $marker) { @(Get-Content $marker | Where-Object { $_ -match '^ci' }).Count } else { 0 } }
    $hostExe = (Get-Process -Id $PID).Path
    function Invoke-Sync($pureRoot, [switch]$Trust) {
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $syncDir 'Sync-LspServer.ps1'), '-Pure', '-PureRoot', $pureRoot)
        if ($Trust) { $a += '-TrustNonGitTree' }
        $o = & $hostExe @a 2>&1
        @{ code = $LASTEXITCODE; text = ($o | ForEach-Object { "$_" }) -join "`n" }
    }

    # ======================================================= part 2: -Pure on a NON-GIT pure tree
    Write-Host "-- 2. Sync-LspServer.ps1 -Pure, non-git tree" -ForegroundColor Cyan
    Write-SyncManifest 'https://example.invalid/Clarion-Extension.git' $null
    $tree = Join-Path $root 'tree'
    New-Item -ItemType Directory -Force (Join-Path $tree 'server\src'), (Join-Path $tree 'out\server\src') | Out-Null
    '{ "name": "fixture" }' | Set-Content (Join-Path $tree 'package.json')
    'export const x = 1;' | Set-Content (Join-Path $tree 'server\src\server.ts')
    [System.IO.File]::WriteAllBytes((Join-Path $tree 'out\server\src\server.js'), [System.Text.Encoding]::ASCII.GetBytes("console.log('built');`n"))
    $treeHash = Get-Sha (Join-Path $tree 'out\server\src\server.js')
    $stampPath = Join-Path $tree 'out\.pure-build-stamp.json'

    $r0 = Invoke-Sync $tree
    Check "0  no -TrustNonGitTree: refuses (exit 2) and runs NO npm" (($r0.code -eq 2) -and ((Get-BuildCount) -eq 0))
    Check "0  ... and names the opt-in" ($r0.text -match 'TrustNonGitTree')

    $r1 = Invoke-Sync $tree -Trust
    $stamp = if (Test-Path $stampPath) { Get-Content $stampPath -Raw | ConvertFrom-Json } else { $null }
    Check "1  -TrustNonGitTree, no stamp: rebuilds, exit 0" (($r1.code -eq 0) -and ((Get-BuildCount) -eq 1))
    Check "1  stamp written with a source-fingerprint build id" ($stamp -and "$($stamp.buildId)" -match '^tree:[0-9a-f]{64}$')
    $m1 = Get-Content $syncManifest -Raw | ConvertFrom-Json
    Check "1  manifest records resolvedServerSha256 of out/server/src/server.js" ($m1.resolvedServerSha256 -eq $treeHash)
    if ($r1.code -ne 0) { Write-Host $r1.text }

    $stampBytes = [System.IO.File]::ReadAllText($stampPath)
    Start-Sleep -Milliseconds 1100                     # builtAt has 1-second resolution
    $r2 = Invoke-Sync $tree -Trust
    Check "2  unchanged tree: NOT rebuilt" (($r2.code -eq 0) -and ((Get-BuildCount) -eq 1))
    Check "2  says the build is already present" ($r2.text -match 'pure build already present for tree:')
    Check "2  skipped build: stamp NOT rewritten" ([System.IO.File]::ReadAllText($stampPath) -eq $stampBytes)
    $r2b = Invoke-Sync $tree
    Check "2b stamped build, no -TrustNonGitTree: no npm needed, so allowed (exit 0)" (($r2b.code -eq 0) -and ((Get-BuildCount) -eq 1))

    'x' | Set-Content (Join-Path $tree 'out\server\src\unrelated.js')
    $null = Invoke-Sync $tree -Trust
    Check "3  a file under out/ does not change the fingerprint" ((Get-BuildCount) -eq 1)

    # Exclusions apply to the ROOT-RELATIVE path: a tree that itself lives under a folder named out\
    # must still fingerprint its sources (matching on FullName excluded every file there).
    # (Get-FileSha256 is already defined from part 1; the Sync copy is identical.)
    foreach ($t in (Import-Functions (Join-Path $RepoDir 'lsp-server-sync\Sync-LspServer.ps1') 'Get-SourceFingerprint')) {
        . ([scriptblock]::Create($t))
    }
    $nested = Join-Path $root 'out\nested-tree'
    New-Item -ItemType Directory -Force (Join-Path $nested 'server\src') | Out-Null
    'export const n = 1;' | Set-Content (Join-Path $nested 'server\src\server.ts')
    Check "3b tree under an out\ folder still fingerprints its sources" ("$(Get-SourceFingerprint $nested)" -match '^tree:[0-9a-f]{64}$')

    'export const x = 2;' | Set-Content (Join-Path $tree 'server\src\server.ts')
    $r4 = Invoke-Sync $tree -Trust
    Check "4  edited source: rebuilt" ((Get-BuildCount) -eq 2)
    Check "4  says which build id changed" ($r4.text -match 'was built from tree:\w+ but the source is now tree:\w+')

    # ============================================= part 3: -Pure on a GIT tree (origin + commit)
    Write-Host "-- 3. Sync-LspServer.ps1 -Pure, git tree: origin and commit verified before npm" -ForegroundColor Cyan
    $upstream = Join-Path $root 'upstream\Clarion-Extension'
    New-Item -ItemType Directory -Force (Join-Path $upstream 'server\src') | Out-Null
    '{ "name": "up" }' | Set-Content (Join-Path $upstream 'package.json')
    'export const v = 1;' | Set-Content (Join-Path $upstream 'server\src\server.ts')
    $null = G -C $upstream init --quiet
    $null = G -C $upstream add -A
    $null = G -C $upstream commit --quiet -m c1
    $null = G -C $upstream tag vTEST
    $c1 = G -C $upstream rev-parse HEAD
    'export const v = 2;' | Set-Content (Join-Path $upstream 'server\src\server.ts')
    $null = G -C $upstream commit --quiet -am c2
    $c2 = G -C $upstream rev-parse HEAD
    # A look-alike "upstream": its URL contains Clarion-Extension, which the old check accepted.
    $evil = Join-Path $root 'evil\Clarion-Extension-fork'
    $null = G clone --quiet $upstream $evil

    $pure = Join-Path $root 'pure'
    $null = G clone --quiet $upstream $pure
    New-Item -ItemType Directory -Force (Join-Path $pure 'out\server\src') | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $pure 'out\server\src\server.js'), [System.Text.Encoding]::ASCII.GetBytes("console.log('git build');`n"))
    $before = Get-BuildCount

    Write-SyncManifest $upstream $null
    $null = G -C $pure remote set-url origin $evil
    $g1 = Invoke-Sync $pure
    Check "G1 origin is a look-alike, not source.repo: refused (exit 2), NO npm" (($g1.code -eq 2) -and ((Get-BuildCount) -eq $before) -and ($g1.text -match 'expected exactly'))
    $null = G -C $pure remote set-url origin $upstream

    Write-SyncManifest $upstream $c2.Substring(0, 8)
    $g2 = Invoke-Sync $pure
    Check "G2 tag names c1 but the pin says c2 (tag moved): refused (exit 2), NO npm" (($g2.code -eq 2) -and ((Get-BuildCount) -eq $before) -and ($g2.text -match 'moved upstream'))

    Write-SyncManifest $upstream $c1.Substring(0, 8)
    $g3 = Invoke-Sync $pure
    Check "G3 origin exact + HEAD = tag = pinned commit: builds (exit 0, npm ran)" (($g3.code -eq 0) -and ((Get-BuildCount) -eq $before + 1))
    if ($g3.code -ne 0) { Write-Host $g3.text }

    # ===================================================== part 4: installer Test-InstallerLspPin
    Write-Host "-- 4. installer\build-installer.ps1 Test-InstallerLspPin" -ForegroundColor Cyan
    foreach ($t in (Import-Functions (Join-Path $RootDir 'installer\build-installer.ps1') 'Get-FileSha256', 'Test-InstallerLspPin')) {
        . ([scriptblock]::Create($t))
    }
    $inst = Join-Path $root 'inst'
    $instBase = Join-Path $inst 'ClarionAssistant'
    $instJs = Join-Path $instBase '.lsp-build\vTEST\out\server\src\server.js'
    New-Item -ItemType Directory -Force (Split-Path $instJs) | Out-Null
    [System.IO.File]::WriteAllBytes($instJs, [System.Text.Encoding]::ASCII.GetBytes("console.log('ship me');`n"))
    $instHash = Get-Sha $instJs
    $instIss = Join-Path $inst 'fixture.iss'
    $instManifest = Join-Path $inst 'lsp-snapshot.json'
    function Write-Iss($tag) { [System.IO.File]::WriteAllText($instIss, "#define SrcBase SourcePath + `"..\ClarionAssistant`"`r`n#define SrcLsp SrcBase + `"\.lsp-build\$tag`"`r`n") }
    function Write-InstManifest($hash, $target = 'vTEST') {
        $m = [ordered]@{ targetPin = [pscustomobject]@{ tag = $target }; resolvedTag = 'vTEST' }
        if ($hash) { $m.resolvedServerSha256 = $hash }
        [System.IO.File]::WriteAllText($instManifest, (([pscustomobject]$m) | ConvertTo-Json))
    }
    function Invoke-Gate { Invoke-Captured { Test-InstallerLspPin $instIss $instManifest $instBase } }

    Write-Iss 'vTEST'; Write-InstManifest $instHash
    $r = Invoke-Gate
    Check "I1 matching hash: passes" (($r.ok -eq $true) -and ($r.text -match 'OK\s+bundled server\.js matches pin'))

    Add-Content -LiteralPath $instJs -Value '// tampered'
    $r = Invoke-Gate
    Check "I2 tampered server.js: FAILS, with esbuild-bundle / re-pin guidance" (($r.ok -eq $false) -and ($r.text -match 'does NOT match the pin') -and ($r.text -match 'esbuild bundle'))
    [System.IO.File]::WriteAllBytes($instJs, [System.Text.Encoding]::ASCII.GetBytes("console.log('ship me');`n"))

    Write-InstManifest $null
    $r = Invoke-Gate
    Check "I3 no resolvedServerSha256: FAILS (release path is strict)" (($r.ok -eq $false) -and ($r.text -match 'no resolvedServerSha256'))

    Write-InstManifest $instHash 'vNEXT'
    $r = Invoke-Gate
    Check "I4 targetPin.tag != resolvedTag (unsynced bump): FAILS" (($r.ok -eq $false) -and ($r.text -match 'never synced'))

    Write-InstManifest $instHash; Write-Iss 'vOLD'
    $r = Invoke-Gate
    Check "I5 .iss SrcLsp packages another tag: FAILS" (($r.ok -eq $false) -and ($r.text -match 'packages \.lsp-build\\vOLD'))
    Write-Iss 'vTEST'

    # The committed pin against the REAL installer script inputs, when this machine has the build.
    $realIss = Join-Path $RootDir 'installer\ClarionAssistant.iss'
    $realManifest = Join-Path $RepoDir 'lsp-server-sync\lsp-snapshot.json'
    $realTag = (Get-Content $realManifest -Raw | ConvertFrom-Json).resolvedTag
    $realJs = Join-Path $RepoDir ".lsp-build\$realTag\out\server\src\server.js"
    if (Test-Path -LiteralPath $realJs) {
        $r = Invoke-Captured { Test-InstallerLspPin $realIss $realManifest $RepoDir }
        Check "R  real .iss + committed manifest + this tree's .lsp-build\${realTag}: passes" ($r.ok -eq $true)
    } else {
        Write-Host "  note  no .lsp-build\$realTag here -- real-tree installer check not run (fixture cases above still apply)" -ForegroundColor DarkGray
    }
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
