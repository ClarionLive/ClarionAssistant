using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClarionCodeGraph.Parsing.Models;

namespace ClarionCodeGraph.Parsing
{
    /// <summary>
    /// Two-pass regex-based parser for Clarion source files.
    /// Pass 1: MAP/MODULE blocks → procedure declarations + which file they're in.
    /// Pass 2: CODE sections → routine defs, procedure calls, DO calls.
    /// </summary>
    public class ClarionParser
    {
        // Patterns relaxed to allow trailing comments, attributes, and continuation
        private static readonly Regex ProgramRegex = new Regex(
            @"^\s*PROGRAM\s*([,!].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MapStartRegex = new Regex(
            @"^\s*MAP\s*([!].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ModuleRegex = new Regex(
            @"MODULE\s*\(\s*'([^']+)'\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MapProcDeclRegex = new Regex(
            @"^\s{2,}(\w+)\s*(\([^)]*\))?\s*(,.*)?$", RegexOptions.Compiled);
        private static readonly Regex MemberRegex = new Regex(
            @"MEMBER\s*\(\s*'([^']+)'\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MemberEmptyRegex = new Regex(
            @"^\s*MEMBER\s*(\(\s*\))?\s*([!].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ProcedureDefRegex = new Regex(
            @"^([\w.]+)\s+PROCEDURE\s*(\([^)]*\))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FunctionDefRegex = new Regex(
            @"^([\w.]+)\s+FUNCTION\s*(\([^)]*\))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RoutineDefRegex = new Regex(
            @"^([\w:]+)\s+ROUTINE\s*([!].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ClassDefRegex = new Regex(
            @"^(\w+)\s+CLASS\s*(\([^)]*\))?\s*(,.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex InterfaceDefRegex = new Regex(
            @"^(\w+)\s+INTERFACE\s*(\([^)]*\))?\s*(,.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IncludeRegex = new Regex(
            @"INCLUDE\s*\(\s*'([^']+)'\s*(?:,\s*'([^']+)')?\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DoCallRegex = new Regex(
            @"\bDO\s+(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex EndRegex = new Regex(
            @"^\s*END\s*([!].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PeriodTermRegex = new Regex(
            @"^\s*\.\s*$", RegexOptions.Compiled);
        private static readonly Regex CodeRegex = new Regex(
            @"^\s*CODE\s*([!].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex OmitCompileRegex = new Regex(
            @"^\s*(OMIT|COMPILE)\s*\(\s*'([^']+)'\s*\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Variable declaration: VarName TYPE[(size)] [,attributes]
        // Matches names with colons (Loc:Name) and standard Clarion data types
        private static readonly Regex VariableDeclRegex = new Regex(
            @"^([\w:]+)\s+(BYTE|SHORT|USHORT|LONG|ULONG|SIGNED|UNSIGNED|SREAL|REAL|BFLOAT4|BFLOAT8|DECIMAL|PDECIMAL|STRING|ASTRING|CSTRING|PSTRING|DATE|TIME|BOOL|ANY)\s*(\([^)]*\))?\s*(,.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Reference variable: VarName &TYPE
        private static readonly Regex RefVariableDeclRegex = new Regex(
            @"^([\w:]+)\s+&(\w+)\s*(,.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // EQUATE constant: ConstName EQUATE(value)
        private static readonly Regex EquateDeclRegex = new Regex(
            @"^([\w:]+)\s+EQUATE\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // GROUP/QUEUE declaration: GrpName GROUP/QUEUE [,PRE(xx)]
        private static readonly Regex GroupQueueDeclRegex = new Regex(
            @"^([\w:]+)\s+(GROUP|QUEUE)\s*(,.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // LIKE declaration: VarName LIKE(OtherVar) [,attributes]
        private static readonly Regex LikeDeclRegex = new Regex(
            @"^([\w:]+)\s+LIKE\s*\(([^)]+)\)\s*(,.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // DATA section keyword
        private static readonly Regex DataRegex = new Regex(
            @"^\s*DATA\s*([!].*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // PRE attribute extractor: ,PRE(prefix)
        private static readonly Regex PreAttrRegex = new Regex(
            @"PRE\s*\(\s*(\w+)\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // CLASS/INTERFACE method prototype pattern (indented inside CLASS body)
        private static readonly Regex MethodPrototypeRegex = new Regex(
            @"^\s{2,}(\w+)\s+PROCEDURE\s*(\([^)]*\))?\s*(,.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Class/interface instance: VarName ClassName [,attributes] [!comment]
        // Catch-all for declarations where the type is not a built-in Clarion type
        private static readonly Regex ClassInstanceDeclRegex = new Regex(
            @"^([\w:]+)\s+(\w+)\s*(,[^!]*)?\s*(!.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Pass 1: Parse a main .clw file (the one with PROGRAM keyword) for MAP declarations.
        /// Returns symbols for each MODULE's procedure declarations.
        /// </summary>
        public ParseResult ParseMainFile(string filePath, int projectId)
        {
            var result = new ParseResult { FilePath = filePath };
            if (!File.Exists(filePath))
                return result;

            var lines = File.ReadAllLines(filePath);
            bool inMap = false;
            bool inModule = false;
            string currentModuleFile = null;
            int mapDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNum = i + 1;

                // Strip line continuation: join lines ending with |
                while (line.TrimEnd().EndsWith("|") && i + 1 < lines.Length)
                {
                    line = line.TrimEnd();
                    line = line.Substring(0, line.Length - 1) + " " + lines[++i].TrimStart();
                }

                // OMIT/COMPILE('terminator') — skip block
                i = SkipConditionalBlock(lines, i, line);
                if (i > lineNum - 1) { line = lines[i]; lineNum = i + 1; }

                // Detect PROGRAM keyword
                if (ProgramRegex.IsMatch(line))
                {
                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = Path.GetFileNameWithoutExtension(filePath),
                        Type = "program",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        Scope = "global"
                    });
                    continue;
                }

                // Detect MAP start
                if (MapStartRegex.IsMatch(line))
                {
                    inMap = true;
                    mapDepth = 1;
                    continue;
                }

                if (!inMap) continue;

                // Track END statements and period terminators for MAP/MODULE nesting
                if (EndRegex.IsMatch(line) || PeriodTermRegex.IsMatch(line))
                {
                    if (inModule)
                    {
                        inModule = false;
                        currentModuleFile = null;
                    }
                    else
                    {
                        mapDepth--;
                        if (mapDepth <= 0)
                        {
                            inMap = false;
                        }
                    }
                    continue;
                }

                // Detect MODULE('filename.clw')
                var moduleMatch = ModuleRegex.Match(line);
                if (moduleMatch.Success)
                {
                    inModule = true;
                    currentModuleFile = moduleMatch.Groups[1].Value;

                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = currentModuleFile,
                        Type = "module",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        Scope = "global"
                    });
                    continue;
                }

                // Inside MODULE block: each indented line is a procedure/function declaration
                if (inModule && currentModuleFile != null)
                {
                    var procMatch = MapProcDeclRegex.Match(line);
                    if (procMatch.Success)
                    {
                        string procName = procMatch.Groups[1].Value;
                        string procParams = procMatch.Groups[2].Success ? procMatch.Groups[2].Value : null;
                        string attributes = procMatch.Groups[3].Success ? procMatch.Groups[3].Value : "";

                        // Skip Clarion keywords and built-ins
                        if (ClarionBuiltins.IsBuiltInOrKeyword(procName)) continue;

                        // Determine if it's a function (has return type in attributes)
                        bool isFunction = !string.IsNullOrEmpty(attributes) &&
                                          attributes.IndexOf(",", StringComparison.Ordinal) >= 0 &&
                                          ExtractReturnType(attributes) != null;

                        result.Symbols.Add(new ClarionSymbol
                        {
                            Name = procName,
                            Type = isFunction ? "function" : "procedure",
                            FilePath = filePath,
                            LineNumber = lineNum,
                            ProjectId = projectId,
                            Params = procParams,
                            ReturnType = isFunction ? ExtractReturnType(attributes) : null,
                            MemberOf = currentModuleFile,
                            Scope = "global"
                        });
                    }
                }

                // Detect INCLUDE statements in MAP
                var includeMatch = IncludeRegex.Match(line);
                if (includeMatch.Success)
                {
                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = includeMatch.Groups[1].Value,
                        Type = "include",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        Scope = "global"
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Pass 2: Parse a MEMBER .clw file for procedure/routine definitions and calls.
        /// </summary>
        public ParseResult ParseMemberFile(string filePath, int projectId, HashSet<string> knownProcedures)
        {
            var result = new ParseResult { FilePath = filePath };
            if (!File.Exists(filePath))
                return result;

            var lines = File.ReadAllLines(filePath);
            string memberOf = null;
            string currentProcedure = null;
            bool inCode = false;
            bool inData = false; // True when between PROCEDURE def and CODE keyword
            int dataGroupDepth = 0; // Track nested GROUP/QUEUE/RECORD in DATA sections
            var localRoutines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Track CLASS bodies to extract method prototypes
            string currentClassName = null;
            bool inClassBody = false;
            int classEndDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNum = i + 1;

                // Strip line continuation
                while (line.TrimEnd().EndsWith("|") && i + 1 < lines.Length)
                {
                    line = line.TrimEnd();
                    line = line.Substring(0, line.Length - 1) + " " + lines[++i].TrimStart();
                }

                // OMIT/COMPILE('terminator') — skip block
                int newI = SkipConditionalBlock(lines, i, line);
                if (newI > i) { i = newI; continue; }

                // Detect MEMBER('parent.clw') or MEMBER()
                var memberMatch = MemberRegex.Match(line);
                if (memberMatch.Success)
                {
                    memberOf = memberMatch.Groups[1].Value;
                    inData = true; // module-level DATA section starts after MEMBER
                    dataGroupDepth = 0;
                    continue;
                }
                if (MemberEmptyRegex.IsMatch(line) && memberOf == null)
                {
                    memberOf = ""; // universal member
                    inData = true;
                    dataGroupDepth = 0;
                    continue;
                }

                // Inside CLASS body: extract method prototypes
                if (inClassBody)
                {
                    if (EndRegex.IsMatch(line) || PeriodTermRegex.IsMatch(line))
                    {
                        classEndDepth--;
                        if (classEndDepth <= 0)
                        {
                            inClassBody = false;
                            currentClassName = null;
                        }
                        continue;
                    }

                    // Method prototype inside CLASS: indented "MethodName PROCEDURE(...)"
                    var methodMatch = MethodPrototypeRegex.Match(line);
                    if (methodMatch.Success && currentClassName != null)
                    {
                        string methodName = methodMatch.Groups[1].Value;
                        if (!ClarionBuiltins.IsBuiltInOrKeyword(methodName))
                        {
                            string fullName = currentClassName + "." + methodName;
                            string methodParams = methodMatch.Groups[2].Success ? methodMatch.Groups[2].Value : null;
                            string attributes = methodMatch.Groups[3].Success ? methodMatch.Groups[3].Value : "";

                            bool isVirtual = attributes.IndexOf("VIRTUAL", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isDerived = attributes.IndexOf("DERIVED", StringComparison.OrdinalIgnoreCase) >= 0;

                            result.Symbols.Add(new ClarionSymbol
                            {
                                Name = fullName,
                                Type = "procedure",
                                FilePath = filePath,
                                LineNumber = lineNum,
                                ProjectId = projectId,
                                Params = methodParams,
                                ReturnType = ExtractReturnType(attributes),
                                MemberOf = memberOf,
                                ParentName = currentClassName,
                                Scope = isVirtual || isDerived ? "virtual" : "class"
                            });
                        }
                    }
                    continue;
                }

                // Detect PROCEDURE definition
                var procMatch = ProcedureDefRegex.Match(line);
                if (procMatch.Success)
                {
                    currentProcedure = procMatch.Groups[1].Value;
                    inCode = false;
                    inData = true;
                    dataGroupDepth = 0;
                    localRoutines.Clear();
                    string procParams = procMatch.Groups[2].Success ? procMatch.Groups[2].Value : null;

                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = currentProcedure,
                        Type = "procedure",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        Params = procParams,
                        MemberOf = memberOf,
                        Scope = "module"
                    });
                    continue;
                }

                // Detect FUNCTION definition
                var funcMatch = FunctionDefRegex.Match(line);
                if (funcMatch.Success)
                {
                    currentProcedure = funcMatch.Groups[1].Value;
                    inCode = false;
                    inData = true;
                    dataGroupDepth = 0;
                    localRoutines.Clear();
                    string funcParams = funcMatch.Groups[2].Success ? funcMatch.Groups[2].Value : null;

                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = currentProcedure,
                        Type = "function",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        Params = funcParams,
                        MemberOf = memberOf,
                        Scope = "module"
                    });
                    continue;
                }

                // Detect ROUTINE definition
                var routineMatch = RoutineDefRegex.Match(line);
                if (routineMatch.Success)
                {
                    string routineName = routineMatch.Groups[1].Value;
                    localRoutines.Add(routineName);
                    inCode = false;

                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = routineName,
                        Type = "routine",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        MemberOf = memberOf,
                        Scope = "local"
                    });
                    continue;
                }

                // Detect CLASS definition
                var classMatch = ClassDefRegex.Match(line);
                if (classMatch.Success)
                {
                    string className = classMatch.Groups[1].Value;
                    string parentClass = classMatch.Groups[2].Success
                        ? classMatch.Groups[2].Value.Trim('(', ')', ' ')
                        : null;

                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = className,
                        Type = "class",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        ParentName = parentClass,
                        Scope = "global"
                    });

                    // Enter CLASS body to extract method prototypes
                    currentClassName = className;
                    inClassBody = true;
                    classEndDepth = 1;
                    continue;
                }

                // Detect INTERFACE definition
                var ifaceMatch = InterfaceDefRegex.Match(line);
                if (ifaceMatch.Success)
                {
                    string ifaceName = ifaceMatch.Groups[1].Value;
                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = ifaceName,
                        Type = "interface",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        Scope = "global"
                    });

                    // Enter INTERFACE body to extract method prototypes
                    currentClassName = ifaceName;
                    inClassBody = true;
                    classEndDepth = 1;
                    continue;
                }

