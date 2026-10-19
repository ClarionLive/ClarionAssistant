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
$snap = Method $nts 'private static List<TabSnap> SnapshotOnUi('
$classify = Method $nts 'private static TabProbe Classify('
$match = Method $nts 'private static List<TabSnap> Match('
$reload = Method $nts 'private static TabReload ReloadOnUi('
$keep = Method $nts 'private static int ReloadKeepingCaret('
$views = Method $nts 'private static System.Collections.IEnumerable OpenViews('
$checkM = Method $nts 'private static TabProbe CheckWith('
Check ($snap.Contains('me.OverlayUp') -and $snap.Contains('me.OverlayDirty') -and $snap.Contains('w.IsDirty') -and
       (Before $classify 'if (s.OverlayUp)' 'else { nativeOpen = true; if (s.Dirty)')) `
    'a tab whose CA Editor is up is classified ca-editor (its native shell stays clean, so IsDirty would lie); others by IsDirty'
Check ((Before $reload 's.View.IsDirty' 'ReloadKeepingCaret(') -and $reload.Contains('unsaved changes now')) 'a reload re-checks IsDirty first and refuses a dirty tab'
Check ((Before $reload 'me.OverlayUp' 'ReloadKeepingCaret(') -and (Before $reload 'open.Contains(s.View)' 'ReloadKeepingCaret(')) `
    'a reload re-checks there that the tab is still open and not covered by the CA Editor'
Check ($reload.Contains('string own = s.File;') -and $reload.Contains('ReloadKeepingCaret(s.View, own)')) 'a reload loads the TAB''S OWN file name (an alias tab keeps its title)'
Check ($keep.Contains('w.Load(path)') -and (Before $keep 'caret.Line' 'w.Load(path)') -and (Before $keep 'w.Load(path)' 'area.Caret.Line =')) `
    'the reload is SharpDevelop''s own Load, with the caret line captured before and put back after'
# Pipeline run 1, fix 1: matched by FILE IDENTITY, and every view swept (FileService only matches path text).
Check ($match.Contains('FileIdentity.MatchesAny(s.File, probes)') -and $views.Contains('ViewContentCollection')) `
    'tabs are matched by file identity, over every open view'
# Pipeline run 2, fix 2: no file I/O on the UI thread - the UI call only snapshots; identity is matched outside it.
Check (-not $snap.Contains('FileIdentity') -and -not $reload.Contains('FileIdentity') -and -not $snap.Contains('File.Exists') -and -not $reload.Contains('File.Exists')) `
    'the UI-thread parts (snapshot, reload) do no file-identity or disk I/O'
Check ((Before $checkM 'snaps = SnapshotOnUi()' 'matched = match(snaps); answer = Classify(matched);') -and $nts.Contains('CheckWith(paths, snaps => Match(snaps, Probes(paths)), "check"') -and -not $checkM.Contains('UiThreadCall.Run(_ui, () => { answer = Classify')) `
    'the check matches identities AFTER the UI call returns, not inside it'
Check ($checkM.Contains('TimedOut = outcome == UiThreadCall.Outcome.TimedOut') -and -not $nts.Contains('Contains("did not answer")')) `
    'a timeout is taken from UiThreadCall''s outcome, not from message text'
$fid = Text 'Services\FileIdentity.cs'
Check ($fid.Contains('if (IsNetworkPath(candidate)) return false;')) 'a network tab is never opened (matched by its text only)'
Check ($match.Length -gt 0 -and -not ($match -match 'string\.Equals|SamePath|PathsEqual')) 'no tab is matched by comparing path text'
# The only Invoke( allowed is a reflected watcher MethodInfo's (_watcherDisable/_watcherSetWatcher/_watcherOnChanged), run
# inside a UiThreadCall: never Control.Invoke/BeginInvoke.
$ntsNoReflect = [regex]::Replace($nts, '_watcher(Disable|SetWatcher|OnChanged)\.Invoke\(', '')
Check ($nts.Contains('UiThreadCall.Run(_ui') -and -not $nts.Contains('.Post(') -and -not $ntsNoReflect.Contains('Invoke(')) 'NativeTabSync goes through the shared UiThreadCall'
$uitc = Text 'UiThreadCall.cs'
Check ($uitc.Contains('ui.Post(') -and $uitc.Contains('done.Wait(waitMs)') -and -not $uitc.Contains('.Send(') -and -not $uitc.Contains('Invoke(')) `
    'UI work is Posted with a bounded wait, never Send/Invoke'
