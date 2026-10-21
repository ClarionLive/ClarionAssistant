# Build Clarion Assistant Installer
# Builds all components, then compiles the Inno Setup installer
# Optionally signs the output with Sectigo USB dongle
param(
    [switch]$SkipBuild,
    [switch]$Sign,
    [switch]$NoDocGraph,
    [switch]$AllowStaleBins,          # bypass the per-config version freshness gate (escape hatch only)
    [switch]$AllowMissingComponents,  # ship even if ISCC reported "shipping WITHOUT ..." (escape hatch only)
    [switch]$AllowUnpinnedLsp         # package an LSP that fails the pin gate (escape hatch only; never publish)
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

# -- Step 0b: LSP pin gate -- the bundled server.js must be the one lsp-snapshot.json pins --------
# The .iss only checks that server.js EXISTS (HaveLsp), so without this an overlay build, a build
# from another tag, or a hand-edited file shipped to every user with nothing saying so. This is the
# release path, so it is stricter than deploy.ps1 (which lets a local dev deploy warn and continue):
#   - targetPin.tag must equal resolvedTag (a staged pin bump that was never synced is refused),
#   - the .iss #define SrcLsp must point at .lsp-build\<that tag> (what ISCC actually packages),
#   - resolvedServerSha256 must be PRESENT, and must equal the sha256 of that server.js.
# A MISSING server.js is left to the missing-component gate after ISCC (it has its own override).
# Override: -AllowUnpinnedLsp (escape hatch only; never for a published release).

# Lowercase hex sha256 of a file's bytes -- the same digest as Get-FileHash -Algorithm SHA256, in the
# lowercase form lsp-snapshot.json stores. Identical to the helper in deploy.ps1 and
# Sync-LspServer.ps1, so all three format it the same way.
function Get-FileSha256($path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        return -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    } finally { $sha.Dispose() }
}

