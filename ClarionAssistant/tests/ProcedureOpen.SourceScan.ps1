# ProcedureOpen.SourceScan.ps1 - a964cde3: open_procedure_embed opened the WRONG procedure (asked CheckComma, got
# VerifyInventoryFiles) and failed "ClaList: NOT FOUND" whenever a CA Editor tab was in front.
#
# ProcedureOpenFlow's decisions are tested in ProcedureOpenFlow.Test.cs. This checks the IDE-coupled WIRING that
# test can't reach: every way into the app-tree locator goes through the flow.
#
# W1  AppTreeService.OpenProcedureEmbed (both overloads) runs ProcedureOpenFlow.Open; SelectProcedure runs
#     ProcedureOpenFlow.Select; ClickEmbeditorButton is called from exactly one place, ProcedureOpenOps.ClickEmbeditor.
# W2  The open_procedure_embed handler no longer calls WaitForEmbedOpen (whose result it ignored; the flow waits
#     and checks) and returns the flow's message; get_embed_info adds procedureName.
# W3  OpenAndMirror opens through OpenProcedureEmbedChecked, never the raw OpenProcedureEmbed, and the loose
#     SourceMentionsProcedure acceptance is gone.
# W4  ActivateAppTree raises the tree with SwitchView(0) only when the window's active view is not the app view;
#     ActivateAppView (used while an embeditor must stay in front: save/cancel) never calls SwitchView.
# W5  The post-open identity settles on the header name, the document's name and THIS open's module
#     (PweeEditorDetails.Module) against the requested procedure's module, via ProcedureOpenFlow.DecideOpenedName
#     (pipeline run 1: the reused ClaGenEditor can still describe the previous open). GetOpenEmbeditorProcedureName
#     (get_embed_info) uses GetOpenNativeEmbeditorProcName (non-focus; 44a1b10c).
# W6  ProcedureOpenFlow.cs is compiled into the addin and its test is registered in Run-Tests.ps1.
# W7  OpenProcedureEmbedChecked and SelectProcedure hold ModernEmbeditorLauncher's busy flag for the whole flow
#     (taken before it, released in a finally), so the CA overlay monitor can't attach Monaco to an open that is
#     still being verified - and then cancel under a live WebView2 (pipeline run 1, debugger finding).
#
# PROVES IT CAN FAIL: after the real scan passes, the same scan runs on temp copies with one planted mutation
# each (listed at the bottom); every one must go red.
#
# Exit: 0 pass, 1 fail, 2 could-not-run.

param(
    [string]$AppTree = (Join-Path $PSScriptRoot '..\Services\AppTreeService.cs'),
    [string]$Registry = (Join-Path $PSScriptRoot '..\Services\McpToolRegistry.cs'),
    [string]$Launcher = (Join-Path $PSScriptRoot '..\Services\ModernEmbeditorLauncher.cs'),
    [string]$Project = (Join-Path $PSScriptRoot '..\ClarionAssistant.csproj'),
    [string]$Runner = (Join-Path $PSScriptRoot 'Run-Tests.ps1')
)

