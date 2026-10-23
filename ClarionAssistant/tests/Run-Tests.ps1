# Run-Tests.ps1 — single entry point for ClarionAssistant's standalone test harnesses.
#
#   .\tests\Run-Tests.ps1                 # everything runnable on this machine
#   .\tests\Run-Tests.ps1 -Probe          # also run the read-only live VS Code probe (diagnostic)
#   .\tests\Run-Tests.ps1 -CSharpOnly     # only the C# harnesses
#   .\tests\Run-Tests.ps1 -NodeOnly       # only the node harnesses
#   .\tests\Run-Tests.ps1 -InstallerOnly  # only the installer script harnesses
#
# There are two families here and they are deliberately different things:
#
#   tests\*.cs             standalone csc harnesses over SERVICE code that has no IDE coupling.
#                          They compile the real .cs file straight out of the tree — no mocks of the
#                          thing under test — which is only possible while those services stay free of
#                          IDE references. Run outside Clarion entirely.
#
#   Terminal\test\*.test.js  node harnesses over the WebView2 pages. Mostly zero-dependency; the ones
#                          that need jsdom (vscode-import-ui, header-tabs, projects-dashboard, schema-sources-solution-key,
#                          editor-sweep-590) say so, and this script installs it into
#                          Terminal\test\node_modules when missing.
#
#   ..\installer\tests\*.ps1  PowerShell harnesses over the INSTALLER scripts. These live outside
#                          this folder because they belong next to what they test, and they run the
#                          real script under the real powershell.exe 5.1 the installer uses. Two
#                          releases have shipped a configure.ps1 that destroyed users' settings.json
#                          (GH #190, GH #200), both from 5.1-only defaults that look correct in a
#                          7.x terminal. Nothing else in the repo exercises that script.
#
# NEITHER family is wired into the MSBuild build. That is intentional — these harnesses exist to be
# run by a developer who just changed something, and a test that only runs in CI would not have caught
# the bugs these were written for. Run this before you deploy.
#
# EXIT CODE: non-zero if any harness fails OR could not run. A test that could not run is NOT a pass,
# and this script will not let a missing dependency read as green.

