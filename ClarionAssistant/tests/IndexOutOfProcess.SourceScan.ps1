# IndexOutOfProcess.SourceScan.ps1 - ticket 583abdec.
#
# The CodeGraph index used to run inside Clarion.exe (32-bit, ~1.5 GB at idle) and ran it out of memory on a
# large solution. Every host-side index now runs in clarion-indexer.exe, a 64-bit child process, through
# Services\OutOfProcessIndexer.cs. This scan pins that:
#
#   S1  no host-side source calls CodeGraphIndexer.IndexSolution( - except ONE call, in
#       OutOfProcessIndexer.RunInProcessFallback, which sets the warning and reports it before it runs
#       (the exe-missing fallback). Host-side = everything under ClarionAssistant\ except the indexer's own
#       definition (CodeGraph\), the child exe (indexer\) and the tests.
#   S2  the three index entry points (the IDE's RunIndex, the index_codegraph tool, the standalone server's
#       index_solution) go through OutOfProcessIndexer.Run.
#   S3  the child: no console window, no shell, found beside the addin assembly, never on PATH; cancel
#       writes to stdin and then kills.
#   S4  the indexer builds 64-bit, with the x64 SQLite interop vendored and copied.
#   S5  deploy.ps1 and the installer ship x64\SQLite.Interop.dll for every Clarion version.
#   S6  the IDE keeps its cancel semantics: SymbolIndex released before the run, a cancelled FULL run's
#       database deleted and the delete verified.
#
# Exit: 0 pass, 1 fail.

$ErrorActionPreference = 'Stop'
$RepoDir = Split-Path -Parent $PSScriptRoot          # ...\ClarionAssistant
$RootDir = Split-Path -Parent $RepoDir
$failures = @()
$n = 0

function Check([bool]$ok, [string]$id, [string]$msg) {
    $script:n++
    if ($ok) { Write-Host "  ok   $id  $msg" }
    else { Write-Host "  FAIL $id  $msg" -ForegroundColor Red; $script:failures += "${id}: $msg" }
}

function Read-Text([string]$rel) {
    $p = Join-Path $RepoDir $rel
    if (-not (Test-Path $p)) { return $null }
    return [IO.File]::ReadAllText($p)
}

# Strip // line comments and /* */ blocks so a comment that MENTIONS IndexSolution( is not a call.
function Strip-Comments([string]$src) {
    $noBlock = [regex]::Replace($src, '/\*.*?\*/', '', 'Singleline')
    return [regex]::Replace($noBlock, '//[^\r\n]*', '')
}

# ---- S1: no in-process index on a host path ----
$hostFiles = Get-ChildItem $RepoDir -Recurse -Filter *.cs -File |
    Where-Object {
        $rel = $_.FullName.Substring($RepoDir.Length + 1)
        -not ($rel -like 'CodeGraph\*' -or $rel -like 'indexer\*' -or $rel -like 'tests\*' -or
              $rel -like 'bin\*' -or $rel -like 'obj\*' -or $rel -like '*\bin\*' -or $rel -like '*\obj\*' -or
              $rel -like '.lsp-build\*' -or $rel -like 'lsp-server*')
    }
$calls = @()
foreach ($f in $hostFiles) {
    $code = Strip-Comments ([IO.File]::ReadAllText($f.FullName))
    foreach ($m in [regex]::Matches($code, '\.IndexSolution\s*\(')) {
        $calls += [pscustomobject]@{ File = $f.FullName.Substring($RepoDir.Length + 1); Index = $m.Index; Code = $code }
    }
}
$allowed = @($calls | Where-Object { $_.File -eq 'Services\OutOfProcessIndexer.cs' })
$others  = @($calls | Where-Object { $_.File -ne 'Services\OutOfProcessIndexer.cs' })
Check ($hostFiles.Count -gt 50) 'S1a' "the scan covers the host-side sources ($($hostFiles.Count) .cs files)"
Check ($others.Count -eq 0) 'S1b' ("no host-side IndexSolution( call outside the fallback" +
    $(if ($others.Count) { ": " + (($others | ForEach-Object { $_.File }) -join ', ') } else { '' }))
