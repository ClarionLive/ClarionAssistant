using System.Collections.Generic;
using System.IO;

// ClarionAppDataReader.cs's IDE-coupled dependencies (AppTreeService wraps the live Clarion app
// tree via ICSharpCode.SharpDevelop.Gui/WinForms; RedFileService pulls in ClarionVersionService's
// own dependency chain) are reachable only from code paths this harness doesn't exercise —
// FindStructureAtLine/ParseLocalData are pure string parsing with no I/O. Minimal stand-ins let the
// real, unmodified ClarionAppDataReader.cs compile standalone without dragging the IDE in.
namespace ClarionAssistant.Services
{
    public class AppTreeService
    {
        public Dictionary<string, object> GetAppInfo() { return null; }
    }

    public class RedFile
    {
        public string ResolveFrom(string name, string dir, params string[] configs) { return null; }
        public string Resolve(string name, params string[] configs) { return null; }
    }

    public static class RedFileService
    {
        public static RedFile Active { get { return null; } }
    }

    public static class EncodingHelper
    {
        public static string[] ReadAllLines(string path, out System.Text.Encoding enc)
        {
            enc = System.Text.Encoding.Default;
            return File.ReadAllLines(path);
        }
    }
}
