# ClarionAssistant Deploy Script
# Builds and deploys the addin for Clarion 10, 11, 11.1, 12, or all.
# Usage: .\deploy.ps1 [-Version 10|11|11.1|12|all] [-NoBuild] [-Kill] [-SkipBomGuard] [-AllowRunning]

param(
    [ValidateSet("10","11","11.1","12","all")]
    [string]$Version = "all",  # Which Clarion version(s) to build/deploy
    [switch]$NoBuild,          # Skip build, just copy
    [switch]$Kill,             # Kill Clarion IDE before deploying
    [switch]$SkipBomGuard,     # Ship without the BOM check (loud, deliberate; see the gate below)
    [switch]$AllowRunning      # Deploy even though something holds the target files (see Get-DeployBlockers)
)

$ErrorActionPreference = "Stop"

# Locate MSBuild.exe without hardcoding a Visual Studio version/edition.
# Order: vswhere (covers VS 2019/2022/18+, any edition) -> common install paths.
function Resolve-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                            -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }

    # Fallback: scan known roots if vswhere is unavailable.
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $root) { continue }
        $candidate = Get-ChildItem -Path (Join-Path $root "Microsoft Visual Studio") `
                        -Filter MSBuild.exe -Recurse -ErrorAction SilentlyContinue |
                        Where-Object { $_.FullName -match "\\Current\\Bin\\MSBuild\.exe$" } |
                        Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "MSBuild.exe not found. Install Visual Studio with the MSBuild component, or set `$MSBuild manually."
}

$ProjectDir  = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir "ClarionAssistant.csproj"
$MSBuild     = Resolve-MSBuild

# Indexer build output. VENDORED into this repo as indexer/ (GitHub #30) — self-contained,
# no longer built from the external H:\DevLaptop\ClarionLSP\indexer tree. Override with
# $env:CLARIONINDEXER_DIR only if you keep the indexer somewhere else.
$IndexerDir    = if ($env:CLARIONINDEXER_DIR) { $env:CLARIONINDEXER_DIR } else { Join-Path $ProjectDir "indexer" }
# Standalone MCP server (ticket d051fbd1) - the editor-agnostic half of the tools as its own
# stdio process. Deployed INTO the addin folder rather than beside it, because the server
# resolves lsp-server\ relative to its own directory; from anywhere else it loses the bundled
# language server and node.exe.
$McpServerDir  = Join-Path $ProjectDir "mcp-server"
$IndexerFile   = "$IndexerDir\ClarionIndexer.csproj"
$IndexerOutput = "$IndexerDir\bin\Debug"

# Version-specific config. "Root" entries are last-resort fallback paths only — actual
# resolution goes registry -> these fallbacks -> drive-root glob scan (Resolve-ClarionRoot).
# 11 and 11.1 are DISTINCT Clarion releases (confirmed via registry: separate install dirs,
# not aliases of each other) and must never share a build/deploy target — their binding DLLs
# (CWBinding.dll etc, see ClarionAssistant.csproj) are version-specific, so building against
# one and shipping into the other risks an ABI mismatch.
$Versions = @{
    "12"   = @{ RegistryKeys = @("Clarion12");              Fallbacks = @("C:\Clarion12");                          GlobPatterns = @("Clarion12*");            Output = "bin\Debug-C12" }
    "11.1" = @{ RegistryKeys = @("Clarion11.1","Clarion111"); Fallbacks = @("d:\Clarion11.1EE", "C:\Clarion11.1");   GlobPatterns = @("Clarion11.1*","Clarion111*"); Output = "bin\Debug-C11.1" }
    "11"   = @{ RegistryKeys = @("Clarion11");              Fallbacks = @("C:\Clarion11-13372", "C:\Clarion11");    GlobPatterns = @("Clarion11","Clarion11-*"); Output = "bin\Debug-C11" }
    "10"   = @{ RegistryKeys = @("Clarion10");              Fallbacks = @("C:\Clarion10", "C:\Clarion10v8");        GlobPatterns = @("Clarion10*");            Output = "bin\Debug-C10" }
}

