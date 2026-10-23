# IndexQuietCheck.SourceScan.ps1 - GH #279.
#
# Work With Solution (and the IDE opening a solution) starts an AUTOMATIC incremental index. On an up-to-date
# solution it finds nothing to do, but the header said "Updating..." with a spinner every time, so it looked like a
# rebuild on every open. This scan pins the fix in AssistantChatControl.RunIndex:
#
#   Q1  the progress window is built ONLY by the lazy factory (ensureProgressForm), never unconditionally.
#   Q2  the factory runs only for a run that shows its window (manual / MCP) or inside the FAILURE branch, so an
#       automatic run that succeeds (an up-to-date check above all) never creates or shows IndexProgressForm.
#   Q3  a quiet check (automatic + incremental) starts as "Checking for changes..." with no spinner; the
#       spinner appears only once the run is known to be real work (showRealWork).
#   Q4  RunIndexAutomatic passes showProgressWindow = false; manual RunIndex(bool) passes true.
#   Q5  the finished run's header line tells an up-to-date check from an update and from an upgrade to full,
#       and keeps it for that solution; a failed or cancelled run clears it.
#   Q6  every use of progressForm outside the factory tolerates null (an automatic run has none).
#
# integ-1023 review (D6/D7; Q7-Q13 fail on 3e57b84):
#   Q7  D6: real work is known from the indexer's mode (a full run of an incremental request) or "N of M projects
#       have changes" with N > 0, not only from the first structured event at Pass 2.
#   Q8  D6: OutOfProcessIndexer reports the mode from the child AND from the in-process fallback.
#   Q9  D7a: a failed or cancelled run's line is the header's until the next run (not painted over at once).
#   Q10 D7b: the spinner is painted only while the header shows the run's own solution.
#   Q11 D7c: a refused run (any kind) restores the header.
#   Q12 D7d: the remembered line carries the database's stamp and is dropped once the database changed.
#   Q13 D7e: an automatic check for another solution during a run is queued and run when the run ends.
#
# -ChatControl / -Oop let the scan run against other copies of AssistantChatControl.cs / OutOfProcessIndexer.cs
# (old-code and mutation proofs).
# Exit: 0 pass, 1 fail.

param([string]$ChatControl, [string]$Oop)

$ErrorActionPreference = 'Stop'
$RepoDir = Split-Path -Parent $PSScriptRoot          # ...\ClarionAssistant
if (-not $ChatControl) { $ChatControl = Join-Path $RepoDir 'AssistantChatControl.cs' }
$failures = @()
$n = 0

function Check([bool]$ok, [string]$id, [string]$msg) {
    $script:n++
    if ($ok) { Write-Host "  ok   $id  $msg" }
    else { Write-Host "  FAIL $id  $msg" -ForegroundColor Red; $script:failures += "${id}: $msg" }
}

function Strip-Comments([string]$src) {
    $noBlock = [regex]::Replace($src, '/\*.*?\*/', '', 'Singleline')
    return [regex]::Replace($noBlock, '//[^\r\n]*', '')
}

# The text from $open (an opening brace's index) to its matching close brace, inclusive.
function Block([string]$code, [int]$open) {
    if ($open -lt 0 -or $open -ge $code.Length -or $code[$open] -ne '{') { return '' }
    $depth = 0
    for ($i = $open; $i -lt $code.Length; $i++) {
        if ($code[$i] -eq '{') { $depth++ }
        elseif ($code[$i] -eq '}') { $depth--; if ($depth -eq 0) { return $code.Substring($open, $i - $open + 1) } }
    }
    return ''
}

$chat = Strip-Comments ([IO.File]::ReadAllText($ChatControl))

# RunIndex's full body: the overload that takes showProgressWindow.
$sigM = [regex]::Match($chat, 'public void RunIndex\(bool incremental,\s*bool showProgressWindow,')
$sigAt = if ($sigM.Success) { $sigM.Index } else { -1 }
$runIndex = if ($sigAt -ge 0) { Block $chat ($chat.IndexOf('{', $sigAt)) } else { '' }
Check ($runIndex.Length -gt 5000) 'Q0' "found RunIndex(incremental, showProgressWindow, ...) ($($runIndex.Length) chars)"

