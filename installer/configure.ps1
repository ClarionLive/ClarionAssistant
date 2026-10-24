# Clarion Assistant - Post-Install Configuration
# Merges Claude Code settings non-destructively
param(
    [string]$ClarionRoot,
    [string]$DocGraphDb,
    # When set, run ONLY the plugin register/install step and skip the settings/env
    # merge. The .iss invokes this a SECOND time with -InstallPlugin under the Inno
    # `runasoriginaluser` flag, so plugin install runs as the ORIGINAL (non-elevated)
    # user -- landing in THEIR profile (where ClarionAssistant reads), not the elevated
    # admin's, and never exec'ing a user-writable CLI from the elevated installer.
    [switch]$InstallPlugin,

    # GH #214 "Plugin location". 'Profile' (the default) = the behaviour above: Claude Code clones the
    # GitHub marketplace into %USERPROFILE%\.claude. 'Shared' = clone it ONCE into -SharedPluginPath,
    # outside every Claude profile, and register that folder as a local marketplace in each profile.
    [ValidateSet('Profile', 'Shared')]
    [string]$PluginLocation = 'Profile',
    [string]$SharedPluginPath = '%LOCALAPPDATA%\ClarionLive\claude-plugins\marketplaces\clarionassistant-marketplace',
    # Extra Claude profile folders to register the shared marketplace in, ';'-separated. Added to the
    # ones found automatically (%USERPROFILE%\.claude, %USERPROFILE%\.claude-* that hold a settings.json
    # or plugins folder, and CLAUDE_CONFIG_DIR).
    [string]$ProfileDirs = '',

    # Uninstall: de-register the shared marketplace from every profile it was registered in, and delete
    # the shared folder if this installer created it. Does nothing in 'Profile' mode installs.
    [switch]$UninstallPlugin,

    # Test hooks (installer\tests\Configure.SharedPluginTest.ps1). Never passed by the installer.
    [string]$MarketplaceRepoUrl = 'https://github.com/ClarionLive/clarionassistant-marketplace.git',
    [string]$RegistryKey = 'HKCU:\Software\ClarionAssistant'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------
# GH #214: shared, profile-independent marketplace.
#
# WHERE THE CHOICE IS REMEMBERED: HKCU\Software\ClarionAssistant, value PluginMarketplacePath. The add-in
# (Services\ClaudePluginLocator.cs) reads it to build --plugin-dir, so every CA-launched session uses the
# shared copy whatever profile it runs under. HKCU because the plugin is per user, and because this step
# runs as the ORIGINAL user (runasoriginaluser), so it lands in the hive the add-in reads - unlike the
# elevated half of setup. Beside it: PluginMarketplaceProfiles (where it was registered, for uninstall)
# and PluginMarketplaceCreated (1 = this installer cloned the folder, so uninstall may delete it).
#
# HOW OTHER PROFILES SEE IT: `claude plugin marketplace add <folder>` run with CLAUDE_CONFIG_DIR set to
# that profile - the documented way to add a local-directory marketplace - then `claude plugin install
# --scope user`. Plain `claude` sessions in that profile then load the plugin too. We never hand-edit a
# profile's plugin files; Claude Code owns those.
#
# The DEFAULT profile is registered with CLAUDE_CONFIG_DIR REMOVED, not set to %USERPROFILE%\.claude:
# with the variable set, Claude Code also moves its global .claude.json into that folder, so pointing it
# at the default folder explicitly is not the same as leaving it unset.
# ---------------------------------------------------------------------------------------------------
$script:MarketplaceName = 'clarionassistant-marketplace'
$script:PluginRef       = "clarion-assistant@$($script:MarketplaceName)"

function Expand-CaPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $p = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"').Trim())
    if (-not [System.IO.Path]::IsPathRooted($p)) { return $null }
    $p = [System.IO.Path]::GetFullPath($p)
    if ($p.Length -gt 3) { $p = $p.TrimEnd('\', '/') }
    return $p
}

function Test-SamePath([string]$A, [string]$B) {
    $a1 = Expand-CaPath $A; $b1 = Expand-CaPath $B
    return ($null -ne $a1 -and $null -ne $b1 -and [string]::Equals($a1, $b1, [StringComparison]::OrdinalIgnoreCase))
}

# True when the path is, or is inside, a Claude profile folder (a ".claude" or ".claude-*" segment). The
# shared copy must live OUTSIDE every profile - otherwise it is just another per-profile clone.
function Test-InsideClaudeProfile([string]$Path) {
    $p = Expand-CaPath $Path
    if (-not $p) { return $false }
    foreach ($seg in $p.Split('\')) {
        if ($seg -ieq '.claude' -or $seg -like '.claude-*') { return $true }
    }
    return $false
}

# The Claude profile folders on this machine: the default one first, then CLAUDE_CONFIG_DIR, then any
# %USERPROFILE%\.claude-* folder that looks like a profile (holds settings.json or a plugins folder), then
# the -ProfileDirs the developer named. Deduplicated, case-insensitively.
function Get-ClaudeProfiles([string]$Extra) {
    $list = New-Object System.Collections.ArrayList
    $add = {
        param($p)
        $x = Expand-CaPath $p
        if (-not $x) { return }
        foreach ($have in $list) { if ([string]::Equals($have, $x, [StringComparison]::OrdinalIgnoreCase)) { return } }
        [void]$list.Add($x)
    }
    & $add (Join-Path $env:USERPROFILE '.claude')
    if ($env:CLAUDE_CONFIG_DIR) { & $add $env:CLAUDE_CONFIG_DIR }
    Get-ChildItem -LiteralPath $env:USERPROFILE -Directory -Force -Filter '.claude-*' -ErrorAction SilentlyContinue |
        Sort-Object Name |
        Where-Object { (Test-Path (Join-Path $_.FullName 'settings.json')) -or (Test-Path (Join-Path $_.FullName 'plugins')) } |
        ForEach-Object { & $add $_.FullName }
    if ($Extra) { foreach ($e in $Extra.Split(';')) { if ($e.Trim()) { & $add $e } } }
    return ,@($list)
}

function Test-IsDefaultProfile([string]$ProfileDir) {
    return (Test-SamePath $ProfileDir (Join-Path $env:USERPROFILE '.claude'))
}

# Where <ProfileDir> currently gets our marketplace from: $null (not registered), or the folder/URL its
# known_marketplaces.json names. Read-only, and fail-soft: an unreadable file reads as "unknown".
function Get-ProfileMarketplaceSource([string]$ProfileDir) {
    $known = Join-Path $ProfileDir 'plugins\known_marketplaces.json'
    if (-not (Test-Path $known)) { return $null }
    try {
        $j = [System.IO.File]::ReadAllText($known) | ConvertFrom-Json
        $entry = $j.PSObject.Properties[$script:MarketplaceName]
        if (-not $entry) { return $null }
        $v = $entry.Value
        if ($v.source -and $v.source.path) { return [string]$v.source.path }
        if ($v.source -and $v.source.repo) { return [string]$v.source.repo }
        if ($v.source -and $v.source.url)  { return [string]$v.source.url }
        if ($v.installLocation)            { return [string]$v.installLocation }
        return '(unknown)'
    } catch { return '(unreadable)' }
}

# Run the Claude CLI against ONE profile. The default profile runs with CLAUDE_CONFIG_DIR removed (see
# the header); any other with it set. The caller's environment is restored either way.
function Invoke-ClaudeForProfile([string]$Claude, [string]$ProfileDir, [string[]]$CliArgs) {
    $saved = $env:CLAUDE_CONFIG_DIR
    try {
        if (Test-IsDefaultProfile $ProfileDir) { Remove-Item Env:\CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue }
        else { $env:CLAUDE_CONFIG_DIR = $ProfileDir }
        & $Claude @CliArgs 2>&1 | ForEach-Object { Write-Host "    $_" }
        return $LASTEXITCODE
    } finally {
        if ($null -eq $saved) { Remove-Item Env:\CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue }
        else { $env:CLAUDE_CONFIG_DIR = $saved }
    }
}

# Clone the marketplace into $Dest, or fast-forward an existing clone. Returns $true when $Dest ends up
# holding a marketplace (fresh, updated, or an existing copy we could not update - which is kept, never
# deleted: the upgrade-safety rule of the profile path applies here too).
function Sync-SharedMarketplace([string]$Dest, [string]$RepoUrl) {
    $manifest = Join-Path $Dest '.claude-plugin\marketplace.json'
    $git = Get-Command git -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $Dest '.git')) {
        if ($git) {
            Write-Host "  Updating shared marketplace: $Dest"
            & git -C $Dest pull --ff-only 2>&1 | ForEach-Object { Write-Host "    $_" }
            if ($LASTEXITCODE -ne 0) { Write-Host "  WARNING: could not update it (exit $LASTEXITCODE) - keeping the existing copy." }
        } else {
            Write-Host "  WARNING: git not found - keeping the existing shared copy without updating it."
        }
    } elseif (Test-Path $manifest) {
        # A marketplace that is not a clone (e.g. kept current by sync-marketplace-to-profile.ps1): ours
        # to use, not ours to replace.
        Write-Host "  Using the existing (non-git) shared marketplace at $Dest"
    } else {
        if (-not $git) {
            Write-Host "  WARNING: git not found - cannot clone the shared marketplace."
            return $false
        }
        if ((Test-Path $Dest) -and @(Get-ChildItem -LiteralPath $Dest -Force -ErrorAction SilentlyContinue).Count -gt 0) {
            Write-Host "  WARNING: $Dest exists, is not empty and is not a marketplace - refusing to clone over it."
            return $false
        }
        $parent = Split-Path -Parent $Dest
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Write-Host "  Cloning $RepoUrl -> $Dest"
        & git clone --quiet $RepoUrl $Dest 2>&1 | ForEach-Object { Write-Host "    $_" }
        if ($LASTEXITCODE -ne 0) { Write-Host "  WARNING: clone failed (exit $LASTEXITCODE)."; return $false }
        Set-CaRegistryValue 'PluginMarketplaceCreated' '1'
    }
    return (Test-Path $manifest)
}

function Get-CaRegistryValue([string]$Name) {
    try { return (Get-ItemProperty -Path $RegistryKey -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

function Set-CaRegistryValue([string]$Name, [string]$Value) {
    if (-not (Test-Path $RegistryKey)) { New-Item -Path $RegistryKey -Force | Out-Null }
    New-ItemProperty -Path $RegistryKey -Name $Name -Value $Value -PropertyType String -Force | Out-Null
}

function Remove-CaRegistryValue([string]$Name) {
    Remove-ItemProperty -Path $RegistryKey -Name $Name -ErrorAction SilentlyContinue
}

# Register $Shared in ONE profile and install the plugin from it. Idempotent: a profile already pointing
# at $Shared is not removed and re-added. A profile pointing anywhere else (the GitHub clone an earlier
# install made) is switched over - safe only because the caller has already proved $Shared holds a
# marketplace. Returns $true on success.
function Register-SharedInProfile([string]$Claude, [string]$ProfileDir, [string]$Shared) {
    Write-Host "  Profile: $ProfileDir"
    $current = Get-ProfileMarketplaceSource $ProfileDir
    if ($current -and (Test-SamePath $current $Shared)) {
        Write-Host "    already registered to the shared folder"
    } else {
        if ($current) {
            Write-Host "    switching from $current to the shared folder"
            [void](Invoke-ClaudeForProfile $Claude $ProfileDir @('plugin', 'marketplace', 'remove', $script:MarketplaceName))
        }
        $rc = Invoke-ClaudeForProfile $Claude $ProfileDir @('plugin', 'marketplace', 'add', $Shared)
        if ($rc -ne 0) { Write-Host "    WARNING: marketplace add failed (exit $rc)"; return $false }
    }
    $rc = Invoke-ClaudeForProfile $Claude $ProfileDir @('plugin', 'install', $script:PluginRef, '--scope', 'user')
    if ($rc -ne 0) { Write-Host "    WARNING: plugin install failed (exit $rc)"; return $false }
    return $true
}

# Shared mode end to end. Returns $true when the shared folder is in place and recorded; $false means
# the caller should fall back to the profile path (nothing was changed in any profile).
function Install-SharedMarketplace([string]$Claude, [string]$SharedPath, [string]$ExtraProfiles, [string]$RepoUrl) {
    $shared = Expand-CaPath $SharedPath
    if (-not $shared) {
        Write-Host "WARNING: shared plugin location '$SharedPath' is not an absolute path - using the default profile instead."
        return $false
    }
    if (Test-InsideClaudeProfile $shared) {
        Write-Host "WARNING: shared plugin location $shared is inside a Claude profile folder - using the default profile instead."
        return $false
    }
    Write-Host "Plugin location: SHARED ($shared)"
    if (-not (Sync-SharedMarketplace $shared $RepoUrl)) {
        Write-Host "WARNING: no marketplace at $shared - using the default profile instead."
        return $false
    }
    # Recorded BEFORE the profiles: even if every CLI call below fails, CA-launched sessions can use it.
    Set-CaRegistryValue 'PluginMarketplacePath' $shared

    $done = New-Object System.Collections.ArrayList
    $prior = Get-CaRegistryValue 'PluginMarketplaceProfiles'
    if ($prior) { foreach ($p in $prior.Split(';')) { if ($p.Trim()) { [void]$done.Add($p.Trim()) } } }
    if ($Claude) {
        foreach ($profileDir in (Get-ClaudeProfiles $ExtraProfiles)) {
            if (-not (Test-Path $profileDir)) { New-Item -ItemType Directory -Path $profileDir -Force | Out-Null }
            if (Register-SharedInProfile $Claude $profileDir $shared) {
                $known = $false
                foreach ($d in $done) { if (Test-SamePath $d $profileDir) { $known = $true } }
                if (-not $known) { [void]$done.Add($profileDir) }
            }
        }
    } else {
        Write-Host "WARNING: Claude Code CLI not found - the shared folder is in place for Clarion Assistant, but no"
        Write-Host "  profile was registered. Re-run setup once Claude Code is installed."
    }
    Set-CaRegistryValue 'PluginMarketplaceProfiles' (($done | ForEach-Object { $_ }) -join ';')
    Write-Host "Shared marketplace registered in $($done.Count) profile(s)."
    return $true
}

# Uninstall: undo Install-SharedMarketplace. Only touches profiles WE registered and a folder WE cloned.
function Uninstall-SharedMarketplace([string]$Claude) {
    $shared = Get-CaRegistryValue 'PluginMarketplacePath'
    if (-not $shared) { Write-Host "No shared plugin location recorded - nothing to undo."; return }
    $profiles = Get-CaRegistryValue 'PluginMarketplaceProfiles'
    if ($Claude -and $profiles) {
        foreach ($p in $profiles.Split(';')) {
            $p = $p.Trim()
            if (-not $p) { continue }
            $current = Get-ProfileMarketplaceSource $p
            if ($current -and -not (Test-SamePath $current $shared)) {
                Write-Host "  $p now uses $current - leaving it alone"
                continue
            }
            Write-Host "  De-registering from $p"
            [void](Invoke-ClaudeForProfile $Claude $p @('plugin', 'uninstall', $script:PluginRef, '--scope', 'user'))
            [void](Invoke-ClaudeForProfile $Claude $p @('plugin', 'marketplace', 'remove', $script:MarketplaceName))
        }
    }
    if ((Get-CaRegistryValue 'PluginMarketplaceCreated') -eq '1') {
        $sharedFull = Expand-CaPath $shared
        if ($sharedFull -and (Test-Path (Join-Path $sharedFull '.claude-plugin\marketplace.json'))) {
            Write-Host "  Deleting the shared folder this installer created: $sharedFull"
            Remove-Item -LiteralPath $sharedFull -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    # PluginMarketplaceExtraProfiles is the wizard's remembered "other profile folders" box.
    foreach ($n in 'PluginMarketplacePath', 'PluginMarketplaceProfiles', 'PluginMarketplaceCreated', 'PluginMarketplaceExtraProfiles') { Remove-CaRegistryValue $n }
    Write-Host "Shared plugin location removed."
}

function Find-ClaudeCli {
    $candidates = @(
        (Join-Path $env:APPDATA 'npm\claude.cmd'),                          # npm global (most common)
        (Join-Path $env:USERPROFILE '.claude\local\claude.exe'),            # standalone CLI
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\claude.exe')   # winget shim
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command claude -ErrorAction SilentlyContinue                 # anything on PATH
    if ($cmd) { return $cmd.Source }
    return $null
}

if ($UninstallPlugin) {
    $ErrorActionPreference = 'Continue'
    try { Uninstall-SharedMarketplace (Find-ClaudeCli) }
    catch { Write-Host "WARNING: could not undo the shared plugin location: $_" }
    return
}

if ($InstallPlugin -and $PluginLocation -eq 'Shared') {
    $ErrorActionPreference = 'Continue'   # same native-stderr reason as the profile path below
    $sharedOk = $false
    try { $sharedOk = Install-SharedMarketplace (Find-ClaudeCli) $SharedPluginPath $ProfileDirs $MarketplaceRepoUrl }
    catch { Write-Host "WARNING: shared plugin location failed: $_" }
    if ($sharedOk) {
        Write-Host "`nClarion Assistant plugin registration complete."
        return
    }
    # Fall through to the default-profile install, so a failed shared setup still leaves a working plugin.
    $ErrorActionPreference = 'Stop'
}

if ($InstallPlugin) {
    # --- Register and install the clarion-assistant plugin from GitHub ---
    # The installer no longer bundles a copy of the marketplace into the profile.
    # Instead we register the real GitHub marketplace and install the plugin, so it is
    # a genuine, updatable Claude Code marketplace (git-sourced, not a static Directory
    # copy). Claude clones it to
    #   %USERPROFILE%\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant
    # which is the exact path the ClarionAssistant runtime reads. Best-effort: if Claude
    # Code is not installed yet or there is no network, we WARN and continue.
    $marketplaceName = 'clarionassistant-marketplace'
    $marketplaceRepo = 'ClarionLive/clarionassistant-marketplace'
    $pluginRef       = "clarion-assistant@$marketplaceName"
    $manualSteps     = "    claude plugin marketplace add $marketplaceRepo`n    claude plugin install $pluginRef --scope user"

    try {
        # Native CLI calls (claude/git) legitimately write progress to stderr and exit
        # non-zero on benign conditions -- notably `marketplace remove` of a not-yet-
        # registered marketplace on a FRESH machine. Under the file-wide 'Stop', Windows
        # PowerShell 5.1 promotes native stderr merged via 2>&1 into a TERMINATING error,
        # which would skip add/install. Scope to 'Continue' and gate on $LASTEXITCODE.
        $ErrorActionPreference = 'Continue'

        $claude = Find-ClaudeCli

        # GH #214: 'Profile' chosen after an earlier 'Shared' install. If the default profile is still
        # registered to the shared folder, removing that registration here could take the folder (and
        # every other profile's plugin) with it, so leave everything as it is and say how to switch.
        # Otherwise forget the shared path, so the add-in stops preferring a copy nothing maintains.
        $priorShared = Get-CaRegistryValue 'PluginMarketplacePath'
        $defaultSource = Get-ProfileMarketplaceSource (Join-Path $env:USERPROFILE '.claude')
        if ($priorShared -and $defaultSource -and (Test-SamePath $defaultSource $priorShared)) {
            Write-Host "Plugin location: the default profile still uses the SHARED marketplace at $priorShared - left unchanged."
            Write-Host "  To move back to a per-profile copy, uninstall Clarion Assistant (which de-registers the shared"
            Write-Host "  folder) and install again choosing the default profile."
            Write-Host "`nClarion Assistant plugin registration complete."
            return
        }
        if ($priorShared) { Remove-CaRegistryValue 'PluginMarketplacePath' }

        if (-not $claude) {
            Write-Host "WARNING: Claude Code CLI not found; skipping plugin registration."
            Write-Host "  Once Claude Code is installed, finish setup with:`n$manualSteps"
        } else {
            Write-Host "Registering clarion-assistant plugin via Claude CLI: $claude"

            # UPGRADE SAFETY. The `remove` clears an existing registration so `add` can
            # re-clone under the same name -- but it also DELETES the on-disk plugin. If
            # the subsequent GitHub `add` then fails, an UPGRADING user is left with no
            # plugin (lost skills/hooks). So we only ever remove when BOTH hold:
            #   (a) there IS an existing plugin to migrate, AND
            #   (b) we have POSITIVELY confirmed the GitHub repo is reachable.
            # A FRESH machine (no existing plugin) has nothing to lose, so it just adds
            # directly. If we cannot confirm reachability -- no network, OR no `git` on
            # PATH to probe with -- we treat it as NOT verified and leave any existing
            # plugin untouched. (Absence of git must fail SAFE, not fall through to the
            # destructive path: claude's own `add` may rely on that same missing git.)
            $repoUrlGit  = "https://github.com/$marketplaceRepo.git"
            $mktDir      = Join-Path $env:USERPROFILE ".claude\plugins\marketplaces\$marketplaceName"
            $hasExisting = Test-Path $mktDir

            $reachable = $false
            if (Get-Command git -ErrorAction SilentlyContinue) {
                & git ls-remote --exit-code --heads $repoUrlGit *> $null
                $reachable = ($LASTEXITCODE -eq 0)
            }

            if ($hasExisting -and -not $reachable) {
                Write-Host "WARNING: cannot confirm $repoUrlGit is reachable -- leaving the existing plugin in place."
                Write-Host "  Update later when online with:`n$manualSteps"
            } else {
                if ($hasExisting) {
                    # Reachable upgrade: clear the prior registration (older installers
                    # bundled it as a Source: Directory copy) so the GitHub git source can
                    # own the name/path, then re-add fresh.
                    & $claude plugin marketplace remove $marketplaceName 2>&1 | Out-Null
                }

                & $claude plugin marketplace add $marketplaceRepo 2>&1 | ForEach-Object { Write-Host "  $_" }
                if ($LASTEXITCODE -ne 0) { throw "marketplace add failed (exit $LASTEXITCODE)" }

                & $claude plugin install $pluginRef --scope user 2>&1 | ForEach-Object { Write-Host "  $_" }
                if ($LASTEXITCODE -ne 0) { throw "plugin install failed (exit $LASTEXITCODE)" }

                Write-Host "Installed $pluginRef from GitHub."
            }
        }
    } catch {
        # Non-fatal: never abort the installer over the plugin step.
        Write-Host "WARNING: Could not register/install the clarion-assistant plugin from GitHub: $_"
        Write-Host "  This is non-fatal. Finish setup later with:`n$manualSteps"
    }

    Write-Host "`nClarion Assistant plugin registration complete."
    return
}

$claudeDir = Join-Path $env:USERPROFILE '.claude'

# Ensure .claude directory exists
if (-not (Test-Path $claudeDir)) {
    New-Item -ItemType Directory -Path $claudeDir -Force | Out-Null
}

# Recursively convert whatever ConvertFrom-Json produced into plain hashtables.
#
# GH #190: this used to be `ConvertFrom-Json -AsHashtable`. That switch is PowerShell 6.0+, and
# the installer runs this script with powershell.exe — which on Windows is ALWAYS Windows
# PowerShell 5.1, never pwsh. So it threw ParameterBindingException on EVERY machine, the catch
# below mistook its own unsupported API call for a corrupt user file, and settings.json was
# rebuilt from an empty hashtable — destroying hooks, model, statusLine, tui, enabledPlugins and
# the user's own permission entries. Converting by hand behaves identically on 5.1 and 7.x.
#
# The `,` before each array is load-bearing: PowerShell unrolls a returned array, so a
# single-element list would come back as a scalar and an empty one as $null. Either would be
# re-serialised with the WRONG SHAPE — a one-entry hooks array silently becoming an object is
# exactly the sort of quiet mangling this function exists to prevent.
function ConvertTo-HashtableDeep {
    param($InputObject)

    if ($null -eq $InputObject) { return $null }

    # [ordered] rather than @{}: a plain hashtable has no defined order, so round-tripping
    # someone's settings.json would silently shuffle every key. Not data loss, but it turns any
    # diff of the file into noise — and diffing against a known-good copy is exactly how the
    # reporter of GH #190 was recovering. Insertion order here is the file's own order.
    if ($InputObject -is [System.Collections.IDictionary]) {
        $h = [ordered]@{}
        foreach ($k in @($InputObject.Keys)) { $h[$k] = ConvertTo-HashtableDeep $InputObject[$k] }
        return $h
    }

    if ($InputObject -is [System.Management.Automation.PSCustomObject]) {
        $h = [ordered]@{}
        foreach ($p in $InputObject.PSObject.Properties) { $h[$p.Name] = ConvertTo-HashtableDeep $p.Value }
        return $h
    }

    if ($InputObject -is [string]) { return $InputObject }

    if ($InputObject -is [System.Collections.IEnumerable]) {
        $list = @(foreach ($item in $InputObject) { ConvertTo-HashtableDeep $item })
        return ,$list
    }

    return $InputObject
}

# ── 1. Merge settings.json (non-destructive) ──
$settingsPath = Join-Path $claudeDir 'settings.json'
$settings = @{}
$originalTopLevelKeys = @()
$skipSettingsWrite = $false

if (Test-Path $settingsPath) {
    try {
        # GH #200: read through .NET, NOT Get-Content. Under Windows PowerShell 5.1 - which is what
        # powershell.exe always is, and what the installer runs this script with - Get-Content with
        # no -Encoding decodes using the system ANSI codepage. A BOM-less UTF-8 settings.json
        # therefore has every non-ASCII character mangled on the way IN, and the mangled form is
        # what gets written back out. Measured on 5.1.26100: U+2019 (bytes e2 80 99) came back as
        # c3 a2 e2 82 ac e2 84 a2, the classic mojibake triple.
        #
        # ReadAllText honours a BOM when one is present and otherwise decodes UTF-8, and does so
        # identically on 5.1 and 7.x. Deliberately NOT Get-Content -Encoding UTF8: that happens to
        # be correct on both hosts today, but it is the same "one token, two meanings depending on
        # the host" trap that produced the BOM defect on the write side below.
        $parsed = [System.IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
        $settings = ConvertTo-HashtableDeep $parsed
        if ($null -eq $settings) { $settings = @{} }
        $originalTopLevelKeys = @($settings.Keys)
        Write-Host "Loaded existing settings.json ($($originalTopLevelKeys.Count) top-level keys)"
    } catch {
        # The file genuinely did not parse. DO NOT rebuild it from an empty hashtable and write
        # that back — that is precisely what destroyed users' configs in GH #190. Back it up,
        # say so loudly, and leave the user's file exactly where it is. Not configuring is a
        # recoverable outcome; silently replacing someone's global Claude Code settings is not.
        $backupPath = Join-Path $claudeDir "settings.json.backup.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item $settingsPath $backupPath -Force
        Write-Host "WARNING: could not parse $settingsPath - $($_.Exception.Message)"
        Write-Host "  A copy was saved as $backupPath and your settings.json was LEFT UNCHANGED."
        Write-Host "  Clarion Assistant permissions were NOT added. Fix the JSON and re-run this script,"
        Write-Host "  or add them from the Clarion Assistant settings screen."
        $skipSettingsWrite = $true
    }
}

# Ensure permissions.allow exists as an array
# .Contains, not .ContainsKey: $settings may be an OrderedDictionary (see ConvertTo-HashtableDeep),
# which implements IDictionary.Contains but has no ContainsKey. Hashtable supports both, so
# .Contains is the form that is correct for either. Getting this wrong throws at run time while
# still producing a plausible-looking settings.json, so it does not announce itself.
if (-not $settings.Contains('permissions')) {
    $settings['permissions'] = @{}
}
if (-not $settings['permissions'].Contains('allow')) {
    $settings['permissions']['allow'] = @()
}

# Permissions to add for Clarion Assistant MCP tools
$requiredPermissions = @(
    'mcp__clarion-assistant__get_active_file',
    'mcp__clarion-assistant__get_selected_text',
    'mcp__clarion-assistant__get_word_under_cursor',
    'mcp__clarion-assistant__get_cursor_position',
    'mcp__clarion-assistant__open_file',
    'mcp__clarion-assistant__go_to_line',
    'mcp__clarion-assistant__insert_text_at_cursor',
    'mcp__clarion-assistant__replace_text',
    'mcp__clarion-assistant__replace_range',
    'mcp__clarion-assistant__select_range',
    'mcp__clarion-assistant__delete_range',
    'mcp__clarion-assistant__undo',
    'mcp__clarion-assistant__redo',
    'mcp__clarion-assistant__save_file',
    'mcp__clarion-assistant__close_file',
    'mcp__clarion-assistant__get_open_files',
    'mcp__clarion-assistant__get_line_text',
    'mcp__clarion-assistant__get_lines_range',
    'mcp__clarion-assistant__find_in_file',
    'mcp__clarion-assistant__is_modified',
    'mcp__clarion-assistant__toggle_comment',
    'mcp__clarion-assistant__open_app',
    'mcp__clarion-assistant__get_app_info',
    'mcp__clarion-assistant__list_procedures',
    'mcp__clarion-assistant__get_procedure_details',
    'mcp__clarion-assistant__open_procedure_embed',
    'mcp__clarion-assistant__get_embed_info',
    'mcp__clarion-assistant__save_and_close_embeditor',
    'mcp__clarion-assistant__cancel_embeditor',
    'mcp__clarion-assistant__read_file',
    'mcp__clarion-assistant__write_file',
    'mcp__clarion-assistant__append_to_file',
    'mcp__clarion-assistant__list_directory',
    'mcp__clarion-assistant__analyze_class',
    'mcp__clarion-assistant__sync_check',
    'mcp__clarion-assistant__generate_stubs',
    'mcp__clarion-assistant__generate_clw',
    'mcp__clarion-assistant__query_codegraph',
    'mcp__clarion-assistant__list_codegraph_databases',
    'mcp__clarion-assistant__query_docs',
    'mcp__clarion-assistant__ingest_docs',
    'mcp__clarion-assistant__list_doc_libraries',
    'mcp__clarion-assistant__discover_docs',
    'mcp__clarion-assistant__docgraph_stats',
    'mcp__clarion-assistant__build_solution',
    'mcp__clarion-assistant__build_app',
    'mcp__clarion-assistant__generate_source',
    'mcp__clarion-assistant__build_com_project',
    'mcp__clarion-assistant__run_command',
    'mcp__clarion-assistant__lsp_start',
    'mcp__clarion-assistant__lsp_definition',
    'mcp__clarion-assistant__lsp_references',
    'mcp__clarion-assistant__lsp_hover',
    'mcp__clarion-assistant__lsp_document_symbols',
    'mcp__clarion-assistant__lsp_find_symbol',
    'mcp__clarion-assistant__show_diff',
    'mcp__clarion-assistant__get_diff_result',
    'mcp__clarion-assistant__export_txa',
    'mcp__clarion-assistant__import_txa',
    'mcp__clarion-assistant__inspect_ide'
)

$existingPerms = [System.Collections.ArrayList]@($settings['permissions']['allow'])
$addedCount = 0

foreach ($perm in $requiredPermissions) {
    if ($perm -notin $existingPerms) {
        $existingPerms.Add($perm) | Out-Null
        $addedCount++
    }
}

$settings['permissions']['allow'] = @($existingPerms)
# Don't claim to have added anything when the write is going to be skipped — on the
# unparseable-file path these mutations are made against an empty hashtable and then discarded,
# and "Added 60 new permissions" immediately above "Skipped writing settings.json" reads as
# though they landed.
if (-not $skipSettingsWrite) {
    Write-Host "Added $addedCount new permissions ($($requiredPermissions.Count) total Clarion tools)"
}

# Ensure env vars
if (-not $settings.Contains('env')) {
    $settings['env'] = @{}
}
if (-not $settings['env'].Contains('CLAUDE_CODE_USE_POWERSHELL_TOOL')) {
    $settings['env']['CLAUDE_CODE_USE_POWERSHELL_TOOL'] = '1'
}
if (-not $settings['env'].Contains('CLAUDE_CODE_NO_FLICKER')) {
    $settings['env']['CLAUDE_CODE_NO_FLICKER'] = '1'
}

# Write settings.json
if ($skipSettingsWrite) {
    Write-Host "Skipped writing settings.json (see the warning above) - your file was left untouched."
} else {
    # POST-MERGE GUARD (GH #190). Everything above only ever ADDS keys, so any top-level key that
    # went in must still be here. If one is missing, something upstream lost user data and we must
    # not commit it to disk. This check is deliberately not specific to the -AsHashtable bug that
    # prompted it: it catches the whole class, including whatever the next refactor of this script
    # gets wrong. A settings.json we failed to update is a nuisance; one we silently truncated is
    # the user's entire Claude Code configuration.
    $finalKeys = @($settings.Keys)
    $missingKeys = @($originalTopLevelKeys | Where-Object { $finalKeys -notcontains $_ })

    if ($missingKeys.Count -gt 0) {
        $backupPath = Join-Path $claudeDir "settings.json.backup.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item $settingsPath $backupPath -Force
        Write-Host "ERROR: refusing to write settings.json - the merge would have dropped $($missingKeys.Count) existing top-level key(s):"
        Write-Host "  $($missingKeys -join ', ')"
        Write-Host "  Your settings.json was LEFT UNCHANGED (a copy is at $backupPath)."
        Write-Host "  This is a bug in the installer - please report it with the key names above."
    } else {
        # GH #200: back up on the SUCCESS path too. Until now a copy was kept only when we REFUSED
        # to write - a parse failure, or the dropped-key guard above - so the COMMON case, a merge
        # that succeeds, overwrote the user's file with no backup at all. The 5.8 release notes told
        # affected users to look for settings.json.backup.<timestamp>, a file the success path never
        # produced. Test-Path guards the fresh-machine case where there is nothing to copy yet.
        $backupPath = $null
        if (Test-Path $settingsPath) {
            $backupPath = Join-Path $claudeDir "settings.json.backup.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
            Copy-Item $settingsPath $backupPath -Force
        }

        $settingsJson = $settings | ConvertTo-Json -Depth 10

        # GH #200: write BOM-less UTF-8 explicitly. Set-Content -Encoding UTF8 means UTF-8 WITH BOM
        # under Windows PowerShell 5.1, and BOM-less under PowerShell 7 - the two hosts disagree
        # about what the token "UTF8" names, so the old call silently added a BOM to a file that
        # belongs to Claude Code rather than to us. UTF8Encoding($false) means the same thing on
        # every host, and also strips a BOM left behind by an earlier 5.8 / 5.8.1 install.
        [System.IO.File]::WriteAllText($settingsPath, $settingsJson, (New-Object System.Text.UTF8Encoding $false))

        if ($backupPath) {
            Write-Host "Updated settings.json ($($finalKeys.Count) top-level keys preserved; backup at $backupPath)"
        } else {
            Write-Host "Created settings.json ($($finalKeys.Count) top-level keys)"
        }
    }
}

# ── 3. Set DocGraph DB path via environment variable ──
if ($DocGraphDb -and (Test-Path $DocGraphDb)) {
    # Set user environment variable so ClarionAssistant can find the pre-loaded DB
    [Environment]::SetEnvironmentVariable('CLARION_DOCGRAPH_DB', $DocGraphDb, 'User')
    Write-Host "Set CLARION_DOCGRAPH_DB = $DocGraphDb"
}

# ── 4. Set Clarion root path ──
if ($ClarionRoot) {
    [Environment]::SetEnvironmentVariable('CLARION_ROOT', $ClarionRoot, 'User')
    Write-Host "Set CLARION_ROOT = $ClarionRoot"
}

# ── 5. Write .clarioncom.env (ClarionCOM environment config) ──
$clarionComEnv = Join-Path $env:USERPROFILE '.clarioncom.env'
$clarionComHome = Join-Path $env:APPDATA 'ClarionCOM'
$envLines = @(
    "CLARIONCOM_HOME=$clarionComHome"
)
if ($ClarionRoot) {
    $envLines += "CLARION_PATH=$ClarionRoot"
}
# Merge with existing file if present
if (Test-Path $clarionComEnv) {
    $existing = @{}
    # GH #200, same defect class as settings.json above: Get-Content with no -Encoding decodes as
    # ANSI on Windows PowerShell 5.1. CLARIONCOM_HOME is built from %APPDATA%, so a Windows account
    # whose name contains a non-ASCII character would have its path mangled here on every re-run.
    [System.IO.File]::ReadAllLines($clarionComEnv) | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') {
            $existing[$matches[1]] = $matches[2]
        }
    }
    # Our values override
    $existing['CLARIONCOM_HOME'] = $clarionComHome
    if ($ClarionRoot) { $existing['CLARION_PATH'] = $ClarionRoot }
    $envLines = $existing.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
}
# GH #200: BOM-less UTF-8, for the same reason as settings.json. Out-File -Encoding UTF8 emits a BOM
# under Windows PowerShell 5.1, and a BOM on line 1 makes the leading key parse as
# "<U+FEFF>CLARIONCOM_HOME" for any consumer that does not strip it - which the regex above would
# then fail to match. PowerShell's own Get-Content hid this by stripping the BOM on read.
[System.IO.File]::WriteAllLines($clarionComEnv, [string[]]@($envLines), (New-Object System.Text.UTF8Encoding $false))
Write-Host "Updated $clarionComEnv"

Write-Host "`nClarion Assistant configuration complete."
Write-Host "Restart the Clarion IDE to load the addins."
