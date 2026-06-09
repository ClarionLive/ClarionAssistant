using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
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

    /// <summary>
    /// Non-invasive driver for the standalone x86 debug engine (ClarionDbg.exe). Launches it with
    /// --json, streams its output, and raises events for hits and log lines. Runs the engine in a
    /// separate process so a debugger fault can never destabilize the IDE.
    /// </summary>
    public sealed class ClarionDebuggerService
    {
        private Process _proc;

        public event Action<DebugHit> HitReceived;
        public event Action<string> LogReceived;
        public event Action<int> Exited;

        public bool IsRunning { get { return _proc != null && !_proc.HasExited; } }

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

        /// <summary>
        /// Start a debug session: launch <paramref name="targetExe"/> under the engine and break at
        /// <paramref name="module"/> line <paramref name="line"/>.
        /// </summary>
        public void Start(string targetExe, string module, int line, bool once)
        {
            if (IsRunning) throw new InvalidOperationException("A debug session is already running.");

            string engine = FindEngine();
            if (engine == null) throw new FileNotFoundException("ClarionDbg.exe not found next to the addin or in the dev build output.");
            if (string.IsNullOrEmpty(targetExe) || !File.Exists(targetExe))
                throw new FileNotFoundException("Target executable not found: " + targetExe);

            string args = "break \"" + targetExe + "\" --line " + line + " --module " + module + " --json --timeout 60000";
            if (once) args += " --once";

            var psi = new ProcessStartInfo(engine, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(targetExe)
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) LogReceived?.Invoke(e.Data); };
            _proc.Exited += (s, e) =>
            {
                int code = 0;
                try { code = _proc.ExitCode; } catch { }
                Exited?.Invoke(code);
            };

            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        public void Stop()
        {
            try { if (IsRunning) _proc.Kill(); } catch { }
        }

        /// <summary>
        /// Synchronously ask the engine for a module's breakable lines (lines that carry a code
        /// record — the only lines a breakpoint binds to exactly). Clarion's TSWD line table is
        /// sparse, so this lets the pad show the user which lines actually work. Returns an empty
        /// array on any failure.
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

        private void OnLine(string line)
        {
            if (line.StartsWith("@JSON "))
            {
                var hit = ParseHit(line.Substring(6));
                if (hit != null)
                {
                    hit.ResolvedPath = ResolveModulePath(hit.Module);
                    HitReceived?.Invoke(hit);
                }
            }
            else
            {
                LogReceived?.Invoke(line);
            }
        }

        private static string ResolveModulePath(string module)
        {
            if (string.IsNullOrEmpty(module)) return null;
            try
            {
                var red = RedFileService.Active;
                if (red == null) return null;
                // Generated source lives in the redirected source dir; try the debug config first.
                return red.Resolve(module, "Debug32", "Common")
                    ?? red.Resolve(module, "Common");
            }
            catch { return null; }
        }

        // The hit JSON has a fixed shape; extract fields directly rather than pulling in a JSON dep.
        private static DebugHit ParseHit(string json)
        {
            try
            {
                var hit = new DebugHit
                {
                    Resolved = GetBool(json, "resolved"),
                    Module = GetStr(json, "module"),
                    Line = GetInt(json, "line"),
                    Rva = GetStr(json, "rva"),
                    Va = GetStr(json, "va"),
                    Gap = GetInt(json, "gap"),
                    Exact = GetBool(json, "exact"),
                };
                return hit;
            }
            catch { return null; }
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
