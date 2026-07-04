# Build Clarion Assistant Installer
# Builds all components, then compiles the Inno Setup installer
# Optionally signs the output with Sectigo USB dongle
param(
    [switch]$SkipBuild,
    [switch]$Sign,
    [switch]$NoDocGraph,
    [switch]$AllowStaleBins   # bypass the per-config version freshness gate (escape hatch only)
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# ── Paths ──
$msbuild = $null
$searchPaths = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
)
foreach ($p in $searchPaths) {
    if (Test-Path $p) { $msbuild = $p; break }
}
if (-not $msbuild) {
    Write-Error "MSBuild not found. Install Visual Studio 2022."
    exit 1
}

$innoSetup = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $innoSetup)) {
    Write-Error "Inno Setup 6 not found at $innoSetup"
    exit 1
}

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter 'signtool.exe' -Recurse |
    Where-Object { $_.FullName -like '*x64*' } |
    Sort-Object { $_.Directory.Name } -Descending |
    Select-Object -First 1 -ExpandProperty FullName

# Sectigo EV cert: "Kennewick Computer Company". Target it explicitly by SHA1
# thumbprint — `signtool /a` would silently fall back to a self-signed cert
# in CurrentUser\My if the EV dongle is unplugged, producing an "Unknown
# Publisher" installer. If the cert expires or is reissued, look up the new
# thumbprint with:
#   Get-ChildItem Cert:\CurrentUser\My | Where Subject -like '*Kennewick*' | Select Thumbprint
$signCertThumbprint = '85C3D22C215029A9F59EFF775720446F3B12FE3A'

Write-Host "=== Clarion Assistant Installer Build ===" -ForegroundColor Cyan
Write-Host "MSBuild:    $msbuild"
Write-Host "Inno Setup: $innoSetup"
Write-Host "SignTool:   $signtool"
Write-Host ""

# ── Step 1: Build ClarionAssistant ──
if (-not $SkipBuild) {
    Write-Host "Building ClarionAssistant..." -ForegroundColor Yellow
    & $msbuild "$repoRoot\ClarionAssistant\ClarionAssistant.csproj" `
        /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /v:minimal /nologo /restore
    if ($LASTEXITCODE -ne 0) { Write-Error "ClarionAssistant build failed"; exit 1 }
    Write-Host "  OK" -ForegroundColor Green

    # Build ClarionIndexer (VENDORED into the repo — GitHub #30 — not the old external H:\DevLaptop\ClarionLSP tree)
    $indexerCsproj = "$repoRoot\ClarionAssistant\indexer\ClarionIndexer.csproj"
    if (Test-Path $indexerCsproj) {
        Write-Host "Building ClarionIndexer..." -ForegroundColor Yellow
        & $msbuild $indexerCsproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /v:minimal /nologo /restore
        if ($LASTEXITCODE -ne 0) { Write-Warning "ClarionIndexer build failed (non-fatal)" }
        else { Write-Host "  OK" -ForegroundColor Green }
    }

    # Build COMforClarion
    $comCsproj = 'H:\DevLaptop\ClarionIdeCOMPane\ClarionCOMBrowser\ClarionCOMBrowser.csproj'
    if (Test-Path $comCsproj) {
        Write-Host "Building COMforClarion..." -ForegroundColor Yellow
        & $msbuild $comCsproj /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /v:minimal /nologo /restore
        if ($LASTEXITCODE -ne 0) { Write-Warning "COMforClarion build failed (non-fatal)" }
        else { Write-Host "  OK" -ForegroundColor Green }
    }
}

# ── Step 1b: Freshness gate — the per-config addin bins (bin\Debug-C10/C11/C12) that the
# .iss packages are built OUT-OF-BAND by deploy.ps1, NOT by this script's build step. With
# -SkipBuild it's easy to silently ship a STALE config (e.g. C11 left at 5.1.612 while C12 is
# 5.2.691 — happened for the 5.2 release). Assert every present config bin matches Version.props
# FullVersion before we sign or package anything. Override with -AllowStaleBins.
$versionProps = "$repoRoot\ClarionAssistant\Version.props"
if (Test-Path $versionProps) {
    $expected = if ((Get-Content $versionProps -Raw) -match '<FullVersion>\s*(.+?)\s*</FullVersion>') { $Matches[1] } else { $null }
    if ($expected) {
        Write-Host "`nChecking per-config addin freshness (expected $expected)..." -ForegroundColor Yellow
        $stale = @(); $found = 0
        foreach ($cfg in 'C10','C11','C12') {
            $dll = "$repoRoot\ClarionAssistant\bin\Debug-$cfg\ClarionAssistant.dll"
            if (-not (Test-Path $dll)) { Write-Host "  --   ${cfg}: no bin (won't ship this config)" -ForegroundColor DarkGray; continue }
            $found++
            # FileVersion is 4-part (5.2.691.0); compare the first three against FullVersion.
            $fv = (Get-Item $dll).VersionInfo.FileVersion
            $fv3 = ($fv -split '\.')[0..2] -join '.'
            if ($fv3 -eq $expected) { Write-Host "  OK   ${cfg}: $fv" -ForegroundColor Green }
            else { Write-Host "  FAIL ${cfg}: $fv (expected $expected)" -ForegroundColor Red; $stale += "${cfg}=$fv3" }
        }
        if ($found -eq 0) {
            Write-Error "No bin\Debug-C* addin builds found. Run deploy.ps1 (per Clarion version) to populate them before building the installer."
            exit 1
        }
        if ($stale.Count -gt 0 -and -not $AllowStaleBins) {
            Write-Error ("Stale addin bin(s): {0}. Expected {1}. These are built by deploy.ps1, not this script. Rebuild the affected config(s), e.g.:`n  msbuild ClarionAssistant.csproj /p:Configuration=Debug /p:ClarionVersion=<10|11|12> /p:BuildingInsideVisualStudio=true`nThen re-run. (Use -AllowStaleBins to override.)" -f ($stale -join ', '), $expected)
            exit 1
        }
        if ($stale.Count -gt 0) { Write-Warning "Shipping stale bins ($($stale -join ', ')) because -AllowStaleBins was passed." }
    } else {
        Write-Warning "Could not parse <FullVersion> from Version.props — skipping freshness gate."
    }
} else {
    Write-Warning "Version.props not found at $versionProps — skipping freshness gate."
}

