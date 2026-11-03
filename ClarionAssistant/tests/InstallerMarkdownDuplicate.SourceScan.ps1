# InstallerMarkdownDuplicate.SourceScan.ps1 - GH #290 / GH #289: the installer's bundled Markdown Editor.
#
# GH #290: DecideInstallMarkdown looked for an existing copy in ONE folder, accessory\addins\MarkdownEditor.
# Clarion Addin Finder and upstream's release notes put the editor in accessory\addins\ClarionMarkdownEditor,
# upstream's addin-config.json names accessory\addins\MarkdownAddin, and a hand-extracted zip can land under
# any name. Two folders declaring the ClarionMarkdownEditor Identity stop the Clarion IDE starting ("Identity
# name used by multiple addins") - and the installer wrote the second one.
#
# M1  the .iss probes all three named folders (MarkdownEditor as the destination, ClarionMarkdownEditor and
#     MarkdownAddin as foreign routes) AND scans every immediate subfolder of accessory\addins with
#     FindFirst/FindNext/FindClose, skipping '.', '..' and our own folder; a folder counts when it holds
#     ClarionMarkdownEditor.addin or any *.addin naming the Identity.
# M2  DecideInstallMarkdown consults that scan BEFORE the "no existing install -> installing" branch, answers
#     False when a foreign copy exists, logs the folder, and records a Finished-page note - in both the
#     foreign-only case and the UPGRADE case (ours still in MarkdownEditor AND theirs elsewhere), whose rule is
#     documented in the comment.
# M3  the verdict stays frozen per Clarion root (an Inno Check on a wildcard [Files] row runs PER FILE, 5.8.1):
#     every {#SrcMarkdown} row goes through ShouldInstallMarkdown, none through DecideInstallMarkdown, and
#     ShouldInstallMarkdown still caches in MarkdownDecided/MarkdownVerdict.
# M4  the note reaches the user: CurPageChanged(wpFinished) appends MarkdownSkipNote to FinishedLabel.
# M5  (GH #289) the pin agrees everywhere it must: SrcMarkdown and MarkdownPinVersion in the .iss,
#     markdown-snapshot.json (targetPin, currentPin, resolvedTag, resolvedIdentityVersion), the sync README's
#     example lines, the sync script's example, and the Markdown Editor row of THIRD-PARTY-NOTICES.md - and all
#     equal -ExpectedVersion (1.5.0).
#
# PROVES IT CAN FAIL: after the real sources pass, the same scan runs on temp copies with one planted mutation
# each (listed at the bottom); every one must go red. The parameters also let it run against another .iss,
# e.g. origin/master's, which fails M1-M4 (no scan) and M5 (1.3.0).
#
# Exit: 0 pass, 1 fail, 2 could-not-run.

param(
    [string]$Installer       = (Join-Path $PSScriptRoot '..\..\installer\ClarionAssistant.iss'),
    [string]$Snapshot        = (Join-Path $PSScriptRoot '..\markdown-editor-sync\markdown-snapshot.json'),
    [string]$SyncReadme      = (Join-Path $PSScriptRoot '..\markdown-editor-sync\README.md'),
    [string]$SyncScript      = (Join-Path $PSScriptRoot '..\markdown-editor-sync\Sync-MarkdownEditor.ps1'),
    [string]$Notices         = (Join-Path $PSScriptRoot '..\..\THIRD-PARTY-NOTICES.md'),
    [string]$ExpectedVersion = '1.5.0',
    [switch]$NoMutations
)

$ErrorActionPreference = 'Stop'
$files = [ordered]@{ Installer = $Installer; Snapshot = $Snapshot; SyncReadme = $SyncReadme
                     SyncScript = $SyncScript; Notices = $Notices }
foreach ($f in $files.Values) {
    if (-not (Test-Path -LiteralPath $f)) { Write-Host "COULD NOT RUN: missing $f" -ForegroundColor Red; exit 2 }
}
$ExpectedTag = 'v' + $ExpectedVersion

# One Pascal routine of the [Code] section: from its header to the first 'end;' at column 0.
function Get-Routine([string]$src, [string]$name) {
    $m = [regex]::Match($src, '(?s)(?m)^(function|procedure) ' + [regex]::Escape($name) + '\(.*?^end;')
    if ($m.Success) { return $m.Value }
    return $null
}

