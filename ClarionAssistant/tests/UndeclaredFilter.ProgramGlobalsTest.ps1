# Regression guard for ticket e2f87efb: CA's 'not declared' filter must clear the global FILE labels of a PROGRAM
# whose CLASS method prototypes sit at column 1.
#
# THE DEFECT. On PRM002022.clw the language server reports ADDONS, PINVDET, VENINV and other FILE labels from
# PRM002.clw's global data as not declared (upstream bug B). SharedLspBridge.ResolveNamesFromProgramGlobals
# should clear them, but it ended the PROGRAM's declaration range at ClarionParser.FindMainTailStart, which
# also stops at the first column-1 "X PROCEDURE" line: PRM002.clw line 93, a CLASS method prototype.
#
# HOW. The real clarion-mcp-server.exe is built. UndeclaredFilter.ProgramGlobals.cs is compiled against it and
# asks SharedLspBridge.GetDiagnostics (the lsp_diagnostics path) for fixtures\lsp-undeclared-program-globals\
# bigmodule.clw. The language server is fixtures\lsp-undeclared-filter\fake-lsp.js, publishing the names on
# the module's "! fake-lsp-undeclared:" line.
#
# MUST BE ABLE TO GO RED: on master ADDONS and PINVDET stay flagged (2 of 6 assertions fail). FxTailLocal and
# FxNoSuchThing are the negative controls and pass on both.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\UndeclaredFilter.ProgramGlobalsTest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run (the suite counts 2 as a failure, never as green).

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# ---------------------------------------------------------------- build the code under test
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

$csproj = Join-Path $repo 'mcp-server\ClarionMcpServer.csproj'
$binDir = Join-Path $repo 'mcp-server\bin\Debug'
& $msbuild $csproj /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $binDir 'clarion-mcp-server.exe'))) {
    Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red
    exit 2
}

# The driver runs from a private copy of the server's bin, so nothing is written into the build output.
$work = Join-Path $env:TEMP ("ca-undeclglobals-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
$src  = Join-Path $work 'src'
New-Item -ItemType Directory -Force $bin, $src | Out-Null
try {
    Copy-Item (Join-Path $binDir '*') $bin -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-undeclared-program-globals\*') $src -Force
    $fake = Join-Path $work 'fake-lsp.js'
    Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-undeclared-filter\fake-lsp.js') $fake -Force

    $exe = Join-Path $bin 'UndeclaredFilter.ProgramGlobals.exe'
    & $csc /nologo /warn:0 /platform:x86 /out:$exe `
        ("/r:" + (Join-Path $bin 'clarion-mcp-server.exe')) `
        /r:System.dll /r:System.Core.dll `
        (Join-Path $PSScriptRoot 'UndeclaredFilter.ProgramGlobals.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: compile of the driver failed." -ForegroundColor Red; exit 2 }

    & $exe $fake $src | Out-Host
    exit $LASTEXITCODE
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
