# CloseTiming.SourceScan.ps1 - 4d63b995: the Clarion IDE took ~20 s to close.
#
# The close path runs only inside a closing IDE, so this checks the C# that shapes it.
#
# C1  lazy panel: InitializeComponents creates no SchemaSourcesView and adds none to Controls; the one
#     `new SchemaSourcesView` is in EnsureSchemaView, which seeds PaneHeight and ZoomFactor from the header,
#     wires ActionReceived / Ready / ZoomChanged, themes it, and docks it at the header's child index;
#     OnHeaderTab calls EnsureSchemaView for a panel tab BEFORE its SchemaViewAlive check; the header's
#     LayoutChanged subscription stays in InitializeComponents behind SchemaViewAlive.
# C2  LSP stop off the UI thread: OnSolutionClosed calls StopInBackground, never Stop(); SymbolIndex.ReleaseAll
#     runs first; StopInBackground clears _running and sets _stopRequested BEFORE its Task.Run(... Stop() ...).
# C3  close-timing log: ShutdownLog.Close starts its clock only from startsClose (a CompareExchange) and
#     swallows everything; ShutdownService hooks the workbench FormClosing with startsClose: true (retried on
#     Application.Idle) and AppDomain ProcessExit; OnSolutionClosed logs begin and end; every step of the chat
#     pad's Dispose is preceded by a Close line, and the last line is "pad dispose: done".
#
# PROVES IT CAN FAIL: after the real scan passes, the same scan runs on temp copies with one planted
# mutation each (listed at the bottom); every one must go red.
#
# Exit: 0 pass, 1 fail, 2 could-not-run.

param(
    [string]$Chat = (Join-Path $PSScriptRoot '..\AssistantChatControl.cs'),
    [string]$Autostart = (Join-Path $PSScriptRoot '..\LspAutostartCommand.cs'),
    [string]$Client = (Join-Path $PSScriptRoot '..\Services\LspClient.cs'),
    [string]$Log = (Join-Path $PSScriptRoot '..\Services\ShutdownLog.cs'),
    [string]$Shutdown = (Join-Path $PSScriptRoot '..\Services\ShutdownService.cs')
)

$ErrorActionPreference = 'Stop'
foreach ($f in $Chat, $Autostart, $Client, $Log, $Shutdown) {
    if (-not (Test-Path $f)) { Write-Host "COULD NOT RUN: missing $f" -ForegroundColor Red; exit 2 }
}

# The text from $signature to its matching close brace (brace counting; the bodies scanned here hold no
# unbalanced braces inside strings).
function Get-Body([string]$src, [string]$signature) {
    $start = $src.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $open = $src.IndexOf('{', $start)
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($j = $open; $j -lt $src.Length; $j++) {
        if ($src[$j] -eq '{') { $depth++ }
        elseif ($src[$j] -eq '}') { $depth--; if ($depth -eq 0) { return $src.Substring($start, $j - $start + 1) } }
    }
    return $null
}
function Idx([string]$s, [string]$find) { if (-not $s) { return -1 }; return $s.IndexOf($find, [StringComparison]::Ordinal) }

