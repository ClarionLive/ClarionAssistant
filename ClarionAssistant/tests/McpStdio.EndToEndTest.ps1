# End-to-end test for the standalone MCP server's stdio transport (ticket d051fbd1).
#
# WHY A SUBPROCESS AND NOT JUST --selftest-stdio. The in-process self-test drives the real
# read/dispatch/write loop, but over a StringReader/StringWriter. Three things that decide
# whether an MCP client can actually talk to this server exist ONLY when the process owns its
# own console, and are therefore invisible to it:
#
#   1. the stdout hijack (Console.SetOut -> stderr), which stops a stray Console.WriteLine
#      anywhere in ~4,500 lines of tool registry from injecting a non-JSON line;
#   2. the UTF-8-no-BOM encoding on the real stdout handle, where the Windows default is the
#      OEM code page;
#   3. clean exit on stdin EOF, which is how a client shuts the server down.
#
# Run:  powershell -ExecutionPolicy Bypass -File ClarionAssistant\tests\McpStdio.EndToEndTest.ps1

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0

function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

# Print a per-block "ok" ONLY if no assertion failed since the block began. An unconditional
# Write-Host reports success next to the failures it contradicts — which is exactly what the
# red-test run did before this was added.
function Report-Block([int]$startFailureCount, [string]$message) {
    if ($script:failures.Count -eq $startFailureCount) { Write-Host "  ok  $message" }
}

$exe = Join-Path $PSScriptRoot '..\mcp-server\bin\Debug\clarion-mcp-server.exe'
$exe = [System.IO.Path]::GetFullPath($exe)
# Build the subject if it is missing, so this harness is self-contained like the C# ones (which
# compile their own sources with csc). The alternative — skipping — is the trap the suite runner
# warns about: a harness that proves nothing must never read as green, so the fallback below is
# exit 2 ("could not run"), which the runner counts as a failure rather than a pass.
if (-not (Test-Path $exe)) {
    $csproj = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\mcp-server\ClarionMcpServer.csproj'))
    $msbuild = $null
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                              -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
    if (-not $msbuild -or -not (Test-Path $msbuild)) {
        Write-Host "COULD NOT RUN: clarion-mcp-server.exe is not built and MSBuild was not found." -ForegroundColor Red
        Write-Host "  Build with: MSBuild.exe $csproj /t:Build /p:Configuration=Debug /p:Platform=x86" -ForegroundColor Red
        exit 2
    }
    Write-Host "building clarion-mcp-server.exe ..." -ForegroundColor DarkGray
    & $msbuild $csproj /t:Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
        Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red
        exit 2
    }
}

# Drive the server: write each line to stdin, close it, read everything back.
function Invoke-Server([string[]]$requests, [string[]]$extraArgs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($extraArgs -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)

    $p = [System.Diagnostics.Process]::Start($psi)

    # Read both streams asynchronously BEFORE writing: a server that answers while we are still
    # writing would otherwise fill the pipe buffer and deadlock both sides.
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()

    foreach ($r in $requests) { $p.StandardInput.WriteLine($r) }
    $p.StandardInput.Close()

    if (-not $p.WaitForExit(60000)) {
        try { $p.Kill() } catch { }
        throw "server did not exit within 60s of stdin EOF"
    }
    # .NET async reads can still have buffered content after WaitForExit; awaiting is what
    # guarantees we see everything the process wrote.
    return [pscustomobject]@{
        Stdout   = $stdoutTask.GetAwaiter().GetResult()
        Stderr   = $stderrTask.GetAwaiter().GetResult()
        ExitCode = $p.ExitCode
    }
}

Write-Host "=== MCP stdio end-to-end ($exe) ===" -ForegroundColor Cyan

# ---------------------------------------------------------------- 1. normal conversation
$blockStart = $failures.Count
$requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"e2e-test","version":"1.0"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    '{"jsonrpc":"2.0","id":3,"method":"ping","params":{}}'
)
$r = Invoke-Server $requests @('--stdio')

Assert-That ($r.ExitCode -eq 0) "expected exit 0 on stdin EOF, got $($r.ExitCode). stderr: $($r.Stderr)"

$frames = @($r.Stdout -split "`n" | Where-Object { $_.Trim().Length -gt 0 })
Assert-That ($frames.Count -eq 3) "expected 3 frames (initialize, tools/list, ping); got $($frames.Count)"

# Every frame must be independently parseable JSON. This is the property that makes the stream
# a protocol rather than a log.
$parsed = @()
foreach ($f in $frames) {
    try { $parsed += ($f | ConvertFrom-Json) }
    catch { $failures.Add("frame is not valid JSON: $($f.Substring(0, [Math]::Min(120, $f.Length)))") }
    $assertions++
}

