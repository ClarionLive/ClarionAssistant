# Clarion Assistant - Post-Install Configuration
# Merges Claude Code settings non-destructively
param(
    [string]$ClarionRoot,
    [string]$DocGraphDb
)

$ErrorActionPreference = 'Stop'

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
