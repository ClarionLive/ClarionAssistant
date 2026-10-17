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

        public static ServerPick Pick(ServerPickInputs input)
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
                found.Add(new ServerCandidate { Root = root, ExePath = exe, ClarionExeVersion = ExeVersion(root), Origin = r.Value });
            }
            return found;
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
                                               string configDirArg, string configDirEnv, string baseDir)
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
                Candidates = FindInstalled(ClarionVersionService.InstalledClarionRoot()),
                FindNamed = name => cfgDir != null
                    ? ClarionVersionService.FindNamedInConfigDir(cfgDir, name)
                    : ClarionVersionService.FindNamedVersion(null, name),
                ProjectMajors = SolutionVersionResolver.ProjectClarionMajors(solutionPath),
                LiveIdes = FindLiveIdes(solutionPath)
            });
        }

        /// <summary>
        /// The serverChosenBy sentence for a server the launcher started (<paramref name="launcherMode"/> is the
        /// CLARION_MCP_LAUNCHER value it set): the launcher's reason, recomputed here from the same inputs, checked
        /// against the server actually running (<paramref name="ownExe"/>). Null when not started by the launcher.
        /// </summary>
        public static string DescribeLaunch(string launcherMode, string ownExe, Func<ServerPick> pick)
        {
            if (string.IsNullOrEmpty(launcherMode)) return null;
            if (string.Equals(launcherMode, "override", StringComparison.OrdinalIgnoreCase))
                return "started by the clarion-tools launcher as named by CLARION_MCP_SERVER (" + ownExe + "); no pick was made";
            ServerPick p;
            try { p = pick(); } catch (Exception ex) { return "started by the clarion-tools launcher; the pick could not be re-checked: " + ex.Message; }
            string prefix = string.Equals(launcherMode, "bootstrap", StringComparison.OrdinalIgnoreCase)
                ? "started by the clarion-tools launcher WITHOUT a pick (the first server it found could not choose; an older version?) - "
                : "started by the clarion-tools launcher - ";
            if (p == null || p.Server == null) return prefix + "this server (" + ownExe + ")";
            if (SolutionFollower.SamePath(p.Server.ExePath, ownExe)) return prefix + p.Note;
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
