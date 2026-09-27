# LocalLayer.SourceScan.ps1 - 1c685f2e item 4: both Monaco hosts route the instant local layer, and the local
# layer never touches the language server.
#
# THE DEFECT CLASS. There are two IMonacoEditorHost implementations (ModernEmbeditorViewContent = the CA
# Embeditor, MonacoClarionEditor = the CA Editor overlay). An action wired in one and a no-op in the other
# has shipped before (gotcha_monaco_dual_host_noop_handlers). And the local layer only stays instant while
# it never enters the LSP path: one EnsureBufferSynced or SharedLspBridge.Get* call and it waits on a
# 3.2 MB didChange again.
#
# 4.13  each of localCompletion / localHover / slotDiagnostics: in the control's dispatch switch, declared on
#       IMonacoEditorHost, implemented in BOTH hosts, and each body calls RunLocalAction with its own lane
#       (local-completion / local-hover / slot-diagnostics). RunLocalAction calls LocalLayerHandlers.Handle
#       inside RunLatest(lane ...).
# 4.14  LocalLayerHandlers.cs, RunLocalAction and the six host bodies contain none of: EnsureLspStarted,
#       EnsureBufferSynced, .Block(, SharedLspBridge.Get, new SQLiteConnection, new CodeGraphProvider,
#       SchemaGraphService.Get.
# slot  each host's LocalOptions() keeps today's slot-check behaviour: the embeditor `SlotChecks = !_fileMode`
#       (its file-mode tab never ran them), the overlay `SlotChecks = true` (whole file, John's request).
#       LocalLayer.Handlers.Test.cs proves those options give markers / an empty list.
#
# PROVES IT CAN FAIL: after the real scan passes, the same scan runs on temp copies with (a) the overlay's
# OnSlotDiagnostics body stubbed to { }, (b) EnsureBufferSynced( planted in LocalLayerHandlers.cs, and
# (c) the embeditor's slot option forced on; each must go red.
#
# Exit: 0 pass, 1 fail, 2 could-not-run.

param(
    [string]$Control = (Join-Path $PSScriptRoot '..\Terminal\MonacoEditorControl.cs'),
    [string]$Embeditor = (Join-Path $PSScriptRoot '..\Terminal\ModernEmbeditorViewContent.cs'),
    [string]$Overlay = (Join-Path $PSScriptRoot '..\MonacoClarionSourceEditor.cs'),
    [string]$Handlers = (Join-Path $PSScriptRoot '..\Services\LocalLayerHandlers.cs')
)

$ErrorActionPreference = 'Stop'
foreach ($f in $Control, $Embeditor, $Overlay, $Handlers) {
    if (-not (Test-Path $f)) { Write-Host "COULD NOT RUN: missing $f" -ForegroundColor Red; exit 2 }
}

$actions = @(
    @{ Action = 'localCompletion'; Method = 'OnLocalCompletion'; Lane = 'local-completion'; Const = 'LocalLayerHandlers.LocalCompletion' },
    @{ Action = 'localHover';      Method = 'OnLocalHover';      Lane = 'local-hover';      Const = 'LocalLayerHandlers.LocalHover' },
    @{ Action = 'slotDiagnostics'; Method = 'OnSlotDiagnostics'; Lane = 'slot-diagnostics'; Const = 'LocalLayerHandlers.SlotDiagnostics' }
)
$forbidden = @('EnsureLspStarted', 'EnsureBufferSynced', '.Block(', 'SharedLspBridge.Get', 'new SQLiteConnection',
               'new CodeGraphProvider', 'SchemaGraphService.Get')

# The text of the member starting at $signature, up to the matching close brace (brace counting; the
# bodies scanned here hold no braces inside strings that would unbalance it).
function Get-Body([string]$src, [string]$signature) {
    $i = $src.IndexOf($signature, [StringComparison]::Ordinal)
    if ($i -lt 0) { return $null }
    $open = $src.IndexOf('{', $i)
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($j = $open; $j -lt $src.Length; $j++) {
        if ($src[$j] -eq '{') { $depth++ }
        elseif ($src[$j] -eq '}') { $depth--; if ($depth -eq 0) { return $src.Substring($i, $j - $i + 1) } }
    }
    return $null
}