# ---- Q1: one construction, inside the lazy factory ----
$news = [regex]::Matches($runIndex, 'new (Dialogs\.)?IndexProgressForm\(')
$facAt = $runIndex.IndexOf('ensureProgressForm = () =>')
$factory = if ($facAt -ge 0) { Block $runIndex ($runIndex.IndexOf('{', $facAt)) } else { '' }
$inFactory = $news.Count -eq 1 -and $factory -ne '' -and $news[0].Index -gt $facAt -and $news[0].Index -lt ($facAt + 40 + $factory.Length)
Check $inFactory 'Q1' ("IndexProgressForm is constructed exactly once in RunIndex, inside the lazy ensureProgressForm factory (found $($news.Count) construction(s)" + $(if ($facAt -lt 0) { ', no factory' } else { '' }) + ')')

# ---- Q2: who calls the factory ----
$calls = [regex]::Matches($runIndex, 'ensureProgressForm\(\)')
$errAt = $runIndex.IndexOf('if (e.Error != null)')
$errBlock = if ($errAt -ge 0) { Block $runIndex ($runIndex.IndexOf('{', $errAt)) } else { '' }
$errStart = if ($errAt -ge 0) { $runIndex.IndexOf('{', $errAt) } else { -1 }
$badCalls = @()
foreach ($c in $calls) {
    $lineStart = $runIndex.LastIndexOf("`n", $c.Index) + 1
    $line = $runIndex.Substring($lineStart, $c.Index - $lineStart)
    $manual = $line -match 'if \(showProgressWindow\) ShowIndexProgress\($'
    $inErr = $errStart -ge 0 -and $c.Index -gt $errStart -and $c.Index -lt ($errStart + $errBlock.Length)
    if (-not ($manual -or $inErr)) { $badCalls += $line.Trim() }
}
Check ($calls.Count -ge 2 -and $badCalls.Count -eq 0) 'Q2a' ("the factory is called only as 'if (showProgressWindow) ShowIndexProgress(ensureProgressForm())' and inside the failure branch" + $(if ($badCalls.Count) { ': ' + ($badCalls -join ' | ') } else { " ($($calls.Count) calls)" }))
Check ($errBlock -match '(?s)if \(!showProgressWindow && progressForm == null\)\s*\{\s*try \{ ensureProgressForm\(\); \}') 'Q2b' 'a FAILED automatic run still builds its window (7f1c67b2: failures are never silent)'
$shows = [regex]::Matches($runIndex, 'ShowIndexProgress\(')
$badShows = @($shows | Where-Object {
    $ls = $runIndex.LastIndexOf("`n", $_.Index) + 1
    $ln = $runIndex.Substring($ls, $_.Index - $ls)
    -not ($ln -match 'if \(showProgressWindow\) $' -or ($errStart -ge 0 -and $_.Index -gt $errStart -and $_.Index -lt ($errStart + $errBlock.Length)))
})
Check ($shows.Count -ge 2 -and $badShows.Count -eq 0) 'Q2c' 'the window is shown only for a run that asked for it, or for a failure'
$deferAt = $runIndex.IndexOf('var deferredEvents = showProgressWindow ? null : new List<')
Check ($deferAt -ge 0 -and $factory -match 'foreach \(var dev in deferredEvents\)' -and $runIndex -match 'deferredEvents\[last\] = ev;') 'Q2d' "an automatic run keeps its progress events (coalesced per phase and project) and replays them into a failure window"

# ---- Q3: header ----
$quietDef = if ($chat -match '(?s)internal static bool IsQuietIndexCheck\(bool showsWindow, bool incremental\)\s*\{\s*return (.*?);\s*\}') { $Matches[1] } else { '' }
Check ($quietDef -eq '!showsWindow && incremental') 'Q3a' "IsQuietIndexCheck = automatic AND incremental (got '$quietDef')"
Check ($runIndex -match '(?s)bool quietCheck = IsQuietIndexCheck\(showProgressWindow, incremental\);.*?if \(quietCheck\)\s*_header\.SetIndexStatus\(Services\.IndexHeaderText\.Checking, ""\);\s*else\s*_header\.SetIndexStatus\(Services\.IndexHeaderText\.WorkText\(incremental, null\), "active"\);') 'Q3b' 'a quiet check starts as "Checking for changes..." with no spinner; other runs show their spinner at once'
$srwAt = $runIndex.IndexOf('Action showRealWork = () =>')
$showRealWork = if ($srwAt -ge 0) { Block $runIndex ($runIndex.IndexOf('{', $srwAt)) } else { '' }
$upfront = $runIndex.Substring(0, [Math]::Max(0, $runIndex.IndexOf('var worker = new BackgroundWorker()')))
if ($showRealWork) { $upfront = $upfront.Replace($showRealWork, '') }
Check (([regex]::Matches($upfront, 'SetIndexStatus\(')).Count -eq 2) 'Q3c' 'nothing else sets the header status before the worker starts (showRealWork aside)'
$pc = if ($runIndex.IndexOf('worker.ProgressChanged +=') -ge 0) { Block $runIndex ($runIndex.IndexOf('{', $runIndex.IndexOf('worker.ProgressChanged +='))) } else { '' }
Check ($pc -match '(?s)if \(ev != null\)\s*\{\s*if \(quietCheck && !realWorkSeen\) showRealWork\(\);') 'Q3d' 'the first structured progress event still shows the spinner (the indexer sends none for an up-to-date run)'
$cgi = Strip-Comments ([IO.File]::ReadAllText((Join-Path $RepoDir 'CodeGraph\Graph\CodeGraphIndexer.cs')))
$upToDateAt = $cgi.IndexOf('result.UpToDate = true;')
$firstEmit = $cgi.IndexOf('EmitProgress(IndexProgressEvent.')
Check ($upToDateAt -ge 0 -and $firstEmit -gt $upToDateAt) 'Q3e' "the indexer's up-to-date return comes before its first EmitProgress (the signal Q3d relies on)"

