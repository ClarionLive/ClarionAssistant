using System;
using System.Collections.Generic;
using System.IO;

namespace ClarionCodeGraph.Parsing
{
    /// <summary>
    /// Locates actual .clw/.inc source files on disk.
    /// For v1, uses the .\source\ subfolder convention.
    /// </summary>
    public class SourceResolver
    {
        /// <summary>
        /// Given a project directory and a list of source file names from the .cwproj,
        /// resolves each to its full path by searching known locations.
        /// </summary>
        /// <param name="searchPaths">
        /// Optional Clarion redirection (.red) search directories — e.g. <c>.\Generated</c>,
        /// <c>.\obj</c>, or config-specific output dirs. The .cwproj lists generated source
        /// as bare filenames and the .red maps them to these directories, so without them the
        /// resolver misses generated .clw/.inc files that don't sit in .\source\ or the project
        /// root. Relative entries are resolved against <paramref name="projectDir"/>; absolute
        /// entries are used as-is. (GitHub #36)
        /// </param>
        public List<ResolvedFile> Resolve(string projectDir, List<string> fileNames, List<string> searchPaths = null)
        {
            var results = new List<ResolvedFile>();

            foreach (string fileName in fileNames)
            {
                string resolved = FindFile(projectDir, fileName, searchPaths);
                results.Add(new ResolvedFile
                {
                    FileName = fileName,
                    FullPath = resolved,
                    Found = resolved != null
                });
            }

            return results;
        }

        private string FindFile(string projectDir, string fileName, List<string> searchPaths)
        {
            // Search order:
            // 1. .\source\ subfolder (primary convention)
            // 2. Project root directory (fallback)
            // 3. Clarion redirection (.red) search paths (e.g. .\Generated) — GitHub #36

            string sourcePath = Path.Combine(projectDir, "source", fileName);
            if (File.Exists(sourcePath))
                return sourcePath;

            // Also try .\Source\ (case variation on case-insensitive Windows)
            string sourcePathAlt = Path.Combine(projectDir, "Source", fileName);
            if (File.Exists(sourcePathAlt))
                return sourcePathAlt;

            string rootPath = Path.Combine(projectDir, fileName);
            if (File.Exists(rootPath))
                return rootPath;

            // Follow the project's redirection search paths. Generated source is listed in
            // the .cwproj as a bare filename and physically lives under a .red-mapped dir
            // (commonly .\Generated). Probe each search dir with both the bare name and the
            // original (possibly sub-pathed) name.
            if (searchPaths != null)
            {
                string baseName = Path.GetFileName(fileName);
                foreach (string sp in searchPaths)
                {
                    if (string.IsNullOrEmpty(sp))
                        continue;

                    string dir = Path.IsPathRooted(sp) ? sp : Path.Combine(projectDir, sp);

                    string candidate = Path.Combine(dir, baseName);
                    if (File.Exists(candidate))
                        return candidate;

                    if (!string.Equals(baseName, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        string candidateFull = Path.Combine(dir, fileName);
                        if (File.Exists(candidateFull))
                            return candidateFull;
                    }
                }
            }

            return null;
        }
    }

    public class ResolvedFile
    {
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public bool Found { get; set; }
    }
}
