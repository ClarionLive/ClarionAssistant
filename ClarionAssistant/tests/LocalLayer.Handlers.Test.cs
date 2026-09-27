// LocalLayer.Handlers.Test.cs - 1c685f2e item 4: LocalLayerHandlers.Handle, the one class both Monaco hosts route
// localCompletion / localHover / slotDiagnostics to. Compiles the REAL Services\LocalLayerHandlers.cs and
// ModernEmbeditorDiagnostics.cs; SharedLspBridge is the SlotBalance stub, whose call counters prove the local
// layer never touches the language server.
//
// Requests are built the way the page sends them and parsed with JavaScriptSerializer, exactly as
// MonacoEditorControl.RunLocalAction does, so ranges arrive as object[] of object[].
//
// Run: tests\Run-Tests.ps1

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

static class LocalLayerHandlersTest
{
    static int _pass, _fail;

    static void Check(string name, bool cond, string detail = null)
    {
        if (cond) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name + (detail != null ? " - " + detail : "")); }
    }

    static Dictionary<string, object> Req(string json)
    {
        return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
    }

    static string Json(object o) { return new JavaScriptSerializer().Serialize(o); }

    static List<Dictionary<string, object>> Markers(Dictionary<string, object> reply)
    {
        return (List<Dictionary<string, object>>)reply["markers"];
    }

    // An embed procedure: slot 4-6 opens a LOOP it never closes and DOes an undefined routine.
    static readonly string EmbedBuffer = string.Join("\r\n", new[] {
        "TestProc PROCEDURE",      // 1
        "  CODE",                  // 2
        "  x# = 1",                // 3
        "  LOOP",                  // 4  slot
        "    DO NoSuchRoutine",    // 5  slot
        "    x# += 1",             // 6  slot
        "  RETURN",                // 7
        "RealRtn ROUTINE",         // 8
        "  x# = 0" });             // 9

    // A whole source file for the CA Editor overlay: an IF never closed.
    static readonly string FileBuffer = string.Join("\r\n", new[] {
        "  MEMBER('app')",
        "Work PROCEDURE",
        "  CODE",
        "  IF a = 1",
        "    b# = 2",
        "  RETURN" });

    static int Main()
    {
        var log = new List<string>();
        var embed = new LocalLayerOptions { ProcedureName = "TestProc", SlotChecks = true, Surface = "CA Embeditor", Log = log.Add,
                                            DefaultRanges = new List<int[]> { new[] { 4, 6 } } };
        var fileTab = new LocalLayerOptions { ProcedureName = null, SlotChecks = false, Surface = "CA Editor(tab)", Log = log.Add };
        var overlay = new LocalLayerOptions { SlotChecks = true, Surface = "CA Editor(overlay)", Log = log.Add };

        SharedLspBridge.Reset();   // the "server" is down for everything below

        Console.WriteLine("\n4.7 slotDiagnostics per surface, with the LSP down");
        {
            var r = LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"action\":\"slotDiagnostics\",\"v\":3,\"ranges\":[[4,6]]}"), 1, embed);
            var ms = Markers(r);
            Check("embed mode: the unterminated LOOP and the undefined DO",
                ms.Any(m => (int)m["line"] == 4 && ((string)m["message"]).StartsWith("LOOP is not terminated")) &&
                ms.Any(m => (int)m["line"] == 5 && ((string)m["message"]).Contains("NoSuchRoutine")), Json(ms));
            Check("...line numbers are Monaco lines: lineOffset=1 is NOT applied to the local lookup",
                ms.All(m => (int)m["line"] >= 4 && (int)m["line"] <= 5), Json(ms));

            var none = LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"action\":\"slotDiagnostics\",\"v\":3}"), 1, embed);
            Check("no ranges in the request -> the host's DefaultRanges are used", Markers(none).Count == ms.Count, Json(Markers(none)));

            var tab = LocalLayerHandlers.Handle("slotDiagnostics", FileBuffer, Req("{\"action\":\"slotDiagnostics\",\"v\":3,\"ranges\":[[1,6]]}"), 0, fileTab);
            Check("the CA Embeditor's FILE MODE tab: an empty marker list", Markers(tab).Count == 0, Json(Markers(tab)));

            var ov = LocalLayerHandlers.Handle("slotDiagnostics", FileBuffer, Req("{\"action\":\"slotDiagnostics\",\"v\":3,\"ranges\":[[1,6]]}"), 0, overlay);
            Check("the CA Editor overlay KEEPS its structure squiggles (whole-file ranges, unbalanced IF -> a marker)",
                Markers(ov).Any(m => (int)m["line"] == 4 && ((string)m["message"]).StartsWith("IF is not terminated")), Json(Markers(ov)));

            Check("none of it touched the language server (stub call counters all 0)", SharedLspBridge.TotalCalls == 0,
                "calls=" + SharedLspBridge.TotalCalls);
        }

        Console.WriteLine("\n4.8 one [local-timing] line per call");
        {
            log.Clear();
            LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"ranges\":[[4,6]]}"), 0, embed);
            LocalLayerHandlers.Handle("localCompletion", EmbedBuffer, Req("{\"line\":5,\"column\":7}"), 0, embed);
            LocalLayerHandlers.Handle("localHover", EmbedBuffer, Req("{\"line\":5,\"column\":7}"), 0, embed);
            var rx = new Regex(@"^\[local-timing\] action=(localCompletion|localHover|slotDiagnostics) ms=\d+ items=\d+");
            Check("three calls -> three lines, each in the agreed shape", log.Count == 3 && log.All(l => rx.IsMatch(l)), string.Join(" | ", log));
            Check("...slotDiagnostics counts its markers as items", log.Count > 0 && log[0].Contains(" items=2"), log.FirstOrDefault());
        }

        Console.WriteLine("\n4.10 reply shapes (as the page receives them)");
        {
            var s = Json(LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"ranges\":[[4,6]]}"), 0, embed));
            Check("slotDiagnostics -> {markers:[...], ms}", Regex.IsMatch(s, "^\\{\"markers\":\\[.*\\],\"ms\":\\d+\\}$"), s);
            s = Json(LocalLayerHandlers.Handle("localCompletion", EmbedBuffer, Req("{\"line\":5,\"column\":7}"), 0, embed));
            Check("localCompletion -> {items:[...], source:'local', ms}", Regex.IsMatch(s, "^\\{\"items\":\\[.*\\],\"source\":\"local\",\"ms\":\\d+\\}$"), s);
            s = Json(LocalLayerHandlers.Handle("localHover", EmbedBuffer, Req("{\"line\":5,\"column\":7}"), 0, embed));
            Check("localHover -> {contents, authoritative:<bool>, ms}", Regex.IsMatch(s, "^\\{\"contents\":(null|\".*\"),\"authoritative\":(true|false),\"ms\":\\d+\\}$"), s);
        }

        Console.WriteLine("\nrobustness");
        {
            log.Clear();
            Dictionary<string, object> r = null;
            bool threw = false;
            try { r = LocalLayerHandlers.Handle("slotDiagnostics", null, null, 0, null); } catch { threw = true; }
            Check("null buffer / args / options -> an empty marker list, never throws", !threw && r != null && Markers(r).Count == 0);
            try { r = LocalLayerHandlers.Handle("nope", EmbedBuffer, null, 0, embed); } catch { threw = true; }
            Check("an unknown action -> answered, and logged as an error", !threw && r != null && log.Any(l => l.Contains("error=unknown action")), string.Join(" | ", log));
        }

        Console.WriteLine("\n" + _pass + " passed, " + _fail + " failed");
        return _fail == 0 ? 0 : 1;
    }
}
