# Regression guard for ticket 44a1b10c: lsp_diagnostics checks the open editor's text ("buffer if open, else disk",
# John 2026-10-04) and never replaces the editor's synced text with the disk text.
#
# HOW. The real clarion-mcp-server.exe is built (SharedLspBridge, LspClient). LspDiagnostics.LiveText.cs is compiled
# against it and drives SharedLspBridge.GetDiagnosticsForTool, lsp_diagnostics' entry point, with
# SharedLspBridge.LiveTextProvider standing in for the addin's editor lookup. The language server is
# fixtures\lsp-live-text\fake-lsp.js, which logs every text it receives and skips identical content (#359).
#
# MUST BE ABLE TO GO RED: master has no GetDiagnosticsForTool, so the guard was proven by mutation. With
# GetDiagnosticsForTool always taking the disk path (master's behaviour) B, C and D fail, 7 of 13 (A, E, F and G
# are the disk and refusal cases and pass by design). With the hash gate removed (an identical buffer re-sent),
# both C assertions fail: the server skips it (#359) and the call hangs to its budget.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\LspDiagnostics.LiveTextTest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run (the suite counts 2 as a failure, never as green).

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

$csproj = Join-Path $repo 'mcp-server\ClarionMcpServer.csproj'
$binDir = Join-Path $repo 'mcp-server\bin\Debug'
& $msbuild $csproj /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $binDir 'clarion-mcp-server.exe'))) {
    Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red
    exit 2
}

$work = Join-Path $env:TEMP ("ca-livetext-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
$src  = Join-Path $work 'src'
New-Item -ItemType Directory -Force $bin, $src | Out-Null
try {
    Copy-Item (Join-Path $binDir '*') $bin -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot 'fixtures\lsp-live-text\fake-lsp.js') $src -Force

    $exe = Join-Path $bin 'LspDiagnostics.LiveText.exe'
    & $csc /nologo /warn:0 /platform:x86 /out:$exe `
        ("/r:" + (Join-Path $bin 'clarion-mcp-server.exe')) `
        /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll `
        (Join-Path $PSScriptRoot 'LspDiagnostics.LiveText.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: compile of the driver failed." -ForegroundColor Red; exit 2 }

    $env:FAKE_LOG = Join-Path $work 'received.jsonl'   # inherited by the scripted server
    & $exe (Join-Path $src 'fake-lsp.js') $src | Out-Host
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:\FAKE_LOG -ErrorAction SilentlyContinue
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
