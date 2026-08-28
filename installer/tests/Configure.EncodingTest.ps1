# Configure.EncodingTest.ps1 - regression test for installer\configure.ps1.
#
# WHY THIS EXISTS
# ---------------
# Twice now the installer has damaged users' global Claude Code settings.json, and both times the
# root cause was a Windows PowerShell 5.1 default that reads as correct source:
#
#   GH #190  ConvertFrom-Json -AsHashtable  - a 6.0+ switch, so it threw on EVERY machine, the catch
#            mistook its own unsupported API call for a corrupt user file, and settings.json was
#            rebuilt from an empty hashtable. Hooks, model, statusLine, enabledPlugins: gone.
#   GH #200  Get-Content with no -Encoding decodes as ANSI on 5.1, and Set-Content -Encoding UTF8
#            means WITH BOM on 5.1 (BOM-less on 7.x). Every non-ASCII character was mangled on the
#            way in and written back mangled, with a BOM added, and no backup kept.
#
# The installer runs configure.ps1 with `powershell.exe`, which on Windows is ALWAYS 5.1, never
# pwsh. So a change that behaves perfectly in a 7.x terminal can still destroy every user's config.
# Nothing else in this repo exercises configure.ps1 at all.
#
# WHAT IT DOES
# ------------
# Runs the REAL configure.ps1 under the REAL powershell.exe 5.1, exactly as setup.iss invokes it,
# against a sandboxed USERPROFILE/APPDATA. It asserts on BYTES, not on appearance.
#
# It never touches the developer's own profile, and it deliberately does NOT pass -ClarionRoot,
# because that argument makes configure.ps1 write CLARION_ROOT to the user's environment. The cost
# is that the .env CLARION_PATH line is not covered; CLARIONCOM_HOME, which is the key built from
# %APPDATA% and therefore the one that carries a non-ASCII account name, is covered.
#
# This file is deliberately pure ASCII: every non-ASCII test character is built from its codepoint
# via [char]. The harness's own encoding must never be a confound - that is the bug under test.
#
# EXIT CODES   0 pass   1 failed   2 could not run (missing host, or the defect cannot reproduce
#                                  on this machine - see the negative control below)

$ErrorActionPreference = 'Stop'

$RepoInstaller = Split-Path -Parent $PSScriptRoot            # ...\installer
$Configure     = Join-Path $RepoInstaller 'configure.ps1'
$PS51          = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$Work          = Join-Path $env:TEMP ('ca-configure-test-' + [Guid]::NewGuid().ToString('N').Substring(0,8))

$script:failures = @()
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("  PASS  " + $name) -ForegroundColor DarkGray }
    else {
        Write-Host ("  FAIL  " + $name + "   " + $detail) -ForegroundColor Red
        $script:failures += $name
    }
}

function CannotRun([string]$why) {
    Write-Host ""
    Write-Host "COULD NOT RUN: $why" -ForegroundColor Yellow
    Write-Host "  Treating as not-a-pass. See the exit-code note at the top of this file." -ForegroundColor Yellow
    if (Test-Path $Work) { Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue }
    exit 2
}

Write-Host "installer\configure.ps1 - encoding and backup regression (GH #190, GH #200)"

if (-not (Test-Path $Configure)) { CannotRun "configure.ps1 not found at $Configure" }
if (-not (Test-Path $PS51))      { CannotRun "Windows PowerShell 5.1 not found at $PS51" }

# --- test data, built from codepoints so this file stays ASCII ---------------
$RSQ = [char]0x2019    # RIGHT SINGLE QUOTATION MARK   utf8 e2 80 99   <- the character in GH #200
$EAC = [char]0x00E9    # LATIN SMALL LETTER E ACUTE    utf8 c3 a9
$EMD = [char]0x2014    # EM DASH                       utf8 e2 80 94

$MARKER   = 'Peter' + $RSQ + 's caf' + $EAC + ' ' + $EMD + ' ready'
$ADLEAF   = 'Roaming' + $EAC          # stands in for a non-ASCII Windows account name
$USERVAL  = 'keep' + $EAC + 'me'
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false

