# Contract guard for GH #216: lsp_diagnostics gates its answer on clarion/diagnosticsStatus.
#
# THE DEFECT. The readiness wait treated clarion/symbolsRefreshed, or 400ms of silence, as the end of
# analysis. Neither is: when the server DEFERS the async pass (solution or index not ready), the
# stream goes quiet with only the partial first publish banked, and the tool answered
# {"pending":false,"count":0} about a file that was not clean. Server 1.0.4+ closes every analysis
# with clarion/diagnosticsStatus {uri, version, state}; the client must wait for `complete` on the
# uri and version it sent, keep waiting on `deferred`/`superseded`, and still return pending:true
# when the budget runs out.
#
# HOW. The real standalone MCP server (built fresh, as in the other .ps1 harnesses) is pointed at a
# SCRIPTED language server, fixtures\lsp-status-gate\fake-lsp-server.js, through the VSCODE_EXTENSIONS
# discovery root (set for the child process only). The script answers each document with a fixed
# notification sequence chosen by file name. LspDiagnostics.SemanticPassTest.ps1 covers the live
# server; this covers the shapes the live server cannot be made to produce on demand:
#
#   status run (the server sends diagnosticsStatus):
#     deferred.clw   partial publish + deferred, real publish + complete 1200ms later (> the 400ms
#                    settle window). Also the FIRST status the client ever sees, so it proves the
#                    switch to status mode happens mid-wait.                 -> pending:false, 1
#     wronguri.clw   deferred, and the only complete is for ANOTHER uri      -> pending:true
#     stalever.clw   deferred, and the only complete is for an OLDER version -> pending:true
#     superseded.clw superseded, then complete for the NEWER version         -> pending:false, 1
#     clean.clw      one publish + complete                                  -> pending:false, 0
#   nostatus run (an older server that never sends it - the fallback must be today's behaviour):
#     twopass.clw    publish, symbolsRefreshed, semantic publish 200ms later -> pending:false, 1
#     plain.clw      one publish, then silence                               -> pending:false, 0
#   firstcall run (c7878eba; server.js is firstcall-server.js, which sends the status the way the real
#   server does, so LspClient.Start finds the sender in the script):
#     firstcall.clw  partial publish, NO status; full publish + complete 1500ms later. The first call of
#                    the session, so no status has been seen on the wire yet -> pending:false, 2
#   partial run (92d06c29; same server.js as firstcall, publishes carry `version` as v1.0.8's do):
#     partial.clw    versioned partial publish, complete only at 6 s          -> pending:true, partial:true, 1
#     slow.clw       completes at 5 s: at the default 3 s                     -> pending:true, partial:true
#     slow.clw|10000 the same with timeout_ms 10000                           -> pending:false, 2
#     stalepub.clw   the only publish is for an OLDER version                 -> pending:true, partial:false, 0
#
# firstcall goes red on the pre-c7878eba LspClient (it settled on SYNC-PARTIAL after 400ms, pending:false,
# count 1: the false 'complete' a 62k-line module showed on the first call of a session).
#
# MUST BE ABLE TO GO RED: against the pre-#216 LspClient the status cases deferred / wronguri /
# stalever / superseded fail (settled after 400ms as pending:false, count 0). The nostatus cases
# pass there by design - they are what must NOT change - and were shown red against a mutation
# that ignores the fallback (status mode forced on).
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\LspDiagnostics.StatusGateTest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run (suite counts 2 as a failure, never as green).

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0

function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

# ---------------------------------------------------------------- build the server under test
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
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host "COULD NOT RUN: node is not on PATH, so the scripted language server cannot start." -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------- stage
$fakeJs  = Join-Path $PSScriptRoot 'fixtures\lsp-status-gate\fake-lsp-server.js'
$firstJs = Join-Path $PSScriptRoot 'fixtures\lsp-status-gate\firstcall-server.js'
$slnSrc  = Join-Path $PSScriptRoot 'fixtures\lsp-semantic-pass'
if (-not (Test-Path $fakeJs) -or -not (Test-Path $firstJs) -or -not (Test-Path (Join-Path $slnSrc 'ctrl.sln'))) {
    Write-Host "COULD NOT RUN: fixture missing ($fakeJs or $slnSrc\ctrl.sln)" -ForegroundColor Red
    exit 2
}

