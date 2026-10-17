# The plugin launcher (marketplace\plugins\clarion-assistant\launcher\clarion-tools.cmd) starts the server the
# project's Clarion calls for, not the newest one (ticket e1df6c2e, GH #244 item 2).
#
# THE DEFECT. The launcher took the NEWEST Clarion that had clarion-mcp-server.exe, whatever the project. A
# Clarion 11 solution was served by Clarion 12's server: its install tree and its bundled language server. The
# choice itself now lives in the server (clarion-mcp-server --pick-server, tested in StandaloneConfigDir.Test.cs,
# with its downgrade guard); this harness tests the LAUNCHER's half: that it asks, runs the answer, falls back
# when the answer cannot start, passes paths and arguments through unchanged, and stays out of stdout.
#
# HOW. The REAL clarion-tools.cmd, run by cmd.exe, against two fake Clarion roots named in CLARION_ROOTS, whose
# names carry the characters cmd mangles most easily (spaces, "&", ")", "%", "!"), each holding a fake
# clarion-mcp-server.exe compiled here. The fake logs every call; for --pick-server it writes whatever FAKE_PICK
# says to the --pick-out file; a "broken.flag" beside it makes it fail to start. Nothing real is run.
#
#   A  the pick names the OTHER root       -> that server serves, CLARION_MCP_LAUNCHER=pick, args forwarded EXACTLY
#                                             (an argument with spaces, "!", "&", ")" and a literal "%EXE%")
#   B  an old bootstrap (no --pick-server) -> the bootstrap serves, CLARION_MCP_LAUNCHER=bootstrap
#   C  the pick names a missing file       -> the bootstrap serves
#   D  CLARION_MCP_SERVER is set           -> that server serves, override, and NO pick is asked
#   E  a stale CLARION_MCP_LAUNCHER=pick in the environment is not passed on as a pick
#   F  the picked server cannot start      -> the bootstrap serves, CLARION_MCP_LAUNCHER=fallback, CLARION_MCP_PICKED
#                                             names the failed one, stderr says so
#   *  stdout carries ONLY the served server's output; the pick's stdin is nul (it cannot eat the client's message)
#
# MUST BE ABLE TO GO RED, and proves it on three broken copies of the launcher, each of which must FAIL case A:
#   N1 the pick line removed                (a launcher that never asks)
#   N2 delayed expansion switched back on   (a "!" in the picked path is eaten)
#   N3 the pick run through "call"          (the client's "%EXE%" argument is expanded a second time)
#   N4 the pick's "<nul" removed            (it could read the client's first message off the pipe)
# If any of them passes, case A cannot detect that defect, and this exits 1.
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
# The bootstrap root carries "&", ")" and "%"; the picked one adds "!" - which only delayed expansion eats.
$nameA = 'Clarion A one & (x) 100%'
$nameB = 'Clarion B two! & (y)'
$rootA = Join-Path $work $nameA
$rootB = Join-Path $work $nameB
$exeA = Join-Path $rootA $rel
$exeB = Join-Path $rootB $rel
$log  = Join-Path $work 'calls.log'
# The client's argument: spaces, "!", "&", ")" and "%EXE%" - a name the LAUNCHER defines and the outer cmd /c
# does not, so only a launcher that expands "%" a second time turns it into a path.
$solArg = 'C:\some dir!x & (1) %EXE%\app.sln'
$argsA = '--stdio --solution "' + $solArg + '"'

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
        string picked = Environment.GetEnvironmentVariable("CLARION_MCP_PICKED") ?? "";
        bool broken = File.Exists(Path.Combine(Path.GetDirectoryName(self), "broken.flag"));
        if (a.Length > 0 && a[0] == "--help")
        {
            File.AppendAllText(log, "HELP|" + self + "\r\n");
            Console.Error.WriteLine("fake usage");
            return broken ? 1 : 0;
        }
        if (a.Length > 0 && a[0] == "--pick-server")
        {
            File.AppendAllText(log, "PICK|" + self + "|" + string.Join(" ", a) + "\r\n");
            string code = Environment.GetEnvironmentVariable("FAKE_PICK_EXIT");
            if (code == "64") { Console.Error.WriteLine("fake: unrecognised arguments."); return 64; }
            int stdin = Console.In.Read();   // the launcher gives it nul: must be end of input at once
            File.AppendAllText(log, "PICKSTDIN|" + stdin + "\r\n");
            string pick = Environment.GetEnvironmentVariable("FAKE_PICK");
            Console.Error.WriteLine("clarion-tools: fake pick " + pick);
            int i = Array.IndexOf(a, "--pick-out");
            if (i >= 0 && i + 1 < a.Length && !string.IsNullOrEmpty(pick)) File.WriteAllText(a[i + 1], pick);
            return 0;
        }
        if (broken) return 3;
        File.AppendAllText(log, "SERVE|" + self + "|" + mode + "|" + picked + "|" + string.Join(" ", a) + "\r\n");
        Console.WriteLine("{\"served\":\"" + (self.Contains(" A one") ? "A" : "B") + "\"}");
        return 0;
    }
}
'@

