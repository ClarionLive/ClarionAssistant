# End-to-end test: write_file / append_to_file must keep a Clarion source file in the encoding
# it already has (GH #203, ticket 90246460).
#
# THE BUG. Clarion source is traditionally Windows-ANSI (cp1252 on a Western machine) with no BOM.
# write_file re-encoded every .clw/.inc as UTF-8, so a Norwegian o-slash (one byte, F8) came back as
# two (C3 B8) - valid UTF-8, and mojibake ("A-tilde,cedilla") to the Clarion IDE and to every string literal the
# compiler bakes into the program. append_to_file was worse: it appended UTF-8 bytes onto a cp1252
# file, leaving one file in two encodings that no single decode can read.
#
# WHY THESE FIXTURES ARE RAW BYTES. Everything here is asserted at the byte level, and every
# fixture is written with WriteAllBytes. Two easier routes both hide the defect:
#   - a BOM'd fixture: every .NET / PowerShell reader honours a BOM, so the ANSI-read path under
#     test is never taken and the suite passes against the broken code;
#   - Set-Content / an encoding object: under PowerShell 7 "Default" is UTF-8, under 5.1 it is the
#     ANSI code page, so the same script seeds different bytes depending on who runs it.
# Non-ASCII in the requests is sent as JSON \u escapes for the same reason: the child's stdin is
# decoded in a console code page this script does not control.
#
# BOTH ENCODINGS ARE SEEDED. A suite with only cp1252 fixtures passes a fix that writes cp1252 to
# EVERYTHING - which would break every UTF-8 Clarion file instead.
#
# Run:  powershell -ExecutionPolicy Bypass -File ClarionAssistant\tests\McpFileTools.EncodingTest.ps1
# Exit: 0 pass, 1 fail, 2 could not run.

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$assertions = 0

function Assert-That([bool]$condition, [string]$message) {
    $script:assertions++
    if (-not $condition) { $script:failures.Add($message) }
}

function Report-Block([int]$startFailureCount, [string]$message) {
    if ($script:failures.Count -eq $startFailureCount) { Write-Host "  ok  $message" }
}

# The cp1252 fixtures only mean "the machine's ANSI file" where the machine's ANSI code page IS
# 1252: the server decodes a no-BOM, non-UTF-8 file in the system code page, the same one Clarion
# uses. On a cp1250 machine F8 is 'r-caron', and every cp1252 assertion below would test the wrong thing.
# That is "could not run", not a pass.
Add-Type -Namespace CaEncTest -Name Kernel32 -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint GetACP();'
$acp = [CaEncTest.Kernel32]::GetACP()
if ($acp -ne 1252) {
    Write-Host "COULD NOT RUN: the system ANSI code page is $acp, and these fixtures are cp1252 bytes." -ForegroundColor Red
    exit 2
}

$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\mcp-server\bin\Debug\clarion-mcp-server.exe'))
$csproj = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\mcp-server\ClarionMcpServer.csproj'))

# ALWAYS BUILD - see McpStdio.EndToEndTest.ps1 for the stale-.exe story. /restore as well: a fresh
# worktree has no restored packages, and building one without them is the stale-restore trap.
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                          -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    Write-Host "COULD NOT RUN: MSBuild was not found, so the server under test cannot be built." -ForegroundColor Red
    exit 2
}
& $msbuild $csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x86 /v:quiet /nologo | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
    Write-Host "COULD NOT RUN: build of ClarionMcpServer.csproj failed." -ForegroundColor Red
    exit 2
}

# One tools/call per server process: send it, close stdin, return the tool's text and error flag.
function Invoke-Tool([string]$name, [hashtable]$arguments) {
    # ConvertTo-Json escapes nothing above ASCII, so build the argument object by hand and escape
    # every non-ASCII char as \uXXXX. See the header: stdin's code page is not ours to choose.
    $parts = foreach ($k in $arguments.Keys) {
        $sb = New-Object System.Text.StringBuilder
        foreach ($c in $arguments[$k].ToCharArray()) {
            $code = [int]$c
            if     ($c -eq '"')  { [void]$sb.Append('\"') }
            elseif ($c -eq '\')  { [void]$sb.Append('\\') }
            elseif ($code -lt 0x20 -or $code -gt 0x7E) { [void]$sb.Append(('\u{0:x4}' -f $code)) }
            else { [void]$sb.Append($c) }
        }
        '"' + $k + '":"' + $sb.ToString() + '"'
    }
    $req = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"' + $name +
           '","arguments":{' + ($parts -join ',') + '}}}'

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '--stdio'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $p.StandardInput.WriteLine($req)
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch { }; throw "server did not exit" }

    $frame = @($out.GetAwaiter().GetResult() -split "`n" | Where-Object { $_.Trim().Length -gt 0 }) | Select-Object -First 1
    if (-not $frame) { throw "no response frame. stderr: $($err.GetAwaiter().GetResult())" }
    $res = ($frame | ConvertFrom-Json).result
    $text = $res.content[0].text
    return [pscustomobject]@{ Text = $text; IsError = ($res.isError -eq $true -or $text -like 'Error*') }
}

