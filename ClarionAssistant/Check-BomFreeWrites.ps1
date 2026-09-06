# Check-BomFreeWrites.ps1
#
# Guards the files we write that something OTHER THAN .NET reads. Each must be written with
# EncodingHelper.Utf8NoBom, never System.Text.Encoding.UTF8 - which emits a byte-order mark.
#
# WHY A GUARD AND NOT A COMMENT. Each of these call sites already carries a comment explaining
# itself. Comments do not survive someone confidently tidying `Services.EncodingHelper.Utf8NoBom`
# back to the shorter, more familiar-looking `Encoding.UTF8`, because that edit LOOKS like a
# cleanup. And the damage is invisible from inside this codebase: File.ReadAllText strips a BOM, so
# every round-trip on our side keeps working while a third-party parser chokes. That combination -
# a tempting wrong edit plus a silent failure - is exactly how ticket 9b9dbc7d shipped, and it went
# unnoticed for the entire life of the status-line feature.
#
# The VS Code repo already had this covered for its one JSON write
# (src/core/mcpConfig.test.ts: assert.notEqual(text.charCodeAt(0), 0xfeff)). Same hazard, both
# sides; only one had a guard. This is the other half.
#
# WHO STRIPS A BOM AND WHO DOES NOT, measured 2026-09-06:
#     File.ReadAllText / ReadAllLines .... strips  -> our own state files are unaffected
#     TextDecoder / fetch().text() ....... strips  -> WebView reads (source.txt) are unaffected
#     node readFileSync + JSON.parse ..... DOES NOT
#     the Clarion compiler ............... does not
# Only the last two matter, which is why this list is short and curated rather than "every write".
# Adding a file here is a claim that a non-.NET reader opens it. Do not add one without checking.
#
# Exit 0 = all guarded writes are BOM-free. Exit 1 = at least one regressed.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# file (relative to this script) ; the write we are guarding ; who reads it and why it matters
$guarded = @(
    @{ File = 'AssistantChatControl.cs'
       Marker = 'settingsPath, json'
       Reader = 'Claude Code / Copilot, node JSON.parse - PROVEN to reject a BOM' }
    @{ File = 'AssistantChatControl.cs'
       Marker = 'promptFile, systemPromptExtra'
       Reader = 'node, via --append-system-prompt-file' }
    @{ File = 'AssistantChatControl.cs'
       Marker = 'initialPromptFile, initialPrompt'
       Reader = 'node' }
    @{ File = 'Services\StructureDesignerService.cs'
       Marker = 'clwPath, normalized'
       Reader = 'the Clarion compiler - a BOM in a .clw is a known breaker' }
)

$failures = @()
$checked  = 0

foreach ($g in $guarded) {
    $path = Join-Path $root $g.File
    if (-not (Test-Path $path)) {
        $failures += "MISSING FILE  $($g.File)"
        continue
    }

    # -SimpleMatch takes the marker VERBATIM. Do NOT wrap it in [regex]::Escape: that escapes
    # spaces to "\ ", and SimpleMatch then hunts for a literal backslash that is not there, so
    # every marker reports GONE. Caught by breaking this script on its first run.
    $line = Select-String -Path $path -Pattern $g.Marker -SimpleMatch |
            Where-Object { $_.Line -match 'WriteAllText' } |
            Select-Object -First 1

    if (-not $line) {
        # The write moved or was renamed. That is a real failure: this guard is now blind, and a
        # blind guard is worse than none because it keeps reporting PASS.
        $failures += "MARKER GONE   $($g.File): no WriteAllText matching '$($g.Marker)' - the guard can no longer see this write"
        continue
    }

    $checked++
    if ($line.Line -notmatch 'Utf8NoBom') {
        $failures += "BOM REGRESSED $($g.File):$($line.LineNumber)`n                $($line.Line.Trim())`n                read by: $($g.Reader)"
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "PASS - $checked guarded writes all use EncodingHelper.Utf8NoBom." -ForegroundColor Green
    exit 0
}

Write-Host "FAIL - a file read by a non-.NET parser is being written with a BOM." -ForegroundColor Red
Write-Host ""
foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
Write-Host ""
Write-Host "  Encoding.UTF8 EMITS a BOM. Use Services.EncodingHelper.Utf8NoBom." -ForegroundColor Yellow
Write-Host "  This will not show up in any .NET test: File.ReadAllText strips the BOM on the way" -ForegroundColor Yellow
Write-Host "  back in, so the round-trip looks fine while the other reader fails. See 9b9dbc7d." -ForegroundColor Yellow
exit 1
