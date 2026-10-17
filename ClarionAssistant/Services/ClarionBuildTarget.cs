using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ClarionAssistant.Services
{
    /// <summary>What kind of Clarion project a .cwproj is, as far as ClarionCL /ag is concerned.</summary>
    internal enum ClarionProjectKind
    {
        /// <summary>Driven by an .app: template generation writes its modules. The .app was found.</summary>
        App,
        /// <summary>Its modules are marked generated, but no .app for it was found: nothing to generate FROM.</summary>
        AppMissing,
        /// <summary>Hand-written: no generated modules and no .app. ClarionCL /ag has nothing to do for it.</summary>
        Source
    }

    /// <summary>One .cwproj as the build tools see it: kind, the evidence for it, and where its binary lands.</summary>
    internal sealed class ClarionProjectInfo
    {
        public string CwprojPath;
        public string ProjectName;
        public string AppPath;
        public ClarionProjectKind Kind;
        public List<string> Evidence = new List<string>();
        public int CompileCount;
        public int GeneratedCount;

        public string Configuration;
        public string ConfigurationNote;
        public string OutputName;
        public string OutputType;
        public string OutputExtension;
        public string OutputDir;
        public string OutputDirSource;
        public string OutputFile;
        public bool OutputExists;
        public DateTime? OutputWritten;
        public List<string> Notes = new List<string>();
        /// <summary>Set when the .cwproj could not be read at all; nothing else is then meaningful.</summary>
        public string Error;
    }

    /// <summary>
    /// GH #204 (ticket 708139c0): decide whether a Clarion project is .app-driven or hand-written source, and
    /// where its .exe/.dll really goes, by reading the project instead of assuming.
    ///
    /// PROJECT KIND. The signals the IDE itself writes (read off real projects: an IDE-generated PRM002.cwproj
    /// next to PRM002.app, and a hand-written prg4.cwproj):
    ///   - every module of an app project is a <c>&lt;Compile&gt;</c> item carrying <c>&lt;Generated&gt;true&lt;/Generated&gt;</c>;
    ///   - the .cwproj sits beside an .app of the same name (the IDE names the project after the app);
    ///   - the .sln lists that .app under its "Solution Items" project (docs\ClarionCL-App-Generation.md §4.4).
    /// A found .app makes it <see cref="ClarionProjectKind.App"/>; generated modules with no .app found make it
    /// <see cref="ClarionProjectKind.AppMissing"/>; neither makes it <see cref="ClarionProjectKind.Source"/>.
    ///
    /// OUTPUT PATH. Mirrors SoftVelocity.Build.Clarion.targets (SetOutDir / SetDestination): <c>$(OutputPath)</c>
    /// when the project sets one (often only inside the per-configuration PropertyGroup); otherwise the
    /// Redirection task places <c>$(OutputName)</c> + the extension its <c>$(OutputType)</c> implies through the
    /// configuration's .red sections with ForOpen=false, i.e. the FIRST entry whose pattern matches, whether or
    /// not the file is there yet; with no entry, the project folder. Relative entries hang off the project folder.
    ///
    /// Kept free of IDE coupling so tests\ClarionBuildTarget.Test.cs compiles the real file.
    /// </summary>
    internal static class ClarionBuildTarget
    {
        /// <summary>Child elements of <paramref name="parent"/> named <paramref name="localName"/> (any namespace; null = all).</summary>
        private static List<XmlElement> Children(XmlNode parent, string localName)
        {
            var result = new List<XmlElement>();
            if (parent == null) return result;
            foreach (XmlNode n in parent.ChildNodes)
            {
                var e = n as XmlElement;
                if (e != null && (localName == null || e.LocalName == localName)) result.Add(e);
            }
            return result;
        }

        private static string Condition(XmlElement e)
        {
            return e.HasAttribute("Condition") ? e.GetAttribute("Condition") : null;
        }

        /// <summary>The extension a Clarion <c>OutputType</c> produces, or null when the type is not one Clarion writes.</summary>
        internal static string OutputExtension(string outputType)
        {
            if (string.IsNullOrEmpty(outputType)) return null;
            switch (outputType.Trim().ToLowerInvariant())
            {
                case "exe":
                case "winexe": return ".exe";
                case "library":
                case "dll": return ".dll";
                case "lib": return ".lib";
                default: return null;
            }
        }

        /// <summary>The .cwproj the IDE keeps beside <paramref name="appPath"/> (same folder, same name), or null.</summary>
        internal static string CwprojForApp(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return null;
            try
            {
                string candidate = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(appPath)),
                                                Path.GetFileNameWithoutExtension(appPath) + ".cwproj");
                return File.Exists(candidate) ? candidate : null;
            }
            catch { return null; }
        }

        private static readonly Regex SolutionItemApp = new Regex(@"^\s*(.+?\.app)\s*=", RegexOptions.IgnoreCase);

        /// <summary>The .app files a .sln lists as Solution Items, as full paths. Empty when there are none or it can't be read.</summary>
        internal static List<string> SolutionItemApps(string slnPath)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(slnPath) || !File.Exists(slnPath)) return result;
            try
            {
                string slnDir = Path.GetDirectoryName(Path.GetFullPath(slnPath));
                bool inItems = false;
                foreach (string line in File.ReadAllLines(slnPath))
                {
                    if (line.IndexOf("ProjectSection(SolutionItems)", StringComparison.OrdinalIgnoreCase) >= 0) { inItems = true; continue; }
                    if (line.IndexOf("EndProjectSection", StringComparison.OrdinalIgnoreCase) >= 0) { inItems = false; continue; }
                    if (!inItems) continue;
                    var m = SolutionItemApp.Match(line);
                    if (!m.Success) continue;
                    try
                    {
                        string full = Path.GetFullPath(Path.Combine(slnDir, m.Groups[1].Value.Trim()));
                        if (!result.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase))) result.Add(full);
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Read <paramref name="cwprojPath"/>: its kind and its output for <paramref name="configuration"/> (null = the
        /// project's own default, noted). <paramref name="red"/> is the solution/version .red (may be null); a
        /// version-named .red in the project's own folder supersedes it, as Clarion does.
        /// <paramref name="solutionApps"/> are the .sln's Solution Items .app files (may be null).
        /// </summary>
        internal static ClarionProjectInfo Inspect(string cwprojPath, string configuration, RedFileService red,
                                                   IEnumerable<string> solutionApps)
        {
            var info = new ClarionProjectInfo { CwprojPath = cwprojPath };
            XmlDocument doc;
            try
            {
                info.CwprojPath = Path.GetFullPath(cwprojPath);
                doc = new XmlDocument { XmlResolver = null };
                doc.Load(info.CwprojPath);
            }
            catch (Exception ex)
            {
                info.Error = "could not read " + cwprojPath + ": " + ex.Message;
                return info;
            }

            string projDir = Path.GetDirectoryName(info.CwprojPath);
            string projBase = Path.GetFileNameWithoutExtension(info.CwprojPath);
            info.ProjectName = projBase;

            DetectKind(info, doc, projDir, projBase, solutionApps);
            EvaluateOutput(info, doc, projDir, projBase, configuration, red);
            return info;
        }

        private static void DetectKind(ClarionProjectInfo info, XmlDocument doc, string projDir, string projBase,
                                       IEnumerable<string> solutionApps)
        {
            foreach (var itemGroup in Children(doc.DocumentElement, "ItemGroup"))
                foreach (var item in Children(itemGroup, "Compile"))
                {
                    info.CompileCount++;
                    foreach (var gen in Children(item, "Generated"))
                        if (string.Equals(gen.InnerText.Trim(), "true", StringComparison.OrdinalIgnoreCase)) { info.GeneratedCount++; break; }
                }
            info.Evidence.Add(info.GeneratedCount + " of " + info.CompileCount + " Compile items marked <Generated>true</Generated>");

            string sibling = Path.Combine(projDir, projBase + ".app");
            bool siblingExists = File.Exists(sibling);
            info.Evidence.Add(siblingExists ? projBase + ".app beside the .cwproj" : "no " + projBase + ".app beside the .cwproj");
            if (siblingExists) info.AppPath = sibling;

            string listed = null;
            if (solutionApps != null)
                foreach (string app in solutionApps)
                    if (string.Equals(Path.GetFileNameWithoutExtension(app), projBase, StringComparison.OrdinalIgnoreCase))
                    {
                        // Prefer the one in the project's own folder when the .sln names several.
                        if (listed == null || string.Equals(Path.GetDirectoryName(app), projDir, StringComparison.OrdinalIgnoreCase))
                            listed = app;
                    }
            if (listed != null)
            {
                bool listedExists = File.Exists(listed);
                info.Evidence.Add("the .sln lists " + Path.GetFileName(listed) + " under Solution Items" + (listedExists ? "" : " (file not found: " + listed + ")"));
                if (info.AppPath == null && listedExists) info.AppPath = listed;
            }
            else if (solutionApps != null)
                info.Evidence.Add("the .sln lists no " + projBase + ".app under Solution Items");

            if (info.AppPath != null) info.Kind = ClarionProjectKind.App;
            else if (info.GeneratedCount > 0) info.Kind = ClarionProjectKind.AppMissing;
            else info.Kind = ClarionProjectKind.Source;
        }

        // ------------------------------------------------------------------ MSBuild property evaluation (the subset Clarion projects use)

        private static readonly Regex PropertyRef = new Regex(@"\$\(([A-Za-z_][A-Za-z0-9_]*)\)");
        private static readonly Regex Comparison = new Regex(@"^\s*'([^']*)'\s*(==|!=)\s*'([^']*)'\s*$");

        /// <summary>
        /// $(Name) from <paramref name="props"/>. An undefined property is empty in a condition, as in MSBuild
        /// (<paramref name="undefinedEmpty"/>); in a value it is left as written, so the caller can say it is unresolved.
        /// </summary>
        private static string Expand(string value, Dictionary<string, string> props, bool undefinedEmpty = false)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            return PropertyRef.Replace(value, m =>
            {
                string v;
                return props.TryGetValue(m.Groups[1].Value, out v) ? v : (undefinedEmpty ? "" : m.Value);
            });
        }

        /// <summary>
        /// A Condition as Clarion projects write it: <c>'$(Configuration)' == 'Debug'</c>,
        /// <c>'$(Configuration)|$(Platform)' == 'Debug|Win32'</c>, <c>'$(X)' == ''</c>, joined by <c>and</c>.
        /// Null when the condition is not in that shape (the caller notes it and treats it as false).
        /// </summary>
        private static bool? EvaluateCondition(string condition, Dictionary<string, string> props)
        {
            if (condition == null || condition.Trim().Length == 0) return true;
            foreach (string part in Regex.Split(condition, @"\s+and\s+", RegexOptions.IgnoreCase))
            {
                var m = Comparison.Match(part);
                if (!m.Success) return null;
                string left = Expand(m.Groups[1].Value, props, true);
                string right = Expand(m.Groups[3].Value, props, true);
                bool equal = string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
                if (m.Groups[2].Value == "==" ? !equal : equal) return false;
            }
            return true;
        }

        private static void EvaluateOutput(ClarionProjectInfo info, XmlDocument doc, string projDir, string projBase,
                                           string configuration, RedFileService red)
        {
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "MSBuildProjectName", projBase },
                { "MSBuildProjectDirectory", projDir },
                { "MSBuildThisFileDirectory", projDir + "\\" },
                { "MSBuildProjectFile", Path.GetFileName(info.CwprojPath) }
            };
            // Like an MSBuild global property: the configuration being built wins over the project's own default.
            var global = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(configuration))
            {
                props["Configuration"] = configuration.Trim();
                global.Add("Configuration");
            }
            string outputPathFrom = null;

            foreach (var group in Children(doc.DocumentElement, "PropertyGroup"))
            {
                string gc = Condition(group);
                bool? groupOn = EvaluateCondition(gc, props);
                if (groupOn == null) { info.Notes.Add("skipped a PropertyGroup with a condition this reader does not evaluate: " + gc.Trim()); continue; }
                if (!groupOn.Value) continue;
                foreach (var p in Children(group, null))
                {
                    string name = p.LocalName;
                    if (global.Contains(name)) continue;
                    string pc = Condition(p);
                    bool? on = EvaluateCondition(pc, props);
                    if (on == null) { info.Notes.Add("skipped " + name + ": condition not evaluated: " + pc.Trim()); continue; }
                    if (!on.Value) continue;
                    props[name] = Expand(p.InnerText.Trim(), props);
                    if (string.Equals(name, "OutputPath", StringComparison.OrdinalIgnoreCase))
                        outputPathFrom = string.IsNullOrEmpty(gc) ? "the .cwproj's <OutputPath>" : "the .cwproj's <OutputPath> for " + gc.Trim();
                }
            }

            string cfg;
            props.TryGetValue("Configuration", out cfg);
            info.Configuration = string.IsNullOrEmpty(cfg) ? null : cfg;
            if (string.IsNullOrEmpty(configuration))
                info.ConfigurationNote = info.Configuration != null
                    ? "the IDE's active configuration is not known; used the project's default (" + info.Configuration + ")"
                    : "no configuration known; .red [Common] only";

            string outputName, outputType, outputPath;
            props.TryGetValue("OutputName", out outputName);
            props.TryGetValue("OutputType", out outputType);
            props.TryGetValue("OutputPath", out outputPath);
            if (string.IsNullOrEmpty(outputName))
            {
                outputName = projBase;
                info.Notes.Add("the .cwproj sets no <OutputName>; assumed the project name");
            }
            info.OutputName = outputName;
            info.OutputType = outputType;
            info.OutputExtension = OutputExtension(outputType);
            if (info.OutputExtension == null)
            {
                info.Notes.Add("OutputType '" + (outputType ?? "(none)") + "' is not Exe/WinExe/Library/Lib; the output file's extension is unknown");
            }
            if (outputName.IndexOf("$(", StringComparison.Ordinal) >= 0)
                info.Notes.Add("OutputName still holds an unexpanded property: " + outputName);

            string fileName = outputName + (info.OutputExtension ?? "");

            if (!string.IsNullOrEmpty(outputPath))
            {
                info.OutputDir = RedFileService.AnchorAt(outputPath, projDir);
                info.OutputDirSource = outputPathFrom + " (" + outputPath + ")";
                if (outputPath.IndexOf("$(", StringComparison.Ordinal) >= 0)
                    info.Notes.Add("OutputPath still holds an unexpanded property: " + outputPath);
            }
            else
            {
                RedFileService projectRed = red != null ? RedFileService.ForProjectDirectory(projDir, red) : null;
                string section = null, rawEntry = null, pattern = null;
                string dir = projectRed != null
                    ? FirstOutputEntry(projectRed, fileName, RedResolution.SectionsFor(info.Configuration), out section, out pattern, out rawEntry)
                    : null;
                if (dir != null)
                {
                    info.OutputDir = RedFileService.AnchorAt(dir, projDir);
                    info.OutputDirSource = Path.GetFileName(projectRed.RedFilePath ?? ".red") + " [" + section + "] " + pattern + " = " + rawEntry
                                         + (projectRed.RedFilePath != null ? " (" + projectRed.RedFilePath + ")" : "");
                    if (RedFileService.HasUnexpandedMacro(dir))
                        info.Notes.Add("the .red entry holds a macro this reader could not expand: " + rawEntry);
                }
                else
                {
                    info.OutputDir = projDir;
                    info.OutputDirSource = projectRed == null
                        ? "no <OutputPath> and no .red loaded: the project folder"
                        : "no <OutputPath> and no " + Path.GetFileName(projectRed.RedFilePath ?? ".red") + " entry for " + fileName + ": the project folder";
                }
            }

            if (info.OutputDir != null)
            {
                try { info.OutputFile = Path.Combine(info.OutputDir, fileName); }
                catch (Exception ex) { info.Notes.Add("output path is not usable: " + ex.Message); info.OutputFile = null; }
            }
            RefreshOutput(info);
        }

        /// <summary>Re-read whether the output file exists and when it was written (the build tools call this after ClarionCL ends).</summary>
        internal static void RefreshOutput(ClarionProjectInfo info)
        {
            if (info == null) return;
            info.OutputExists = false;
            info.OutputWritten = null;
            if (info.OutputFile == null) return;
            try
            {
                info.OutputExists = File.Exists(info.OutputFile);
                if (info.OutputExists) info.OutputWritten = File.GetLastWriteTime(info.OutputFile);
            }
            catch { info.OutputExists = false; }
        }

        /// <summary>
        /// Where the Redirection task (ForOpen=false) puts a NEW <paramref name="fileName"/>: the first entry, in
        /// <paramref name="sections"/> order, whose pattern matches it. The raw directory (macros expanded), or null.
        /// </summary>
        internal static string FirstOutputEntry(RedFileService red, string fileName, string[] sections,
                                                out string section, out string pattern, out string rawEntry)
        {
            section = pattern = rawEntry = null;
            if (red == null || string.IsNullOrEmpty(fileName) || sections == null) return null;
            foreach (string s in sections)
            {
                RedSection sec;
                if (!red.Sections.TryGetValue(s, out sec)) continue;
                foreach (var e in sec.Entries)
                {
                    // The resolver's own matcher, so this lookup and resolve_red_path never disagree on an entry.
                    if (string.IsNullOrEmpty(e.Pattern) || !RedFileService.MatchesPattern(fileName, e.Pattern)) continue;
                    section = sec.Name;
                    pattern = e.Pattern;
                    rawEntry = e.RawPath;
                    return e.ResolvedPath;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ result text

        internal static string KindLabel(ClarionProjectKind kind)
        {
            switch (kind)
            {
                case ClarionProjectKind.App: return "app-generated";
                case ClarionProjectKind.AppMissing: return "app-generated, .app NOT FOUND";
                default: return "source (no .app)";
            }
        }

        /// <summary>"PROJECT: ..." and "OUTPUT: ..." lines (plus notes) for one project.</summary>
        internal static string Describe(ClarionProjectInfo info)
        {
            var sb = new StringBuilder();
            if (info == null) return "";
            if (info.Error != null) { sb.AppendLine("PROJECT: " + info.Error); return sb.ToString(); }
            RefreshOutput(info);
            // NOT COMPILED on every project line: an existing, recent OUTPUT below must not read as "it built".
            sb.AppendLine("PROJECT: " + Path.GetFileName(info.CwprojPath) + " - " + KindLabel(info.Kind)
                + (info.AppPath != null ? " from " + info.AppPath : "")
                + " - NOT COMPILED by this call"
                + " [" + string.Join("; ", info.Evidence.ToArray()) + "]");
            sb.AppendLine(DescribeOutput(info));
            foreach (string n in info.Notes) sb.AppendLine("  note: " + n);
            return sb.ToString();
        }

        internal static string DescribeOutput(ClarionProjectInfo info)
        {
            if (info.OutputFile == null)
                return "OUTPUT: not resolved" + (info.OutputDirSource != null ? " (" + info.OutputDirSource + ")" : "");
            // Nothing these tools run compiles, so never word this as if the file were fresh (GH #204).
            string state = info.OutputExists
                ? "existing output, not produced by this call (last written " + info.OutputWritten.Value.ToString("yyyy-MM-dd HH:mm:ss") + ")"
                : "no such file yet (this call does not produce it)";
            return "OUTPUT: " + info.OutputFile + " - " + state
                 + " | configuration " + (info.Configuration ?? "(unknown)")
                 + (info.ConfigurationNote != null ? " (" + info.ConfigurationNote + ")" : "")
                 + " | from " + info.OutputDirSource;
        }

        /// <summary>The one line every ClarionCL result carries: /ag does not compile, so the output above is not its product.</summary>
        internal const string GenerateOnlyNote =
            "NOTE: generated, not compiled. ClarionCL /ag generates source only; it does not compile or link, so no OUTPUT "
          + "file was produced or updated by this call. Build in the IDE, or with MSBuild, to refresh it.";

        /// <summary>The result's first line for a ClarionCL /ag run: never worded as a successful BUILD.</summary>
        internal static string GenerationHeadline(bool success)
        {
            // Says outright that nothing was compiled: a recent OUTPUT file further down must not read as "it built".
            return (success ? "GENERATION SUCCEEDED" : "GENERATION FAILED")
                 + " — NOT COMPILED: no .exe/.dll was produced by this call";
        }

        /// <summary>The MSBuild command line that compiles <paramref name="info"/>'s project (the IDE's build, headless).</summary>
        internal static string MsBuildCommand(ClarionProjectInfo info, string msbuildPath, string clarionRoot)
        {
            string bin = string.IsNullOrEmpty(clarionRoot) ? "<Clarion root>\\bin" : Path.Combine(clarionRoot, "bin");
            return "\"" + (msbuildPath ?? "MSBuild.exe") + "\" \"" + info.CwprojPath + "\" /p:ClarionBinPath=\"" + bin
                 + "\" /p:Configuration=" + (info.Configuration ?? "Debug") + " /p:Platform=Win32 /v:minimal";
        }

        /// <summary>
        /// Why <paramref name="toolName"/> refuses <paramref name="info"/>'s project, or null when it can run.
        /// Refuses a Source project (nothing to generate) and an AppMissing one (the .app to generate from is gone).
        /// </summary>
        internal static string Refusal(string toolName, ClarionProjectInfo info, string msbuildPath, string clarionRoot)
        {
            if (info == null || info.Error != null || info.Kind == ClarionProjectKind.App) return null;
            var sb = new StringBuilder();
            if (info.Kind == ClarionProjectKind.Source)
            {
                sb.AppendLine("Error: " + info.ProjectName + " is a source project with no .app; nothing to generate.");
                sb.AppendLine(toolName + " runs ClarionCL /ag, which only generates source from an .app and has no compile step, "
                    + "so it cannot do anything for a hand-written project (ClarionCL would fail with \"no templates have been registered\" "
                    + "or similar, which is not a build error). build_solution is the same /ag over the whole .sln.");
            }
            else
            {
                sb.AppendLine("Error: " + info.ProjectName + "'s modules are marked generated, but no " + info.ProjectName
                    + ".app was found beside the .cwproj or in the .sln's Solution Items; nothing to generate from.");
                sb.AppendLine("Pass app_path with the .app that generates this project if it lives elsewhere.");
            }
            sb.AppendLine("To compile it, build it in the IDE, or run (e.g. through run_command):");
            sb.AppendLine("  " + MsBuildCommand(info, msbuildPath, clarionRoot));
            sb.AppendLine();
            sb.Append(Describe(info));
            return sb.ToString();
        }
    }
}