# ---- Q4: entry points ----
Check ($chat -match '(?s)public void RunIndexAutomatic\(bool incremental\)\s*\{\s*RunIndex\(incremental, false, null, null\);' -and
       $chat -match '(?s)public void RunIndex\(bool incremental\)\s*\{\s*RunIndex\(incremental, true, null, null\);') 'Q4' 'automatic runs pass showProgressWindow=false; the header Reindex/Update pass true'

# ---- Q5: outcome line ----
$outcome = if ($chat -match '(?s)internal static string IndexOutcomeText\(ClarionCodeGraph\.Graph\.IndexResult result, DateTime when\)\s*(\{.*?\n        \})') { $Matches[1] } else { '' }
Check ($outcome -match 'result\.Incremental && result\.UpToDate' -and $outcome -match '"Up to date \(checked "' -and $outcome -match '"Updated: "' -and
       $outcome -match 'UpgradedFromFeatureLevel > 0' -and $outcome -match '"Rebuilt \(index upgrade\): "' -and $outcome -match '"Indexed: "') 'Q5a' 'IndexOutcomeText says "Up to date", "Updated", "Rebuilt (index upgrade)" or "Indexed"'
Check ($runIndex -match '(?s)string outcome = IndexOutcomeText\(result, DateTime\.Now\);\s*if \(outcome != null\)\s*_indexOutcome\.Record\(slnPath, outcome, "",' -and
       $runIndex -match '(?s)if \(e\.Error != null \|\| wasCancelled\)\s*_indexOutcome\.Clear\(\);') 'Q5b' 'a finished run records its outcome; a failed or cancelled one clears it'
$uis = if ($chat -match '(?s)private void UpdateIndexStatus\(\)\s*(\{.*?\n        \})') { $Matches[1] } else { '' }
Check ($uis -match '(?s)if \(_indexOutcome\.TryGet\(_currentSlnPath, .*?out outcomeText, out outcomeStyle\)\)\s*\{\s*_header\.SetIndexStatus\(outcomeText, outcomeStyle\);') 'Q5c' 'the header keeps that outcome for the solution it ran on'

# ---- Q7-Q13: integ-1023 review D6/D7 ----
if (-not $Oop) { $Oop = Join-Path $RepoDir 'Services\OutOfProcessIndexer.cs' }
$oopSrc = Strip-Comments ([IO.File]::ReadAllText($Oop))
Check ($runIndex -match 'indexer\.OnModeDecided \+= inc => \(\(BackgroundWorker\)s\)\.ReportProgress\(0, new IndexModeNotice\(inc\)\);' -and
       $pc -match '(?s)var mode = e\.UserState as IndexModeNotice;\s*if \(mode != null\)\s*\{\s*effectiveIncremental = mode\.Incremental;\s*if \(incremental && !mode\.Incremental\) showRealWork\(\);\s*return;' -and
       $pc -match 'if \(quietCheck && !realWorkSeen && Services\.IndexHeaderText\.MessageSignalsWork\(msg\)\) showRealWork\(\);') 'Q7' 'D6: the spinner shows on the mode record of a run that goes full, or on "N of M projects have changes" with N > 0'
