# Run-Tests.ps1 — single entry point for ClarionAssistant's standalone test harnesses.
#
#   .\tests\Run-Tests.ps1              # everything runnable on this machine
#   .\tests\Run-Tests.ps1 -Probe       # also run the read-only live VS Code probe (diagnostic)
#   .\tests\Run-Tests.ps1 -CSharpOnly  # skip the node tests
#   .\tests\Run-Tests.ps1 -NodeOnly    # skip the C# harnesses
#
# There are two families here and they are deliberately different things:
#
#   tests\*.cs             standalone csc harnesses over SERVICE code that has no IDE coupling.
#                          They compile the real .cs file straight out of the tree — no mocks of the
#                          thing under test — which is only possible while those services stay free of
#                          IDE references. Run outside Clarion entirely.
#
#   Terminal\test\*.test.js  node harnesses over the WebView2 pages. Mostly zero-dependency; the one
#                          exception (vscode-import-ui.test.js) needs jsdom and says so.
#
# NEITHER family is wired into the MSBuild build. That is intentional — these harnesses exist to be
# run by a developer who just changed something, and a test that only runs in CI would not have caught
# the bugs these were written for. Run this before you deploy.
#
# EXIT CODE: non-zero if any harness fails OR could not run. A test that could not run is NOT a pass,
# and this script will not let a missing dependency read as green.

param(
    [switch]$Probe,        # also run the live VS Code probe (reads the developer's own settings.json)
    [switch]$CSharpOnly,
    [switch]$NodeOnly
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent $PSScriptRoot          # ...\ClarionAssistant
$TestDir = $PSScriptRoot
$OutDir  = Join-Path $env:TEMP ("ca-tests-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))

$failures = @()
$ran = 0

function Section($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }

# --------------------------------------------------------------------------- C# harnesses
if (-not $NodeOnly) {

    # Resolve csc. The .NET Framework compiler is enough — these harnesses target the same
    # net48 surface the addin does (System.Web.Extensions for JavaScriptSerializer).
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }

    if (-not (Test-Path $csc)) {
        Write-Host "csc.exe not found — cannot run the C# harnesses." -ForegroundColor Red
        $failures += "C# harnesses (no csc.exe)"
    }
    else {
        New-Item -ItemType Directory -Force $OutDir | Out-Null

        # Each harness pairs with the service file(s) it exercises. Listing the sources explicitly
        # (rather than globbing) keeps it obvious WHICH production code each harness actually covers.
        $harnesses = @(
            @{ Name = "VsCodeSettingsImporter.SmokeTest"
               Sources = @("tests\VsCodeSettingsImporter.SmokeTest.cs", "Services\VsCodeSettingsImporter.cs")
               Refs = @("System.dll", "System.Web.Extensions.dll") }
            @{ Name = "VsCodeSettingsImporter.PayloadCheck"
               Sources = @("tests\VsCodeSettingsImporter.PayloadCheck.cs", "Services\VsCodeSettingsImporter.cs")
               Refs = @("System.dll", "System.Web.Extensions.dll") }
        )
        if ($Probe) {
            $harnesses += @{ Name = "VsCodeSettingsImporter.LiveProbe"
                             Sources = @("tests\VsCodeSettingsImporter.LiveProbe.cs", "Services\VsCodeSettingsImporter.cs")
                             Refs = @("System.dll", "System.Web.Extensions.dll") }
        }

        foreach ($h in $harnesses) {
            Section $h.Name
            $exe  = Join-Path $OutDir ($h.Name + ".exe")
            $srcs = $h.Sources | ForEach-Object { Join-Path $RepoDir $_ }

            $missing = $srcs | Where-Object { -not (Test-Path $_) }
            if ($missing) {
                Write-Host "  source(s) missing: $($missing -join ', ')" -ForegroundColor Red
                $failures += $h.Name + " (missing source)"
                continue
            }

            $refArgs = $h.Refs | ForEach-Object { "/r:$_" }
            & $csc /nologo /warn:0 /out:$exe $refArgs $srcs 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
            if ($LASTEXITCODE -ne 0) {
                Write-Host "  COMPILE FAILED" -ForegroundColor Red
                $failures += $h.Name + " (compile failed)"
                continue
            }

            & $exe
            $ran++
            if ($LASTEXITCODE -ne 0) { $failures += $h.Name }
        }

        try { Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# --------------------------------------------------------------------------- node harnesses
if (-not $CSharpOnly) {

    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $node) {
        Write-Host ""
        Write-Host "node not found on PATH — cannot run the Terminal page tests." -ForegroundColor Red
        $failures += "node harnesses (no node.exe)"
    }
    else {
        $jsTests = Get-ChildItem (Join-Path $RepoDir "Terminal\test") -Filter *.test.js -ErrorAction SilentlyContinue |
                   Sort-Object Name
        foreach ($t in $jsTests) {
            Section $t.Name
            & $node $t.FullName
            $code = $LASTEXITCODE
            $ran++
            # Exit 2 is the agreed "could not run — missing dev dependency" signal (see
            # vscode-import-ui.test.js). Report it distinctly: it is neither a pass nor a real failure,
            # but it must still make the overall run non-zero so it cannot be mistaken for green.
            if ($code -eq 2) {
                Write-Host "  SKIPPED — dependency missing (see message above)" -ForegroundColor Yellow
                $failures += $t.Name + " (dependency missing)"
            }
            elseif ($code -ne 0) { $failures += $t.Name }
        }
    }
}

# --------------------------------------------------------------------------- summary
Write-Host ""
Write-Host ("=" * 60)
if ($failures.Count -eq 0) {
    Write-Host "ALL HARNESSES PASSED ($ran run)" -ForegroundColor Green
    exit 0
}
Write-Host "$($failures.Count) harness(es) failed or could not run, of ${ran}:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
