using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

// 1d8d1c49: an LSP message written by JsonTextStream (serializer prefix/suffix around a streamed, escaped
// text) must PARSE to the same value as the old path (JavaScriptSerializer over the whole message, then
// UTF8.GetBytes), carry a Content-Length equal to its body's byte count, and allocate a small fraction of
// the old path on a 3.2M-char buffer. Uses the same sentinel split LspClient uses. Proves the comparison
// can fail with an escaper that forgets quotes.
//
// Run:  tests\Run-Tests.ps1      (optional arg: a big text file to use as the buffer)
static class JsonTextStreamTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    const string Sentinel = "\u0001CA_LARGE_TEXT_1d8d1c49\u0001";

    static Dictionary<string, object> Message(string text, out Dictionary<string, object> holder)
    {
        holder = new Dictionary<string, object> { { "text", text } };
        return new Dictionary<string, object>
        {
            { "jsonrpc", "2.0" }, { "method", "textDocument/didChange" },
            { "params", new Dictionary<string, object> {
                { "textDocument", new Dictionary<string, object> { { "uri", "file:///c%3A/x/<a&b>'q'.clw" }, { "version", 7 } } },
                { "contentChanges", new System.Collections.ArrayList { holder } } } }
        };
    }

    static byte[] OldPath(string text)
    {
        Dictionary<string, object> h;
        byte[] body = Encoding.UTF8.GetBytes(Ser.Serialize(Message(text, out h)));
        byte[] head = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
        var all = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, all, 0, head.Length); Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
        return all;
    }

    static byte[] NewPath(string text)
    {
        Dictionary<string, object> h;
        var msg = Message(Sentinel, out h);
        string json = Ser.Serialize(msg);
        string prefix, suffix;
        if (!JsonTextStream.SplitAroundMarker(json, Ser.Serialize(Sentinel), out prefix, out suffix)) return null;
        var ms = new MemoryStream();
        JsonTextStream.WriteLspMessage(ms, prefix, text, suffix);
        return ms.ToArray();
    }

    /// <summary>Header length vs body bytes, then the parsed body re-serialized (a canonical form to compare).</summary>
    static string Parse(byte[] msg, out string error)
    {
        error = null;
        if (msg == null) { error = "no message"; return null; }
        int sep = -1;
        for (int i = 0; i + 3 < msg.Length; i++) if (msg[i] == 13 && msg[i + 1] == 10 && msg[i + 2] == 13 && msg[i + 3] == 10) { sep = i; break; }
        if (sep < 0) { error = "no header terminator"; return null; }
        string head = Encoding.ASCII.GetString(msg, 0, sep);
        long declared = long.Parse(head.Substring("Content-Length: ".Length));
        long actual = msg.Length - (sep + 4);
        if (declared != actual) { error = "Content-Length " + declared + " but body is " + actual + " bytes"; return null; }
        try { return Ser.Serialize(Ser.DeserializeObject(Encoding.UTF8.GetString(msg, sep + 4, (int)actual))); }
        catch (Exception ex) { error = "parse: " + ex.Message; return null; }
    }

    static string Check(string text)
    {
        string e1, e2;
        string a = Parse(OldPath(text), out e1), b = Parse(NewPath(text), out e2);
        if (e1 != null) return "old path: " + e1;
        if (e2 != null) return "new path: " + e2;
        return string.Equals(a, b, StringComparison.Ordinal) ? null : "parsed values differ";
    }

    static long Allocated() { AppDomain.MonitoringIsEnabled = true; return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize; }

    static int Main(string[] args)
    {
        Console.WriteLine("JsonTextStream.Test");

        var edge = new[] { "", "a", "\"", "\\", "\r\n", "\t\b\f", "\u0000\u0001\u001f", "<script>&'</script>", "\u00e9\u4e2d\u6587",
                           "\ud83d\ude00", "\ud83d", "x\udc00y", "\u2028\u2029", "END\r\n  ! comment \"q\"\r\n" };
        int bad = 0; string first = null;
        foreach (var t in edge) { string d = Check(t); if (d != null) { bad++; if (first == null) first = "\"" + t + "\": " + d; } }
        Ok("edge texts (" + edge.Length + ") parse identically, Content-Length exact", bad == 0, first);

        // Surrogate pairs and escapes straddling the 8192-char chunk boundary.
        var sb = new StringBuilder();
        for (int i = 0; i < 8189; i++) sb.Append('a');
        sb.Append("\ud83d\ude00\"\\\n\ud83d\ude00");
        for (int i = 0; i < 20000; i++) sb.Append(i % 7 == 0 ? '"' : (i % 11 == 0 ? '\u00e9' : 'b'));
        Ok("chunk-boundary text parses identically", Check(sb.ToString()) == null, Check(sb.ToString()));

        var rnd = new Random(99);
        char[] alphabet = { 'a', ' ', '"', '\\', '\n', '\r', '\t', '\u0001', '\u00e9', '\u4e2d', '\ud83d', '\ude00', '<', '&', '\u2028' };
        bad = 0; first = null;
        for (int n = 0; n < 2000; n++)
        {
            var f = new StringBuilder();
            int len = rnd.Next(0, 60);
            for (int i = 0; i < len; i++) f.Append(alphabet[rnd.Next(alphabet.Length)]);
            string d = Check(f.ToString());
            if (d != null) { bad++; if (first == null) first = d; }
        }
        Ok("2000 fuzz texts parse identically", bad == 0, first);

        // Proof: an escaper that forgets '"' must be caught.
        {
            Dictionary<string, object> h;
            string json = Ser.Serialize(Message(Sentinel, out h)); string prefix, suffix;
            JsonTextStream.SplitAroundMarker(json, Ser.Serialize(Sentinel), out prefix, out suffix);
            string text = "say \"hi\"";
            byte[] body = Encoding.UTF8.GetBytes(prefix + text + suffix);   // unescaped
            var msg = new MemoryStream();
            byte[] head = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            msg.Write(head, 0, head.Length); msg.Write(body, 0, body.Length);
            string e; string parsed = Parse(msg.ToArray(), out e); string e0; string good = Parse(OldPath(text), out e0);
            Ok("proof: an unescaped quote is caught", e != null || parsed != good);
        }
        Ok("proof: a marker that is not present is refused", !JsonTextStream.SplitAroundMarker("{\"a\":\"b\"}", "\"zz\"", out _, out _));
        Ok("proof: a marker present twice is refused", !JsonTextStream.SplitAroundMarker("[\"m\",\"m\"]", "\"m\"", out _, out _));

        // The real size: a 3.2M-char buffer.
        string big;
        if (args.Length > 0 && File.Exists(args[0])) big = File.ReadAllText(args[0]);
        else
        {
            var b = new StringBuilder(3300000);
            for (int i = 0; b.Length < 3200000; i++) b.Append("  LOC:Var").Append(i).Append("  STRING(20)  ! \"quoted\" \u00e9\r\n");
            big = b.ToString();
        }
        Ok("big buffer parses identically", Check(big) == null, Check(big));
        GC.Collect();
        long a0 = Allocated(); OldPath(big); long a1 = Allocated();
        var sink = new NullStream(); Dictionary<string, object> hh; string js = Ser.Serialize(Message(Sentinel, out hh)); string pre, suf;
        long a2 = Allocated(); JsonTextStream.SplitAroundMarker(js, Ser.Serialize(Sentinel), out pre, out suf); JsonTextStream.WriteLspMessage(sink, pre, big, suf); long a3 = Allocated();
        double oldMb = (a1 - a0) / 1048576.0, newMb = (a3 - a2) / 1048576.0;
        Console.WriteLine("  big: chars=" + big.Length + "  alloc old=" + oldMb.ToString("0.0") + "MB streamed=" + newMb.ToString("0.00") + "MB  bytes=" + sink.Written);
        Ok("big buffer: streamed path allocates under 1 MB (old path " + oldMb.ToString("0") + " MB)", newMb < 1.0, newMb.ToString("0.00") + " MB");

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail));
        return fail == 0 ? 0 : 1;
    }

    sealed class NullStream : Stream
    {
        public long Written;
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return Written; } }
        public override long Position { get { return Written; } set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { Written += count; }
    }
}
