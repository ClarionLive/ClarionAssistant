using System;
using System.IO;
using System.Reflection;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Real-module LSP context for the CA Embeditor (GitHub #56 — embed global scope via a synthetic
    /// MEMBER header + the module's REAL path).
    ///
    /// The embed buffer is a procedure slice with no module header, and it used to be addressed to the
    /// LSP by a synthetic bare name (e.g. "UpdateCustomer.clw", no directory). The Clarion LSP resolves
    /// a member module's GLOBAL scope (program globals, file/field labels, ABC classes) by following its
    /// MEMBER('App.clw') to the parent program file on disk — roughly path.resolve(dirOfCurrentFile, arg)
    /// + fs.existsSync. A directory-less, header-less buffer gives that resolution nothing to work from,
    /// so program globals never resolved inside the embed.
    ///
    /// This context fixes both halves, WITHOUT touching the Monaco buffer itself:
    ///   1. Address the buffer by the generated module's REAL full path — PweeEditorDetails.AppName's
    ///      directory + PweeEditorDetails.Module — the generated .clw already on disk and in the project.
    ///      The buffer then lives in the real project directory (redirection/libsrc/project match).
    ///   2. Prepend the module's own MEMBER(...) line — read VERBATIM from the on-disk module so the
    ///      exact argument form matches — to every LSP-bound copy of the buffer. MEMBER→parent resolution
    ///      then lands on the real PROGRAM .clw and pulls in global scope.
    ///
    /// The prepend happens ONLY in the LSP-facing buffer (requests carry <see cref="LineOffset"/>);
    /// the Monaco model, editable ranges, caret mirror, and save/write-back all keep their existing
    /// 1:1 line mapping with the native document.
    ///
    /// Capture timing matters: PweeEditorDetails exists only while the NATIVE embeditor is open, and the
    /// snapshot launcher cancels it before the Monaco tab is constructed — so the launcher captures this
    /// context at mirror time and passes it into the view.
    ///
    /// While a context is active, the pushed buffer SHADOWS the on-disk module in the LSP under the same
    /// path. There is no didClose in the transport, so <see cref="RevertShadow"/> pushes the on-disk
    /// content back on tab teardown to restore the server to the truth.
    /// </summary>
    public sealed class EmbedLspContext
    {
        /// <summary>Full path of the generated module .clw on disk (dir of the .app + module name).
        /// The embed buffer is addressed to the LSP by this path.</summary>
        public string RealPath { get; private set; }

        /// <summary>The module's own MEMBER(...) line, read verbatim from disk (or synthesized from the
        /// .app name when the read fails). Prepended to every LSP-bound buffer.</summary>
        public string HeaderLine { get; private set; }

        /// <summary>Lines prepended to the LSP-facing buffer (the MEMBER header). Add to a Monaco line
        /// to get the LSP line; subtract from an LSP line to get back to Monaco.</summary>
        public int LineOffset { get { return 1; } }

        private EmbedLspContext(string realPath, string headerLine)
        {
            RealPath = realPath;
            HeaderLine = headerLine;
        }

        /// <summary>
        /// Build the context from the currently-open native embeditor's PweeEditorDetails, or null when
        /// it can't be built (no embed open, details lack AppName/Module, or the generated module isn't
        /// on disk — e.g. never generated). Null simply means "keep the synthetic-name behavior".
        /// MUST be called while the native embeditor is still open (the snapshot path cancels it later).
        /// </summary>
        public static EmbedLspContext TryCapture(AppTreeService appTree = null)
        {
            try
            {
                var pwee = (appTree ?? new AppTreeService()).GetOpenPweeDetails();
                if (pwee == null) return null;
                string appName = GetProp(pwee, "AppName") as string;
                string module = GetProp(pwee, "Module") as string;
                if (string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(module)) return null;

                string dir = Path.GetDirectoryName(appName);
                if (string.IsNullOrEmpty(dir)) return null;
                string candidate = Path.Combine(dir, Path.GetFileName(module.Trim()));
                if (!File.Exists(candidate))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[EmbedLspContext] generated module not on disk: '" + candidate + "' — keeping synthetic LSP name.");
                    return null;
                }

                string header = ReadMemberLine(candidate)
                    ?? "  MEMBER('" + Path.GetFileNameWithoutExtension(appName) + ".clw')";
                System.Diagnostics.Debug.WriteLine(
                    "[EmbedLspContext] captured: realPath='" + candidate + "', header='" + header.Trim() + "'");
                return new EmbedLspContext(candidate, header);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[EmbedLspContext] TryCapture: " + ex.Message);
                return null;
            }
        }

        /// <summary>The LSP-facing copy of a Monaco buffer: the MEMBER header + the buffer. The embed
        /// buffer is a procedure slice, so it never carries its own MEMBER/PROGRAM — but guard anyway
        /// (a buffer that already opens with one is passed through untouched, offset stays harmless-safe
        /// only because such a buffer never occurs in embed mode).</summary>
        public string WrapBuffer(string buffer)
        {
            string b = buffer ?? "";
            string firstLine = b;
            int nl = b.IndexOf('\n');
            if (nl >= 0) firstLine = b.Substring(0, nl);
            string t = firstLine.TrimStart();
            if (t.StartsWith("MEMBER", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("PROGRAM", StringComparison.OrdinalIgnoreCase))
                return b;
            return HeaderLine + "\r\n" + b;
        }

        /// <summary>
        /// Un-shadow the on-disk module in the LSP. While the embeditor tab is open, every request pushes
        /// the (wrapped) embed buffer to the server under <see cref="RealPath"/>, overriding the on-disk
        /// file in the server's view. The transport exposes no didClose, so on tab teardown we push the
        /// REAL on-disk content back. Safe no-op when the LSP isn't running or the file vanished.
        /// </summary>
        public void RevertShadow()
        {
            try
            {
                if (string.IsNullOrEmpty(RealPath) || !File.Exists(RealPath)) return;
                if (!SharedLspBridge.IsRunning) return;
                SharedLspBridge.EnsureBufferSynced(RealPath, File.ReadAllText(RealPath));
                System.Diagnostics.Debug.WriteLine("[EmbedLspContext] reverted LSP shadow for '" + RealPath + "'.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[EmbedLspContext] RevertShadow: " + ex.Message);
            }
        }

        /// <summary>The module's own MEMBER(...) line, verbatim from disk (skipping leading blanks and
        /// '!' comments), or null when none precedes the first real statement.</summary>
        private static string ReadMemberLine(string path)
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var t = line.Trim();
                    if (t.StartsWith("MEMBER", StringComparison.OrdinalIgnoreCase)) return line;
                    if (t.Length > 0 && !t.StartsWith("!")) break; // first real statement — MEMBER must precede it
                }
            }
            catch { }
            return null;
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var p = obj.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return (p != null && p.GetIndexParameters().Length == 0) ? p.GetValue(obj, null) : null;
            }
            catch { return null; }
        }
    }
}
