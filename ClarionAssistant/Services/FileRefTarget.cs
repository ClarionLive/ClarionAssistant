using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The file a file-reference argument names - INCLUDE('x.inc'), MODULE('x.clw'), MEMBER('x.clw'),
    /// LINK('x.clw') - and where the build finds it. Used by the definition fallback when the language server
    /// returns nothing for a click on such a name.
    ///
    /// The lookup goes through the .red, the way the compiler and resolve_red_path find a file, and never through
    /// the CodeGraph symbols: CodeGraph's 'include' rows record where a file is INCLUDEd, not where it is, so a
    /// name lookup there jumped into whichever file happened to include it first (ABFILE.INC for CWSyncHM.inc).
    /// </summary>
    public static class FileRefTarget
    {
        // The FIRST string argument only: INCLUDE('file','section') names the file in its first argument. Not after
        // a '.': ST.Include('x') is a method call, not the statement.
        private static readonly Regex FileRefArg = new Regex(
            @"(?<![.\w:])(?:INCLUDE|MODULE|MEMBER|LINK)\s*\(\s*'((?:[^']|'')*)'", RegexOptions.IgnoreCase);

        /// <summary>
        /// The file name when <paramref name="character"/> (0-based) is on the first string argument of
        /// INCLUDE / MODULE / MEMBER / LINK in <paramref name="lineText"/>, quotes included; otherwise null.
        /// An empty argument (MODULE('')) and anything after a '!' comment give null.
        /// </summary>
        public static string FileNameAt(string lineText, int character)
        {
            if (string.IsNullOrEmpty(lineText) || character < 0) return null;
            int comment = CommentStart(lineText);
            foreach (Match m in FileRefArg.Matches(lineText))
            {
                if (comment >= 0 && m.Index >= comment) break;
                Group arg = m.Groups[1];
                if (character < arg.Index - 1 || character > arg.Index + arg.Length) continue;
                string name = arg.Value.Replace("''", "'").Trim();
                return name.Length == 0 ? null : name;
            }
            return null;
        }

        /// <summary>
        /// Where the build finds <paramref name="fileName"/>, referenced from <paramref name="contextFile"/>: the .red
        /// that governs the context file's project (its own version-named .red, else <paramref name="red"/>),
        /// relative entries anchored at that project's folder, <paramref name="sections"/> searched in order.
        /// Null when it is not found: the reference is authoritative, so the caller must not guess by name instead.
        /// </summary>
        public static string Resolve(string fileName, string contextFile, string solutionDir, RedFileService red, string[] sections)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            try
            {
                if (Path.IsPathRooted(fileName)) return File.Exists(fileName) ? fileName : null;
                if (red == null) return null;
                string projectDir = ProjectDirFor(contextFile, solutionDir);
                if (string.IsNullOrEmpty(projectDir)) return null;
                RedFileService governing = RedFileService.ForProjectDirectory(projectDir, red) ?? red;
                return governing.ResolveFrom(fileName, projectDir, sections);
            }
            catch { return null; }
        }

        /// <summary>
        /// The project folder a reference in <paramref name="contextFile"/> resolves from: the nearest folder holding
        /// a .cwproj, from the file's own folder up to <paramref name="solutionDir"/>. A file outside the solution
        /// (a libsrc header) or with no .cwproj on that path resolves from the solution folder. A walk, not the .sln's
        /// project list: it runs on a click, and a .cwproj folder is what the build anchors relative entries at.
        /// </summary>
        internal static string ProjectDirFor(string contextFile, string solutionDir)
        {
            // The embeditor's synthetic bare "Proc.clw" has no folder: GetFullPath would invent the working directory.
            string fileDir = null;
            try { if (!string.IsNullOrEmpty(contextFile) && Path.IsPathRooted(contextFile)) fileDir = Path.GetDirectoryName(Path.GetFullPath(contextFile)); } catch { }
            if (string.IsNullOrEmpty(solutionDir)) return fileDir;
            string root = Path.GetFullPath(solutionDir).TrimEnd('\\', '/');
            // A drive root keeps its backslash: "D:" alone means the current folder on D:.
            string rootDir = root.EndsWith(":") ? root + "\\" : root;
            if (fileDir == null || !IsUnder(fileDir, root)) return rootDir;
            for (string dir = fileDir; dir != null && IsUnder(dir, root); dir = Path.GetDirectoryName(dir))
            {
                try { if (Directory.GetFiles(dir, "*.cwproj").Length > 0) return dir; } catch { }
                if (string.Equals(dir.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)) break;
            }
            return rootDir;
        }

        private static bool IsUnder(string dir, string root)
        {
            string d = dir.TrimEnd('\\', '/');
            return string.Equals(d, root, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Index of the '!' that starts a comment (outside a string literal), or -1.</summary>
        private static int CommentStart(string lineText)
        {
            bool inString = false;
            for (int i = 0; i < lineText.Length; i++)
            {
                char ch = lineText[i];
                if (ch == '\'') inString = !inString;
                else if (ch == '!' && !inString) return i;
            }
            return -1;
        }
    }
}
