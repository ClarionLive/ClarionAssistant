# Harness for the deletion gate in installer/publish-marketplace-to-github.ps1
# (ticket 69b2f1fb). Discovered and run automatically by ClarionAssistant\tests\Run-Tests.ps1.
#
# WHAT IT GUARDS. The publish script mirrors marketplace/ to the GitHub marketplace
# with robocopy /MIR, which PRUNES anything absent from the source. That is only safe
# while marketplace/ is genuinely the source of truth, and once it was not: the skill
# clarion-appgen was published from a laptop-local tree, never committed here, and the
# next routine publish would have deleted all nine of its files - appearing as nine
# more lines in a `git status --short` that already scrolls past.
#
# WHY IT IS A COPY OF THE LOGIC, NOT A CALL INTO THE SCRIPT. The real script clones
# a live GitHub repo and its $repoUrl is built as https://github.com/$Repo.git, so it
# cannot be pointed at a local fixture without changing it. Rather than loosen the
# script's own safety to make it testable, this harness replicates the exact pipeline
# it uses - robocopy /MIR -> git add -A -> git status --porcelain -> the '^D' regex ->
# reset --hard + clean - against a scratch repo.
#
# THAT IS A REAL LIMITATION, SO SAY SO: if someone edits the detection in the script
# they must edit it here too, and nothing enforces that. The alternative was no test
# at all. If the script ever gains a -RepoUrl (or similar) seam, replace this harness
# with one that drives the script directly and delete this note.
#
# Exit 0 = all assertions pass. Exit 1 = a real failure. There is no exit-2
# "could not run" path: this needs only git and robocopy, both of which are already
# required by the script under test.
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ('gate-test-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$src  = Join-Path $root 'source'
$work = Join-Path $root 'clone'
$fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:fail++ }
}

try {
    New-Item -ItemType Directory -Force $src, $work | Out-Null

    # Build the "published clone": three skills, committed.
    foreach ($n in 'keep-me','retire-me','also-retire') {
        New-Item -ItemType Directory -Force (Join-Path $work "skills\$n") | Out-Null
        "content of $n" | Set-Content (Join-Path $work "skills\$n\SKILL.md") -Encoding utf8
    }
    & git -C $work init --quiet
    & git -C $work config user.email t@t; & git -C $work config user.name t
    & git -C $work add -A; & git -C $work commit --quiet -m base
    $baseSha = (& git -C $work rev-parse HEAD).Trim()

    # Source has only keep-me, plus a NEW skill and a MODIFIED file.
    New-Item -ItemType Directory -Force (Join-Path $src 'skills\keep-me') | Out-Null
    "content of keep-me MODIFIED" | Set-Content (Join-Path $src 'skills\keep-me\SKILL.md') -Encoding utf8
    New-Item -ItemType Directory -Force (Join-Path $src 'skills\brand-new') | Out-Null
    "new skill" | Set-Content (Join-Path $src 'skills\brand-new\SKILL.md') -Encoding utf8

    # --- exactly what the script does ---
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & robocopy $src $work /MIR /XD '.git' /NFL /NDL /NJH /NJS /NP | Out-Null
    $rc = $LASTEXITCODE; $ErrorActionPreference = $prev; $global:LASTEXITCODE = 0
    if ($rc -ge 8) { throw "robocopy failed $rc" }

    & git -C $work add -A
    $pending = (& git -C $work status --porcelain)

    $deleted = @(
        $pending | Where-Object { $_ -match '^D[ ACDMRTU]\s' } | ForEach-Object { ($_ -replace '^..\s+','').Trim('"') }
    )

    Write-Host "`nRaw porcelain:" -ForegroundColor Cyan
    $pending | ForEach-Object { Write-Host "    [$_]" }
    Write-Host "`nDetected deletions ($($deleted.Count)):" -ForegroundColor Cyan
    $deleted | ForEach-Object { Write-Host "    $_" }

    Write-Host "`nAssertions:" -ForegroundColor Cyan
    Check "detects exactly 2 deletions"        ($deleted.Count -eq 2)
    Check "names retire-me"                    ($deleted -contains 'skills/retire-me/SKILL.md')
    Check "names also-retire"                  ($deleted -contains 'skills/also-retire/SKILL.md')
    Check "does NOT flag the added file"       (-not ($deleted -match 'brand-new'))
    Check "does NOT flag the modified file"    (-not ($deleted -match 'keep-me'))

    # The restore the gate performs before throwing.
    & git -C $work reset --quiet --hard HEAD
    & git -C $work clean --quiet -fd
    $after = (& git -C $work status --porcelain)
    Check "clone is clean after restore"       (-not $after)
    Check "clone still at base commit"         ((& git -C $work rev-parse HEAD).Trim() -eq $baseSha)
    Check "pruned file is BACK on disk"        (Test-Path (Join-Path $work 'skills\retire-me\SKILL.md'))
    Check "added file removed by clean"        (-not (Test-Path (Join-Path $work 'skills\brand-new\SKILL.md')))

    # Negative control: an identical tree must produce NO deletions, so the gate
    # cannot fire on a normal publish.
    & robocopy $work $src /MIR /XD '.git' /NFL /NDL /NJH /NJS /NP | Out-Null
    $global:LASTEXITCODE = 0
    $ErrorActionPreference = 'Continue'
    & robocopy $src $work /MIR /XD '.git' /NFL /NDL /NJH /NJS /NP | Out-Null
    $global:LASTEXITCODE = 0
    $ErrorActionPreference = $prev
    & git -C $work add -A
    $pending2 = (& git -C $work status --porcelain)
    $deleted2 = @($pending2 | Where-Object { $_ -match '^D[ ACDMRTU]\s' })
    Check "no deletions when trees match"      ($deleted2.Count -eq 0)

    Write-Host ""
    if ($fail -eq 0) { Write-Host "ALL CHECKS PASSED" -ForegroundColor Green; exit 0 }
    else { Write-Host "$fail CHECK(S) FAILED" -ForegroundColor Red; exit 1 }
}
finally {
    if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
}