function Invoke-Scan($p) {
    $fails = New-Object System.Collections.Generic.List[string]
    $t = @{}
    foreach ($k in $p.Keys) { $t[$k] = [IO.File]::ReadAllText($p[$k]).Replace("`r`n", "`n") }
    $iss = $t.Installer

    # ---- M1: the three folders and the directory scan ----
    $foreign = Get-Routine $iss 'MarkdownForeignFolder'
    $holds   = Get-Routine $iss 'DirHoldsMarkdownIdentity'
    $decide  = Get-Routine $iss 'DecideInstallMarkdown'
    if (-not $foreign) { $fails.Add('M1 no MarkdownForeignFolder routine in the .iss') }
    else {
        if ($foreign -notmatch "Base := Root \+ '\\accessory\\addins';") { $fails.Add('M1 MarkdownForeignFolder does not scan <Root>\accessory\addins') }
        if ($foreign -notmatch "DirHoldsMarkdownIdentity\(Base \+ '\\ClarionMarkdownEditor'\)") { $fails.Add('M1 MarkdownForeignFolder does not probe accessory\addins\ClarionMarkdownEditor (Addin Finder / upstream notes)') }
        if ($foreign -notmatch "DirHoldsMarkdownIdentity\(Base \+ '\\MarkdownAddin'\)") { $fails.Add('M1 MarkdownForeignFolder does not probe accessory\addins\MarkdownAddin (upstream addin-config.json)') }
        if ($foreign -notmatch "FindFirst\(Base \+ '\\\*', FR\)") { $fails.Add('M1 MarkdownForeignFolder does not FindFirst over accessory\addins\*') }
        if ($foreign -notmatch 'until not FindNext\(FR\)') { $fails.Add('M1 MarkdownForeignFolder does not FindNext through the subfolders') }
        if ($foreign -notmatch 'FindClose\(FR\)') { $fails.Add('M1 MarkdownForeignFolder leaks the find handle (no FindClose)') }
        if ($foreign -notmatch 'FR\.Attributes and FILE_ATTRIBUTE_DIRECTORY\) <> 0') { $fails.Add('M1 MarkdownForeignFolder does not restrict the scan to directories') }
        if ($foreign -notmatch "\(FR\.Name <> '\.'\) and \(FR\.Name <> '\.\.'\)") { $fails.Add("M1 MarkdownForeignFolder does not skip '.' and '..'") }
        if ($foreign -notmatch "CompareText\(FR\.Name, 'MarkdownEditor'\) <> 0") { $fails.Add('M1 MarkdownForeignFolder does not exclude our own MarkdownEditor folder (every existing install would read as foreign)') }
        if ($foreign -notmatch "DirHoldsMarkdownIdentity\(Base \+ '\\' \+ FR\.Name\)") { $fails.Add('M1 the scanned subfolders are not tested for the manifest') }
    }
    if (-not $holds) { $fails.Add('M1 no DirHoldsMarkdownIdentity routine in the .iss') }
    else {
        if ($holds -notmatch "FileExists\(Dir \+ '\\ClarionMarkdownEditor\.addin'\)") { $fails.Add('M1 DirHoldsMarkdownIdentity does not look for ClarionMarkdownEditor.addin') }
        if ($holds -notmatch "FindFirst\(Dir \+ '\\\*\.addin', FR\)") { $fails.Add('M1 DirHoldsMarkdownIdentity does not read the other *.addin files') }
        if ($holds -notmatch '<Identity name="ClarionMarkdownEditor"') { $fails.Add('M1 DirHoldsMarkdownIdentity does not read the Identity name out of a manifest') }
    }
    # The destination itself, in [Files], is still the one folder: all four roots, all through the frozen gate.
    $rows = [regex]::Matches($iss, '(?m)^Source: "\{#SrcMarkdown\}\\\*";[^\n]*$') | ForEach-Object { $_.Value }
    if ($rows.Count -ne 4) { $fails.Add("M1 expected 4 {#SrcMarkdown} [Files] rows (C10/C11/C11.1/C12), found $($rows.Count)") }
    foreach ($r in $rows) {
        if ($r -notmatch '\\accessory\\addins\\MarkdownEditor"') { $fails.Add("M1 a Markdown row does not install to accessory\addins\MarkdownEditor: $r") }
    }

    # ---- M2: the decision ----
    if (-not $decide) { $fails.Add('M2 no DecideInstallMarkdown routine in the .iss') }
    else {
        $call = $decide.IndexOf('Foreign := MarkdownForeignFolder(Root);', [StringComparison]::Ordinal)
        $none = $decide.IndexOf('no existing install', [StringComparison]::Ordinal)
        if ($call -lt 0) { $fails.Add('M2 DecideInstallMarkdown never asks MarkdownForeignFolder') }
        elseif ($none -ge 0 -and $call -gt $none) { $fails.Add('M2 DecideInstallMarkdown asks MarkdownForeignFolder only AFTER deciding "no existing install -> installing"') }
        $branch = [regex]::Match($decide, "(?s)if Foreign <> '' then\s*begin(.*?)\n  end;")
        if (-not $branch.Success) { $fails.Add('M2 no "if Foreign <> ''''" branch in DecideInstallMarkdown') }
        else {
            $b = $branch.Groups[1].Value
            if ($b -notmatch '(?s)Result := False;\s*Exit;') { $fails.Add('M2 the foreign-copy branch does not answer False (ours would still be written)') }
            if ($b -notmatch 'if FileExists\(AddinPath\) then') { $fails.Add('M2 the foreign-copy branch does not tell the UPGRADE case (ours still present) apart') }
            # -cnotmatch: -match is case-insensitive and the foreign-only log line says "two copies" too.
            if ($b -cnotmatch "Log\('Markdown\[' \+ Param \+ '\]: TWO copies -- ours in ' \+ Dir \+ ' and another") { $fails.Add('M2 the UPGRADE case is not logged as two copies naming both folders') }
            if ($b -notmatch "Log\('Markdown\[' \+ Param \+ '\]: already installed \(' \+ ForeignVer \+ '\) in ' \+ Foreign") { $fails.Add('M2 the foreign-only case does not log the folder that holds the editor') }
            if (([regex]::Matches($b, 'NoteMarkdownSkip\(')).Count -lt 2) { $fails.Add('M2 the Finished-page note is not recorded in both cases') }
            if ($b -notmatch 'Delete the ' -or $b -notmatch "\+ Dir \+\s*\n?\s*' \(by an earlier Clarion Assistant\)") { $fails.Add('M2 the UPGRADE note does not tell the user which folder (ours) to remove') }
        }
        if ($decide -notmatch '(?m)^\s*// UPGRADE, when OUR earlier copy') { $fails.Add('M2 the UPGRADE rule is not documented in the .iss comment') }
    }

    # ---- M3: frozen per root ----
    $should = Get-Routine $iss 'ShouldInstallMarkdown'
    if (-not $should) { $fails.Add('M3 no ShouldInstallMarkdown routine') }
    else {
        if ($should -notmatch 'if not MarkdownDecided\[Idx\] then') { $fails.Add('M3 ShouldInstallMarkdown no longer caches the verdict (per-file re-evaluation, the 5.8 defect)') }
        if ($should -notmatch 'MarkdownVerdict\[Idx\] := DecideInstallMarkdown\(Param\);') { $fails.Add('M3 ShouldInstallMarkdown does not take its verdict from DecideInstallMarkdown') }
    }
    foreach ($r in $rows) {
        if ($r -notmatch "Check: ShouldInstallMarkdown\('(10|11|111|12)'\)") { $fails.Add("M3 a Markdown row is not gated by ShouldInstallMarkdown: $r") }
    }
    if ($iss -match '(?m)^Source:[^\n]*Check: DecideInstallMarkdown') { $fails.Add('M3 a [Files] row calls DecideInstallMarkdown directly (unfrozen)') }

    # ---- M4: the Finished page ----
    $page = Get-Routine $iss 'CurPageChanged'
    if (-not $page) { $fails.Add('M4 no CurPageChanged routine') }
    else {
        if ($page -notmatch "\(CurPageID = wpFinished\) and \(MarkdownSkipNote <> ''\)") { $fails.Add('M4 CurPageChanged does not act on wpFinished when a note was recorded') }
        if ($page -notmatch 'WizardForm\.FinishedLabel\.Caption := WizardForm\.FinishedLabel\.Caption \+ #13#10#13#10 \+ MarkdownSkipNote;') { $fails.Add('M4 the note is not appended to the Finished page text') }
    }
    if ($iss -notmatch '(?m)^\s*MarkdownSkipNote: String;') { $fails.Add('M4 MarkdownSkipNote is not declared as a global') }

    # ---- M5: the pin ----
    $m = [regex]::Match($iss, '(?m)^#define SrcMarkdown SrcBase \+ "\\\.markdown-build\\(v[0-9.]+)"')
    if (-not $m.Success) { $fails.Add('M5 no SrcMarkdown define in the .iss') }
    elseif ($m.Groups[1].Value -ne $ExpectedTag) { $fails.Add("M5 .iss SrcMarkdown is $($m.Groups[1].Value), expected $ExpectedTag") }
    $m = [regex]::Match($iss, '(?m)^#define MarkdownPinVersion "([0-9.]+)"')
    if (-not $m.Success) { $fails.Add('M5 no MarkdownPinVersion define in the .iss') }
    elseif ($m.Groups[1].Value -ne $ExpectedVersion) { $fails.Add("M5 .iss MarkdownPinVersion is $($m.Groups[1].Value), expected $ExpectedVersion") }
    try { $snap = $t.Snapshot | ConvertFrom-Json } catch { $snap = $null; $fails.Add("M5 markdown-snapshot.json is not valid JSON: $($_.Exception.Message)") }
    if ($snap) {
        foreach ($pair in @(@('targetPin.tag', $snap.targetPin.tag, $ExpectedTag), @('currentPin.tag', $snap.currentPin.tag, $ExpectedTag),
                            @('currentPin.identityVersion', $snap.currentPin.identityVersion, $ExpectedVersion),
                            @('resolvedTag', $snap.resolvedTag, $ExpectedTag), @('resolvedIdentityVersion', $snap.resolvedIdentityVersion, $ExpectedVersion))) {
            if ($pair[1] -ne $pair[2]) { $fails.Add("M5 snapshot $($pair[0]) is '$($pair[1])', expected $($pair[2])") }
        }
        if ($snap.currentPin.assetSha256 -notmatch '^[0-9A-F]{64}$') { $fails.Add('M5 snapshot currentPin.assetSha256 is not a SHA-256') }
    }
    $readme = $t.SyncReadme
    if ($readme -notmatch ('(?m)^\.\\Sync-MarkdownEditor\.ps1 -Tag ' + [regex]::Escape($ExpectedTag) + '\s*$')) { $fails.Add("M5 sync README example does not sync $ExpectedTag") }
    if ($readme -notmatch ('(?m)^#define SrcMarkdown SrcBase \+ "\\\.markdown-build\\' + [regex]::Escape($ExpectedTag) + '"')) { $fails.Add("M5 sync README SrcMarkdown example is not $ExpectedTag") }
    if ($readme -notmatch ('(?m)^#define MarkdownPinVersion "' + [regex]::Escape($ExpectedVersion) + '"')) { $fails.Add("M5 sync README MarkdownPinVersion example is not $ExpectedVersion") }
    if ($t.SyncScript -notmatch ('(?m)^\s*\.\\Sync-MarkdownEditor\.ps1 -Tag ' + [regex]::Escape($ExpectedTag) + '\s*$')) { $fails.Add("M5 Sync-MarkdownEditor.ps1 example does not sync $ExpectedTag") }
    $row = [regex]::Match($t.Notices, '(?m)^\| \[Clarion Markdown Editor\]\([^)]*\) \(Mark Sarson\) \| ([^|]+) \|')
    if (-not $row.Success) { $fails.Add('M5 THIRD-PARTY-NOTICES.md has no Clarion Markdown Editor row') }
    elseif ($row.Groups[1].Value.Trim() -ne $ExpectedVersion) { $fails.Add("M5 THIRD-PARTY-NOTICES.md lists Markdown Editor $($row.Groups[1].Value.Trim()), expected $ExpectedVersion") }
    return $fails
}

$real = @{}; foreach ($k in $files.Keys) { $real[$k] = $files[$k] }
$fails = Invoke-Scan $real
if ($fails.Count -gt 0) {
    Write-Host "FAIL - $($fails.Count) finding(s):" -ForegroundColor Red
    $fails | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  [ok]   the real sources pass M1-M5 (pin $ExpectedTag / $ExpectedVersion)" -ForegroundColor Green
if ($NoMutations) { exit 0 }

# ---- mutation self-check: each planted defect must turn the scan red ----
$tmp = Join-Path $env:TEMP ("ca-mddup-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$V = $ExpectedVersion; $T = $ExpectedTag
$mutations = @(
    ,@('Installer',  "if DirHoldsMarkdownIdentity(Base + '\ClarionMarkdownEditor') then", "if DirHoldsMarkdownIdentity(Base + '\Markdown-Editor') then", 'Addin Finder folder no longer probed')
    ,@('Installer',  "if DirHoldsMarkdownIdentity(Base + '\MarkdownAddin') then",        'if False then',                                                 'upstream MarkdownAddin folder no longer probed')
    ,@('Installer',  "if FindFirst(Base + '\*', FR) then",                               'if False then',                                                 'addins\* directory scan removed')
    ,@('Installer',  "and (CompareText(FR.Name, 'MarkdownEditor') <> 0) then",           'and True then',                                                 'our own folder counted as foreign')
    ,@('Installer',  "DirHoldsMarkdownIdentity(Base + '\' + FR.Name)",                   'False',                                                         'scanned subfolders never tested')
    ,@('Installer',  "Result := FileExists(Dir + '\ClarionMarkdownEditor.addin');",      'Result := False;',                                              'canonical manifest name not looked for')
    ,@('Installer',  "if FindFirst(Dir + '\*.addin', FR) then",                          'if False then',                                                 'renamed manifests not read')
    ,@('Installer',  'Foreign := MarkdownForeignFolder(Root);',                          "Foreign := '';",                                                'DecideInstallMarkdown ignores the scan')
    ,@('Installer',  "    Result := False;`n    Exit;`n  end;`n`n  if not FileExists(AddinPath) then", "    Result := True;`n    Exit;`n  end;`n`n  if not FileExists(AddinPath) then", 'foreign copy found but ours still written')
    ,@('Installer',  "      Log('Markdown[' + Param + ']: TWO copies",                   "      Log('Markdown[' + Param + ']: second copy",               'UPGRADE case not logged as two copies')
    ,@('Installer',  '  // UPGRADE, when OUR earlier copy',                              '  // Upgrade: when our earlier copy',                           'UPGRADE rule comment removed')
    ,@('Installer',  'if not MarkdownDecided[Idx] then',                                 'if True then',                                                  'verdict no longer frozen per root')
    ,@('Installer',  "Check: ShouldInstallMarkdown('12')",                               "Check: DecideInstallMarkdown('12')",                            'a [Files] row bypasses the frozen gate')
    ,@('Installer',  'WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + MarkdownSkipNote;', '', 'Finished page no longer told')
    ,@('Installer',  "#define SrcMarkdown SrcBase + `"\.markdown-build\$T`"",            "#define SrcMarkdown SrcBase + `"\.markdown-build\v0.0.1`"",     '.iss SrcMarkdown off the pin')
    ,@('Installer',  "#define MarkdownPinVersion `"$V`"",                                '#define MarkdownPinVersion "0.0.1"',                            '.iss MarkdownPinVersion off the pin')
    ,@('Snapshot',   "`"resolvedIdentityVersion`": `"$V`"",                              '"resolvedIdentityVersion": "0.0.1"',                            'snapshot resolvedIdentityVersion off the pin')
    ,@('Snapshot',   "`"resolvedTag`": `"$T`"",                                          '"resolvedTag": "v0.0.1"',                                       'snapshot resolvedTag off the pin')
    ,@('SyncReadme', "#define MarkdownPinVersion `"$V`"",                                '#define MarkdownPinVersion "0.0.1"',                            'README MarkdownPinVersion example off the pin')
    ,@('SyncScript', ".\Sync-MarkdownEditor.ps1 -Tag $T",                                '.\Sync-MarkdownEditor.ps1 -Tag v0.0.1',                         'sync script example off the pin')
    ,@('Notices',    "(Mark Sarson) | $V |",                                             '(Mark Sarson) | 0.0.1 |',                                       'THIRD-PARTY-NOTICES row off the pin')
)
$survived = 0
try {
    foreach ($m in $mutations) {
        $copy = @{}; foreach ($k in $files.Keys) { $copy[$k] = $files[$k] }
        $orig = [IO.File]::ReadAllText($files[$m[0]])
        $find = $m[1].Replace("`n", "`r`n")
        if ($orig.IndexOf($find, [StringComparison]::Ordinal) -lt 0) { $find = $m[1] }
        $mut = $orig.Replace($find, $m[2].Replace("`n", "`r`n"))
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
Write-Host "PASS - M1-M5, and all $($mutations.Count) mutations go red" -ForegroundColor Green
exit 0
