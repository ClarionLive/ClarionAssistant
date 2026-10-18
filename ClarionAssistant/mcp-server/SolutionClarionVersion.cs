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
    /// FORMAT — two keys, and deliberately not a settings dumping ground:
    ///
    ///     {
    ///       "clarionVersion": "Clarion12",
    ///       "configDir": "C:\\Clarion12.Settings"
    ///     }
    ///
    /// "configDir" (e1df6c2e, GH #244) is only for a Clarion started with /ConfigDir=: the folder that holds its
    /// ClarionProperties.xml. A server with no IDE has no command line of Clarion's to read it from.
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
        /// What the file says: the version name and the settings folder (e1df6c2e), each null when absent, and a
        /// note whenever there is something a human should be told - including the failure cases.
        /// </summary>
        internal sealed class Pin
        {
            /// <summary>"clarionVersion", trimmed, or null.</summary>
            public string Version;
            /// <summary>
            /// "configDir" as a FULL path, or null: a Clarion settings folder relocated with /ConfigDir=, the folder
            /// holding that Clarion's ClarionProperties.xml. Relative to the json's own folder; %VARS% expanded, so
            /// a committed file can say "%APPDATA%\..." and still travel.
            /// </summary>
            public string ConfigDir;
            /// <summary>The json file, when it exists.</summary>
            public string FilePath;
            /// <summary>
            /// "allowOlderServer" (e1df6c2e pipeline run 2), or null when absent (= true): whether the plugin launcher
            /// may start an OLDER Clarion Assistant server installed under the clarionVersion's Clarion (it warns).
            /// </summary>
            public bool? AllowOlderServer;
            public string Note;
        }

        /// <summary>
        /// The version name the solution asks for, or null. <paramref name="note"/> is set
        /// whenever there is something a human should be told - including the failure cases,
        /// which are the ones worth surfacing.
        /// </summary>
        public static string Read(string solutionPath, out string note)
        {
            var pin = ReadPin(solutionPath);
            note = pin.Note;
            return pin.Version;
        }

        /// <summary>Both keys of the file next to <paramref name="solutionPath"/>. Never null, never throws.</summary>
        internal static Pin ReadPin(string solutionPath)
        {
            var pin = new Pin();
            if (string.IsNullOrEmpty(solutionPath)) return pin;

            string path;
            try
            {
                string dir = Path.GetDirectoryName(solutionPath);
                if (string.IsNullOrEmpty(dir)) return pin;
                path = Path.Combine(dir, FileName);
                if (!File.Exists(path)) return pin;   // the common case: no file, no note, no noise
            }
            catch (Exception ex)
            {
                pin.Note = "could not look for " + FileName + " next to the solution: " + ex.Message;
                return pin;
            }
            pin.FilePath = path;

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                pin.Note = path + " exists but could not be read: " + ex.Message;
                return pin;
            }

            Dictionary<string, object> map;
            try
            {
                map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            }
            catch (Exception ex)
            {
                pin.Note = path + " is not valid JSON: " + ex.Message;
                return pin;
            }
            if (map == null)
            {
                pin.Note = path + " is not a JSON object.";
                return pin;
            }

            var notes = new List<string>();
            object raw;
            bool hasVersion = map.TryGetValue("clarionVersion", out raw) && raw != null;
            if (hasVersion)
            {
                string name = (raw.ToString() ?? "").Trim();
                if (name.Length == 0) notes.Add(path + " has an empty \"clarionVersion\".");
                else pin.Version = name;
            }

            object rawAllow;
            if (map.TryGetValue("allowOlderServer", out rawAllow) && rawAllow != null)
            {
                if (rawAllow is bool) pin.AllowOlderServer = (bool)rawAllow;
                else
                {
                    // Pipeline run 3: a non-boolean ("false" in quotes, 0, ...) is read as FALSE, the safe side - not
                    // ignored back to the default true.
                    pin.AllowOlderServer = false;
                    notes.Add(path + " has an \"allowOlderServer\" that is not true or false; it is treated as false.");
                }
            }

            object rawDir;
            bool hasDir = map.TryGetValue("configDir", out rawDir) && rawDir != null;
            if (hasDir)
            {
                string d = (rawDir.ToString() ?? "").Trim();
                if (d.Length == 0) notes.Add(path + " has an empty \"configDir\".");
                else
                {
                    string full = ResolveDir(d, Path.GetDirectoryName(path));
                    if (full == null) notes.Add(path + " has a \"configDir\" that is not a usable path: '" + d + "'.");
                    else pin.ConfigDir = full;
                }
            }

            // A file that exists but selects nothing is worth a word: the author plainly meant to configure
            // something, and a typo in a key would otherwise be indistinguishable from having no file at all.
            if (!hasVersion && !hasDir)
                notes.Add(path + " has no \"clarionVersion\" (or \"configDir\") key, so it does not select a Clarion.");

            if (notes.Count > 0) pin.Note = string.Join(" ", notes.ToArray());
            return pin;
        }

        /// <summary>
        /// A folder named by a user (json, command line, environment) as a full, trimmed path: %VARS% expanded and a
        /// relative path anchored at <paramref name="baseDir"/>. Null when it cannot be made a path.
        /// </summary>
        internal static string ResolveDir(string value, string baseDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                string v = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                if (v.IndexOf('%') >= 0) return null;   // an unexpanded %VAR% is not a folder anyone meant
                if (!Path.IsPathRooted(v) && !string.IsNullOrEmpty(baseDir)) v = Path.Combine(baseDir, v);
                return Path.GetFullPath(v).TrimEnd('\\', '/');
            }
            catch { return null; }
        }
    }
}
