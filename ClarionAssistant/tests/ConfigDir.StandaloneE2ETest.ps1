# Regression guard for ticket e1df6c2e (GH #244 items 1 and 2), through the REAL clarion-mcp-server.exe over stdio.
#
# THE DEFECT. A Clarion started with /ConfigDir=<folder> keeps its ClarionProperties.xml (versions, macros, .red) in
# that folder. The standalone server has no Clarion command line to read it from, so it read the default
# %APPDATA%\SoftVelocity\Clarion\<X.Y> folders: the wrong macros, silently. And the plugin launcher started the
# newest Clarion's server whatever the project.
#
# HOW. Settings folders in a temp dir, each a ClarionProperties.xml listing ONE version under a name no real
# settings folder has, with a "probe" macro saying which folder it is. The server is started once per case and
# asked get_solution_info; the probe macro says which folder it actually read.
#
#   1  clarion-assistant.json {clarionVersion, configDir: A}   -> probe A, settingsFile = A's xml, chosen by the json
#   2  ... and --clarion-config-dir B                          -> probe B
#   3  ... and CLARION_CONFIG_DIR=B instead                    -> probe B, chosen by CLARION_CONFIG_DIR
#   4  ... and a live IDE (no --ide-pid: the plugin case) with this solution open on folder L -> probe L
#   5  --clarion-config-dir <missing>                          -> NO version, the error names the folder (no fallback)
#   6  CONTROL: the json names the version but no configDir    -> no version (the name is only in the temp folders),
#      which is what every case above returned before this fix: so 1-4 fail without it
#   7  --pick-server with the json and CLARION_ROOTS=<the version's root> -> prints that root's server, exit 0
#   8  CLARION_MCP_LAUNCHER=pick                               -> get_solution_info has serverChosenBy
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\ConfigDir.StandaloneE2ETest.ps1
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
& $msbuild $csproj /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
$exe = Join-Path $binDir 'clarion-mcp-server.exe'
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
    Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red; exit 2
}

# ---------------------------------------------------------------- fixtures
# Under LocalApplicationData\Temp, not $env:TEMP: %TEMP% is often an 8.3 short path (C:\Users\JOHNHI~1\...), and the
# server's Path.GetFullPath expands short names, so paths it reports would never equal the ones built here.
$work = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ("Temp\ca-cfgdir-e2e-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$sol  = Join-Path $work 'sol'
$recs = Join-Path $work 'records'
$fakeRoot = Join-Path $work 'FakeClarion11'
$fakeServer = Join-Path $fakeRoot 'accessory\addins\ClarionAssistant\clarion-mcp-server.exe'
$name = 'CA E2E Clarion 11.0.99999'
New-Item -ItemType Directory -Force $sol, $recs, (Split-Path $fakeServer), (Join-Path $fakeRoot 'bin') | Out-Null
[System.IO.File]::WriteAllText($fakeServer, 'placeholder')   # the pick only needs it to exist
$sln = Join-Path $sol 'app.sln'
[System.IO.File]::WriteAllText($sln, '')
$utf8 = New-Object System.Text.UTF8Encoding($false)

function New-Settings([string]$dir, [string]$probe) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $xml = "<ClarionProperties>`r`n  <Properties name=`"Clarion.Versions`">`r`n    <Properties name=`"$name`">`r`n" +
           "      <path value=`"$fakeRoot\bin`" />`r`n      <IsWindowsVersion value=`"True`" />`r`n" +
           "      <Properties name=`"RedirectionFile`">`r`n        <Name value=`"Clarion110.red`" />`r`n" +
           "        <Properties name=`"Macros`">`r`n          <root value=`"$fakeRoot`" />`r`n" +
           "          <probe value=`"$probe`" />`r`n        </Properties>`r`n      </Properties>`r`n" +
           "      <CWVersion value=`"11000`" />`r`n    </Properties>`r`n  </Properties>`r`n</ClarionProperties>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $dir 'ClarionProperties.xml'), $xml, $utf8)
}
$cfgA = Join-Path $work 'cfg A';   New-Settings $cfgA 'fromA'
$cfgB = Join-Path $work 'cfgB';    New-Settings $cfgB 'fromB'
$cfgL = Join-Path $work 'cfgLive'; New-Settings $cfgL 'fromLive'
function Set-Pin([hashtable]$pin) {
    [System.IO.File]::WriteAllText((Join-Path $sol 'clarion-assistant.json'), ($pin | ConvertTo-Json -Compress), $utf8)
}

