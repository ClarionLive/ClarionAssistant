# Plugin integrity: every file the plugin POINTS AT must exist AND be tracked by git.
#
# WHY THIS HARNESS EXISTS. Twice in one day the marketplace plugin nearly shipped a pointer to a
# file that was not in the repository:
#
#   1. Nine skills were trimmed and their detail moved into references/ folders. Those 52 files
#      were untracked; committing with `git commit -a` would have shipped skills whose detail was
#      gone and whose pointers went nowhere.
#   2. plugin.json gained an mcpServers entry pointing at launcher/clarion-tools.cmd, which I
#      first placed under bin/ - a directory the repo gitignores globally for .NET build output.
#      The commit succeeded. It shipped a plugin declaring a server whose launcher did not exist.
#
# Both failures are INVISIBLE to a build and to a diff of what changed. Nothing is broken until a
# user installs the plugin and invokes it, and then the symptom - a server that will not start, a
# skill that has lost its detail - points nowhere near the cause.
#
# Existence alone is not enough: the file existed on MY disk both times. Tracked-ness is the
# property that actually matters, because that is what a user receives.

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0

function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$pluginDir = Join-Path $repoRoot 'marketplace\plugins\clarion-assistant'
$pluginJson = Join-Path $pluginDir '.claude-plugin\plugin.json'

Write-Host "=== plugin integrity ($pluginDir) ===" -ForegroundColor Cyan

if (-not (Test-Path $pluginJson)) {
    Write-Host "COULD NOT RUN: plugin.json not found at $pluginJson" -ForegroundColor Red
    exit 2
}

# Tracked = what a user actually receives. `git ls-files --error-unmatch` exits non-zero for an
# untracked OR ignored path, which is exactly the distinction being tested.
function Test-Tracked([string]$absPath) {
    Push-Location $repoRoot
    try {
        $rel = [IO.Path]::GetRelativePath($repoRoot, $absPath)
        & git ls-files --error-unmatch -- $rel 2>&1 | Out-Null
        return ($LASTEXITCODE -eq 0)
    } finally { Pop-Location }
}

# --------------------------------------------------------------- 1. plugin.json declared paths
$cfg = Get-Content $pluginJson -Raw | ConvertFrom-Json
Assert-That ($null -ne $cfg.name) "plugin.json has no name"

$declared = @()
if ($cfg.mcpServers) {
    foreach ($p in $cfg.mcpServers.PSObject.Properties) {
        $srv = $p.Value
        foreach ($token in @($srv.command) + @($srv.args)) {
            if ($token -is [string] -and $token -match '\$\{CLAUDE_PLUGIN_ROOT\}') {
                $declared += [pscustomobject]@{ Server = $p.Name; Token = $token }
            }
        }
    }
}
Write-Host "  mcpServers declaring a plugin-relative path: $($declared.Count)"

foreach ($d in $declared) {
    $rel = ($d.Token -replace '\$\{CLAUDE_PLUGIN_ROOT\}', '') -replace '/', '\'
    $abs = Join-Path $pluginDir $rel.TrimStart('\')
    Assert-That (Test-Path $abs) "mcpServers.$($d.Server) points at '$($d.Token)' which does not exist on disk"
    if (Test-Path $abs) {
        Assert-That (Test-Tracked $abs) `
            "mcpServers.$($d.Server) points at '$($d.Token)' which EXISTS but is NOT TRACKED by git - users would receive a plugin whose launcher is missing"
    }
}

# --------------------------------------------------------------- 2. skills and their references
$skillsDir = Join-Path $pluginDir 'skills'
$refMentions = 0; $skillCount = 0
if (Test-Path $skillsDir) {
    foreach ($skill in Get-ChildItem $skillsDir -Directory) {
        $md = Get-ChildItem $skill.FullName -File | Where-Object { $_.Name -in 'SKILL.md','skill.md' } | Select-Object -First 1
        if (-not $md) { continue }
        $skillCount++
        $text = Get-Content $md.FullName -Raw

        # The SKILL.md ITSELF must be tracked. The reference checks below would happily pass for a
        # skill that exists only on the author's disk - which is exactly how a new skill gets
        # written, verified, and then never shipped.
        Assert-That (Test-Tracked $md.FullName) `
            "$($skill.Name): $($md.Name) is NOT TRACKED by git - the skill would not ship at all"

        # Frontmatter: a skill without it does not error, it simply never loads.
        Assert-That ($text -match '(?s)^\s*---\s*\r?\n.*?\r?\n---') "$($skill.Name): SKILL.md has no closed --- frontmatter block"
        Assert-That ($text -match '(?m)^name:\s*\S')                "$($skill.Name): frontmatter has no name"
        Assert-That ($text -match '(?m)^description:\s*\S')          "$($skill.Name): frontmatter has no description"

        foreach ($m in [regex]::Matches($text, 'references/([A-Za-z0-9._-]+\.md)')) {
            $refMentions++
            $refPath = Join-Path $skill.FullName ("references\" + $m.Groups[1].Value)
            Assert-That (Test-Path $refPath) "$($skill.Name): SKILL.md points at references/$($m.Groups[1].Value), which does not exist"
            if (Test-Path $refPath) {
                Assert-That (Test-Tracked $refPath) `
                    "$($skill.Name): references/$($m.Groups[1].Value) exists but is NOT TRACKED - the skill would ship with its detail missing"
            }
        }
    }
}
Write-Host "  skills checked: $skillCount, reference pointers checked: $refMentions"

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "FAILED - $($failures.Count) of $assertions assertions" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
Write-Host "PASSED - $assertions assertions" -ForegroundColor Green
exit 0
