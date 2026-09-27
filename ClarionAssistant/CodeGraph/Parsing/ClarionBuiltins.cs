using System;
using System.Collections.Generic;

namespace ClarionCodeGraph.Parsing
{
    /// <summary>
    /// Static set of all Clarion built-in procedure/function/statement names.
    /// Used to exclude built-in calls from the user-defined call graph.
    /// Extracted from the Clarion Language Reference (SoftVelocity).
    /// </summary>
    public static class ClarionBuiltins
    {
        // Grouped by category (each group's first element). The category is the detail line a
        // keyword/built-in completion item and hover card show (1c685f2e); membership is unchanged.
        private static readonly string[][] _builtinGroups =
        {
            // Math
            new[] { "Math", "ABS", "ACOS", "ASIN", "ATAN", "COS", "INT", "LOG10", "LOGE", "MAXIMUM", "ROUND",
                    "SIN", "SQRT", "TAN" },
            // String
            new[] { "String", "CENTER", "CHR", "CLIP", "CLIPBOARD", "DEFORMAT", "FORMAT", "INSTRING", "LEFT",
                    "LEN", "LOWER", "MATCH", "NUMERIC", "SUB", "STRPOS", "UPPER", "VAL", "RIGHT" },
            // Date/Time
            new[] { "Date/Time", "CLOCK", "DATE", "DAY", "MONTH", "SETCLOCK", "SETTODAY", "TODAY", "YEAR" },
            // File I/O
            new[] { "File I/O", "ADD", "BUILD", "BUFFER", "CALLBACK", "CLOSE", "COMMIT", "COPY", "CREATE",
                    "DELETE", "DUPLICATE", "EMPTY", "EOF", "EXISTS", "FIXFORMAT", "FLUSH", "FREE", "GET",
                    "GETSTATE", "HOLD", "LOCK", "LOGOUT", "NAME", "NEXT", "NOMEMO", "OPEN", "PACK",
                    "PREVIOUS", "PUT", "RECORDS", "REGET", "RELEASE", "REMOVE", "RENAME", "RESET",
                    "RESTORESTATE", "ROLLBACK", "SEND", "SET", "SHARE", "STATUS", "STREAM", "UNFIXFORMAT",
                    "UNLOCK", "WATCH" },
            // Queue
            new[] { "Queue", "CHANGES", "POINTER", "SORT" },
            // Window / UI
            new[] { "Window/UI", "ACCEPT", "ACCEPTED", "ASK", "BEEP", "CHANGE", "CHOICE", "CLONE",
                    "COLORDIALOG", "CONTENTS", "DESTROY", "DISABLE", "DISPLAY", "DRAGID", "DROPID", "ENABLE",
                    "ERASE", "EVENT", "FIELD", "FILEDIALOG", "FIRSTFIELD", "FOCUS", "FONTDIALOG", "GETFONT",
                    "GETPOSITION", "HIDE", "KEYBOARD", "KEYCHAR", "KEYCODE", "KEYSTATE", "LASTFIELD",
                    "MOUSEX", "MOUSEY", "POPUP", "POST", "PRESSKEY", "SELECT", "SELECTED", "SET3DLOOK",
                    "SETCLIPBOARD", "SETCURSOR", "SETDROPID", "SETFONT", "SETKEYCHAR", "SETKEYCODE",
                    "SETLAYOUT", "SETPENCOLOR", "SETPENSTYLE", "SETPENWIDTH", "SETPOSITION", "SETTARGET",
                    "SHOW", "TYPE", "UNHIDE", "UPDATE" },
            // Report
            new[] { "Report", "ENDPAGE", "PRINT" },
            // Memory / System
            new[] { "Memory/System", "ADDRESS", "BAND", "BOR", "BSHIFT", "BXOR", "CALL", "CHAIN", "HALT",
                    "INSTANCE", "PEEK", "POKE", "RUN", "RUNCODE", "SHUTDOWN", "STOP", "UNLOAD" },
            // Threading
            new[] { "Threading", "LOCKTHREAD", "NOTIFICATION", "NOTIFY", "RESUME", "START", "SUSPEND",
                    "THREAD", "THREADLOCKED", "UNLOCKTHREAD" },
            // Runtime expressions
            new[] { "Runtime expressions", "BIND", "BINDEXPRESSION", "EVALUATE", "POPBIND", "PUSHBIND",
                    "UNBIND" },
            // Registry
            new[] { "Registry", "DELETEREG", "GETREG", "PUTREG" },
            // Path / Filesystem
            new[] { "Path/Filesystem", "DIRECTORY", "LONGPATH", "PATH", "SETPATH", "SHORTPATH" },
            // Introspection
            new[] { "Introspection", "GETGROUP", "HOWMANY", "ISALPHA", "ISGROUP", "ISLOWER", "ISSTRING",
                    "ISUPPER", "NULL", "OMITTED", "SETNULL", "SETNULLS", "SETNONULL", "GETNULLS", "WHAT",
                    "WHERE", "WHO" },
            // Error handling
            new[] { "Error handling", "ASSERT", "ERROR", "ERRORCODE", "ERRORFILE", "FILEERROR",
                    "FILEERRORCODE", "POPERRORS", "PUSHERRORS" },
            // Miscellaneous
            new[] { "Miscellaneous", "BOF", "CHOOSE", "CLEAR", "COMMAND", "CONVERTANSITOOEM",
                    "CONVERTOEMTOANSI", "INLIST", "INRANGE", "MESSAGE", "SETCOMMAND", "SQL", "SQLCALLBACK" },
            // Drawing
            new[] { "Drawing", "ARC", "BOX", "CHORD", "ELLIPSE", "IMAGE", "LINE", "PENCOLOR", "PENSTYLE",
                    "PENWIDTH", "PIE", "POLYGON" },
            // DDE
            new[] { "DDE", "DDEACKNOWLEDGE", "DDEAPP", "DDECHANNEL", "DDECLIENT", "DDECLOSE", "DDEEXECUTE",
                    "DDEITEM", "DDEPOKE", "DDEQUERY", "DDEREAD", "DDESERVER", "DDETOPIC", "DDEVALUE",
                    "DDEWRITE" },
            // OLE
            new[] { "OLE", "OLEDIRECTORY", "OCXREGISTERPROPEDIT", "OCXREGISTERPROPCHANGE",
                    "OCXREGISTEREVENTPROC", "OCXUNREGISTERPROPEDIT", "OCXUNREGISTERPROPCHANGE",
                    "OCXUNREGISTEREVENTPROC" },
            // Object lifecycle
            new[] { "Object lifecycle", "NEW", "DISPOSE" },
            // ASTRING
            new[] { "ASTRING", "TIE", "TIED", "UNTIE" },
            // Misc functions
            new[] { "Miscellaneous", "LOCALE", "PRAGMA", "PRESS", "POSITION", "SETTODAY", "GOTOXYABS" },
            // View
            new[] { "View", "FREESTATE" },
            // Language structures that look like calls but aren't
            new[] { "Statement", "IF", "THEN", "ELSIF", "ELSE", "CASE", "OF", "OROF", "LOOP", "WHILE",
                    "UNTIL", "TIMES", "BY", "EXECUTE", "BEGIN", "RETURN", "EXIT", "CYCLE", "BREAK", "GOTO",
                    "DO", "NOT", "AND", "OR", "XOR", "BAND", "BOR", "BXOR", "BSHIFT" },
        };

