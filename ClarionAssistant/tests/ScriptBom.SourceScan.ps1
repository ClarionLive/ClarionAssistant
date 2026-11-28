# ScriptBom.SourceScan.ps1 - every PowerShell script with a non-ASCII character must start with a BOM.
#
# WHY. Windows PowerShell 5.1 reads a .ps1 WITHOUT a byte-order mark in the ANSI code page, not as
# UTF-8. An em-dash (E2 80 94) then decodes as three CP1252 characters, and the last one, 0x94, is a
# curly closing quote, which PowerShell accepts as a STRING DELIMITER. PowerShell 7 reads UTF-8 by
# default, so a script can be fine in 7 and broken in 5.1 with nothing in the repo noticing:
#   - #59: deploy.ps1, Sync-LspServer.ps1 and Update-Version.ps1.
#   - ticket 7cf3a895: installer\build-installer.ps1.
#   - tests\Run-Tests.ps1: 22 parse errors, the suite never started.
#   - Check-ReleaseDocs.ps1: ZERO parse errors, and still wrong. A string closed early on line 264
#     and the next one ran to a later em-dash, so the commit classification loop became a string
#     argument to Fail and the check silently did nothing.
#
# WHY A BYTE RULE AND NOT A 5.1 PARSE CHECK. build-installer.ps1 already parses the installer
# scripts in a real 5.1 host, and that is right for its job. As a repo-wide guard, a parse check
# misses the Check-ReleaseDocs shape (it parses) and needs a 5.1 host, so it proves nothing on a
# machine with only PowerShell 7. The byte rule needs neither: it runs the same in 5.1 and 7.
#
# WHY COMMENTS ARE NOT EXEMPT. A non-ASCII character in a comment is harmless in 5.1 today. The
# same character moved into a string by the next edit is not, and nothing would say so. A BOM costs
# 3 bytes and makes the whole file safe, so the rule is categorical: any byte >= 0x80, BOM required.
# A pure-ASCII script needs no BOM (Check-BomFreeWrites.ps1 keeps itself that way on purpose).
# With a BOM the file is accepted as it is: whether its bytes are valid UTF-8 is not this check.
#
# SCOPE: tracked and untracked-but-not-ignored *.ps1 / *.psm1 / *.psd1, any case (git ls-files),
# so a new script is caught before it is committed, and node_modules, bin and obj stay out.
#
# PROVES IT CAN FAIL: before the real scan, the verdict function runs over in-memory fixtures
# (known-bad and known-good byte shapes) and every one must classify as expected; so do the git
# path unquoting and the file-name filter. The listing must include tests\Run-Tests.ps1, or the scan
# cannot be trusted to have seen the repo.
#
# Fix a reported file that is UTF-8 (keeps the text and line endings, adds the BOM). Use a FULL
# path: [IO.File] resolves a relative one against the process directory, not the PowerShell location.
# A file saved in CP1252 instead must be re-encoded first, or its non-ASCII bytes become U+FFFD.
#   $p = '<full path>'; [IO.File]::WriteAllText($p, [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8), (New-Object Text.UTF8Encoding $true))
#
# Pure ASCII, so this file needs no BOM itself.
# Exit: 0 pass, 1 fail, 2 could-not-run.

param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..\..')
)

$ErrorActionPreference = 'Stop'

# ISO-8859-1 maps every byte to the char with the same code, so a regex over this string is a
# byte scan, and the match index is the byte offset.
$latin1 = [Text.Encoding]::GetEncoding(28591)

# $null when the bytes are safe for 5.1, otherwise the reason.
function Get-BomVerdict([byte[]]$b) {
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { return $null }  # UTF-8 BOM
    if ($b.Length -ge 2 -and (($b[0] -eq 0xFF -and $b[1] -eq 0xFE) -or ($b[0] -eq 0xFE -and $b[1] -eq 0xFF))) { return $null }  # UTF-16 BOM
    $s = $latin1.GetString($b)
    $m = [regex]::Match($s, '[^\x00-\x7F]')
    if (-not $m.Success) { return $null }
    $line = 1 + ([regex]::Matches($s.Substring(0, $m.Index), "`n")).Count
    return ('non-ASCII byte 0x{0:X2} on line {1}, and no BOM' -f [int][char]$s[$m.Index], $line)
}

