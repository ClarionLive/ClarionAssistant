# InstallerIndexerGate.Test.ps1 - ticket 583abdec review D2 + L4: the release gates build-installer.ps1 puts on
# clarion-indexer.exe, the vendored SQLite interops and signing.
#
#   I1  Get-PeArchitecture tells AnyCPU, AnyCPU-32BitPreferred, x86 and x64 apart - on REAL exes compiled here
#       (an AnyCPU exe's PE header says i386 like a 32-bit one; only the CLR header's flags differ).
#   I2  Test-IndexerBitness: on a 64-bit build machine AnyCPU (Prefer32Bit off) and x64 pass, 32-bit-only and
#       32-bit-preferred fail.
#   I3  the gate runs on the built indexer: the step that exits 1 calls both, and the built exe (when present) is
#       AnyCPU.
#   I4  Get-PinnedSqliteInterop pins BOTH vendored interops, the x64 one to the reviewed value, and both files on
#       disk match; a one-byte change fails Test-PinnedFileHash; the gate exits 1 on a mismatch.
#   I5  with -Sign every signing failure is fatal (no "Write-Warning ... Failed to sign"; signtool missing, a file
#       missing, a non-zero signtool exit all exit 1).
#
# The functions are taken from build-installer.ps1 itself (its AST), so this tests the shipped code.
# MUST BE ABLE TO GO RED: on 90477fb the functions do not exist and the sign failure is a warning.
# Exit: 0 pass, 1 fail.

$ErrorActionPreference = 'Stop'
$RepoDir = Split-Path -Parent $PSScriptRoot          # ...\ClarionAssistant
$RootDir = Split-Path -Parent $RepoDir
$failures = @()
$n = 0
function Check([bool]$ok, [string]$id, [string]$msg) {
    $script:n++
    if ($ok) { Write-Host "  ok   $id  $msg" }
    else { Write-Host "  FAIL $id  $msg" -ForegroundColor Red; $script:failures += "${id}: $msg" }
}