$ErrorActionPreference = 'Stop'
foreach ($f in $AppTree, $Registry, $Launcher, $Project, $Runner) {
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

# As Get-Body, for a member whose signature also appears as a one-line wrapper elsewhere: the occurrence
# whose opening brace starts the NEXT line.
function Get-BlockBody([string]$src, [string]$signature) {
    $m = [regex]::Match($src, [regex]::Escape($signature) + '[ \t]*\r?\n[ \t]*\{')
    if (-not $m.Success) { return $null }
    return Get-Body $src.Substring($m.Index) $signature
}

# A tool's Register block: from its Name line to the next Register(new McpTool.
function Get-Tool([string]$src, [string]$name) {
    $start = $src.IndexOf('Name = "' + $name + '"', [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $next = $src.IndexOf('Register(new McpTool', $start, [StringComparison]::Ordinal)
    if ($next -lt 0) { $next = $src.Length }
    return $src.Substring($start, $next - $start)
}

function Invoke-Scan($p) {
    $fails = New-Object System.Collections.Generic.List[string]
    $at = [IO.File]::ReadAllText($p.AppTree); $reg = [IO.File]::ReadAllText($p.Registry)
    $ml = [IO.File]::ReadAllText($p.Launcher); $proj = [IO.File]::ReadAllText($p.Project); $run = [IO.File]::ReadAllText($p.Runner)

    # W1
    $open1 = Get-Body $at 'public string OpenProcedureEmbed(string procedureName)'
    $open2 = Get-Body $at 'public string OpenProcedureEmbed(string procedureName, int charDelayMs)'
    $checked = Get-Body $at 'internal ProcedureOpenResult OpenProcedureEmbedChecked('
    $select = Get-Body $at 'public string SelectProcedure(string procedureName)'
    if (-not $checked -or -not $checked.Contains('ProcedureOpenFlow.Open(')) { $fails.Add('W1 OpenProcedureEmbedChecked does not run ProcedureOpenFlow.Open') }
    foreach ($b in @(@('OpenProcedureEmbed(string)', $open1), @('OpenProcedureEmbed(string,int)', $open2))) {
        if (-not $b[1] -or -not $b[1].Contains('OpenProcedureEmbedChecked(')) { $fails.Add("W1 $($b[0]) does not go through the verified open") }
        elseif ($b[1] -match 'ClickEmbeditorButton|PostMessage|BM_CLICK') { $fails.Add("W1 $($b[0]) drives the locator itself") }
    }
    if (-not $select -or -not $select.Contains('ProcedureOpenFlow.Select(')) { $fails.Add('W1 SelectProcedure does not run ProcedureOpenFlow.Select') }
    elseif ($select -match 'PostMessage') { $fails.Add('W1 SelectProcedure drives the locator itself') }
    $clickCalls = [regex]::Matches($at, '(?<!bool )ClickEmbeditorButton\(').Count
    $ops = Get-Body $at 'private sealed class ProcedureOpenOps : IProcedureOpenOps'
    $opsClick = if ($ops) { Get-Body $ops 'public string ClickEmbeditor()' } else { $null }
    if (-not $ops) { $fails.Add('W1 ProcedureOpenOps not found') }
    if ($clickCalls -ne 1) { $fails.Add("W1 ClickEmbeditorButton has $clickCalls call sites (expected exactly one, in ProcedureOpenOps.ClickEmbeditor)") }
    elseif (-not $opsClick -or -not $opsClick.Contains('ClickEmbeditorButton(')) { $fails.Add('W1 the one ClickEmbeditorButton call is not in ProcedureOpenOps.ClickEmbeditor') }
    if ($ops -and $ops -notmatch 'public bool ActivateAppTree\(\) \{ return _t\.ActivateAppTree\(\); \}') { $fails.Add('W1 ProcedureOpenOps.ActivateAppTree does not call AppTreeService.ActivateAppTree') }

    # W2
    $tool = Get-Tool $reg 'open_procedure_embed'
    if (-not $tool) { $fails.Add('W2 open_procedure_embed not found') }
    else {
        if ($tool.Contains('WaitForEmbedOpen(')) { $fails.Add('W2 open_procedure_embed still calls WaitForEmbedOpen (the flow waits and checks)') }
        if ($tool -notmatch 'string openResult = _appTree\.OpenProcedureEmbed\(name\);') { $fails.Add('W2 open_procedure_embed does not call the verified OpenProcedureEmbed(name)') }
        if ($tool -notmatch 'return warning != null \? warning \+ "\\n\\n" \+ openResult : openResult;') { $fails.Add('W2 open_procedure_embed does not return the flow''s message') }
    }
    $sel = Get-Tool $reg 'select_procedure'
    if (-not $sel -or -not $sel.Contains('return _appTree.SelectProcedure(name);')) { $fails.Add('W2 select_procedure does not return SelectProcedure') }
    $info = Get-Tool $reg 'get_embed_info'
    if (-not $info -or -not $info.Contains('info["procedureName"] = _appTree.GetOpenEmbeditorProcedureName();')) { $fails.Add('W2 get_embed_info does not add procedureName') }

    # W3
    $mirror = Get-Body $ml 'internal static bool OpenAndMirror('
    if (-not $mirror) { $fails.Add('W3 OpenAndMirror not found') }
    else {
        if (-not $mirror.Contains('appTree.OpenProcedureEmbedChecked(procName, CharDelaysMs)')) { $fails.Add('W3 OpenAndMirror does not use the verified open') }
        if ($mirror -match 'appTree\.OpenProcedureEmbed\(') { $fails.Add('W3 OpenAndMirror calls the raw OpenProcedureEmbed') }
        if ($mirror -notmatch 'if \(!opened\.Ok\) \{ error = opened\.Message; return false; \}') { $fails.Add('W3 OpenAndMirror does not stop on a failed verified open') }
    }
    if ($ml.Contains('SourceMentionsProcedure')) { $fails.Add('W3 SourceMentionsProcedure (the loose acceptance) is still present') }

    # W4
    $tree = Get-BlockBody $at 'public bool ActivateAppTree()'
    $view = Get-Body $at 'public bool ActivateAppView()'
    if (-not $tree) { $fails.Add('W4 ActivateAppTree not found') }
    else {
        if (-not $tree.Contains('if (!ActivateAppView()) return false;')) { $fails.Add('W4 ActivateAppTree does not select the window first') }
        if ($tree -notmatch 'if \(active != null && !ReferenceEquals\(active, vc\)\)\s*\{[^}]*SwitchView') { $fails.Add('W4 ActivateAppTree''s SwitchView is not gated on "the app view is not already active"') }
        if (-not $tree.Contains('new object[] { 0 }')) { $fails.Add('W4 ActivateAppTree does not switch to view 0 (the app tree)') }
    }
    if (-not $view) { $fails.Add('W4 ActivateAppView not found') }
    elseif ($view.Contains('SwitchView')) { $fails.Add('W4 ActivateAppView calls SwitchView (it would hide the embeditor on save/cancel)') }

    # W5
    $opsName = if ($ops) { Get-Body $ops 'public string OpenProcedureName(string requested)' } else { $null }
    if (-not $opsName) { $fails.Add('W5 ProcedureOpenOps.OpenProcedureName(requested) not found') }
    else {
        foreach ($need in @('_t.GetProcedureModule(requested)', 'ProcedureOpenFlow.DecideOpenedName(_t.ReadEmbeditorHeaderProcName(), _t.ReadEmbeditorDocumentProcName(),',
                            '_t.ReadOpenEmbedModule(), expectedModule, false, out settled)', 'ProcedureOpenFlow.IdentitySettleMs')) {
            if (-not $opsName.Contains($need)) { $fails.Add("W5 the post-open identity does not settle on header + document + this open's module (lacks: $need)") }
        }
    }
    $modRead = Get-Body $at 'private string ReadOpenEmbedModule()'
    if (-not $modRead -or -not $modRead.Contains('GetProp(GetOpenPweeDetails(), "Module")')) { $fails.Add('W5 ReadOpenEmbedModule does not read the OPEN embed''s PweeEditorDetails.Module') }
    $named = Get-Body $at 'public string GetOpenEmbeditorProcedureName()'
    if (-not $named -or -not $named.Contains('GetOpenNativeEmbeditorProcName()')) { $fails.Add('W5 GetOpenEmbeditorProcedureName does not use GetOpenNativeEmbeditorProcName') }
    if (-not (Get-Body $at 'public string GetOpenNativeEmbeditorProcName()')) { $fails.Add('W5 GetOpenNativeEmbeditorProcName is missing') }

    # W7 busy for the whole flow, so the overlay monitor never attaches to an open being verified (pipeline run 1)
    foreach ($b in @(@('OpenProcedureEmbedChecked', $checked), @('SelectProcedure', $select))) {
        if (-not $b[1]) { continue }
        $e = $b[1].IndexOf('ModernEmbeditorLauncher.EnterBusy();', [StringComparison]::Ordinal)
        $f = $b[1].IndexOf('ProcedureOpenFlow.', [StringComparison]::Ordinal)
        if ($e -lt 0 -or $e -gt $f) { $fails.Add("W7 $($b[0]) does not take the busy flag before the flow runs") }
        $l = $b[1].IndexOf('ModernEmbeditorLauncher.LeaveBusy();', [StringComparison]::Ordinal)
        if ($l -ge 0 -and $l -lt $f) { $fails.Add("W7 $($b[0]) releases the busy flag before the flow runs") }
        if ($b[1] -notmatch 'finally \{ ModernEmbeditorLauncher\.LeaveBusy\(\); \}') { $fails.Add("W7 $($b[0]) does not release the busy flag in a finally") }
    }

    # W6
    if (-not $proj.Contains('<Compile Include="Services\ProcedureOpenFlow.cs" />')) { $fails.Add('W6 ProcedureOpenFlow.cs is not compiled into the addin') }
    if ($run -notmatch 'Name = "ProcedureOpenFlow\.Test"') { $fails.Add('W6 ProcedureOpenFlow.Test is not registered in Run-Tests.ps1') }

    return $fails
}

$paths = @{ AppTree = $AppTree; Registry = $Registry; Launcher = $Launcher; Project = $Project; Runner = $Runner }
$real = Invoke-Scan $paths
if ($real.Count -gt 0) {
    Write-Host "ProcedureOpen.SourceScan: FAIL" -ForegroundColor Red
    $real | ForEach-Object { Write-Host "  [FAIL] $_" -ForegroundColor Red }
    exit 1
}
Write-Host "ProcedureOpen.SourceScan: all wiring checks pass" -ForegroundColor Green

# --- proves it can fail ---
$mutations = @(
    @{ Name = 'open bypasses the flow';         Key = 'AppTree';  From = 'return ProcedureOpenFlow.Open(new ProcedureOpenOps(this), procedureName, charDelaysMs);'; To = 'return new ProcedureOpenResult();' }
    @{ Name = 'select bypasses the flow';       Key = 'AppTree';  From = 'return ProcedureOpenFlow.Select(new ProcedureOpenOps(this), procedureName, ToolCharDelaysMs).Message;'; To = 'return TypeLocator(procedureName, 100);' }
    @{ Name = 'a second click site';            Key = 'AppTree';  From = 'private IntPtr FindVisibleClaList()'; To = 'private void Stray() { ClickEmbeditorButton(null, null); }' + "`n        private IntPtr FindVisibleClaList()" }
    @{ Name = 'handler waits unchecked again';  Key = 'Registry'; From = 'string openResult = _appTree.OpenProcedureEmbed(name);'; To = 'string openResult = _appTree.OpenProcedureEmbed(name); _appTree.WaitForEmbedOpen(45000);' }
    @{ Name = 'get_embed_info loses the name';  Key = 'Registry'; From = 'info["procedureName"] = _appTree.GetOpenEmbeditorProcedureName();'; To = '' }
    @{ Name = 'mirror uses the raw open';       Key = 'Launcher'; From = 'var opened = appTree.OpenProcedureEmbedChecked(procName, CharDelaysMs);'; To = 'appTree.OpenProcedureEmbed(procName, 70); var opened = new ProcedureOpenResult { Ok = true };' }
    @{ Name = 'SwitchView ungated';             Key = 'AppTree';  From = 'if (active != null && !ReferenceEquals(active, vc))'; To = 'if (window != null)' }
    @{ Name = 'identity ignores this open''s module'; Key = 'AppTree'; From = '_t.ReadOpenEmbedModule(), expectedModule, false, out settled)'; To = 'null, null, false, out settled)' }
    @{ Name = 'module read from a stale source'; Key = 'AppTree';  From = 'try { return GetProp(GetOpenPweeDetails(), "Module") as string; }'; To = 'try { return GetProp(GetClaGenEditor(), "Module") as string; }' }
    @{ Name = 'open not busy-guarded';          Key = 'AppTree';  From = "ModernEmbeditorLauncher.EnterBusy();`n            try { return ProcedureOpenFlow.Open("; To = "try { return ProcedureOpenFlow.Open(" }
    @{ Name = 'select releases busy early';     Key = 'AppTree';  From = 'try { return ProcedureOpenFlow.Select(new ProcedureOpenOps(this), procedureName, ToolCharDelaysMs).Message; }'; To = 'ModernEmbeditorLauncher.LeaveBusy(); try { return ProcedureOpenFlow.Select(new ProcedureOpenOps(this), procedureName, ToolCharDelaysMs).Message; } finally { }' }
    @{ Name = 'flow not compiled';              Key = 'Project';  From = '<Compile Include="Services\ProcedureOpenFlow.cs" />'; To = '' }
)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ProcOpenScan_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$survivors = 0
try {
    foreach ($m in $mutations) {
        $mp = @{}
        foreach ($k in $paths.Keys) {
            $dst = Join-Path $tmp ($k + [IO.Path]::GetExtension($paths[$k]))
            Copy-Item $paths[$k] $dst -Force
            $mp[$k] = $dst
        }
        # Line endings depend on the checkout (core.autocrlf), so match anchors on LF-normalized text.
        $text = [IO.File]::ReadAllText($mp[$m.Key]).Replace("`r`n", "`n")
        if (-not $text.Contains($m.From)) { Write-Host "  [FAIL] mutation '$($m.Name)': its anchor is gone from the source" -ForegroundColor Red; $survivors++; continue }
        [IO.File]::WriteAllText($mp[$m.Key], $text.Replace($m.From, $m.To))
        $r = Invoke-Scan $mp
        if ($r.Count -eq 0) { Write-Host "  [FAIL] mutation survived: $($m.Name)" -ForegroundColor Red; $survivors++ }
        else { Write-Host "  [ok]   mutation caught: $($m.Name)" }
    }
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

if ($survivors -gt 0) { Write-Host "ProcedureOpen.SourceScan: $survivors mutation(s) not caught" -ForegroundColor Red; exit 1 }
exit 0