# Resolve ALL install roots for a Clarion version: registry (authoritative, modern Clarion
# versions register a "root" value under SoftVelocity\Clarion<key>) + known fallback paths
# (other dev machines) + drive-root glob scan. Returns EVERY existing install, deduped,
# registry hit first (the build binds against the first root's DLLs).
#
# Returning only the FIRST hit shipped a real incident (2026-08-13): the registry resolved
# Clarion 11 to C:\Clarion11, so the ALSO-LIVE C:\Clarion11-13372 silently kept a stale
# addin — the developer ran the old indexer for a day while every verification pass showed
# the new build "deployed and hash-verified" (in the four folders the script chose). A
# machine with two installs of one version must get the addin in BOTH.
function Resolve-ClarionRoots {
    param(
        [string[]]$RegistryKeys,
        [string[]]$Fallbacks,
        [string[]]$GlobPatterns
    )

    function Test-ClarionRoot([string]$path) {
        if (-not $path) { return $false }
        return Test-Path (Join-Path $path "bin\ICSharpCode.Core.dll")
    }

    $found = New-Object System.Collections.Generic.List[string]
    $seen  = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    function Add-Root([string]$path) {
        if (-not $path) { return }
        $norm = $path.TrimEnd('\')
        if ((Test-ClarionRoot $norm) -and $seen.Add($norm)) { $found.Add($norm) }
    }

    $regHives = @(
        "HKLM:\SOFTWARE\WOW6432Node\SoftVelocity",
        "HKLM:\SOFTWARE\SoftVelocity",
        "HKCU:\SOFTWARE\SoftVelocity"
    )
    foreach ($hive in $regHives) {
        foreach ($key in $RegistryKeys) {
            Add-Root (Get-ItemProperty -Path "$hive\$key" -Name root -ErrorAction SilentlyContinue).root
        }
    }

    foreach ($p in $Fallbacks) { Add-Root $p }

    $drives = (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
                Where-Object { Test-Path $_.Root }).Root
    foreach ($drive in $drives) {
        foreach ($pattern in $GlobPatterns) {
            Get-ChildItem -Path $drive -Directory -Filter $pattern -ErrorAction SilentlyContinue |
                ForEach-Object { Add-Root $_.FullName }
        }
    }

    return ,$found.ToArray()
}

function Resolve-BuildOutputDir {
    param(
        [string]$ProjectDir,
        [string]$PreferredOutput
    )

    # NOTE: deliberately no fallback to the generic bin\Debug-C folder. That folder is whatever
    # was last built by a plain `msbuild /p:Configuration=Debug` with no ClarionVersion pinned
    # (e.g. an ad-hoc build-installer.ps1 run) - it could be built against ANY Clarion version's
    # binding DLLs. Falling back to it here previously caused a real incident: a Clarion-12-built
    # DLL got silently deployed into a live Clarion 11.1 install because bin\Debug-C11.1 didn't
    # exist yet. Missing the real per-version folder must be a clean skip, not a guess.
    return Join-Path $ProjectDir $PreferredOutput
}

# Which versions to process
if ($Version -eq "all") {
    $TargetVersions = @("12", "11.1", "11", "10")
} else {
    $TargetVersions = @($Version)
}

# Resolve install roots up front (needed by both the build and deploy loops below, and
# independent of -NoBuild). A version with no resolvable install is skipped, not fatal —
# previously a missing version aborted the whole run because MSBuild's own hardcoded
# ClarionRoot default in Directory.Build.props errored out mid-build.
$ResolvedRoots = @{}
foreach ($ver in $TargetVersions) {
    $cfg   = $Versions[$ver]
    $roots = Resolve-ClarionRoots -RegistryKeys $cfg.RegistryKeys -Fallbacks $cfg.Fallbacks -GlobPatterns $cfg.GlobPatterns
    if ($roots -and $roots.Count -gt 0) {
        $ResolvedRoots[$ver] = $roots
        Write-Host "Clarion ${ver}: $($roots -join ', ')" -ForegroundColor DarkGray
    } else {
        Write-Host "Clarion ${ver}: no install found (registry / known paths / drive scan) - will skip" -ForegroundColor DarkGray
    }
}

# Files and folders to deploy
$Items = @(
    "ClarionAssistant.dll"
    "ClarionAssistant.pdb"
    "ClarionAssistant.addin"
    # Shared ClarionLsp contract assembly — our addin references IClarionLanguageClient /
    # ClarionLspLocator (SharedLspBridge) so this DLL MUST ship in our addin folder, or the
    # CLR can't resolve the type and the ENTIRE addin silently fails to load (Tools menu empty).
    # SharpDevelop does NOT resolve it from ClarionLsp's own folder into ours. Required for BOTH
    # the shared path AND the no-ClarionLsp fallback (the assembly is absent otherwise).
    "ClarionLsp.Contracts.dll"
    "Microsoft.Web.WebView2.Core.dll"
    "Microsoft.Web.WebView2.WinForms.dll"
    "Microsoft.Web.WebView2.Wpf.dll"
    "WebView2Loader.dll"
    # PdfPig — in-process PDF text extraction for DocGraph ingestion (#167). ALL of these ship:
    # the five System.*/Microsoft.Bcl shims are NOT inbox on .NET Framework 4.8, they arrive via
    # PdfPig's own package dependencies, and omitting any one of them fails at RUNTIME (the first
    # PDF import throws FileNotFoundException) rather than at build — so a missing entry here would
    # look exactly like the silent "no documents found" bug this replaced. Keep in sync with the
    # matching block in installer\ClarionAssistant.iss.
    "UglyToad.PdfPig.dll"
    "UglyToad.PdfPig.Core.dll"
    "UglyToad.PdfPig.DocumentLayoutAnalysis.dll"
    "UglyToad.PdfPig.Fonts.dll"
    "UglyToad.PdfPig.Package.dll"
    "UglyToad.PdfPig.Tokenization.dll"
    "UglyToad.PdfPig.Tokens.dll"
    "Microsoft.Bcl.HashCode.dll"
    "System.Buffers.dll"
    "System.Memory.dll"
    "System.Numerics.Vectors.dll"
    "System.Runtime.CompilerServices.Unsafe.dll"
    # DEPLOY INVARIANT: Terminal\ is copied as a whole folder, which is the ONLY safe way to ship the
    # Monaco editor pages. monaco-embeditor.html and monaco-diff.html have a HARD runtime dependency on
    # Terminal\clarion-language.js (the shared Clarion grammar + folding registration, task 04dd97f9) —
    # if either HTML is hot-copied WITHOUT clarion-language.js, the editor fails to start (the pages now
    # detect this and show a "Failed to load clarion-language.js" message instead of hanging). Never
    # single-file hot-copy either HTML without also copying clarion-language.js.
    "Terminal"
    "TaskLifecycleBoard"
    "runtimes"
)

# LSP Server (Clarion Language Server) — #40: PURE upstream msarson/Clarion-Extension at the pinned tag,
# with NO CodeGraph overlay. CodeGraph go-to-def / references / completion are served C#-side
# (SharedLspBridge + CodeGraphProvider), so the bundled server is stock upstream. The pure build is a clean
# tag checkout produced by lsp-server-sync\Sync-LspServer.ps1 -Pure, cached under .lsp-build\<tag>.
# $env:CLARIONLSP_ROOT still overrides (dev escape hatch) if you deliberately want a custom server tree.
function Resolve-LspBuild {
    if ($env:CLARIONLSP_ROOT) {
        # Loud, because this is the one path that bypasses the tag-in-path pin check below: whatever
        # is in that tree ships, while lsp-snapshot.json goes on asserting resolvedCommit. A consumer
        # then has no way to tell which server they actually received.
        Write-Host "  WARN  CLARIONLSP_ROOT is set — shipping an UNPINNED LSP from $env:CLARIONLSP_ROOT" -ForegroundColor Yellow
        Write-Host "  WARN  lsp-snapshot.json will NOT describe what ships. Do not use this for a release build." -ForegroundColor Yellow
        return $env:CLARIONLSP_ROOT
    }
    $syncScript = Join-Path $ProjectDir "lsp-server-sync\Sync-LspServer.ps1"
    $manifest   = Get-Content (Join-Path $ProjectDir "lsp-server-sync\lsp-snapshot.json") -Raw | ConvertFrom-Json
    $tag        = if ($manifest.resolvedTag) { $manifest.resolvedTag } else { $manifest.targetPin.tag }
    $pureDir    = Join-Path $ProjectDir (".lsp-build\" + $tag)
    if (-not (Test-Path (Join-Path $pureDir "out\server\src\server.js"))) {
        Write-Host "  INFO  pure LSP build for $tag missing — building via Sync-LspServer.ps1 -Pure ..." -ForegroundColor Cyan
        # | Out-Host is load-bearing, do NOT drop it. A PowerShell function returns EVERYTHING written
        # to its output stream, not just what `return` names. Without this, every line git and npm emit
        # (checkout notice, "added 255 packages", the whole tsc/copy-data transcript) became part of
        # this function's return value, so $LspSourceDir came back as an ARRAY of output lines with the
        # real path merely last.
        #
        # That failed in the worst possible way -- silently, and only on the FIRST deploy in a fresh
        # tree, the one run that has to build. `if (Test-Path $LspSourceDir)` passed, because a
        # non-empty array is truthy, so node.exe still copied and the block looked alive. But
        # "$LspSourceDir\out\server" interpolated the whole array space-joined into a nonsense path, so
        # the server copy was skipped with one DarkGray line. The guard below missed it too: it tests
        # $pureDir directly, which is still a clean string inside the function. Second runs worked
        # because the build already existed and this branch never ran.
        #
        # Net effect before the fix: a release built from a fresh worktree in one pass shipped an addin
        # with NO language server. Out-Host writes the transcript to the console without putting it on
        # the pipeline. Ticket 0cd0b20c.
        & $syncScript -Pure -Tag $tag | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Host "  WARN  pure LSP build failed (exit $LASTEXITCODE) — LSP copy will be skipped." -ForegroundColor Yellow }
        # Loud guard: on a from-scratch build the out/ is created mid-run; if it's not visible yet the copy
        # below would SILENTLY skip and ship an addin with NO server. Fail loudly so the installer never does.
        elseif (-not (Test-Path (Join-Path $pureDir "out\server\src\server.js"))) {
            Write-Host "  WARN  pure build reported success but out\server is not visible yet — RE-RUN deploy.ps1 to copy the LSP (first-run timing)." -ForegroundColor Yellow
        }
    }
    return $pureDir
}
$LspSourceDir = Resolve-LspBuild

# --- Verify the LSP about to ship IS the pinned one --------------------------------------------
# Previously nothing checked this. lsp-snapshot.json could assert resolvedCommit while a different
# server.js was copied (CLARIONLSP_ROOT override, hand-edited tree, an out/ built from another tag),
# and the manifest is the ONLY thing a consumer can inspect to know what they got. Assert before any
# copy so "what shipped" and "what is pinned" cannot silently diverge.

# Run git and return its stdout, or $null if it failed for ANY reason. Do NOT replace this with a
# bare `git ... 2>$null`.
#
# Under Windows PowerShell 5.1, redirecting a NATIVE command's stderr wraps the output in a
# NativeCommandError record, and $ErrorActionPreference='Stop' (set at the top of this file) makes
# that record TERMINATING. So `$x = (git ... 2>$null); if (-not $x) { warn; return }` never reaches
# the soft-fail branch -- the script dies on the git line. Reproduced on 5.1.19041.6456 and on
# 5.1.26100.8972; the identical code under pwsh 7 returns empty and continues, which is why the bug
# looks like it isn't there when tested from a pwsh prompt.
#
# That is the exact failure this file's LSP handling is supposed to avoid: a contributor whose pure
# build fails, or whose CLARIONLSP_ROOT is a plain unpacked server tree with no .git, would see the
# WARN and then have the whole deploy abort anyway -- before a single Clarion version builds.
#
# Relaxing $ErrorActionPreference locally makes the NativeCommandError non-terminating; 2>&1 keeps
# git's stderr off the console; the ErrorRecord filter keeps it out of the return value; the
# try/catch covers git being absent from PATH entirely (CommandNotFoundException terminates
# regardless of the preference).
function Invoke-GitQuiet([string[]]$GitArgs) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git @GitArgs 2>&1
        if ($LASTEXITCODE -ne 0) { return $null }
        $clean = $out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
        if (-not $clean) { return $null }
        return (($clean -join "`n").Trim())
    } catch {
        return $null
    } finally {
        $ErrorActionPreference = $prevEap
    }
}

