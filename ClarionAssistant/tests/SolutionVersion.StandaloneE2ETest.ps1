# Regression guard for ticket 0ce0b5e2: the standalone clarion-mcp-server resolves a solution's Clarion version
# from the IDE's own choice for that solution, reports it, and restarts its language server when it changes.
#
# THE DEFECT. v61POSitive.sln builds under Clarion 10 but was browsed in the Clarion 12 IDE. The IDE had the
# solution on "Clarion 10 Active And Updated" (it restores that from the solution's preferences on open), but
# the standalone - which serves lsp_diagnostics, resolve_red_path and get_solution_info - took the Clarion it
# is installed under. Its language server resolved INCLUDE('PS_ProImage.clw') against C12: "cannot be found".
#
# HOW. The real clarion-mcp-server.exe, launched the way the addin launches it (--ide-pid = this test's pid,
# --solution), from a private copy of its bin with a scripted language server planted where LspService looks
# for the bundled one. The scripted server logs every clarion/updatePaths it receives. This test plays the IDE:
# it writes the record the addin publishes (%LOCALAPPDATA%\ClarionAssistant\ide-solution\ide-<pid>.json, the
# same pattern LspStart.FollowIdeSolutionTest.ps1 uses) with the IDE's version choice, then switches it.
# Version names are two real Win32 entries from this machine's ClarionProperties.xml files.
#
#   A  record says V1          -> get_solution_info V1 "live from the IDE"; the server started with V1
#   B  record says V2          -> the server RESTARTED with V2 (a second updatePaths), lsp_diagnostics says V2
#   C  record names no version, its configDir's preferences\ctrl.sln.<hex>.xml says V1 -> V1 "saved"
#   D  record says 'Current'   -> not V1/V2; the note says the IDE is on 'Current'
#
# MUST BE ABLE TO GO RED: on master the standalone ignores the record's version entirely (A-D fail).
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\SolutionVersion.StandaloneE2ETest.ps1
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
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Write-Host "COULD NOT RUN: node is not on PATH." -ForegroundColor Red; exit 2 }
& $msbuild $csproj /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $binDir 'clarion-mcp-server.exe'))) {
    Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red; exit 2
}

# ---------------------------------------------------------------- two real version names
$settingsRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'SoftVelocity\Clarion'
$names = New-Object System.Collections.Generic.List[string]
foreach ($f in (Get-ChildItem $settingsRoot -Filter ClarionProperties.xml -Recurse -Depth 1 -ErrorAction SilentlyContinue)) {
    try {
        [xml]$x = Get-Content -Raw -LiteralPath $f.FullName
        foreach ($v in $x.SelectNodes("//Properties[@name='Clarion.Versions']/Properties")) {
            $win = $v.SelectSingleNode("IsWindowsVersion")
            $nm  = $v.GetAttribute('name')
            if ($nm -and $win -and $win.GetAttribute('value') -eq 'True' -and $nm -notmatch 'Current' -and -not $names.Contains($nm)) { $names.Add($nm) }
        }
    } catch { }
}
if ($names.Count -lt 2) { Write-Host "COULD NOT RUN: fewer than two Win32 Clarion versions are configured on this machine." -ForegroundColor Yellow; exit 2 }
$v1 = $names[0]; $v2 = $names[1]
Write-Host "  versions: V1='$v1'  V2='$v2'" -ForegroundColor DarkGray

