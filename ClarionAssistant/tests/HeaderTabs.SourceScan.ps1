# HeaderTabs.SourceScan.ps1 - 82938fc7: the host side of the CA pane header's tabs.
#
# header-tabs.test.js drives header.html; this checks the C# it talks to, which cannot run outside the IDE.
#
# H1  fixed height: no Splitter / OnSplitterMoved / "Header.Height" in AssistantChatControl, no dead tab-bar
#     plumbing (SyncTabBarToHeader, HeaderWebView.SetTabs / SetActiveTab / TabDescriptor), and the header's
#     height comes from its CssFullHeight / CssStripHeight constants, not a literal.
# H2  ONE solution-level panel: no AttachSchemaSourcesView, no TerminalTab.SchemaSourcesView, exactly one
#     `new SchemaSourcesView(`, disposed in Dispose, theme not applied per tab.
# H3  every method that assigns _currentSlnPath then calls RefreshSolutionSettings() or LoadSolutionHistory(),
#     and LoadSolutionHistory and OnSolutionChanged themselves call RefreshSolutionSettings().
# H4  OnSchemaSourcesReady runs once: wired only to the view's Ready event; no "schemaSourcesReady" case.
# H5  worker-thread results (HandleIndexSource, HandleTestConnection) go through PostToSchemaView, which
#     checks IsDisposed; the modal still grows the panel to 580 and restores the pane height.
# H6  LSP diagnostics bar never visible: no toggleDiagBar / OnToggleDiagnosticsBar, no `Visible = true` in
#     LspStatusBar.cs, nothing sets _lspStatusBar.Visible, and it is created hidden.
# H7  copy: the "copySolutionPath" case passes no page data, and the handler copies _currentSlnPath with
#     Clipboard.SetText inside a try.
# H8  RED: the "openRedFile" case passes no page data; the handler reads _redFileService.RedFilePath, is
#     gated on RedFileOpenable (which excludes "warning"), and calls OpenFileOnly INSIDE a BeginInvoke after
#     activating the workbench window.
#
# PROVES IT CAN FAIL: after the real scan passes, the same scan runs on temp copies with one planted
# mutation each (listed at the bottom); every one must go red.
#
# Exit: 0 pass, 1 fail, 2 could-not-run.

param(
    [string]$Chat = (Join-Path $PSScriptRoot '..\AssistantChatControl.cs'),
    [string]$Header = (Join-Path $PSScriptRoot '..\Terminal\HeaderWebView.cs'),
    [string]$Schema = (Join-Path $PSScriptRoot '..\Terminal\SchemaSourcesView.cs'),
    [string]$Tab = (Join-Path $PSScriptRoot '..\Terminal\TerminalTab.cs'),
    [string]$Bar = (Join-Path $PSScriptRoot '..\Terminal\LspStatusBar.cs')
)

$ErrorActionPreference = 'Stop'
foreach ($f in $Chat, $Header, $Schema, $Tab, $Bar) {
    if (-not (Test-Path $f)) { Write-Host "COULD NOT RUN: missing $f" -ForegroundColor Red; exit 2 }
}

# The text from $start's opening brace to its matching close brace (brace counting; the bodies scanned
# here hold no unbalanced braces inside strings).
function Get-BodyAt([string]$src, [int]$start) {
    $open = $src.IndexOf('{', $start)
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($j = $open; $j -lt $src.Length; $j++) {
        if ($src[$j] -eq '{') { $depth++ }
        elseif ($src[$j] -eq '}') { $depth--; if ($depth -eq 0) { return $src.Substring($start, $j - $start + 1) } }
    }
    return $null
}
function Get-Body([string]$src, [string]$signature) {
    $i = $src.IndexOf($signature, [StringComparison]::Ordinal)
    if ($i -lt 0) { return $null }
    return Get-BodyAt $src $i
}
# The method that encloses $pos: its name and the text from $pos to the end of its body.
function Get-EnclosingMethod([string]$src, [int]$pos) {
    $before = $src.Substring(0, $pos)
    $m = [regex]::Matches($before, '(?m)^\s*(?:public|private|protected|internal)[^\r\n(=]*\s(\w+)\s*\(')
    if ($m.Count -eq 0) { return $null }
    $last = $m[$m.Count - 1]
    $body = Get-BodyAt $src $last.Index
    if (-not $body -or ($last.Index + $body.Length) -le $pos) { return $null }
    return @{ Name = $last.Groups[1].Value; Rest = $src.Substring($pos, $last.Index + $body.Length - $pos) }
}