# Returns $true when the LSP the installer will package matches the pin (or is absent -- see above),
# $false otherwise. Prints why. Pure function of its three paths, so a harness can call it directly.
function Test-InstallerLspPin($issPath, $manifestPath, $srcBase) {
    $ok = $true
    $manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $targetTag   = $manifest.targetPin.tag
    $resolvedTag = $manifest.resolvedTag
    $tag = if ($resolvedTag) { $resolvedTag } else { $targetTag }

    if ($targetTag -and $resolvedTag -and ($targetTag -ne $resolvedTag)) {
        Write-Host "  FAIL  lsp-snapshot.json targets $targetTag but is still resolved to $resolvedTag -- the pin bump was never synced." -ForegroundColor Red
        Write-Host "        Run ClarionAssistant\lsp-server-sync\Sync-LspServer.ps1 -Pure and commit lsp-snapshot.json." -ForegroundColor Red
        $ok = $false
    }

    $issText = [System.IO.File]::ReadAllText($issPath)
    $m = [regex]::Match($issText, '(?m)^\s*#define\s+SrcLsp\s+SrcBase\s*\+\s*"\\\.lsp-build\\([^"\\]+)"')
    if (-not $m.Success) {
        Write-Host "  FAIL  cannot find '#define SrcLsp SrcBase + ""\.lsp-build\<tag>""' in $issPath -- cannot tell which LSP ISCC packages." -ForegroundColor Red
        return $false
    }
    $issTag = $m.Groups[1].Value
    if ($issTag -ne $tag) {
        Write-Host "  FAIL  ClarionAssistant.iss packages .lsp-build\$issTag but lsp-snapshot.json pins $tag." -ForegroundColor Red
        Write-Host "        Bump '#define SrcLsp' in installer\ClarionAssistant.iss to .lsp-build\$tag (see lsp-server-sync\README.md)." -ForegroundColor Red
        $ok = $false
    }

    $pinnedHash = $manifest.resolvedServerSha256
    if (-not $pinnedHash) {
        Write-Host "  FAIL  lsp-snapshot.json has no resolvedServerSha256 -- the bundled server.js cannot be verified." -ForegroundColor Red
        Write-Host "        Run Sync-LspServer.ps1 -Pure and commit lsp-snapshot.json. (deploy.ps1 only warns on this; a release may not.)" -ForegroundColor Red
        return $false
    }

    $serverJs = Join-Path $srcBase (".lsp-build\" + $issTag + "\out\server\src\server.js")
    if (-not (Test-Path -LiteralPath $serverJs)) {
        Write-Host "  WARN  no server.js at $serverJs -- nothing to hash; the missing-component gate after ISCC decides." -ForegroundColor Yellow
        return $ok
    }
    $actual = Get-FileSha256 $serverJs
    if ($actual -ne $pinnedHash) {
        Write-Host "  FAIL  bundled server.js does NOT match the pin." -ForegroundColor Red
        Write-Host "        file   : $serverJs" -ForegroundColor Red
        Write-Host "        built  : $actual" -ForegroundColor Red
        Write-Host "        pinned : $pinnedHash" -ForegroundColor Red
        Write-Host "        server.js is upstream's full esbuild bundle, so ANY difference in what was built changes it." -ForegroundColor Red
        Write-Host "        Wrong build: delete that tree's out\ and re-run Sync-LspServer.ps1 -Pure. Legitimate rebuild" -ForegroundColor Red
        Write-Host "        that no longer reproduces the pinned bytes: re-run -Pure on the machine that owns the pin" -ForegroundColor Red
        Write-Host "        and commit the new resolvedServerSha256." -ForegroundColor Red
        return $false
    }
    if ($ok) { Write-Host "  OK    bundled server.js matches pin $tag ($($pinnedHash.Substring(0,16))...)" -ForegroundColor Green }
    return $ok
}

Write-Host "Checking the bundled LSP against lsp-snapshot.json..." -ForegroundColor Yellow
$lspPinOk = Test-InstallerLspPin (Join-Path $scriptDir 'ClarionAssistant.iss') `
                                 (Join-Path $repoRoot 'ClarionAssistant\lsp-server-sync\lsp-snapshot.json') `
                                 (Join-Path $repoRoot 'ClarionAssistant')
if (-not $lspPinOk) {
    if ($AllowUnpinnedLsp) {
        Write-Warning "Packaging an LSP that does not match lsp-snapshot.json because -AllowUnpinnedLsp was passed. Do NOT publish this installer."
    } else {
        Write-Error "LSP pin gate failed (see FAIL above). Fix the pin, or pass -AllowUnpinnedLsp for a local, never-published build."
        exit 1
    }
}
Write-Host ""

# ── Step 1: Build ClarionAssistant ──
if (-not $SkipBuild) {
    Write-Host "Building ClarionAssistant..." -ForegroundColor Yellow
    & $msbuild "$repoRoot\ClarionAssistant\ClarionAssistant.csproj" `
        /p:Configuration=Debug /p:Platform=AnyCPU /t:Build /v:minimal /nologo /restore
    if ($LASTEXITCODE -ne 0) { Write-Error "ClarionAssistant build failed"; exit 1 }
    Write-Host "  OK" -ForegroundColor Green

    # Build ClarionIndexer (VENDORED into the repo — GitHub #30 — not the old external H:\DevLaptop\ClarionLSP tree)
    # NOTE: no /p:Platform override here. ClarionIndexer.csproj only defines <OutputPath> for
    # Platform=AnyCPU (its own default when Platform is unset; AnyCPU with Prefer32Bit off since the
    # 583abdec review) - forcing another platform matches neither PropertyGroup condition, so MSBuild
    # errors with "BaseOutputPath/OutputPath property is not set". deploy.ps1 builds it the same way.
    #
    # FATAL since ticket 583abdec: the addin and the standalone MCP server run EVERY index in this exe
    # (64-bit, out of the 32-bit IDE), so a failed build would ship a stale indexer that every Reindex uses.
    $indexerCsproj = "$repoRoot\ClarionAssistant\indexer\ClarionIndexer.csproj"
    if (Test-Path $indexerCsproj) {
        Write-Host "Building ClarionIndexer..." -ForegroundColor Yellow
        & $msbuild $indexerCsproj /p:Configuration=Debug /t:Build /v:minimal /nologo /restore
        if ($LASTEXITCODE -ne 0) { Write-Error "ClarionIndexer build failed"; exit 1 }

    # Build the standalone MCP server (ticket d051fbd1) - the editor-agnostic tools as their own
    # stdio process, for Clarion developers who work in Sublime/VS Code rather than the IDE.
    #
    # Platform=x86 is REQUIRED, unlike the indexer: this project references the vendored 32-bit
    # System.Data.SQLite and its x86 native interop.
    #
    # A failure here is FATAL, deliberately, where the indexer's is a warning. If this build
    # fails the freshness gate below refuses the stale exe anyway - failing here just says so at
    # the point it happened, instead of 40 lines later with a version number as the only clue.
    $mcpCsproj = "$repoRoot\ClarionAssistant\mcp-server\ClarionMcpServer.csproj"
    if (Test-Path $mcpCsproj) {
        Write-Host "Building ClarionMcpServer..." -ForegroundColor Yellow
        & $msbuild $mcpCsproj /p:Configuration=Debug /p:Platform=x86 /t:Build /v:minimal /nologo /restore
        if ($LASTEXITCODE -ne 0) { Write-Error "ClarionMcpServer build failed"; exit 1 }
    }
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
# AssemblyFullVersion before we sign or package anything. Override with -AllowStaleBins.
$versionProps = "$repoRoot\ClarionAssistant\Version.props"
if (Test-Path $versionProps) {
    $propsRaw = Get-Content $versionProps -Raw
    # FullVersion (5.8.2) is the RELEASED version -- it goes to ISCC as MyAppVersion below.
    $release  = if ($propsRaw -match '<FullVersion>\s*(.+?)\s*</FullVersion>') { $Matches[1] } else { $null }
    # AssemblyFullVersion (5.8.2.1165) is what the gate compares against, and the distinction is
    # load-bearing. The gate used to take the first three parts of each DLL's FileVersion and
    # compare them to FullVersion. That only worked because FullVersion USED to carry the
    # auto-incrementing build counter, so any stale bin differed. Now FullVersion is fixed for the
    # whole life of a patch version, so a bin from an earlier build of the SAME patch would compare
    # EQUAL and ship -- the gate would keep running, keep printing OK, and stop catching the one
    # thing it exists to catch (C11 left at 5.1.612 while C12 was 5.2.691, the 5.2 release).
    # Comparing the full 4-part FileVersion against AssemblyFullVersion restores the discrimination,
    # because the build counter still moves on every compile.
    $expected = if ($propsRaw -match '<AssemblyFullVersion>\s*(.+?)\s*</AssemblyFullVersion>') { $Matches[1] } else { $null }
    if ($expected) {
        Write-Host "`nChecking per-config addin freshness (expected $expected)..." -ForegroundColor Yellow
        $stale = @(); $found = 0
        foreach ($cfg in 'C10','C11','C11.1','C12') {
            $dll = "$repoRoot\ClarionAssistant\bin\Debug-$cfg\ClarionAssistant.dll"
            if (-not (Test-Path $dll)) { Write-Host "  --   ${cfg}: no bin (won't ship this config)" -ForegroundColor DarkGray; continue }
            $found++
            # FileVersion is 4-part (5.8.2.1165); compare it WHOLE against AssemblyFullVersion.
            $fv = (Get-Item $dll).VersionInfo.FileVersion
            if ($fv -eq $expected) { Write-Host "  OK   ${cfg}: $fv" -ForegroundColor Green }
            else { Write-Host "  FAIL ${cfg}: $fv (expected $expected)" -ForegroundColor Red; $stale += "${cfg}=$fv" }
        }
        if ($found -eq 0) {
            Write-Error "No bin\Debug-C* addin builds found. Run deploy.ps1 (per Clarion version) to populate them before building the installer."
            exit 1
        }

        # clarion-mcp-server.exe is gated too, and it needs to be. It carries the 59 editor-agnostic
        # tools, it is shipped to all four Clarion configs from ONE build, and -SkipBuild skips the
        # step above that rebuilds it - so without this check a stale exe ships silently and every
        # standalone user gets yesterday's tool set with nothing to indicate it.
        #
        # It reports 0.0.0.0 unless Properties\AssemblyVersion.cs was present at compile time (it is
        # generated by the addin build, not source-controlled). That is treated as stale rather than
        # excused: an unversioned binary is exactly the one whose provenance cannot be established.
        $mcpExe = "$repoRoot\ClarionAssistant\mcp-server\bin\Debug\clarion-mcp-server.exe"
        if (-not (Test-Path $mcpExe)) {
            Write-Error "clarion-mcp-server.exe not found at $mcpExe. Build ClarionMcpServer.csproj (Debug|x86) before packaging."
            exit 1
        }
        $mcpVer = (Get-Item $mcpExe).VersionInfo.FileVersion
        if ($mcpVer -eq $expected) {
            Write-Host "  OK   mcp-server: $mcpVer" -ForegroundColor Green
        } elseif ($AllowStaleBins) {
            Write-Warning "Shipping stale mcp-server ($mcpVer, expected $expected) because -AllowStaleBins was passed."
        } else {
            Write-Error "Stale clarion-mcp-server.exe: $mcpVer (expected $expected). Rebuild it:`n  msbuild ClarionAssistant\mcp-server\ClarionMcpServer.csproj /p:Configuration=Debug /p:Platform=x86`n(A version of 0.0.0.0 means Properties\AssemblyVersion.cs was missing when it compiled - build the addin first. Use -AllowStaleBins to override.)"
            exit 1
        }
        if ($stale.Count -gt 0 -and -not $AllowStaleBins) {
            Write-Error ("Stale addin bin(s): {0}. Expected {1}. These are built by deploy.ps1, not this script. Rebuild the affected config(s), e.g.:`n  msbuild ClarionAssistant.csproj /p:Configuration=Debug /p:ClarionVersion=<10|11|12> /p:BuildingInsideVisualStudio=true`nThen re-run. (Use -AllowStaleBins to override.)" -f ($stale -join ', '), $expected)
            exit 1
        }
        if ($stale.Count -gt 0) { Write-Warning "Shipping stale bins ($($stale -join ', ')) because -AllowStaleBins was passed." }
    } else {
        Write-Warning "Could not parse <AssemblyFullVersion> from Version.props - skipping freshness gate."
    }
} else {
    Write-Warning "Version.props not found at $versionProps - skipping freshness gate."
}

