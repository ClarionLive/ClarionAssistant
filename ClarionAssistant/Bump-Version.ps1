# Bump-Version.ps1
# Manually set any part of the ClarionAssistant version stored in Version.props.
#
# Examples:
#   .\Bump-Version.ps1 -Major 5                  # Set major to 5 (minor and build unchanged)
#   .\Bump-Version.ps1 -Minor 7                  # Set minor to 7
#   .\Bump-Version.ps1 -Build 12345              # Set build counter
#   .\Bump-Version.ps1 -Major 4 -Minor 7         # Set major and minor together
#   .\Bump-Version.ps1 -Major 5 -Minor 0 -Build 0  # Full reset to 5.0.0
#
# After bumping, runs Update-Version.ps1 with -NoIncrement so generated files
# (AssemblyVersion.cs, ClarionAssistant.addin) immediately reflect the new value.

param(
    [int]$Major,
    [int]$Minor,
    [int]$Build,
    [switch]$BumpBuild   # Convenience: increment build by 1 without compiling
)

$ErrorActionPreference = 'Stop'
$ScriptDir    = $PSScriptRoot
$VersionProps = Join-Path $ScriptDir 'Version.props'

if (-not (Test-Path $VersionProps)) { throw "Version.props not found: $VersionProps" }

[xml]$xml = Get-Content -LiteralPath $VersionProps -Raw
$pg = $xml.Project.PropertyGroup

if ($PSBoundParameters.ContainsKey('Major')) { $pg.VersionMajor = "$Major" }
if ($PSBoundParameters.ContainsKey('Minor')) { $pg.VersionMinor = "$Minor" }
if ($PSBoundParameters.ContainsKey('Build')) { $pg.VersionBuild = "$Build" }
if ($BumpBuild) { $pg.VersionBuild = "$(([int]$pg.VersionBuild) + 1)" }

$major = [int]$pg.VersionMajor
$minor = [int]$pg.VersionMinor
$bld   = [int]$pg.VersionBuild

$pg.FullVersion         = "$major.$minor.$bld"
$pg.AssemblyFullVersion = "$major.$minor.$bld.0"
$xml.Save($VersionProps)

Write-Host "Version set to $major.$minor.$bld" -ForegroundColor Green

# Regenerate AssemblyVersion.cs + .addin so they reflect the new version immediately
& (Join-Path $ScriptDir 'Update-Version.ps1') -NoIncrement