function Invoke-Scan($p) {
    $fails = New-Object System.Collections.Generic.List[string]
    $chat = [IO.File]::ReadAllText($p.Chat); $hdr = [IO.File]::ReadAllText($p.Header)
    $sv = [IO.File]::ReadAllText($p.Schema); $tab = [IO.File]::ReadAllText($p.Tab); $bar = [IO.File]::ReadAllText($p.Bar)

    # H1
    foreach ($bad in @('Splitter', 'OnSplitterMoved', 'SyncTabBarToHeader')) {
        if ($chat.Contains($bad)) { $fails.Add("H1 AssistantChatControl still has $bad") }
    }
    if ($chat -match '(Get|Set)\("Header\.Height"') { $fails.Add('H1 AssistantChatControl still reads or saves the Header.Height setting') }
    foreach ($bad in @('SetTabs(', 'SetActiveTab(', 'TabDescriptor')) {
        if ($hdr.Contains($bad)) { $fails.Add("H1 HeaderWebView still has $bad") }
    }
    if ($hdr -notmatch 'public const int CssFullHeight = \d+;' -or $hdr -notmatch 'public const int CssStripHeight = \d+;') {
        $fails.Add('H1 HeaderWebView has no CssFullHeight / CssStripHeight constants')
    }
    if ($hdr -match '(?m)^\s*Height\s*=\s*\d+\s*;') { $fails.Add('H1 HeaderWebView sets a literal Height') }
    $apply = Get-Body $hdr 'private void ApplyHeight('
    if (-not $apply -or -not $apply.Contains('CssFullHeight') -or -not $apply.Contains('CssStripHeight')) {
        $fails.Add('H1 ApplyHeight does not size from the constants')
    }

    # H2
    if ($chat.Contains('AttachSchemaSourcesView')) { $fails.Add('H2 AttachSchemaSourcesView is back') }
    if ($chat -match '\.SchemaSourcesView\b' -or $tab.Contains('SchemaSourcesView')) { $fails.Add('H2 a per-tab SchemaSourcesView is back') }
    $news = ([regex]::Matches($chat, 'new SchemaSourcesView\b')).Count
    if ($news -ne 1) { $fails.Add("H2 expected exactly one new SchemaSourcesView, found $news") }
    $disp = Get-Body $chat 'protected override void Dispose(bool disposing)'
    if (-not $disp -or -not $disp.Contains('_schemaView.Dispose()')) { $fails.Add('H2 Dispose does not dispose _schemaView') }
    $theme = Get-Body $chat 'private void OnThemeChanged('
    if (-not $theme -or ([regex]::Matches($theme, '_schemaView\.SetTheme')).Count -ne 1) { $fails.Add('H2 OnThemeChanged does not theme the panel exactly once') }

    # H3
    $assigns = [regex]::Matches($chat, '_currentSlnPath\s*=\s*[^=]')
    if ($assigns.Count -eq 0) { $fails.Add('H3 found no _currentSlnPath assignments (scan broken?)') }
    foreach ($a in $assigns) {
        $m = Get-EnclosingMethod $chat $a.Index
        if (-not $m) { $fails.Add('H3 could not find the method around a _currentSlnPath assignment'); continue }
        if (-not ($m.Rest.Contains('RefreshSolutionSettings();') -or $m.Rest.Contains('LoadSolutionHistory();'))) {
            $fails.Add('H3 ' + $m.Name + ' changes _currentSlnPath without RefreshSolutionSettings / LoadSolutionHistory after it')
        }
    }
    foreach ($sig in @('private void LoadSolutionHistory(', 'private void OnSolutionChanged(')) {
        $b = Get-Body $chat $sig
        if (-not $b -or -not $b.Contains('RefreshSolutionSettings();')) { $fails.Add("H3 $sig does not call RefreshSolutionSettings()") }
    }
    $refresh = Get-Body $chat 'private void RefreshSolutionSettings('
    if (-not $refresh -or -not $refresh.Contains('SendSchemaSources();') -or -not $refresh.Contains('SendRepoData();')) {
        $fails.Add('H3 RefreshSolutionSettings does not send both the sources and the repo data')
    }
    $sss = Get-Body $chat 'private void SendSchemaSources('
    if (-not $sss -or -not $sss.Contains('_currentSlnPath') -or -not $sss.Contains('SetSchemaCount(')) {
        $fails.Add('H3 SendSchemaSources is not keyed on _currentSlnPath or does not send the badge count')
    }

    # H4
    if (([regex]::Matches($chat, 'Ready \+= OnSchemaSourcesReady')).Count -ne 1) { $fails.Add('H4 OnSchemaSourcesReady is not wired exactly once') }
    if ($chat.Contains('case "schemaSourcesReady"')) { $fails.Add('H4 the page''s schemaSourcesReady post still runs OnSchemaSourcesReady (twice)') }

    # H5
    foreach ($sig in @('private void HandleIndexSource(', 'private void HandleTestConnection(')) {
        $b = Get-Body $chat $sig
        if (-not $b -or -not $b.Contains('PostToSchemaView(') -or $b -match '\btab\.') { $fails.Add("H5 $sig does not post through PostToSchemaView") }
    }
    $post = Get-Body $chat 'private void PostToSchemaView('
    if (-not $post -or -not $post.Contains('IsDisposed')) { $fails.Add('H5 PostToSchemaView is not guarded against disposal') }
    if ($sv -notmatch 'MODAL_HEIGHT = 580;') { $fails.Add('H5 the Manage Sources modal height is not 580') }
    if ($sv -notmatch '"modalClosed"\)\s*\{[^}]*Height = _paneHeight;') { $fails.Add('H5 modalClosed does not restore the pane height') }

    # H6
    foreach ($bad in @('toggleDiagBar', 'OnToggleDiagnosticsBar')) { if ($chat.Contains($bad)) { $fails.Add("H6 $bad is back") } }
    if ($bar -match 'Visible\s*=\s*true') { $fails.Add('H6 LspStatusBar.cs sets Visible = true') }
    if ($chat -match '_lspStatusBar\s*\.\s*(Visible\s*=|Show\s*\()') { $fails.Add('H6 AssistantChatControl shows _lspStatusBar') }
    if ($chat -notmatch 'new Terminal\.LspStatusBar \{ Visible = false \}') { $fails.Add('H6 _lspStatusBar is not created hidden') }

    # H7
    if ($chat -notmatch 'case "copySolutionPath": OnCopySolutionPath\(\); break;') { $fails.Add('H7 copySolutionPath case missing or passes page data') }
    $copy = Get-Body $chat 'private void OnCopySolutionPath('
    if (-not $copy -or $copy -notmatch 'string path = _currentSlnPath;' -or $copy -notmatch 'try \{ Clipboard\.SetText\(path\);') {
        $fails.Add('H7 OnCopySolutionPath does not copy _currentSlnPath with Clipboard.SetText in a try')
    }

    # H8
    if ($chat -notmatch 'case "openRedFile": OnOpenRedFile\(\); break;') { $fails.Add('H8 openRedFile case missing or passes page data') }
    $red = Get-Body $chat 'private void OnOpenRedFile('
    if (-not $red) { $fails.Add('H8 OnOpenRedFile not found') }
    else {
        if (-not $red.Contains('if (!RedFileOpenable) return;')) { $fails.Add('H8 OnOpenRedFile is not gated on RedFileOpenable') }
        if ($red -notmatch 'string path = _redFileService\.RedFilePath;') { $fails.Add('H8 OnOpenRedFile does not use _redFileService.RedFilePath') }
        $bi = $red.IndexOf('BeginInvoke(', [StringComparison]::Ordinal)
        $act = $red.IndexOf('.Activate()', [StringComparison]::Ordinal)
        $open = $red.IndexOf('OpenFileOnly(path)', [StringComparison]::Ordinal)
        if ($bi -lt 0 -or $act -lt $bi -or $open -lt $act) { $fails.Add('H8 OpenFileOnly is not deferred in BeginInvoke after activating the main window') }
    }
    $openable = Get-Body $chat 'private bool RedFileOpenable'
    if (-not $openable -or -not $openable.Contains('_redFileCss != "warning"')) { $fails.Add('H8 RedFileOpenable does not exclude the warning state') }

    return , $fails
}

