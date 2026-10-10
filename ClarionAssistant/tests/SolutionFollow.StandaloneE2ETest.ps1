# Regression guard for ticket 9e1bd280: every tool in a tab answers for the solution the IDE has open NOW.
#
# THE DEFECT. The Owner opened UltimateSQLTest in the Clarion IDE. The CA header followed (Clarion 10 Active And
# Updated, Clarion100.red), but the open tab labelled CA1 + v61POSitive kept answering get_solution_info for
# v61POSitive.sln with that solution's version and Clarion10v61.red: its standalone clarion-mcp-server was launched
# with --solution, a snapshot, and only its LSP (and only without --solution) followed the IDE (77aceec5).
#
# HOW. The real clarion-mcp-server.exe, launched the way the addin launches it (--ide-pid = this test's pid,
# --solution A), from a private copy of its bin with the scripted language server from
# SolutionVersion.StandaloneE2ETest.ps1 planted where LspService looks (it logs every clarion/updatePaths, which
# carries solutionFilePath). This test plays the IDE by writing the record the addin publishes.
#
#   S0  no record yet              -> --solution A, solutionSource launch-arg
#   S1  the IDE has A on V1        -> ide-live, A, V1
#   S2  the IDE switches to B (V1) -> B, switchedFrom A, B's OWN local .red (same version: the .red cache must be
#                                     keyed on the solution), B's CodeGraph db, the LSP restarted on B
#   S3  the IDE puts B on V2       -> V2 (the version is B's, re-resolved for the new solution)
#   S5  back to A, its record names no build configuration -> NOT B's 'Release' (the record is A's now), A's folder
#   S6  A's record says 'Profile'  -> 'Profile': the configuration is always the FOLLOWED solution's
#   S7  index_codegraph on A, the IDE switches to B between the run's snapshot and its set-up -> A's db, A's .red
#   S8  index_solution on B, the IDE switches to A at the same point -> the run log names B's .red
#   S9  index_codegraph sln_path=B while A is followed -> B's own .red
#   S4  the IDE closes A          -> solutionSource none, switchedFrom A; the solution tools and the LSP say so
#
# MUST BE ABLE TO GO RED: before 9e1bd280 S1-S4 fail (the tools stay on A; the LSP, started from --solution, never
# follows).
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\SolutionFollow.StandaloneE2ETest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run (the suite counts 2 as a failure, never as green).

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0
function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

# ---------------------------------------------------------------- build
$repo   = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$csproj = Join-Path $repo 'mcp-server\ClarionMcpServer.csproj'
$binDir = Join-Path $repo 'mcp-server\bin\Debug'
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { Write-Host "COULD NOT RUN: MSBuild not found." -ForegroundColor Red; exit 2 }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Write-Host "COULD NOT RUN: node is not on PATH." -ForegroundColor Red; exit 2 }
& $msbuild $csproj /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $binDir 'clarion-mcp-server.exe'))) {
    Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red; exit 2
}

# ---------------------------------------------------------------- two real version names
$settingsRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'SoftVelocity\Clarion'
$names = New-Object System.Collections.Generic.List[string]
foreach ($f in (Get-ChildItem $settingsRoot -Filter ClarionProperties.xml -Recurse -Depth 1 -ErrorAction SilentlyContinue)) {
    try {
        [xml]$x = Get-Content -Raw -LiteralPath $f.FullName
        foreach ($v in $x.SelectNodes("//Properties[@name='Clarion.Versions']/Properties")) {
            $win = $v.SelectSingleNode("IsWindowsVersion")
            $nm  = $v.GetAttribute('name')
            if ($nm -and $win -and $win.GetAttribute('value') -eq 'True' -and $nm -notmatch 'Current' -and -not $names.Contains($nm)) { $names.Add($nm) }
        }
    } catch { }
}
if ($names.Count -lt 2) { Write-Host "COULD NOT RUN: fewer than two Win32 Clarion versions are configured on this machine." -ForegroundColor Yellow; exit 2 }
$v1 = $names[0]; $v2 = $names[1]
Write-Host "  versions: V1='$v1'  V2='$v2'" -ForegroundColor DarkGray

