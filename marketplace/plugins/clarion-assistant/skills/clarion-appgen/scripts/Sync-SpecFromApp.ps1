<#
.SYNOPSIS
    Capture a built .app back into its spec folder as a TXA snapshot, so IDE-side edits
    (embeds above all) stop living only inside the .app.

.DESCRIPTION
    New-ClarionApp.ps1 runs one way: authored text -> .app -> .exe. The moment someone opens the
    generated .app in the IDE and adds an embed, that work exists ONLY in the .app - the authored
    .txa has no record of it, and the next build discards it.

    This script closes that loop:

        .app --/ax--> <App>.export.txa   (written into the SPEC folder, beside the authored .txa)

    VERIFIED (2026-08-07): the export is a COMPLETE serialization. An app exported this way,
    re-imported with /ai into an empty directory and rebuilt, produced generated source matching
    the original on every embed, per-instance template prompt, and range limit.
    See the clarion-appgen skill's SKILL.md ("Two TXA files, two jobs") and
    references/txa-grammar.md ("Embeds round-trip").

    TWO FILES, TWO JOBS - this script never overwrites the authored .txa:

      <App>.txa          authored bootstrap. Small, readable, the artifact that proves an app can
                         be written from nothing. Frozen once the .app exists.
      <App>.export.txa   machine snapshot of the live .app. ~20x larger (every prompt written out
                         at its default). Not for hand-authoring; for capture and for diffing.

    THE LOCK. ClarionCL cannot open an .app that the IDE holds - it fails with
    'status 32' after 50 retries. There is no way around that from PowerShell: the only tool that
    can export a loaded app is the IDE itself (Clarion Assistant's export_txa MCP tool, which
    routes through the IDE object model). So: close the application in the IDE first, or ask the
    assistant to export it. This script detects the lock and says so rather than burning 50
    retries and reporting something vague.

.PARAMETER SpecPath
    The spec folder - the same one passed to New-ClarionApp.ps1. Must hold exactly one authored
    .txa, which names the app.

.PARAMETER BuildPath
    Where the built .app lives. Default: <SpecPath>\.build

.PARAMETER AppName
    App base name. Default: base name of the single authored .txa in SpecPath.

.PARAMETER ClarionRoot
    Clarion installation. Default: C:\Clarion12

.PARAMETER PassThru
    Emit the result object even when nothing changed.

.OUTPUTS
    [pscustomobject] Ok, App, Export, Changed, Bytes, Message

.EXAMPLE
    .\Sync-SpecFromApp.ps1 -SpecPath .\specs\orders
    # -> specs\orders\ORDERS.export.txa, and reports whether it changed since last capture

.NOTES
    Companion to New-ClarionApp.ps1, whose app-newer-than-spec guard points here.

    Known cosmetic difference on re-import: SELF.AddItem(Toolbar) generates two lines later than
    in an authored build (addition ordering). Functionally equivalent, but it means the first
    export after adopting this workflow shows a diff in every window procedure.

    THE Changed FLAG IS NOT PROOF THE APP CHANGED. The IDE loads ABC class metadata
    lazily, and an app exported through the IDE before that load is missing %ClassLines content
    that a later export includes - same app, different text, ~1.2 KB apart on a six-procedure app.
    This script uses ClarionCL /ax, a fresh process each run, so it should be self-consistent;
    whether its output matches an IDE-warm export is UNTESTED. Two rules follow:
      - Do not mix capture routes. Compare /ax against /ax, or export_txa against export_txa.
      - If capturing through the IDE instead (the only option while the app is open), warm the
        cache first - warmup_abc, or open any procedure in the embeditor - or the snapshot is
        incomplete in a way that varies run to run.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$SpecPath,
    [string]$BuildPath,
    [string]$AppName,
    [string]$ClarionRoot = 'C:\Clarion12',
    [int]$TimeoutSeconds = 180,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'

$clarionCL = Join-Path $ClarionRoot 'bin\ClarionCL.exe'
if (-not (Test-Path -LiteralPath $clarionCL)) { throw "ClarionCL.exe not found: $clarionCL" }