# Returns $true when the LSP may ship, $false when it must not. It does NOT exit: an LSP-copy
# problem is not a reason to abandon the Clarion builds. This used to `exit 7`, which aborted all
# four Clarion targets AND the C# addin before a single one was built -- cutting straight across
# the "a build failure for ONE Clarion release must not block shipping to the others" rule the
# final summary below is built around. The caller skips just the LSP copy instead, so every version
# still deploys and keeps whatever server it already had, and the run ends non-zero so nobody reads
# it as clean.
function Test-LspPin($sourceDir) {
    $manifest = Get-Content (Join-Path $ProjectDir "lsp-server-sync\lsp-snapshot.json") -Raw | ConvertFrom-Json
    $pinned   = $manifest.resolvedCommit
    if (-not $pinned) {
        Write-Host "  WARN  manifest has no resolvedCommit — cannot verify the bundled LSP." -ForegroundColor Yellow
        return $true
    }
    $head = Invoke-GitQuiet @('-C', $sourceDir, 'rev-parse', '--short', 'HEAD')
    if (-not $head) {
        Write-Host "  WARN  $sourceDir is not a git tree — cannot verify it matches pin $pinned." -ForegroundColor Yellow
        return $true
    }
    # Prefix compare: `git rev-parse --short` auto-scales its length, so 7- and 8-char forms of the
    # same commit must still count as a match.
    if (-not ($head.StartsWith($pinned) -or $pinned.StartsWith($head))) {
        if ($env:CLARIONLSP_ROOT) {
            Write-Host "  WARN  bundled LSP is $head but the pin says $pinned (CLARIONLSP_ROOT override in effect)." -ForegroundColor Yellow
            return $true
        }
        Write-Host "  FAIL  bundled LSP is $head but lsp-snapshot.json pins $pinned." -ForegroundColor Red
        Write-Host "        Re-run lsp-server-sync\Sync-LspServer.ps1 -Pure, or bump the pin deliberately." -ForegroundColor Red
        Write-Host "        The LSP copy will be SKIPPED for every version; the rest of the deploy continues." -ForegroundColor Red
        return $false
    }
    Write-Host "  OK    bundled LSP matches pin: $pinned ($($manifest.resolvedTag))" -ForegroundColor Green
    return $true
}
$LspPinOK = Test-LspPin $LspSourceDir
# Pure v1.0.0 runtime deps only — NO better-sqlite3/bindings/file-uri-to-path (those backed the retired
# CodeGraph overlay). With better-sqlite3 absent the #42 ABI check below self-skips ("module not deployed").
# iconv-lite (+ its dep safer-buffer) is NEW at v1.0.0: UnicodeDiagnostics requires it in the EAGER
# startup graph — without it the server dies at startup with MODULE_NOT_FOUND (GitHub #77 re-pin).
$LspNodeModules = @(
    "vscode-jsonrpc"
    "vscode-languageserver"
    "vscode-languageserver-protocol"
    "vscode-languageserver-textdocument"
    "vscode-languageserver-types"
    "xml2js"
    "sax"
    "xmlbuilder"
    "iconv-lite"
    "safer-buffer"
)

