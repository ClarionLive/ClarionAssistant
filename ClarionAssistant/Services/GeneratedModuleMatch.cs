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

        /// <summary>The page message that updates generatedSource (GH #263 Q2 follow-up).</summary>
        public static string UpdateMessage(bool generated)
        {
            return "{\"type\":\"generatedSource\",\"value\":" + (generated ? "true" : "false") + "}";
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

    /// <summary>
    /// What one CA Editor page was last told about generatedSource. setSource computes the flag once, but a tab
    /// opened, or restored at startup, BEFORE its app loads would keep the wrong answer, and with it the chips
    /// whose edits the next generate wipes. So the host re-checks when apps open or close
    /// (GeneratedSourceWatch) and posts an update only when the answer changed.
    /// </summary>
    public sealed class GeneratedSourceFlag
    {
        private readonly object _gate = new object();
        private bool? _sent;

        /// <summary>setSource carried <paramref name="generated"/>: the page now holds it.</summary>
        public void MarkSent(bool generated) { lock (_gate) { _sent = generated; } }

        /// <summary>The page was replaced (or never loaded): nothing is known to be there.</summary>
        public void Reset() { lock (_gate) { _sent = null; } }

        /// <summary>The message to post for the current answer, or null when the page already has it or has
        /// had no setSource yet (that setSource will carry the answer itself).</summary>
        public string Update(bool generated)
        {
            lock (_gate)
            {
                if (_sent == null || _sent.Value == generated) return null;
                _sent = generated;
                return GeneratedModuleMatch.UpdateMessage(generated);
            }
        }
    }
}
