using System;
using ClarionAssistant.Services;

// Harness for ClarionClDiagnosis (GH #204) — compiles the REAL service file on its own.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class ClarionClDiagnosisTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name); }
    }

    static int Main()
    {
        // The exact signature from the #204 report.
        string reported = "Generating MyApp\r\n"
                        + "Could not gain access to MyApp.ap~ after 50 attempts\r\n"
                        + "error GENE000: Cannot open application C:\\Dev\\MyApp.app (status 32)\r\n";
        Ok("reported #204 output is recognised as an app lock", ClarionClDiagnosis.DescribeAppLock(reported) != null);
        Ok("the explanation tells the caller to close the app",
           (ClarionClDiagnosis.DescribeAppLock(reported) ?? "").IndexOf("close the app", StringComparison.Ordinal) >= 0);

        // Either half alone is enough.
        Ok("the .ap~ line alone is recognised",
           ClarionClDiagnosis.DescribeAppLock("Could not gain access to X.AP~ after 50 attempts") != null);
        Ok("the status 32 line alone is recognised",
           ClarionClDiagnosis.DescribeAppLock("error GENE000: Cannot open application X.app (status 32)") != null);

        // Things that must NOT be read as a lock.
        Ok("null output", ClarionClDiagnosis.DescribeAppLock(null) == null);
        Ok("a real compile error", ClarionClDiagnosis.DescribeAppLock("MyApp001.clw(32,5): error: Unknown identifier: FOO") == null);
        Ok("a different open failure status", ClarionClDiagnosis.DescribeAppLock("error GENE000: Cannot open application X.app (status 2)") == null);
        Ok("a bare 'status 32' with no application wording", ClarionClDiagnosis.DescribeAppLock("copied 32 files (status 32)") == null);
        Ok("the template-registration failure from #204 is a different problem",
           ClarionClDiagnosis.DescribeAppLock("Cannot create an application because no templates have been registered.") == null);

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }
}