$elp = Text 'EditorLiveTextProvider.cs'
Check ($elp.Contains('UiThreadCall.Run(ui') -and $elp.Contains('FileIdentity.SameFile(a, b)') -and -not $elp.Contains('ui.Post(')) `
    'EditorLiveTextProvider shares the UI helper and the identity compare (no copy)'

# Live test 2026-10-19 (C12): our Load re-arms the IDE's FileChangeWatcher but left its private wasChangedExternally set,
# so on the next activation the IDE auto-reloaded again (caret to line 1) or asked "changed externally, reload?".
# MUST GO RED against 4761c93 (integ-1017 before this fix).
$resolveM = Method $nts 'private static void ResolveWatcherMembers('
$resetM = Method $nts 'private static void WatcherReset('
$holdM = Method $nts 'private static void HoldWatchers('
$holdUiM = Method $nts 'private static int HoldOnUi('
$releaseUiM = Method $nts 'private static string ReleaseOnUi('
$releaseM = Method $nts 'private static void FinishRelease('
$reloadW = Method $nts 'internal static TabReload Reload('
Check ((Before $keep 'w.Load(path)' 'WatcherReset(w)') -and (Before $keep 'WatcherReset(w)' 'area.Caret.Line =')) `
    'after a successful Load the IDE watcher is reset (before the caret is put back)'
Check ($resetM.Contains('_watcherChanged.SetValue(watcher, false)') -and $resetM.Contains('_book.AfterReload(w, out stillHeld, out ended)') -and $resetM.Contains('DisposeCap(ended)')) `
    'the reset clears the IDE''s wasChangedExternally and drops one holder (Load already re-armed the watcher)'
Check ($resolveM.Contains('GetField("watcher", inst)') -and $resolveM.Contains('"wasChangedExternally"') -and $resolveM.Contains('"fileName"') -and
       $resolveM.Contains('"Disable"') -and $resolveM.Contains('"SetWatcher"') -and $resolveM.Contains('"OnFileChangedEvent"') -and
       $resolveM.Contains('[tab-sync] WATCHER MEMBER MISSING') -and $resolveM.Contains('missing.Add("FileChangeWatcher.wasChangedExternally (bool)")') -and
       $resolveM.Contains('missing.Add("FileChangeWatcher.SetWatcher(string)")') -and $resolveM.Contains('missing.Add("FileChangeWatcher.Disable()")')) `
    'every watcher member is looked up by reflection and a missing one is logged loudly (WATCHER MEMBER MISSING)'
Check ($resetM.Contains('if (!CanReset) { MonacoSpikeLog.Write("[tab-sync] watcher NOT reset after reload (WATCHER MEMBER MISSING') -and
       $holdM.Contains('if (!CanHold) { MonacoSpikeLog.Write("[tab-sync] watcher NOT held (WATCHER MEMBER MISSING')) `
    'a reset or hold that cannot run because a member is missing says so in the log, and degrades (no throw)'
Check ($checkM.Contains('if (holdWatchers && answer.Reachable && answer.Kind == OpenTabGuard.KindNative && !answer.Dirty && matched != null)') -and
       $nts.Contains('CheckWith(paths, snaps => Match(snaps, Probes(paths)), "check", holdPaths)') -and $nts.Contains('CheckWith(paths, snaps => MatchLexical(snaps, paths), "check-lexical", null)')) `
    'a write-path check that finds a CLEAN native tab holds its watcher before the write (never the lexical check of a refused path)'
