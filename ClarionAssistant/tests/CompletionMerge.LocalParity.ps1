# 1c685f2e item 1: golden parity of SharedLspBridge's merged completion + hover across the
# LocalScopeIndex extraction.
#
# The buffer-local helpers (scope DATA ranges, locals, module data, MAP procedures, routines, GROUP/
# QUEUE fields, the buffer-local hover) moved out of SharedLspBridge into Services\LocalScopeIndex.cs,
# and the late merge now calls that class. This harness proves the merged output did not move: it
# drives the REAL SharedLspBridge.GetCompletion / GetHover (built into clarion-mcp-server.exe) over the
# bundled LspClient and the GH #187 stand-in server (fake-lsp.js) answering an EMPTY list, at fixed
# carets in tests\fixtures\local-scope\two-procs.clw, and compares against golden-master.json -
# generated ONCE at master 2fcb940 before the refactor. The only allowed differences are the ones the
# driver lists (procedure parameters; a local-class method seeing its owning procedure's data).
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\CompletionMerge.LocalParity.ps1
#       add -Generate to rewrite the golden file (ONLY on the master commit it names).
# Exit: 0 pass, 1 fail, 2 could-not-run.
param([switch]$Generate)

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$msbuild = $null
$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    $vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vsRoot) {
        $c = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
        if (Test-Path $c) { $csc = $c }
    }
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { Write-Host "COULD NOT RUN: MSBuild not found." -ForegroundColor Red; exit 2 }
if (-not $csc) { Write-Host "COULD NOT RUN: the VS2022 Roslyn csc.exe was not found." -ForegroundColor Red; exit 2 }

$csproj = Join-Path $repo 'mcp-server\ClarionMcpServer.csproj'
$binDir = Join-Path $repo 'mcp-server\bin\Debug'
& $msbuild $csproj /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $binDir 'clarion-mcp-server.exe'))) {
    Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red
    exit 2
}

$work = Join-Path $env:TEMP ("ca-parity-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
$src  = Join-Path $work 'src'
New-Item -ItemType Directory -Force $bin, $src | Out-Null
try {
    Copy-Item (Join-Path $binDir '*') $bin -Recurse -Force
    # The GH #187 stand-in server, answering an EMPTY completion list: every item is the host's own.
    Copy-Item (Join-Path $PSScriptRoot 'fixtures\completion-dup\fake-lsp.js') $src -Force
    [System.IO.File]::WriteAllText((Join-Path $src 'completion-items.json'), '[]')

    $exe = Join-Path $bin 'CompletionMerge.LocalParity.exe'
    & $csc /nologo /warn:0 /platform:x86 /out:$exe `
        ("/r:" + (Join-Path $bin 'clarion-mcp-server.exe')) `
        /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll `
        (Join-Path $PSScriptRoot 'CompletionMerge.LocalParity.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: compile of the driver failed." -ForegroundColor Red; exit 2 }

    $fixture = Join-Path $PSScriptRoot 'fixtures\local-scope\two-procs.clw'
    $golden  = Join-Path $PSScriptRoot 'fixtures\local-scope\golden-master.json'
    $mode = if ($Generate) { 'generate' } else { 'check' }
    if (-not $Generate -and -not (Test-Path $golden)) { Write-Host "COULD NOT RUN: $golden is missing." -ForegroundColor Red; exit 2 }
    & $exe (Join-Path $src 'fake-lsp.js') $src $fixture $golden $mode | Out-Host
    exit $LASTEXITCODE
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
