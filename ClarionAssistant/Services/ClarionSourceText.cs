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
            if (content[0] == '﻿') content = content.Substring(1);

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

            Encoding enc = ResolveEncoding(path);
            // UTF-16/32 has no BOM-free form Clarion can read; UTF-8 without a BOM is what this path
            // wrote for those before GH #203, and a UTF-16 .clw is not something Clarion produces.
            if (enc.CodePage != EncodingHelper.Ansi.CodePage) enc = EncodingHelper.Utf8NoBom;

            // Encode BEFORE opening the file, so a refusal leaves the disk exactly as it was.
            byte[] bytes = Encode(Normalize(content), enc, path);
            File.WriteAllBytes(path, bytes);
            return enc;
        }

        /// <summary>
        /// Appends a CRLF and then <paramref name="text"/> to the existing file at
        /// <paramref name="path"/>. For Clarion source the text is CRLF-normalized and encoded in the
        /// file's own encoding — appending UTF-8 onto an ANSI file leaves one file in two encodings,
        /// which no single decode can read (GH #203). The existing bytes, BOM included, are never
        /// rewritten. Any other file keeps the prior behavior.
        /// </summary>
        /// <exception cref="ClarionEncodingException">
        /// The text holds a character the file's code page cannot represent. Nothing is written.
        /// </exception>
        public static void AppendFile(string path, string text)
        {
            if (!IsClarionSource(path))
            {
                File.AppendAllText(path, "\r\n" + text);
                return;
            }

            Encoding enc = ResolveEncoding(path);
            byte[] eol = enc.GetBytes("\r\n");
            byte[] body = Encode(Normalize(text), enc, path);

            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                fs.Write(eol, 0, eol.Length);
                fs.Write(body, 0, body.Length);
            }
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
