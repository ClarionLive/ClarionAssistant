using System;
using System.Collections.Generic;
using System.Threading;

// Stand-in for McpToolRegistry.cs so the REAL McpDispatcher.cs, McpJsonRpc.cs, IUiDispatcher.cs and
// McpUiTimeoutPolicy.cs compile standalone. The real registry drags in every IDE service; the dispatcher
// only needs the six members below. Each fake tool sleeps for a set time on the "UI thread" and declares
// its own UiTimeoutSeconds, which is exactly the surface under test (PR #198).
namespace ClarionAssistant.Services
{
    public class McpToolRegistry
    {
        private readonly Dictionary<string, KeyValuePair<int, int>> _tools =
            new Dictionary<string, KeyValuePair<int, int>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Register a UI-thread tool that sleeps sleepMs and declares declaredSeconds (0 = none).</summary>
        public void Add(string name, int sleepMs, int declaredSeconds)
        {
            _tools[name] = new KeyValuePair<int, int>(sleepMs, declaredSeconds);
        }

        public bool RequiresUiThread(string toolName) { return _tools.ContainsKey(toolName); }

        public int UiTimeoutSeconds(string toolName)
        {
            KeyValuePair<int, int> t;
            return _tools.TryGetValue(toolName, out t) ? t.Value : 0;
        }

        public object ExecuteTool(string name, Dictionary<string, object> arguments)
        {
            KeyValuePair<int, int> t;
            if (!_tools.TryGetValue(name, out t)) throw new InvalidOperationException("unknown tool " + name);
            Thread.Sleep(t.Key);
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

    /// <summary>A "UI thread" that is a fresh background thread per call - enough to exercise the wait.</summary>
    public sealed class ThreadUiDispatcher : IUiDispatcher
    {
        public bool HasUiThread { get { return true; } }

        public void BeginInvokeOnUi(Action action)
        {
            var t = new Thread(() => action()) { IsBackground = true };
            t.Start();
        }
    }
}
