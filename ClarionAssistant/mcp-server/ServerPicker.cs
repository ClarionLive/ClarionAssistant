using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ClarionAssistant.Services;

namespace ClarionAssistant.McpServer
{
    /// <summary>One installed clarion-mcp-server: the Clarion root it is installed under, and that Clarion's version.</summary>
    internal sealed class ServerCandidate
    {
        public string Root;
        public string ExePath;
        /// <summary>&lt;Root&gt;\bin\Clarion.exe's file version, or null when it cannot be read.</summary>
        public Version ClarionExeVersion;
        /// <summary>
        /// The SERVER's own file version (Clarion Assistant's, e.g. 6.0.0.1344), or null when unreadable. Deploys go per
        /// Clarion version, so one root can hold an older server than another (pipeline run 1, e1df6c2e).
        /// </summary>
        public Version ServerVersion;
        /// <summary>The path-policy capability it reported (--capabilities), once probed; 0 = none / no answer.</summary>
        public int PathPolicy;
        /// <summary>Where the candidate was found: CLARION_ROOTS, the installer's registry record, C:\ClarionXX, this server's own install.</summary>
        public string Origin;
    }

    /// <summary>A live IDE that has the solution open: its process id, its Set Clarion Version choice, its Clarion.exe.</summary>
    internal sealed class LiveIde
    {
        public int Pid;
        public string VersionChoice;
        public string ExePath;
    }

    internal sealed class ServerPickInputs
    {
        public string SolutionPath;
        /// <summary>--clarion-version, forwarded by the launcher, or null.</summary>
        public string CommandLineVersion;
        /// <summary>clarion-assistant.json's clarionVersion, or null.</summary>
        public string PinnedVersion;
        public List<ServerCandidate> Candidates = new List<ServerCandidate>();
        /// <summary>A version NAME to its entry (its RootPath is what is matched).</summary>
        public Func<string, ClarionVersionConfig> FindNamed;
        /// <summary>(project, Clarion major) from the projects' .Version resources.</summary>
        public List<KeyValuePair<string, int>> ProjectMajors = new List<KeyValuePair<string, int>>();
        public List<LiveIde> LiveIdes = new List<LiveIde>();
        /// <summary>
        /// The server answering --pick-server (the launcher's "bootstrap", the first one it found), or null. A pick is
        /// never OLDER than it unless the user named that Clarion (--clarion-version or clarion-assistant.json; see <see cref="ServerPicker.Pick"/>).
        /// </summary>
        public ServerCandidate Bootstrap;
        /// <summary>
        /// clarion-assistant.json's "allowOlderServer" (default true): whether its clarionVersion pin may start an OLDER
        /// server than the bootstrap (with a warning). False refuses it like an inferred choice. Ignored for other tiers.
        /// </summary>
        public bool AllowOlderPinnedServer = true;
        /// <summary>The path-policy a candidate reports; <see cref="ServerPicker.ProbePathPolicy"/> in production (test seam).</summary>
        public Func<ServerCandidate, int> CapabilityOf;
    }

    internal sealed class ServerPick
    {
        /// <summary>The server to run, or null when no server is installed anywhere it looked.</summary>
        public ServerCandidate Server;
        /// <summary>One of the Source* constants.</summary>
        public string Source;
        /// <summary>True when nothing about the project decided and the newest install was taken.</summary>
        public bool IsFallback;
        public string Note;
        /// <summary>The project's choice was an OLDER server than the bootstrap and was refused; <see cref="Server"/> is the bootstrap.</summary>
        public bool DowngradeRefused;
        /// <summary>Set when an older server was picked anyway because the user pinned it; also part of <see cref="Note"/>.</summary>
        public string Warning;
    }

