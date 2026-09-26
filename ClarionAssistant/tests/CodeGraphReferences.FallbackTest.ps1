# Builds and runs CodeGraphReferences.Fallback.cs against the REAL CodeGraphProvider.cs
# (ticket 77aceec5 item 5: the CodeGraph fallback behind lsp_references).
#
# A .ps1 wrapper rather than an entry in Run-Tests.ps1's C# list because the provider needs the
# vendored x86 System.Data.SQLite: the harness must be compiled /platform:x86 and run with
# System.Data.SQLite.dll beside it and SQLite.Interop.dll in an x86\ subfolder, which that list's
# one-exe-in-a-shared-folder model does not do.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\CodeGraphReferences.FallbackTest.ps1
# Exit: 0 pass, 1 fail, 2 could-not-run.

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lib  = Join-Path $repo 'lib\sqlite-fts5'

$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vsRoot) {
        $c = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
        if (Test-Path $c) { $csc = $c }
    }
}
if (-not $csc) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host "COULD NOT RUN: csc.exe not found." -ForegroundColor Red; exit 2 }
foreach ($f in @('System.Data.SQLite.dll', 'SQLite.Interop.dll')) {
    if (-not (Test-Path (Join-Path $lib $f))) { Write-Host "COULD NOT RUN: $f missing from $lib" -ForegroundColor Red; exit 2 }
}

$out = Join-Path $env:TEMP ("ca-cgref-bin-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force (Join-Path $out 'x86') | Out-Null
try {
    Copy-Item (Join-Path $lib 'System.Data.SQLite.dll') $out
    Copy-Item (Join-Path $lib 'SQLite.Interop.dll') (Join-Path $out 'x86')
    $exe = Join-Path $out 'CodeGraphReferences.Fallback.exe'
    & $csc /nologo /warn:0 /platform:x86 /out:$exe /r:System.dll /r:System.Data.dll `
        ("/r:" + (Join-Path $lib 'System.Data.SQLite.dll')) `
        (Join-Path $repo 'tests\CodeGraphReferences.Fallback.cs') `
        (Join-Path $repo 'CodeGraph\Graph\CodeGraphProvider.cs') | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "COULD NOT RUN: compile failed." -ForegroundColor Red; exit 2 }
    & $exe
    exit $LASTEXITCODE
}
finally {
    try { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
