using System;
using System.IO;
using ClarionAssistant.Services;

// Runs the importer against whatever VS Code install is actually on this machine (auto-locate path),
// so the mapping is proven against a real file, not just a synthetic fixture. Prints ONLY the mapped
// CA settings — never the raw file contents.
//
// Run:  tests\Run-Tests.ps1 -Probe        (OPT-IN — excluded from the default run)
//
// This is a DIAGNOSTIC, not an assertion suite: it has no expected values and always exits 0, because
// what it reports depends entirely on the developer's own VS Code configuration. It is opt-in for a
// second reason — it reads a personal file, and a test run should not touch one unless asked. It is
// genuinely useful when a developer reports "the import found nothing": this says whether the file
// was located, which install it came from, and exactly what the mapping made of it.
//
// Read-only. It never writes settings.json, and never echoes its raw contents.
//
// Not in ClarionAssistant.csproj — it has its own Main(). See the note in the SmokeTest header.
static class LiveProbe
{
    static int Main()
    {
        var r = VsCodeSettingsImporter.Read(null);

        Console.WriteLine("found : " + r.Found);
        Console.WriteLine("source: " + (String.IsNullOrEmpty(r.Source) ? "(none located)" : r.Source));
        Console.WriteLine("path  : " + (String.IsNullOrEmpty(r.Path) ? "(none)" : r.Path));
        Console.WriteLine("error : " + (String.IsNullOrEmpty(r.Error) ? "(none)" : r.Error));
        Console.WriteLine();

        if (!r.Found)
        {
            Console.WriteLine("No VS Code settings.json located in the well-known %APPDATA% locations.");
            Console.WriteLine("(This is the case the Browse... fallback in item 3 exists for.)");
            return 0;
        }

        Console.WriteLine("mapped values (" + r.Values.Count + "):");
        foreach (var kv in r.Values)
            Console.WriteLine("   " + kv.Key.PadRight(22) + " = " + kv.Value);

        Console.WriteLine();
        Console.WriteLine("skipped (" + r.Skipped.Count + "):");
        foreach (var kv in r.Skipped)
            Console.WriteLine("   " + kv.Key + " -> " + kv.Value);

        return 0;
    }
}