    /// <summary>
    /// Which installed clarion-mcp-server the Claude Code plugin's launcher should start (e1df6c2e, GH #244 item 2).
    ///
    /// The launcher (marketplace\...\launcher\clarion-tools.cmd) used to start the NEWEST Clarion's server, whatever
    /// the project. The server's own version resolution then often still picked the right Clarion - a named version
    /// is looked up in every settings folder - but everything keyed on the INSTALL went to the newest one: the
    /// "Clarion tree this server is installed under" tier, and the bundled language server it runs. A Clarion 11
    /// solution was indexed through Clarion 12's install and reported success.
    ///
    /// The launcher now asks the server it found (clarion-mcp-server --pick-server) and runs the answer. The order,
    /// first that names an installed server:
    ///   1. --clarion-version (forwarded by the launcher), then clarion-assistant.json's clarionVersion;
    ///   2. the solution's own projects: the Clarion major their .Version resources say generated them, when they
    ///      agree (the newest install of that major when there are several - said so);
    ///   3. a live IDE that has this solution open: its named Build &gt; Set Clarion Version, else its own Clarion;
    ///   4. the newest install that has the server - a FALLBACK, and the note says so in capitals.
    /// A tier that applied but named a Clarion without the server installed is carried into the note.
    /// </summary>
    internal static class ServerPicker
    {
        public const string SourceCommandLine = "command-line";
        public const string SourceSolutionFile = "solution-file";
        public const string SourceProjectVersion = "project-version";
        public const string SourceIdeLive = "ide-live";
        public const string SourceNewest = "newest-fallback";
        public const string SourceNone = "none";

        /// <summary>The server's place in a Clarion tree, as the installer and deploy.ps1 put it.</summary>
        public const string RelativeExe = @"accessory\addins\ClarionAssistant\clarion-mcp-server.exe";

        /// <summary>Extra Clarion roots to consider, semicolon-separated (a Clarion not in C:\ClarionXX nor recorded by the installer).</summary>
        public const string RootsEnvVar = "CLARION_ROOTS";

        /// <summary>
        /// The pick, then the DOWNGRADE GUARD (pipeline run 1, e1df6c2e): deploys go per Clarion version, so the server
        /// under the project's Clarion can be an older Clarion Assistant than the one answering. Running it would silently
        /// drop every fix since (path policy #266, #205, ...). So a pick older than the bootstrap - or whose version cannot
        /// be read - is REFUSED and the bootstrap serves instead, said so; except when the user NAMED that Clarion
        /// (--clarion-version, or clarionVersion in clarion-assistant.json unless "allowOlderServer": false), where it
        /// runs with a WARNING naming both versions.
        ///
        /// THE SECURITY FLOOR (pipeline run 3) comes first and no file can lower it: clarion-assistant.json is committed
        /// in SOMEONE ELSE'S repo as easily as in yours, so a pin there must not be able to start a server that predates
        /// the #266 path policy. The floor is a CAPABILITY the server reports (--capabilities: path-policy=N), not a
        /// version number - per-version deploys can share one version string. A server that does not answer, reports less
        /// than <see cref="PathPolicyFloor"/>, or whose version cannot be read, is below the floor: never picked from the
        /// json or an inferred tier. Only --clarion-version (a command line the user wrote) may still run it, warned.
        /// </summary>
        public static ServerPick Pick(ServerPickInputs input)
        {
            var pick = PickByProject(input);
            var boot = input.Bootstrap;
            if (pick.Server == null || boot == null) return pick;
            if (SolutionFollower.SamePath(pick.Server.ExePath, boot.ExePath)) return pick;

            var v = pick.Server.ServerVersion;
            int cap = (input.CapabilityOf ?? ProbePathPolicy)(pick.Server);
            pick.Server.PathPolicy = cap;
            bool belowFloor = cap < PathPolicyFloor || v == null;
            bool older = belowFloor || (boot.ServerVersion != null && v < boot.ServerVersion);
            if (!older) return pick;

            string what = "the server under " + pick.Server.Root + " is Clarion Assistant "
                + (v != null ? v.ToString() : "(version unreadable)")
                + (belowFloor ? ", BELOW the security floor (it reports " + (cap > 0 ? "path-policy=" + cap : "no path policy")
                    + "; path-policy=" + PathPolicyFloor + " is required, GH #266)" : "")
                + ", OLDER than " + boot.ExePath + " (Clarion Assistant " + (boot.ServerVersion != null ? boot.ServerVersion.ToString() : "?")
                + ") - it lacks every fix made since";
            // A NAMED version is the developer's deliberate choice - on the command line, or committed in the solution's
            // clarion-assistant.json (pipeline run 2): refusing it would bring back the install-keyed mismatch #244 is
            // about. Only an INFERRED choice (.Version resources, a live IDE, the newest) is refused for being older.
            // clarion-assistant.json's "allowOlderServer": false opts its pin out. BELOW THE FLOOR only the command
            // line counts (run 3).
            bool pinned = pick.Source == SourceCommandLine
                || (!belowFloor && pick.Source == SourceSolutionFile && input.AllowOlderPinnedServer);
            if (pinned)
            {
                string who = pick.Source == SourceCommandLine ? "--clarion-version names"
                    : SolutionClarionVersion.FileName + " (clarionVersion) pins";
                pick.Warning = "WARNING: " + what + ". Running it anyway because " + who + " that Clarion; "
                    + "update Clarion Assistant in " + pick.Server.Root + " to get those fixes";
                pick.Note = pick.Note + "; " + pick.Warning;
                return pick;
            }
            string howToUse;
            if (belowFloor)
                howToUse = " its own server. A server below the security floor is never started from "
                    + SolutionClarionVersion.FileName + " or an inferred choice; only --clarion-version on your own command line can insist";
            else if (pick.Source == SourceSolutionFile)
                howToUse = " its own server (" + SolutionClarionVersion.FileName + " pins that Clarion, but its \"allowOlderServer\" refuses an older server)";
            else
                howToUse = " its own server, or name the version (--clarion-version, or clarionVersion in "
                    + SolutionClarionVersion.FileName + ") to insist";
            return new ServerPick
            {
                Server = boot,
                Source = pick.Source,
                IsFallback = pick.IsFallback,
                DowngradeRefused = true,
                Note = pick.Note + "; REFUSED: " + what + ", so " + boot.ExePath + " serves instead (it still uses the"
                    + " Clarion version chosen for the project). Install or deploy Clarion Assistant for that Clarion to use"
                    + howToUse
            };
        }

