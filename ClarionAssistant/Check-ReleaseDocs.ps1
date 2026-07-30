# Check-ReleaseDocs.ps1
# Bidirectional release-docs drift reconciler.
#
# Answers one question: "is everything that landed since the last release tag actually
# documented in README's 'What's New (Unreleased)' block, and is everything documented
# there real?"
#
# WHY THIS EXISTS
#   Nine community PRs merged for 5.5 and eight were documented nowhere. But the deeper
#   problem is not contributor discipline: maintainer-authored features (the CA Compare
#   run, Data pad, the CA Explorer rebuild) carry no GitHub issue/PR reference at all, so
#   a tool that reconciles PR numbers is structurally blind to them. Hence two independent
#   passes — one reference-free (scopes), one reference-based (issue/PR numbers).
#
# ANCHOR
#   The last release tag (v5.4.0, v5.3.0, ...), NOT Version.props. The build stamp
#   increments on every local build and cannot delineate what shipped.
#
# EXAMPLES
#   .\Check-ReleaseDocs.ps1                        # check working tree against last tag
#   .\Check-ReleaseDocs.ps1 -Ref 800b03a           # check docs as they were at a commit
#   .\Check-ReleaseDocs.ps1 -SinceTag v5.3.0       # different baseline
#   .\Check-ReleaseDocs.ps1 -NoGh                  # offline: skip all GitHub lookups
#   .\Check-ReleaseDocs.ps1 -Json                  # machine-readable output
#
# EXIT CODES
#   0  clean
#   1  drift found
#   2  could not run (bad args, not a repo, no tag, missing files)

[CmdletBinding()]
param(
    # Baseline tag. Defaults to the most recent tag reachable from -Ref.
    [string]$SinceTag,

    # Commit to check. Defaults to the working tree (uncommitted doc edits DO count).
    # Passing an explicit ref also reads README/config AT that ref, so historical
    # commits can be checked — that is what makes this script testable against its own
    # repo history rather than only against "now".
    [string]$Ref,

    [string]$RepoRoot,

    # Skip every GitHub API call. Reference resolution and attribution degrade to
    # "skipped" and say so — they never report a false clean.
    [switch]$NoGh,

    [switch]$Json,

    # Report drift but exit 0 anyway. For advisory runs; the release gate does not use it.
    [switch]$WarnOnly
)

$ErrorActionPreference = 'Stop'

# Commit types that never produce a release note.
$script:NonUserFacingTypes = @(
    'chore', 'docs', 'test', 'tests', 'ci', 'build', 'style', 'refactor', 'revert', 'wip'
)

# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

function Fail([string]$Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 2
}

function Invoke-Git {
    param([string[]]$GitArgs, [switch]$AllowFail)
    $out = & git -C $script:Root @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFail) { return $null }
        Fail "git $($GitArgs -join ' ') failed: $out"
    }
    return $out
}

# Normalises prose for matching: HTML entities the notes use, punctuation, backticks.
function Normalize-Text([string]$Text) {
    if (-not $Text) { return '' }
    $t = $Text
    $t = $t -replace '&mdash;|&ndash;|&rarr;|&amp;', ' '
    $t = $t -replace '&[a-z]+;', ' '
    $t = $t -replace '[`*_\[\]()]', ' '
    $t = $t -replace '\s+', ' '
    return $t.Trim().ToLowerInvariant()
}

function Get-RefsFromText([string]$Text) {
    if (-not $Text) { return @() }
    # '#123' covers the bare spelling and the 'GH #123' spelling alike.
    #
    # The lookarounds reject hex colour codes, which are otherwise a real false-positive
    # source: '#6c7086' and '#9a9ab0' in a dark-mode contrast commit were being read as
    # references to issues #6 and #9, and '#666' would read as #666. Requiring a
    # non-alphanumeric on both sides means a colour never parses as a reference, while
    # '(#140)', 'GH #126' and '#159/#160' all still do.
    return [regex]::Matches($Text, '(?<![0-9A-Za-z])#(\d{1,5})(?![0-9A-Za-z])') |
        ForEach-Object { [int]$_.Groups[1].Value } |
        Sort-Object -Unique
}

# ---------------------------------------------------------------------------
# repo / config
# ---------------------------------------------------------------------------

if (-not $RepoRoot) {
    $probe = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
    $top = & git -C $probe rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $top) { Fail "not inside a git repository (probed: $probe)" }
    $RepoRoot = ($top | Select-Object -First 1).Trim()
}
$script:Root = $RepoRoot

