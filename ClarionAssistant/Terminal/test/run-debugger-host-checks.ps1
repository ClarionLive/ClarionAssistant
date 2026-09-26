# run-debugger-host-checks.ps1 - builds and runs the C# host-side checks for the CA Debugger hooks (f022fb4e).
#
#   powershell -ExecutionPolicy Bypass -File Terminal\test\run-debugger-host-checks.ps1
#
# Two harnesses, compiled against the REAL sources:
#   DebuggerHookGuardsCheck  Services\DocumentLineGuard.cs + Services\ExecutionLineGate.cs, executed directly.
#   DebuggerBridgeCheck      Services\ClarionDebuggerBridge.cs, in eight scenarios. Each scenario is its own
#                            process AND its own ASSEMBLY NAME, because that is exactly what the bridge's
#                            identity and ambiguity guards are about: the same source compiled as
#                            ClarionDebugger.exe and as SomeOtherAddin.exe must behave differently. The
#                            bridge also caches its binding in statics, so one process can ask one question.
#
# Artifacts go to a temp folder; nothing is written into the repo. Exit code 0 = all pass.
[CmdletBinding()]
param([string]$OutDir = (Join-Path $env:TEMP ("ca-debugger-host-checks-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))))

$ErrorActionPreference = 'Stop'
$testDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root    = Resolve-Path (Join-Path $testDir '..\..')          # ClarionAssistant\
$bridgeCheck = Join-Path $testDir 'DebuggerBridgeCheck.cs'
$guardsCheck = Join-Path $testDir 'DebuggerHookGuardsCheck.cs'
$bridge      = Join-Path $root 'Services\ClarionDebuggerBridge.cs'
$lineGuard   = Join-Path $root 'Services\DocumentLineGuard.cs'
$execGate    = Join-Path $root 'Services\ExecutionLineGate.cs'
$dupSrc      = Join-Path $testDir 'fixtures\DuplicateClarionDebugger.cs'

$csc = Get-ChildItem -Path @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\Roslyn\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $csc) { Write-Error "No C# compiler found (looked for VS2022 Roslyn csc.exe and the .NET Framework csc.exe)." }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'dup') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'dup2') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'boevoid') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'boenone') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'boewrongret') | Out-Null
Write-Host "csc:  $($csc.FullName)"
Write-Host "out:  $OutDir`n"

function Build([string]$outFile, [string[]]$sources, [string[]]$extra) {
    $cscArgs = @('/nologo', "/out:$outFile") + $extra + $sources
    & $csc.FullName @cscArgs
    if ($LASTEXITCODE -ne 0) { Write-Error "compile failed: $outFile" }
}

Build (Join-Path $OutDir 'HookGuards.exe')          @($guardsCheck, $lineGuard, $execGate) @()
# The assembly NAME is the point of each bridge build, and csc takes it from /out.
Build (Join-Path $OutDir 'ClarionDebugger.exe')     @($bridgeCheck, $bridge) @()
Build (Join-Path $OutDir 'SomeOtherAddin.exe')      @($bridgeCheck, $bridge) @()
Build (Join-Path $OutDir 'NoClarionDebugger.exe')   @($bridgeCheck, $bridge) @('/define:NO_DEBUGGER')
Build (Join-Path $OutDir 'dup\ClarionDebugger.dll')  @($dupSrc) @('/target:library')
Build (Join-Path $OutDir 'dup2\ClarionDebugger.dll') @($dupSrc) @('/target:library', '/define:DUP2')
# e61e4f92: a debugger with the OLD void BreakOnProcEntry, and one with none. Named ClarionDebugger, so each
# binds, and only the optional member can differ.
Build (Join-Path $OutDir 'boevoid\ClarionDebugger.exe') @($bridgeCheck, $bridge) @('/define:BOE_VOID')
Build (Join-Path $OutDir 'boenone\ClarionDebugger.exe') @($bridgeCheck, $bridge) @('/define:BOE_NONE')
Build (Join-Path $OutDir 'boewrongret\ClarionDebugger.exe') @($bridgeCheck, $bridge) @('/define:BOE_WRONGRET')

$failed = @()
function Run([string]$title, [string]$exe, [string[]]$exeArgs) {
    Write-Host "-- $title ---------------------------------------------"
    & (Join-Path $OutDir $exe) @exeArgs
    if ($LASTEXITCODE -ne 0) { $script:failed += $title }
    Write-Host ''
}

Run 'run-to-cursor line + execution-line marker guards' 'HookGuards.exe'        @()
Run 'bridge: installed debugger'                        'ClarionDebugger.exe'   @('bound')
Run 'bridge: debugger not installed'                    'NoClarionDebugger.exe' @('none')
Run 'bridge: controller in a foreign assembly'          'SomeOtherAddin.exe'    @('decoy')
Run 'bridge: two ClarionDebugger assemblies'            'ClarionDebugger.exe'   @('ambiguous', (Join-Path $OutDir 'dup\ClarionDebugger.dll'))
Run 'bridge: debugger loads mid-session'                'NoClarionDebugger.exe' @('late', (Join-Path $OutDir 'dup\ClarionDebugger.dll'), (Join-Path $OutDir 'dup2\ClarionDebugger.dll'))
Run 'bridge: old void BreakOnProcEntry'                 'boevoid\ClarionDebugger.exe' @('boevoid')
Run 'bridge: no BreakOnProcEntry'                       'boenone\ClarionDebugger.exe' @('boenone')
Run 'bridge: BreakOnProcEntry with no bool to return'   'boewrongret\ClarionDebugger.exe' @('boewrongret')

if ($failed.Count) {
    Write-Host ("CHECKS FAILED: " + ($failed -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host 'ALL CHECKS PASS' -ForegroundColor Green
exit 0
