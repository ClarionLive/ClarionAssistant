# RedFirst.SourceScan.ps1 - 5a5581e6: the in-IDE agent searched whole drives for UltimateSQL.tpl (60+ stale copies)
# when resolve_red_path names the one the build uses instantly. Its own account: every doc framed the .red tools as
# .clw/.inc tools, nothing said "resolve before you search", it trusted a remembered Clarion folder, and the search
# tool it fell back to was broken because deploy.ps1 never shipped Everything32.dll.
#
# The Owner's directive: ALWAYS find Clarion files through the .red - files move; a remembered path is never one.
#
# D1  the runtime CLAUDE.md and the bundled prompt carry the Critical Rule: ALWAYS through the active .red,
#     resolve_red_path / get_red_search_paths, the root from get_solution_info, never search drives, never from memory,
#     project_name in a multi-project solution, alsoFoundIn - and no "first match wins" (pipeline run 1).
# D2  the /clarion and clarion-solution skills carry the same directive.
# D3  the tools say it too (their schemas can be deferred, but not always): resolve_red_path and get_red_search_paths
#     name templates and "before any disk search"; get_red_search_paths says order is precedence and answers
#     absoluteSearchPaths; get_solution_info answers tplSearchPaths and baseDir. Pipeline run 1 wiring: resolve_red_path
#     takes a bare name (LeafNameError), resolves through the IDE's configuration's sections (RedScopeFor ->
#     ActiveBuildConfiguration -> SectionsFor) in the project's folder and .red (ProjectDirectory, ForProjectDirectory),
#     reports alsoFoundIn, and asks the language server only when it runs the same configuration; get_red_search_paths
#     walks the SAME sections; no fixed BuildSectionOrder, no "the way the build finds it".
# D4  deploy.ps1 ships Everything32.dll in $Items, and the installer ships it beside every ClarionAssistant.dll.
#
# PROVES IT CAN FAIL: after the real scan passes, the same scan runs on temp copies with one planted mutation each
# (listed at the bottom); every one must go red.
#
# Exit: 0 pass, 1 fail, 2 could-not-run.

param(
    [string]$ClaudeMd  = (Join-Path $PSScriptRoot '..\.claude\CLAUDE.md'),
    [string]$Bundled   = (Join-Path $PSScriptRoot '..\Terminal\clarion-assistant-prompt.md'),
    [string]$Skill     = (Join-Path $PSScriptRoot '..\..\marketplace\plugins\clarion-assistant\skills\clarion\SKILL.md'),
    [string]$SolSkill  = (Join-Path $PSScriptRoot '..\..\marketplace\plugins\clarion-assistant\skills\clarion-solution\SKILL.md'),
    [string]$Registry  = (Join-Path $PSScriptRoot '..\Services\McpToolRegistry.cs'),
    [string]$Lsp       = (Join-Path $PSScriptRoot '..\Services\LspService.cs'),
    [string]$Deploy    = (Join-Path $PSScriptRoot '..\deploy.ps1'),
    [string]$Installer = (Join-Path $PSScriptRoot '..\..\installer\ClarionAssistant.iss')
)

$ErrorActionPreference = 'Stop'
$files = [ordered]@{ ClaudeMd = $ClaudeMd; Bundled = $Bundled; Skill = $Skill; SolSkill = $SolSkill
                     Registry = $Registry; Lsp = $Lsp; Deploy = $Deploy; Installer = $Installer }
foreach ($f in $files.Values) {
    if (-not (Test-Path $f)) { Write-Host "COULD NOT RUN: missing $f" -ForegroundColor Red; exit 2 }
}

$Directive = 'ALWAYS find Clarion files through the active .red'

# The Critical Rule carrying the directive: from its numbered heading to the next numbered rule or section.
function Get-Rule([string]$doc) {
    $crit = $doc.IndexOf("`n## Critical Rules", [StringComparison]::Ordinal)
    if ($crit -lt 0) { return $null }
    $m = [regex]::Match($doc.Substring($crit), '(?m)^\d+\. \*\*' + [regex]::Escape($Directive))
    if (-not $m.Success) { return $null }
    $start = $crit + $m.Index
    $next = [regex]::Match($doc.Substring($start + 4), '(?m)^(\d+\. \*\*|## )')
    if ($next.Success) { return $doc.Substring($start, $next.Index + 4) }
    return $doc.Substring($start)
}