$readTarget = if ($Ref) { $Ref } else { 'HEAD' }
$rangeEnd   = if ($Ref) { $Ref } else { 'HEAD' }

if (-not $SinceTag) {
    $SinceTag = Invoke-Git @('describe', '--tags', '--abbrev=0', $rangeEnd) -AllowFail
    if (-not $SinceTag) { Fail "no tag reachable from '$rangeEnd'; pass -SinceTag explicitly" }
    $SinceTag = ($SinceTag | Select-Object -First 1).Trim()
}

$configRelPath = 'docs/releases/release-docs.config.json'
$readmeRelPath = 'README.md'

function Get-FileFromWorkingTree([string]$RelPath) {
    $full = Join-Path $script:Root $RelPath
    if (-not (Test-Path -LiteralPath $full)) { return $null }
    return (Get-Content -LiteralPath $full -Raw -Encoding UTF8)
}

function Get-FileAt([string]$RelPath) {
    if ($Ref) {
        $content = Invoke-Git @('show', "${readTarget}:$RelPath") -AllowFail
        if ($null -eq $content) { return $null }
        return ($content -join "`n")
    }
    return Get-FileFromWorkingTree $RelPath
}

# The config is tooling state, not history: always read the CURRENT alias/author map even
# when checking an old ref. Reading a historical copy would mean testing against whatever
# aliases existed back then — and at any ref predating this script there is no copy at all.
$configRaw = Get-FileFromWorkingTree $configRelPath
if (-not $configRaw) { Fail "config not found: $configRelPath (at $readTarget)" }
try { $cfg = $configRaw | ConvertFrom-Json } catch { Fail "config is not valid JSON: $_" }

$readme = Get-FileAt $readmeRelPath
if (-not $readme) { Fail "README.md not found (at $readTarget)" }

function Get-ConfigMap($Node) {
    $map = @{}
    if ($null -eq $Node) { return $map }
    foreach ($p in $Node.PSObject.Properties) {
        if ($p.Name -like '$comment*') { continue }
        $map[$p.Name] = $p.Value
    }
    return $map
}

$scopeAliases   = Get-ConfigMap $cfg.scopeAliases
$authorMap      = Get-ConfigMap $cfg.authors
$coveredOverride= Get-ConfigMap $cfg.coveredOverrides
$ignoreScopes   = @($cfg.ignoreScopes)   | Where-Object { $_ }
$ackCommits     = @($cfg.acknowledgedCommits) | Where-Object { $_ }

# ---------------------------------------------------------------------------
# README: the Unreleased block
# ---------------------------------------------------------------------------

$lines = $readme -split "`r?`n"
$startIdx = -1; $endIdx = $lines.Count
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($startIdx -lt 0) {
        if ($lines[$i] -match "^##\s+What.s New \(Unreleased\)") { $startIdx = $i; continue }
    } elseif ($lines[$i] -match '^##\s+') { $endIdx = $i; break }
}
if ($startIdx -lt 0) { Fail "could not find a '## What's New (Unreleased)' section in README.md (at $readTarget)" }

$blockLines = $lines[($startIdx + 1)..($endIdx - 1)]
$blockText  = $blockLines -join "`n"

# Entry TITLES only — '###' headings and '- **bold**' lead-ins.
#
# Deliberately excludes body prose. Measured reason: the word 'compare' occurs inside the
# CA Explorer entry's paragraph ('per-compare'), so a body-text match reported scope
# 'compare' as documented during the window when CA Compare had no entry of its own.
# A title is a claim that something is documented; a passing mention in a paragraph is not.
$entryTitles = @()
foreach ($l in $blockLines) {
    if ($l -match '^#{3,}\s+(.+)$')       { $entryTitles += $Matches[1] }
    elseif ($l -match '^\s*[-*]\s+\*\*(.+?)\*\*') { $entryTitles += $Matches[1] }
}
$normalizedTitles = @($entryTitles | ForEach-Object { Normalize-Text $_ })

# In-README coverage markers: <!-- release-docs: covered=scope[,scope] -->
$markedScopes = @()
foreach ($m in [regex]::Matches($blockText, '<!--\s*release-docs:\s*covered=([^>]+?)\s*-->')) {
    $markedScopes += ($m.Groups[1].Value -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() })
}
$markedScopes = @($markedScopes | Where-Object { $_ })

