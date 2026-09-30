using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace ClarionAssistant.Services
{
    public class ClarionVersionConfig
    {
        public string Name { get; set; }
        public string BinPath { get; set; }
        public string RootPath { get; set; }
        public string RedFileName { get; set; }
        public List<string> LibSrcPaths { get; set; }
        public Dictionary<string, string> Macros { get; set; }

        /// <summary>
        /// The entry's &lt;IsWindowsVersion&gt;: true for a Win32 Clarion, false for a Clarion.NET
        /// (Clarion#) compiler entry, null when the XML does not say.
        /// </summary>
        public bool? IsWindowsVersion { get; set; }

        /// <summary>
        /// The entry's &lt;CWVersion&gt; (e.g. 12026 for Clarion 12, 11100 for 11.1, 2000 for a Clarion.NET entry),
        /// or null when the XML does not say. Its thousands are the Clarion major version.
        /// </summary>
        public int? CWVersion { get; set; }

        /// <summary>
        /// The ClarionGraph library-DB key for THIS version (16d140e9): its own Clarion.exe build
        /// (<paramref name="exeBuildKey"/>, e.g. "12.0.0.14313"; "nobuild" when unknown) plus a stable
        /// fingerprint of its root, which is where its LibSrc comes from. Entries sharing one root share one
        /// DB (same library); different installs never do, even under one running IDE.
        /// </summary>
        public string LibraryGraphKey(string exeBuildKey)
        {
            string basis = !string.IsNullOrEmpty(RootPath) ? RootPath : (BinPath ?? Name ?? "");
            basis = basis.Trim().TrimEnd('\\', '/').Replace('/', '\\').ToUpperInvariant();
            uint h = 2166136261;   // FNV-1a 32: stable across processes and runtimes (string.GetHashCode is not)
            foreach (char c in basis) { h ^= c; h *= 16777619; }
            return (string.IsNullOrEmpty(exeBuildKey) ? "nobuild" : exeBuildKey) + "_" + h.ToString("x8");
        }

        public string RedFilePath
        {
            get
            {
                if (string.IsNullOrEmpty(RootPath) || string.IsNullOrEmpty(RedFileName))
                    return null;
                return Path.Combine(RootPath, "bin", RedFileName);
            }
        }
    }

    public class ClarionVersionInfo
    {
        public string ClarionExePath { get; set; }

        /// <summary>
        /// The running Clarion.exe's file version (e.g. 11.0.0.13372 — the Clarion build is the last
        /// part), or null if it could not be read. Used only to tell apart version entries that share
        /// the running exe's bin folder.
        /// </summary>
        public Version ClarionExeVersion { get; set; }

        public string PropertiesXmlPath { get; set; }

        /// <summary>
        /// The IDE's Build &gt; Set Clarion Version choice. Clarion.Core.Options.Versions.SetActiveVersion
        /// writes it to the IDE's PropertyService key "Clarion.Version" (null/empty = "Current", i.e. the
        /// running Clarion's own version) AND to the open solution's preferences file (ActiveVersion in
        /// ConfigDir\preferences\&lt;sln&gt;.&lt;hash&gt;.xml); on solution open the IDE copies the solution's
        /// ActiveVersion back into "Clarion.Version". So the live key is the solution's choice.
        /// </summary>
        public string CurrentVersionName { get; set; }

        /// <summary>
        /// True when <see cref="CurrentVersionName"/> came from the running IDE's PropertyService (live);
        /// false when it came from ClarionProperties.xml on disk (stale until the IDE closes, and the only
        /// source outside the IDE).
        /// </summary>
        public bool CurrentVersionFromLiveIde { get; set; }

        public List<ClarionVersionConfig> Versions { get; set; }

        public ClarionVersionInfo()
        {
            Versions = new List<ClarionVersionConfig>();
        }

        public ClarionVersionConfig GetCurrentConfig()
        {
            ClarionVersionTier tier;
            return ResolveIdeChoice(out tier);
        }

        /// <summary>
        /// The IDE's selection, and which tier decided it: the named entry (<see cref="ClarionVersionTier.IdeSelection"/>),
        /// the running Clarion.exe when the name is "Current"/empty/unknown (<see cref="ClarionVersionTier.RunningExe"/>),
        /// else the first listed entry (<see cref="ClarionVersionTier.FirstListed"/>).
        /// </summary>
        public ClarionVersionConfig ResolveIdeChoice(out ClarionVersionTier tier)
        {
            tier = ClarionVersionTier.None;
            if (!ClarionVersionSelector.IsCurrentChoice(CurrentVersionName))
            {
                var named = Versions.Find(v => v.Name == CurrentVersionName);
                if (named != null) { tier = ClarionVersionTier.IdeSelection; return named; }
            }

            var byExe = ResolveByExePath();
            if (byExe != null) { tier = ClarionVersionTier.RunningExe; return byExe; }

            if (Versions.Count > 0) { tier = ClarionVersionTier.FirstListed; return Versions[0]; }
            return null;
        }

        /// <summary>
        /// The version entry for the running Clarion.exe, found by its bin folder.
        ///
        /// GH #209: several entries can share one bin folder. Every Clarion install registers its
        /// Clarion.NET compiler on the same bin as the Win32 IDE ("Clarion.NET 4.0.13372" and
        /// "Clarion 11.0.13372" both on C:\Clarion\v11\bin), and Clarion 11 / 11.1 installs all
        /// share one ClarionProperties.xml, so the list can also hold other installs' entries. The
        /// old first-match returned whichever came first in the XML — on the reporter's machine the
        /// .NET compiler, not the IDE. Candidates are now narrowed by what the running process can
        /// prove about itself, and first-match survives only as the tie-break:
        ///   1. Drop IsWindowsVersion=False — Clarion.exe is the Win32 IDE; a Clarion.NET entry never
        ///      is. An entry that omits the flag stays a candidate.
        ///   2. Drop entries whose CWVersion major (CWVersion / 1000) differs from the exe's FileMajorPart —
        ///      C:\Clarion12\bin holds "Clarion 12.0.14313" (12026) and "Clarion.NET 4.0.14313" (2000).
        ///   3. The exe's build number (FileVersion's last part, e.g. 13372) as a whole number in the
        ///      entry name — how Clarion names the entries it creates ("Clarion 11.0.13372").
        /// Each step narrows only when it leaves at least one candidate, so an XML without those
        /// fields, or a renamed entry, degrades to the old answer rather than to none.
        /// </summary>
        private ClarionVersionConfig ResolveByExePath()
        {
            if (string.IsNullOrEmpty(ClarionExePath)) return null;
            string exeDir = Path.GetDirectoryName(ClarionExePath);
            if (string.IsNullOrEmpty(exeDir)) return null;
            exeDir = exeDir.TrimEnd('\\');

            var candidates = new List<ClarionVersionConfig>();
            foreach (var v in Versions)
            {
                if (!string.IsNullOrEmpty(v.BinPath) &&
                    exeDir.Equals(v.BinPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    candidates.Add(v);
            }
            if (candidates.Count <= 1) return candidates.Count == 1 ? candidates[0] : null;

            // Drop only PROVEN .NET entries: an older XML can mark the .NET entry False and omit
            // the flag on the Win32 one, and "== true" would then keep nothing and fall to first-match.
            candidates = Narrow(candidates, v => v.IsWindowsVersion != false);

            // 286f2e57: CWVersion's thousands are the Clarion major version (C12 12026, Clarion.NET 2000), so an
            // entry whose major differs from the exe's is not this IDE, even one that omits IsWindowsVersion.
            int major = ClarionExeVersion != null ? ClarionExeVersion.Major : -1;
            if (major > 0)
                candidates = Narrow(candidates, v => v.CWVersion == null || v.CWVersion.Value / 1000 == major);

            int build = ClarionExeVersion != null ? ClarionExeVersion.Revision : -1;
            if (build > 0)
            {
                var token = new Regex(
                    "(?<![0-9])" + build.ToString(CultureInfo.InvariantCulture) + "(?![0-9])");
                candidates = Narrow(candidates, v => v.Name != null && token.IsMatch(v.Name));
            }

            return candidates[0];
        }

        private static List<ClarionVersionConfig> Narrow(List<ClarionVersionConfig> list, Predicate<ClarionVersionConfig> keep)
        {
            var kept = list.FindAll(keep);
            return kept.Count > 0 ? kept : list;
        }
    }

    public static class ClarionVersionService
    {
        public static ClarionVersionInfo Detect()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                if (string.IsNullOrEmpty(exePath)) return null;

                string xmlPath = FindPropertiesXml(exePath);
                if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath)) return null;

                var info = ParsePropertiesXml(xmlPath);
                if (info != null)
                {
                    info.ClarionExePath = exePath;
                    try
                    {
                        var fv = FileVersionInfo.GetVersionInfo(exePath);
                        info.ClarionExeVersion = new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart);
                    }
                    catch { }

                    // Try to get the LIVE current version from the running IDE
                    // (the XML may be stale until IDE closes). An EMPTY live answer is an answer:
                    // the IDE stores "Current" (the running Clarion's own version) as a null
                    // Clarion.Version, so falling back to the XML there would resurrect whatever
                    // the file last held (16d140e9).
                    string liveVersion;
                    if (TryGetLiveIdeVersionName(out liveVersion))
                    {
                        info.CurrentVersionName = liveVersion;
                        info.CurrentVersionFromLiveIde = true;
                    }
                }
                return info;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read the IDE's current Build &gt; Set Clarion Version choice from the running IDE's
        /// PropertyService ("Clarion.Version"). This reflects the live selection, not the on-disk XML.
        ///
        /// Returns false when there is no live IDE to ask (outside Clarion.exe: the standalone MCP
        /// server, the indexer, a test harness) — ICSharpCode.Core not loaded, or its PropertyService
        /// not initialized. Returns TRUE with an EMPTY name when the IDE is on "Current": Clarion stores
        /// that choice as a null Clarion.Version (Versions.ActiveWinVersion setter), and the caller must
        /// then resolve by the running exe rather than fall back to the XML's stale value.
        ///
        /// Only an ALREADY-LOADED ICSharpCode.Core is used (it used to be Assembly.Load'ed): loading it
        /// into a process that is not the IDE would find an uninitialized service and, with the empty
        /// answer now meaning "Current", override the XML with nothing.
        /// </summary>
        public static bool TryGetLiveIdeVersionName(out string name)
        {
            name = null;
            try
            {
                var members = _livePropertyService ?? (_livePropertyService = ResolveLivePropertyService());
                if (members == null) return false;

                if (members.Initialized != null && !Equals(members.Initialized.GetValue(null, null), true))
                    return false;

                var result = members.GetString.Invoke(null, new object[] { "Clarion.Version", "" });
                name = result as string ?? "";
                return true;
            }
            catch { name = null; return false; }
        }

        /// <summary>PropertyService's Initialized property and its Get&lt;string&gt;(key, default) method.</summary>
        private sealed class LivePropertyServiceMembers
        {
            public System.Reflection.PropertyInfo Initialized;
            public System.Reflection.MethodInfo GetString;
        }

        // f3b47441: resolved once, on the first call that finds ICSharpCode.Core loaded. LspAutostartCommand's
        // 5 s tick reads the version, and scanning every loaded assembly (GetName() allocates) each time was
        // a steady cost on the UI thread. A miss is NOT cached: the assembly may simply not be loaded yet.
        private static LivePropertyServiceMembers _livePropertyService;

        private static LivePropertyServiceMembers ResolveLivePropertyService()
        {
            System.Reflection.Assembly sharpDevelopAsm = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(a.GetName().Name, "ICSharpCode.Core", StringComparison.OrdinalIgnoreCase))
                { sharpDevelopAsm = a; break; }
            }
            if (sharpDevelopAsm == null) return null;

            var propertyServiceType = sharpDevelopAsm.GetType("ICSharpCode.Core.PropertyService");
            if (propertyServiceType == null) return null;

            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            // PropertyService.Get<string>("Clarion.Version", "") — the two-parameter generic overload
            // (there is also a four-parameter one; invoking that with two arguments throws, which the
            // old first-generic-match loop could hit depending on reflection order).
            foreach (var m in propertyServiceType.GetMethods(flags))
            {
                if (m.Name != "Get" || !m.IsGenericMethodDefinition || m.GetParameters().Length != 2) continue;
                return new LivePropertyServiceMembers
                {
                    Initialized = propertyServiceType.GetProperty("Initialized", flags),
                    GetString = m.MakeGenericMethod(typeof(string))
                };
            }
            return null;
        }

        private static string FindPropertiesXml(string exePath)
        {
            try
            {
                // ASK THE IDE FIRST (GitHub #197). Clarion.exe accepts /ConfigDir=<path>, which moves this
                // whole tree; reconstructing the default path from the exe's version stamp reads a
                // DIFFERENT environment's file whenever the switch is in play. It is not a rare mismatch
                // either — Clarion 11 and 11.1 both stamp major.minor as "11.0", so two such installs
                // collapse onto one ClarionProperties.xml, which is what merged their app histories.
                string configDir = ClarionConfigDirectory.Resolve();
                if (!string.IsNullOrEmpty(configDir))
                {
                    string direct = Path.Combine(configDir, "ClarionProperties.xml");
                    if (File.Exists(direct)) return direct;
                    // Resolved but no file there: fall through rather than fail. A custom ConfigDir that
                    // has not been written yet is a legitimate first-run state.
                }

                string appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SoftVelocity", "Clarion");
                if (!Directory.Exists(appDataDir)) return null;

                var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                if (versionInfo.FileMajorPart > 0)
                {
                    string versionDir = string.Format("{0}.{1}", versionInfo.FileMajorPart, versionInfo.FileMinorPart);
                    string xmlPath = Path.Combine(appDataDir, versionDir, "ClarionProperties.xml");
                    if (File.Exists(xmlPath)) return xmlPath;
                }

                // Fallback: newest version folder
                string bestPath = null;
                Version bestVersion = null;
                foreach (string dir in Directory.GetDirectories(appDataDir))
                {
                    Version v;
                    if (Version.TryParse(Path.GetFileName(dir), out v))
                    {
                        string candidate = Path.Combine(dir, "ClarionProperties.xml");
                        if (File.Exists(candidate) && (bestVersion == null || v > bestVersion))
                        {
                            bestVersion = v;
                            bestPath = candidate;
                        }
                    }
                }
                return bestPath;
            }
            catch { return null; }
        }

        // internal (not private) so tests\ClarionVersionService.ExeMatchTest.cs can parse a fixture.
        internal static ClarionVersionInfo ParsePropertiesXml(string xmlPath)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(xmlPath);
                var info = new ClarionVersionInfo { PropertiesXmlPath = xmlPath };

                var currentNode = doc.SelectSingleNode("//ClarionProperties/Clarion.Version");
                if (currentNode != null && currentNode.Attributes["value"] != null)
                    info.CurrentVersionName = currentNode.Attributes["value"].Value;

                var versionsNode = doc.SelectSingleNode("//ClarionProperties/Properties[@name='Clarion.Versions']");
                if (versionsNode != null)
                {
                    foreach (XmlNode versionNode in versionsNode.ChildNodes)
                    {
                        if (versionNode.Name == "Properties" && versionNode.Attributes["name"] != null)
                        {
                            var config = ParseVersionConfig(versionNode);
                            if (config != null) info.Versions.Add(config);
                        }
                    }
                }
                return info;
            }
            catch { return null; }
        }

        private static ClarionVersionConfig ParseVersionConfig(XmlNode node)
        {
            try
            {
                var config = new ClarionVersionConfig
                {
                    Name = node.Attributes["name"].Value,
                    Macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };

                var pathNode = node.SelectSingleNode("path");
                if (pathNode != null && pathNode.Attributes["value"] != null)
                    config.BinPath = pathNode.Attributes["value"].Value;

                var winNode = node.SelectSingleNode("IsWindowsVersion");
                bool isWin;
                if (winNode != null && winNode.Attributes["value"] != null &&
                    bool.TryParse(winNode.Attributes["value"].Value, out isWin))
                    config.IsWindowsVersion = isWin;

                var cwNode = node.SelectSingleNode("CWVersion");
                int cw;
                if (cwNode != null && cwNode.Attributes["value"] != null &&
                    int.TryParse(cwNode.Attributes["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cw))
                    config.CWVersion = cw;

                var redNode = node.SelectSingleNode("Properties[@name='RedirectionFile']");
                if (redNode != null)
                {
                    var nameNode = redNode.SelectSingleNode("Name");
                    if (nameNode != null && nameNode.Attributes["value"] != null)
                        config.RedFileName = nameNode.Attributes["value"].Value;

                    var macrosNode = redNode.SelectSingleNode("Properties[@name='Macros']");
                    if (macrosNode != null)
                    {
                        foreach (XmlNode macroNode in macrosNode.ChildNodes)
                        {
                            if (macroNode.Attributes != null && macroNode.Attributes["value"] != null)
                                config.Macros[macroNode.Name] = macroNode.Attributes["value"].Value;
                        }
                        string rootValue;
                        if (config.Macros.TryGetValue("root", out rootValue))
                            config.RootPath = rootValue;
                    }
                }

                // Parse <libsrc> paths (semicolon-separated) — used by LSP for library source resolution
                config.LibSrcPaths = new List<string>();
                var libsrcNode = node.SelectSingleNode("libsrc");
                if (libsrcNode != null && libsrcNode.Attributes["value"] != null)
                {
                    foreach (var p in libsrcNode.Attributes["value"].Value.Split(';'))
                    {
                        var trimmed = p.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                            config.LibSrcPaths.Add(trimmed);
                    }
                }

                if (string.IsNullOrEmpty(config.RootPath) && !string.IsNullOrEmpty(config.BinPath))
                    config.RootPath = Path.GetDirectoryName(config.BinPath);

                return config;
            }
            catch { return null; }
        }

        /// <summary>
        /// Parse RecentOpen.xml (same folder as ClarionProperties.xml) and return .sln paths
        /// in order (most recent first). Paths that don't exist on disk are included so the
        /// caller can decide whether to skip them.
        /// </summary>
        public static List<string> GetRecentSolutionPaths(string propertiesXmlPath)
        {
            var result = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(propertiesXmlPath)) return result;
                string dir = Path.GetDirectoryName(propertiesXmlPath);
                string recentPath = Path.Combine(dir, "RecentOpen.xml");
                if (!File.Exists(recentPath)) return result;

                var doc = new XmlDocument();
                doc.Load(recentPath);
                var projectNode = doc.SelectSingleNode("//Properties/Project");
                if (projectNode == null) return result;
                XmlAttribute attr = projectNode.Attributes["value"];
                if (attr == null || string.IsNullOrEmpty(attr.Value)) return result;

                // Deduplicate while preserving order (most-recent first)
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in attr.Value.Split(','))
                {
                    string path = part.Trim();
                    if (!string.IsNullOrEmpty(path) &&
                        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) &&
                        seen.Add(path))
                    {
                        result.Add(path);
                    }
                }
            }
            catch { }
            return result;
        }
    }

    /// <summary>Which source decided the Clarion version CA uses (never resolve one silently).</summary>
    public enum ClarionVersionTier
    {
        None,
        /// <summary>The IDE's Build &gt; Set Clarion Version, naming a configured entry.</summary>
        IdeSelection,
        /// <summary>The IDE is on "Current" (or names nothing configured): the running Clarion.exe's own entry.</summary>
        RunningExe,
        /// <summary>Nothing matched: the first entry in ClarionProperties.xml.</summary>
        FirstListed
    }

    /// <summary>The outcome of <see cref="ClarionVersionSelector.Select"/>: one version for every CA component.</summary>
    public sealed class ClarionVersionSelection
    {
        public ClarionVersionConfig Config { get; internal set; }
        public ClarionVersionTier Tier { get; internal set; }

        /// <summary>The IDE's Build &gt; Set Clarion Version choice, normalized ("Current" for the running version).</summary>
        public string IdeChoice { get; internal set; }

        /// <summary>True when <see cref="IdeChoice"/> was read live from the running IDE, false when from the XML.</summary>
        public bool IdeChoiceLive { get; internal set; }

        /// <summary>
        /// Short source label for the VERSION display: "IDE" whenever the IDE's Build &gt; Set Clarion Version
        /// decided, including "Current" (the running Clarion's own version); "first listed" when nothing matched.
        /// </summary>
        public string ShortSource
        {
            get
            {
                switch (Tier)
                {
                    case ClarionVersionTier.IdeSelection:
                    case ClarionVersionTier.RunningExe: return "IDE";
                    case ClarionVersionTier.FirstListed: return "first listed";
                    default: return null;
                }
            }
        }

        /// <summary>One line for logs: the version and the tier that chose it.</summary>
        public string Describe()
        {
            string name = Config != null ? Config.Name : "(none)";
            string src;
            switch (Tier)
            {
                case ClarionVersionTier.IdeSelection:
                    src = "the IDE's Build > Set Clarion Version" + (IdeChoiceLive ? "" : " (read from ClarionProperties.xml - no live IDE)");
                    break;
                case ClarionVersionTier.RunningExe:
                    src = "the running Clarion.exe (the IDE's Build > Set Clarion Version is '" + IdeChoice + "')";
                    break;
                case ClarionVersionTier.FirstListed:
                    src = "the first entry in ClarionProperties.xml (nothing else matched)";
                    break;
                default:
                    src = "no Clarion version detected";
                    break;
            }
            return "Clarion version: " + name + " - chosen by " + src;
        }
    }

    /// <summary>
    /// The ONE rule for which Clarion version CA uses. Pure — no IDE, no settings file — so the harness
    /// (tests\ClarionVersionSelector.Test.cs) drives it directly.
    ///
    /// 286f2e57: the IDE's Build &gt; Set Clarion Version is the ONLY authority. CA displays it and has no version
    /// picker of its own. Before, CA's VERSION dropdown saved an override (GH #32; per solution since 16d140e9)
    /// that could outrank the IDE: the Owner's C12 IDE on "(Current Version)" showed "Clarion 10 Active And
    /// Updated (saved)", from a legacy override another IDE had written to the shared settings.txt. Two places
    /// to set one version was the defect. The overrides are no longer read; any left in settings.txt
    /// ("Clarion.Version.Override", "Clarion.Version.Override@&lt;sln&gt;") are inert and deliberately not deleted.
    /// </summary>
    public static class ClarionVersionSelector
    {
        public const string CurrentChoice = "Current";

        /// <summary>True for the IDE's "use the running version" choice: empty, "Current", "(Current Version)".
        /// Clarion also stores an explicit pick of the RUNNING version's own name as null (the
        /// Versions.ActiveWinVersion setter), so that reaches CA as empty too.</summary>
        public static bool IsCurrentChoice(string name)
        {
            return string.IsNullOrEmpty(name) || name.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string NormalizeIdeChoice(string name)
        {
            return IsCurrentChoice(name) ? CurrentChoice : name.Trim();
        }

        public static ClarionVersionSelection Select(ClarionVersionInfo info)
        {
            var sel = new ClarionVersionSelection { Tier = ClarionVersionTier.None };
            if (info == null) return sel;

            ClarionVersionTier ideTier;
            var ideConfig = info.ResolveIdeChoice(out ideTier);
            sel.Config = ideConfig;
            sel.Tier = ideConfig != null ? ideTier : ClarionVersionTier.None;
            sel.IdeChoice = NormalizeIdeChoice(info.CurrentVersionName);
            sel.IdeChoiceLive = info.CurrentVersionFromLiveIde;
            return sel;
        }
    }
}
