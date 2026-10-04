using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

// HoverBench — the CA Editor's local-first hover layer vs the Clarion language server, measured the same way.
//
// WHY. The page answers a hover from CA's local layer (buffer, live dictionary, CodeGraph indexes, keywords) and,
// when that card is "authoritative", never asks the language server (monaco-embeditor.html, the second hover
// provider; LocalLayerHandlers.HoverAt). The design traded the server's richer card for speed: a full buffer sync
// once cost ~100 ms per keystroke (1c685f2e). The server has since got much faster. This measures, on real
// solutions, whether local-first still pays for itself — latency on both sides, and what each side answers —
// so the call can be made from numbers.
//
// WHAT RUNS. The REAL LocalLayerHandlers.HoverAt over the real project and ClarionGraph library DBs, and the REAL
// LspClient against each server.js given (shipped, a development build, ...), started with the handshake
// LspService.EnsureRunning sends. Two timings per side:
//   steady  — the buffer is unchanged since the last request (hovering after typing stops).
//   edited  — the buffer just changed: the server pays a full-text didChange, the local layer a buffer re-parse.
// Not included (both sides would pay them in the IDE): the page<->host round trip, SharedLspBridge's
// library-member pre-emption and CodeGraph fallback, and the page's idle-sync wait.
//
// Run through tests\bench\Run-HoverBench.ps1 (it compiles this with the sources it needs). Not part of
// Run-Tests.ps1: it needs real solutions and a server.js.
static class HoverBench
{
    // After an edit: the didChange alone (CA's cost to send it) and the hover that follows (the server catching up).
    static readonly List<double> SendMs = new List<double>(), NextHoverMs = new List<double>();
    // How the last server run sent its changes ("ranged"/"full") and how many of each, for its history line.
    static string SyncMode = "";
    static int RangedSent, FullSent;
    sealed class Opt
    {
        public string Sln, Version, PropsXml, ProjectDb, LibraryDb, HistoryPath, DumpPath, CaCommit = "";
        public List<KeyValuePair<string, string>> Servers = new List<KeyValuePair<string, string>>();
        public List<string> Files = new List<string>();
        public int MaxFiles = 4, PerFile = 60, Edited = 30, MarginMs = 20;
        public bool PrintRed, PrintGraphHash;
        public string EditMode = "end";   // end: a comment line appended at the end of the file; near: see EditFor
    }

    sealed class Sample { public string File; public int Line0, Col0; public string Word; }

    sealed class LocalAnswer { public bool Any, Auth; public string Kind, Symbol, Card; public double Ms; }
    sealed class LspAnswer { public bool Any, TimedOut; public string Symbol, Card; public double Ms; }

    // LspClient.GetHover gives the server 1500 ms (SendRequest's deadline) and returns null when it passes. A null
    // after at least 1400 ms counts as that timeout: the deadline is checked against DateTime.UtcNow, whose ~15 ms
    // tick can end the wait a little before the Stopwatch reaches 1500.
    const double HoverTimeoutFloorMs = 1400;

    static bool IsTimeout(Dictionary<string, object> r, double ms) { return r == null && ms >= HoverTimeoutFloorMs; }

