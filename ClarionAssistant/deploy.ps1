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
    if ($env:CLARIONLSP_ROOT) { return $env:CLARIONLSP_ROOT }   # explicit override wins
    $syncScript = Join-Path $ProjectDir "lsp-server-sync\Sync-LspServer.ps1"
    $manifest   = Get-Content (Join-Path $ProjectDir "lsp-server-sync\lsp-snapshot.json") -Raw | ConvertFrom-Json
    $tag        = if ($manifest.resolvedTag) { $manifest.resolvedTag } else { $manifest.targetPin.tag }
    $pureDir    = Join-Path $ProjectDir (".lsp-build\" + $tag)
    if (-not (Test-Path (Join-Path $pureDir "out\server\src\server.js"))) {
        Write-Host "  INFO  pure LSP build for $tag missing — building via Sync-LspServer.ps1 -Pure ..." -ForegroundColor Cyan
        & $syncScript -Pure -Tag $tag
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
# Pure v0.9.6 runtime deps only — NO better-sqlite3/bindings/file-uri-to-path (those backed the retired
# CodeGraph overlay). With better-sqlite3 absent the #42 ABI check below self-skips ("module not deployed").
$LspNodeModules = @(
    "vscode-jsonrpc"
    "vscode-languageserver"
    "vscode-languageserver-protocol"
    "vscode-languageserver-textdocument"
    "vscode-languageserver-types"
    "xml2js"
    "sax"
    "xmlbuilder"
)

# SQLite DLLs with FTS5 support (from lib/sqlite-fts5 in project)
# NOTE: Deployed AFTER indexer items to ensure ClarionAssistant's version wins
$SqliteFts5Dir = Join-Path $ProjectDir "lib\sqlite-fts5"

# --- Build ---
if (-not $NoBuild) {
    Write-Host "Restoring packages..." -ForegroundColor Cyan
    & $MSBuild $ProjectFile /t:Restore /p:Configuration=Debug /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "Restore failed." -ForegroundColor Red; exit 1 }

    foreach ($ver in $TargetVersions) {
        Write-Host ""
        if (-not $ResolvedRoots.ContainsKey($ver)) {
            Write-Host "SKIP  build for Clarion $ver (no install found)" -ForegroundColor DarkGray
            continue
        }
        Write-Host "Building for Clarion $ver ($($ResolvedRoots[$ver]))..." -ForegroundColor Cyan
        & $MSBuild $ProjectFile /p:Configuration=Debug /p:ClarionVersion=$ver /p:ClarionRoot="$($ResolvedRoots[$ver])" /v:minimal
        if ($LASTEXITCODE -ne 0) { Write-Host "Build failed for Clarion $ver." -ForegroundColor Red; exit 1 }
        Write-Host "Build succeeded for Clarion $ver." -ForegroundColor Green
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

        if (Test-Path $LspSourceDir) {
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
Write-Host "All done." -ForegroundColor Green
