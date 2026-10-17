using System;
using System.Collections.Generic;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Is a source file one of an open app's GENERATED modules? (GH #263 question 2.) The CA Editor uses
    /// the answer to show a control hover card's attribute names as plain text, not clickable chips, in
    /// such a file: the template generator rewrites it, so a whole-file edit there is lost on the next
    /// generate. The CA Embeditor never asks, because its embed slots already guard the generated lines.
    ///
    /// The comparison is by file NAME only, ignoring case. The app knows its modules by name
    /// ("PRM002081.clw"), and the generated files may sit wherever the .red sends *.clw. A hand-coded
    /// file that happens to share a generated module's name therefore also gets plain names, which is the
    /// safe direction: the developer loses a shortcut, not their edit.
    ///
    /// Zero IDE coupling: AppTreeService.IsGeneratedModuleOfOpenApp collects the names from the live app
    /// model and calls this.
    /// </summary>
    public static class GeneratedModuleMatch
    {
        /// <param name="filePath">The file the editor shows (full path or bare name).</param>
        /// <param name="moduleNames">Module names of the open apps (App.Procedures[i].Module, App.Modules), with or
        /// without the .clw extension or a folder.</param>
        /// <param name="appFiles">The open .app files: each one's PROGRAM module is &lt;app name&gt;.clw.</param>
        public static bool IsGeneratedModule(string filePath, IEnumerable<string> moduleNames, IEnumerable<string> appFiles)
        {
            string target = FileNameOf(filePath);
            if (target == null) return false;
            if (moduleNames != null)
                foreach (var m in moduleNames)
                    if (Same(target, ClwNameOf(m))) return true;
            if (appFiles != null)
                foreach (var a in appFiles)
                {
                    string baseName = SafeBaseName(a);
                    if (baseName != null && Same(target, baseName + ".clw")) return true;
                }
            return false;
        }

        private static bool Same(string a, string b)
        {
            return b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string FileNameOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string n = Path.GetFileName(path.Trim());
                return string.IsNullOrEmpty(n) ? null : n;
            }
            catch (ArgumentException) { return null; }
        }

        // "PRM002081" / "PRM002081.clw" / "C:\App\prm002081.CLW" -> its file name, with .clw added when bare.
        private static string ClwNameOf(string module)
        {
            string n = FileNameOf(module);
            if (n == null) return null;
            return Path.HasExtension(n) ? n : n + ".clw";
        }

        private static string SafeBaseName(string appFile)
        {
            if (string.IsNullOrWhiteSpace(appFile)) return null;
            try
            {
                string b = Path.GetFileNameWithoutExtension(appFile.Trim());
                return string.IsNullOrEmpty(b) ? null : b;
            }
            catch (ArgumentException) { return null; }
        }
    }
}
