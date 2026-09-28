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
    /// The prepend happens ONLY in the LSP-facing buffer (requests carry <see cref="LineOffsetFor"/>);
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

        /// <summary>Lines <see cref="WrapBuffer"/> prepends to THIS buffer: 1 for the MEMBER header, 0 when
        /// the buffer already opens with MEMBER/PROGRAM and is passed through untouched. Add to a Monaco
        /// line to get the LSP line; subtract from an LSP line to get back to Monaco. Per buffer, because
        /// a constant 1 put every position one line LOW for a pass-through buffer.</summary>
        public int LineOffsetFor(string buffer)
        {
            return OpensWithModuleHeader(buffer) ? 0 : 1;
        }

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

                string candidate = ResolveModulePath(appName, module, RedFileService.Active);
                if (candidate == null) return null;

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

        /// <summary>
        /// The generated module's full path on disk, or null when it can't be found. First the .app's own
        /// directory, then the redirection file. Split out of <see cref="TryCapture"/> (which needs the live
        /// embeditor) so the lookup can be exercised without the IDE — see tests\EmbedLspContext.RedResolve.Test.cs.
        /// </summary>
        internal static string ResolveModulePath(string appName, string module, RedFileService red)
        {
            if (string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(module)) return null;
            string dir = Path.GetDirectoryName(appName);
            if (string.IsNullOrEmpty(dir)) return null;
            string fileName = Path.GetFileName(module.Trim());
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;

            // The generated module is NOT necessarily next to the .app. A redirection entry
            // (e.g. "*.clw = Z:\ClwAux\Caj11clw") sends generated sources to another tree
            // entirely, and then this probe always misses and every embed falls back to the
            // synthetic LSP name - diagnostics and navigation run against a file that does not
            // exist, and RevertShadow has nothing to restore. Live symptom: the log line
            // "generated module not on disk" followed by lspRevertShadow(ctx=False).
            // Ask the .red, anchored at the .app directory, exactly as the MCP file tools do.
            //
            // Search the build sections too, not just [Common] (ResolveFrom's default): a .red that
            // redirects generated sources under [Debug32]/[Release32] only was still missed. Same
            // order ClarionAppDataReader uses to find the PROGRAM module (RedFileService.BuildSectionOrder).
            //
            // And ask the .red that governs THIS .app: RedFileService.Active is the solution's, and an .app
            // whose own project folder carries its own .red is built through that one instead.
            //
            // No File.Exists re-probes below: ResolveFrom only returns a path it has just found on disk,
            // and the .app-dir candidate already failed above. This runs on the UI thread, and every probe
            // of an unreachable UNC path can stall it.
            try
            {
                var governing = RedFileService.ForProjectDirectory(dir, red);
                string viaRed = governing?.ResolveForBuild(fileName, dir);
                if (!string.IsNullOrEmpty(viaRed)) return viaRed;
            }
            catch (Exception rex)
            {
                System.Diagnostics.Debug.WriteLine("[EmbedLspContext] redirection lookup failed: " + rex.Message);
            }

            System.Diagnostics.Debug.WriteLine(
                "[EmbedLspContext] generated module not on disk: '" + candidate + "' — keeping synthetic LSP name.");
            return null;
        }

        /// <summary>The LSP-facing copy of a Monaco buffer: the MEMBER header + the buffer. The embed
        /// buffer is a procedure slice that normally opens with blank lines before its own MEMBER — but a
        /// buffer whose FIRST line is MEMBER/PROGRAM is passed through untouched, and
        /// <see cref="LineOffsetFor"/> then reports 0 for it.</summary>
        public string WrapBuffer(string buffer)
        {
            string b = buffer ?? "";
            // 16d140e9: the page now syncs its buffer once per content version, so every request for that
            // version hands us the SAME string instance. Re-wrapping it built a fresh multi-megabyte copy
            // per completion/hover/folding request (3.2 MB -> 6.4 MB UTF-16 each on a big generated module,
            // in a 32-bit IDE). Reuse the last result while the input is the same instance.
            var last = _lastWrap;
            if (last != null && ReferenceEquals(last.Input, b)) return last.Output;
            string wrapped = OpensWithModuleHeader(b) ? b : HeaderLine + "\r\n" + b;
            _lastWrap = new WrapPair(b, wrapped);
            return wrapped;
        }

        // One immutable pair, swapped atomically (requests run on pool threads). Holds at most the current
        // buffer and its wrapped form — both already alive while that version is the one being edited.
        private sealed class WrapPair
        {
            public readonly string Input, Output;
            public WrapPair(string input, string output) { Input = input; Output = output; }
        }
        private volatile WrapPair _lastWrap;

        /// <summary>True when the buffer's first line is a MEMBER/PROGRAM statement — the one test that
        /// decides both whether <see cref="WrapBuffer"/> prepends and what <see cref="LineOffsetFor"/>
        /// reports, so the two can never disagree.</summary>
        private static bool OpensWithModuleHeader(string buffer)
        {
            string b = buffer ?? "";
            string firstLine = b;
            int nl = b.IndexOf('\n');
            if (nl >= 0) firstLine = b.Substring(0, nl);
            string t = firstLine.TrimStart();
            return t.StartsWith("MEMBER", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("PROGRAM", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Un-shadow the on-disk module in the LSP. While the embeditor tab is open, every request pushes
        /// the (wrapped) embed buffer to the server under <see cref="RealPath"/>, overriding the on-disk
        /// file in the server's view. The transport exposes no didClose, so on tab teardown we push the
        /// REAL on-disk content back. Safe no-op when the LSP isn't running or the file vanished.
        /// </summary>
        /// <remarks>
        /// FIRE-AND-FORGET, deliberately (ticket e1162adf). This used to run inline on the UI thread during
        /// embed teardown, and MEASURED at 57.7s on the first save of a Clarion session — the entire
        /// "saving takes over a minute" report. Everything else in that teardown totalled under 100ms, and
        /// the save itself (299 slots) took 664ms.
        ///
        /// The cost is not the work; it is sync-over-async. SharedLspBridge.SharedEnsureBufferSynced ends in
        ///     c.NotifyBufferChangedAsync(...).GetAwaiter().GetResult()
        /// and blocking a WinForms UI thread on a task whose continuation wants that same thread is the
        /// classic stalemate. The ~58s is it eventually breaking, not the server being slow.
        ///
        /// Moving it off-thread is safe because NOTHING depends on the result: this only restores the
        /// server's view of a file to its on-disk truth, and no caller inspects an outcome. It is ordering-
        /// safe too — the shadow it reverts is keyed by path, and a later re-open pushes its own buffer.
        ///
        /// The proper fix is to stop blocking on async inside SharedLspBridge, which would also help the
        /// diagnostics path that calls EnsureBufferSynced the same way. That is a wider change than this
        /// one and is left to the ticket rather than done the night before a demo.
        /// </remarks>
        public void RevertShadow()
        {
            _lastWrap = null;         // 16d140e9: the embed is closing — release the cached wrapped buffer
            string path = RealPath;   // capture — the context may be torn down under us
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (!SharedLspBridge.IsRunning) return;
            }
            catch { return; }

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Encoding-aware: this pushes on-disk .clw text straight back into the LSP buffer, so
                    // the no-encoding overload here reintroduced exactly the U+FFFD diagnostics #168
                    // removed — every embeditor tab teardown re-poisoned the server's view of the file.
                    SharedLspBridge.EnsureBufferSynced(path, EncodingHelper.ReadAllText(path, out _));
                    // K2 (1c685f2e): drop what is cached for this URI now. Anything published for the embed's text (or
                    // for this disk text) has line numbers that are wrong for the NEXT embeditor on the module.
                    SharedLspBridge.ClearDiagnostics(path);
                    System.Diagnostics.Debug.WriteLine("[EmbedLspContext] reverted LSP shadow for '" + path + "'.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[EmbedLspContext] RevertShadow: " + ex.Message);
                }
            });
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