# ---------------------------------------------------------------- private bin, fake server, two solutions
# A LONG path, not $env:TEMP: that can be the 8.3 form (C:\Users\JOHNHI~1\...), and the test compares the paths the
# server echoes as strings.
$work = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ("Temp\ca-follow-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
$solA = Join-Path $work 'v61POSitive'
$solB = Join-Path $work 'UltimateSQLTest'
$log  = Join-Path $work 'updatepaths.log'
New-Item -ItemType Directory -Force $bin, $solA, $solB | Out-Null
Copy-Item (Join-Path $binDir '*') $bin -Recurse -Force
$serverDir = Join-Path $bin 'lsp-server\out\server\src'
New-Item -ItemType Directory -Force $serverDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-version-follow\server.js') (Join-Path $serverDir 'server.js') -Force
foreach ($d in @($solA, $solB)) { Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-semantic-pass\*') $d -Force }
# Unique solution NAMES: an index run logs to %APPDATA%\ClarionAssistant\codegraph-index-<name>.log, and a real
# v61POSitive's log must not be rotated away by this test.
$tag  = [System.IO.Path]::GetFileName($work)
$nameA = "v61POSitive-$tag"; $nameB = "UltimateSQLTest-$tag"
$slnA = Join-Path $solA "$nameA.sln"; Move-Item (Join-Path $solA 'ctrl.sln') $slnA
$slnB = Join-Path $solB "$nameB.sln"; Move-Item (Join-Path $solB 'ctrl.sln') $slnB
$pause = Join-Path $work 'index.pause'
$runLogDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ClarionAssistant'
$clwA = Join-Path $solA 'ctrl.clw'
$clwB = Join-Path $solB 'ctrl.clw'

$recordDir  = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClarionAssistant\ide-solution'
$recordFile = Join-Path $recordDir ("ide-" + $PID + ".json")
New-Item -ItemType Directory -Force $recordDir | Out-Null
function Publish([string]$solution, [string]$version, [string]$config) {
    if ([string]::IsNullOrEmpty($solution)) { if (Test-Path $recordFile) { [System.IO.File]::Delete($recordFile) }; return }
    $rec = [ordered]@{ solution = $solution; pid = $PID; writtenAt = (Get-Date).ToString('o') }
    if ($version) { $rec.clarionVersion = $version }
    if ($config)  { $rec.buildConfiguration = $config; $rec.buildPlatform = 'Win32' }
    [System.IO.File]::WriteAllText($recordFile, ($rec | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
}
function LoggedSolutions() {
    if (-not (Test-Path $log)) { return ,@() }
    return ,@(Get-Content $log | Where-Object { $_ } | ForEach-Object { [string](($_ | ConvertFrom-Json).params.solutionFilePath) })
}

$p = $null
try {
    Publish $null      # S0 needs "this IDE has published nothing yet"

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = Join-Path $bin 'clarion-mcp-server.exe'
    $psi.Arguments = "--stdio --ide-pid $PID --solution `"$slnA`""
    $psi.WorkingDirectory       = $solA
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardErrorEncoding  = New-Object System.Text.UTF8Encoding($false)
    $psi.EnvironmentVariables['CA_FAKE_LSP_LOG'] = $log
    $psi.EnvironmentVariables['CA_TEST_INDEX_SETUP_PAUSE'] = $pause   # inert until the file exists (S7, S8)
    $p = [System.Diagnostics.Process]::Start($psi)
    $stderrTask = $p.StandardError.ReadToEndAsync()
    $script:nextId = 1

    function Send([string]$method, $params, [switch]$notify) {
        $msg = @{ jsonrpc = '2.0'; method = $method; params = $params }
        if (-not $notify) { $msg.id = $script:nextId; $script:nextId++ }
        $p.StandardInput.WriteLine(($msg | ConvertTo-Json -Compress -Depth 6))
        $p.StandardInput.Flush()
        if ($notify) { return $null }
        return Answer $msg.id
    }
    # Send without waiting (S7, S8: the call pauses in the server while this test switches the IDE's solution).
    function Begin([string]$name, [hashtable]$arguments) {
        $msg = @{ jsonrpc = '2.0'; method = 'tools/call'; params = @{ name = $name; arguments = $arguments }; id = $script:nextId }
        $script:nextId++
        $p.StandardInput.WriteLine(($msg | ConvertTo-Json -Compress -Depth 6))
        $p.StandardInput.Flush()
        return $msg.id
    }
    function Answer($want) {
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        while ([DateTime]::UtcNow -lt $deadline) {
            $line = $p.StandardOutput.ReadLine()
            if ($null -eq $line) { throw "server closed stdout" }
            try { $r = $line | ConvertFrom-Json } catch { continue }
            if ($r.id -eq $want) {
                if ($r.result -and $r.result.content) { return [string]$r.result.content[0].text }
                return "JSON-RPC error: " + $r.error.message
            }
        }
        throw "no answer to request $want"
    }
    function Tool([string]$name, [hashtable]$arguments) { return Send 'tools/call' @{ name = $name; arguments = $arguments } }
    function Json([string]$text) { try { return $text | ConvertFrom-Json } catch { return $null } }
    function Show([string]$label, $value) {
        $flat = ([string]$value -replace '\s+', ' '); if ($flat.Length -gt 220) { $flat = $flat.Substring(0, 220) + '...' }
        Write-Host ("  {0,-26} {1}" -f $label, $flat) -ForegroundColor DarkGray
    }
    function SamePath([string]$x, [string]$y) { return [string]::Equals($x, $y, [StringComparison]::OrdinalIgnoreCase) }

    [void](Send 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'solution-follow-e2e'; version = '1' } })
    [void](Send 'notifications/initialized' @{} -notify)

    # ---- S0: launched on A, the IDE has published nothing: --solution ----
    $i0 = Json (Tool 'get_solution_info' @{})
    Show 'S0 get_solution_info' ("" + $i0.solutionPath + " | " + $i0.solutionSource + " | " + $i0.solutionSourceNote)
    Assert-That ($i0 -and (SamePath $i0.solutionPath $slnA)) "S0: no IDE record: the solution should be --solution A, got '$($i0.solutionPath)'"
    Assert-That ($i0.solutionSource -eq 'launch-arg') "S0: solutionSource should be launch-arg, got '$($i0.solutionSource)'"

    # ---- S1: the IDE publishes A on V1 ----
    Publish $slnA $v1
    $i1 = Json (Tool 'get_solution_info' @{})
    Show 'S1 get_solution_info' ("" + $i1.solutionPath + " | " + $i1.solutionSource + " | " + $i1.versionName + " | " + $i1.activeRedFile)
    Assert-That ((SamePath $i1.solutionPath $slnA) -and $i1.solutionSource -eq 'ide-live') "S1: the IDE's A should be ide-live, got '$($i1.solutionPath)' / '$($i1.solutionSource)'"
    Assert-That ($i1.versionName -eq $v1) "S1: version should be V1 '$v1', got '$($i1.versionName)'"
    $redName = [string]$i1.redFileName
    if (-not $redName) { Write-Host "COULD NOT RUN: V1 '$v1' names no redirection file (answer: $($i1 | ConvertTo-Json -Compress))." -ForegroundColor Yellow; exit 2 }
    $dA = Tool 'lsp_diagnostics' @{ file_path = $clwA; timeout_ms = 15000 }
    Show 'S1 lsp_diagnostics' $dA
    if ((LoggedSolutions).Count -eq 0) {
        Write-Host "COULD NOT RUN: the scripted language server did not start or log (answer: $dA)." -ForegroundColor Yellow
        exit 2
    }
    Assert-That (SamePath (LoggedSolutions)[-1] $slnA) "S1: the language server should serve A, log: $((LoggedSolutions) -join ' ; ')"
    $startsA = (LoggedSolutions).Count

    # ---- S2: THE DEFECT - the IDE opens B on the same Clarion; B has its own local .red ----
    $localRed = Join-Path $solB $redName
    [System.IO.File]::WriteAllText($localRed, "[Common]`r`n*.clw = .`r`n*.inc = .`r`n", (New-Object System.Text.UTF8Encoding($false)))
    Publish $slnB $v1 'Release'
    $i2 = Json (Tool 'get_solution_info' @{})
    Show 'S2 get_solution_info' ("" + $i2.solutionPath + " | " + $i2.solutionSource + " | from " + $i2.switchedFrom + " | " + $i2.activeRedFile)
    Assert-That (SamePath $i2.solutionPath $slnB) "S2: the tab should follow the IDE to B, got '$($i2.solutionPath)'"
    Assert-That ($i2.solutionSource -eq 'ide-live') "S2: solutionSource should be ide-live, got '$($i2.solutionSource)'"
    Assert-That (SamePath $i2.switchedFrom $slnA) "S2: switchedFrom should name A, got '$($i2.switchedFrom)'"
    Assert-That (SamePath $i2.activeRedFile $localRed) "S2: the .red should be B's own '$localRed' (same version - keyed on the solution), got '$($i2.activeRedFile)'"
    Assert-That (SamePath $i2.databasePath (Join-Path $solB "$nameB.codegraph.db")) "S2: the CodeGraph db should be B's, got '$($i2.databasePath)'"
    $dB = Tool 'lsp_diagnostics' @{ file_path = $clwB; timeout_ms = 15000 }
    Show 'S2 updatePaths log' ((LoggedSolutions) -join ' ; ')
    Assert-That ((LoggedSolutions).Count -gt $startsA -and (SamePath (LoggedSolutions)[-1] $slnB)) "S2: the language server should have restarted on B, log: $((LoggedSolutions) -join ' ; ')"
    # 5a5581e6 x 9e1bd280: the build configuration and the project folder are B's - from B's record.
    $g2 = Json (Tool 'get_red_search_paths' @{ extension = 'clw'; project_name = 'ctrl' })
    Show 'S2 get_red_search_paths' ("" + $g2.buildConfiguration + " | " + $g2.baseDir + " | " + $g2.configurationNote)
    Assert-That ($g2.buildConfiguration -eq 'Release') "S2: the configuration should be B's record's 'Release', got '$($g2.buildConfiguration)' ($($g2.configurationNote))"
    Assert-That (SamePath $g2.baseDir $solB) "S2: project_name 'ctrl' should resolve in B's folder, got '$($g2.baseDir)'"

    # ---- S3: the IDE puts B on V2: the version is re-resolved for B ----
    Publish $slnB $v2
    $i3 = Json (Tool 'get_solution_info' @{})
    Show 'S3 get_solution_info' ("" + $i3.solutionPath + " | " + $i3.versionName + " | " + $i3.versionNote)
    Assert-That ((SamePath $i3.solutionPath $slnB) -and $i3.versionName -eq $v2) "S3: B on V2 should report V2 '$v2', got '$($i3.versionName)' for '$($i3.solutionPath)'"

    # ---- S5: back to A, whose record names no build configuration: never B's 'Release' ----
    [System.IO.File]::WriteAllText((Join-Path $solA $redName), "[Common]`r`n*.clw = .`r`n*.inc = .`r`n", (New-Object System.Text.UTF8Encoding($false)))
    Publish $slnA $v1
    $g5 = Json (Tool 'get_red_search_paths' @{ extension = 'clw'; project_name = 'ctrl' })
    Show 'S5 get_red_search_paths' ("" + $g5.buildConfiguration + " | " + $g5.baseDir + " | " + $g5.configurationNote)
    Assert-That ($g5 -and $g5.buildConfiguration -ne 'Release') "S5: after the switch to A the configuration must not be B's 'Release', got '$($g5.buildConfiguration)' ($($g5.configurationNote))"
    Assert-That (SamePath $g5.baseDir $solA) "S5: project_name 'ctrl' should resolve in A's folder, got '$($g5.baseDir)'"

    # ---- S6: A's record names 'Profile' ----
    Publish $slnA $v1 'Profile'
    $g6 = Json (Tool 'get_red_search_paths' @{ extension = 'clw'; project_name = 'ctrl' })
    Show 'S6 get_red_search_paths' ("" + $g6.buildConfiguration + " | " + $g6.configurationNote)
    Assert-That ($g6.buildConfiguration -eq 'Profile') "S6: the configuration should be A's record's 'Profile', got '$($g6.buildConfiguration)' ($($g6.configurationNote))"

    # ---- S7/S8 (run 1, debugger + Codex): an index run is ONE solution, even when the IDE switches mid-run ----
    # The server pauses each run between snapshotting its solution and resolving that solution's .red
    # (IndexRunGate.PauseBeforeSetUpForTest); the IDE switches exactly then. A and B each have their own local .red.
    function SwitchDuringIndex([string]$tool, [string]$switchTo) {
        [System.IO.File]::WriteAllText($pause, '')
        if (Test-Path "$pause.reached") { Remove-Item "$pause.reached" -Force }
        $id = Begin $tool @{}
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        while (-not (Test-Path "$pause.reached") -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        $reached = Test-Path "$pause.reached"
        Publish $switchTo $v1
        Remove-Item $pause -Force
        return @{ reached = $reached; answer = (Answer $id) }
    }
    $redA = Join-Path $solA $redName
    $redB = Join-Path $solB $redName

    # S7: index_codegraph on A (followed since S5), the IDE switches to B mid-run.
    $r7 = SwitchDuringIndex 'index_codegraph' $slnB
    Show 'S7 index_codegraph' $r7.answer
    if (-not $r7.reached) { Write-Host "COULD NOT RUN: index_codegraph never reached the set-up pause (answer: $($r7.answer))." -ForegroundColor Yellow; exit 2 }
    Assert-That ($r7.answer -match [regex]::Escape("Database: " + (Join-Path $solA "$nameA.codegraph.db"))) "S7: index_codegraph should index A's db, got: $($r7.answer)"
    Assert-That ($r7.answer -match ("Redirection: " + [regex]::Escape($redA))) "S7: A's run must use A's .red '$redA' though the IDE switched to B, got: $($r7.answer)"

    # S8: index_solution on B (followed now), the IDE switches back to A mid-run. Its answer has no summary; the run log does.
    $r8 = SwitchDuringIndex 'index_solution' $slnA
    Show 'S8 index_solution' $r8.answer
    if (-not $r8.reached) { Write-Host "COULD NOT RUN: index_solution never reached the set-up pause (answer: $($r8.answer))." -ForegroundColor Yellow; exit 2 }
    $logB = Join-Path $runLogDir "codegraph-index-$nameB.log"
    $redLine = if (Test-Path $logB) { (Get-Content $logB | Where-Object { $_ -match 'Redirection file:' } | Select-Object -First 1) } else { $null }
    Show 'S8 run log' $redLine
    Assert-That ($redLine -and $redLine -match [regex]::Escape($redB)) "S8: B's run must use B's .red '$redB' though the IDE switched to A, log line: '$redLine'"
    $i8 = Json (Tool 'get_solution_info' @{})
    Assert-That ((SamePath $i8.solutionPath $slnA) -and (SamePath $i8.activeRedFile $redA)) "S8: after the run the tab follows A with A's .red, got '$($i8.solutionPath)' / '$($i8.activeRedFile)'"

    # ---- S9 (run 2): index_codegraph with an explicit sln_path that is NOT the followed solution (A) uses B's own .red ----
    $r9 = Tool 'index_codegraph' @{ sln_path = $slnB }
    Show 'S9 index_codegraph(B)' $r9
    # B is not open in the IDE, so the IDE's version choice (V1) is A's, not B's; this test server sits outside any
    # Clarion tree, so B has no version and no .red here - but NEVER the followed A's .red. (In a Clarion tree B gets
    # the host version and its own local .red.)
    $red9 = if ($r9 -match 'Redirection: ([^\r\n]+)') { $Matches[1].Trim() } else { '' }
    Show 'S9 redirection' $red9
    Assert-That ($red9 -ne '' -and -not (SamePath $red9 $redA)) "S9: an explicit sln_path B must not index with the followed A's .red '$redA', got: '$red9'"

    # ---- S4: the IDE closes A: none, never the old solution ----
    Publish $null
    $i4 = Json (Tool 'get_solution_info' @{})
    Show 'S4 get_solution_info' ("" + $i4.solutionPath + " | " + $i4.solutionSource + " | from " + $i4.switchedFrom + " | " + $i4.note)
    Assert-That ($i4.solutionSource -eq 'none') "S4: the IDE closed its solution: solutionSource should be none, got '$($i4.solutionSource)'"
    Assert-That ($i4.solutionPath -eq '(none open)') "S4: solutionPath should be '(none open)', got '$($i4.solutionPath)'"
    Assert-That (SamePath $i4.switchedFrom $slnA) "S4: switchedFrom should name A, got '$($i4.switchedFrom)'"
    $q4 = Tool 'query_codegraph' @{ sql = 'SELECT 1' }
    Show 'S4 query_codegraph' $q4
    Assert-That ($q4 -match 'No solution is open in the IDE') "S4: query_codegraph should say no solution is open in the IDE, got: $q4"
    $l4 = Tool 'lsp_start' @{}
    Show 'S4 lsp_start' $l4
    Assert-That ($l4 -match 'no longer has a solution open|no solution') "S4: the LSP should follow the close, got: $l4"
}
finally {
    if ($p -and -not $p.HasExited) {
        try { $p.StandardInput.Close() } catch { }
        if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch { } }
    }
    try { Publish $null } catch { }
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    try { Get-ChildItem $runLogDir -Filter "codegraph-index-*$tag*.log" -ErrorAction SilentlyContinue | Remove-Item -Force } catch { }
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS - $assertions assertions" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $($failures.Count) of $assertions assertions:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
