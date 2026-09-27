using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClarionAssistant.Services
{
    /// <summary>What a host tells the local layer about its surface (1c685f2e item 4).</summary>
    public sealed class LocalLayerOptions
    {
        /// <summary>The embeditor's procedure (for the routine set of the slot checks); null for a whole file.</summary>
        public string ProcedureName;
        /// <summary>Whether slotDiagnostics runs the slot checks at all: embed mode and the CA Editor overlay
        /// do, the CA Embeditor's plain-source FILE MODE tab does not (ticket 564aa142). False = empty list.</summary>
        public bool SlotChecks;
        /// <summary>Slot ranges to use when the request carries none (the embeditor's load-time ranges).</summary>
        public List<int[]> DefaultRanges;
        /// <summary>The LSP line offset for a buffer (the embeditor's MEMBER header: 0 or 1); null = 0. Passed
        /// to <see cref="LocalLayerHandlers.Handle"/> as lineOffset.</summary>
        public Func<string, int> LineOffsetFor;
        /// <summary>For the [local-timing] line.</summary>
        public string Surface;
        /// <summary>Where the [local-timing] line goes (monaco-spike.log in the IDE; a list in tests).</summary>
        public Action<string> Log;
    }

    /// <summary>
    /// The R11 slice a localCompletion / localHover request carries instead of a synced-buffer `v`
    /// (1c685f2e, gate failed: a full sync cost ~100 ms per keystroke on InventoryTable):
    /// <c>slice:{headerHash, span:{start,text}, ownerData:{start,text}|null, routines:[names]}</c>.
    /// Starts are 1-based Monaco lines of the unwrapped buffer.
    /// </summary>
    public sealed class LocalSlice
    {
        public string HeaderHash;
        public int SpanStart;
        public string SpanText;
        public int OwnerStart;
        public string OwnerText;
        public List<string> Routines = new List<string>();

        /// <summary>Chars of text the page shipped (for [local-timing] sliceChars).</summary>
        public int Chars { get { return (SpanText != null ? SpanText.Length : 0) + (OwnerText != null ? OwnerText.Length : 0); } }

        /// <summary>The request's "slice", or null when it has none (the `v` form).</summary>
        public static LocalSlice From(IDictionary<string, object> args)
        {
            object o;
            if (args == null || !args.TryGetValue("slice", out o)) return null;
            var d = o as IDictionary<string, object>;
            if (d == null) return null;
            var s = new LocalSlice();
            object h;
            if (d.TryGetValue("headerHash", out h) && h != null) s.HeaderHash = Convert.ToString(h, System.Globalization.CultureInfo.InvariantCulture);
            ReadPart(d, "span", out s.SpanStart, out s.SpanText);
            ReadPart(d, "ownerData", out s.OwnerStart, out s.OwnerText);
            s.Routines = LocalLayerHandlers.ReadStrings(d, "routines");
            return s.SpanText != null ? s : null;
        }

        private static void ReadPart(IDictionary<string, object> d, string key, out int start, out string text)
        {
            start = 0; text = null;
            object o;
            var part = d.TryGetValue(key, out o) ? o as IDictionary<string, object> : null;
            if (part == null) return;
            start = LocalLayerHandlers.ReadInt(part, "start");
            object t;
            if (part.TryGetValue("text", out t)) text = t as string;
            if (start < 1) text = null;
        }
    }

    /// <summary>
    /// The instant local layer's page requests, answered WITHOUT the language server (1c685f2e item 4):
    /// <c>localCompletion</c>, <c>localHover</c> and <c>slotDiagnostics</c>. Both Monaco hosts route the three
    /// actions here and add only the lane and the reply (MonacoEditorControl.RunLocalAction), so the answer
    /// is the same in the CA Embeditor and the CA Editor overlay.
    ///
    /// The rules that keep it instant, pinned by tests\LocalLayer.SourceScan.ps1 (which lists the exact
    /// forbidden calls): never start the language server, never push the buffer to it, never wait on it or
    /// make any SharedLspBridge request, and never open a database per request. It therefore answers
    /// while the server is starting, busy or down.
    ///
    /// Coordinates: requests carry Monaco positions (1-based) on the UNWRAPPED buffer, and every local
    /// lookup works in exactly those. Only LSP positions use the embeditor's MEMBER-header offset, so the
    /// local layer never applies <c>lineOffset</c> to a lookup.
    ///
    /// No IDE references: compiles under the csc harnesses.
    /// </summary>
    public static class LocalLayerHandlers
    {
        public const string LocalCompletion = "localCompletion";
        public const string LocalHover = "localHover";
        public const string SlotDiagnostics = "slotDiagnostics";

        /// <summary>
        /// Answer one local request. <paramref name="args"/> is the parsed page request (line, column,
        /// triggerCharacter, ranges ...). <paramref name="lineOffset"/> is the LSP line offset for this buffer,
        /// passed for completeness; local lookups do not use it (see the class comment). Never throws: an
        /// unknown action or a failure answers the action's empty shape. Writes one
        /// <c>[local-timing] action= ms= items=</c> line to <see cref="LocalLayerOptions.Log"/>.
        /// </summary>
        public static Dictionary<string, object> Handle(string action, string buffer, IDictionary<string, object> args,
            int lineOffset, LocalLayerOptions options)
        {
            options = options ?? new LocalLayerOptions();
            var sw = Stopwatch.StartNew();
            Dictionary<string, object> reply;
            int items = 0;
            int sliceChars = -1;   // -1 = the request used the synced buffer (`v`), not a slice
            string error = null;
            try
            {
                switch (action)
                {
                    case SlotDiagnostics:
                        {
                            var markers = Slot(buffer, args, options, out sliceChars);
                            items = markers.Count;
                            reply = new Dictionary<string, object> { { "markers", markers } };
                            break;
                        }
                    case LocalCompletion:
                    case LocalHover:
                        {
                            var slice = LocalSlice.From(args);
                            if (slice != null) sliceChars = slice.Chars;
                            reply = EmptyReply(action);
                            break;
                        }
                    default:
                        reply = EmptyReply(action);
                        error = "unknown action";
                        break;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                reply = EmptyReply(action);
                items = 0;
            }
            long ms = sw.ElapsedMilliseconds;
            reply["ms"] = ms;
            WriteTiming(options, action, ms, items, sliceChars, error);
            return reply;
        }

        private static Dictionary<string, object> EmptyReply(string action)
        {
            switch (action)
            {
                case SlotDiagnostics: return new Dictionary<string, object> { { "markers", new List<Dictionary<string, object>>() } };
                case LocalCompletion: return new Dictionary<string, object> { { "items", new List<Dictionary<string, object>>() }, { "source", "local" } };
                case LocalHover: return new Dictionary<string, object> { { "contents", null }, { "authoritative", false } };
                default: return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// slotDiagnostics. The R11 slice form <c>{procedureName, routines:[..], slots:[{start,text}]}</c> carries the
        /// slots' own text (no synced buffer); without <c>slots</c> the request refers to the synced buffer by `v`
        /// and its <c>ranges</c>, as before.
        /// </summary>
        private static List<Dictionary<string, object>> Slot(string buffer, IDictionary<string, object> args,
            LocalLayerOptions options, out int sliceChars)
        {
            sliceChars = -1;
            var slots = ReadSlots(args);
            if (slots != null)
            {
                sliceChars = 0;
                foreach (var s in slots) sliceChars += s.Text != null ? s.Text.Length : 0;
            }
            if (!options.SlotChecks) return new List<Dictionary<string, object>>();
            if (slots != null) return ModernEmbeditorDiagnostics.ComputeSlotChecks(slots, ReadStrings(args, "routines"));
            var ranges = ReadRanges(args);
            if (ranges == null || ranges.Count == 0) ranges = options.DefaultRanges;
            return ModernEmbeditorDiagnostics.ComputeSlotChecks(buffer, ranges, options.ProcedureName);
        }

        /// <summary>The request's "ranges": an array of [start,end] 1-based line pairs; null when absent.</summary>
        public static List<int[]> ReadRanges(IDictionary<string, object> args)
        {
            object o;
            if (args == null || !args.TryGetValue("ranges", out o) || o == null) return null;
            var ranges = new List<int[]>();
            foreach (var item in AsArray(o))
            {
                var pair = AsArray(item);
                if (pair.Length >= 2)
                {
                    try { ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) }); } catch { }
                }
            }
            return ranges;
        }

        /// <summary>
        /// The <c>{type:'spanMap', v, headerHash, procs:[{name,start,dataEnd,end,owner,routines}]}</c> push for a
        /// fully synced buffer (R11), or null when none can be built. PENDING Coder-A's merge: the map comes from
        /// LocalScopeIndex.BuildSpanMap, which also caches the module header by hash. Until then no map is
        /// pushed, and the page keeps using `v` for its local requests (the R11 fallback).
        /// </summary>
        public static string SpanMapMessage(long v, string buffer, Action<string> log)
        {
            return null;
        }

        /// <summary>
        /// The page's <c>{action:'headerSync', hash, text}</c>: the module header for a hash the host answered
        /// {needHeader:true} for. PENDING Coder-A's merge for the store (LocalScopeIndex's header cache); for now
        /// it is parsed and logged. Never throws.
        /// </summary>
        public static bool AcceptHeaderSync(string json, Action<string> log)
        {
            string hash = null, text = null;
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as IDictionary<string, object>;
                object o;
                if (d != null && d.TryGetValue("hash", out o) && o != null) hash = Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture);
                if (d != null && d.TryGetValue("text", out o)) text = o as string;
            }
            catch { }
            bool ok = !string.IsNullOrEmpty(hash) && text != null;
            if (log != null)
            {
                try { log("[local-timing] action=headerSync " + (ok ? "hash=" + hash + " chars=" + text.Length : "refused (no hash or text)")); }
                catch { }
            }
            return ok;
        }

        /// <summary>True when the request carries its own text (R11 slice form) and needs no synced buffer.</summary>
        public static bool CarriesSlice(IDictionary<string, object> args)
        {
            return args != null && (args.ContainsKey("slice") || args.ContainsKey("slots"));
        }

        /// <summary>The slotDiagnostics slice's "slots": [{start, text}]; null when absent (the `v` form).</summary>
        public static List<ModernEmbeditorDiagnostics.SlotText> ReadSlots(IDictionary<string, object> args)
        {
            object o;
            if (args == null || !args.TryGetValue("slots", out o)) return null;
            var slots = new List<ModernEmbeditorDiagnostics.SlotText>();
            foreach (var item in AsArray(o))
            {
                var d = item as IDictionary<string, object>;
                if (d == null) continue;
                int start = ReadInt(d, "start");
                object t;
                string text = d.TryGetValue("text", out t) ? t as string : null;
                if (start >= 1 && text != null) slots.Add(new ModernEmbeditorDiagnostics.SlotText { Start = start, Text = text });
            }
            return slots;
        }

        /// <summary>A string array field (e.g. "routines"); empty when absent.</summary>
        public static List<string> ReadStrings(IDictionary<string, object> args, string key)
        {
            var list = new List<string>();
            object o;
            if (args == null || !args.TryGetValue(key, out o)) return list;
            foreach (var item in AsArray(o)) { var s = item as string; if (!string.IsNullOrEmpty(s)) list.Add(s); }
            return list;
        }

        internal static object[] AsArray(object o)
        {
            var arr = o as object[];
            if (arr != null) return arr;
            var list = o as System.Collections.IList;
            if (list == null) return new object[0];
            arr = new object[list.Count];
            list.CopyTo(arr, 0);
            return arr;
        }

        internal static int ReadInt(IDictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return 0;
            try { return Convert.ToInt32(o); } catch { return 0; }
        }

        private static void WriteTiming(LocalLayerOptions options, string action, long ms, int items, int sliceChars, string error)
        {
            var log = options.Log;
            if (log == null) return;
            try
            {
                log("[local-timing] action=" + (action ?? "?") + " ms=" + ms + " items=" + items +
                    " sliceChars=" + (sliceChars >= 0 ? sliceChars.ToString() : "none(v)") +
                    (options.Surface != null ? " surface=" + options.Surface : "") +
                    (error != null ? " error=" + error : ""));
            }
            catch { }
        }
    }
}
