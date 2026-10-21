# Regression guard for ticket 583abdec review M1 (security): index_codegraph's sln_path goes through the path
# policy (GH #266) BEFORE any I/O on it.
#
# THE DEFECT. index_codegraph took sln_path and called File.Exists on it, then claimed the run gate and indexed it.
# File.Exists on a UNC path is an SMB connection that hands the share the user's NTLM credentials, and a path outside
# every allowed root was indexed (read) and its database written - read_file and write_file refuse both.
#
# HOW. The real clarion-mcp-server.exe (standalone, --solution A), with the real clarion-indexer.exe beside it, over
# stdio:
#   G1  sln_path = A's own .sln spelled through the \\localhost\<drive>$ admin share -> refused as UNC, the read_file
#       refusal convention, audited as a DENY, quickly, and no database appears beside A (nothing was indexed through
#       the share - with the old code either the share answered and A was indexed, or File.Exists said "not found").
#   G2  sln_path = B, a solution in a folder outside every allowed root -> refused (outside every allowed root), and
#       no database appears beside B.
#   G3  sln_path = A (inside the solution root) -> indexed, A's database written, no in-process fallback warning.
#
# MUST BE ABLE TO GO RED: on 90477fb G1 and G2 fail (no gate: "solution file not found" or an index of the path).
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\IndexCodeGraphPathGate.StandaloneE2ETest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run (the suite counts 2 as a failure, never as green).

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0
function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if ($condition) { Write-Host "  ok   $message" }
    else { Write-Host "  FAIL $message" -ForegroundColor Red; $script:failures.Add($message) }
}

# ---------------------------------------------------------------- build
$repo    = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { Write-Host "COULD NOT RUN: MSBuild not found." -ForegroundColor Red; exit 2 }
& $msbuild (Join-Path $repo 'mcp-server\ClarionMcpServer.csproj') /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red; exit 2 }
& $msbuild (Join-Path $repo 'indexer\ClarionIndexer.csproj') /t:Restore`;Build /p:Configuration=Debug /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: build of ClarionIndexer.csproj failed." -ForegroundColor Red; exit 2 }
$mcpBin = Join-Path $repo 'mcp-server\bin\Debug'
$idxBin = Join-Path $repo 'indexer\bin\Debug'

# ---------------------------------------------------------------- private bin, two solutions
# A LONG path, not $env:TEMP (that can be the 8.3 form, and answers are compared as strings).
$work = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ("Temp\ca-icgpg-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
$solA = Join-Path $work 'SolA'
$solB = Join-Path $work 'Elsewhere\SolB'
New-Item -ItemType Directory -Force $bin, $solA, $solB | Out-Null
Copy-Item (Join-Path $mcpBin '*') $bin -Recurse -Force
# The deploy puts the indexer beside the server: the exe, its pdb, and both interop folders.
Copy-Item (Join-Path $idxBin 'clarion-indexer.exe') $bin -Force
foreach ($arch in 'x86', 'x64') {
    New-Item -ItemType Directory -Force (Join-Path $bin $arch) | Out-Null
    Copy-Item (Join-Path $idxBin "$arch\SQLite.Interop.dll") (Join-Path $bin $arch) -Force
}
foreach ($d in @($solA, $solB)) { Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-semantic-pass\*') $d -Force }
$tag = [System.IO.Path]::GetFileName($work)
$slnA = Join-Path $solA "IcgA-$tag.sln"; Move-Item (Join-Path $solA 'ctrl.sln') $slnA
$slnB = Join-Path $solB "IcgB-$tag.sln"; Move-Item (Join-Path $solB 'ctrl.sln') $slnB
$dbA = Join-Path $solA "IcgA-$tag.codegraph.db"
$dbB = Join-Path $solB "IcgB-$tag.codegraph.db"
$drive = $slnA.Substring(0, 1)
$uncA = "\\localhost\$drive`$\" + $slnA.Substring(3)
$auditLog = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ClarionAssistant\mcp-path-audit.log'

