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
        private static readonly HashSet<string> _builtins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Math
            "ABS", "ACOS", "ASIN", "ATAN", "COS", "INT", "LOG10", "LOGE",
            "MAXIMUM", "ROUND", "SIN", "SQRT", "TAN",

            // String
            "CENTER", "CHR", "CLIP", "CLIPBOARD", "DEFORMAT", "FORMAT",
            "INSTRING", "LEFT", "LEN", "LOWER", "MATCH", "NUMERIC",
            "SUB", "STRPOS", "UPPER", "VAL", "RIGHT",

            // Date/Time
            "CLOCK", "DATE", "DAY", "MONTH", "SETCLOCK", "SETTODAY", "TODAY", "YEAR",

            // File I/O
            "ADD", "BUILD", "BUFFER", "CALLBACK", "CLOSE", "COMMIT", "COPY",
            "CREATE", "DELETE", "DUPLICATE", "EMPTY", "EOF", "EXISTS",
            "FIXFORMAT", "FLUSH", "FREE", "GET", "GETSTATE", "HOLD",
            "LOCK", "LOGOUT", "NAME", "NEXT", "NOMEMO", "OPEN", "PACK",
            "PREVIOUS", "PUT", "RECORDS", "REGET", "RELEASE", "REMOVE",
            "RENAME", "RESET", "RESTORESTATE", "ROLLBACK", "SEND", "SET",
            "SHARE", "STATUS", "STREAM", "UNFIXFORMAT", "UNLOCK", "WATCH",

            // Queue
            "CHANGES", "POINTER", "SORT",

            // Window / UI
            "ACCEPT", "ACCEPTED", "ASK", "BEEP", "CHANGE", "CHOICE",
            "CLONE", "COLORDIALOG", "CONTENTS", "DESTROY", "DISABLE",
            "DISPLAY", "DRAGID", "DROPID", "ENABLE", "ERASE", "EVENT",
            "FIELD", "FILEDIALOG", "FIRSTFIELD", "FOCUS", "FONTDIALOG",
            "GETFONT", "GETPOSITION", "HIDE", "KEYBOARD", "KEYCHAR",
            "KEYCODE", "KEYSTATE", "LASTFIELD", "MOUSEX", "MOUSEY",
            "POPUP", "POST", "PRESSKEY", "SELECT", "SELECTED",
            "SET3DLOOK", "SETCLIPBOARD", "SETCURSOR", "SETDROPID",
            "SETFONT", "SETKEYCHAR", "SETKEYCODE", "SETLAYOUT",
            "SETPENCOLOR", "SETPENSTYLE", "SETPENWIDTH", "SETPOSITION",
            "SETTARGET", "SHOW", "TYPE", "UNHIDE", "UPDATE",

            // Report
            "ENDPAGE", "PRINT",

            // Memory / System
            "ADDRESS", "BAND", "BOR", "BSHIFT", "BXOR", "CALL", "CHAIN",
            "HALT", "INSTANCE", "PEEK", "POKE", "RUN", "RUNCODE",
            "SHUTDOWN", "STOP", "UNLOAD",

            // Threading
            "LOCKTHREAD", "NOTIFICATION", "NOTIFY", "RESUME", "START",
            "SUSPEND", "THREAD", "THREADLOCKED", "UNLOCKTHREAD",

            // Runtime expressions
            "BIND", "BINDEXPRESSION", "EVALUATE", "POPBIND", "PUSHBIND",
            "UNBIND",

            // Registry
            "DELETEREG", "GETREG", "PUTREG",

            // Path / Filesystem
            "DIRECTORY", "LONGPATH", "PATH", "SETPATH", "SHORTPATH",

            // Introspection
            "GETGROUP", "HOWMANY", "ISALPHA", "ISGROUP", "ISLOWER",
            "ISSTRING", "ISUPPER", "NULL", "OMITTED", "SETNULL",
            "SETNULLS", "SETNONULL", "GETNULLS", "WHAT", "WHERE", "WHO",

            // Error handling
            "ASSERT", "ERROR", "ERRORCODE", "ERRORFILE", "FILEERROR",
            "FILEERRORCODE", "POPERRORS", "PUSHERRORS",

            // Miscellaneous
            "BOF", "CHOOSE", "CLEAR", "COMMAND", "CONVERTANSITOOEM",
            "CONVERTOEMTOANSI", "INLIST", "INRANGE", "MESSAGE",
            "SETCOMMAND", "SQL", "SQLCALLBACK",

            // Drawing
            "ARC", "BOX", "CHORD", "ELLIPSE", "IMAGE", "LINE",
            "PENCOLOR", "PENSTYLE", "PENWIDTH", "PIE", "POLYGON",

            // DDE
            "DDEACKNOWLEDGE", "DDEAPP", "DDECHANNEL", "DDECLIENT",
            "DDECLOSE", "DDEEXECUTE", "DDEITEM", "DDEPOKE",
            "DDEQUERY", "DDEREAD", "DDESERVER", "DDETOPIC",
            "DDEVALUE", "DDEWRITE",

            // OLE
            "OLEDIRECTORY", "OCXREGISTERPROPEDIT",
            "OCXREGISTERPROPCHANGE", "OCXREGISTEREVENTPROC",
            "OCXUNREGISTERPROPEDIT", "OCXUNREGISTERPROPCHANGE",
            "OCXUNREGISTEREVENTPROC",

            // Object lifecycle
            "NEW", "DISPOSE",

            // ASTRING
            "TIE", "TIED", "UNTIE",

            // Misc functions
            "LOCALE", "PRAGMA", "PRESS", "POSITION",
            "SETTODAY", "GOTOXYABS",

            // View
            "FREESTATE",

            // Language structures that look like calls but aren't
            "IF", "THEN", "ELSIF", "ELSE", "CASE", "OF", "OROF",
            "LOOP", "WHILE", "UNTIL", "TIMES", "BY", "EXECUTE",
            "BEGIN", "RETURN", "EXIT", "CYCLE", "BREAK", "GOTO", "DO",
            "NOT", "AND", "OR", "XOR", "BAND", "BOR", "BXOR", "BSHIFT",
        };

        /// <summary>
        /// All Clarion reserved/structure keywords that should never be treated as procedure names.
        /// Superset of the old IsKeyword() method.
        /// </summary>
        private static readonly HashSet<string> _keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Program structure
            "PROGRAM", "MEMBER", "MAP", "MODULE", "END", "PROCEDURE", "FUNCTION",
            "CODE", "DATA", "ROUTINE", "CLASS", "INTERFACE", "APPLICATION",

            // Data structure keywords
            "GROUP", "QUEUE", "FILE", "RECORD", "KEY", "INDEX", "MEMO", "BLOB",
            "VIEW", "JOIN", "WINDOW", "REPORT", "TOOLBAR", "MENUBAR",

            // Control keywords (inside WINDOW/REPORT)
            "BUTTON", "CHECK", "COMBO", "ENTRY", "ITEM", "LIST", "MENU",
            "OLE", "OPTION", "PANEL", "PROGRESS", "PROMPT", "RADIO",
            "REGION", "SHEET", "TAB", "SPIN", "TEXT",

            // Report sub-structures
            "HEADER", "FOOTER", "DETAIL", "FORM",

            // Compiler directives
            "INCLUDE", "SECTION", "COMPILE", "OMIT", "ONCE", "ITEMIZE", "EQUATE",

            // Declaration attributes
            "VIRTUAL", "DERIVED", "PRIVATE", "PROTECTED", "PUBLIC",
            "STATIC", "THREAD", "EXTERNAL", "DLL", "TYPE", "AUTO",
            "BINDABLE", "IMPLEMENTS", "REPLACE", "PROC",
            "NAME", "PRE", "DIM", "OVER", "LIKE",

            // Calling conventions
            "C", "PASCAL", "RAW",

            // Data types
            "BYTE", "SHORT", "USHORT", "LONG", "ULONG", "SIGNED", "UNSIGNED",
            "SREAL", "REAL", "BFLOAT4", "BFLOAT8", "DECIMAL", "PDECIMAL",
            "STRING", "ASTRING", "CSTRING", "PSTRING", "DATE", "TIME",
            "ANY", "BOOL", "LIKE",

            // Special
            "SELF", "PARENT", "ACCEPT", "RETURN", "EXIT",
        };

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
    }
}