# The Thanks sub-block, for attribution.
$thanksText = ''
$inThanks = $false
foreach ($l in $blockLines) {
    if ($l -match '^#{3,}\s+Thanks') { $inThanks = $true; continue }
    if ($inThanks -and $l -match '^#{3,}\s+') { $inThanks = $false }
    if ($inThanks) { $thanksText += "$l`n" }
}

$documentedRefs = Get-RefsFromText $blockText

# ---------------------------------------------------------------------------
# commits in range
# ---------------------------------------------------------------------------

$range = "$SinceTag..$rangeEnd"
$US = [char]0x1f; $RS = [char]0x1e
$rawLog = Invoke-Git @('log', $range, "--format=%H$US%P$US%an$US%s$US%b$RS")
$logText = ($rawLog -join "`n")

$commits = @()
foreach ($rec in ($logText -split $RS)) {
    $rec = $rec.Trim("`n", "`r", ' ')
    if (-not $rec) { continue }
    $parts = $rec -split $US
    if ($parts.Count -lt 4) { continue }
    $commits += [pscustomobject]@{
        Sha     = $parts[0].Trim()
        Short   = $parts[0].Trim().Substring(0, 7)
        IsMerge = (($parts[1].Trim() -split '\s+' | Where-Object { $_ }).Count -gt 1)
        Author  = $parts[2].Trim()
        Subject = $parts[3].Trim()
        Body    = if ($parts.Count -ge 5) { $parts[4].Trim() } else { '' }
    }
}
if (-not $commits) { Fail "no commits in range $range — is the tag right?" }

# Classify each non-merge commit: user-facing? which scope(s)?
$scopeCommits = @{}   # scope -> list of commits
$unscoped     = @()   # user-facing but carrying no scope we can trust

foreach ($c in ($commits | Where-Object { -not $_.IsMerge })) {
    $subj = $c.Subject

    if ($subj -match '^(?<type>[A-Za-z]+)(\((?<scope>[^)]*)\))?!?:\s*(?<rest>.*)$') {
        $type = $Matches['type'].ToLowerInvariant()
        $scopeRaw = $Matches['scope']

        if ($script:NonUserFacingTypes -contains $type) { continue }

        if ($scopeRaw) {
            # 'fix(completion,hover)' -> two scopes, both must be covered.
            foreach ($s in ($scopeRaw -split '[,/]' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })) {
                if ($ignoreScopes -contains $s) { continue }
                if (-not $scopeCommits.ContainsKey($s)) { $scopeCommits[$s] = @() }
                $scopeCommits[$s] += $c
            }
            continue
        }
        # Typed but unscoped, e.g. 'feat: something'.
        $unscoped += $c
        continue
    }

    # This repo also uses bare feature subjects: 'CA Compare: editable diff ...'
    if ($subj -match '^CA\s+(?<feature>[A-Za-z]+)\s*:') {
        $s = $Matches['feature'].ToLowerInvariant()
        if ($ignoreScopes -notcontains $s) {
            if (-not $scopeCommits.ContainsKey($s)) { $scopeCommits[$s] = @() }
            $scopeCommits[$s] += $c
        }
        continue
    }

    # No prefix, no recognisable feature name. The tool does NOT guess here — an
    # unprefixed subject has no reliable key, and guessing would either invent drift
    # or hide it. It goes to triage and a human acknowledges it in the config.
    $unscoped += $c
}

# ---------------------------------------------------------------------------
# GitHub
# ---------------------------------------------------------------------------

$ghAvailable = $false
$ghRepo = $null
$ghSkipReason = $null

if ($NoGh) {
    $ghSkipReason = '-NoGh was passed'
} else {
    $ghCmd = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $ghCmd) {
        $ghSkipReason = 'the gh CLI is not on PATH'
    } else {
        $origin = Invoke-Git @('remote', 'get-url', 'origin') -AllowFail
        if ($origin) {
            $originStr = ($origin | Select-Object -First 1).Trim()
            if ($originStr -match '[:/]([^/:]+)/([^/]+?)(\.git)?$') {
                $ghRepo = "$($Matches[1])/$($Matches[2])"
                $ghAvailable = $true
            }
        }
        if (-not $ghAvailable) { $ghSkipReason = 'could not derive owner/repo from the origin remote' }
    }
}