# SQLite DLLs with FTS5 support (from lib/sqlite-fts5 in project)
# NOTE: Deployed AFTER indexer items to ensure ClarionAssistant's version wins
$SqliteFts5Dir = Join-Path $ProjectDir "lib\sqlite-fts5"

# Versions whose build failed this run — excluded from the deploy loop below and reported at the end,
# so "built but NOT deployed" is never silently indistinguishable from "deployed".
$FailedBuilds = @()

# Roots whose COPY was incomplete. Tracked separately from $FailedBuilds because the two failures
# are not the same shape: a failed build ships nothing, while a failed copy leaves a HALF-WRITTEN
# live install - some files new, some stale - which is the worse of the two and used to exit 0.
$PartialRoots = @()

# --- BOM guard (ticket 9b9dbc7d) ---
# Runs BEFORE the build, so a failure costs seconds instead of four MSBuild passes, and runs even
# under -NoBuild, because shipping pre-built binaries from source that can emit a BOM is still
# shipping the bug.
#
# WHY A DEPLOY GATE AND NOT A HABIT. 14 call sites now depend on writing through
# EncodingHelper.Utf8NoBom rather than System.Text.Encoding.UTF8. The wrong spelling is SHORTER and
# reads like a clarification, and its damage is invisible from inside .NET - File.ReadAllText strips
# the BOM on the way back in, so our own round-trips keep working while node's JSON.parse and the
# Clarion compiler choke. That combination shipped 9b9dbc7d and the status line never worked for
# anyone, for the entire life of the feature. Nothing else in this repo would catch a regression.
#
# -SelfTest RUNS FIRST, and that ordering is the point. It asserts the scanner still discriminates
# against ~43 known shapes. A scanner that has gone blind reports PASS forever, which is precisely
# the failure mode this guard exists to prevent - so "the guard passed" is only evidence once the
# guard has been shown it can still fail. Costs about a second.
#
# The deeper check - that the FIXTURE SET would notice a broken scanner - is
# Check-BomFreeWrites.Mutations.ps1. It is deliberately NOT wired in here: it only needs re-running
# when the scanner or its fixtures change, not on every deploy.
if (-not $SkipBomGuard) {
    $BomGuard = Join-Path $ProjectDir "Check-BomFreeWrites.ps1"
    if (-not (Test-Path $BomGuard)) {
        Write-Host ""
        Write-Host "BOM guard NOT FOUND: $BomGuard" -ForegroundColor Red
        Write-Host "  Refusing to deploy. A guard that has been deleted or renamed is not a guard that passed." -ForegroundColor Yellow
        exit 1
    }

    Write-Host ""
    Write-Host "Checking BOM-free writes..." -ForegroundColor Cyan

    # Out-Host, not bare invocation: an uncaptured & writes objects into this script's own output
    # stream, and this repo has shipped a release where that turned a caller's variable into an
    # array. $LASTEXITCODE still carries the child's exit code (verified).
    & $BomGuard -SelfTest | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BOM guard SELF-TEST failed - the scanner itself is broken, so its verdict means nothing." -ForegroundColor Red
        Write-Host "  Fix Check-BomFreeWrites.ps1 before deploying. Override with -SkipBomGuard only if you" -ForegroundColor Yellow
        Write-Host "  have decided, deliberately, to ship without this check." -ForegroundColor Yellow
        exit 1
    }

    & $BomGuard | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BOM guard FAILED - a file we write can be given a byte-order mark. NOT deploying." -ForegroundColor Red
        Write-Host "  This will not show up in any .NET test. See the guard output above and ticket 9b9dbc7d." -ForegroundColor Yellow
        exit 1
    }
}
else {
    Write-Host ""
    Write-Host "!! BOM GUARD SKIPPED (-SkipBomGuard) - deploying WITHOUT the BOM check !!" -ForegroundColor Red
    Write-Host "   A BOM in a file read by node or the Clarion compiler fails SILENTLY. See 9b9dbc7d." -ForegroundColor Yellow
}