# ---- self-test: the verdict must still tell the shapes apart ----
$utf8NoBom = New-Object Text.UTF8Encoding $false
$emDash = [string][char]0x2014
# Each byte expression is parenthesised: the comma binds tighter than +, so an unparenthesised
# concatenation would split across the fixture's elements (the self-test caught exactly that).
$bom8 = [byte[]](0xEF,0xBB,0xBF)
$fixtures = @(
    ,@('pure ASCII, no BOM',                    ($utf8NoBom.GetBytes("Write-Host 'ok'`r`n")),                        $false)
    ,@('pure ASCII, with BOM',                  ($bom8 + $utf8NoBom.GetBytes("Write-Host 'ok'")),                     $false)
    ,@('empty file',                            ([byte[]]@()),                                                       $false)
    ,@('BOM only',                              ($bom8),                                                             $false)
    ,@('em-dash in a string, with BOM',         ($bom8 + $utf8NoBom.GetBytes("Write-Host `"a $emDash b`"")),          $false)
    ,@('UTF-16 LE with BOM',                    ([Text.Encoding]::Unicode.GetPreamble() + [Text.Encoding]::Unicode.GetBytes("'a $emDash b'")), $false)
    ,@('UTF-16 BE with BOM',                    ([Text.Encoding]::BigEndianUnicode.GetPreamble() + [Text.Encoding]::BigEndianUnicode.GetBytes("'a $emDash b'")), $false)
    ,@('em-dash in a string, no BOM',           ($utf8NoBom.GetBytes("Write-Host `"a $emDash b`"")),                  $true)
    ,@('em-dash in a comment only, no BOM',     ($utf8NoBom.GetBytes("# a $emDash b`r`nWrite-Host 'ok'")),           $true)
    ,@('non-ASCII on a later line, no BOM',     ($utf8NoBom.GetBytes("'ok'`r`n'ok'`r`n'" + [char]0xE6 + "'")),       $true)
    ,@('CP1252 byte (not UTF-8), no BOM',       ([byte[]](0x27,0x94,0x27)),                                          $true)
    ,@('BOM bytes not at the start',            ($utf8NoBom.GetBytes("'a'") + $bom8),                                $true)
)
foreach ($f in $fixtures) { if ($f.Count -ne 3) { Write-Host "  [FAIL] self-test: fixture '$($f[0])' has $($f.Count) elements, not 3" -ForegroundColor Red; exit 1 } }
$selfFail = 0
foreach ($f in $fixtures) {
    $red = $null -ne (Get-BomVerdict $f[1])
    if ($red -ne $f[2]) {
        Write-Host ("  [FAIL] self-test: {0} -> {1}, expected {2}" -f $f[0], $(if ($red) { 'red' } else { 'green' }), $(if ($f[2]) { 'red' } else { 'green' })) -ForegroundColor Red
        $selfFail++
    }
}
$later = Get-BomVerdict $fixtures[9][1]
if ($later -notmatch 'line 3') { Write-Host "  [FAIL] self-test: the line number is wrong: $later" -ForegroundColor Red; $selfFail++ }

# git ls-files C-quotes a path with non-ASCII or special characters ("n\303\246.ps1"), and with
# the default core.quotePath every such byte is an octal escape, so git's output is pure ASCII and
# the console encoding never matters. Undo the quoting here; a quoted name used as a path would make
# Test-Path throw (5.1) or come back $false, and that file would never be scanned.
function ConvertFrom-GitPath([string]$s) {
    if ($s.Length -lt 2 -or $s[0] -ne '"' -or $s[$s.Length - 1] -ne '"') { return $s }
    $bytes = New-Object System.Collections.Generic.List[byte]
    $simple = @{ 'a' = 7; 'b' = 8; 't' = 9; 'n' = 10; 'v' = 11; 'f' = 12; 'r' = 13; '"' = 34; '\' = 92 }
    $i = 1
    while ($i -lt $s.Length - 1) {
        $c = $s[$i]
        if ($c -ne '\') { $bytes.Add([byte][char]$c); $i++; continue }
        $n = [string]$s[$i + 1]
        if ($n -match '^[0-7]$') { $bytes.Add([Convert]::ToByte($s.Substring($i + 1, 3), 8)); $i += 4 }
        elseif ($simple.ContainsKey($n)) { $bytes.Add([byte]$simple[$n]); $i += 2 }
        else { $bytes.Add([byte][char]$c); $i++ }
    }
    return [Text.Encoding]::UTF8.GetString($bytes.ToArray())
}
$ae = [string][char]0xE6
foreach ($q in @(@('plain/name.ps1', 'plain/name.ps1'), @('"n\303\246.ps1"', "n$ae.ps1"), @('"a\"b\\c.ps1"', 'a"b\c.ps1'), @('"tab\there.ps1"', "tab`there.ps1"))) {
    $got = ConvertFrom-GitPath $q[0]
    if ($got -cne $q[1]) { Write-Host "  [FAIL] self-test: git path $($q[0]) unquoted as '$got'" -ForegroundColor Red; $selfFail++ }
}
$scriptName = '\.ps[dm]?1$'
foreach ($n in 'a.ps1', 'UP.PS1', 'm.Psm1', 'd.psd1') { if ($n -notmatch $scriptName) { Write-Host "  [FAIL] self-test: $n is not taken as a script" -ForegroundColor Red; $selfFail++ } }
foreach ($n in 'a.ps1.txt', 'b.ps2', 'c.ps1xml', 'ps1') { if ($n -match $scriptName) { Write-Host "  [FAIL] self-test: $n is taken as a script" -ForegroundColor Red; $selfFail++ } }
if ($selfFail -gt 0) { Write-Host "FAIL - the self-test failed, so a PASS from the scan would mean nothing" -ForegroundColor Red; exit 1 }
Write-Host "  [ok]   self-test: all $($fixtures.Count) byte shapes classified as expected; git path unquoting and the name filter work" -ForegroundColor Green

# ---- the real scan ----
if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) { Write-Host "COULD NOT RUN: no folder at $RepoRoot" -ForegroundColor Red; exit 2 }
$root = (Resolve-Path -LiteralPath $RepoRoot).Path
# Every file, filtered here with a case-insensitive -match: a git pathspec like '*.ps1' is case-
# sensitive and would miss UP.PS1. try/catch: under 'Stop', 5.1 turns a native command's stderr
# line into a terminating error.
$listed = $null
try { $listed = & git -C $root ls-files --cached --others --exclude-standard 2>$null } catch { $listed = $null }
if ($LASTEXITCODE -ne 0 -or $null -eq $listed) { Write-Host "COULD NOT RUN: git ls-files failed or listed nothing in $root" -ForegroundColor Red; exit 2 }
$files = @($listed | Where-Object { $_ } | ForEach-Object { ConvertFrom-GitPath $_ } |
           Where-Object { $_ -match $scriptName } | Sort-Object -Unique)
$must = 'ClarionAssistant/tests/Run-Tests.ps1'
if ($files -notcontains $must) { Write-Host "COULD NOT RUN: the listing of $root does not include $must, so it cannot be trusted" -ForegroundColor Red; exit 2 }

$bad = New-Object System.Collections.Generic.List[string]
$scanned = 0
$missing = 0
foreach ($rel in $files) {
    $p = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { $missing++; continue }  # deleted in the working tree
    $scanned++
    $why = Get-BomVerdict ([IO.File]::ReadAllBytes($p))
    if ($why) { $bad.Add("$rel : $why") }
}
$note = ''
if ($missing -gt 0) { $note = " ($missing listed by git but not on disk, not scanned)" }

if ($bad.Count -gt 0) {
    Write-Host "FAIL - $($bad.Count) of $scanned script(s) would be misread by Windows PowerShell 5.1$note`:" -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "  Save each as UTF-8 WITH BOM (one-liner in this file's header), or make it pure ASCII." -ForegroundColor Yellow
    exit 1
}
Write-Host "PASS - $scanned PowerShell scripts: each is pure ASCII or starts with a BOM$note" -ForegroundColor Green
exit 0