$refCache = @{}
function Resolve-Ref([int]$Number) {
    if ($refCache.ContainsKey($Number)) { return $refCache[$Number] }
    if (-not $ghAvailable) { return $null }
    # The issues endpoint serves BOTH issues and pull requests; a 'pull_request' member
    # marks the latter. Deliberately does not assert which one a reference "should" be —
    # this repo's notes cite issues in headings and PRs in Thanks, and also the reverse
    # (v5.4 headings link both issues/66 and pull/91; its Thanks credits bug reporters by
    # issue). Any rule stronger than "it must exist" false-positives on real release notes.
    $json = & gh api "repos/$ghRepo/issues/$Number" --jq '{state:.state, isPr:(.pull_request!=null), merged:(.pull_request.merged_at!=null), title:.title, login:.user.login}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) {
        $refCache[$Number] = [pscustomobject]@{ Exists = $false }
        return $refCache[$Number]
    }
    $o = $json | ConvertFrom-Json
    $refCache[$Number] = [pscustomobject]@{
        Exists = $true
        State  = $o.state
        IsPr   = [bool]$o.isPr
        Merged = [bool]$o.merged
        Title  = $o.title
        Login  = $o.login
    }
    return $refCache[$Number]
}

# ---------------------------------------------------------------------------
# PASS A — scope coverage (reference-free)
# ---------------------------------------------------------------------------

function Test-ScopeCovered([string]$Scope) {
    if ($markedScopes -contains $Scope) { return @{ Covered = $true; How = 'README marker' } }
    if ($coveredOverride.ContainsKey($Scope)) { return @{ Covered = $true; How = 'config override' } }

    $needles = @($Scope)
    if ($scopeAliases.ContainsKey($Scope)) { $needles += @($scopeAliases[$Scope]) }
    foreach ($n in ($needles | ForEach-Object { Normalize-Text $_ } | Where-Object { $_ })) {
        foreach ($t in $normalizedTitles) {
            if ($t -like "*$n*") { return @{ Covered = $true; How = "title match on '$n'" } }
        }
    }
    return @{ Covered = $false; How = $null }
}

$passA = @{ Uncovered = @(); Covered = @(); Triage = @() }

foreach ($scope in ($scopeCommits.Keys | Sort-Object)) {
    $r = Test-ScopeCovered $scope
    $entry = [pscustomobject]@{
        Scope   = $scope
        Count   = $scopeCommits[$scope].Count
        How     = $r.How
        Commits = @($scopeCommits[$scope] | ForEach-Object { "$($_.Short) $($_.Subject)" })
    }
    if ($r.Covered) { $passA.Covered += $entry } else { $passA.Uncovered += $entry }
}

foreach ($c in $unscoped) {
    $isAck = $false
    foreach ($a in $ackCommits) {
        if ($a -and ($c.Sha -like "$a*")) { $isAck = $true; break }
    }
    if ($isAck) { continue }

    # An unscoped commit that names issues in its subject is not really unattributable:
    # if every one of those references is cited in the Unreleased block, the work is
    # documented and asking a human to acknowledge it again is pure noise. Example:
    # 'Monaco editor steals focus ... (GH #140)' against the '#140' Fixed bullet.
    $subjRefs = Get-RefsFromText $c.Subject
    if ($subjRefs.Count -gt 0) {
        $allCited = $true
        foreach ($r in $subjRefs) { if ($documentedRefs -notcontains $r) { $allCited = $false; break } }
        if ($allCited) { continue }
    }

    $passA.Triage += [pscustomobject]@{ Sha = $c.Short; Subject = $c.Subject; Author = $c.Author }
}

# ---------------------------------------------------------------------------
# PASS B — reference reconciliation (bidirectional)
# ---------------------------------------------------------------------------

# Landed refs come from commit SUBJECTS ONLY, and merge commits are deliberately included:
# merge subjects are precisely where PR numbers live ('Merge pull request #157 from ...'),
# so filtering merges out would discard the only reference-bearing commits for contributor
# work. Bodies are excluded — measured, they cite prior art, upstream tickets and unrelated
# context (a body reference to #66 pulled in a feature that shipped back in v5.4). A subject
# reference is a claim about what the commit does; a body reference is background.
$prMergeRefs = @()
$otherRefs   = @()
foreach ($c in $commits) {
    $refs = Get-RefsFromText $c.Subject
    if ($c.Subject -match '^Merge pull request\s+#(\d+)') {
        $prMergeRefs += [int]$Matches[1]
        $otherRefs   += @($refs | Where-Object { $_ -ne [int]$Matches[1] })
    } else {
        $otherRefs += $refs
    }
}
$prMergeRefs = @($prMergeRefs | Sort-Object -Unique)
$otherRefs   = @($otherRefs   | Sort-Object -Unique | Where-Object { $prMergeRefs -notcontains $_ })
$landedRefs  = @(($prMergeRefs + $otherRefs) | Sort-Object -Unique)

