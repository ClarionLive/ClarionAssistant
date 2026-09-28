// Harness for Terminal\SolutionStamp.cs (ticket 82938fc7, pipeline run 2 R1): the key the Schema Sources
// modal and the Source Control fields are drawn under.
//
// Compiled against the REAL SolutionStamp.cs by Run-Tests.ps1.
//
// What each check stops:
//   A->B->A           - a Select / repo save queued while the IDE switched away and back matching A again and
//                       writing an obsolete snapshot (the path-only check let it through)
//   current matches   - a legitimate write refused
//   other solution    - a write for A applied to B (pipeline run 1, P1)
//   case / normalise  - the same .sln spelled differently refused
//   empty / missing   - a view the host never stamped being accepted
using System;
using ClarionAssistant.Terminal;

static class SolutionStampTest
{
    static int _failures, _assertions;

    static void Check(bool ok, string what)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what); }
        else Console.WriteLine("  ok   " + what);
    }

    static int Main()
    {
        const string A = @"C:\Apps\School\school.sln";
        const string B = @"D:\Work\Pos\pos.sln";
        var stamp = new SolutionStamp();

        stamp.Advance();                       // A shown
        long genA1 = stamp.Gen;
        Check(stamp.Matches(A, genA1, A), "a write drawn for the current solution and generation is applied");
        Check(!stamp.Matches(B, genA1, A), "a write drawn for another solution is refused");
        Check(stamp.Matches(@"c:\apps\school\SCHOOL.SLN", genA1, A), "the same .sln in another case matches");
        Check(stamp.Matches(@"C:\Apps\School\.\school.sln", genA1, A), "the same .sln un-normalised matches");

        stamp.Advance();                       // A -> B
        Check(!stamp.Matches(A, genA1, B), "after A->B, A's write is refused");
        stamp.Advance();                       // B -> A
        long genA2 = stamp.Gen;
        Check(!stamp.Matches(A, genA1, A), "A->B->A: a write drawn before the switch (old generation) is refused");
        Check(stamp.Matches(A, genA2, A), "A->B->A: a write drawn after coming back is applied");
        Check(genA2 > genA1, "the generation only moves forward");

        Check(!stamp.Matches(null, genA2, A), "no echoed solution is refused");
        Check(!stamp.Matches("", genA2, A), "an empty echoed solution is refused");
        Check(!stamp.Matches(A, -1, A), "no echoed generation (-1) is refused");
        Check(!stamp.Matches(A, genA2, null), "no current solution refuses every write");

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS (" + _assertions + ")" : _failures + " FAILED of " + _assertions);
        return _failures == 0 ? 0 : 1;
    }
}
