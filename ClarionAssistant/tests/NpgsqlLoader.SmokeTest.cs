using System;
using System.IO;
using ClarionAssistant.Services;

// Verifies NpgsqlLoader tells "Npgsql is not there" apart from "Npgsql is there but broken" (GH #188).
//
// Run:  tests\Run-Tests.ps1
//
// Only a missing Npgsql.dll may be reported as not found. A present-but-broken one (wrong version ->
// FileLoadException, wrong bitness or a corrupt file -> BadImageFormatException) must surface the
// loader's own message, or a broken install becomes undiagnosable. The two real loads below run
// against this harness's own folder: first with no Npgsql.dll, then, in a fresh AppDomain (the
// runtime caches a failed bind per domain), with a garbage Npgsql.dll dropped beside the exe.
//
// Not in ClarionAssistant.csproj — it has its own Main(). See the note in the SmokeTest header.
static class NpgsqlLoaderSmokeTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    // Runs inside the second AppDomain.
    static void LoadInThisDomain()
    {
        string err;
        var asm = NpgsqlLoader.TryLoad(out err);
        AppDomain.CurrentDomain.SetData("loaded", asm != null);
        AppDomain.CurrentDomain.SetData("error", err);
    }

    static int Main()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string fake = Path.Combine(dir, "Npgsql.dll");
        if (File.Exists(fake))
        {
            Console.WriteLine("  Npgsql.dll already beside the harness; cannot test the not-found path: " + fake);
            return 2;
        }

        // --- classification ---
        Ok("FileNotFoundException -> not found",
           NpgsqlLoader.DescribeLoadFailure(new FileNotFoundException("x")) == NpgsqlLoader.NotFoundMessage, null);
        var fle = new FileLoadException("Could not load file or assembly 'Npgsql, Version=9.0.0.0' ... manifest definition does not match");
        string fleMsg = NpgsqlLoader.DescribeLoadFailure(fle);
        Ok("FileLoadException -> load-failed, not 'not found'",
           fleMsg.StartsWith(NpgsqlLoader.LoadFailedPrefix) && fleMsg != NpgsqlLoader.NotFoundMessage, fleMsg);
        Ok("FileLoadException keeps the loader's own text", fleMsg.EndsWith(fle.Message), fleMsg);
        string bifMsg = NpgsqlLoader.DescribeLoadFailure(new BadImageFormatException("wrong format"));
        Ok("BadImageFormatException -> load-failed with its text",
           bifMsg == NpgsqlLoader.LoadFailedPrefix + "wrong format", bifMsg);

        // --- real load, Npgsql absent ---
        string err;
        var asm = NpgsqlLoader.TryLoad(out err);
        Ok("absent: returns null without throwing", asm == null, null);
        Ok("absent: reports not found", err == NpgsqlLoader.NotFoundMessage, err);
        Ok("absent: ingest output byte-identical to before",
           ("Error: " + err) == "Error: Npgsql.dll not found. Place Npgsql.dll in the ClarionAssistant folder to enable PostgreSQL support.",
           err);

        // --- real load, Npgsql present but not a valid assembly ---
        File.WriteAllBytes(fake, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
        AppDomain domain = null;
        try
        {
            domain = AppDomain.CreateDomain("npgsql-broken", null, new AppDomainSetup { ApplicationBase = dir });
            domain.DoCallBack(LoadInThisDomain);
            bool loaded = (bool)domain.GetData("loaded");
            string brokenErr = (string)domain.GetData("error");
            Ok("broken: returns null without throwing", !loaded, null);
            Ok("broken: NOT reported as not found", brokenErr != NpgsqlLoader.NotFoundMessage, brokenErr);
            Ok("broken: friendly prefix plus the loader's text",
               brokenErr != null && brokenErr.StartsWith(NpgsqlLoader.LoadFailedPrefix) && brokenErr.Length > NpgsqlLoader.LoadFailedPrefix.Length,
               brokenErr);
            Console.WriteLine("         " + brokenErr);
        }
        finally
        {
            if (domain != null) AppDomain.Unload(domain);
            try { File.Delete(fake); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS (" + pass + ")" : fail + " FAILED, " + pass + " passed");
        return fail == 0 ? 0 : 1;
    }
}