$p = $null
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = Join-Path $bin 'clarion-mcp-server.exe'
    $psi.Arguments = "--stdio --solution `"$slnA`""
    $psi.WorkingDirectory       = $solA
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardErrorEncoding  = New-Object System.Text.UTF8Encoding($false)
    # This test's roots are the policy's own: no extra roots from the environment.
    $psi.EnvironmentVariables.Remove('CLARION_MCP_ALLOWED_ROOTS')
    $p = [System.Diagnostics.Process]::Start($psi)
    $stderrTask = $p.StandardError.ReadToEndAsync()
    $script:nextId = 1

    function Send([string]$method, $params, [switch]$notify) {
        $msg = @{ jsonrpc = '2.0'; method = $method; params = $params }
        if (-not $notify) { $msg.id = $script:nextId; $script:nextId++ }
        $p.StandardInput.WriteLine(($msg | ConvertTo-Json -Compress -Depth 6))
        $p.StandardInput.Flush()
        if ($notify) { return $null }
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        while ([DateTime]::UtcNow -lt $deadline) {
            $line = $p.StandardOutput.ReadLine()
            if ($null -eq $line) { throw "server closed stdout" }
            try { $r = $line | ConvertFrom-Json } catch { continue }
            if ($r.id -eq $msg.id) {
                if ($r.result -and $r.result.content) { return [string]$r.result.content[0].text }
                return "JSON-RPC error: " + $r.error.message
            }
        }
        throw "no answer to request $($msg.id)"
    }
    function Tool([string]$name, [hashtable]$arguments) { return Send 'tools/call' @{ name = $name; arguments = $arguments } }
    function Show([string]$label, $value) {
        $flat = ([string]$value -replace '\s+', ' '); if ($flat.Length -gt 240) { $flat = $flat.Substring(0, 240) + '...' }
        Write-Host ("  {0,-22} {1}" -f $label, $flat) -ForegroundColor DarkGray
    }
    function AuditLines([string]$needle) {
        if (-not (Test-Path $auditLog)) { return @() }
        $fs = New-Object System.IO.FileStream($auditLog, 'Open', 'Read', 'ReadWrite')
        try { $text = (New-Object System.IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
        return @($text -split "`r?`n" | Where-Object { $_ -match "pid=$($p.Id)\b" -and $_.Contains($needle) })
    }

    [void](Send 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'icg-path-gate-e2e'; version = '1' } })
    [void](Send 'notifications/initialized' @{} -notify)

    # ---- G1: UNC (A itself, through the admin share) ----
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $g1 = Tool 'index_codegraph' @{ sln_path = $uncA; incremental = 'false' }
    $sw.Stop()
    Show 'G1 UNC' $g1
    Assert-That ($g1 -like 'Error: index_codegraph refused by the Clarion Assistant path policy*') "G1: a UNC sln_path is refused with the read_file/write_file refusal convention (got: $g1)"
    Assert-That ($g1 -match 'UNC') "G1: the refusal names the UNC rule"
    Assert-That ($sw.ElapsedMilliseconds -lt 5000) "G1: refused without waiting on the network ($($sw.ElapsedMilliseconds) ms)"
    Assert-That (-not (Test-Path $dbA)) "G1: nothing was indexed through the share (no $dbA)"
    Assert-That ((AuditLines 'index_codegraph').Count -gt 0 -and @(AuditLines 'index_codegraph' | Where-Object { $_ -match "`tDENY`t" -and $_.Contains('\\localhost\') }).Count -gt 0) "G1: the refusal is in the path audit log as a DENY of the UNC path"

    # ---- G2: outside every allowed root ----
    $g2 = Tool 'index_codegraph' @{ sln_path = $slnB; incremental = 'false' }
    Show 'G2 outside' $g2
    Assert-That ($g2 -like 'Error: index_codegraph refused by the Clarion Assistant path policy*' -and $g2 -match 'outside every allowed root') "G2: a solution outside every allowed root is refused (got: $g2)"
    Assert-That (-not (Test-Path $dbB)) "G2: no database was written beside it (no $dbB)"

    # ---- G3: inside the roots ----
    $g3 = Tool 'index_codegraph' @{ sln_path = $slnA; incremental = 'false' }
    Show 'G3 inside' $g3
    Assert-That ($g3 -match [regex]::Escape("Database: $dbA") -and $g3 -match 'Symbols: [1-9]') "G3: A's own solution (inside the roots) is indexed into its database (got: $g3)"
    Assert-That ((Test-Path $dbA) -and $g3 -notmatch 'WARNING: clarion-indexer\.exe') "G3: through the child process (no in-process fallback warning), database written"
}
finally {
    if ($p -and -not $p.HasExited) {
        try { $p.StandardInput.Close() } catch { }
        if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch { } }
    }
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS  IndexCodeGraphPathGate.StandaloneE2ETest ($assertions assertions)"; exit 0 }
Write-Host "FAIL  IndexCodeGraphPathGate.StandaloneE2ETest: $($failures.Count) of $assertions failed" -ForegroundColor Red
exit 1
