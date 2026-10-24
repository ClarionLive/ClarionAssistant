# Configure.SharedPluginTest.ps1 - GH #214: configure.ps1's "Plugin location: Shared" mode.
#
# Runs the REAL configure.ps1 under the REAL powershell.exe 5.1 (what the installer uses), with:
#   - a sandboxed USERPROFILE / APPDATA / LOCALAPPDATA under %TEMP%, holding fake Claude profiles;
#   - a FAKE claude CLI (APPDATA\npm\claude.cmd, the first place Find-ClaudeCli looks) that logs every
#     call with the CLAUDE_CONFIG_DIR it saw, and keeps a known_marketplaces.json per profile the way
#     `claude plugin marketplace add/remove` does;
#   - a LOCAL bare git repo standing in for the GitHub marketplace (-MarketplaceRepoUrl);
#   - a throwaway registry key HKCU:\Software\ClarionAssistantTest-<guid> (-RegistryKey), deleted at the
#     end. The real HKCU\Software\ClarionAssistant and the real profiles are never touched.
#
# EXIT CODES   0 pass   1 failed   2 could not run (no powershell.exe 5.1, or no git)

$ErrorActionPreference = 'Stop'

$RepoInstaller = Split-Path -Parent $PSScriptRoot
$Configure     = if ($env:CA_CONFIGURE_UNDER_TEST) { $env:CA_CONFIGURE_UNDER_TEST } else { Join-Path $RepoInstaller 'configure.ps1' }
$PS51          = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$Work          = Join-Path $env:TEMP ('ca-sharedplugin-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$RegKey        = 'HKCU:\Software\ClarionAssistantTest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)

$script:failures = @()
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("  PASS  " + $name) -ForegroundColor DarkGray }
    else {
        Write-Host ("  FAIL  " + $name + "   " + $detail) -ForegroundColor Red
        $script:failures += $name
    }
}
function CannotRun([string]$why) {
    Write-Host "COULD NOT RUN: $why" -ForegroundColor Yellow
    if (Test-Path $Work) { Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue }
    exit 2
}

Write-Host "installer\configure.ps1 - shared plugin location (GH #214)"
if (-not (Test-Path $Configure)) { CannotRun "configure.ps1 not found at $Configure" }
if (-not (Test-Path $PS51))      { CannotRun "Windows PowerShell 5.1 not found at $PS51" }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { CannotRun "git not on PATH (needed for the local stand-in marketplace repo)" }

$Utf8NoBom = New-Object System.Text.UTF8Encoding $false
function WriteText([string]$path, [string]$text) {
    $d = Split-Path -Parent $path
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    [System.IO.File]::WriteAllText($path, $text, $Utf8NoBom)
}

# --- sandbox layout ---------------------------------------------------------------------------------
# Long-name form: $env:TEMP is often 8.3 (JOHNHI~1), and .NET Framework's GetFullPath - which configure.ps1
# uses under 5.1 - expands it, so every path compared below must be in the expanded form too.
New-Item -ItemType Directory -Path $Work -Force | Out-Null
$Work = (& $PS51 -NoProfile -Command "[System.IO.Path]::GetFullPath('$Work')").Trim()
$User     = Join-Path $Work 'Users\Some One'          # a space, like a real account name
$Roaming  = Join-Path $User 'AppData\Roaming'
$Local    = Join-Path $User 'AppData\Local'
$Default  = Join-Path $User '.claude'
$WorkProf = Join-Path $User '.claude-work'
$Junk     = Join-Path $User '.claude-notaprofile'      # no settings.json, no plugins: must be skipped
$Elsewhere = Join-Path $Work 'Profiles\client-x'       # named explicitly via -ProfileDirs
$Github   = Join-Path $User '.claude-github'           # previously installed from GitHub
$Shared   = Join-Path $Local 'ClarionLive\claude-plugins\marketplaces\clarionassistant-marketplace'
$CallLog  = Join-Path $Work 'claude-calls.log'