# --- Target must be idle (ticket 8aa391ba) ---
# Deploying over a live install produces a PARTIAL copy, and a partial copy is the dangerous
# outcome, not merely an annoying one: ClarionAssistant.dll is the first item copied and the least
# likely to be locked, so the one file anybody checks to confirm a deploy is exactly the file that
# lies. It happened twice, sessions apart, and cost a diagnosis both times.
#
# Checked BEFORE the build so a refusal costs seconds, not four MSBuild passes. -Kill is handled
# here too (moved up from just-before-the-deploy-loop) so "make the target idle" is one step rather
# than two, and so the build does not run against a target we are about to refuse.
function Get-DeployBlockers {
    <#
      Anything holding files inside an addin folder WE ARE ABOUT TO WRITE. Scoped to $Roots on
      purpose: a Clarion 12 IDE does not lock Clarion 11.1's addin folder, and a gate that refuses
      safe work teaches people to pass -AllowRunning reflexively, which is the same as having no
      gate. Only report what would actually collide.

      Two categories, because a name list alone misses the ones that matter:
        - Clarion.exe: lives in <root>\bin\ but LOADS the addin, locking ClarionAssistant.dll.
        - ANY process whose own image sits under <root>\accessory\addins\ClarionAssistant\ - that
          is clarion-mcp-server.exe, clarion-indexer.exe and the bundled lsp-server\node.exe. These
          run with Clarion CLOSED (an external MCP client starts one), which is precisely how a
          deploy with the IDE shut down still comes out half-written. That happened today.

      Path access throws for processes we cannot open; those are skipped rather than guessed at.
      A process we cannot see is a gap in this check, not a clean result - which is why the copy
      loop still reports FAIL per file and the run still exits non-zero on a partial.
    #>
    param([string[]] $Roots)

    $blockers = @()
    if (-not $Roots -or $Roots.Count -eq 0) { return $blockers }

    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { continue }
        if (-not $path) { continue }
        foreach ($r in $Roots) {
            $rr = $r.TrimEnd('\')
            if ($path -like "$rr\accessory\addins\ClarionAssistant\*") {
                $blockers += [PSCustomObject]@{ Name = $p.ProcessName; Id = $p.Id; Why = $path }
                break
            }
            if ($p.ProcessName -eq "Clarion" -and $path -like "$rr\*") {
                $blockers += [PSCustomObject]@{ Name = $p.ProcessName; Id = $p.Id; Why = "$path (loads the addin)" }
                break
            }
        }
    }
    return $blockers
}

if ($Kill) {
    $proc = Get-Process -Name "Clarion" -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Host ""
        Write-Host "Stopping Clarion IDE..." -ForegroundColor Yellow
        $proc | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
}

$TargetRoots = @()
foreach ($ver in $TargetVersions) { if ($ResolvedRoots.ContainsKey($ver)) { $TargetRoots += $ResolvedRoots[$ver] } }
$Blockers = @(Get-DeployBlockers -Roots $TargetRoots)
if ($Blockers.Count -gt 0) {
    Write-Host ""
    if ($AllowRunning) {
        Write-Host "!! DEPLOYING OVER A LIVE INSTALL (-AllowRunning) - expect a PARTIAL copy !!" -ForegroundColor Red
        foreach ($b in $Blockers) { Write-Host "   holding files: $($b.Name) (pid $($b.Id)) - $($b.Why)" -ForegroundColor Yellow }
        Write-Host "   Locked files will be reported FAIL and this run will exit non-zero." -ForegroundColor Yellow
    }
    else {
        Write-Host "REFUSING TO DEPLOY - something is holding the target files:" -ForegroundColor Red
        foreach ($b in $Blockers) { Write-Host "   $($b.Name) (pid $($b.Id)) - $($b.Why)" -ForegroundColor Red }
        Write-Host ""
        Write-Host "  Close Clarion by hand and stop the helper processes, then re-run:" -ForegroundColor Yellow
        Write-Host "      Get-Process clarion-mcp-server,clarion-indexer -ErrorAction SilentlyContinue | Stop-Process -Force" -ForegroundColor Cyan
        Write-Host "  Prefer that over -Kill: per gotcha_deploy_kill_wedges_clarion, killing the IDE can" -ForegroundColor Yellow
        Write-Host "  wedge it. -AllowRunning overrides this check if you have decided a partial copy is" -ForegroundColor Yellow
        Write-Host "  acceptable, but a half-written install reads as a code regression later. See 8aa391ba." -ForegroundColor Yellow
        exit 1
    }
}

# --- Build ---
if (-not $NoBuild) {
    Write-Host "Restoring packages..." -ForegroundColor Cyan
    & $MSBuild $ProjectFile /t:Restore /p:Configuration=Debug /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "Restore failed." -ForegroundColor Red; exit 1 }

    # A build failure for ONE Clarion release must not block shipping to the others. This loop used to
    # `exit 1` on the first failure — and since the deploy loop runs AFTER every build, a single broken
    # target meant NOTHING was deployed at all, while the console showed the other three building fine.
    # The failure line named only the broken version, so it read as "three of four went out" when the
    # true answer was zero, and the symptom (a stale deployed DLL) is easy to mistake for a code
    # regression. Collect failures instead, deploy every version that DID build, and report the split
    # explicitly at the end.
    foreach ($ver in $TargetVersions) {
        Write-Host ""
        if (-not $ResolvedRoots.ContainsKey($ver)) {
            Write-Host "SKIP  build for Clarion $ver (no install found)" -ForegroundColor DarkGray
            continue
        }
        # Build binds against the FIRST root's DLLs (registry-preferred); additional roots
        # of the same version receive the same build (proven pattern — the strong-name
        # version lock is handled by the AssemblyResolve shim).
        Write-Host "Building for Clarion $ver ($($ResolvedRoots[$ver][0]))..." -ForegroundColor Cyan
        & $MSBuild $ProjectFile /p:Configuration=Debug /p:ClarionVersion=$ver /p:ClarionRoot="$($ResolvedRoots[$ver][0])" /v:minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build FAILED for Clarion $ver — it will NOT be deployed." -ForegroundColor Red
            $FailedBuilds += $ver
            continue
        }
        Write-Host "Build succeeded for Clarion $ver." -ForegroundColor Green
    }

    # Every requested target failed → there is nothing to deploy, so stop here rather than walking a
    # deploy loop that would skip every entry and still print "All done."
    if ($FailedBuilds.Count -gt 0 -and $FailedBuilds.Count -eq @($TargetVersions | Where-Object { $ResolvedRoots.ContainsKey($_) }).Count) {
        Write-Host ""
        Write-Host "No version built successfully — nothing deployed." -ForegroundColor Red
        exit 1
    }

    if (Test-Path $IndexerFile) {
        Write-Host ""
        Write-Host "Building indexer..." -ForegroundColor Cyan
        & $MSBuild $IndexerFile /p:Configuration=Debug /v:minimal
        if ($LASTEXITCODE -ne 0) { Write-Host "Indexer build failed." -ForegroundColor Red; exit 1 }
        Write-Host "Indexer build succeeded." -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "Skipping indexer build (project not found: $IndexerFile)" -ForegroundColor Yellow
    }

    # Platform=x86 is REQUIRED here, unlike the indexer: this project references the vendored
    # 32-bit System.Data.SQLite and its x86 native interop.
    $McpServerFile = Join-Path $McpServerDir "ClarionMcpServer.csproj"
    if (Test-Path $McpServerFile) {
        Write-Host ""
        Write-Host "Building standalone MCP server..." -ForegroundColor Cyan
        & $MSBuild $McpServerFile /p:Configuration=Debug /p:Platform=x86 /v:minimal
        if ($LASTEXITCODE -ne 0) { Write-Host "MCP server build failed." -ForegroundColor Red; exit 1 }
        Write-Host "MCP server build succeeded." -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "Skipping MCP server build (project not found: $McpServerFile)" -ForegroundColor Yellow
    }
}

