using System;
using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #205 pipeline run 1: which of an IDE's running MCP servers publishes the IDE's ONE endpoint record
    /// (IdeEndpointRecord). The chat pane's server and the always-on IdeCallbackHost both serve the standalone server's
    /// call-backs; publishing both would leave two records for one IDE, and publishing only the chat pane's left none
    /// without a chat pane.
    ///
    /// Rules: the first server to join publishes; later ones stand by. When the publisher leaves, its record is withdrawn
    /// and the record moves to the first standing-by server that is still running. A stand-by leaving changes nothing.
    /// Pure and thread-safe; the actual publish/withdraw are delegates (McpServer passes IdeEndpointRecord's).
    /// </summary>
    public sealed class EndpointOwnership<T> where T : class
    {
        private readonly object _lock = new object();
        private readonly List<T> _live = new List<T>();
        private T _owner;

        public T Owner { get { lock (_lock) return _owner; } }

        /// <summary><paramref name="server"/> started. Publishes it when nobody running holds the record.</summary>
        public void Join(T server, Func<T, bool> isRunning, Action<T> publish)
        {
            lock (_lock)
            {
                if (!_live.Contains(server)) _live.Add(server);
                if (_owner != null && isRunning(_owner)) return;
                _owner = server;
                publish(server);
            }
        }

        /// <summary><paramref name="server"/> is stopping. If it held the record: withdraw it, hand it on.</summary>
        public void Leave(T server, Func<T, bool> isRunning, Action<T> publish, Action<T> withdraw)
        {
            lock (_lock)
            {
                _live.Remove(server);
                if (_owner != server) return;
                withdraw(server);
                _owner = null;
                foreach (var s in _live)
                {
                    if (!isRunning(s)) continue;
                    _owner = s;
                    publish(s);
                    break;
                }
            }
        }
    }
}
