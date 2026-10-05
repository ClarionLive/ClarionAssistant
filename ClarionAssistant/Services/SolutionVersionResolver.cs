using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace ClarionAssistant.Services
{
    /// <summary>Which source decided a solution's Clarion version (0ce0b5e2). Never resolve one silently.</summary>
    public enum SolutionVersionSource
    {
        None,
        /// <summary>--clarion-version on the standalone's command line.</summary>
        CommandLine,
        /// <summary>clarion-assistant.json next to the .sln.</summary>
        SolutionFile,
        /// <summary>The IDE's live Build &gt; Set Clarion Version, published with this solution.</summary>
        IdeLive,
        /// <summary>The IDE's saved choice for this solution: ActiveVersion in its preferences\&lt;sln&gt;.&lt;hash&gt;.xml.</summary>
        IdePreferences,
        /// <summary>The Clarion tree the standalone is installed under.</summary>
        HostInstall,
        /// <summary>That Clarion's own "current" version.</summary>
        HostCurrent
    }

    /// <summary>The answer: a version (or none), the source that decided, and a sentence saying so.</summary>
    public sealed class SolutionVersionChoice
    {
        public ClarionVersionConfig Config { get; internal set; }
        public SolutionVersionSource Source { get; internal set; }
        public string Note { get; internal set; }
    }

    /// <summary>Everything <see cref="SolutionVersionResolver.Resolve"/> decides from. Gathered by the host, so the
    /// order itself is pure and the harness drives it with fakes.</summary>
    public sealed class SolutionVersionInputs
    {
        public string SolutionPath { get; set; }
        /// <summary>--clarion-version, or null.</summary>
        public string CommandLineVersion { get; set; }
        /// <summary>clarion-assistant.json's clarionVersion, or null.</summary>
        public string PinnedVersion { get; set; }
        /// <summary>Why a clarion-assistant.json that exists gave no name, or null.</summary>
        public string PinNote { get; set; }
        /// <summary>The record the launching IDE published, or null (no IDE, or none published).</summary>
        public IdeSolutionRecord.Details IdeRecord { get; set; }
        /// <summary>The host Clarion's preferences folder, used when the IDE record names no config dir. May be null.</summary>
        public string PreferencesDir { get; set; }
        /// <summary>The host install's ClarionVersionService.Detect(), or null.</summary>
        public ClarionVersionInfo HostInfo { get; set; }
        /// <summary>The Clarion tree the server is installed under, or null.</summary>
        public string HostRoot { get; set; }
        /// <summary>A NAMED version lookup (ClarionVersionService.FindNamedVersion in production).</summary>
        public Func<string, ClarionVersionConfig> FindNamed { get; set; }
    }

    /// <summary>
    /// "Which Clarion is this solution?" - the one order for the standalone server and its language server
    /// (0ce0b5e2). v61POSitive.sln builds under Clarion 10 but was browsed in Clarion 12: the standalone took the
    /// Clarion it is installed under, so INCLUDE('PS_ProImage.clw') resolved against C12 and was "not found",
    /// though the IDE itself had the solution on "Clarion 10 Active And Updated". The IDE stores that per solution
    /// and restores it on open; the standalone never asked. The order:
    ///
    ///   1. an explicit pin: --clarion-version, then clarion-assistant.json (an unmatched name is an error);
    ///   2. the IDE's choice for THIS solution: live from the record the IDE publishes, else its saved
    ///      preferences file. "Current" there means the IDE's own running version, which is step 3;
    ///   3. the Clarion tree this server is installed under, then that Clarion's current version.
    ///
    /// Every answer says which source decided, and a step that was consulted and could not decide says why.
    /// </summary>
    public static class SolutionVersionResolver
    {
        public const string PinFileName = "clarion-assistant.json";

        public static SolutionVersionChoice Resolve(SolutionVersionInputs input)
        {
            var info = input.HostInfo;
            Func<string, ClarionVersionConfig> find = input.FindNamed ?? (n => null);
            ClarionVersionConfig cfg;

            // ---- 1a. --clarion-version. Explicit beats everything; a miss STOPS (the user stated an intent). ----
            if (!string.IsNullOrEmpty(input.CommandLineVersion))
            {
                cfg = find(input.CommandLineVersion);
                if (cfg != null)
                    return Choice(cfg, SolutionVersionSource.CommandLine,
                        "Clarion version " + cfg.Name + " [chosen by: --clarion-version]");
                return Choice(null, SolutionVersionSource.None, "no Clarion version: --clarion-version '"
                    + input.CommandLineVersion + "' matches nothing installed. " + NotFoundHint(info));
            }

            // ---- 1b. The solution's own committed clarion-assistant.json ----
            if (!string.IsNullOrEmpty(input.PinnedVersion))
            {
                cfg = find(input.PinnedVersion);
                if (cfg != null)
                    return Choice(cfg, SolutionVersionSource.SolutionFile,
                        "Clarion version " + cfg.Name + " [chosen by: " + PinFileName + " next to the solution]");
                return Choice(null, SolutionVersionSource.None, "no Clarion version: " + PinFileName + " asks for '"
                    + input.PinnedVersion + "', which matches nothing installed. " + NotFoundHint(info));
            }
            string carried = input.PinNote != null ? "ignoring " + input.PinNote + " " : "";

            // ---- 2. The IDE's choice for this solution ----
            string sln = input.SolutionPath;
            var rec = input.IdeRecord;
            bool recordIsThisSolution = rec != null && !string.IsNullOrEmpty(sln) && SamePath(rec.Solution, sln);
            bool ideSaysCurrent = false;

            // 2a. Live: what the IDE has this solution on right now, published with it.
            if (recordIsThisSolution && !string.IsNullOrEmpty(rec.VersionChoice))
            {
                if (ClarionVersionSelector.IsCurrentChoice(rec.VersionChoice))
                {
                    ideSaysCurrent = true;
                    carried += "the IDE (pid " + rec.Pid + ") has this solution on Build > Set Clarion Version "
                        + "'Current', its own running Clarion; ";
                }
                else
                {
                    cfg = find(rec.VersionChoice);
                    if (cfg != null)
                        return Choice(cfg, SolutionVersionSource.IdeLive, carried + "Clarion version " + cfg.Name
                            + " [chosen by: the IDE's Build > Set Clarion Version for this solution, live from the IDE (pid "
                            + rec.Pid + ")]");
                    carried += "the IDE (pid " + rec.Pid + ") has this solution on '" + rec.VersionChoice
                        + "', which matches nothing installed; ";
                }
            }

            // 2b. Saved: the IDE's preferences file for this solution. Skipped when the live IDE said "Current":
            // live beats the file, which may predate a switch.
            if (!ideSaysCurrent && !string.IsNullOrEmpty(sln))
            {
                string prefsDir = rec != null && !string.IsNullOrEmpty(rec.ConfigDir)
                    ? Path.Combine(rec.ConfigDir, "preferences") : input.PreferencesDir;
                string file, prefsNote;
                string saved = IdeSolutionPreferences.ReadActiveVersion(prefsDir, sln, out file, out prefsNote);
                if (saved != null)
                {
                    string where = "preferences\\" + Path.GetFileName(file)
                        + (prefsNote != null ? " (" + prefsNote + ")" : "");
                    if (ClarionVersionSelector.IsCurrentChoice(saved))
                    {
                        carried += "the IDE's saved choice for this solution (" + where + ") is 'Current'; ";
                    }
                    else
                    {
                        cfg = find(saved);
                        if (cfg != null)
                            return Choice(cfg, SolutionVersionSource.IdePreferences, carried + "Clarion version "
                                + cfg.Name + " [chosen by: the IDE's saved Build > Set Clarion Version for this solution, "
                                + where + "]");
                        carried += "the IDE's saved choice for this solution (" + where + ") is '" + saved
                            + "', which matches nothing installed; ";
                    }
                }
                else if (prefsNote != null)
                {
                    carried += prefsNote + "; ";
                }
            }

            // ---- 3. This machine: the Clarion tree this server is installed under, then its current version ----
            if (info == null)
            {
                string root = input.HostRoot;
                return Choice(null, SolutionVersionSource.None, carried + (root == null
                    ? "no Clarion version: this server is not installed in a Clarion folder "
                      + "(<Clarion>\\accessory\\addins\\ClarionAssistant), so nothing says which Clarion's "
                      + "settings to use, and it will not guess. Pass --clarion-version <name>, or put "
                      + PinFileName + " next to the solution."
                    : !File.Exists(Path.Combine(root, "bin", "Clarion.exe"))
                    // Installed, but the tree has no Clarion.exe, whose version names the settings
                    // folder; DetectForInstall then detects nothing rather than guess one.
                    ? "no Clarion version: this server is installed under " + root + ", but "
                      + Path.Combine(root, "bin", "Clarion.exe") + " is missing, so nothing says "
                      + "which Clarion's settings to use, and it will not guess. Pass --clarion-version "
                      + "<name>, or put " + PinFileName + " next to the solution."
                    : "no Clarion version: the ClarionProperties.xml for the Clarion at " + root
                      + " could not be found or parsed, so redirection, library paths and the "
                      + "build root are unavailable."));
            }

            // PREFER THE CLARION THIS SERVER IS INSTALLED UNDER, over the one the machine calls "current": the
            // installer places a copy in every selected Clarion's addin folder, and its own location cannot drift.
            cfg = null;
            if (input.HostRoot != null && info.Versions != null)
                cfg = info.ResolveByRoot(input.HostRoot);
            if (cfg != null)
                return Choice(cfg, SolutionVersionSource.HostInstall, carried + "Clarion version " + cfg.Name
                    + " [chosen by: the Clarion tree this server is installed under]" + Ambiguity(info));

            cfg = info.GetCurrentConfig();
            return cfg == null
                ? Choice(null, SolutionVersionSource.None, carried + "no Clarion version: none of the tiers resolved one. "
                    + InstalledList(info))
                : Choice(cfg, SolutionVersionSource.HostCurrent, carried + "Clarion version " + cfg.Name
                    + " [chosen by: the machine's current version, no project setting]" + Ambiguity(info));
        }

        private static SolutionVersionChoice Choice(ClarionVersionConfig cfg, SolutionVersionSource source, string note)
        {
            return new SolutionVersionChoice { Config = cfg, Source = source, Note = note };
        }

        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { a = Path.GetFullPath(a); b = Path.GetFullPath(b); } catch { }
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>What to tell the user when a named version was not found.</summary>
        private static string NotFoundHint(ClarionVersionInfo info)
        {
            return info != null ? InstalledList(info)
                : "It is not listed in any Clarion settings folder on this machine (%APPDATA%\\SoftVelocity\\Clarion). "
                  + "The name must match what Clarion records under Build > Set Clarion Version, exactly.";
        }

        /// <summary>
        /// Version names as configured, real compiler names first, newest-looking first, and CAPPED: a real machine
        /// carries 27 (Clarion 10/11 point releases, Clarion.NET entries, per-product profiles), and printing all of
        /// them buried the sentence that mattered. Descending is ordinal, not natural-numeric.
        /// </summary>
        private static string Names(ClarionVersionInfo info, int max)
        {
            var names = new List<string>();
            if (info != null && info.Versions != null)
                foreach (var v in info.Versions)
                    if (v != null && !string.IsNullOrEmpty(v.Name)) names.Add(v.Name);

            names.Sort(delegate(string a, string b)
            {
                bool ca = a.StartsWith("Clarion ", StringComparison.OrdinalIgnoreCase);
                bool cb = b.StartsWith("Clarion ", StringComparison.OrdinalIgnoreCase);
                if (ca != cb) return ca ? -1 : 1;
                return string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
            });

            if (names.Count <= max) return string.Join(", ", names.ToArray());
            return string.Join(", ", names.GetRange(0, max).ToArray())
                + ", and " + (names.Count - max) + " more";
        }

        private static int NameCount(ClarionVersionInfo info)
        {
            int n = 0;
            if (info != null && info.Versions != null)
                foreach (var v in info.Versions) if (v != null && !string.IsNullOrEmpty(v.Name)) n++;
            return n;
        }

        /// <summary>The installed version names, for an error the reader can act on immediately.</summary>
        private static string InstalledList(ClarionVersionInfo info)
        {
            if (NameCount(info) == 0) return "No Clarion versions are configured on this machine.";

            return "Installed (" + NameCount(info) + "): " + Names(info, 8)
                + ". Name one EXACTLY in " + PinFileName
                + " next to the solution - the name must match what Clarion records, and no"
                + " prefix matching is done, because \"Clarion 11.0\" and \"Clarion 11.1\" are"
                + " different compilers.";
        }

        /// <summary>
        /// Appended when a MACHINE-shaped tier decided while more than one Clarion is installed: the choice sets the
        /// redirection file, library paths and build root, and a guess that is right today breaks when the server is
        /// launched from elsewhere. Naming the alternatives and the file that would settle it makes it a decision.
        /// </summary>
        private static string Ambiguity(ClarionVersionInfo info)
        {
            int n = NameCount(info);
            if (n < 2) return "";

            return " - GUESSED, from " + n + " configured (" + Names(info, 5)
                + "). This is a property of THIS MACHINE, not of the project, and will change if the"
                + " server is launched from elsewhere. Set the solution's version in the IDE (Build > Set Clarion"
                + " Version) or commit " + PinFileName + " next to the solution to settle it.";
        }

        // ------------------------------------------------------------------------------------------------
        // Advisory: the projects' own version resources
        // ------------------------------------------------------------------------------------------------

        private static readonly Regex SlnProjectLine = new Regex(
            @"^Project\(""\{[^}]*\}""\)\s*=\s*""([^""]*)""\s*,\s*""([^""]+\.cwproj)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VersionResourceValue = new Regex(
            @"VALUE\s+""ClarionVersion""\s*,\s*""(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A WARNING when the solution's projects say, in their template-written &lt;project&gt;.Version resource
        /// (VALUE "ClarionVersion", "10000"), that they were generated by a different Clarion major than
        /// <paramref name="chosen"/>'s CWVersion; null when they agree, say nothing, or cannot be read. Advisory
        /// only: it never selects a version, it says the choice may be the wrong one.
        /// </summary>
        public static string ProjectVersionWarning(string solutionPath, ClarionVersionConfig chosen)
        {
            try
            {
                if (chosen == null || !chosen.CWVersion.HasValue || chosen.CWVersion.Value < 1000) return null;
                if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath)) return null;
                int want = chosen.CWVersion.Value / 1000;
                string dir = Path.GetDirectoryName(solutionPath);

                var differ = new List<string>();
                int checkedCount = 0;
                foreach (string line in File.ReadAllLines(solutionPath))
                {
                    var m = SlnProjectLine.Match(line);
                    if (!m.Success) continue;
                    if (++checkedCount > 500) break;
                    string verFile = Path.Combine(dir, Path.ChangeExtension(m.Groups[2].Value, ".Version"));
                    if (!File.Exists(verFile)) continue;
                    var vm = VersionResourceValue.Match(File.ReadAllText(verFile));
                    int n;
                    if (!vm.Success || !int.TryParse(vm.Groups[1].Value, out n) || n < 1000) continue;
                    if (n / 1000 != want) differ.Add(m.Groups[1].Value + " (Clarion " + (n / 1000) + ")");
                }
                if (differ.Count == 0) return null;
                string list = differ.Count <= 5 ? string.Join(", ", differ.ToArray())
                    : string.Join(", ", differ.GetRange(0, 5).ToArray()) + ", and " + (differ.Count - 5) + " more";
                return "WARNING: " + differ.Count + " project(s) of this solution were generated by a different Clarion"
                    + " than " + chosen.Name + " (Clarion " + want + "), per their .Version resource: " + list
                    + ". Advisory only - the version was not changed. If it is wrong, set the solution's version in"
                    + " the IDE (Build > Set Clarion Version) or commit " + PinFileName + " next to the solution.";
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// The Clarion IDE's saved per-solution choice: ActiveVersion in ConfigDir\preferences\&lt;sln file name&gt;.&lt;hash&gt;.xml,
    /// which Versions.SetActiveVersion writes and the IDE restores into Build &gt; Set Clarion Version when the solution
    /// opens (0ce0b5e2).
    ///
    /// THE FILE IS CHOSEN BY CONTENT, NOT BY REPRODUCING ITS NAME. The hash is the IDE's string.GetHashCode() of the
    /// lower-cased .sln path, which differs between 32 and 64 bit and between runtimes, so it is only a tiebreak, and
    /// only in a 32-bit process like the IDE. Candidates are every &lt;name&gt;.&lt;hex&gt;.xml for the solution's file
    /// name. One, or several that agree: that choice. Several that disagree: the one whose recorded paths (open
    /// files) lie under the solution's folder; then the hash; else none, said so.
    /// </summary>
    public static class IdeSolutionPreferences
    {
        /// <summary>The IDE's preferences folder for a Clarion whose ClarionProperties.xml is <paramref name="propertiesXmlPath"/>.</summary>
        public static string PreferencesDirFor(string propertiesXmlPath)
        {
            try
            {
                if (string.IsNullOrEmpty(propertiesXmlPath)) return null;
                return Path.Combine(Path.GetDirectoryName(propertiesXmlPath), "preferences");
            }
            catch { return null; }
        }

        /// <summary>
        /// The ActiveVersion the IDE saved for <paramref name="solutionPath"/> ("Current" passes through as is), or
        /// null when there is none. <paramref name="file"/> is the file it came from. <paramref name="note"/> says
        /// how the file was chosen when that was not obvious, or why there is no answer when there should have been.
        /// </summary>
        public static string ReadActiveVersion(string prefsDir, string solutionPath, out string file, out string note)
        {
            file = null;
            note = null;
            try
            {
                if (string.IsNullOrEmpty(prefsDir) || string.IsNullOrEmpty(solutionPath)) return null;
                if (!Directory.Exists(prefsDir)) return null;

                string slnName = Path.GetFileName(solutionPath);
                var pattern = new Regex("^" + Regex.Escape(slnName) + @"\.([0-9a-f]{1,8})\.xml$", RegexOptions.IgnoreCase);
                var candidates = new List<Candidate>();
                foreach (string f in Directory.GetFiles(prefsDir, slnName + ".*.xml"))
                {
                    var m = pattern.Match(Path.GetFileName(f));
                    if (!m.Success) continue;
                    var c = ReadCandidate(f, m.Groups[1].Value);
                    if (c != null) candidates.Add(c);
                }
                if (candidates.Count == 0) return null;

                if (candidates.Count == 1)
                    return Take(candidates[0], out file);

                // Several files for one solution NAME (other folders' solutions of the same name). When they all say
                // the same thing, which one is ours does not matter.
                var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in candidates) distinct.Add(c.ActiveVersion ?? "");
                if (distinct.Count == 1)
                {
                    note = candidates.Count + " preference files for " + slnName + ", all saying the same";
                    return Take(candidates[0], out file);
                }

                string hash = HashSuffix(solutionPath);
                Candidate byHash = hash == null ? null
                    : candidates.Find(c => string.Equals(c.Suffix.TrimStart('0'), hash.TrimStart('0'), StringComparison.OrdinalIgnoreCase));

                // By content: recorded paths under this solution's folder.
                string slnDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath)).TrimEnd('\\') + "\\";
                var byContent = candidates.FindAll(c => c.MentionsFolder(slnDir));
                if (byContent.Count == 1)
                {
                    var pick = byContent[0];
                    note = "chosen among " + candidates.Count + " preference files for " + slnName
                        + " by the paths it records";
                    if (byHash != null && !ReferenceEquals(byHash, pick))
                        note += "; NOTE the name hash points at " + Path.GetFileName(byHash.Path) + " instead";
                    return Take(pick, out file);
                }

                if (byHash != null)
                {
                    note = "chosen among " + candidates.Count + " preference files for " + slnName
                        + " by the IDE's name hash (their contents do not say which solution they belong to)";
                    return Take(byHash, out file);
                }

                note = "the IDE has " + candidates.Count + " preference files for " + slnName + " ("
                    + string.Join(", ", candidates.ConvertAll(c => Path.GetFileName(c.Path) + "='"
                        + (c.ActiveVersion ?? "") + "'").ToArray())
                    + ") that disagree, and nothing says which is this solution's, so none was used";
                return null;
            }
            catch (Exception ex)
            {
                note = "could not read the IDE's preferences for this solution: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// The IDE's file-name hash for a solution - string.GetHashCode() of the lower-cased path, as the 32-bit .NET
        /// Framework IDE computes it - or null in a process where that would not reproduce it (64-bit).
        /// </summary>
        internal static string HashSuffix(string solutionPath)
        {
            if (IntPtr.Size != 4) return null;
            try { return Path.GetFullPath(solutionPath).ToLowerInvariant().GetHashCode().ToString("x8"); }
            catch { return null; }
        }

        private static string Take(Candidate c, out string file)
        {
            file = c.Path;
            return c.ActiveVersion;
        }

        private sealed class Candidate
        {
            public string Path;
            public string Suffix;
            public string ActiveVersion;
            public List<string> Paths = new List<string>();

            public bool MentionsFolder(string folderWithSlash)
            {
                foreach (var p in Paths)
                    if (p.StartsWith(folderWithSlash, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        private static Candidate ReadCandidate(string file, string suffix)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(file);   // honours the BOM the IDE writes
                var c = new Candidate { Path = file, Suffix = suffix };
                var av = doc.SelectSingleNode("/Properties/ActiveVersion") as XmlElement;
                if (av != null)
                {
                    string v = av.GetAttribute("value");
                    c.ActiveVersion = string.IsNullOrEmpty(v) ? null : v.Trim();
                }
                foreach (XmlNode n in doc.SelectNodes("//@value"))
                {
                    string v = n.Value;
                    if (!string.IsNullOrEmpty(v) && v.Length > 3 && v[1] == ':' && (v[2] == '\\' || v[2] == '/'))
                    {
                        // Normalized the way the solution's own path is (GetFullPath also expands 8.3 short names).
                        string full;
                        try { full = System.IO.Path.GetFullPath(v); } catch { full = v.Replace('/', '\\'); }
                        c.Paths.Add(full);
                    }
                }
                return c;
            }
            catch { return null; }
        }
    }
}
