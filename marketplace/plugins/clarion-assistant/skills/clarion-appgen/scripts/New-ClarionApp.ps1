<#
.SYNOPSIS
    Round-trip-to-zero rig: authored text (.dctx + .txa) -> .dct -> .app -> source -> .exe -> runs.
    One command, one honest pass/fail, full diagnostics on failure.

.DESCRIPTION
    Turns a folder of hand-authored Clarion text into a running executable with no IDE, and
    reports each stage as a scored step. This exists so that "can I author a Clarion app from a
    description?" becomes a green/red measurement instead of a manual slog.

        .dctx --/di--> .dct
        .txa  --/ai--> .app --/ag--> .clw/.inc --MSBuild--> .exe --> run

    Every ClarionCL stage goes through Invoke-ClarionCL.ps1, so an invisible modal dialog is
    captured and reported rather than hanging the run to a timeout.

    The build directory is a COPY of the spec folder. The spec is never written to, so a failed
    attempt leaves the authored inputs pristine and the whole build dir is disposable.

.PARAMETER SpecPath
    Folder holding the authored inputs: exactly one <App>.txa, optionally one <App>.dctx, plus
    any hand-coded .clw/.inc to compile alongside the generated modules.

.PARAMETER OutputPath
    Build directory. Default: <SpecPath>\.build. Created if absent.

.PARAMETER AppName
    App base name. Default: the base name of the single .txa in SpecPath.

.PARAMETER Run
    After a successful build, launch the .exe, watch it for -RunSeconds, and fail the run if it
    exits non-zero or raises a dialog (a GPF or runtime-error box). A windowed app that is still
    alive at the end of the watch counts as a PASS, and is then closed.

.PARAMETER ForIde
    Also write a .sln with the shape the IDE itself writes (incl. the "Solution Items" block that
    associates the .app). Not needed for a headless build.

.PARAMETER Clean
    Delete the build directory before starting. Use for a true cold run — otherwise stale
    artifacts from a previous attempt can make a broken step look like it passed.

.PARAMETER Force
    Rebuild even when the existing .app is newer than the spec. By default that mismatch is a
    hard stop: an .app newer than its .txa usually means someone edited it in the IDE (embeds
    live only in the .app until exported), and rebuilding from the spec would discard that work.
    -Force declares the spec authoritative and accepts the loss. See Sync-SpecFromApp.ps1.

.OUTPUTS
    [pscustomobject] Ok, App, BuildDir, Exe, Steps[], FailedStep, Message.
    Each step: Name, Ok, DurationMs, Detail, Dialogs, Output.

.EXAMPLE
    $r = .\New-ClarionApp.ps1 -SpecPath .\specs\orders -Run -Clean
    if (-not $r.Ok) { $r.FailedStep; $r.Steps[-1].Output }

.NOTES
    Recipe and traps: see the clarion-appgen skill's references/clarioncl-reference.md
    (switches, modal classes, redirection, .cwproj) and references/failure-signatures.md.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$SpecPath,
    [string]$OutputPath,
    [string]$AppName,
    [string]$ClarionRoot = 'C:\Clarion12',
    [string]$RedName = 'Clarion120.red',
    [string]$MSBuildPath = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe",
    [string[]]$FileDriver,
    [switch]$Run,
    [int]$RunSeconds = 8,
    [switch]$ForIde,
    [int]$TimeoutSeconds = 180,
    [switch]$Clean,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$wrapper = Join-Path $PSScriptRoot 'Invoke-ClarionCL.ps1'
if (-not (Test-Path -LiteralPath $wrapper)) { throw "Invoke-ClarionCL.ps1 not found next to this script: $wrapper" }

$clarionBin = Join-Path $ClarionRoot 'bin'
$clarionCL  = Join-Path $clarionBin 'ClarionCL.exe'
if (-not (Test-Path -LiteralPath $clarionCL))  { throw "ClarionCL.exe not found: $clarionCL" }
if (-not (Test-Path -LiteralPath $MSBuildPath)) { throw "MSBuild not found: $MSBuildPath" }

# ---------------------------------------------------------------- step plumbing
$steps  = [System.Collections.Generic.List[object]]::new()
$failed = $null