param(
    [switch]$Probe,        # also run the live VS Code probe (reads the developer's own settings.json)
    [switch]$CSharpOnly,
    [switch]$NodeOnly,
    [switch]$InstallerOnly
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent $PSScriptRoot          # ...\ClarionAssistant
$RootDir = Split-Path -Parent $RepoDir              # repository root (holds installer\)
$TestDir = $PSScriptRoot
$OutDir  = Join-Path $env:TEMP ("ca-tests-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))

$failures = @()
$ran = 0

function Section($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }

# --------------------------------------------------------------------------- C# harnesses
if (-not $NodeOnly -and -not $InstallerOnly) {

    # Resolve csc. Prefer the VS2022 Roslyn compiler — ClarionAppDataReader.cs (and its
    # StructureScan harness) use C# 6 syntax (expression-bodied members) the old .NET Framework
    # compiler (v4.0.30319, C# 5) rejects outright. Roslyn is a superset, so it still covers the
    # net48-targeted harnesses (System.Web.Extensions for JavaScriptSerializer) that motivated the
    # original choice. Fall back to the Framework compiler if VS2022 isn't installed — every
    # harness present at that point predates the C# 6 requirement.
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    $vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
    $csc = $null
    if (Test-Path $vswhere) {
        $vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($vsRoot) {
            $roslynCsc = Join-Path $vsRoot "MSBuild\Current\Bin\Roslyn\csc.exe"
            if (Test-Path $roslynCsc) { $csc = $roslynCsc }
        }
    }
    if (-not $csc) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
    if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }

    if (-not (Test-Path $csc)) {
        Write-Host "csc.exe not found — cannot run the C# harnesses." -ForegroundColor Red
        $failures += "C# harnesses (no csc.exe)"
    }
    else {
        New-Item -ItemType Directory -Force $OutDir | Out-Null

        # LspClient.IncrementalSync.Test's stand-in server keeps documents as the real server does. When deploy.ps1's
        # pinned LSP build cache is here, point it at the REAL vscode-languageserver-textdocument from the newest build;
        # otherwise it uses its port of that package (it prints which). Either way the harness is runnable.
        $textDocPkg = Get-ChildItem (Join-Path $RepoDir ".lsp-build") -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^v(\d+\.\d+\.\d+)$' } |
            Sort-Object { [version]($_.Name.Substring(1)) } -Descending |
            ForEach-Object { Join-Path $_.FullName "node_modules\vscode-languageserver-textdocument" } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        $env:FAKE_TEXTDOC_MODULE = if ($textDocPkg) { $textDocPkg } else { "" }

        # Each harness pairs with the service file(s) it exercises. Listing the sources explicitly
        # (rather than globbing) keeps it obvious WHICH production code each harness actually covers.
        $harnesses = @(
            @{ Name = "VsCodeSettingsImporter.SmokeTest"
               Sources = @("tests\VsCodeSettingsImporter.SmokeTest.cs", "Services\VsCodeSettingsImporter.cs")
               Refs = @("System.dll", "System.Web.Extensions.dll") }
            @{ Name = "VsCodeSettingsImporter.PayloadCheck"
               Sources = @("tests\VsCodeSettingsImporter.PayloadCheck.cs", "Services\VsCodeSettingsImporter.cs")
               Refs = @("System.dll", "System.Web.Extensions.dll") }
            @{ Name = "ClarionAppDataReader.StructureScan"
               Sources = @("tests\ClarionAppDataReader.StructureScan.cs", "tests\ClarionAppDataReader.StructureScan.Stubs.cs",
                           "Services\ClarionAppDataReader.cs", "Services\ClarionAppDataReader.Model.cs")
               Refs = @("System.dll", "System.Xml.dll") }
            # 1d8d1c49: the streaming TXA [DATA]-region index must match the whole-string parsers exactly.
            # Fixture only here; pass a real whole-app .txa (or set CA_TXA_REAL) to compare a real export.
            # 1d8d1c49: the Split-free slot extractor must match the Split version, and allocate far less.
            @{ Name = "TextLines.Test"
               Sources = @("tests\TextLines.Test.cs", "Services\TextLines.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # 1d8d1c49: streamed LSP text messages must parse identically to Serialize+GetBytes, far cheaper.
            @{ Name = "JsonTextStream.Test"
               Sources = @("tests\JsonTextStream.Test.cs", "Services\JsonTextStream.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll") }
            @{ Name = "TxaDataIndex.Test"
               Sources = @("tests\TxaDataIndex.Test.cs", "tests\ClarionAppDataReader.StructureScan.Stubs.cs",
                           "Services\ClarionAppDataReader.cs", "Services\ClarionAppDataReader.Model.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll") }
            # GH #227: New Chat overwrote the user's global ~\.claude\CLAUDE.md. Gets the project dir
            # so it can check the real shipped prompt still opens with the ownership signature.
            @{ Name = "ClaudeMdDeployer.Test"
               Sources = @("tests\ClaudeMdDeployer.Test.cs", "Services\ClaudeMdDeployer.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll")
               Args = @($RepoDir) }
            # 6a4b4d71: closing a tab that was not the active one never repainted the custom-painted strip,
            # so the closed tab stayed drawn and the other tabs' X buttons seemed dead until a refresh.
            @{ Name = "TabManager.Close.Test"
               Sources = @("tests\TabManager.Close.Test.cs", "tests\TabManager.Close.Stubs.cs", "Terminal\TerminalTab.cs", "Terminal\TabManager.cs")
               Refs = @("System.dll", "System.Drawing.dll", "System.Windows.Forms.dll") }
            # 0f928849: models.json went stale (no Opus 5.5, retired Copilot/Codex ids). Checks the
            # registry's internal consistency and the retired-model remap. Gets the project dir to
            # read the shipped models.json and the Codex launcher's hard-coded default.
            @{ Name = "ModelRegistry.Test"
               Sources = @("tests\ModelRegistry.Test.cs", "Services\SettingsService.cs")
               Refs = @("System.dll", "System.Web.Extensions.dll")
               Args = @($RepoDir) }
            # c175492a: two IDEs' CA tabs could not tell which MultiTerminal name was theirs. Gets the
            # project dir so it can check the launch resolves the name before composing the prompt.
            @{ Name = "CaAgentIdentity.Test"
               Sources = @("tests\CaAgentIdentity.Test.cs", "Services\CaAgentIdentity.cs")
               Refs = @("System.dll")
               Args = @($RepoDir) }
            # GH #263 Q2: a generated module opened as a plain file shows attribute names, not chips.
            @{ Name = "GeneratedModuleMatch.Test"
               Sources = @("tests\GeneratedModuleMatch.Test.cs", "Services\GeneratedModuleMatch.cs")
               Refs = @("System.dll") }
            # GH #263 run 2: GeneratedSourceWatch's wiring never subscribes a handler twice.
            @{ Name = "EventWiringOnce.Test"
               Sources = @("tests\EventWiringOnce.Test.cs", "Services\EventWiringOnce.cs")
               Refs = @("System.dll", "System.Core.dll")
               Args = @($RepoDir) }
            @{ Name = "NpgsqlLoader.SmokeTest"
               Sources = @("tests\NpgsqlLoader.SmokeTest.cs", "Services\NpgsqlLoader.cs")
               Refs = @("System.dll") }
            # 77aceec5: the addin -> standalone "IDE's open solution" handover the plain-Chat LSP
            # fallback reads. Record dir redirected to temp; never touches %LOCALAPPDATA%.
            @{ Name = "IdeSolutionRecord.Test"
               Sources = @("tests\IdeSolutionRecord.Test.cs", "Services\IdeSolutionRecord.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Web.Extensions.dll") }
            # 9e1bd280: every tool in a tab follows the IDE's open solution - --solution only until the IDE publishes,
            # none once it closes it, the switch reported - and the standalone/LSP/addin wiring reads that one follower.
            @{ Name = "SolutionFollower.Test"
               Sources = @("tests\SolutionFollower.Test.cs", "Services\SolutionFollower.cs",
                           "Services\IdeSolutionRecord.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Web.Extensions.dll")
               Args = @($RepoDir) }
            # 0ce0b5e2: a solution's Clarion version - pins, then the IDE's own choice for it (live record, then its
            # saved preferences file, chosen by content with the name hash only as a tiebreak), then the host
            # install. x86 like the IDE, so the hash tiebreak is checked as the IDE computes it.
            @{ Name = "SolutionVersionResolver.Test"
               Sources = @("tests\SolutionVersionResolver.Test.cs", "Services\SolutionVersionResolver.cs",
                           "Services\IdeSolutionRecord.cs", "Services\EncodingHelper.cs",
                           "Services\ClarionVersionService.cs", "Services\ClarionConfigDirectory.cs")
               Refs = @("System.dll", "System.Xml.dll", "System.Core.dll", "System.Web.Extensions.dll")
               Platform = "x86" }
            # e1df6c2e (GH #244): the standalone server's settings folder for a Clarion run with /ConfigDir (live IDE,
            # --clarion-config-dir, CLARION_CONFIG_DIR, clarion-assistant.json; a named folder fails closed), and the
            # plugin launcher's pick of WHICH installed server to start (newest only as a reported fallback).
            @{ Name = "StandaloneConfigDir.Test"
               Sources = @("tests\StandaloneConfigDir.Test.cs", "mcp-server\StandaloneConfigDir.cs",
                           "mcp-server\SolutionClarionVersion.cs", "mcp-server\ServerPicker.cs",
                           "Services\IdeSolutionRecord.cs", "Services\SolutionFollower.cs", "Services\EncodingHelper.cs",
                           "Services\SolutionVersionResolver.cs", "Services\ClarionVersionService.cs",
                           "Services\ClarionConfigDirectory.cs")
               Refs = @("System.dll", "System.Xml.dll", "System.Core.dll", "System.Web.Extensions.dll")
               # The project dir: the running-server downgrade checks need the BUILT server (a real file version).
               Args = @($RepoDir) }
            # GH #209: the running Clarion.exe matched the FIRST version entry on its bin folder (a
            # Clarion.NET compiler, not the IDE). Fixture = the reporter's own ClarionProperties.xml.
            @{ Name = "ClarionVersionService.ExeMatchTest"
               Sources = @("tests\ClarionVersionService.ExeMatchTest.cs", "Services\ClarionVersionService.cs", "Services\ClarionConfigDirectory.cs", "Services\EffectiveClarionVersion.cs")
               Refs = @("System.dll", "System.Xml.dll")
               Args = @((Join-Path $TestDir "fixtures\gh209\ClarionProperties.xml")) }
            # GH #247: the standalone server read the NEWEST settings folder's ClarionProperties.xml (its own
            # exe is 5.9, not Clarion's 11.0). Fixtures = the reporter's 11.0 file and the 12.0 one the server read.
            @{ Name = "ClarionVersionService.InstallDetectTest"
               Sources = @("tests\ClarionVersionService.InstallDetectTest.cs", "Services\ClarionVersionService.cs", "Services\ClarionConfigDirectory.cs")
               Refs = @("System.dll", "System.Xml.dll")
               Args = @((Join-Path $TestDir "fixtures\gh247\ClarionProperties.xml"), (Join-Path $TestDir "fixtures\gh247\ClarionProperties-12.0.xml")) }
            # 16d140e9 / 286f2e57: CA's version is the IDE's Build > Set Clarion Version only (no CA
            # override, VERSION is read-only), and every selection names the tier that decided it.
            # Gets the project dir for its source scans.
            @{ Name = "ClarionVersionSelector.Test"
               Sources = @("tests\ClarionVersionSelector.Test.cs", "Services\ClarionVersionService.cs", "Services\ClarionConfigDirectory.cs")
               Refs = @("System.dll", "System.Xml.dll", "System.Core.dll")
               Args = @($RepoDir) }
            # 16d140e9 pipeline run 1: a restart requested while a background LSP start still holds the
            # single-flight guard is served, never dropped.
            @{ Name = "LspStartGate.Test"
               Sources = @("tests\LspStartGate.Test.cs", "Services\LspStartGate.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # 905928c7: Build > Set Clarion Version is followed with no CA chat tab — seeded quietly at
            # addin start, one change per switch however many sources observe it.
            @{ Name = "IdeVersionFollower.Test"
               Sources = @("tests\IdeVersionFollower.Test.cs", "Services\IdeVersionFollower.cs",
                           "Services\ClarionVersionService.cs", "Services\ClarionConfigDirectory.cs")
               Refs = @("System.dll", "System.Xml.dll", "System.Core.dll") }
            # GH #187 follow-up: the CA Embeditor's Monaco <-> LSP line mapping agrees with what
            # WrapBuffer actually prepended (0 lines for a buffer opening with MEMBER/PROGRAM).
            @{ Name = "EmbedLspContext.LineMapping"
               Sources = @("tests\EmbedLspContext.LineMapping.cs", "tests\EmbedLspContext.LineMapping.Stubs.cs",
                           "Services\EmbedLspContext.cs", "Services\RedFileService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # H1-a: the CA Embeditor's hover links to its own module, put in embeditor lines (server card)
            # or marked ?ondisk (index card, disk lines); links to other files untouched.
            @{ Name = "EmbedLspContext.SelfLinks"
               Sources = @("tests\EmbedLspContext.SelfLinks.cs", "tests\EmbedLspContext.LineMapping.Stubs.cs",
                           "Services\EmbedLspContext.cs", "Services\RedFileService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # 16d140e9: the CA Explorer header's APP/VERSION/ROOT values and the path check in front of
            # the "open in Windows Explorer" clicks. Existence probes injected; touches no real path.
            @{ Name = "ExplorerHeader.Test"
               Sources = @("tests\ExplorerHeader.Test.cs", "Services\ExplorerHeader.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # 82938fc7: a Schema Sources / Source Control write drawn before an A->B->A switch is refused.
            @{ Name = "SolutionStamp.Test"
               Sources = @("tests\SolutionStamp.Test.cs", "Terminal\SolutionStamp.cs")
               Refs = @("System.dll") }
            @{ Name = "ClarionClDiagnosis.Test"
               Sources = @("tests\ClarionClDiagnosis.Test.cs", "Services\ClarionClDiagnosis.cs")
               Refs = @("System.dll") }
            # GH #204 (708139c0): app vs. source project detection and the real output path (.cwproj OutputPath per
            # configuration, else the .red), plus a source scan that the three ClarionCL tools use them.
            @{ Name = "ClarionBuildTarget.Test"
               Sources = @("tests\ClarionBuildTarget.Test.cs", "Services\ClarionBuildTarget.cs", "tests\EmbedLspContext.RedResolve.Stubs.cs",
                           "Services\RedFileService.cs", "Services\EncodingHelper.cs", "Services\RedResolution.cs",
                           "CodeGraph\Parsing\SolutionParser.cs", "CodeGraph\Parsing\Models\SolutionProject.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll")
               Args = @($RepoDir) }
            # PR #228: the embeditor finds the generated module through the .red's build sections too.
            @{ Name = "EmbedLspContext.RedResolve.Test"
               Sources = @("tests\EmbedLspContext.RedResolve.Test.cs", "tests\EmbedLspContext.RedResolve.Stubs.cs",
                           "Services\EmbedLspContext.cs", "Services\RedFileService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll") }
            # f3b47441: a .red that can't be loaded clears RedFileService.Active instead of leaving the
            # previous version's file in force. Reuses the RedResolve stubs.
            @{ Name = "RedFileService.FailClosed.Test"
               Sources = @("tests\RedFileService.FailClosed.Test.cs", "tests\EmbedLspContext.RedResolve.Stubs.cs",
                           "Services\EmbedLspContext.cs", "Services\RedFileService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # GH #261: {include} lines are followed (a local .red that includes %BIN%\clarion110.red resolved
            # none of the app's .\clw modules), repeated sections add up, and IsStale sees an edited .red.
            @{ Name = "RedFileService.Include.Test"
               Sources = @("tests\RedFileService.Include.Test.cs", "tests\EmbedLspContext.RedResolve.Stubs.cs",
                           "Services\EmbedLspContext.cs", "Services\RedFileService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # 5a5581e6: template search paths (*.tp?/*.tpl/*.tpw in .red order) and relative entries anchored at the
            # solution folder - what get_solution_info / get_red_search_paths hand the agent instead of a drive search.
            @{ Name = "RedFileService.SearchPaths.Test"
               Sources = @("tests\RedFileService.SearchPaths.Test.cs", "tests\EmbedLspContext.RedResolve.Stubs.cs",
                           "Services\EmbedLspContext.cs", "Services\RedFileService.cs", "Services\EncodingHelper.cs",
                           "Services\RedResolution.cs", "CodeGraph\Parsing\SolutionParser.cs", "CodeGraph\Parsing\Models\SolutionProject.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # PR #208: a hung instance stays swept, a busy one re-registers, beats never overlap.
            # The vendored SQLite is x86-only (SQLite.Interop.dll), hence Platform and the copies.
            @{ Name = "InstanceCoordination.ReRegister.Test"
               Sources = @("tests\InstanceCoordination.ReRegister.Test.cs", "Services\InstanceCoordinationService.cs")
               Refs = @("System.dll", "System.Data.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86" }
            # PR #198: the UI-thread tool timeout (setting + per-tool budget) through the REAL McpDispatcher,
            # registry stubbed. Gets the project dir to check the real registry still declares the budgets.
            @{ Name = "McpDispatcher.UiTimeout.Test"
               Sources = @("tests\McpDispatcher.UiTimeout.Test.cs", "tests\McpDispatcher.UiTimeout.Stubs.cs",
                           "Services\McpDispatcher.cs", "Services\McpUiTimeoutPolicy.cs", "Services\McpCallContext.cs",
                           "Services\McpJsonRpc.cs", "Services\IUiDispatcher.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll")
               Args = @($RepoDir) }
            # PR #198: apply_embed_edits never adopts a dirty embeditor or one under the CA Embeditor.
            @{ Name = "EmbedAdoptPolicy.Test"
               Sources = @("tests\EmbedAdoptPolicy.Test.cs", "Services\EmbedAdoptPolicy.cs")
               Refs = @("System.dll") }
            # 73bd1f03: no embed/editor tool writes the native embed document hidden behind the CA Embeditor.
            @{ Name = "EmbedOverlayGuard.Test"
               Sources = @("tests\EmbedOverlayGuard.Test.cs", "Services\EmbedOverlayGuard.cs")
               Refs = @("System.dll")
               Args = @($RepoDir) }
            # 73bd1f03 fix (2): the embed tools routed to the CA Embeditor's Monaco buffer — formats identical to the
            # native tools; reads/writes in the CA Embeditor's line space; native column by NATIVE line; fail closed.
            @{ Name = "EmbedSlotText.Test"
               Sources = @("tests\EmbedSlotText.Test.cs", "Services\EmbedSlotText.cs")
               Refs = @("System.dll") }
            @{ Name = "EmbedToolRouter.Test"
               Sources = @("tests\EmbedToolRouter.Test.cs", "Services\EmbedToolRouter.cs", "Services\EmbedSlotText.cs",
                           "Services\EditorToolRouter.cs", "Services\HostRequestBroker.cs", "Services\EditorTextOps.cs",
                           "Services\IUiDispatcher.cs", "Services\FileIdentity.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll") }
            # PR #198 pipeline round: apply_embed_edits' write/commit/save half - an abandoned call rolls
            # back instead of saving; a failed save or unconfirmed close discards our writes.
            @{ Name = "EmbedApplyFlow.Test"
               Sources = @("tests\EmbedApplyFlow.Test.cs", "Services\EmbedApplyFlow.cs", "Services\McpCallContext.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # 1565ef7b: a CA Embeditor save that can't go through never loses the developer's text - slots are
            # matched by read-only skeleton (a drift elsewhere re-maps), the save decides BEFORE the overlay
            # detaches and never cancels, recovery files are BOM-free and atomic, the stash restores per slot.
            @{ Name = "EmbedSaveFlow.Test"
               Sources = @("tests\EmbedSaveFlow.Test.cs", "Services\EmbedSaveFlow.cs", "Services\EmbedRecovery.cs",
                           "Services\EmbedSaveJournal.cs", "Services\TextLines.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # ... and the ordering in the IDE-coupled callers that the harness above can't reach (red on 3904549).
            @{ Name = "EmbedSaveOrder.SourceScan"
               Sources = @("tests\EmbedSaveOrder.SourceScan.cs")
               Refs = @("System.dll")
               Args = @($RepoDir) }
            # a964cde3: open_procedure_embed / select_procedure refuse rather than guess - loading and exact-name
            # gates, app tree brought forward first, no click on a wrong selection, a wrong open cancelled unsaved.
            @{ Name = "ProcedureOpenFlow.Test"
               Sources = @("tests\ProcedureOpenFlow.Test.cs", "Services\ProcedureOpenFlow.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # 16d140e9: the Monaco buffer crosses to the host once per content version - the per-surface
            # cache/accessor MonacoEditorControl uses, the sync-message parser, and the newest-wins lane.
            @{ Name = "MonacoBufferSync.Test"
               Sources = @("tests\MonacoBufferSync.Test.cs", "Terminal\MonacoBufferSync.cs", "Services\WebMessageGuard.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll")
               Args = @($RepoDir) }
            # 1c685f2e item 8: the bundled LspClient stops claiming to run when node crashes (exit line with
            # code + stderr tail) or its reader loop ends with the process still alive. The harness plays
            # the language server itself (copied to <temp>\node.exe), so it needs no node.
            # LSP incremental sync: the ranged change LspClient sends instead of the whole buffer must reproduce
            # the new text exactly under the LSP's line/UTF-16 rules, never split a CRLF or surrogate pair, and leave
            # the server's incrementally patched line table (a port of TextDocument.update) equal to a fresh one.
            @{ Name = "LspTextDiff.Test"
               Sources = @("tests\LspTextDiff.Test.cs", "Services\LspTextDiff.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # ... and LspClient wired to it, against a stand-in server that applies changes as the real server does
            # (TextDocument.update, incremental line table checked against a fresh document after every change):
            # ranges only when the server advertises incremental sync (bare or { change: N }), its copy exact after
            # many edits, a disk resync re-based (an unchanged one still sent, as an empty range), the retained texts
            # bounded, and Stop/Start forgetting every document. Needs node.exe where LspClient looks for it.
            @{ Name = "LspClient.IncrementalSync.Test"
               Sources = @("tests\LspClient.IncrementalSync.Test.cs", "Services\LspClient.cs", "Services\LspTextDiff.cs",
                           "Services\JsonTextStream.cs", "Services\LspTrace.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll")
               Args = @((Join-Path $TestDir "fixtures\incremental-sync\fake-lsp.js")) }
            # fc420c30: the MCP editor tools against the CA Editor (Monaco) — reads and writes hit its text, never the
            # native document under it; save keeps the edit; a dirty close is refused; fail closed; native unchanged.
            # b8f9431c N1/N2: edits and saves gated on the ACTIVE file's write access (embeds exempt); file_path as text.
            @{ Name = "EditorToolRouter.Test"
               Sources = @("tests\EditorToolRouter.Test.cs", "Services\EditorToolRouter.cs", "Services\HostRequestBroker.cs",
                           "Services\EditorTextOps.cs", "Services\IUiDispatcher.cs", "Services\FileIdentity.cs",
                           "Services\EmbedToolRouter.cs", "Services\EmbedSlotText.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll") }
            @{ Name = "LspClient.Robustness.Test"
               Sources = @("tests\LspClient.Robustness.Test.cs", "Services\LspClient.cs", "Services\LspTextDiff.cs", "Services\JsonTextStream.cs", "Services\LspTrace.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll") }
            # 1c685f2e item 4: LocalLayerHandlers, the one class both hosts route the local layer to, over the
            # REAL local indexes (LocalScopeIndex, LiveDictionaryIndex, SymbolIndex on synthetic x86 SQLite DBs).
            # The SlotBalance stubs stand in for SharedLspBridge and count calls (the local layer makes none).
            @{ Name = "LocalLayer.Handlers.Test"
               Sources = @("tests\LocalLayer.Handlers.Test.cs", "tests\LocalLayer.Handlers.Stubs.cs", "Services\LocalLayerHandlers.cs", "Services\WebMessageGuard.cs",
                           "tests\ModernEmbeditorDiagnostics.SlotBalance.Stubs.cs",
                           "Services\ModernEmbeditorDiagnostics.cs",
                           "Services\ClarionAppDataReader.cs", "Services\ClarionAppDataReader.Model.cs",
                           "Services\LocalScopeIndex.cs", "Services\LiveDictionaryIndex.cs", "Services\SymbolIndex.cs",
                           "Services\IndexRunGate.cs",
                           "CodeGraph\Graph\CodeGraphProvider.cs", "CodeGraph\Graph\CodeGraphDatabase.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\SolutionProject.cs", "CodeGraph\Parsing\Models\ParseResult.cs",
                           "CodeGraph\Parsing\ClarionBuiltins.cs",
                           "Services\LspClient.cs", "Services\LspTextDiff.cs", "Services\JsonTextStream.cs", "Services\LspTrace.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll", "System.Data.dll", "System.Web.Extensions.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86"
               Args = @($RepoDir) }
            # Per-embed-slot structure balance (Passes 2 & 3), LSP pass stubbed. Reuses the
            # StructureScan stubs so the REAL ClarionAppDataReader supplies the routine set.
            @{ Name = "ModernEmbeditorDiagnostics.SlotBalance"
               Sources = @("tests\ModernEmbeditorDiagnostics.SlotBalance.cs", "tests\ModernEmbeditorDiagnostics.SlotBalance.Stubs.cs",
                           "tests\ModernEmbeditorDiagnostics.SlotBalance.LspClientStub.cs",
                           "tests\ClarionAppDataReader.StructureScan.Stubs.cs",
                           "Services\ModernEmbeditorDiagnostics.cs",
                           "Services\ClarionAppDataReader.cs", "Services\ClarionAppDataReader.Model.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll") }
            # 1c685f2e: instant buffer-local completion/hover - scope, parameters, the local-class owner
            # rule, encodings, and R3 (never Split the whole 3.2 MB buffer; allocation + scaling budgets).
            @{ Name = "LocalScopeIndex.Test"
               Sources = @("tests\LocalScopeIndex.Test.cs", "Services\LocalScopeIndex.cs", "Services\LspClient.cs", "Services\LspTextDiff.cs", "Services\JsonTextStream.cs",
                           "Services\LspTrace.cs", "Services\EncodingHelper.cs", "CodeGraph\Parsing\ClarionBuiltins.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll")
               Args = @((Join-Path $TestDir "fixtures\local-scope"), (Join-Path $RepoDir "Services\LocalScopeIndex.cs")) }
            # 1c685f2e R11: the slice overloads (header + owner DATA + caret span, from the span map)
            # answer exactly what the full-buffer overloads answer, at every caret of the fixture.
            @{ Name = "LocalScopeIndex.SliceParity"
               Sources = @("tests\LocalScopeIndex.SliceParity.cs", "Services\LocalScopeIndex.cs", "Services\LspClient.cs", "Services\LspTextDiff.cs", "Services\JsonTextStream.cs",
                           "Services\LspTrace.cs", "Services\EncodingHelper.cs", "CodeGraph\Parsing\ClarionBuiltins.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll")
               Args = @((Join-Path $TestDir "fixtures\local-scope")) }
            # 1c685f2e: dictionary PRE:Field / table-name completion and hover from the live snapshot
            # (SchemaGraph only as the no-snapshot fallback), plus keyword/built-in names + categories.
            @{ Name = "LiveDictionaryIndex.Test"
               Sources = @("tests\LiveDictionaryIndex.Test.cs", "Services\LiveDictionaryIndex.cs", "Services\ClarionAppDataReader.Model.cs",
                           "Services\LocalScopeIndex.cs", "Services\LspClient.cs", "Services\LspTextDiff.cs", "Services\JsonTextStream.cs", "Services\LspTrace.cs", "Services\EncodingHelper.cs",
                           "CodeGraph\Parsing\ClarionBuiltins.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll")
               Args = @((Join-Path $TestDir "fixtures\keyword-data")) }
            # Colon-qualified completion ("Glob:S"): the server's bare-labelled qualifier items survive the
            # host's qualifier scoping once CodeGraph has supplied a match of its own.
            @{ Name = "ColonQualifierScope.Test"
               Sources = @("tests\ColonQualifierScope.Test.cs", "Services\ColonQualifierScope.cs", "Services\LspClient.cs", "Services\LspTextDiff.cs",
                           "Services\JsonTextStream.cs", "Services\LspTrace.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll") }
            # 1c685f2e: held-open NOCASE symbol lookups - range queries and their plans, the parameter
            # leak, inherited members across both DBs, the old-schema fallback, and the connection
            # lifecycle (a held handle must never block the reindex delete). Synthetic x86 SQLite DBs.
            @{ Name = "SymbolIndex.Test"
               Sources = @("tests\SymbolIndex.Test.cs", "Services\SymbolIndex.cs", "Services\IndexRunGate.cs",
                           "CodeGraph\Graph\CodeGraphProvider.cs", "CodeGraph\Graph\CodeGraphDatabase.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\SolutionProject.cs", "CodeGraph\Parsing\Models\ParseResult.cs",
                           "Services\LspClient.cs", "Services\LspTextDiff.cs", "Services\JsonTextStream.cs", "Services\LspTrace.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Data.dll", "System.Web.Extensions.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86"
               Args = @($RepoDir) }
            # GH #267 (b9260ec6): the .dctx ingest carries key CaseSensitive/AutoNumber/ExternalName, field InitialValue/
            # DerivedFrom/BaseType/ExternalName/Binary/Password/ReadOnly and table Path; get_table shows them; a pre-#267
            # database answers read-only and gains the columns on EnsureDatabase; the SQLite merge path still ingests.
            @{ Name = "SchemaGraphService.Ingest.Test"
               Sources = @("tests\SchemaGraphService.Ingest.Test.cs", "Services\SchemaGraphService.cs", "Services\NpgsqlLoader.cs",
                           "Services\LspClient.cs", "Services\LspTextDiff.cs", "Services\JsonTextStream.cs", "Services\LspTrace.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Data.dll", "System.Xml.dll", "System.Security.dll", "System.Web.Extensions.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86"
               Args = @((Join-Path $TestDir "fixtures\schemagraph-gh267\attrs.dctx")) }
            # PR #237: the active tab re-selected on solution reopen - a view opening before the reopen loop
            # (the .app tab from Recent Applications) neither gives up the restore nor lets the loop record
            # over the saved choice; the store's temp file is per process and never left behind. IDE stubbed.
            @{ Name = "ActiveDocumentRestore.Test"
               Sources = @("tests\ActiveDocumentRestore.Test.cs", "tests\ActiveDocumentRestore.Stubs.cs", "ActiveDocumentRestoreCommand.cs")
               Refs = @("System.dll", "System.Windows.Forms.dll") }
            # File-level EQUATEs in a .inc (outside any CLASS body) must become CodeGraph symbols.
            @{ Name = "ClarionParser.IncEquates.Test"
               Sources = @("tests\ClarionParser.IncEquates.Test.cs", "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # ITEMIZE members are indexed as Prefix:Name (PRE / empty PRE / no PRE), value-less members
            # included, across ParseIncFile, ParseMemberFile DATA and the library .EQU scan (x86 SQLite).
            @{ Name = "ClarionParser.Itemize.Test"
               Sources = @("tests\ClarionParser.Itemize.Test.cs", "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "CodeGraph\Parsing\Models\SolutionProject.cs",
                           "CodeGraph\Graph\CodeGraphDatabase.cs", "CodeGraph\Graph\CodeGraphProvider.cs",
                           "Services\ClarionGraphService.cs", "Services\LibraryIndexer.cs", "Services\SymbolIndex.cs", "Services\IndexRunGate.cs",
                           "Services\ClarionVersionService.cs", "Services\ClarionConfigDirectory.cs", "Services\EffectiveClarionVersion.cs",
                           "Services\LspClient.cs", "Services\LspTextDiff.cs", "Services\JsonTextStream.cs", "Services\LspTrace.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll", "System.Data.dll", "System.Web.Extensions.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86" }
            # A CLASS / INTERFACE whose label contains a colon must be indexed, with its methods and
            # the instances/references typed by it (GH #246).
            @{ Name = "ClarionParser.ColonClass.Test"
               Sources = @("tests\ClarionParser.ColonClass.Test.cs", "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # GH #266: the path policy the MCP file tools and the Everything tools go through - allowed roots,
            # deny-wins, canonicalisation (sibling prefix, '..', UNC/device, case, separators), a REAL junction
            # and 8.3 name, CA's read-only data folder, and the no-solution scope.
            @{ Name = "McpPathPolicy.Test"
               Sources = @("tests\McpPathPolicy.Test.cs", "Services\McpPathPolicy.cs")
               Refs = @("System.dll") }
            # The REAL indexer end to end over a temp solution: CLASS MODULE()/LINK() followed to the implementing
            # .clw through the project's .red and build configuration (GH #245), honest coverage status (#244 item 3);
            # every feature check re-run with the pass off, where it must fail (x86 SQLite).
            @{ Name = "CodeGraphIndexer.ModuleLink.Test"
               Sources = @("tests\CodeGraphIndexer.ModuleLink.Test.cs",
                           "CodeGraph\Graph\CodeGraphIndexer.cs", "CodeGraph\Graph\CodeGraphDatabase.cs", "CodeGraph\Graph\IndexProgress.cs",
                           "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs", "CodeGraph\Parsing\SolutionParser.cs",
                           "CodeGraph\Parsing\ProjectParser.cs", "CodeGraph\Parsing\SourceResolver.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "CodeGraph\Parsing\Models\SolutionProject.cs",
                           "Services\RedFileService.cs", "Services\RedResolution.cs", "Services\ClarionConfigDirectory.cs",
                           "Services\ClarionVersionService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll", "System.Data.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86" }
            # The REAL indexer end to end: data in an .inc INCLUDEd into the PROGRAM's global data is scope='global',
            # and calls through such a global object (Mark's ActiveThreads.Add in N modules) link to the class's
            # method, inherited ones included (GH #246); every feature check re-run with the pass off (x86 SQLite).
            @{ Name = "CodeGraphIndexer.GlobalObject.Test"
               Sources = @("tests\CodeGraphIndexer.GlobalObject.Test.cs",
                           "CodeGraph\Graph\CodeGraphIndexer.cs", "CodeGraph\Graph\CodeGraphDatabase.cs", "CodeGraph\Graph\IndexProgress.cs",
                           "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs", "CodeGraph\Parsing\SolutionParser.cs",
                           "CodeGraph\Parsing\ProjectParser.cs", "CodeGraph\Parsing\SourceResolver.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "CodeGraph\Parsing\Models\SolutionProject.cs",
                           "Services\RedFileService.cs", "Services\RedResolution.cs", "Services\ClarionConfigDirectory.cs",
                           "Services\ClarionVersionService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll", "System.Data.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86" }
            # GH #271: Pass 2b reads INCLUDE() as the compiler does - a commented-out INCLUDE is not followed, one that
            # does not resolve inside a conditional COMPILE/OMIT block is unresolved_conditional (not a gap), and the
            # warning names each real gap with its includer; the feature checks fail on 6.1.0 (x86 SQLite).
            @{ Name = "CodeGraphIndexer.IncludeOutcome.Test"
               Sources = @("tests\CodeGraphIndexer.IncludeOutcome.Test.cs",
                           "CodeGraph\Graph\CodeGraphIndexer.cs", "CodeGraph\Graph\CodeGraphDatabase.cs", "CodeGraph\Graph\IndexProgress.cs",
                           "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs", "CodeGraph\Parsing\SolutionParser.cs",
                           "CodeGraph\Parsing\ProjectParser.cs", "CodeGraph\Parsing\SourceResolver.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "CodeGraph\Parsing\Models\SolutionProject.cs",
                           "Services\RedFileService.cs", "Services\RedResolution.cs", "Services\ClarionConfigDirectory.cs",
                           "Services\ClarionVersionService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll", "System.Data.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86" }
            # integ-1023 review D1-D5: the include guard (ABUTIL.INC's OMIT('_EndOfInclude_',_XPresent_)) does not hide
            # a real gap, a .cwproj-listed .inc is judged by how it is reached, an outer block can end inside an OMIT,
            # a | in a comment is no continuation, and the .red {include} log line is worded per reason; 15 of 22
            # checks fail on 3e57b84 (x86 SQLite).
            @{ Name = "CodeGraphIndexer.IncludeGuard.Test"
               Sources = @("tests\CodeGraphIndexer.IncludeGuard.Test.cs",
                           "CodeGraph\Graph\CodeGraphIndexer.cs", "CodeGraph\Graph\CodeGraphDatabase.cs", "CodeGraph\Graph\IndexProgress.cs",
                           "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs", "CodeGraph\Parsing\SolutionParser.cs",
                           "CodeGraph\Parsing\ProjectParser.cs", "CodeGraph\Parsing\SourceResolver.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "CodeGraph\Parsing\Models\SolutionProject.cs",
                           "Services\RedFileService.cs", "Services\RedResolution.cs", "Services\ClarionConfigDirectory.cs",
                           "Services\ClarionVersionService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll", "System.Data.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86" }
            # ef2fd2a9 / cea47717: an incremental index of a database built below the current feature level (or with
            # none recorded) runs FULL and says so; a current database stays incremental and an unchanged one reads
            # "up to date", not a bare 0-symbol success (x86 SQLite).
            @{ Name = "CodeGraphIndexer.FeatureUpgrade.Test"
               Sources = @("tests\CodeGraphIndexer.FeatureUpgrade.Test.cs",
                           "CodeGraph\Graph\CodeGraphIndexer.cs", "CodeGraph\Graph\CodeGraphDatabase.cs", "CodeGraph\Graph\IndexProgress.cs",
                           "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs", "CodeGraph\Parsing\SolutionParser.cs",
                           "CodeGraph\Parsing\ProjectParser.cs", "CodeGraph\Parsing\SourceResolver.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "CodeGraph\Parsing\Models\SolutionProject.cs",
                           "Services\RedFileService.cs", "Services\RedResolution.cs", "Services\ClarionConfigDirectory.cs",
                           "Services\ClarionVersionService.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll", "System.Data.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86" }
            # 583abdec: every host-side index runs in clarion-indexer.exe (64-bit), not in the 32-bit IDE. This 32-bit
            # harness builds the REAL indexer into a deployed-addin-like folder (32-bit interop at its root), runs it
            # through the REAL OutOfProcessIndexer, and requires the in-process run's result, rows and progress stream;
            # plus the protocol round trip, cancel (graceful and killed), child failures and the missing-exe fallback.
            # Review fixes: the child's own writer lock (D1), an unstartable exe falls back (D2), a finished run is never
            # a cancel (D3), the last stdout object (D4a), UTF-8 before the preload (D4b), absolute paths (L1), one-line
            # stderr text (L2), the short MCP failure reply (L3).
            @{ Name = "OutOfProcessIndexer.Test"
               Sources = @("tests\OutOfProcessIndexer.Test.cs", "Services\OutOfProcessIndexer.cs", "CodeGraph\Graph\IndexerProtocol.cs",
                           "CodeGraph\Graph\CodeGraphIndexer.cs", "CodeGraph\Graph\CodeGraphDatabase.cs", "CodeGraph\Graph\IndexProgress.cs",
                           "CodeGraph\Parsing\ClarionParser.cs", "CodeGraph\Parsing\ClarionBuiltins.cs", "CodeGraph\Parsing\SolutionParser.cs",
                           "CodeGraph\Parsing\ProjectParser.cs", "CodeGraph\Parsing\SourceResolver.cs",
                           "CodeGraph\Parsing\Models\ClarionSymbol.cs", "CodeGraph\Parsing\Models\ClarionRelationship.cs",
                           "CodeGraph\Parsing\Models\ParseResult.cs", "CodeGraph\Parsing\Models\SolutionProject.cs",
                           "Services\RedFileService.cs", "Services\RedResolution.cs", "Services\ClarionConfigDirectory.cs",
                           "Services\ClarionVersionService.cs", "Services\EncodingHelper.cs", "Services\IndexRunGate.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Xml.dll", "System.Data.dll")
               RepoRefs = @("lib\sqlite-fts5\System.Data.SQLite.dll")
               Copy = @("lib\sqlite-fts5\System.Data.SQLite.dll", "lib\sqlite-fts5\SQLite.Interop.dll")
               Platform = "x86"
               Args = @($csc, $RepoDir) }
            # GH #205: write_file / append_to_file vs an open native editor tab - refuse under unsaved changes (file
            # untouched), reload a clean tab only AFTER a successful write, the override, the CA Editor note, and the
            # could-not-check line when no IDE answers. The IDE half is pinned by OpenTabGuard.SourceScan.ps1.
            @{ Name = "OpenTabGuard.Test"
               Sources = @("tests\OpenTabGuard.Test.cs", "Services\OpenTabGuard.cs", "Services\FileIdentity.cs")
               Refs = @("System.dll", "System.Core.dll", "System.Web.Extensions.dll") }
            # GH #205 review: the IDE file-watcher hold's bookkeeping - counted holders, a cap from the FIRST hold that is
            # never renewed (plus a cool-down), and a generation per hold that a late timer or release must still match.
            @{ Name = "WatcherHoldBook.Test"
               Sources = @("tests\WatcherHoldBook.Test.cs", "Services\WatcherHoldBook.cs")
               Refs = @("System.dll") }
            # GH #205 pipeline run 1: the open-tab check matches a tab by FILE IDENTITY - a REAL junction, hard link,
            # subst drive, symlink and 8.3 name (the last three skipped, never passed, when this machine cannot make them).
            @{ Name = "FileIdentity.Test"
               Sources = @("tests\FileIdentity.Test.cs", "Services\FileIdentity.cs")
               Refs = @("System.dll") }
            # GH #205 pipeline run 1: IDEs asked in parallel inside one budget, a timed-out IDE skipped for a minute; and
            # one endpoint record per IDE whichever of its servers runs (no chat pane needed).
            @{ Name = "IdeFanOut.Test"
               Sources = @("tests\IdeFanOut.Test.cs", "Services\IdeFanOut.cs", "Services\EndpointOwnership.cs", "Services\OpenTabGuard.cs", "Services\FileIdentity.cs")
               Refs = @("System.dll", "System.Core.dll") }
            # GH #205 pipeline run 2: every endpoint publish sweeps records (with tokens) of IDEs that died without Stop.
            @{ Name = "EndpointRecordSweep.Test"
               Sources = @("tests\EndpointRecordSweep.Test.cs", "Services\IdeEndpointRecord.cs", "Services\IdeSolutionRecord.cs", "Services\EncodingHelper.cs")
               Refs = @("System.dll", "System.Web.Extensions.dll") }
            # GH #279 follow-up (integ-1023 review D6/D7): the header's CodeGraph line - "Rebuilding index..." for an
            # incremental request that runs full, the early real-work signal, an error/cancel line that stays until the
            # database changes, and a queued check for another solution. Does not exist on 3e57b84.
            @{ Name = "IndexHeaderStatus.Test"
               Sources = @("tests\IndexHeaderStatus.Test.cs", "Services\IndexHeaderStatus.cs")
               Refs = @("System.dll") }
        )
        if ($Probe) {
            $harnesses += @{ Name = "VsCodeSettingsImporter.LiveProbe"
                             Sources = @("tests\VsCodeSettingsImporter.LiveProbe.cs", "Services\VsCodeSettingsImporter.cs")
                             Refs = @("System.dll", "System.Web.Extensions.dll") }
        }

        foreach ($h in $harnesses) {
            Section $h.Name
            $exe  = Join-Path $OutDir ($h.Name + ".exe")
            $srcs = $h.Sources | ForEach-Object { Join-Path $RepoDir $_ }

            $missing = $srcs | Where-Object { -not (Test-Path $_) }
            if ($missing) {
                Write-Host "  source(s) missing: $($missing -join ', ')" -ForegroundColor Red
                $failures += $h.Name + " (missing source)"
                continue
            }

            $refArgs = @($h.Refs | ForEach-Object { "/r:$_" })
            # RepoRefs: assemblies vendored in the repo (paths relative to ClarionAssistant\).
            if ($h.RepoRefs) { $refArgs += @($h.RepoRefs | ForEach-Object { "/r:" + (Join-Path $RepoDir $_) }) }
            $platArgs = @(if ($h.Platform) { "/platform:" + $h.Platform })
            # Copy: runtime files the harness exe must find next to itself (native interop, vendored refs).
            if ($h.Copy) {
                try { $h.Copy | ForEach-Object { Copy-Item (Join-Path $RepoDir $_) $OutDir -Force -ErrorAction Stop } }
                catch {
                    Write-Host "  COPY FAILED: $($_.Exception.Message)" -ForegroundColor Red
                    $failures += $h.Name + " (copy failed)"
                    continue
                }
            }
            & $csc /nologo /warn:0 /out:$exe $platArgs $refArgs $srcs 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
            if ($LASTEXITCODE -ne 0) {
                Write-Host "  COMPILE FAILED" -ForegroundColor Red
                $failures += $h.Name + " (compile failed)"
                continue
            }

            # @() around the whole thing: an if-expression unrolls a one-element array to a bare
            # string, and splatting a string passes its FIRST CHARACTER ("H" for H:\...).
            $exeArgs = @(if ($h.Args) { $h.Args })
            & $exe @exeArgs
            $ran++
            if ($LASTEXITCODE -ne 0) { $failures += $h.Name }
        }

        try { Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# --------------------------------------------------------------------------- node harnesses
if (-not $CSharpOnly -and -not $InstallerOnly) {

    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $node) {
        Write-Host ""
        Write-Host "node not found on PATH — cannot run the Terminal page tests." -ForegroundColor Red
        $failures += "node harnesses (no node.exe)"
    }
    else {
        # Install the dev-only test dependencies if they are not there yet.
        #
        # Terminal\test\package.json has declared jsdom for a long time and is committed — what was
        # missing was anyone ever installing it. So on a fresh clone, or in any git worktree, the one
        # harness that needs it exited 2 and this script reported "dependency missing", which reads as
        # a problem with your machine rather than with the code. vscode-import-ui.test.js was failing
        # 8 assertions from at least v5.8.1 until 2026-08-31 and nobody saw it, because it only ever
        # turned red on a checkout that happened to have node_modules populated.
        #
        # npm install, not npm ci: there is no package-lock.json in that folder.
        #
        # --loglevel=error, NOT --silent. Measured with an unreachable registry: --silent gives
        # exit=1 with ZERO bytes on both streams, because loglevel=silent suppresses `npm error`
        # too. That would print nothing at all and leave the run reporting only "SKIPPED —
        # dependency missing" — which is precisely the reads-as-a-machine-problem failure this
        # whole change exists to remove, reintroduced one layer down.
        #
        # The catch is load-bearing and must stay with the loglevel change. $ErrorActionPreference
        # is "Stop" at the top of this script, and under Windows PowerShell a native command's
        # stderr redirected with 2>&1 into a pipeline becomes a terminating RemoteException. With
        # no catch, one `npm WARN` would abort the ENTIRE suite — including the installer harnesses
        # that guard configure.ps1. --silent was hiding that; naming the exit code arms it.
        $testDir = Join-Path $RepoDir "Terminal\test"
        if ((Test-Path (Join-Path $testDir "package.json")) -and
            -not (Test-Path (Join-Path $testDir "node_modules\jsdom"))) {
            $npm = (Get-Command npm -ErrorAction SilentlyContinue)
            if ($npm) {
                Write-Host ""
                Write-Host "Installing dev-only test dependencies (Terminal\test)..." -ForegroundColor Cyan
                Push-Location $testDir
                try {
                    & npm install --no-audit --no-fund --loglevel=error 2>&1 |
                        ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
                    if ($LASTEXITCODE -ne 0) {
                        Write-Host "npm install failed (exit $LASTEXITCODE) — the jsdom harnesses will report 'could not run'." -ForegroundColor Yellow
                    }
                }
                catch { Write-Host "npm install failed: $_" -ForegroundColor Yellow }
                finally { Pop-Location }
            }
            else {
                Write-Host "npm not found — cannot install the node test dependencies." -ForegroundColor Yellow
            }
        }

        $jsTests = Get-ChildItem $testDir -Filter *.test.js -ErrorAction SilentlyContinue |
                   Sort-Object Name
        foreach ($t in $jsTests) {
            Section $t.Name
            & $node $t.FullName
            $code = $LASTEXITCODE
            $ran++
            # Exit 2 is the agreed "could not run — missing dev dependency" signal (see
            # vscode-import-ui.test.js). Report it distinctly: it is neither a pass nor a real failure,
            # but it must still make the overall run non-zero so it cannot be mistaken for green.
            if ($code -eq 2) {
                Write-Host "  SKIPPED — dependency missing (see message above)" -ForegroundColor Yellow
                $failures += $t.Name + " (dependency missing)"
            }
            elseif ($code -ne 0) { $failures += $t.Name }
        }
    }
}

# --------------------------------------------------------------------- ClarionAssistant harnesses
if (-not $CSharpOnly -and -not $NodeOnly) {

    # Auto-discovered, like the installer family below, so a new harness is picked up by being
    # written rather than by also remembering to edit this file. Run-Tests.ps1 EXCLUDES ITSELF —
    # it lives in this directory and would otherwise recurse.
    $caTests = Get-ChildItem $PSScriptRoot -Filter *.ps1 -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -ne 'Run-Tests.ps1' } |
               Sort-Object Name
    foreach ($t in $caTests) {
        Section $t.Name
        & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $t.FullName
        $code = $LASTEXITCODE
        $ran++
        # Exit 2 is the shared "could not run" signal. A harness that skips because its subject
        # was never built has proven nothing, and must not read as green.
        if ($code -eq 2) {
            Write-Host "  SKIPPED — could not run (see message above)" -ForegroundColor Yellow
            $failures += $t.Name + " (could not run)"
        }
        elseif ($code -ne 0) { $failures += $t.Name }
    }
}

# --------------------------------------------------------------------------- installer harnesses
if (-not $CSharpOnly -and -not $NodeOnly) {

    $installerTests = Get-ChildItem (Join-Path $RootDir "installer\tests") -Filter *.ps1 -ErrorAction SilentlyContinue |
                      Sort-Object Name
    if (-not $installerTests) {
        Write-Host ""
        Write-Host "no installer harnesses found under installer\tests — expected at least one." -ForegroundColor Red
        $failures += "installer harnesses (none found)"
    }
    foreach ($t in $installerTests) {
        Section $t.Name
        # Run each in its own powershell so a harness cannot leak $ErrorActionPreference, cwd, or a
        # sandboxed $env:USERPROFILE into the next one. These harnesses reassign USERPROFILE/APPDATA
        # while the script under test runs; a leak would point a later test at the real profile.
        & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $t.FullName
        $code = $LASTEXITCODE
        $ran++
        # Exit 2 is the shared "could not run" signal (see the node family above). For these it also
        # covers "the defect cannot reproduce on this machine" — e.g. the system ANSI codepage is
        # UTF-8, so an encoding assertion could not fail and therefore proves nothing. That must not
        # read as green.
        if ($code -eq 2) {
            Write-Host "  SKIPPED — could not run (see message above)" -ForegroundColor Yellow
            $failures += $t.Name + " (could not run)"
        }
        elseif ($code -ne 0) { $failures += $t.Name }
    }
}

# --------------------------------------------------------------------------- summary
Write-Host ""
Write-Host ("=" * 60)
if ($failures.Count -eq 0) {
    Write-Host "ALL HARNESSES PASSED ($ran run)" -ForegroundColor Green
    exit 0
}
Write-Host "$($failures.Count) harness(es) failed or could not run, of ${ran}:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
