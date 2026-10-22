using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

// Stand-in for McpToolRegistry.cs so the REAL McpDispatcher.cs, McpJsonRpc.cs, IUiDispatcher.cs,
// McpUiTimeoutPolicy.cs and McpCallContext.cs compile standalone. The real registry drags in every IDE
// service; the dispatcher only needs the six members below. Each fake tool runs on the "UI thread",
// declares its own UiTimeoutSeconds and may claim a commit point - the surface under test (PR #198).
namespace ClarionAssistant.Services
{
    public enum FakeToolMode
    {
        Plain,          // sleep, return
        CommitAtEnd,    // sleep, then McpCallContext.TryCommit() - like apply_embed_edits reaching its save
        CommitAtStart   // TryCommit() after 1s, then sleep the rest - already saving when the wait ends
    }

    public class McpToolRegistry
    {
        private sealed class Fake { public int SleepMs; public int Declared; public FakeToolMode Mode; public bool Worker; }

        private readonly Dictionary<string, Fake> _tools = new Dictionary<string, Fake>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many times each tool actually ran, and what its TryCommit returned.</summary>
        public static readonly ConcurrentDictionary<string, int> Executions = new ConcurrentDictionary<string, int>();
        public static readonly ConcurrentDictionary<string, bool> CommitResults = new ConcurrentDictionary<string, bool>();

        /// <summary>b8f9431c M3: how many fake tools run at this moment, and the most that ever ran at once.</summary>
        public static int Running, MaxRunning;
        /// <summary>Completion order ("name" per finished call).</summary>
        public static readonly ConcurrentQueue<string> Finished = new ConcurrentQueue<string>();

        public static void ResetConcurrency() { Running = 0; MaxRunning = 0; string x; while (Finished.TryDequeue(out x)) { } }

        public void Add(string name, int sleepMs, int declaredSeconds, FakeToolMode mode = FakeToolMode.Plain)
        {
            _tools[name] = new Fake { SleepMs = sleepMs, Declared = declaredSeconds, Mode = mode };
        }

        /// <summary>b8f9431c M2: a tool that runs OFF the UI thread (RequiresUiThread false), like open_file.</summary>
        public void AddWorker(string name, int sleepMs)
        {
            _tools[name] = new Fake { SleepMs = sleepMs, Worker = true };
        }

        public bool RequiresUiThread(string toolName) { Fake t; return _tools.TryGetValue(toolName, out t) && !t.Worker; }

        public int UiTimeoutSeconds(string toolName)
        {
            Fake t;
            return _tools.TryGetValue(toolName, out t) ? t.Declared : 0;
        }

        public object ExecuteTool(string name, Dictionary<string, object> arguments)
        {
            Fake t;
            if (!_tools.TryGetValue(name, out t)) throw new InvalidOperationException("unknown tool " + name);
            Executions.AddOrUpdate(name, 1, (k, v) => v + 1);
            int now = Interlocked.Increment(ref Running);
            int seen;
            while ((seen = MaxRunning) < now && Interlocked.CompareExchange(ref MaxRunning, now, seen) != seen) { }
            try
            {
            switch (t.Mode)
            {
                case FakeToolMode.CommitAtEnd:
                    Thread.Sleep(t.SleepMs);
                    CommitResults[name] = McpCallContext.TryCommit();
                    break;
                case FakeToolMode.CommitAtStart:
                    Thread.Sleep(1000);
                    CommitResults[name] = McpCallContext.TryCommit();
                    Thread.Sleep(Math.Max(0, t.SleepMs - 1000));
                    break;
                default:
                    Thread.Sleep(t.SleepMs);
                    break;
            }
            }
            finally { Interlocked.Decrement(ref Running); Finished.Enqueue(name); }
            return "done:" + name;
        }

        public bool SupportsStreaming(string toolName) { return false; }

        public object ExecuteToolStreaming(string name, Dictionary<string, object> arguments,
            Action<double, string> progress)
        {
            throw new NotSupportedException();
        }

        public List<Dictionary<string, object>> GetToolDefinitions() { return new List<Dictionary<string, object>>(); }
    }

    /// <summary>A "UI thread" that is a fresh background thread per call, optionally busy for StartDelayMs first.</summary>
    public sealed class ThreadUiDispatcher : IUiDispatcher
    {
        public int StartDelayMs;

        public bool HasUiThread { get { return true; } }

        public void BeginInvokeOnUi(Action action)
        {
            int delay = StartDelayMs;
            var t = new Thread(() => { if (delay > 0) Thread.Sleep(delay); action(); }) { IsBackground = true, Name = "fake-ui" };
            t.Start();
        }
    }
}