# ── Step 1c: the indexer the addin runs every index in must run 64-bit wherever Windows is 64-bit, with its
# x64 interop (ticket 583abdec). A 32-bit-only exe from an old build would put large solutions back under a
# 2-4 GB cap; a missing x64\SQLite.Interop.dll makes every Reindex fail at start.
#
# Review D2: the indexer builds AnyCPU with Prefer32Bit OFF, so it runs 64-bit on 64-bit Windows and still
# starts on 32-bit Windows. Its PE header then says i386 (0x014C) like a 32-bit exe - what tells them apart is
# the CLR header's flags. Get-PeArchitecture reads both; pure functions of a file, so a harness calls them
# (tests\InstallerIndexerGate.Test.ps1).

# 'x64', 'ARM64', 'AnyCPU', 'AnyCPU-32BitPreferred', 'x86' (managed 32-bit-only or native i386), or 'unknown'.
function Get-PeArchitecture($path) {
    $b = [System.IO.File]::ReadAllBytes($path)
    if ($b.Length -lt 0x40 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return 'unknown' }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ($pe -lt 0 -or $pe + 24 -gt $b.Length) { return 'unknown' }
    $machine = [BitConverter]::ToUInt16($b, $pe + 4)
    if ($machine -eq 0x8664) { return 'x64' }
    if ($machine -eq 0xAA64) { return 'ARM64' }
    if ($machine -ne 0x014C) { return 'unknown' }
    $sections = [BitConverter]::ToUInt16($b, $pe + 6)
    $optSize = [BitConverter]::ToUInt16($b, $pe + 20)
    $opt = $pe + 24
    $magic = [BitConverter]::ToUInt16($b, $opt)
    $dataDirs = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
    $clrRva = [BitConverter]::ToUInt32($b, $dataDirs + 14 * 8)       # IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR
    if ($clrRva -eq 0) { return 'x86' }                               # native i386
    $clrOffset = -1
    for ($s = 0; $s -lt $sections; $s++) {
        $h = $opt + $optSize + $s * 40
        $va = [BitConverter]::ToUInt32($b, $h + 12)
        $rawSize = [BitConverter]::ToUInt32($b, $h + 16)
        $raw = [BitConverter]::ToUInt32($b, $h + 20)
        if ($clrRva -ge $va -and $clrRva -lt $va + $rawSize) { $clrOffset = [int]($clrRva - $va + $raw); break }
    }
    if ($clrOffset -lt 0 -or $clrOffset + 20 -gt $b.Length) { return 'unknown' }
    $flags = [BitConverter]::ToUInt32($b, $clrOffset + 16)            # IMAGE_COR20_HEADER.Flags
    $required = ($flags -band 0x2) -ne 0                              # COMIMAGE_FLAGS_32BITREQUIRED
    $preferred = ($flags -band 0x20000) -ne 0                         # COMIMAGE_FLAGS_32BITPREFERRED
    if ($required -and $preferred) { return 'AnyCPU-32BitPreferred' }
    if ($required) { return 'x86' }
    return 'AnyCPU'
}