# --- Kill Clarion IDE if requested ---
# (-Kill and the idle check moved UP to just before the build - see "Target must be idle".
#  Doing it there means a refusal costs seconds instead of four MSBuild passes.)

# --- Deploy each version ---
foreach ($ver in $TargetVersions) {
    if (-not $ResolvedRoots.ContainsKey($ver)) {
        Write-Host ""
        Write-Host "=== Skipping Clarion $ver deploy (no install found) ===" -ForegroundColor DarkGray
        continue
    }
    if ($FailedBuilds -contains $ver) {
        Write-Host ""
        Write-Host "=== Skipping Clarion $ver deploy (its build FAILED — see above) ===" -ForegroundColor Red
        continue
    }
    $cfg         = $Versions[$ver]
    $BuildOutput = Resolve-BuildOutputDir -ProjectDir $ProjectDir -PreferredOutput $cfg.Output
    # ALL live installs of this version — not just the registry pick (2026-08-13 incident).
    $Roots       = $ResolvedRoots[$ver]

    # Same no-guessing principle as Resolve-BuildOutputDir: a config that was never built
    # (-NoBuild, or a fresh checkout) must be a clean skip — otherwise the item loop below
    # creates the live addin folder and fills it with indexer/LSP/SQLite but NO addin DLL.
    if (-not (Test-Path $BuildOutput)) {
        Write-Host ""
        Write-Host "=== Skipping Clarion $ver deploy (build output missing: $BuildOutput) ===" -ForegroundColor DarkGray
        continue
    }

    foreach ($root in $Roots) {
        $DeployDir = Join-Path $root "accessory\addins\ClarionAssistant"

        Write-Host ""
        Write-Host "=== Deploying Clarion $ver -> $root ===" -ForegroundColor Magenta
        Write-Host "  From: $BuildOutput" -ForegroundColor DarkGray
        Write-Host "  To:   $DeployDir" -ForegroundColor DarkGray

        if (-not (Test-Path $root)) {
            Write-Host "  SKIP  $root (not found)" -ForegroundColor DarkGray
            continue
        }

        if (-not (Test-Path $DeployDir)) {
            New-Item -Path $DeployDir -ItemType Directory | Out-Null
        }

        $copied = 0
        $failed = 0

        foreach ($item in $Items) {
            $src = Join-Path $BuildOutput $item
            $dst = Join-Path $DeployDir $item

            if (-not (Test-Path $src)) {
                Write-Host "  SKIP  $item (not found in build output)" -ForegroundColor DarkGray
                continue
            }

            try {
                if (Test-Path $src -PathType Container) {
                    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
                    Copy-Item $src $dst -Recurse -Force
                } else {
                    Copy-Item $src $dst -Force
                }
                Write-Host "  OK    $item" -ForegroundColor Green
                $copied++
            }
            catch {
                Write-Host "  FAIL  $item - $($_.Exception.Message)" -ForegroundColor Red
                $failed++
            }
        }

        # --- Deploy the standalone MCP server ---
        # Only the exe and pdb: every DLL it needs is already in this folder from the addin's own
        # deployment, at the same versions, so copying them again would be pure duplication.
        #
        # ITS PRESENCE IS A SWITCH, not just a file. The addin checks for it at startup and, when
        # it is there, stops serving the 59 editor-agnostic tools itself and declares clarion-tools
        # in mcp-config.json instead. Miss this step and the addin silently falls back to serving
        # all 115 - which looks exactly like the split not working.
        $McpServerOutput = Join-Path $McpServerDir "bin\Debug"
        foreach ($item in @("clarion-mcp-server.exe", "clarion-mcp-server.pdb")) {
            $src = Join-Path $McpServerOutput $item
            if (-not (Test-Path $src)) {
                Write-Host "  SKIP  $item (not found in mcp-server output)" -ForegroundColor DarkGray
                continue
            }
            try {
                Copy-Item $src (Join-Path $DeployDir $item) -Force -ErrorAction Stop
                Write-Host "  OK    $item" -ForegroundColor Green
            } catch {
                Write-Host "  FAIL  $item - $($_.Exception.Message)" -ForegroundColor Red
            }
        }

        # --- Deploy indexer ---
        $IndexerItems = @(
            "clarion-indexer.exe"
            "clarion-indexer.pdb"
            "System.Data.SQLite.dll"
            "x86"
        )

        if (Test-Path $IndexerOutput) {
            foreach ($item in $IndexerItems) {
                $src = "$IndexerOutput\$item"
                $dst = Join-Path $DeployDir $item

                if (-not (Test-Path $src)) {
                    Write-Host "  SKIP  $item (not found in indexer output)" -ForegroundColor DarkGray
                    continue
                }

                try {
                    if (Test-Path $src -PathType Container) {
                        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
                        Copy-Item $src $dst -Recurse -Force
                    } else {
                        Copy-Item $src $dst -Force
                    }
                    Write-Host "  OK    $item (indexer)" -ForegroundColor Green
                    $copied++
                }
                catch {
                    Write-Host "  FAIL  $item - $($_.Exception.Message)" -ForegroundColor Red
                    $failed++
                }
            }
        } else {
            Write-Host "  SKIP  indexer output (not found: $IndexerOutput)" -ForegroundColor DarkGray
        }

        # --- Deploy SQLite FTS5 DLLs (after indexer, so correct version wins) ---
        $SqliteItems = @{
            "System.Data.SQLite.dll" = Join-Path $SqliteFts5Dir "System.Data.SQLite.dll"
            "SQLite.Interop.dll"     = Join-Path $SqliteFts5Dir "SQLite.Interop.dll"
        }
        foreach ($name in $SqliteItems.Keys) {
            $src = $SqliteItems[$name]
            if (Test-Path $src) {
                try {
                    Copy-Item $src (Join-Path $DeployDir $name) -Force
                    if ($name -eq "SQLite.Interop.dll") {
                        $x86Dir = Join-Path $DeployDir "x86"
                        if (-not (Test-Path $x86Dir)) { New-Item $x86Dir -ItemType Directory | Out-Null }
                        Copy-Item $src (Join-Path $x86Dir $name) -Force
                    }
                    Write-Host "  OK    $name (FTS5)" -ForegroundColor Green
                    $copied++
                } catch {
                    Write-Host "  FAIL  $name - $($_.Exception.Message)" -ForegroundColor Red
                    $failed++
                }
            } else {
                Write-Host "  SKIP  $name (not found in lib/sqlite-fts5)" -ForegroundColor DarkGray
            }
        }

        # --- Deploy LSP Server ---
        $LspDestDir = Join-Path $DeployDir "lsp-server"

        if ($LspPinOK -and (Test-Path $LspSourceDir)) {
            # Copy compiled server JS + common shared code
            foreach ($outDir in @("out\server", "out\common")) {
                $LspOutSrc = "$LspSourceDir\$outDir"
                if (Test-Path $LspOutSrc) {
                    $LspOutDst = Join-Path $LspDestDir $outDir
                    # DELETE-THEN-COPY. If the copy fails after the delete, the destination is not
                    # stale - it is GONE, which is worse. The catch says so explicitly rather than
                    # letting "FAIL" imply the old files survived. (Making this atomic - stage to a
                    # sibling and swap - is the real fix and is filed on 8aa391ba, not done here.)
                    try {
                        if (Test-Path $LspOutDst) { Remove-Item $LspOutDst -Recurse -Force }
                        New-Item -Path $LspOutDst -ItemType Directory -Force | Out-Null
                        Copy-Item "$LspOutSrc\*" $LspOutDst -Recurse -Force
                        Write-Host "  OK    lsp-server\$outDir" -ForegroundColor Green
                        $copied++
                    } catch {
                        Write-Host "  FAIL  lsp-server\$outDir - $($_.Exception.Message)" -ForegroundColor Red
                        Write-Host "        ^ this directory was CLEARED before the copy - it may now be EMPTY, not stale." -ForegroundColor Red
                        $failed++
                    }
                }
            }

            if (-not (Test-Path "$LspSourceDir\out\server")) {
                Write-Host "  SKIP  lsp-server (ClarionLSP build output not found)" -ForegroundColor DarkGray
            }

            # Copy bundled node.exe (so end users don't need Node.js installed).
            # Resolve portably (GitHub #30): explicit $env:CLARIONLSP_NODE, else node on PATH,
            # else the legacy default install location.
            $NodeExeSrc =
                if ($env:CLARIONLSP_NODE) { $env:CLARIONLSP_NODE }
                elseif (Get-Command node -ErrorAction SilentlyContinue) { (Get-Command node).Source }
                else { "C:\Program Files\nodejs\node.exe" }
            if (Test-Path $NodeExeSrc) {
                # $LspDestDir is only created by the server-output copy above; when that was SKIPped
                # (no LSP build output) a fresh target has no lsp-server dir and this copy would die.
                # This copy used to be UNGUARDED while its four neighbours all caught, counted and
                # continued. With $ErrorActionPreference='Stop' a lock on node.exe therefore threw
                # and killed the whole remaining run - so under -Version all, every LATER Clarion
                # root was left untouched while earlier ones sat half-written. Measured 2026-09-10.
                try {
                    New-Item -Path $LspDestDir -ItemType Directory -Force | Out-Null
                    Copy-Item $NodeExeSrc (Join-Path $LspDestDir "node.exe") -Force
                    Write-Host "  OK    lsp-server\node.exe" -ForegroundColor Green
                    $copied++
                } catch {
                    Write-Host "  FAIL  lsp-server\node.exe - $($_.Exception.Message)" -ForegroundColor Red
                    $failed++
                }
            } else {
                Write-Host "  SKIP  node.exe (not found at $NodeExeSrc)" -ForegroundColor DarkGray
            }

            # Copy required node_modules
            foreach ($mod in $LspNodeModules) {
                $modSrc = "$LspSourceDir\node_modules\$mod"
                $modDst = Join-Path $LspDestDir "node_modules\$mod"
                if (Test-Path $modSrc) {
                    # Delete-then-copy again: on failure this module is missing, not stale.
                    try {
                        if (Test-Path $modDst) { Remove-Item $modDst -Recurse -Force }
                        Copy-Item $modSrc $modDst -Recurse -Force
                        Write-Host "  OK    lsp-server\node_modules\$mod" -ForegroundColor Green
                        $copied++
                    } catch {
                        Write-Host "  FAIL  lsp-server\node_modules\$mod - $($_.Exception.Message)" -ForegroundColor Red
                        Write-Host "        ^ this module was CLEARED before the copy - it may now be MISSING." -ForegroundColor Red
                        $failed++
                    }
                }
            }

            # #40 pure: purge RETIRED CodeGraph-overlay modules that a prior (codegraph) deploy may have
            # left in the dest — the node_modules dir isn't wiped wholesale, so stale better-sqlite3 etc.
            # would otherwise linger (bloat + a misleading "codegraph present" signal in the shipped addin).
            foreach ($stale in @("better-sqlite3", "bindings", "file-uri-to-path")) {
                $staleDst = Join-Path $LspDestDir "node_modules\$stale"
                if (Test-Path $staleDst) {
                    Remove-Item $staleDst -Recurse -Force
                    Write-Host "  OK    lsp-server purge stale $stale (retired codegraph dep)" -ForegroundColor DarkYellow
                }
            }

            # --- ABI assertion: bundled better-sqlite3 must load under bundled node.exe (GitHub #42) ---
            # The prebuilt better-sqlite3 .node addon is compiled for a specific Node ABI/arch. If the
            # bundled node.exe drifts from the build machine's Node, the LSP's CodeGraphBridge silently
            # self-disables ("better-sqlite3 not available") on end-user installs. Assert the EXACT
            # end-user path here: the just-deployed node.exe loading better-sqlite3 by relative require.
            $DeployedNode = Join-Path $LspDestDir "node.exe"
            $DeployedBsq3 = Join-Path $LspDestDir "node_modules\better-sqlite3"
            if ((Test-Path $DeployedNode) -and (Test-Path $DeployedBsq3)) {
                $abiProbe = "var D=require('better-sqlite3');var db=new D(':memory:');db.prepare('select 1 as x').get();db.close();process.stdout.write('OK');"
                Push-Location $LspDestDir
                try {
                    $abiOut  = & $DeployedNode -e $abiProbe 2>&1
                    $abiExit = $LASTEXITCODE
                } finally {
                    Pop-Location
                }
                if ($abiExit -eq 0) {
                    Write-Host "  OK    lsp-server better-sqlite3 ABI (loads under bundled node.exe)" -ForegroundColor Green
                    $copied++
                } else {
                    Write-Host "  FAIL  better-sqlite3 ABI mismatch (GitHub #42): bundled node.exe cannot load the prebuilt addon." -ForegroundColor Red
                    Write-Host "        CodeGraphBridge would self-disable on end-user installs. Rebuild better-sqlite3 against the bundled node's ABI/arch." -ForegroundColor Red
                    Write-Host "        node: $DeployedNode" -ForegroundColor DarkGray
                    Write-Host "        $abiOut" -ForegroundColor DarkGray
                    $failed++
                }
            } elseif (Test-Path $DeployedNode) {
                Write-Host "  SKIP  better-sqlite3 ABI check (module not deployed)" -ForegroundColor DarkGray
            }
        } elseif (-not $LspPinOK) {
            # Deliberate: shipping a server that does not match the pin is worse than shipping none,
            # because lsp-snapshot.json would go on asserting a commit the consumer did not receive.
            # The version keeps whatever lsp-server it already had; the final summary exits non-zero.
            Write-Host "  SKIP  lsp-server (does not match the pin — see FAIL above)" -ForegroundColor Yellow
        } else {
            Write-Host "  SKIP  lsp-server (ClarionLSP not found)" -ForegroundColor DarkGray
        }

        # --- Post-copy verification of the one file that lies ---
        # ClarionAssistant.dll is the first item copied and the least likely to be locked, so it is
        # the file everybody checks to confirm a deploy - and on both recorded partial deploys it
        # copied FINE while later files did not. Hash it against the build rather than trusting the
        # copy's return: this is the check that actually proved an install undamaged on 2026-09-10.
        $DeployedDll = Join-Path $DeployDir "ClarionAssistant.dll"
        $BuiltDll    = Join-Path $BuildOutput "ClarionAssistant.dll"
        if ((Test-Path $DeployedDll) -and (Test-Path $BuiltDll)) {
            try {
                if ((Get-FileHash $DeployedDll -Algorithm SHA256).Hash -ne (Get-FileHash $BuiltDll -Algorithm SHA256).Hash) {
                    Write-Host "  FAIL  ClarionAssistant.dll does NOT match the build it came from." -ForegroundColor Red
                    Write-Host "        deployed: $DeployedDll" -ForegroundColor Red
                    Write-Host "        built:    $BuiltDll" -ForegroundColor Red
                    $failed++
                }
            } catch {
                Write-Host "  FAIL  could not verify ClarionAssistant.dll - $($_.Exception.Message)" -ForegroundColor Red
                $failed++
            }
        }
        elseif (-not (Test-Path $DeployedDll)) {
            Write-Host "  FAIL  ClarionAssistant.dll is MISSING from the deployed folder." -ForegroundColor Red
            $failed++
        }

        # --- Version summary ---
        if ($failed -eq 0) {
            Write-Host "  $root deploy complete: $copied items." -ForegroundColor Green
        } else {
            Write-Host "  $root deploy: $copied copied, $failed FAILED - this install is PARTIAL." -ForegroundColor Red
            $PartialRoots += $root
        }
    }
}

