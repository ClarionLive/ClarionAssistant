# ClarionAssistant Deploy Script
# Builds and deploys the addin for Clarion 10, 11, 11.1, 12, or all.
# Usage: .\deploy.ps1 [-Version 10|11|11.1|12|all] [-NoBuild] [-Kill]

param(
    [ValidateSet("10","11","11.1","12","all")]
    [string]$Version = "all",  # Which Clarion version(s) to build/deploy
    [switch]$NoBuild,          # Skip build, just copy
    [switch]$Kill              # Kill Clarion IDE before deploying
)

$ErrorActionPreference = "Stop"

# Locate MSBuild.exe without hardcoding a Visual Studio version/edition.
# Order: vswhere (covers VS 2019/2022/18+, any edition) -> common install paths.
function Resolve-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                            -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }

    # Fallback: scan known roots if vswhere is unavailable.
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $root) { continue }
        $candidate = Get-ChildItem -Path (Join-Path $root "Microsoft Visual Studio") `
                        -Filter MSBuild.exe -Recurse -ErrorAction SilentlyContinue |
                        Where-Object { $_.FullName -match "\\Current\\Bin\\MSBuild\.exe$" } |
                        Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "MSBuild.exe not found. Install Visual Studio with the MSBuild component, or set `$MSBuild manually."
}

$ProjectDir  = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir "ClarionAssistant.csproj"
$MSBuild     = Resolve-MSBuild

# Indexer build output. VENDORED into this repo as indexer/ (GitHub #30) — self-contained,
# no longer built from the external H:\DevLaptop\ClarionLSP\indexer tree. Override with
# $env:CLARIONINDEXER_DIR only if you keep the indexer somewhere else.
$IndexerDir    = if ($env:CLARIONINDEXER_DIR) { $env:CLARIONINDEXER_DIR } else { Join-Path $ProjectDir "indexer" }
$IndexerFile   = "$IndexerDir\ClarionIndexer.csproj"
$IndexerOutput = "$IndexerDir\bin\Debug"

# Version-specific config. "Root" entries are last-resort fallback paths only — actual
# resolution goes registry -> these fallbacks -> drive-root glob scan (Resolve-ClarionRoot).
# 11 and 11.1 are DISTINCT Clarion releases (confirmed via registry: separate install dirs,
# not aliases of each other) and must never share a build/deploy target — their binding DLLs
# (CWBinding.dll etc, see ClarionAssistant.csproj) are version-specific, so building against
# one and shipping into the other risks an ABI mismatch.
$Versions = @{
    "12"   = @{ RegistryKeys = @("Clarion12");              Fallbacks = @("C:\Clarion12");                          GlobPatterns = @("Clarion12*");            Output = "bin\Debug-C12" }
    "11.1" = @{ RegistryKeys = @("Clarion11.1","Clarion111"); Fallbacks = @("d:\Clarion11.1EE", "C:\Clarion11.1");   GlobPatterns = @("Clarion11.1*","Clarion111*"); Output = "bin\Debug-C11.1" }
    "11"   = @{ RegistryKeys = @("Clarion11");              Fallbacks = @("C:\Clarion11-13372", "C:\Clarion11");    GlobPatterns = @("Clarion11","Clarion11-*"); Output = "bin\Debug-C11" }
    "10"   = @{ RegistryKeys = @("Clarion10");              Fallbacks = @("C:\Clarion10", "C:\Clarion10v8");        GlobPatterns = @("Clarion10*");            Output = "bin\Debug-C10" }
}

