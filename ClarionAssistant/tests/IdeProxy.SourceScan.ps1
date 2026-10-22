# Source guard for ticket b8f9431c, the IDE half: IdeCallbackHost (the IDE's call-back endpoint, which runs with NO chat
# pane) serves EVERY IdeOnly tool, so the standalone server a plugin client runs can forward them, and it serves them
# exactly as a CA tab would.
#
# IdeProxy.StandaloneE2ETest.ps1 proves the standalone half against a stand-in IDE; the IDE half needs the real
# Clarion IDE to run, so it is pinned here, from the source, plus the standalone's --selftest (which builds the call-back
# registry the way IdeCallbackHost does, with a stub editor). Checked:
#   1 the callback registry serves every IdeOnly tool named in McpToolRegistry.cs except IdeCallbackExcludedTools
#     (the IdeOnly names are parsed from the source, independently of the registry, and compared with --selftest's count)
#   2 every exclusion carries a reason
#   3 IdeCallbackHost installs the registry statics the chat pane used to set ALONE (AppTreeFactory, IdeProbeFactory,
#     DiagnosticLog, CaEmbeditorLiveProbe, ActiveEditorCoveredProbe), with the chat pane's values, BEFORE it builds the
#     registry (the constructor reads AppTreeFactory). Without the overlay probes the 73bd1f03 guard reads "no CA
#     Embeditor" and a forwarded write_embed_content would write the native embeditor hidden behind one.
#   4 it gives the registry the IDE workspace (the #266 path policy's roots), the UI dispatcher and a DiffService
#   5 its McpServer is built WITHOUT settings (no external token is accepted) and takes the UI budget setting on its own
#   6 McpServer still validates Host, Origin and the token before dispatching anything
#   7 the standalone connects to 127.0.0.1 only, with no proxy, and the forwarder never touches the token
# Review round (b8f9431c pipeline):
#   8 H1: every PATH-LIKE argument (path, file, file_path, *_path, *_file) of every IdeOnly tool is either gated by
#     McpPathGate.GatePath - in the tool's block or a helper it calls (ResolveClarionClTarget) - BEFORE the block's first
#     File.Exists, or is on an explicit allow-list here with the reason it is not a file the tool opens
#   9 M1: execute_command and dump_object_api are excluded (with reasons); inspect_ide is limited off the IDE
#  10 M3: McpDispatcher's gate-exempt tools are exactly McpToolRegistry.IdeCallbackTools
#  11 every IdeOnly tool is classified exactly once as a write tool or a read-only one (the visibility log)
#  12 IdeCallbackHost: the M2 guard, the M1 filter and the visibility observer are installed; the write log line; the
#     solution events; a read-only InstanceCoordinationService (L3); the live UI-budget reader (L6)
#  13 L2: the record env hooks are read only through TestHook (CA_TEST_HOOKS=1)
#  14 M2: the forwarder sends the expected solution in params._meta
# Security re-check (b8f9431c):
#  15 N1: edits/saves gated on the ACTIVE document's write access (router + registry wiring, close_file the only
#     exemption, the embed exemption by channel type / native .app), the save-before-build asks the gate before it
#     writes; N2: EditorToolRouter.cs touches no path on disk; N3: the build tools' READ gate is commented as deliberate;
#     N6: open_file uses the gated path
#
# MUST BE ABLE TO GO RED: against 385a3da (-Root <its ClarionAssistant>) checks 1, 3, 4, 5 and 7 fail; against c627adc
# checks 4 and 8-14 fail; against 21d0205 every check in 15 fails.
#
# Run:  pwsh -ExecutionPolicy Bypass -File ClarionAssistant\tests\IdeProxy.SourceScan.ps1 [-Root <dir>] [-StandaloneExe <exe>]
# Exit: 0 pass, 1 fail, 2 could-not-run.
param([string]$Root, [string]$StandaloneExe)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
$registryFile = Join-Path $Root 'Services\McpToolRegistry.cs'
$hostFile = Join-Path $Root 'IdeCallbackHost.cs'
$serverFile = Join-Path $Root 'Services\McpServer.cs'
$chatFile = Join-Path $Root 'AssistantChatControl.cs'
$liveFile = Join-Path $Root 'Services\IdeLiveTextClient.cs'
$fwdFile = Join-Path $Root 'Services\IdeToolForwarder.cs'
foreach ($f in @($registryFile, $hostFile, $serverFile, $chatFile, $liveFile)) {
    if (-not (Test-Path $f)) { Write-Host "COULD NOT RUN: $f not found" -ForegroundColor Red; exit 2 }
}