Check ($allowed.Count -eq 1) 'S1c' "exactly one IndexSolution( call in OutOfProcessIndexer.cs (found $($allowed.Count))"
if ($allowed.Count -eq 1) {
    $code = $allowed[0].Code
    $start = $code.IndexOf('private IndexResult RunInProcessFallback(')
    $next  = if ($start -ge 0) { $code.IndexOf("`n        }", $start) } else { -1 }
    $inFallback = $start -ge 0 -and $allowed[0].Index -gt $start -and $allowed[0].Index -lt $next
    Check $inFallback 'S1d' 'that call is inside RunInProcessFallback'
    $body = if ($start -ge 0 -and $next -gt $start) { $code.Substring($start, $next - $start) } else { '' }
    $warnAt = $body.IndexOf('FallbackWarning = ')
    $reportAt = $body.IndexOf('p(FallbackWarning)')
    $callAt = $body.IndexOf('.IndexSolution(')
    Check ($warnAt -ge 0 -and $reportAt -gt $warnAt -and $callAt -gt $reportAt -and $body.Contains('RanInProcess = true')) 'S1e' 'the fallback sets RanInProcess and the warning, and reports the warning BEFORE it indexes'
    $runText = Strip-Comments (Read-Text 'Services\OutOfProcessIndexer.cs')
    $runStart = $runText.IndexOf('public IndexResult Run(IndexRunRequest request)')
    $runBody = if ($runStart -ge 0) { $runText.Substring($runStart, 900) } else { '' }
    Check ($runBody -match 'if \(string\.IsNullOrEmpty\(exe\) \|\| !File\.Exists\(exe\)\)\s*return RunInProcessFallback') 'S1f' 'the fallback is taken ONLY when the exe is missing'
}

# ---- S2: the entry points use the child ----
$chat = Strip-Comments (Read-Text 'AssistantChatControl.cs')
$reg  = Strip-Comments (Read-Text 'Services\McpToolRegistry.cs')
$ws   = Strip-Comments (Read-Text 'mcp-server\StandaloneWorkspace.cs')
function Body([string]$code, [string]$sig, [int]$len) {
    if (-not $code) { return '' }
    $i = $code.IndexOf($sig)
    if ($i -lt 0) { return '' }
    return $code.Substring($i, [Math]::Min($len, $code.Length - $i))
}
$runIndex = Body $chat 'bool showProgressWindow,' 30000
$exec = Body $reg 'private object ExecuteIndexCodeGraph(' 9000
$held = Body $ws 'private void IndexHeld(' 6000
foreach ($e in @(@{ Id = 'S2a'; Name = 'the IDE Reindex/Update (AssistantChatControl.RunIndex)'; Body = $runIndex },
                 @{ Id = 'S2b'; Name = 'index_codegraph (McpToolRegistry.ExecuteIndexCodeGraph)'; Body = $exec },
                 @{ Id = 'S2c'; Name = 'the standalone index_solution (StandaloneWorkspace.IndexHeld)'; Body = $held })) {
    $b = $e.Body
    Check ($b -ne '' -and $b -match 'new (ClarionAssistant\.Services\.)?OutOfProcessIndexer\(\)' -and $b -match '\.Run\(new (ClarionAssistant\.Services\.)?IndexRunRequest' -and
           $b -notmatch 'new (ClarionCodeGraph\.Graph\.)?CodeGraphIndexer\(') $e.Id "$($e.Name) runs through OutOfProcessIndexer, and builds no CodeGraphIndexer"
}
Check ($runIndex -match 'Red = activeRed' -and $runIndex -match 'BuildConfiguration = buildConfiguration' -and $runIndex -match 'LibraryPaths = libPaths' -and
       $runIndex -match 'Incremental = incremental' -and $runIndex -match 'DbPath = dbPath') 'S2d' "the IDE passes its ACTIVE .red, the build configuration (GH #245), library paths, mode and db"

# ---- S3: the child process ----
$oop = Strip-Comments (Read-Text 'Services\OutOfProcessIndexer.cs')
Check ($oop -match 'CreateNoWindow\s*=\s*true' -and $oop -match 'UseShellExecute\s*=\s*false') 'S3a' 'no console window, no shell'
Check ($oop -match 'typeof\(OutOfProcessIndexer\)\.Assembly\.Location' -and $oop -notmatch 'GetEnvironmentVariable\(\s*"PATH"' -and $oop -match 'FileName\s*=\s*exe') 'S3b' 'the exe is found beside the addin assembly, never on PATH'
Check ($oop -match 'StandardOutputEncoding\s*=\s*new UTF8Encoding\(false\)' -and $oop -match 'StandardErrorEncoding\s*=\s*new UTF8Encoding\(false\)') 'S3c' 'the pipes are read as UTF-8 without a BOM'
Check ($oop -match 'SendCancel\(child\)' -and $oop -match 'child\.Kill\(\)' -and $oop -match 'CancelGrace') 'S3d' 'cancel asks the child first, then kills it after the grace'
$prog = Strip-Comments (Read-Text 'indexer\Program.cs')
Check ($prog -match 'Console\.OpenStandardInput\(\)' -and $prog -match 'IndexerProtocol\.CancelCommand' -and $prog -match 'hostGone') 'S3e' 'the child cancels on "cancel" and when its host dies (stdin closes)'