foreach ($d in $Default, $WorkProf, $Junk, $Elsewhere, $Github, $Roaming, $Local) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
WriteText (Join-Path $Default 'settings.json') '{ "model": "opus" }'
WriteText (Join-Path $WorkProf 'settings.json') '{ "model": "sonnet" }'
WriteText (Join-Path $Github 'settings.json') '{}'
WriteText (Join-Path $Github 'plugins\known_marketplaces.json') ('{ "clarionassistant-marketplace": { "source": { "source": "github", "repo": "ClarionLive/clarionassistant-marketplace" }, "installLocation": "' + ((Join-Path $Github 'plugins\marketplaces\clarionassistant-marketplace') -replace '\\', '\\') + '" } }')

# --- stand-in marketplace repo ------------------------------------------------------------------------
$Seed = Join-Path $Work 'seed'
$Bare = Join-Path $Work 'marketplace.git'
WriteText (Join-Path $Seed '.claude-plugin\marketplace.json') '{ "name": "clarionassistant-marketplace", "plugins": [ { "name": "clarion-assistant", "source": "./plugins/clarion-assistant" } ] }'
WriteText (Join-Path $Seed 'plugins\clarion-assistant\.claude-plugin\plugin.json') '{ "name": "clarion-assistant", "version": "2.1.0" }'
function G { & git @args 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { CannotRun "git $($args -join ' ') failed" } }
G init --quiet $Seed
G -C $Seed -c user.email=t@t -c user.name=t add -A
G -C $Seed -c user.email=t@t -c user.name=t commit --quiet -m seed
G clone --quiet --bare $Seed $Bare

# --- fake claude CLI ----------------------------------------------------------------------------------
$Npm = Join-Path $Roaming 'npm'
New-Item -ItemType Directory -Path $Npm -Force | Out-Null
WriteText (Join-Path $Npm 'claude.cmd') "@powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0fake-claude.ps1`" %*`r`n"
WriteText (Join-Path $Npm 'fake-claude.ps1') @'
$cfg = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
$who = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { '(unset)' }
[System.IO.File]::AppendAllText($env:FAKE_CLAUDE_LOG, ($who + '|' + ($args -join ' ') + "`r`n"))
$known = Join-Path $cfg 'plugins\known_marketplaces.json'
$map = @{}
if (Test-Path $known) {
    $j = [System.IO.File]::ReadAllText($known) | ConvertFrom-Json
    foreach ($p in $j.PSObject.Properties) { $map[$p.Name] = $p.Value }
}
function Save { New-Item -ItemType Directory -Path (Split-Path $known) -Force | Out-Null
                [System.IO.File]::WriteAllText($known, ($map | ConvertTo-Json -Depth 5)) }
if ($args[0] -eq 'plugin' -and $args[1] -eq 'marketplace' -and $args[2] -eq 'add') {
    $src = $args[3]
    if (-not (Test-Path (Join-Path $src '.claude-plugin\marketplace.json'))) { Write-Output "not a marketplace: $src"; exit 1 }
    if ($map.ContainsKey('clarionassistant-marketplace')) { Write-Output 'already exists'; exit 1 }
    $map['clarionassistant-marketplace'] = @{ source = @{ source = 'directory'; path = $src }; installLocation = $src }
    Save; exit 0
}
if ($args[0] -eq 'plugin' -and $args[1] -eq 'marketplace' -and $args[2] -eq 'remove') {
    $map.Remove($args[3]); Save; exit 0
}
if ($args[0] -eq 'plugin' -and $args[1] -eq 'install') {
    if (-not $map.ContainsKey('clarionassistant-marketplace')) { Write-Output 'marketplace not found'; exit 1 }
    exit 0
}
exit 0
'@