function Invoke-Scan($p) {
    $fails = New-Object System.Collections.Generic.List[string]
    $chat = [IO.File]::ReadAllText($p.Chat); $auto = [IO.File]::ReadAllText($p.Autostart)
    $cli = [IO.File]::ReadAllText($p.Client); $log = [IO.File]::ReadAllText($p.Log); $sd = [IO.File]::ReadAllText($p.Shutdown)

    # C1
    $init = Get-Body $chat 'private void InitializeComponents('
    if (-not $init) { $fails.Add('C1 InitializeComponents not found (scan broken?)') }
    else {
        if ($init -match 'new SchemaSourcesView\b') { $fails.Add('C1 InitializeComponents creates the SchemaSourcesView (it must be lazy)') }
        if ($init -match 'Controls\.Add\(_schemaView\)') { $fails.Add('C1 InitializeComponents adds _schemaView to Controls') }
        if ($init -notmatch '_header\.LayoutChanged \+= \(s, e\) =>\s*\{\s*if \(!SchemaViewAlive\) return;') { $fails.Add('C1 the header LayoutChanged subscription is not in InitializeComponents behind SchemaViewAlive') }
    }
    $news = ([regex]::Matches($chat, 'new SchemaSourcesView\b')).Count
    if ($news -ne 1) { $fails.Add("C1 expected exactly one new SchemaSourcesView, found $news") }
    $ens = Get-Body $chat 'private void EnsureSchemaView('
    if (-not $ens) { $fails.Add('C1 EnsureSchemaView not found') }
    else {
        foreach ($need in @('if (SchemaViewAlive', 'new SchemaSourcesView', 'PaneHeight = _header.PanePixelHeight', 'ZoomFactor = _header.ZoomFactor',
                            'ActionReceived += OnSchemaSourceAction;', 'Ready += OnSchemaSourcesReady;', 'ZoomChanged +=', 'SetTheme(_isDarkTheme);',
                            'Controls.Add(_schemaView);', 'Controls.SetChildIndex(_schemaView, Controls.GetChildIndex(_header));')) {
            if (-not $ens.Contains($need)) { $fails.Add("C1 EnsureSchemaView lacks: $need") }
        }
        if ((Idx $ens 'Controls.SetChildIndex(') -lt (Idx $ens 'Controls.Add(_schemaView);')) { $fails.Add('C1 EnsureSchemaView sets the child index before adding the panel') }
    }
    $tab = Get-Body $chat 'private void OnHeaderTab('
    $call = Idx $tab 'if (show) EnsureSchemaView();'
    $alive = Idx $tab 'if (!SchemaViewAlive) return;'
    if ($call -lt 0) { $fails.Add('C1 OnHeaderTab does not create the panel for a panel tab') }
    elseif ($alive -lt 0 -or $alive -lt $call) { $fails.Add('C1 OnHeaderTab returns on SchemaViewAlive before creating the panel') }
    $disp = Get-Body $chat 'protected override void Dispose(bool disposing)'
    if (-not $disp -or -not $disp.Contains('if (_schemaView != null) { _schemaView.Dispose(); _schemaView = null; }')) { $fails.Add('C1 Dispose is not null-safe for the never-created panel') }

    # C2
    $osc = Get-Body $auto 'private static void OnSolutionClosed('
    if (-not $osc) { $fails.Add('C2 OnSolutionClosed not found') }
    else {
        if (-not $osc.Contains('.StopInBackground(')) { $fails.Add('C2 OnSolutionClosed does not stop the LSP in the background') }
        if ($osc -match '\.Stop\(\)') { $fails.Add('C2 OnSolutionClosed still calls Stop() on the UI thread') }
        $rel = Idx $osc 'SymbolIndex.ReleaseAll();'
        if ($rel -lt 0 -or $rel -gt (Idx $osc '.StopInBackground(')) { $fails.Add('C2 SymbolIndex.ReleaseAll does not run first') }
        if ((Idx $osc 'ShutdownLog.Close("OnSolutionClosed begin")') -lt 0 -or (Idx $osc 'ShutdownLog.Close("OnSolutionClosed end")') -lt 0) { $fails.Add('C3 OnSolutionClosed does not log begin and end') }
    }
    $sib = Get-Body $cli 'public void StopInBackground('
    if (-not $sib) { $fails.Add('C2 LspClient.StopInBackground not found') }
    else {
        $run = Idx $sib 'Task.Run('
        $a = Idx $sib '_running = false;'; $b = Idx $sib '_stopRequested = true;'
        if ($run -lt 0 -or (Idx $sib 'Stop();') -lt $run) { $fails.Add('C2 StopInBackground does not run Stop() on the pool') }
        elseif ($a -lt 0 -or $b -lt 0 -or $a -gt $run -or $b -gt $run) { $fails.Add('C2 StopInBackground does not clear _running / set _stopRequested synchronously') }
    }

    # C3
    $close = Get-Body $log 'public static void Close(string step, bool startsClose = false)'
    if (-not $close) { $fails.Add('C3 ShutdownLog.Close not found') }
    else {
        if ($close -notmatch 'if \(startsClose\) Interlocked\.CompareExchange\(ref _closeStartTicks, now, 0\);') { $fails.Add('C3 the close clock is not CAS-started by startsClose alone') }
        if (([regex]::Matches($close, '_closeStartTicks')).Count -ne 2) { $fails.Add('C3 ShutdownLog.Close writes the close clock outside the startsClose CAS') }
        if ($close -notmatch 'catch \{ \}\s*\}$') { $fails.Add('C3 ShutdownLog.Close can throw') }
        if (-not $close.Contains('"[close +"') -or -not $close.Contains('"[close] "')) { $fails.Add('C3 ShutdownLog.Close does not write "[close +N ms]" / "[close]"') }
    }
    $hook = Get-Body $sd 'private static bool TryHookMainFormClosing('
    if (-not $hook -or $hook -notmatch 'WorkbenchSingleton\.Workbench as Form' -or $hook -notmatch 'FormClosing \+= .*ShutdownLog\.Close\(.*startsClose: true\)') { $fails.Add('C3 the workbench FormClosing does not start the close clock') }
    $hct = Get-Body $sd 'private static void HookCloseTiming('
    if (-not $hct -or -not $hct.Contains('AppDomain.CurrentDomain.ProcessExit +=') -or -not $hct.Contains('Application.Idle +=')) { $fails.Add('C3 HookCloseTiming does not hook ProcessExit and retry the form on Application.Idle') }
    $arm = Get-Body $sd 'public static void ArmBackstop('
    if (-not $arm -or -not $arm.Contains('HookCloseTiming();')) { $fails.Add('C3 ArmBackstop does not hook the close timing') }
    if (([regex]::Matches($sd + $chat + $auto, 'startsClose: true')).Count -ne 1) { $fails.Add('C3 something other than the workbench FormClosing starts the close clock') }
    if (-not $disp) { $fails.Add('C3 Dispose not found') }
    else {
        foreach ($step in @('_tabManager.Dispose()', '_mcpServer.Dispose()', '_knowledgeService.Dispose()', '_instanceCoord.Dispose()', '_homeView.Dispose()', '_schemaView.Dispose()', '_header.Dispose()')) {
            $si = Idx $disp $step
            if ($si -lt 0) { $fails.Add("C3 Dispose has no $step (scan broken?)"); continue }
            $prevLine = $disp.LastIndexOf("`n", $si); $prevLine = $disp.LastIndexOf("`n", [Math]::Max($prevLine - 1, 0))
            if ($disp.Substring($prevLine, $si - $prevLine) -notmatch 'ShutdownLog\.Close\("pad dispose: ') { $fails.Add("C3 Dispose does not log the step before $step") }
        }
        if ((Idx $disp 'ShutdownLog.Close("pad dispose: done")') -lt (Idx $disp '_header.Dispose()')) { $fails.Add('C3 Dispose does not log its end') }
    }

    return , $fails
}

