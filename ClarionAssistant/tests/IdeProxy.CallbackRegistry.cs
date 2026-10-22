// IdeProxy.CallbackRegistry.cs - ticket b8f9431c review fixes, the IDE half of the forwarded tools, run on the REAL
// McpToolRegistry (referenced from the built clarion-mcp-server.exe, which links the same Services\McpToolRegistry.cs as
// the add-in) built exactly as IdeCallbackHost builds it (McpToolRegistry.BuildIdeCallbackRegistry), with recording
// fakes (RealProxy) for the editor, app tree, diff service, probe and workspace. No IDE needed.
//
//   H1  every path argument of a forwarded IdeOnly tool goes through the #266 path policy BEFORE any I/O:
//       a UNC path is refused at once (TEST-NET 192.0.2.1: a real SMB attempt would take seconds) and never reaches
//       the fake; a path outside the allowed roots and one under .ssh are refused; one inside the solution is let through
//       (export_txa, import_txa, open_file, export_dctx, import_dctx, show_diff x2, build_app x2, generate_source x2)
//   M1  execute_command / dump_object_api are not served; inspect_ide refuses probe_* and path: (the probe is never
//       called) and serves all_windows; ForwardedRefusal says the same for the chat pane's server
//   L1  generate_source accepts only on/off for conditional_generation / debug_generation
//   L3  the callback registry's open_procedure_embed reads a read-only InstanceCoordinationService: a peer IDE with the
//       procedure open is reported
//   L6  a build's timeout is capped at 3600 s in the IDE, and the forwarder waits that plus its margin
//
// Args: <work dir>. Exit 0 pass, 1 fail, 2 could-not-run.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using ClarionAssistant.Services;

