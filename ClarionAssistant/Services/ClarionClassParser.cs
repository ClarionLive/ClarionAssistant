using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Represents a parsed method declaration from a CLASS definition.
    /// </summary>
    public class MethodDeclaration
    {
        public string Name { get; set; }
        public string Keyword { get; set; }          // PROCEDURE or FUNCTION
        public string Params { get; set; }            // e.g., "(STRING s)" or ""
        public string ReturnType { get; set; }        // e.g., ",BYTE" or ""
        public List<string> Attributes { get; set; }  // e.g., VIRTUAL, PROC, PROTECTED
        public string FullSignature { get; set; }     // e.g., "PROCEDURE(STRING s),BYTE"
        public int LineNumber { get; set; }
        public string RawLine { get; set; }           // Original line from .inc

        public MethodDeclaration()
        {
            Attributes = new List<string>();
        }

        /// <summary>
        /// Returns a unique key for this method (name + param signature) to handle overloads.
        /// </summary>
        public string OverloadKey
        {
            get { return $"{Name.ToLower()}|{Params.ToLower()}"; }
        }
    }

    /// <summary>
    /// Represents a parsed CLASS definition from an .inc file.
    /// </summary>
    public class ClassDefinition
    {
        public string ClassName { get; set; }
        public string ModuleFile { get; set; }        // From MODULE('...') attribute
        public string ParentClass { get; set; }       // From CLASS(ParentName)
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public List<MethodDeclaration> Methods { get; set; }
        public List<string> DataMembers { get; set; } // Property/data lines
        public string RawDeclarationLine { get; set; }

        public ClassDefinition()
        {
            Methods = new List<MethodDeclaration>();
            DataMembers = new List<string>();
        }
    }

    /// <summary>
    /// Represents an existing method implementation found in a .clw file.
    /// </summary>
    public class MethodImplementation
    {
        public string ClassName { get; set; }
        public string MethodName { get; set; }
        public string Signature { get; set; }
        public int LineNumber { get; set; }

        public string OverloadKey
        {
            get
            {
                // Extract params from signature for overload matching
                var match = Regex.Match(Signature, @"\(([^)]*)\)", RegexOptions.IgnoreCase);
                string parms = match.Success ? $"({match.Groups[1].Value})" : "";
                return $"{MethodName.ToLower()}|{parms.ToLower()}";
            }
        }
    }

    /// <summary>
    /// Result of comparing .inc declarations with .clw implementations.
    /// </summary>
    public class SyncResult
    {
        public string ClassName { get; set; }
        public string IncFile { get; set; }
        public string ClwFile { get; set; }
        public List<MethodDeclaration> MissingImplementations { get; set; }
        public List<MethodImplementation> OrphanedImplementations { get; set; }
        public List<MethodDeclaration> ImplementedMethods { get; set; }

        public SyncResult()
        {
            MissingImplementations = new List<MethodDeclaration>();
            OrphanedImplementations = new List<MethodImplementation>();
            ImplementedMethods = new List<MethodDeclaration>();
        }

        public bool IsInSync => MissingImplementations.Count == 0 && OrphanedImplementations.Count == 0;
    }

    /// <summary>
    /// Parses Clarion CLASS definitions from .inc files and method implementations from .clw files.
    /// </summary>
    public class ClarionClassParser
    {
        // Known data types that should not be treated as method names
        private static readonly HashSet<string> DataTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "byte", "short", "ushort", "long", "ulong", "real", "sreal",
            "string", "cstring", "pstring", "astring", "bstring",
            "decimal", "pdecimal", "date", "time", "group", "queue",
            "file", "record", "window", "report", "view", "blob", "memo",
            "any", "like", "class", "interface", "end", "map", "module",
            "include", "member", "itemize", "equate", "unsigned"
        };

        // Known method attributes (declaration-only, not repeated in implementation)
        private static readonly HashSet<string> MethodAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VIRTUAL", "PROC", "PROTECTED", "PRIVATE", "DERIVED", "FINAL"
        };

        private static readonly Regex ClassStartPattern = new Regex(
            @"^(\w+)\s+CLASS\b", RegexOptions.IgnoreCase);

        private static readonly Regex MethodPattern = new Regex(
            @"^(\w+)\s+(PROCEDURE|FUNCTION)\s*(\([^)]*\))?\s*(.*)?$", RegexOptions.IgnoreCase);

        private static readonly Regex ImplPattern = new Regex(
            @"^(\w+)\.(\w+)\s+(PROCEDURE|FUNCTION)\s*(.*)?$", RegexOptions.IgnoreCase);

        private static readonly Regex ModulePattern = new Regex(
            @"MODULE\s*\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.IgnoreCase);

        private static readonly Regex ParentPattern = new Regex(
            @"CLASS\s*\(\s*(\w+)\s*\)", RegexOptions.IgnoreCase);

        #region Parse .inc files

        /// <summary>
        /// Parse all CLASS definitions from an .inc file.
        /// </summary>
        public List<ClassDefinition> ParseIncFile(string filePath)
        {
            if (!File.Exists(filePath)) return new List<ClassDefinition>();
            string content = EncodingHelper.ReadAllText(filePath, out _);
            return ParseIncContent(content, filePath);
        }

        /// <summary>
        /// Parse all CLASS definitions from .inc file content.
        /// </summary>
        public List<ClassDefinition> ParseIncContent(string content, string sourceFile = null)
        {
            var classes = new List<ClassDefinition>();
            var lines = content.Split(new[] { '\n' }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var classMatch = ClassStartPattern.Match(line);

                if (!classMatch.Success) continue;

                var classDef = new ClassDefinition
                {
                    ClassName = classMatch.Groups[1].Value,
                    StartLine = i,
                    RawDeclarationLine = line.TrimEnd()
                };

                // Extract MODULE file
                var moduleMatch = ModulePattern.Match(line);
                classDef.ModuleFile = moduleMatch.Success
                    ? moduleMatch.Groups[1].Value
                    : $"{classDef.ClassName}.clw";

                // Extract parent class
                var parentMatch = ParentPattern.Match(line);
                if (parentMatch.Success)
                    classDef.ParentClass = parentMatch.Groups[1].Value;

                // Parse class body until END, tracking nested structure depth
                // CLASSes can contain GROUP, QUEUE, RECORD, etc. with their own END statements
                int j = i + 1;
                int nestingDepth = 0;
                while (j < lines.Length)
                {
                    string currentLine = lines[j].Trim();

                    // Track nested structures (GROUP, QUEUE, RECORD, etc.)
                    if (nestingDepth == 0)
                    {
                        // Only check for nested structure starts when not already nested
                        if (Regex.IsMatch(currentLine, @"\b(GROUP|QUEUE|RECORD|VIEW|FILE)\b", RegexOptions.IgnoreCase) &&
                            !Regex.IsMatch(currentLine, @"^(\w+)\s+(PROCEDURE|FUNCTION)", RegexOptions.IgnoreCase) &&
                            !currentLine.StartsWith("!"))
                        {
                            nestingDepth++;
                        }
                    }
                    else
                    {
                        // Inside a nested structure, look for more nesting or END
                        if (Regex.IsMatch(currentLine, @"\b(GROUP|QUEUE|RECORD|VIEW|FILE)\b", RegexOptions.IgnoreCase) &&
                            !currentLine.StartsWith("!"))
                        {
                            nestingDepth++;
                        }
                    }

                    // Check for END
                    if (Regex.IsMatch(currentLine, @"^END\b", RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(currentLine, @"^end!", RegexOptions.IgnoreCase))
                    {
                        if (nestingDepth > 0)
                        {
                            // This END closes a nested structure, not the CLASS
                            nestingDepth--;
                            j++;
                            continue;
                        }
                        classDef.EndLine = j;
                        break;
                    }

                    // Check for next CLASS definition
                    if (ClassStartPattern.IsMatch(lines[j]))
                    {
                        classDef.EndLine = j - 1;
                        break;
                    }

                    // Try parsing as method declaration
                    var method = ParseMethodDeclaration(currentLine, j);
                    if (method != null)
                    {
                        classDef.Methods.Add(method);
                    }
                    else if (!string.IsNullOrWhiteSpace(currentLine) &&
                             !currentLine.StartsWith("!") &&
                             !currentLine.StartsWith("OMIT"))
                    {
                        // Likely a data member
                        classDef.DataMembers.Add(currentLine);
                    }

                    j++;
                }

                if (classDef.EndLine == 0) classDef.EndLine = j;

                classes.Add(classDef);
                i = j - 1; // Skip past this class
            }

            return classes;
        }

        /// <summary>
        /// Parse a single line as a method declaration.
        /// </summary>
        private MethodDeclaration ParseMethodDeclaration(string line, int lineNumber)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("!") || line.StartsWith("OMIT"))
                return null;

            var match = MethodPattern.Match(line);
            if (!match.Success) return null;

            string name = match.Groups[1].Value;
            if (DataTypes.Contains(name)) return null;

            string keyword = match.Groups[2].Value;
            string parms = match.Groups[3].Success ? match.Groups[3].Value : "";
            string remainder = match.Groups[4].Success ? match.Groups[4].Value.Trim() : "";

            string returnType = "";
            var attributes = new List<string>();

            if (!string.IsNullOrEmpty(remainder))
            {
                // Remove trailing comments
                string noComment = Regex.Replace(remainder, @"\s*!.*$", "");
                var parts = noComment.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                foreach (var part in parts)
                {
                    string upper = part.ToUpper();
                    if (MethodAttributes.Contains(upper))
                    {
                        attributes.Add(upper);
                    }
                    else if (upper.StartsWith("IMPLEMENTS"))
                    {
                        // Skip — not a method attribute
                    }
                    else if (string.IsNullOrEmpty(returnType) &&
                             Regex.IsMatch(part, @"^[A-Z]+$", RegexOptions.IgnoreCase) &&
                             !part.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase) &&
                             !part.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase))
                    {
                        returnType = "," + part;
                    }
                }
            }

            return new MethodDeclaration
            {
                Name = name,
                Keyword = keyword,
                Params = parms,
                ReturnType = returnType,
                Attributes = attributes,
                FullSignature = $"{keyword}{parms}{returnType}",
                LineNumber = lineNumber,
                RawLine = line
            };
        }

        #endregion

        #region Parse .clw files

        /// <summary>
        /// Parse all method implementations from a .clw file.
        /// </summary>
        public List<MethodImplementation> ParseClwFile(string filePath)
        {
            if (!File.Exists(filePath)) return new List<MethodImplementation>();
            string content = EncodingHelper.ReadAllText(filePath, out _);
            return ParseClwContent(content);
        }

        /// <summary>
        /// Parse method implementations from .clw content.
        /// </summary>
        public List<MethodImplementation> ParseClwContent(string content)
        {
            var implementations = new List<MethodImplementation>();
            var lines = content.Split(new[] { '\n' }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                var match = ImplPattern.Match(lines[i].Trim());
                if (match.Success)
                {
                    implementations.Add(new MethodImplementation
                    {
                        ClassName = match.Groups[1].Value,
                        MethodName = match.Groups[2].Value,
                        Signature = match.Groups[3].Value + (match.Groups[4].Success ? match.Groups[4].Value : ""),
                        LineNumber = i
                    });
                }
            }

            return implementations;
        }

        /// <summary>
        /// Get implementations for a specific class from a .clw file.
        /// </summary>
        public List<MethodImplementation> GetImplementationsForClass(string clwPath, string className)
        {
            return ParseClwFile(clwPath)
                .Where(m => m.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        #endregion

        #region Sync Check

        /// <summary>
        /// Compare method declarations in .inc with implementations in .clw.
        /// Returns missing and orphaned methods.
        /// </summary>
        public SyncResult CompareIncWithClw(string incPath, string clwPath, string className = null)
        {
            var classes = ParseIncFile(incPath);
            var implementations = ParseClwFile(clwPath);

            // If className specified, filter to that class; otherwise use first class
            var classDef = className != null
                ? classes.FirstOrDefault(c => c.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
                : classes.FirstOrDefault();

            if (classDef == null)
            {
                return new SyncResult
                {
                    IncFile = incPath,
                    ClwFile = clwPath,
                    ClassName = className ?? "(no class found)"
                };
            }

            var classImpls = implementations
                .Where(m => m.ClassName.Equals(classDef.ClassName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var implKeys = new HashSet<string>(classImpls.Select(m => m.MethodName.ToLower()));

            var result = new SyncResult
            {
                ClassName = classDef.ClassName,
                IncFile = incPath,
                ClwFile = clwPath
            };

            // Find missing implementations
            foreach (var method in classDef.Methods)
            {
                if (implKeys.Contains(method.Name.ToLower()))
                {
                    result.ImplementedMethods.Add(method);
                }
                else
                {
                    result.MissingImplementations.Add(method);
                }
            }

            // Find orphaned implementations (in .clw but not in .inc)
            var declaredNames = new HashSet<string>(
                classDef.Methods.Select(m => m.Name.ToLower()));

            foreach (var impl in classImpls)
            {
                if (!declaredNames.Contains(impl.MethodName.ToLower()))
                {
                    result.OrphanedImplementations.Add(impl);
                }
            }

            return result;
        }

        #endregion

        #region Stub Generation

        /// <summary>
        /// Generate a method implementation stub for a single method.
        /// </summary>
        public string GenerateMethodStub(string className, MethodDeclaration method)
        {
            string label = $"{className}.{method.Name}";
            int padding = Math.Max(1, 40 - label.Length);

            // Implementation signature: PROCEDURE(params) only
            // Return type and attributes (VIRTUAL, PROTECTED, etc.) are declaration-only
            string implSignature = $"{method.Keyword}{method.Params}";

            return $"{label}{new string(' ', padding)}{implSignature}\r\n\r\n  CODE\r\n";
        }

        /// <summary>
        /// Generate stubs for all missing methods.
        /// </summary>
        public string GenerateAllMissingStubs(SyncResult syncResult)
        {
            if (syncResult.MissingImplementations.Count == 0) return "";

            var stubs = syncResult.MissingImplementations
                .Select(m => GenerateMethodStub(syncResult.ClassName, m));

            return string.Join("\r\n", stubs);
        }

        /// <summary>
        /// Generate a complete .clw implementation file for a class.
        /// Includes MEMBER, INCLUDE, MAP, and all method stubs.
        /// </summary>
        public string GenerateClwFile(ClassDefinition classDef)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("  MEMBER()");
            sb.AppendLine();
            sb.AppendLine($"  INCLUDE('{Path.GetFileNameWithoutExtension(classDef.ModuleFile)}.inc'),ONCE");
            sb.AppendLine();
            sb.AppendLine("  MAP");
            sb.AppendLine("  END");
            sb.AppendLine();

            foreach (var method in classDef.Methods)
            {
                sb.Append(GenerateMethodStub(classDef.ClassName, method));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        #endregion

        #region File Resolution

        /// <summary>
        /// Resolve the .clw file path from an .inc file path using the MODULE attribute.
        /// </summary>
        public string ResolveClwPath(string incPath, ClassDefinition classDef = null)
        {
            string dir = Path.GetDirectoryName(incPath);
            string moduleFile = classDef?.ModuleFile;

            if (!string.IsNullOrEmpty(moduleFile))
            {
                string clwPath = Path.Combine(dir, moduleFile);
                if (File.Exists(clwPath)) return clwPath;
            }

            // Fallback: same name as .inc but with .clw
            string baseName = Path.GetFileNameWithoutExtension(incPath);
            string fallback = Path.Combine(dir, baseName + ".clw");
            if (File.Exists(fallback)) return fallback;

            // Return expected path even if doesn't exist
            return !string.IsNullOrEmpty(moduleFile)
                ? Path.Combine(dir, moduleFile)
                : fallback;
        }

        /// <summary>
        /// Find the .inc file referenced by a .clw file's INCLUDE statement.
        /// </summary>
        public string FindIncFromClw(string clwPath)
        {
            if (!File.Exists(clwPath)) return null;

            string content = EncodingHelper.ReadAllText(clwPath, out _);
            string dir = Path.GetDirectoryName(clwPath);
            string clwBaseName = Path.GetFileNameWithoutExtension(clwPath).ToLower();

            // Find ALL INCLUDE('*.inc') statements
            var matches = Regex.Matches(content, @"INCLUDE\s*\(\s*['""]([^'""]+\.inc)['""]\s*\)", RegexOptions.IgnoreCase);

            // Strategy 1: Find an .inc whose name matches the .clw filename
            // e.g., UltimateDebug.clw → UltimateDebug.INC
            foreach (Match match in matches)
            {
                string incFile = match.Groups[1].Value;
                string incBaseName = Path.GetFileNameWithoutExtension(incFile).ToLower();
                if (incBaseName == clwBaseName)
                {
                    string incPath = Path.Combine(dir, incFile);
                    if (File.Exists(incPath)) return incPath;
                }
            }

            // Strategy 2: Find any .inc that exists in the same directory
            foreach (Match match in matches)
            {
                string incFile = match.Groups[1].Value;
                string incPath = Path.Combine(dir, incFile);
                if (File.Exists(incPath)) return incPath;
            }

            return null;
        }

        #endregion
    }
}
