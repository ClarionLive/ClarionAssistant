# Source guard for ticket fc420c30: every MCP editor tool goes through EditorRouter, so with a CA Editor (Monaco overlay)
# up it reads and writes Monaco's text instead of the native document hidden under it.
#
# EditorToolRouter.Test.cs proves the ROUTER does the right thing. This proves the TOOLS use it: a tool registered
# straight to _editorService again would pass that harness and still edit the hidden document. Checked per tool:
#   * its handler calls EditorRouter.Run("<tool>", ...) and is RequiresUiThread = false (the router marshals; the
#     overlay path waits for the page, which cannot happen on the UI thread)
#   * get_open_files marks dirty CA Editor tabs (EditorToolRouter.OpenFilesAdjuster)
#   * the addin registers the resolver at STARTUP (LspAutostartCommand), not in the chat panel
#   * get_live_text says "open in the CA Editor with no unsaved edits" for an open, unedited CA Editor tab, and that a
#     NATIVE-mode tab's unsaved edits are not visible
#   * open_file waits until the file is the active editor; the write tools take file_path and name the file they changed
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

# fc420c30 safety (live, combined-1005b): open_file returned "Opened" while the previous tab was still active.
$of = Block 'open_file'
Check ($null -ne $of -and $of.Contains('EditorRouter.OpenAndWait(path') -and $of.Contains('_editorService.ActivateOpenFile(path)') -and
       $of.Contains('RequiresUiThread = false')) 'open_file waits (off the UI thread) until the file is active, selecting its tab'
Check ($auto.Contains('EditorToolRouter.OverlayExpectedFor = ') -and $auto.Contains('CaEditorSettings.SourceAppliesTo(path)')) `
    'open_file knows when to wait for a CA Editor page too (OverlayExpectedFor at startup)'
# Live (combined-1005c): from the .app view, SelectWindow alone displayed the tab but left the .app the active window.
$svcPath = Join-Path $Root 'Services\EditorService.cs'
$svc = if (Test-Path $svcPath) { [System.IO.File]::ReadAllText($svcPath) } else { '' }
Check ($svc.Contains('return EditorToolRouter.ActivateTab(') -and $auto.Contains('EditorToolRouter.FocusTab = MonacoClarionEditor.FocusTabFor')) `
    'open_file activates an open tab at both levels (select, then keyboard focus)'
$g = $svc.IndexOf('public List<string> GetOpenFiles()')
$gof = if ($g -ge 0) { $svc.Substring($g, [Math]::Min(2500, $svc.Length - $g)) } else { '' }
Check ($gof.Contains('"ToolTipText"') -and $gof.IndexOf('"ToolTipText"') -lt $gof.IndexOf('"TitleName"')) `
    'get_open_files gives full paths (the window tooltip) before the bare tab title'
foreach ($t in @('insert_text_at_cursor', 'replace_text', 'replace_range', 'delete_range', 'toggle_comment', 'undo', 'redo',
                 'save_file', 'close_file')) {
    $b = Block $t
    # "file_path?": BuildSchema makes a key without '?' REQUIRED when the tool passes no required list (live, round 2:
    # undo's schema demanded file_path although its help says optional).
    Check ($null -ne $b -and $b.Contains('WriteOpts(args') -and $b.Contains('{ "file_path?", WriteFilePathHelp }') -and
           -not $b.Contains('{ "file_path", WriteFilePathHelp }')) `
        "$t names the file it changed and takes an OPTIONAL file_path"
}

