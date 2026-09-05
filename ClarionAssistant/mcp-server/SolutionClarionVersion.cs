using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ClarionAssistant.McpServer
{
    /// <summary>
    /// Reads <c>clarion-assistant.json</c> from the solution folder — the committed, per-project
    /// answer to "which Clarion is this?" (ticket d051fbd1, checklist item 5).
    ///
    /// WHY A COMMITTED FILE, AND WHY IT HAS TO WIN. A hand-coder with no editor has no
    /// <c>clarion.activeVersion</c> setting (that is Mark's VS Code extension, which answers this
    /// for VS Code users) and no IDE version dropdown. Before this, the standalone server picked a
    /// Clarion by two fallbacks, neither of which is a property of the PROJECT:
    ///
    ///   - the Clarion tree this .exe happens to be installed under, and
    ///   - failing that, whichever version the machine last called "current".
    ///
    /// On a machine with one Clarion those agree and nobody notices. On a machine with four or
    /// more — which is the case this was specified against — they routinely disagree, and the
    /// answer decides the redirection file, the library search paths and the root used for builds.
    /// A wrong pick there does not throw; it quietly resolves the wrong sources. The project is the
    /// only party that actually knows which Clarion it targets, so a file that travels with the
    /// project and survives a fresh clone outranks both machine-shaped guesses.
    ///
    /// FORMAT — deliberately one key, and deliberately not a settings dumping ground:
    ///
    ///     {
    ///       "clarionVersion": "Clarion12"
    ///     }
    ///
    /// The value is the version NAME as Clarion itself records it in ClarionProperties.xml, which
    /// is what the version dropdown shows and what <see cref="ClarionAssistant.Services.ClarionVersionConfig.Name"/>
    /// carries. A name that matches nothing installed is reported as such rather than ignored:
    /// silently falling through would reproduce the exact failure this file exists to end, except
    /// now with a config file present making it look handled.
    ///
    /// NEVER THROWS. A malformed or unreadable file degrades to "no answer here, try the next
    /// tier", carrying the reason so the caller can say it out loud.
    /// </summary>
    internal static class SolutionClarionVersion
    {
        /// <summary>The file name looked for, alongside the .sln.</summary>
        public const string FileName = "clarion-assistant.json";

        /// <summary>
        /// The version name the solution asks for, or null. <paramref name="note"/> is set
        /// whenever there is something a human should be told — including the failure cases,
        /// which are the ones worth surfacing.
        /// </summary>
        public static string Read(string solutionPath, out string note)
        {
            note = null;
            if (string.IsNullOrEmpty(solutionPath)) return null;

            string path;
            try
            {
                string dir = Path.GetDirectoryName(solutionPath);
                if (string.IsNullOrEmpty(dir)) return null;
                path = Path.Combine(dir, FileName);
                if (!File.Exists(path)) return null;   // the common case: no file, no note, no noise
            }
            catch (Exception ex)
            {
                note = "could not look for " + FileName + " next to the solution: " + ex.Message;
                return null;
            }

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                note = path + " exists but could not be read: " + ex.Message;
                return null;
            }

            try
            {
                var map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                if (map == null)
                {
                    note = path + " is not a JSON object.";
                    return null;
                }

                object raw;
                if (!map.TryGetValue("clarionVersion", out raw) || raw == null)
                {
                    // A file that exists but says nothing about the version is worth a word: the
                    // author plainly meant to configure something, and a typo in the key would
                    // otherwise be indistinguishable from having no file at all.
                    note = path + " has no \"clarionVersion\" key, so it does not select a Clarion.";
                    return null;
                }

                string name = (raw.ToString() ?? "").Trim();
                if (name.Length == 0)
                {
                    note = path + " has an empty \"clarionVersion\".";
                    return null;
                }
                return name;
            }
            catch (Exception ex)
            {
                note = path + " is not valid JSON: " + ex.Message;
                return null;
            }
        }
    }
}
