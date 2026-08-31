# Run-Tests.ps1 — single entry point for ClarionAssistant's standalone test harnesses.
#
#   .\tests\Run-Tests.ps1                 # everything runnable on this machine
#   .\tests\Run-Tests.ps1 -Probe          # also run the read-only live VS Code probe (diagnostic)
#   .\tests\Run-Tests.ps1 -CSharpOnly     # only the C# harnesses
#   .\tests\Run-Tests.ps1 -NodeOnly       # only the node harnesses
#   .\tests\Run-Tests.ps1 -InstallerOnly  # only the installer script harnesses
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
#   ..\installer\tests\*.ps1  PowerShell harnesses over the INSTALLER scripts. These live outside
#                          this folder because they belong next to what they test, and they run the
#                          real script under the real powershell.exe 5.1 the installer uses. Two
#                          releases have shipped a configure.ps1 that destroyed users' settings.json
#                          (GH #190, GH #200), both from 5.1-only defaults that look correct in a
#                          7.x terminal. Nothing else in the repo exercises that script.
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
    [switch]$NodeOnly,
    [switch]$InstallerOnly
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent $PSScriptRoot          # ...\ClarionAssistant
$RootDir = Split-Path -Parent $RepoDir              # repository root (holds installer\)
$TestDir = $PSScriptRoot
$OutDir  = Join-Path $env:TEMP ("ca-tests-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))

$failures = @()
$ran = 0

function Section($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }

# --------------------------------------------------------------------------- C# harnesses
if (-not $NodeOnly -and -not $InstallerOnly) {

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
if (-not $CSharpOnly -and -not $InstallerOnly) {

    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $node) {
        Write-Host ""
        Write-Host "node not found on PATH — cannot run the Terminal page tests." -ForegroundColor Red
        $failures += "node harnesses (no node.exe)"
    }
    else {
        # Install the dev-only test dependencies if they are not there yet.
        #
        # Terminal\test\package.json has declared jsdom for a long time and is committed — what was
        # missing was anyone ever installing it. So on a fresh clone, or in any git worktree, the one
        # harness that needs it exited 2 and this script reported "dependency missing", which reads as
        # a problem with your machine rather than with the code. vscode-import-ui.test.js was failing
        # 8 assertions from at least v5.8.1 until 2026-08-31 and nobody saw it, because it only ever
        # turned red on a checkout that happened to have node_modules populated.
        #
        # npm install, not npm ci: there is no package-lock.json in that folder. If npm is absent or
        # offline this fails and the harness still exits 2 below, which is the correct outcome — it
        # genuinely could not run.
        $testDir = Join-Path $RepoDir "Terminal\test"
        if ((Test-Path (Join-Path $testDir "package.json")) -and
            -not (Test-Path (Join-Path $testDir "node_modules\jsdom"))) {
            $npm = (Get-Command npm -ErrorAction SilentlyContinue)
            if ($npm) {
                Write-Host ""
                Write-Host "Installing dev-only test dependencies (Terminal\test)..." -ForegroundColor Cyan
                Push-Location $testDir
                try { & npm install --no-audit --no-fund --silent 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray } }
                finally { Pop-Location }
            }
            else {
                Write-Host "npm not found — cannot install the node test dependencies." -ForegroundColor Yellow
            }
        }

        $jsTests = Get-ChildItem $testDir -Filter *.test.js -ErrorAction SilentlyContinue |
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

# --------------------------------------------------------------------------- installer harnesses
if (-not $CSharpOnly -and -not $NodeOnly) {

    $installerTests = Get-ChildItem (Join-Path $RootDir "installer\tests") -Filter *.ps1 -ErrorAction SilentlyContinue |
                      Sort-Object Name
    if (-not $installerTests) {
        Write-Host ""
        Write-Host "no installer harnesses found under installer\tests — expected at least one." -ForegroundColor Red
        $failures += "installer harnesses (none found)"
    }
    foreach ($t in $installerTests) {
        Section $t.Name
        # Run each in its own powershell so a harness cannot leak $ErrorActionPreference, cwd, or a
        # sandboxed $env:USERPROFILE into the next one. These harnesses reassign USERPROFILE/APPDATA
        # while the script under test runs; a leak would point a later test at the real profile.
        & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $t.FullName
        $code = $LASTEXITCODE
        $ran++
        # Exit 2 is the shared "could not run" signal (see the node family above). For these it also
        # covers "the defect cannot reproduce on this machine" — e.g. the system ANSI codepage is
        # UTF-8, so an encoding assertion could not fail and therefore proves nothing. That must not
        # read as green.
        if ($code -eq 2) {
            Write-Host "  SKIPPED — could not run (see message above)" -ForegroundColor Yellow
            $failures += $t.Name + " (could not run)"
        }
        elseif ($code -ne 0) { $failures += $t.Name }
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
