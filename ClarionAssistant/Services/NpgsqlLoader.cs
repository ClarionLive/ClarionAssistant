using System;
using System.IO;
using System.Reflection;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Loads Npgsql dynamically, so PostgreSQL support needs no hard reference. The installer
    /// does not ship it (GH #188), so a load failure is usually an instruction for the user,
    /// not a bug. Kept free of other dependencies so tests\NpgsqlLoader.SmokeTest.cs can
    /// compile it on its own.
    /// </summary>
    public static class NpgsqlLoader
    {
        /// <summary>Npgsql.dll is absent. This wording predates the class; the ingest path's output depends on it.</summary>
        public const string NotFoundMessage =
            "Npgsql.dll not found. Place Npgsql.dll in the ClarionAssistant folder to enable PostgreSQL support.";

        /// <summary>
        /// Npgsql.dll is there but will not load (wrong version, wrong bitness, a missing
        /// dependency). The exception's own text is appended, because it names the culprit.
        /// </summary>
        public const string LoadFailedPrefix =
            "Npgsql.dll is present but could not be loaded. PostgreSQL support needs a .NET Framework compatible (netstandard2.0) Npgsql.dll and its dependencies in the ClarionAssistant folder. Loader error: ";

        /// <summary>Returns the Npgsql assembly, or null with a user-facing reason in <paramref name="error"/>.</summary>
        public static Assembly TryLoad(out string error)
        {
            try
            {
                error = null;
                return Assembly.Load("Npgsql");
            }
            catch (Exception ex)
            {
                error = DescribeLoadFailure(ex);
                return null;
            }
        }

        /// <summary>
        /// Only FileNotFoundException means "not there". FileLoadException and
        /// BadImageFormatException mean a broken install, and must stay diagnosable.
        /// </summary>
        public static string DescribeLoadFailure(Exception ex)
        {
            if (ex is FileNotFoundException)
                return NotFoundMessage;
            return LoadFailedPrefix + ex.Message;
        }
    }
}