# A merged PR that the notes never cite is hard drift: it is the exact failure this tool
# exists for, and it has attribution consequences (an uncredited contributor).
#
# Any OTHER subject reference — 'GH #126' on a maintainer fix — is advisory. Maintainer
# entries in this repo describe the change in prose without citing a number, so demanding a
# citation would impose a new editorial convention rather than enforce the existing one.
# Pass A already covers that work by scope; this is corroboration, not an independent check.
$passB = @{
    PrNotDocumented    = @($prMergeRefs | Where-Object { $documentedRefs -notcontains $_ } | Sort-Object)
    MentionedNotCited  = @($otherRefs   | Where-Object { $documentedRefs -notcontains $_ } | Sort-Object)
    Unresolvable       = @()
    StillOpen          = @()
    Skipped            = (-not $ghAvailable)
}

if ($ghAvailable) {
    foreach ($n in (@($landedRefs + $documentedRefs) | Sort-Object -Unique)) {
        $info = Resolve-Ref $n
        if (-not $info) { continue }
        if (-not $info.Exists) {
            if ($documentedRefs -contains $n) {
                $passB.Unresolvable += [pscustomobject]@{ Ref = $n }
            }
            continue
        }
        if ($info.State -eq 'open') {
            $passB.StillOpen += [pscustomobject]@{
                Ref   = $n
                IsPr  = $info.IsPr
                Title = $info.Title
                Where = @(
                    $(if ($landedRefs -contains $n) { 'referenced by a commit in range' })
                    $(if ($documentedRefs -contains $n) { 'cited in the Unreleased block' })
                ) -ne $null -join '; '
            }
        }
    }
}

# ---------------------------------------------------------------------------
# PASS C — attribution
# ---------------------------------------------------------------------------

$passC = @{ MissingThanks = @(); UnmappedLogins = @(); Skipped = (-not $ghAvailable) }

if ($ghAvailable) {
    $normalizedThanks = Normalize-Text $thanksText
    $contributorLogins = @()
    foreach ($n in $landedRefs) {
        $info = Resolve-Ref $n
        if ($info -and $info.Exists -and $info.IsPr -and $info.Merged -and $info.Login) {
            $contributorLogins += $info.Login
        }
    }
    foreach ($login in ($contributorLogins | Sort-Object -Unique)) {
        if (-not $authorMap.ContainsKey($login)) {
            $passC.UnmappedLogins += $login
            continue
        }
        $display = Normalize-Text $authorMap[$login]
        if ($display -and ($normalizedThanks -notlike "*$display*")) {
            $passC.MissingThanks += [pscustomobject]@{ Login = $login; Display = $authorMap[$login] }
        }
    }
}

# ---------------------------------------------------------------------------
# report
# ---------------------------------------------------------------------------

# Only hard findings gate a release. MentionedNotCited and StillOpen are reported but do
# not fail the build: the first would impose a citation convention the repo does not have,
# and the second is about GitHub hygiene rather than the notes being wrong.
$driftCount =
    $passA.Uncovered.Count +
    $passA.Triage.Count +
    $passB.PrNotDocumented.Count +
    $passB.Unresolvable.Count +
    $passC.MissingThanks.Count +
    $passC.UnmappedLogins.Count

if ($Json) {
    [pscustomobject]@{
        range        = $range
        ref          = $readTarget
        commits      = $commits.Count
        ghAvailable  = $ghAvailable
        ghSkipReason = $ghSkipReason
        drift        = $driftCount
        passA        = $passA
        passB        = $passB
        passC        = $passC
    } | ConvertTo-Json -Depth 8
    if ($driftCount -gt 0 -and -not $WarnOnly) { exit 1 } else { exit 0 }
}

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host $Title -ForegroundColor Cyan
}

Write-Host ''
Write-Host "Release-docs check  $range  ($($commits.Count) commits, docs at $readTarget)" -ForegroundColor White
if (-not $ghAvailable) {
    Write-Host "GitHub lookups SKIPPED - $ghSkipReason. Reference resolution, still-open and attribution checks did not run." -ForegroundColor Yellow
}