$fail = 0; $pass = 0
function Check([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $name" }
    else { $script:fail++; Write-Host ("  FAIL  $name" + $(if ($detail) { "  ($detail)" } else { "" })) -ForegroundColor Red }
}

$reg = [System.IO.File]::ReadAllText($registryFile)
$hostSrc = [System.IO.File]::ReadAllText($hostFile)
$srv = [System.IO.File]::ReadAllText($serverFile)
$chat = [System.IO.File]::ReadAllText($chatFile)
$live = [System.IO.File]::ReadAllText($liveFile)

# ---- 1 / 2: the IdeOnly names, from the source ----
$ideOnly = New-Object System.Collections.Generic.List[string]
foreach ($block in [regex]::Split($reg, 'Register\(new McpTool')) {
    $m = [regex]::Match($block, 'Name\s*=\s*"([^"]+)"')
    if ($m.Success -and $block -match 'IdeOnly\s*=\s*true') { $ideOnly.Add($m.Groups[1].Value) }
}
$excluded = @{}
# The initializer's entries are { "name", "reason" + "reason" ... }; // comments are ignored.
$exm = [regex]::Match($reg, 'IdeCallbackExcludedTools\s*=\s*new Dictionary<string, string>\(StringComparer\.OrdinalIgnoreCase\)\s*(\{(?<body>.*?)\n\s*\})?\s*;', 'Singleline')
if ($exm.Success) {
    $exBody = [regex]::Replace($exm.Groups['body'].Value, '//[^\n]*', '')
    foreach ($e in [regex]::Matches($exBody, '\{\s*"([^"]+)"\s*,\s*(?<r>"[^"]*"(\s*\+\s*"[^"]*")*)\s*\}')) {
        $excluded[$e.Groups[1].Value] = (([regex]::Matches($e.Groups['r'].Value, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }) -join '')
    }
}
Check ($ideOnly.Count -ge 55) "the source declares the IdeOnly tools" "parsed $($ideOnly.Count)"
Check $exm.Success 'McpToolRegistry declares IdeCallbackExcludedTools (the named exclusions, with reasons)'
foreach ($k in $excluded.Keys) {
    Check ($excluded[$k].Trim().Length -ge 20) "exclusion '$k' gives a reason" $excluded[$k]
    Check ($ideOnly -contains $k) "exclusion '$k' is an IdeOnly tool"
}

$build = [regex]::Match($reg, 'public static McpToolRegistry BuildIdeCallbackRegistry\((?<sig>[^)]*)\)\s*\{(?<body>.*?)\n        \}', 'Singleline')
Check ($build.Success -and $build.Groups['body'].Value -notmatch 'RetainOnly\(IdeCallbackTools\)' -and
       $build.Groups['body'].Value -match 'new McpToolRegistry\(editorService, parser, true\)') `
    'BuildIdeCallbackRegistry keeps every IdeOnly tool (no RetainOnly to the two call-backs)'

# The registry as built: the standalone's --selftest builds it exactly as IdeCallbackHost does.
if (-not $StandaloneExe) { $StandaloneExe = Join-Path $Root 'mcp-server\bin\Debug\clarion-mcp-server.exe' }
if (-not (Test-Path $StandaloneExe)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = if (Test-Path $vswhere) { & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
    if (-not $msbuild) { Write-Host "COULD NOT RUN: MSBuild not found to build the standalone for --selftest." -ForegroundColor Red; exit 2 }
    & $msbuild (Join-Path $Root 'mcp-server\ClarionMcpServer.csproj') /t:Restore`;Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
    if (-not (Test-Path $StandaloneExe)) { Write-Host "COULD NOT RUN: the standalone did not build." -ForegroundColor Red; exit 2 }
}
$self = (& $StandaloneExe --selftest 2>&1 | Out-String)
$sm = [regex]::Match($self, 'IDE call-back endpoint serves all (\d+) IdeOnly tools \((\d+) excluded\); the standalone forwards the same (\d+)')
$want = $ideOnly.Count - $excluded.Count
Check ($sm.Success -and [int]$sm.Groups[1].Value -eq $want -and [int]$sm.Groups[2].Value -eq $excluded.Count -and [int]$sm.Groups[3].Value -eq $want) `
    "the callback registry serves all $want IdeOnly tools the source declares (minus $($excluded.Count) named exclusions), and the standalone forwards the same set" `
    $(if ($sm.Success) { $sm.Value } else { 'no such --selftest line' })