function Start-Server([string]$arguments, [hashtable]$envVars) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = $exe
    $psi.Arguments = $arguments
    $psi.WorkingDirectory       = $sol
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding  = $utf8
    foreach ($k in @('CLARION_CONFIG_DIR', 'CLARION_MCP_LAUNCHER', 'CLARION_ROOTS')) {
        if ($psi.EnvironmentVariables.ContainsKey($k)) { $psi.EnvironmentVariables.Remove($k) }
    }
    $psi.EnvironmentVariables['CA_IDE_RECORD_DIR'] = $recs   # never this machine's real IDE records
    if ($envVars) { foreach ($k in $envVars.Keys) { $psi.EnvironmentVariables[$k] = [string]$envVars[$k] } }
    return [System.Diagnostics.Process]::Start($psi)
}

# One server, one get_solution_info, close. Returns the parsed answer (and the stderr, for the report).
function Get-Info([string]$arguments, [hashtable]$envVars) {
    $p = Start-Server ("--stdio " + $arguments) $envVars
    $errTask = $p.StandardError.ReadToEndAsync()
    try {
        $id = 0
        foreach ($m in @(
            @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'cfgdir-e2e'; version = '1' } } },
            @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} },
            @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{ name = 'get_solution_info'; arguments = @{} } })) {
            $p.StandardInput.WriteLine(($m | ConvertTo-Json -Compress -Depth 6))
        }
        $p.StandardInput.Flush()
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while ([DateTime]::UtcNow -lt $deadline) {
            $line = $p.StandardOutput.ReadLine()
            if ($null -eq $line) { throw "server closed stdout" }
            try { $r = $line | ConvertFrom-Json } catch { continue }
            if ($r.id -eq 2) {
                $text = [string]$r.result.content[0].text
                try { $info = $text | ConvertFrom-Json } catch { $info = $null }
                return New-Object PSObject -Property @{ Info = $info; Text = $text }
            }
        }
        throw "no answer to get_solution_info"
    }
    finally {
        try { $p.StandardInput.Close() } catch { }
        if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch { } }
        $script:lastErr = $errTask.Result
    }
}
function Show([string]$label, $r) {
    $i = $r.Info
    $flat = if ($i) { "" + $i.versionName + " | probe=" + $i.macros.probe + " | " + $i.settingsFile + " | " + $i.settingsChosenBy } else { $r.Text }
    $flat = ($flat -replace '\s+', ' '); if ($flat.Length -gt 260) { $flat = $flat.Substring(0, 260) + '...' }
    Write-Host ("  {0,-3} {1}" -f $label, $flat) -ForegroundColor DarkGray
}

