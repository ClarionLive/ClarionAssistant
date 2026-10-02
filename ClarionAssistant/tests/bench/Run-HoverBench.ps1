# Run-HoverBench.ps1 — the CA Editor's local-first hover layer vs the Clarion language server, on real solutions.
#
# Compiles tests\bench\HoverBench.cs with the REAL sources it measures (LocalLayerHandlers and the local indexes,
# LspClient, ClarionVersionService) and runs it once per solution. See HoverBench.cs for what is measured and why.
#
#   .\tests\bench\Run-HoverBench.ps1 -Solution 'F:\Apps\Inv\Inv.sln' -Version 'Clarion 12.0.14373'
#   .\tests\bench\Run-HoverBench.ps1 -Solution a.sln,b.sln -Version 'Clarion 11.0.13372' `
#       -Server 'shipped', 'dev=F:\github\Clarion-Extension\Clarion-Extension\out\server\src\server.js' -IndexMissing
#
# -Server takes name=path pairs; the bare word 'shipped' means the pinned build deploy.ps1 ships
# (.lsp-build\<tag>\out\server\src\server.js, tag from lsp-server-sync\lsp-snapshot.json). Default: shipped.
# Each run appends one JSON line per solution x server to -History, so the trend is visible across server releases.
#
# Not part of Run-Tests.ps1: it needs real solutions, their CodeGraph DBs and a server.js. Exit 2 = could not run.

param(
    [Parameter(Mandatory = $true)][string[]]$Solution,
    [Parameter(Mandatory = $true)][string]$Version,
    [string[]]$Server = @('shipped'),
    [string]$LibraryDb,
    [int]$PerFile = 60,
    [int]$MaxFiles = 4,
    [int]$Edited = 30,
    [int]$MarginMs = 20,
    [string]$History = (Join-Path $env:LOCALAPPDATA 'ClarionAssistant\hover-bench-history.jsonl'),
    [string]$Dump,
    [switch]$IndexMissing
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# ---- servers: 'shipped' -> the pinned pure build deploy.ps1 ships ----
$servers = @()
foreach ($s in $Server) {
    if ($s -eq 'shipped') {
        $snap = Get-Content (Join-Path $repo 'lsp-server-sync\lsp-snapshot.json') -Raw | ConvertFrom-Json
        $tag = $snap.currentPin.tag
        if (-not $tag) { $tag = $snap.targetPin.tag }
        $js = Join-Path $repo ".lsp-build\$tag\out\server\src\server.js"
        if (-not (Test-Path $js)) { Write-Host "COULD NOT RUN: shipped server $tag not built at $js - run deploy.ps1 (or Sync-LspServer.ps1 -Pure) first." -ForegroundColor Red; exit 2 }
        $servers += "shipped-$tag=$js"
    } else { $servers += $s }
}

# ---- library DB: newest ClarionGraph cache unless given ----
if (-not $LibraryDb) {
    $lib = Get-ChildItem (Join-Path $env:APPDATA 'ClarionAssistant\clariongraph') -Filter 'ClarionGraph_*.db' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($lib) { $LibraryDb = $lib.FullName }
}

# ---- compile ----
$out = Join-Path $env:TEMP 'ca-hoverbench'
New-Item -ItemType Directory -Force $out | Out-Null
$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -property installationPath
    if ($vs -and (Test-Path (Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'))) { $csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe' }
}
if (-not $csc) { Write-Host 'COULD NOT RUN: the Roslyn csc.exe (Visual Studio / Build Tools) was not found.' -ForegroundColor Red; exit 2 }
$sources = @('tests\bench\HoverBench.cs', 'tests\LocalLayer.Handlers.Stubs.cs', 'Services\LocalLayerHandlers.cs', 'Services\WebMessageGuard.cs',
             'tests\ModernEmbeditorDiagnostics.SlotBalance.Stubs.cs', 'Services\ModernEmbeditorDiagnostics.cs',
             'Services\ClarionAppDataReader.cs', 'Services\ClarionAppDataReader.Model.cs',
             'Services\LocalScopeIndex.cs', 'Services\LiveDictionaryIndex.cs', 'Services\SymbolIndex.cs', 'Services\IndexRunGate.cs',
             'CodeGraph\Graph\CodeGraphProvider.cs', 'CodeGraph\Graph\CodeGraphDatabase.cs',
             'CodeGraph\Parsing\Models\ClarionSymbol.cs', 'CodeGraph\Parsing\Models\ClarionRelationship.cs',
             'CodeGraph\Parsing\Models\SolutionProject.cs', 'CodeGraph\Parsing\Models\ParseResult.cs', 'CodeGraph\Parsing\ClarionBuiltins.cs',
             'Services\LspClient.cs', 'Services\JsonTextStream.cs', 'Services\LspTrace.cs', 'Services\EncodingHelper.cs',
             'Services\ClarionVersionService.cs', 'Services\ClarionConfigDirectory.cs') | ForEach-Object { Join-Path $repo $_ }
Copy-Item (Join-Path $repo 'lib\sqlite-fts5\System.Data.SQLite.dll'), (Join-Path $repo 'lib\sqlite-fts5\SQLite.Interop.dll') $out -Force
$exe = Join-Path $out 'HoverBench.exe'
& $csc -nologo -nowarn:0168,0169,0414,0649 -platform:x86 -out:$exe -r:System.dll -r:System.Core.dll -r:System.Xml.dll -r:System.Data.dll `
    -r:System.Web.Extensions.dll -r:(Join-Path $out 'System.Data.SQLite.dll') @sources
if ($LASTEXITCODE -ne 0) { Write-Host 'COULD NOT RUN: HoverBench did not compile.' -ForegroundColor Red; exit 2 }

$commit = ''
try { $commit = (git -C $repo rev-parse --short HEAD 2>$null) } catch { }

$worst = 0
foreach ($sln in $Solution) {
    $sln = (Resolve-Path $sln).Path
    $db = [System.IO.Path]::ChangeExtension($sln, '.codegraph.db')
    if (-not (Test-Path $db) -and $IndexMissing) {
        # The local layer reads the solution's CodeGraph DB; without one it is measured at a disadvantage.
        $indexer = Join-Path $repo 'indexer\bin\Debug\clarion-indexer.exe'
        if (Test-Path $indexer) {
            Write-Host "Indexing $sln (no CodeGraph DB yet)..." -ForegroundColor DarkGray
            # With the version's .red, as the IDE indexes: without it only the solution folder resolves, and the
            # local layer would be measured against a thinner index than a developer actually has.
            $red = (& $exe --sln $sln --version $Version --print-red | Select-Object -Last 1)
            if ($red -and (Test-Path $red)) { & $indexer index $sln --red $red | Out-Null } else { & $indexer index $sln | Out-Null }
        } else { Write-Host "  (no indexer at $indexer - build indexer\ClarionIndexer.csproj, or open the solution in the IDE once)" -ForegroundColor Yellow }
    }
    $a = @('--sln', $sln, '--version', $Version, '--per-file', $PerFile, '--max-files', $MaxFiles, '--edited', $Edited,
           '--margin-ms', $MarginMs, '--history', $History, '--ca-commit', $commit)
    if ($LibraryDb) { $a += @('--library-db', $LibraryDb) }
    if ($Dump) { $a += @('--dump', $Dump) }   # every sample with both raw cards, as JSON lines
    foreach ($s in $servers) { $a += @('--server', $s) }
    & $exe @a
    if ($LASTEXITCODE -gt $worst) { $worst = $LASTEXITCODE }
    Write-Host ''
}
exit $worst