function Hex([byte[]]$b) { ($b | ForEach-Object { $_.ToString('X2') }) -join ' ' }

# Index of a byte sequence in a buffer, -1 if absent.
function Find-Bytes([byte[]]$hay, [byte[]]$needle) {
    for ($i = 0; $i -le $hay.Length - $needle.Length; $i++) {
        $hit = $true
        for ($j = 0; $j -lt $needle.Length; $j++) { if ($hay[$i + $j] -ne $needle[$j]) { $hit = $false; break } }
        if ($hit) { return $i }
    }
    return -1
}

$ascii = [System.Text.Encoding]::ASCII
function Bytes([string]$asciiText) { [byte[]]$ascii.GetBytes($asciiText) }
function Join-Bytes { [byte[]]($args | ForEach-Object { $_ }) }

# The characters under test. o-slash and e-acute are the reporter's; both are single bytes in cp1252
# and two-byte sequences in UTF-8, so the two encodings are distinguishable at every occurrence.
$oSlash = [char]0x00F8; $eAcute = [char]0x00E9
[byte[]]$oSlash1252 = 0xF8;       [byte[]]$eAcute1252 = 0xE9
[byte[]]$oSlashUtf8 = 0xC3, 0xB8; [byte[]]$eAcuteUtf8 = 0xC3, 0xA9
[byte[]]$bom = 0xEF, 0xBB, 0xBF

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('ca-enc-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null

Write-Host "=== write_file / append_to_file encoding preservation ($exe) ===" -ForegroundColor Cyan

try {
    # ------------------------------------------------------------ 1. write_file, cp1252 file
    # The reporter's case. The model read the file (read_file decodes cp1252 correctly), edited it,
    # and wrote it back. The file must stay cp1252: one byte per char, no UTF-8 sequences.
    $b = $failures.Count
    $f = Join-Path $work 'Ansi.clw'
    [IO.File]::WriteAllBytes($f, (Join-Bytes (Bytes "  MEMBER()`r`n! Kj") $oSlash1252 (Bytes "pt`r`n")))
    $r = Invoke-Tool 'write_file' @{ path = $f; content = "  MEMBER()`r`n! Kj${oSlash}pt caf${eAcute}`r`n" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That (-not $r.IsError) "write_file on a cp1252 file reported an error: $($r.Text)"
    Assert-That ((Find-Bytes $got $oSlashUtf8) -lt 0 -and (Find-Bytes $got $eAcuteUtf8) -lt 0) `
        "write_file converted a cp1252 .clw to UTF-8 (GH #203). bytes: $(Hex $got)"
    Assert-That ((Find-Bytes $got (Join-Bytes (Bytes 'Kj') $oSlash1252 (Bytes 'pt caf') $eAcute1252)) -ge 0) `
        "write_file did not write the cp1252 bytes for o-slash / e-acute. bytes: $(Hex $got)"
    Assert-That ((Find-Bytes $got $bom) -ne 0) "write_file put a BOM on a .clw"
    Report-Block $b "write_file keeps a cp1252 .clw in cp1252"

    # ------------------------------------------------------------ 2. write_file, UTF-8 file
    # The control for 1: a fix that writes ANSI to everything passes block 1 and fails here.
    $b = $failures.Count
    $f = Join-Path $work 'Utf8.inc'
    [IO.File]::WriteAllBytes($f, (Join-Bytes (Bytes "! Kj") $oSlashUtf8 (Bytes "pt`r`n")))
    $r = Invoke-Tool 'write_file' @{ path = $f; content = "! Kj${oSlash}pt caf${eAcute}`r`n" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That (-not $r.IsError) "write_file on a UTF-8 file reported an error: $($r.Text)"
    Assert-That ((Find-Bytes $got (Join-Bytes (Bytes 'Kj') $oSlashUtf8 (Bytes 'pt caf') $eAcuteUtf8)) -ge 0) `
        "write_file did not keep a UTF-8 .inc in UTF-8. bytes: $(Hex $got)"
    Assert-That ((Find-Bytes $got $bom) -ne 0) "write_file put a BOM on a UTF-8 .inc"
    Report-Block $b "write_file keeps a UTF-8 .inc in UTF-8, no BOM"

    # ------------------------------------------------------------ 3. write_file, UTF-8 WITH BOM
    # The one place "same encoding" yields to the hard rule: Clarion breaks on a BOM, so the write
    # stays UTF-8 and the BOM goes. (This is what write_file already did; it must keep doing it.)
    $b = $failures.Count
    $f = Join-Path $work 'Bom.clw'
    [IO.File]::WriteAllBytes($f, (Join-Bytes $bom (Bytes "! x`r`n")))
    $r = Invoke-Tool 'write_file' @{ path = $f; content = "! Kj${oSlash}pt`r`n" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That ((Find-Bytes $got $bom) -lt 0) "write_file kept a BOM on a .clw: $(Hex $got)"
    Assert-That ((Find-Bytes $got (Join-Bytes (Bytes 'Kj') $oSlashUtf8)) -ge 0) `
        "write_file did not write a BOM'd UTF-8 .clw as UTF-8. bytes: $(Hex $got)"
    Report-Block $b "write_file drops a .clw BOM and stays UTF-8"

    # ------------------------------------------------------------ 4. write_file, new file
    # No existing encoding to preserve. Clarion is an ANSI toolchain - the compiler bakes string
    # literals in as bytes and the app shows them in the ANSI code page - so UTF-8 here is the same
    # corruption one step later. A new Clarion source file is written in the ANSI code page, CRLF.
    $b = $failures.Count
    $f = Join-Path $work 'New.clw'
    $r = Invoke-Tool 'write_file' @{ path = $f; content = "! Kj${oSlash}pt`n" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That (-not $r.IsError) "write_file of a new .clw reported an error: $($r.Text)"
    Assert-That ((Hex $got) -eq (Hex (Join-Bytes (Bytes '! Kj') $oSlash1252 (Bytes "pt`r`n")))) `
        "a new .clw was not written as ANSI / no BOM / CRLF. bytes: $(Hex $got)"
    Report-Block $b "write_file creates a new .clw in the ANSI code page, CRLF"

    # ------------------------------------------------------------ 4b. write_file, all-ASCII file
    # Pure ASCII is valid UTF-8, so a naive "valid UTF-8 -> UTF-8" rule would flip this file to
    # UTF-8 on its first non-ASCII char. ASCII is no evidence of UTF-8; for Clarion source it
    # resolves to the ANSI code page. Only a BOM or a real multi-byte sequence is evidence.
    $b = $failures.Count
    $f = Join-Path $work 'Ascii.clw'
    [IO.File]::WriteAllBytes($f, (Bytes "  MEMBER()`r`n"))
    $r = Invoke-Tool 'write_file' @{ path = $f; content = "  MEMBER()`r`n! Kj${oSlash}pt`r`n" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That (-not $r.IsError) "write_file on an all-ASCII .clw reported an error: $($r.Text)"
    Assert-That ((Hex $got) -eq (Hex (Join-Bytes (Bytes "  MEMBER()`r`n! Kj") $oSlash1252 (Bytes "pt`r`n")))) `
        "an all-ASCII .clw was flipped to UTF-8 by its first non-ASCII write. bytes: $(Hex $got)"
    Report-Block $b "write_file keeps an all-ASCII .clw in the ANSI code page"

    # ------------------------------------------------------------ 4b2. every Clarion extension
    # The rule keys off the extension list, so each entry is checked - an extension missing from
    # the list silently falls back to the UTF-8 default.
    $b = $failures.Count
    foreach ($ext in @('.clw', '.inc', '.equ', '.int', '.trn', '.tpl', '.tpw')) {
        $f = Join-Path $work ('Ext' + $ext)
        $null = Invoke-Tool 'write_file' @{ path = $f; content = "! Kj${oSlash}pt`r`n" }
        $got = [IO.File]::ReadAllBytes($f)
        Assert-That ((Hex $got) -eq (Hex (Join-Bytes (Bytes '! Kj') $oSlash1252 (Bytes "pt`r`n")))) `
            "a new $ext was not written in the ANSI code page. bytes: $(Hex $got)"
    }
    Report-Block $b "every Clarion source extension is written in the ANSI code page"

    # ------------------------------------------------------------ 4c. non-Clarion file: unchanged
    # The ANSI rule is for the Clarion toolchain only. A new .txt keeps the old UTF-8 default.
    $b = $failures.Count
    $f = Join-Path $work 'Notes.txt'
    $r = Invoke-Tool 'write_file' @{ path = $f; content = "Kj${oSlash}pt" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That ((Hex $got) -eq (Hex (Join-Bytes (Bytes 'Kj') $oSlashUtf8 (Bytes 'pt')))) `
        "a new non-Clarion file was not written as UTF-8 / no BOM. bytes: $(Hex $got)"
    Report-Block $b "write_file leaves non-Clarion files on the UTF-8 default"

    # ------------------------------------------------------------ 5. write_file, unrepresentable
    # A char the file's code page cannot hold must not silently become '?'. The write is refused
    # and the file is left exactly as it was.
    $b = $failures.Count
    $f = Join-Path $work 'Ansi2.clw'
    [byte[]]$before = Join-Bytes (Bytes "! Kj") $oSlash1252 (Bytes "pt`r`n")
    [IO.File]::WriteAllBytes($f, $before)
    $r = Invoke-Tool 'write_file' @{ path = $f; content = "! Kj${oSlash}pt $([char]0x0416)`r`n" }   # Cyrillic Zhe
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That $r.IsError "write_file accepted a char cp1252 cannot hold instead of refusing: $($r.Text)"
    Assert-That ((Hex $got) -eq (Hex $before)) "a refused write_file still changed the file: $(Hex $got)"
    Report-Block $b "write_file refuses a char the file's code page can't hold, file untouched"

    # ------------------------------------------------------------ 6. append_to_file, cp1252 file
    $b = $failures.Count
    $f = Join-Path $work 'AnsiAppend.clw'
    [IO.File]::WriteAllBytes($f, (Join-Bytes (Bytes "! Kj") $oSlash1252 (Bytes "pt")))
    $r = Invoke-Tool 'append_to_file' @{ path = $f; text = "! caf${eAcute}" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That (-not $r.IsError) "append_to_file on a cp1252 file reported an error: $($r.Text)"
    Assert-That ((Hex $got) -eq (Hex (Join-Bytes (Bytes '! Kj') $oSlash1252 (Bytes "pt`r`n! caf") $eAcute1252))) `
        "append_to_file wrote UTF-8 onto a cp1252 .clw - mixed encoding (GH #203). bytes: $(Hex $got)"
    Report-Block $b "append_to_file appends cp1252 to a cp1252 .clw"

    # ------------------------------------------------------------ 7. append_to_file, UTF-8 file
    $b = $failures.Count
    $f = Join-Path $work 'Utf8Append.clw'
    [IO.File]::WriteAllBytes($f, (Join-Bytes (Bytes "! Kj") $oSlashUtf8 (Bytes "pt")))
    $r = Invoke-Tool 'append_to_file' @{ path = $f; text = "! caf${eAcute}" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That ((Hex $got) -eq (Hex (Join-Bytes (Bytes '! Kj') $oSlashUtf8 (Bytes "pt`r`n! caf") $eAcuteUtf8))) `
        "append_to_file did not append UTF-8 to a UTF-8 .clw. bytes: $(Hex $got)"
    Report-Block $b "append_to_file appends UTF-8 to a UTF-8 .clw"

    # ------------------------------------------------------------ 7b. append_to_file, all-ASCII
    $b = $failures.Count
    $f = Join-Path $work 'AsciiAppend.inc'
    [IO.File]::WriteAllBytes($f, (Bytes "! x"))
    $r = Invoke-Tool 'append_to_file' @{ path = $f; text = "! caf${eAcute}" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That ((Hex $got) -eq (Hex (Join-Bytes (Bytes "! x`r`n! caf") $eAcute1252))) `
        "append_to_file wrote UTF-8 onto an all-ASCII .inc. bytes: $(Hex $got)"
    Report-Block $b "append_to_file appends ANSI to an all-ASCII .inc"

    # ------------------------------------------------------------ 8. append_to_file, BOM'd file
    # Append must neither remove the existing BOM (it is not rewriting the file) nor add a second
    # one mid-file.
    $b = $failures.Count
    $f = Join-Path $work 'BomAppend.inc'
    [IO.File]::WriteAllBytes($f, (Join-Bytes $bom (Bytes "! x")))
    $r = Invoke-Tool 'append_to_file' @{ path = $f; text = "! caf${eAcute}" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That ((Hex $got) -eq (Hex (Join-Bytes $bom (Bytes "! x`r`n! caf") $eAcuteUtf8))) `
        "append_to_file on a BOM'd UTF-8 file produced the wrong bytes: $(Hex $got)"
    Report-Block $b "append_to_file appends UTF-8 to a BOM'd file, one BOM"

    # ------------------------------------------------------------ 9. append_to_file, unrepresentable
    $b = $failures.Count
    $f = Join-Path $work 'AnsiAppend2.clw'
    [byte[]]$before = Join-Bytes (Bytes "! Kj") $oSlash1252 (Bytes "pt")
    [IO.File]::WriteAllBytes($f, $before)
    $r = Invoke-Tool 'append_to_file' @{ path = $f; text = "! $([char]0x0416)" }
    $got = [IO.File]::ReadAllBytes($f)
    Assert-That $r.IsError "append_to_file accepted a char cp1252 cannot hold instead of refusing: $($r.Text)"
    Assert-That ((Hex $got) -eq (Hex $before)) "a refused append_to_file still changed the file: $(Hex $got)"
    Report-Block $b "append_to_file refuses a char the file's code page can't hold, file untouched"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "FAILED - $($failures.Count) of $assertions assertions" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
Write-Host "PASSED - $assertions assertions" -ForegroundColor Green
exit 0
