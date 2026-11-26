// ReferenceLocations.cs turns a location's uri into a path through SharedLspBridge.UriToFilePath. The real
// SharedLspBridge drags in the whole LSP client, so this stand-in carries only that one method, with the
// same body, to let the REAL, unmodified ReferenceLocations.cs compile standalone.
using System;

namespace ClarionAssistant.Services
{
    public static class SharedLspBridge
    {
        public static string UriToFilePath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return uri;
            string p = uri;
            if (p.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) p = p.Substring(8);
            else if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) p = p.Substring(7);
            try { p = Uri.UnescapeDataString(p); }
            catch { p = p.Replace("%3A", ":").Replace("%3a", ":").Replace("%20", " "); }
            return p.Replace("/", "\\");
        }
    }
}