# ---------------------------------------------------------------- private bin, fake server, solution, IDE config dir
$work = Join-Path $env:TEMP ("ca-solver-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
$sol  = Join-Path $work 'sol'
$cfg  = Join-Path $work 'idecfg'
$log  = Join-Path $work 'updatepaths.log'
New-Item -ItemType Directory -Force $bin, $sol, (Join-Path $cfg 'preferences') | Out-Null
Copy-Item (Join-Path $binDir '*') $bin -Recurse -Force
$serverDir = Join-Path $bin 'lsp-server\out\server\src'
New-Item -ItemType Directory -Force $serverDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-version-follow\server.js') (Join-Path $serverDir 'server.js') -Force
Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-semantic-pass\*') $sol -Force
$sln = Join-Path $sol 'ctrl.sln'
$clw = Join-Path $sol 'ctrl.clw'

$recordDir  = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClarionAssistant\ide-solution'
$recordFile = Join-Path $recordDir ("ide-" + $PID + ".json")
New-Item -ItemType Directory -Force $recordDir | Out-Null
function Publish([string]$solution, [string]$version, [string]$configDir) {
    if ([string]::IsNullOrEmpty($solution)) { if (Test-Path $recordFile) { [System.IO.File]::Delete($recordFile) }; return }
    $rec = [ordered]@{ solution = $solution; pid = $PID; writtenAt = (Get-Date).ToString('o') }
    if ($version)   { $rec.clarionVersion = $version }
    if ($configDir) { $rec.configDir = $configDir }
    [System.IO.File]::WriteAllText($recordFile, ($rec | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
}
function LoggedVersions() {
    if (-not (Test-Path $log)) { return ,@() }
    # The leading comma keeps a one-element answer an array (else [-1] indexes the string's characters).
    return ,@(Get-Content $log | Where-Object { $_ } | ForEach-Object { [string](($_ | ConvertFrom-Json).params.clarionVersion) })
}

$p = $null
try {
    Publish $sln $v1 $cfg

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName  = Join-Path $bin 'clarion-mcp-server.exe'
    $psi.Arguments = "--stdio --ide-pid $PID --solution `"$sln`""
    $psi.WorkingDirectory       = $sol
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardErrorEncoding  = New-Object System.Text.UTF8Encoding($false)
    $psi.EnvironmentVariables['CA_FAKE_LSP_LOG'] = $log
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
    function Json([string]$text) { try { return $text | ConvertFrom-Json } catch { return $null } }
    function Show([string]$label, $value) {
        $flat = ([string]$value -replace '\s+', ' '); if ($flat.Length -gt 220) { $flat = $flat.Substring(0, 220) + '...' }
        Write-Host ("  {0,-26} {1}" -f $label, $flat) -ForegroundColor DarkGray
    }

    [void](Send 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'solution-version-e2e'; version = '1' } })
    [void](Send 'notifications/initialized' @{} -notify)

    # ---- A: the IDE has the solution on V1 ----
    $infoA = Json (Tool 'get_solution_info' @{})
    Show 'A get_solution_info' ("" + $infoA.versionName + " | " + $infoA.versionNote)
    Assert-That ($infoA -and $infoA.versionName -eq $v1) "A: get_solution_info should name V1 '$v1' (the IDE's live choice), got '$($infoA.versionName)' - note: $($infoA.versionNote)"
    Assert-That ($infoA -and ([string]$infoA.versionNote) -match 'live from the IDE') "A: versionNote should say the live IDE chose it, got: $($infoA.versionNote)"

    $dA = Tool 'lsp_diagnostics' @{ file_path = $clw; timeout_ms = 15000 }
    $diagA = Json $dA
    Show 'A lsp_diagnostics' $dA
    if (-not $diagA -or (LoggedVersions).Count -eq 0) {
        Write-Host "COULD NOT RUN: the scripted language server did not start or log (answer: $dA)." -ForegroundColor Yellow
        exit 2
    }
    Assert-That ((LoggedVersions)[-1] -eq $v1) "A: the language server was started with '$((LoggedVersions)[-1])', expected V1 '$v1'"
    Assert-That ($diagA.clarionVersion -eq $v1) "A: lsp_diagnostics should report clarionVersion V1, got '$($diagA.clarionVersion)'"
    Assert-That (([string]$diagA.clarionVersionChosenBy) -match 'live from the IDE') "A: lsp_diagnostics should say what chose the version, got '$($diagA.clarionVersionChosenBy)'"
    $startsA = (LoggedVersions).Count

    # ---- B: Build > Set Clarion Version switched the same solution to V2 ----
    Publish $sln $v2 $cfg
    $diagB = Json (Tool 'lsp_diagnostics' @{ file_path = $clw; timeout_ms = 15000 })
    Show 'B lsp_diagnostics' ("" + $diagB.clarionVersion + " | " + $diagB.clarionVersionChosenBy)
    Show 'B updatePaths log' ((LoggedVersions) -join ' ; ')
    Assert-That ((LoggedVersions).Count -gt $startsA) "B: the version changed but the language server was not restarted (no new updatePaths)"
    Assert-That ((LoggedVersions)[-1] -eq $v2) "B: the restarted server got '$((LoggedVersions)[-1])', expected V2 '$v2'"
    Assert-That ($diagB.clarionVersion -eq $v2) "B: lsp_diagnostics should report V2, got '$($diagB.clarionVersion)'"
    $infoB = Json (Tool 'get_solution_info' @{})
    Assert-That ($infoB.versionName -eq $v2) "B: get_solution_info should follow to V2, got '$($infoB.versionName)'"
    $startsB = (LoggedVersions).Count

    # ---- C: no live choice; the IDE's saved preferences for this solution say V1 ----
    # Two files for the solution's name, as on a real machine: the decoy, of another folder's ctrl.sln, says V2 and
    # records a path elsewhere; ours says V1 and records a path under this solution's folder.
    $prefs = Join-Path $cfg 'preferences'
    $ours  = "<?xml version=`"1.0`"?>`r`n<Properties>`r`n  <Array name=`"OpenFiles`">`r`n    <String value=`"$clw`" />`r`n  </Array>`r`n  <ActiveVersion value=`"$v1`" />`r`n</Properties>"
    $decoy = "<?xml version=`"1.0`"?>`r`n<Properties>`r`n  <Array name=`"OpenFiles`">`r`n    <String value=`"C:\elsewhere\ctrl.clw`" />`r`n  </Array>`r`n  <ActiveVersion value=`"$v2`" />`r`n</Properties>"
    [System.IO.File]::WriteAllText((Join-Path $prefs 'ctrl.sln.1a2b3c4d.xml'), $ours, (New-Object System.Text.UTF8Encoding($true)))
    [System.IO.File]::WriteAllText((Join-Path $prefs 'ctrl.sln.5e6f7a8b.xml'), $decoy, (New-Object System.Text.UTF8Encoding($true)))
    Publish $sln $null $cfg
    $infoC = Json (Tool 'get_solution_info' @{})
    Show 'C get_solution_info' ("" + $infoC.versionName + " | " + $infoC.versionNote)
    Assert-That ($infoC.versionName -eq $v1) "C: get_solution_info should take V1 from the IDE's saved preferences, got '$($infoC.versionName)' - note: $($infoC.versionNote)"
    Assert-That (([string]$infoC.versionNote) -match 'saved' -and ([string]$infoC.versionNote) -match 'ctrl\.sln\.1a2b3c4d\.xml') "C: the note should name the saved preferences file it used, got: $($infoC.versionNote)"
    $diagC = Json (Tool 'lsp_diagnostics' @{ file_path = $clw; timeout_ms = 15000 })
    Assert-That ((LoggedVersions).Count -gt $startsB -and (LoggedVersions)[-1] -eq $v1) "C: the language server should have restarted with V1, log: $((LoggedVersions) -join ' ; ')"

    # ---- D: the IDE is on 'Current' for this solution: neither V1 nor V2 by way of the IDE ----
    Publish $sln 'Current' $cfg
    $infoD = Json (Tool 'get_solution_info' @{})
    Show 'D get_solution_info' ("" + $infoD.versionName + " | " + $infoD.versionNote)
    Assert-That (([string]$infoD.versionNote) -match "'Current'") "D: the note should say the IDE is on 'Current', got: $($infoD.versionNote)"
    Assert-That ($infoD.versionName -ne $v1 -or ([string]$infoD.versionNote) -notmatch 'saved') "D: a live 'Current' must not fall back to the saved preferences (V1), got '$($infoD.versionName)': $($infoD.versionNote)"
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
