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
        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
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

    // ------------------------------------------------------------------ localCompletion / localHover (v form)

    // A module-shaped buffer: module data, a MAP, one procedure with a parameter, locals and a class-typed
    // local. Lines are 1-based Monaco lines.
    static readonly string[] ModLines = {
        "  MEMBER('app')",              // 1
        "Clip       LONG",              // 2  module data named like a built-in
        "  MAP",                        // 3
        "Other        PROCEDURE",       // 4
        "  END",                        // 5
        "TestProc PROCEDURE(LONG pCount)", // 6
        "glovar     LONG",              // 7  a local that clashes (by case) with the DB global GloVar
        "loTotal    LONG",              // 8
        "obj        &MyBrowse",         // 9
        "  CODE",                       // 10
        "  lo",                         // 11  bare prefix
        "  obj.",                       // 12  member access
        "  INV:",                       // 13  dictionary qualifier
        "  Glo",                        // 14  solution globals
        "  RETURN loTotal",             // 15  hover: keyword / local
        "  x# = INV:Qty + GloVar + Clip" }; // 16  hover: dictionary / DB / local-vs-keyword
    static readonly string ModBuffer = string.Join("\r\n", ModLines);

    static Dictionary<string, object> At(string action, int line, int column, LocalLayerOptions o)
    {
        return LocalLayerHandlers.Handle(action, ModBuffer, Req("{\"line\":" + line + ",\"column\":" + column + "}"), o);
    }

    static List<string> Labels(Dictionary<string, object> reply)
    {
        return ((List<Dictionary<string, object>>)reply["items"]).Select(i => (string)i["label"]).ToList();
    }

    static void Exec(System.Data.SQLite.SQLiteConnection cn, string sql)
    {
        using (var cmd = new System.Data.SQLite.SQLiteCommand(sql, cn)) cmd.ExecuteNonQuery();
    }

    /// <summary>A CodeGraph-schema DB; <paramref name="indexed"/> = through the indexer's write open (NOCASE
    /// indexes), else the pre-1c685f2e schema with none.</summary>
    static void BuildDb(string path, bool indexed, params string[][] rows)
    {
        if (indexed) { using (var db = new ClarionCodeGraph.Graph.CodeGraphDatabase()) db.Open(path); }
        else System.Data.SQLite.SQLiteConnection.CreateFile(path);
        using (var cn = new System.Data.SQLite.SQLiteConnection("Data Source=" + path + ";Version=3;"))
        {
            cn.Open();
            if (!indexed)
            {
                Exec(cn, "CREATE TABLE projects (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, guid TEXT, cwproj_path TEXT, output_type TEXT, sln_path TEXT)");
                Exec(cn, "CREATE TABLE symbols (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, type TEXT NOT NULL, file_path TEXT NOT NULL, line_number INTEGER, project_id INTEGER, params TEXT, return_type TEXT, parent_name TEXT, member_of TEXT, scope TEXT, source_preview TEXT, decl_kind TEXT)");
            }
            Exec(cn, "INSERT INTO projects (id, name) VALUES (1, 'proj')");
            foreach (var r in rows)
                using (var cmd = new System.Data.SQLite.SQLiteCommand("INSERT INTO symbols (name, type, file_path, line_number, project_id, params, parent_name, scope) VALUES (@n, @t, 'x.clw', 1, 1, @p, @par, @s)", cn))
                {
                    cmd.Parameters.AddWithValue("@n", r[0]); cmd.Parameters.AddWithValue("@t", r[1]);
                    cmd.Parameters.AddWithValue("@s", r[2]); cmd.Parameters.AddWithValue("@par", (object)r[3] ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@p", (object)r[4] ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
        }
    }

    static ClarionAppDataReader.FieldDef F(string n, string t) { return new ClarionAppDataReader.FieldDef { Name = n, Type = t }; }

    static void CompletionAndHover(List<string> log)
    {
        string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ca-locallayer-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        System.IO.Directory.CreateDirectory(work);
        string proj = System.IO.Path.Combine(work, "proj.codegraph.db"), lib = System.IO.Path.Combine(work, "ClarionGraph_t.db"),
               old = System.IO.Path.Combine(work, "old.codegraph.db");
        var projRows = new[] {
            new[] { "GloVar", "variable", "global", null, "LONG" },
            new[] { "GloLocal", "variable", "local", "SomeProc", "LONG" },
            new[] { "GloParam", "variable", "parameter", "SomeProc", "LONG" },
            new[] { "MyBrowse", "class", "global", "BrowseClass", null },
            new[] { "MyBrowse.Custom", "procedure", "global", "MyBrowse", "()" },
            new[] { "MyBrowse.ResetSort", "procedure", "global", "MyBrowse", "(BYTE Force)" },
            // L3: PRM002 has a PROCEDURE named like the PASSWORD attribute; a same-named variable is listed FIRST so
            // only the call-context preference picks the procedure.
            new[] { "PASSWORD", "variable", "global", null, "STRING(20)" },
            new[] { "PASSWORD", "procedure", "global", null, "(STRING pType, LONG pLevel)" } };
        BuildDb(proj, true, projRows);
        BuildDb(lib, true,
            new[] { "BrowseClass", "class", "global", "ViewManager", null },
            new[] { "BrowseClass.ResetSort", "procedure", "global", "BrowseClass", "(BYTE Force)" },
            new[] { "BrowseClass.TakeKey", "procedure", "global", "BrowseClass", "()" },
            new[] { "ViewManager", "class", "global", null, null },
            new[] { "ViewManager.Open", "procedure", "global", "ViewManager", "()" });
        BuildDb(old, false, projRows);

        var inv = new ClarionAppDataReader.TableDef { Name = "Inventory", Prefix = "INV" };
        inv.Fields.Add(F("Qty", "LONG"));
        inv.Fields.Add(F("Descr", "STRING(40)"));
        LiveDictionaryIndex.Publish(new Dictionary<string, ClarionAppDataReader.TableDef>(StringComparer.OrdinalIgnoreCase) { { inv.Name, inv } });

        var o = new LocalLayerOptions { ProcedureName = "TestProc", SlotChecks = true, Log = log.Add,
                                        FileName = "mod.clw" };
        LocalLayerHandlers.ProjectDbPath = () => proj;
        LocalLayerHandlers.LibraryDbPath = () => lib;
        try
        {
            LocalLayerHandlers.ResetPathCache();
            SharedLspBridge.Reset();
            SharedLspBridge.Running = false;   // 4.2: the LSP is down (and 4.1: nothing below may ask it anyway)

            Console.WriteLine("\n4.1-4.4 localCompletion (v form), LSP down");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lo = Labels(At("localCompletion", 11, 5, o));
            long loMs = sw.ElapsedMilliseconds;
            Check("4.1/4.2 'lo' answers locals at once with the LSP down (" + loMs + " ms, first call opens the DBs)",
                loMs < 300 && lo.Contains("loTotal", StringComparer.OrdinalIgnoreCase), string.Join(",", lo));
            Check("...keywords join the list (LOOP), after the buffer's own names",
                lo.Contains("LOOP") && lo.IndexOf("LOOP") > lo.FindIndex(l => l.Equals("loTotal", StringComparison.OrdinalIgnoreCase)), string.Join(",", lo));

            var mem = Labels(At("localCompletion", 12, 7, o));
            Check("4.3 'obj.' -> the declared class's members, inherited across both DBs (Custom ResetSort TakeKey Open)",
                new[] { "Custom", "ResetSort", "TakeKey", "Open" }.All(x => mem.Contains(x, StringComparer.OrdinalIgnoreCase)), string.Join(",", mem));
            Check("...ResetSort (declared on both MyBrowse and BrowseClass) listed once",
                mem.Count(x => x.Equals("ResetSort", StringComparison.OrdinalIgnoreCase)) == 1, string.Join(",", mem));

            var dict = Labels(At("localCompletion", 13, 7, o));
            Check("4.4 'INV:' -> the live dictionary's fields", dict.Contains("INV:Qty") && dict.Contains("INV:Descr"), string.Join(",", dict));

            var glo = Labels(At("localCompletion", 14, 6, o));
            Check("'Glo' -> the local glovar and the index's GloVar are ONE item, and the local wins (its spelling)",
                glo.Count(x => x.Equals("glovar", StringComparison.OrdinalIgnoreCase)) == 1 && glo.Contains("glovar"), string.Join(",", glo));
            SharedLspBridge.Reset();
            var gloDb = Labels(LocalLayerHandlers.Handle("localCompletion", ModBuffer.Replace("glovar     LONG", "other      LONG"),
                Req("{\"line\":14,\"column\":6}"), o));
            Check("'Glo' with no local clash -> the solution's GloVar from the index", gloDb.Contains("GloVar"), string.Join(",", gloDb));
            Check("...never a procedure's local or parameter row (the scope leak)", !glo.Contains("GloLocal") && !glo.Contains("GloParam"), string.Join(",", glo));

            Check("no call reached the language server", SharedLspBridge.TotalCalls == 0, "calls=" + SharedLspBridge.TotalCalls);

            Console.WriteLine("\nlocalHover (v form)");
            var hLocal = At("localHover", 15, 12, o);
            Check("a local -> its card, authoritative", hLocal["contents"] != null && ((string)hLocal["contents"]).Contains("loTotal") && (bool)hLocal["authoritative"],
                Json(hLocal));
            // H4: keyword data NOT loaded (a directory that does not exist) -> name + category only, not final.
            ClarionKeywordIndex.DataDirOverride = System.IO.Path.Combine(work, "no-keyword-data");
            ClarionKeywordIndex.ResetForTest();
            ClarionKeywordIndex.WaitForLoad(5000);
            var hKw = At("localHover", 15, 4, o);
            Check("4.5/H4 RETURN with no keyword data -> the name+category card, NOT authoritative", hKw["contents"] != null && !(bool)hKw["authoritative"], Json(hKw));
            // H4: with the keyword data loaded, the card carries its description and is FINAL.
            ClarionKeywordIndex.DataDirOverride = KeywordDataDir;
            ClarionKeywordIndex.ResetForTest();
            Check("(keyword fixture loads)", ClarionKeywordIndex.WaitForLoad(5000) && ClarionKeywordIndex.IsFinalCard("RETURN"));
            var hKwFull = At("localHover", 15, 4, o);
            Check("H4 RETURN with its loaded description -> the full card, AUTHORITATIVE",
                hKwFull["contents"] != null && ((string)hKwFull["contents"]).Contains("Terminates") && (bool)hKwFull["authoritative"], Json(hKwFull));
            var hDict = At("localHover", 16, 12, o);
            Check("H4 a dictionary field (INV:Qty) -> the dictionary card, AUTHORITATIVE", hDict["contents"] != null && ((string)hDict["contents"]).Contains("Qty") && (bool)hDict["authoritative"], Json(hDict));
            var hDb = LocalLayerHandlers.Handle("localHover", ModBuffer.Replace("glovar     LONG", "other      LONG"),
                Req("{\"line\":16,\"column\":22}"), o);
            Check("H4 a solution global (GloVar) -> the index card, AUTHORITATIVE",
                hDb["contents"] != null && ((string)hDb["contents"]).Contains("GloVar") && (bool)hDb["authoritative"], Json(hDb));
            ClarionKeywordIndex.DataDirOverride = System.IO.Path.Combine(work, "no-keyword-data");
            ClarionKeywordIndex.ResetForTest();
            ClarionKeywordIndex.WaitForLoad(5000);
            Console.WriteLine("\nL3: a keyword card is final only for reserved words; a call prefers a procedure");
            {
                string kwDir = System.IO.Path.Combine(work, "kw-l3");
                System.IO.Directory.CreateDirectory(kwDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(kwDir, "clarion-keywords.json"),
                    "{\"keywords\":[{\"name\":\"RETURN\",\"description\":\"Terminates the procedure.\",\"category\":\"Control Flow\"}]}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(kwDir, "clarion-attributes.json"),
                    "{\"attributes\":[{\"name\":\"PASSWORD\",\"description\":\"Specifies a password entry field.\",\"category\":\"Control\"}]}");
                ClarionKeywordIndex.DataDirOverride = kwDir;
                ClarionKeywordIndex.ResetForTest();
                ClarionKeywordIndex.WaitForLoad(5000);
                string call = ModBuffer.Replace("  x# = INV:Qty + GloVar + Clip", "  x# = ~PASSWORD('IN',103) + 1");

                var withDb = LocalLayerHandlers.Handle("localHover", call, Req("{\"line\":16,\"column\":10}"), o);
                Check("L3 PASSWORD( with a DB procedure -> the PROCEDURE card (not the attribute, not the same-named variable)",
                    withDb["contents"] != null && ((string)withDb["contents"]).Contains("pLevel") && (bool)withDb["authoritative"], Json(withDb));

                LocalLayerHandlers.ProjectDbPath = null;
                LocalLayerHandlers.ResetPathCache();
                var noDb = LocalLayerHandlers.Handle("localHover", call, Req("{\"line\":16,\"column\":10}"), new LocalLayerOptions { Log = log.Add });
                Check("L3 PASSWORD with no DB -> the attribute card, NOT authoritative (the LSP may know a procedure)",
                    noDb["contents"] != null && ((string)noDb["contents"]).Contains("password entry") && !(bool)noDb["authoritative"], Json(noDb));
                var ret = At("localHover", 15, 4, o);
                Check("L3 RETURN (a reserved keyword with its description) -> authoritative", ret["contents"] != null && (bool)ret["authoritative"], Json(ret));
                Check("L3 FollowedByParen: '~PASSWORD(' yes, 'PASSWORD +' no",
                    LocalLayerHandlers.FollowedByParen("  x# = ~PASSWORD('IN')", 10, "PASSWORD") &&
                    !LocalLayerHandlers.FollowedByParen("  x# = PASSWORD + 1", 9, "PASSWORD"));
                LocalLayerHandlers.ProjectDbPath = () => proj;
                LocalLayerHandlers.ResetPathCache();
                ClarionKeywordIndex.DataDirOverride = System.IO.Path.Combine(work, "no-keyword-data");
                ClarionKeywordIndex.ResetForTest();
                ClarionKeywordIndex.WaitForLoad(5000);
            }

            var hClip = At("localHover", 16, 28, o);
            Check("a module variable named like a built-in (Clip) -> the buffer's card wins over the keyword",
                hClip["contents"] != null && ((string)hClip["contents"]).Contains("LONG"), Json(hClip));
            var hOff = At("localHover", 8, 2, o);
            Check("4.6 Monaco line 8 is loTotal: no LSP header offset is applied to a local lookup",
                hOff["contents"] != null && ((string)hOff["contents"]).Contains("loTotal"), Json(hOff));

            Console.WriteLine("\nL1: no provider (no CA chat open) -> the nearest *.codegraph.db above the module");
            {
                string walkRoot = System.IO.Path.Combine(work, "walk");
                string source = System.IO.Path.Combine(walkRoot, "v61PRM002", "source");
                System.IO.Directory.CreateDirectory(source);
                System.IO.File.Copy(proj, System.IO.Path.Combine(walkRoot, "v61POSitive.codegraph.db"));
                LocalLayerHandlers.ProjectDbPath = null;
                LocalLayerHandlers.ResetPathCache();
                var oWalk = new LocalLayerOptions { Log = log.Add, FileName = System.IO.Path.Combine(source, "PRM002023.clw") };
                string walkBuf = ModBuffer.Replace("glovar     LONG", "other      LONG");
                var walked = Labels(LocalLayerHandlers.Handle("localCompletion", walkBuf, Req("{\"line\":14,\"column\":6}"), oWalk));
                Check("L1 'Glo' finds GloVar through the walk-up (module ...\\v61PRM002\\source\\, DB two levels up)", walked.Contains("GloVar"), string.Join(",", walked));
                var hWalk = LocalLayerHandlers.Handle("localHover", walkBuf, Req("{\"line\":16,\"column\":22}"), oWalk);
                Check("L1 ...and hover on GloVar answers from it", hWalk["contents"] != null && ((string)hWalk["contents"]).Contains("GloVar"), Json(hWalk));
                var oNoFile = new LocalLayerOptions { Log = log.Add };
                Check("L1 no provider and no module path -> no project DB (no crash)",
                    !Labels(LocalLayerHandlers.Handle("localCompletion", walkBuf, Req("{\"line\":14,\"column\":6}"), oNoFile)).Contains("GloVar"));
                LocalLayerHandlers.ProjectDbPath = () => proj;
                LocalLayerHandlers.ResetPathCache();
            }

            SliceForm(o, log);

            Console.WriteLine("\na DB without the NOCASE indexes is skipped in this lane (its fallback costs ~150 ms)");
            var oOld = new LocalLayerOptions { Log = log.Add };
            LocalLayerHandlers.ProjectDbPath = () => old;
            LocalLayerHandlers.LibraryDbPath = null;
            LocalLayerHandlers.ResetPathCache();
            // Every lookup passes fastOnly, so even the FIRST request never runs the old DB's fallback scan.
            // (No local glovar here: it would dedupe GloVar away and hide what this check is about.)
            string noClash = ModBuffer.Replace("glovar     LONG", "other      LONG");
            var gloOld = Labels(LocalLayerHandlers.Handle("localCompletion", noClash, Req("{\"line\":14,\"column\":6}"), oOld));
            Check("an old-schema project DB contributes nothing, from the first request on (fastOnly)", !gloOld.Contains("GloVar"), string.Join(",", gloOld));
        }
        finally
        {
            LocalLayerHandlers.ProjectDbPath = null;
            LocalLayerHandlers.LibraryDbPath = null;
            LocalLayerHandlers.ResetPathCache();
            LiveDictionaryIndex.Publish(null);
            SymbolIndex.ReleaseAll();
            try { System.IO.Directory.Delete(work, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ R11b: span map + slice requests

    static string Lines(int first, int last) { return string.Join("\r\n", ModLines.Skip(first - 1).Take(last - first + 1)); }

    static string SliceReq(int line, int column, string headerHash, params string[] pieces)
    {
        return "{\"line\":" + line + ",\"column\":" + column + ",\"slice\":{\"headerHash\":" + Json(headerHash) +
               ",\"routines\":[],\"pieces\":[" + string.Join(",", pieces) + "]}}";
    }

    static string HashPiece(int start, string hash) { return "{\"start\":" + start + ",\"hash\":" + Json(hash) + "}"; }
    static string TextPiece(int start, string text) { return "{\"start\":" + start + ",\"text\":" + Json(text) + "}"; }

    static void SliceForm(LocalLayerOptions o, List<string> log)
    {
        Console.WriteLine("\nR11b spanMap push (built from the full buffer after an idle sync)");
        log.Clear();
        string msg = LocalLayerHandlers.SpanMapMessage(7, ModBuffer, log.Add);
        var map = msg == null ? null : Req(msg);
        var procs = map == null ? new object[0] : LocalLayerHandlers.AsArray(map["procs"]);
        var proc = procs.Cast<Dictionary<string, object>>().FirstOrDefault(p => (string)p["name"] == "TestProc");
        Check("{type:'spanMap', v, headerHash, procs} for the synced version",
            map != null && (string)map["type"] == "spanMap" && Convert.ToInt64(map["v"]) == 7 && !string.IsNullOrEmpty(map["headerHash"] as string), msg);
        Check("...TestProc spans header 6, CODE at 10, with its DATA hash",
            proc != null && (int)proc["start"] == 6 && (int)proc["dataEnd"] == 10 && !string.IsNullOrEmpty(proc["dataHash"] as string) && proc.ContainsKey("routineSpans"),
            proc == null ? "(no TestProc)" : Json(proc));
        Check("...and one [local-timing] action=spanMap line", log.Count == 1 && log[0].StartsWith("[local-timing] action=spanMap v=7 ms="), log.FirstOrDefault());
        string rtnMsg = LocalLayerHandlers.SpanMapMessage(8, ModBuffer + "\r\nRtn ROUTINE\r\nrl LONG\r\n  CODE\r\n  rl = 1", null);
        var rtnProc = LocalLayerHandlers.AsArray(Req(rtnMsg)["procs"]).Cast<Dictionary<string, object>>().First(p => (string)p["name"] == "TestProc");
        var rs = LocalLayerHandlers.AsArray(rtnProc["routineSpans"]).Cast<Dictionary<string, object>>().ToList();
        Check("routineSpans carry name, start, dataEnd and their own dataHash",
            rs.Count == 1 && (string)rs[0]["name"] == "Rtn" && (int)rs[0]["start"] == 17 && (int)rs[0]["dataEnd"] == 19 && !string.IsNullOrEmpty(rs[0]["dataHash"] as string), Json(rs));
        Check("a buffer with no procedure pushes nothing", LocalLayerHandlers.SpanMapMessage(9, "  MEMBER()\r\nX LONG", null) == null);
        if (proc == null) return;

        string header = (string)map["headerHash"], data = (string)proc["dataHash"];
        string window = TextPiece(11, Lines(11, 16));
        SharedLspBridge.Reset();

        Console.WriteLine("\nR11b slice requests: hash-only DATA piece + the caret window, no synced buffer");
        var vLo = Labels(At("localCompletion", 11, 5, o));
        var sLo = Labels(LocalLayerHandlers.Handle("localCompletion", null, Req(SliceReq(11, 5, header, HashPiece(6, data), window)), o));
        Check("'lo' from the slice = 'lo' from the full buffer", string.Join(",", sLo) == string.Join(",", vLo), string.Join(",", sLo) + " vs " + string.Join(",", vLo));
        var sMem = Labels(LocalLayerHandlers.Handle("localCompletion", null, Req(SliceReq(12, 7, header, HashPiece(6, data), window)), o));
        Check("'obj.' from the slice: the declared class's members from the DBs (the local's type comes from the hashed DATA piece)",
            new[] { "Custom", "ResetSort", "TakeKey", "Open" }.All(x => sMem.Contains(x, StringComparer.OrdinalIgnoreCase)), string.Join(",", sMem));
        var sHover = LocalLayerHandlers.Handle("localHover", null, Req(SliceReq(15, 12, header, HashPiece(6, data), window)), o);
        Check("hover on a local from the slice: its card, authoritative", sHover["contents"] != null && (bool)sHover["authoritative"], Json(sHover));
        var sKw = LocalLayerHandlers.Handle("localHover", null, Req(SliceReq(15, 4, header, HashPiece(6, data), window)), o);
        Check("hover on RETURN from the slice: the keyword card (the caret line comes from the window piece)", sKw["contents"] != null && !(bool)sKw["authoritative"], Json(sKw));
        var sDecl = LocalLayerHandlers.Handle("localHover", null, Req(SliceReq(9, 14, header, HashPiece(6, data), window)), o);
        Check("hover inside the hash-only DATA piece (on &MyBrowse): answered, the caret line resolved from the cache",
            sDecl["contents"] != null, Json(sDecl));
        Check("no call reached the language server", SharedLspBridge.TotalCalls == 0, "calls=" + SharedLspBridge.TotalCalls);

        Console.WriteLine("\nR11b needHeader / needPieces, and the page's resend");
        var nh = LocalLayerHandlers.Handle("localCompletion", null, Req(SliceReq(11, 5, "unknown-header", HashPiece(6, data), window)), o);
        Check("an unknown header hash -> {needHeader:true} with the empty shape",
            nh.ContainsKey("needHeader") && (bool)nh["needHeader"] && ((List<Dictionary<string, object>>)nh["items"]).Count == 0 && !nh.ContainsKey("needPieces"), Json(nh));
        var np = LocalLayerHandlers.Handle("localHover", null, Req(SliceReq(15, 12, header, HashPiece(6, "unknown-piece"), window)), o);
        Check("an unknown piece hash -> {needPieces:[that hash]}",
            np.ContainsKey("needPieces") && string.Join(",", (List<string>)np["needPieces"]) == "unknown-piece" && !np.ContainsKey("needHeader"), Json(np));
        var resent = LocalLayerHandlers.Handle("localHover", null, Req(SliceReq(15, 12, header, TextPiece(6, Lines(6, 10)), window)), o);
        Check("the retry with the piece's text answers", resent["contents"] != null && !resent.ContainsKey("needPieces"), Json(resent));
        // headerSync after the host lost its header (evicted, IDE restarted mid-session): the page's text under
        // the map's hash is stored and the retry answers. RegisterHeader fails closed on a mismatch (F9).
        string headerText = LocalScopeIndex.BuildSpanMap(ModBuffer).HeaderText;
        LocalScopeIndex.ResetCaches();
        var lost = LocalLayerHandlers.Handle("localCompletion", null, Req(SliceReq(11, 5, header, TextPiece(6, Lines(6, 10)), window)), o);
        Check("with the header gone from the cache -> needHeader", lost.ContainsKey("needHeader"), Json(lost));
        var hsLog = new List<string>();
        Check("a headerSync whose text does not match the hash is NOT stored (fails closed) and says so",
            !LocalLayerHandlers.AcceptHeader(header, headerText + "edited", hsLog.Add) && hsLog.Count == 1 && hsLog[0].Contains("NOT stored"), string.Join(" | ", hsLog));
        var stillLost = LocalLayerHandlers.Handle("localCompletion", null, Req(SliceReq(11, 5, header, TextPiece(6, Lines(6, 10)), window)), o);
        Check("...so the retry still answers needHeader (the page then gives up)", stillLost.ContainsKey("needHeader"), Json(stillLost));
        Check("a headerSync with the matching text is stored", LocalLayerHandlers.AcceptHeader(header, headerText, null));
        var afterSync = LocalLayerHandlers.Handle("localCompletion", null, Req(SliceReq(11, 5, header, TextPiece(6, Lines(6, 10)), window)), o);
        Check("...and the retry answers", !afterSync.ContainsKey("needHeader") && Labels(afterSync).Contains("loTotal", StringComparer.OrdinalIgnoreCase), Json(afterSync));
        LocalLayerHandlers.SpanMapMessage(7, ModBuffer, null);   // re-prime the caches for the F2 case below

        Console.WriteLine("\nF2: a slice carrying headerText (the page's header is edited) uses it, not the cache");
        {
            // The edited header declares a module variable the cached one does not have (same line count).
            string edited = Lines(1, 5).Replace("Clip       LONG", "hdrNew     LONG");
            string req = "{\"line\":11,\"column\":5,\"slice\":{\"headerHash\":\"never-seen\",\"headerText\":" + Json(edited) +
                         ",\"routines\":[],\"pieces\":[" + HashPiece(6, data) + "," + TextPiece(11, "  hd\r\n" + Lines(12, 16)) + "]}}";
            var r = LocalLayerHandlers.Handle("localCompletion", null, Req(req), o);
            Check("an unknown headerHash WITH headerText -> no needHeader", !r.ContainsKey("needHeader"), Json(r));
            Check("...and the answer reflects the edited header (hdrNew offered)", Labels(r).Contains("hdrNew"), string.Join(",", Labels(r)));
        }
    }

    /// <summary>tests\fixtures\keyword-data, found from the harness's repo argument or the working directory.</summary>
    static string KeywordDataDir;

    static int Main()
    {
        foreach (var root in new[] { Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(), System.IO.Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var rel in new[] { @"tests\fixtures\keyword-data", @"ClarionAssistant\tests\fixtures\keyword-data" })
            {
                string d = System.IO.Path.Combine(root, rel);
                if (KeywordDataDir == null && System.IO.Directory.Exists(d)) KeywordDataDir = d;
            }
        }
        var log = new List<string>();
        var embed = new LocalLayerOptions { ProcedureName = "TestProc", SlotChecks = true, Surface = "CA Embeditor", Log = log.Add,
                                            DefaultRanges = new List<int[]> { new[] { 4, 6 } } };
        var fileTab = new LocalLayerOptions { ProcedureName = null, SlotChecks = false, Surface = "CA Editor(tab)", Log = log.Add };
        var overlay = new LocalLayerOptions { SlotChecks = true, Surface = "CA Editor(overlay)", Log = log.Add };

        SharedLspBridge.Reset();   // the "server" is down for everything below

        Console.WriteLine("\n4.7 slotDiagnostics per surface, with the LSP down");
        {
            var r = LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"action\":\"slotDiagnostics\",\"v\":3,\"ranges\":[[4,6]]}"), embed);
            var ms = Markers(r);
            Check("embed mode: the unterminated LOOP and the undefined DO",
                ms.Any(m => (int)m["line"] == 4 && ((string)m["message"]).StartsWith("LOOP is not terminated")) &&
                ms.Any(m => (int)m["line"] == 5 && ((string)m["message"]).Contains("NoSuchRoutine")), Json(ms));
            Check("...line numbers are Monaco lines (no LSP header offset)",
                ms.All(m => (int)m["line"] >= 4 && (int)m["line"] <= 5), Json(ms));

            var none = LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"action\":\"slotDiagnostics\",\"v\":3}"), embed);
            Check("no ranges in the request -> the host's DefaultRanges are used", Markers(none).Count == ms.Count, Json(Markers(none)));

            var tab = LocalLayerHandlers.Handle("slotDiagnostics", FileBuffer, Req("{\"action\":\"slotDiagnostics\",\"v\":3,\"ranges\":[[1,6]]}"), fileTab);
            Check("the CA Embeditor's FILE MODE tab: an empty marker list", Markers(tab).Count == 0, Json(Markers(tab)));

            var ov = LocalLayerHandlers.Handle("slotDiagnostics", FileBuffer, Req("{\"action\":\"slotDiagnostics\",\"v\":3,\"ranges\":[[1,6]]}"), overlay);
            Check("the CA Editor overlay KEEPS its structure squiggles (whole-file ranges, unbalanced IF -> a marker)",
                Markers(ov).Any(m => (int)m["line"] == 4 && ((string)m["message"]).StartsWith("IF is not terminated")), Json(Markers(ov)));

            Check("none of it touched the language server (stub call counters all 0)", SharedLspBridge.TotalCalls == 0,
                "calls=" + SharedLspBridge.TotalCalls);
        }

        Console.WriteLine("\nR11: slotDiagnostics slice payload {procedureName, routines, slots:[{start,text}]} - no synced buffer");
        {
            log.Clear();
            string req = "{\"action\":\"slotDiagnostics\",\"procedureName\":\"TestProc\",\"routines\":[\"RealRtn\"]," +
                         "\"slots\":[{\"start\":4,\"text\":" + Json("  LOOP\r\n    DO NoSuchRoutine\r\n    x# += 1") + "}]}";
            var args = Req(req);
            Check("the request is recognised as carrying its own slice (the control skips the `v` lookup)", LocalLayerHandlers.CarriesSlice(args));
            var r = LocalLayerHandlers.Handle("slotDiagnostics", null, args, embed);
            var ms = Markers(r);
            Check("markers from the slice alone, in Monaco lines (LOOP at 4, DO at 5)",
                ms.Count == 2 && ms.Any(m => (int)m["line"] == 4) && ms.Any(m => (int)m["line"] == 5 && ((string)m["message"]).Contains("NoSuchRoutine")), Json(ms));
            Check("[local-timing] reports sliceChars (the slot text length)",
                log.Count == 1 && log[0].Contains(" sliceChars=" + "  LOOP\r\n    DO NoSuchRoutine\r\n    x# += 1".Length), log.FirstOrDefault());
            // The page does not know the procedure: it sends procedureName:null. The routine set comes from the
            // span map's routines + the slot texts, and the `v` form uses the HOST's name (options), so a null
            // from the page must give exactly the markers an explicit name does.
            var nullName = LocalLayerHandlers.Handle("slotDiagnostics", null, Req(req.Replace("\"procedureName\":\"TestProc\"", "\"procedureName\":null")), embed);
            Check("procedureName:null from the page -> the same markers as the explicit name",
                Json(Markers(nullName)) == Json(ms) && ms.Count == 2, Json(Markers(nullName)));
            // The `v` form: a buffer where the name MATTERS - a routine of the same name belongs to an EARLIER
            // procedure, so only a scan that starts at TestProc's header (the host's name) flags the DO.
            string twoProcs = "Other PROCEDURE\r\n  CODE\r\nNoSuchRoutine ROUTINE\r\n  x# = 0\r\n" + EmbedBuffer;   // TestProc now starts at line 5
            var vNull = LocalLayerHandlers.Handle("slotDiagnostics", twoProcs, Req("{\"procedureName\":null,\"ranges\":[[8,10]]}"), embed);
            var vNamed = LocalLayerHandlers.Handle("slotDiagnostics", twoProcs, Req("{\"procedureName\":\"TestProc\",\"ranges\":[[8,10]]}"), embed);
            Check("...and in the `v` form: the host's own procedure name is used (the DO is still flagged)",
                Json(Markers(vNull)) == Json(Markers(vNamed)) && Markers(vNull).Any(m => ((string)m["message"]).Contains("NoSuchRoutine")),
                Json(Markers(vNull)));

            var tab = LocalLayerHandlers.Handle("slotDiagnostics", null, args, fileTab);
            Check("the file-mode tab still answers an empty list for a slice", Markers(tab).Count == 0);
            log.Clear();
            LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"ranges\":[[4,6]]}"), embed);
            Check("the `v` form logs sliceChars=none(v)", log.Count == 1 && log[0].Contains(" sliceChars=none(v)"), log.FirstOrDefault());
            log.Clear();
            LocalLayerHandlers.Handle("localCompletion", null,
                Req("{\"line\":1,\"column\":2,\"slice\":{\"headerHash\":\"nope\",\"routines\":[],\"pieces\":[{\"start\":1,\"text\":\"abc\"},{\"start\":4,\"hash\":\"h\"},{\"start\":9,\"text\":\"de\"}]}}"), embed);
            Check("a localCompletion slice logs sliceChars = the text it carried (a hash-only piece costs 0)",
                log.Count == 1 && log[0].Contains(" sliceChars=5"), log.FirstOrDefault());
        }

        CompletionAndHover(log);

        Console.WriteLine("\n4.8 one [local-timing] line per call");
        {
            log.Clear();
            LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"ranges\":[[4,6]]}"), embed);
            LocalLayerHandlers.Handle("localCompletion", EmbedBuffer, Req("{\"line\":5,\"column\":7}"), embed);
            LocalLayerHandlers.Handle("localHover", EmbedBuffer, Req("{\"line\":5,\"column\":7}"), embed);
            var rx = new Regex(@"^\[local-timing\] action=(localCompletion|localHover|slotDiagnostics) ms=\d+ items=\d+");
            Check("three calls -> three lines, each in the agreed shape", log.Count == 3 && log.All(l => rx.IsMatch(l)), string.Join(" | ", log));
            Check("...slotDiagnostics counts its markers as items", log.Count > 0 && log[0].Contains(" items=2"), log.FirstOrDefault());
        }

        Console.WriteLine("\n4.10 reply shapes (as the page receives them)");
        {
            var s = Json(LocalLayerHandlers.Handle("slotDiagnostics", EmbedBuffer, Req("{\"ranges\":[[4,6]]}"), embed));
            Check("slotDiagnostics -> {markers:[...], ms}", Regex.IsMatch(s, "^\\{\"markers\":\\[.*\\],\"ms\":\\d+\\}$"), s);
            s = Json(LocalLayerHandlers.Handle("localCompletion", EmbedBuffer, Req("{\"line\":5,\"column\":7}"), embed));
            Check("localCompletion -> {items:[...], source:'local', ms}", Regex.IsMatch(s, "^\\{\"items\":\\[.*\\],\"source\":\"local\",\"ms\":\\d+\\}$"), s);
            s = Json(LocalLayerHandlers.Handle("localHover", EmbedBuffer, Req("{\"line\":5,\"column\":7}"), embed));
            Check("localHover -> {contents, authoritative:<bool>, ms}", Regex.IsMatch(s, "^\\{\"contents\":(null|\".*\"),\"authoritative\":(true|false),\"ms\":\\d+\\}$"), s);
        }

        Console.WriteLine("\nF6: the parsed payload is bounded; a reject is the empty shape plus one [webmsg] line");
        {
            Func<string, string, Dictionary<string, object>> run = (action, json) =>
            {
                WebMessageGuard.ResetRateLimit();
                log.Clear();
                return LocalLayerHandlers.Handle(action, EmbedBuffer, Req(json), embed);
            };
            Func<Dictionary<string, object>, bool> rejected = r =>
                log.Any(l => l.StartsWith("[webmsg] rejected action=")) && (r.ContainsKey("items") ? ((List<Dictionary<string, object>>)r["items"]).Count == 0 :
                                                                           r.ContainsKey("markers") ? ((List<Dictionary<string, object>>)r["markers"]).Count == 0 : r["contents"] == null);
            Func<string, bool, bool> Because = (why, r) => r && log.Any(l => l.Contains("reason=") && l.Contains(why));
            Func<int, string> pieces = n => string.Join(",", Enumerable.Range(0, n).Select(i => TextPiece(1 + i, i == 0 ? "  CODE\r\n  lo" : "x")));
            Func<int, int, string> routines = (n, len) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => "\"" + ("R" + i).PadRight(len, 'x') + "\"")) + "]";

            var ok8 = run("localCompletion", "{\"line\":2,\"column\":5,\"slice\":{\"headerText\":\"\",\"routines\":[],\"pieces\":[" + pieces(8) + "]}}");
            Check("8 pieces accepted", !rejected(ok8), string.Join(" | ", log));
            Check("9 pieces rejected", Because("more than 8 pieces", rejected(run("localCompletion", "{\"line\":2,\"column\":5,\"slice\":{\"headerText\":\"\",\"routines\":[],\"pieces\":[" + pieces(9) + "]}}"))), string.Join(" | ", log));
            Check("10,000 routines accepted", !rejected(run("slotDiagnostics", "{\"routines\":" + routines(10000, 8) + ",\"slots\":[{\"start\":4,\"text\":\"  x# = 1\"}]}")), string.Join(" | ", log));
            Check("10,001 routines rejected", Because("more than 10000 routines", rejected(run("slotDiagnostics", "{\"routines\":" + routines(10001, 8) + ",\"slots\":[{\"start\":4,\"text\":\"  x# = 1\"}]}"))), string.Join(" | ", log));
            Check("a 256-char routine name accepted", !rejected(run("slotDiagnostics", "{\"routines\":" + routines(1, 256) + ",\"slots\":[]}")), string.Join(" | ", log));
            Check("a 257-char routine name rejected", Because("routine name over 256", rejected(run("slotDiagnostics", "{\"routines\":" + routines(1, 257) + ",\"slots\":[]}"))), string.Join(" | ", log));
            Func<int, string> slots = n => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => "{\"start\":" + (1 + i) + ",\"text\":\"x\"}")) + "]";
            // H1: InventoryTable has more than 2,000 editable embed ranges in one procedure.
            Func<int, string> ranges = n => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => "[" + (1 + i) + "," + (1 + i) + "]")) + "]";
            Check("H1 5,000 slots accepted (slice form)", !rejected(run("slotDiagnostics", "{\"routines\":[],\"slots\":" + slots(5000) + "}")), string.Join(" | ", log));
            Check("H1 5,000 ranges accepted (v form)", !rejected(run("slotDiagnostics", "{\"ranges\":" + ranges(5000) + "}")), string.Join(" | ", log));
            Check("H1 100,000 slots accepted", !rejected(run("slotDiagnostics", "{\"routines\":[],\"slots\":" + slots(100000) + "}")), string.Join(" | ", log));
            Check("H1 100,001 slots rejected", Because("more than 100000 slots", rejected(run("slotDiagnostics", "{\"routines\":[],\"slots\":" + slots(100001) + "}"))), string.Join(" | ", log));
            Check("H1 100,001 ranges rejected (v form)", Because("more than 100000 ranges", rejected(run("slotDiagnostics", "{\"ranges\":" + ranges(100001) + "}"))), string.Join(" | ", log));
            Check("line 0 rejected", Because("below 1", rejected(run("localHover", "{\"line\":0,\"column\":1}"))), string.Join(" | ", log));
            Check("column 0 rejected", Because("below 1", rejected(run("localHover", "{\"line\":5,\"column\":0}"))), string.Join(" | ", log));
            Check("a line past the buffer end rejected", Because("past the buffer end", rejected(run("localHover", "{\"line\":99999,\"column\":1}"))), string.Join(" | ", log));
            Check("a column past the line end rejected", Because("past the line end", rejected(run("localHover", "{\"line\":5,\"column\":500}"))), string.Join(" | ", log));
            Check("the last line + one past its last char accepted", !rejected(run("localHover", "{\"line\":9,\"column\":9}")), string.Join(" | ", log));
            Check("a caret outside the slice's text rejected",
                Because("outside the slice", rejected(run("localCompletion", "{\"line\":50,\"column\":1,\"slice\":{\"headerText\":\"\",\"routines\":[],\"pieces\":[" + pieces(2) + "]}}"))), string.Join(" | ", log));
            WebMessageGuard.ResetRateLimit();
        }

        Console.WriteLine("\nrobustness");
        {
            log.Clear();
            Dictionary<string, object> r = null;
            bool threw = false;
            try { r = LocalLayerHandlers.Handle("slotDiagnostics", null, null, null); } catch { threw = true; }
            Check("null buffer / args / options -> an empty marker list, never throws", !threw && r != null && Markers(r).Count == 0);
            try { r = LocalLayerHandlers.Handle("nope", EmbedBuffer, null, embed); } catch { threw = true; }
            Check("an unknown action -> answered, and logged as an error", !threw && r != null && log.Any(l => l.Contains("error=unknown action")), string.Join(" | ", log));
        }

        Console.WriteLine("\n" + _pass + " passed, " + _fail + " failed");
        return _fail == 0 ? 0 : 1;
    }
}
