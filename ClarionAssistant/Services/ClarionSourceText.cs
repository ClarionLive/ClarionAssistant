using System;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Enforces the Clarion source-file hard rule when the addin writes to disk:
    /// UTF-8 without BOM and CRLF line endings. Clarion's compiler and IDE reject
    /// or misparse files with LF-only endings or a UTF-8 BOM, so any source the
    /// addin writes is normalized here regardless of the line endings the caller
    /// (e.g. the model via write_file / append_to_file) supplied. See issue #34.
    /// </summary>
    internal static class ClarionSourceText
    {
        // Source / template extensions the Clarion toolchain requires to be
        // CRLF + no-BOM. Matches the hard-rule list in issue #34.
        private static readonly string[] ClarionExtensions =
            { ".clw", ".inc", ".equ", ".tpw", ".tpl" };

        // UTF-8 without BOM. Byte-identical to ANSI for the ASCII that virtually
        // all Clarion source is, but preserves any non-ASCII chars in comments or
        // string literals instead of replacing them with '?' (the strict-ANSI risk).
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

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
        /// Writes <paramref name="content"/> to <paramref name="path"/>. Clarion
        /// source files are normalized to CRLF and written as UTF-8 without BOM;
        /// any other file is written verbatim (unchanged from the prior behavior).
        /// </summary>
        public static void WriteFile(string path, string content)
        {
            if (IsClarionSource(path))
                File.WriteAllText(path, Normalize(content), Utf8NoBom);
            else
                File.WriteAllText(path, content);
        }

        /// <summary>
        /// Returns <paramref name="text"/> CRLF-normalized when <paramref name="path"/>
        /// is a Clarion source file, otherwise unchanged. Used by append paths where
        /// the existing file's encoding is left alone but the appended text must
        /// still carry CRLF line endings.
        /// </summary>
        public static string NormalizeIfClarion(string path, string text)
        {
            return IsClarionSource(path) ? Normalize(text) : text;
        }
    }
}
