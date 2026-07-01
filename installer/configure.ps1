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
    [switch]$InstallPlugin
)

$ErrorActionPreference = 'Stop'

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

# ── 1. Merge settings.json (non-destructive) ──
$settingsPath = Join-Path $claudeDir 'settings.json'
$settings = @{}

if (Test-Path $settingsPath) {
    try {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
        Write-Host "Loaded existing settings.json"
    } catch {
        # Backup corrupted file
        $backupPath = Join-Path $claudeDir "settings.json.backup.$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item $settingsPath $backupPath -Force
        Write-Host "Backed up corrupted settings.json"
        $settings = @{}
    }
}

# Ensure permissions.allow exists as an array
if (-not $settings.ContainsKey('permissions')) {
    $settings['permissions'] = @{}
}
if (-not $settings['permissions'].ContainsKey('allow')) {
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
Write-Host "Added $addedCount new permissions ($($requiredPermissions.Count) total Clarion tools)"

# Ensure env vars
if (-not $settings.ContainsKey('env')) {
    $settings['env'] = @{}
}
if (-not $settings['env'].ContainsKey('CLAUDE_CODE_USE_POWERSHELL_TOOL')) {
    $settings['env']['CLAUDE_CODE_USE_POWERSHELL_TOOL'] = '1'
}
if (-not $settings['env'].ContainsKey('CLAUDE_CODE_NO_FLICKER')) {
    $settings['env']['CLAUDE_CODE_NO_FLICKER'] = '1'
}

# Write settings.json
$settingsJson = $settings | ConvertTo-Json -Depth 10
Set-Content -Path $settingsPath -Value $settingsJson -Encoding UTF8
Write-Host "Updated settings.json"

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
    Get-Content $clarionComEnv | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') {
            $existing[$matches[1]] = $matches[2]
        }
    }
    # Our values override
    $existing['CLARIONCOM_HOME'] = $clarionComHome
    if ($ClarionRoot) { $existing['CLARION_PATH'] = $ClarionRoot }
    $envLines = $existing.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
}
$envLines | Out-File -FilePath $clarionComEnv -Encoding UTF8
Write-Host "Updated $clarionComEnv"

Write-Host "`nClarion Assistant configuration complete."
Write-Host "Restart the Clarion IDE to load the addins."
