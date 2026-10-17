# Source guard for GH #205: write_file / append_to_file must not write underneath a NATIVE Clarion editor tab's stale
# buffer. tests\OpenTabGuard.Test.cs proves the DECISIONS (OpenTabGuard). This proves the WIRING and the IDE half,
# which needs a live IDE to run:
#   * both write tools go through OpenTabGuard.Run, and their actual write sits INSIDE it (so a refusal writes nothing)
#   * both take overwrite_open_buffer and their descriptions say what happens to an open tab
#   * editor_tab_sync is IdeOnly and answers from OpenTabGuard.LocalCheck / LocalReload
#   * NativeTabSync: the CA Editor is asked first (its native shell is always clean); a reload re-checks IsDirty
#     BEFORE SharpDevelop's own AbstractViewContent.Load; UI work is Posted with a bounded wait, never Invoke/Send
#   * the addin registers it at STARTUP (LspAutostartCommand), not in the chat panel
#   * the standalone wires OpenTabGuard to IdeTabClient before serving, outside 'if (workspace != null)'
#   * both projects compile the new files
#
# MUST BE ABLE TO GO RED: against integ-1017 (3820d68, before the change) every check fails (-Root <that tree>).
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\OpenTabGuard.SourceScan.ps1 [-Root <dir>]
# Exit: 0 pass, 1 fail, 2 could-not-run.
param([string]$Root)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
$registryPath = Join-Path $Root 'Services\McpToolRegistry.cs'
$programPath = Join-Path $Root 'mcp-server\Program.cs'
$autostartPath = Join-Path $Root 'LspAutostartCommand.cs'
foreach ($f in @($registryPath, $programPath, $autostartPath)) {
    if (-not (Test-Path $f)) { Write-Host "COULD NOT RUN: $f not found" -ForegroundColor Red; exit 2 }
}
function Text([string]$rel) {
    $p = Join-Path $Root $rel
    if (Test-Path $p) { return [System.IO.File]::ReadAllText($p) } else { return '' }
}
$src = Text 'Services\McpToolRegistry.cs'

$fail = 0; $pass = 0
function Check([bool]$ok, [string]$name) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $name" } else { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# The registration block of one tool: from its Name to the next Register(.
function Block([string]$tool) {
    $i = $src.IndexOf('Name = "' + $tool + '",')
    if ($i -lt 0) { return '' }
    $j = $src.IndexOf('Register(new McpTool', $i)
    if ($j -lt 0) { $j = $src.Length }
    return $src.Substring($i, $j - $i)
}
function Before([string]$text, [string]$a, [string]$b) {
    $i = $text.IndexOf($a); $j = $text.IndexOf($b)
    return ($i -ge 0 -and $j -ge 0 -and $i -lt $j)
}

foreach ($pair in @(@('write_file', 'ClarionSourceText.WriteFile('), @('append_to_file', 'ClarionSourceText.AppendFile('))) {
    $t = $pair[0]; $b = Block $t
    Check ($b.Contains('OpenTabGuard.Run(') -and (Before $b 'OpenTabGuard.Run(' $pair[1])) "$t writes only inside OpenTabGuard.Run"
    Check ((Before $b 'McpPathGate.GatePath(' 'OpenTabGuard.Run(')) "$t is path-gated (#266) before the open-tab check"
    Check ($b.Contains('OpenTabGuard.OverrideArg') -and $b.Contains('OpenTabGuard.ParseFlag(args, OpenTabGuard.OverrideArg)')) "$t takes and reads overwrite_open_buffer"
    Check ($b.Contains('OpenTabNote')) "$t's description says what happens to an open tab"
}
$wb = Block 'write_file'
Check ((Before $wb 'OpenTabGuard.Run(' 'Directory.CreateDirectory(')) 'write_file creates the folder only inside the guard (a refusal creates nothing)'
$noteAt = $src.IndexOf('private const string OpenTabNote =')
Check ($noteAt -ge 0 -and $src.IndexOf('unsaved changes; save or close it there first', $noteAt) -gt $noteAt -and
       $src.IndexOf('unsaved changes; save or close it there first', $noteAt) -lt $src.IndexOf('private const string OpenTabOverrideArgNote', $noteAt)) `
    'the description quotes the refusal the developer will see'

$sync = Block 'editor_tab_sync'
Check ($sync.Contains('IdeOnly = true') -and $sync.Contains('OpenTabGuard.LocalCheck') -and $sync.Contains('OpenTabGuard.LocalReload')) `
    'editor_tab_sync is IdeOnly and answers from the IDE''s local check/reload'

