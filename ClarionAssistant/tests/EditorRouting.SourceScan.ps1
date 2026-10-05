# Source guard for ticket fc420c30: every MCP editor tool goes through EditorRouter, so with a CA Editor (Monaco overlay)
# up it reads and writes Monaco's text instead of the native document hidden under it.
#
# EditorToolRouter.Test.cs proves the ROUTER does the right thing. This proves the TOOLS use it: a tool registered
# straight to _editorService again would pass that harness and still edit the hidden document. Checked per tool:
#   * its handler calls EditorRouter.Run("<tool>", ...) and is RequiresUiThread = false (the router marshals; the
#     overlay path waits for the page, which cannot happen on the UI thread)
#   * get_open_files marks dirty CA Editor tabs (EditorToolRouter.OpenFilesAdjuster)
#   * the addin registers the resolver at STARTUP (LspAutostartCommand), not in the chat panel
#   * get_live_text says "open in the CA Editor with no unsaved edits" for an open, unedited CA Editor tab
#
# MUST BE ABLE TO GO RED: on master (3904549) every tool check fails (-Root <a master checkout's ClarionAssistant>).
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\EditorRouting.SourceScan.ps1 [-Root <dir>]
# Exit: 0 pass, 1 fail, 2 could-not-run.
param([string]$Root)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
$registry = Join-Path $Root 'Services\McpToolRegistry.cs'
$autostart = Join-Path $Root 'LspAutostartCommand.cs'
$provider = Join-Path $Root 'EditorLiveTextProvider.cs'
foreach ($f in @($registry, $autostart)) {
    if (-not (Test-Path $f)) { Write-Host "COULD NOT RUN: $f not found" -ForegroundColor Red; exit 2 }
}
$src = [System.IO.File]::ReadAllText($registry)

$fail = 0; $pass = 0
function Check([bool]$ok, [string]$name) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $name" } else { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# The registration block of one tool: from its Name to the next Register(.
function Block([string]$tool) {
    $i = $src.IndexOf('Name = "' + $tool + '",')
    if ($i -lt 0) { return $null }
    $j = $src.IndexOf('Register(new McpTool', $i)
    if ($j -lt 0) { $j = $src.Length }
    return $src.Substring($i, $j - $i)
}

$routed = @('get_active_file', 'get_selected_text', 'get_word_under_cursor', 'get_cursor_position', 'go_to_line',
            'insert_text_at_cursor', 'replace_text', 'replace_range', 'select_range', 'delete_range', 'undo', 'redo',
            'save_file', 'close_file', 'get_line_text', 'get_lines_range', 'find_in_file', 'is_modified', 'toggle_comment')
foreach ($t in $routed) {
    $b = Block $t
    Check ($null -ne $b -and $b.Contains('EditorRouter.Run("' + $t + '"') -and $b.Contains('RequiresUiThread = false')) `
        "$t goes through EditorRouter.Run and is not UI-bound"
}

$open = Block 'get_open_files'
Check ($null -ne $open -and $open.Contains('EditorToolRouter.OpenFilesAdjuster')) 'get_open_files marks dirty CA Editor tabs'

$auto = [System.IO.File]::ReadAllText($autostart)
# 73bd1f03 fix (2) composes the resolver (the CA Editor first, then the covered CA Embeditor): accept either form, as
# long as the CA Editor's resolver is the one asked first.
Check (($auto -match 'EditorToolRouter\.ActiveOverlayResolver\s*=\s*(\(\)\s*=>\s*)?MonacoClarionEditor\.ResolveActiveOverlay') -and
       $auto.Contains('EditorToolRouter.UiThreadId')) 'the resolver is registered at addin startup (LspAutostartCommand)'

$prov = if (Test-Path $provider) { [System.IO.File]::ReadAllText($provider) } else { '' }
Check ($prov.Contains('MonacoClarionEditor.IsOpenInOverlay(path)') -and $prov.Contains('open in the CA Editor with no unsaved edits')) `
    'get_live_text tells an open, unedited CA Editor tab apart from "no editor has this file open"'

Write-Host ''
if ($fail -eq 0) { Write-Host "PASS - $pass checks" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $fail of $($pass + $fail) checks" -ForegroundColor Red
exit 1
