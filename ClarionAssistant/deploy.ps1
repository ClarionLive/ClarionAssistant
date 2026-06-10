# ClarionAssistant Deploy Script
# Builds and deploys the addin for Clarion 10, 11, 12, or all.
# Usage: .\deploy.ps1 [-Version 10|11|12|all] [-NoBuild] [-Kill]

param(
    [ValidateSet("10","11","12","all")]
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
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild `
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

# Indexer build output (separate project, shares source files with ClarionCodeGraph)
$IndexerDir    = "H:\DevLaptop\ClarionLSP\indexer"
$IndexerFile   = "$IndexerDir\ClarionIndexer.csproj"
$IndexerOutput = "$IndexerDir\bin\Debug"

# Version-specific config
$Versions = @{
    "12" = @{ Root = "C:\Clarion12";                           Output = "bin\Debug-C12" }
    "11" = @{ Root = @("d:\Clarion11.1EE", "C:\Clarion11-13372"); Output = "bin\Debug-C11" }
    "10" = @{ Root = @("C:\Clarion10", "C:\Clarion10v8");    Output = "bin\Debug-C10" }
}

function Resolve-BuildOutputDir {
    param(
        [string]$ProjectDir,
        [string]$PreferredOutput
    )

    $preferred = Join-Path $ProjectDir $PreferredOutput
    if (Test-Path $preferred) { return $preferred }

    $fallback = Join-Path $ProjectDir "bin\Debug-C"
    if (Test-Path $fallback) { return $fallback }

    return $preferred
}

# Which versions to process
if ($Version -eq "all") {
    $TargetVersions = @("12", "11", "10")
} else {
    $TargetVersions = @($Version)
}

# Files and folders to deploy
$Items = @(
    "ClarionAssistant.dll"
    "ClarionAssistant.pdb"
    "ClarionAssistant.addin"
    "Microsoft.Web.WebView2.Core.dll"
    "Microsoft.Web.WebView2.WinForms.dll"
    "Microsoft.Web.WebView2.Wpf.dll"
    "WebView2Loader.dll"
    "Terminal"
    "TaskLifecycleBoard"
    "runtimes"
)

# LSP Server (Clarion Language Server)
$LspSourceDir = "H:\DevLaptop\ClarionLSP"
$LspNodeModules = @(
    "vscode-jsonrpc"
    "vscode-languageserver"
    "vscode-languageserver-protocol"
    "vscode-languageserver-textdocument"
    "vscode-languageserver-types"
    "xml2js"
    "sax"
    "xmlbuilder"
    "better-sqlite3"
    "bindings"
    "file-uri-to-path"
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
        Write-Host "Building for Clarion $ver..." -ForegroundColor Cyan
        & $MSBuild $ProjectFile /p:Configuration=Debug /p:ClarionVersion=$ver /v:minimal
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
    $cfg         = $Versions[$ver]
    $BuildOutput = Resolve-BuildOutputDir -ProjectDir $ProjectDir -PreferredOutput $cfg.Output

    # Support single root or array of roots
    $Roots = @($cfg.Root) | ForEach-Object { $_ }

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

            # Copy bundled node.exe (so end users don't need Node.js installed)
            $NodeExeSrc = "C:\Program Files\nodejs\node.exe"
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
