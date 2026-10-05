# Regression guard for ticket 2abfbba2: a false "'X' is not declared in this file." that CA's filter clears
# must not come back through the cached-diagnostics read.
#
# THE DEFECT. On PRM002004.clw the language server (v1.0.8, upstream bug B) reports GlobalRequest, an .app
# global, as not declared. SharedLspBridge.DropUndeclaredWeCanResolve clears it, and every other entry, so
# the waited answer was empty. ModernEmbeditorDiagnostics' settle loop then read
# SharedLspBridge.GetCachedDiagnostics, which returned the RAW cache, and the CA Editor painted the squiggle
# anyway (monaco-spike.log: waitEnd=complete+settle:republish@2 lspEntries=3 markers=3). The status pill
# counted the same raw entries.
#
# HOW. The real clarion-mcp-server.exe is built (SharedLspBridge, LspClient, the filter, from their one
# home). UndeclaredFilter.CachedRead.cs is compiled against it together with the real, unmodified
# Services\ModernEmbeditorDiagnostics.cs, and drives ComputeAsync the way the CA Editor overlay does,
# plus the pill's accessor. The language server is fixtures\lsp-undeclared-filter\fake-lsp.js, which
# publishes only 'not declared' warnings; prog.clw declares GlobRes as a global.
#
# MUST BE ABLE TO GO RED: on master (raw GetCachedDiagnostics) onlyglobal.clw gives 1 marker and 1 pill
# entry. mixed.clw is the negative control (NoSuchThing must still show) and passes on both.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\UndeclaredFilter.CachedReadTest.ps1
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
$work = Join-Path $env:TEMP ("ca-undeclfilter-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
$src  = Join-Path $work 'src'
New-Item -ItemType Directory -Force $bin, $src | Out-Null
try {
    Copy-Item (Join-Path $binDir '*') $bin -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-undeclared-filter\*') $src -Force

    $exe = Join-Path $bin 'UndeclaredFilter.CachedRead.exe'
    # ModernEmbeditorDiagnostics.cs is not in the server build; it is compiled here, unmodified, with
    # ClarionAppDataReader (its routine parser) and that reader's IDE stub. CS0436: the partial
    # ClarionAppDataReader compiled here wins over the server exe's copy of its Model half.
    & $csc /nologo /warn:0 /nowarn:0436 /platform:x86 /out:$exe `
        ("/r:" + (Join-Path $bin 'clarion-mcp-server.exe')) `
        /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Data.dll `
        (Join-Path $PSScriptRoot 'UndeclaredFilter.CachedRead.cs') `
        (Join-Path $repo 'Services\ModernEmbeditorDiagnostics.cs') `
        (Join-Path $repo 'Services\ClarionAppDataReader.cs') `
        (Join-Path $repo 'Services\ClarionAppDataReader.Model.cs') `
        (Join-Path $PSScriptRoot 'ClarionAppDataReader.StructureScan.Stubs.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: compile of the driver failed." -ForegroundColor Red; exit 2 }

    & $exe (Join-Path $src 'fake-lsp.js') $src | Out-Host
    exit $LASTEXITCODE
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
