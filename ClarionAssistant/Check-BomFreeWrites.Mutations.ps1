<#
.SYNOPSIS
    Mutation-tests Check-BomFreeWrites.ps1's own fixture set. Breaks one branch of the scanner at
    a time and requires -SelfTest to go RED.

.DESCRIPTION
    WHY THIS EXISTS. Check-BomFreeWrites -SelfTest proves the scanner catches every evasion shape
    we know of. It does NOT prove the fixture set would notice if the scanner broke - and twice
    during review it would not have:

      1. Three branches were pinned by nothing at all. Deleting the verbatim-string body blank, the
         newline containment guards, or the {{ escape each still reported a clean 33/33. Those were
         exactly the statements hand-inlined for a speed fix, so the untested branches and the
         hand-edited branches were the same branches.
      2. Worse, a fixture set DECAYS. Adding the hole newline guard silently disarmed two fixtures
         that had been pinning real branches, because their damage was on the NEXT line and the new
         guard contained it. The guard and the fixture were measuring the same thing, so the
         fixture stopped discriminating the moment the guard landed. Nothing failed. Nothing said
         a word.

    That second one is the reason this is a script and not a paragraph of advice. A fixture set is
    not a thing you verify once; it is a thing that quietly stops working as the code it guards
    improves. Run this after ANY change to Split-Code, Get-CallArguments, the regex constants, or
    the fixture list itself.

    Branches are located by CONTENT, not line number - a line-numbered harness rots on the first
    edit. A branch that cannot be found reports SKIPPED, which is a HARNESS failure, not a pass:
    fix the pattern. (That has already happened twice here, and a SKIPPED row reads a lot like a
    clean run if you are not counting.)

    Expected output: every branch CAUGHT, "ALL BRANCHES PINNED", and the control - which inverts
    the self-test's own verdict comparison - failing every fixture. A harness that reports CAUGHT
    for everything would be as useless as a guard that reports PASS for everything.

.NOTES
    Windows PowerShell 5.1 compatible. Pure ASCII, no BOM. Modifies nothing: it works on copies in
    %TEMP% and deletes them. Companion to Check-BomFreeWrites.ps1; see ticket 9b9dbc7d.
#>
$src  = 'H:\DevLaptop\ClarionAssistant\ClarionAssistant\Check-BomFreeWrites.ps1'
$work = Join-Path $env:TEMP ('mut2_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$lines = [IO.File]::ReadAllLines($src)

# Each mutation: a unique substring identifying the branch, and how to break it.
# Kill = replace the whole line with a comment. Sub = regex-replace within the line.
$mutations = @(
    @{ Name = '*/ close detection';        Find = '-eq .\*. -and \$i \+ 1 -lt \$n';                    Kill = $true }
    @{ Name = '@" opener push';            Find = "-eq '@' -and .next -eq '`"'\) \{";              Kill = $true }
    @{ Name = 'str/char backslash escape'; Find = '^\s+if \(\$c -eq .\\.\)\s+\{ Set-Blank';           Kill = $true }
    @{ Name = 'interp backslash escape';   Find = '-not \$top\.Verbatim -and \$c -eq .\\.\)\s+\{ Set-Blank'; Kill = $true }
    @{ Name = 'interp newline guard';      Find = "-not .top\.Verbatim -and .c -eq `"``n`"";        Kill = $true }
    @{ Name = 'hole newline guard';        Find = '\$encl = if \(\$stack\.Count -ge 2\)';          Sub = @('\$encl = if.*$', '$encl = $null') }
    @{ Name = 'hole body blank';           Find = 'inside a literal: blank everything';            Sub = @('^\s*if \(\$out.*?\}; ', '            ') }
    @{ Name = 'vstr body blank';           Find = '# newlines legal';                              Sub = @('^\s*if \(\$out.*?\}; ', '            ') }
    @{ Name = 'str/char newline guard';    Find = '# unterminated: contain it';                    Kill = $true }
    @{ Name = 'interp {{ escape';          Find = "-eq '\{' -and .next -eq '\{'";                  Kill = $true }
    @{ Name = 'line number computation';   Find = '\$lineNo  = ';                                  Sub = @('\)\.Count$', ').Count + 1') }
    @{ Name = 'member-access strip';       Find = '\$probe = \[regex\]::Replace';                  Sub = @('\$encodingMemberAccess', "'ZZZNOMATCH'") }
    @{ Name = 'qualified StreamWriter';    Find = '^\$writeApis = ';                               Sub = @('\(\?:\[\\w\.\]\+\\\.\)\?', '') }
)

function Run-SelfTest([string]$path) {
    (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $path -SelfTest 2>&1 | Out-String).Trim()
}
function FirstLine([string]$s) {
    ($s -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
}

$base = Join-Path $work 'base.ps1'; [IO.File]::WriteAllLines($base, $lines)
"BASELINE: " + (FirstLine (Run-SelfTest $base))
""

$missed = 0
$idx = 0
foreach ($m in $mutations) {
    $idx++
    $copy = [string[]]$lines.Clone()
    $hit  = -1
    for ($k = 0; $k -lt $copy.Length; $k++) {
        if ($copy[$k] -match $m.Find) { $hit = $k; break }
    }
    if ($hit -lt 0) { "SKIPPED  {0,-28} (branch not found - harness is stale)" -f $m.Name; $missed++; continue }

    if ($m.Kill) { $copy[$hit] = '            # MUTATED OUT' }
    else         { $copy[$hit] = $copy[$hit] -replace $m.Sub[0], $m.Sub[1] }

    if ($copy[$hit] -eq $lines[$hit]) { "SKIPPED  {0,-28} (mutation was a no-op)" -f $m.Name; $missed++; continue }

    $f = Join-Path $work ("m$idx.ps1")
    [IO.File]::WriteAllLines($f, $copy)
    $res = Run-SelfTest $f
    if ($res -match 'SELF-TEST FAIL') {
        "CAUGHT   {0,-28} {1}" -f $m.Name, (FirstLine $res)
    } else {
        "MISSED ! {0,-28} {1}" -f $m.Name, (FirstLine $res)
        $missed++
    }
}
""
if ($missed -eq 0) { "ALL BRANCHES PINNED - every mutation turns the self-test red." }
else               { "$missed branch(es) NOT pinned by any fixture." }

# Harness control: if the verdict test itself is inverted, most fixtures must fail. A harness that
# reports CAUGHT for everything would be as useless as a guard that reports PASS for everything.
$ctl = [string[]]$lines.Clone()
for ($k = 0; $k -lt $ctl.Length; $k++) {
    if ($ctl[$k] -match 'if \(\$tripped -ne \$f\.Trip\)') { $ctl[$k] = $ctl[$k] -replace '-ne', '-eq'; break }
}
$c = Join-Path $work 'ctl.ps1'; [IO.File]::WriteAllLines($c, $ctl)
"CONTROL (verdict test inverted): " + (FirstLine (Run-SelfTest $c))

Remove-Item -LiteralPath $work -Recurse -Force