$selfTail = (($self -split "`n") | Select-Object -Last 4) -join ' '
Check ($self -match 'SELFTEST PASSED') 'the standalone --selftest passes' $selfTail

# ---- 3: the registry statics, installed by the host, before the registry is built ----
$inst = [regex]::Match($hostSrc, 'internal static void InstallRegistryHooks\(\)\s*\{(?<body>.*?)\n        \}', 'Singleline')
$hooks = @(
    @{ N = 'AppTreeFactory'; Chat = 'McpToolRegistry.AppTreeFactory = () => new AppTreeService();'; Host = 'McpToolRegistry.AppTreeFactory = () => new AppTreeService();' },
    @{ N = 'IdeProbeFactory'; Chat = 'McpToolRegistry.IdeProbeFactory = () => new Services.IdeProbeService();'; Host = 'McpToolRegistry.IdeProbeFactory = () => new IdeProbeService();' },
    @{ N = 'DiagnosticLog'; Chat = 'McpToolRegistry.DiagnosticLog = msg => MonacoSpikeLog.Write(msg);'; Host = 'McpToolRegistry.DiagnosticLog = msg => MonacoSpikeLog.Write(msg);' },
    @{ N = 'CaEmbeditorLiveProbe'; Chat = 'McpToolRegistry.CaEmbeditorLiveProbe = () => ModernEmbeditorViewContent.HasLiveOverlay;'; Host = 'McpToolRegistry.CaEmbeditorLiveProbe = () => Terminal.ModernEmbeditorViewContent.HasLiveOverlay;' },
    @{ N = 'ActiveEditorCoveredProbe'; Chat = 'McpToolRegistry.ActiveEditorCoveredProbe = () => ModernEmbeditorViewContent.ActiveEditorIsCoveredByOverlay();'; Host = 'McpToolRegistry.ActiveEditorCoveredProbe = () => Terminal.ModernEmbeditorViewContent.ActiveEditorIsCoveredByOverlay();' }
)
foreach ($h in $hooks) {
    Check ($chat.Contains($h.Chat)) "the chat pane still sets $($h.N) (the value the host must match)"
    Check ($inst.Success -and $inst.Groups['body'].Value.Contains($h.Host)) "IdeCallbackHost.InstallRegistryHooks sets $($h.N) to the chat pane's value"
}
$start = [regex]::Match($hostSrc, 'internal static void Start\(\)\s*\{(?<body>.*?)\n        \}', 'Singleline')
$sb = $start.Groups['body'].Value
$iHooks = $sb.IndexOf('InstallRegistryHooks();')
$iBuild = $sb.IndexOf('McpToolRegistry.BuildIdeCallbackRegistry(')
Check ($start.Success -and $iHooks -ge 0 -and $iBuild -gt $iHooks) 'Start installs the hooks BEFORE building the registry'

# ---- 4: workspace, dispatcher, diff service ----
Check ($sb -match 'BuildIdeCallbackRegistry\(new EditorService\(\), new ClarionClassParser\(\),\s*new IdeWorkspace\(\), new McpServer\.ControlUiDispatcher\(ui\), new DiffService\(\), coord\)' -and
       $sb -match 'coord = new InstanceCoordinationService\(\)' -and $sb -notmatch 'coord\.Start\(') `
    'the registry gets the IDE workspace (path policy roots), the UI dispatcher, a DiffService and a READ-ONLY InstanceCoordinationService (L3: never Start()ed)'

# ---- 5: no settings to the server (no external token), the UI budget on its own ----
Check ($sb -match 'new McpServer\(ui, null\)' -and $sb -match 'UiTimeoutSettingReader\s*=\s*McpServer\.ReadUiTimeoutSettingLive') `
    'the callback McpServer has no SettingsService (no external token) and reads the UI budget setting itself'
