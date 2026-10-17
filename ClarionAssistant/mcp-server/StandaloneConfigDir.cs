using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

namespace ClarionAssistant.McpServer
{
    /// <summary>The settings folder a solution's Clarion reads, and who said so (e1df6c2e).</summary>
    internal sealed class ConfigDirChoice
    {
        /// <summary>The folder holding ClarionProperties.xml, or null: the default per-version folders apply.</summary>
        public string Path;
        /// <summary>One of the <see cref="StandaloneConfigDir"/> source constants.</summary>
        public string Source;
        /// <summary>The sentence for startup stderr and get_solution_info's settingsChosenBy.</summary>
        public string Note;
        /// <summary>
        /// Set when a folder was NAMED but cannot be used (missing, no ClarionProperties.xml). The caller then has no
        /// Clarion version and says this, rather than read a different folder: never guess a relocated folder.
        /// </summary>
        public string Error;
    }

    /// <summary>
    /// "Which Clarion settings folder?" for the standalone server (e1df6c2e, GH #244 item 1).
    ///
    /// Clarion.exe's <c>/ConfigDir=&lt;path&gt;</c> moves the whole settings tree - ClarionProperties.xml (versions,
    /// macros, redirection) and preferences\ - away from %APPDATA%\SoftVelocity\Clarion\&lt;X.Y&gt;. Inside the IDE
    /// ClarionConfigDirectory reads it from the IDE (#197). A server with no IDE has no Clarion command line to read,
    /// so it went on reading the default folder: the wrong macros, the wrong .red paths, silently.
    ///
    /// The order, first that applies:
    ///   1. a LIVE IDE that has this solution open on a relocated folder (its published record). It is what that
    ///      IDE actually reads, so it beats anything typed by hand; a live IDE on the DEFAULT folder adds nothing
    ///      (the default folders are already the fallback, and taking its copy would bring back #244's stale copy);
    ///   2. --clarion-config-dir &lt;path&gt;;
    ///   3. the CLARION_CONFIG_DIR environment variable (a plugin config can set it, it cannot pass arguments);
    ///   4. "configDir" in the solution's clarion-assistant.json;
    ///   5. none: the default per-version folders, as before.
    /// A folder named in 2-4 that has no ClarionProperties.xml is an ERROR, never a fall-through to the default:
    /// the user said where the settings are, and reading other ones would look like an answer.
    /// </summary>
    internal static class StandaloneConfigDir
    {
        public const string EnvVar = "CLARION_CONFIG_DIR";
        public const string ArgName = "--clarion-config-dir";

        public const string SourceIdeLive = "ide-live";
        public const string SourceCommandLine = "command-line";
        public const string SourceEnvironment = "environment";
        public const string SourceSolutionFile = "solution-file";
        public const string SourceDefault = "default";

