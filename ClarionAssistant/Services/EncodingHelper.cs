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
    /// </summary>
    public static class EncodingHelper
    {
        public static Encoding DetectFileEncoding(string path)
        {
            try
            {
                byte[] bytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bytes = new byte[fs.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = fs.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) break;
                        offset += read;
                    }
                }

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
            catch { }
            return Encoding.Default;
        }
    }
}
