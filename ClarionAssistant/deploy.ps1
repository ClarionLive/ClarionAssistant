# ClarionAssistant Deploy Script
# Builds and deploys the addin to the Clarion IDE addins folder.
# Usage: .\deploy.ps1 [-NoBuild] [-Kill]

param(
    [switch]$NoBuild,  # Skip build, just copy
    [switch]$Kill      # Kill Clarion IDE before deploying
)

$ErrorActionPreference = "Stop"

$ProjectDir  = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir "ClarionAssistant.csproj"
$BuildOutput = Join-Path $ProjectDir "bin\Debug"
$DeployDir   = "C:\Clarion12\accessory\addins\ClarionAssistant"
$MSBuild     = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

# Indexer build output (separate project, shares source files with ClarionCodeGraph)
$IndexerDir  = "H:\DevLaptop\ClarionLSP\indexer"
$IndexerFile = Join-Path $IndexerDir "ClarionIndexer.csproj"
$IndexerOutput = Join-Path $IndexerDir "bin\Debug"

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

# SQLite DLLs with FTS5 support (from lib/sqlite-fts5 in project)
# NOTE: Deployed AFTER indexer items to ensure ClarionAssistant's version wins
$SqliteFts5Dir = Join-Path $ProjectDir "lib\sqlite-fts5"

# --- Build ---
if (-not $NoBuild) {
    Write-Host "Restoring packages..." -ForegroundColor Cyan
    & $MSBuild $ProjectFile /t:Restore /p:Configuration=Debug /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "Restore failed." -ForegroundColor Red; exit 1 }

    Write-Host "Building..." -ForegroundColor Cyan
    & $MSBuild $ProjectFile /p:Configuration=Debug /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

    Write-Host "Build succeeded." -ForegroundColor Green

    Write-Host "Building indexer..." -ForegroundColor Cyan
    & $MSBuild $IndexerFile /p:Configuration=Debug /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "Indexer build failed." -ForegroundColor Red; exit 1 }
    Write-Host "Indexer build succeeded." -ForegroundColor Green
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

# --- Deploy ---
if (-not (Test-Path $DeployDir)) {
    New-Item -Path $DeployDir -ItemType Directory | Out-Null
}

Write-Host "Deploying to $DeployDir ..." -ForegroundColor Cyan
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
            # Directory - mirror it
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

foreach ($item in $IndexerItems) {
    $src = Join-Path $IndexerOutput $item
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
            # Also copy interop to x86 subfolder
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

# --- Summary ---
Write-Host ""
if ($failed -eq 0) {
    Write-Host "Deploy complete: $copied items copied." -ForegroundColor Green
} else {
    Write-Host "Deploy finished with errors: $copied copied, $failed failed." -ForegroundColor Yellow
    Write-Host "If files are locked, use -Kill to stop the IDE first, or restart it manually." -ForegroundColor Yellow
    exit 1
}