$paths = @{ Chat = $Chat; Autostart = $Autostart; Client = $Client; Log = $Log; Shutdown = $Shutdown }
$real = Invoke-Scan $paths
if ($real.Count -gt 0) {
    Write-Host "FAIL - the real sources:" -ForegroundColor Red
    $real | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  PASS  C1-C3 on the real sources (lazy Schema panel, LSP stop off the UI thread, close-timing log)"

# ---- prove the scan can fail ----
$tmp = Join-Path $env:TEMP ("ca-closescan-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$proofFailed = $false
function Test-Mutation([string]$label, [string]$which, [string]$find, [string]$replace) {
    $p = $paths.Clone()
    $src = [IO.File]::ReadAllText($p[$which])
    if (-not $src.Contains($find)) { Write-Host "  FAIL  proof '$label': mutation anchor not found" -ForegroundColor Red; $script:proofFailed = $true; return }
    $mut = Join-Path $tmp ($which + [IO.Path]::GetExtension($p[$which]))
    [IO.File]::WriteAllText($mut, $src.Replace($find, $replace))
    $p[$which] = $mut
    $r = Invoke-Scan $p
    if ($r.Count -gt 0) { Write-Host "  PASS  proof '$label' goes red: $($r[0])" }
    else { Write-Host "  FAIL  proof '$label' stayed green" -ForegroundColor Red; $script:proofFailed = $true }
}
try {
    Test-Mutation 'panel created in the ctor again' 'Chat' "            _tabStrip = new Panel" "            _schemaView = new SchemaSourcesView { Visible = false };`r`n            _tabStrip = new Panel"
    Test-Mutation 'panel added in the ctor again' 'Chat' "            Controls.Add(_tabStrip);`r`n" "            Controls.Add(_tabStrip);`r`n            Controls.Add(_schemaView);`r`n"
    Test-Mutation 'panel tab never creates it' 'Chat' 'if (show) EnsureSchemaView();' ''
    Test-Mutation 'alive check before the create' 'Chat' "            bool show = HeaderWebView.IsPanelTab(tab);`r`n            if (show) EnsureSchemaView();`r`n            if (!SchemaViewAlive) return;" "            if (!SchemaViewAlive) return;`r`n            bool show = HeaderWebView.IsPanelTab(tab);`r`n            if (show) EnsureSchemaView();"
    Test-Mutation 'panel docked above the header' 'Chat' 'Controls.SetChildIndex(_schemaView, Controls.GetChildIndex(_header));' ''
    Test-Mutation 'panel misses the header zoom' 'Chat' 'ZoomFactor = _header.ZoomFactor' 'ZoomFactor = 1.0'
    Test-Mutation 'panel not themed at creation' 'Chat' "            _schemaView.SetTheme(_isDarkTheme);`r`n            // Docking" "            // Docking"
    Test-Mutation 'LSP stopped on the UI thread' 'Autostart' 'c.StopInBackground(m => ShutdownLog.Close(m));' 'c.Stop();'
    Test-Mutation 'StopInBackground leaves IsRunning true' 'Client' "        public void StopInBackground(Action<string> log)`r`n        {`r`n            _stopRequested = true;`r`n            _running = false;" "        public void StopInBackground(Action<string> log)`r`n        {`r`n            _stopRequested = true;"
    Test-Mutation 'StopInBackground runs Stop inline' 'Client' 'System.Threading.Tasks.Task.Run(() =>' 'new Action(() =>'
    Test-Mutation 'no OnSolutionClosed end line' 'Autostart' 'ShutdownLog.Close("OnSolutionClosed end");' ''
    Test-Mutation 'clock started by every line' 'Log' 'if (startsClose) Interlocked.CompareExchange(ref _closeStartTicks, now, 0);' 'Interlocked.CompareExchange(ref _closeStartTicks, now, 0);'
    Test-Mutation 'Close can throw' 'Log' "                    : `"[close +`" + ((now - start) * 1000 / Stopwatch.Frequency) + `" ms] `" + step);`r`n            }`r`n            catch { }" "                    : `"[close +`" + ((now - start) * 1000 / Stopwatch.Frequency) + `" ms] `" + step);`r`n            }`r`n            finally { }"
    Test-Mutation 'FormClosing does not start the clock' 'Shutdown' ', startsClose: true);' ');'
    Test-Mutation 'no ProcessExit hook' 'Shutdown' 'AppDomain.CurrentDomain.ProcessExit +=' 'AppDomain.CurrentDomain.DomainUnload +='
    Test-Mutation 'close timing never hooked' 'Shutdown' "            HookCloseTiming();`r`n        }" "        }"
    Test-Mutation 'solution close starts the clock' 'Autostart' 'ShutdownLog.Close("OnSolutionClosed end");' 'ShutdownLog.Close("OnSolutionClosed end"); ShutdownLog.Close("switch", startsClose: true);'
    Test-Mutation 'schema dispose step unlogged' 'Chat' "                Services.ShutdownLog.Close(`"pad dispose: schema view`" + (_schemaView != null ? `"`" : `" (never created)`"));`r`n" ''
    Test-Mutation 'no pad dispose end line' 'Chat' 'Services.ShutdownLog.Close("pad dispose: done");' ''
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

if ($proofFailed) { exit 1 }
exit 0