# --- Final summary ---
# The rule stated here for BUILD failures applied to builds only, and copy failures - the ones that
# actually leave a stale install - were exempt from it: a run could print "42 copied, 8 failed" in
# yellow and then "All done." in green and exit 0. Both halves are covered now (8aa391ba).
Write-Host ""
if ($FailedBuilds.Count -gt 0 -or $PartialRoots.Count -gt 0 -or -not $LspPinOK) {
    # Never let a partial run exit 0 with a bare "All done." — that is what made a stale deployed DLL
    # look like a successful deploy. Name what did NOT ship, and fail the exit code so a caller
    # (CI, the installer, another script) can't read this run as clean.
    Write-Host "Done, WITH FAILURES." -ForegroundColor Yellow
    if ($FailedBuilds.Count -gt 0) {
        Write-Host "  NOT deployed (build failed): $($FailedBuilds -join ', ')" -ForegroundColor Red
    }
    if ($PartialRoots.Count -gt 0) {
        Write-Host "  PARTIALLY deployed - these installs are half-written and must NOT be trusted:" -ForegroundColor Red
        foreach ($r in $PartialRoots) { Write-Host "      $r" -ForegroundColor Red }
        Write-Host "  A partial install is worse than an untouched one: some files are new, some stale," -ForegroundColor Yellow
        Write-Host "  and the symptom later reads as a code regression. Close everything holding the" -ForegroundColor Yellow
        Write-Host "  target files and re-run before using these." -ForegroundColor Yellow
    }
    if (-not $LspPinOK) {
        Write-Host "  NOT deployed (pin mismatch): lsp-server — every version kept the LSP it already had." -ForegroundColor Red
        Write-Host "  Re-run lsp-server-sync\Sync-LspServer.ps1 -Pure, or bump the pin, then re-run this script." -ForegroundColor Red
    }
    if ($PartialRoots.Count -eq 0) {
        Write-Host "  Everything else deployed normally." -ForegroundColor Yellow
    }
    exit 1
}
Write-Host "All done." -ForegroundColor Green
