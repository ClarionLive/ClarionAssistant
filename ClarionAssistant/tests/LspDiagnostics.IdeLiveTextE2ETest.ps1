# End-to-end guard for ticket 44a1b10c: lsp_diagnostics, served by the STANDALONE clarion-mcp-server.exe, checks the
# text open in the IDE by asking the IDE pane (get_live_text over its MCP endpoint), else the disk with a reason.
#
# WHY END TO END. 03b55cb passed an in-process harness and failed live: lsp_diagnostics is not IdeOnly, so it runs in
# the standalone process, where the addin's editor lookup did not exist. Only a test that drives the real standalone exe
# through its stdio, with a real IDE-shaped HTTP endpoint found through the real records, sees which process answers.
# See LspDiagnostics.IdeLiveTextE2E.cs for the cases.
#
# MUST BE ABLE TO GO RED: against 03b55cb's standalone (pass -StandaloneExe <its clarion-mcp-server.exe>) 12 of 13
# fail: every call answers analysed:disk with no reason and no [live-text] line, the buffer never reaches the language
# server, and the pid-reused record is left in place. That is exactly what the live test showed.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\LspDiagnostics.IdeLiveTextE2ETest.ps1 [-StandaloneExe <exe>]
# Exit: 0 pass, 1 fail, 2 could-not-run (the suite counts 2 as a failure, never as green).
param([string]$StandaloneExe)

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$msbuild = $null
$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    # VS2022 (17.x) explicitly: in a worktree `-latest` can pick SSMS's MSBuild, which has no Restore target.
    $msbuild = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    $vsRoot = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild -property installationPath |
              Select-Object -First 1
    if ($vsRoot) {
        $c = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
        if (Test-Path $c) { $csc = $c }
    }
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { Write-Host "COULD NOT RUN: MSBuild not found." -ForegroundColor Red; exit 2 }
if (-not $csc) { Write-Host "COULD NOT RUN: the VS2022 Roslyn csc.exe was not found." -ForegroundColor Red; exit 2 }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host "COULD NOT RUN: node is not on PATH, so the scripted language server cannot start." -ForegroundColor Red
    exit 2
}

$binDir = Join-Path $repo 'mcp-server\bin\Debug'
if (-not $StandaloneExe) {
    & $msbuild (Join-Path $repo 'mcp-server\ClarionMcpServer.csproj') /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $binDir 'clarion-mcp-server.exe'))) {
        Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red
        exit 2
    }
}

$work = Join-Path $env:TEMP ("ca-ide-e2e-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
try {
    # The standalone runs from a private copy of its bin (nothing written into the build output).
    $srcBin = if ($StandaloneExe) { Split-Path -Parent $StandaloneExe } else { $binDir }
    Copy-Item (Join-Path $srcBin '*') $bin -Recurse -Force
    $exe = Join-Path $bin 'clarion-mcp-server.exe'

    $driver = Join-Path $work 'IdeLiveTextE2E.exe'
    & $csc /nologo /warn:0 /platform:x86 /out:$driver /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll `
        (Join-Path $PSScriptRoot 'LspDiagnostics.IdeLiveTextE2E.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: compile of the driver failed." -ForegroundColor Red; exit 2 }

    # The size case uses the real 62k-line module when this machine has it, else a synthetic 2.4 MB text.
    $big = 'H:\Dev\aPOSitive\v61PRM002\source\PRM002023.clw'
    $driverArgs = @($exe, (Join-Path $PSScriptRoot 'fixtures\lsp-live-text\fake-lsp.js'), (Join-Path $work 'run'))
    if (Test-Path $big) { $driverArgs += $big }
    New-Item -ItemType Directory -Force (Join-Path $work 'run') | Out-Null
    & $driver @driverArgs | Out-Host
    exit $LASTEXITCODE
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
