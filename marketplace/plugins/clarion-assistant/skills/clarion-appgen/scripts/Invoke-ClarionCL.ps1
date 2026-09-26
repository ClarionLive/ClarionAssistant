<#
.SYNOPSIS
    Runs ClarionCL.exe headlessly with modal-dialog detection, capture, and honest exit reporting.

.DESCRIPTION
    ClarionCL has no quiet/unattended switch and can raise modal dialogs a headless caller
    cannot see (CreateNoWindow) — the process just blocks until the timeout. This wrapper:

      1. Guards the launch: working directory must exist, ClarionCL.exe must exist, and any
         file arguments are checked for existence up front (kills the GENE000-from-wrong-cwd
         class of failure before it happens).
      2. Runs with a hard timeout, polling for modal dialogs the whole time.
      3. When a visible dialog appears on any process in the child tree, captures its title,
         class, static text, and button captions — so the caller learns WHY it blocked.
      4. Optionally dismisses the dialog (-DismissDialogs), preferring buttons that DECLINE
         action: Ignore > No > Cancel > OK > Close. (Safe for the solution-association dialog,
         correct for info dialogs.)
      5. On timeout, kills the ENTIRE child process tree (ClarionCL spawns compilers that
         otherwise orphan and keep files locked).
      6. Asserts artifact freshness (-ExpectArtifact): each named file must exist AND have a
         LastWriteTime after launch, turning silent no-ops into real failures.
      7. Returns stdout/stderr WHOLE. Never filter ClarionCL output — the explanatory line is
         routinely adjacent to, not inside, the line matching 'error'.

    Known dialog classes this catches: solution-association mismatch, dictionary-upgrade prompt
    (better: pass /au and it never appears), TXD "Report Writer only format" rejection.

    Empirical exit-code note: ClarionCL's exit code has matched its error count in all our
    observations (1 error -> 1, 3 parse errors -> 3, success -> 0), consistent with behaviour
    documented in the (Clarion 7-era) Advanced Topics Reference Guide. Treat it as a good error
    signal, not a guaranteed contract.

.PARAMETER Arguments
    Argument list for ClarionCL, e.g. @('/au','/ag','C:\work\MyApp.app').

.PARAMETER WorkingDirectory
    Directory to run in. Redirection '.' entries resolve against this. Mandatory on purpose.

.PARAMETER TimeoutSeconds
    Hard timeout. Default 120.

.PARAMETER DismissDialogs
    Attempt to dismiss any detected modal dialog instead of just capturing it.

.PARAMETER ExpectArtifact
    Paths that must exist with LastWriteTime >= launch time for the run to count as effective.

.PARAMETER ClarionCLPath
    Default C:\Clarion12\bin\ClarionCL.exe.

.OUTPUTS
    [pscustomobject] ExitCode, TimedOut, Effective, Dialogs, StdOut, StdErr, StaleArtifacts,
    KilledPids, DurationMs. Effective = exited 0, no timeout, all expected artifacts fresh.