# Resolve the install root for a Clarion version: registry (authoritative, modern Clarion
# versions register a "root" value under SoftVelocity\Clarion<key>) -> known fallback paths
# (other dev machines) -> drive-root glob scan (machines where neither of the above hit).
function Resolve-ClarionRoot {
    param(
        [string[]]$RegistryKeys,
        [string[]]$Fallbacks,
        [string[]]$GlobPatterns
    )

    function Test-ClarionRoot([string]$path) {
        if (-not $path) { return $false }
        return Test-Path (Join-Path $path "bin\ICSharpCode.Core.dll")
    }

    $regHives = @(
        "HKLM:\SOFTWARE\WOW6432Node\SoftVelocity",
        "HKLM:\SOFTWARE\SoftVelocity",
        "HKCU:\SOFTWARE\SoftVelocity"
    )
    foreach ($hive in $regHives) {
        foreach ($key in $RegistryKeys) {
            $val = (Get-ItemProperty -Path "$hive\$key" -Name root -ErrorAction SilentlyContinue).root
            if ($val) {
                $val = $val.TrimEnd('\')
                if (Test-ClarionRoot $val) { return $val }
            }
        }
    }

    foreach ($p in $Fallbacks) {
        if (Test-ClarionRoot $p) { return $p }
    }

    $drives = (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
                Where-Object { Test-Path $_.Root }).Root
    foreach ($drive in $drives) {
        foreach ($pattern in $GlobPatterns) {
            $hit = Get-ChildItem -Path $drive -Directory -Filter $pattern -ErrorAction SilentlyContinue |
                    Where-Object { Test-ClarionRoot $_.FullName } |
                    Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }

    return $null
}

function Resolve-BuildOutputDir {
    param(
        [string]$ProjectDir,
        [string]$PreferredOutput
    )

    # NOTE: deliberately no fallback to the generic bin\Debug-C folder. That folder is whatever
    # was last built by a plain `msbuild /p:Configuration=Debug` with no ClarionVersion pinned
    # (e.g. an ad-hoc build-installer.ps1 run) - it could be built against ANY Clarion version's
    # binding DLLs. Falling back to it here previously caused a real incident: a Clarion-12-built
    # DLL got silently deployed into a live Clarion 11.1 install because bin\Debug-C11.1 didn't
    # exist yet. Missing the real per-version folder must be a clean skip, not a guess.
    return Join-Path $ProjectDir $PreferredOutput
}

# Which versions to process
if ($Version -eq "all") {
    $TargetVersions = @("12", "11.1", "11", "10")
} else {
    $TargetVersions = @($Version)
}

# Resolve install roots up front (needed by both the build and deploy loops below, and
# independent of -NoBuild). A version with no resolvable install is skipped, not fatal —
# previously a missing version aborted the whole run because MSBuild's own hardcoded
# ClarionRoot default in Directory.Build.props errored out mid-build.
$ResolvedRoots = @{}
foreach ($ver in $TargetVersions) {
    $cfg  = $Versions[$ver]
    $root = Resolve-ClarionRoot -RegistryKeys $cfg.RegistryKeys -Fallbacks $cfg.Fallbacks -GlobPatterns $cfg.GlobPatterns
    if ($root) {
        $ResolvedRoots[$ver] = $root
        Write-Host "Clarion ${ver}: $root" -ForegroundColor DarkGray
    } else {
        Write-Host "Clarion ${ver}: no install found (registry / known paths / drive scan) - will skip" -ForegroundColor DarkGray
    }
}

# Files and folders to deploy
$Items = @(
    "ClarionAssistant.dll"
    "ClarionAssistant.pdb"
    "ClarionAssistant.addin"
    # Shared ClarionLsp contract assembly — our addin references IClarionLanguageClient /
    # ClarionLspLocator (SharedLspBridge) so this DLL MUST ship in our addin folder, or the
    # CLR can't resolve the type and the ENTIRE addin silently fails to load (Tools menu empty).
    # SharpDevelop does NOT resolve it from ClarionLsp's own folder into ours. Required for BOTH
    # the shared path AND the no-ClarionLsp fallback (the assembly is absent otherwise).
    "ClarionLsp.Contracts.dll"
    "Microsoft.Web.WebView2.Core.dll"
    "Microsoft.Web.WebView2.WinForms.dll"
    "Microsoft.Web.WebView2.Wpf.dll"
    "WebView2Loader.dll"
    # PdfPig — in-process PDF text extraction for DocGraph ingestion (#167). ALL of these ship:
    # the five System.*/Microsoft.Bcl shims are NOT inbox on .NET Framework 4.8, they arrive via
    # PdfPig's own package dependencies, and omitting any one of them fails at RUNTIME (the first
    # PDF import throws FileNotFoundException) rather than at build — so a missing entry here would
    # look exactly like the silent "no documents found" bug this replaced. Keep in sync with the
    # matching block in installer\ClarionAssistant.iss.
    "UglyToad.PdfPig.dll"
    "UglyToad.PdfPig.Core.dll"
    "UglyToad.PdfPig.DocumentLayoutAnalysis.dll"
    "UglyToad.PdfPig.Fonts.dll"
    "UglyToad.PdfPig.Package.dll"
    "UglyToad.PdfPig.Tokenization.dll"
    "UglyToad.PdfPig.Tokens.dll"
    "Microsoft.Bcl.HashCode.dll"
    "System.Buffers.dll"
    "System.Memory.dll"
    "System.Numerics.Vectors.dll"
    "System.Runtime.CompilerServices.Unsafe.dll"
    # DEPLOY INVARIANT: Terminal\ is copied as a whole folder, which is the ONLY safe way to ship the
    # Monaco editor pages. monaco-embeditor.html and monaco-diff.html have a HARD runtime dependency on
    # Terminal\clarion-language.js (the shared Clarion grammar + folding registration, task 04dd97f9) —
    # if either HTML is hot-copied WITHOUT clarion-language.js, the editor fails to start (the pages now
    # detect this and show a "Failed to load clarion-language.js" message instead of hanging). Never
    # single-file hot-copy either HTML without also copying clarion-language.js.
    "Terminal"
    "TaskLifecycleBoard"
    "runtimes"
)

# LSP Server (Clarion Language Server) — #40: PURE upstream msarson/Clarion-Extension at the pinned tag,
# with NO CodeGraph overlay. CodeGraph go-to-def / references / completion are served C#-side
# (SharedLspBridge + CodeGraphProvider), so the bundled server is stock upstream. The pure build is a clean
# tag checkout produced by lsp-server-sync\Sync-LspServer.ps1 -Pure, cached under .lsp-build\<tag>.
# $env:CLARIONLSP_ROOT still overrides (dev escape hatch) if you deliberately want a custom server tree.
function Resolve-LspBuild {
    if ($env:CLARIONLSP_ROOT) {
        # Loud, because this is the one path that bypasses the tag-in-path pin check below: whatever
        # is in that tree ships, while lsp-snapshot.json goes on asserting resolvedCommit. A consumer
        # then has no way to tell which server they actually received.
        Write-Host "  WARN  CLARIONLSP_ROOT is set — shipping an UNPINNED LSP from $env:CLARIONLSP_ROOT" -ForegroundColor Yellow
        Write-Host "  WARN  lsp-snapshot.json will NOT describe what ships. Do not use this for a release build." -ForegroundColor Yellow
        return $env:CLARIONLSP_ROOT
    }
    $syncScript = Join-Path $ProjectDir "lsp-server-sync\Sync-LspServer.ps1"
    $manifest   = Get-Content (Join-Path $ProjectDir "lsp-server-sync\lsp-snapshot.json") -Raw | ConvertFrom-Json
    $tag        = if ($manifest.resolvedTag) { $manifest.resolvedTag } else { $manifest.targetPin.tag }
    $pureDir    = Join-Path $ProjectDir (".lsp-build\" + $tag)
    if (-not (Test-Path (Join-Path $pureDir "out\server\src\server.js"))) {
        Write-Host "  INFO  pure LSP build for $tag missing — building via Sync-LspServer.ps1 -Pure ..." -ForegroundColor Cyan
        # | Out-Host is load-bearing, do NOT drop it. A PowerShell function returns EVERYTHING written
        # to its output stream, not just what `return` names. Without this, every line git and npm emit
        # (checkout notice, "added 255 packages", the whole tsc/copy-data transcript) became part of
        # this function's return value, so $LspSourceDir came back as an ARRAY of output lines with the
        # real path merely last.
        #
        # That failed in the worst possible way -- silently, and only on the FIRST deploy in a fresh
        # tree, the one run that has to build. `if (Test-Path $LspSourceDir)` passed, because a
        # non-empty array is truthy, so node.exe still copied and the block looked alive. But
        # "$LspSourceDir\out\server" interpolated the whole array space-joined into a nonsense path, so
        # the server copy was skipped with one DarkGray line. The guard below missed it too: it tests
        # $pureDir directly, which is still a clean string inside the function. Second runs worked
        # because the build already existed and this branch never ran.
        #
        # Net effect before the fix: a release built from a fresh worktree in one pass shipped an addin
        # with NO language server. Out-Host writes the transcript to the console without putting it on
        # the pipeline. Ticket 0cd0b20c.
        & $syncScript -Pure -Tag $tag | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Host "  WARN  pure LSP build failed (exit $LASTEXITCODE) — LSP copy will be skipped." -ForegroundColor Yellow }
        # Loud guard: on a from-scratch build the out/ is created mid-run; if it's not visible yet the copy
        # below would SILENTLY skip and ship an addin with NO server. Fail loudly so the installer never does.
        elseif (-not (Test-Path (Join-Path $pureDir "out\server\src\server.js"))) {
            Write-Host "  WARN  pure build reported success but out\server is not visible yet — RE-RUN deploy.ps1 to copy the LSP (first-run timing)." -ForegroundColor Yellow
        }
    }
    return $pureDir
}
$LspSourceDir = Resolve-LspBuild

# --- Verify the LSP about to ship IS the pinned one --------------------------------------------
# Previously nothing checked this. lsp-snapshot.json could assert resolvedCommit while a different
# server.js was copied (CLARIONLSP_ROOT override, hand-edited tree, an out/ built from another tag),
# and the manifest is the ONLY thing a consumer can inspect to know what they got. Assert before any
# copy so "what shipped" and "what is pinned" cannot silently diverge.

# Run git and return its stdout, or $null if it failed for ANY reason. Do NOT replace this with a
# bare `git ... 2>$null`.
#
# Under Windows PowerShell 5.1, redirecting a NATIVE command's stderr wraps the output in a
# NativeCommandError record, and $ErrorActionPreference='Stop' (set at the top of this file) makes
# that record TERMINATING. So `$x = (git ... 2>$null); if (-not $x) { warn; return }` never reaches
# the soft-fail branch -- the script dies on the git line. Reproduced on 5.1.19041.6456 and on
# 5.1.26100.8972; the identical code under pwsh 7 returns empty and continues, which is why the bug
# looks like it isn't there when tested from a pwsh prompt.
#
# That is the exact failure this file's LSP handling is supposed to avoid: a contributor whose pure
# build fails, or whose CLARIONLSP_ROOT is a plain unpacked server tree with no .git, would see the
# WARN and then have the whole deploy abort anyway -- before a single Clarion version builds.
#
# Relaxing $ErrorActionPreference locally makes the NativeCommandError non-terminating; 2>&1 keeps
# git's stderr off the console; the ErrorRecord filter keeps it out of the return value; the
# try/catch covers git being absent from PATH entirely (CommandNotFoundException terminates
# regardless of the preference).
function Invoke-GitQuiet([string[]]$GitArgs) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git @GitArgs 2>&1
        if ($LASTEXITCODE -ne 0) { return $null }
        $clean = $out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
        if (-not $clean) { return $null }
        return (($clean -join "`n").Trim())
    } catch {
        return $null
    } finally {
        $ErrorActionPreference = $prevEap
    }
}