$paths = @{ Chat = $Chat; Header = $Header; Schema = $Schema; Tab = $Tab; Bar = $Bar }
$real = Invoke-Scan $paths
if ($real.Count -gt 0) {
    Write-Host "FAIL - the real sources:" -ForegroundColor Red
    $real | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  PASS  H1-H8 on the real sources (fixed height, one panel, solution-change refresh, ready once, disposal-guarded posts, hidden LSP bar, copy + RED intents)"

# ---- prove the scan can fail ----
$tmp = Join-Path $env:TEMP ("ca-headerscan-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$proofFailed = $false
function Test-Mutation([string]$label, [string]$which, [string]$find, [string]$replace) {
    $p = $paths.Clone()
    $src = [IO.File]::ReadAllText($p[$which])
    if (-not $src.Contains($find)) { Write-Host "  FAIL  proof '$label': mutation anchor not found" -ForegroundColor Red; $script:proofFailed = $true; return }
    $mut = Join-Path $tmp ($which + '.cs')
    [IO.File]::WriteAllText($mut, $src.Replace($find, $replace))
    $p[$which] = $mut
    $r = Invoke-Scan $p
    if ($r.Count -gt 0) { Write-Host "  PASS  proof '$label' goes red: $($r[0])" }
    else { Write-Host "  FAIL  proof '$label' stayed green" -ForegroundColor Red; $script:proofFailed = $true }
}
try {
    Test-Mutation 'splitter field restored' 'Chat' 'private HeaderWebView _header;' 'private HeaderWebView _header; private Splitter _splitter;'
    Test-Mutation 'saved header height restored' 'Chat' '_header.HeaderReady += OnHeaderReady;' '_header.HeaderReady += OnHeaderReady; string heightStr = _settings.Get("Header.Height");'
    Test-Mutation 'header height back to a literal' 'Header' 'Height = ToPixels(CssFullHeight);' 'Height = 110;'
    Test-Mutation 'per-tab SchemaSourcesView property restored' 'Tab' 'public string StartupCommand { get; set; }' 'public string StartupCommand { get; set; } public SchemaSourcesView SchemaSourcesView { get; set; }'
    Test-Mutation 'panel not disposed' 'Chat' 'if (_schemaView != null) { _schemaView.Dispose(); _schemaView = null; }' ''
    Test-Mutation 'OnBrowseSolution no longer refreshes' 'Chat' "_currentSlnPath = dlg.FileName;`r`n                    AddToSolutionHistory(dlg.FileName);`r`n                    LoadSolutionHistory();" "_currentSlnPath = dlg.FileName;`r`n                    AddToSolutionHistory(dlg.FileName);"
    Test-Mutation 'LoadSolutionHistory no longer refreshes' 'Chat' "            RefreshSolutionSettings();`r`n`r`n            // NO auto-index here" "`r`n            // NO auto-index here"
    Test-Mutation 'OnSchemaSourcesReady run twice' 'Chat' 'case "getGlobalSources":' "case `"schemaSourcesReady`": OnSchemaSourcesReady(null, EventArgs.Empty); break;`r`n                case `"getGlobalSources`":"
    Test-Mutation 'test-connection result posted without the guard' 'Chat' 'PostToSchemaView(view => view.SendMessage(resultJson));' 'BeginInvoke(new Action(() => _schemaView.SendMessage(resultJson)));'
    Test-Mutation 'modal no longer restores' 'Schema' "_modalOpen = false;`r`n                    Height = _paneHeight;" "_modalOpen = false;"
    Test-Mutation 'LSP bar shown by SetDiagnostics again' 'Bar' '// Retired from view (82938fc7)' 'Visible = true; // Retired from view (82938fc7)'
    Test-Mutation 'LSP bar created visible' 'Chat' 'new Terminal.LspStatusBar { Visible = false }' 'new Terminal.LspStatusBar()'
    Test-Mutation 'copy takes the path from the page' 'Chat' 'case "copySolutionPath": OnCopySolutionPath(); break;' 'case "copySolutionPath": OnCopySolutionPath(e.Data); break;'
    Test-Mutation 'copy uses another path' 'Chat' 'string path = _currentSlnPath;' 'string path = CurrentDbPath;'
    Test-Mutation 'RED opened synchronously' 'Chat' "                BeginInvoke((Action)(() =>`r`n                {`r`n                    try`r`n                    {`r`n                        var mainForm" "                ((Action)(() =>`r`n                {`r`n                    try`r`n                    {`r`n                        var mainForm"
    Test-Mutation 'RED clickable in the warning state' 'Chat' 'return _redFileCss != "warning" && _redFileService != null' 'return _redFileService != null'
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

if ($proofFailed) { exit 1 }
exit 0
