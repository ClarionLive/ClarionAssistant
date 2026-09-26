# Regression guard for 77aceec5: lsp_start must USE workspace_path, and every LSP start path must
# say WHY it did not start instead of blaming a handshake that never happened.
#
# THE DEFECTS THIS EXISTS TO CATCH. Measured live in Clarion 12 (2026-09-25), from a plain Chat tab
# whose standalone clarion-mcp-server had been launched with no --solution:
#
#   lsp_diagnostics                          -> "Error: LSP not running. Start it with lsp_start ..."
#   lsp_start(workspace_path=<clbrws dir>)   -> "Error: LSP server failed to start ... server.js and
#                                                node above are BOTH resolved ... the failure is in
#                                                the client handshake"
#
# Both wrong. lsp_start resolved workspace_path and then called a parameterless EnsureRunning()
# that read only the host's solution hook, so the argument was dropped; the start returned without
# spawning anything ("no solution"), and the caller guessed "handshake". The fix makes the argument
# reach the start and makes the start REPORT its outcome.
#
# WHY A REAL PROCESS. The defect lived in the wiring between the tool handler, LspService and the
# standalone's provider hook - exactly what a stub would re-decide. The server is built and driven
# over stdio as a client would.
#
# NO LANGUAGE SERVER IS REQUIRED. The single-.sln cases accept any outcome that NAMES THE SOLUTION
# (started, no server.js, or a genuine start failure) - what they reject is "no solution" and the
# directory-only text the old code printed. So this runs, and can fail, on a machine without node.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\LspStart.WorkspacePathTest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run (suite counts 2 as a failure, never as green).

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0

function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

# ---------------------------------------------------------------- build the server under test
# ALWAYS build: a suite run after a source edit must not test the previous binary.
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