$scriptPath = Join-Path $RootDir 'installer\build-installer.ps1'
$text = [IO.File]::ReadAllText($scriptPath)
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
$wanted = 'Get-PeArchitecture', 'Test-IndexerBitness', 'Get-PinnedSqliteInterop', 'Test-PinnedFileHash'
$defs = @{}
foreach ($f in $ast.FindAll({ param($x) $x -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($wanted -contains $f.Name) { $defs[$f.Name] = $f.Extent.Text }
}
foreach ($w in $wanted) { Check ($defs.ContainsKey($w)) 'I0' "build-installer.ps1 defines $w" }
foreach ($d in $defs.Values) { . ([ScriptBlock]::Create($d)) }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ("ca-instgate-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $scratch | Out-Null
try {
    # ---- I1: real exes ----
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    $src = Join-Path $scratch 'p.cs'
    [IO.File]::WriteAllText($src, 'class P { static void Main() { } }', (New-Object System.Text.UTF8Encoding($false)))
    $kinds = [ordered]@{ 'anycpu' = 'AnyCPU'; 'anycpu32bitpreferred' = 'AnyCPU-32BitPreferred'; 'x86' = 'x86'; 'x64' = 'x64' }
    $built = @{}
    foreach ($e in $kinds.GetEnumerator()) {
        $exe = Join-Path $scratch ("p-" + $e.Key + ".exe")
        & $csc /nologo /target:exe "/platform:$($e.Key)" "/out:$exe" $src | Out-Null
        $built[$e.Key] = $exe
        $arch = if ((Test-Path $exe) -and (Get-Command Get-PeArchitecture -ErrorAction SilentlyContinue)) { Get-PeArchitecture $exe } else { '(not built / no function)' }
        Check ($arch -eq $e.Value) 'I1' "a /platform:$($e.Key) exe reads as $($e.Value) (got $arch)"
    }
    $nativeLike = Join-Path $env:WINDIR 'SysWOW64\cmd.exe'
    if ((Test-Path $nativeLike) -and (Get-Command Get-PeArchitecture -ErrorAction SilentlyContinue)) {
        Check ((Get-PeArchitecture $nativeLike) -eq 'x86') 'I1' 'a native i386 exe (SysWOW64\cmd.exe) reads as x86'
    }
    $junk = Join-Path $scratch 'junk.exe'
    [IO.File]::WriteAllText($junk, 'not a PE file', (New-Object System.Text.UTF8Encoding($false)))
    if (Get-Command Get-PeArchitecture -ErrorAction SilentlyContinue) {
        Check ((Get-PeArchitecture $junk) -eq 'unknown') 'I1' 'a file that is not a PE reads as unknown'
    }

    # ---- I2 ----
    if (Get-Command Test-IndexerBitness -ErrorAction SilentlyContinue) {
        Check ((Test-IndexerBitness 'AnyCPU' $true) -and (Test-IndexerBitness 'x64' $true)) 'I2' 'on 64-bit Windows AnyCPU (Prefer32Bit off) and x64 pass'
        Check (-not (Test-IndexerBitness 'x86' $true) -and -not (Test-IndexerBitness 'AnyCPU-32BitPreferred' $true) -and -not (Test-IndexerBitness 'unknown' $true)) 'I2' 'on 64-bit Windows 32-bit-only, 32-bit-preferred and unknown fail'
    } else { Check $false 'I2' 'Test-IndexerBitness exists' }

    # ---- I3: the gate and the built exe ----
    Check ($text -match '(?s)\$indexerArch = Get-PeArchitecture \$indexerExe\s*\r?\n\s*if \(-not \(Test-IndexerBitness \$indexerArch \(\[Environment\]::Is64BitOperatingSystem\)\)\) \{[^}]*exit 1') 'I3' 'Step 1c exits 1 unless the indexer runs 64-bit on a 64-bit build machine'
    Check ($text -notmatch '\$machine -ne 0x8664') 'I3' 'the old "PE machine must be 0x8664" test is gone (it refuses the AnyCPU build)'
    $realExe = Join-Path $RepoDir 'indexer\bin\Debug\clarion-indexer.exe'
    if ((Test-Path $realExe) -and (Get-Command Get-PeArchitecture -ErrorAction SilentlyContinue)) {
        $ra = Get-PeArchitecture $realExe
        Check ($ra -eq 'AnyCPU') 'I3' "the built indexer\bin\Debug\clarion-indexer.exe is AnyCPU with Prefer32Bit off (got $ra)"
    } else { Write-Host "  --   I3  no built indexer (or no function) - the built-exe check was skipped" -ForegroundColor DarkGray }

    # ---- I4: pinned interops ----
    if ((Get-Command Get-PinnedSqliteInterop -ErrorAction SilentlyContinue) -and (Get-Command Test-PinnedFileHash -ErrorAction SilentlyContinue)) {
        $pins = Get-PinnedSqliteInterop
        $x64Key = 'ClarionAssistant\lib\sqlite-fts5\x64\SQLite.Interop.dll'
        $x86Key = 'ClarionAssistant\lib\sqlite-fts5\SQLite.Interop.dll'
        Check ($pins[$x64Key] -eq 'ABA67C7E6C01856838B8BC6B0BA95E864E1FDCB3750AA7CDC1BC73511CEA6FE4') 'I4' 'the x64 interop is pinned to the reviewed SHA-256 (ABA67C7E...6FE4)'
        Check ($pins.ContainsKey($x86Key) -and $pins[$x86Key] -match '^[0-9A-F]{64}$') 'I4' 'the x86 interop is pinned too'
        foreach ($k in @($pins.Keys)) {
            Check (Test-PinnedFileHash (Join-Path $RootDir $k) $pins[$k]) 'I4' "$k matches its pin"
        }
        $copy = Join-Path $scratch 'SQLite.Interop.dll'
        $bytes = [IO.File]::ReadAllBytes((Join-Path $RootDir $x64Key))
        $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 0xFF
        [IO.File]::WriteAllBytes($copy, $bytes)
        Check (-not (Test-PinnedFileHash $copy $pins[$x64Key])) 'I4' 'a one-byte change fails the pin'
        Check (-not (Test-PinnedFileHash (Join-Path $scratch 'missing.dll') $pins[$x64Key])) 'I4' 'a missing file fails the pin'
    } else { Check $false 'I4' 'Get-PinnedSqliteInterop and Test-PinnedFileHash exist' }
    Check ($text -match '(?s)foreach \(\$rel in @\(\$pinned\.Keys\)\).*?if \(-not \(Test-PinnedFileHash \$full \$pinned\[\$rel\]\)\) \{.*?exit 1') 'I4' 'the gate exits 1 when an interop does not match its pin'

    # ---- I5: signing is fatal under -Sign ----
    $signBlock = if ($text -match '(?s)# .. Step 2: Sign DLLs.*?(?=# .. Step 3)') { $Matches[0] } else { '' }
    Check ($signBlock -ne '' -and $signBlock -notmatch 'Write-Warning') 'I5' 'the DLL signing step has no warning-and-continue path'
    Check ($signBlock -match 'if \(-not \$signtool\) \{[^}]*exit 1') 'I5' '-Sign without signtool.exe exits 1'
    Check ($signBlock -match 'if \(\$LASTEXITCODE -ne 0\) \{[^}]*Failed to sign[^}]*exit 1') 'I5' 'a non-zero signtool exit exits 1'
    Check ($signBlock -match 'if \(-not \(Test-Path \$f\)\) \{[^}]*exit 1') 'I5' 'a required binary missing under -Sign exits 1'
    Check ($text -match 'if \(\$Sign -and -not \$installerExe\) \{[^}]*exit 1') 'I5' '-Sign with no installer exe to sign exits 1'
}
finally {
    try { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
if ($failures.Count -eq 0) { Write-Host "PASS  InstallerIndexerGate.Test ($n checks)"; exit 0 }
Write-Host "FAIL  InstallerIndexerGate.Test: $($failures.Count) of $n failed" -ForegroundColor Red
exit 1
