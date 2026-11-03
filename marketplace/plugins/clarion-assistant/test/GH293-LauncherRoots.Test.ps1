# GH #293: the plugin's clarion-tools MCP server "failed to connect" on every machine without CLARION_ROOTS.
#
# THE DEFECT. Line 55 of launcher\clarion-tools.cmd was
#     if defined CLARION_ROOTS for %%R in ("%CLARION_ROOTS:;=" "%") do ... "%%~R" ...
# cmd expands every %...% in a line BEFORE it runs "if defined", and expands the :old=new form of an
# UNDEFINED variable by dropping only "%CLARION_ROOTS:" - the tail ;=" "% stays, its closing % pairs with
# the next % along, every %%~R after it shifts into a %~R batch-parameter expansion, and cmd aborts the
# script: "The following usage of the path operator in batch-parameter substitution is invalid: %~R"==""
# if exist ..." / "The syntax of the command is incorrect." (exit 255) before any server was looked for.
# CLARION_ROOTS is unset on most machines, so most 6.2.0 plugin installs never connected.
#
# HOW. The REAL launcher, run by cmd.exe the two ways a client runs it (the plugin's `cmd /c "<launcher>"`
# with no arguments, and `cmd /d /c ""<launcher>" <args>"` with the client's arguments), under every shape
# of CLARION_ROOTS: unset; empty (both an empty variable and `set CLARION_ROOTS=` in the same cmd); one
# root; two; a trailing, leading and doubled ";"; a root with spaces, with "(x86)", with "&", "!" and "%";
# roots a user wrapped in quotes; a trailing backslash; a missing first root. Each real root holds a fake
# clarion-mcp-server.exe compiled here that logs its arguments and answers --pick-server with no pick, so
# the launcher serves it as the bootstrap. The client's argument carries spaces, "!", "&", ")" and a
# literal "%EXE%" (a name the launcher defines and the outer cmd does not).
#
# ASSERTS, per run: stderr carries no cmd parse error; where a fake root is reachable, THAT fake served,
# once, with the arguments EXACTLY as given, CLARION_MCP_LAUNCHER=bootstrap, and stdout carrying only its
# output. With no root reachable (unset/empty) nothing is hermetic to serve: those runs assert the parse
# error alone, which is exactly what #293 saw, plus that the launcher went on to look elsewhere (its own
# "could not find" message, or a real installed server starting).
#
# MUST BE ABLE TO GO RED, and proves it on two broken copies of the launcher under test:
#   M1 the #293 line put back (guard and quote-stripping gone) -> the unset shape must show the parse error
#   M2 the quote-stripping line removed                        -> the quoted shape must not find the root
# Run it against the pre-fix launcher (git show <old>:marketplace/.../clarion-tools.cmd > x.cmd, CRLF) with
# -Launcher x.cmd: the unset/empty and quoted shapes fail.
#
# Windows PowerShell 5.1 compatible (no PS6+ syntax), ASCII only.
# Exit: 0 pass, 1 fail, 2 could-not-run.

