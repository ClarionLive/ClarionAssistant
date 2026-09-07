# Regression guard for b7505691: lsp_diagnostics must not report a file CLEAN on the
# first query, when the language server has only sent half its answer.
#
# THE DEFECT THIS EXISTS TO CATCH. The server analyses a document in TWO passes and
# publishes after each - a synchronous pass, then an async semantic pass. One didOpen
# produces:
#
#     publishDiagnostics: 0 entries          <- the wait used to be satisfied by this
#     clarion/symbolsRefreshed
#     publishDiagnostics: 3 entries          <- the actual answer
#
# The undeclared-variable diagnostic exists only in the second pass. So the FIRST
# lsp_diagnostics call on any file returned {"pending":false,"count":0} - "analysed,
# clean" - about a file that was not clean, and every call after the first was correct.
# That is invisible in exactly the usage the tool is registered for: write a file,
# check it once.
#
# WHY IT NEEDS A LIVE SERVER RATHER THAN A UNIT TEST. The bug is a race between two
# notifications from a process we do not control. Nothing that stubs the server can
# reproduce it, because the stub author decides how many times to publish - which is
# the exact thing under test.
#
# THIS HARNESS MUST BE ABLE TO GO RED. Two of the four assertions exist only to stop
# a lazy fix from passing:
#   - a genuinely clean file must STILL report zero. "Always wait longer and return
#     whatever is cached" would satisfy the regression guard alone.
#   - the cross-file globals must NOT be flagged. A fix that gave up and reported
#     everything the server ever said would satisfy the first two.
# Verified red against the pre-fix code, not merely observed green after it.
#
# Run:  powershell -ExecutionPolicy Bypass -File ClarionAssistant\tests\LspDiagnostics.SemanticPassTest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run (suite counts 2 as a failure, never as green).

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0

function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

# ---------------------------------------------------------------- build the server under test
# ALWAYS build, never only-when-missing: a source edit followed by a suite run would otherwise
# silently test the PREVIOUS binary. That is not hypothetical here - the bug this guards was
# first "measured" against a Release exe built four minutes BEFORE the fix it depended on
# landed, and produced a confident, entirely wrong answer.
$exe    = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\mcp-server\bin\Debug\clarion-mcp-server.exe'))
$csproj = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\mcp-server\ClarionMcpServer.csproj'))

$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    Write-Host "COULD NOT RUN: MSBuild was not found, so the server under test cannot be built." -ForegroundColor Red
    exit 2
}
& $msbuild $csproj /t:Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
    Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------- stage the fixture