Check ($holdUiM.Contains('_watcherDisable.Invoke(watcher, null)') -and $holdUiM.Contains('s.View.IsDirty') -and $holdUiM.Contains('me.OverlayUp') -and
       $holdM.Contains('if (sw.ElapsedMilliseconds <= HoldWaitMs)') -and $holdM.Contains('FileIdentity.IsNetworkPath(p)') -and $holdM.Contains('Stamp(statPath, out ticks, out length)')) `
    'the hold disables the watcher of a re-checked clean tab, stamps the file off the UI thread (never a network path), and a late hold does nothing'
Check ($nts -match 'HoldExpiryMs\s*=\s*30000;' -and $nts.Contains('new WatcherHoldBook(HoldExpiryMs)') -and
       $reloadW.Contains('DropHolder(s.View,') -and (Before $reloadW 'ReloadOnUi(natives)' 'DropHolder(s.View,')) `
    'a hold is never left behind: it is capped, and a reload that did not happen drops its holder at once'
Check ($releaseUiM.Contains('_watcherSetWatcher.Invoke(watcher, new object[] { file })') -and (Before $releaseUiM '_watcherSetWatcher.Invoke(' '_watcherOnChanged.Invoke(') -and
       $releaseUiM.Contains('if (!changed) return') -and
       $releaseM.Contains('bool changed = WatcherHoldBook.Changed(e, stampOk, t, l);') -and $releaseM.Contains('bool stampOk = Stamp(e.StatPath, out t, out l);')) `
    'a released hold re-arms the watcher and, if the file changed meanwhile, replays the IDE''s own change notice'

# ---- GH #205 review of 58aa198. Each of these MUST GO RED against 6ddde38. ----
$guardSrc205 = Text 'Services\OpenTabGuard.cs'
$runM = $guardSrc205.Substring([Math]::Max(0, $guardSrc205.IndexOf('public static string Run(string path, string askedPath')))
$runM = $runM.Substring(0, [Math]::Max(0, $runM.IndexOf('private static string ReloadNote(')))
$tabSrc205 = Text 'Services\IdeTabClient.cs'
# [SEC MED] the hold is the write path's, and only for a write-gated spelling.
$checkAt = $sync.IndexOf('if (action == "check")')
$checkBlock = if ($checkAt -ge 0) { $sync.Substring($checkAt) } else { '' }
Check ($checkBlock.Contains('OpenTabGuard.ParseFlag(args, OpenTabGuard.HoldArg)') -and
       $checkBlock.Contains('McpPathGate.GatePath("editor_tab_sync", sp, PathAccess.Write, _workspace, out writeCanonical') -and
       $checkBlock.Contains('holdCheck(paths, holdable)') -and $checkBlock.Contains(': OpenTabGuard.LocalCheck;') -and
       (Before $checkBlock 'PathAccess.Write' 'OpenTabGuard.CheckGated(gated')) `
    '[SEC MED] editor_tab_sync check holds only with hold:true, and only spellings that pass the #266 WRITE gate (a plain check never holds)'
Check ($nts.Contains('OpenTabGuard.LocalCheck = CheckOnly;') -and $nts.Contains('OpenTabGuard.LocalCheckHold = CheckAndHold;') -and
       $checkM.Contains('HoldWatchers(Match(snaps, Probes(holdPaths)), holdPaths)') -and
       (Method $nts 'internal static TabProbe CheckOnly(').Contains('"check", null)')) `
    '[SEC MED] the IDE holds only tabs matched against the write-allowed spellings, never via a read-only also_path'
Check ($tabSrc205.Contains('args[OpenTabGuard.HoldArg] = true;') -and (Before $tabSrc205 'args[OpenTabGuard.HoldArg] = true;' 'CallTool(ep, "editor_tab_sync", args, one, CheckBudgetMs)')) `
    '[SEC MED] the standalone''s write-path check is the one that asks for the hold'