# ---- S4: 64-bit indexer ----
$csproj = Read-Text 'indexer\ClarionIndexer.csproj'
Check ($csproj -and ([regex]::Matches($csproj, '<PlatformTarget>x64</PlatformTarget>').Count -eq 2) -and $csproj -notmatch '<PlatformTarget>x86</PlatformTarget>' -and
       ([regex]::Matches($csproj, '<Prefer32Bit>false</Prefer32Bit>').Count -eq 2)) 'S4a' 'ClarionIndexer.csproj builds x64 in Debug and Release, never x86'
Check ($csproj -match 'SqliteVendored\)\\x64\\SQLite\.Interop\.dll" DestinationFolder="\$\(OutputPath\)x64"') 'S4b' 'the build copies the x64 interop to x64\'
$x64 = Join-Path $RepoDir 'lib\sqlite-fts5\x64\SQLite.Interop.dll'
$machine = 0
if (Test-Path $x64) {
    $b = [IO.File]::ReadAllBytes($x64)
    $machine = [BitConverter]::ToUInt16($b, [BitConverter]::ToInt32($b, 0x3C) + 4)
}
Check ($machine -eq 0x8664) 'S4c' ("lib\sqlite-fts5\x64\SQLite.Interop.dll is vendored and 64-bit (PE machine 0x{0:X4})" -f $machine)
Check ($prog -match 'LoadLibraryW\(path\)' -and $prog -match 'Environment\.Is64BitProcess \? "x64" : "x86"') 'S4d' 'the indexer loads the interop for its own bitness before System.Data.SQLite does'

# ---- S5: deploy and installer ship x64\ ----
$deploy = Read-Text 'deploy.ps1'
$items = if ($deploy -match '(?s)\$IndexerItems = @\((.*?)\)') { $Matches[1] } else { '' }
Check ($items -match '"x64"' -and $items -match '"x86"') 'S5a' 'deploy.ps1 deploys the indexer x64\ folder (and x86\)'
$iss = [IO.File]::ReadAllText((Join-Path $RootDir 'installer\ClarionAssistant.iss'))
foreach ($c in 'clarion10', 'clarion11', 'clarion111', 'clarion12') {
    $hasExe = $iss -match ('Source: "\{#SrcClarionIndexer\}\\clarion-indexer\.exe";[^\r\n]*Components: ' + $c + ';')
    $hasX64 = $iss -match ('Source: "\{#SrcFts5\}\\x64\\SQLite\.Interop\.dll"; DestDir: "\{code:Get\w+Path\}\\accessory\\addins\\ClarionAssistant\\x64"; Components: ' + $c + ';')
    Check ($hasExe -and $hasX64) "S5-$c" "the installer ships clarion-indexer.exe AND x64\SQLite.Interop.dll for $c"
}

# ---- S6: cancel semantics kept in the IDE ----
$doWork = Body $runIndex 'worker.DoWork +=' 6000
$relAt = $doWork.IndexOf('SymbolIndex.Release(dbPath)')
$runAt = $doWork.IndexOf('.Run(new')
Check ($relAt -ge 0 -and $runAt -gt $relAt) 'S6a' "SymbolIndex's read connection is released before the run"
Check ($doWork -match 'catch \(OperationCanceledException\)\s*\{\s*wasCancelled = true;' -and $doWork -match 'ranIncremental = indexer\.EffectiveIncremental' -and
       $doWork -match '(?s)if \(wasCancelled && !ranIncremental\).*?File\.Delete\(dbPath\).*?partialDbDeleted = !File\.Exists\(dbPath\)') 'S6b' 'a cancelled FULL run (incremental upgraded to full included) deletes the partial database and verifies it'
Check ($doWork -match 'File\.Delete\(dbPath \+ "-wal"\)' -and $doWork -match 'File\.Delete\(dbPath \+ "-shm"\)') 'S6c' "a deleted partial database takes its WAL with it (a KILLED child never closed it)"

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS  IndexOutOfProcess.SourceScan ($n checks)"; exit 0 }
Write-Host "FAIL  IndexOutOfProcess.SourceScan: $($failures.Count) of $n failed" -ForegroundColor Red
exit 1
