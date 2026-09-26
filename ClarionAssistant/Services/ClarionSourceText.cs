using System;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Enforces the Clarion source-file hard rules when the addin writes to disk: CRLF line
    /// endings, no BOM, and the file's OWN encoding. Clarion's compiler and IDE reject or misparse
    /// files with LF-only endings or a UTF-8 BOM (issue #34), and Clarion is an ANSI toolchain, so
    /// silently re-encoding an ANSI file as UTF-8 turns every accented character into mojibake
    /// (GH #203). Any source the addin writes is normalized here regardless of what the caller
    /// (e.g. the model via write_file / append_to_file) supplied.
    /// </summary>
    internal static class ClarionSourceText
    {
        // Source / template extensions the Clarion toolchain reads. Same text-file set the Monaco
        // opener treats as Clarion (MonacoFileOpener, minus the binary .app).
        private static readonly string[] ClarionExtensions =
            { ".clw", ".inc", ".equ", ".int", ".trn", ".tpw", ".tpl" };

        /// <summary>True if the path has a Clarion source/template extension.</summary>
        public static bool IsClarionSource(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            foreach (var e in ClarionExtensions)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Strips a leading BOM and rewrites every line ending (CRLF, lone CR, or
        /// lone LF) as CRLF. Idempotent — already-correct content is returned
        /// unchanged byte-for-byte.
        /// </summary>
        public static string Normalize(string content)
        {
            if (string.IsNullOrEmpty(content)) return content ?? string.Empty;

            // Drop a leading BOM (U+FEFF) if one slipped into the string.
            if (content[0] == '\uFEFF') content = content.Substring(1);

            var sb = new StringBuilder(content.Length + 16);
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c == '\r')
                {
                    sb.Append("\r\n");
                    // Skip the LF of an existing CRLF so it isn't doubled.
                    if (i + 1 < content.Length && content[i + 1] == '\n') i++;
                }
                else if (c == '\n')
                {
                    sb.Append("\r\n");
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="path"/>. Clarion source files are
        /// normalized to CRLF and written, without a BOM, in the encoding <see cref="ResolveEncoding"/>
        /// picks; any other file is written verbatim (unchanged from the prior behavior). Returns the
        /// encoding a Clarion source file was written in, or null for any other file.
        /// </summary>
        /// <exception cref="ClarionEncodingException">
        /// The content holds a character the file's code page cannot represent. Nothing is written.
        /// </exception>
        public static Encoding WriteFile(string path, string content)
        {
            if (!IsClarionSource(path))
            {
                File.WriteAllText(path, content);
                return null;
            }
            return WriteFile(path, content, ResolveEncoding(path));
        }

        /// <summary>
        /// Writes Clarion source in an encoding the CALLER chose, for files whose encoding comes from
        /// somewhere other than their own previous contents: a class created from a model file takes
        /// the model's (<c>ResolveEncoding(modelPath)</c>), and a scratch file takes
        /// <see cref="EncodingHelper.Ansi"/>. Same CRLF / no-BOM / refuse-rather-than-'?' rules as
        /// the one-argument overload, whatever the extension.
        /// </summary>
        /// <exception cref="ClarionEncodingException">
        /// The content holds a character <paramref name="enc"/> cannot represent. Nothing is written.
        /// </exception>
        public static Encoding WriteFile(string path, string content, Encoding enc)
        {
            // UTF-16/32 has no BOM-free form Clarion can read; UTF-8 without a BOM is what this path
            // wrote for those before GH #203, and a UTF-16 .clw is not something Clarion produces.
            if (enc.CodePage != EncodingHelper.Ansi.CodePage) enc = EncodingHelper.Utf8NoBom;

            // Encode BEFORE opening the file, so a refusal leaves the disk exactly as it was.
            byte[] bytes = Encode(Normalize(content), enc, path);
            File.WriteAllBytes(path, bytes);
            return enc;
        }

        /// <summary>
        /// Appends <paramref name="text"/> to the end of the existing file at <paramref name="path"/>,
        /// on a new line: a CRLF goes in first ONLY when the file is non-empty and does not already end
        /// in a line break (GH #232 — an unconditional CRLF put a blank line after every file that
        /// ends in one, which is most of them). An empty file, or one holding nothing but a BOM, gets
        /// no leading break. For Clarion source the text is CRLF-normalized and encoded in the file's
        /// own encoding — appending UTF-8 onto an ANSI file leaves one file in two encodings, which no
        /// single decode can read (GH #203). The existing bytes, BOM included, are never rewritten.
        /// Any other file has its text appended verbatim (UTF-8, no BOM), as before.
        /// </summary>
        /// <exception cref="ClarionEncodingException">
        /// The text holds a character the file's code page cannot represent. Nothing is written.
        /// </exception>
        public static void AppendFile(string path, string text)
        {
            if (!IsClarionSource(path))
            {
                // File.AppendAllText writes UTF-8, so the break test looks for UTF-8 (= ASCII) bytes.
                File.AppendAllText(path, (NeedsLeadingBreak(path, EncodingHelper.Utf8NoBom) ? "\r\n" : "") + text);
                return;
            }

            Encoding enc = ResolveEncoding(path);
            // Check-then-append is not atomic (another writer could append in between); accepted for a local tool.
            byte[] eol = NeedsLeadingBreak(path, enc) ? enc.GetBytes("\r\n") : new byte[0];
            byte[] body = Encode(Normalize(text), enc, path);

            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                fs.Write(eol, 0, eol.Length);
                fs.Write(body, 0, body.Length);
            }
        }

        /// <summary>
        /// True when text appended to <paramref name="path"/> must be preceded by a line break: the
        /// file has content (a BOM alone is not content) and its last character is not a line break
        /// (LF, or a lone CR, as <paramref name="enc"/> encodes it). This helper reads only the file's head and tail
        /// (AppendFile as a whole still reads the file once, in ResolveEncoding).
        /// </summary>
        private static bool NeedsLeadingBreak(string path, Encoding enc)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length;
                if (len == 0) return false;

                byte[] head = new byte[(int)Math.Min(4, len)];
                ReadFully(fs, head);
                long contentLen = len - BomLength(head);
                if (contentLen <= 0) return false;

                foreach (string brk in new[] { "\n", "\r" })
                {
                    byte[] want = enc.GetBytes(brk);
                    if (want.Length == 0 || contentLen < want.Length) continue;
                    byte[] tail = new byte[want.Length];
                    fs.Seek(len - want.Length, SeekOrigin.Begin);
                    ReadFully(fs, tail);
                    bool same = true;
                    for (int i = 0; i < want.Length; i++) if (tail[i] != want[i]) { same = false; break; }
                    if (same) return false;
                }
                return true;
            }
        }

        private static void ReadFully(Stream s, byte[] buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
            }
        }

        // Length of the byte-order mark at the start of a file (UTF-8, UTF-32 LE/BE, UTF-16 LE/BE), 0 if none.
        private static int BomLength(byte[] h)
        {
            if (h.Length >= 3 && h[0] == 0xEF && h[1] == 0xBB && h[2] == 0xBF) return 3;
            if (h.Length >= 4 && h[0] == 0xFF && h[1] == 0xFE && h[2] == 0 && h[3] == 0) return 4;
            if (h.Length >= 4 && h[0] == 0 && h[1] == 0 && h[2] == 0xFE && h[3] == 0xFF) return 4;
            if (h.Length >= 2 && ((h[0] == 0xFF && h[1] == 0xFE) || (h[0] == 0xFE && h[1] == 0xFF))) return 2;
            return 0;
        }

        /// <summary>
        /// The encoding to write Clarion source at <paramref name="path"/> in. A file keeps its own
        /// UTF-8 only where there is EVIDENCE for UTF-8: a BOM, or valid UTF-8 that contains at least
        /// one multi-byte sequence. Everything else — a new file, an all-ASCII file, a file that is not
        /// valid UTF-8 — is the system ANSI code page (<see cref="EncodingHelper.Ansi"/>).
        ///
        /// WHY ALL-ASCII IS ANSI. Pure ASCII is also valid UTF-8, so "valid UTF-8 means UTF-8" would
        /// flip an ASCII .clw to UTF-8 the first time the model writes an accented character into it.
        /// The Clarion IDE then shows that character as two, and the compiler bakes both bytes into the
        /// program — the GH #203 corruption, one write later. ASCII says nothing about the encoding;
        /// the toolchain's own default decides it.
        ///
        /// Detection is <see cref="EncodingHelper.ReadAllText(string, out Encoding)"/>, the same ladder
        /// the Modern Embeditor and the diff viewer use to round-trip a file's encoding, so the tools
        /// and the editors cannot disagree about what a file is.
        ///
        /// KNOWN LIMIT OF THE HEURISTIC. A no-BOM file is UTF-8 if its bytes decode as valid UTF-8,
        /// and an ANSI file can pass that test by accident: every high-bit byte has to be part of a
        /// well-formed sequence, e.g. cp1252 "Ã©" (C3 A9) reads as UTF-8 "é". Such a file is treated
        /// as UTF-8 and written back as UTF-8. Real Clarion source essentially never lines up like that
        /// (an accented letter next to a lone one breaks it), and the embeditor makes the same call,
        /// so the two stay consistent. Without a BOM there is no way to tell them apart for certain.
        /// </summary>
        public static Encoding ResolveEncoding(string path)
        {
            if (!File.Exists(path)) return EncodingHelper.Ansi;

            Encoding detected;
            string text = EncodingHelper.ReadAllText(path, out detected);

            if (detected.CodePage == 65001)
            {
                bool hasBom = detected.GetPreamble().Length > 0;
                return hasBom || !IsAscii(text) ? (Encoding)EncodingHelper.Utf8NoBom : EncodingHelper.Ansi;
            }
            if (detected.CodePage == EncodingHelper.Ansi.CodePage) return EncodingHelper.Ansi;
            return detected;   // UTF-16/32, identified by its BOM
        }

        private static bool IsAscii(string text)
        {
            foreach (char c in text) if (c > '\x7F') return false;
            return true;
        }

        /// <summary>
        /// Encode <paramref name="text"/>, refusing — rather than writing '?' — any character an ANSI
        /// code page cannot hold. A silent '?' is data loss the model never hears about; an error
        /// names the character so it can choose another. Unicode encodings keep their replacement
        /// behavior: they can hold every character, and the only thing they replace is a lone
        /// surrogate, which EncodingHelper.Utf8NoBom deliberately does not throw on (10dfb53).
        /// </summary>
        private static byte[] Encode(string text, Encoding enc, string path)
        {
            // The second test only matters on a machine whose ANSI code page IS UTF-8 (Windows' beta
            // "Use Unicode UTF-8 for worldwide language support" option sets ACP = 65001). There
            // Ansi.CodePage == 65001, so the first test passes UTF-8 through to the strict path, which
            // would then throw on a lone surrogate. UTF-8 holds every character, so there is nothing
            // to refuse: keep the replacement behavior.
            if (enc.CodePage != EncodingHelper.Ansi.CodePage || enc.CodePage == 65001)
                return enc.GetBytes(text);

            var strict = Encoding.GetEncoding(enc.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            try
            {
                return strict.GetBytes(text);
            }
            catch (EncoderFallbackException ex)
            {
                int line = 1;
                for (int i = 0; i < ex.Index && i < text.Length; i++) if (text[i] == '\n') line++;

                string shown, code;
                if (ex.CharUnknownHigh != '\0')
                {
                    shown = new string(new[] { ex.CharUnknownHigh, ex.CharUnknownLow });
                    code = "U+" + char.ConvertToUtf32(ex.CharUnknownHigh, ex.CharUnknownLow).ToString("X4");
                }
                else
                {
                    shown = ex.CharUnknown.ToString();
                    code = "U+" + ((int)ex.CharUnknown).ToString("X4");
                }

                throw new ClarionEncodingException(
                    "'" + shown + "' (" + code + ") on line " + line + " can't be stored in " + Path.GetFileName(path) +
                    ", which is " + enc.EncodingName + " (code page " + enc.CodePage + "). Clarion source is written " +
                    "in the ANSI code page, so the character would have become '?'. Nothing was written.");
            }
        }
    }

    /// <summary>
    /// A Clarion source write refused because the text holds a character the file's code page
    /// cannot represent. The file on disk is untouched.
    /// </summary>
    internal sealed class ClarionEncodingException : Exception
    {
        public ClarionEncodingException(string message) : base(message) { }
    }
}
