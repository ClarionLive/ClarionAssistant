using System.Collections.Generic;

// ClarionAppDataReader.cs's IDE-coupled types, for LocalLayer.Handlers.Test (same stand-ins as
// ClarionAppDataReader.StructureScan.Stubs.cs, minus EncodingHelper: this harness compiles the real one,
// which the real LspClient.cs needs). Never reached by the code under test.
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
        public static readonly string[] BuildSectionOrder = { "Debug32", "Release32", "Debug", "Release", "Common" };
    }
}
