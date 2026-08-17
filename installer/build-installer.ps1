# Build Clarion Assistant Installer
# Builds all components, then compiles the Inno Setup installer
# Optionally signs the output with Sectigo USB dongle
param(
    [switch]$SkipBuild,
    [switch]$Sign,
    [switch]$NoDocGraph,
    [switch]$AllowStaleBins,          # bypass the per-config version freshness gate (escape hatch only)
    [switch]$AllowMissingComponents   # ship even if ISCC reported "shipping WITHOUT ..." (escape hatch only)
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# ── Paths ──
$msbuild = $null
$searchPaths = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
)
foreach ($p in $searchPaths) {
    if (Test-Path $p) { $msbuild = $p; break }
}
if (-not $msbuild) {
    # Fall back to vswhere, which locates any VS/Build Tools install regardless of year/edition.
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vsInstall = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($vsInstall) {
            foreach ($candidate in @("$vsInstall\MSBuild\Current\Bin\MSBuild.exe", "$vsInstall\MSBuild\Current\Bin\amd64\MSBuild.exe")) {
                if (Test-Path $candidate) { $msbuild = $candidate; break }
            }
        }
    }
}
if (-not $msbuild) {
    Write-Error "MSBuild not found. Install Visual Studio 2022/2026 (or Build Tools)."
    exit 1
}

$innoSetup = $null
$innoSearchPaths = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
foreach ($p in $innoSearchPaths) {
    if (Test-Path $p) { $innoSetup = $p; break }
}
if (-not $innoSetup) {
    Write-Error "Inno Setup 6 not found. Checked: $($innoSearchPaths -join ', ')"
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

# --- Gate: every installer script must PARSE under Windows PowerShell 5.1 ---
#
# configure.ps1 SHIPS with the installer and the .iss runs it via powershell.exe, which on Windows
# is always Windows PowerShell 5.1 -- never pwsh. Anything 5.1 cannot handle therefore fails on the
# USER'S machine, after release, where it is most expensive. GH #190 was exactly that: a PS 6+ only
# parameter reached 5.1, threw, and the catch around it rewrote the user's ~/.claude/settings.json
# from an empty hashtable, destroying their hooks, model, statusLine and permissions on every install.
#
# This script itself had a sibling of the same bug (ticket 7cf3a895): saved as UTF-8 WITHOUT a BOM,
# 5.1 decoded it as CP1252, an em-dash inside a string literal became a U+201D -- which PowerShell
# treats as a STRING DELIMITER -- and the parse collapsed with a bogus "Missing closing '}'" pointing
# at innocent code. It parses fine under PS7, so nothing revealed it.
#
# Parse-only: this compiles the scripts, it does not run them. Cheap, and it fails the RELEASE rather
# than the customer.
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $ps51)) {
    Write-Warning "Windows PowerShell 5.1 not found at $ps51 - skipping the 5.1 parse gate."
} else {
    Write-Host "Checking installer scripts parse under Windows PowerShell 5.1..." -ForegroundColor Yellow
    $badScripts = @()
    foreach ($ps1 in (Get-ChildItem $scriptDir -Filter *.ps1 -File | Sort-Object Name)) {
        # Run the 5.1 PARSER in a real 5.1 host. Doing this in-process would prove nothing: the
        # whole failure mode is that PS7 parses these files happily.
        $probe = "`$e=`$null; [void][System.Management.Automation.Language.Parser]::ParseFile('$($ps1.FullName)',[ref]`$null,[ref]`$e); if (`$e.Count) { 'line ' + `$e[0].Extent.StartLineNumber + ': ' + `$e[0].Message } else { 'OK' }"
        $result = & $ps51 -NoProfile -Command $probe
        if ($result -ne 'OK') {
            $badScripts += "$($ps1.Name) -> $result"
            Write-Host ("  FAIL  {0,-34} {1}" -f $ps1.Name, $result) -ForegroundColor Red
        } else {
            Write-Host ("  ok    {0}" -f $ps1.Name)
        }
    }
    if ($badScripts.Count -gt 0) {
        Write-Error ("These installer scripts do not parse under Windows PowerShell 5.1, which is the host the installer runs them with:`n  {0}`nUsual causes: a PowerShell 6+ only construct, or a non-ASCII character inside a STRING LITERAL in a file saved as UTF-8 without a BOM (5.1 reads it as CP1252 and an em-dash turns into a quote character). Fix the script, or save it as UTF-8 WITH BOM." -f ($badScripts -join "`n  "))
        exit 1
    }
    Write-Host ""
}