    static int Main(string[] args)
    {
        Opt o;
        try { o = Parse(args); }
        catch (Exception ex) { Console.Error.WriteLine("usage error: " + ex.Message); return 2; }

        var cfg = FindVersion(o);
        if (cfg == null) { Console.Error.WriteLine("COULD NOT RUN: Clarion version '" + o.Version + "' not found in any ClarionProperties.xml"); return 2; }
        if (o.PrintRed) { Console.WriteLine(cfg.RedFilePath ?? ""); return 0; }   // for the runner's indexer call
        if (o.PrintGraphHash)
        {
            // The root fingerprint the IDE puts in this version's library DB name (ClarionGraph_<build>_<hash>.db),
            // so the runner can pick that DB rather than whichever cache was built last.
            string key = cfg.LibraryGraphKey(null);
            Console.WriteLine(key.Substring(key.LastIndexOf('_') + 1));
            return 0;
        }
        if (o.Files.Count == 0) o.Files = DefaultFiles(o.Sln, o.MaxFiles);
        if (o.ProjectDb == null) o.ProjectDb = Path.Combine(Path.GetDirectoryName(o.Sln), Path.GetFileNameWithoutExtension(o.Sln) + ".codegraph.db");
        LocalLayerHandlers.ProjectDbPath = () => File.Exists(o.ProjectDb) ? o.ProjectDb : null;
        LocalLayerHandlers.LibraryDbPath = () => o.LibraryDb != null && File.Exists(o.LibraryDb) ? o.LibraryDb : null;

        Console.WriteLine("HoverBench  solution=" + o.Sln);
        Console.WriteLine("  Clarion version: " + cfg.Name + "  (.red " + cfg.RedFileName + ")");
        Console.WriteLine("  project DB: " + (File.Exists(o.ProjectDb) ? o.ProjectDb : "(none - the local layer has no project index)"));
        Console.WriteLine("  library DB: " + (o.LibraryDb != null && File.Exists(o.LibraryDb) ? o.LibraryDb : "(none)"));
        Console.WriteLine("  edits: " + (o.EditMode == "near" ? "near (same procedure, cumulative)" : "end of file"));
        var texts = new Dictionary<string, string>();
        var samples = new List<Sample>();
        foreach (var f in o.Files)
        {
            string text = File.ReadAllText(f);
            texts[f] = text;
            var s = SampleFile(f, text, o.PerFile);
            samples.AddRange(s);
            Console.WriteLine("  file: " + Path.GetFileName(f) + "  (" + text.Length / 1024 + " KB, " + s.Count + " samples)");
        }
        if (samples.Count == 0) { Console.Error.WriteLine("COULD NOT RUN: no identifiers sampled"); return 2; }

        // ---- the local layer ----
        var local = new LocalAnswer[samples.Count];
        var sources = texts.ToDictionary(kv => kv.Key, kv => LocalSource.OfBuffer(kv.Value));
        for (int w = 0; w < Math.Min(5, samples.Count); w++) LocalHover(sources[samples[w].File], samples[w]);   // warm the DB handles
        for (int i = 0; i < samples.Count; i++) local[i] = LocalHover(sources[samples[i].File], samples[i]);
        var localEdited = new List<double>();
        var localRunning = new Dictionary<string, string>();
        for (int i = 0; i < Math.Min(o.Edited, samples.Count); i++)
        {
            var sm = samples[i];
            string edited = EditFor(o, localRunning, texts, sm, i);
            var sw = Stopwatch.StartNew();
            // As LocalHover: a throw is a miss (the page would show no local card), not the end of the run.
            try { LocalLayerHandlers.HoverAt(LocalSource.OfBuffer(edited), sm.Line0, sm.Col0, new LocalLayerOptions { FileName = Path.GetFileName(sm.File) }); }
            catch { }
            localEdited.Add(sw.Elapsed.TotalMilliseconds);
        }

        var history = new List<string>();
        foreach (var server in o.Servers)
        {
            Console.WriteLine();
            Console.WriteLine("=== server: " + server.Key + "  " + server.Value + "  (build " + BuildSha(server.Value) + ")");
            // A server named "...-full" runs with ranged (incremental) sync off, so one session can compare both.
            LspClient.IncrementalSyncEnabled = !server.Key.EndsWith("-full", StringComparison.OrdinalIgnoreCase);
            SendMs.Clear(); NextHoverMs.Clear(); SyncMode = ""; RangedSent = FullSent = 0;
            var lsp = RunServer(o, cfg, server.Value, samples, texts, out List<double> lspEdited, out int lspEditedTimeouts, out string startNote);
            if (lsp == null) { Console.WriteLine("  COULD NOT START: " + startNote); continue; }
            history.Add(Report(o, server, samples, local, localEdited, lsp, lspEdited, lspEditedTimeouts));
        }

        if (!string.IsNullOrEmpty(o.HistoryPath) && history.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.HistoryPath)));
            File.AppendAllText(o.HistoryPath, string.Join(Environment.NewLine, history) + Environment.NewLine);
            Console.WriteLine();
            Console.WriteLine("History: appended " + history.Count + " line(s) to " + o.HistoryPath);
        }
        // Nothing measured is "could not run" (the runner's exit-code contract), not a pass.
        if (history.Count == 0) { Console.Error.WriteLine("COULD NOT RUN: no server started"); return 2; }
        return 0;
    }

    // ------------------------------------------------------------------ the language server
    static LspAnswer[] RunServer(Opt o, ClarionVersionConfig cfg, string serverJs, List<Sample> samples,
        Dictionary<string, string> texts, out List<double> edited, out int editedTimeouts, out string note)
    {
        edited = new List<double>();
        editedTimeouts = 0;
        note = null;
        if (!File.Exists(serverJs)) { note = "no such file"; return null; }
        string nodeLine = null;
        LspTrace.SetSink(s => { if (s.StartsWith("[LSP] Starting:")) nodeLine = s; });
        var client = new LspClient();
        string reddir;
        if (cfg.Macros == null || !cfg.Macros.TryGetValue("reddir", out reddir) || string.IsNullOrEmpty(reddir))
            reddir = string.IsNullOrEmpty(cfg.RedFilePath) ? null : Path.GetDirectoryName(cfg.RedFilePath);
        string ws = Path.GetDirectoryName(o.Sln);
        // The shape LspService.EnsureRunning sends (clarion/updatePaths, PR #37 contract).
        client.SetUpdatePaths(new Dictionary<string, object>
        {
            { "solutionFilePath", o.Sln }, { "redirectionFile", cfg.RedFileName ?? "" }, { "clarionVersion", cfg.Name ?? "" },
            { "configuration", "Debug" }, { "macros", cfg.Macros ?? new Dictionary<string, string>() },
            { "redirectionPaths", reddir != null ? new List<string> { reddir } : new List<string>() },
            { "libsrcPaths", cfg.LibSrcPaths ?? new List<string>() }, { "projectPaths", new List<string> { ws } },
            { "defaultLookupExtensions", new[] { ".clw", ".inc", ".equ", ".int" } }
        });
        var startSw = Stopwatch.StartNew();
        if (!client.Start(serverJs, "file:///" + ws.Replace("\\", "/"), Path.GetFileName(ws)))
        {
            note = client.LastSpawnError ?? "Start returned false";
            return null;
        }
        Console.WriteLine("  " + (nodeLine ?? "(node line not captured)"));
        try
        {
            // Ready = the first hover that answers, on any sample, within 60 s (cold-start time is reported).
            var readySw = Stopwatch.StartNew();
            bool ready = false;
            for (int t = 0; !ready && readySw.Elapsed.TotalSeconds < 60; t++)
            {
                var sm = samples[t % samples.Count];
                if (Symbol(client.GetHover(sm.File, sm.Line0, sm.Col0, texts[sm.File])) != null) ready = true;
                else System.Threading.Thread.Sleep(200);
            }
            Console.WriteLine("  start -> first answer: " + startSw.ElapsedMilliseconds + " ms" + (ready ? "" : "  (NEVER ANSWERED in 60 s)"));
            for (int w = 0; w < Math.Min(5, samples.Count); w++) client.GetHover(samples[w].File, samples[w].Line0, samples[w].Col0, texts[samples[w].File]);

            var res = new LspAnswer[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                var sm = samples[i];
                var sw = Stopwatch.StartNew();
                var r = client.GetHover(sm.File, sm.Line0, sm.Col0, texts[sm.File]);
                double ms = sw.Elapsed.TotalMilliseconds;
                string sym = Symbol(r);
                string card = CardText(r);
                res[i] = new LspAnswer { Any = sym != null, Symbol = sym, Card = card, Ms = ms, TimedOut = IsTimeout(r, ms) };
            }
            var running = new Dictionary<string, string>();
            for (int i = 0; i < Math.Min(o.Edited, samples.Count); i++)
            {
                var sm = samples[i];
                string text = EditFor(o, running, texts, sm, i);
                var sw = Stopwatch.StartNew();
                client.EnsureBufferSynced(sm.File, text);   // the didChange (ranged or full text)
                SendMs.Add(sw.Elapsed.TotalMilliseconds);
                var hw = Stopwatch.StartNew();
                var r = client.GetHover(sm.File, sm.Line0, sm.Col0, text);   // then the hover
                double hoverMs = hw.Elapsed.TotalMilliseconds;
                NextHoverMs.Add(hoverMs);
                edited.Add(sw.Elapsed.TotalMilliseconds);
                if (IsTimeout(r, hoverMs)) editedTimeouts++;   // against the hover's own deadline, not send + hover
            }
            client.GetHover(samples[0].File, samples[0].Line0, samples[0].Col0, texts[samples[0].File]);   // leave it as found
            SyncMode = client.UsesIncrementalSync ? "ranged" : "full";
            RangedSent = client.IncrementalChangesSent; FullSent = client.FullChangesSent;
            Console.WriteLine("  sync: " + (client.UsesIncrementalSync ? "ranged (incremental)" : "full text")
                + "  - changes sent: ranged " + client.IncrementalChangesSent + ", full " + client.FullChangesSent);
            if (SendMs.Count > 0)
                Console.WriteLine(string.Format("  after an edit:  send p50 {0:0.0} p95 {1:0.0} max {2:0.0} ms   |   next hover p50 {3:0.0} p95 {4:0.0} max {5:0.0} ms",
                    P(SendMs, 50), P(SendMs, 95), SendMs.Max(), P(NextHoverMs, 50), P(NextHoverMs, 95), NextHoverMs.Max()));
            return res;
        }
        finally { try { client.Stop(); } catch { } LspTrace.SetSink(null); }
    }

    // ------------------------------------------------------------------ report
    static string Report(Opt o, KeyValuePair<string, string> server, List<Sample> samples, LocalAnswer[] local,
        List<double> localEdited, LspAnswer[] lsp, List<double> lspEdited, int lspEditedTimeouts)
    {
        int n = samples.Count;
        int localAny = 0, localAuth = 0, lspAny = 0, both = 0, localOnly = 0, lspOnly = 0, neither = 0, timeouts = 0;
        int hidden = 0, hiddenDisagree = 0, localWrong = 0, lspWrong = 0;
        var disagreements = new List<string>();
        var lspOnlyEx = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var l = local[i]; var s = lsp[i]; var sm = samples[i];
            if (l.Any) localAny++;
            if (l.Auth) localAuth++;
            if (s.Any) lspAny++;
            if (s.TimedOut) timeouts++;
            if (l.Any && s.Any) both++; else if (l.Any) localOnly++; else if (s.Any) lspOnly++; else neither++;
            string want = Norm(sm.Word);
            if (l.Any && l.Symbol != null && Norm(l.Symbol) != want) localWrong++;
            if (s.Any && s.Symbol != null && Norm(s.Symbol) != want) lspWrong++;
            if (l.Auth && s.Any)
            {
                hidden++;   // the page shows the local card and never asks the server
                if (l.Symbol != null && Norm(l.Symbol) != Norm(s.Symbol))
                {
                    hiddenDisagree++;
                    if (disagreements.Count < 12)
                        disagreements.Add(Path.GetFileName(sm.File) + ":" + (sm.Line0 + 1) + "  '" + sm.Word + "'  local(" + l.Kind + ")=" + l.Symbol + "  server=" + s.Symbol);
                }
            }
            if (!l.Any && s.Any && lspOnlyEx.Count < 8) lspOnlyEx.Add(Path.GetFileName(sm.File) + ":" + (sm.Line0 + 1) + " '" + sm.Word + "'");
        }
        // Where both answer: how much each card says (characters of markdown) - a crude but neutral proxy for detail.
        var bothIdx = Enumerable.Range(0, n).Where(i => local[i].Any && lsp[i].Any).ToList();
        var localLen = bothIdx.Select(i => (double)(local[i].Card ?? "").Length).ToList();
        var serverLen = bothIdx.Select(i => (double)(lsp[i].Card ?? "").Length).ToList();
        var lMs = local.Select(x => x.Ms).ToList();
        var sMs = lsp.Select(x => x.Ms).ToList();
        Func<int, string> pct = c => (100.0 * c / n).ToString("0") + "%";

        // Lead with what is stable between runs of the same build: timeouts and the worst hover after an edit.
        // The p50 swings with machine load (anything else busy at the time), so compare it across interleaved runs only.
        Console.WriteLine(string.Format("  samples: {0}   server timeouts (no answer in 1.5 s): {1} steady, {2} after an edit   worst server hover after an edit: {3:0} ms",
            n, timeouts, lspEditedTimeouts, lspEdited.Count > 0 ? lspEdited.Max() : 0));
        Console.WriteLine("  latency (ms)            p50      p95      max");
        Row("local   steady", lMs); Row("server  steady", sMs);
        Row("local   edited", localEdited); Row("server  edited", lspEdited);
        Console.WriteLine("  coverage: local answers " + pct(localAny) + " (authoritative " + pct(localAuth) + "), server answers " + pct(lspAny)
            + (timeouts > 0 ? ", server timeouts " + timeouts : ""));
        Console.WriteLine("            both " + pct(both) + ", local only " + pct(localOnly) + ", server only " + pct(lspOnly) + ", neither " + pct(neither));
        Console.WriteLine("  hidden:   " + hidden + " server answers never asked for (an authoritative local card won), "
            + hiddenDisagree + " of them naming a different symbol");
        Console.WriteLine("  card names a different word than the one hovered: local " + localWrong + ", server " + lspWrong);
        if (bothIdx.Count > 0)
            Console.WriteLine("  card size where both answer (chars, median): local " + P(localLen, 50).ToString("0") + ", server " + P(serverLen, 50).ToString("0"));
        if (lspOnlyEx.Count > 0) Console.WriteLine("  e.g. server-only: " + string.Join(", ", lspOnlyEx));
        foreach (var d in disagreements) Console.WriteLine("  differs: " + d);

        // The verdict: the numbers that decide it, said plainly either way.
        double gap = P(sMs, 95) - P(lMs, 95);
        bool fastEnough = gap <= o.MarginMs;
        bool coversMore = lspAny > localAny;
        Console.WriteLine(fastEnough
            ? "  FLAG: server p95 is within " + o.MarginMs + " ms of the local layer's (" + gap.ToString("+0;-0") + " ms) - local-first no longer buys speed here."
            : "  ok:   local layer is faster by " + gap.ToString("0") + " ms at p95 (margin " + o.MarginMs + " ms).");
        if (coversMore) Console.WriteLine("  FLAG: the server answers more hovers than the local layer (" + pct(lspAny) + " vs " + pct(localAny) + ").");
        if (hiddenDisagree > 0) Console.WriteLine("  FLAG: " + hiddenDisagree + " authoritative local card(s) name a different symbol than the server.");

        var ser = new JavaScriptSerializer();
        if (!string.IsNullOrEmpty(o.DumpPath))
        {
            // Every sample with both raw cards, for checking any number above by hand. Appended: one run covers
            // several servers (and the runner calls this once per solution); Run-HoverBench.ps1 starts the file fresh.
            var dump = new StringBuilder();
            for (int i = 0; i < n; i++)
                dump.AppendLine(ser.Serialize(new Dictionary<string, object> {
                    { "server", server.Key }, { "file", Path.GetFileName(samples[i].File) }, { "line", samples[i].Line0 + 1 }, { "word", samples[i].Word },
                    { "localKind", local[i].Kind }, { "localAuth", local[i].Auth }, { "localSymbol", local[i].Symbol }, { "localCard", local[i].Card },
                    { "localMs", Math.Round(local[i].Ms, 2) }, { "serverMs", Math.Round(lsp[i].Ms, 2) }, { "serverTimedOut", lsp[i].TimedOut },
                    { "serverSymbol", lsp[i].Symbol }, { "serverCard", lsp[i].Card } }));
            File.AppendAllText(o.DumpPath, dump.ToString());
        }
        return ser.Serialize(new Dictionary<string, object>
        {
            { "when", DateTime.Now.ToString("s") }, { "caCommit", o.CaCommit }, { "server", server.Key },
            { "serverJs", server.Value }, { "serverSha256", Sha(server.Value) }, { "serverBuild", BuildSha(server.Value) }, { "serverEditedMax", Math.Round(lspEdited.Count > 0 ? lspEdited.Max() : 0, 1) }, { "solution", o.Sln },
            { "files", o.Files.Select(Path.GetFileName).ToArray() }, { "samples", n },
            { "localP50", Math.Round(P(lMs, 50), 1) }, { "localP95", Math.Round(P(lMs, 95), 1) },
            { "serverP50", Math.Round(P(sMs, 50), 1) }, { "serverP95", Math.Round(P(sMs, 95), 1) },
            { "localEditedP95", Math.Round(P(localEdited, 95), 1) }, { "serverEditedP95", Math.Round(P(lspEdited, 95), 1) },
            { "localAnswers", localAny }, { "localAuthoritative", localAuth }, { "serverAnswers", lspAny },
            { "serverOnly", lspOnly }, { "localOnly", localOnly }, { "hidden", hidden }, { "hiddenDisagree", hiddenDisagree },
            // What "after an edit" meant for this line: end and near runs (and ranged vs full sync) are not comparable.
            { "editMode", o.EditMode }, { "sync", SyncMode }, { "rangedChanges", RangedSent }, { "fullChanges", FullSent },
            { "sendP95", Math.Round(P(SendMs, 95), 1) }, { "nextHoverP95", Math.Round(P(NextHoverMs, 95), 1) },
            { "serverTimeouts", timeouts }, { "serverEditedTimeouts", lspEditedTimeouts },{ "localCardChars", P(localLen, 50) }, { "serverCardChars", P(serverLen, 50) }, { "flagFastEnough", fastEnough }, { "flagCoversMore", coversMore }
        });
    }

    static void Row(string name, List<double> ms)
    {
        if (ms.Count == 0) { Console.WriteLine("  " + name + "      -"); return; }
        Console.WriteLine(string.Format("  {0,-20} {1,8:0.0} {2,8:0.0} {3,8:0.0}", name, P(ms, 50), P(ms, 95), ms.Max()));
    }

    static double P(List<double> v, int p)
    {
        if (v == null || v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        int i = (int)Math.Ceiling(p / 100.0 * s.Count) - 1;
        return s[Math.Max(0, Math.Min(s.Count - 1, i))];
    }

    // ------------------------------------------------------------------ the local layer
    static LocalAnswer LocalHover(LocalSource src, Sample sm)
    {
        var sw = Stopwatch.StartNew();
        LocalHoverResult h = null;
        try { h = LocalLayerHandlers.HoverAt(src, sm.Line0, sm.Col0, new LocalLayerOptions { FileName = Path.GetFileName(sm.File) }); }
        catch { h = null; }
        double ms = sw.Elapsed.TotalMilliseconds;
        bool any = h != null && !string.IsNullOrEmpty(h.Markdown);
        return new LocalAnswer { Any = any, Auth = any && h.Authoritative, Kind = h != null ? (h.Kind ?? "local") : null,
                                 Symbol = any ? LocalSymbol(h.Markdown) : null, Card = any ? h.Markdown : null, Ms = ms };
    }

    // A local card opens with a code block whose first token is the symbol ("```clarion\nSelCustID  LONG").
    static string LocalSymbol(string md)
    {
        var m = Regex.Match(md, @"```clarion\s*\n\s*([A-Za-z_][\w:.]*)");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(md, @"\*\*([^*]+)\*\*");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // The server card's markdown, or null when there is no card.
    static string CardText(Dictionary<string, object> r)
    {
        if (r == null) return null;
        object res;
        if (!r.TryGetValue("result", out res) || res == null) return null;
        var d = res as Dictionary<string, object>;
        object contents = d != null && d.ContainsKey("contents") ? d["contents"] : null;
        string md = null;
        if (contents is string) md = (string)contents;
        else if (contents is Dictionary<string, object>) { object v; if (((Dictionary<string, object>)contents).TryGetValue("value", out v)) md = v as string; }
        else if (contents is object[]) md = string.Join("\n", ((object[])contents).Select(x => x is Dictionary<string, object> ? ((Dictionary<string, object>)x)["value"] as string : x as string));
        return string.IsNullOrWhiteSpace(md) ? null : md;
    }

    static string Symbol(Dictionary<string, object> r)
    {
        string md = CardText(r);
        return md == null ? null : SymbolFromCard(md);
    }

    // The symbol a server card is about. Calibrated on the real cards (--dump), which open:
    //   **Name** — `TYPE`              most symbols
    //   **Routine:** `Name`            a label ending ':' then the name in backticks (Routine, Parameter, <Queue> Field)
    //   **Built-in Function: MESSAGE** / **Attribute: AT**   the name after the label (Norm takes the last segment)
    //   **?Cancel** — `BUTTON`         a control: '?' is part of the label, not of the word hovered
    static string SymbolFromCard(string md)
    {
        var m = Regex.Match(md, @"\*\*([^*]+)\*\*(\s*`([^`]+)`)?");
        if (m.Success)
        {
            string bold = m.Groups[1].Value.Trim();
            if (bold.EndsWith(":") && m.Groups[3].Success) return m.Groups[3].Value.Trim();
            return bold.TrimStart('?');
        }
        m = Regex.Match(md, @"```clarion\s*\n\s*([A-Za-z_][\w:.]*)");
        return m.Success ? m.Groups[1].Value : md.Trim().Split('\n')[0];
    }

    // Compare names the way a reader would: the last segment (CUS:Name -> name, Built-in Function: MESSAGE -> message).
    static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        int cut = Math.Max(s.LastIndexOf(':'), s.LastIndexOf('.'));
        if (cut >= 0 && cut < s.Length - 1) s = s.Substring(cut + 1);
        s = s.Trim().Split(' ', '(', '\t')[0];
        return s.ToLowerInvariant();
    }

    // ------------------------------------------------------------------ sampling
    // A Clarion name can carry several colon segments (PRE:Field, Hide:Relate:Customer); a dot starts a member.
    static readonly Regex Ident = new Regex(@"(?<![A-Za-z0-9_:])[A-Za-z_][A-Za-z0-9_]*(:[A-Za-z_][A-Za-z0-9_]*)*");

    // Every identifier outside comments and strings in the code, then `perFile` of them evenly spread (deterministic).
    static List<Sample> SampleFile(string file, string text, int perFile)
    {
        var all = new List<Sample>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            foreach (Match m in Ident.Matches(line))
            {
                int col = m.Index + Math.Min(1, m.Length - 1);
                if (LocalScopeIndex.IsInsideStringOrComment(line, col)) continue;
                all.Add(new Sample { File = file, Line0 = i, Col0 = col, Word = m.Value });
            }
        }
        if (all.Count <= perFile) return all;
        var pick = new List<Sample>(perFile);
        double step = (double)all.Count / perFile;
        for (int k = 0; k < perFile; k++) pick.Add(all[(int)(k * step)]);
        return pick;
    }

    // An edit that changes the text but none of the sampled positions: a comment line appended at the end.
    static string Edit(string text, int i) { return text + "\r\n! hover-bench edit " + i; }

    // The largest modules the solution's projects actually compile: the .cwproj files the .sln names, their
    // <Compile Include> entries, each found beside the project or anywhere under the solution folder (generated
    // modules usually sit in a subfolder the .red points at, e.g. genfiles\source). Every .clw in the folder only
    // when no project says. A stray "X - Copy.clw" is not a module, and neither is another solution's.
    // The text for edit i before hovering sample sm.
    //   end  - the original text plus one comment line at the END of the file. Every hover is then OUTSIDE the edit,
    //          which flatters any server that skips work for hovers away from the change.
    //   near - a comment appended to the line ABOVE the hovered line: typing in a procedure, then hovering nearby.
    //          Edits ACCUMULATE per file, as typing does (rebuilding from the original each time would make every change
    //          span the gap between two edit sites). No line is added, so the samples keep their positions.
    static string EditFor(Opt o, Dictionary<string, string> running, Dictionary<string, string> texts, Sample sm, int i)
    {
        if (o.EditMode != "near") return Edit(texts[sm.File], i);
        string cur;
        if (!running.TryGetValue(sm.File, out cur)) cur = texts[sm.File];
        int target = sm.Line0 > 0 ? sm.Line0 - 1 : sm.Line0, line = 0, at = 0;
        while (line < target && at < cur.Length)
        {
            int nl = cur.IndexOf('\n', at);
            if (nl < 0) { at = cur.Length; break; }
            at = nl + 1; line++;
        }
        int end = at;
        while (end < cur.Length && cur[end] != '\r' && cur[end] != '\n') end++;
        string next = cur.Substring(0, end) + " !e" + i + cur.Substring(end);
        running[sm.File] = next;
        return next;
    }

    static List<string> DefaultFiles(string sln, int max)
    {
        string dir = Path.GetDirectoryName(sln);
        var projects = Regex.Matches(File.ReadAllText(sln), @"""([^""]+\.cwproj)""", RegexOptions.IgnoreCase)
            .Cast<Match>().Select(m => Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value))).Where(File.Exists).Distinct().ToList();
        Dictionary<string, string> byName = null;   // built on first need: every .clw under the solution folder
        var compiled = new List<string>();
        foreach (var proj in projects)
            foreach (Match m in Regex.Matches(File.ReadAllText(proj), @"<Compile\s+Include=""([^""]+\.clw)""", RegexOptions.IgnoreCase))
            {
                string p = Path.Combine(Path.GetDirectoryName(proj), m.Groups[1].Value);
                if (!File.Exists(p))
                {
                    if (byName == null)
                    {
                        byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in Directory.GetFiles(dir, "*.clw", SearchOption.AllDirectories))
                            if (!byName.ContainsKey(Path.GetFileName(f))) byName[Path.GetFileName(f)] = f;
                    }
                    byName.TryGetValue(Path.GetFileName(p), out p);
                }
                if (p != null && File.Exists(p) && !compiled.Contains(p, StringComparer.OrdinalIgnoreCase)) compiled.Add(p);
            }
        var pool = compiled.Count > 0 ? compiled : Directory.GetFiles(dir, "*.clw").ToList();
        return pool.OrderByDescending(f => new FileInfo(f).Length).Take(max).ToList();
    }

    // ------------------------------------------------------------------ setup
    static ClarionVersionConfig FindVersion(Opt o)
    {
        var paths = new List<string>();
        if (o.PropsXml != null) paths.Add(o.PropsXml);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftVelocity", "Clarion");
        if (Directory.Exists(root))
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(d => d))
                if (File.Exists(Path.Combine(d, "ClarionProperties.xml"))) paths.Add(Path.Combine(d, "ClarionProperties.xml"));
        foreach (var p in paths)
        {
            var info = ClarionVersionService.ParsePropertiesXml(p);
            var v = info != null ? info.Versions.Find(x => string.Equals(x.Name, o.Version, StringComparison.OrdinalIgnoreCase)) : null;
            if (v != null) return v;
        }
        return null;
    }

    // A fingerprint of the whole server BUILD, not just its entry file: server.js barely changes between builds
    // while the code it loads does. Every file under out\server and out\common (the server loads both, and its
    // data\*.json, by relative path), by relative path and content. "" when server.js is not under an out\ folder.
    static string BuildSha(string serverJs)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(serverJs)));
            while (dir != null && !string.Equals(dir.Name, "out", StringComparison.OrdinalIgnoreCase)) dir = dir.Parent;
            if (dir == null) return "";
            var files = new List<string>();
            foreach (var sub in new[] { "server", "common" })
            {
                string p = Path.Combine(dir.FullName, sub);
                if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "*", SearchOption.AllDirectories));
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            using (var all = SHA256.Create())
            using (var one = SHA256.Create())
            {
                foreach (var f in files)
                {
                    byte[] name = Encoding.UTF8.GetBytes(f.Substring(dir.FullName.Length).ToLowerInvariant() + "\0");
                    byte[] body;
                    using (var s = File.OpenRead(f)) body = one.ComputeHash(s);
                    all.TransformBlock(name, 0, name.Length, null, 0);
                    all.TransformBlock(body, 0, body.Length, null, 0);
                }
                all.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(all.Hash).Replace("-", "").Substring(0, 12).ToLowerInvariant() + "/" + files.Count;
            }
        }
        catch { return ""; }
    }

    static string Sha(string path)
    {
        try { using (var s = SHA256.Create()) using (var f = File.OpenRead(path)) return BitConverter.ToString(s.ComputeHash(f)).Replace("-", "").Substring(0, 12).ToLowerInvariant(); }
        catch { return ""; }
    }

    static Opt Parse(string[] a)
    {
        var o = new Opt();
        for (int i = 0; i < a.Length; i++)
        {
            string k = a[i];
            Func<string> next = () => { if (i + 1 >= a.Length) throw new ArgumentException(k + " needs a value"); return a[++i]; };
            switch (k)
            {
                case "--sln": o.Sln = Path.GetFullPath(next()); break;
                case "--version": o.Version = next(); break;
                case "--props": o.PropsXml = next(); break;
                case "--file": o.Files.Add(Path.GetFullPath(next())); break;
                case "--server":
                    string v = next(); int eq = v.IndexOf('=');
                    o.Servers.Add(eq > 0 ? new KeyValuePair<string, string>(v.Substring(0, eq), v.Substring(eq + 1))
                                         : new KeyValuePair<string, string>(Path.GetFileName(Path.GetDirectoryName(v)), v));
                    break;
                case "--project-db": o.ProjectDb = next(); break;
                case "--library-db": o.LibraryDb = next(); break;
                case "--max-files": o.MaxFiles = int.Parse(next()); break;
                case "--per-file": o.PerFile = int.Parse(next()); break;
                case "--edited": o.Edited = int.Parse(next()); break;
                case "--margin-ms": o.MarginMs = int.Parse(next()); break;
                case "--history": o.HistoryPath = next(); break;
                case "--ca-commit": o.CaCommit = next(); break;
                case "--dump": o.DumpPath = next(); break;
                case "--print-red": o.PrintRed = true; break;
                case "--print-graph-hash": o.PrintGraphHash = true; break;
                case "--edit-mode": o.EditMode = next().ToLowerInvariant(); break;
                default: throw new ArgumentException("unknown option " + k);
            }
        }
        if (o.Sln == null || !File.Exists(o.Sln)) throw new ArgumentException("--sln <solution.sln> is required");
        if (o.Version == null) throw new ArgumentException("--version <Clarion version name> is required");
        // A typo would otherwise run as 'end' and be recorded as whatever the caller thought it asked for.
        if (o.EditMode != "end" && o.EditMode != "near") throw new ArgumentException("--edit-mode is 'end' or 'near', not '" + o.EditMode + "'");
        if (o.Servers.Count == 0 && !o.PrintRed && !o.PrintGraphHash) throw new ArgumentException("at least one --server name=<server.js> is required");
        return o;
    }
}
