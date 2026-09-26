using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Recognises ClarionCL failures whose output hides what actually went wrong, so the build tools
    /// can say it plainly instead of leaving the caller to pattern-match raw generator output.
    ///
    /// Kept free of IDE coupling so tests\ClarionClDiagnosis.Test.cs can compile the real file alone.
    /// </summary>
    internal static class ClarionClDiagnosis
    {
        /// <summary>
        /// GH #204. When the .app is open in a Clarion IDE, ClarionCL can't get exclusive access to it
        /// and fails with, e.g.:
        ///
        ///     Could not gain access to MyApp.ap~ after 50 attempts
        ///     error GENE000: Cannot open application ... (status 32)
        ///
        /// That reads like any other generation failure, but it is a file lock (Windows error 32,
        /// sharing violation), and usually a self-inflicted one: build_app/generate_source default to
        /// the app open in THIS IDE, which is exactly the IDE holding the lock. Returns a one-paragraph
        /// explanation when the output carries that signature, or null when it doesn't.
        ///
        /// Both halves are checked on their own: either line alone is enough to identify the lock, and
        /// requiring both would miss the output of a ClarionCL build that words one of them differently.
        /// "status 32" is matched only with the "Cannot open application" wording beside it, because a
        /// bare "32" can turn up anywhere in compiler output.
        /// </summary>
        internal static string DescribeAppLock(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;

            bool apTilde = output.IndexOf("Could not gain access to", StringComparison.OrdinalIgnoreCase) >= 0
                        && output.IndexOf(".ap~", StringComparison.OrdinalIgnoreCase) >= 0;
            bool status32 = output.IndexOf("Cannot open application", StringComparison.OrdinalIgnoreCase) >= 0
                         && output.IndexOf("(status 32)", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!apTilde && !status32) return null;

            return "DIAGNOSIS: the .app is locked by a Clarion IDE (sharing violation, status 32). This is not a "
                 + "template, source or project problem. Most often the IDE holding the lock is the one Clarion "
                 + "Assistant is running in: close the app in this IDE (or build from the IDE itself) and retry.";
        }
    }
}