# Returns $true when the LSP may ship, $false when it must not. It does NOT exit: an LSP-copy
# problem is not a reason to abandon the Clarion builds. This used to `exit 7`, which aborted all
# four Clarion targets AND the C# addin before a single one was built -- cutting straight across
# the "a build failure for ONE Clarion release must not block shipping to the others" rule the
# final summary below is built around. The caller skips just the LSP copy instead, so every version
# still deploys and keeps whatever server it already had, and the run ends non-zero so nobody reads
# it as clean.
# Lowercase hex sha256 of a file's BYTES — see the matching helper in Sync-LspServer.ps1. Read the
# bytes explicitly: this hash identifies the exact artifact that ships, so nothing about it may
# depend on an encoding or line-ending interpretation.
function Get-FileSha256($path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        return -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    } finally { $sha.Dispose() }
}

function Test-LspPin($sourceDir) {
    $manifest = Get-Content (Join-Path $ProjectDir "lsp-server-sync\lsp-snapshot.json") -Raw | ConvertFrom-Json

    # --- Primary check: the ARTIFACT about to be copied ---------------------------------------
    # The commit check below verifies the SOURCE checkout, which is not what ships. out/ is
    # gitignored, survives `git checkout --force`, and the sync skips rebuilding when a server.js
    # already exists -- so a tree whose HEAD matches the pin exactly can still hold an overlay build
    # or a build from another tag. Hash the file that actually gets copied.
    $pinnedHash = $manifest.resolvedServerSha256
    $shippingJs = Join-Path $sourceDir "out\server\src\server.js"
    if (-not $pinnedHash) {
        # Manifests written before resolvedServerSha256 existed. Say so rather than implying the
        # artifact was checked; the commit check below still runs.
        Write-Host "  WARN  manifest has no resolvedServerSha256 — the shipped server.js is NOT verified." -ForegroundColor Yellow
        Write-Host "        Re-run lsp-server-sync\Sync-LspServer.ps1 -Pure to record it." -ForegroundColor Yellow
    } elseif (-not (Test-Path -LiteralPath $shippingJs)) {
        # Not a failure here: the copy below reports "build output not found" on its own.
        Write-Host "  WARN  no built server.js at $shippingJs — nothing to verify." -ForegroundColor Yellow
    } else {
        $actualHash = Get-FileSha256 $shippingJs
        if ($actualHash -ne $pinnedHash) {
            if ($env:CLARIONLSP_ROOT) {
                Write-Host "  WARN  shipped server.js hashes $($actualHash.Substring(0,16))… but the pin says $($pinnedHash.Substring(0,16))… (CLARIONLSP_ROOT override in effect)." -ForegroundColor Yellow
                return $true
            }
            Write-Host "  FAIL  shipped server.js does NOT match the pin." -ForegroundColor Red
            Write-Host "        built  : $actualHash" -ForegroundColor Red
            Write-Host "        pinned : $pinnedHash" -ForegroundColor Red
            Write-Host "        The source tree may sit at the right commit while out/ was built from another." -ForegroundColor Red
            Write-Host "        Delete $sourceDir\out and re-run lsp-server-sync\Sync-LspServer.ps1 -Pure." -ForegroundColor Red
            Write-Host "        The LSP copy will be SKIPPED for every version; the rest of the deploy continues." -ForegroundColor Red
            return $false
        }
        Write-Host "  OK    shipped server.js matches pin: $($pinnedHash.Substring(0,16))…" -ForegroundColor Green
    }

    # --- Secondary check: which SOURCE the artifact came from ----------------------------------
    # Kept alongside the hash: the hash says "this is the right file", the commit says "and it came
    # from the right tag", and a mismatch between the two is itself worth seeing.
    $pinned = $manifest.resolvedCommit
    if (-not $pinned) {
        Write-Host "  WARN  manifest has no resolvedCommit — cannot verify the LSP source tree." -ForegroundColor Yellow
        return $true
    }
    $head = Invoke-GitQuiet @('-C', $sourceDir, 'rev-parse', '--short', 'HEAD')
    if (-not $head) {
        Write-Host "  WARN  $sourceDir is not a git tree — cannot verify it matches pin $pinned." -ForegroundColor Yellow
        return $true
    }
    # Prefix compare: `git rev-parse --short` auto-scales its length, so 7- and 8-char forms of the
    # same commit must still count as a match.
    if (-not ($head.StartsWith($pinned) -or $pinned.StartsWith($head))) {
        if ($env:CLARIONLSP_ROOT) {
            Write-Host "  WARN  LSP source tree is at $head but the pin says $pinned (CLARIONLSP_ROOT override in effect)." -ForegroundColor Yellow
            return $true
        }
        Write-Host "  FAIL  LSP source tree is at $head but lsp-snapshot.json pins $pinned." -ForegroundColor Red
        Write-Host "        Re-run lsp-server-sync\Sync-LspServer.ps1 -Pure, or bump the pin deliberately." -ForegroundColor Red
        Write-Host "        The LSP copy will be SKIPPED for every version; the rest of the deploy continues." -ForegroundColor Red
        return $false
    }
    Write-Host "  OK    LSP source tree matches pin: $pinned ($($manifest.resolvedTag))" -ForegroundColor Green
    return $true
}
$LspPinOK = Test-LspPin $LspSourceDir
# Pure v1.0.0 runtime deps only — NO better-sqlite3/bindings/file-uri-to-path (those backed the retired
# CodeGraph overlay). With better-sqlite3 absent the #42 ABI check below self-skips ("module not deployed").
# iconv-lite (+ its dep safer-buffer) is NEW at v1.0.0: UnicodeDiagnostics requires it in the EAGER
# startup graph — without it the server dies at startup with MODULE_NOT_FOUND (GitHub #77 re-pin).
$LspNodeModules = @(
    "vscode-jsonrpc"
    "vscode-languageserver"
    "vscode-languageserver-protocol"
    "vscode-languageserver-textdocument"
    "vscode-languageserver-types"
    "xml2js"
    "sax"
    "xmlbuilder"
    "iconv-lite"
    "safer-buffer"
)