$wrapper = Join-Path $PSScriptRoot 'Invoke-ClarionCL.ps1'
if (-not (Test-Path -LiteralPath $wrapper)) { throw "Invoke-ClarionCL.ps1 not found next to this script: $wrapper" }

$SpecPath = (Resolve-Path -LiteralPath $SpecPath).Path
$authored = @(Get-ChildItem -LiteralPath $SpecPath -Filter '*.txa' -File |
              Where-Object { $_.Name -notlike '*.export.txa' })
if ($authored.Count -ne 1) {
    throw "SpecPath must contain exactly one authored .txa (found $($authored.Count)): $SpecPath"
}
if (-not $AppName)   { $AppName = $authored[0].BaseName }
if (-not $BuildPath) { $BuildPath = Join-Path $SpecPath '.build' }

$appFile = Join-Path $BuildPath "$AppName.app"
if (-not (Test-Path -LiteralPath $appFile)) {
    throw "No built app to capture: $appFile (run New-ClarionApp.ps1 first)"
}

$export = Join-Path $SpecPath "$AppName.export.txa"
$prior  = if (Test-Path -LiteralPath $export) { Get-Content -LiteralPath $export -Raw } else { $null }

# Export to a temp file first: a failed /ax must not leave a truncated snapshot in the spec
# folder, because a truncated snapshot is worse than none - it looks like a successful capture.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "$AppName-$PID.export.txa"
Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue

Write-Host "Sync-SpecFromApp: $AppName" -ForegroundColor Cyan
Write-Host "  app    $appFile"
Write-Host "  export $export"

# Route /ax through the wrapper, never raw: /ax opens the .app, and an .app can raise a modal
# (solution association, dictionary upgrade) that blocks forever with no output. A bare
# invocation here would hang the capture in a package whose first rule is "always use a timeout".
$r    = & $wrapper -Arguments @('/au','/ax',$appFile,$tmp) -WorkingDirectory $BuildPath `
                   -TimeoutSeconds $TimeoutSeconds -DismissDialogs -ClarionCLPath $clarionCL
$out  = "$($r.StdOut)`n$($r.StdErr)".Trim()
$code = $r.ExitCode

if ($r.TimedOut) {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    $d = if ($r.Dialogs.Count) {
             ($r.Dialogs | ForEach-Object { "dialog '$($_.Title)': $($_.Text)" }) -join ' / '
         } else { 'no dialog seen' }
    throw "ClarionCL /ax timed out after ${TimeoutSeconds}s ($d)`n$out"
}

if ($code -ne 0 -or -not (Test-Path -LiteralPath $tmp)) {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    if ($out -match 'status 32' -or $out -match 'Could not gain access') {
        throw ("The .app is held open by another process - almost always the Clarion IDE. " +
               "ClarionCL cannot export a loaded app. Close the application in the IDE " +
               "(the solution can stay open) and re-run, or have Clarion Assistant export it " +
               "via its export_txa tool, which goes through the IDE and works on a loaded app." +
               "`n`n$out")
    }
    throw "ClarionCL /ax failed (exit $code):`n$out"
}

$new   = Get-Content -LiteralPath $tmp -Raw
$bytes = (Get-Item -LiteralPath $tmp).Length

# A zero-length or absurdly small export means /ax reported success but wrote nothing usable.
# Accepted is not complete - check the artifact, not the exit code.
if ($bytes -lt 1024) {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    throw "Export succeeded but produced only $bytes bytes - refusing to write it. Check the app."
}

$changed = ($null -eq $prior) -or ($new -ne $prior)
Move-Item -LiteralPath $tmp -Destination $export -Force

if ($changed) {
    $verb = if ($null -eq $prior) { 'created' } else { 'UPDATED - the app changed since last capture' }
    Write-Host "  $verb ($bytes bytes)" -ForegroundColor Yellow
} else {
    Write-Host "  unchanged ($bytes bytes) - app matches last capture" -ForegroundColor Green
}

$result = [pscustomobject]@{
    Ok      = $true
    App     = $AppName
    Export  = $export
    Changed = $changed
    Bytes   = $bytes
    Message = if ($changed) { 'export refreshed' } else { 'no change' }
}
if ($PassThru -or $changed) { $result }