# True when an exe of this architecture runs 64-bit on a 64-bit build machine (i.e. it is NOT 32-bit-only).
# On a 32-bit machine nothing can be proven about 64-bit Windows: anything that starts there passes.
function Test-IndexerBitness([string]$arch, [bool]$is64BitOs) {
    if (-not $is64BitOs) { return $arch -in @('AnyCPU', 'AnyCPU-32BitPreferred', 'x86') }
    return $arch -in @('AnyCPU', 'x64')
}

# Review L4: the vendored native SQLite interops, pinned by SHA-256. Both are System.Data.SQLite 1.0.118
# (stub.system.data.sqlite.core.netframework 1.0.118, build\net46\x86 and \x64), Authenticode-signed by
# Mistachkin Systems. A changed byte here is a different native DLL loaded into Clarion.exe and the indexer.
function Get-PinnedSqliteInterop {
    return @{
        'ClarionAssistant\lib\sqlite-fts5\SQLite.Interop.dll'     = '70E48EF5C14766F3601C97451B47859FDDCBE7F237E1C5200CEA8E7A7609D809'
        'ClarionAssistant\lib\sqlite-fts5\x64\SQLite.Interop.dll' = 'ABA67C7E6C01856838B8BC6B0BA95E864E1FDCB3750AA7CDC1BC73511CEA6FE4'
    }
}

