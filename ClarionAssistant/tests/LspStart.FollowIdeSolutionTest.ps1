# Regression guard for 77aceec5 (pipeline run 1, B2): a standalone server launched by the IDE with no
# --solution must FOLLOW the solution the IDE publishes, not freeze on the first one it saw.
#
# THE DEFECT. The plain-Chat fallback read the IDE's record once, at start. After that the
# AlreadyRunning fast path answered every call, so a server started on A kept serving A after the
# developer switched the IDE to B - and kept serving it after the IDE closed A altogether.
#
# THE SCENARIO, driven INTERACTIVELY (one request, one answer, then the record changes):
#   record = A   lsp_start      -> started on A
#   record = B   lsp_start      -> restarted on B          (was: "already running for A")
#   record gone  lsp_start      -> NoSolution, stopped     (was: "already running for A")
#                lsp_diagnostics -> not running, no solution
#
# NEEDS A LANGUAGE SERVER: following only matters for a server that actually started. If the first
# start does not report "started", this exits 2 (could not run) - a machine that never started a
# server has proven nothing about restarting one.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\LspStart.FollowIdeSolutionTest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run.

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0
function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

# ---------------------------------------------------------------- build
$exe    = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\mcp-server\bin\Debug\clarion-mcp-server.exe'))
$csproj = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\mcp-server\ClarionMcpServer.csproj'))
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { Write-Host "COULD NOT RUN: MSBuild not found." -ForegroundColor Red; exit 2 }
& $msbuild $csproj /t:Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { Write-Host "COULD NOT RUN: build failed." -ForegroundColor Red; exit 2 }

# ---------------------------------------------------------------- fixtures: two solutions + an empty cwd
$fixtureSrc = Join-Path $PSScriptRoot 'fixtures\lsp-semantic-pass'
if (-not (Test-Path (Join-Path $fixtureSrc 'ctrl.sln'))) { Write-Host "COULD NOT RUN: fixture missing." -ForegroundColor Red; exit 2 }
$work  = Join-Path $env:TEMP ("ca-lspfollow-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$runId = Split-Path $work -Leaf
$empty = Join-Path $work 'empty'
$solA  = Join-Path $work 'solA'
$solB  = Join-Path $work 'solB'
foreach ($d in @($empty, $solA, $solB)) { New-Item -ItemType Directory -Force $d | Out-Null }
Copy-Item (Join-Path $fixtureSrc '*') $solA -Force
Copy-Item (Join-Path $fixtureSrc '*') $solB -Force
$slnA = Join-Path $solA 'ctrl.sln'
$slnB = Join-Path $solB 'ctrl.sln'
$tailA = $runId + '\solA\ctrl.sln'
$tailB = $runId + '\solB\ctrl.sln'

# The record the fake IDE (this process) publishes - same location and shape as
# IdeSolutionRecord.Publish (whose own round trip is IdeSolutionRecord.Test.cs).
$recordDir  = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClarionAssistant\ide-solution'
$recordFile = Join-Path $recordDir ("ide-" + $PID + ".json")
New-Item -ItemType Directory -Force $recordDir | Out-Null
function Publish([string]$sln) {
    if ($null -eq $sln) { if (Test-Path $recordFile) { [System.IO.File]::Delete($recordFile) }; return }
    $rec = @{ solution = $sln; pid = $PID; writtenAt = (Get-Date).ToString('o') } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($recordFile, $rec, (New-Object System.Text.UTF8Encoding($false)))
}

$p = $null
try {
    Publish $slnA

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = $exe
    $psi.Arguments = "--stdio --debug --ide-pid $PID"      # NO --solution: the plain Chat tab
    $psi.WorkingDirectory       = $empty
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardErrorEncoding  = New-Object System.Text.UTF8Encoding($false)
    $p = [System.Diagnostics.Process]::Start($psi)
    $stderrTask = $p.StandardError.ReadToEndAsync()
    $script:nextId = 1

    function Send([string]$method, $params, [switch]$notify) {
        $msg = @{ jsonrpc = '2.0'; method = $method; params = $params }
        if (-not $notify) { $msg.id = $script:nextId; $script:nextId++ }
        $p.StandardInput.WriteLine(($msg | ConvertTo-Json -Compress -Depth 6))
        $p.StandardInput.Flush()
        if ($notify) { return $null }
        $want = $msg.id
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
    function Show([string]$label, [string]$text) {
        $flat = ($text -replace '\s+', ' '); if ($flat.Length -gt 200) { $flat = $flat.Substring(0, 200) + '...' }
        Write-Host ("  {0,-22} {1}" -f $label, $flat) -ForegroundColor DarkGray
    }

    [void](Send 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'lsp-follow-test'; version = '1' } })
    [void](Send 'notifications/initialized' @{} -notify)

    # -- A ---------------------------------------------------------------------------------------
    $r1 = Tool 'lsp_start' @{}
    Show 'record=A  lsp_start' $r1
    if ($r1 -notmatch 'LSP server started for solution') {
        Write-Host "COULD NOT RUN: the language server did not start on A (needs node + the Clarion extension)." -ForegroundColor Yellow
        exit 2
    }
    Assert-That ($r1.Contains($tailA)) "first start is not on A: $r1"

    # -- A -> B ----------------------------------------------------------------------------------
    Publish $slnB
    $r2 = Tool 'lsp_start' @{}
    Show 'record=B  lsp_start' $r2
    Assert-That ($r2 -notmatch 'already running') "after the IDE switched to B the server still reports running (stale A): $r2"
    Assert-That ($r2.Contains($tailB)) "after the IDE switched to B, lsp_start does not name B: $r2"

    # -- B -> none -------------------------------------------------------------------------------
    Publish $null
    $r3 = Tool 'lsp_start' @{}
    Show 'record gone lsp_start' $r3
    Assert-That ($r3 -match 'No solution selected') "after the IDE closed its solution lsp_start does not report no solution: $r3"
    Assert-That ($r3 -notmatch 'already running') "after the IDE closed its solution the server is still reported running: $r3"

    $r4 = Tool 'lsp_diagnostics' @{ file_path = (Join-Path $solB 'ctrl.clw') }
    Show 'record gone diagnostics' $r4
    Assert-That ($r4 -match 'LSP not running' -and $r4 -match 'No solution selected') `
        "after the IDE closed its solution, lsp_diagnostics should say not running / no solution: $r4"

    # -- NEGATIVE CONTROL: an unchanged record does NOT restart --------------------------------
    # Without this, "restart on every call" would pass everything above.
    Publish $slnA
    $r5 = Tool 'lsp_start' @{}
    Show 'record=A  lsp_start' $r5
    $r6 = Tool 'lsp_start' @{}
    Show 'record=A  again' $r6
    Assert-That ($r5.Contains($tailA) -and $r5 -match 'started') "re-publishing A did not start the server on A: $r5"
    Assert-That ($r6 -match 'already running' -and $r6.Contains($tailA)) "an unchanged record restarted the server: $r6"
}
finally {
    if ($p -and -not $p.HasExited) {
        try { $p.StandardInput.Close() } catch { }
        if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch { } }
    }
    try { Publish $null } catch { }
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS - $assertions assertions" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $($failures.Count) of $assertions assertions:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