# --- run configure.ps1 in the sandbox -----------------------------------------------------------------
function RunConfigure([string[]]$ScriptArgs) {
    $saved = @{ USERPROFILE = $env:USERPROFILE; APPDATA = $env:APPDATA; LOCALAPPDATA = $env:LOCALAPPDATA; CLAUDE_CONFIG_DIR = $env:CLAUDE_CONFIG_DIR }
    try {
        $env:USERPROFILE = $User; $env:APPDATA = $Roaming; $env:LOCALAPPDATA = $Local
        Remove-Item Env:\CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
        $env:FAKE_CLAUDE_LOG = $CallLog
        if (Test-Path $CallLog) { Remove-Item $CallLog -Force }
        $out = & $PS51 -NoProfile -ExecutionPolicy Bypass -File $Configure -RegistryKey $RegKey -MarketplaceRepoUrl $Bare @ScriptArgs 2>&1
        $script:lastOut = ($out | ForEach-Object { "$_" }) -join "`n"
        $script:lastExit = $LASTEXITCODE
    } finally {
        foreach ($k in $saved.Keys) {
            if ($null -eq $saved[$k]) { Remove-Item "Env:\$k" -ErrorAction SilentlyContinue } else { Set-Item "Env:\$k" $saved[$k] }
        }
    }
    if (Test-Path $CallLog) { return @([System.IO.File]::ReadAllLines($CallLog)) }
    return @()
}
function Reg([string]$name) { try { (Get-ItemProperty -Path $RegKey -Name $name -ErrorAction Stop).$name } catch { $null } }
function Calls($log, [string]$who, [string]$pattern) { @($log | Where-Object { $_ -like ($who + '|' + $pattern) }) }
function KnownSource([string]$profileDir) {
    $k = Join-Path $profileDir 'plugins\known_marketplaces.json'
    if (-not (Test-Path $k)) { return $null }
    $e = ([System.IO.File]::ReadAllText($k) | ConvertFrom-Json).'clarionassistant-marketplace'
    if ($e) { return $e.source.path } else { return $null }
}

