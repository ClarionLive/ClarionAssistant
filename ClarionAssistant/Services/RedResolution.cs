using System;
using System.Collections.Generic;
using System.IO;
using ClarionCodeGraph.Parsing;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// 5a5581e6: what resolve_red_path / get_red_search_paths answer from the local .red, kept out of
    /// McpToolRegistry so a csc harness can run it.
    ///
    /// THREE THINGS IT GETS RIGHT THAT A FIXED LOOKUP DID NOT:
    ///   - the build CONFIGURATION: the IDE's ActiveConfiguration for the solution decides the section
    ///     ([Release32] then [Release], then [Common]). A fixed Debug-first order handed a Release build the Debug
    ///     copy. Unknown configuration = [Common] only; a hit in any other section is reported, never chosen.
    ///   - the PROJECT: relative entries are relative to the owning project's folder, and that folder may hold
    ///     its own version-named .red (RedFileService.ForProjectDirectory).
    ///   - the NAME: a bare file name only, so a "match" cannot climb out of the .red's folders.
    /// </summary>
    public static class RedResolution
    {
        /// <summary>Sections never searched for a build file: [Copy] says what to copy, not where to find.</summary>
        private static readonly string[] NotSearched = { "Copy" };

        /// <summary>
        /// The sections a build in <paramref name="configuration"/> searches, in order: "&lt;config&gt;32" (the C10+ name,
        /// Win32 being Clarion's only platform), the legacy "&lt;config&gt;", then Common. Unknown configuration:
        /// Common only.
        /// </summary>
        public static string[] SectionsFor(string configuration)
        {
            if (string.IsNullOrEmpty(configuration)) return new[] { "Common" };
            string c = configuration.Trim();
            if (c.Length == 0) return new[] { "Common" };
            return new[] { c + "32", c, "Common" };
        }

        /// <summary>
        /// Why <paramref name="fileName"/> is not a bare file name (rooted, a folder in it, ".", "..", a drive or an
        /// alternate data stream ':', invalid characters), or null when it is one.
        /// </summary>
        public static string LeafNameError(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.Trim().Length == 0) return "filename is required";
            if (fileName == "." || fileName == "..") return "'" + fileName + "' is not a file name";
            if (fileName.IndexOf('\\') >= 0 || fileName.IndexOf('/') >= 0)
                return "'" + fileName + "' contains a folder: pass the bare file name (e.g. 'UltimateSQL.tpl'); the .red decides the folder";
            if (fileName.IndexOf(':') >= 0)
                return "'" + fileName + "' contains ':' (a drive or an alternate data stream): pass the bare file name";
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "'" + fileName + "' contains characters that cannot be in a file name";
            return null;
        }

        /// <summary>The answer for one file: where the active sections put it, and where any OTHER section would.</summary>
        public sealed class Answer
        {
            public string Path;
            public string Section;
            public List<RedFileService.RedHit> AlsoFoundIn = new List<RedFileService.RedHit>();
        }

        /// <summary>
        /// Resolve <paramref name="fileName"/> (a bare name - check <see cref="LeafNameError"/> first) through
        /// <paramref name="activeSections"/> in order. Every other section of the .red (but [Copy]) is searched too,
        /// and a hit there at a DIFFERENT path goes to AlsoFoundIn - shown, never chosen.
        /// </summary>
        public static Answer Resolve(RedFileService red, string fileName, string baseDir, string[] activeSections)
        {
            var answer = new Answer();
            if (red == null) return answer;
            var active = new List<string>(activeSections ?? new[] { "Common" });
            var hits = red.FindInSections(fileName, baseDir, active);
            if (hits.Count > 0) { answer.Path = hits[0].Path; answer.Section = hits[0].Section; }

            var others = new List<string>();
            foreach (string s in RedFileService.BuildSectionOrder)
                if (!Contains(active, s) && !Contains(others, s)) others.Add(s);
            foreach (string s in red.Sections.Keys)
                if (!Contains(active, s) && !Contains(others, s) && !Contains(NotSearched, s)) others.Add(s);
            foreach (var h in red.FindInSections(fileName, baseDir, others))
                if (!string.Equals(h.Path, answer.Path, StringComparison.OrdinalIgnoreCase)) answer.AlsoFoundIn.Add(h);
            return answer;
        }

        /// <summary>
        /// The search directories for <paramref name="extensions"/> across <paramref name="sections"/>, in order,
        /// duplicates dropped - exactly the entries <see cref="Resolve"/> walks for those sections.
        /// </summary>
        public static List<string> SearchPaths(RedFileService red, IEnumerable<string> extensions, IEnumerable<string> sections)
        {
            var result = new List<string>();
            if (red == null || sections == null) return result;
            foreach (string s in sections)
                foreach (string p in red.GetSearchPathsForAny(extensions, s))
                    if (!result.Contains(p)) result.Add(p);
            return result;
        }

        /// <summary>
        /// The folder a .red's relative entries hang off for <paramref name="projectName"/> in
        /// <paramref name="solutionPath"/>: that project's (.cwproj) folder. With no project named: the only project's
        /// folder when there is one, else the solution's folder with a <paramref name="note"/> saying relative
        /// entries are per project. <paramref name="error"/> is set (and null returned) for a project name the
        /// solution does not have.
        /// </summary>
        public static string ProjectDirectory(string solutionPath, string projectName, out string note, out string error)
        {
            note = null;
            error = null;
            if (string.IsNullOrEmpty(solutionPath)) return null;
            string slnDir;
            try { slnDir = Path.GetDirectoryName(solutionPath); } catch { return null; }

            List<ClarionCodeGraph.Parsing.Models.SolutionProject> projects = null;
            try { if (File.Exists(solutionPath)) projects = new SolutionParser().Parse(solutionPath); } catch { }
            if (projects == null) projects = new List<ClarionCodeGraph.Parsing.Models.SolutionProject>();
            var names = new List<string>();
            foreach (var p in projects) names.Add(p.Name);

            if (!string.IsNullOrEmpty(projectName))
            {
                foreach (var p in projects)
                    if (string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileNameWithoutExtension(p.CwprojPath), projectName, StringComparison.OrdinalIgnoreCase))
                        return Path.GetDirectoryName(p.CwprojPath);
                error = "no project '" + projectName + "' in " + Path.GetFileName(solutionPath)
                      + (names.Count > 0 ? "; its projects: " + string.Join(", ", names.ToArray()) : "");
                return null;
            }

            if (projects.Count == 1) return Path.GetDirectoryName(projects[0].CwprojPath);
            if (projects.Count > 1)
                note = "relative .red entries are relative to each PROJECT's folder; these are anchored at the solution folder. "
                     + "Pass project_name for the project you mean (" + string.Join(", ", names.ToArray()) + ").";
            return slnDir;
        }

        private static bool Contains(IEnumerable<string> list, string s)
        {
            foreach (string x in list) if (string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
