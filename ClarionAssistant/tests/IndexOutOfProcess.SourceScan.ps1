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
#   583abdec review fixes:
#   S7  D1  the child takes its OWN <db>.writer.lock before opening the db; TryEnter treats it as held; no
#           inheritable handle; the fallback takes it too; the relationship scan polls cancel every 4096 lines.
#   S8  D3  success is checked before cancel; killed only when Kill() succeeded.
#   S9  D4a db closed before the result JSON; ParseOutcome takes the last object.
#   S10 D4b UTF-8 writers before the interop preload.         S11 L2  stderr text is sanitised under --protocol.
#   S12 L3  MCP callers get McpFailureReply.                   S13 M1  sln_path / db gated before any I/O.
#   S14 L1  every path argument absolute.  (S4a: D2, AnyCPU with Prefer32Bit off; S1g: D2, unstartable exe falls back.)
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
    Check ($runBody -match 'if \(string\.IsNullOrEmpty\(exe\) \|\| !File\.Exists\(exe\)\)\s*return RunInProcessFallback') 'S1f' 'the fallback is taken when the exe is missing'
    # Review D2: ...and when Windows will not START it (Win32Exception) - and nowhere else.
    $fallbackCalls = [regex]::Matches($runText, 'return RunInProcessFallback\(')
    Check ($fallbackCalls.Count -eq 2 -and $runText -match '(?s)catch \(System\.ComponentModel\.Win32Exception ex\)\s*\{\s*return RunInProcessFallback\(request, exe, "could not be started') 'S1g' 'the only other fallback is an exe that fails to START (Win32Exception), with the reason in the warning'
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
Check ($oop -match 'SendCancel\(child\)' -and $oop -match 'TryKill\(child\)' -and $oop -match 'p\.Kill\(\)' -and $oop -match 'CancelGrace') 'S3d' 'cancel asks the child first, then kills it after the grace'
$prog = Strip-Comments (Read-Text 'indexer\Program.cs')
Check ($prog -match 'Console\.OpenStandardInput\(\)' -and $prog -match 'IndexerProtocol\.CancelCommand' -and $prog -match 'hostGone') 'S3e' 'the child cancels on "cancel" and when its host dies (stdin closes)'