$work = Join-Path $env:TEMP ("ca-lspstatus-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$src  = Join-Path $work 'src'
# A version far above any real install, so the discovery scan cannot prefer something else in the root.
$ext  = Join-Path $work 'ext\msarson.clarion-extensions-99.0.0\out\server\src'
New-Item -ItemType Directory -Force $src, $ext | Out-Null
Copy-Item (Join-Path $slnSrc '*') $src -Force
Copy-Item $fakeJs (Join-Path $ext 'server.js') -Force
# The firstcall run's own discovery root: firstcall-server.js as server.js, the fake it wraps beside it.
$extFirst = Join-Path $work 'ext-first\msarson.clarion-extensions-99.0.0\out\server\src'
New-Item -ItemType Directory -Force $extFirst | Out-Null
Copy-Item $firstJs (Join-Path $extFirst 'server.js') -Force
Copy-Item $fakeJs (Join-Path $extFirst 'fake-lsp-server.js') -Force

$docs = @('deferred.clw', 'wronguri.clw', 'stalever.clw', 'superseded.clw', 'clean.clw', 'twopass.clw', 'plain.clw', 'firstcall.clw',
          'partial.clw', 'slow.clw', 'stalepub.clw')
foreach ($d in $docs) {
    [System.IO.File]::WriteAllText((Join-Path $src $d), "  MEMBER()`r`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-Server([string]$mode, [string[]]$files, [string]$extRoot = (Join-Path $work 'ext')) {
    $requests = New-Object System.Collections.Generic.List[string]
    $requests.Add('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"status-gate-test","version":"1"}}}')
    $requests.Add('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}')
    $id = 2
    foreach ($f in $files) {
        # "name.clw|10000" passes timeout_ms 10000 (92d06c29); results stay keyed by the whole entry.
        $parts = $f -split '\|'
        $extra = if ($parts.Count -gt 1) { ',"timeout_ms":' + $parts[1] } else { '' }
        $requests.Add('{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"file_path":' + (ConvertTo-Json (Join-Path $src $parts[0])) + $extra + '}}}')
        $id++
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = $exe
    $psi.Arguments = '--stdio --debug --solution "' + (Join-Path $src 'ctrl.sln') + '"'
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardErrorEncoding  = New-Object System.Text.UTF8Encoding($false)
    # Child-only: the developer's own environment is untouched.
    $psi.EnvironmentVariables['VSCODE_EXTENSIONS'] = $extRoot
    $psi.EnvironmentVariables['FAKE_LSP_MODE']     = $mode

    $p = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()
    foreach ($r in $requests) { $p.StandardInput.WriteLine($r) }
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(180000)) {
        try { $p.Kill() } catch { }
        Write-Host "COULD NOT RUN: server did not exit within 180s of stdin EOF ($mode run)." -ForegroundColor Red
        exit 2
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    # Preflight: the SCRIPTED server must be the one that answered. Anything else (a Lsp.ServerPath
    # setting, a bundled lsp-server beside the exe) means these assertions would be about some other
    # server, so they prove nothing either way.
    if ($stderr -notmatch '\[LSP\] Ready' -or $stderr -notmatch '\[fake-lsp\] analyse') {
        Write-Host "COULD NOT RUN: the scripted language server did not answer ($mode run)." -ForegroundColor Yellow
        ($stderr -split "`n" | Select-String 'LspService|\[LSP\]|fake-lsp' | Select-Object -Last 20) |
            ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        exit 2
    }

    $results = @{}
    $id = 2
    foreach ($f in $files) {
        $results[$f] = $null
        foreach ($line in ($stdout -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $msg = $line | ConvertFrom-Json } catch { continue }
            if ($msg.id -ne $id -or -not $msg.result) { continue }
            try { $results[$f] = ($msg.result.content[0].text | ConvertFrom-Json) } catch { }
        }
        $id++
    }
    return @{ Results = $results; Stderr = $stderr }
}

function Show([string]$f, $r) {
    if ($null -eq $r) { Write-Host ("  {0,-18} (no parseable result)" -f $f); return }
    $msgs = @(); if ($r.diagnostics) { $msgs = @($r.diagnostics | ForEach-Object { $_.message }) }
    Write-Host ("  {0,-18} pending={1} partial={2} count={3} {4}" -f $f, $r.pending, $r.partial, $r.count, ($msgs -join ' | '))
}

function Has([object]$r, [string]$text) {
    if ($null -eq $r -or -not $r.diagnostics) { return $false }
    return (@($r.diagnostics | ForEach-Object { $_.message }) -join "`n").Contains($text)
}

try {
    # ------------------------------------------------------------ status run
    $statusFiles = @('deferred.clw', 'wronguri.clw', 'stalever.clw', 'superseded.clw', 'clean.clw')
    $run = Invoke-Server 'status' $statusFiles
    $r = $run.Results
    Write-Host "status run:"
    foreach ($f in $statusFiles) { Show $f $r[$f] }

    # 1. deferred -> complete: the real answer, not the partial publish.
    Assert-That ($null -ne $r['deferred.clw'] -and $r['deferred.clw'].pending -eq $false -and (Has $r['deferred.clw'] 'DEFERRED-REAL')) `
        "deferred.clw: expected pending:false with DEFERRED-REAL (the publish before 'complete'); the wait released on the partial publish or never switched to status mode"

    # 2. a complete for ANOTHER uri must not release this file's wait.
    Assert-That ($null -ne $r['wronguri.clw'] -and $r['wronguri.clw'].pending -eq $true) `
        "wronguri.clw: expected pending:true (only bystander.clw completed); a complete for another uri released the wait"

    # 3. a complete for an OLDER version must not release it either.
    Assert-That ($null -ne $r['stalever.clw'] -and $r['stalever.clw'].pending -eq $true) `
        "stalever.clw: expected pending:true (complete arrived only for version-1); a stale-version complete released the wait"

    # 4. superseded -> the newer version completes: accepted.
    Assert-That ($null -ne $r['superseded.clw'] -and $r['superseded.clw'].pending -eq $false -and (Has $r['superseded.clw'] 'SUPERSEDED-NEWER')) `
        "superseded.clw: expected pending:false with SUPERSEDED-NEWER once the newer version completed"

    # 5. negative control: a genuinely clean file is still answered, and clean.
    Assert-That ($null -ne $r['clean.clw'] -and $r['clean.clw'].pending -eq $false -and [int]$r['clean.clw'].count -eq 0) `
        "clean.clw: expected pending:false count:0"

    # 6. mechanism: the notification is handled, not dropped.
    Assert-That ($run.Stderr -notmatch 'Ignored notification: clarion/diagnosticsStatus') `
        "clarion/diagnosticsStatus is being ignored - the GH #216 gate is unhandled"

    # ------------------------------------------------------------ nostatus run (fallback)
    $fallbackFiles = @('twopass.clw', 'plain.clw')
    $run = Invoke-Server 'nostatus' $fallbackFiles
    $r = $run.Results
    Write-Host "nostatus run:"
    foreach ($f in $fallbackFiles) { Show $f $r[$f] }

    # 7. an older server's two-pass answer is still found via symbolsRefreshed.
    Assert-That ($null -ne $r['twopass.clw'] -and $r['twopass.clw'].pending -eq $false -and (Has $r['twopass.clw'] 'NOSTATUS-SEMANTIC')) `
        "twopass.clw (no diagnosticsStatus): expected pending:false with NOSTATUS-SEMANTIC - the symbolsRefreshed fallback broke"

    # 8. and a clean file on an older server is answered clean, not turned into a timeout.
    Assert-That ($null -ne $r['plain.clw'] -and $r['plain.clw'].pending -eq $false -and [int]$r['plain.clw'].count -eq 0) `
        "plain.clw (no diagnosticsStatus): expected pending:false count:0 - a server that never sends the status must keep the settle-window fallback"

    # ------------------------------------------------------------ firstcall run (c7878eba)
    $run = Invoke-Server 'status' @('firstcall.clw') (Join-Path $work 'ext-first')
    $r = $run.Results
    Write-Host "firstcall run:"
    Show 'firstcall.clw' $r['firstcall.clw']

    # 9. the first call of a session waits for `complete`, not the settle window, when server.js sends the status.
    Assert-That ($null -ne $r['firstcall.clw'] -and $r['firstcall.clw'].pending -eq $false -and [int]$r['firstcall.clw'].count -eq 2 -and (Has $r['firstcall.clw'] 'SEMANTIC-FULL')) `
        "firstcall.clw: expected pending:false count:2 with SEMANTIC-FULL; the first call settled on the partial publish (status mode was not on before the first status)"

    # 10. mechanism: the start-time probe found the sender (not the wire switching over by luck).
    Assert-That ($run.Stderr -match 'server\.js sends clarion/diagnosticsStatus') `
        "firstcall run: LspClient.Start did not report finding the diagnosticsStatus sender in server.js"

    # ------------------------------------------------------------ partial run (92d06c29)
    # Through the firstcall root, so status mode is on from the start, as with the shipped server.
    $partialFiles = @('partial.clw', 'slow.clw', 'slow.clw|10000', 'stalepub.clw')
    $run = Invoke-Server 'status' $partialFiles (Join-Path $work 'ext-first')
    $r = $run.Results
    Write-Host "partial run:"
    foreach ($f in $partialFiles) { Show $f $r[$f] }

    # 11. budget expires on a publish for the current text: pending, flagged partial, its entries returned.
    Assert-That ($null -ne $r['partial.clw'] -and $r['partial.clw'].pending -eq $true -and $r['partial.clw'].partial -eq $true -and [int]$r['partial.clw'].count -eq 1 -and (Has $r['partial.clw'] 'PARTIAL-SO-FAR')) `
        "partial.clw: expected pending:true partial:true count:1 with PARTIAL-SO-FAR (the entries received before the budget ran out)"

    # 12. default budget (3 s) on a file that completes at 5 s: still pending (partial), NOT complete.
    Assert-That ($null -ne $r['slow.clw'] -and $r['slow.clw'].pending -eq $true -and $r['slow.clw'].partial -eq $true -and -not (Has $r['slow.clw'] 'SLOW-FULL')) `
        "slow.clw at the default timeout: expected pending:true partial:true without SLOW-FULL"

    # 13. timeout_ms 10000 waits for the complete answer.
    Assert-That ($null -ne $r['slow.clw|10000'] -and $r['slow.clw|10000'].pending -eq $false -and $r['slow.clw|10000'].partial -eq $false -and [int]$r['slow.clw|10000'].count -eq 2 -and (Has $r['slow.clw|10000'] 'SLOW-FULL')) `
        "slow.clw with timeout_ms 10000: expected pending:false partial:false count:2 with SLOW-FULL - timeout_ms was not honoured"

    # 14. a publish for an OLDER text is never served, not even as partial.
    Assert-That ($null -ne $r['stalepub.clw'] -and $r['stalepub.clw'].pending -eq $true -and $r['stalepub.clw'].partial -eq $false -and [int]$r['stalepub.clw'].count -eq 0) `
        "stalepub.clw: expected pending:true partial:false count:0 - a publish for an older version leaked out as the answer"
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "PASS - $assertions assertions" -ForegroundColor Green
    exit 0
}
Write-Host "FAIL - $($failures.Count) of $assertions assertions:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
