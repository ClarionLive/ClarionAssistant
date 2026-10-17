# The plugin launcher (marketplace\plugins\clarion-assistant\launcher\clarion-tools.cmd) starts the server the
# project's Clarion calls for, not the newest one (ticket e1df6c2e, GH #244 item 2).
#
# THE DEFECT. The launcher took the NEWEST Clarion that had clarion-mcp-server.exe, whatever the project. A
# Clarion 11 solution was served by Clarion 12's server: its install tree and its bundled language server. The
# choice itself now lives in the server (clarion-mcp-server --pick-server, tested in StandaloneConfigDir.Test.cs);
# this harness tests the LAUNCHER's half: that it asks, runs the answer, and stays out of stdout.
#
# HOW. The REAL clarion-tools.cmd, run by cmd.exe, against two fake Clarion roots (with spaces in their names)
# named in CLARION_ROOTS, each holding a fake clarion-mcp-server.exe compiled here. The fake logs every call and,
# for --pick-server, prints whatever FAKE_PICK says. Nothing on this machine's real Clarion installs is run.
#
#   A  bootstrap picks the OTHER root    -> that root's server serves, CLARION_MCP_LAUNCHER=pick, args forwarded
#   B  bootstrap is an old server (exit 64, no stdout) -> the bootstrap serves, CLARION_MCP_LAUNCHER=bootstrap
#   C  the pick names a missing file     -> the bootstrap serves
#   D  CLARION_MCP_SERVER is set         -> that server serves, override, and NO pick is asked
#   E  a stale CLARION_MCP_LAUNCHER=pick in the environment is not passed on as a pick
#   *  stdout carries ONLY the served server's output: the pick's line never leaks into the MCP stream
#
# MUST BE ABLE TO GO RED, and proves it: a copy of the launcher with step 3 (the pick) removed is run through case
# A, and must FAIL it. If that copy passes, case A cannot detect a launcher that never asks, and this exits 1.
#
# Windows PowerShell 5.1 compatible (no PS6+ syntax), ASCII only.
# Exit: 0 pass, 1 fail, 2 could-not-run.

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0
function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$launcher = Join-Path $repoRoot 'marketplace\plugins\clarion-assistant\launcher\clarion-tools.cmd'
if (-not (Test-Path $launcher)) { Write-Host "COULD NOT RUN: launcher not found at $launcher" -ForegroundColor Red; exit 2 }

# ---------------------------------------------------------------- csc
$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $vsRoot = & $vswhere -latest -products * -property installationPath
    if ($vsRoot) {
        $roslyn = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
        if (Test-Path $roslyn) { $csc = $roslyn }
    }
}
if (-not $csc) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host "COULD NOT RUN: csc.exe not found." -ForegroundColor Red; exit 2 }

$work = Join-Path $env:TEMP ("ca-launcher-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$rel  = 'accessory\addins\ClarionAssistant\clarion-mcp-server.exe'
$rootA = Join-Path $work 'Clarion A one'
$rootB = Join-Path $work 'Clarion B two'
$exeA = Join-Path $rootA $rel
$exeB = Join-Path $rootB $rel
$log  = Join-Path $work 'calls.log'

$fakeSource = @'
using System;
using System.IO;
static class FakeServer
{
    static int Main(string[] a)
    {
        string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string log = Environment.GetEnvironmentVariable("FAKE_LOG");
        string mode = Environment.GetEnvironmentVariable("CLARION_MCP_LAUNCHER") ?? "";
        if (a.Length > 0 && a[0] == "--pick-server")
        {
            File.AppendAllText(log, "PICK|" + self + "|" + string.Join(" ", a) + "\r\n");
            string code = Environment.GetEnvironmentVariable("FAKE_PICK_EXIT");
            if (code == "64") { Console.Error.WriteLine("fake: unrecognised arguments."); return 64; }
            string pick = Environment.GetEnvironmentVariable("FAKE_PICK");
            Console.Error.WriteLine("clarion-tools: fake pick " + pick);
            if (!string.IsNullOrEmpty(pick)) Console.WriteLine(pick);
            return 0;
        }
        File.AppendAllText(log, "SERVE|" + self + "|" + mode + "|" + string.Join(" ", a) + "\r\n");
        Console.WriteLine("{\"served\":\"" + Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(self))))) + "\"}");
        return 0;
    }
}
'@