function Test-PinnedFileHash([string]$path, [string]$expectedSha256) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $actual = -join ($sha.ComputeHash([System.IO.File]::ReadAllBytes($path)) | ForEach-Object { $_.ToString('X2') })
    } finally { $sha.Dispose() }
    return $actual -eq $expectedSha256.ToUpperInvariant()
}

$indexerExe = "$repoRoot\ClarionAssistant\indexer\bin\Debug\clarion-indexer.exe"
$x64Interop = "$repoRoot\ClarionAssistant\lib\sqlite-fts5\x64\SQLite.Interop.dll"
if (-not (Test-Path $indexerExe)) { Write-Error "clarion-indexer.exe not found at $indexerExe. Build indexer\ClarionIndexer.csproj first."; exit 1 }
if (-not (Test-Path $x64Interop)) { Write-Error "x64 SQLite interop missing: $x64Interop"; exit 1 }
$pinned = Get-PinnedSqliteInterop
foreach ($rel in @($pinned.Keys)) {
    $full = Join-Path $repoRoot $rel
    if (-not (Test-PinnedFileHash $full $pinned[$rel])) {
        Write-Error "$rel does not match its pinned SHA-256 ($($pinned[$rel])). It is not the vendored System.Data.SQLite 1.0.118 interop; restore it from git, or re-verify a new one (Authenticode, package source) and update the pin in Get-PinnedSqliteInterop."
        exit 1
    }
}
$indexerArch = Get-PeArchitecture $indexerExe
if (-not (Test-IndexerBitness $indexerArch ([Environment]::Is64BitOperatingSystem))) {
    Write-Error "clarion-indexer.exe is $indexerArch - it would run 32-bit on 64-bit Windows. Expected AnyCPU with Prefer32Bit off (or x64). Rebuild indexer\ClarionIndexer.csproj."
    exit 1
}
$idxVer = (Get-Item $indexerExe).VersionInfo.FileVersion
if ($expected -and $idxVer -ne $expected) {
    if ($AllowStaleBins) { Write-Warning "Shipping stale clarion-indexer.exe ($idxVer, expected $expected) because -AllowStaleBins was passed." }
    else {
        Write-Error "Stale clarion-indexer.exe: $idxVer (expected $expected). Rebuild it: msbuild ClarionAssistant\indexer\ClarionIndexer.csproj /p:Configuration=Debug (after the addin, which generates Properties\AssemblyVersion.cs). Use -AllowStaleBins to override."
        exit 1
    }
}
Write-Host "  OK   clarion-indexer.exe is $indexerArch ($idxVer, runs 64-bit on 64-bit Windows); SQLite interops match their pins" -ForegroundColor Green