function Add-Step {
    param([string]$Name, [scriptblock]$Body)
    if ($script:failed) { return }                       # stop at the first failure
    $t0 = Get-Date
    $ok = $false; $detail = ''; $dialogs = @(); $output = ''
    try {
        $r = & $Body
        if ($r -is [hashtable]) {
            $ok = [bool]$r.Ok; $detail = [string]$r.Detail
            if ($r.ContainsKey('Dialogs')) { $dialogs = @($r.Dialogs) }
            if ($r.ContainsKey('Output'))  { $output  = [string]$r.Output }
        } else { $ok = $true; $detail = [string]$r }
    } catch {
        $ok = $false; $detail = $_.Exception.Message
    }
    $steps.Add([pscustomobject]@{
        Name = $Name; Ok = $ok; DurationMs = [int]((Get-Date) - $t0).TotalMilliseconds
        Detail = $detail; Dialogs = $dialogs; Output = $output
    })
    Write-Host ("  [{0}] {1,-22} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $Name, $detail)
    if (-not $ok) { $script:failed = $Name }
}

function Find-GeneratedClw {
    # Redirection decides where /ag writes .clw (commonly '.\Source'), so search the whole build
    # tree. Exclude obj\ - intermediate output, never a compile input.
    Get-ChildItem -LiteralPath $OutputPath -Filter '*.clw' -File -Recurse |
        Where-Object { $_.FullName -notmatch '\\obj\\' } | Sort-Object Name
}

function Invoke-CCL {
    # NOT named $Args - that collides with PowerShell's automatic variable and arrives empty.
    param([string[]]$CclArgs, [string]$Dir, [string[]]$Expect = @())
    & $wrapper -Arguments $CclArgs -WorkingDirectory $Dir -ExpectArtifact $Expect `
               -TimeoutSeconds $TimeoutSeconds -DismissDialogs -ClarionCLPath $clarionCL
}

function Format-CclFailure {
    param($r)
    if ($r.TimedOut) {
        $d = if ($r.Dialogs.Count) { ($r.Dialogs | ForEach-Object { "dialog '$($_.Title)': $($_.Text)" }) -join ' / ' }
             else { 'no dialog seen' }
        return "TIMED OUT after ${TimeoutSeconds}s ($d)"
    }
    if ($r.StaleArtifacts.Count) { return "exit $($r.ExitCode) but produced nothing: $($r.StaleArtifacts -join ', ')" }
    return "exit $($r.ExitCode) (ClarionCL exit code = error count)"
}

# ---------------------------------------------------------------- resolve spec
$SpecPath = (Resolve-Path -LiteralPath $SpecPath).Path
# Exclude <App>.export.txa: Sync-SpecFromApp.ps1 writes it INTO the spec folder beside the
# authored .txa. Counting it here means one capture permanently breaks every later build with
# "found 2". The authored .txa is the only input; the export is a snapshot.
$txas = @(Get-ChildItem -LiteralPath $SpecPath -Filter '*.txa' -File |
          Where-Object { $_.Name -notlike '*.export.txa' })
if ($txas.Count -ne 1) { throw "SpecPath must contain exactly one authored .txa (found $($txas.Count)): $SpecPath" }
if (-not $AppName) { $AppName = $txas[0].BaseName }

if (-not $OutputPath) { $OutputPath = Join-Path $SpecPath '.build' }

# --------------------------------------------------- guard: app changed out of band
# The .app is a build artifact HERE, but the moment someone opens it in the IDE and adds an
# embed it becomes the only copy of that work - the spec .txa has no record of it. Rebuilding
# from the spec would then silently discard it, and -Clean would delete the .app outright.
#
# Comparing .app mtime against .txa mtime CANNOT express that: a successful build always leaves
# the .app newer than the spec, so every rerun of the documented start-here command trips the
# guard, and users learn to pass -Force reflexively - which defeats the only case it exists for.
# So stamp the .app we produced, and block only when it has changed SINCE. That is what "someone
# edited it outside this script" actually looks like.
# Sync-SpecFromApp.ps1 refreshes the spec from the app; -Force says "discard, the spec wins".
$guardApp  = Join-Path $OutputPath "$AppName.app"
$stampFile = Join-Path $OutputPath "$AppName.buildstamp"
if ((Test-Path -LiteralPath $guardApp) -and -not $Force) {
    $appTicks = (Get-Item -LiteralPath $guardApp).LastWriteTime.Ticks
    $stamped  = if (Test-Path -LiteralPath $stampFile) {
                    (Get-Content -LiteralPath $stampFile -Raw).Trim()
                } else { $null }
    if ($stamped -ne "$appTicks") {
        $why = if ($stamped) { 'has changed since this script built it' }
               else          { 'was not built by this script' }
        throw ("$AppName.app $why - it may hold IDE edits (embeds) the spec does not. " +
               "Rebuilding would discard them. Run Sync-SpecFromApp.ps1 to capture the app " +
               "first, or pass -Force to rebuild from the spec anyway.")
    }
}

if ($Clean -and (Test-Path -LiteralPath $OutputPath)) {
    # A process holding the build dir as its CURRENT WORKING DIRECTORY (the Clarion IDE does this
    # if you ever opened an app from here) lets you delete the CONTENTS but not the directory
    # itself. Emptying it is all -Clean actually needs, so don't abort the run over the husk.
    Remove-Item -LiteralPath $OutputPath -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $OutputPath) {
        Get-ChildItem -LiteralPath $OutputPath -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        $left = @(Get-ChildItem -LiteralPath $OutputPath -Force -Recurse -ErrorAction SilentlyContinue)
        if ($left.Count) {
            throw ("-Clean could not empty $OutputPath - $($left.Count) item(s) locked, first: " +
                   "$($left[0].Name). Close whatever holds them, or pass -OutputPath <fresh dir>.")
        }
        Write-Host "  (build dir emptied but pinned by another process - continuing)" -ForegroundColor DarkYellow
    }
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path
$objDir = Join-Path $OutputPath 'obj'
New-Item -ItemType Directory -Path $objDir -Force | Out-Null

$appFile = Join-Path $OutputPath "$AppName.app"
$exeFile = Join-Path $OutputPath "$AppName.exe"

Write-Host "ClarionAppGen: $AppName" -ForegroundColor Cyan
Write-Host "  spec  $SpecPath"
Write-Host "  build $OutputPath"

# ---------------------------------------------------------------- 1. stage inputs
Add-Step 'stage-inputs' {
    # -File already excludes the .build directory. What DOES need excluding is a captured
    # <App>.export.txa - copying it in would put two .txa files in the build dir.
    Get-ChildItem -LiteralPath $SpecPath -File |
        Where-Object { $_.Name -notlike '*.export.txa' } |
        Copy-Item -Destination $OutputPath -Force
    # Count what we COPIED, not what the build dir now holds - the old count included artifacts
    # from a previous run and reported "13 file(s) copied" for a one-file spec.
    $n = @(Get-ChildItem -LiteralPath $SpecPath -File |
           Where-Object { $_.Name -notlike '*.export.txa' }).Count
    @{ Ok = $true; Detail = "$n file(s) copied from spec" }
}

# ---------------------------------------------------------------- 2. redirection
# Derive from the global .red; localize ONLY relative output paths. Absolute paths and %MACRO%
# entries are search paths (ABC libsrc lives behind %ROOT%) and MUST survive untouched.
# See references/clarioncl-reference.md, "Redirection traps".
Add-Step 'redirection' {
    $globalRed = Join-Path $clarionBin $RedName
    if (-not (Test-Path -LiteralPath $globalRed)) { return @{ Ok = $false; Detail = "global red not found: $globalRed" } }

    $intermediate = @('obj', 'res', 'rsc', 'lib', 'map', 'filelist.xml')
    $rewritten = 0
    $lines = Get-Content -LiteralPath $globalRed | ForEach-Object {
        $line = $_
        if ($line -match '^(\s*)([^=\[;]+?)\s*=\s*(.+?)\s*$' -and $line -notmatch '^\s*--') {
            $indent = $Matches[1]; $lhs = $Matches[2].Trim(); $rhs = $Matches[3]
            $trailing = if ($rhs -match ';\s*$') { '; ' } else { '' }
            $ext = ($lhs -replace '^\*\.', '').ToLowerInvariant()
            $target = if ($intermediate -contains $ext) { '.\obj' } else { '.' }

            $parts = $rhs -split ';' | Where-Object { $_.Trim().Length }
            $new = foreach ($p in $parts) {
                $t = $p.Trim()
                # Localize ONLY paths that ESCAPE the build directory - '..\v8Source' and friends
                # point into the developer's own tree and would send output somewhere invisible.
                # Everything else survives verbatim: absolute paths, %MACRO% paths (ABC libsrc
                # lives behind %ROOT%), and project-local '.\x' search paths such as '.\images'.
                # Flattening those last ones is precisely the mistake that broke app opening.
                if ($t -match '^\.\.[\\/]' -or $t -match '^\.[^\\/.]') { $target } else { $t }
            }
            $joined = ($new | Select-Object -Unique) -join '; '
            if ($joined -ne (($parts | ForEach-Object { $_.Trim() }) -join '; ')) { $rewritten++ }
            "$indent$lhs = $joined$trailing"
        } else { $line }
    }
    $localRed = Join-Path $OutputPath $RedName
    # .red must keep the VERSION name to override the global one - a differently-named file is
    # silently ignored.
    Set-Content -LiteralPath $localRed -Value $lines -Encoding ASCII
    @{ Ok = $true; Detail = "$RedName derived from global, $rewritten line(s) localized" }
}

# ---------------------------------------------------------------- 3. dictionary
$dctxs = @(Get-ChildItem -LiteralPath $SpecPath -Filter '*.dctx' -File)
$dctFile = $null
if ($dctxs.Count -gt 1) { throw "SpecPath has more than one .dctx: $($dctxs.Name -join ', ')" }
if ($dctxs.Count -eq 1) {
    $dctFile = Join-Path $OutputPath ($dctxs[0].BaseName + '.dct')
    Add-Step 'dictionary /di' {
        $src = Join-Path $OutputPath $dctxs[0].Name
        # /di <dct> <textfile> - dct is the OUTPUT
        $r = Invoke-CCL -CclArgs @('/au', '/di', $dctFile, $src) -Dir $OutputPath -Expect @($dctFile)
        if ($r.Effective) {
            @{ Ok = $true; Detail = "$([IO.Path]::GetFileName($dctFile)) ($([int]((Get-Item $dctFile).Length/1KB)) KB)"
               Output = $r.StdOut; Dialogs = $r.Dialogs }
        } else {
            @{ Ok = $false; Detail = (Format-CclFailure $r); Output = ($r.StdOut + $r.StdErr); Dialogs = $r.Dialogs }
        }
    }
}

# ---------------------------------------------------------------- 4. text -> app
Add-Step 'import /ai' {
    $txa = Join-Path $OutputPath $txas[0].Name
    # /ai CREATES the .app when it does not exist - the discovery this whole rig rests on.
    $r = Invoke-CCL -CclArgs @('/au', '/ai', $appFile, $txa) -Dir $OutputPath -Expect @($appFile)
    if ($r.Effective) {
        @{ Ok = $true; Detail = "$AppName.app ($([int]((Get-Item $appFile).Length/1KB)) KB)"
           Output = $r.StdOut; Dialogs = $r.Dialogs }
    } else {
        @{ Ok = $false; Detail = (Format-CclFailure $r); Output = ($r.StdOut + $r.StdErr); Dialogs = $r.Dialogs }
    }
}

# ---------------------------------------------------------------- 5. app -> source
Add-Step 'generate /ag' {
    # WHERE /ag writes the .clw is decided by redirection, not by us. A stock red often carries
    # '*.clw = .\Source', which puts every generated module in <build>\Source. So we cannot name
    # the expected artifact up front - pass no -ExpectArtifact and do the freshness check here,
    # searching recursively for whatever /ag actually produced.
    # Clarion's codegen SKIPS writing a module whose generated content is unchanged - /agc off
    # turns CONDITIONAL generation off, it does not force a rewrite of identical output. So a warm
    # rerun legitimately leaves every .clw at its previous mtime. Hash them first, so we can tell
    # "not written because nothing changed" (fine) from "not written because the run silently did
    # nothing" (the failure this check exists to catch). A false alarm here is the worst outcome
    # available to this package: it teaches people to distrust the real alarms.
    $before = @{}
    foreach ($f in Find-GeneratedClw) {
        $before[$f.FullName] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    $launch = Get-Date
    $r = Invoke-CCL -CclArgs @('/au', '/agc', 'off', '/ag', $appFile) -Dir $OutputPath
    if (-not $r.Effective) {
        return @{ Ok = $false; Detail = (Format-CclFailure $r); Output = ($r.StdOut + $r.StdErr); Dialogs = $r.Dialogs }
    }
    $mods = @(Find-GeneratedClw)
    $main = $mods | Where-Object { $_.Name -ieq "$AppName.clw" } | Select-Object -First 1
    if (-not $main) {
        return @{ Ok = $false; Dialogs = $r.Dialogs; Output = ($r.StdOut + $r.StdErr)
                  Detail = ("exit 0 but no $AppName.clw found anywhere under $OutputPath - check " +
                            "where the .red sends *.clw") }
    }
    $where = if ($main.DirectoryName -ne $OutputPath) { " in $(Split-Path $main.DirectoryName -Leaf)\" } else { '' }
    if ($main.LastWriteTime -lt $launch) {
        # Stale is only acceptable when the file is PROVABLY the one we already validated.
        $prior = $before[$main.FullName]
        $now   = (Get-FileHash -LiteralPath $main.FullName -Algorithm SHA256).Hash
        if ($prior -and $prior -eq $now) {
            return @{ Ok = $true; Output = $r.StdOut; Dialogs = $r.Dialogs
                      Detail = "$($mods.Count) module(s) unchanged (no-op regeneration)$where" }
        }
        return @{ Ok = $false; Dialogs = $r.Dialogs; Output = ($r.StdOut + $r.StdErr)
                  Detail = "exit 0 but $AppName.clw is stale (not rewritten this run)" }
    }
    @{ Ok = $true; Detail = "$($mods.Count) module(s) generated$where"; Output = $r.StdOut; Dialogs = $r.Dialogs }
}

# ---------------------------------------------------------------- 6. project file
Add-Step 'write cwproj' {
    # Compile items need FULL REAL paths - the CW task does not resolve bare includes via
    # redirection under any RedFile setting, and no subst drives (they leak into the artifact).
    # See references/clarioncl-reference.md, ".cwproj".
    # Recursive: redirection may have sent the generated modules to a subfolder such as .\Source.
    $mods = @(Find-GeneratedClw)
    if (-not $mods.Count) { return @{ Ok = $false; Detail = "no .clw modules to compile under $OutputPath" } }

    # Deterministic ProjectGuid from the app name, so re-runs and the .sln always agree.
    $md5   = [Security.Cryptography.MD5]::Create()
    $guid  = ([guid]::new($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes("ClarionAppGen:$AppName")))).ToString().ToUpperInvariant()

    if (-not $FileDriver) {
        $FileDriver = if ($dctxs.Count) {
            @(Select-String -LiteralPath $dctxs[0].FullName -Pattern 'Driver="([^"]+)"' -AllMatches |
              ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        } else { @() }
    }

    $sb = [Text.StringBuilder]::new()
    [void]$sb.AppendLine('<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">')
    [void]$sb.AppendLine('  <PropertyGroup>')
    [void]$sb.AppendLine("    <ProjectGuid>{$guid}</ProjectGuid>")
    [void]$sb.AppendLine("    <Configuration Condition=`" '`$(Configuration)' == '' `">Debug</Configuration>")
    [void]$sb.AppendLine("    <Platform Condition=`" '`$(Platform)' == '' `">Win32</Platform>")
    [void]$sb.AppendLine('    <OutputType>Exe</OutputType>')
    [void]$sb.AppendLine("    <AssemblyName>$AppName</AssemblyName>")
    [void]$sb.AppendLine("    <OutputName>$AppName</OutputName>")
    [void]$sb.AppendLine('    <DefineConstants>_ABCDllMode_=&gt;0%3b_ABCLinkMode_=&gt;1</DefineConstants>')
    [void]$sb.AppendLine('    <Model>Dll</Model>')
    [void]$sb.AppendLine('  </PropertyGroup>')
    [void]$sb.AppendLine("  <PropertyGroup Condition=`" '`$(Configuration)' == 'Debug' `">")
    [void]$sb.AppendLine('    <DebugSymbols>True</DebugSymbols><DebugType>Full</DebugType><vid>full</vid>')
    [void]$sb.AppendLine('  </PropertyGroup>')
    [void]$sb.AppendLine('  <ItemGroup>')
    foreach ($m in $mods) {
        $gen = if ($m.BaseName -like "$AppName*") { 'true' } else { 'false' }
        [void]$sb.AppendLine("    <Compile Include=`"$($m.FullName)`"><Generated>$gen</Generated></Compile>")
    }
    [void]$sb.AppendLine('  </ItemGroup>')
    [void]$sb.AppendLine('  <ItemGroup>')
    foreach ($d in $FileDriver) { [void]$sb.AppendLine("    <FileDriver Include=`"$d`" />") }
    [void]$sb.AppendLine('    <Library Include="C%25V%25DF%25X%25.LIB" />')
    [void]$sb.AppendLine('  </ItemGroup>')
    [void]$sb.AppendLine('  <Import Project="$(ClarionBinPath)\SoftVelocity.Build.Clarion.targets" />')
    [void]$sb.Append('</Project>')

    $cwproj = Join-Path $OutputPath "$AppName.cwproj"
    Set-Content -LiteralPath $cwproj -Value $sb.ToString() -Encoding UTF8

    if ($ForIde) {
        # Shape the IDE itself writes. The "Solution Items" block is what associates the .app -
        # omitting it is the likely cause of the solution-association modal (see
        # references/clarioncl-reference.md, "Modal dialogs").
        $slnGuid = '12B76EC0-1D7B-4FA7-A7D0-C524288B48A1'   # Clarion project type
        $folder  = '2150E333-8FDC-42A3-9474-1A3956D46DE8'   # VS solution folder
        $sln = @"

Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio 2012
# Clarion 2.1.0.2447
Project("{$slnGuid}") = "$AppName", "$AppName.cwproj", "{$guid}"
EndProject
Project("{$folder}") = "Solution Items", "Solution Items", "{$folder}"
	ProjectSection(SolutionItems) = postProject
		$AppName.app = $AppName.app
	EndProjectSection
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Win32 = Debug|Win32
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{$guid}.Debug|Win32.Build.0 = Debug|Win32
		{$guid}.Debug|Win32.ActiveCfg = Debug|Win32
	EndGlobalSection
EndGlobal
"@
        Set-Content -LiteralPath (Join-Path $OutputPath "$AppName.sln") -Value $sln -Encoding ASCII
    }

    $drv = if ($FileDriver) { ", drivers: $($FileDriver -join ',')" } else { '' }
    @{ Ok = $true; Detail = "$($mods.Count) Compile item(s)$drv" }
}

# ---------------------------------------------------------------- 7. compile
Add-Step 'MSBuild' {
    $env:ClarionBinPath = $clarionBin
    $cwproj  = Join-Path $OutputPath "$AppName.cwproj"
    $logFile = Join-Path $env:TEMP ("msb_{0}.log" -f [guid]::NewGuid().ToString('N'))
    # Same reasoning as the /ag step: MSBuild skips relinking an .exe that is already up to date,
    # so on a warm rerun a correct build legitimately leaves the .exe untouched. Hash first.
    $exeBefore = if (Test-Path -LiteralPath $exeFile) {
                     (Get-FileHash -LiteralPath $exeFile -Algorithm SHA256).Hash
                 } else { $null }
    $launch  = Get-Date

    # Quote the project path: an unquoted array element containing a space (an ordinary
    # "C:\Users\First Last\..." profile path) splits into two arguments and MSBuild sees garbage.
    $q = if ($cwproj -match '\s') { '"' + $cwproj + '"' } else { $cwproj }
    $p = Start-Process -FilePath $MSBuildPath -WorkingDirectory $OutputPath -NoNewWindow -PassThru `
         -ArgumentList "$q /p:Configuration=Debug /p:Platform=Win32 /nologo /v:minimal" `
         -RedirectStandardOutput $logFile
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        try { Stop-Process -Id $p.Id -Force } catch {}
        return @{ Ok = $false; Detail = "MSBuild timed out after ${TimeoutSeconds}s" }
    }
    # Never filter build output - the explanatory line sits ADJACENT to the 'error' line.
    $log = if (Test-Path $logFile) { Get-Content $logFile -Raw } else { '' }
    Remove-Item $logFile -Force -ErrorAction SilentlyContinue

    # The .exe location is redirection-dependent too. If it is not at the build root, find it.
    if (-not (Test-Path -LiteralPath $exeFile)) {
        $found = Get-ChildItem -LiteralPath $OutputPath -Filter "$AppName.exe" -File -Recurse -ErrorAction SilentlyContinue |
                 Where-Object { $_.FullName -notmatch '\\obj\\' } | Select-Object -First 1
        if ($found) { $script:exeFile = $found.FullName }
    }

    $exists = Test-Path -LiteralPath $exeFile
    $fresh  = $exists -and ((Get-Item $exeFile).LastWriteTime -ge $launch)
    # An .exe left untouched is acceptable ONLY when it is provably the same binary we already
    # validated - i.e. MSBuild judged it up to date. Anything else stays a failure.
    $sameAsBefore = $exists -and $exeBefore -and
                    ((Get-FileHash -LiteralPath $exeFile -Algorithm SHA256).Hash -eq $exeBefore)
    if ($p.ExitCode -eq 0 -and ($fresh -or $sameAsBefore)) {
        $note = if (-not $fresh) { ' unchanged (up to date)' } else { '' }
        @{ Ok = $true; Detail = "$AppName.exe ($([int]((Get-Item $exeFile).Length/1KB)) KB)$note"; Output = $log }
    } elseif ($p.ExitCode -eq 0) {
        @{ Ok = $false; Detail = 'MSBuild exit 0 but no fresh .exe'; Output = $log }
    } else {
        @{ Ok = $false; Detail = "MSBuild exit $($p.ExitCode)"; Output = $log }
    }
}

# ---------------------------------------------------------------- 8. run
if ($Run) {
    Add-Step 'run' {
        $old = $env:PATH
        $env:PATH = "$clarionBin;$old"          # ClaRUN.dll et al without copying them in
        try {
            $p = Start-Process -FilePath $exeFile -WorkingDirectory $OutputPath -PassThru
            $deadline = (Get-Date).AddSeconds($RunSeconds)
            $seen = @{}; $dialogs = @()
            $canWatch = ('ClarionCLWrapper.Native' -as [type]) -ne $null   # loaded by the wrapper

            while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
                Start-Sleep -Milliseconds 300
                if (-not $canWatch) { continue }
                $tree = [ClarionCLWrapper.Native]::GetProcessTree($p.Id)
                foreach ($w in [ClarionCLWrapper.Native]::GetVisibleWindows($tree)) {
                    if ($seen.ContainsKey($w.Hwnd.ToString())) { continue }
                    $seen[$w.Hwnd.ToString()] = $true
                    # The app's OWN window is expected; an error box is not. Treat a window with
                    # buttons but no client area we asked for as suspicious only if it is modal-shaped.
                    if ($w.ClassName -match '^#32770$' -or $w.Title -match 'error|exception|GPF|fault') {
                        $dialogs += [pscustomobject]@{ Title = $w.Title; Text = ($w.StaticTexts -join ' | '); Buttons = @($w.Buttons) }
                    }
                }
            }

            if ($p.HasExited -and $p.ExitCode -ne 0) {
                return @{ Ok = $false; Detail = "exited early with code $($p.ExitCode)"; Dialogs = $dialogs }
            }
            if ($dialogs.Count) {
                $d = ($dialogs | ForEach-Object { "'$($_.Title)': $($_.Text)" }) -join ' / '
                if (-not $p.HasExited) { try { $p.Kill() } catch {} }
                return @{ Ok = $false; Detail = "raised a dialog - $d"; Dialogs = $dialogs }
            }
            if ($p.HasExited) { return @{ Ok = $true; Detail = "ran and exited 0" } }

            $null = $p.CloseMainWindow(); Start-Sleep -Milliseconds 800
            if (-not $p.HasExited) { try { $p.Kill() } catch {} }
            @{ Ok = $true; Detail = "alive and healthy for ${RunSeconds}s, window closed cleanly" }
        } finally { $env:PATH = $old }
    }
}

# ---------------------------------------------------------------- stamp what we built
# Record the .app we produced, so the next run can tell "my own artifact" from "someone opened
# this in the IDE". Written even after a failed run: the .app on disk is still ours, and leaving
# it unstamped would block the next attempt for the wrong reason.
if (Test-Path -LiteralPath $guardApp) {
    Set-Content -LiteralPath $stampFile -Encoding ASCII `
                -Value ((Get-Item -LiteralPath $guardApp).LastWriteTime.Ticks)
}

# ---------------------------------------------------------------- verdict
$ok = -not $failed
Write-Host ''
if ($ok) { Write-Host "GREEN - $AppName" -ForegroundColor Green }
else     { Write-Host "RED - failed at: $failed" -ForegroundColor Red }

[pscustomobject]@{
    Ok         = $ok
    App        = $AppName
    BuildDir   = $OutputPath
    Exe        = if (Test-Path -LiteralPath $exeFile) { $exeFile } else { $null }
    Steps      = @($steps)
    FailedStep = $failed
    Message    = if ($ok) { "$AppName built" } else { ($steps | Where-Object { -not $_.Ok } | Select-Object -First 1).Detail }
}