# ---------------------------------------------------------------- stage the fixtures
# Three directories, each shaped to make exactly one resolver branch decide:
#   empty\   no .sln at all        - the server's working directory, so it resolves NO solution
#   two\     a.sln + b.sln         - ambiguous: must be refused, not guessed
#   one\     ctrl.sln (+ sources)  - exactly one: must be the solution the start uses
$fixtureSrc = Join-Path $PSScriptRoot 'fixtures\lsp-semantic-pass'
if (-not (Test-Path (Join-Path $fixtureSrc 'ctrl.sln'))) {
    Write-Host "COULD NOT RUN: fixture missing at $fixtureSrc" -ForegroundColor Red
    exit 2
}
$work = Join-Path $env:TEMP ("ca-lspstart-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$empty = Join-Path $work 'empty'
$two   = Join-Path $work 'two'
$one   = Join-Path $work 'one'
foreach ($d in @($empty, $two, $one)) { New-Item -ItemType Directory -Force $d | Out-Null }
Copy-Item (Join-Path $fixtureSrc '*') $one -Force
Copy-Item (Join-Path $fixtureSrc 'ctrl.sln') (Join-Path $two 'a.sln')
Copy-Item (Join-Path $fixtureSrc 'ctrl.sln') (Join-Path $two 'b.sln')
$notSln = Join-Path $one 'ctrl.clw'
$oneSln = Join-Path $one 'ctrl.sln'
# Matched by TAIL, not full path: $env:TEMP may be an 8.3 short path (JOHNHI~1) on one side and the
# server may print the long form on the other. The tail still carries the unique run directory.
$oneTail = (Split-Path $work -Leaf) + '\one\ctrl.sln'

function Invoke-Server([string[]]$extraArgs, [string[]]$toolCalls) {
    # MCP stdio framing is newline-delimited JSON. Requests go in one batch, then stdin closes;
    # the server dispatches strictly in order, so call N sees the state calls 1..N-1 left.
    $requests = New-Object System.Collections.Generic.List[string]
    $requests.Add('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"lsp-start-test","version":"1"}}}')
    $requests.Add('{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}')
    $id = 10
    foreach ($c in $toolCalls) {
        $requests.Add('{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":' + $c + '}')
        $id++
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = $exe
    $psi.Arguments = (@('--stdio', '--debug') + $extraArgs) -join ' '
    $psi.WorkingDirectory       = $empty        # nothing to discover: no solution from cwd
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardErrorEncoding  = New-Object System.Text.UTF8Encoding($false)

    $p = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()
    foreach ($r in $requests) { $p.StandardInput.WriteLine($r) }
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(180000)) {
        try { $p.Kill() } catch { }
        Write-Host "COULD NOT RUN: server did not exit within 180s of stdin EOF." -ForegroundColor Red
        exit 2
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()

    $texts = @{}
    foreach ($line in ($stdout -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $msg = $line | ConvertFrom-Json } catch { continue }
        if ($null -eq $msg.id -or $msg.id -lt 10) { continue }
        if ($msg.result -and $msg.result.content) { $texts[[int]$msg.id] = [string]$msg.result.content[0].text }
        elseif ($msg.error) { $texts[[int]$msg.id] = "JSON-RPC error: " + $msg.error.message }
    }
    $out = @()
    for ($i = 0; $i -lt $toolCalls.Count; $i++) {
        $t = $texts[10 + $i]
        if ($null -eq $t) {
            Write-Host "COULD NOT RUN: no tool result for call $($i + 1)." -ForegroundColor Red
            Write-Host ($stderrTask.GetAwaiter().GetResult() -split "`n" | Select-Object -Last 20 | Out-String) -ForegroundColor DarkGray
            exit 2
        }
        $out += $t
    }
    return ,$out
}

function Call([string]$tool, [hashtable]$arguments) {
    return (@{ name = $tool; arguments = $arguments } | ConvertTo-Json -Compress -Depth 5)
}

function Show([string]$label, [string]$text) {
    $flat = ($text -replace "\s+", ' ')
    if ($flat.Length -gt 220) { $flat = $flat.Substring(0, 220) + '...' }
    Write-Host ("  {0,-26} {1}" -f $label, $flat) -ForegroundColor DarkGray
}

try {
    # ============================================================ RUN 1: no solution anywhere
    # Error cases FIRST: once a start succeeds the rest would read "already running". The single
    # .sln case is last for that reason.
    $r = Invoke-Server @() @(
        (Call 'lsp_diagnostics' @{ file_path = $notSln })          # 0 auto-start, no solution
        (Call 'lsp_start' @{})                                     # 1 no args, no solution
        (Call 'lsp_start' @{ workspace_path = $two })              # 2 two .sln files
        (Call 'lsp_start' @{ workspace_path = $empty })            # 3 no .sln
        (Call 'lsp_start' @{ workspace_path = $notSln })           # 4 a file that is not a .sln
        (Call 'lsp_debug_status' @{})                              # 5 never started: say why
        (Call 'lsp_start' @{ workspace_path = $one })              # 6 exactly one .sln (dir form)
    )
    $labels = 'diagnostics/no-solution', 'start/no-args', 'start/two-sln', 'start/empty-dir',
              'start/not-sln-file', 'debug_status/never', 'start/one-sln-dir'
    for ($i = 0; $i -lt $r.Count; $i++) { Show $labels[$i] $r[$i] }

    # -- 1. auto-start paths carry the REASON, not a bare "LSP not running" ---------------------
    Assert-That ($r[0] -match 'No solution selected') `
        "lsp_diagnostics with no solution must say 'No solution selected', got: $($r[0])"
    Assert-That ($r[0] -notmatch 'check Lsp\.ServerPath') `
        "lsp_diagnostics still sends the user to Lsp.ServerPath when the problem is the missing solution"

    # -- 2. lsp_start with nothing to start on says so, and names both ways out ----------------
    Assert-That ($r[1] -match 'No solution selected' -and $r[1] -match 'Work With Open Solution' -and $r[1] -match 'workspace_path') `
        "lsp_start with no solution must say 'No solution selected' and name Work With Open Solution + workspace_path, got: $($r[1])"

    # -- 3. never blame the handshake when nothing was spawned ---------------------------------
    for ($i = 0; $i -le 4; $i++) {
        Assert-That ($r[$i] -notmatch 'handshake') `
            "$($labels[$i]) blamed the client handshake although nothing was spawned: $($r[$i])"
    }

    # -- 4. ambiguous and empty directories are refused, not guessed ---------------------------
    Assert-That ($r[2] -match '2 \.sln files' -and $r[2] -match 'a\.sln' -and $r[2] -match 'b\.sln') `
        "lsp_start on a folder with two .sln files must refuse and name both, got: $($r[2])"
    Assert-That ($r[3] -match 'no \.sln file in') `
        "lsp_start on a folder with no .sln must say so, got: $($r[3])"
    Assert-That ($r[4] -match 'not a \.sln') `
        "lsp_start on a non-.sln file must say so, got: $($r[4])"

    # -- 5. lsp_debug_status no longer stops at "never been started" --------------------------
    Assert-That ($r[5] -match 'lastStartOutcome' -and $r[5] -match 'NoSolution') `
        "lsp_debug_status should carry the last start outcome (NoSolution), got: $($r[5])"

    # -- 6. THE REGRESSION GUARD: workspace_path reaches the start ----------------------------
    # Any outcome is acceptable here EXCEPT "no solution" - but it has to NAME ctrl.sln, which only
    # happens if the argument was carried into the start. The old code printed the directory as a
    # file:/// URI and blamed the handshake.
    Assert-That ($r[6] -notmatch 'No solution selected') `
        "lsp_start(workspace_path=<dir with one .sln>) still reports no solution - the argument was dropped: $($r[6])"
    Assert-That ($r[6].Contains($oneTail)) `
        "lsp_start(workspace_path=<dir with one .sln>) does not name the solution it started on ($oneSln): $($r[6])"
    if ($r[6] -match 'handshake') {
        # A real start failure is allowed to blame the handshake - but only when a start happened,
        # which the line above (naming the .sln) establishes.
        Write-Host "  note: the language server was spawned and failed its start on this machine." -ForegroundColor Yellow
    }

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