# ── Step 2: Sign DLLs before packaging (if requested) ──
# Review L4: with -Sign every failure is FATAL. An unsigned binary inside a signed installer is still an
# unsigned binary loaded into Clarion.exe; a release that asked to be signed must not ship one quietly.
if ($Sign) {
    if (-not $signtool) { Write-Error "-Sign was passed but signtool.exe was not found under Windows Kits\10\bin. Install the Windows SDK signing tools."; exit 1 }
    Write-Host "`nSigning binaries..." -ForegroundColor Yellow
    # The add-in DLLs signed are the per-Clarion ones the installer SHIPS (bin\Debug-C<v>), not the
    # generic bin\Debug that nothing packages - signing that one left every shipped add-in DLL unsigned.
    # C10, C11 and C12 are required; C11.1 is signed when built (the .iss guards it with HaveC11_1).
    $requiredToSign = @(
        "$repoRoot\ClarionAssistant\bin\Debug-C10\ClarionAssistant.dll",
        "$repoRoot\ClarionAssistant\bin\Debug-C11\ClarionAssistant.dll",
        "$repoRoot\ClarionAssistant\bin\Debug-C12\ClarionAssistant.dll",
        "$repoRoot\ClarionAssistant\indexer\bin\Debug\clarion-indexer.exe",
        "$repoRoot\ClarionAssistant\mcp-server\bin\Debug\clarion-mcp-server.exe"
    )
    # Built outside this repo and only when present (Step 1 skips it otherwise); signed when it is there.
    $optionalToSign = @(
        "$repoRoot\ClarionAssistant\bin\Debug-C11.1\ClarionAssistant.dll",
        'H:\DevLaptop\ClarionIdeCOMPane\ClarionCOMBrowser\bin\Debug\ClarionCOMBrowser.dll'
    )
    foreach ($f in $requiredToSign) {
        if (-not (Test-Path $f)) { Write-Error "Cannot sign $f - it does not exist."; exit 1 }
    }
    foreach ($f in @($requiredToSign + @($optionalToSign | Where-Object { Test-Path $_ }))) {
        Write-Host "  Signing $([IO.Path]::GetFileName($f))..."
        & $signtool sign /sha1 $signCertThumbprint /fd sha256 /tr http://timestamp.sectigo.com /td sha256 /d "Clarion Assistant" $f
        if ($LASTEXITCODE -ne 0) { Write-Error "Failed to sign $f (signtool exit $LASTEXITCODE). Plug in the Sectigo EV dongle and rebuild."; exit 1 }
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
# Pass the released version through to the .iss, which no longer hardcodes it and #errors without
# it. /D applies to ClarionAssistant.iss.tmp (the rewritten missing-icon path) just as it does to
# the original, so nothing special is needed for that branch.
if (-not $release) {
    Write-Error "Could not parse <FullVersion> from $versionProps. build-installer.ps1 must supply MyAppVersion to ISCC - the .iss refuses to compile without it."
    exit 1
}
Write-Host "Compiling installer as version $release" -ForegroundColor Yellow
& $innoSetup "/DMyAppVersion=$release" $issFile 2>&1 | Tee-Object -Variable isccOutput | Out-Host
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
if ($Sign -and -not $installerExe) { Write-Error "-Sign was passed but no installer .exe was found in $outputDir to sign."; exit 1 }
if ($Sign) {
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
