using System;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// 1d8d1c49: write one LSP message whose JSON carries a very large string (a whole embeditor buffer in
    /// didOpen/didChange) WITHOUT materialising the JSON. JavaScriptSerializer plus UTF8.GetBytes allocated
    /// 31 MB (5.1x the text) per push of a 3.2M-char buffer, all of it large-object heap, on every open and
    /// every idle sync while typing. Here the caller supplies the JSON before and after the string (from the
    /// real serializer, so every other field is escaped exactly as before) and the string is JSON-escaped
    /// and UTF-8 encoded in chunks straight to the stream. The text is walked twice: once to count bytes for
    /// Content-Length, once to write. Chunk buffers stay under the 85 KB large-object threshold.
    ///
    /// Escaping is standard JSON: \" \\ \b \f \n \r \t, other control characters as \u00XX, everything else
    /// literal. A lone surrogate becomes U+FFFD in UTF-8, which is what UTF8.GetBytes did to the serializer's
    /// output. JsonTextStream.Test proves the message parses to the same value as the serializer path.
    /// </summary>
    public static class JsonTextStream
    {
        private const int ChunkChars = 8192;   // escaped chars per chunk; bytes <= 3x, well under 85 KB

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>
        /// Fill <paramref name="buf"/> with the JSON-escaped form of text[pos..], stopping before an escape that
        /// would not fit. Returns the number of chars written and advances <paramref name="pos"/>.
        /// </summary>
        internal static int EscapeChunk(string text, ref int pos, char[] buf)
        {
            int n = 0;
            while (pos < text.Length)
            {
                char c = text[pos];
                string esc = null;
                switch (c)
                {
                    case '"': esc = "\\\""; break;
                    case '\\': esc = "\\\\"; break;
                    case '\n': esc = "\\n"; break;
                    case '\r': esc = "\\r"; break;
                    case '\t': esc = "\\t"; break;
                    case '\b': esc = "\\b"; break;
                    case '\f': esc = "\\f"; break;
                    default:
                        if (c < 0x20) esc = "\\u00" + ((int)c).ToString("x2");
                        break;
                }
                if (esc == null)
                {
                    if (n + 1 > buf.Length) break;
                    buf[n++] = c;
                }
                else
                {
                    if (n + esc.Length > buf.Length) break;
                    for (int i = 0; i < esc.Length; i++) buf[n++] = esc[i];
                }
                pos++;
            }
            return n;
        }

        /// <summary>
        /// Split serialized JSON around a string value that was serialized as <paramref name="marker"/> (quotes
        /// included, exactly as the serializer wrote it). The prefix ends with the value's opening quote and the
        /// suffix starts with its closing quote. False unless the marker occurs exactly once.
        /// </summary>
        public static bool SplitAroundMarker(string json, string marker, out string prefix, out string suffix)
        {
            prefix = suffix = null;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(marker) || marker.Length < 2) return false;
            int at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0 || json.IndexOf(marker, at + marker.Length, StringComparison.Ordinal) >= 0) return false;
            prefix = json.Substring(0, at + 1);
            suffix = json.Substring(at + marker.Length - 1);
            return true;
        }

        /// <summary>UTF-8 byte count of the JSON-escaped text (no quotes), without building it.</summary>
        public static long EscapedUtf8Length(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var enc = Utf8.GetEncoder();
            var buf = new char[ChunkChars];
            long total = 0;
            int pos = 0;
            while (pos < text.Length)
            {
                int n = EscapeChunk(text, ref pos, buf);
                total += enc.GetByteCount(buf, 0, n, pos >= text.Length);
            }
            return total;
        }

        /// <summary>
        /// Write "Content-Length: N\r\n\r\n" + prefix + escaped(text) + suffix to <paramref name="stream"/>.
        /// <paramref name="prefix"/> must end with the string's opening quote and <paramref name="suffix"/>
        /// start with its closing one. Returns the header bytes written (for the caller's first-write log).
        /// </summary>
        public static byte[] WriteLspMessage(Stream stream, string prefix, string text, string suffix)
        {
            byte[] pre = Utf8.GetBytes(prefix ?? "");
            byte[] post = Utf8.GetBytes(suffix ?? "");
            text = text ?? "";
            long length = pre.Length + EscapedUtf8Length(text) + post.Length;
            byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + length + "\r\n\r\n");

            stream.Write(header, 0, header.Length);
            stream.Write(pre, 0, pre.Length);
            var enc = Utf8.GetEncoder();
            var chars = new char[ChunkChars];
            var bytes = new byte[Utf8.GetMaxByteCount(ChunkChars)];
            int pos = 0;
            while (pos < text.Length)
            {
                int n = EscapeChunk(text, ref pos, chars);
                int b = enc.GetBytes(chars, 0, n, bytes, 0, pos >= text.Length);
                stream.Write(bytes, 0, b);
            }
            stream.Write(post, 0, post.Length);
            return header;
        }
    }
}