Check ($showRealWork -match 'Services\.IndexHeaderText\.WorkText\(incremental, effectiveIncremental\)') 'Q7b' 'D6: the spinner says what runs ("Rebuilding index..." for a full run of an incremental request)'
$kindMode = if ($oopSrc -match '(?s)case IndexerProtocol\.KindMode:(.*?)break;') { $Matches[1] } else { '' }
$fallback = if ($oopSrc -match '(?s)private IndexResult RunInProcessFallback\((.*)$') { $Matches[1] } else { '' }
Check ($oopSrc -match 'public event Action<bool> OnModeDecided;' -and $kindMode -match 'OnModeDecided' -and
       $fallback -match '(?s)indexer\.OnProgress \+= msg =>\s*\{.*?if \(!modeSent\).*?OnModeDecided.*?self\.EffectiveIncremental') 'Q8' 'D6: OutOfProcessIndexer raises OnModeDecided for the child (KindMode) and the in-process fallback (before its first message)'
$errB = $errBlock
$cancAt = $runIndex.IndexOf('if (wasCancelled)')
$cancB = if ($cancAt -ge 0) { Block $runIndex ($runIndex.IndexOf('{', $cancAt)) } else { '' }
Check ($errB -match '_indexOutcome\.Record\(slnPath, "Error: "' -and $cancB -match '_indexOutcome\.Record\(slnPath, "Index cancelled", "error"' -and
       $runIndex -notmatch 'SetIndexStatus\("Error: ' -and $runIndex -notmatch 'SetIndexStatus\("Index cancelled"') 'Q9' 'D7a: the error / cancel line is recorded as the header line, so UpdateIndexStatus keeps it until the next run'
Check ($showRealWork -match '(?s)realWorkSeen = true;\s*if \(string\.Equals\(slnPath, _currentSlnPath, StringComparison\.OrdinalIgnoreCase\)\)\s*_header\.SetIndexStatus\(' -and
       $pc -notmatch 'SetIndexStatus\(') 'Q10' "D7b: the spinner is painted only while the header shows the run's own solution"
$gateAt = $runIndex.IndexOf('if (!ClarionAssistant.Services.IndexRunGate.TryEnter(dbPath, out indexHolder))')
$gateB = if ($gateAt -ge 0) { Block $runIndex ($runIndex.IndexOf('{', $gateAt)) } else { '' }
Check ($gateB -match '(?s)\n\s*UpdateIndexStatus\(\);\s*return;\s*\}$' -and $gateB -notmatch 'if \(quietCheck\) UpdateIndexStatus') 'Q11' 'D7c: a refused run of any kind restores the header'
Check (([regex]::Matches($runIndex, '_indexOutcome\.Record\(slnPath, [^;]*Services\.IndexOutcomeMemo\.StampOf\(dbPath\)\);')).Count -eq 3 -and
       $uis -match '_indexOutcome\.TryGet\(_currentSlnPath, Services\.IndexOutcomeMemo\.StampOf\(dbPath\)') 'Q12' "D7d: every remembered line carries the database's stamp, and is shown only while the database is unchanged"
$busyAt = $runIndex.IndexOf('if (_indexRunInProgress)')
$busyB = if ($busyAt -ge 0) { Block $runIndex ($runIndex.IndexOf('{', $busyAt)) } else { '' }
$finAt = $runIndex.IndexOf('_indexRunInProgress = false;')
Check ($busyB -match '(?s)if \(!showProgressWindow\)\s*\{\s*_indexFollowUp\.Queue\(_indexRunSlnPath, _currentSlnPath\);\s*return;' -and
       $runIndex -match '(?s)_indexRunInProgress = true;\s*_indexRunSlnPath = slnPath;' -and
       $runIndex -match '(?s)if \(_indexFollowUp\.Take\(_currentSlnPath\) != null\)\s*\{\s*try \{ BeginInvoke\(new Action\(\(\) => RunIndexAutomatic\(') 'Q13' 'D7e: an automatic check for another solution during a run is queued, and run when the run ends'

# ---- Q6: null-tolerant uses ----
$outside = if ($factory) { $runIndex.Replace($factory, '') } else { $runIndex }
$unguarded = [regex]::Matches($outside, '(?<!progressForm != null && )(?<!else if \()!progressForm\.IsDisposed')
Check ($runIndex -match 'Dialogs\.IndexProgressForm progressForm = null;' -and $unguarded.Count -eq 0) 'Q6' "every IsDisposed test on progressForm outside the factory is null-guarded ($($unguarded.Count) unguarded)"

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS  IndexQuietCheck.SourceScan ($n checks)"; exit 0 }
Write-Host "FAIL  IndexQuietCheck.SourceScan: $($failures.Count) of $n failed" -ForegroundColor Red
exit 1
