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
    [switch]$BumpBuild,      # Convenience: increment build by 1 without compiling
    [switch]$SkipDocsCheck   # Cut the version even if release docs have drifted
)

$ErrorActionPreference = 'Stop'
$ScriptDir    = $PSScriptRoot
$VersionProps = Join-Path $ScriptDir 'Version.props'

if (-not (Test-Path $VersionProps)) { throw "Version.props not found: $VersionProps" }

[xml]$xml = Get-Content -LiteralPath $VersionProps -Raw
$pg = $xml.Project.PropertyGroup

# --- release-docs gate -------------------------------------------------------
# A MAJOR or MINOR change is a release cut, and release notes are written per minor.
# Check that everything landed since the last tag is documented BEFORE writing
# Version.props, so a failed check leaves the tree untouched.
#
# Deliberately NOT run for -Build / -BumpBuild: the build counter increments on every
# local build, and gating that would fire the check dozens of times a day and train
# everyone to pass -SkipDocsCheck reflexively. A gate people route around is worse
# than no gate.
$oldMajor = [int]$pg.VersionMajor
$oldMinor = [int]$pg.VersionMinor
$isReleaseCut =
    ($PSBoundParameters.ContainsKey('Major') -and $Major -ne $oldMajor) -or
    ($PSBoundParameters.ContainsKey('Minor') -and $Minor -ne $oldMinor)

if ($isReleaseCut -and -not $SkipDocsCheck) {
    $checker = Join-Path $ScriptDir 'Check-ReleaseDocs.ps1'
    if (Test-Path $checker) {
        Write-Host "Release cut detected - checking release docs..." -ForegroundColor Cyan
        & $checker
        if ($LASTEXITCODE -ne 0) {
            throw "Release docs have drifted (see above). Fix the notes, or re-run with -SkipDocsCheck to override."
        }
    } else {
        Write-Warning "Check-ReleaseDocs.ps1 not found next to this script - skipping the release-docs gate."
    }
}
# -----------------------------------------------------------------------------

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
