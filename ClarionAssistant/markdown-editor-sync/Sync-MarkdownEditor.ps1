<#
.SYNOPSIS
    Sync the bundled Markdown Editor addin to a pinned upstream release.

.DESCRIPTION
    Downloads a msarson/ClarionMarkdownEditor release ZIP, verifies it, extracts it to
    .markdown-build\<tag>, stages the upstream MIT LICENSE beside the payload, and records what
    was actually resolved back into markdown-snapshot.json.

    This is the Markdown Editor counterpart to lsp-server-sync\Sync-LspServer.ps1, with one
    important simplification: upstream publishes a PREBUILT release zip, so there is no clone
    and no compile. It needs only network access -- no git, no npm, no local clone of anything.
    That means it is self-contained on any machine, which is the property Sync-LspServer.ps1's
    -Pure mode had to be repaired to get (PR #186).

    WHY THE IDENTITY VERSION MATTERS. The upstream DLL's FileVersion resource is not bumped
    between releases: v1.2.0 reports 1.0.2.0, byte-identical to v1.0.2. Any freshness decision
    based on file version -- including Inno Setup's built-in "only replace if newer" -- is
    therefore inert for this addin and silently cannot tell one release from another. The
    <Identity version="..."/> attribute in ClarionMarkdownEditor.addin IS maintained upstream,
    so that is what we parse and record, and that is what the installer compares against a
    user's existing install.

.PARAMETER Tag
    Release tag to sync to (e.g. v1.2.0). Defaults to targetPin.tag in markdown-snapshot.json.

.PARAMETER Force
    Re-download and re-extract even when .markdown-build\<tag> already looks complete.

.EXAMPLE
    .\Sync-MarkdownEditor.ps1
    .\Sync-MarkdownEditor.ps1 -Tag v1.3.0

.NOTES
    If you ever call this from another script's FUNCTION, pipe it: `& $sync | Out-Host`.
    A PowerShell function returns everything written to its output stream, so an uncaptured
    call makes the caller's return value an ARRAY of this script's log lines with the real
    value merely last -- the failure that shipped a release with no language server (0cd0b20c).
#>
[CmdletBinding()]
param(
    [string] $Tag,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# TLS 1.2 for PS 5.1, whose default SecurityProtocol predates GitHub's minimum.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$SyncDir      = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir   = Split-Path -Parent $SyncDir
$ManifestPath = Join-Path $SyncDir 'markdown-snapshot.json'
$BuildRoot    = Join-Path $ProjectDir '.markdown-build'

function Write-Step($m) { Write-Host "  $m" -ForegroundColor Cyan }
function Write-Ok  ($m) { Write-Host "  OK    $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "  WARN  $m" -ForegroundColor Yellow }
function Fail      ($m, $code) { Write-Host "  FAIL  $m" -ForegroundColor Red; exit $code }

# ---- Manifest I/O -----------------------------------------------------------------------------
# Read/write as UTF-8 WITHOUT a BOM explicitly. Get-Content -Raw decodes using the system ANSI
# codepage on Windows PowerShell 5.1, and Set-Content -Encoding UTF8 writes a BOM, so a naive
# round-trip corrupts non-ASCII text and prepends a BOM. (Same defect fixed upstream in
# Sync-LspServer.ps1 by PR #186 -- do not reintroduce it here.)
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-Manifest {
    if (-not (Test-Path -LiteralPath $ManifestPath)) { Fail "manifest not found: $ManifestPath" 2 }
    $text = [System.IO.File]::ReadAllText($ManifestPath, [System.Text.Encoding]::UTF8)
    return $text | ConvertFrom-Json
}

function Save-Manifest($obj) {
    $json = $obj | ConvertTo-Json -Depth 12
    # PS 5.1's ConvertTo-Json has no -EscapeHandling and escapes & < > ' as \uXXXX. Undo only
    # those four, and only where they are NOT preceded by a backslash, so a literal backslash-u
    # sequence in free-form prose cannot be collapsed into an invalid escape.
    $json = [regex]::Replace($json, '(?<!\\)\\u003c', '<')
    $json = [regex]::Replace($json, '(?<!\\)\\u003e', '>')
    $json = [regex]::Replace($json, '(?<!\\)\\u0026', '&')
    $json = [regex]::Replace($json, '(?<!\\)\\u0027', "'")
    # Prove the result is still valid JSON before overwriting a good file with a bad one.
    try { $null = $json | ConvertFrom-Json }
    catch { Fail "refusing to write manifest: unescape produced invalid JSON ($($_.Exception.Message))" 6 }
    [System.IO.File]::WriteAllText($ManifestPath, $json, $Utf8NoBom)
}

# Pull <Identity ... version="X"/> out of the .addin manifest. This is the authoritative version
# for this addin -- see the note in markdown-snapshot.json about FileVersion being frozen.
function Get-AddinIdentityVersion([string] $addinPath) {
    if (-not (Test-Path -LiteralPath $addinPath)) { return $null }
    try {
        $xml = [xml][System.IO.File]::ReadAllText($addinPath, [System.Text.Encoding]::UTF8)
        $node = $xml.SelectSingleNode('//Identity')
        if ($null -eq $node) { return $null }
        return $node.GetAttribute('version')
    } catch { return $null }
}

# ---- Resolve the target release ---------------------------------------------------------------
$manifest = Read-Manifest
if (-not $Tag) { $Tag = $manifest.targetPin.tag }
if (-not $Tag) { Fail "no tag given and targetPin.tag is empty in $ManifestPath" 2 }

$repoUrl = $manifest.source.repo -replace '\.git$', ''
if ($repoUrl -notmatch 'github\.com/([^/]+)/([^/]+)$') { Fail "cannot parse owner/repo from source.repo '$repoUrl'" 2 }
$owner = $Matches[1]; $repo = $Matches[2]

$assetName = $manifest.source.assetPattern -replace '\{tag\}', $Tag
$payloadDir = Join-Path $BuildRoot $Tag
$addinPath  = Join-Path $payloadDir $manifest.source.manifestFile

Write-Host ""
Write-Host "Markdown Editor sync -> $owner/$repo @ $Tag" -ForegroundColor White

$alreadyThere = (Test-Path -LiteralPath $addinPath)
if ($alreadyThere -and -not $Force) {
    Write-Step "payload already present at $payloadDir (use -Force to re-download)"
} else {
    $apiUrl = "https://api.github.com/repos/$owner/$repo/releases/tags/$Tag"
    Write-Step "resolving release $Tag ..."
    try {
        $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'ClarionAssistant-sync' }
    } catch {
        Fail "could not resolve release '$Tag' from $apiUrl ($($_.Exception.Message))" 3
    }

    $asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
    if (-not $asset) {
        $names = ($release.assets | ForEach-Object { $_.name }) -join ', '
        Fail "release $Tag has no asset named '$assetName' (assets: $names)" 3
    }

    $tmpZip = Join-Path ([System.IO.Path]::GetTempPath()) ("md-" + [System.Guid]::NewGuid().ToString('N') + '.zip')
    Write-Step "downloading $assetName ($([math]::Round($asset.size / 1KB)) KB) ..."
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmpZip -Headers @{ 'User-Agent' = 'ClarionAssistant-sync' }
    } catch {
        Fail "download failed: $($_.Exception.Message)" 3
    }

    $sha = (Get-FileHash -LiteralPath $tmpZip -Algorithm SHA256).Hash
    # A pin recorded for THIS tag must keep matching. A changed hash under an unchanged tag means
    # the release asset was replaced upstream, which we must never absorb silently.
    $pinnedSha = $null
    if ($manifest.currentPin.tag -eq $Tag) { $pinnedSha = $manifest.currentPin.assetSha256 }
    if ($pinnedSha -and ($pinnedSha -ne $sha)) {
        Remove-Item -LiteralPath $tmpZip -Force -ErrorAction SilentlyContinue
        Fail "asset hash for $Tag changed: manifest pins $pinnedSha but the download is $sha. The upstream release asset was replaced; verify deliberately, then clear currentPin.assetSha256 to accept." 5
    }
    Write-Ok "sha256 $sha"

    if (Test-Path -LiteralPath $payloadDir) { Remove-Item -LiteralPath $payloadDir -Recurse -Force }
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    Write-Step "extracting to $payloadDir ..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($tmpZip, $payloadDir)
    Remove-Item -LiteralPath $tmpZip -Force -ErrorAction SilentlyContinue

    # MIT: the copyright and permission notice must travel with the copy. Upstream's release ZIP
    # does not contain it, so fetch it from the repo at the SAME tag we just shipped from.
    $licUrl  = "https://raw.githubusercontent.com/$owner/$repo/$Tag/$($manifest.source.licensePath)"
    $licDest = Join-Path $payloadDir 'LICENSE-ClarionMarkdownEditor.txt'
    Write-Step "fetching upstream LICENSE ..."
    try {
        Invoke-WebRequest -Uri $licUrl -OutFile $licDest -Headers @{ 'User-Agent' = 'ClarionAssistant-sync' }
    } catch {
        Fail "could not fetch LICENSE from $licUrl ($($_.Exception.Message)). We cannot redistribute an MIT addin without its notice." 4
    }
    if ((Get-Item -LiteralPath $licDest).Length -lt 100) { Fail "fetched LICENSE looks truncated ($licDest)" 4 }
    Write-Ok "staged LICENSE-ClarionMarkdownEditor.txt"

    $manifest.currentPin.assetSha256 = $sha
    $manifest.currentPin.publishedAt = $release.published_at
}

# ---- Verify and record ------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $addinPath)) {
    Fail "payload is missing $($manifest.source.manifestFile) after sync -- refusing to record a pin for a tree that cannot ship." 4
}

