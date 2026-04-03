using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml;

namespace ClarionAssistant.Services
{
    public class ClarionVersionConfig
    {
        public string Name { get; set; }
        public string BinPath { get; set; }
        public string RootPath { get; set; }
        public string RedFileName { get; set; }
        public Dictionary<string, string> Macros { get; set; }

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
        public string PropertiesXmlPath { get; set; }
        public string CurrentVersionName { get; set; }
        public List<ClarionVersionConfig> Versions { get; set; }

        public ClarionVersionInfo()
        {
            Versions = new List<ClarionVersionConfig>();
        }

        public ClarionVersionConfig GetCurrentConfig()
        {
            // If "(Current Version)" or similar, match by bin path of running exe
            if (string.IsNullOrEmpty(CurrentVersionName) ||
                CurrentVersionName.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ResolveByExePath() ?? (Versions.Count > 0 ? Versions[0] : null);
            }

            return Versions.Find(v => v.Name == CurrentVersionName)
                ?? ResolveByExePath()
                ?? (Versions.Count > 0 ? Versions[0] : null);
        }

        private ClarionVersionConfig ResolveByExePath()
        {
            if (string.IsNullOrEmpty(ClarionExePath)) return null;
            string exeDir = Path.GetDirectoryName(ClarionExePath);
            if (string.IsNullOrEmpty(exeDir)) return null;

            // Match against each version config's BinPath
            foreach (var v in Versions)
            {
                if (!string.IsNullOrEmpty(v.BinPath) &&
                    exeDir.Equals(v.BinPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return v;
            }
            return null;
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

                    // Try to get the LIVE current version from the running IDE
                    // (the XML may be stale until IDE closes)
                    string liveVersion = GetLiveVersionFromIde();
                    if (!string.IsNullOrEmpty(liveVersion))
                        info.CurrentVersionName = liveVersion;
                }
                return info;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read the current version selection from the running IDE's PropertyService.
        /// This reflects the live selection, not the on-disk XML.
        /// </summary>
        private static string GetLiveVersionFromIde()
        {
            try
            {
                var sharpDevelopAsm = System.Reflection.Assembly.Load("ICSharpCode.Core");
                if (sharpDevelopAsm == null) return null;

                var propertyServiceType = sharpDevelopAsm.GetType("ICSharpCode.Core.PropertyService");
                if (propertyServiceType == null) return null;

                // Try PropertyService.Get("Clarion.Version", "")
                var getMethod = propertyServiceType.GetMethod("Get",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new Type[] { typeof(string), typeof(string) }, null);

                if (getMethod != null)
                {
                    var result = getMethod.Invoke(null, new object[] { "Clarion.Version", "" });
                    if (result is string s && !string.IsNullOrEmpty(s))
                        return s;
                }

                // Fallback: try the generic Get<T> method
                var getMethods = propertyServiceType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                foreach (var m in getMethods)
                {
                    if (m.Name == "Get" && m.IsGenericMethod)
                    {
                        var generic = m.MakeGenericMethod(typeof(string));
                        var result = generic.Invoke(null, new object[] { "Clarion.Version", "" });
                        if (result is string s2 && !string.IsNullOrEmpty(s2))
                            return s2;
                    }
                }

                return null;
            }
            catch { return null; }
        }

        private static string FindPropertiesXml(string exePath)
        {
            try
            {
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

        private static ClarionVersionInfo ParsePropertiesXml(string xmlPath)
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
}