try {
    # ===== 1. fresh shared install ======================================================================
    Write-Host ""
    Write-Host "1. fresh shared install"
    $log = RunConfigure @('-InstallPlugin', '-PluginLocation', 'Shared', '-SharedPluginPath', '%LOCALAPPDATA%\ClarionLive\claude-plugins\marketplaces\clarionassistant-marketplace', '-ProfileDirs', $Elsewhere)
    Check 'configure.ps1 exits 0' ($lastExit -eq 0) "exit $lastExit`n$lastOut"
    Check 'shared folder cloned (marketplace.json present)' (Test-Path (Join-Path $Shared '.claude-plugin\marketplace.json')) $lastOut
    Check 'shared folder is a git clone (updatable)' (Test-Path (Join-Path $Shared '.git'))
    Check 'registry PluginMarketplacePath = shared folder (%LOCALAPPDATA% expanded)' ((Reg 'PluginMarketplacePath') -eq $Shared) "got '$(Reg 'PluginMarketplacePath')'"
    Check 'registry PluginMarketplaceCreated = 1' ((Reg 'PluginMarketplaceCreated') -eq '1') "got '$(Reg 'PluginMarketplaceCreated')'"

    Check 'default profile: add <shared> run with CLAUDE_CONFIG_DIR UNSET' ((Calls $log '(unset)' "plugin marketplace add $Shared").Count -eq 1) ($log -join ' ; ')
    Check 'default profile: plugin install --scope user' ((Calls $log '(unset)' 'plugin install clarion-assistant@clarionassistant-marketplace --scope user').Count -eq 1) ($log -join ' ; ')
    Check 'default profile never run with CLAUDE_CONFIG_DIR=<default>' ((Calls $log $Default '*').Count -eq 0) ($log -join ' ; ')
    Check '.claude-work found automatically and registered' ((Calls $log $WorkProf "plugin marketplace add $Shared").Count -eq 1) ($log -join ' ; ')
    Check '-ProfileDirs folder registered' ((Calls $log $Elsewhere "plugin marketplace add $Shared").Count -eq 1) ($log -join ' ; ')
    Check '.claude-* folder that is not a profile skipped' ((Calls $log $Junk '*').Count -eq 0) ($log -join ' ; ')
    Check 'GitHub-installed profile: old registration removed first' ((Calls $log $Github 'plugin marketplace remove clarionassistant-marketplace').Count -eq 1) ($log -join ' ; ')
    Check 'GitHub-installed profile: now points at the shared folder' ((KnownSource $Github) -eq $Shared) "got '$(KnownSource $Github)'"
    Check 'no profile cloned from GitHub (no marketplace add of the repo)' (@($log | Where-Object { $_ -like '*marketplace add ClarionLive/*' }).Count -eq 0) ($log -join ' ; ')
    Check 'exactly one marketplace copy (none under any profile)' (@(Get-ChildItem $User -Recurse -Force -Filter marketplace.json -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notlike "$Shared*" }).Count -eq 0)
    $profilesRec = "$(Reg 'PluginMarketplaceProfiles')"
    Check 'registered profiles recorded for uninstall (4)' (@($profilesRec.Split(';') | Where-Object { $_ }).Count -eq 4) "got '$profilesRec'"

    # ===== 2. re-run (upgrade): idempotent, and the ONE shared copy is updated ==========================
    Write-Host ""
    Write-Host "2. re-run / upgrade"
    WriteText (Join-Path $Seed 'plugins\clarion-assistant\NEW-IN-UPDATE.txt') 'v2'
    G -C $Seed -c user.email=t@t -c user.name=t add -A
    G -C $Seed -c user.email=t@t -c user.name=t commit --quiet -m update
    G -C $Seed push --quiet $Bare HEAD
    $log = RunConfigure @('-InstallPlugin', '-PluginLocation', 'Shared', '-SharedPluginPath', $Shared, '-ProfileDirs', $Elsewhere)
    Check 'configure.ps1 exits 0' ($lastExit -eq 0) "exit $lastExit"
    Check 'shared copy fast-forwarded (update visible)' (Test-Path (Join-Path $Shared 'plugins\clarion-assistant\NEW-IN-UPDATE.txt')) $lastOut
    Check 're-run: no marketplace add (already registered)' (@($log | Where-Object { $_ -like '*marketplace add*' }).Count -eq 0) ($log -join ' ; ')
    Check 're-run: no marketplace remove' (@($log | Where-Object { $_ -like '*marketplace remove*' }).Count -eq 0) ($log -join ' ; ')
    Check 're-run: plugin install re-run in each profile' (@($log | Where-Object { $_ -like '*plugin install*' }).Count -eq 4) ($log -join ' ; ')
    Check 're-run: still 4 recorded profiles' (@("$(Reg 'PluginMarketplaceProfiles')".Split(';') | Where-Object { $_ }).Count -eq 4) "$(Reg 'PluginMarketplaceProfiles')"

    # ===== 3. upgrade choosing 'Profile' again: never silently moves back ===============================
    Write-Host ""
    Write-Host "3. re-run choosing the default profile"
    $log = RunConfigure @('-InstallPlugin')
    Check 'no remove/add against the default profile' (@($log | Where-Object { $_ -like '*marketplace*' }).Count -eq 0) ($log -join ' ; ')
    Check 'shared path still recorded' ((Reg 'PluginMarketplacePath') -eq $Shared) "got '$(Reg 'PluginMarketplacePath')'"
    Check 'says it left the shared setup unchanged' ($lastOut -match 'left unchanged') $lastOut

    # ===== 4. a shared path INSIDE a profile is refused ==================================================
    Write-Host ""
    Write-Host "4. shared path inside a profile"
    $savedPath = Reg 'PluginMarketplacePath'
    $bad = Join-Path $WorkProf 'plugins\shared'
    $log = RunConfigure @('-InstallPlugin', '-PluginLocation', 'Shared', '-SharedPluginPath', $bad)
    Check 'refused (warning printed)' ($lastOut -match 'inside a Claude profile') $lastOut
    Check 'nothing cloned there' (-not (Test-Path (Join-Path $bad '.claude-plugin')))
    Check 'registry not pointed at it' ((Reg 'PluginMarketplacePath') -eq $savedPath) "got '$(Reg 'PluginMarketplacePath')'"

    # ===== 5. uninstall ==================================================================================
    Write-Host ""
    Write-Host "5. uninstall"
    $log = RunConfigure @('-UninstallPlugin')
    Check 'configure.ps1 exits 0' ($lastExit -eq 0) "exit $lastExit"
    foreach ($p in @(@{ N = 'default'; W = '(unset)' }, @{ N = 'work'; W = $WorkProf }, @{ N = 'explicit'; W = $Elsewhere }, @{ N = 'github'; W = $Github })) {
        Check ("uninstall: de-registered from " + $p.N) ((Calls $log $p.W 'plugin marketplace remove clarionassistant-marketplace').Count -eq 1) ($log -join ' ; ')
    }
    Check 'uninstall: no dangling registration in any profile' ((@($Default, $WorkProf, $Elsewhere, $Github) | Where-Object { KnownSource $_ }).Count -eq 0)
    Check 'uninstall: shared folder (created by us) deleted' (-not (Test-Path $Shared))
    Check 'uninstall: registry values removed' (($null -eq (Reg 'PluginMarketplacePath')) -and ($null -eq (Reg 'PluginMarketplaceProfiles')) -and ($null -eq (Reg 'PluginMarketplaceCreated')))

    # ===== 6. a shared folder we did NOT create is never deleted =========================================
    Write-Host ""
    Write-Host "6. pre-existing shared folder"
    $Mine = Join-Path $Work 'MyOwnCopy\clarionassistant-marketplace'
    G clone --quiet $Bare $Mine
    $log = RunConfigure @('-InstallPlugin', '-PluginLocation', 'Shared', '-SharedPluginPath', $Mine)
    Check 'existing folder adopted' ((Reg 'PluginMarketplacePath') -eq $Mine) "got '$(Reg 'PluginMarketplacePath')'"
    Check 'not marked as created by the installer' ($null -eq (Reg 'PluginMarketplaceCreated')) "got '$(Reg 'PluginMarketplaceCreated')'"
    $log = RunConfigure @('-UninstallPlugin')
    Check 'uninstall keeps a folder it did not create' (Test-Path (Join-Path $Mine '.claude-plugin\marketplace.json'))

    # ===== 7. sync-marketplace-to-profile.ps1 defaults -Dest to the shared folder ======================
    Write-Host ""
    Write-Host "7. sync-marketplace-to-profile.ps1"
    $Sync = Join-Path $RepoInstaller 'sync-marketplace-to-profile.ps1'
    $SyncDest = Join-Path $Work 'SyncShared\clarionassistant-marketplace'
    New-Item -Path $RegKey -Force | Out-Null
    New-ItemProperty -Path $RegKey -Name 'PluginMarketplacePath' -Value $SyncDest -PropertyType String -Force | Out-Null
    $savedUp = $env:USERPROFILE
    try {
        $env:USERPROFILE = $User
        & $PS51 -NoProfile -ExecutionPolicy Bypass -File $Sync -Source $Seed -RegistryKey $RegKey *> $null
        $syncExit = $LASTEXITCODE
    } finally { $env:USERPROFILE = $savedUp }
    Check 'sync: exits 0' ($syncExit -eq 0) "exit $syncExit"
    Check 'sync: mirrored into the registry shared folder' (Test-Path (Join-Path $SyncDest '.claude-plugin\marketplace.json'))
    Check 'sync: default profile left alone' (-not (Test-Path (Join-Path $Default 'plugins\marketplaces\clarionassistant-marketplace')))

    # ===== 8. 5.1 parse ==================================================================================
    $parseOut = & $PS51 -NoProfile -Command "`$e=`$null; [void][System.Management.Automation.Language.Parser]::ParseFile('$Configure',[ref]`$null,[ref]`$e); `$e.Count"
    Check 'configure.ps1 parses under 5.1' ([int]$parseOut -eq 0) "$parseOut parse error(s)"
}
finally {
    Remove-Item -Path $RegKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:failures.Count -eq 0) {
    Write-Host "configure.ps1 shared plugin location: PASSED" -ForegroundColor Green
    exit 0
}
Write-Host "configure.ps1 shared plugin location: $($script:failures.Count) FAILED" -ForegroundColor Red
exit 1
