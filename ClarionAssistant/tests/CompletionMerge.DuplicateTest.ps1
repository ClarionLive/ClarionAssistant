# Regression guard for GH #187 (a): member completion listing the same method twice.
#
# THE DEFECT. SharedLspBridge.GetCompletion passed the language server's own item list straight
# through. Every host merge after it (bare-prefix, qualified field, member access, colon qualifier)
# de-duplicates what IT adds against that list - but nothing ever de-duplicated the list against
# itself. So a member the server sent twice was shown twice, with the server's own "Name(params)"
# label on both rows: exactly the reporter's screenshot (_ColorFromCSL, _ColorFromHex, _ColorToHex,
# _EqualsUnicode each doubled; the _DataEnd field, which has no second declaration, listed once).
#
# WHY A STAND-IN SERVER. The item list IS the fixture here: the thing under test is what the host
# does with an answer that lists one member twice, not how a server comes to send one, so a stub
# cannot decide the outcome. Everything on the host side is real - LspClient over stdio, and
# SharedLspBridge.GetCompletion with all of its merges, compiled from their one home into the
# standalone clarion-mcp-server.exe this script builds - plus a synthetic .codegraph.db for the
# member-access merge, so "the same method from both sources" is exercised too.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\CompletionMerge.DuplicateTest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run (needs MSBuild, csc, and node.exe where LspClient looks for
#       it; the suite counts 2 as a failure, never as green).

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# ---------------------------------------------------------------- build the code under test
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

# The driver runs from a private copy of the server's bin (x86 SQLite interop, Contracts dll beside
# it), so nothing is written into the build output.
$work = Join-Path $env:TEMP ("ca-cmpdup-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
$bin  = Join-Path $work 'bin'
$src  = Join-Path $work 'src'
New-Item -ItemType Directory -Force $bin, $src | Out-Null
try {
    Copy-Item (Join-Path $binDir '*') $bin -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot 'fixtures\completion-dup\*') $src -Force

    $exe = Join-Path $bin 'CompletionMerge.Duplicates.exe'
    & $csc /nologo /warn:0 /platform:x86 /out:$exe `
        ("/r:" + (Join-Path $bin 'clarion-mcp-server.exe')) `
        ("/r:" + (Join-Path $bin 'System.Data.SQLite.dll')) `
        /r:System.dll /r:System.Core.dll /r:System.Data.dll `
        (Join-Path $PSScriptRoot 'CompletionMerge.Duplicates.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: compile of the driver failed." -ForegroundColor Red; exit 2 }

    & $exe (Join-Path $src 'fake-lsp.js') $src | Out-Host
    exit $LASTEXITCODE
}
finally {
    try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