Check ($holdUiM.Contains('_book.Hold(s.View, watching, statPath, ticks, length,') -and $holdUiM.Contains('cap.Change(_book.DueMs(e), Timeout.Infinite);') -and
       (Before $holdUiM 'if (take == WatcherHoldBook.Take.NewHold)' 'cap.Change(') -and -not ($nts -match 'Expiry\.Change\(HoldExpiryMs') -and
       -not $holdUiM.Contains('HoldExpiryMs')) `
    '[SEC MED] the cap timer is set once, by the FIRST holder, and never renewed by a later check'
# [MED F1-1] a refused or failed write releases at once.
Check ((Before $runM 'if (d.Refusal != null) { ReleaseHolds(spellings, probes); return d.Refusal; }' 'string result;') -and
       $runM.Contains('catch { ReleaseHolds(spellings, probes); throw; }') -and
       $runM.Contains('if (result.StartsWith("Error", StringComparison.Ordinal)) { ReleaseHolds(spellings, probes); return result; }')) `
    '[F1-1] OpenTabGuard.Run releases the hold on a refusal, a failed write and a throwing write'
Check ($sync.Contains('if (action == "release")') -and $sync.Contains('OpenTabGuard.LocalRelease') -and $nts.Contains('OpenTabGuard.LocalRelease = Release;') -and
       $nts.Contains('OpenTabGuard.Release = (paths, tab) => Release(paths, null);') -and
       (Method $nts 'internal static TabReload Release(').Contains('DropHolder(s.View, "the writer released it')) `
    '[F1-1] editor_tab_sync has action ''release'', and the IDE drops the holder of every matched tab'
Check (([System.IO.File]::ReadAllText($programPath)).Contains('OpenTabGuard.Release = ClarionAssistant.Services.IdeTabClient.Release;') -and
       $tabSrc205.Contains('Args(paths, "release")') -and $tabSrc205.Contains('OpenTabGuard.IsOlderAddinReleaseReply(text)') -and
       $guardSrc205.Contains('OldActionReply = "Error: action must be ''check'' or ''reload''.";')) `
    '[F1-1] the standalone sends release to the IDE, and tolerates an older addin''s Unknown tool / unknown action silently'
# [SEC LOW] replay whenever the file changed, even when the IDE re-armed the watcher meanwhile.
$rearmAt = $releaseUiM.IndexOf('bool rearmedByIde = fsw != null && fsw.EnableRaisingEvents;')
$unchAt = $releaseUiM.IndexOf('if (!changed) return')
$between = if ($rearmAt -ge 0 -and $unchAt -gt $rearmAt) { $releaseUiM.Substring($rearmAt, $unchAt - $rearmAt) } else { '' }
# Between "is it re-armed" and "did it change", the only way out is a tab with no file name: re-armed never returns early.
Check ($between.Length -gt 0 -and ([regex]::Matches($between, '\breturn\b').Count -eq 1) -and $between.Contains('if (string.IsNullOrEmpty(file)) return "the tab has no file name";') -and
       (Before $releaseUiM 'if (!rearmedByIde)' 'if (!changed) return') -and (Before $releaseUiM 'if (!changed) return' '_watcherOnChanged.Invoke(')) `
    '[SEC LOW] a release replays the change notice whenever the file changed since the hold began, even if the IDE re-armed it'
# [LOW F1-2] holders are counted; re-armed only at 0 (or the cap).
Check ($resetM.Contains('if (stillHeld && CanHold) _watcherDisable.Invoke(watcher, null);') -and
       (Method $nts 'private static bool DropHolder(').Contains('var e = _book.Drop(view, why);') -and
       (Text 'Services\WatcherHoldBook.cs').Contains('if (--h.Count > 0) return null;') -and (Text 'Services\WatcherHoldBook.cs').Contains('h.Count++;')) `
    '[F1-2] concurrent writes share one counted hold: a reload with holders left disables the watcher again; only the last releases'
# [LOW F1-3] a generation per hold; the release acts only for its own; HoldOnUi decides under the book's lock.
Check ($releaseUiM.Contains('if (!_book.Finish(e.Key, gen)) return') -and $releaseM.Contains('ReleaseOnUi(e, gen, changed)') -and
       $holdUiM.Contains('new System.Threading.Timer(_ => ExpireHold(view, gen)') -and (Method $nts 'private static void ExpireHold(').Contains('_book.Expire(view, gen)') -and
       (Text 'Services\WatcherHoldBook.cs').Contains('if (h.Releasing) return Take.Releasing;')) `
    '[F1-3] each hold has a generation: a late timer or release acts only on its own, and a releasing hold takes no new holder'
Check ((Text 'ClarionAssistant.csproj').Contains('Services\WatcherHoldBook.cs')) 'the addin compiles WatcherHoldBook'
Check (-not $holdUiM.Contains('FileInfo') -and -not $holdUiM.Contains('Stamp(') -and -not $releaseUiM.Contains('FileInfo') -and -not $releaseUiM.Contains('Stamp(') -and
       -not $resetM.Contains('FileInfo') -and -not $resetM.Contains('File.Exists') -and $holdUiM.Length -gt 0 -and $releaseUiM.Length -gt 0) `
    'the watcher''s UI-thread parts (hold, release, reset) do no disk I/O of their own'

