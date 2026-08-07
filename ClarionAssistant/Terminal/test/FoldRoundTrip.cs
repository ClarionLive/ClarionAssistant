using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

// Round-trip check for the fold state on-disk format (ticket 91107c1a).
// Mirrors ModernEmbeditorState.FoldsToWire (write) and LoadFolds (read) exactly.
// The bug this pins: serializing the typed FoldRecord emitted "Line"/"Text" (member names) while the
// reader looked up "line"/"text", and DeserializeObject's dictionary is case-SENSITIVE -> every record
// silently dropped, so folds saved fine and always loaded back empty.
public sealed class FoldRecord { public int Line; public string Text; }

public static class Program
{
    static List<Dictionary<string, object>> FoldsToWire(IList<FoldRecord> folds)
    {
        var outp = new List<Dictionary<string, object>>();
        foreach (var f in folds)
            outp.Add(new Dictionary<string, object> { { "line", f.Line }, { "text", f.Text ?? "" } });
        return outp;
    }

    static bool TryGetEither(IDictionary<string, object> d, string lower, string upper, out object v)
    {
        v = null;
        if (d == null) return false;
        return d.TryGetValue(lower, out v) || d.TryGetValue(upper, out v);
    }

    static List<FoldRecord> ReadBack(object o)
    {
        var folds = new List<FoldRecord>();
        var arr = o as object[];
        if (arr == null) return folds;
        foreach (var item in arr)
        {
            var d = item as Dictionary<string, object>;
            if (d == null) continue;
            object lv, tv;
            if (!TryGetEither(d, "line", "Line", out lv) || lv == null) continue;
            int line;
            try { line = Convert.ToInt32(lv); } catch { continue; }
            if (line < 1) continue;
            string text = (TryGetEither(d, "text", "Text", out tv) && tv != null) ? tv.ToString() : "";
            folds.Add(new FoldRecord { Line = line, Text = text });
        }
        return folds;
    }

    static int fails = 0;
    static void Check(string name, bool cond, string detail)
    {
        Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + name + (cond ? "" : "  -- " + detail));
        if (!cond) fails++;
    }

    public static int Main()
    {
        var ser = new JavaScriptSerializer();
        var src = new List<FoldRecord> { new FoldRecord { Line = 70, Text = "BRW1::VIEW:BROWSE VIEW(AUTHORS)" } };

        // --- the FIXED path ---
        var rec  = new Dictionary<string, object> { { "cursorLine", 1 }, { "folds", FoldsToWire(src) } };
        var root = new Dictionary<string, object> { { "clbrws::BrowseAuthors", rec } };
        string json = ser.Serialize(root);
        Console.WriteLine("  on-disk: " + json);

        var back    = ser.DeserializeObject(json) as Dictionary<string, object>;
        var recBack = back["clbrws::BrowseAuthors"] as Dictionary<string, object>;
        var got     = ReadBack(recBack["folds"]);

        Check("lowercase keys on disk", json.Contains("\"line\":70"), json);
        Check("round-trips one record", got.Count == 1, "count=" + got.Count);
        Check("line survives", got.Count == 1 && got[0].Line == 70, got.Count > 0 ? got[0].Line.ToString() : "-");
        Check("text survives", got.Count == 1 && got[0].Text == "BRW1::VIEW:BROWSE VIEW(AUTHORS)",
              got.Count > 0 ? got[0].Text : "-");

        // --- the OLD broken path: typed record serialized directly ---
        var brokenRoot = new Dictionary<string, object> { { "folds", src } };
        string brokenJson = ser.Serialize(brokenRoot);
        Console.WriteLine("  legacy shape: " + brokenJson);
        var brokenBack = ser.DeserializeObject(brokenJson) as Dictionary<string, object>;
        var brokenGot  = ReadBack(brokenBack["folds"]);
        Check("reproduces the bug: capitalised keys were written", brokenJson.Contains("\"Line\":70"), brokenJson);
        Check("case-tolerant reader recovers legacy records", brokenGot.Count == 1, "count=" + brokenGot.Count);

        Console.WriteLine(fails == 0 ? "\nALL PASS" : "\n" + fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