# SQLite DLLs with FTS5 support (from lib/sqlite-fts5 in project)
# NOTE: Deployed AFTER indexer items to ensure ClarionAssistant's version wins
$SqliteFts5Dir = Join-Path $ProjectDir "lib\sqlite-fts5"

# Versions whose build failed this run — excluded from the deploy loop below and reported at the end,
# so "built but NOT deployed" is never silently indistinguishable from "deployed".
$FailedBuilds = @()

# --- Build ---
if (-not $NoBuild) {
    Write-Host "Restoring packages..." -ForegroundColor Cyan
    & $MSBuild $ProjectFile /t:Restore /p:Configuration=Debug /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "Restore failed." -ForegroundColor Red; exit 1 }

    # A build failure for ONE Clarion release must not block shipping to the others. This loop used to
    # `exit 1` on the first failure — and since the deploy loop runs AFTER every build, a single broken
    # target meant NOTHING was deployed at all, while the console showed the other three building fine.
    # The failure line named only the broken version, so it read as "three of four went out" when the
    # true answer was zero, and the symptom (a stale deployed DLL) is easy to mistake for a code
    # regression. Collect failures instead, deploy every version that DID build, and report the split
    # explicitly at the end.
    foreach ($ver in $TargetVersions) {
        Write-Host ""
        if (-not $ResolvedRoots.ContainsKey($ver)) {
            Write-Host "SKIP  build for Clarion $ver (no install found)" -ForegroundColor DarkGray
            continue
        }
        Write-Host "Building for Clarion $ver ($($ResolvedRoots[$ver]))..." -ForegroundColor Cyan
        & $MSBuild $ProjectFile /p:Configuration=Debug /p:ClarionVersion=$ver /p:ClarionRoot="$($ResolvedRoots[$ver])" /v:minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build FAILED for Clarion $ver — it will NOT be deployed." -ForegroundColor Red
            $FailedBuilds += $ver
            continue
        }
        Write-Host "Build succeeded for Clarion $ver." -ForegroundColor Green
    }

    # Every requested target failed → there is nothing to deploy, so stop here rather than walking a
    # deploy loop that would skip every entry and still print "All done."
    if ($FailedBuilds.Count -gt 0 -and $FailedBuilds.Count -eq @($TargetVersions | Where-Object { $ResolvedRoots.ContainsKey($_) }).Count) {
        Write-Host ""
        Write-Host "No version built successfully — nothing deployed." -ForegroundColor Red
        exit 1
    }

    if (Test-Path $IndexerFile) {
        Write-Host ""
        Write-Host "Building indexer..." -ForegroundColor Cyan
        & $MSBuild $IndexerFile /p:Configuration=Debug /v:minimal
        if ($LASTEXITCODE -ne 0) { Write-Host "Indexer build failed." -ForegroundColor Red; exit 1 }
        Write-Host "Indexer build succeeded." -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "Skipping indexer build (project not found: $IndexerFile)" -ForegroundColor Yellow
    }
}