Check ($srv -match 'var reader = UiTimeoutSettingReader;' -and $srv -match '_dispatcher\.UiTimeoutSettingReader = reader \?\?') `
    'McpServer hands that reader to its dispatcher (the IDE''s UI timeout decides a forwarded call)'

# ---- 6: Host / Origin / token before any dispatch ----
$hr = [regex]::Match($srv, 'private void HandleRequest\(HttpListenerContext context\)\s*\{(?<body>.*?)\n        \}', 'Singleline').Groups['body'].Value
$iH = $hr.IndexOf('if (!ValidateHost(context)) return;'); $iO = $hr.IndexOf('if (!ValidateOrigin(context)) return;')
$iA = $hr.IndexOf('if (!RequireAuth(context)) return;'); $iD = $hr.IndexOf('HandleStreamableHttpPost(context)')
Check ($iH -ge 0 -and $iO -gt $iH -and $iA -gt $iO -and $iD -gt $iA) 'McpServer validates Host, then Origin, then the token, before dispatching'
$auth = [regex]::Match($srv, 'private bool RequireAuth\(HttpListenerContext context\)\s*\{(?<body>.*?)\n        \}', 'Singleline').Groups['body'].Value
Check ($auth -match 'TokensEqual\(presented, _sessionToken' -and $auth -match '_settings != null') 'the token check is constant-time, and the external token needs settings'

# ---- 7: the standalone's transport ----
Check ($live.Contains('WebRequest.Create("http://127.0.0.1:" + ep.Port + "/mcp")') -and $live.Contains('req.Proxy = null;') -and
       ($live -notmatch 'WebRequest\.Create\("http://localhost')) 'the standalone connects to 127.0.0.1 (never localhost or a recorded host), with no proxy'
if (Test-Path $fwdFile) {
    $fwd = [System.IO.File]::ReadAllText($fwdFile)
    Check ($fwd -notmatch '\.Token\b') 'IdeToolForwarder never reads the token (it stays in IdeLiveTextClient''s Authorization header)'
    Check ($fwd.Contains('IdeLiveTextClient.CallToolRaw(')) 'IdeToolForwarder uses the shared IdeLiveTextClient transport'
}
else { Check $false 'IdeToolForwarder.cs exists' }

# ---- 8: H1, every path-like argument of an IdeOnly tool is gated, or allow-listed with a reason ----
# Allow-list: tool.arg -> why it is not a file the tool opens. Anything else path-like must be gated.
$editorTarget = 'compared as TEXT with the active editor''s path (EditorToolRouter.SamePath: FileIdentity.NormalizeText, no I/O - check 15) so a write to the wrong tab is refused; never opened. The write itself is gated on the ACTIVE file (N1)'
$allow = @{
    'dump_object_api.path'        = 'a dot-path through the IDE object graph from the App object, not a file (and the tool is excluded outside the IDE)'
    'insert_text_at_cursor.file_path' = $editorTarget
    'replace_text.file_path'      = $editorTarget
    'replace_range.file_path'     = $editorTarget
    'delete_range.file_path'      = $editorTarget
    'undo.file_path'              = $editorTarget
    'redo.file_path'              = $editorTarget
    'save_file.file_path'         = $editorTarget
    'close_file.file_path'        = $editorTarget
    'toggle_comment.file_path'    = $editorTarget
}
foreach ($k in $allow.Keys) { Check ($allow[$k].Length -ge 20) "allow-list entry '$k' gives a reason" }
$helpers = @{}
foreach ($h in @('ResolveClarionClTarget')) {
    $hm = [regex]::Match($reg, 'private \w+ ' + $h + '\((?<sig>[^)]*)\)\s*\{(?<body>.*?)\n        \}', 'Singleline')
    if ($hm.Success) { $helpers[$h] = $hm.Groups['body'].Value }
}
Check ($helpers.ContainsKey('ResolveClarionClTarget')) 'the scan finds ResolveClarionClTarget (build_app / generate_source gate their paths there)'
$pathLike = '^(path|file|file_path|.+_path|.+_file)$'
$audited = 0
foreach ($block in [regex]::Split($reg, 'Register\(new McpTool')) {
    $nm = [regex]::Match($block, 'Name\s*=\s*"([^"]+)"')
    if (-not $nm.Success -or $block -notmatch 'IdeOnly\s*=\s*true') { continue }
    $tool = $nm.Groups[1].Value
    $hIdx = $block.IndexOf('Handler')
    $schema = if ($hIdx -gt 0) { $block.Substring(0, $hIdx) } else { $block }
    $body = if ($hIdx -gt 0) { $block.Substring($hIdx) } else { '' }
    $targs = @([regex]::Matches($schema, '\{\s*"([a-z_0-9]+)\??"\s*,\s*[A-Za-z"]') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $paths = @($targs | Where-Object { $_ -match $pathLike })
    if ($paths.Count -eq 0) { continue }
    $code = $body
    foreach ($h in $helpers.Keys) { if ($body.Contains($h + '(')) { $code = $helpers[$h] + "`n" + $code } }
    $gates = [regex]::Matches($code, 'McpPathGate\.GatePath\(').Count
    $need = @($paths | Where-Object { -not $allow.ContainsKey("$tool.$_") })
    foreach ($a in $paths) {
        $audited++
        if ($allow.ContainsKey("$tool.$a")) { continue }
        Check ($gates -ge $need.Count) "H1: $tool.$a goes through McpPathGate.GatePath ($gates gate call(s) for $($need.Count) path argument(s))"
    }
    if ($need.Count -gt 0) {
        $iGate = $code.IndexOf('McpPathGate.GatePath(')
        $iIo = $code.IndexOf('File.Exists(')
        Check ($iGate -ge 0 -and ($iIo -lt 0 -or $iGate -lt $iIo)) "H1: $tool gates its path BEFORE its first File.Exists (a UNC path is never touched)"
    }
}
Check ($audited -ge 20) "H1: the audit saw the IdeOnly tools' path-like arguments" "audited $audited"