# ── Step 1: Build ClarionAssistant ──
if (-not $SkipBuild) {
    Write-Host "Building ClarionAssistant..." -ForegroundColor Yellow
    & $msbuild "$repoRoot\ClarionAssistant\ClarionAssistant.csproj" `
        /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /v:minimal /nologo /restore
    if ($LASTEXITCODE -ne 0) { Write-Error "ClarionAssistant build failed"; exit 1 }
    Write-Host "  OK" -ForegroundColor Green

    # Build ClarionIndexer (VENDORED into the repo — GitHub #30 — not the old external H:\DevLaptop\ClarionLSP tree)
    # NOTE: no /p:Platform override here. ClarionIndexer.csproj only defines <OutputPath> for
    # Platform=x86 (its own default when Platform is unset) — forcing AnyCPU here matches neither
    # PropertyGroup condition, so MSBuild errors with "BaseOutputPath/OutputPath property is not
    # set". deploy.ps1 already builds it this way (Platform left unset); mirror that here.
    $indexerCsproj = "$repoRoot\ClarionAssistant\indexer\ClarionIndexer.csproj"
    if (Test-Path $indexerCsproj) {
        Write-Host "Building ClarionIndexer..." -ForegroundColor Yellow
        & $msbuild $indexerCsproj /p:Configuration=Debug /t:Build /v:minimal /nologo /restore
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

# ── Step 1b: Freshness gate — the per-config addin bins (bin\Debug-C10/C11/C11.1/C12) that the
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
        foreach ($cfg in 'C10','C11','C11.1','C12') {
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
        Write-Warning "Could not parse <FullVersion> from Version.props - skipping freshness gate."
    }
} else {
    Write-Warning "Version.props not found at $versionProps - skipping freshness gate."
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
    Write-Warning "clarion-assistant.ico not found - Inno Setup will use default icon."
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
# ingest_docs() writes the bundled DB to DocGraphService.GetDefaultDbPath(), i.e.
# %APPDATA%\ClarionAssistant\docgraph.db (Roaming — Environment.SpecialFolder.ApplicationData),
# NOT this installer folder. Auto-copy it here if present, instead of requiring a manual copy
# step every release.
$docGraphPath = Join-Path $scriptDir 'docgraph.db'
if (-not (Test-Path $docGraphPath) -and -not $NoDocGraph) {
    $defaultDocGraphPath = Join-Path $env:APPDATA 'ClarionAssistant\docgraph.db'
    if (Test-Path $defaultDocGraphPath) {
        Copy-Item $defaultDocGraphPath $docGraphPath -Force
        Write-Host "Copied docgraph.db from $defaultDocGraphPath" -ForegroundColor Green
    } else {
        Write-Warning "docgraph.db not found in installer directory or at $defaultDocGraphPath."
        Write-Warning "The DocGraph component will be empty. To include it:"
        Write-Warning "  1. Run ingest_docs() in Clarion Assistant"
        Write-Warning "  2. Re-run this script (it will auto-copy from $defaultDocGraphPath)"
        Write-Warning ""
        Write-Warning "Continuing without DocGraph DB..."
    }
}

# ── Step 5: Compile Inno Setup installer ──
Write-Host "`nCompiling installer..." -ForegroundColor Yellow

# Create output directory
$outputDir = Join-Path $scriptDir 'output'
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# Output is TEED, not just streamed: it has to stay on screen (this compile takes a
# minute and its progress is worth watching) while also being inspectable below.
& $innoSetup $issFile 2>&1 | Tee-Object -Variable isccOutput | Out-Host
$isccExit = $LASTEXITCODE

if ($isccExit -ne 0) {
    Write-Error "Inno Setup compilation failed"
    # Clean up temp file if created
    if (Test-Path (Join-Path $scriptDir 'ClarionAssistant.iss.tmp')) {
        Remove-Item (Join-Path $scriptDir 'ClarionAssistant.iss.tmp') -Force
    }
    exit 1
}

# ── Missing-component gate ──
# The .iss guards every optional source with an ISPP presence flag and announces absence
# with `#pragma message "... shipping WITHOUT ..."`. A pragma message does NOT affect
# ISCC's exit code -- "shipping WITHOUT the Clarion LSP server" and "Successful compile"
# appear in the same run, and the compile returns 0. So exit-code checking alone reports
# success for an installer that is quietly missing components users expect.
#
# That is the same failure that shipped from deploy.ps1 (ticket 0cd0b20c): a component
# silently absent from a build, evidenced by one line nobody reads. This is the installer
# half of it. Roughly ten sources are guarded this way -- node.exe, the bundled LSP,
# bin\Debug-C11.1, ClarionCOMBrowser, the three UltimateCOM sets, ComForClarion docs,
# ClarionCOM tooling, blank.dct and the Claude agents -- and ANY of them can go missing.
#
# Mirrors the existing stale-bins gate above: fail by default, name what is wrong, and
# provide one explicit override for people who genuinely want a partial build.
$missing = @($isccOutput | Where-Object { "$_" -match 'shipping WITHOUT' })
if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "Installer compiled, but $($missing.Count) component(s) were NOT included:" -ForegroundColor Red
    foreach ($m in $missing) { Write-Host "  $("$_".Trim())" -ForegroundColor Red }
    Write-Host ""
    if (-not $AllowMissingComponents) {
        Write-Host "The compile itself succeeded -- ISCC reports missing optional sources as warnings," -ForegroundColor Yellow
        Write-Host "not errors -- so this would otherwise have been reported as a clean build." -ForegroundColor Yellow
        Write-Host "Populate the missing source(s) and re-run, or pass -AllowMissingComponents if a" -ForegroundColor Yellow
        Write-Host "partial installer is genuinely what you want." -ForegroundColor Yellow
        if (Test-Path (Join-Path $scriptDir 'ClarionAssistant.iss.tmp')) {
            Remove-Item (Join-Path $scriptDir 'ClarionAssistant.iss.tmp') -Force
        }
        exit 1
    }
    Write-Host "-AllowMissingComponents was passed - shipping the partial installer anyway." -ForegroundColor Yellow
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