param(
    [string]$Launcher = (Join-Path $PSScriptRoot '..\launcher\clarion-tools.cmd')
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0
function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

$Launcher = [System.IO.Path]::GetFullPath($Launcher)
if (-not (Test-Path -LiteralPath $Launcher)) { Write-Host "COULD NOT RUN: launcher not found at $Launcher" -ForegroundColor Red; exit 2 }
Write-Host "launcher under test: $Launcher" -ForegroundColor DarkGray

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

# %TEMP% is often an 8.3 short name (C:\Users\JOHNHI~1\...); the fake logs the long name Windows gives its
# module, so the work folder is created first and then resolved to its long name for every expected path.
Add-Type -Namespace GH293 -Name Native -MemberDefinition '[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint GetLongPathNameW(string shortPath, System.Text.StringBuilder longPath, uint size);'
function Get-LongPath([string]$p) {
    $sb = New-Object System.Text.StringBuilder 2048
    $n = [GH293.Native]::GetLongPathNameW($p, $sb, 2048)
    if ($n -gt 0 -and $n -lt 2048) { return $sb.ToString() }
    return $p
}
$work = Join-Path $env:TEMP ("ca-gh293-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
$work = Get-LongPath $work
$rel  = 'accessory\addins\ClarionAssistant\clarion-mcp-server.exe'
$rootSpace   = Join-Path $work 'Clarion one'
$rootParens  = Join-Path $work 'Program Files (x86)\Clarion12'
$rootSpecial = Join-Path $work 'C11 & x! 100%'
$rootMissing = Join-Path $work 'nope'
$log  = Join-Path $work 'calls.log'
# The client's argument: spaces, "!", "&", ")" and "%EXE%".
$solArg = 'C:\some dir!x & (1) %EXE%\app.sln'
$clientArgs = '--stdio --solution "' + $solArg + '"'
$expectedArgs = '--stdio --solution ' + $solArg

$fakeSource = @'
using System;
using System.IO;
static class FakeServer
{
    static int Main(string[] a)
    {
        string self = Path.GetFullPath(System.Reflection.Assembly.GetExecutingAssembly().Location);
        string log = Environment.GetEnvironmentVariable("FAKE_LOG");
        string mode = Environment.GetEnvironmentVariable("CLARION_MCP_LAUNCHER") ?? "";
        if (a.Length > 0 && a[0] == "--help") { Console.Error.WriteLine("fake usage"); return 0; }
        if (a.Length > 0 && a[0] == "--pick-server")
        {
            // An old server: it does not know --pick-server, writes no pick file. The launcher keeps the bootstrap.
            File.AppendAllText(log, "PICK|" + self + "|" + a.Length + "|" + string.Join(" ", a) + "\r\n");
            Console.Error.WriteLine("fake: unrecognised arguments.");
            return 64;
        }
        File.AppendAllText(log, "SERVE|" + self + "|" + mode + "|" + a.Length + "|" + string.Join(" ", a) + "\r\n");
        Console.WriteLine("{\"served\":\"fake\"}");
        return 0;
    }
}
'@

# A copy of the launcher under test with one line changed; $null when the line is not there.
function Write-Mutant([string]$path, [scriptblock]$transform) {
    $lines = [System.IO.File]::ReadAllLines($Launcher)
    $out = New-Object System.Collections.Generic.List[string]
    $changed = 0
    foreach ($l in $lines) {
        $n = & $transform $l
        if ($n -ne $l) { $changed++ }
        if ($null -ne $n) { $out.Add($n) }
    }
    if ($changed -eq 0) { return $null }
    [System.IO.File]::WriteAllText($path, ([string]::Join("`r`n", $out.ToArray()) + "`r`n"), (New-Object System.Text.ASCIIEncoding))
    return $path
}

$parseError = 'path operator in batch-parameter|syntax of the command is incorrect|CALL /\? or FOR /\?|was unexpected at this time|is not recognized as an internal or external command'

try {
    foreach ($r in @($rootSpace, $rootParens, $rootSpecial)) { New-Item -ItemType Directory -Force (Split-Path (Join-Path $r $rel)) | Out-Null }
    $src = Join-Path $work 'fake.cs'
    [System.IO.File]::WriteAllText($src, $fakeSource, (New-Object System.Text.UTF8Encoding($false)))
    $fakeExe = Join-Path $work 'fake.exe'
    & $csc /nologo /out:$fakeExe $src | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: the fake server did not compile." -ForegroundColor Red; exit 2 }
    foreach ($r in @($rootSpace, $rootParens, $rootSpecial)) { Copy-Item -LiteralPath $fakeExe -Destination (Join-Path $r $rel) -Force }
    $exeSpace   = Join-Path $rootSpace $rel
    $exeParens  = Join-Path $rootParens $rel
    $exeSpecial = Join-Path $rootSpecial $rel

    # Roots: $null = the variable is absent from the environment. Expect: the exe that must serve, or '' when
    # no fake is reachable. SetInCmd: also run it as `set "CLARION_ROOTS=<v>" & launcher` inside the one cmd.
    $shapes = @(
        @{ N = 'unset';          Roots = $null;                              Expect = '' },
        @{ N = 'empty';          Roots = '';                                 Expect = '';          SetInCmd = $true },
        @{ N = 'one';            Roots = $rootSpace;                         Expect = $exeSpace },
        @{ N = 'two';            Roots = "$rootSpace;$rootParens";           Expect = $exeSpace },
        @{ N = 'second-wins';    Roots = "$rootMissing;$rootParens";         Expect = $exeParens },
        @{ N = 'trailing;';      Roots = "$rootSpace;";                      Expect = $exeSpace },
        @{ N = 'leading;';       Roots = ";$rootParens";                     Expect = $exeParens },
        @{ N = 'doubled;;';      Roots = "$rootMissing;;$rootSpace";         Expect = $exeSpace },
        @{ N = 'only;';          Roots = ';';                                Expect = '' },
        @{ N = 'parens';         Roots = $rootParens;                        Expect = $exeParens },
        @{ N = 'specials';       Roots = $rootSpecial;                       Expect = $exeSpecial },
        @{ N = 'quoted';         Roots = ('"' + $rootSpace + '";"' + $rootParens + '"'); Expect = $exeSpace },
        @{ N = 'quoted-one';     Roots = ('"' + $rootParens + '"');          Expect = $exeParens },
        @{ N = 'trailing-slash'; Roots = ($rootParens + '\');                Expect = $exeParens },
        @{ N = 'missing-only';   Roots = $rootMissing;                       Expect = '' }
    )

    function Invoke-Launcher([string]$script, $roots, [string]$form) {
        if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = Join-Path $env:WINDIR 'System32\cmd.exe'
        switch ($form) {
            # Exactly the plugin's invocation: command "cmd", args ["/c", "<launcher>"], no client arguments.
            'plugin' { $psi.Arguments = '/c "' + $script + '"' }
            # A client that passes arguments.
            'args'   { $psi.Arguments = '/d /c ""' + $script + '" ' + $clientArgs + '"' }
            # `set CLARION_ROOTS=` in the same cmd, then the launcher.
            'setcmd' { $psi.Arguments = '/d /c "set "CLARION_ROOTS=' + $roots + '" & "' + $script + '" ' + $clientArgs + '"' }
        }
        $psi.WorkingDirectory = $work
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        foreach ($k in @('CLARION_ROOTS', 'CLARION_MCP_SERVER', 'CLARION_MCP_LAUNCHER', 'CLARION_MCP_PICKED', 'CLARION_MCP_BOOTSTRAP',
                         'EXE', 'REL', 'ROOTS', 'PICKED')) {
            if ($psi.EnvironmentVariables.ContainsKey($k)) { $psi.EnvironmentVariables.Remove($k) }
        }
        $psi.EnvironmentVariables['FAKE_LOG'] = $log
        if ($null -ne $roots -and $form -ne 'setcmd') { $psi.EnvironmentVariables['CLARION_ROOTS'] = [string]$roots }
        $p = [System.Diagnostics.Process]::Start($psi)
        $p.StandardInput.Close()
        $errTask = $p.StandardError.ReadToEndAsync()
        $out = $p.StandardOutput.ReadToEnd()
        if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch { }; throw "launcher did not exit ($form)" }
        $calls = @()
        if (Test-Path -LiteralPath $log) { $calls = @([System.IO.File]::ReadAllLines($log) | Where-Object { $_ }) }
        return New-Object PSObject -Property @{ Out = $out; Err = $errTask.Result; Code = $p.ExitCode; Calls = $calls }
    }
    function Of($r, [string]$kind) { return @($r.Calls | Where-Object { $_.StartsWith($kind + '|') }) }
    function Show([string]$label, [string]$form, $r) {
        $e = ($r.Err -replace '\s+', ' ').Trim()
        if ($e.Length -gt 100) { $e = $e.Substring(0, 100) + '...' }
        Write-Host ("  {0,-15} {1,-7} exit={2,-3} stdout={3,-18} stderr={4}" -f $label, $form, $r.Code, ($r.Out -replace '\s+', ' ').Trim(), $e) -ForegroundColor DarkGray
    }

    # Runs one shape in one form against one launcher; returns $true when every check held.
    function Check([string]$script, $shape, [string]$form, [bool]$record) {
        $r = Invoke-Launcher $script $shape.Roots $form
        if ($record) { Show $shape.N $form $r }
        $tag = "$($shape.N)/$form"
        $ok = $true
        $noParse = -not ($r.Err -match $parseError)
        $ok = $ok -and $noParse
        if ($record) { Assert-That $noParse "${tag}: cmd reported a parse error on stderr (GH #293): $($r.Err)" }
        $serves = @(Of $r 'SERVE')
        if ($shape.Expect) {
            $wantArgs = if ($form -eq 'plugin') { '0|' } else { '3|' + $expectedArgs }
            $want = 'SERVE|' + $shape.Expect + '|bootstrap|' + $wantArgs
            $served = ($serves.Count -eq 1) -and ($serves[0] -ieq $want)
            $ok = $ok -and $served
            if ($record) { Assert-That $served "${tag}: expected exactly one serve '$want', got: $($r.Calls -join ' ; ')" }
            $outOk = $r.Out.Trim() -eq '{"served":"fake"}'
            $ok = $ok -and $outOk
            if ($record) { Assert-That $outOk "${tag}: stdout must carry only the served server's output, got: '$($r.Out)'" }
            # PICK|<exe>|<argc>|--pick-server --pick-out <file> [client args]; the pick file's name is random.
            $picks = @(Of $r 'PICK')
            $pickPattern = if ($form -eq 'plugin') { '^PICK\|' + [regex]::Escape($shape.Expect) + '\|3\|--pick-server --pick-out .+$' }
                           else { '^PICK\|' + [regex]::Escape($shape.Expect) + '\|6\|--pick-server --pick-out .+ ' + [regex]::Escape($expectedArgs) + '$' }
            $pickOk = ($picks.Count -eq 1) -and ($picks[0] -match $pickPattern)
            $ok = $ok -and $pickOk
            if ($record) { Assert-That $pickOk "${tag}: the found server should be asked --pick-server once with the client's args unchanged, got: $($picks -join ' ; ')" }
        } else {
            # No fake is reachable: the launcher must have gone on to the other places - its own message, or a
            # real server found there (which starts, sees end of input and exits). The fake must not have served.
            $wentOn = ($r.Err -match 'clarion-tools: could not find') -or ($r.Err -match 'clarion-tools: server under') -or ($r.Err -match 'clarion-mcp-server:')
            $ok = $ok -and $wentOn -and ($serves.Count -eq 0)
            if ($record) {
                Assert-That $wentOn "${tag}: the launcher should have searched beyond CLARION_ROOTS (its own 'could not find' or a real server on stderr), got exit $($r.Code): $($r.Err)"
                Assert-That ($serves.Count -eq 0) "${tag}: no fake root was named, yet a fake served: $($r.Calls -join ' ; ')"
            }
        }
        return $ok
    }

    Write-Host "shapes against the launcher under test:" -ForegroundColor Cyan
    foreach ($s in $shapes) {
        [void](Check $Launcher $s 'plugin' $true)
        [void](Check $Launcher $s 'args' $true)
        if ($s.SetInCmd) { [void](Check $Launcher $s 'setcmd' $true) }
    }

    # ---- negative controls ----
    Write-Host "negative controls:" -ForegroundColor Cyan
    # M1: the #293 line put back - the goto guard and the quote-stripping gone, the for-set expanding
    # CLARION_ROOTS itself. (The guard alone cannot be removed in isolation: the quote-stripping line that
    # follows it changes what an undefined CLARION_ROOTS does to the line.)
    $m1 = Write-Mutant (Join-Path $work 'm1-gh293-line.cmd') { param($l)
        if ($l -like 'if not defined CLARION_ROOTS goto*' -or $l -like 'if not defined ROOTS goto*' -or $l -eq 'set "ROOTS=%CLARION_ROOTS:"=%"') { $null }
        elseif ($l.StartsWith('for %%R in ("%ROOTS:;=" "%")')) { $l.Replace('("%ROOTS:;=" "%")', '("%CLARION_ROOTS:;=" "%")') }
        else { $l } }
    if ($m1) {
        $pass = Check $m1 ($shapes | Where-Object { $_.N -eq 'unset' }) 'plugin' $false
        Assert-That (-not $pass) "NEGATIVE CONTROL M1 (the #293 line put back): the broken launcher still passed the unset shape - the check cannot fail"
        if (-not $pass) { Write-Host "  ok  M1: with the #293 line put back the unset shape fails" -ForegroundColor DarkGray }
    } else {
        Assert-That $false "NEGATIVE CONTROL M1: no 'if not defined CLARION_ROOTS goto' guard in this launcher - there is nothing to put back"
    }
    $m2 = Write-Mutant (Join-Path $work 'm2-noquotestrip.cmd') { param($l) if ($l -eq 'set "ROOTS=%CLARION_ROOTS:"=%"') { 'set "ROOTS=%CLARION_ROOTS%"' } else { $l } }
    if ($m2) {
        $pass = Check $m2 ($shapes | Where-Object { $_.N -eq 'quoted' }) 'args' $false
        Assert-That (-not $pass) "NEGATIVE CONTROL M2 (quote stripping removed): the broken launcher still passed the quoted shape - the check cannot fail"
        if (-not $pass) { Write-Host "  ok  M2: without quote stripping the quoted shape fails" -ForegroundColor DarkGray }
    } else {
        Assert-That $false "NEGATIVE CONTROL M2: no quote-stripping line in this launcher - it has none to remove"
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