try {
    # ---- 1: the json's configDir ----
    Set-Pin @{ clarionVersion = $name; configDir = $cfgA }
    $r1 = Get-Info '' $null; Show '1' $r1
    Assert-That ($r1.Info.versionName -eq $name) "1: the version should resolve from the json's configDir, got '$($r1.Info.versionName)': $($r1.Info.versionNote)"
    Assert-That ($r1.Info.macros.probe -eq 'fromA') "1: macros should come from folder A, got probe='$($r1.Info.macros.probe)'"
    Assert-That ($r1.Info.settingsFile -eq (Join-Path $cfgA 'ClarionProperties.xml')) "1: settingsFile should be A's xml, got '$($r1.Info.settingsFile)'"
    Assert-That (([string]$r1.Info.settingsChosenBy) -match 'configDir.*clarion-assistant\.json') "1: settingsChosenBy should credit the json, got: $($r1.Info.settingsChosenBy)"
    Assert-That (([string]$script:lastErr) -match 'settings: read ') "1: startup stderr should name the settings file, got: $($script:lastErr)"

    # ---- 2: --clarion-config-dir beats the json ----
    $r2 = Get-Info ("--clarion-config-dir `"$cfgB`"") $null; Show '2' $r2
    Assert-That ($r2.Info.macros.probe -eq 'fromB') "2: --clarion-config-dir should win, got probe='$($r2.Info.macros.probe)'"
    Assert-That (([string]$r2.Info.settingsChosenBy) -match '--clarion-config-dir') "2: settingsChosenBy should credit --clarion-config-dir, got: $($r2.Info.settingsChosenBy)"

    # ---- 3: CLARION_CONFIG_DIR beats the json ----
    $r3 = Get-Info '' @{ CLARION_CONFIG_DIR = $cfgB }; Show '3' $r3
    Assert-That ($r3.Info.macros.probe -eq 'fromB') "3: CLARION_CONFIG_DIR should win over the json, got probe='$($r3.Info.macros.probe)'"
    Assert-That (([string]$r3.Info.settingsChosenBy) -match 'CLARION_CONFIG_DIR') "3: settingsChosenBy should credit CLARION_CONFIG_DIR, got: $($r3.Info.settingsChosenBy)"

    # ---- 4: a live IDE on this solution with a /ConfigDir folder beats all of it (no --ide-pid) ----
    $rec = [ordered]@{ solution = $sln; pid = $PID; writtenAt = (Get-Date).ToString('o'); configDir = $cfgL }
    New-Item -ItemType Directory -Force (Join-Path $recs 'ide-solution') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $recs ("ide-solution\ide-" + $PID + ".json")), ($rec | ConvertTo-Json -Compress), $utf8)
    $r4 = Get-Info ("--clarion-config-dir `"$cfgB`"") $null; Show '4' $r4
    Assert-That ($r4.Info.macros.probe -eq 'fromLive') "4: the live IDE's /ConfigDir folder should win, got probe='$($r4.Info.macros.probe)': $($r4.Info.settingsChosenBy)"
    Assert-That (([string]$r4.Info.settingsChosenBy) -match "pid $PID" -and ([string]$r4.Info.settingsChosenBy) -match 'IGNORING') "4: settingsChosenBy should name the IDE and what it overrode, got: $($r4.Info.settingsChosenBy)"
    Remove-Item (Join-Path $recs 'ide-solution') -Recurse -Force

    # ---- 5: a named folder that is not there: no version, no fallback ----
    $missing = Join-Path $work 'no such settings'
    $r5 = Get-Info ("--clarion-config-dir `"$missing`"") $null; Show '5' $r5
    Assert-That ([string]::IsNullOrEmpty([string]$r5.Info.versionName)) "5: a missing named folder must give NO version, got '$($r5.Info.versionName)'"
    Assert-That (([string]$r5.Info.versionNote) -match 'no ClarionProperties\.xml' -and ([string]$r5.Info.versionNote) -match [regex]::Escape($missing)) "5: the note should name the folder and why, got: $($r5.Info.versionNote)"

    # ---- 6: CONTROL - without a configDir the name is not found (so 1-4 really depend on the folder) ----
    Set-Pin @{ clarionVersion = $name }
    $r6 = Get-Info '' $null; Show '6' $r6
    Assert-That ([string]::IsNullOrEmpty([string]$r6.Info.versionName)) "6: CONTROL - without configDir '$name' should not resolve (it is only in the temp folders), got '$($r6.Info.versionName)'"
    Assert-That (([string]$r6.Info.versionNote) -match 'matches nothing installed') "6: CONTROL - the note should say the name matches nothing, got: $($r6.Info.versionNote)"

    # ---- 7: --pick-server picks the version's own root ----
    Set-Pin @{ clarionVersion = $name; configDir = $cfgA }
    $p7 = Start-Server '--pick-server' @{ CLARION_ROOTS = $fakeRoot }
    $p7.StandardInput.Close()
    $err7 = $p7.StandardError.ReadToEndAsync()
    $out7 = $p7.StandardOutput.ReadToEnd()
    [void]$p7.WaitForExit(30000)
    Write-Host ("  7   exit={0} stdout={1} stderr={2}" -f $p7.ExitCode, ($out7 -replace '\s+', ' '), ($err7.Result -replace '\s+', ' ')) -ForegroundColor DarkGray
    $outLines = @($out7 -split "`r?`n" | Where-Object { $_ })
    Assert-That ($p7.ExitCode -eq 0) "7: --pick-server should exit 0, got $($p7.ExitCode)"
    Assert-That ($outLines.Count -eq 1 -and $outLines[0] -eq $fakeServer) "7: stdout should be exactly the version's own server '$fakeServer', got: $out7"
    Assert-That (([string]$err7.Result) -match 'clarion-assistant\.json') "7: stderr should say the json chose it, got: $($err7.Result)"

    # ---- 8: launched by the launcher: serverChosenBy ----
    $r8 = Get-Info '' @{ CLARION_MCP_LAUNCHER = 'pick'; CLARION_ROOTS = $fakeRoot }; Show '8' $r8
    Assert-That (([string]$r8.Info.serverChosenBy) -match 'clarion-tools launcher' -and ([string]$r8.Info.serverChosenBy) -match [regex]::Escape($fakeRoot)) "8: serverChosenBy should report the launcher's pick, got: $($r8.Info.serverChosenBy)"
    $r9 = Get-Info '' $null
    Assert-That ($null -eq $r9.Info.serverChosenBy) "8: without the launcher there should be no serverChosenBy, got: $($r9.Info.serverChosenBy)"
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS - $assertions assertions" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $($failures.Count) of $assertions assertions:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