        /// <param name="liveRecords">Live IDE records for THIS solution (already filtered), or null.</param>
        /// <param name="commandLine">--clarion-config-dir as given, or null.</param>
        /// <param name="environment">CLARION_CONFIG_DIR as given, or null.</param>
        /// <param name="pin">clarion-assistant.json as read, or null.</param>
        /// <param name="baseDir">What a relative --clarion-config-dir / CLARION_CONFIG_DIR is relative to (the working directory).</param>
        /// <param name="isRelocated">ClarionConfigDirectory.IsOutsideDefaultRoot in production.</param>
        /// <param name="hasProperties">"does this folder hold a ClarionProperties.xml" (File.Exists in production).</param>
        public static ConfigDirChoice Resolve(IList<IdeSolutionRecord.Details> liveRecords, string commandLine,
                                              string environment, SolutionClarionVersion.Pin pin, string baseDir,
                                              Func<string, bool> isRelocated, Func<string, bool> hasProperties)
        {
            isRelocated = isRelocated ?? ClarionConfigDirectory.IsOutsideDefaultRoot;
            hasProperties = hasProperties ?? HasPropertiesXml;

            // What the user named, in order, so tier 1 can say what it overrode.
            var named = new List<KeyValuePair<string, string>>();   // source -> raw value
            if (!string.IsNullOrWhiteSpace(commandLine)) named.Add(new KeyValuePair<string, string>(SourceCommandLine, commandLine));
            if (!string.IsNullOrWhiteSpace(environment)) named.Add(new KeyValuePair<string, string>(SourceEnvironment, environment));
            if (pin != null && !string.IsNullOrEmpty(pin.ConfigDir)) named.Add(new KeyValuePair<string, string>(SourceSolutionFile, pin.ConfigDir));

            // ---- 1. A live IDE on this solution, on a relocated folder ----
            string liveDefaultNote = null;
            if (liveRecords != null)
            {
                foreach (var rec in liveRecords)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.ConfigDir)) continue;
                    string dir = SolutionClarionVersion.ResolveDir(rec.ConfigDir, null);
                    if (dir == null) continue;
                    if (!isRelocated(dir))
                    {
                        if (liveDefaultNote == null)
                            liveDefaultNote = "the IDE (pid " + rec.Pid + ") that has this solution open uses the default settings folder " + dir;
                        continue;
                    }
                    if (!hasProperties(dir)) continue;   // cannot happen for a live IDE's own folder; never read half of one
                    string note = "settings folder " + dir + " [chosen by: the IDE (pid " + rec.Pid
                        + ") that has this solution open, which runs with /ConfigDir]";
                    foreach (var n in named)
                    {
                        string other = n.Key == SourceSolutionFile ? n.Value : SolutionClarionVersion.ResolveDir(n.Value, baseDir);
                        if (other != null && !string.Equals(other, dir, StringComparison.OrdinalIgnoreCase))
                            note += "; IGNORING " + Describe(n.Key) + " '" + other + "', which disagrees with the live IDE";
                    }
                    return new ConfigDirChoice { Path = dir, Source = SourceIdeLive, Note = note };
                }
            }

            // ---- 2-4. Named by the user ----
            if (named.Count > 0)
            {
                var n = named[0];
                string dir = n.Key == SourceSolutionFile ? n.Value : SolutionClarionVersion.ResolveDir(n.Value, baseDir);
                if (dir == null)
                    return new ConfigDirChoice
                    {
                        Source = n.Key,
                        Error = Describe(n.Key) + " '" + n.Value + "' is not a usable folder path, so no Clarion settings were read."
                    };
                if (!hasProperties(dir))
                    return new ConfigDirChoice
                    {
                        Path = dir,
                        Source = n.Key,
                        Error = Describe(n.Key) + " names the settings folder " + dir + ", which has no ClarionProperties.xml"
                            + (Directory.Exists(dir) ? "" : " (the folder does not exist)")
                            + ". It must be the folder Clarion was started with as /ConfigDir=. No other settings folder was read in its place."
                    };
                string note = "settings folder " + dir + " [chosen by: " + Describe(n.Key) + "]";
                for (int i = 1; i < named.Count; i++)
                    note += "; " + Describe(named[i].Key) + " is ignored (" + Describe(n.Key) + " comes first)";
                if (liveDefaultNote != null) note += "; NOTE " + liveDefaultNote;
                return new ConfigDirChoice { Path = dir, Source = n.Key, Note = note };
            }

            // ---- 5. Default ----
            return new ConfigDirChoice
            {
                Source = SourceDefault,
                Note = "default settings folders (%APPDATA%\\SoftVelocity\\Clarion\\<version>) [no /ConfigDir known: if this Clarion "
                    + "runs with /ConfigDir=, name that folder with " + ArgName + ", " + EnvVar + ", or \"configDir\" in "
                    + SolutionClarionVersion.FileName + "]"
                    + (liveDefaultNote != null ? "; " + liveDefaultNote : "")
            };
        }

        private static string Describe(string source)
        {
            switch (source)
            {
                case SourceCommandLine: return ArgName;
                case SourceEnvironment: return EnvVar;
                case SourceSolutionFile: return "\"configDir\" in " + SolutionClarionVersion.FileName + " next to the solution";
                default: return source;
            }
        }

        private static bool HasPropertiesXml(string dir)
        {
            try { return File.Exists(System.IO.Path.Combine(dir, "ClarionProperties.xml")); }
            catch { return false; }
        }
    }
}
