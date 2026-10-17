using System;
using ClarionAssistant.Services;

// Regression harness for GH #263 question 2: a generated .clw opened in the CA Editor as a plain file
// must not offer the hover card's attribute chips, because the next generate would overwrite the edit.
// Compiles the REAL Services\GeneratedModuleMatch.cs (zero IDE coupling).
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
static class GeneratedModuleMatchTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name); }
    }

    static int Main()
    {
        Console.WriteLine("GeneratedModuleMatch (GH #263 Q2: plain names in generated modules)");
        var mods = new[] { "PRM002081.clw", "Prm002082", null, "", "C:\\Elsewhere\\prm002083.CLW" };
        var apps = new[] { "C:\\Apps\\Invoice.app", null };

        Ok("a procedure module, same case", GeneratedModuleMatch.IsGeneratedModule("C:\\Apps\\PRM002081.clw", mods, apps));
        Ok("a procedure module, other case and folder", GeneratedModuleMatch.IsGeneratedModule("D:\\clw\\prm002081.CLW", mods, apps));
        Ok("a bare module name gets .clw", GeneratedModuleMatch.IsGeneratedModule("C:\\Apps\\PRM002082.clw", mods, apps));
        Ok("a module given as a full path", GeneratedModuleMatch.IsGeneratedModule("PRM002083.clw", mods, apps));
        Ok("the app's PROGRAM module <app>.clw", GeneratedModuleMatch.IsGeneratedModule("C:\\Apps\\INVOICE.clw", mods, apps));

        Ok("a hand-coded source is not generated", !GeneratedModuleMatch.IsGeneratedModule("C:\\Apps\\MyClass.clw", mods, apps));
        Ok("the bare name needs .clw, not any extension", !GeneratedModuleMatch.IsGeneratedModule("C:\\Apps\\PRM002082.inc", mods, apps));
        Ok("the app file itself is not a module", !GeneratedModuleMatch.IsGeneratedModule("C:\\Apps\\Invoice.app", mods, apps));
        Ok("no apps open: nothing is generated", !GeneratedModuleMatch.IsGeneratedModule("C:\\Apps\\PRM002081.clw", null, null));
        Ok("no file: false, no throw", !GeneratedModuleMatch.IsGeneratedModule(null, mods, apps)
                                        && !GeneratedModuleMatch.IsGeneratedModule("  ", mods, apps));
        Ok("an invalid path: false, no throw", !GeneratedModuleMatch.IsGeneratedModule("C:\\bad<|>name.clw", new[] { "x<|>.clw" }, new[] { "a<|>.app" }));

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