$SEEDJSON = '{
  "model": "opus",
  "systemMessage": "' + $MARKER + '",
  "statusLine": { "type": "command", "command": "echo hi" },
  "hooks": { "Stop": [ { "matcher": "", "hooks": [ { "type": "command", "command": "x" } ] } ] },
  "permissions": { "allow": [ "Bash(ls:*)" ] },
  "env": { "MY_OWN": "1" }
}'

function HasBom([byte[]]$b) {
    return ($null -ne $b -and $b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}

# ---------------------------------------------------------------------------
# NEGATIVE CONTROL, and it runs FIRST.
#
# The two defective calls, in isolation. If they do NOT mangle on this machine then the whole bug
# class is undetectable here - most likely because Windows' "Use Unicode UTF-8 for worldwide
# language support" beta option is on, making the ANSI codepage 65001 - and every assertion below
# would pass for the wrong reason. A green result we cannot trust is worse than no result, so this
# exits 2 rather than reporting success.
# ---------------------------------------------------------------------------
New-Item -ItemType Directory $Work -Force | Out-Null
$ctlIn  = Join-Path $Work 'control.json'
$ctlOut = Join-Path $Work 'control.out.json'
[System.IO.File]::WriteAllText($ctlIn, ('{"m":"' + $MARKER + '"}'), $Utf8NoBom)

$ctlScript = Join-Path $Work 'control.ps1'
@'
param([string]$In, [string]$Out)
$o = Get-Content $In -Raw | ConvertFrom-Json          # DEFECT 1: ANSI decode on 5.1
Set-Content -Path $Out -Value ($o | ConvertTo-Json) -Encoding UTF8   # DEFECT 2: BOM on 5.1
'@ | Set-Content -Path $ctlScript -Encoding ASCII

& $PS51 -NoProfile -ExecutionPolicy Bypass -File $ctlScript -In $ctlIn -Out $ctlOut | Out-Null

$ctlBytes = [System.IO.File]::ReadAllBytes($ctlOut)
$ctlValue = ([System.IO.File]::ReadAllText($ctlOut) | ConvertFrom-Json).m
$controlMangles = ($ctlValue -ne $MARKER)
$controlAddsBom = (HasBom $ctlBytes)

Write-Host ""
Write-Host "negative control (the two defective calls, unfixed)"
& $PS51 -NoProfile -Command '"  host " + $PSVersionTable.PSVersion.ToString() + ", ANSI codepage " + [System.Text.Encoding]::Default.WebName'
if (-not $controlMangles -or -not $controlAddsBom) {
    CannotRun ("the GH #200 defect does not reproduce on this machine " +
               "(mangles=$controlMangles, addsBom=$controlAddsBom). " +
               "Most likely the system ANSI codepage is UTF-8. Nothing below could fail, so nothing below is evidence.")
}
Write-Host "  control mangles and BOMs as expected - the assertions below can fail" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# Run the real script against a sandboxed profile.
# ---------------------------------------------------------------------------
function Invoke-Configure {
    param([string]$Profile)
    $oldUP = $env:USERPROFILE; $oldAD = $env:APPDATA
    try {
        $env:USERPROFILE = $Profile
        $env:APPDATA     = Join-Path $Profile $ADLEAF
        # Mirrors setup.iss, minus -ClarionRoot/-DocGraphDb (see the header note: those write to
        # the real user environment, which a test must not do).
        & $PS51 -ExecutionPolicy Bypass -File $Configure 2>&1 | Out-Null
    } finally { $env:USERPROFILE = $oldUP; $env:APPDATA = $oldAD }
}

function New-Profile {
    param([string]$Name, [bool]$SeedSettings, [bool]$SeedBom, [bool]$SeedEnv = $false, [bool]$SeedEnvBom = $false)
    $p = Join-Path $Work $Name
    $c = Join-Path $p '.claude'
    New-Item -ItemType Directory $c -Force | Out-Null
    New-Item -ItemType Directory (Join-Path $p $ADLEAF) -Force | Out-Null
    if ($SeedSettings) {
        $f = Join-Path $c 'settings.json'
        [System.IO.File]::WriteAllText($f, $SEEDJSON, (New-Object System.Text.UTF8Encoding $SeedBom))
        # FIXTURE SELF-CHECK. A malformed seed sends configure.ps1 down its parse-failure branch,
        # where it correctly refuses to write - and every assertion below would then pass while
        # testing nothing.
        $chk = [System.IO.File]::ReadAllText($f) | ConvertFrom-Json
        if ($chk.systemMessage -ne $MARKER) { throw "FIXTURE BROKEN ($Name): seed marker did not round-trip" }
    }
    if ($SeedEnv) {
        # A .clarioncom.env carrying a key of the user's own. Each line is its own variable on
        # purpose - inlining 'K=' + $USERVAL inside @(...) does NOT concatenate, because comma
        # binds tighter than +, and the stray fragment then has no '=' and is dropped by the merge.
        #
        # The BOM flag matters more than it looks. Get-Content DOES honour a BOM when one is
        # present, so a BOM'd seed decodes correctly even on the defective code path and cannot
        # detect the ANSI-read defect at all. Only a BOM-LESS seed exercises it. Both are seeded
        # below, for that reason.
        $l1 = 'CLARIONCOM_HOME=C:\old'
        $l2 = 'USER_OWN_KEY=' + $USERVAL
        [System.IO.File]::WriteAllLines((Join-Path $p '.clarioncom.env'), [string[]]@($l1, $l2),
                                        (New-Object System.Text.UTF8Encoding $SeedEnvBom))
    }
    return $p
}

function Read-Settings([string]$Profile) {
    $f = Join-Path $Profile '.claude\settings.json'
    if (-not (Test-Path $f)) { return $null }
    return @{
        Bytes  = [System.IO.File]::ReadAllBytes($f)
        Parsed = ([System.IO.File]::ReadAllText($f) | ConvertFrom-Json)
        Backups = @(Get-ChildItem (Join-Path $Profile '.claude') -Filter 'settings.json.backup.*' -EA SilentlyContinue)
    }
}

# --- 1. BOM-less UTF-8 in: characters preserved, no BOM out, backup written ---
Write-Host ""
Write-Host "settings.json, BOM-less UTF-8 input (the common case)"
$p1 = New-Profile 'p1' $true $false
$inBytes = [System.IO.File]::ReadAllBytes((Join-Path $p1 '.claude\settings.json'))
Invoke-Configure $p1
$r1 = Read-Settings $p1
Check 'value round-trips byte-for-byte'  ($r1.Parsed.systemMessage -eq $MARKER) "got '$($r1.Parsed.systemMessage)'"
Check 'no BOM on output'                 (-not (HasBom $r1.Bytes))             "first bytes $([BitConverter]::ToString($r1.Bytes[0..2]))"
# Bind the whole boolean in ONE parenthesised expression. Written as
#   Check 'name' (...).Count -eq 0 'detail'
# PowerShell reads five POSITIONAL arguments, binds $ok to .Count, and the assertion inverts.
$seenKeys    = @($r1.Parsed.PSObject.Properties.Name)
$missingKeys = @(@('model','systemMessage','statusLine','hooks','permissions','env') |
                 Where-Object { $seenKeys -notcontains $_ })
Check 'user keys all preserved'          ($missingKeys.Count -eq 0)            "dropped: $($missingKeys -join ',')"
Check "user's own permission kept"       (@($r1.Parsed.permissions.allow) -contains 'Bash(ls:*)') 'Bash(ls:*) missing'
Check 'Clarion tools added'              (@($r1.Parsed.permissions.allow) -contains 'mcp__clarion-assistant__open_file') 'CA permissions missing'
Check 'backup written on success path'   ($r1.Backups.Count -eq 1)             "found $($r1.Backups.Count)"
if ($r1.Backups.Count -ge 1) {
    $bk = [System.IO.File]::ReadAllBytes($r1.Backups[0].FullName)
    $same = ($bk.Length -eq $inBytes.Length)
    if ($same) { for ($i = 0; $i -lt $bk.Length; $i++) { if ($bk[$i] -ne $inBytes[$i]) { $same = $false; break } } }
    Check 'backup is byte-identical to the original' $same 'backup differs from input'
}

# --- 2. BOM'd input (a file an earlier 5.8 install left behind): BOM stripped ---
Write-Host ""
Write-Host "settings.json, BOM'd input (repairing a 5.8-damaged file)"
$p2 = New-Profile 'p2' $true $true
Invoke-Configure $p2
$r2 = Read-Settings $p2
Check 'BOM removed'                      (-not (HasBom $r2.Bytes))             'BOM still present'
Check 'value still intact'               ($r2.Parsed.systemMessage -eq $MARKER) "got '$($r2.Parsed.systemMessage)'"

# --- 3. Fresh machine: no settings.json to back up, must not throw ---
Write-Host ""
Write-Host "settings.json, fresh machine (nothing to back up)"
$p3 = New-Profile 'p3' $false $false
Invoke-Configure $p3
$r3 = Read-Settings $p3
Check 'settings.json created'            ($null -ne $r3)                       'no file written'
Check 'no BOM'                           ($null -ne $r3 -and -not (HasBom $r3.Bytes)) 'BOM present'
Check 'no spurious backup'               ($null -ne $r3 -and $r3.Backups.Count -eq 0) "found $($r3.Backups.Count)"

# --- 4. Second install over our own output: idempotent, and backs up again ---
Write-Host ""
Write-Host "settings.json, installing twice"
$p4 = New-Profile 'p4' $true $false
Invoke-Configure $p4
Start-Sleep -Milliseconds 1100        # backup filenames are stamped to the second
Invoke-Configure $p4
$r4 = Read-Settings $p4
Check 'value survives a second install'  ($r4.Parsed.systemMessage -eq $MARKER) "got '$($r4.Parsed.systemMessage)'"
Check 'still no BOM after two installs'  (-not (HasBom $r4.Bytes))             'BOM appeared on the second pass'
Check 'a backup per install'             ($r4.Backups.Count -eq 2)             "found $($r4.Backups.Count)"

# --- 5. .clarioncom.env, both encodings of the existing file --------------
function Read-Env([string]$Profile) {
    $f  = Join-Path $Profile '.clarioncom.env'
    $kv = @{}
    foreach ($l in [System.IO.File]::ReadAllLines($f)) {
        if ($l -match '^([^=]+)=(.*)$') { $kv[$matches[1]] = $matches[2] }
    }
    return @{ Bytes = [System.IO.File]::ReadAllBytes($f); Kv = $kv }
}

Write-Host ""
Write-Host ".clarioncom.env, BOM-less existing file (exercises the ANSI-read defect)"
$p5 = New-Profile 'p5' $false $false $true $false
Invoke-Configure $p5
$e5 = Read-Env $p5
Check 'no BOM on .clarioncom.env'        (-not (HasBom $e5.Bytes))             'BOM present'
Check 'leading key name is clean'        ($e5.Kv.ContainsKey('CLARIONCOM_HOME')) "keys: $($e5.Kv.Keys -join ',')"
Check 'non-ASCII %APPDATA% preserved'    ($e5.Kv['CLARIONCOM_HOME'] -eq (Join-Path (Join-Path $p5 $ADLEAF) 'ClarionCOM')) "got '$($e5.Kv['CLARIONCOM_HOME'])'"
Check "user's own key survives merge"    ($e5.Kv['USER_OWN_KEY'] -eq $USERVAL) "got '$($e5.Kv['USER_OWN_KEY'])'"

Write-Host ""
Write-Host ".clarioncom.env, BOM'd existing file (repairing what 5.8 left)"
$p6 = New-Profile 'p6' $false $false $true $true
Invoke-Configure $p6
$e6 = Read-Env $p6
Check "BOM removed from .clarioncom.env" (-not (HasBom $e6.Bytes))             'BOM still present'
Check "user's own key still intact"      ($e6.Kv['USER_OWN_KEY'] -eq $USERVAL) "got '$($e6.Kv['USER_OWN_KEY'])'"

# --- 6. The script itself must parse under 5.1 -------------------------------
Write-Host ""
Write-Host "syntax gate"
$parseOut = & $PS51 -NoProfile -Command (
    '$e=$null;$t=$null;' +
    "[System.Management.Automation.Language.Parser]::ParseFile('$Configure',[ref]`$t,[ref]`$e)|Out-Null;" +
    '@($e).Count')
Check 'configure.ps1 parses under 5.1'   ([int]$parseOut -eq 0)                "$parseOut parse error(s)"

# ---------------------------------------------------------------------------
Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($script:failures.Count -eq 0) {
    Write-Host "configure.ps1 encoding regression: PASSED" -ForegroundColor Green
    exit 0
}
Write-Host "configure.ps1 encoding regression: $($script:failures.Count) FAILED" -ForegroundColor Red
$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
