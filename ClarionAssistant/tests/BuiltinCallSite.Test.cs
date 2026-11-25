using System;
using ClarionAssistant.Services;

// Harness for BuiltinCallSite: is the cursor on a call of a Clarion built-in procedure?
//
// THE BUG. Find All References and F12 on ERRORCODE() in `IF ERRORCODE() = 0` returned one location: the
// field ErrorCode of an unrelated QUEUE,TYPE in an include file. The language server answered nothing for the
// built-in (correctly: it has no source declaration), so SharedLspBridge fell back to a CodeGraph lookup by name
// and took the first symbol called ErrorCode. A built-in followed by '(' cannot be a variable or a field, so the
// fallback now returns the server's empty answer for it.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class BuiltinCallSiteTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name); }
    }

    static bool At(string line, string word, int offset = 0)
    {
        return BuiltinCallSite.IsBuiltinCallAt(line, line.IndexOf(word, StringComparison.Ordinal) + offset);
    }

    static void Calls()
    {
        const string check = "    IF ERRORCODE() = 0";
        int start = check.IndexOf("ERRORCODE"), end = start + "ERRORCODE".Length;
        bool all = true;
        for (int i = start; i <= end; i++)
            if (!BuiltinCallSite.IsBuiltinCallAt(check, i)) { all = false; Console.WriteLine("    miss at " + i); }
        Ok("B1 every position on ERRORCODE in IF ERRORCODE() = 0, word end included", all);
        Ok("B2 a call with arguments", At("  Name = CLIP(Name)", "CLIP"));
        Ok("B3 a space before the parenthesis", At("  IF ERRORCODE () THEN BREAK.", "ERRORCODE"));
        Ok("B4 case does not matter", At("  if errorcode() then break.", "errorcode"));
        Ok("B5 a statement-style built-in", At("  OPEN(MyFile)", "OPEN"));
        Ok("B6 a call inside a string concatenation", At("  Msg = 'Error ' & ERROR()", "ERROR("));
        Ok("B7 FILEERRORCODE, a name that also starts another built-in", At("  x = FILEERRORCODE()", "FILEERRORCODE"));
        Ok("B8 a call after a string that holds a '!'", At("  Msg = 'Hi!' & CLIP(Name)", "CLIP"));
    }

    static void NotCalls()
    {
        Ok("N1 the same name without '(' is a label the fallback may still look up",
           !At("  x = ErrorCode + 1", "ErrorCode"));
        Ok("N2 the field declaration itself", !At("ErrorCode             LONG", "ErrorCode"));
        Ok("N3 a method with a built-in's name", !At("  SELF.Error('x')", "Error"));
        Ok("N3b a method call with a built-in's name and parentheses", !At("  x = SELF.Error()", "Error"));
        Ok("N4 a queue field called with a built-in's name and parentheses", !At("  x = MyQ.ErrorCode()", "ErrorCode"));
        Ok("N5 a prefixed label", !At("  x = Loc:Clip('a')", "Clip"));
        Ok("N5b a prefixed label, cursor at its end (on the parenthesis)", !At("  x = Loc:Clip('a')", "("));
        Ok("N6 a field equate", !At("  ?Error{PROP:Text} = 'x'", "Error"));
        Ok("N7 a user procedure", !At("  MyProc()", "MyProc"));
        Ok("N8 inside a string literal", !At("  Msg = 'call ERRORCODE() here'", "ERRORCODE"));
        Ok("N9 a doubled quote does not end the string", !At("  Msg = 'it''s ERRORCODE() here'", "ERRORCODE"));
        Ok("N10 after a ! comment", !At("  x = 1 ! IF ERRORCODE() THEN", "ERRORCODE"));
        Ok("N11 the closing parenthesis, not the name", !At("  IF ERRORCODE() = 0", ")"));
        Ok("N12 a longer name that contains a built-in", !At("  MyCLIP(Name)", "CLIP"));
        Ok("N13 no line, negative or past-the-end position",
           !BuiltinCallSite.IsBuiltinCallAt(null, 0) && !BuiltinCallSite.IsBuiltinCallAt("CLIP(x)", -1)
           && !BuiltinCallSite.IsBuiltinCallAt("CLIP(x)", 99));
    }

    static int Main()
    {
        try
        {
            Calls();
            NotCalls();
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  [FAIL] threw: " + ex);
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail));
        return fail == 0 ? 0 : 1;
    }
}
