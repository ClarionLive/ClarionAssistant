using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>Debug session run-state (Phase 2 interactive engine).</summary>
    public enum DebugSessionState
    {
        Idle,       // no engine process
        Launching,  // engine started, target not yet loaded
        Running,    // target executing
        Paused      // target suspended at a breakpoint / step
    }

    /// <summary>A breakpoint hit reported by the ClarionDbg helper engine.</summary>
    public sealed class DebugHit
    {
        public bool Resolved;
        public string Module;
        public int Line;
        public string Rva;
        public string Va;
        public int Gap;
        public bool Exact;

        /// <summary>Full path to the source module, resolved via the active .red redirection (or null).</summary>
        public string ResolvedPath;
    }

    /// <summary>The target paused (breakpoint hit, step complete, or step-limit).</summary>
    public sealed class DebugPause
    {
        public string Reason;       // "breakpoint" | "step" | "step-limit"
        public bool Resolved;
        public string Module;
        public int Line;
        public string Rva;
        public string Va;
        public int Gap;
        public bool Exact;
        /// <summary>x86 registers as hex strings keyed by name (eax..eflags), or null.</summary>
        public Dictionary<string, string> Regs;
        /// <summary>Full path to the source module, resolved via the active .red redirection (or null).</summary>
        public string ResolvedPath;
    }

    /// <summary>One logical breakpoint as confirmed by the engine (bp-set / bp-list).</summary>
    public sealed class DebugBreakpoint
    {
        public string Module;
        public int RequestedLine;
        public int Line;            // line actually planted (snapped to nearest code record)
    }

    /// <summary>
    /// Non-invasive driver for the standalone x86 debug engine (ClarionDbg.exe). Launches it with
    /// --interactive --json, streams its @JSON events, raises typed events, and forwards commands
    /// (continue / step / breakpoints / memory reads) over the engine's stdin. Runs the engine in a
    /// separate process so a debugger fault can never destabilize the IDE.
    /// </summary>
    public sealed class ClarionDebuggerService
    {
        private Process _proc;
        private string _targetDir; // target EXE's directory — anchors relative .red redirection paths
        private readonly object _stateLock = new object();
        private DebugSessionState _state = DebugSessionState.Idle;
        private readonly List<DebugBreakpoint> _breakpoints = new List<DebugBreakpoint>();

        // ---- events (raised on a threadpool thread — marshal to UI in handlers) ----
        public event Action<DebugSessionState> StateChanged;
        public event Action<DebugHit> HitReceived;
        public event Action<DebugPause> Paused;
        public event Action<string> Resumed;                       // resume mode: continue/step/stepover/stepout
        public event Action<DebugBreakpoint> BreakpointSet;
        public event Action<string, int> BreakpointRemoved;        // module, line
        public event Action<string, int, string> BreakpointError;  // module, line, error
        public event Action<List<DebugBreakpoint>> BreakpointListReceived;
        public event Action<uint, int, byte[]> MemoryReceived;     // addr, requested len, bytes
        public event Action<string> EngineError;                   // engine-reported error event
        public event Action<string> LogReceived;
        public event Action<int> Exited;

        public bool IsRunning { get { return _proc != null && !_proc.HasExited; } }

        public DebugSessionState State
        {
            get { lock (_stateLock) return _state; }
        }

        /// <summary>Engine-confirmed breakpoints (snapshot).</summary>
        public DebugBreakpoint[] Breakpoints
        {
            get { lock (_breakpoints) return _breakpoints.ToArray(); }
        }

        /// <summary>Locate ClarionDbg.exe: next to this addin first, then a dev build fallback.</summary>
        public static string FindEngine()
        {
            try
            {
                string addinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string local = Path.Combine(addinDir, "ClarionDbg.exe");
                if (File.Exists(local)) return local;
            }
            catch { }

            string dev = @"H:\DevLaptop\Projects\ClarionDebugger\src\ClarionDbg.Cli\bin\Debug\net48\ClarionDbg.exe";
            return File.Exists(dev) ? dev : null;
        }

        // ------------------------------------------------------------------ session lifecycle

        /// <summary>
        /// Start an interactive debug session: launch <paramref name="targetExe"/> under the engine
        /// with zero or more module:line breakpoints. The engine pauses at each hit and waits for
        /// Continue/Step*/Quit. Breakpoints can be added/removed at any time via Add/RemoveBreakpoint.
        /// </summary>
        public void StartSession(string targetExe, IEnumerable<DebugBreakpoint> breakpoints)
        {
            var args = new System.Text.StringBuilder();
            args.Append("break \"").Append(targetExe).Append("\" --interactive --json");
            if (breakpoints != null)
                foreach (var bp in breakpoints)
                {
                    if (!IsValidModuleName(bp.Module)) continue; // blocks argument smuggling via module text
                    args.Append(" --bp ").Append(bp.Module).Append(':').Append(bp.RequestedLine > 0 ? bp.RequestedLine : bp.Line);
                }
            Launch(targetExe, args.ToString(), true);
        }

        /// <summary>
        /// Legacy one-shot session (Phase 1e pad): single module:line breakpoint, no interactivity.
        /// </summary>
        public void Start(string targetExe, string module, int line, bool once)
        {
            string args = "break \"" + targetExe + "\" --line " + line + " --module " + module + " --json --timeout 60000";
            if (once) args += " --once";
            Launch(targetExe, args, false);
        }

        private void Launch(string targetExe, string args, bool interactive)
        {
            if (IsRunning) throw new InvalidOperationException("A debug session is already running.");

            string engine = FindEngine();
            if (engine == null) throw new FileNotFoundException("ClarionDbg.exe not found next to the addin or in the dev build output.");
            if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe))
                throw new FileNotFoundException("Target executable not found: " + targetExe);

            lock (_breakpoints) _breakpoints.Clear();
            string newTargetDir = Path.GetDirectoryName(Path.GetFullPath(targetExe));
            if (!string.Equals(newTargetDir, _targetDir, StringComparison.OrdinalIgnoreCase))
                _redFallback = null; // different target → its local .red may differ; re-resolve lazily
            _targetDir = newTargetDir;

            var psi = new ProcessStartInfo(engine, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = interactive,
                WorkingDirectory = Path.GetDirectoryName(targetExe)
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) LogReceived?.Invoke(e.Data); };
            _proc.Exited += (s, e) =>
            {
                int code = 0;
                try { code = _proc.ExitCode; } catch { }
                SetState(DebugSessionState.Idle);
                Exited?.Invoke(code);
            };

            SetState(DebugSessionState.Launching);
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        public void Stop()
        {
            // prefer a clean engine-side teardown (kills the target too); a successful pipe write
            // does NOT prove the engine consumed the command, so always verify exit and fall back
            try
            {
                if (SendCommand("quit") && _proc.WaitForExit(1500)) return;
                if (IsRunning) _proc.Kill();
            }
            catch { }
        }

        // ------------------------------------------------------------------ execution control

        public bool Continue() { return SendCommand("continue"); }
        public bool StepInto() { return SendCommand("step"); }
        public bool StepOver() { return SendCommand("stepover"); }
        public bool StepOut() { return SendCommand("stepout"); }

        /// <summary>Valid Clarion module file name (e.g. clbrws011.clw) — also blocks argument
        /// smuggling and command injection through the engine command line / stdin protocol.</summary>
        public static bool IsValidModuleName(string module)
        {
            return !string.IsNullOrEmpty(module)
                && Regex.IsMatch(module, @"^[A-Za-z0-9_.\-]+$")
                && !module.Contains("..");
        }

        /// <summary>Add a breakpoint (engine snaps to the nearest code-record line and replies bp-set).</summary>
        public bool AddBreakpoint(string module, int line)
        {
            return IsValidModuleName(module) && SendCommand("bp add " + module + ":" + line);
        }

        /// <summary>Remove a breakpoint by module:line (planted or requested line both match).</summary>
        public bool RemoveBreakpoint(string module, int line)
        {
            return IsValidModuleName(module) && SendCommand("bp del " + module + ":" + line);
        }

        public bool RequestBreakpointList() { return SendCommand("bp list"); }

        /// <summary>Read target memory while paused; result arrives via MemoryReceived.</summary>
        public bool ReadMemory(uint address, int length)
        {
            return SendCommand("mem 0x" + address.ToString("X") + " " + length);
        }

        /// <summary>Send a raw command line to the engine's stdin. False if no session / stdin closed.
        /// Rejects embedded newlines — the protocol is line-oriented, so a \n would inject a second command.</summary>
        public bool SendCommand(string command)
        {
            try
            {
                if (command == null || command.IndexOf('\n') >= 0 || command.IndexOf('\r') >= 0) return false;
                if (!IsRunning || !_proc.StartInfo.RedirectStandardInput) return false;
                _proc.StandardInput.WriteLine(command);
                _proc.StandardInput.Flush();
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ breakable lines (static query)

        /// <summary>
        /// Synchronously ask the engine for a module's breakable lines (lines that carry a code
        /// record — the only lines a breakpoint binds to exactly). Clarion's TSWD line table is
        /// sparse, so this lets the UI show which lines actually work. Empty array on any failure.
        /// </summary>
        public static int[] GetBreakableLines(string targetExe, string module)
        {
            try
            {
                string engine = FindEngine();
                if (engine == null || string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe) || string.IsNullOrEmpty(module))
                    return new int[0];

                string args = "lines \"" + targetExe + "\" --module " + module + " --json";
                var psi = new ProcessStartInfo(engine, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(targetExe)
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return ParseLinesJson(outp);
                }
            }
            catch { return new int[0]; }
        }

        // Pull the integer array out of the engine's "@LINES {...,"lines":[...]}" output.
        private static int[] ParseLinesJson(string stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return new int[0];
            int at = stdout.IndexOf("@LINES", StringComparison.Ordinal);
            if (at < 0) return new int[0];
            int lb = stdout.IndexOf('[', at);
            int rb = lb >= 0 ? stdout.IndexOf(']', lb) : -1;
            if (lb < 0 || rb < 0) return new int[0];
            string body = stdout.Substring(lb + 1, rb - lb - 1).Trim();
            if (body.Length == 0) return new int[0];
            var list = new List<int>();
            foreach (var s in body.Split(','))
            {
                int v;
                if (int.TryParse(s.Trim(), out v)) list.Add(v);
            }
            return list.ToArray();
        }

        // ------------------------------------------------------------------ event stream parsing

        private void OnLine(string line)
        {
            if (!line.StartsWith("@JSON ", StringComparison.Ordinal))
            {
                LogReceived?.Invoke(line);
                return;
            }

            string json = line.Substring(6);
            string evt = GetStr(json, "event");
            switch (evt)
            {
                case "loaded":
                    SetState(DebugSessionState.Running);
                    break;

                case "hit":
                    var hit = ParseHit(json);
                    if (hit != null)
                    {
                        hit.ResolvedPath = ResolveModulePath(hit.Module);
                        HitReceived?.Invoke(hit);
                    }
                    break;

                case "paused":
                    var pause = ParsePause(json);
                    if (pause != null)
                    {
                        pause.ResolvedPath = ResolveModulePath(pause.Module);
                        SetState(DebugSessionState.Paused);
                        Paused?.Invoke(pause);
                    }
                    break;

                case "resumed":
                    SetState(DebugSessionState.Running);
                    Resumed?.Invoke(GetStr(json, "mode"));
                    break;

                case "bp-set":
                    var bp = new DebugBreakpoint
                    {
                        Module = GetStr(json, "module"),
                        RequestedLine = GetInt(json, "requestedLine"),
                        Line = GetInt(json, "line")
                    };
                    lock (_breakpoints)
                    {
                        bool known = false;
                        foreach (var b in _breakpoints)
                            if (b.Module == bp.Module && b.Line == bp.Line) { known = true; break; }
                        if (!known) _breakpoints.Add(bp);
                    }
                    BreakpointSet?.Invoke(bp);
                    break;

                case "bp-del":
                    string delMod = GetStr(json, "module");
                    int delLine = GetInt(json, "line");
                    lock (_breakpoints)
                        _breakpoints.RemoveAll(b => b.Module == delMod && b.Line == delLine);
                    BreakpointRemoved?.Invoke(delMod, delLine);
                    break;

                case "bp-error":
                    BreakpointError?.Invoke(GetStr(json, "module"), GetInt(json, "line"), GetStr(json, "error"));
                    break;

                case "bp-list":
                    var list = ParseBpList(json);
                    lock (_breakpoints)
                    {
                        _breakpoints.Clear();
                        _breakpoints.AddRange(list);
                    }
                    BreakpointListReceived?.Invoke(list);
                    break;

                case "mem":
                    uint addr = ParseHexU32(GetStr(json, "addr"));
                    int memLen = GetInt(json, "len");
                    byte[] bytes = ParseHexBytes(GetStr(json, "bytes"));
                    if (bytes != null) MemoryReceived?.Invoke(addr, memLen, bytes);
                    break;

                case "regs":
                    // standalone regs reply — surface as a log line for now (paused carries regs too)
                    LogReceived?.Invoke(line);
                    break;

                case "error":
                    EngineError?.Invoke(GetStr(json, "message"));
                    break;

                case "exited":
                    SetState(DebugSessionState.Idle);
                    break;

                default:
                    LogReceived?.Invoke(line);
                    break;
            }
        }

        private void SetState(DebugSessionState s)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state != s;
                _state = s;
            }
            if (changed) StateChanged?.Invoke(s);
        }

        private RedFileService _redFallback;

        /// <summary>
        /// The effective .red service. RedFileService.Active is only populated when the chat
        /// assistant pane initializes — a session that only uses the debugger pad would otherwise
        /// have NO redirection loaded and every module→source resolution would fail. Self-load the
        /// effective .red for the target (local project .red supersedes the version-level one).
        /// </summary>
        private RedFileService GetRedService()
        {
            var red = RedFileService.Active;
            if (red != null) return red;
            if (_redFallback != null) return _redFallback;
            try
            {
                var info = ClarionVersionService.Detect();
                var cfg = info != null ? info.GetCurrentConfig() : null;
                if (cfg == null) return null;
                var svc = new RedFileService();
                if (svc.LoadForProject(_targetDir, cfg)) _redFallback = svc;
                return _redFallback;
            }
            catch { return null; }
        }

        private string ResolveModulePath(string module)
        {
            if (string.IsNullOrEmpty(module)) return null;
            // Module names come from the debuggee's TSWD debug info — UNTRUSTED when debugging a
            // hostile EXE. A name carrying path separators or ".." would traverse out of the .red
            // redirection dir (Path.Combine + GetFullPath normalize it) and open an arbitrary file.
            if (module != Path.GetFileName(module) || module.Contains("..")) return null;
            try
            {
                var red = GetRedService();
                if (red == null) return null;
                // Relative .red entries (".", "..\obj", …) are relative to the app, not the IDE's
                // CWD — anchor them to the target EXE's directory and search the same section list
                // the app-data reader uses (ClarionAppDataReader: ResolveFrom with baseDir).
                return red.ResolveFrom(module, _targetDir, "Debug32", "Release32", "Debug", "Release", "Common")
                    ?? red.ResolveFrom(module, _targetDir, "Common")
                    ?? (_targetDir != null && File.Exists(Path.Combine(_targetDir, module))
                        ? Path.Combine(_targetDir, module) : null); // generated source often sits next to the EXE
            }
            catch { return null; }
        }

        // The event JSON has a fixed shape; extract fields directly rather than pulling in a JSON dep.
        private static DebugHit ParseHit(string json)
        {
            try
            {
                return new DebugHit
                {
                    Resolved = GetBool(json, "resolved"),
                    Module = GetStr(json, "module"),
                    Line = GetInt(json, "line"),
                    Rva = GetStr(json, "rva"),
                    Va = GetStr(json, "va"),
                    Gap = GetInt(json, "gap"),
                    Exact = GetBool(json, "exact"),
                };
            }
            catch { return null; }
        }

        private static DebugPause ParsePause(string json)
        {
            try
            {
                var p = new DebugPause
                {
                    Reason = GetStr(json, "reason"),
                    Resolved = GetBool(json, "resolved"),
                    Module = GetStr(json, "module"),
                    Line = GetInt(json, "line"),
                    Rva = GetStr(json, "rva"),
                    Va = GetStr(json, "va"),
                    Gap = GetInt(json, "gap"),
                    Exact = GetBool(json, "exact"),
                };
                // regs block: {"eax":"0x...",...} — flat unique keys, extract directly
                if (json.Contains("\"regs\":{"))
                {
                    p.Regs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var reg in new[] { "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip", "eflags" })
                    {
                        string v = GetStr(json, reg);
                        if (v != null) p.Regs[reg] = v;
                    }
                }
                return p;
            }
            catch { return null; }
        }

        private static List<DebugBreakpoint> ParseBpList(string json)
        {
            var list = new List<DebugBreakpoint>();
            try
            {
                foreach (Match m in Regex.Matches(json, "\\{\"module\":\"([^\"]*)\",\"line\":(\\d+),\"requestedLine\":(\\d+)\\}"))
                {
                    list.Add(new DebugBreakpoint
                    {
                        Module = m.Groups[1].Value,
                        Line = int.Parse(m.Groups[2].Value),
                        RequestedLine = int.Parse(m.Groups[3].Value)
                    });
                }
            }
            catch { }
            return list;
        }

        private static uint ParseHexU32(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            uint v;
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static byte[] ParseHexBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return null;
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexVal(hex[i * 2]), lo = HexVal(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return null;
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return bytes;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static string GetStr(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        private static int GetInt(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        private static bool GetBool(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
            return m.Success && m.Groups[1].Value == "true";
        }
    }
}