                // Detect CODE section start
                if (CodeRegex.IsMatch(line))
                {
                    inCode = true;
                    inData = false;
                    dataGroupDepth = 0;
                    continue;
                }

                // Detect INCLUDE
                var includeMatch = IncludeRegex.Match(line);
                if (includeMatch.Success)
                {
                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = includeMatch.Groups[1].Value,
                        Type = "include",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId
                    });
                }

                // In DATA section: scan for variable declarations
                if (inData)
                {
                    string trimmedData = line.TrimStart();
                    if (trimmedData.StartsWith("!")) continue; // comment line

                    // Track END for GROUP/QUEUE nesting
                    if (dataGroupDepth > 0 && (EndRegex.IsMatch(line) || PeriodTermRegex.IsMatch(line)))
                    {
                        dataGroupDepth--;
                        continue;
                    }

                    // Skip lines inside GROUP/QUEUE bodies (member fields)
                    if (dataGroupDepth > 0) continue;

                    // Skip MAP, WINDOW, REPORT, TOOLBAR, MENUBAR blocks inside DATA sections
                    if (MapStartRegex.IsMatch(line) ||
                        Regex.IsMatch(trimmedData, @"^\w+\s+(WINDOW|REPORT|TOOLBAR|MENUBAR|FILE|VIEW)\b", RegexOptions.IgnoreCase))
                    {
                        dataGroupDepth++;
                        continue;
                    }

                    // Determine scope: module-level (before first PROCEDURE) vs local
                    string varScope = currentProcedure != null ? "local" : "module";
                    string varOwner = currentProcedure;

                    // GROUP/QUEUE declaration
                    var gqMatch = GroupQueueDeclRegex.Match(trimmedData);
                    if (gqMatch.Success)
                    {
                        string gqName = gqMatch.Groups[1].Value;
                        string gqType = gqMatch.Groups[2].Value.ToUpperInvariant();
                        string gqAttrs = gqMatch.Groups[3].Success ? gqMatch.Groups[3].Value : "";

                        // Extract PRE() attribute if present
                        string prefix = null;
                        var preMatch = PreAttrRegex.Match(gqAttrs);
                        if (preMatch.Success)
                            prefix = preMatch.Groups[1].Value;

                        result.Symbols.Add(new ClarionSymbol
                        {
                            Name = gqName,
                            Type = "variable",
                            FilePath = filePath,
                            LineNumber = lineNum,
                            ProjectId = projectId,
                            Params = gqType + (prefix != null ? ",PRE(" + prefix + ")" : ""),
                            ParentName = varOwner,
                            Scope = varScope
                        });
                        dataGroupDepth++;
                        continue;
                    }

                    // Simple variable declaration: VarName TYPE[(size)]
                    var varMatch = VariableDeclRegex.Match(trimmedData);
                    if (varMatch.Success)
                    {
                        string varName = varMatch.Groups[1].Value;
                        string varType = varMatch.Groups[2].Value.ToUpperInvariant();
                        string varSize = varMatch.Groups[3].Success ? varMatch.Groups[3].Value : "";

                        result.Symbols.Add(new ClarionSymbol
                        {
                            Name = varName,
                            Type = "variable",
                            FilePath = filePath,
                            LineNumber = lineNum,
                            ProjectId = projectId,
                            Params = varType + varSize,
                            ParentName = varOwner,
                            Scope = varScope
                        });
                        continue;
                    }

                    // Reference variable: VarName &TYPE
                    var refMatch = RefVariableDeclRegex.Match(trimmedData);
                    if (refMatch.Success)
                    {
                        string refName = refMatch.Groups[1].Value;
                        string refType = refMatch.Groups[2].Value;

                        result.Symbols.Add(new ClarionSymbol
                        {
                            Name = refName,
                            Type = "variable",
                            FilePath = filePath,
                            LineNumber = lineNum,
                            ProjectId = projectId,
                            Params = "&" + refType.ToUpperInvariant(),
                            ParentName = varOwner,
                            Scope = varScope
                        });
                        continue;
                    }

                    // EQUATE constant
                    var eqMatch = EquateDeclRegex.Match(trimmedData);
                    if (eqMatch.Success)
                    {
                        string eqName = eqMatch.Groups[1].Value;

                        result.Symbols.Add(new ClarionSymbol
                        {
                            Name = eqName,
                            Type = "variable",
                            FilePath = filePath,
                            LineNumber = lineNum,
                            ProjectId = projectId,
                            Params = "EQUATE",
                            ParentName = varOwner,
                            Scope = varScope
                        });
                        continue;
                    }

                    // LIKE declaration
                    var likeMatch = LikeDeclRegex.Match(trimmedData);
                    if (likeMatch.Success)
                    {
                        string likeName = likeMatch.Groups[1].Value;
                        string likeTarget = likeMatch.Groups[2].Value;

                        result.Symbols.Add(new ClarionSymbol
                        {
                            Name = likeName,
                            Type = "variable",
                            FilePath = filePath,
                            LineNumber = lineNum,
                            ProjectId = projectId,
                            Params = "LIKE(" + likeTarget + ")",
                            ParentName = varOwner,
                            Scope = varScope
                        });
                        continue;
                    }

                    // Class/interface instance: VarName ClassName [,attributes]
                    // Catch-all after all specific type matchers — captures MyObj SomeClass
                    var classInstMatch = ClassInstanceDeclRegex.Match(trimmedData);
                    if (classInstMatch.Success)
                    {
                        string ciName = classInstMatch.Groups[1].Value;
                        string ciType = classInstMatch.Groups[2].Value;

                        // Only capture if type is not a built-in type or keyword
                        if (!ClarionBuiltins.IsBuiltInOrKeyword(ciType) &&
                            !ClarionBuiltins.IsClarionType(ciType))
                        {
                            result.Symbols.Add(new ClarionSymbol
                            {
                                Name = ciName,
                                Type = "variable",
                                FilePath = filePath,
                                LineNumber = lineNum,
                                ProjectId = projectId,
                                Params = ciType.ToUpperInvariant(),
                                ParentName = varOwner,
                                Scope = varScope
                            });
                        }
                        continue;
                    }
                }

                // In CODE section: scan for calls
                if (inCode && currentProcedure != null)
                {
                    // Skip comment lines
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("!")) continue;
                    // Strip inline comments
                    string codePart = StripInlineComment(trimmed);

                    // Detect DO RoutineName (routine calls)
                    var doMatch = DoCallRegex.Match(codePart);
                    if (doMatch.Success)
                    {
                        string routineName = doMatch.Groups[1].Value;
                        StoreCallReference(result, currentProcedure, routineName, "do", filePath, lineNum);
                    }

                    // Detect procedure calls (known procedure names appearing in code)
                    if (knownProcedures != null)
                    {
                        foreach (string procName in knownProcedures)
                        {
                            if (string.Equals(procName, currentProcedure, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (LineContainsCall(codePart, procName))
                            {
                                StoreCallReference(result, currentProcedure, procName, "calls", filePath, lineNum);
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Parse a .inc file for CLASS, INTERFACE, and method prototype definitions.
        /// </summary>
        public ParseResult ParseIncFile(string filePath, int projectId)
        {
            var result = new ParseResult { FilePath = filePath };
            if (!File.Exists(filePath))
                return result;

            var lines = File.ReadAllLines(filePath);
            string currentClassName = null;
            bool inClassBody = false;
            int classEndDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int lineNum = i + 1;

                // Strip line continuation
                while (line.TrimEnd().EndsWith("|") && i + 1 < lines.Length)
                {
                    line = line.TrimEnd();
                    line = line.Substring(0, line.Length - 1) + " " + lines[++i].TrimStart();
                }

                // OMIT/COMPILE('terminator') — skip block
                int newI = SkipConditionalBlock(lines, i, line);
                if (newI > i) { i = newI; continue; }

                // Inside CLASS/INTERFACE body: extract method prototypes
                if (inClassBody)
                {
                    if (EndRegex.IsMatch(line) || PeriodTermRegex.IsMatch(line))
                    {
                        classEndDepth--;
                        if (classEndDepth <= 0)
                        {
                            inClassBody = false;
                            currentClassName = null;
                        }
                        continue;
                    }

                    // Nested END for inner GROUP/QUEUE etc.
                    if (Regex.IsMatch(line.TrimStart(), @"^\w+\s+(GROUP|QUEUE|RECORD)\b", RegexOptions.IgnoreCase))
                    {
                        classEndDepth++;
                        continue;
                    }

                    // Method prototype inside CLASS/INTERFACE
                    var methodMatch = MethodPrototypeRegex.Match(line);
                    if (methodMatch.Success && currentClassName != null)
                    {
                        string methodName = methodMatch.Groups[1].Value;
                        if (!ClarionBuiltins.IsBuiltInOrKeyword(methodName))
                        {
                            string fullName = currentClassName + "." + methodName;
                            string methodParams = methodMatch.Groups[2].Success ? methodMatch.Groups[2].Value : null;
                            string attributes = methodMatch.Groups[3].Success ? methodMatch.Groups[3].Value : "";

                            bool isVirtual = attributes.IndexOf("VIRTUAL", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isDerived = attributes.IndexOf("DERIVED", StringComparison.OrdinalIgnoreCase) >= 0;

                            result.Symbols.Add(new ClarionSymbol
                            {
                                Name = fullName,
                                Type = "procedure",
                                FilePath = filePath,
                                LineNumber = lineNum,
                                ProjectId = projectId,
                                Params = methodParams,
                                ReturnType = ExtractReturnType(attributes),
                                ParentName = currentClassName,
                                Scope = isVirtual || isDerived ? "virtual" : "class"
                            });
                        }
                    }
                    continue;
                }

                var classMatch = ClassDefRegex.Match(line);
                if (classMatch.Success)
                {
                    string className = classMatch.Groups[1].Value;
                    string parentClass = classMatch.Groups[2].Success
                        ? classMatch.Groups[2].Value.Trim('(', ')', ' ')
                        : null;

                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = className,
                        Type = "class",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        ParentName = parentClass,
                        Scope = "global"
                    });

                    currentClassName = className;
                    inClassBody = true;
                    classEndDepth = 1;
                    continue;
                }

                var ifaceMatch = InterfaceDefRegex.Match(line);
                if (ifaceMatch.Success)
                {
                    string ifaceName = ifaceMatch.Groups[1].Value;
                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = ifaceName,
                        Type = "interface",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId,
                        Scope = "global"
                    });

                    currentClassName = ifaceName;
                    inClassBody = true;
                    classEndDepth = 1;
                    continue;
                }

                var includeMatch = IncludeRegex.Match(line);
                if (includeMatch.Success)
                {
                    result.Symbols.Add(new ClarionSymbol
                    {
                        Name = includeMatch.Groups[1].Value,
                        Type = "include",
                        FilePath = filePath,
                        LineNumber = lineNum,
                        ProjectId = projectId
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Skip OMIT('term') or COMPILE('term', FALSE) blocks.
        /// Returns the updated line index (past the terminator).
        /// </summary>
        private int SkipConditionalBlock(string[] lines, int currentIndex, string currentLine)
        {
            var match = OmitCompileRegex.Match(currentLine);
            if (!match.Success) return currentIndex;

            string directive = match.Groups[1].Value.ToUpperInvariant();
            string terminator = match.Groups[2].Value;

            // OMIT always skips; COMPILE with a false expression skips
            // For simplicity, always skip OMIT blocks.
            // For COMPILE, we'd need expression evaluation — skip for now and include the code.
            if (directive == "COMPILE") return currentIndex;

            int i = currentIndex + 1;
            while (i < lines.Length)
            {
                if (lines[i].TrimStart().StartsWith(terminator, StringComparison.Ordinal))
                    return i;
                i++;
            }
            return i - 1; // EOF reached
        }

        /// <summary>
        /// Strip inline comment (everything after ! that isn't inside a quoted string).
        /// </summary>
        private string StripInlineComment(string line)
        {
            bool inString = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\'')
                    inString = !inString;
                else if (line[i] == '!' && !inString)
                    return line.Substring(0, i);
            }
            return line;
        }

        private void StoreCallReference(ParseResult result, string caller, string callee, string type, string filePath, int lineNum)
        {
            result.Relationships.Add(new ClarionRelationship
            {
                FromId = caller.GetHashCode(),
                ToId = callee.GetHashCode(),
                Type = type,
                FilePath = filePath,
                LineNumber = lineNum
            });
        }

        private bool LineContainsCall(string line, string procName)
        {
            int idx = line.IndexOf(procName, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            // Check word boundary before
            if (idx > 0 && (char.IsLetterOrDigit(line[idx - 1]) || line[idx - 1] == '_'))
                return false;

            // Check word boundary after
            int afterIdx = idx + procName.Length;
            if (afterIdx < line.Length && (char.IsLetterOrDigit(line[afterIdx]) || line[afterIdx] == '_'))
                return false;

            return true;
        }

        private string ExtractReturnType(string attributes)
        {
            if (string.IsNullOrEmpty(attributes)) return null;

            string[] parts = attributes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (ClarionBuiltins.IsClarionType(trimmed))
                    return trimmed.TrimStart('*', '&').ToUpperInvariant();
            }
            return null;
        }
    }
}