        private static readonly HashSet<string> _builtins = Flatten(_builtinGroups);

        /// <summary>
        /// All Clarion reserved/structure keywords that should never be treated as procedure names.
        /// Superset of the old IsKeyword() method.
        /// </summary>
        // Grouped by category (each group's first element). The category is the detail line a
        // keyword/built-in completion item and hover card show (1c685f2e); membership is unchanged.
        private static readonly string[][] _keywordGroups =
        {
            // Program structure
            new[] { "Program structure", "PROGRAM", "MEMBER", "MAP", "MODULE", "END", "PROCEDURE", "FUNCTION",
                    "CODE", "DATA", "ROUTINE", "CLASS", "INTERFACE", "APPLICATION" },
            // Data structure keywords
            new[] { "Data structure", "GROUP", "QUEUE", "FILE", "RECORD", "KEY", "INDEX", "MEMO", "BLOB",
                    "VIEW", "JOIN", "WINDOW", "REPORT", "TOOLBAR", "MENUBAR" },
            // Control keywords (inside WINDOW/REPORT)
            new[] { "Control", "BUTTON", "CHECK", "COMBO", "ENTRY", "ITEM", "LIST", "MENU", "OLE", "OPTION",
                    "PANEL", "PROGRESS", "PROMPT", "RADIO", "REGION", "SHEET", "TAB", "SPIN", "TEXT" },
            // Report sub-structures
            new[] { "Report structure", "HEADER", "FOOTER", "DETAIL", "FORM" },
            // Compiler directives
            new[] { "Compiler directive", "INCLUDE", "SECTION", "COMPILE", "OMIT", "ONCE", "ITEMIZE",
                    "EQUATE" },
            // Declaration attributes
            new[] { "Attribute", "VIRTUAL", "DERIVED", "PRIVATE", "PROTECTED", "PUBLIC", "STATIC", "THREAD",
                    "EXTERNAL", "DLL", "TYPE", "AUTO", "BINDABLE", "IMPLEMENTS", "REPLACE", "PROC", "NAME",
                    "PRE", "DIM", "OVER", "LIKE" },
            // Calling conventions
            new[] { "Calling convention", "C", "PASCAL", "RAW" },
            // Data types
            new[] { "Data type", "BYTE", "SHORT", "USHORT", "LONG", "ULONG", "SIGNED", "UNSIGNED", "SREAL",
                    "REAL", "BFLOAT4", "BFLOAT8", "DECIMAL", "PDECIMAL", "STRING", "ASTRING", "CSTRING",
                    "PSTRING", "DATE", "TIME", "ANY", "BOOL", "LIKE" },
            // Special
            new[] { "Special", "SELF", "PARENT", "ACCEPT", "RETURN", "EXIT" },
        };

