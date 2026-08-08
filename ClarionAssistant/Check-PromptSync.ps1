# Check-PromptSync.ps1
# Guards the embedded assistant's system prompt against the drift that shipped in 5.7.
#
# THE SETUP. Two files hold the same document:
#   Terminal\clarion-assistant-prompt.md   the BUNDLED prompt - what the installer ships
#   .claude\CLAUDE.md                      what the assistant actually reads at runtime
#
# AssistantChatControl.DeployClaudeMd() copies the first over the second on EVERY terminal
# spin-up ("Always overwrite - the dynamic context from last session needs to be cleared").
# That is correct behaviour and is not what broke.
#
# WHAT BROKE. In this repo the destination is also a TRACKED file, and it is the copy people
# naturally edit - it is the one loaded as project instructions when you work here. So doc
# improvements landed on .claude\CLAUDE.md, the overwrite silently discarded them from disk,
# and the bundled prompt - the artifact users receive - never moved:
#
#   .claude\CLAUDE.md                      last touched 2026-08-05  311 lines
#   Terminal\clarion-assistant-prompt.md   last touched 2026-07-30  240 lines
#
# Six days apart, and among the casualties was the 51-tool audit that 5.7's release notes
# announced as "Registry and documentation are now at parity". True of the repo copy; not
# true of the artifact. Every user's assistant ran without ~50 of its own tools documented,
# and the tools worked fine when called - only the doc hid them. Confirmed live 2026-08-08:
# the deployed prompt in C:\Clarion12 was byte-identical to the Jul 30 one.
#
# WHY A CHECK RATHER THAN A CODE CHANGE. Keep the files identical and the overwrite becomes a
# no-op: nothing is lost, and the working tree stops showing a phantom modification. The only
# thing that has to hold is that they never diverge - which is precisely what nothing was
# checking. Same reasoning as Check-ReleaseDocs.ps1: the failure was silent, so make it loud.
#
# Usage:
#   .\Check-PromptSync.ps1            # exit 0 in sync, 1 drifted
#   .\Check-PromptSync.ps1 -Fix       # copy the runtime doc over the bundled prompt
#
# Line endings are normalised before comparing: git converts on checkout, so a CRLF/LF
# mismatch is an artifact of how a file was written, not real drift.

param(
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$Bundled   = Join-Path $ScriptDir 'Terminal\clarion-assistant-prompt.md'
$Runtime   = Join-Path $ScriptDir '.claude\CLAUDE.md'

function Read-Normalised([string]$Path) {
    $text = [System.IO.File]::ReadAllText($Path)
    return $text.Replace("`r`n", "`n")
}

$missing = $false
foreach ($p in @($Bundled, $Runtime)) {
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p" -ForegroundColor Red; $missing = $true }
}
if ($missing) { exit 2 }

$b = Read-Normalised $Bundled
$r = Read-Normalised $Runtime

if ($b -eq $r) {
    $lines = ($b -split "`n").Count
    Write-Host "PASS - bundled prompt and runtime CLAUDE.md are in sync ($lines lines)." -ForegroundColor Green
    exit 0
}

# Drifted. Report usefully rather than just failing: which tools does the SHIPPED prompt not
# mention? That is the user-visible consequence, and it is what made the 5.7 miss invisible.
Write-Host "DRIFT - the bundled prompt and the runtime CLAUDE.md differ." -ForegroundColor Red
Write-Host ""
Write-Host ("  bundled (ships to users)  {0,5} lines   {1}" -f ($b -split "`n").Count, $Bundled)
Write-Host ("  runtime (what it reads)   {0,5} lines   {1}" -f ($r -split "`n").Count, $Runtime)
Write-Host ""

$runtimeTools = [regex]::Matches($r, '(?m)^-\s+`([a-z0-9_]+)`') | ForEach-Object { $_.Groups[1].Value }
$absent = $runtimeTools | Sort-Object -Unique | Where-Object { $b -notmatch [regex]::Escape($_) }
if ($absent) {
    Write-Host "  Tools documented for the assistant but ABSENT from the shipped prompt:" -ForegroundColor Yellow
    foreach ($t in $absent) { Write-Host "    $t" }
    Write-Host ""
    Write-Host "  Users' assistants will not know these exist, even though the tools work." -ForegroundColor Yellow
    Write-Host ""
}

if ($Fix) {
    # Copy runtime -> bundled: the runtime copy is the one people edit, so it is the newer of
    # the two in every drift seen so far. Preserve the destination's line endings.
    $bundledUsesCrlf = ([System.IO.File]::ReadAllText($Bundled)) -match "`r`n"
    $out = if ($bundledUsesCrlf) { $r.Replace("`n", "`r`n") } else { $r }
    [System.IO.File]::WriteAllText($Bundled, $out)
    Write-Host "FIXED - copied the runtime doc over the bundled prompt. Review and commit it." -ForegroundColor Green
    exit 0
}

Write-Host "Re-run with -Fix to copy the runtime doc over the bundled prompt." -ForegroundColor Cyan
exit 1
