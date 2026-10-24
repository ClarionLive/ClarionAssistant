# Mirror the repo marketplace (source of truth) out to the live Claude Code
# profile location that the runtime reads from. Run this after editing skills in
# the repo so your local Claude Code picks them up. See marketplace/README.md.
#
# Usage:  pwsh installer\sync-marketplace-to-profile.ps1 [-WhatIf]
#
# GH #214: when setup was told to keep the marketplace in a SHARED folder (registry
# HKCU\Software\ClarionAssistant\PluginMarketplacePath), that folder is the default -Dest, because it is
# what the add-in and every registered profile read. Otherwise the default profile's marketplace folder,
# as before. Pass -Dest to override either.

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Repo marketplace folder (source). Defaults to ..\marketplace relative to this script.
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'marketplace'),
    # Live marketplace folder (destination = what Claude Code reads). Default: see the header.
    [string]$Dest = '',
    # Test hook: the registry key holding PluginMarketplacePath.
    [string]$RegistryKey = 'HKCU:\Software\ClarionAssistant'
)

$ErrorActionPreference = 'Stop'

if (-not $Dest) {
    $shared = $null
    try { $shared = (Get-ItemProperty -Path $RegistryKey -Name 'PluginMarketplacePath' -ErrorAction Stop).PluginMarketplacePath } catch { }
    if ($shared) {
        $Dest = [Environment]::ExpandEnvironmentVariables($shared)
        Write-Host "Using the shared plugin location from the registry." -ForegroundColor DarkGray
    } else {
        $Dest = Join-Path $env:USERPROFILE '.claude\plugins\marketplaces\clarionassistant-marketplace'
    }
}

if (-not (Test-Path (Join-Path $Source '.claude-plugin\marketplace.json'))) {
    throw "Source does not look like the marketplace folder: $Source"
}

Write-Host "Syncing marketplace:" -ForegroundColor Cyan
Write-Host "  from (repo)    $Source"
Write-Host "  to   (profile) $Dest"

if ($PSCmdlet.ShouldProcess($Dest, "Mirror from $Source")) {
    # /MIR mirrors (adds, updates, and prunes profile-only files so it matches the repo).
    # /XD .git guards against ever copying a VCS dir if one exists under Source.
    & robocopy $Source $Dest /MIR /XD '.git' /NFL /NDL /NJH /NJS /NP | Out-Null
    # robocopy exit codes 0-7 are success (bit 0=copied, 1=extra, 2=mismatch, 3=changed); 8+ is a real error.
    $rc = $LASTEXITCODE
    if ($rc -ge 8) { throw "robocopy failed with exit code $rc" }
    Write-Host "Done (robocopy exit $rc = success)." -ForegroundColor Green
    # Don't let robocopy's non-zero success codes propagate as a failure to the caller.
    exit 0
}