$nts = Text 'NativeTabSync.cs'
function Method([string]$text, [string]$sig) {
    $i = $text.IndexOf($sig)
    if ($i -lt 0) { return '' }
    $j = $text.IndexOf("`n        private static", $i + $sig.Length)
    $k = $text.IndexOf("`n        internal static", $i + $sig.Length)
    if ($k -ge 0 -and ($j -lt 0 -or $k -lt $j)) { $j = $k }
    if ($j -lt 0) { $j = $text.Length }
    return $text.Substring($i, $j - $i)
}
$check = Method $nts 'private static TabProbe CheckOnUi('
$reload = Method $nts 'private static TabReload ReloadOnUi('
$keep = Method $nts 'private static int ReloadKeepingCaret('
$views = Method $nts 'private static List<TextEditorDisplayBindingWrapper> ViewsOf('
Check ((Before $check 'me.OverlayUp' 'w.IsDirty') -and $check.Contains('me.OverlayDirty')) `
    'the check classifies a tab whose CA Editor is up as ca-editor first (its native shell stays clean, so IsDirty would lie)'
Check ($check.Contains('w.IsDirty')) 'the check reads the native tab''s IsDirty'
Check ((Before $reload 'w.IsDirty' 'ReloadKeepingCaret(') -and $reload.Contains('unsaved changes now')) 'a reload re-checks IsDirty first and refuses a dirty tab'
Check ((Before $reload 'me.OverlayUp' 'ReloadKeepingCaret(')) 'a reload never touches a tab the CA Editor covers'
Check ($reload.Contains('string own = FileNameOf(w);') -and $reload.Contains('ReloadKeepingCaret(w, own)')) 'a reload loads the TAB''S OWN file name (an alias tab keeps its title)'
Check ($keep.Contains('w.Load(path)') -and (Before $keep 'caret.Line' 'w.Load(path)') -and (Before $keep 'w.Load(path)' 'area.Caret.Line =')) `
    'the reload is SharpDevelop''s own Load, with the caret line captured before and put back after'
# Pipeline run 1, fix 1: matched by FILE IDENTITY, and every view swept (FileService only matches path text).
Check ($views.Contains('FileIdentity.MatchesAny(FileNameOf(w), probes)') -and $views.Contains('ViewContentCollection')) `
    'tabs are matched by file identity, over every open view'
Check (-not ($nts -match 'string\.Equals\([^)]*FileName')) 'no tab is matched by comparing path text'
Check ($nts.Contains('UiThreadCall.Run(_ui') -and -not $nts.Contains('.Post(') -and -not $nts.Contains('Invoke(')) 'NativeTabSync goes through the shared UiThreadCall'
$uitc = Text 'UiThreadCall.cs'
Check ($uitc.Contains('ui.Post(') -and $uitc.Contains('done.Wait(waitMs)') -and -not $uitc.Contains('.Send(') -and -not $uitc.Contains('Invoke(')) `
    'UI work is Posted with a bounded wait, never Send/Invoke'
$elp = Text 'EditorLiveTextProvider.cs'
Check ($elp.Contains('UiThreadCall.Run(ui') -and $elp.Contains('FileIdentity.SameFile(a, b)') -and -not $elp.Contains('ui.Post(')) `
    'EditorLiveTextProvider shares the UI helper and the identity compare (no copy)'

$auto = [System.IO.File]::ReadAllText($autostartPath)
Check ($auto.Contains('NativeTabSync.Register()')) 'the addin registers it at startup (LspAutostartCommand)'

# Pipeline run 1, fix 2: the endpoint exists with NO chat pane.
Check ($auto.Contains('IdeCallbackHost.Start()')) 'the IDE call-back endpoint starts at addin start, not in the chat pane'
$cbHost = Text 'IdeCallbackHost.cs'
Check ($cbHost -match 'CallbackTools\s*=\s*\{\s*"get_live_text",\s*"editor_tab_sync"\s*\}' -and $cbHost.Contains('registry.RetainOnly(CallbackTools)')) `
    'the call-back endpoint serves exactly get_live_text and editor_tab_sync'