# --- Kill Clarion IDE if requested ---
if ($Kill) {
    $proc = Get-Process -Name "Clarion" -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Host "Stopping Clarion IDE..." -ForegroundColor Yellow
        $proc | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
}

# --- Deploy each version ---
foreach ($ver in $TargetVersions) {
    if (-not $ResolvedRoots.ContainsKey($ver)) {
        Write-Host ""
        Write-Host "=== Skipping Clarion $ver deploy (no install found) ===" -ForegroundColor DarkGray
        continue
    }
    if ($FailedBuilds -contains $ver) {
        Write-Host ""
        Write-Host "=== Skipping Clarion $ver deploy (its build FAILED — see above) ===" -ForegroundColor Red
        continue
    }
    $cfg         = $Versions[$ver]
    $BuildOutput = Resolve-BuildOutputDir -ProjectDir $ProjectDir -PreferredOutput $cfg.Output
    $Roots       = @($ResolvedRoots[$ver])

    # Same no-guessing principle as Resolve-BuildOutputDir: a config that was never built
    # (-NoBuild, or a fresh checkout) must be a clean skip — otherwise the item loop below
    # creates the live addin folder and fills it with indexer/LSP/SQLite but NO addin DLL.
    if (-not (Test-Path $BuildOutput)) {
        Write-Host ""
        Write-Host "=== Skipping Clarion $ver deploy (build output missing: $BuildOutput) ===" -ForegroundColor DarkGray
        continue
    }

    foreach ($root in $Roots) {
        $DeployDir = Join-Path $root "accessory\addins\ClarionAssistant"

        Write-Host ""
        Write-Host "=== Deploying Clarion $ver -> $root ===" -ForegroundColor Magenta
        Write-Host "  From: $BuildOutput" -ForegroundColor DarkGray
        Write-Host "  To:   $DeployDir" -ForegroundColor DarkGray

        if (-not (Test-Path $root)) {
            Write-Host "  SKIP  $root (not found)" -ForegroundColor DarkGray
            continue
        }

        if (-not (Test-Path $DeployDir)) {
            New-Item -Path $DeployDir -ItemType Directory | Out-Null
        }

        $copied = 0
        $failed = 0

        foreach ($item in $Items) {
            $src = Join-Path $BuildOutput $item
            $dst = Join-Path $DeployDir $item

            if (-not (Test-Path $src)) {
                Write-Host "  SKIP  $item (not found in build output)" -ForegroundColor DarkGray
                continue
            }

            try {
                if (Test-Path $src -PathType Container) {
                    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
                    Copy-Item $src $dst -Recurse -Force
                } else {
                    Copy-Item $src $dst -Force
                }
                Write-Host "  OK    $item" -ForegroundColor Green
                $copied++
            }
            catch {
                Write-Host "  FAIL  $item - $($_.Exception.Message)" -ForegroundColor Red
                $failed++
            }
        }

        # --- Deploy indexer ---
        $IndexerItems = @(
            "clarion-indexer.exe"
            "clarion-indexer.pdb"
            "System.Data.SQLite.dll"
            "x86"
        )

        if (Test-Path $IndexerOutput) {
            foreach ($item in $IndexerItems) {
                $src = "$IndexerOutput\$item"
                $dst = Join-Path $DeployDir $item

                if (-not (Test-Path $src)) {
                    Write-Host "  SKIP  $item (not found in indexer output)" -ForegroundColor DarkGray
                    continue
                }

                try {
                    if (Test-Path $src -PathType Container) {
                        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
                        Copy-Item $src $dst -Recurse -Force
                    } else {
                        Copy-Item $src $dst -Force
                    }
                    Write-Host "  OK    $item (indexer)" -ForegroundColor Green
                    $copied++
                }
                catch {
                    Write-Host "  FAIL  $item - $($_.Exception.Message)" -ForegroundColor Red
                    $failed++
                }
            }
        } else {
            Write-Host "  SKIP  indexer output (not found: $IndexerOutput)" -ForegroundColor DarkGray
        }

        # --- Deploy SQLite FTS5 DLLs (after indexer, so correct version wins) ---
        $SqliteItems = @{
            "System.Data.SQLite.dll" = Join-Path $SqliteFts5Dir "System.Data.SQLite.dll"
            "SQLite.Interop.dll"     = Join-Path $SqliteFts5Dir "SQLite.Interop.dll"
        }
        foreach ($name in $SqliteItems.Keys) {
            $src = $SqliteItems[$name]
            if (Test-Path $src) {
                try {
                    Copy-Item $src (Join-Path $DeployDir $name) -Force
                    if ($name -eq "SQLite.Interop.dll") {
                        $x86Dir = Join-Path $DeployDir "x86"
                        if (-not (Test-Path $x86Dir)) { New-Item $x86Dir -ItemType Directory | Out-Null }
                        Copy-Item $src (Join-Path $x86Dir $name) -Force
                    }
                    Write-Host "  OK    $name (FTS5)" -ForegroundColor Green
                    $copied++
                } catch {
                    Write-Host "  FAIL  $name - $($_.Exception.Message)" -ForegroundColor Red
                    $failed++
                }
            } else {
                Write-Host "  SKIP  $name (not found in lib/sqlite-fts5)" -ForegroundColor DarkGray
            }
        }

        # --- Deploy LSP Server ---
        $LspDestDir = Join-Path $DeployDir "lsp-server"

        if ($LspPinOK -and (Test-Path $LspSourceDir)) {
            # Copy compiled server JS + common shared code
            foreach ($outDir in @("out\server", "out\common")) {
                $LspOutSrc = "$LspSourceDir\$outDir"
                if (Test-Path $LspOutSrc) {
                    $LspOutDst = Join-Path $LspDestDir $outDir
                    if (Test-Path $LspOutDst) { Remove-Item $LspOutDst -Recurse -Force }
                    New-Item -Path $LspOutDst -ItemType Directory -Force | Out-Null
                    Copy-Item "$LspOutSrc\*" $LspOutDst -Recurse -Force
                    Write-Host "  OK    lsp-server\$outDir" -ForegroundColor Green
                    $copied++
                }
            }

            if (-not (Test-Path "$LspSourceDir\out\server")) {
                Write-Host "  SKIP  lsp-server (ClarionLSP build output not found)" -ForegroundColor DarkGray
            }

            # Copy bundled node.exe (so end users don't need Node.js installed).
            # Resolve portably (GitHub #30): explicit $env:CLARIONLSP_NODE, else node on PATH,
            # else the legacy default install location.
            $NodeExeSrc =
                if ($env:CLARIONLSP_NODE) { $env:CLARIONLSP_NODE }
                elseif (Get-Command node -ErrorAction SilentlyContinue) { (Get-Command node).Source }
                else { "C:\Program Files\nodejs\node.exe" }
            if (Test-Path $NodeExeSrc) {
                # $LspDestDir is only created by the server-output copy above; when that was SKIPped
                # (no LSP build output) a fresh target has no lsp-server dir and this copy would die.
                New-Item -Path $LspDestDir -ItemType Directory -Force | Out-Null
                Copy-Item $NodeExeSrc (Join-Path $LspDestDir "node.exe") -Force
                Write-Host "  OK    lsp-server\node.exe" -ForegroundColor Green
                $copied++
            } else {
                Write-Host "  SKIP  node.exe (not found at $NodeExeSrc)" -ForegroundColor DarkGray
            }

            # Copy required node_modules
            foreach ($mod in $LspNodeModules) {
                $modSrc = "$LspSourceDir\node_modules\$mod"
                $modDst = Join-Path $LspDestDir "node_modules\$mod"
                if (Test-Path $modSrc) {
                    if (Test-Path $modDst) { Remove-Item $modDst -Recurse -Force }
                    Copy-Item $modSrc $modDst -Recurse -Force
                    Write-Host "  OK    lsp-server\node_modules\$mod" -ForegroundColor Green
                    $copied++
                }
            }

            # #40 pure: purge RETIRED CodeGraph-overlay modules that a prior (codegraph) deploy may have
            # left in the dest — the node_modules dir isn't wiped wholesale, so stale better-sqlite3 etc.
            # would otherwise linger (bloat + a misleading "codegraph present" signal in the shipped addin).
            foreach ($stale in @("better-sqlite3", "bindings", "file-uri-to-path")) {
                $staleDst = Join-Path $LspDestDir "node_modules\$stale"
                if (Test-Path $staleDst) {
                    Remove-Item $staleDst -Recurse -Force
                    Write-Host "  OK    lsp-server purge stale $stale (retired codegraph dep)" -ForegroundColor DarkYellow
                }
            }

            # --- ABI assertion: bundled better-sqlite3 must load under bundled node.exe (GitHub #42) ---
            # The prebuilt better-sqlite3 .node addon is compiled for a specific Node ABI/arch. If the
            # bundled node.exe drifts from the build machine's Node, the LSP's CodeGraphBridge silently
            # self-disables ("better-sqlite3 not available") on end-user installs. Assert the EXACT
            # end-user path here: the just-deployed node.exe loading better-sqlite3 by relative require.
            $DeployedNode = Join-Path $LspDestDir "node.exe"
            $DeployedBsq3 = Join-Path $LspDestDir "node_modules\better-sqlite3"
            if ((Test-Path $DeployedNode) -and (Test-Path $DeployedBsq3)) {
                $abiProbe = "var D=require('better-sqlite3');var db=new D(':memory:');db.prepare('select 1 as x').get();db.close();process.stdout.write('OK');"
                Push-Location $LspDestDir
                try {
                    $abiOut  = & $DeployedNode -e $abiProbe 2>&1
                    $abiExit = $LASTEXITCODE
                } finally {
                    Pop-Location
                }
                if ($abiExit -eq 0) {
                    Write-Host "  OK    lsp-server better-sqlite3 ABI (loads under bundled node.exe)" -ForegroundColor Green
                    $copied++
                } else {
                    Write-Host "  FAIL  better-sqlite3 ABI mismatch (GitHub #42): bundled node.exe cannot load the prebuilt addon." -ForegroundColor Red
                    Write-Host "        CodeGraphBridge would self-disable on end-user installs. Rebuild better-sqlite3 against the bundled node's ABI/arch." -ForegroundColor Red
                    Write-Host "        node: $DeployedNode" -ForegroundColor DarkGray
                    Write-Host "        $abiOut" -ForegroundColor DarkGray
                    $failed++
                }
            } elseif (Test-Path $DeployedNode) {
                Write-Host "  SKIP  better-sqlite3 ABI check (module not deployed)" -ForegroundColor DarkGray
            }
        } elseif (-not $LspPinOK) {
            # Deliberate: shipping a server that does not match the pin is worse than shipping none,
            # because lsp-snapshot.json would go on asserting a commit the consumer did not receive.
            # The version keeps whatever lsp-server it already had; the final summary exits non-zero.
            Write-Host "  SKIP  lsp-server (does not match the pin — see FAIL above)" -ForegroundColor Yellow
        } else {
            Write-Host "  SKIP  lsp-server (ClarionLSP not found)" -ForegroundColor DarkGray
        }

        # --- Version summary ---
        if ($failed -eq 0) {
            Write-Host "  $root deploy complete: $copied items." -ForegroundColor Green
        } else {
            Write-Host "  $root deploy: $copied copied, $failed failed." -ForegroundColor Yellow
        }
    }
}

# --- Final summary ---
Write-Host ""
if ($FailedBuilds.Count -gt 0 -or -not $LspPinOK) {
    # Never let a partial run exit 0 with a bare "All done." — that is what made a stale deployed DLL
    # look like a successful deploy. Name what did NOT ship, and fail the exit code so a caller (CI,
    # the installer, another script) can't read this run as clean.
    Write-Host "Done, WITH FAILURES." -ForegroundColor Yellow
    if ($FailedBuilds.Count -gt 0) {
        Write-Host "  NOT deployed (build failed): $($FailedBuilds -join ', ')" -ForegroundColor Red
    }
    if (-not $LspPinOK) {
        Write-Host "  NOT deployed (pin mismatch): lsp-server — every version kept the LSP it already had." -ForegroundColor Red
        Write-Host "  Re-run lsp-server-sync\Sync-LspServer.ps1 -Pure, or bump the pin, then re-run this script." -ForegroundColor Red
    }
    Write-Host "  Everything else deployed normally." -ForegroundColor Yellow
    exit 1
}
Write-Host "All done." -ForegroundColor Green
