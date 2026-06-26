using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClarionCodeGraph.Parsing.Models;

namespace ClarionCodeGraph.Parsing
{
    /// <summary>
    /// Parses a Clarion .sln file for project entries and inter-project dependencies.
    /// </summary>
    public class SolutionParser
    {
        // Project("{TypeGuid}") = "Name", "RelativePath.cwproj", "{ProjectGuid}"
        private static readonly Regex ProjectLineRegex = new Regex(
            @"Project\(""\{[^}]+\}""\)\s*=\s*""([^""]+)""\s*,\s*""([^""]+)""\s*,\s*""\{([^}]+)\}""",
            RegexOptions.Compiled);

        // {GUID} = {GUID}  (inside ProjectDependencies section)
        private static readonly Regex DependencyRegex = new Regex(
            @"\{([A-Fa-f0-9\-]+)\}\s*=\s*\{[A-Fa-f0-9\-]+\}",
            RegexOptions.Compiled);

        public List<SolutionProject> Parse(string slnPath)
        {
            if (!File.Exists(slnPath))
                throw new FileNotFoundException("Solution file not found: " + slnPath);

            var projects = new List<SolutionProject>();
            var lines = File.ReadAllLines(slnPath);
            string slnDir = Path.GetDirectoryName(slnPath);

            SolutionProject currentProject = null;
            bool inDependencies = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Match project line
                var projectMatch = ProjectLineRegex.Match(line);
                if (projectMatch.Success)
                {
                    string name = projectMatch.Groups[1].Value;
                    string relativePath = projectMatch.Groups[2].Value;
                    string guid = projectMatch.Groups[3].Value;

                    // Only include Clarion projects (.cwproj)
                    if (relativePath.EndsWith(".cwproj", StringComparison.OrdinalIgnoreCase))
                    {
                        currentProject = new SolutionProject
                        {
                            Name = name,
                            Guid = guid.ToUpperInvariant(),
                            CwprojPath = Path.GetFullPath(Path.Combine(slnDir, relativePath)),
                            SlnPath = slnPath
                        };
                        projects.Add(currentProject);
                    }
                    else
                    {
                        currentProject = null;
                    }
                    continue;
                }

                // Track dependency sections
                if (line.Contains("ProjectSection(ProjectDependencies)"))
                {
                    inDependencies = true;
                    continue;
                }
                if (line.Contains("EndProjectSection"))
                {
                    inDependencies = false;
                    continue;
                }
                if (line.Trim() == "EndProject")
                {
                    currentProject = null;
                    inDependencies = false;
                    continue;
                }

                // Capture dependency GUIDs
                if (inDependencies && currentProject != null)
                {
                    var depMatch = DependencyRegex.Match(line);
                    if (depMatch.Success)
                    {
                        currentProject.DependencyGuids.Add(depMatch.Groups[1].Value.ToUpperInvariant());
                    }
                }
            }

            return projects;
        }
    }
}