$identity = Get-AddinIdentityVersion $addinPath
if (-not $identity) {
    Fail "could not read <Identity version> from $addinPath. The installer's only-if-newer check depends on it; refusing to record an empty version." 4
}
Write-Ok "identity version $identity (tag $Tag)"

# Loud, not fatal: upstream tags are vX.Y.Z and Identity is X.Y.Z, so a mismatch means either the
# tag or the manifest was not bumped -- worth a human look before a release goes out.
$tagAsVersion = $Tag -replace '^v', ''
if ($tagAsVersion -ne $identity) {
    Write-Warn "tag $Tag implies version $tagAsVersion but the addin manifest says $identity -- upstream may not have bumped one of them. The installer will compare using $identity."
}

$manifest.currentPin.tag             = $Tag
$manifest.currentPin.identityVersion = $identity
$manifest.targetPin.tag              = $Tag
$manifest.resolvedTag                = $Tag
$manifest.resolvedIdentityVersion    = $identity
$manifest.lastSync                   = (Get-Date).ToString('yyyy-MM-dd')
Save-Manifest $manifest

Write-Ok "manifest updated: resolvedTag=$Tag resolvedIdentityVersion=$identity"
Write-Host ""
Write-Host "  NEXT: installer\ClarionAssistant.iss pins this path by tag." -ForegroundColor White
Write-Host "        Update BOTH #define SrcMarkdown (.markdown-build\$Tag) and" -ForegroundColor White
Write-Host "        #define MarkdownPinVersion ($identity), then rebuild the installer." -ForegroundColor White
Write-Host ""
exit 0
