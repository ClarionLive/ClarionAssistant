<#
.SYNOPSIS
    Fails if any file we write can be given a UTF-8 byte-order mark.

.DESCRIPTION
    THE MEASUREMENT THIS GUARD RESTS ON (2026-09-10, CLR 4.0.30319, every probe paired with a
    control that fired, so a PASS means something):

        File.WriteAllText(p, s, Encoding.UTF8)        -> ef bb bf 61 ...   BOM
        File.WriteAllText(p, s, new UTF8Encoding($false)) -> 61 ...        no BOM
        File.WriteAllText(p, s)            [2-arg]    -> 61 ...            no BOM
        File.ReadAllText(p, Encoding.UTF8)           -> strips it (first char U+007B, len 7 not 8)
        File.ReadAllLines(p)                         -> strips it
        fetch().then(r => r.text())                  -> strips it (control: ignoreBOM:true kept U+FEFF)
        node readFileSync + JSON.parse               -> DOES NOT. The only reader that breaks.
        the Clarion compiler                         -> does not

    THE ARGUMENT IS THE DEFECT, NOT A MISSING ONE. `System.Text.Encoding.UTF8` emits a preamble;
    the overload default does not. So the wrong spelling is the one that LOOKS like a
    clarification, which is how ticket 9b9dbc7d shipped: we wrote .claude\settings.local.json with
    a BOM, node's JSON.parse rejected the file, Claude Code ignored it silently, and the status
    line never worked for anyone.

    BUT DO NOT "FIX" IT BY DELETING THE ARGUMENT. That was this ticket's first instinct and it is
    subtly wrong. Measured, with a lone surrogate as input:
        File.WriteAllText(p, s)                      -> THREW
        File.WriteAllText(p, s, Encoding.UTF8)       -> ef bb bf 61 ef bf bd 62
        File.WriteAllText(p, s, new UTF8Encoding($false)) ->        61 ef bf bd 62
    The 2-arg overload is UTF8Encoding(false, throwOnInvalidBytes: TRUE). Dropping the argument
    therefore removes the BOM *and* flips invalid input from "write U+FFFD" to "throw" - and at a
    site whose write sits in a try/catch that only logs, that turns malformed text into a silently
    stale file. Same failure class as the bug we were fixing.
    `EncodingHelper.Utf8NoBom` is UTF8Encoding(false, false): byte-identical to Encoding.UTF8
    minus the preamble, and nothing else. That is the correct substitution, and it is what all
    14 triaged sites now use.

    RULE 1 - the curated list: files a non-.NET parser opens. Fails on a BOM-emitting encoding,
             and ALSO fails if the write MOVES, because a guard that can no longer see its target
             keeps reporting PASS. Warns (does not fail) if a guarded site drops to the implicit
             2-arg form, per the paragraph above.
    RULE 2 - repo-wide and categorical: no write API anywhere may be handed a BOM-emitting
             encoding. Rule 1 protects the known readers; Rule 2 protects the reader nobody has
             written yet. Rule 1's BOM check is deliberately belt-and-braces behind Rule 2, which
             is the stronger of the two; Rule 1's unique value is marker-gone detection.

    HOW RULE 2 READS C#, AND WHY IT IS NOT A REGEX. The first cut was regex over comment-stripped
    text, and review took it apart four times running, every time with ordinary compiling C#: a
    `//` inside a string, three-deep parens, an interpolation hole holding a quote, and a verbatim
    string inside a hole. Each was scanned and passed while the file count went UP, so the miss was
    always the pattern, never the walk - and the last one blanked the REST OF THE FILE, reporting
    every write below it clean. The per-shape history now lives in the $fixtures list below, which
    is the canonical catalogue: each entry carries the shape and what it defeated.
    So Rule 2 LEXES. Split-Code blanks every non-code character - line and block comments, strings,
    verbatim strings, char literals, and interpolated strings including their holes - while
    preserving offsets so line numbers stay exact; Get-CallArguments then walks real balanced
    parens. Nesting is unbounded, and a `//`, quote or paren inside any literal is inert.

    WHAT IT STILL CANNOT SEE, stated rather than left to be discovered:
      - Indirection. `var enc = Encoding.UTF8; File.WriteAllText(p, s, enc);` is green, and MUST
        stay green - ModernEmbeditorViewContent's .clw save passes a detected `_fileEncoding` this
        way on purpose. No non-dataflow scanner can separate those two cases.
      - A write API this list does not name.
      - A C# 11 raw string, which is over-blanked rather than modelled (see Split-Code).

    PROVE IT RATHER THAN TRUST IT:  .\Check-BomFreeWrites.ps1 -SelfTest
    Asserts the scanner goes red on every evasion shape review has found, and green on every
    look-alike. It builds its fixtures in memory and touches no file. A guard nobody has watched
    fail is not evidence - and four times now, the thing that was wrong was the guard.

    AND PROVE THE FIXTURES THEMSELVES. A green self-test only means something if a broken scanner
    would turn it red, so the set is MUTATION-TESTED: break one branch of Split-Code, re-run
    -SelfTest, and confirm it fails. That check found three branches pinned by nothing - the vstr
    body blank, the newline containment guards, and the {{ escape - which were exactly the
    statements hand-inlined for the speed fix, so the untested branches and the hand-edited ones
    were the same branches. The BRANCH COVERAGE fixtures below close that, each verified to fail
    under exactly one mutation (each reports "1 of N", so no fixture is masking another).
    It also corrected one of them: the obvious two-line form of the {{ fixture pinned NOTHING,
    because the hole's newline guard contained the damage; only a same-line write is swallowed.

    AND THE FIXTURE SET DECAYS, which is the part worth remembering. Adding that same newline guard
    silently disarmed two fixtures that HAD been pinning real branches, because their damage was on
    the next line and the guard now contained it. The guard and the fixture were measuring the same
    thing, so the fixtures stopped discriminating the moment the guard landed, and nothing failed.
    That is why the sweep is a script and not advice:
        .\Check-BomFreeWrites.Mutations.ps1
    Run it after ANY change to Split-Code, Get-CallArguments, the regex constants, or the fixture
    list. Expect every branch CAUGHT and "ALL BRANCHES PINNED". A SKIPPED row means the harness
    lost track of a branch - that is a harness failure, not a pass.

.PARAMETER SelfTest
    Verify the scanner against known-bad and known-good fixtures, then exit. Touches no repo file.

.NOTES
    Windows PowerShell 5.1 compatible, and must stay that way. Pure ASCII, no BOM.
    Exit 0 = clean. Exit 1 = at least one write can emit a BOM.
#>
[CmdletBinding()]
param(
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

# Rule 2 scans the whole REPO, not just the addin project: .cs also lives in tools\,
# DatePickerWebviewCOM\, docs\ and installer\. Worktrees are other branches' checkouts and are
# deliberately excluded - this run judges THIS tree only.
$repoRoot = Split-Path -Parent $scriptDir
if (-not (Test-Path (Join-Path $repoRoot '.git'))) { $repoRoot = $scriptDir }
$excludeDirs = '\\(obj|bin|packages|node_modules|worktrees)\\'

# Encodings that write a preamble. Utf8NoBom / new UTF8Encoding(false) deliberately do not.
$bomEmitting = 'Encoding\.(?:UTF8|Unicode|BigEndianUnicode|UTF32)|UTF8Encoding\s*\(\s*true\s*\)'

# `Encoding.UTF8.GetBytes(...)` is NOT a hazard - GetBytes never emits a preamble - but it can sit
# INSIDE a write's argument list, e.g. WriteAllText(p, Convert.ToBase64String(Encoding.UTF8
# .GetBytes(s))). Strip member access off an encoding before testing, so that is not a false alarm.
$encodingMemberAccess = 'Encoding\.(?:UTF8|Unicode|BigEndianUnicode|UTF32)\s*\.\s*\w+'

# .NET APIs that write a file. `File.WriteAllText` also matches the fully-qualified
# System.IO.File.WriteAllText as a substring; the constructor needs the optional qualifier spelled
# out, because `new System.IO.StreamWriter(fs, Encoding.UTF8)` walked straight through the bare form.
$writeApis = 'File\.(?:WriteAllText|AppendAllText|WriteAllLines|AppendAllLines)|new\s+(?:[\w.]+\.)?StreamWriter'

# --------------------------------------------------------------------------- the C# region lexer
function Set-Blank {
    <# Blank $Count characters from $From, preserving newlines so line numbers survive.
       [char[]] is a reference type, so the caller's buffer is mutated in place.

       Used only for the 2- and 3-character openers and escape pairs. The single-character case is
       deliberately INLINED at every call site instead: it runs once per character of every comment
       and every string in the repo, and routing it through a PowerShell function call took the
       full scan from 4s to 52s. Ugly beats unrun - a guard slow enough to skip is a guard nobody
       runs, which is the state this whole script exists to fix. #>
    param([char[]] $Buffer, [int] $From, [int] $Count)
    for ($k = $From; $k -lt ($From + $Count) -and $k -lt $Buffer.Length; $k++) {
        if ($Buffer[$k] -ne "`n" -and $Buffer[$k] -ne "`r") { $Buffer[$k] = ' ' }
    }
}

function Split-Code {
    <#
      Return a copy of $Text with every character that is NOT code replaced by a space, so string
      literals and comments cannot contribute tokens, while every offset - and therefore every
      line number - stays exactly where it was. Newlines are preserved for the same reason.

      A STACK, NOT NESTED LOOPS. The first lexer handled an interpolation hole with an inner loop
      that re-implemented string scanning, and that copy did not know about @"verbatim" strings.
      So `$"{p.TrimEnd(@"\")}"` - ordinary Windows C# - had its backslash read as an escape, which
      ate the closing quote, lost quote parity, and (with no newline guard on that branch) blanked
      THE REST OF THE FILE. Every write below it was reported clean. One context stack removes the
      whole class: a hole is just code again, so any literal legal at top level is legal inside it,
      to any depth, and each literal kind is spelled out exactly once.

      Contexts: code | str | vstr | char | interp(verbatim) | hole
        - code   : real source. Nothing is blanked. Opening a literal pushes.
        - hole   : the {...} of an interpolated string. Behaves like code - and IS blanked, because
                   it lives inside a literal - and `}` at its own brace depth 0 pops back to interp.
        - str    : "..."   \ escapes; an unterminated one stops at the newline rather than
                   swallowing the file.
        - vstr   : @"..."  "" escapes, \ is NOT an escape, newlines are legal.
        - char   : '...'   \ escapes, newline-guarded like str.
        - interp : the literal text of $"..." / $@"..." / @$"...". {{ and }} are escapes, { pushes
                   a hole, and a non-verbatim one is newline-guarded.

      KNOWN LIMIT: a C# 11 raw string ("""...""") is lexed as a run of empty/short strings rather
      than modelled properly. That over-blanks rather than under-blanks - parity is preserved and
      the fixture for it trips - but it is not a faithful reading.
    #>
    param([string] $Text)

    $out   = $Text.ToCharArray()
    $n     = $out.Length
    $i     = 0
    $stack = New-Object System.Collections.Generic.List[object]

    while ($i -lt $n) {
        $top  = $null
        $kind = 'code'
        if ($stack.Count -gt 0) { $top = $stack[$stack.Count - 1]; $kind = $top.Kind }

        $c    = $out[$i]
        $next = if ($i + 1 -lt $n) { $out[$i + 1] } else { [char]0 }
        $n2   = if ($i + 2 -lt $n) { $out[$i + 2] } else { [char]0 }

        # ---------------------------------------------------------------- code, and holes in it
        if ($kind -eq 'code' -or $kind -eq 'hole') {
            if ($c -eq '/' -and $next -eq '/') {
                while ($i -lt $n -and $out[$i] -ne "`n") { if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++ }
                continue
            }
            if ($c -eq '/' -and $next -eq '*') {
                Set-Blank $out $i 2; $i += 2
                while ($i -lt $n) {
                    if ($out[$i] -eq '*' -and $i + 1 -lt $n -and $out[$i + 1] -eq '/') { Set-Blank $out $i 2; $i += 2; break }
                    if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++
                }
                continue
            }
            if ((($c -eq '$' -and $next -eq '@') -or ($c -eq '@' -and $next -eq '$')) -and $n2 -eq '"') {
                Set-Blank $out $i 3; $i += 3; $stack.Add(@{ Kind = 'interp'; Verbatim = $true }); continue
            }
            if ($c -eq '$' -and $next -eq '"') { Set-Blank $out $i 2; $i += 2; $stack.Add(@{ Kind = 'interp'; Verbatim = $false }); continue }
            if ($c -eq '@' -and $next -eq '"') { Set-Blank $out $i 2; $i += 2; $stack.Add(@{ Kind = 'vstr' }); continue }
            if ($c -eq '"')  { if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; $stack.Add(@{ Kind = 'str'  }); continue }
            if ($c -eq "'")  { if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; $stack.Add(@{ Kind = 'char' }); continue }

            if ($kind -eq 'hole') {
                if ($c -eq '{') { $top.Brace++; if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; continue }
                if ($c -eq '}') {
                    if ($top.Brace -gt 0) { $top.Brace--; if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; continue }
                    $stack.RemoveAt($stack.Count - 1)   # back to the enclosing interpolated literal
                    if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; continue
                }
                if ($c -eq "`n") {
                    # Containment, matching str/char/interp. A non-verbatim interpolated string
                    # cannot span a line, so a newline inside its hole means the source is
                    # malformed - pop the hole AND its interp rather than blanking to EOF. Without
                    # this the hole was the one context that could still swallow a whole file, and
                    # hide every write below it: the same failure MODE as the @"-in-a-hole bug,
                    # one context over.
                    $encl = if ($stack.Count -ge 2) { $stack[$stack.Count - 2] } else { $null }
                    if ($null -ne $encl -and $encl.Kind -eq 'interp' -and -not $encl.Verbatim) {
                        $stack.RemoveAt($stack.Count - 1)   # the hole
                        $stack.RemoveAt($stack.Count - 1)   # its interpolated literal
                        continue
                    }
                }
                if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; continue      # inside a literal: blank everything
            }
            $i++                                         # ordinary code character: leave it alone
            continue
        }

        # ---------------------------------------------------------------------------- literals
        if ($kind -eq 'str' -or $kind -eq 'char') {
            $close = if ($kind -eq 'str') { '"' } else { "'" }
            if ($c -eq '\')     { Set-Blank $out $i 2; $i += 2; continue }
            if ($c -eq $close)  { if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; $stack.RemoveAt($stack.Count - 1); continue }
            if ($c -eq "`n")    { $stack.RemoveAt($stack.Count - 1); continue }   # unterminated: contain it
            if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; continue
        }

        if ($kind -eq 'vstr') {
            if ($c -eq '"' -and $next -eq '"') { Set-Blank $out $i 2; $i += 2; continue }   # "" escape
            if ($c -eq '"') { if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; $stack.RemoveAt($stack.Count - 1); continue }
            if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; continue                                            # newlines legal
        }

        if ($kind -eq 'interp') {
            if (-not $top.Verbatim -and $c -eq '\')                  { Set-Blank $out $i 2; $i += 2; continue }
            if ($top.Verbatim -and $c -eq '"' -and $next -eq '"')    { Set-Blank $out $i 2; $i += 2; continue }
            if ($c -eq '"')                                          { if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; $stack.RemoveAt($stack.Count - 1); continue }
            if ($c -eq '{' -and $next -eq '{')                       { Set-Blank $out $i 2; $i += 2; continue }
            if ($c -eq '}' -and $next -eq '}')                       { Set-Blank $out $i 2; $i += 2; continue }
            if ($c -eq '{')                                          { if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; $stack.Add(@{ Kind = 'hole'; Brace = 0 }); continue }
            if (-not $top.Verbatim -and $c -eq "`n")                 { $stack.RemoveAt($stack.Count - 1); continue }
            if ($out[$i] -ne "`n" -and $out[$i] -ne "`r") { $out[$i] = ' ' }; $i++; continue
        }

        $i++   # unreachable; keeps the loop total
    }
    return -join $out
}


function Get-CallArguments {
    <#
      From the '(' at or after $Start in already-lexed code text, return the text between that
      paren and its true partner. Unbounded nesting; $null if the call never closes.
    #>
    param([string] $Code, [int] $Start)

    $open = $Code.IndexOf('(', $Start)
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($i = $open; $i -lt $Code.Length; $i++) {
        if ($Code[$i] -eq '(') { $depth++ }
        elseif ($Code[$i] -eq ')') {
            $depth--
            if ($depth -eq 0) { return $Code.Substring($open + 1, $i - $open - 1) }
        }
    }
    return $null
}

function Test-CodeForBomWrites {
    <# Findings for one blob of C# source: @{ Line; Snippet }. Empty array when clean. #>
    param([string] $Source)

    if ([string]::IsNullOrEmpty($Source)) { return @() }
    # Cheap reject: a file with no write API at all cannot fail, and skipping it keeps the lexer
    # off the ~4000-line files that dominate the repo. Same pattern as the real pass, so it can
    # only ever over-admit - it cannot silently skip a file the lexer would have flagged.
    if ($Source -notmatch $writeApis) { return @() }

    $code     = Split-Code $Source
    $findings = @()
    foreach ($m in [regex]::Matches($code, $writeApis)) {
        $callArgs = Get-CallArguments $code ($m.Index + $m.Length)
        if ($null -eq $callArgs) { continue }
        $probe = [regex]::Replace($callArgs, $encodingMemberAccess, ' ')
        if ($probe -match $bomEmitting) {
            $lineNo  = ($code.Substring(0, $m.Index) -split "`n").Count
            $snippet = ($Source.Substring($m.Index, [Math]::Min(140, $Source.Length - $m.Index)) -replace '\s+', ' ').Trim()
            if ($snippet.Length -gt 120) { $snippet = $snippet.Substring(0, 120) + '...' }
            $findings += @{ Line = $lineNo; Snippet = $snippet }
        }
    }
    return $findings
}

# ------------------------------------------------------------------------------------- self-test
if ($SelfTest) {
    # The MUST-TRIP list is an evasion catalogue: every shape that has ever slipped past a version
    # of this rule, plus the ones review threw at it afterwards. Add to it, never subtract.
    $fixtures = @(
        @{ Trip = $true;  Line = 4; Why = 'plain single-line write';
           Code = 'File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'WRAPPED args - encoding on a continuation line (SnippetStore.Save shape)';
           Code = "File.WriteAllText(Path.Combine(dir, `"x.json`"),`r`n    new JavaScriptSerializer().Serialize(o), Encoding.UTF8);" }
        @{ Trip = $true;  Why = 'new StreamWriter';
           Code = 'using (var w = new StreamWriter(fs, Encoding.UTF8)) { }' }
        @{ Trip = $true;  Why = 'NAMESPACE-QUALIFIED constructor (walked through the bare `new StreamWriter` form)';
           Code = 'using (var w = new System.IO.StreamWriter(fs, Encoding.UTF8)) { }' }
        @{ Trip = $true;  Why = 'fully-qualified static write';
           Code = 'System.IO.File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'AppendAllText';
           Code = 'File.AppendAllText(LogPath(), line, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'WriteAllLines';
           Code = 'File.WriteAllLines(p, lines, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'DOUBLE SLASH INSIDE A STRING (defeated comment-stripping)';
           Code = 'File.WriteAllText(p, "http://example.com/x", Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'THREE-DEEP PAREN NESTING (defeated the two-level arg pattern)';
           Code = 'File.WriteAllText(Path.Combine(d, Path.Combine(a, Path.GetFileName(b))), s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'INTERPOLATED string whose hole holds a quoted paren (defeated quote parity)';
           Code = 'File.WriteAllText(p, $"a{Fmt("(")}b", Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'verbatim INTERPOLATED string with a hole';
           Code = 'File.WriteAllText(p, $@"C:\{a}//{Fmt(")")}", Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'VERBATIM string inside an interpolation hole - the nested-string handler had no @" awareness, read the \ as an escape, ate the closing quote and blanked THE REST OF THE FILE';
           Code = 'var a = $"{p.TrimEnd(@"\")}";' + "`r`n    " + 'File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'nested interpolated string inside a hole, with a later write';
           Code = 'var a = $"{$"{p}"}";' + "`r`n    " + 'File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'interpolated hole holding a char literal that is a quote';
           Code = "File.WriteAllText(p, `$`"{s.Trim('`"')}x`", Encoding.UTF8);" }
        @{ Trip = $true;  Why = 'paren inside a plain string literal';
           Code = 'File.WriteAllText(p, "a)b(c", Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'char literal holding a quote';
           Code = "File.WriteAllText(p, s.Trim('`"'), Encoding.UTF8);" }
        @{ Trip = $true;  Why = 'verbatim string containing a double slash';
           Code = 'File.WriteAllText(p, @"C:\a//b", Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'BOM-emitting encoding that is not UTF8';
           Code = 'File.WriteAllText(p, s, Encoding.Unicode);' }
        @{ Trip = $true;  Why = 'new UTF8Encoding(true)';
           Code = 'File.WriteAllText(p, s, new UTF8Encoding(true));' }

        @{ Trip = $false; Why = 'EncodingHelper.Utf8NoBom - the correct substitution';
           Code = 'File.WriteAllText(p, s, Services.EncodingHelper.Utf8NoBom);' }
        @{ Trip = $false; Why = 'new UTF8Encoding(false)';
           Code = 'File.WriteAllText(p, s, new UTF8Encoding(false));' }
        @{ Trip = $false; Why = 'the 2-arg overload - BOM-free, though stricter on invalid bytes';
           Code = 'File.WriteAllText(p, s);' }
        @{ Trip = $false; Why = 'GetBytes NESTED INSIDE a write - correct BOM-free code, must not alarm';
           Code = 'File.WriteAllText(p, Convert.ToBase64String(Encoding.UTF8.GetBytes(s)));' }
        @{ Trip = $false; Why = 'GetBytes on its own statement';
           Code = 'var b = Encoding.UTF8.GetBytes(s); File.WriteAllBytes(p, b);' }
        @{ Trip = $false; Why = 'a READ - File.ReadAllText strips a BOM, so this is harmless';
           Code = 'var s = File.ReadAllText(p, Encoding.UTF8);' }
        @{ Trip = $false; Why = 'LINE COMMENT naming both tokens (this repo has one on purpose)';
           Code = '// Encoding.UTF8 emits a BOM via File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $false; Why = 'BLOCK comment naming both tokens';
           Code = '/* File.WriteAllText(p, s, Encoding.UTF8); */' }
        @{ Trip = $false; Why = 'XML doc comment naming both tokens';
           Code = '/// <summary>File.WriteAllText(p, s, Encoding.UTF8)</summary>' }
        @{ Trip = $false; Why = 'a string literal containing the whole offending call';
           Code = 'var msg = "File.WriteAllText(p, s, Encoding.UTF8);";' }
        @{ Trip = $false; Why = 'DOCUMENTED LIMITATION - encoding held in a variable is invisible';
           Code = 'var enc = Encoding.UTF8; File.WriteAllText(p, s, enc);' }
        @{ Trip = $false; Why = 'StreamWriter round-tripping a detected encoding (the real .clw save)';
           Code = 'using (var sw = new StreamWriter(fs, _fileEncoding)) { }' }
        @{ Trip = $false; Why = 'CONTROL for the verbatim-in-hole case: EVEN backslashes, correct write after';
           Code = 'var a = $"{p.TrimEnd(@"\\")}";' + "`r`n    " + 'File.WriteAllText(p, s, Services.EncodingHelper.Utf8NoBom);' }
        @{ Trip = $false; Why = 'CONTROL for the verbatim-in-hole case: "" escape in the hole, correct write after';
           Code = 'var a = $"{p.Replace(@"a""b", "")}";' + "`r`n    " + 'File.WriteAllText(p, s, Services.EncodingHelper.Utf8NoBom);' }

        # --- BRANCH COVERAGE -----------------------------------------------------------------
        # The four above pin literal OPENERS. These pin the parts of Split-Code that were hand-
        # INLINED for the 52s->8s fix: a literal's BODY being blanked, newline containment, and
        # the brace escapes. Mutation testing showed the untested branches and the hand-edited
        # branches were the same branches - deleting the vstr body blank, the newline guard, or
        # the {{ escape each still reported a clean 33/33. Each fixture here is verified to go red
        # under exactly one such mutation; do not remove one without checking what it was pinning.
        @{ Trip = $false; Why = 'BRANCH: a vstr BODY holding a whole offending call - green only if the body is really blanked';
           Code = 'var a = @"File.WriteAllText(p, s, Encoding.UTF8);";' }
        @{ Trip = $true;  Why = 'BRANCH: unterminated string - the newline guard must contain it, or the write below is hidden';
           Code = 'var a = "unterminated' + "`r`n    " + 'File.WriteAllText(p, s, Encoding.UTF8);' }
        # SAME LINE deliberately. The next-line form does NOT discriminate: without the {{ escape
        # the brace opens a hole, but the hole's own newline guard then contains it and the write
        # below is still seen. Only a write on the same line is actually swallowed. Mutation
        # testing caught this - the obvious two-line version of this fixture was pinning nothing.
        @{ Trip = $true;  Why = 'BRANCH: the {{ escape - without it the brace opens a hole that eats the rest of the LINE';
           Code = 'var a = $"{{"; File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'BRANCH: unterminated HOLE - the one context that had no containment guard';
           Code = 'var a = $"{oops;' + "`r`n    " + 'File.WriteAllText(p, s, Encoding.UTF8);' }

        # --- BRANCH COVERAGE, SECOND SWEEP ---------------------------------------------------
        # From mutating EVERY branch rather than only the ones that had already failed. Six of
        # these were pinned by nothing at all.
        #
        # NOTE THE SAME-LINE SHAPES BELOW, AND WHY. A containment guard and a next-line fixture
        # measure the same thing, so adding a guard silently DISARMS any fixture whose damage was
        # next-line: the hole newline guard quietly stopped the `@"`-opener fixture from
        # discriminating, and nothing noticed until every branch was mutated. Where a mutation's
        # damage is confined to one line, the bad write must be ON that line.
        @{ Trip = $true;  Line = 5; Why = 'BRANCH: */ close detection - without it a block comment eats to EOF and hides the write below';
           Code = '/* c */' + "`r`n    " + 'File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'BRANCH: the @" opener push, SAME LINE - the next-line form was disarmed by the hole newline guard';
           Code = 'var a = $"{p.TrimEnd(@"\")}"; File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'BRANCH: str/char backslash escape';
           Code = 'var a = "a\"b"; File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Why = 'BRANCH: interpolated-string backslash escape';
           Code = 'var a = $"a\"b"; File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $true;  Line = 5; Why = 'BRANCH: interp non-verbatim newline guard - unterminated interpolated string must not blank to EOF';
           Code = 'var a = $"abc' + "`r`n    " + 'File.WriteAllText(p, s, Encoding.UTF8);' }
        @{ Trip = $false; Why = 'BRANCH: hole BODY blank - a write buried inside a hole must not trip';
           Code = 'var a = $"{new StreamWriter(fs, Encoding.UTF8)}";' }
    )

    $pass = 0; $fail = @()
    for ($k = 0; $k -lt $fixtures.Count; $k++) {
        $f       = $fixtures[$k]
        $source  = "using System.IO;`r`nusing System.Text;`r`nclass F$k { void M(string p, string s) {`r`n    $($f.Code)`r`n} }`r`n"
        $hits    = @(Test-CodeForBomWrites $source)
        $tripped = ($hits.Count -gt 0)
        if ($tripped -ne $f.Trip) {
            $want = if ($f.Trip) { 'TRIP' } else { 'stay green' }
            $fail += "  expected to $want but did not: $($f.Why)`n      $($f.Code)"
        }
        # A fixture may ALSO assert the line the finding was reported on. Without at least one of
        # these the entire line-number path is untested - mutation proved it: changing the count to
        # `.Count + 1` still passed every fixture, because trip/no-trip is all they ever asserted.
        # Fixture source is wrapped in a 3-line preamble, so the first statement is line 4.
        elseif ($f.Trip -and $f.ContainsKey('Line') -and $hits[0].Line -ne $f.Line) {
            $fail += "  reported line $($hits[0].Line), expected $($f.Line): $($f.Why)"
        }
        else { $pass++ }
    }

    $mustTrip  = @($fixtures | Where-Object { $_.Trip }).Count
    $mustGreen = $fixtures.Count - $mustTrip

    Write-Host ""
    if ($fail.Count -eq 0) {
        Write-Host "SELF-TEST PASS - $pass/$($fixtures.Count) fixtures behaved as specified ($mustTrip must-trip, $mustGreen must-stay-green)." -ForegroundColor Green
        Write-Host "  Every known evasion shape trips the scanner; every look-alike stays green." -ForegroundColor DarkGray
        exit 0
    }
    Write-Host "SELF-TEST FAIL - $($fail.Count) of $($fixtures.Count) fixtures misbehaved." -ForegroundColor Red
    Write-Host ""
    foreach ($f in $fail) { Write-Host $f -ForegroundColor Red }
    Write-Host ""
    Write-Host "  The scanner is blind to at least one shape. Fix Split-Code / Get-CallArguments" -ForegroundColor Yellow
    Write-Host "  before trusting a PASS from the main run." -ForegroundColor Yellow
    exit 1
}

# ---------------------------------------------------------------- RULE 1: the curated readers
# file (relative to the addin project) ; the write we are guarding ; who reads it and why
$guarded = @(
    @{ File = 'Services\ClaudeMdDeployer.cs'      # moved out of AssistantChatControl by GH #227
       Marker = 'path, json'
       Reader = 'Claude Code / Copilot, node JSON.parse - PROVEN to reject a BOM' }
    @{ File = 'AssistantChatControl.cs'
       Marker = 'promptFile, systemPromptExtra'
       Reader = 'node, via --append-system-prompt-file' }
    @{ File = 'AssistantChatControl.cs'
       Marker = 'initialPromptFile, initialPrompt'
       Reader = 'node' }
    # Clarion source no longer goes through WriteAllText at all (GH #203): every .clw/.inc write in
    # the addin and the MCP tools resolves an encoding in ClarionSourceText and ends in ONE raw-byte
    # write, whose bytes come from Encoding.GetBytes - which never emits a preamble. `Api` names the
    # call the marker must sit on; it defaults to WriteAllText. Guarding the final write (not just
    # its callers) is what keeps a future "simplify it back to WriteAllText(path, s, enc)" visible:
    # with a BOM'd UTF-8 enc that overload WOULD write a preamble.
    @{ File = 'Services\StructureDesignerService.cs'
       Marker = 'clwPath, normalized'
       Api = 'ClarionSourceText.WriteFile'
       Reader = 'the Clarion compiler - a BOM in a .clw is a known breaker' }
    @{ File = 'Services\ClarionSourceText.cs'
       Marker = 'File.WriteAllBytes(path, bytes)'
       Api = 'WriteAllBytes'
       Reader = 'the Clarion compiler and IDE - every Clarion source write (write_file, create class, designer scratch) lands here' }
)

$failures = @()
$warnings = @()
$checked  = 0

foreach ($g in $guarded) {
    $path = Join-Path $scriptDir $g.File
    if (-not (Test-Path $path)) {
        $failures += "MISSING FILE  $($g.File)"
        continue
    }

    # -SimpleMatch takes the marker VERBATIM. Do NOT wrap it in [regex]::Escape: that escapes
    # spaces to "\ ", and SimpleMatch then hunts for a literal backslash that is not there, so
    # every marker reports GONE. Caught by breaking this script on its first run.
    $api = if ($g.Api) { $g.Api } else { 'WriteAllText' }
    $line = Select-String -Path $path -Pattern $g.Marker -SimpleMatch |
            Where-Object { $_.Line.Contains($api) } |
            Select-Object -First 1

    if (-not $line) {
        # The write moved or was renamed. That is a real failure: this guard is now blind, and a
        # blind guard is worse than none because it keeps reporting PASS. THIS is Rule 1's unique
        # value - the BOM check below is belt-and-braces behind Rule 2, which is stronger.
        $failures += "MARKER GONE   $($g.File): no $api matching '$($g.Marker)' - the guard can no longer see this write"
        continue
    }

    $checked++
    if ($line.Line -match $bomEmitting) {
        $failures += "BOM REGRESSED $($g.File):$($line.LineNumber)`n                $($line.Line.Trim())`n                read by: $($g.Reader)"
    } elseif ($api -eq 'WriteAllText' -and $line.Line -notmatch 'Utf8NoBom|UTF8Encoding\s*\(\s*false') {
        # (Only WriteAllText has an implicit-encoding form. The Clarion sites pass pre-encoded bytes
        # or an explicit ANSI/UTF-8-no-BOM encoding through ClarionSourceText.)
        # Not a failure - the 2-arg form is BOM-free. But at a site whose reader is node or the
        # Clarion compiler the encoding is load-bearing and should be visible, and the implicit
        # form also throws on invalid bytes where Utf8NoBom writes U+FFFD. Say so out loud rather
        # than leaving the convention to live only in a comment.
        $warnings += "IMPLICIT      $($g.File):$($line.LineNumber) uses the 2-arg overload; prefer an explicit EncodingHelper.Utf8NoBom here (reader: $($g.Reader))"
    }
}

# ------------------------------------------------- RULE 2: no BOM-emitting encoding on any write
$scanned = 0
foreach ($cs in Get-ChildItem -Path $repoRoot -Filter *.cs -Recurse -File -ErrorAction SilentlyContinue) {
    if ($cs.FullName -match $excludeDirs) { continue }
    $rel = $cs.FullName.Substring($repoRoot.Length).TrimStart('\')
    # A file that vanished or is locked between enumeration and read must not abort the run and
    # mask the other 177 results - report it as its own failure line instead.
    try   { $source = Get-Content -LiteralPath $cs.FullName -Raw -ErrorAction Stop }
    catch { $failures += "COULD NOT READ ${rel}: $($_.Exception.Message)"; continue }
    $scanned++
    foreach ($hit in @(Test-CodeForBomWrites $source)) {
        $failures += "BOM-EMITTING  ${rel}:$($hit.Line)`n                $($hit.Snippet)`n                a write API was handed a BOM-emitting encoding"
    }
}

Write-Host ""
foreach ($w in $warnings) { Write-Host "  $w" -ForegroundColor Yellow }
if ($warnings.Count -gt 0) { Write-Host "" }

if ($failures.Count -eq 0) {
    Write-Host "PASS - $checked guarded writes are BOM-free; $scanned .cs files carry no BOM-emitting write." -ForegroundColor Green
    Write-Host "       Run with -SelfTest to watch the scanner fail on purpose before trusting this." -ForegroundColor DarkGray
    exit 0
}

Write-Host "FAIL - a file we write can be given a byte-order mark." -ForegroundColor Red
Write-Host ""
foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
Write-Host ""
Write-Host "  Encoding.UTF8 EMITS a BOM. Use Services.EncodingHelper.Utf8NoBom, which is the same" -ForegroundColor Yellow
Write-Host "  encoding minus the preamble. Do NOT just delete the argument: the 2-arg overload is" -ForegroundColor Yellow
Write-Host "  BOM-free but THROWS on invalid bytes where Encoding.UTF8 wrote U+FFFD, which turns" -ForegroundColor Yellow
Write-Host "  malformed input into a silently stale file wherever the write is inside a catch." -ForegroundColor Yellow
Write-Host "  None of this shows up in a .NET test: File.ReadAllText strips the BOM on the way" -ForegroundColor Yellow
Write-Host "  back in, so the round-trip looks fine while the other reader fails. See 9b9dbc7d." -ForegroundColor Yellow
exit 1