# One tool's Description, alone: the parameter examples (e.g. 'UltimateSQL.tpl') must not stand in for it.
function Get-ToolHead([string]$src, [string]$name) {
    $start = $src.IndexOf("Name = `"$name`"", [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $h = $src.IndexOf('InputSchema =', $start, [StringComparison]::Ordinal)
    if ($h -lt 0) { return $null }
    return $src.Substring($start, $h - $start)
}
# ... and its handler, up to the next registration.
function Get-ToolBody([string]$src, [string]$name) {
    $start = $src.IndexOf("Name = `"$name`"", [StringComparison]::Ordinal)
    if ($start -lt 0) { $start = $src.IndexOf($name, [StringComparison]::Ordinal) }
    if ($start -lt 0) { return $null }
    $end = $src.IndexOf('Register(new McpTool', $start + 1, [StringComparison]::Ordinal)
    if ($end -lt 0) { $end = $src.Length }
    return $src.Substring($start, $end - $start)
}

function Invoke-Scan($p) {
    $fails = New-Object System.Collections.Generic.List[string]
    $t = @{}
    foreach ($k in $p.Keys) { $t[$k] = [IO.File]::ReadAllText($p[$k]).Replace("`r`n", "`n") }

    # D1
    foreach ($k in 'ClaudeMd', 'Bundled') {
        $rule = Get-Rule $t[$k]
        if (-not $rule) { $fails.Add("D1 $k has no Critical Rule '$Directive'"); continue }
        foreach ($need in 'resolve_red_path', 'get_red_search_paths', 'get_solution_info', 'clarionRoot', 'activeRedFile', '.tpl', 'Build > Set Clarion Version') {
            if ($rule.IndexOf($need, [StringComparison]::Ordinal) -lt 0) { $fails.Add("D1 $k rule does not mention $need") }
        }
        if ($rule -notmatch '(?i)never recursively search drives') { $fails.Add("D1 $k rule does not forbid searching drives") }
        if ($rule -notmatch '(?i)memory[^\n]*NEVER a file.s location') { $fails.Add("D1 $k rule does not say a remembered path is never a location") }
        if ($rule -notmatch '(?i)precedence') { $fails.Add("D1 $k rule does not say the search-path order is precedence") }
        if ($rule -match '(?i)first match wins') { $fails.Add("D1 $k rule still claims 'first match wins'") }
        if ($rule -notmatch 'project_name') { $fails.Add("D1 $k rule does not tell the agent to pass project_name") }
        if ($rule -notmatch 'alsoFoundIn') { $fails.Add("D1 $k rule does not explain alsoFoundIn") }
        if ($t[$k] -notmatch '(?m)^- `resolve_red_path` -[^\n]*\.tpl') { $fails.Add("D1 $k tools list: resolve_red_path does not mention templates") }
        if ($t[$k] -notmatch '(?m)^- `get_red_search_paths` -[^\n]*tpl') { $fails.Add("D1 $k tools list: get_red_search_paths does not mention templates") }
        if ($t[$k] -notmatch '(?m)^- `get_solution_info` -[^\n]*tplSearchPaths') { $fails.Add("D1 $k tools list: get_solution_info does not mention tplSearchPaths") }
    }

    # D2
    foreach ($k in 'Skill', 'SolSkill') {
        $s = $t[$k]
        $i = $s.IndexOf("**$Directive**", [StringComparison]::Ordinal)
        if ($i -lt 0) { $fails.Add("D2 $k lacks the directive '**$Directive**'"); continue }
        $para = $s.Substring($i, [Math]::Min(400, $s.Length - $i))
        foreach ($need in 'resolve_red_path', 'get_red_search_paths') {
            if ($para.IndexOf($need, [StringComparison]::Ordinal) -lt 0) { $fails.Add("D2 $k directive does not name $need") }
        }
        if ($para -notmatch '(?i)never search drives') { $fails.Add("D2 $k directive does not forbid searching drives") }
        if ($para -notmatch '(?i)path from memory') { $fails.Add("D2 $k directive does not rule out remembered paths") }
    }

    # D3
    $reg = $t.Registry
    $resolveHead = Get-ToolHead $reg 'resolve_red_path'
    $searchHead  = Get-ToolHead $reg 'get_red_search_paths'
    $infoHead    = Get-ToolHead $reg 'get_solution_info'
    if (-not $resolveHead -or -not $searchHead -or -not $infoHead) { $fails.Add('D3 a .red tool registration was not found (scan broken?)') }
    else {
        foreach ($pair in @(@('resolve_red_path', $resolveHead), @('get_red_search_paths', $searchHead))) {
            if ($pair[1] -notmatch '\.tpl|''tpl''') { $fails.Add("D3 $($pair[0]) description does not mention templates") }
            if ($pair[1] -notmatch '(?i)before any disk search') { $fails.Add("D3 $($pair[0]) description does not say 'before any disk search'") }
        }
        if ($searchHead -notmatch '(?i)precedence') { $fails.Add('D3 get_red_search_paths does not say order is precedence') }
        if ($searchHead -notmatch 'absoluteSearchPaths') { $fails.Add('D3 get_red_search_paths does not describe absoluteSearchPaths') }
        if ($infoHead -notmatch 'tplSearchPaths') { $fails.Add('D3 get_solution_info does not describe tplSearchPaths') }
    }
    $infoBody    = Get-ToolBody $reg 'get_solution_info'
    $resolveBody = Get-ToolBody $reg 'resolve_red_path'
    $searchBody  = Get-ToolBody $reg 'get_red_search_paths'
    if ($infoBody -notmatch 'var tplPaths = RedResolution\.SearchPaths\(scope\.Red, TemplateExtensions, scope\.Sections\);\s*result\["tplSearchPaths"\] = tplPaths;') { $fails.Add('D3 get_solution_info does not answer tplSearchPaths over the resolution sections') }
    if ($infoBody -notmatch 'scope\.Describe\(result\)') { $fails.Add('D3 get_solution_info does not report baseDir / buildConfiguration') }
    if ($resolveHead -match '(?i)the way the build finds it|first match wins') { $fails.Add('D3 resolve_red_path still claims to resolve the way the build does') }
    if ($resolveHead -notmatch 'alsoFoundIn' -or $resolveHead -notmatch 'project_name') { $fails.Add('D3 resolve_red_path does not describe alsoFoundIn and project_name') }
    if ($resolveBody -notmatch 'RedResolution\.LeafNameError\(fileName\)') { $fails.Add('D3 resolve_red_path accepts non-leaf names') }
    if ($resolveBody -match 'BuildSectionOrder' -or $resolveBody -match 'red\.Resolve(From)?\(') { $fails.Add('D3 resolve_red_path still resolves through a fixed section order / ResolveFrom') }
    if ($resolveBody -notmatch 'RedResolution\.Resolve\(scope\.Red, fileName, scope\.BaseDir, scope\.Sections\)') { $fails.Add('D3 resolve_red_path does not resolve through the scope (configuration, project)') }
    if ($resolveBody -notmatch 'RedResolution\.Decide\(local, lspPath, LspService\.LspConfiguration, scope\.LspMatchesConfiguration\)') { $fails.Add('D3 resolve_red_path does not let RedResolution.Decide weigh the language server against the configuration') }
    if ($reg -notmatch 'get \{ return Configuration != null && string\.Equals\(Configuration, LspService\.LspConfiguration') { $fails.Add('D3 LspMatchesConfiguration trusts an UNKNOWN configuration') }
    if ($resolveBody -notmatch 'AddAlsoFoundIn\(result, decision\.AlsoFoundIn, decision\.Path\)') { $fails.Add('D3 resolve_red_path does not report alsoFoundIn') }
    if ($searchBody -notmatch 'RedResolution\.SearchPaths\(scope\.Red, new\[\] \{ ext \}, scope\.Sections\)') { $fails.Add('D3 get_red_search_paths does not walk the same sections as resolve_red_path') }
    if ($searchBody -notmatch 'scope\.LspMatchesConfiguration') { $fails.Add('D3 get_red_search_paths asks the language server whatever the configuration') }
    if (([regex]::Matches([string]$searchBody, 'WithAbsolutePaths\(')).Count -lt 2) { $fails.Add('D3 get_red_search_paths does not add absoluteSearchPaths to both answers') }
    if ($reg -notmatch 'result\[key\] = absolute;' -or $reg -notmatch 'result\[unresolvedKey\] = unresolved;') { $fails.Add('D3 WithAbsolutePaths does not write absoluteSearchPaths / unresolvedEntries') }
    $scopeFn = Get-ToolBody $reg 'private RedScope RedScopeFor('
    foreach ($need in 'RedResolution.ProjectDirectory(', 'RedFileService.ForProjectDirectory(scope.BaseDir', 'ActiveBuildConfiguration(sln', 'RedResolution.SectionsFor(scope.Configuration)') {
        if ([string]$scopeFn -notlike "*$need*") { $fails.Add("D3 RedScopeFor does not call $need") }
    }
    if ($reg -notmatch 'IdeSolutionPreferences\.ResolveBuildConfiguration\(slnPath, inProcess, rec, configDirs') { $fails.Add('D3 the active configuration is not resolved live-first (IDE, record, then saved preferences)') }
    if ($reg -notmatch 'inProcess = p\(\)') { $fails.Add('D3 the in-process IDE configuration is never asked') }
    if ($t.Lsp -notmatch '\{ "configuration", LspConfiguration \}') { $fails.Add('D3 the language server is not started with LspService.LspConfiguration') }
    # D4
    $items = [regex]::Match($t.Deploy, '(?s)\$Items = @\((.*?)\n\)')
    if (-not $items.Success) { $fails.Add('D4 deploy.ps1 $Items not found (scan broken?)') }
    elseif ($items.Groups[1].Value -notmatch '(?m)^\s*"Everything32\.dll"\s*$') { $fails.Add('D4 deploy.ps1 $Items does not ship Everything32.dll') }
    $srcs = [regex]::Matches($t.Installer, '(?m)^Source: "\{#(Src\w+)\}\\ClarionAssistant\.dll"') | ForEach-Object { $_.Groups[1].Value }
    if (-not $srcs) { $fails.Add('D4 no ClarionAssistant.dll Source lines in the installer (scan broken?)') }
    foreach ($s in $srcs) {
        if ($t.Installer -notmatch ('(?m)^Source: "\{#' + $s + '\}\\Everything32\.dll"')) { $fails.Add("D4 installer ships no Everything32.dll for $s") }
    }
    return $fails
}

$real = @{}; foreach ($k in $files.Keys) { $real[$k] = $files[$k] }
$fails = Invoke-Scan $real
if ($fails.Count -gt 0) {
    Write-Host "FAIL - $($fails.Count) finding(s):" -ForegroundColor Red
    $fails | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  [ok]   the real sources pass D1-D4" -ForegroundColor Green

# ---- mutation self-check: each planted defect must turn the scan red ----
$tmp = Join-Path $env:TEMP ("ca-redfirst-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$mutations = @(
    ,@('ClaudeMd',  "**$Directive",                                    '**Prefer the .red',                       'CLAUDE.md rule removed')
    ,@('Bundled',   "**$Directive",                                    '**Prefer the .red',                       'bundled prompt rule removed')
    ,@('ClaudeMd',  'Never recursively search drives',                 'Avoid searching drives',                  'CLAUDE.md rule allows drive searches')
    ,@('ClaudeMd',  'is NEVER a file''s location',                     'is usually a file''s location',           'CLAUDE.md rule trusts memory')
    ,@('ClaudeMd',  '- `resolve_red_path` -Resolve ANY Clarion filename (.tpl, .tpw,', '- `resolve_red_path` -Resolve a Clarion filename (', 'tools list framed as source-only')
    ,@('Skill',     "**$Directive**",                                  '**Use the .red**',                        '/clarion skill directive removed')
    ,@('Skill',     'never search drives',                             'search drives if needed',                 '/clarion skill allows drive searches')
    ,@('SolSkill',  "**$Directive**",                                  '**Use the .red**',                        'clarion-solution directive removed')
    ,@('Registry',  'Use this before any disk search: stale copies',  'Stale copies',                             'resolve_red_path drops "before any disk search"')
    ,@('Registry',  '"Resolve ANY file the Clarion compiler or template engine finds - templates (.tpl, .tpw), "', '"Resolve a Clarion filename "', 'resolve_red_path drops templates')
    ,@('Registry',  'so the order is precedence',                      'so the order is arbitrary',                  'get_red_search_paths drops precedence')
    ,@('Registry',  'result["tplSearchPaths"] = tplPaths;', '', 'get_solution_info drops tplSearchPaths')
    ,@('Registry',  'result[key] = absolute;',                         '',                                        'absoluteSearchPaths never written')
    ,@('Registry',  'RedResolution.SectionsFor(scope.Configuration)',  'RedFileService.BuildSectionOrder',        'config-blind fixed section order')
    ,@('Registry',  'string nameError = RedResolution.LeafNameError(fileName);', 'string nameError = null;',      'resolve_red_path accepts any name')
    ,@('Registry',  'var paths = RedResolution.SearchPaths(scope.Red, new[] { ext }, scope.Sections);', 'var paths = scope.Red.GetSearchPaths(ext, "Common");', 'search paths over other sections than resolution')
    ,@('Registry',  'get { return Configuration != null && string.Equals(', 'get { return Configuration == null || string.Equals(', 'unknown configuration trusts the language server')
    ,@('Registry',  'RedResolution.Decide(local, lspPath, LspService.LspConfiguration, scope.LspMatchesConfiguration)', 'RedResolution.Decide(local, lspPath, LspService.LspConfiguration, true)', 'Decide told the server always matches')
    ,@('Registry',  'scope.Red = RedFileService.ForProjectDirectory(scope.BaseDir, scope.Red);', '',             "the project's own .red ignored")
    ,@('Registry',  'AddAlsoFoundIn(result, decision.AlsoFoundIn, decision.Path);', '',                               'alsoFoundIn dropped from the local answer')
    ,@('Registry',  'IdeSolutionPreferences.ResolveBuildConfiguration(slnPath, inProcess, rec, configDirs', 'IdeSolutionPreferences.ResolveBuildConfiguration(slnPath, null, null, configDirs', 'live configuration sources dropped')
    ,@('Registry',  'the build is pointed at.',                        'the build is pointed at, the way the build finds it.', 'resolve_red_path overclaims again')
    ,@('ClaudeMd',  'in order: earlier folders take precedence.',      'in order: the first match wins.',         'CLAUDE.md claims first match wins')
    ,@('ClaudeMd',  '    - In a multi-project solution pass `project_name`', '    - In a multi-project solution pass the project', 'CLAUDE.md drops project_name')
    ,@('Lsp',       '{ "configuration", LspConfiguration },',          '{ "configuration", "Release" },',         'language server started off LspConfiguration')
    ,@('Deploy',    '    "Everything32.dll"',                          '    # "Everything32.dll"',                   'deploy.ps1 drops Everything32.dll')
    ,@('Installer', 'Source: "{#SrcC11}\Everything32.dll"',            '; Source: "{#SrcC11}\Everything32.dll"',  'installer drops Everything32.dll for C11')
)
$survived = 0
try {
    foreach ($m in $mutations) {
        $copy = @{}; foreach ($k in $files.Keys) { $copy[$k] = $files[$k] }
        $orig = [IO.File]::ReadAllText($files[$m[0]])
        $mut = $orig.Replace($m[1], $m[2])
        if ($mut -eq $orig) { Write-Host "  [FAIL] mutation '$($m[3])' did not apply (its anchor text moved)" -ForegroundColor Red; $survived++; continue }
        $path = Join-Path $tmp ($m[0] + [IO.Path]::GetExtension($files[$m[0]]))
        [IO.File]::WriteAllText($path, $mut)
        $copy[$m[0]] = $path
        $r = Invoke-Scan $copy
        if ($r.Count -gt 0) { Write-Host "  [ok]   red on: $($m[3])" -ForegroundColor Green }
        else { Write-Host "  [FAIL] still green with: $($m[3])" -ForegroundColor Red; $survived++ }
    }
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

if ($survived -gt 0) { Write-Host "FAIL - $survived mutation(s) not caught" -ForegroundColor Red; exit 1 }
Write-Host "PASS - D1-D4, and all $($mutations.Count) mutations go red" -ForegroundColor Green
exit 0
