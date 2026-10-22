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
#
# MUST BE ABLE TO GO RED: against 385a3da (-Root <its ClarionAssistant>) checks 1, 3, 4, 5 and 7 fail.
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
$exm = [regex]::Match($reg, 'IdeCallbackExcludedTools\s*=\s*new Dictionary<string, string>\(StringComparer\.OrdinalIgnoreCase\)\s*(\{(?<body>[^}]*)\})?\s*;')
if ($exm.Success) {
    foreach ($e in [regex]::Matches($exm.Groups['body'].Value, '\{\s*"([^"]+)"\s*,\s*"([^"]*)"\s*\}')) { $excluded[$e.Groups[1].Value] = $e.Groups[2].Value }
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
Check ($sb -match 'BuildIdeCallbackRegistry\(new EditorService\(\), new ClarionClassParser\(\),\s*new IdeWorkspace\(\), new McpServer\.ControlUiDispatcher\(ui\), new DiffService\(\)\)') `
    'the registry gets the IDE workspace (path policy roots), the UI dispatcher and a DiffService'

# ---- 5: no settings to the server (no external token), the UI budget on its own ----
Check ($sb -match 'new McpServer\(ui, null\)' -and $sb -match 'UiTimeoutSettingReader\s*=') `
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

Write-Host ""
if ($fail -eq 0) { Write-Host "PASS - $pass checks" -ForegroundColor Green; exit 0 }
Write-Host "FAIL - $fail of $($pass + $fail) checks" -ForegroundColor Red
exit 1