$auto = [System.IO.File]::ReadAllText($autostartPath)
Check ($auto.Contains('NativeTabSync.Register()')) 'the addin registers it at startup (LspAutostartCommand)'

# Pipeline run 1, fix 2: the endpoint exists with NO chat pane.
Check ($auto.Contains('IdeCallbackHost.Start()')) 'the IDE call-back endpoint starts at addin start, not in the chat pane'
$cbHost = Text 'IdeCallbackHost.cs'
Check ($src -match 'IdeCallbackTools\s*=\s*\{\s*"get_live_text",\s*"editor_tab_sync"\s*\}' -and $src.Contains('registry.RetainOnly(IdeCallbackTools)') -and
       $cbHost.Contains('McpToolRegistry.BuildIdeCallbackRegistry(')) `
    'the call-back endpoint serves exactly get_live_text and editor_tab_sync'
# Pipeline run 2 (HIGH): WITH a workspace, so the #266 read gate on editor_tab_sync check has the solution's roots.
Check ($cbHost -match 'BuildIdeCallbackRegistry\([^;]*new IdeWorkspace\(\)[^;]*new McpServer\.ControlUiDispatcher\(ui\)\)') `
    'the call-back registry gets the IDE''s workspace and a UI dispatcher'
Check ($src -match 'registry\._workspace = workspace;' -and -not ($src -match 'BuildIdeCallbackRegistry[\s\S]{0,900}?SetWorkspace\(')) `
    'the workspace is set on that registry only (the shared SharedLspBridge providers are not rebound)'
$ws = Text 'IdeWorkspace.cs'
Check ($ws.Contains('EditorService.GetOpenSolutionPath()') -and $ws.Contains('EffectiveClarionVersion.CurrentConfig()') -and $ws.Contains('RedFileService.Active')) `
    'IdeWorkspace reports the IDE''s open solution, effective version and .red'
Check ($cbHost.Contains('ModernEmbeditorLauncher.EnsureRedirectionLoaded()')) 'the call-back host keeps the .red loaded with no chat pane'
Check ($sync.Contains('McpPathGate.GatePath("editor_tab_sync"') -and $sync.Contains('OpenTabGuard.CheckGated(gated, _workspace != null, hasSolution, fullCheck, OpenTabGuard.LocalCheckLexical') -and
       $sync.Contains(': OpenTabGuard.LocalCheck;') -and
       -not $sync.Contains('OutOfScope = _workspace != null')) `
    'the #266 gate stays, but its verdict goes to CheckGated: it never short-circuits the tab lookup'
Check ($sync.Contains('McpPathAudit.Write("editor_tab_sync", "LOOKUP"')) 'answering a refused path is written to the #266 audit log'
$live = Block 'get_live_text'
Check ((Before $live 'McpPathGate.GatePath("get_live_text"' 'SharedLspBridge.LiveTextProvider') -and $live.Contains('if (liveRefused != null) return liveRefused;')) `
    'get_live_text (it returns buffer TEXT) is read-gated before any editor is read'