try {
    New-Item -ItemType Directory -Force (Split-Path $exeA), (Split-Path $exeB) | Out-Null
    $src = Join-Path $work 'fake.cs'
    [System.IO.File]::WriteAllText($src, $fakeSource, (New-Object System.Text.UTF8Encoding($false)))
    & $csc /nologo /out:$exeA $src | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: the fake server did not compile." -ForegroundColor Red; exit 2 }
    Copy-Item $exeA $exeB -Force

    # A launcher with step 3 removed: the negative control.
    $mutated = Join-Path $work 'clarion-tools-nopick.cmd'
    $lines = [System.IO.File]::ReadAllLines($launcher)
    $keep = New-Object System.Collections.Generic.List[string]
    $skipping = $false
    foreach ($l in $lines) {
        if ($l -like 'REM 3. *') { $skipping = $true }
        if ($skipping -and $l -like 'REM Hand over.*') { $skipping = $false }
        if (-not $skipping) { $keep.Add($l) }
    }
    [System.IO.File]::WriteAllText($mutated, ([string]::Join("`r`n", $keep.ToArray()) + "`r`n"), (New-Object System.Text.ASCIIEncoding))
    if ($keep.Count -ge $lines.Count) { Write-Host "COULD NOT RUN: could not find step 3 in the launcher to mutate." -ForegroundColor Red; exit 2 }

    function Invoke-Launcher([string]$script, [hashtable]$envVars, [string]$launcherArgs) {
        if (Test-Path $log) { Remove-Item $log -Force }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = Join-Path $env:WINDIR 'System32\cmd.exe'
        $psi.Arguments = '/d /c ""' + $script + '" ' + $launcherArgs + '"'
        $psi.WorkingDirectory = $work
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        foreach ($k in @('CLARION_MCP_SERVER', 'CLARION_MCP_LAUNCHER', 'FAKE_PICK', 'FAKE_PICK_EXIT')) {
            if ($psi.EnvironmentVariables.ContainsKey($k)) { $psi.EnvironmentVariables.Remove($k) }
        }
        $psi.EnvironmentVariables['FAKE_LOG'] = $log
        $psi.EnvironmentVariables['CLARION_ROOTS'] = $rootA + ';' + $rootB
        foreach ($k in $envVars.Keys) { $psi.EnvironmentVariables[$k] = [string]$envVars[$k] }
        $p = [System.Diagnostics.Process]::Start($psi)
        $errTask = $p.StandardError.ReadToEndAsync()
        $out = $p.StandardOutput.ReadToEnd()
        if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch { }; throw "launcher did not exit" }
        $calls = @()
        if (Test-Path $log) { $calls = @([System.IO.File]::ReadAllLines($log) | Where-Object { $_ }) }
        return New-Object PSObject -Property @{ Out = $out; Err = $errTask.Result; Code = $p.ExitCode; Calls = $calls }
    }
    function Serves($r) { return @($r.Calls | Where-Object { $_ -like 'SERVE|*' }) }
    function Picks($r)  { return @($r.Calls | Where-Object { $_ -like 'PICK|*' }) }
    function Show([string]$label, $r) {
        Write-Host ("  {0,-3} exit={1} stdout={2} calls={3}" -f $label, $r.Code, ($r.Out -replace '\s+', ' '), ($r.Calls -join ' ; ')) -ForegroundColor DarkGray
    }
    $args1 = '--stdio --solution "C:\some folder\app.sln"'

    # ---- A ----
    $a = Invoke-Launcher $launcher @{ FAKE_PICK = $exeB } $args1
    Show 'A' $a
    $s = @(Serves $a)
    Assert-That (@(Picks $a).Count -eq 1 -and @(Picks $a)[0] -like ("PICK|" + $exeA + "|--pick-server --stdio --solution*")) "A: the bootstrap (root A) should be asked --pick-server once, with the client's args; calls: $($a.Calls -join ' ; ')"
    Assert-That ($s.Count -eq 1 -and $s[0] -like ("SERVE|" + $exeB + "|pick|*")) "A: root B's server should serve with CLARION_MCP_LAUNCHER=pick; calls: $($a.Calls -join ' ; ')"
    Assert-That ($s.Count -eq 1 -and $s[0] -like '*|--stdio --solution C:\some folder\app.sln') "A: the client's args (a quoted path with a space) should reach the server intact; calls: $($a.Calls -join ' ; ')"
    Assert-That ($a.Out.Trim() -eq '{"served":"Clarion B two"}') "A: stdout must carry only the served server's output (no pick line), got: $($a.Out)"
    Assert-That ($a.Err -match 'fake pick') "A: the pick's reason should reach stderr, got: $($a.Err)"

    # ---- B: an old bootstrap that does not know --pick-server ----
    $b = Invoke-Launcher $launcher @{ FAKE_PICK_EXIT = '64' } '--stdio'
    Show 'B' $b
    $s = @(Serves $b)
    Assert-That ($s.Count -eq 1 -and $s[0] -like ("SERVE|" + $exeA + "|bootstrap|*")) "B: an old bootstrap should serve itself, marked bootstrap; calls: $($b.Calls -join ' ; ')"
    Assert-That ($b.Out.Trim() -eq '{"served":"Clarion A one"}') "B: stdout must carry only the served output, got: $($b.Out)"

    # ---- C: the pick names a file that does not exist ----
    $c = Invoke-Launcher $launcher @{ FAKE_PICK = (Join-Path $work 'nope\clarion-mcp-server.exe') } '--stdio'
    Show 'C' $c
    $s = @(Serves $c)
    Assert-That ($s.Count -eq 1 -and $s[0] -like ("SERVE|" + $exeA + "|bootstrap|*")) "C: a missing pick should leave the bootstrap serving; calls: $($c.Calls -join ' ; ')"

    # ---- D: CLARION_MCP_SERVER ----
    $d = Invoke-Launcher $launcher @{ CLARION_MCP_SERVER = $exeB; FAKE_PICK = $exeA } '--stdio'
    Show 'D' $d
    $s = @(Serves $d)
    Assert-That (@(Picks $d).Count -eq 0) "D: with CLARION_MCP_SERVER set no pick may be asked; calls: $($d.Calls -join ' ; ')"
    Assert-That ($s.Count -eq 1 -and $s[0] -like ("SERVE|" + $exeB + "|override|*")) "D: the named server should serve, marked override; calls: $($d.Calls -join ' ; ')"

    # ---- E: a stale CLARION_MCP_LAUNCHER in the environment ----
    $e = Invoke-Launcher $launcher @{ CLARION_MCP_LAUNCHER = 'pick'; FAKE_PICK_EXIT = '64' } '--stdio'
    Show 'E' $e
    $s = @(Serves $e)
    Assert-That (@(Picks $e).Count -eq 1) "E: a stale CLARION_MCP_LAUNCHER must not stop the launcher asking; calls: $($e.Calls -join ' ; ')"
    Assert-That ($s.Count -eq 1 -and $s[0] -like ("SERVE|" + $exeA + "|bootstrap|*")) "E: the stale 'pick' must not be passed on; calls: $($e.Calls -join ' ; ')"

    # ---- negative control: a launcher that never asks must fail case A ----
    $n = Invoke-Launcher $mutated @{ FAKE_PICK = $exeB } $args1
    Show 'neg' $n
    $s = @(Serves $n)
    $wouldPass = ($s.Count -eq 1 -and $s[0] -like ("SERVE|" + $exeB + "|pick|*"))
    Assert-That (-not $wouldPass) "NEGATIVE CONTROL: the launcher with the pick removed still passed case A - the check cannot fail"
    if (-not $wouldPass) { Write-Host "  ok  negative control: the pick-less launcher fails case A (served: $($s -join ' ; '))" -ForegroundColor DarkGray }
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS - $assertions assertions" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $($failures.Count) of $assertions assertions:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