Write-Section '[A] Scope coverage (reference-free)'
if ($passA.Uncovered.Count -eq 0 -and $passA.Triage.Count -eq 0) {
    Write-Host "  OK - every user-facing scope in range is named by an entry title." -ForegroundColor Green
}
foreach ($u in $passA.Uncovered) {
    Write-Host "  UNDOCUMENTED  scope '$($u.Scope)' - $($u.Count) commit(s), no entry title mentions it" -ForegroundColor Red
    foreach ($c in ($u.Commits | Select-Object -First 6)) { Write-Host "      $c" -ForegroundColor DarkGray }
    if ($u.Commits.Count -gt 6) { Write-Host "      ... and $($u.Commits.Count - 6) more" -ForegroundColor DarkGray }
    Write-Host "      -> add an entry naming it, add an alias in $configRelPath," -ForegroundColor DarkGray
    Write-Host "         or mark the entry with <!-- release-docs: covered=$($u.Scope) -->" -ForegroundColor DarkGray
}
foreach ($t in $passA.Triage) {
    Write-Host "  TRIAGE        $($t.Sha) $($t.Subject)" -ForegroundColor Yellow
    Write-Host "      -> no scope to key on. Document it, or add '$($t.Sha)' to acknowledgedCommits." -ForegroundColor DarkGray
}
if ($passA.Covered.Count -gt 0) {
    Write-Host "  covered: $((($passA.Covered | ForEach-Object { $_.Scope }) -join ', '))" -ForegroundColor DarkGray
}

Write-Section '[B] Issue/PR references (bidirectional)'
if ($passB.PrNotDocumented.Count -eq 0) {
    Write-Host "  OK - every PR merged in range is cited in the Unreleased block." -ForegroundColor Green
} else {
    foreach ($n in $passB.PrNotDocumented) {
        $info = if ($ghAvailable) { Resolve-Ref $n } else { $null }
        $title = if ($info -and $info.Exists) { " - $($info.Title)" } else { '' }
        $who   = if ($info -and $info.Exists -and $info.Login) { " [@$($info.Login)]" } else { '' }
        Write-Host "  PR UNCITED    #$n$title$who" -ForegroundColor Red
    }
    Write-Host "      -> a merged PR nobody cited is the original failure mode, and its author" -ForegroundColor DarkGray
    Write-Host "         goes uncredited. Cite it in an entry, or in the Thanks block." -ForegroundColor DarkGray
}
if ($passB.MentionedNotCited.Count -gt 0) {
    Write-Host "  note: subject-referenced but not cited (advisory, does not gate): $((($passB.MentionedNotCited | ForEach-Object { "#$_" }) -join ', '))" -ForegroundColor DarkGray
}
foreach ($u in $passB.Unresolvable) {
    Write-Host "  BAD REFERENCE #$($u.Ref) is cited in the notes but does not exist on GitHub" -ForegroundColor Red
}
foreach ($s in $passB.StillOpen) {
    $kind = if ($s.IsPr) { 'PR' } else { 'issue' }
    Write-Host "  STILL OPEN    #$($s.Ref) ($kind) - $($s.Title)" -ForegroundColor Yellow
    Write-Host "      $($s.Where) - close it if it shipped." -ForegroundColor DarkGray
}
if ($passB.Skipped) { Write-Host "  (resolution and still-open checks skipped)" -ForegroundColor DarkGray }

Write-Section '[C] Attribution'
if ($passC.Skipped) {
    Write-Host "  (skipped)" -ForegroundColor DarkGray
} elseif ($passC.MissingThanks.Count -eq 0 -and $passC.UnmappedLogins.Count -eq 0) {
    Write-Host "  OK - every merged-PR author in range is credited in Thanks." -ForegroundColor Green
} else {
    foreach ($l in $passC.UnmappedLogins) {
        Write-Host "  UNMAPPED      login '$l' has a merged PR in range but no display name" -ForegroundColor Red
        Write-Host "      -> add \"$l\": \"Their Name\" to authors in $configRelPath" -ForegroundColor DarkGray
    }
    foreach ($m in $passC.MissingThanks) {
        Write-Host "  NOT CREDITED  $($m.Display) ($($m.Login)) is absent from the Thanks block" -ForegroundColor Red
    }
}

Write-Host ''
if ($driftCount -eq 0) {
    Write-Host "PASS - release docs are in sync with $range." -ForegroundColor Green
    exit 0
}
Write-Host "DRIFT - $driftCount item(s) need attention before cutting a release." -ForegroundColor Red
if ($WarnOnly) { Write-Host "(-WarnOnly: exiting 0 anyway)" -ForegroundColor Yellow; exit 0 }
exit 1