$policySrc = Text 'Services\McpPathPolicy.cs'
$guardSrc = Text 'Services\OpenTabGuard.cs'
$gateSrc = Text 'Services\McpPathGate.cs'
Check ($policySrc.Contains('"it is outside every allowed root (') -and $gateSrc.Contains('OutsideRootsReason = "it is outside every allowed root";') -and
       $gateSrc.Contains('outsideRoots = d.Reason != null && d.Reason.StartsWith(OutsideRootsReason') -and -not $guardSrc.Contains('OutsideRootsMarker')) `
    'out of scope comes from the gate''s structured flag (the decision''s Reason, pinned to McpPathPolicy''s wording), never the refusal text'
$disp = Text 'Services\McpDispatcher.cs'
Check ($disp.Contains('"Error executing tool ''" + toolName + "'': " + ex.Message') -and $src.Contains('throw new ArgumentException("Unknown tool: " + name);') -and
       $guardSrc.Contains('UnknownToolReply = "Error executing tool ''editor_tab_sync'': Unknown tool: editor_tab_sync";') -and $guardSrc.Contains('string.Equals(reply.Trim(), UnknownToolReply, StringComparison.Ordinal)') -and (Text 'Services\IdeTabClient.cs').Contains('OpenTabGuard.FromCheckReply(text,')) `
    'an older addin''s "Unknown tool" reply (the dispatcher''s real wording) is recognised by the standalone'
Check ($nts.Contains('OpenTabGuard.LocalCheckLexical = CheckLexical;') -and $nts.Contains('FileIdentity.LexicalMatch(s.File, paths)') -and
       -not (Method $nts 'private static List<TabSnap> MatchLexical(').Contains('GetFullPath')) `
    'the lexical lookup compares path text only (no FileIdentity query, no GetFullPath disk touch)'
Check ($nts -match 'ReloadSnapshotWaitMs\s*=\s*1500;' -and $nts -match 'ReloadWaitMs\s*=\s*7500;' -and (Text 'Services\IdeTabClient.cs') -match 'ReloadBudgetMs\s*=\s*10000;') `
    'the IDE-side reload waits (1.5 + 7.5 s) fit inside the client''s 10 s budget'
$recSrc = Text 'Services\IdeEndpointRecord.cs'
Check ($recSrc.Contains('TryInt(rec, "pid", out pid)') -and -not ($recSrc -match 'Convert\.ToInt32\(v\)')) 'an endpoint record''s pid/port is parsed, never Convert.ToInt32''d'
$guard = Text 'Services\OpenTabGuard.cs'
Check ($guard.Contains('if (p.Answered) failedChecks.Add(who); else unreachable.Add(who);') -and $guard.Contains('"Error: could not check whether "')) `
    'an IDE that answered with a failed check refuses the write (fail closed)'
$rec = Text 'Services\IdeEndpointRecord.cs'
$pubAt = $rec.IndexOf('public static void Publish(')
Check ($pubAt -ge 0 -and $rec.IndexOf('SweepStale();', $pubAt) -gt $pubAt -and $rec.IndexOf('SweepStale();', $pubAt) -lt $rec.IndexOf('File.WriteAllText', $pubAt)) `
    'every publish first sweeps records of IDEs that died without Stop'
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
Check ($sync.Contains('OpenTabGuard.AlsoPathArg') -and $sync.Contains('foreach (var sp in spellings)') -and
       $sync.Contains('McpPathGate.GatePath("editor_tab_sync", sp, PathAccess.Read, _workspace, out canonical, out unusedPolicy, out outsideRoots)')) `
    'editor_tab_sync takes also_path, and gates EVERY spelling (pipeline run 4)'
Check ($sync.Contains('reload(byIdentity, byName)') -and (Before $sync 'foreach (var sp in spellings)' 'if (action == "reload")')) `
    'reload is gated the same way: refused spellings only match an open tab by name'
$fidSrc = Text 'Services\FileIdentity.cs'
$ofAt = $fidSrc.IndexOf('public static Probe Of(')
$queryAt = $fidSrc.IndexOf('private static void Query(')
Check ($ofAt -ge 0 -and $fidSrc.IndexOf('if (IsNetworkPath(path)) { p.Network = true; return p; }', $ofAt) -gt $ofAt -and
       $fidSrc.IndexOf('if (IsNetworkPath(path)) return;', $queryAt) -gt $queryAt -and $fidSrc.Contains('if (IsNetworkPath(candidate)) return false;')) `
    'FileIdentity never opens a network path (Of, MatchesAny, and Query itself as the floor)'

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