        private static readonly HashSet<string> _keywords = Flatten(_keywordGroups);

        private static HashSet<string> Flatten(string[][] groups)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
                for (int i = 1; i < g.Length; i++) set.Add(g[i]);
            return set;
        }

        /// <summary>Every built-in procedure/statement name with its category, in list order; a name listed
        /// in two groups is returned once, under its first.</summary>
        public static IEnumerable<KeyValuePair<string, string>> BuiltinsWithCategory() { return WithCategory(_builtinGroups); }

        /// <summary>Every reserved/structure keyword with its category, in list order.</summary>
        public static IEnumerable<KeyValuePair<string, string>> KeywordsWithCategory() { return WithCategory(_keywordGroups); }

        private static IEnumerable<KeyValuePair<string, string>> WithCategory(string[][] groups)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
                for (int i = 1; i < g.Length; i++)
                    if (seen.Add(g[i])) yield return new KeyValuePair<string, string>(g[i], g[0]);
        }

        /// <summary>The category of a built-in (first) or keyword, or null for neither.</summary>
        public static string CategoryOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var groups in new[] { _builtinGroups, _keywordGroups })
                foreach (var g in groups)
                    for (int i = 1; i < g.Length; i++)
                        if (string.Equals(g[i], name, StringComparison.OrdinalIgnoreCase)) return g[0];
            return null;
        }

        /// <summary>
        /// Returns true if the name is a Clarion built-in procedure/function/statement.
        /// </summary>
        public static bool IsBuiltIn(string name)
        {
            return _builtins.Contains(name);
        }

        /// <summary>
        /// Returns true if the name is a Clarion keyword that should never be
        /// treated as a user-defined procedure name.
        /// </summary>
        public static bool IsKeyword(string name)
        {
            return _keywords.Contains(name);
        }

        /// <summary>
        /// Returns true if the name is either a built-in or a keyword.
        /// Use this to filter out non-user-defined names from the call graph.
        /// </summary>
        public static bool IsBuiltInOrKeyword(string name)
        {
            return _builtins.Contains(name) || _keywords.Contains(name);
        }

        /// <summary>
        /// Returns true if the given type name is a known Clarion data type.
        /// </summary>
        public static bool IsClarionType(string name)
        {
            string upper = name.TrimStart('*', '&').ToUpperInvariant();
            switch (upper)
            {
                case "BYTE": case "SHORT": case "USHORT": case "LONG": case "ULONG":
                case "SIGNED": case "UNSIGNED": case "SREAL": case "REAL":
                case "BFLOAT4": case "BFLOAT8": case "DECIMAL": case "PDECIMAL":
                case "STRING": case "ASTRING": case "CSTRING": case "PSTRING":
                case "DATE": case "TIME": case "BOOL": case "ANY":
                case "GROUP": case "QUEUE": case "FILE": case "KEY":
                case "VIEW": case "WINDOW": case "REPORT":
                    return true;
                default:
                    return false;
            }
        }

        // === ABC standard globals — bare-prefix completion source (task a47a6cac) ===
        // ABC TEMPLATE-GENERATED global names: NOT user-declared (the CodeGraph never indexes them) and
        // NOT language built-ins/keywords — so they need this curated source. Generated into every ABC
        // app's global data + \LIBSRC\TPLEQU.CLW. Verified against the SoftVelocity ABC Library Reference.
        // Kind ints are LSP CompletionItemKind: 6 = Variable, 21 = Constant.
        private static readonly BuiltinCompletionItem[] _abcStandardGlobals =
        {
            new BuiltinCompletionItem("GlobalRequest",     6,  "global request (ABC) — BYTE"),
            new BuiltinCompletionItem("GlobalResponse",    6,  "global response (ABC) — BYTE"),
            new BuiltinCompletionItem("VCRRequest",        6,  "VCR request (ABC) — LONG"),
            new BuiltinCompletionItem("SilentRunning",     6,  "silent/batch flag (ABC) — BYTE"),
            new BuiltinCompletionItem("GlobalErrors",      6,  "global error mgr (ABC) — ErrorClass"),
            new BuiltinCompletionItem("GlobalErrorStatus", 6,  "global error status (ABC) — ErrorStatusClass"),
            new BuiltinCompletionItem("INIMgr",            6,  "global INI mgr (ABC) — INIClass"),
            new BuiltinCompletionItem("InsertRecord",      21, "request equate (ABC) = 1"),
            new BuiltinCompletionItem("ChangeRecord",      21, "request equate (ABC) = 2"),
            new BuiltinCompletionItem("DeleteRecord",      21, "request equate (ABC) = 3"),
            new BuiltinCompletionItem("SelectRecord",      21, "request equate (ABC) = 4"),
            new BuiltinCompletionItem("SaveRecord",        21, "request equate (ABC)"),
            new BuiltinCompletionItem("RequestCompleted",  21, "response equate (ABC) = 1"),
            new BuiltinCompletionItem("RequestCancelled",  21, "response equate (ABC) = 2"),
        };

        /// <summary>ABC standard globals/equates whose name starts with <paramref name="prefix"/>
        /// (case-insensitive). Bare-prefix completion source for task a47a6cac.</summary>
        public static IEnumerable<BuiltinCompletionItem> AbcStandardGlobalsByPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) yield break;
            foreach (var b in _abcStandardGlobals)
                if (b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    yield return b;
        }

        /// <summary>Exact (case-insensitive) ABC standard global/equate match, or null. Hover source for the
        /// template-generated globals (GlobalRequest/Response …) that live in no indexed DB. (task 37e2079f)</summary>
        public static BuiltinCompletionItem AbcStandardGlobalExact(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var b in _abcStandardGlobals)
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }
    }

    /// <summary>A curated completion entry (name + LSP CompletionItemKind int + detail) for ABC standard
    /// globals — names present in neither the CodeGraph index nor the language built-in/keyword lists.</summary>
    public sealed class BuiltinCompletionItem
    {
        public readonly string Name;
        public readonly int Kind;     // LSP CompletionItemKind int
        public readonly string Detail;
        public BuiltinCompletionItem(string name, int kind, string detail)
        {
            Name = name; Kind = kind; Detail = detail;
        }
    }
}
