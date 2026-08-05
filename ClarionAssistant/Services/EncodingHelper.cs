using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Shared file-encoding detection for the codebase. Clarion source is traditionally
    /// saved as Windows-ANSI with no BOM; decoding it as UTF-8 (the .NET default when no
    /// BOM is present) mangles high-bit characters. Plain .cs files in this repo are
    /// typically real UTF-8 WITHOUT a BOM, so a no-BOM file can't be assumed ANSI outright —
    /// a strict UTF-8 decode attempt disambiguates the two (invalid UTF-8 byte sequences,
    /// e.g. a lone cp1252 high-bit byte, throw and fall back to ANSI).
    ///
    /// Prefer <see cref="ReadAllText(string, out Encoding)"/> over
    /// <see cref="DetectFileEncoding"/> + <c>File.ReadAllText</c>: detection has to read the
    /// whole file to attempt the strict decode, so the pair opens, reads and decodes the file
    /// TWICE and throws the first decode away. On the LSP text-sync path that repeats per
    /// didChange, and on a generated .clw both the byte[] and the discarded string are large
    /// enough to land on the Large Object Heap.
    /// </summary>
    public static class EncodingHelper
    {
        /// <summary>
        /// Read a file and report the encoding it was decoded with, opening and decoding it ONCE.
        /// Observationally identical to <c>File.ReadAllText(path, DetectFileEncoding(path))</c> —
        /// same text, same reported encoding, same exceptions, same FileShare — but without the
        /// second read. Callers that already tolerate an unreadable file keep their try/catch.
        /// </summary>
        public static string ReadAllText(string path, out Encoding encoding)
        {
            // FileShare.Read, matching File.ReadAllText. Detection uses ReadWrite, but the pair's
            // NET behaviour was the stricter of the two: detection would succeed on a file held
            // open for writing and the follow-up read would then throw. Widening the share mode
            // here would silently hand callers a half-written file instead of that exception —
            // a real change in failure semantics, and not one a read-once change should make.
            byte[] bytes = ReadAllBytes(path, FileShare.Read);

            // Anything BOM-shaped is decoded by a StreamReader over the buffer we already hold,
            // which inherits File.ReadAllText's exact semantics for free: preamble stripping, a
            // dropped trailing partial code unit, and UTF-32 sniffing. Decoding a BOM'd buffer by
            // hand gets all three subtly wrong — GetString keeps the U+FEFF, and flushes a partial
            // trailing unit through the fallback as U+FFFD, which is precisely the character the
            // server's UnicodeDiagnostics flags. No extra IO: same bytes, just wrapped.
            if (StartsWithBom(bytes))
            {
                using (var ms = new MemoryStream(bytes, false))
                using (var reader = new StreamReader(ms, DetectFromBytes(bytes), true))
                {
                    string bomText = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;   // post-read: reflects what StreamReader sniffed
                    return bomText;
                }
            }

            // No BOM — the common Clarion case, and the one worth the fast path. A strict decode
            // both TESTS the UTF-8 hypothesis and PRODUCES the text, so a UTF-8 file is decoded
            // exactly once; DetectFileEncoding has to throw this same string away. When it isn't
            // UTF-8 the attempt aborts at the first invalid byte rather than decoding the whole
            // buffer, so the cp1252 path costs one full decode, not two.
            try
            {
                string text = new UTF8Encoding(false, true).GetString(bytes);
                encoding = new UTF8Encoding(false);
                return text;
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Default;
                return encoding.GetString(bytes);
            }
        }

        /// <summary>
        /// Detect a file's encoding without keeping the text. Use only where the text is genuinely
        /// not wanted, or is read in a different shape (e.g. <c>File.ReadAllLines</c> for a
        /// sub-range) — otherwise <see cref="ReadAllText(string, out Encoding)"/> gets both for the
        /// price of one read. Returns <c>Encoding.Default</c> if the file can't be read.
        /// </summary>
        public static Encoding DetectFileEncoding(string path)
        {
            try
            {
                return DetectFromBytes(ReadAllBytes(path, FileShare.ReadWrite));
            }
            catch { }
            return Encoding.Default;
        }

        /// <summary>
        /// True if the buffer opens with a byte pattern <c>StreamReader</c> would treat as a BOM.
        /// UTF-32LE (FF FE 00 00) is deliberately covered by the UTF-16LE prefix, matching what
        /// <see cref="DetectFromBytes"/> reports and therefore what the previous code did.
        /// </summary>
        private static bool StartsWithBom(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return true;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return true;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return true;
            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF) return true;
            return false;
        }

        private static Encoding DetectFromBytes(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;

            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default;
            }
        }

        private static byte[] ReadAllBytes(string path, FileShare share)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, share))
            {
                var bytes = new byte[fs.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = fs.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) break;
                    offset += read;
                }

                // The buffer is sized from fs.Length, but a short read leaves the tail zero-filled.
                // Harmless while this only fed a throwaway validity test; now the buffer IS the
                // text, so those pad bytes would decode to U+0000 and be handed to the server.
                if (offset != bytes.Length) System.Array.Resize(ref bytes, offset);
                return bytes;
            }
        }
    }
}
