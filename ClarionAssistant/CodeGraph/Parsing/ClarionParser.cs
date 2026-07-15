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
            @"^\s*(OMIT|COMPILE)\s*\(\s*'([^']+)'\s*(?:,\s*([^)]+?)\s*)?\)",
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

        // GROUP/QUEUE declaration: GrpName GROUP/QUEUE [(NamedType)] [,PRE(xx)] | [END | .]
        // The optional parenthesized group captures a named GROUP/QUEUE,TYPE instantiated
        // inline (e.g. "PersonData GROUP(PTJ_PersonDataGroupType)") -- without it, this line
        // never matched any declaration regex at all and the local variable silently vanished
        // from the index (issue: GROUP(NamedType) local/DATA-section variable never captured).
        // The named "term" group additionally recognizes a same-line closing END or bare period
        // -- a self-closing single-line form, e.g. "InlineGroup GROUP(SmallGroupType) END" --
        // so the caller can tell it apart from a genuine multi-line group with its own,
        // separately-appearing closing line (see the "term" self-closing check below).
        private static readonly Regex GroupQueueDeclRegex = new Regex(
            @"^([\w:]+)\s+(GROUP|QUEUE)\s*(\([^)]*\))?\s*(?:(,.*)|(?<term>END\b\s*|\.\s*))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // CLASS member that is itself a GROUP/QUEUE/RECORD instantiation (also allows RECORD,
        // unlike GroupQueueDeclRegex above, which only ever needed GROUP/QUEUE for DATA-section
        // locals). Same shape and same named "term" self-closing group as GroupQueueDeclRegex --
        // used by ParseIncFile's CLASS-body nested-structure check to ALSO capture a symbol for
        // the member's own name, not just track nesting depth (issue: GROUP/QUEUE/RECORD CLASS
        // member's own name never captured as a symbol at all).
        private static readonly Regex ClassGroupQueueRecordDeclRegex = new Regex(
            @"^([\w:]+)\s+(GROUP|QUEUE|RECORD)\s*(\([^)]*\))?\s*(?:(,.*)|(?<term>END\b\s*|\.\s*))?$",
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

        // LIBRARY-MODE method prototype pattern: same, but allows the method name in column 0.
        // ABC / library .inc files declare class members and methods in the label column
        // (e.g. "Open  PROCEDURE(),BYTE,PROC,VIRTUAL"), unlike app-generated .inc which indent.
        private static readonly Regex MethodPrototypeRegexLib = new Regex(
            @"^\s*(\w+)\s+PROCEDURE\s*(\([^)]*\))?\s*(,.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// LIBRARY MODE (ticket 6e8f2439, ClarionGraph): when true, class-body method
        /// prototypes may start in column 0, and method names that collide with Clarion
        /// built-in statement keywords (Open/Close/Next/Add/...) are KEPT rather than skipped —
        /// ABC methods legitimately use those names and are always called qualified
        /// (FileManager.Open). Default false preserves the exact existing CodeGraph behaviour
        /// for app source.
        /// </summary>
        public bool LibraryMode { get; set; }

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
        /// Find the line index where a PROGRAM file's "tail" starts: the global CODE section,
        /// followed by any hand-written procedure implementations. Per the language reference,
        /// a MAP can only appear in the declaration section of a PROGRAM/MEMBER module or of a
        /// PROCEDURE — so once the global CODE statement is reached, no further top-level MAP
        /// can occur, and everything from there on is MEMBER-shaped source that ParseMemberFile
        /// understands. Returns lines.Length when no boundary exists (tail scan becomes a no-op).
        /// </summary>
        public int FindMainTailStart(string filePath)
        {
            if (!File.Exists(filePath)) return 0;
            var lines = File.ReadAllLines(filePath);

            bool inMap = false;
            bool inModule = false;
            int mapDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                int skipped = SkipConditionalBlock(lines, i, line);
                if (skipped > i) { i = skipped; continue; }

                if (inMap)
                {
                    if (EndRegex.IsMatch(line) || PeriodTermRegex.IsMatch(line))
                    {
                        if (inModule)
                            inModule = false;
                        else
                        {
                            mapDepth--;
                            if (mapDepth <= 0) inMap = false;
                        }
                    }
                    // Looser than ModuleRegex on purpose: MODULE('') (external DLL, no file
                    // name) must still bump the nesting, or its END would close the MAP early.
                    else if (Regex.IsMatch(line, @"^\s*MODULE\s*\(", RegexOptions.IgnoreCase))
                    {
                        inModule = true;
                    }
                    continue;
                }

                if (MapStartRegex.IsMatch(line))
                {
                    inMap = true;
                    inModule = false;
                    mapDepth = 1;
                    continue;
                }

                // Global CODE — the program's main routine; the tail starts here.
                if (CodeRegex.IsMatch(line)) return i;

                // Defensive: a column-1 implementation with no global CODE before it.
                if (ProcedureDefRegex.IsMatch(line) || FunctionDefRegex.IsMatch(line)) return i;
            }

            return lines.Length;
        }

        /// <summary>
        /// Pass 2: Parse a MEMBER .clw file for procedure/routine definitions and calls.
        /// Pass a non-zero startLine to scan only a PROGRAM file's tail (see FindMainTailStart).
        /// </summary>
        public ParseResult ParseMemberFile(string filePath, int projectId, HashSet<string> knownProcedures, int startLine = 0)
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

            for (int i = startLine; i < lines.Length; i++)
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
                    // (library mode also allows column-0 names — ABC/library style)
                    // Strip a trailing inline comment first -- see the DATA-section fix above for why.
                    var methodMatch = (LibraryMode ? MethodPrototypeRegexLib : MethodPrototypeRegex).Match(StripInlineComment(line));
                    if (methodMatch.Success && currentClassName != null)
                    {
                        string methodName = methodMatch.Groups[1].Value;
                        if (LibraryMode || !ClarionBuiltins.IsBuiltInOrKeyword(methodName))
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
                    ExtractNamedParameters(procParams, currentProcedure, filePath, lineNum, projectId, result);
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
                    ExtractNamedParameters(funcParams, currentProcedure, filePath, lineNum, projectId, result);
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

                    // A CLASS declared inside a procedure's own DATA section -- e.g.
                    // "InputJson CLASS(jsonClass)" with one of jsonClass's virtual methods
                    // overridden inline and implemented later in the same procedure via the
                    // standard "ClassName.MethodName PROCEDURE(...)" syntax -- is a LOCAL
                    // variable of that procedure, not a top-level/global class declaration.
                    // Before this fix, this check fired unconditionally regardless of whether
                    // the parser was currently inside a procedure's own DATA section, so a
                    // locally-declared derived class was indexed as a phantom global class
                    // instead of a local variable of the enclosing procedure (issue:
                    // procedure-local derived-class variable misclassified as a global class).
                    if (currentProcedure != null)
                    {
                        result.Symbols.Add(new ClarionSymbol
                        {
                            Name = className,
                            Type = "variable",
                            FilePath = filePath,
                            LineNumber = lineNum,
                            ProjectId = projectId,
                            Params = parentClass != null ? parentClass.ToUpperInvariant() : "CLASS",
                            ParentName = currentProcedure,
                            Scope = "local"
                        });

                        // The inline class body (its overridden method prototype(s)) still needs
                        // to be skipped until its own closing line. This CANNOT reuse
                        // dataGroupDepth the way a local GROUP/QUEUE body does (see the
                        // GROUP/QUEUE(NamedType) local-variable fix): a GROUP/QUEUE body only
                        // ever contains plain data-field lines, but a CLASS body's overridden
                        // method prototype is itself shaped like "MethodName PROCEDURE(...)" --
                        // which the unconditional, unanchored "Detect PROCEDURE definition" check
                        // earlier in this same per-line dispatch loop would intercept BEFORE the
                        // DATA-section dataGroupDepth-skip logic further down ever runs, silently
                        // clobbering currentProcedure (and therefore every local variable and
                        // call attributed to the REAL enclosing procedure for the rest of its
                        // body) -- confirmed by direct verification against the repro. Instead,
                        // reuse the SAME inClassBody/classEndDepth mechanism a top-level CLASS
                        // body already uses, since that check runs at the very top of this loop,
                        // before "Detect PROCEDURE definition" ever gets a chance to fire.
                        // Deliberately leave currentClassName unset (still null): the method-
                        // prototype capture inside "if (inClassBody)" is gated on
                        // currentClassName != null, so no symbol is created for the inline
                        // prototype line here -- the real implementation of any overridden
                        // method is already captured separately via the standard top-level
                        // "ClassName.MethodName PROCEDURE(...)" syntax elsewhere in the file, so
                        // capturing this inline prototype too would only risk a duplicate symbol.
                        inClassBody = true;
                        classEndDepth = 1;
                        continue;
                    }

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

                    // Strip a trailing inline comment before type-matching -- a declaration like
                    // "PrivKey &SomeClass !some comment" would otherwise never match any of the
                    // regexes below, since they all anchor at end-of-line (issue: trailing-comment
                    // member capture gap).
                    string dataForTypeMatch = StripInlineComment(trimmedData);

                    // GROUP/QUEUE declaration
                    var gqMatch = GroupQueueDeclRegex.Match(dataForTypeMatch);
                    if (gqMatch.Success)
                    {
                        string gqName = gqMatch.Groups[1].Value;
                        string gqType = gqMatch.Groups[2].Value.ToUpperInvariant();
                        string gqNamedType = gqMatch.Groups[3].Success ? gqMatch.Groups[3].Value : "";
                        string gqAttrs = gqMatch.Groups[4].Success ? gqMatch.Groups[4].Value : "";

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
                            Params = gqType + gqNamedType + (prefix != null ? ",PRE(" + prefix + ")" : ""),
                            ParentName = varOwner,
                            Scope = varScope
                        });

                        // A named-type form (e.g. "PersonData GROUP(PTJ_PersonDataGroupType)") can be
                        // self-closing on this same line -- "InlineGroup GROUP(SmallGroupType) END" (or
                        // terminated with a bare "." instead) -- in which case there is no separate
                        // closing line for the END/period check above to ever match, and incrementing
                        // dataGroupDepth here would leak it permanently, silently swallowing every
                        // subsequent local variable in this DATA section (the same class of bug fixed
                        // for ParseIncFile's classEndDepth -- see the inline-group-depth-leak fix).
                        // GroupQueueDeclRegex's own "term" group already recognized the same-line
                        // terminator (if any), so no separate re-check of the line text is needed here.
                        if (!gqMatch.Groups["term"].Success)
                        {
                            dataGroupDepth++;
                        }
                        continue;
                    }

                    // Simple variable declaration: VarName TYPE[(size)]
                    var varMatch = VariableDeclRegex.Match(dataForTypeMatch);
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
                    var refMatch = RefVariableDeclRegex.Match(dataForTypeMatch);
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
                    var eqMatch = EquateDeclRegex.Match(dataForTypeMatch);
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
                    var likeMatch = LikeDeclRegex.Match(dataForTypeMatch);
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
                    var classInstMatch = ClassInstanceDeclRegex.Match(dataForTypeMatch);
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

                    // Nested END for inner GROUP/QUEUE etc. -- also captures a symbol for the
                    // member's own name when it's a DIRECT class member (classEndDepth==1),
                    // mirroring the GROUP/QUEUE(NamedType) local-variable fix. Before this fix, a
                    // CLASS member that is itself a GROUP/QUEUE/RECORD -- e.g.
                    // "InlineGroup GROUP(SmallGroupType) END" -- never got a symbol for its OWN
                    // name at all here; only classEndDepth bookkeeping happened (needed to
                    // correctly skip the member's nested body and know when the class itself
                    // closes), never symbol creation (issue: GROUP/QUEUE/RECORD CLASS member's
                    // own name never captured as a symbol).
                    var groupMemberMatch = ClassGroupQueueRecordDeclRegex.Match(StripInlineComment(line.TrimStart()));
                    if (groupMemberMatch.Success)
                    {
                        if (currentClassName != null && classEndDepth == 1 &&
                            !Regex.IsMatch(groupMemberMatch.Value, @",\s*PRIVATE\b", RegexOptions.IgnoreCase))
                        {
                            string groupMemberName = groupMemberMatch.Groups[1].Value;
                            string groupMemberType = groupMemberMatch.Groups[2].Value.ToUpperInvariant() +
                                (groupMemberMatch.Groups[3].Success ? groupMemberMatch.Groups[3].Value : "");
                            result.Symbols.Add(new ClarionSymbol
                            {
                                Name = currentClassName + "." + groupMemberName,
                                Type = "variable",
                                FilePath = filePath,
                                LineNumber = lineNum,
                                ProjectId = projectId,
                                Params = groupMemberType,
                                ParentName = currentClassName,
                                Scope = "class"
                            });
                        }

                        // A self-closing single-line form -- e.g. "CertInfo GROUP(CertInfoGroupType) END"
                        // (a named GROUP/QUEUE/RECORD,TYPE instantiated inline, all on one line), or the
                        // same thing terminated with a bare period instead of END (per the language
                        // reference, "." is fully interchangeable with END for terminating any structure,
                        // not just executable statements) -- opens and closes on the same line. Incrementing
                        // classEndDepth unconditionally here would leak it permanently: there is no separate
                        // closing line for the EndRegex/PeriodTermRegex check above to ever match, since both
                        // require the terminator to be the ONLY content on the line, and this line has other
                        // content before it. That leak silently breaks every subsequent data member AND every
                        // subsequent CLASS declaration in the rest of the file (issue: classEndDepth leak on
                        // self-closing inline GROUP/QUEUE/RECORD). The regex's own "term" group already
                        // recognizes the same-line terminator (if any), so no separate re-check is needed.
                        if (!groupMemberMatch.Groups["term"].Success)
                        {
                            classEndDepth++;
                        }
                        continue;
                    }

                    // Method prototype inside CLASS/INTERFACE
                    // (library mode also allows column-0 names — ABC/library style)
                    // Strip a trailing inline comment first -- see the DATA-section fix above for why.
                    var methodMatch = (LibraryMode ? MethodPrototypeRegexLib : MethodPrototypeRegex).Match(StripInlineComment(line));
                    if (methodMatch.Success && currentClassName != null)
                    {
                        string methodName = methodMatch.Groups[1].Value;
                        if (LibraryMode || !ClarionBuiltins.IsBuiltInOrKeyword(methodName))
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
                    // Data member of the CLASS (scalar property like "AutoRefresh BYTE", or a reference like
                    // "Errors &ErrorClass"). Stored DOTTED ("Class.Member", parent_name=Class) so member-access
                    // completion lists it AND FindMembersOfParent's dotted-name filter keeps it distinct from
                    // subclasses (which carry parent_name=Class via inheritance). Only direct members
                    // (classEndDepth==1, i.e. not inside a nested GROUP/QUEUE). PRIVATE members are skipped —
                    // they aren't accessible via instance member access, matching native Clarion completion.
                    else if (currentClassName != null && classEndDepth == 1)
                    {
                        string trimmedMember = line.TrimStart();
                        // Strip a trailing inline comment before matching -- see the DATA-section
                        // fix above for why (e.g. "PrivKey &SomeClass !some comment" must still match).
                        string memberForTypeMatch = StripInlineComment(trimmedMember);
                        var refMatch = RefVariableDeclRegex.Match(memberForTypeMatch);
                        var sclMatch = refMatch.Success ? Match.Empty : VariableDeclRegex.Match(memberForTypeMatch);
                        if (refMatch.Success || sclMatch.Success)
                        {
                            string memberName = (refMatch.Success ? refMatch : sclMatch).Groups[1].Value;
                            string attrs = refMatch.Success
                                ? (refMatch.Groups[3].Success ? refMatch.Groups[3].Value : "")
                                : (sclMatch.Groups[4].Success ? sclMatch.Groups[4].Value : "");
                            if (attrs.IndexOf("PRIVATE", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                string memberType = refMatch.Success
                                    ? "&" + refMatch.Groups[2].Value
                                    : sclMatch.Groups[2].Value.ToUpperInvariant() +
                                      (sclMatch.Groups[3].Success ? sclMatch.Groups[3].Value : "");
                                result.Symbols.Add(new ClarionSymbol
                                {
                                    Name = currentClassName + "." + memberName,
                                    Type = "variable",
                                    FilePath = filePath,
                                    LineNumber = lineNum,
                                    ProjectId = projectId,
                                    Params = memberType,
                                    ParentName = currentClassName,
                                    Scope = "class"
                                });
                            }
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
        /// Skip an unconditional OMIT('term') block. Returns the updated line index (past the terminator).
        /// </summary>
        private int SkipConditionalBlock(string[] lines, int currentIndex, string currentLine)
        {
            var match = OmitCompileRegex.Match(currentLine);
            if (!match.Success) return currentIndex;

            string directive = match.Groups[1].Value.ToUpperInvariant();
            string terminator = match.Groups[2].Value;
            bool hasExpression = match.Groups[3].Success;

            // COMPILE's code is always treated as included (real conditional-compile evaluation
            // isn't implemented). A conditional OMIT('term', someEquate) is symmetric: whether it's
            // really omitted depends on a project-specific EQUATE/Conditional Switch value that
            // CodeGraph has no way to know, so it's also always treated as included — better to show
            // a call that might not exist in every build than to hide one that does. Only a bare,
            // unconditional OMIT('term') (no expression) is unambiguously dead code in every build,
            // so that's the only case still skipped below.
            if (directive == "COMPILE" || hasExpression) return currentIndex;

            int i = currentIndex + 1;
            while (i < lines.Length)
            {
                // Per Clarion language reference: the block "ends with the line that contains
                // the same string constant as the terminator" — a substring match anywhere in
                // the line, not a prefix match. The terminator is commonly written as a bare
                // label, a "!label" comment, or embedded in a longer decorative comment (e.g.
                // "!end- COMPILE ('*debug*',_debug_)") — all three are legal and must match.
                if (lines[i].Contains(terminator))
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

        // Strips a leading CONST/REF qualifier from a parameter declaration segment (Clarion#
        // compatibility keywords, see "Prototype Parameter Lists" in the language reference).
        private static readonly Regex ConstRefPrefixRegex = new Regex(
            @"^(CONST|REF)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Splits a PROCEDURE/FUNCTION's raw parameter-list string (as captured by
        /// ProcedureDefRegex/FunctionDefRegex, including the outer parens) into named,
        /// typed parameters, emitting each as a "variable" symbol scoped to the owning
        /// procedure (Scope="parameter", ParentName = the procedure's own full name) so a
        /// call like "pPrivKey.Sign(...)" inside that procedure's CODE can resolve pPrivKey's
        /// declared type. Unnamed (prototype-only) parameters -- e.g. "PROCEDURE(*SomeClass)"
        /// with no trailing label, a legal and commonly-used Clarion style -- are skipped, since
        /// there's no name for a call site to bind to. By-address parameters ("*Type Name")
        /// are stored "&amp;Type", matching the existing convention for reference-typed DATA
        /// declarations, so TryResolveVariableClassType needs no changes to handle them.
        /// </summary>
        private void ExtractNamedParameters(string rawParams, string ownerFullName, string filePath, int lineNum, int projectId, ParseResult result)
        {
            if (string.IsNullOrEmpty(rawParams)) return;

            string content = rawParams.Trim();
            if (content.StartsWith("(") && content.EndsWith(")"))
                content = content.Substring(1, content.Length - 2);

            foreach (string rawSegment in SplitParameterList(content))
            {
                string segment = rawSegment.Trim();
                if (segment.Length == 0) continue;

                // Strip a default value: "Type Name = default" (only valid on simple numeric
                // types per the language reference, but harmless to strip unconditionally here).
                int eqIdx = FindTopLevelChar(segment, '=');
                if (eqIdx >= 0) segment = segment.Substring(0, eqIdx).Trim();

                // Strip omittable-parameter angle brackets: <*Type Name>
                if (segment.StartsWith("<") && segment.EndsWith(">"))
                    segment = segment.Substring(1, segment.Length - 2).Trim();

                segment = ConstRefPrefixRegex.Replace(segment, "").TrimStart();

                bool byAddress = false;
                if (segment.StartsWith("*"))
                {
                    byAddress = true;
                    segment = segment.Substring(1).TrimStart();
                }

                var tokens = segment.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue; // unnamed (prototype-only) parameter -- nothing to bind

                string typeName = tokens[0];
                string paramName = tokens[tokens.Length - 1];
                if (!Regex.IsMatch(paramName, @"^[A-Za-z_]\w*$")) continue; // defensive: not a plausible identifier

                string storedType = byAddress ? "&" + typeName : typeName;

                result.Symbols.Add(new ClarionSymbol
                {
                    Name = paramName,
                    Type = "variable",
                    FilePath = filePath,
                    LineNumber = lineNum,
                    ProjectId = projectId,
                    Params = storedType,
                    ParentName = ownerFullName,
                    Scope = "parameter"
                });
            }
        }

        /// <summary>
        /// Splits a parameter-list body on top-level commas -- respecting nested parens/brackets
        /// (e.g. array subscript lists) and single-quoted string literals, so a default value
        /// like "STRING pMsg = 'a, b'" is not incorrectly split on the comma inside the quotes.
        /// </summary>
        private static List<string> SplitParameterList(string content)
        {
            var results = new List<string>();
            int depth = 0;
            bool inString = false;
            int start = 0;
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c == '\'') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '(' || c == '[') { depth++; continue; }
                if (c == ')' || c == ']') { depth--; continue; }
                if (c == ',' && depth == 0)
                {
                    results.Add(content.Substring(start, i - start));
                    start = i + 1;
                }
            }
            results.Add(content.Substring(start));
            return results;
        }

        /// <summary>
        /// Finds the index of the first top-level (not inside a quoted string) occurrence of a
        /// character.
        /// </summary>
        private static int FindTopLevelChar(string s, char target)
        {
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'') { inString = !inString; continue; }
                if (!inString && c == target) return i;
            }
            return -1;
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