# ---- S4: 64-bit indexer ----
$csproj = Read-Text 'indexer\ClarionIndexer.csproj'
# Review D2: AnyCPU with Prefer32Bit OFF - 64-bit wherever Windows is, and it still starts on 32-bit Windows.
Check ($csproj -and ([regex]::Matches($csproj, '<PlatformTarget>AnyCPU</PlatformTarget>').Count -eq 2) -and $csproj -notmatch '<PlatformTarget>(x86|x64)</PlatformTarget>' -and
       ([regex]::Matches($csproj, '<Prefer32Bit>false</Prefer32Bit>').Count -eq 2) -and $csproj -notmatch '<Prefer32Bit>true' -and
       $csproj.Contains('<Platform Condition=" ''$(Platform)'' == '''' ">AnyCPU</Platform>')) 'S4a' 'ClarionIndexer.csproj builds AnyCPU with Prefer32Bit off in Debug and Release (its default platform), never x86/x64-only'
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

# ---- S7 (review D1): the lock covers the child ----
$gate = Strip-Comments (Read-Text 'Services\IndexRunGate.cs')
$idxProj = Read-Text 'indexer\ClarionIndexer.csproj'
Check ($idxProj -match '<Compile Include="\.\.\\Services\\IndexRunGate\.cs">') 'S7a' 'the indexer compiles the same IndexRunGate the hosts use'
$progMain = Body $prog 'static int RunIndex(string[] args)' 30000
$lockAt = $progMain.IndexOf('IndexRunGate.TryAcquireWriterLock(dbPath')
$openAt = $progMain.IndexOf('db.Open(dbPath)')
Check ($lockAt -ge 0 -and $openAt -gt $lockAt -and $progMain -match '(?s)TryAcquireWriterLock\(dbPath[^;]*;?\s*==\s*ClarionAssistant\.Services\.IndexRunGate\.LockResult\.Blocked\)\s*\{.*?return IndexerProtocol\.ExitFailed;') 'S7b' 'the child takes its OWN writer lock before it opens the database, and refuses to run when another writer holds it'
$enter = Body $gate 'public static bool TryEnter(string dbPath, out string holder)' 3000
Check ($enter -match 'ProbeWriterLock\(key, out writer\)' -and $enter -match '(?s)if \(ProbeWriterLock\(key, out writer\)\)\s*\{.*?return false;') 'S7c' "the host-side TryEnter refuses while a writer lock is held (an orphaned child is still writing)"
Check ($gate -notmatch 'FileShare\.Inheritable' -and $gate -notmatch 'HandleInheritability\.Inheritable') 'S7d' "no lock handle is ever inheritable (the child takes its own, never the host's)"
Check ($gate -match 'WriterLockSuffix = "\.writer\.lock"' -and $gate -match 'HostLockSuffix = "\.lock"') 'S7e' 'host lock <db>.lock and writer lock <db>.writer.lock are separate files'
$fb = Body $oop 'private IndexResult RunInProcessFallback(' 3000
Check ($fb -match 'IndexRunGate\.TryAcquireWriterLock\(dbPath' -and $fb.IndexOf('TryAcquireWriterLock') -lt $fb.IndexOf('db.Open(dbPath)')) 'S7f' 'the in-process fallback takes the writer lock too, before it opens the database'
$cgi = Strip-Comments (Read-Text 'CodeGraph\Graph\CodeGraphIndexer.cs')
Check ($cgi -match 'if \(\(\(i - target\.StartLine\) & CancelPollLineMask\) == CancelPollLineMask\) ThrowIfCancelled\(\);' -and $cgi -match 'CancelPollLineMask = 4095;') 'S7g' 'the relationship scan polls cancel (and so the child''s host-died signal) every 4096 lines of a file, not only between files'

# ---- S8 (review D3): a finished run is never a cancel; "killed" means the kill worked ----
$run = Body $oop 'public IndexResult Run(IndexRunRequest request)' 12000
$okAt = $run.IndexOf('if (outcome != null && outcome.Success && outcome.Result != null && exitCode == IndexerProtocol.ExitOk)')
$ocAt = $run.IndexOf('throw new OperationCanceledException(')
Check ($okAt -ge 0 -and $ocAt -gt $okAt) 'S8a' 'Run returns a successful outcome (exit 0, Success) BEFORE it tests for a cancel'
Check ($run -match '(?s)if \(!HasExited\(child\) && TryKill\(child\)\)\s*\{\s*killed = true;' -and ([regex]::Matches($run, 'killed = true').Count -eq 1) -and
       $oop -match '(?s)private static bool TryKill\(Process p\)\s*\{\s*try \{ p\.Kill\(\); return true; \}\s*catch \{ return false; \}') 'S8b' 'killed is set only when Kill() succeeded (never for a child that already exited, never when Kill throws)'

# ---- S9 (review D4a): the database is closed before the result is printed ----
$closeAt = $progMain.IndexOf('db.Close();')
$jsonAt  = $progMain.IndexOf('Console.WriteLine(IndexerProtocol.ResultToJson(')
Check ($closeAt -ge 0 -and $jsonAt -gt $closeAt -and $progMain.IndexOf('ReleaseWriter(ref writerLock);') -lt $jsonAt) 'S9a' 'the child closes the database (and drops its writer lock) BEFORE it prints the result JSON'
$proto = Strip-Comments (Read-Text 'CodeGraph\Graph\IndexerProtocol.cs')
Check ($proto -notmatch "stdout\.IndexOf\('\{'\)" -and $proto -match 'LastObject\(stdout\)') 'S9b' 'ParseOutcome takes the LAST complete object, not the span from the first { to the last }'

# ---- S10 (review D4b): UTF-8 pipes before anything can fail ----
$mainBody = Body $prog 'static int Main(string[] args)' 3000
$utfAt = $mainBody.IndexOf('Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false))')
$preAt = $mainBody.IndexOf('PreloadSqliteInterop()')
Check ($utfAt -ge 0 -and $preAt -gt $utfAt -and $mainBody.IndexOf('Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))') -lt $preAt) 'S10' 'Main installs the UTF-8 (no BOM) stdout/stderr writers before the SQLite interop preload'

# ---- S11 (review L2): no raw text line can start a protocol record ----
# Main and RunIndex (the --protocol paths) plus the helpers; query/stats never run under --protocol.
$noUsage = [regex]::Replace($prog, '(?s)static void PrintUsage\(\).*?(?=static int RunIndex\(string\[\] args\))', '')
$q = $noUsage.IndexOf('static int RunQuery(string[] args)')
if ($q -gt 0) { $noUsage = $noUsage.Substring(0, $q) }
$raw = [regex]::Matches($noUsage, 'Console\.Error\.WriteLine\(([^;]*)\);')
$rawBad = @($raw | Where-Object { $_.Groups[1].Value -notin @('_protocol ? ProtocolSafe(text) : text', 'encoded') })
Check ($raw.Count -ge 2 -and $rawBad.Count -eq 0) ('S11a' ) ("outside PrintUsage, stderr is written only by ErrLine (sanitised under --protocol) and Record (protocol records)" + $(if ($rawBad.Count) { ': ' + (($rawBad | ForEach-Object { $_.Value }) -join ' | ') } else { '' }))
$records = [regex]::Matches($prog, '(?<!void )\bRecord\(([^;]*)\);')
Check ($records.Count -ge 3 -and @($records | Where-Object { $_.Groups[1].Value -notmatch '^IndexerProtocol\.Encode(Mode|Message|Event)\(' }).Count -eq 0) 'S11b' 'Record is only ever given IndexerProtocol.Encode* output'
Check ($prog -match "c == '\\r' \|\| c == '\\n' \|\| c == '\\u0085' \|\| c == '\\u2028' \|\| c == '\\u2029'" -and $prog -match 'StartsWith\(IndexerProtocol\.Prefix\.TrimEnd\(\), StringComparison\.Ordinal\) \? "  " \+ s') 'S11c' 'ProtocolSafe removes every line break and indents a line that would begin with the protocol prefix'

# ---- S12 (review L3): MCP callers get the short failure reply ----
$heldBody = Body $ws 'private void IndexHeld(' 7000
$regExec = Body $reg 'private object ExecuteIndexCodeGraph(' 10000
$rcBody = Body $runIndex 'if (e.Error != null)' 800
Check ($regExec -match 'return OutOfProcessIndexer\.McpFailureReply\(ex, runLog\.LogPath\);' -and $regExec -notmatch '"Error indexing solution: " \+ ex\.Message') 'S12a' 'index_codegraph replies with McpFailureReply (headline, last lines, the run log), not the whole stderr tail'
Check ($heldBody -match 'complete\(OutOfProcessIndexer\.McpFailureReply\(ex,' -and $heldBody -notmatch '"Error indexing solution: " \+ ex\.Message') 'S12b' "the standalone index_solution replies with McpFailureReply"
Check ($rcBody -match 'externalSummary = ClarionAssistant\.Services\.OutOfProcessIndexer\.McpFailureReply\(e\.Error, runLog\.LogPath\);' -and $rcBody -match 'runLog\.WriteLine\("FAILED: " \+ e\.Error\.Message\)') 'S12c' "the IDE's MCP-triggered run replies with McpFailureReply; the run log keeps the whole message"

# ---- S13 (review M1, security): an explicit sln_path is gated before ANY I/O ----
$gateAt = $regExec.IndexOf('McpPathGate.GatePath("index_codegraph", slnPath, PathAccess.Read, _workspace, out slnPath)')
$firstIo = @(
    $regExec.IndexOf('File.Exists(slnPath)'), $regExec.IndexOf('IndexRunGate.TryEnter('), $regExec.IndexOf('ScopeFor(slnPath)'),
    $regExec.IndexOf('SnapshotSolution('), $regExec.IndexOf('new IndexRunLog('), $regExec.IndexOf('BuildConfigurationForIndex(slnPath)')
) | Where-Object { $_ -ge 0 } | Sort-Object | Select-Object -First 1
Check ($gateAt -ge 0 -and $firstIo -gt $gateAt -and $regExec -match '(?s)if \(explicitSln\)\s*\{\s*string slnRefusal = McpPathGate\.GatePath\("index_codegraph", slnPath, PathAccess\.Read, _workspace, out slnPath\);\s*if \(slnRefusal != null\) return slnRefusal;') 'S13a' 'index_codegraph gates an explicit sln_path (Read) before File.Exists, the run gate, the scope or any other I/O on it'
$dbGateAt = $regExec.IndexOf('McpPathGate.GatePath("index_codegraph", dbPath, PathAccess.Write, _workspace, out dbPath)')
Check ($dbGateAt -gt $gateAt -and $dbGateAt -lt $regExec.IndexOf('IndexRunGate.TryEnter(') -and $dbGateAt -lt $regExec.IndexOf('File.Exists(slnPath)')) 'S13b' "...and the database it would write (Write), before the run gate"
$scopeFor = Body $ws 'public SolutionScope ScopeFor(string solutionPath)' 1200
Check ($scopeFor -match '(?s)McpPathGate\.GatePath\("index_codegraph", solutionPath, PathAccess\.Read, this, out canonical\);\s*if \(refusal != null\) throw' -and
       $scopeFor.IndexOf('GatePath(') -lt $scopeFor.IndexOf('ScopeOf(')) 'S13c' "the standalone server's ScopeFor (a caller-named solution) is gated before it reads anything"

# ---- S14 (review L1): every path reaches the child absolute ----
$ba = Body $oop 'public static string BuildArguments(IndexRunRequest r)' 2500
Check ($ba -match 'AbsolutePath\(r\.SlnPath\)' -and $ba -match 'AbsolutePath\(r\.DbPath\)' -and $ba -match 'AbsolutePath\(r\.Red\.RedFilePath\)' -and $ba -match 'libs\.Add\(AbsolutePath\(lib\)\)' -and
       $ba -notmatch 'a\.Add\(r\.DbPath\)' -and $ba -notmatch '"index", r\.SlnPath') 'S14' 'BuildArguments makes the solution, database, .red and every library path absolute in the host'

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS  IndexOutOfProcess.SourceScan ($n checks)"; exit 0 }
Write-Host "FAIL  IndexOutOfProcess.SourceScan: $($failures.Count) of $n failed" -ForegroundColor Red
exit 1
