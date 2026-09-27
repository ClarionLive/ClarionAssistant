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
            string error = null;
            try
            {
                switch (action)
                {
                    case SlotDiagnostics:
                        {
                            var markers = Slot(buffer, args, options);
                            items = markers.Count;
                            reply = new Dictionary<string, object> { { "markers", markers } };
                            break;
                        }
                    case LocalCompletion:
                        reply = new Dictionary<string, object> { { "items", new List<Dictionary<string, object>>() }, { "source", "local" } };
                        break;
                    case LocalHover:
                        reply = new Dictionary<string, object> { { "contents", null }, { "authoritative", false } };
                        break;
                    default:
                        reply = new Dictionary<string, object>();
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
            WriteTiming(options, action, ms, items, error);
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

        private static List<Dictionary<string, object>> Slot(string buffer, IDictionary<string, object> args, LocalLayerOptions options)
        {
            if (!options.SlotChecks) return new List<Dictionary<string, object>>();
            var ranges = ReadRanges(args);
            if (ranges == null || ranges.Count == 0) ranges = options.DefaultRanges;
            return ModernEmbeditorDiagnostics.ComputeSlotChecks(buffer, ranges, options.ProcedureName);
        }

        /// <summary>The request's "ranges": an array of [start,end] 1-based line pairs; null when absent.</summary>
        public static List<int[]> ReadRanges(IDictionary<string, object> args)
        {
            object o;
            if (args == null || !args.TryGetValue("ranges", out o)) return null;
            var arr = o as object[];
            if (arr == null)
            {
                var list = o as System.Collections.IList;
                if (list == null) return null;
                arr = new object[list.Count];
                list.CopyTo(arr, 0);
            }
            var ranges = new List<int[]>();
            foreach (var item in arr)
            {
                var pair = item as object[];
                if (pair != null && pair.Length >= 2)
                {
                    try { ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) }); } catch { }
                }
            }
            return ranges;
        }

        private static void WriteTiming(LocalLayerOptions options, string action, long ms, int items, string error)
        {
            var log = options.Log;
            if (log == null) return;
            try
            {
                log("[local-timing] action=" + (action ?? "?") + " ms=" + ms + " items=" + items +
                    (options.Surface != null ? " surface=" + options.Surface : "") +
                    (error != null ? " error=" + error : ""));
            }
            catch { }
        }
    }
}