# Copied to a temp directory rather than driven in place: the run makes the server write a
# .codegraph.db and LSP state next to the .sln, and the repo should not collect those.
$fixtureSrc = Join-Path $PSScriptRoot 'fixtures\lsp-semantic-pass'
if (-not (Test-Path (Join-Path $fixtureSrc 'ctrl.sln'))) {
    Write-Host "COULD NOT RUN: fixture missing at $fixtureSrc" -ForegroundColor Red
    exit 2
}
$work = Join-Path $env:TEMP ("ca-lspdiag-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
Copy-Item (Join-Path $fixtureSrc '*') $work -Force

$sln    = Join-Path $work 'ctrl.sln'
$second = Join-Path $work 'second.clw'   # two undeclared names + four cross-file globals
$clean  = Join-Path $work 'ctrl.clw'     # the clean control

try {
    # ------------------------------------------------------------ drive the server over stdio
    # MCP stdio framing is NEWLINE-DELIMITED JSON, NOT the LSP's Content-Length headers. The two
    # protocols look alike and this project hosts a client for both; StdioTransport.cs warns about
    # precisely this confusion, and getting it wrong presents as a silent hang, not an error.
    #
    # The requests are written in one batch and stdin is then closed. The server's read loop
    # dispatches them strictly in order, so request 1 is still the FIRST query on second.clw and
    # the race is preserved - batching does not weaken the test.
    $requests = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"semantic-pass-test","version":"1"}}}'
        '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
        ('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"file_path":' + (ConvertTo-Json $second) + '}}}')
        ('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"file_path":' + (ConvertTo-Json $clean)  + '}}}')
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = $exe
    # --debug routes the LSP trace to stderr, which is how the preflight below can tell
    # "the server said the file is clean" apart from "the language server never started".
    $psi.Arguments = '--stdio --debug --solution "' + $sln + '"'
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardErrorEncoding  = New-Object System.Text.UTF8Encoding($false)

    $p = [System.Diagnostics.Process]::Start($psi)
    # Read both streams before writing: a server answering while we are still writing would
    # otherwise fill the pipe buffer and deadlock both sides.
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()

    foreach ($r in $requests) { $p.StandardInput.WriteLine($r) }
    $p.StandardInput.Close()

    if (-not $p.WaitForExit(300000)) {
        try { $p.Kill() } catch { }
        Write-Host "COULD NOT RUN: server did not exit within 300s of stdin EOF." -ForegroundColor Red
        exit 2
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    # ------------------------------------------------------------ preflight, before any assertion
    # If the language server never became ready there are no diagnostics to be right or wrong
    # about, and every assertion below would fail for a reason that has nothing to do with the
    # code under test. Report could-not-run instead: a machine without node or without msarson's
    # extension installed has proven nothing either way.
    if ($stderr -notmatch '\[LSP\] Ready') {
        Write-Host "COULD NOT RUN: the language server never reported Ready." -ForegroundColor Yellow
        Write-Host "  Needs node on PATH and msarson's Clarion extension installed." -ForegroundColor Yellow
        Write-Host "  --- server stderr ---" -ForegroundColor DarkGray
        ($stderr -split "`n" | Select-Object -Last 25) | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        exit 2
    }

    # ------------------------------------------------------------ parse the two tool results
    function Get-Diagnostics([string]$raw, [int]$id) {
        foreach ($line in ($raw -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $msg = $line | ConvertFrom-Json } catch { continue }
            if ($msg.id -ne $id) { continue }
            if (-not $msg.result) { return $null }
            return ($msg.result.content[0].text | ConvertFrom-Json)
        }
        return $null
    }

    $dirty = Get-Diagnostics $stdout 2
    $cleanResult = Get-Diagnostics $stdout 3

    if ($null -eq $dirty -or $null -eq $cleanResult) {
        Write-Host "COULD NOT RUN: could not parse a tool result from the server's stdout." -ForegroundColor Red
        exit 2
    }

    $messages = @()
    if ($dirty.diagnostics) { $messages = $dirty.diagnostics | ForEach-Object { $_.message } }

    Write-Host ("  second.clw: pending={0} count={1}" -f $dirty.pending, $dirty.count)
    Write-Host ("  ctrl.clw:   pending={0} count={1}" -f $cleanResult.pending, $cleanResult.count)

    # -- 1. THE REGRESSION GUARD ------------------------------------------------------------
    # Before the fix this was count=0 with pending=false. These two names arrive ONLY in the
    # server's async semantic publish, so seeing them proves the wait no longer settles for the
    # partial first one.
    Assert-That ($messages -join "`n").Contains("'TotallyUndeclaredXyz' is not declared in this file.") `
        "FIRST query on second.clw did not report TotallyUndeclaredXyz (b7505691 has regressed: the partial first publish is being returned again)"
    Assert-That ($messages -join "`n").Contains("'AlsoNotDeclaredAbc' is not declared in this file.") `
        "FIRST query on second.clw did not report AlsoNotDeclaredAbc"

    # -- 2. and it must say so authoritatively ----------------------------------------------
    Assert-That ($dirty.pending -eq $false) `
        "second.clw came back pending:true - the semantic pass did not arrive inside the budget"

    # -- 3. NEGATIVE CONTROL: a genuinely clean file still reports zero ----------------------
    # Without this, "wait longer and return whatever is cached" passes assertion 1 while having
    # traded a wrong answer for a hang.
    Assert-That ($cleanResult.pending -eq $false -and [int]$cleanResult.count -eq 0) `
        ("clean control ctrl.clw should be pending:false count:0, got pending:{0} count:{1}" -f $cleanResult.pending, $cleanResult.count)

    # -- 4. NEGATIVE CONTROL: cross-file globals must NOT be flagged -------------------------
    # Declared in the PROGRAM module and in an INCLUDE, referenced from a MEMBER module. A fix
    # that simply forwarded everything the server ever emitted would trip this.
    foreach ($g in @('MyGlobalVar', 'AnotherGlobal', 'IncGlobalOne', 'IncGlobalTwo')) {
        Assert-That (-not ($messages -join "`n").Contains("'$g' is not declared in this file.")) `
            "cross-file global '$g' was reported undeclared - either the server regressed or our undeclared-identifier filter stopped suppressing it"
    }

    # -- 5. the mechanism itself, so it cannot be quietly reverted ---------------------------
    # clarion/symbolsRefreshed is the boundary marker between the two publishes. It was arriving
    # all along and being discarded as "Ignored notification"; if that line comes back, the fix
    # has been undone even if the timing happens to make the assertions above pass on this run.
    Assert-That ($stderr -notmatch 'Ignored notification: clarion/symbolsRefreshed') `
        "clarion/symbolsRefreshed is being ignored again - the semantic-pass boundary signal is unhandled"
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

# ---------------------------------------------------------------- summary
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "PASS - $assertions assertions" -ForegroundColor Green
    exit 0
}
Write-Host "FAIL - $($failures.Count) of $assertions assertions:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
