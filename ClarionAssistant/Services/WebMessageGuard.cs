using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Bounds on what a Monaco page may send the host (1c685f2e pipeline F6). The page runs in WebView2, so
    /// its messages are input: a message is size-checked BEFORE any JSON parsing, and the local layer's
    /// parsed payloads are bounded again (pieces, routines, slots, coordinates). A reject is answered with the
    /// action's empty shape and logged as one <c>[webmsg] rejected action= chars= reason=</c> line, at most
    /// once per action per 5 s so a misbehaving page cannot flood the log. No IDE references.
    /// </summary>
    public static class WebMessageGuard
    {
        public const int MaxSyncChars = 16000000;       // bufferSync / fileState: the whole buffer
        public const int MaxLogChars = 4096;            // log
        public const int MaxHeaderSyncChars = 1000000;  // headerSync: the module header
        public const int MaxLocalChars = 2000000;       // localCompletion / localHover / slotDiagnostics
        public const int MaxOtherChars = 1000000;       // every other action

        public const int MaxPieces = 8;
        public const int MaxRoutines = 10000;
        public const int MaxRoutineNameChars = 256;
        public const int MaxSlots = 2000;

        /// <summary>The largest message (in chars) accepted for <paramref name="action"/>.</summary>
        public static int MaxChars(string action)
        {
            switch (action)
            {
                case "bufferSync":
                case "fileState": return MaxSyncChars;
                case "log": return MaxLogChars;
                case "headerSync": return MaxHeaderSyncChars;
                case "localCompletion":
                case "localHover":
                case "slotDiagnostics": return MaxLocalChars;
                default: return MaxOtherChars;
            }
        }

        /// <summary>Null when a message of <paramref name="chars"/> chars is acceptable for the action,
        /// else the reject reason.</summary>
        public static string CheckSize(string action, int chars)
        {
            int max = MaxChars(action);
            return chars > max ? "over " + max + " chars" : null;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, long> LastLogged = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        public const int LogIntervalMs = 5000;

        /// <summary>Test hook: the clock the rate limit reads (ms). Null = real time.</summary>
        public static Func<long> NowMs;

        /// <summary>Log one reject, at most once per action per <see cref="LogIntervalMs"/>. True when written.</summary>
        public static bool LogReject(Action<string> log, string action, long chars, string reason)
        {
            if (log == null) return false;
            string key = action ?? "?";
            long now = NowMs != null ? NowMs() : Clock.ElapsedMilliseconds;
            lock (Gate)
            {
                long last;
                if (LastLogged.TryGetValue(key, out last) && now - last < LogIntervalMs) return false;
                LastLogged[key] = now;
            }
            try { log("[webmsg] rejected action=" + key + " chars=" + chars + " reason=" + reason); } catch { }
            return true;
        }

        /// <summary>Test hook: forget the rate-limit history.</summary>
        public static void ResetRateLimit() { lock (Gate) LastLogged.Clear(); }
    }
}
