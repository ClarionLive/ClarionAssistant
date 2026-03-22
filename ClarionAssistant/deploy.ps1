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

# --- Build ---
if (-not $NoBuild) {
    Write-Host "Restoring packages..." -ForegroundColor Cyan
    & $MSBuild $ProjectFile /t:Restore /p:Configuration=Debug /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "Restore failed." -ForegroundColor Red; exit 1 }

    Write-Host "Building..." -ForegroundColor Cyan
    & $MSBuild $ProjectFile /p:Configuration=Debug /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

    Write-Host "Build succeeded." -ForegroundColor Green
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

# --- Summary ---
Write-Host ""
if ($failed -eq 0) {
    Write-Host "Deploy complete: $copied items copied." -ForegroundColor Green
} else {
    Write-Host "Deploy finished with errors: $copied copied, $failed failed." -ForegroundColor Yellow
    Write-Host "If files are locked, use -Kill to stop the IDE first, or restart it manually." -ForegroundColor Yellow
    exit 1
}
