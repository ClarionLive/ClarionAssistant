using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Terminal
{
    // ── Buffer sync for the Monaco pages (ticket 16d140e9) ──────────────────────────────────────
    // Crash: Clarion.exe is 32-bit and not LargeAddressAware (2 GB of address space). On a generated
    // module of 86,722 lines / 3.2 MB, monaco-embeditor.html sent the WHOLE buffer inside every LSP
    // request — completion, hover, signature help, definition, implementation, diagnostics, document
    // structure and (new in 5.9.0) folding, which Monaco re-asks after every edit. Each one became a
    // 6.4 MB UTF-16 string in the IDE plus JSON copies, and with the IDE already at 1.5-1.65 GB the
    // Chromium side of TryGetWebMessageAsString died with 0xE0000008 (OOM, uncatchable).
    //
    // Now the page posts {action:"bufferSync", v, buffer} only when the content changed since its last
    // sync, and requests carry just `v`. MonacoEditorControl keeps ONE cached copy per surface here and
    // every handler resolves its buffer through it. Everything in this file is free of IDE and WebView2
    // references so tests\MonacoBufferSync.Test.cs compiles it standalone.

    /// <summary>
    /// The one cached copy of a Monaco surface's buffer, stamped with the page's sync version `v`.
    /// Replaced on every sync, never accumulated. Thread-safe: messages arrive on the UI thread, handlers
    /// read on pool threads.
    /// </summary>
    public sealed class MonacoBufferCache
    {
        private readonly object _gate = new object();
        private string _buffer;
        private long _version = -1;

        /// <summary>The cached buffer (null until the first sync).</summary>
        public string CurrentBuffer { get { lock (_gate) return _buffer; } }

        /// <summary>The page's `v` for <see cref="CurrentBuffer"/>; -1 when nothing is cached.</summary>
        public long CurrentBufferVersion { get { lock (_gate) return _version; } }

        /// <summary>Replace the cached copy. The previous string becomes garbage at once.</summary>
        public void Store(long v, string buffer)
        {
            lock (_gate) { _buffer = buffer; _version = buffer != null ? v : -1; }
        }

        public void Clear()
        {
            lock (_gate) { _buffer = null; _version = -1; }
        }

        /// <summary>The cached buffer when it is version <paramref name="v"/>, else null.</summary>
        public string Resolve(long v)
        {
            lock (_gate) return (_buffer != null && v == _version) ? _buffer : null;
        }

        /// <summary>How a request's buffer was found.</summary>
        public enum Lookup
        {
            /// <summary>The request carried no buffer and no `v` (a caller that never needed one).</summary>
            None,
            /// <summary>An older page: the buffer came inline in the request (still supported).</summary>
            Inline,
            /// <summary>Resolved from the cache by `v`.</summary>
            Cached,
            /// <summary>The request named a `v` this cache does not hold. The caller must answer with the
            /// request's failure shape and ask the page to resync.</summary>
            Missing
        }

        /// <summary>
        /// Resolve the buffer a parsed request refers to. An inline "buffer" string wins (older page);
        /// otherwise "v" is looked up in the cache. Never throws.
        /// </summary>
        public Lookup ResolveRequest(IDictionary<string, object> data, out string buffer)
        {
            buffer = null;
            if (data == null) return Lookup.None;
            object o;
            if (data.TryGetValue("buffer", out o) && o is string)
            {
                buffer = (string)o;
                return Lookup.Inline;
            }
            long v;
            if (!TryGetLong(data, "v", out v)) return Lookup.None;
            buffer = Resolve(v);
            return buffer != null ? Lookup.Cached : Lookup.Missing;
        }

        /// <summary>
        /// The gate every buffer-dependent handler goes through: true ONLY for an inline buffer or a cached
        /// `v`. A request with neither (a dropped or malformed `v`) is refused exactly like an unknown `v` —
        /// serving it with a null buffer would let a handler fall back to load-time or on-disk text and
        /// answer for a document that is not on screen. <paramref name="lookup"/> says why, for the log.
        /// </summary>
        public bool TryResolveForRequest(IDictionary<string, object> data, out string buffer, out Lookup lookup)
        {
            lookup = ResolveRequest(data, out buffer);
            if (lookup == Lookup.Inline || lookup == Lookup.Cached) return buffer != null;
            buffer = null;
            return false;
        }

        public static bool TryGetLong(IDictionary<string, object> data, string key, out long value)
        {
            value = 0;
            object o;
            if (data == null || !data.TryGetValue(key, out o) || o == null) return false;
            try { value = Convert.ToInt64(o); return true; }
            catch { return false; }
        }

        // ── Parsing a sync message without a full JSON deserialise ──────────────────────────────
        // The page builds these messages with the text field LAST (bufferSync: action, v, buffer;
        // fileState: action, dirty, seq, v, text). Everything before the text key is a handful of
        // small scalars, so it is deserialised on its own and the text is unescaped exactly once into
        // an exactly-sized buffer. A message in any other shape falls back to JavaScriptSerializer.

        /// <summary>
        /// The small fields that precede <paramref name="textKey"/> in <paramref name="json"/>, as a
        /// dictionary, plus the index where the text's opening quote ends. Null when the message is not
        /// in the expected shape (text key absent, or not a string value).
        /// </summary>
        public static Dictionary<string, object> ParseHeader(string json, string textKey, out int textStart)
        {
            textStart = -1;
            if (string.IsNullOrEmpty(json) || json[0] != '{') return null;
            string tag = "\"" + textKey + "\":\"";
            int idx = json.IndexOf(tag, StringComparison.Ordinal);
            if (idx <= 1) return null;
            // The prefix must end in a comma: {"a":1,"text":"...  →  {"a":1}
            int end = idx - 1;
            while (end > 0 && char.IsWhiteSpace(json[end])) end--;
            if (json[end] != ',') return null;
            try
            {
                string head = json.Substring(0, end) + "}";
                var d = new JavaScriptSerializer().DeserializeObject(head) as Dictionary<string, object>;
                if (d == null) return null;
                textStart = idx + tag.Length;
                return d;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read a sync-shaped message: the version `v` and the text field. Fast path for the page's own
        /// shape (text last); anything else is deserialised in full. False when there is no `v` or no text.
        /// </summary>
        public static bool TryParseSync(string json, string textKey, out long v, out string text)
        {
            v = -1; text = null;
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                int start;
                var head = ParseHeader(json, textKey, out start);
                if (head != null)
                {
                    int after;
                    string s = UnescapeJsonString(json, start, out after);
                    if (s != null && IsObjectEnd(json, after) && TryGetLong(head, "v", out v))
                    {
                        text = s;
                        return true;
                    }
                }
                // Not our shape: pay for the general parser (rare — hand-built or older messages).
                var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                object o;
                if (d == null || !d.TryGetValue(textKey, out o) || !(o is string)) return false;
                if (!TryGetLong(d, "v", out v)) return false;
                text = (string)o;
                return true;
            }
            catch { v = -1; text = null; return false; }
        }

        private static bool IsObjectEnd(string json, int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            return i < json.Length && json[i] == '}';
        }

        /// <summary>
        /// Unescape the JSON string whose body starts at <paramref name="start"/> (just past the opening
        /// quote). Two passes — measure, then fill an exactly-sized char[] — so a 3 MB buffer costs one
        /// array plus the final string, never a doubling StringBuilder. Returns null on malformed input.
        /// <paramref name="after"/> is the index just past the closing quote.
        /// </summary>
        public static string UnescapeJsonString(string json, int start, out int after)
        {
            after = -1;
            int n = 0, i = start;
            bool anyEscape = false;
            while (true)
            {
                if (i >= json.Length) return null;
                char c = json[i];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (i + 1 >= json.Length) return null;
                    anyEscape = true;
                    if (json[i + 1] == 'u') { if (i + 5 >= json.Length) return null; i += 6; }
                    else i += 2;
                }
                else i++;
                n++;
            }
            int close = i;
            after = close + 1;
            if (!anyEscape) return json.Substring(start, close - start);

            var buf = new char[n];
            int o = 0;
            for (i = start; i < close; )
            {
                char c = json[i];
                if (c != '\\') { buf[o++] = c; i++; continue; }
                char e = json[i + 1];
                switch (e)
                {
                    case '"': buf[o++] = '"'; i += 2; break;
                    case '\\': buf[o++] = '\\'; i += 2; break;
                    case '/': buf[o++] = '/'; i += 2; break;
                    case 'b': buf[o++] = '\b'; i += 2; break;
                    case 'f': buf[o++] = '\f'; i += 2; break;
                    case 'n': buf[o++] = '\n'; i += 2; break;
                    case 'r': buf[o++] = '\r'; i += 2; break;
                    case 't': buf[o++] = '\t'; i += 2; break;
                    case 'u':
                        int code;
                        if (!int.TryParse(json.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out code)) return null;
                        buf[o++] = (char)code; i += 6; break;
                    default: return null;
                }
            }
            return new string(buf, 0, o);
        }
    }

    /// <summary>
    /// Runs at most one job at a time and keeps only the NEWEST waiting job. Monaco re-asks completion on
    /// every keystroke while the suggest widget is open (the page returns incomplete:true), so on a slow
    /// language server a queue of requests — each syncing a multi-megabyte buffer — could build up behind
    /// the one in progress. A job displaced from the waiting slot is answered at once via its dropped
    /// callback instead of being run. (16d140e9)
    /// </summary>
    public sealed class LatestOnlyWorker
    {
        private sealed class Job { public Action Work; public Action Dropped; }

        private readonly object _gate = new object();
        private readonly Func<Action, Task> _start;
        private bool _running;
        private Job _pending;

        /// <param name="start">How the drain loop is started (default Task.Run). Tests pass a synchronous
        /// or controllable starter.</param>
        public LatestOnlyWorker(Func<Action, Task> start = null)
        {
            _start = start ?? (a => Task.Run(a));
        }

        public void Submit(Action work, Action dropped)
        {
            var job = new Job { Work = work, Dropped = dropped };
            Job displaced = null, first = null;
            lock (_gate)
            {
                if (_running) { displaced = _pending; _pending = job; }
                else { _running = true; first = job; }
            }
            if (displaced != null) SafeRun(displaced.Dropped);
            if (first != null)
            {
                try { _start(() => Drain(first)); }
                catch { lock (_gate) { _running = false; } SafeRun(first.Dropped); }
            }
        }

        private void Drain(Job job)
        {
            while (job != null)
            {
                SafeRun(job.Work);
                lock (_gate)
                {
                    job = _pending;
                    _pending = null;
                    if (job == null) _running = false;
                }
            }
        }

        private static void SafeRun(Action a)
        {
            if (a == null) return;
            try { a(); } catch { }
        }
    }

    /// <summary>
    /// The page stamps every request with sentAt (epoch ms) and timeoutMs. The host uses them to log how
    /// long a request waited before it was handled and whether its reply arrived after the page gave up.
    /// </summary>
    public sealed class MonacoRequestStamp
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public long SentAt { get; private set; }
        public long TimeoutMs { get; private set; }
        public long ReceivedAt { get; private set; }

        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }

        public static MonacoRequestStamp From(IDictionary<string, object> data)
        {
            long sent, to;
            var s = new MonacoRequestStamp { ReceivedAt = NowMs() };
            if (MonacoBufferCache.TryGetLong(data, "sentAt", out sent)) s.SentAt = sent;
            if (MonacoBufferCache.TryGetLong(data, "timeoutMs", out to)) s.TimeoutMs = to;
            return s;
        }

        /// <summary>ms between the page posting the request and the host picking it up (-1 unknown).</summary>
        public long QueuedMs { get { return SentAt > 0 ? Math.Max(0, ReceivedAt - SentAt) : -1; } }

        /// <summary>True when, at <paramref name="nowMs"/>, the page has already stopped waiting.</summary>
        public bool IsLate(long nowMs) { return SentAt > 0 && TimeoutMs > 0 && nowMs - SentAt > TimeoutMs; }

        /// <summary>" queuedMs=.. pageAgeMs=.. pageTimeoutMs=.. late=yes|no" for a log line.</summary>
        public string Describe(long nowMs)
        {
            if (SentAt <= 0) return " page=unstamped";
            return " queuedMs=" + QueuedMs + " pageAgeMs=" + Math.Max(0, nowMs - SentAt) +
                   " pageTimeoutMs=" + TimeoutMs + " late=" + (IsLate(nowMs) ? "YES(page already gave up)" : "no");
        }
    }

    /// <summary>
    /// One instrumentation line per request ([lsp-timing] / [diag-timing]): ordered key=value fields, the
    /// total time since construction, and the page stamp (queued / late). Pure formatting — the hosts
    /// hand the result to MonacoSpikeLog.Write. (16d140e9)
    /// </summary>
    public sealed class RequestTimingLine
    {
        private readonly string _marker;
        private readonly MonacoRequestStamp _stamp;
        private readonly System.Diagnostics.Stopwatch _total = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<KeyValuePair<string, string>> _fields = new List<KeyValuePair<string, string>>();

        public RequestTimingLine(string marker, string action, MonacoRequestStamp stamp)
        {
            _marker = marker;
            _stamp = stamp;
            Add("action", action);
        }

        public long ElapsedMs { get { return _total.ElapsedMilliseconds; } }

        public RequestTimingLine Add(string key, object value)
        {
            _fields.Add(new KeyValuePair<string, string>(key, value == null ? "null" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
            return this;
        }

        public string Format()
        {
            var sb = new System.Text.StringBuilder(_marker);
            foreach (var f in _fields) sb.Append(' ').Append(f.Key).Append('=').Append(f.Value);
            sb.Append(" totalMs=").Append(_total.ElapsedMilliseconds);
            if (_stamp != null) sb.Append(_stamp.Describe(MonacoRequestStamp.NowMs()));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Remembers, per LSP document, a cheap fingerprint of the last text a host pushed, so a timing line
    /// can say whether a sync actually had new text to send. Both language clients (bundled LspClient and
    /// the ClarionLsp addin) skip an unchanged buffer and send a FULL-document didChange otherwise.
    /// </summary>
    public static class LspSyncFingerprint
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, long> Last = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="text"/> differs from the last text noted for the file.</summary>
        public static bool NoteAndCompare(string filePath, string text)
        {
            if (string.IsNullOrEmpty(filePath) || text == null) return false;
            long fp = ((long)text.Length << 32) ^ (uint)text.GetHashCode();
            lock (Gate)
            {
                long prev;
                bool changed = !Last.TryGetValue(filePath, out prev) || prev != fp;
                Last[filePath] = fp;
                return changed;
            }
        }
    }
}
