# End-to-end guard for ticket b8f9431c: the STANDALONE clarion-mcp-server.exe (what MultiTerminal, VS Code and other
# clients of the clarion-assistant plugin run) lists every IdeOnly tool and forwards each call to a running Clarion IDE
# that has the SAME solution open, returning the IDE's result verbatim; with no such IDE, an IDE on another solution,
# several IDEs or an older add-in it explains itself. See IdeProxy.StandaloneE2E.cs for the cases.
#
# WHY END TO END. The forwarding spans two processes, the endpoint/solution record trust rules and a real HTTP hop.
# This drives the real standalone exe over stdio against a stand-in IDE found through real records in a temp
# CA_IDE_RECORD_DIR, the way the live plugin finds a real Clarion.
#
# MUST BE ABLE TO GO RED: against 385a3da's standalone (pass -StandaloneExe <its clarion-mcp-server.exe>) it fails:
# tools/list has 61 tools and no IdeOnly tool, and every forwarded call is answered "Unknown tool".
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\IdeProxy.StandaloneE2ETest.ps1 [-StandaloneExe <exe>]
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

$binDir = Join-Path $repo 'mcp-server\bin\Debug'
if (-not $StandaloneExe) {
    & $msbuild (Join-Path $repo 'mcp-server\ClarionMcpServer.csproj') /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $binDir 'clarion-mcp-server.exe'))) {
        Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red
        exit 2
    }
}

$work = Join-Path $env:TEMP ("ca-ideproxy-e2e-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$code = 2
try {
    # The standalone runs from a private copy of its bin (nothing written into the build output).
    $srcBin = if ($StandaloneExe) { Split-Path -Parent $StandaloneExe } else { $binDir }
    Copy-Item (Join-Path $srcBin '*') $bin -Recurse -Force
    $exe = Join-Path $bin 'clarion-mcp-server.exe'

    $driver = Join-Path $work 'IdeProxyE2E.exe'
    & $csc /nologo /warn:0 /platform:x86 /out:$driver /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll `
        (Join-Path $PSScriptRoot 'IdeProxy.StandaloneE2E.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: compile of the driver failed." -ForegroundColor Red; exit 2 }

    $run = Join-Path $work 'run'
    New-Item -ItemType Directory -Force $run | Out-Null
    & $driver $exe $run | Out-Host
    $code = $LASTEXITCODE
}
finally {
    # The driver removes its junction itself; remove it here too before the recursive delete, in case it was killed.
    $alias = Join-Path $work 'run\alias'
    if (Test-Path $alias) { try { [System.IO.Directory]::Delete($alias, $false) } catch { } }
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
exit $code
