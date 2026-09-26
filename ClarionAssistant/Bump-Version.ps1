# Bump-Version.ps1
# Manually set any part of the ClarionAssistant version stored in Version.props.
#
# Examples:
#   .\Bump-Version.ps1 -Patch 3                  # 5.8.2 -> 5.8.3  (the usual release cut)
#   .\Bump-Version.ps1 -Minor 9 -Patch 0         # 5.8.2 -> 5.9.0
#   .\Bump-Version.ps1 -Major 6 -Minor 0 -Patch 0  # 5.8.2 -> 6.0.0
#   .\Bump-Version.ps1 -Build 12345              # Set the build counter (NOT a release cut)
#
# Major.Minor.Patch is the RELEASED version: it is what the .addin manifest declares and what the
# git tag must equal. Build is the auto-incrementing compile counter and appears only in the
# assembly's file version. See the comment in Version.props.
#
# After bumping, runs Update-Version.ps1 with -NoIncrement so generated files
# (AssemblyVersion.cs, ClarionAssistant.addin) immediately reflect the new value.

param(
    [int]$Major,
    [int]$Minor,
    [int]$Patch,
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
# A MAJOR, MINOR or PATCH change is a release cut, and release notes are written per minor.
# Check that everything landed since the last tag is documented BEFORE writing
# Version.props, so a failed check leaves the tree untouched.
#
# PATCH is gated too, and that is not cosmetic. Major.Minor.Patch IS the released version now --
# it is what ships in the manifest and what the git tag has to equal -- so a patch bump is a
# release cut in every sense that matters. Before, the only way to move the shipped version was
# Major/Minor, so gating those two was the whole surface; leaving Patch ungated would open a hole
# straight through the middle of the gate.
#
# Deliberately still NOT run for -Build / -BumpBuild: the build counter increments on every
# local build, and gating that would fire the check dozens of times a day and train
# everyone to pass -SkipDocsCheck reflexively. A gate people route around is worse
# than no gate. Build no longer appears in the released version at all.
$oldMajor = [int]$pg.VersionMajor
$oldMinor = [int]$pg.VersionMinor
$oldPatch = [int]$pg.VersionPatch
$isReleaseCut =
    ($PSBoundParameters.ContainsKey('Major') -and $Major -ne $oldMajor) -or
    ($PSBoundParameters.ContainsKey('Minor') -and $Minor -ne $oldMinor) -or
    ($PSBoundParameters.ContainsKey('Patch') -and $Patch -ne $oldPatch)

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
if ($PSBoundParameters.ContainsKey('Patch')) { $pg.VersionPatch = "$Patch" }
if ($PSBoundParameters.ContainsKey('Build')) { $pg.VersionBuild = "$Build" }
if ($BumpBuild) { $pg.VersionBuild = "$(([int]$pg.VersionBuild) + 1)" }

$major = [int]$pg.VersionMajor
$minor = [int]$pg.VersionMinor
$ptch  = [int]$pg.VersionPatch
$bld   = [int]$pg.VersionBuild

$pg.FullVersion         = "$major.$minor.$ptch"
$pg.AssemblyFullVersion = "$major.$minor.$ptch.$bld"
$xml.Save($VersionProps)

Write-Host "Released version set to $major.$minor.$ptch (assembly $major.$minor.$ptch.$bld)" -ForegroundColor Green
if ($isReleaseCut) {
    Write-Host "Tag this release v$major.$minor.$ptch - the manifest declares $major.$minor.$ptch and AddinFinder compares the two." -ForegroundColor Cyan
}

# Regenerate AssemblyVersion.cs + .addin so they reflect the new version immediately
& (Join-Path $ScriptDir 'Update-Version.ps1') -NoIncrement
