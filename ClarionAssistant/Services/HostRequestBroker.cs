using System;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// fc420c30: host → Monaco page requests that wait for the page's answer. Monaco's own requests run the other way
    /// (requestFromHost/reqId); this is the mirror image, so the host can ask the page and use the reply.
    ///
    /// Wire shape: host posts {type:"hostRequest", reqId, action, args}; the page answers
    /// {action:"hostReply", reqId, ok, data | error}. The page's reply is delivered on the UI THREAD, so a request
    /// must NEVER be waited on from the UI thread: it would block the very thread the answer needs. Request refuses that
    /// with InvalidOperationException (IsUiThread, supplied by the owner). IDE-free; MonacoEditorControl owns the
    /// transport.
    /// </summary>
    public sealed class HostRequestBroker
    {
        /// <summary>The page refused, or answered ok:false. Code is its error ("stale", "readOnly", "notEditable", ...).</summary>
        public sealed class RefusedException : Exception
        {
            public string Code { get; private set; }
            public RefusedException(string code) : base("the CA Editor refused: " + code) { Code = code; }
        }

        private sealed class Pending
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public Dictionary<string, object> Reply;
        }

        private readonly Action<string> _post;
        private readonly Func<bool> _isUiThread;
        private readonly object _lock = new object();
        private readonly Dictionary<long, Pending> _pending = new Dictionary<long, Pending>();
        private long _nextId;

        /// <param name="post">Sends a JSON message to the page (MonacoEditorControl.PostJson, which marshals itself).</param>
        /// <param name="isUiThread">True on the thread the page's replies are delivered on.</param>
        public HostRequestBroker(Action<string> post, Func<bool> isUiThread)
        {
            _post = post;
            _isUiThread = isUiThread ?? (() => false);
        }

        /// <summary>
        /// Ask the page and wait up to <paramref name="timeoutMs"/> for its answer: the reply's data. Throws
        /// TimeoutException when the page does not answer in time, RefusedException when it says no, and
        /// InvalidOperationException when called on the UI thread.
        /// </summary>
        public Dictionary<string, object> Request(string action, Dictionary<string, object> args, int timeoutMs)
        {
            if (_isUiThread()) throw new InvalidOperationException(
                "HostRequestBroker.Request was called on the UI thread; the page's reply is delivered there, so this would deadlock");

            var p = new Pending();
            long id;
            lock (_lock) { id = ++_nextId; _pending[id] = p; }
            try
            {
                _post(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new Dictionary<string, object>
                {
                    { "type", "hostRequest" }, { "reqId", id }, { "action", action },
                    { "args", args ?? new Dictionary<string, object>() }
                }));
                if (!p.Done.Wait(timeoutMs))
                    throw new TimeoutException("the CA Editor did not answer '" + action + "' within " + timeoutMs + " ms");

                var reply = p.Reply;
                object ok, err, data;
                bool good = reply != null && reply.TryGetValue("ok", out ok) && ok is bool && (bool)ok;
                if (!good)
                    throw new RefusedException(reply != null && reply.TryGetValue("error", out err) && err != null ? Convert.ToString(err) : "error");
                return reply.TryGetValue("data", out data) && data is Dictionary<string, object>
                    ? (Dictionary<string, object>)data : new Dictionary<string, object>();
            }
            finally
            {
                lock (_lock) _pending.Remove(id);
                p.Done.Dispose();
            }
        }

        /// <summary>A hostReply arrived from the page (UI thread). True when it matched a waiting request.</summary>
        public bool Complete(string json)
        {
            Dictionary<string, object> msg;
            try { msg = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>; }
            catch { return false; }
            object rid;
            if (msg == null || !msg.TryGetValue("reqId", out rid) || rid == null) return false;
            long id;
            try { id = Convert.ToInt64(rid); } catch { return false; }
            Pending p;
            lock (_lock) { if (!_pending.TryGetValue(id, out p)) return false; }   // late: the waiter gave up
            p.Reply = msg;
            try { p.Done.Set(); } catch (ObjectDisposedException) { return false; }
            return true;
        }

        /// <summary>Requests still waiting (for tests and logs).</summary>
        public int PendingCount { get { lock (_lock) return _pending.Count; } }
    }
}