# ── Step 2: Sign DLLs before packaging (if requested) ──
if ($Sign -and $signtool) {
    Write-Host "`nSigning binaries..." -ForegroundColor Yellow
    $filesToSign = @(
        "$repoRoot\ClarionAssistant\bin\Debug\ClarionAssistant.dll",
        "$repoRoot\ClarionAssistant\indexer\bin\Debug\clarion-indexer.exe",
        'H:\DevLaptop\ClarionIdeCOMPane\ClarionCOMBrowser\bin\Debug\ClarionCOMBrowser.dll'
    )
    foreach ($f in $filesToSign) {
        if (Test-Path $f) {
            Write-Host "  Signing $([IO.Path]::GetFileName($f))..."
            & $signtool sign /sha1 $signCertThumbprint /fd sha256 /tr http://timestamp.sectigo.com /td sha256 /d "Clarion Assistant" $f
            if ($LASTEXITCODE -ne 0) { Write-Warning "Failed to sign $f" }
        }
    }
}

# ── Step 3: Ensure icon exists (create placeholder if needed) ──
$iconPath = Join-Path $scriptDir 'clarion-assistant.ico'
if (-not (Test-Path $iconPath)) {
    Write-Warning "clarion-assistant.ico not found — Inno Setup will use default icon."
    Write-Warning "Place your .ico file at: $iconPath"
    # Remove SetupIconFile from the .iss to avoid build error
    $issContent = Get-Content (Join-Path $scriptDir 'ClarionAssistant.iss') -Raw
    $issContent = $issContent -replace 'SetupIconFile=.*\r?\n', ''
    Set-Content (Join-Path $scriptDir 'ClarionAssistant.iss.tmp') $issContent
    $issFile = Join-Path $scriptDir 'ClarionAssistant.iss.tmp'
} else {
    $issFile = Join-Path $scriptDir 'ClarionAssistant.iss'
}

# ── Step 4: Check for DocGraph DB ──
$docGraphPath = Join-Path $scriptDir 'docgraph.db'
if (-not (Test-Path $docGraphPath) -and -not $NoDocGraph) {
    Write-Warning "docgraph.db not found in installer directory."
    Write-Warning "The DocGraph component will be empty. To include it:"
    Write-Warning "  1. Run ingest_docs() in Clarion Assistant"
    Write-Warning "  2. Copy the generated docgraph.db to: $scriptDir"
    Write-Warning ""
    Write-Warning "Continuing without DocGraph DB..."
}

# ── Step 5: Compile Inno Setup installer ──
Write-Host "`nCompiling installer..." -ForegroundColor Yellow

# Create output directory
$outputDir = Join-Path $scriptDir 'output'
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

& $innoSetup $issFile
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed"
    # Clean up temp file if created
    if (Test-Path (Join-Path $scriptDir 'ClarionAssistant.iss.tmp')) {
        Remove-Item (Join-Path $scriptDir 'ClarionAssistant.iss.tmp') -Force
    }
    exit 1
}

# Clean up temp file if created
if (Test-Path (Join-Path $scriptDir 'ClarionAssistant.iss.tmp')) {
    Remove-Item (Join-Path $scriptDir 'ClarionAssistant.iss.tmp') -Force
}

# ── Step 6: Sign the installer itself ──
$installerExe = Get-ChildItem $outputDir -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($Sign -and $signtool -and $installerExe) {
    Write-Host "`nSigning installer..." -ForegroundColor Yellow
    & $signtool sign /sha1 $signCertThumbprint /fd sha256 /tr http://timestamp.sectigo.com /td sha256 /d "Clarion Assistant Installer" $installerExe.FullName
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to sign installer"
        exit 1
    }

    # Verify the signature came from the right cert. signtool /a used to silently
    # fall back to a self-signed test cert when the EV dongle was unplugged, so
    # check the subject matches before we ever distribute. Don't use
    # `signtool verify /pa` — its LocalMachine root store can lack AAA
    # Certificate Services and report false negatives.
    $sig = Get-AuthenticodeSignature $installerExe.FullName
    if ($sig.SignerCertificate -and $sig.SignerCertificate.Subject -like '*Kennewick Computer Company*') {
        Write-Host "  OK (signed by $($sig.SignerCertificate.Subject.Split(',')[0]))" -ForegroundColor Green
    } else {
        $actual = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '(no signer cert)' }
        Write-Error "Installer signed with WRONG certificate: $actual. Expected Kennewick Computer Company. Plug in the Sectigo EV dongle and rebuild."
        exit 1
    }
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Green
if ($installerExe) {
    Write-Host "Installer: $($installerExe.FullName)"
    Write-Host "Size: $([math]::Round($installerExe.Length / 1MB, 2)) MB"
}