function Invoke-Scan([string]$ctlPath, [string]$viewPath, [string]$ovPath, [string]$hPath) {
    $fails = New-Object System.Collections.Generic.List[string]
    $ctl = [IO.File]::ReadAllText($ctlPath); $view = [IO.File]::ReadAllText($viewPath)
    $ov = [IO.File]::ReadAllText($ovPath); $hnd = [IO.File]::ReadAllText($hPath)

    $run = Get-Body $ctl 'public void RunLocalAction('
    if (-not $run) { $fails.Add('4.13 MonacoEditorControl.RunLocalAction not found') }
    else {
        if ($run -notmatch 'RunLatest\(lane,') { $fails.Add('4.13 RunLocalAction does not run in RunLatest(lane, ...)') }
        if (-not $run.Contains('LocalLayerHandlers.Handle(')) { $fails.Add('4.13 RunLocalAction does not call LocalLayerHandlers.Handle') }
    }

    $bodies = @{ 'RunLocalAction' = $run; 'LocalLayerHandlers.cs' = $hnd }
    foreach ($a in $actions) {
        if ($ctl -notmatch ('case "' + $a.Action + '":\s*h\.' + $a.Method + '\(this, json\);')) {
            $fails.Add("4.13 $($a.Action): not in the control's dispatch switch")
        }
        if (-not $ctl.Contains('void ' + $a.Method + '(MonacoEditorControl editor, string rawJson);')) {
            $fails.Add("4.13 $($a.Action): $($a.Method) is not on IMonacoEditorHost")
        }
        foreach ($h in @(@{ Name = 'CA Embeditor'; Src = $view }, @{ Name = 'CA Editor overlay'; Src = $ov })) {
            $b = Get-Body $h.Src ('IMonacoEditorHost.' + $a.Method + '(')
            if (-not $b) { $fails.Add("4.13 $($a.Action): not implemented in the $($h.Name)"); continue }
            $want = 'RunLocalAction("' + $a.Lane + '", ' + $a.Const + ','
            if (-not $b.Contains($want)) { $fails.Add("4.13 $($a.Action): the $($h.Name) body does not call $want") }
            $bodies["$($h.Name) $($a.Method)"] = $b
        }
    }
    foreach ($k in $bodies.Keys) {
        $b = $bodies[$k]
        if (-not $b) { continue }
        foreach ($f in $forbidden) { if ($b.Contains($f)) { $fails.Add("4.14 $k contains forbidden '$f'") } }
    }

    $viewOpts = Get-Body $view 'private LocalLayerOptions LocalOptions('
    $ovOpts = Get-Body $ov 'private LocalLayerOptions LocalOptions('
    if (-not $viewOpts -or $viewOpts -notmatch 'SlotChecks = !_fileMode') { $fails.Add('slot: the CA Embeditor options do not keep SlotChecks = !_fileMode') }
    if (-not $ovOpts -or $ovOpts -notmatch 'SlotChecks = true') { $fails.Add('slot: the CA Editor overlay options do not keep SlotChecks = true') }
    return , $fails
}

$real = Invoke-Scan $Control $Embeditor $Overlay $Handlers
if ($real.Count -gt 0) {
    Write-Host "FAIL - the real sources:" -ForegroundColor Red
    $real | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  PASS  4.13/4.14/slot on the real sources (3 actions x 2 hosts, forbidden-call scan, slot options)"

# ---- prove the scan can fail ----
$tmp = Join-Path $env:TEMP ("ca-localscan-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$proofFailed = $false
function Test-Mutation([string]$label, [string]$which, [string]$find, [string]$replace) {
    $paths = @{ ctl = $Control; view = $Embeditor; ov = $Overlay; h = $Handlers }
    $src = [IO.File]::ReadAllText($paths[$which])
    if (-not $src.Contains($find)) { Write-Host "  FAIL  proof '$label': mutation anchor not found" -ForegroundColor Red; $script:proofFailed = $true; return }
    $mut = Join-Path $tmp ($which + '.cs')
    [IO.File]::WriteAllText($mut, $src.Replace($find, $replace))
    $paths[$which] = $mut
    $r = Invoke-Scan $paths.ctl $paths.view $paths.ov $paths.h
    if ($r.Count -gt 0) { Write-Host "  PASS  proof '$label' goes red: $($r[0])" }
    else { Write-Host "  FAIL  proof '$label' stayed green" -ForegroundColor Red; $script:proofFailed = $true }
}
try {
    Test-Mutation 'overlay OnSlotDiagnostics stubbed to { }' 'ov' `
        'void IMonacoEditorHost.OnSlotDiagnostics(MonacoEditorControl editor, string rawJson) { editor.RunLocalAction("slot-diagnostics", LocalLayerHandlers.SlotDiagnostics, rawJson, LocalOptions()); }' `
        'void IMonacoEditorHost.OnSlotDiagnostics(MonacoEditorControl editor, string rawJson) { }'
    Test-Mutation 'EnsureBufferSynced( planted in LocalLayerHandlers' 'h' `
        'var sw = Stopwatch.StartNew();' 'var sw = Stopwatch.StartNew(); SharedLspBridge.EnsureBufferSynced(null, buffer);'
    Test-Mutation 'embeditor slot checks forced on in file mode' 'view' 'SlotChecks = !_fileMode' 'SlotChecks = true'
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

if ($proofFailed) { exit 1 }
exit 0
