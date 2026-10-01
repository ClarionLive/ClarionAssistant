using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ClarionAssistant.Services;

// 1d8d1c49: ClarionAppDataReader.TxaDataIndex must give EXACTLY what the whole-string TXA parsers give
// (ParseTxaProcedureData / ParseTxaGlobalData / ParseTxaOtherFiles / ParseTxaPrimaryFile /
// ParseTxaDictionaryPath). The string versions are the oracle; the index only exists so the pad never
// holds a whole-app export (20 MB on v61PRM004, 38 MB as a string) or Splits it.
//
// Default run: a synthetic fixture with the edge cases (control sub-sections, "!" lines, a NAME a few
// lines down, a block without [DATA], a duplicate NAME, a region running to end of file, LF-only lines).
// Opt-in: pass a real whole-app .txa path (or set CA_TXA_REAL) to compare a sample of its procedures too.
// Either way it first proves the comparison CAN fail, by corrupting a copy of one region.
//
// Run:  tests\Run-Tests.ps1   (fixture only)
//       TxaDataIndex.Test.exe <path-to-wholeapp.txa> [sampleCount]
static class TxaDataIndexTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static string Dump(object o)
    {
        if (o == null) return "null";
        var sb = new StringBuilder();
        DumpInto(sb, o);
        return sb.ToString();
    }

    static void DumpInto(StringBuilder sb, object o)
    {
        if (o == null) { sb.Append("null"); return; }
        if (o is string) { sb.Append('"').Append((string)o).Append('"'); return; }
        var list = o as System.Collections.IEnumerable;
        if (list != null)
        {
            sb.Append('[');
            foreach (var e in list) { DumpInto(sb, e); sb.Append(','); }
            sb.Append(']');
            return;
        }
        sb.Append('{');
        foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name))
        {
            sb.Append(f.Name).Append('=');
            DumpInto(sb, f.GetValue(o));
            sb.Append(';');
        }
        sb.Append('}');
    }

    static ClarionAppDataReader.TxaDataIndex Index(string text)
    {
        using (var r = new StringReader(text)) return ClarionAppDataReader.TxaDataIndex.Build(r);
    }

    /// <summary>Every NAME that follows a [PROCEDURE] within 5 lines, the same window the parsers use.</summary>
    static List<string> ProcNames(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var names = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "[PROCEDURE]") continue;
            for (int j = i + 1; j < Math.Min(i + 6, lines.Length); j++)
            {
                var m = Regex.Match(lines[j], @"^\s*NAME\s+(.+?)\s*$");
                if (m.Success) { names.Add(m.Groups[1].Value.Trim()); break; }
            }
        }
        return names;
    }

    /// <summary>All five outputs for one procedure, old vs new; returns the first difference or null.</summary>
    static string Compare(string text, ClarionAppDataReader.TxaDataIndex idx, string proc)
    {
        string a, b;
        a = Dump(ClarionAppDataReader.ParseTxaProcedureData(text, proc)); b = Dump(ClarionAppDataReader.ParseTxaProcedureData(idx, proc));
        if (a != b) return "ProcedureData(" + proc + ") old=" + Trunc(a) + " new=" + Trunc(b);
        a = Dump(ClarionAppDataReader.ParseTxaOtherFiles(text, proc)); b = Dump(ClarionAppDataReader.ParseTxaOtherFiles(idx, proc));
        if (a != b) return "OtherFiles(" + proc + ") old=" + Trunc(a) + " new=" + Trunc(b);
        a = Dump(ClarionAppDataReader.ParseTxaPrimaryFile(text, proc)); b = Dump(ClarionAppDataReader.ParseTxaPrimaryFile(idx, proc));
        if (a != b) return "PrimaryFile(" + proc + ") old=" + Trunc(a) + " new=" + Trunc(b);
        return null;
    }

    static string CompareGlobal(string text, ClarionAppDataReader.TxaDataIndex idx)
    {
        string a = Dump(ClarionAppDataReader.ParseTxaGlobalData(text)), b = Dump(ClarionAppDataReader.ParseTxaGlobalData(idx));
        if (a != b) return "GlobalData old=" + Trunc(a) + " new=" + Trunc(b);
        a = ClarionAppDataReader.ParseTxaDictionaryPath(text) ?? "null"; b = ClarionAppDataReader.ParseTxaDictionaryPath(idx) ?? "null";
        if (a != b) return "DictionaryPath old=" + a + " new=" + b;
        return null;
    }

    static string Trunc(string s) { return s.Length > 160 ? s.Substring(0, 160) + "..." : s; }

    static readonly string Fixture = string.Join("\r\n", new[]
    {
        "[APPLICATION]",
        "VERSION 36",
        "DICTIONARY 'C:\\Apps\\Sample.dct'",
        "[PROGRAM]",
        "[COMMON]",
        "FROM ABC Application",
        "[DATA]",
        "[SCREENCONTROLS]",
        "! PROMPT('Setup:'),USE(?SET:Prompt)",
        "Setup                GROUP,PRE(SET)",
        "!!> PROMPT('Setup:')",
        "Name                   STRING(40)",
        "!!> PICTURE(@s40),PROMPT('Name:')",
        "Count                  LONG",
        "                     END",
        "GlobalFlag           BYTE",
        "!!> PICTURE(@n3)",
        "[MODULE]",
        "[COMMON]",
        "[PROCEDURE]",
        "NAME Main",
        "[COMMON]",
        "[DATA]",
        "[REPORTCONTROLS]",
        "! STRING(@s20),USE(LOC:Title)",
        "LOC:Title            STRING(20)",
        "!!> PICTURE(@s20),PROMPT('Title:'),TOOLTIP('The title')",
        "LOC:Queue            QUEUE,PRE(LQ)",
        "Item                   STRING(10)",
        "                     END",
        "[FILES]",
        "[PRIMARY]",
        "Customer",
        "[INSTANCE]",
        "1",
        "[KEY]",
        "CUS:KeyName",
        "[OTHERS]",
        "Invoice",
        "",
        "Product",
        "[PROMPTS]",
        "%Something LONG  (1)",
        "[WINDOW]",
        "Window WINDOW('x')",
        "[PROCEDURE]",
        "FROM ABC Window",
        "CATEGORY 'Browse'",
        "NAME NoData",
        "[COMMON]",
        "[FILES]",
        "[OTHERS]",
        "Supplier",
        "[EMBED]",
        "code",
        "[PROCEDURE]",
        "NAME Main",                       // duplicate NAME: the first block must win everywhere
        "[DATA]",
        "Dup                  LONG",
        "[FILES]",
        "[PRIMARY]",
        "Other",
    }) + "\n[PROCEDURE]\nNAME TailData\n[DATA]\nT1                   LONG\n!!> PICTURE(@n5)\nT2                   STRING(3)";   // LF-only, region to EOF

    static int Main(string[] args)
    {
        Console.WriteLine("TxaDataIndex.Test");

        // ── Fixture ────────────────────────────────────────────────────────────────────────────────
        var idx = Index(Fixture);
        Ok("fixture: globals + dictionary path match", CompareGlobal(Fixture, idx) == null, CompareGlobal(Fixture, idx));
        foreach (var p in new[] { "Main", "main", "NoData", "TailData", "Missing" })
        {
            string d = Compare(Fixture, idx, p);
            Ok("fixture: " + p + " matches the string parsers", d == null, d);
        }
        // Sanity: the fixture really exercises what it claims (so a match isn't two empties agreeing).
        Ok("fixture: Main has 2 top-level locals (a queue with a child)", ClarionAppDataReader.ParseTxaProcedureData(idx, "Main").Count == 2);
        Ok("fixture: Main primary is Customer / CUS:KeyName",
            Dump(ClarionAppDataReader.ParseTxaPrimaryFile(idx, "Main")) == Dump(new ClarionAppDataReader.ProcPrimaryFile { File = "Customer", Key = "CUS:KeyName" }));
        Ok("fixture: Main others = Invoice, Product", string.Join(",", ClarionAppDataReader.ParseTxaOtherFiles(idx, "Main")) == "Invoice,Product");
        Ok("fixture: globals = Setup group + GlobalFlag", ClarionAppDataReader.ParseTxaGlobalData(idx).Count == 2);
        Ok("fixture: TailData region to EOF has 2 locals", ClarionAppDataReader.ParseTxaProcedureData(idx, "TailData").Count == 2);

        // ── The comparison can fail: corrupt a COPY of Main's region and require a difference ───────
        var bad = Index(Fixture);
        var region = bad.ProcedureData["Main"];
        bad.ProcedureData["Main"] = region.Take(region.Length - 2).ToArray();
        Ok("proof: a corrupted region is reported as a mismatch", Compare(Fixture, bad, "Main") != null);
        var bad2 = Index(Fixture);
        bad2.OtherFiles["Main"].RemoveAt(0);
        Ok("proof: a corrupted OTHERS list is reported as a mismatch", Compare(Fixture, bad2, "Main") != null);

        // ── Opt-in: a real whole-app export ───────────────────────────────────────────────────────
        string real = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("CA_TXA_REAL");
        if (!string.IsNullOrEmpty(real) && File.Exists(real))
        {
            int sample = args.Length > 1 ? int.Parse(args[1]) : 40;
            Encoding enc;
            string text;
            try { text = new StreamReader(real, new UTF8Encoding(false, true), true).ReadToEnd(); }
            catch (DecoderFallbackException) { text = File.ReadAllText(real, Encoding.Default); }
            ClarionAppDataReader.TxaDataIndex ridx;
            try { using (var sr = new StreamReader(real, new UTF8Encoding(false, true), true)) ridx = ClarionAppDataReader.TxaDataIndex.Build(sr); }
            catch (DecoderFallbackException) { using (var sr = new StreamReader(real, Encoding.Default, true)) ridx = ClarionAppDataReader.TxaDataIndex.Build(sr); }

            var names = ProcNames(text);
            var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            long regionChars = ridx.ProcedureData.Values.Sum(r => r.Sum(l => (long)l.Length)) + (ridx.ProgramData ?? new string[0]).Sum(l => (long)l.Length);
            Console.WriteLine("  real: " + real + "  chars=" + text.Length + " procedures=" + distinct.Count
                + " withData=" + ridx.ProcedureData.Count + " keptChars=" + regionChars + " (" + (100.0 * regionChars / text.Length).ToString("0.0") + "%)");
            Ok("real: globals + dictionary path match", CompareGlobal(text, ridx) == null, CompareGlobal(text, ridx));
            Ok("real: every procedure NAME is indexed", distinct.All(n => ridx.OtherFiles.ContainsKey(n)),
                string.Join(",", distinct.Where(n => !ridx.OtherFiles.ContainsKey(n)).Take(5)));
            // Deterministic spread over the file, plus the procedures with the most local data.
            var picks = new List<string>();
            int step = Math.Max(1, distinct.Count / Math.Max(1, sample));
            for (int i = 0; i < distinct.Count && picks.Count < sample; i += step) picks.Add(distinct[i]);
            foreach (var n in ridx.ProcedureData.OrderByDescending(kv => kv.Value.Length).Take(5).Select(kv => kv.Key))
                if (!picks.Contains(n, StringComparer.OrdinalIgnoreCase)) picks.Add(n);
            int bad3 = 0; string first = null;
            foreach (var p in picks) { string d = Compare(text, ridx, p); if (d != null) { bad3++; if (first == null) first = d; } }
            Ok("real: " + picks.Count + " sampled procedures match on all outputs", bad3 == 0, bad3 + " differ; first: " + first);
        }
        else Console.WriteLine("  (real-export comparison skipped: no path given and CA_TXA_REAL unset)");

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail));
        return fail == 0 ? 0 : 1;
    }
}