static class IdeProxyCallbackRegistry
{
    static int _pass;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string name, string detail = null)
    {
        if (ok) { _pass++; Console.WriteLine("  ok   " + name); }
        else { Failures.Add(name); Console.WriteLine("  FAIL " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    // ---- a recording fake for any interface ----
    sealed class Fake : RealProxy
    {
        public readonly List<string> Calls = new List<string>();
        readonly Func<string, object[], Type, object> _answer;
        public Fake(Type t, Func<string, object[], Type, object> answer) : base(t) { _answer = answer; }

        public override IMessage Invoke(IMessage msg)
        {
            var call = (IMethodCallMessage)msg;
            var mi = (MethodInfo)call.MethodBase;
            lock (Calls) Calls.Add(mi.Name + "(" + string.Join(", ", call.InArgs.Select(a => a == null ? "null" : a.ToString())) + ")");
            object ret = _answer != null ? _answer(mi.Name, call.InArgs, mi.ReturnType) : null;
            if (ret == null && mi.ReturnType == typeof(string) && !mi.Name.StartsWith("get_")) ret = "FAKE " + mi.Name;
            if (ret == null && mi.ReturnType.IsValueType && mi.ReturnType != typeof(void)) ret = Activator.CreateInstance(mi.ReturnType);
            return new ReturnMessage(ret, null, 0, call.LogicalCallContext, call);
        }

        public bool Saw(string member) { lock (Calls) return Calls.Any(c => c.StartsWith(member + "(")); }
        public int Count { get { lock (Calls) return Calls.Count; } }
        public void Clear() { lock (Calls) Calls.Clear(); }
    }

    static T Make<T>(Fake f) { return (T)f.GetTransparentProxy(); }

    static string Run(McpToolRegistry reg, string tool, Dictionary<string, object> args)
    {
        try
        {
            object r = reg.ExecuteTool(tool, args);
            return r as string ?? (r == null ? "(null)" : new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(r));
        }
        catch (Exception ex) { return "THREW " + ex.GetType().Name + ": " + ex.Message; }
    }

    static Dictionary<string, object> A(params object[] kv)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }

    const string Policy = "refused by the Clarion Assistant path policy";

    static int Main(string[] argv)
    {
        if (argv.Length < 1) { Console.WriteLine("usage: <work dir>"); return 2; }
        string root = Path.GetFullPath(argv[0]);
        try
        {
            string sol = Path.Combine(root, "Sol");
            string outside = Path.Combine(root, "Outside");
            Directory.CreateDirectory(sol); Directory.CreateDirectory(outside);
            Directory.CreateDirectory(Path.Combine(sol, ".ssh"));
            Directory.CreateDirectory(Path.Combine(sol, "exports"));
            string sln = Path.Combine(sol, "Demo.sln"); File.WriteAllText(sln, "");
            string inTxa = Path.Combine(sol, "in.txa"); File.WriteAllText(inTxa, "x");
            string inClw = Path.Combine(sol, "Main.clw"); File.WriteAllText(inClw, "  MEMBER()\r\n");
            string inApp = Path.Combine(sol, "Demo.app"); File.WriteAllText(inApp, "x");
            string inProj = Path.Combine(sol, "Demo.cwproj"); File.WriteAllText(inProj, "<Project/>");
            string inDctx = Path.Combine(sol, "dict.dctx"); File.WriteAllText(inDctx, "x");
            string outFile = Path.Combine(outside, "x.txa"); File.WriteAllText(outFile, "x");
            string ssh = Path.Combine(sol, ".ssh", "id_rsa"); File.WriteAllText(ssh, "x");
            const string unc = @"\\192.0.2.1\share\x.txa";

            // ---- fakes ----
            string lastOpened = null;
            var editorF = new Fake(typeof(IEditorService), (n, a, t) =>
            {
                if (n == "OpenFileOnly" || n == "NavigateToFileAndLine") { lastOpened = (string)a[0]; return null; }
                if (n == "GetActiveDocumentPath") return lastOpened;
                if (n == "ActivateOpenFile") return true;
                return null;
            });
            var appF = new Fake(typeof(IAppTreeService), (n, a, t) =>
            {
                if (n == "GetAppInfo") return new Dictionary<string, object> { { "fileName", inApp } };
                if (n == "OpenProcedureEmbed") return "FAKE opened " + a[0];
                return null;
            });
            var probeF = new Fake(typeof(IIdeProbeService), null);
            var diffF = new Fake(typeof(IDiffService), null);
            var wsF = new Fake(typeof(IWorkspaceContext), (n, a, t) =>
            {
                if (n == "get_CurrentSolutionPath" || n == "GetHostOpenSolutionPath") return sln;
                return null;
            });
            var uiF = new Fake(typeof(IUiDispatcher), (n, a, t) =>
            {
                if (n == "get_HasUiThread") return true;
                if (n == "BeginInvokeOnUi") { ((Action)a[0])(); return null; }
                return null;
            });

            McpToolRegistry.AppTreeFactory = () => Make<IAppTreeService>(appF);
            McpToolRegistry.IdeProbeFactory = () => Make<IIdeProbeService>(probeF);

            // L3: a read-only coordination service on a temp database (the peer row is written at the L3 check).
            string coordDb = Path.Combine(root, "instances.db");
            var coord = new InstanceCoordinationService(coordDb);

#if OLD_API
            // Fail-on-old proof only (compiled with /define:OLD_API against c627adc's exe, which has no coordination parameter).
            var reg = McpToolRegistry.BuildIdeCallbackRegistry(Make<IEditorService>(editorF), new ClarionClassParser(),
                Make<IWorkspaceContext>(wsF), Make<IUiDispatcher>(uiF), Make<IDiffService>(diffF));
#else
            var reg = McpToolRegistry.BuildIdeCallbackRegistry(Make<IEditorService>(editorF), new ClarionClassParser(),
                Make<IWorkspaceContext>(wsF), Make<IUiDispatcher>(uiF), Make<IDiffService>(diffF), coord);
#endif

            // control: the policy really does see "outside" as outside and the solution as inside (else H1 proves nothing)
            var pol = McpPathGate.BuildPolicy(Make<IWorkspaceContext>(wsF));
            Check(!pol.Check(outFile, PathAccess.Read).Allowed && pol.Check(inTxa, PathAccess.Read).Allowed,
                "control: the policy built from the fake workspace allows the solution and refuses the outside folder");

            // ---- H1 ----
            Console.WriteLine("=== H1: path arguments of the forwarded IdeOnly tools ===");
            var cases = new[]
            {
                new { Tool = "export_txa", Arg = "path", Inside = Path.Combine(sol, "exports", "out.txa"), Down = appF, Member = "ExportTxa", Extra = A() },
                new { Tool = "import_txa", Arg = "path", Inside = inTxa, Down = appF, Member = "ImportTxa", Extra = A() },
                new { Tool = "open_file", Arg = "path", Inside = inClw, Down = editorF, Member = "OpenFileOnly", Extra = A() },
                new { Tool = "export_dctx", Arg = "path", Inside = Path.Combine(sol, "exports", "out.dctx"), Down = (Fake)null, Member = (string)null, Extra = A() },
                new { Tool = "import_dctx", Arg = "path", Inside = inDctx, Down = (Fake)null, Member = (string)null, Extra = A() },
                new { Tool = "show_diff", Arg = "original_file", Inside = inClw, Down = diffF, Member = "ShowDiffFromFile", Extra = A("title", "t", "modified_text", "x") },
                new { Tool = "show_diff", Arg = "modified_file", Inside = inClw, Down = diffF, Member = "ShowDiffFromFiles", Extra = A("title", "t", "original_file", inClw) },
                new { Tool = "build_app", Arg = "app_path", Inside = inApp, Down = (Fake)null, Member = (string)null, Extra = A() },
                new { Tool = "build_app", Arg = "project_path", Inside = inProj, Down = (Fake)null, Member = (string)null, Extra = A() },
                new { Tool = "generate_source", Arg = "app_path", Inside = inApp, Down = (Fake)null, Member = (string)null, Extra = A() },
                new { Tool = "generate_source", Arg = "project_path", Inside = inProj, Down = (Fake)null, Member = (string)null, Extra = A() },
            };
            foreach (var c in cases)
            {
                string label = c.Tool + "." + c.Arg;
                foreach (var bad in new[] { new { P = unc, Why = "a UNC path" }, new { P = outFile, Why = "a path outside the allowed roots" }, new { P = ssh, Why = "a path under .ssh" } })
                {
                    editorF.Clear(); appF.Clear(); diffF.Clear();
                    var args = new Dictionary<string, object>(c.Extra);
                    args[c.Arg] = bad.P;
                    var sw = Stopwatch.StartNew();
                    string r = Run(reg, c.Tool, args);
                    sw.Stop();
                    bool reached = c.Down != null && c.Down.Saw(c.Member);
                    bool fast = bad.P != unc || sw.ElapsedMilliseconds < 1500;
                    Check(r.Contains(Policy) && r.Contains(c.Tool) && !reached && fast,
                        label + ": " + bad.Why + " is refused by the policy" + (bad.P == unc ? " with no I/O (" + sw.ElapsedMilliseconds + " ms)" : "")
                        + (c.Down != null ? ", never reaching " + c.Member : ""),
                        Short(r) + (reached ? " [REACHED " + c.Member + "]" : ""));
                }
                {
                    editorF.Clear(); appF.Clear(); diffF.Clear();
                    var args = new Dictionary<string, object>(c.Extra);
                    args[c.Arg] = c.Inside;
                    string r = Run(reg, c.Tool, args);
                    bool reached = c.Down == null || c.Down.Saw(c.Member);
                    Check(!r.Contains(Policy) && reached, label + ": a path inside the solution is allowed" + (c.Down != null ? " and reaches " + c.Member : ""), Short(r));
                }
            }

            // ---- M1 ----
            Console.WriteLine("=== M1: what the call-back endpoint does not serve ===");
            var names = reg.GetToolDefinitions().Select(t => (string)t["name"]).ToList();
            Check(!names.Contains("execute_command") && !names.Contains("dump_object_api") && names.Contains("inspect_ide"),
                "execute_command and dump_object_api are not served by the call-back registry (inspect_ide is)");
            Check(!McpToolRegistry.IdeOnlyToolDefinitions().Any(d => d.Name == "execute_command" || d.Name == "dump_object_api"),
                "...and the standalone does not list or forward them");
            foreach (var cmd in new[] { "probe_popup_arm", "probe_popup_arm_inject", "probe_native_chain", "probe_mark:x", "path:ActiveWorkbenchWindow", "embed_details", "types", "assemblies" })
            {
                probeF.Clear();
                string r = Run(reg, "inspect_ide", A("command", cmd));
                Check(r.StartsWith("Error:") && r.Contains("not available to clients outside") && probeF.Count == 0,
                    "inspect_ide '" + cmd + "' is refused on the call-back registry and the probe is never called", Short(r));
            }
            probeF.Clear();
            string allWin = Run(reg, "inspect_ide", A("command", "all_windows"));
            Check(probeF.Saw("ListAllWindows") && !allWin.StartsWith("Error"), "inspect_ide 'all_windows' is still served", Short(allWin));
#if OLD_API
            Check(false, "ForwardedRefusal (the chat pane server's filter for forwarded calls) refuses the same and nothing else", "no ForwardedRefusal");
#else
            Check(McpToolRegistry.ForwardedRefusal("execute_command", A()) != null && McpToolRegistry.ForwardedRefusal("dump_object_api", A()) != null
                  && McpToolRegistry.ForwardedRefusal("inspect_ide", A("command", "probe_popup_arm")) != null
                  && McpToolRegistry.ForwardedRefusal("inspect_ide", A("command", "editor_text")) == null
                  && McpToolRegistry.ForwardedRefusal("get_active_file", A()) == null,
                "ForwardedRefusal (the chat pane server's filter for forwarded calls) refuses the same and nothing else");
#endif

            // ---- L1 ----
            Console.WriteLine("=== L1: on/off only ===");
            foreach (var bad in new[] { "on /ag \"C:\\other.app\"", "yes", "on\"" })
            {
                string r = Run(reg, "generate_source", A("app_path", inApp, "conditional_generation", bad));
                Check(r.Contains("conditional_generation must be 'on' or 'off'"), "generate_source refuses conditional_generation '" + bad + "'", Short(r));
                r = Run(reg, "generate_source", A("app_path", inApp, "debug_generation", bad));
                Check(r.Contains("debug_generation must be 'on' or 'off'"), "generate_source refuses debug_generation '" + bad + "'", Short(r));
            }
            string onOk = Run(reg, "generate_source", A("app_path", inApp, "conditional_generation", "ON", "debug_generation", "off"));
            Check(!onOk.Contains("must be 'on' or 'off'"), "generate_source accepts ON / off (any case)", Short(onOk));

            // ---- L3 ----
            Console.WriteLine("=== L3: open_procedure_embed's conflict check on the call-back registry ===");
            // The peer row is written HERE, not at setup: its heartbeat must be fresh (a slow H1 run - the old code's
            // 42 s UNC attempt - would otherwise age it past InstanceCoordinationService.StaleSeconds).
            using (var conn = new System.Data.SQLite.SQLiteConnection("Data Source=" + coordDb))
            {
                conn.Open();
                using (var cmd = new System.Data.SQLite.SQLiteCommand(
                    "INSERT INTO instances (pid, app_file, active_procedure, heartbeat_at) VALUES (999999, @a, 'BrowseCustomers', datetime('now'))", conn))
                { cmd.Parameters.AddWithValue("@a", inApp); cmd.ExecuteNonQuery(); }
            }
            string opened = Run(reg, "open_procedure_embed", A("procedure_name", "BrowseCustomers"));
            Check(opened.Contains("Another IDE instance (PID 999999)") && opened.Contains("FAKE opened BrowseCustomers"),
                "a peer IDE that has the procedure open is reported (the registry has a read-only InstanceCoordinationService)", Short(opened));

            // ---- L6 ----
            Console.WriteLine("=== L6: budgets ===");
            var cap = typeof(McpToolRegistry).GetMethod("CapBuildTimeout", BindingFlags.NonPublic | BindingFlags.Static);
            Check(cap != null && (int)cap.Invoke(null, new object[] { 99999 }) == 3600 && (int)cap.Invoke(null, new object[] { 0 }) == 1
                  && (int)cap.Invoke(null, new object[] { 120 }) == 120, "the IDE caps a build's timeout at 3600 s (and at least 1)");
            var buildDef = McpToolRegistry.IdeOnlyToolDefinitions().First(d => d.Name == "build_app");
            int budget = IdeToolForwarder.BudgetMs(buildDef, A("timeout", 99999), null);
            Check(IdeToolForwarder.MaxSeconds == 3600 + IdeToolForwarder.MarginSeconds && budget == (3600 + IdeToolForwarder.MarginSeconds) * 1000,
                "the forwarder waits for the IDE's longest run plus its margin (" + budget + " ms), so it never gives up first");
        }
        catch (Exception ex)
        {
            Console.WriteLine("COULD NOT RUN: " + ex);
            return 2;
        }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _pass + " checks"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + (_pass + Failures.Count) + " checks");
        return 1;
    }

    static string Short(string s)
    {
        if (s == null) return "(null)";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 220 ? s.Substring(0, 220) + "..." : s;
    }
}