# ---- 9: M1 ----
Check ($excluded.ContainsKey('execute_command') -and $excluded.ContainsKey('dump_object_api')) 'M1: execute_command and dump_object_api are excluded from the call-back endpoint (with reasons)'
Check ($reg -match 'CallbackInspectCommands = new HashSet<string>\(StringComparer\.OrdinalIgnoreCase\)\s*\{\s*"active_view", "editor_text", "all_windows", "all_pads", "app_details"\s*\}') `
    'M1: off the IDE, inspect_ide serves only active_view, editor_text, all_windows, all_pads, app_details'
$insp = [regex]::Match($reg, 'Name = "inspect_ide"(?<b>.*?)switch \(command\.ToLower\(\)\)', 'Singleline').Groups['b'].Value
Check ($insp -match 'if \(_callbackHost\)' -and $insp -match 'ForwardedRefusal\("inspect_ide", args\)') 'M1: inspect_ide checks the call-back limit BEFORE dispatching any command'

# ---- 10: M3 exemptions ----
$disp = [System.IO.File]::ReadAllText((Join-Path $Root 'Services\McpDispatcher.cs'))
$ex = [regex]::Match($disp, 'UiGateExemptTools = new HashSet<string>\(StringComparer\.OrdinalIgnoreCase\)\s*\{(?<b>[^}]*)\}')
$cbt = [regex]::Match($reg, 'IdeCallbackTools = \{(?<b>[^}]*)\}')
$exNames = @([regex]::Matches($ex.Groups['b'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
$cbNames = @([regex]::Matches($cbt.Groups['b'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
Check ($ex.Success -and $cbt.Success -and (($exNames -join ',') -eq ($cbNames -join ','))) 'M3: the UI gate exempts exactly the standalone''s call-backs (McpToolRegistry.IdeCallbackTools)' "$($exNames -join ',') vs $($cbNames -join ',')"
Check ($disp -match 'private static readonly SemaphoreSlim UiGate = new SemaphoreSlim\(1, 1\);') 'M3: ONE static UI gate for every dispatcher in the process'

# ---- 11: write / read-only classification ----
function SetOf([string]$name) {
    $m = [regex]::Match($reg, $name + ' = new HashSet<string>\(StringComparer\.OrdinalIgnoreCase\)\s*\{(?<b>[^}]*)\}')
    return @([regex]::Matches($m.Groups['b'].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
}
$w = SetOf 'IdeWriteTools'; $ro = SetOf 'ReadOnlyIdeTools'
$both = @($w | Where-Object { $ro -contains $_ })
$neither = @($ideOnly | Where-Object { ($w -notcontains $_) -and ($ro -notcontains $_) })
Check ($w.Count -gt 0 -and $both.Count -eq 0 -and $neither.Count -eq 0) 'every IdeOnly tool is classified once, as a write tool or a read-only one' "both [$($both -join ',')] neither [$($neither -join ',')]"

# ---- 12: the host's hooks ----
$inst2 = $inst.Groups['body'].Value
Check ($inst2.Contains('McpDispatcher.ExpectedSolutionGuard = CheckExpectedSolution;') -and $inst2.Contains('McpDispatcher.ForwardedCallObserver = OnForwardedCall;') -and
       $inst2.Contains('McpDispatcher.ForwardedCallFilter = McpToolRegistry.ForwardedRefusal;')) 'M2/M1: the host installs the solution guard, the forwarded-call filter and the observer'
Check ($hostSrc -match 'FileIdentity\.SameFile\(open, expected\)' -and $hostSrc -match 'EditorService\.GetOpenSolutionPath\(\)') 'M2: the guard compares the IDE''s LIVE open solution by file identity'
Check ($hostSrc.Contains('MonacoSpikeLog.Write("[ide-callback] " + tool + " from plugin client");') -and $hostSrc.Contains('McpToolRegistry.IdeWriteTools.Contains(')) 'visibility: a forwarded write tool is logged "[ide-callback] <tool> from plugin client"'
Check ($hostSrc.Contains('SubscribeSolutionEvent("SolutionLoaded"') -and $hostSrc.Contains('SubscribeSolutionEvent("SolutionClosed"')) 'M2: the solution record is also published on SolutionLoaded / SolutionClosed'

# ---- 13: L2 ----
$epSrc = [System.IO.File]::ReadAllText((Join-Path $Root 'Services\IdeEndpointRecord.cs'))
$slnSrc = [System.IO.File]::ReadAllText((Join-Path $Root 'Services\IdeSolutionRecord.cs'))
$raw = [regex]::Matches($epSrc + $slnSrc, 'GetEnvironmentVariable\((RecordDirEnv|ImageNameEnv|"CA_IDE_RECORD_DIR"|"CA_IDE_IMAGE_NAME")\)').Count
Check ($raw -eq 0 -and $epSrc -match 'TestHook\(RecordDirEnv\)' -and $epSrc -match 'TestHook\(ImageNameEnv\)' -and
       $slnSrc -match 'TestHook\("CA_IDE_RECORD_DIR"\)' -and $slnSrc -match 'TestHook\("CA_IDE_IMAGE_NAME"\)' -and
       ($epSrc + $slnSrc) -match 'GetEnvironmentVariable\("CA_TEST_HOOKS"\) != "1"|GetEnvironmentVariable\(TestHooksEnv\) != "1"') 'L2: the record env hooks are read only through TestHook, which needs CA_TEST_HOOKS=1'

# ---- 14: M2, the forwarder side ----
if (Test-Path $fwdFile) {
    $fwd2 = [System.IO.File]::ReadAllText($fwdFile)
    Check ($fwd2 -match 'McpDispatcher\.ExpectedSolutionMetaKey, sel\.Solution') 'M2: the forwarder sends the matched solution in params._meta'
}

# ---- 15: security re-check (b8f9431c N1, N2, N3, N6) ----
function CodeOnly([string]$src) { return [regex]::Replace($src, '(?m)//.*$', '') }
$routerFile = Join-Path $Root 'Services\EditorToolRouter.cs'
$monacoFile = Join-Path $Root 'MonacoClarionSourceEditor.cs'
$probeFile = Join-Path $Root 'Services\IdeProbeService.cs'
if ((Test-Path $routerFile) -and (Test-Path $monacoFile) -and (Test-Path $probeFile)) {
    $router = CodeOnly ([System.IO.File]::ReadAllText($routerFile))
    $monaco = [System.IO.File]::ReadAllText($monacoFile)
    $probeSrc = [System.IO.File]::ReadAllText($probeFile)
    $io = @([regex]::Matches($router, '\bGetFullPath\(|\bFile\.\w+\(|\bFileInfo\b|\bDirectory\.\w+\(|GetLongPathName') | ForEach-Object { $_.Value })
    Check ($io.Count -eq 0) 'N2: EditorToolRouter.cs never resolves or touches a path on disk (no GetFullPath, File.*, FileInfo, Directory.*): file_path is compared as text' ($io -join ', ')
    Check ($router -match 'FileIdentity\.Lexical\(FileIdentity\.NormalizeText\(') 'N2: SamePath compares FileIdentity.NormalizeText forms'
    Check ($reg -match 'new EditorToolRouter\(\(\) => _ui, \(\) => _editorService\.GetActiveDocumentPath\(\), ActiveDocumentWriteRefusal\)' -and
           $reg -match 'private string ActiveDocumentWriteRefusal\(string tool, string activePath\)\s*\{[^}]*McpPathGate\.GatePath\(tool, activePath, PathAccess\.Write, _workspace') `
        'N1: the registry builds its EditorToolRouter with the #266 WRITE gate on the active document'
    Check ($router -match 'bool gated = opts\.IsWrite && !opts\.NoFileWrite && _writeGate != null;' -and $router -match 'if \(ch is IEmbedOverlayChannel\) return' -and
           $router -match 'EndsWith\("\.app", StringComparison\.OrdinalIgnoreCase\)') 'N1: the router gates every IsWrite call; the embed exemption is the embed channel type, or a native .app window'
    $noFile = [regex]::Matches($reg, 'NoFileWrite = true').Count
    $closeBlock = [regex]::Match($reg, 'Name = "close_file"(?<b>.*?)Register\(new McpTool', 'Singleline').Groups['b'].Value
    Check ($noFile -eq 1 -and $closeBlock.Contains('opts.NoFileWrite = true;')) 'N1: only close_file is exempt from the write gate (NoFileWrite)' "NoFileWrite = true x$noFile"
    $saveBody = [regex]::Match($monaco, 'private void SaveDirtyBeforeBuild\(Func<string, string> writeRefusal, List<string> skipped\)\s*\{(?<b>.*?)\n        \}', 'Singleline').Groups['b'].Value
    $iGateM = $saveBody.IndexOf('writeRefusal(_filePath)'); $iWrite = $saveBody.IndexOf('WriteToDisk(')
    Check ($iGateM -ge 0 -and $iWrite -gt $iGateM -and $saveBody.Contains('skipped.Add(')) 'N1: a CA Editor tab''s save-before-build asks the write gate BEFORE WriteToDisk, and reports a skip'
    Check ($probeSrc -match 'MonacoClarionEditor\.SaveAllDirtyBeforeBuild\(writeRefusal\)') 'N1: IdeProbeService passes the gate through to the CA Editor tabs'
    Check ($reg -match '_ideProbe\.SaveAllDirtyEditors\(p =>\s*\{\s*string canonical;\s*return McpPathGate\.GatePath\(gateTool, p, PathAccess\.Write, _workspace' -and
           $reg -match 'skippedNote = SaveBeforeBuildSkippedNote\(skipped\);') 'N1: the ClarionCL tools save dirty tabs through the WRITE gate and name the skipped ones'
    Check ($helpers['ResolveClarionClTarget'] -match 'DELIBERATELY the READ gate') 'N3: ResolveClarionClTarget says why app_path / project_path take the READ gate'
    $openBlock = [regex]::Match($reg, 'Name = "open_file"(?<b>.*?)Register\(new McpTool', 'Singleline').Groups['b'].Value
    $iAssign = $openBlock.IndexOf('path = gatedPath;'); $iExists = $openBlock.IndexOf('File.Exists(path)')
    Check ($iAssign -ge 0 -and $iExists -gt $iAssign) 'N6: open_file uses the gated canonical path from the gate on (checked, opened, waited for)'
}
else { Check $false 'N1/N2: EditorToolRouter.cs, MonacoClarionSourceEditor.cs and IdeProbeService.cs exist' }

Write-Host ""
if ($fail -eq 0) { Write-Host "PASS - $pass checks" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $fail of $($pass + $fail) checks" -ForegroundColor Red
exit 1