# fc420c30 (live): a NATIVE-mode tab (CA Editor toggled off) was reported as "open in the CA Editor with no unsaved edits".
$mce = Join-Path $Root 'MonacoClarionSourceEditor.cs'
$mceSrc = if (Test-Path $mce) { [System.IO.File]::ReadAllText($mce) } else { '' }
$k = $mceSrc.IndexOf('private static bool? OpenTabHasOverlay(')
$tabHas = if ($k -ge 0) { $mceSrc.Substring($k, [Math]::Min(900, $mceSrc.Length - $k)) } else { '' }
Check ($tabHas.Contains('if (inst._editor != null) return true;') -and
       $mceSrc.Contains('internal static bool IsOpenInOverlay(string path) { return OpenTabHasOverlay(path) == true; }')) `
    'IsOpenInOverlay counts a tab only when its CA Editor is up'
Check ($prov.Contains('MonacoClarionEditor.IsOpenInNativeEditor(path)') -and $prov.Contains('open in the native Clarion editor') -and
       $prov.Contains('not visible to the tool')) 'get_live_text says a native-mode tab''s unsaved edits are not visible'

# GH #262: the native range tools use the shared column rule and put the caret after the text actually inserted (its
# CRLF-normalized form; EditorToolRouter.Test checks the helpers themselves).
$rr = $svc.IndexOf('public InsertResult ReplaceRange(')
$rrBody = if ($rr -ge 0) { $svc.Substring($rr, [Math]::Min(3000, $svc.Length - $rr)) } else { '' }
Check ($rrBody.Contains('EditorTextOps.ReplacementText(newText, startOffset, out caretOffset)') -and
       $rrBody.Contains('SetProperty(caret, "Offset", caretOffset)') -and -not $rrBody.Contains('(newText ?? "").Length')) `
    'replace_range (native) sets the caret from the inserted, CRLF-normalized text'
Check ($svc.Contains('return lineOffset + EditorTextOps.ColumnIndex(col1, lineLength);')) `
    'the native range tools clamp columns by EditorTextOps.ColumnIndex (the CA Editor''s rule)'
# A reversed range is swapped before Document.Replace (a negative length threw after DocumentAboutToBeChanged).
$swap = $rrBody.IndexOf('if (endOffset < startOffset) { int swap = startOffset; startOffset = endOffset; endOffset = swap; }')
Check ($swap -ge 0 -and $swap -lt $rrBody.IndexOf('int length = endOffset - startOffset;')) `
    'replace_range / delete_range (native) swap a reversed range before computing its length'
$rangeRules = $src.Contains('end_col is EXCLUSIVE')
foreach ($t in @('replace_range', 'delete_range', 'select_range')) {
    $b = Block $t
    Check ($rangeRules -and $null -ne $b -and $b.Contains('RangeColumnRules') -and $b.Contains('{ "end_col", RangeEndColHelp }')) `
        "$t documents its column rules (end_col exclusive)"
    # An omitted end_col is "the end of end_line" for ANY line: 999 left everything past column 998 behind.
    Check ($null -ne $b -and $b.Contains('McpJsonRpc.GetInt(args, "end_col", int.MaxValue)') -and -not $b.Contains('"end_col", 999')) `
        "$t defaults an omitted end_col to int.MaxValue (the end of a line of any length)"
}
$del = Block 'delete_range'
Check ($null -ne $del -and $del.Contains('If M is the last line') -and $del.Contains('To empty the whole document')) `
    'delete_range documents the last-line and whole-document recipes'
# ...and the bundled prompt says the same (Check-PromptSync keeps .claude\CLAUDE.md equal to it).
$promptPath = Join-Path $Root 'Terminal\clarion-assistant-prompt.md'
$prompt = if (Test-Path $promptPath) { [System.IO.File]::ReadAllText($promptPath) } else { '' }
Check ($prompt -match '(?m)^- `replace_range`.*`end_col` is EXCLUSIVE' -and $prompt -match '(?m)^- `select_range`.*not selected' -and
       $prompt -match '(?m)^- `delete_range`.*If M is the last line') 'the bundled prompt gives the range tools'' column rules and delete recipes'

Write-Host ''
if ($fail -eq 0) { Write-Host "PASS - $pass checks" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $fail of $($pass + $fail) checks" -ForegroundColor Red
exit 1