function Write-Mutant([string]$path, [scriptblock]$transform) {
    $lines = [System.IO.File]::ReadAllLines($launcher)
    $out = New-Object System.Collections.Generic.List[string]
    $changed = 0
    foreach ($l in $lines) {
        $n = & $transform $l
        if ($n -ne $l) { $changed++ }
        if ($null -ne $n) { $out.Add($n) }
    }
    if ($changed -eq 0) { Write-Host "COULD NOT RUN: mutation for $path matched nothing in the launcher." -ForegroundColor Red; exit 2 }
    [System.IO.File]::WriteAllText($path, ([string]::Join("`r`n", $out.ToArray()) + "`r`n"), (New-Object System.Text.ASCIIEncoding))
}

try {
    New-Item -ItemType Directory -Force (Split-Path $exeA), (Split-Path $exeB) | Out-Null
    $src = Join-Path $work 'fake.cs'
    [System.IO.File]::WriteAllText($src, $fakeSource, (New-Object System.Text.UTF8Encoding($false)))
    $fakeExe = Join-Path $work 'fake.exe'
    & $csc /nologo /out:$fakeExe $src | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: the fake server did not compile." -ForegroundColor Red; exit 2 }
    Copy-Item -LiteralPath $fakeExe -Destination $exeA -Force
    Copy-Item -LiteralPath $fakeExe -Destination $exeB -Force

    $n1 = Join-Path $work 'n1-nopick.cmd'
    Write-Mutant $n1 { param($l) if ($l -like '"%EXE%" --pick-server*') { $null } else { $l } }
    $n2 = Join-Path $work 'n2-delayed.cmd'
    Write-Mutant $n2 { param($l) if ($l -eq 'setlocal DisableDelayedExpansion') { 'setlocal EnableDelayedExpansion' } else { $l } }
    $n3 = Join-Path $work 'n3-call.cmd'
    Write-Mutant $n3 { param($l) if ($l -like '"%EXE%" --pick-server*') { 'call ' + $l } else { $l } }
    $n4 = Join-Path $work 'n4-stdin.cmd'
    Write-Mutant $n4 { param($l) if ($l -like '"%EXE%" --pick-server*') { $l.Replace(' <nul', '') } else { $l } }

    function Invoke-Launcher([string]$script, [hashtable]$envVars, [string]$launcherArgs) {
        if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = Join-Path $env:WINDIR 'System32\cmd.exe'
        $psi.Arguments = '/d /c ""' + $script + '" ' + $launcherArgs + '"'
        $psi.WorkingDirectory = $work
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        foreach ($k in @('CLARION_MCP_SERVER', 'CLARION_MCP_LAUNCHER', 'CLARION_MCP_PICKED', 'CLARION_MCP_BOOTSTRAP',
                         'FAKE_PICK', 'FAKE_PICK_EXIT', 'EXE', 'REL', 'PICKED')) {
            if ($psi.EnvironmentVariables.ContainsKey($k)) { $psi.EnvironmentVariables.Remove($k) }
        }
        $psi.EnvironmentVariables['FAKE_LOG'] = $log
        $psi.EnvironmentVariables['CLARION_ROOTS'] = $rootA + ';' + $rootB
        foreach ($k in $envVars.Keys) { $psi.EnvironmentVariables[$k] = [string]$envVars[$k] }
        $p = [System.Diagnostics.Process]::Start($psi)
        # The client's first bytes are already waiting, as with a real MCP client: a pick that read stdin would get them.
        $p.StandardInput.Write("X`n")
        $p.StandardInput.Close()
        $errTask = $p.StandardError.ReadToEndAsync()
        $out = $p.StandardOutput.ReadToEnd()
        if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch { }; throw "launcher did not exit" }
        $calls = @()
        if (Test-Path -LiteralPath $log) { $calls = @([System.IO.File]::ReadAllLines($log) | Where-Object { $_ }) }
        return New-Object PSObject -Property @{ Out = $out; Err = $errTask.Result; Code = $p.ExitCode; Calls = $calls }
    }
    function Of($r, [string]$kind) { return @($r.Calls | Where-Object { $_.StartsWith($kind + '|') }) }
    function Show([string]$label, $r) {
        Write-Host ("  {0,-4} exit={1} stdout={2} calls={3}" -f $label, $r.Code, ($r.Out -replace '\s+', ' '), ($r.Calls -join ' ; ')) -ForegroundColor DarkGray
    }
    # SERVE|<exe>|<mode>|<picked>|<args>
    function Served($r, [string]$exe, [string]$mode, [string]$serveArgs) {
        $s = @(Of $r 'SERVE')
        return ($s.Count -eq 1 -and $s[0] -eq ('SERVE|' + $exe + '|' + $mode + '|' + $s[0].Split('|')[3] + '|' + $serveArgs))
    }
    function CaseA([string]$script) {
        $r = Invoke-Launcher $script @{ FAKE_PICK = $exeB } $argsA
        $picks = @(Of $r 'PICK')
        $pickOk = $picks.Count -eq 1 -and $picks[0].StartsWith('PICK|' + $exeA + '|--pick-server --pick-out ') `
                  -and $picks[0].EndsWith(' --stdio --solution ' + $solArg)
        $serveOk = Served $r $exeB 'pick' ('--stdio --solution ' + $solArg)
        $outOk = $r.Out.Trim() -eq '{"served":"B"}'
        $seen = @(Of $r 'PICKSTDIN')
        $stdinOk = $seen.Count -eq 1 -and $seen[0] -eq 'PICKSTDIN|-1'
        return New-Object PSObject -Property @{ R = $r; PickOk = $pickOk; ServeOk = $serveOk; OutOk = $outOk; StdinOk = $stdinOk
                                                All = ($pickOk -and $serveOk -and $outOk -and $stdinOk) }
    }

    # ---- A ----
    $a = CaseA $launcher
    Show 'A' $a.R
    Assert-That $a.PickOk "A: the bootstrap (root A) should be asked --pick-server --pick-out once, with the client's args unchanged; calls: $($a.R.Calls -join ' ; ')"
    Assert-That $a.ServeOk "A: root B's server should serve with CLARION_MCP_LAUNCHER=pick and the client's args EXACTLY ('--stdio --solution $solArg'); calls: $($a.R.Calls -join ' ; ')"
    Assert-That $a.OutOk "A: stdout must carry only the served server's output, got: $($a.R.Out)"
    Assert-That $a.StdinOk "A: the pick must get nul for stdin (end of input), got: $($a.R.Calls -join ' ; ')"
    Assert-That (@(Of $a.R 'HELP').Count -eq 1) "A: the picked server should get exactly one startup check (--help) before the hand-over; calls: $($a.R.Calls -join ' ; ')"
    Assert-That ($a.R.Err -match 'fake pick') "A: the pick's reason should reach stderr, got: $($a.R.Err)"
    Assert-That (-not (Get-ChildItem $env:TEMP -Filter 'clarion-tools-pick-*.txt' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-2) })) "A: the --pick-out file should be deleted"

    # ---- B: an old bootstrap that does not know --pick-server ----
    $b = Invoke-Launcher $launcher @{ FAKE_PICK_EXIT = '64' } '--stdio'
    Show 'B' $b
    Assert-That (Served $b $exeA 'bootstrap' '--stdio') "B: an old bootstrap should serve itself, marked bootstrap; calls: $($b.Calls -join ' ; ')"
    Assert-That ($b.Out.Trim() -eq '{"served":"A"}') "B: stdout must carry only the served output, got: $($b.Out)"

    # ---- C: the pick names a file that does not exist ----
    $c = Invoke-Launcher $launcher @{ FAKE_PICK = (Join-Path $work 'nope\clarion-mcp-server.exe') } '--stdio'
    Show 'C' $c
    Assert-That (Served $c $exeA 'bootstrap' '--stdio') "C: a missing pick should leave the bootstrap serving; calls: $($c.Calls -join ' ; ')"

    # ---- D: CLARION_MCP_SERVER ----
    $d = Invoke-Launcher $launcher @{ CLARION_MCP_SERVER = $exeB; FAKE_PICK = $exeA } '--stdio'
    Show 'D' $d
    Assert-That (@(Of $d 'PICK').Count -eq 0) "D: with CLARION_MCP_SERVER set no pick may be asked; calls: $($d.Calls -join ' ; ')"
    Assert-That (Served $d $exeB 'override' '--stdio') "D: the named server should serve, marked override; calls: $($d.Calls -join ' ; ')"

    # ---- E: a stale CLARION_MCP_LAUNCHER in the environment ----
    $e = Invoke-Launcher $launcher @{ CLARION_MCP_LAUNCHER = 'pick'; FAKE_PICK_EXIT = '64' } '--stdio'
    Show 'E' $e
    Assert-That (@(Of $e 'PICK').Count -eq 1) "E: a stale CLARION_MCP_LAUNCHER must not stop the launcher asking; calls: $($e.Calls -join ' ; ')"
    Assert-That (Served $e $exeA 'bootstrap' '--stdio') "E: the stale 'pick' must not be passed on; calls: $($e.Calls -join ' ; ')"

    # ---- F: the picked server cannot start ----
    $flag = Join-Path (Split-Path $exeB) 'broken.flag'
    [System.IO.File]::WriteAllText($flag, 'x')
    $f = Invoke-Launcher $launcher @{ FAKE_PICK = $exeB } '--stdio'
    Remove-Item -LiteralPath $flag -Force
    Show 'F' $f
    $fs = @(Of $f 'SERVE')
    Assert-That ($fs.Count -eq 1 -and $fs[0] -eq ('SERVE|' + $exeA + '|fallback|' + $exeB + '|--stdio')) "F: a pick that fails to start should leave the bootstrap serving, CLARION_MCP_LAUNCHER=fallback, CLARION_MCP_PICKED=the failed one; calls: $($f.Calls -join ' ; ')"
    Assert-That ($f.Err -match 'failed to start' -and $f.Err -match [regex]::Escape($exeB)) "F: stderr should say which picked server failed, got: $($f.Err)"
    Assert-That ($f.Out.Trim() -eq '{"served":"A"}') "F: stdout must carry only the bootstrap's output, got: $($f.Out)"

    # ---- negative controls: each broken launcher must fail case A ----
    foreach ($m in @(@{ N = 'N1'; P = $n1; W = 'never asks' }, @{ N = 'N2'; P = $n2; W = 'delayed expansion eats "!"' },
                     @{ N = 'N3'; P = $n3; W = '"call" expands the client''s "%" twice' },
                     @{ N = 'N4'; P = $n4; W = 'lets the pick read the client''s stdin' })) {
        $x = CaseA $m.P
        Show $m.N $x.R
        Assert-That (-not $x.All) "NEGATIVE CONTROL $($m.N) ($($m.W)): the broken launcher still passed case A - the check cannot fail"
        if (-not $x.All) { Write-Host "  ok  negative control $($m.N): a launcher that $($m.W) fails case A" -ForegroundColor DarkGray }
    }
}
finally {
    try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS - $assertions assertions" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $($failures.Count) of $assertions assertions:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