Check ($cbHost.Contains('ReclaimPreferredPort = false') -and $cbHost -match 'PreferredPort\s*=\s*19390;') `
    'it never takes or retries the chat pane''s 19372'
Check ($cbHost.Contains('new McpServer(ui, null)')) 'it accepts only its own session token (no external token)'
Check ($cbHost.Contains('IdeSolutionRecord.Publish(EditorService.GetOpenSolutionPath())')) 'it publishes the IDE''s open solution without a chat pane'
$srv = Text 'Services\McpServer.cs'
$startAt = $srv.IndexOf('public bool Start(')
$stopAt = $srv.IndexOf('public void Stop()')
Check ($startAt -ge 0 -and $srv.IndexOf('JoinEndpoint();', $startAt) -gt $startAt -and $srv.IndexOf('IdeEndpointRecord.Publish(port', $startAt) -lt 0) `
    'every McpServer publishes through the one-record-per-IDE ownership, never directly'
Check ($stopAt -ge 0 -and $srv.IndexOf('LeaveEndpoint();', $stopAt) -gt $stopAt) 'stopping a server hands the record on'

# Pipeline run 1, fix 1: both spellings travel to the IDE.
foreach ($t in @('write_file', 'append_to_file')) {
    $b = Block $t
    Check ((Before $b 'string asked = path;' 'McpPathGate.GatePath(') -and $b.Contains('OpenTabGuard.Run(target, asked,')) "$t sends the caller's spelling with the resolved path"
}
Check ($sync.Contains('OpenTabGuard.AlsoPathArg') -and $sync.Contains('check(spellings)') -and $sync.Contains('reload(spellings)')) 'editor_tab_sync takes also_path'

$prog = [System.IO.File]::ReadAllText($programPath)
$setWs = $prog.IndexOf('registry.SetWorkspace(workspace, ui);')
$wsBlock = if ($setWs -ge 0) { $prog.LastIndexOf('if (workspace != null)', $setWs) } else { -1 }
$probeAt = $prog.IndexOf('OpenTabGuard.Probe = ClarionAssistant.Services.IdeTabClient.Probe')
Check ($probeAt -ge 0 -and $wsBlock -ge 0 -and $probeAt -lt $wsBlock -and
       $prog.Contains('OpenTabGuard.Reload = ClarionAssistant.Services.IdeTabClient.Reload')) `
    'the standalone wires OpenTabGuard to IdeTabClient, with or without a workspace'

$tab = Text 'Services\IdeTabClient.cs'
Check ($tab.Contains('IdeEndpointRecord.DiscoverAll(') -and $tab.Contains('"editor_tab_sync"') -and $tab.Contains('"reload"')) `
    'the standalone asks every trusted IDE pane through editor_tab_sync'
Check ($tab.Contains('_fanOut.Ask(eps') -and $tab.Contains('OpenTabGuard.AlsoPathArg') -and $tab.Contains('TimedOut = IsTimeout(ex)')) `
    'the IDEs are asked in parallel (IdeFanOut), with both spellings, and a timeout is reported as one'

$addinProj = Text 'ClarionAssistant.csproj'
$mcpProj = Text 'mcp-server\ClarionMcpServer.csproj'
Check ($addinProj.Contains('Services\OpenTabGuard.cs') -and $addinProj.Contains('NativeTabSync.cs') -and $addinProj.Contains('Services\FileIdentity.cs') -and
       $addinProj.Contains('Services\EndpointOwnership.cs') -and $addinProj.Contains('IdeCallbackHost.cs') -and $addinProj.Contains('UiThreadCall.cs')) `
    'the addin compiles the guard, the tab sync, file identity, the endpoint ownership, the call-back host and the UI helper'
Check ($mcpProj.Contains('..\Services\OpenTabGuard.cs') -and $mcpProj.Contains('..\Services\IdeTabClient.cs') -and $mcpProj.Contains('..\Services\IdeFanOut.cs')) `
    'the standalone compiles OpenTabGuard, IdeTabClient and IdeFanOut'

Write-Host ""
Write-Host "OpenTabGuard.SourceScan: $pass passed, $fail failed"
if ($fail -gt 0) { exit 1 } else { exit 0 }