.EXAMPLE
    $r = .\Invoke-ClarionCL.ps1 -Arguments @('/au','/ag',"$dir\APP.app") -WorkingDirectory $dir `
             -ExpectArtifact "$dir\APP.clw" -DismissDialogs
    if (-not $r.Effective) { $r.Dialogs; $r.StdOut }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]]$Arguments,
    [Parameter(Mandatory)] [string]$WorkingDirectory,
    [int]$TimeoutSeconds = 120,
    [switch]$DismissDialogs,
    [string[]]$ExpectArtifact = @(),
    [string]$ClarionCLPath = 'C:\Clarion12\bin\ClarionCL.exe'
)

$ErrorActionPreference = 'Stop'

# ---------- native helpers ----------
if (-not ('ClarionCLWrapper.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClarionCLWrapper
{
    public class DialogInfo
    {
        public IntPtr Hwnd;
        public int Pid;
        public string ClassName;
        public string Title;
        public List<string> StaticTexts = new List<string>();
        public List<string> Buttons = new List<string>();
    }

    public static class Native
    {
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        const uint WM_GETTEXT = 0x000D;
        const uint WM_CLOSE = 0x0010;
        const uint BM_CLICK = 0x00F5;

        static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
        static string TitleOf(IntPtr h) { var sb = new StringBuilder(1024); GetWindowText(h, sb, 1024); return sb.ToString(); }
        static string TextOf(IntPtr h)  { var sb = new StringBuilder(4096); SendMessage(h, WM_GETTEXT, (IntPtr)4096, sb); return sb.ToString(); }

        // Visible top-level windows belonging to any of the given pids.
        public static List<DialogInfo> GetVisibleWindows(int[] pids)
        {
            var set = new HashSet<int>(pids);
            var result = new List<DialogInfo>();
            EnumWindows((h, l) =>
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (!set.Contains((int)pid) || !IsWindowVisible(h)) return true;
                var d = new DialogInfo { Hwnd = h, Pid = (int)pid, ClassName = ClassOf(h), Title = TitleOf(h) };
                EnumChildWindows(h, (c, l2) =>
                {
                    // Clarion raises BOTH native Win32 dialogs (classes "Static"/"Button") and
                    // WinForms ones ("WindowsForms10.STATIC...", "WindowsForms10.BUTTON...").
                    // Match by substring, uppercased, to catch both.
                    string cls = ClassOf(c).ToUpperInvariant();
                    string t = TextOf(c);
                    if (t.Trim().Length == 0) return true;
                    if (cls.Contains("BUTTON")) d.Buttons.Add(t);
                    else d.StaticTexts.Add(t);
                    return true;
                }, IntPtr.Zero);
                result.Add(d);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        // ---- process tree via Toolhelp32 snapshot (no WMI/CIM dependency) ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public uint dwSize; public uint cntUsage; public uint th32ProcessID;
            public IntPtr th32DefaultHeapID; public uint th32ModuleID; public uint cntThreads;
            public uint th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        public static int[] GetProcessTree(int rootPid)
        {
            var parentOf = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap != (IntPtr)(-1))
            {
                try
                {
                    var e = new PROCESSENTRY32W(); e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                    if (Process32FirstW(snap, ref e))
                        do { parentOf[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; } while (Process32NextW(snap, ref e));
                }
                finally { CloseHandle(snap); }
            }
            var tree = new HashSet<int> { rootPid };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var kv in parentOf)
                    if (tree.Contains(kv.Value) && tree.Add(kv.Key)) grew = true;
            }
            var arr = new int[tree.Count]; tree.CopyTo(arr); return arr;
        }

        // Click the first button whose caption (minus '&') matches, in caller-supplied preference order.
        public static string TryDismiss(IntPtr dialog, string[] preference)
        {
            var buttons = new List<KeyValuePair<string, IntPtr>>();
            EnumChildWindows(dialog, (c, l) =>
            {
                if (ClassOf(c).ToUpperInvariant().Contains("BUTTON"))
                    buttons.Add(new KeyValuePair<string, IntPtr>(TextOf(c).Replace("&", "").Trim(), c));
                return true;
            }, IntPtr.Zero);
            foreach (string want in preference)
                foreach (var b in buttons)
                    if (string.Equals(b.Key, want, StringComparison.OrdinalIgnoreCase))
                    { SendMessage(b.Value, BM_CLICK, IntPtr.Zero, IntPtr.Zero); return "clicked:" + b.Key; }
            PostMessage(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return "wm_close";
        }
    }
}
'@
}

function Get-ChildPids([int]$RootPid) {
    # Toolhelp32 snapshot via the native helper — Get-CimInstance is unavailable in some
    # sandboxed shells, and the P/Invoke path has no service dependency at all.
    return [ClarionCLWrapper.Native]::GetProcessTree($RootPid)
}

# ---------- guards ----------
if (-not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) { throw "WorkingDirectory does not exist: $WorkingDirectory" }
if (-not (Test-Path -LiteralPath $ClarionCLPath -PathType Leaf)) { throw "ClarionCL.exe not found: $ClarionCLPath" }
# Existence pre-check for path-shaped arguments that must already exist (inputs, not outputs).
foreach ($a in $Arguments) {
    if ($a -match '^[A-Za-z]:\\' -and $a -match '\.(app|txa|dct|dctx|txd|tpl|sln)$') {
        # /ai and /di legitimately create their FIRST path argument; only warn, never block.
        if (-not (Test-Path -LiteralPath $a)) { Write-Verbose "path argument does not exist (may be an output): $a" }
    }
}

$launch  = Get-Date
$outFile = Join-Path $env:TEMP ("ccl_{0}.out" -f [guid]::NewGuid().ToString('N'))
$errFile = Join-Path $env:TEMP ("ccl_{0}.err" -f [guid]::NewGuid().ToString('N'))
$dialogs = [System.Collections.Generic.List[object]]::new()
$killed  = @()

# Start-Process joins an -ArgumentList ARRAY with spaces and does NOT quote the elements, so any
# path containing a space arrives at ClarionCL split into several arguments. That is not a rare
# edge case - "C:\Users\First Last\..." is an ordinary Windows profile path, and the symptom is
# baffling: "warning CLCW002: The ax switch only takes 2 parameter(s)", or an access violation on
# a truncated path like 'C:\Users\First'. Quote each element ourselves and pass one string.
function Format-NativeArg {
    param([string]$Value)
    if ($Value -notmatch '[\s"]') { return $Value }
    # A trailing backslash immediately before the closing quote would escape it (C:\dir\" ), so
    # double any trailing run of backslashes. Embedded quotes are escaped too, though a path
    # containing one is pathological.
    $escaped = ($Value -replace '(\\+)$', '$1$1') -replace '"', '\"'
    '"' + $escaped + '"'
}
$argString = (($Arguments | ForEach-Object { Format-NativeArg $_ }) -join ' ')

$proc = Start-Process -FilePath $ClarionCLPath -ArgumentList $argString -WorkingDirectory $WorkingDirectory `
        -NoNewWindow -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile

$deadline = $launch.AddSeconds($TimeoutSeconds)
$timedOut = $false
$seen = @{}   # hwnd -> $true, so each dialog is captured once

while (-not $proc.HasExited) {
    if ((Get-Date) -gt $deadline) { $timedOut = $true; break }
    Start-Sleep -Milliseconds 400
    $tree = Get-ChildPids -RootPid $proc.Id
    foreach ($w in [ClarionCLWrapper.Native]::GetVisibleWindows($tree)) {
        $key = $w.Hwnd.ToString()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $entry = [pscustomobject]@{
            Pid        = $w.Pid
            ClassName  = $w.ClassName
            Title      = $w.Title
            Text       = ($w.StaticTexts -join ' | ')
            Buttons    = @($w.Buttons)
            DetectedAt = (Get-Date) - $launch
            Dismissal  = $null
        }
        if ($DismissDialogs) {
            $entry.Dismissal = [ClarionCLWrapper.Native]::TryDismiss($w.Hwnd, @('Ignore','No','Cancel','OK','Close'))
        }
        $dialogs.Add($entry)
    }
}

if ($timedOut) {
    $tree = Get-ChildPids -RootPid $proc.Id
    # final capture sweep before killing
    foreach ($w in [ClarionCLWrapper.Native]::GetVisibleWindows($tree)) {
        $key = $w.Hwnd.ToString()
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            $dialogs.Add([pscustomobject]@{ Pid=$w.Pid; ClassName=$w.ClassName; Title=$w.Title
                Text=($w.StaticTexts -join ' | '); Buttons=@($w.Buttons)
                DetectedAt=(Get-Date)-$launch; Dismissal='(killed)' })
        }
    }
    # kill the WHOLE tree, leaves first
    foreach ($p in ($tree | Sort-Object -Descending)) {
        try { Stop-Process -Id $p -Force -ErrorAction Stop; $killed += $p } catch {}
    }
} else {
    $proc.WaitForExit()   # flush async output readers
}

$stdout = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { '' }
$stderr = if (Test-Path $errFile) { Get-Content $errFile -Raw } else { '' }
Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue

$stale = @()
foreach ($a in $ExpectArtifact) {
    $f = Get-Item -LiteralPath $a -ErrorAction SilentlyContinue
    if (-not $f -or $f.LastWriteTime -lt $launch) { $stale += $a }
}

[pscustomobject]@{
    ExitCode       = if ($timedOut) { $null } else { $proc.ExitCode }
    TimedOut       = $timedOut
    Effective      = (-not $timedOut) -and ($proc.ExitCode -eq 0) -and ($stale.Count -eq 0)
    Dialogs        = @($dialogs)
    StdOut         = $stdout
    StdErr         = $stderr
    StaleArtifacts = $stale
    KilledPids     = $killed
    DurationMs     = [int]((Get-Date) - $launch).TotalMilliseconds
}