        /// <summary>
        /// The path-policy capability this build has (GH #266; its pipeline fix round is generation 2). Printed by
        /// --capabilities. Raise it when a change to the path policy must not be bypassed by starting an older server.
        /// </summary>
        public const int PathPolicyCapability = 2;

        /// <summary>The lowest path-policy a server may report and still be picked from a file or an inferred choice.</summary>
        public const int PathPolicyFloor = 2;

        /// <summary>
        /// The path-policy capability <paramref name="c"/> reports: runs it with --capabilities (stdin closed, 10 s cap)
        /// and reads "path-policy=N". 0 when it does not answer - an older server rejects the argument - or cannot run.
        /// </summary>
        public static int ProbePathPolicy(ServerCandidate c)
        {
            if (c == null || string.IsNullOrEmpty(c.ExePath) || !File.Exists(c.ExePath)) return 0;
            // Only a real PE file is ever started. Measured: starting a non-PE ".exe" from a hidden console session hung
            // this process inside CreateProcess indefinitely (no timeout reaches it), and such a file is below the floor
            // anyway - it has no version either.
            if (!LooksLikePe(c.ExePath)) return 0;
            int result = 0;
            var worker = new System.Threading.Thread(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(c.ExePath, "--capabilities")
                    {
                        UseShellExecute = false, CreateNoWindow = true, ErrorDialog = false,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.StandardInput.Close();
                        var outTask = p.StandardOutput.ReadToEndAsync();
                        var errTask = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return; }
                        if (p.ExitCode != 0) return;
                        result = ParsePathPolicy(outTask.Result);
                    }
                }
                catch { }
            });
            worker.IsBackground = true;
            worker.Start();
            // The whole probe, start included, is bounded: an answer that does not come is "no capability".
            return worker.Join(15000) ? result : 0;
        }

        private static bool LooksLikePe(string path)
        {
            try
            {
                using (var f = File.OpenRead(path))
                    return f.Length > 64 && f.ReadByte() == 'M' && f.ReadByte() == 'Z';
            }
            catch { return false; }
        }

        /// <summary>"path-policy=N" from --capabilities output; 0 when absent or malformed.</summary>
        public static int ParsePathPolicy(string capabilities)
        {
            if (string.IsNullOrEmpty(capabilities)) return 0;
            foreach (string line in capabilities.Split('\n'))
            {
                string l = line.Trim();
                if (!l.StartsWith("path-policy=", StringComparison.OrdinalIgnoreCase)) continue;
                int n;
                return int.TryParse(l.Substring("path-policy=".Length), out n) && n > 0 ? n : 0;
            }
            return 0;
        }

        /// <summary>
        /// The MCP initialize instructions for a downgraded server (pipeline run 3), or null: the warning, and an explicit
        /// ask that the model pass it on - a developer reads neither stderr nor a field of get_solution_info.
        /// </summary>
        public static string DowngradeInstructions(string warning)
        {
            if (string.IsNullOrEmpty(warning)) return null;
            return "IMPORTANT - tell the developer this at the start of your next reply, in plain words: " + warning
                + " Until they update it, treat this server's tools as missing recent fixes.";
        }

        /// <summary>What --capabilities prints for this build.</summary>
        public static string CapabilitiesText()
        {
            return "path-policy=" + PathPolicyCapability + "\npick-server=2\n";
        }

        /// <summary>
        /// Pipeline run 3: the warning for the RUNNING server when it is an older Clarion Assistant than the one the
        /// launcher asked (<paramref name="bootstrapExe"/>, "pick"), or - started by CLARION_MCP_SERVER ("override") -
        /// than another installed server; null otherwise. It goes into the MCP initialize instructions as well as stderr
        /// and serverChosenBy, because only the model reliably reads what reaches it.
        /// </summary>
        public static string RunningDowngradeWarning(string launcherMode, string ownExe, string bootstrapExe,
                                                     Func<List<ServerCandidate>> installed)
        {
            try
            {
                if (string.IsNullOrEmpty(launcherMode)) return null;
                Version own = FileVersionOf(ownExe);
                ServerCandidate newer = null;
                if (string.Equals(launcherMode, "pick", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(bootstrapExe) || SolutionFollower.SamePath(bootstrapExe, ownExe)) return null;
                    var b = CandidateFor(bootstrapExe, "bootstrap");
                    if (b.ServerVersion != null && (own == null || b.ServerVersion > own)) newer = b;
                }
                else if (string.Equals(launcherMode, "override", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var c in (installed != null ? installed() : null) ?? new List<ServerCandidate>())
                        if (c.ServerVersion != null && (own == null || c.ServerVersion > own)
                            && (newer == null || c.ServerVersion > newer.ServerVersion)) newer = c;
                }
                if (newer == null) return null;
                return "WARNING: this Clarion Assistant server (" + ownExe + ", version " + (own != null ? own.ToString() : "unreadable")
                    + ") is OLDER than " + newer.ExePath + " (version " + newer.ServerVersion + ") and lacks every fix made"
                    + " since, which can include security fixes. It runs because "
                    + (string.Equals(launcherMode, "override", StringComparison.OrdinalIgnoreCase)
                        ? "CLARION_MCP_SERVER names it" : "the project's Clarion version was named explicitly")
                    + ". Update Clarion Assistant for that Clarion.";
            }
            catch { return null; }
        }

        private static ServerPick PickByProject(ServerPickInputs input)
        {
            var cands = input.Candidates ?? new List<ServerCandidate>();
            Func<string, ClarionVersionConfig> find = input.FindNamed ?? (n => null);
            var carried = new List<string>();

            if (cands.Count == 0)
                return new ServerPick { Source = SourceNone, Note = "no clarion-mcp-server is installed in any Clarion root looked at" };

            // ---- 1. A named version ----
            var named = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(input.CommandLineVersion))
                named.Add(new KeyValuePair<string, string>(SourceCommandLine, input.CommandLineVersion));
            if (!string.IsNullOrEmpty(input.PinnedVersion))
                named.Add(new KeyValuePair<string, string>(SourceSolutionFile, input.PinnedVersion));
            foreach (var n in named)
            {
                string who = n.Key == SourceCommandLine ? "--clarion-version" : SolutionClarionVersion.FileName;
                var cfg = find(n.Value);
                if (cfg == null || string.IsNullOrEmpty(cfg.RootPath))
                {
                    carried.Add(who + " names '" + n.Value + "', which matches no Clarion on this machine");
                    // An explicit name STOPS the server's own version resolution; it must stop here too, or the pick
                    // would quietly go on to a different Clarion the user never named.
                    break;
                }
                var at = AtRoot(cands, cfg.RootPath);
                if (at != null)
                    return Picked(at, n.Key, "server under " + at.Root + " [chosen by: " + who + " '" + cfg.Name + "']", carried);
                carried.Add(who + " names " + cfg.Name + " at " + cfg.RootPath + ", but Clarion Assistant's server is not installed there");
                break;
            }

            // ---- 2. The projects' own .Version resources ----
            var majors = input.ProjectMajors ?? new List<KeyValuePair<string, int>>();
            if (named.Count == 0 && majors.Count > 0)
            {
                var distinct = new HashSet<int>();
                foreach (var p in majors) distinct.Add(p.Value);
                if (distinct.Count > 1)
                {
                    var parts = new List<string>();
                    foreach (int m in distinct) parts.Add("Clarion " + m);
                    carried.Add("the solution's projects disagree about their Clarion (" + string.Join(", ", parts.ToArray())
                        + ", per their .Version resources)");
                }
                else
                {
                    int major = majors[0].Value;
                    var ofMajor = cands.FindAll(c => c.ClarionExeVersion != null && c.ClarionExeVersion.Major == major);
                    if (ofMajor.Count > 0)
                    {
                        var best = Newest(ofMajor);
                        string why = "server under " + best.Root + " [chosen by: the solution's projects, generated by Clarion "
                            + major + " per " + majors.Count + " .Version resource(s)";
                        if (ofMajor.Count > 1)
                            why += "; " + ofMajor.Count + " Clarion " + major + " installs have the server, took the newest ("
                                + VersionText(best) + "). Commit " + SolutionClarionVersion.FileName + " to name one exactly";
                        return Picked(best, SourceProjectVersion, why + "]", carried);
                    }
                    carried.Add("the solution's projects were generated by Clarion " + major
                        + ", but no Clarion " + major + " install has Clarion Assistant's server");
                }
            }

            // ---- 3. A live IDE on this solution ----
            if (named.Count == 0)
            {
                foreach (var ide in input.LiveIdes ?? new List<LiveIde>())
                {
                    if (ide == null) continue;
                    string root = null, how = null;
                    if (!string.IsNullOrEmpty(ide.VersionChoice) && !ClarionVersionSelector.IsCurrentChoice(ide.VersionChoice))
                    {
                        var cfg = find(ide.VersionChoice);
                        if (cfg != null && !string.IsNullOrEmpty(cfg.RootPath))
                        {
                            root = cfg.RootPath;
                            how = "its Build > Set Clarion Version '" + cfg.Name + "'";
                        }
                    }
                    if (root == null && !string.IsNullOrEmpty(ide.ExePath))
                    {
                        try { root = Path.GetDirectoryName(Path.GetDirectoryName(ide.ExePath)); } catch { root = null; }
                        how = "its own Clarion (" + ide.ExePath + ")";
                    }
                    if (root == null) continue;
                    var at = AtRoot(cands, root);
                    if (at != null)
                        return Picked(at, SourceIdeLive, "server under " + at.Root + " [chosen by: the IDE (pid " + ide.Pid
                            + ") that has this solution open - " + how + "]", carried);
                    carried.Add("the IDE (pid " + ide.Pid + ") that has this solution open runs " + how
                        + ", but Clarion Assistant's server is not installed under " + root);
                }
            }

            // ---- 4. Newest: a fallback, and said so ----
            var newest = Newest(cands);
            string fallback = "server under " + newest.Root + " [chosen by: NEWEST installed Clarion with the server ("
                + VersionText(newest) + ") - a FALLBACK: "
                + (string.IsNullOrEmpty(input.SolutionPath) ? "no solution was found to ask"
                   : carried.Count > 0 ? "nothing above named a Clarion that has the server"
                   : "nothing about this solution named its Clarion")
                + ". Commit " + SolutionClarionVersion.FileName + " {\"clarionVersion\": \"<name>\"} next to the .sln to settle it]";
            var pick = Picked(newest, SourceNewest, fallback, carried);
            pick.IsFallback = true;
            return pick;
        }

        private static ServerPick Picked(ServerCandidate c, string source, string note, List<string> carried)
        {
            if (carried.Count > 0) note = string.Join("; ", carried.ToArray()) + "; so " + note;
            return new ServerPick { Server = c, Source = source, Note = note };
        }

        private static ServerCandidate AtRoot(List<ServerCandidate> cands, string root)
        {
            return cands.Find(c => SolutionFollower.SamePath(c.Root, root));
        }

        /// <summary>The newest by Clarion.exe version; unknown versions last; ties keep the given order.</summary>
        private static ServerCandidate Newest(List<ServerCandidate> cands)
        {
            ServerCandidate best = null;
            foreach (var c in cands)
            {
                if (best == null) { best = c; continue; }
                if (c.ClarionExeVersion != null && (best.ClarionExeVersion == null || c.ClarionExeVersion > best.ClarionExeVersion))
                    best = c;
            }
            return best;
        }

        private static string VersionText(ServerCandidate c)
        {
            return c.ClarionExeVersion != null ? "Clarion.exe " + c.ClarionExeVersion : "Clarion.exe version unknown";
        }

        // ------------------------------------------------------------------------------------------------
        // Gathering the inputs on a real machine
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Every installed server, deduplicated by root, in the order looked at: CLARION_ROOTS, the installer's
        /// HKCU\Software\ClarionAssistant\InstallPaths values, C:\Clarion12, C:\Clarion11.1, C:\Clarion11, C:\Clarion10,
        /// and the Clarion this server is itself installed under. Only roots that actually hold the server count.
        /// </summary>
        public static List<ServerCandidate> FindInstalled(string ownRoot)
        {
            var roots = new List<KeyValuePair<string, string>>();
            string env = Environment.GetEnvironmentVariable(RootsEnvVar);
            if (!string.IsNullOrEmpty(env))
                foreach (string r in env.Split(';'))
                    if (!string.IsNullOrWhiteSpace(r)) roots.Add(new KeyValuePair<string, string>(r.Trim().Trim('"'), RootsEnvVar));
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\ClarionAssistant\InstallPaths"))
                {
                    if (key != null)
                        foreach (string name in key.GetValueNames())
                        {
                            string v = key.GetValue(name) as string;
                            if (!string.IsNullOrWhiteSpace(v)) roots.Add(new KeyValuePair<string, string>(v.Trim(), "installer record " + name));
                        }
                }
            }
            catch { }
            foreach (string c in new[] { @"C:\Clarion12", @"C:\Clarion11.1", @"C:\Clarion11", @"C:\Clarion10" })
                roots.Add(new KeyValuePair<string, string>(c, "conventional root"));
            if (!string.IsNullOrEmpty(ownRoot)) roots.Add(new KeyValuePair<string, string>(ownRoot, "this server's own install"));

            var found = new List<ServerCandidate>();
            foreach (var r in roots)
            {
                string root;
                try { root = Path.GetFullPath(r.Key).TrimEnd('\\'); } catch { continue; }
                if (found.Exists(f => SolutionFollower.SamePath(f.Root, root))) continue;
                string exe = Path.Combine(root, RelativeExe);
                if (!File.Exists(exe)) continue;
                found.Add(new ServerCandidate
                {
                    Root = root, ExePath = exe, ClarionExeVersion = ExeVersion(root), ServerVersion = FileVersionOf(exe), Origin = r.Value
                });
            }
            return found;
        }

        /// <summary>The candidate for one server exe (the bootstrap): its root is three folders up when it sits in a Clarion tree.</summary>
        public static ServerCandidate CandidateFor(string exe, string origin)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            string root = null;
            try { root = ClarionVersionService.InstalledClarionRoot(Path.GetDirectoryName(Path.GetFullPath(exe))); } catch { }
            return new ServerCandidate
            {
                Root = root ?? Path.GetDirectoryName(exe), ExePath = exe,
                ClarionExeVersion = root != null ? ExeVersion(root) : null, ServerVersion = FileVersionOf(exe), Origin = origin
            };
        }

        /// <summary>A server exe's file version (Clarion Assistant's own), or null when it has none or cannot be read.</summary>
        public static Version FileVersionOf(string exe)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
                var fv = FileVersionInfo.GetVersionInfo(exe);
                if (fv.FileMajorPart == 0 && fv.FileMinorPart == 0 && fv.FileBuildPart == 0 && fv.FilePrivatePart == 0) return null;
                return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart);
            }
            catch { return null; }
        }

        private static Version ExeVersion(string root)
        {
            try
            {
                string exe = Path.Combine(root, "bin", "Clarion.exe");
                if (!File.Exists(exe)) return null;
                var fv = FileVersionInfo.GetVersionInfo(exe);
                return fv.FileMajorPart > 0 ? new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The pick on this machine for <paramref name="solutionPath"/>: every input gathered for real. A named version
        /// is looked up in the settings folder the server itself would use (a /ConfigDir one when one is known).
        /// </summary>
        public static ServerPick PickForMachine(string solutionPath, string commandLineVersion,
                                               string configDirArg, string configDirEnv, string baseDir,
                                               string bootstrapExe)
        {
            var pin = SolutionClarionVersion.ReadPin(solutionPath);
            var dir = StandaloneConfigDir.Resolve(IdeSolutionRecord.FindLiveForSolution(solutionPath), configDirArg,
                configDirEnv, pin, baseDir, null, null);
            string cfgDir = dir.Error == null ? dir.Path : null;
            return Pick(new ServerPickInputs
            {
                SolutionPath = solutionPath,
                CommandLineVersion = commandLineVersion,
                PinnedVersion = pin.Version,
                AllowOlderPinnedServer = pin.AllowOlderServer != false,
                Candidates = FindInstalled(ClarionVersionService.InstalledClarionRoot()),
                FindNamed = name => cfgDir != null
                    ? ClarionVersionService.FindNamedInConfigDir(cfgDir, name)
                    : ClarionVersionService.FindNamedVersion(null, name),
                ProjectMajors = SolutionVersionResolver.ProjectClarionMajors(solutionPath),
                LiveIdes = FindLiveIdes(solutionPath),
                Bootstrap = CandidateFor(bootstrapExe, "the server answering --pick-server")
            });
        }

        /// <summary>
        /// The serverChosenBy sentence for a server the launcher started (<paramref name="launcherMode"/> is the
        /// CLARION_MCP_LAUNCHER value it set): the launcher's reason, recomputed here from the same inputs, checked
        /// against the server actually running (<paramref name="ownExe"/>). Null when not started by the launcher.
        /// "override" (CLARION_MCP_SERVER) makes no pick, but still WARNS when the named server is older than another
        /// installed one (<paramref name="installed"/>). "fallback": the pick (<paramref name="failedPick"/>) failed to
        /// start and the launcher fell back to the bootstrap, this server.
        /// </summary>
        public static string DescribeLaunch(string launcherMode, string ownExe, Func<ServerPick> pick,
                                            Func<List<ServerCandidate>> installed, string failedPick)
        {
            if (string.IsNullOrEmpty(launcherMode)) return null;
            if (string.Equals(launcherMode, "override", StringComparison.OrdinalIgnoreCase))
            {
                string line = "started by the clarion-tools launcher as named by CLARION_MCP_SERVER (" + ownExe + "); no pick was made";
                try
                {
                    Version own = FileVersionOf(ownExe);
                    ServerCandidate newer = null;
                    foreach (var c in (installed != null ? installed() : null) ?? new List<ServerCandidate>())
                        if (c.ServerVersion != null && (own == null || c.ServerVersion > own)
                            && (newer == null || c.ServerVersion > newer.ServerVersion)) newer = c;
                    if (newer != null)
                        line += "; WARNING: this server is Clarion Assistant " + (own != null ? own.ToString() : "(version unreadable)")
                            + ", OLDER than " + newer.ExePath + " (Clarion Assistant " + newer.ServerVersion
                            + ") - it lacks every fix made since";
                }
                catch { }
                return line;
            }
            bool fallback = string.Equals(launcherMode, "fallback", StringComparison.OrdinalIgnoreCase);
            string prefix = "started by the clarion-tools launcher - ";
            if (string.Equals(launcherMode, "bootstrap", StringComparison.OrdinalIgnoreCase))
                prefix = "started by the clarion-tools launcher WITHOUT a pick (the first server it found could not choose; an older version?) - ";
            else if (fallback)
                prefix = "started by the clarion-tools launcher as a FALLBACK: the server it picked (" + (failedPick ?? "?")
                    + ") failed to start, so this server (" + ownExe + ") serves instead - ";
            ServerPick p;
            try { p = pick(); } catch (Exception ex) { return prefix + "the pick could not be re-checked: " + ex.Message; }
            if (p == null || p.Server == null) return prefix + "this server (" + ownExe + ")";
            if (SolutionFollower.SamePath(p.Server.ExePath, ownExe)) return prefix + p.Note;
            if (fallback) return prefix + "the pick was: " + p.Note;
            return prefix + "this server is " + ownExe + ", but a pick now gives " + p.Server.ExePath + " (" + p.Note
                + "); restart the MCP server to switch";
        }

        /// <summary>The live IDEs on <paramref name="solutionPath"/>, from their published records.</summary>
        public static List<LiveIde> FindLiveIdes(string solutionPath)
        {
            var list = new List<LiveIde>();
            foreach (var d in IdeSolutionRecord.FindLiveForSolution(solutionPath))
            {
                string exe = null;
                try { using (var p = Process.GetProcessById(d.Pid)) exe = p.MainModule.FileName; } catch { exe = null; }
                list.Add(new LiveIde { Pid = d.Pid, VersionChoice = d.VersionChoice, ExePath = exe });
            }
            return list;
        }
    }
}