if ($parsed.Count -ge 1) {
    Assert-That ($parsed[0].result.serverInfo.name -eq 'clarion-mcp-server') `
        "initialize must identify as clarion-mcp-server, got '$($parsed[0].result.serverInfo.name)'"
    Assert-That ($parsed[0].result.protocolVersion -eq '2025-03-26') `
        "unexpected protocolVersion '$($parsed[0].result.protocolVersion)'"
    Assert-That ($parsed[0].id -eq 1) "initialize response id must echo the request id"
}

if ($parsed.Count -ge 2) {
    $toolCount = @($parsed[1].result.tools).Count
    Assert-That ($toolCount -eq 57) "expected 57 tools advertised standalone, got $toolCount"

    # The gate must hold on the WIRE, not just in the registry: no IDE-only tool may be listed.
    $names = @($parsed[1].result.tools | ForEach-Object { $_.name })
    foreach ($banned in @('get_active_file', 'insert_text', 'open_procedure', 'export_dctx')) {
        Assert-That (-not ($names -contains $banned)) `
            "IDE-only tool '$banned' was advertised by a server with no IDE"
    }
    # ...and the tools this ticket exists to deliver must be present.
    foreach ($wanted in @('query_docs', 'lsp_find_symbol', 'query_knowledge', 'read_file')) {
        Assert-That ($names -contains $wanted) "expected tool '$wanted' to be advertised"
    }
    Report-Block $blockStart "57 tools advertised; IDE-only withheld, agnostic present"
}

# The notification must NOT have been answered — 3 frames for 4 inputs proves it.
Assert-That ($frames.Count -eq 3) "a notification was answered (would give 4 frames)"

# ---------------------------------------------------------------- 2. a real tool call
$blockStart = $failures.Count
$callReq = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_directory","arguments":{"path":"' +
        ($PSScriptRoot -replace '\\', '\\\\') + '"}}}'
)
$r2 = Invoke-Server $callReq @('--stdio')
Assert-That ($r2.ExitCode -eq 0) "tool-call run exited $($r2.ExitCode). stderr: $($r2.Stderr)"

$frames2 = @($r2.Stdout -split "`n" | Where-Object { $_.Trim().Length -gt 0 })
Assert-That ($frames2.Count -eq 2) "expected 2 frames from the tool-call run, got $($frames2.Count)"
if ($frames2.Count -ge 2) {
    $callResult = $frames2[1] | ConvertFrom-Json
    Assert-That ($null -ne $callResult.result.content) "tools/call returned no content block"
    $text = $callResult.result.content[0].text
    Assert-That ($callResult.result.isError -ne $true) "tools/call reported isError: $text"
    Assert-That ($text -match 'McpStdio') `
        "list_directory did not list this test's own directory; got: $($text.Substring(0, [Math]::Min(160, $text.Length)))"
    Report-Block $blockStart "tools/call executed a real tool and returned its output"
}

# ---------------------------------------------------------------- 3. stdout-hijack control
$blockStart = $failures.Count
# THE POINT OF THIS BLOCK. The hijack is a guard that passes by finding nothing, so on its own it
# is indistinguishable from no guard at all — delete the Console.SetOut line and every other
# assertion here still passes. --stdio-noise emits a Console.WriteLine after the redirect: it MUST
# surface on stderr and MUST NOT corrupt the protocol stream.
$r3 = Invoke-Server @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}') @('--stdio-noise')

Assert-That ($r3.Stderr -match 'STRAY-CONSOLE-WRITE') `
    "negative control did not fire: the stray write never appeared on stderr, so this proves nothing"
Assert-That ($r3.Stdout -notmatch 'STRAY-CONSOLE-WRITE') `
    "STDOUT HIJACK BROKEN: a stray Console.WriteLine reached the protocol stream"

$frames3 = @($r3.Stdout -split "`n" | Where-Object { $_.Trim().Length -gt 0 })
Assert-That ($frames3.Count -eq 1) "expected 1 frame under noise, got $($frames3.Count)"
if ($frames3.Count -ge 1) {
    try { $null = $frames3[0] | ConvertFrom-Json; $assertions++ }
    catch { $failures.Add("protocol stream corrupted by stray output: $($frames3[0])"); $assertions++ }
}
Report-Block $blockStart "stray Console.WriteLine landed on stderr, protocol stream stayed clean"

# ---------------------------------------------------------------- results
Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "FAILED - $($failures.Count) of $assertions assertions" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
Write-Host "PASSED - $assertions assertions" -ForegroundColor Green
exit 0
