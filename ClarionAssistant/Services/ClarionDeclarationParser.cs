using System;
using System.Collections.Generic;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Parses pasted / dropped Clarion variable DECLARATION text into structured field specs the
    /// FileSchemaVariableInserter can turn into DDFields. MVP scope (ticket aca8c8ed): SIMPLE single-line
    /// types only — one field per line:
    ///   Label  STRING(20)            → sized string (STRING/CSTRING/PSTRING/ASTRING/BSTRING → Characters)
    ///   Label  LONG                  → fixed numeric/date/time (size intrinsic, no Characters)
    ///   Label  DECIMAL(10,2)         → DECIMAL/PDECIMAL → Characters + Places
    ///   Label  STRING(30),DIM(5),NAME('X')   → DIM() → Dimensions, NAME('..') → ExternalName
    /// Blank lines and !-comment-only lines are skipped. A line we can't parse becomes a spec with Error set
    /// (the rest of the block still parses) so the caller can add the good fields and report the skips.
    ///
    /// DELIBERATELY NOT supported in the MVP (each yields a clear Error, never a malformed field):
    /// GROUP/QUEUE/CLASS/TYPE/FILE/VIEW structures, LIKE(), &amp;-reference types, OVER(), picture-form sizes
    /// (e.g. STRING(@s20)). Pure C# — no SoftVelocity types — so it stays unit-testable off the IDE.
    /// </summary>
    public static class ClarionDeclarationParser
    {
        /// <summary>One parsed declaration line. Ok == (Error == null). Sizing fields are null when N/A.</summary>
        public sealed class ParsedFieldSpec
        {
            public string Label;          // the variable name (may carry a Clarion prefix, e.g. "LOC:Name")
            public string ClarionType;    // upper-cased token, e.g. "STRING", "LONG", "DECIMAL"
            public uint? Characters;      // sized-string length, or DECIMAL/PDECIMAL total digits
            public ushort? Places;        // DECIMAL/PDECIMAL fractional places
            public uint? Dim;             // DIM(n) — first dimension only in the MVP
            public string ExternalName;   // NAME('x') external name attribute
            public string Raw;            // original source line (trimmed), for error messages
            public int LineNumber;        // 1-based line number within the pasted block
            public string Error;          // null when parsed cleanly; otherwise why it was rejected

            public bool Ok { get { return Error == null; } }
        }

        // Sized character types: the (n) argument is the declared length, stored as DDField.Characters.
        private static readonly HashSet<string> SizedStringTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "STRING", "CSTRING", "PSTRING", "ASTRING", "BSTRING" };

        // Fixed-size types: size is intrinsic to the type (DDField derives it), so no (n) is needed/allowed.
        private static readonly HashSet<string> FixedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "LONG", "ULONG", "SHORT", "USHORT", "BYTE", "SBYTE", "SIGNED", "UNSIGNED",
              "REAL", "SREAL", "BFLOAT4", "BFLOAT8", "DATE", "TIME" };

        // DECIMAL/PDECIMAL: (digits) or (digits,places).
        private static readonly HashSet<string> DecimalTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "DECIMAL", "PDECIMAL" };

        // Structure / reference / complex types we intentionally reject in the MVP with a helpful message.
        private static readonly HashSet<string> UnsupportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "GROUP", "QUEUE", "CLASS", "TYPE", "FILE", "VIEW", "REPORT", "WINDOW", "LIKE",
              "BLOB", "MEMO", "ANY", "PICTURE", "KEY" };

        /// <summary>
        /// Parse a whole pasted/dropped block. Returns one ParsedFieldSpec per NON-skipped line, in order;
        /// blank and comment-only lines produce no spec. Never throws — a bad line is reported via spec.Error.
        /// </summary>
        public static IList<ParsedFieldSpec> Parse(string text)
        {
            var specs = new List<ParsedFieldSpec>();
            if (string.IsNullOrEmpty(text)) return specs;

            // Normalize newlines, then walk each physical line (one declaration per line in the MVP).
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string stripped = StripComment(lines[i]).Trim();
                if (stripped.Length == 0) continue;       // blank or comment-only → skip silently
                specs.Add(ParseLine(stripped, i + 1));
            }
            return specs;
        }

        /// <summary>Parse a single already-comment-stripped, trimmed declaration line. Never throws.</summary>
        public static ParsedFieldSpec ParseLine(string raw, int lineNumber)
        {
            var spec = new ParsedFieldSpec { Raw = raw, LineNumber = lineNumber };
            try
            {
                int pos = 0;

                // ---- Label: first whitespace-delimited token, must be a legal Clarion identifier. ----
                string label = NextToken(raw, ref pos);
                if (string.IsNullOrEmpty(label))
                    return Err(spec, "No variable name found.");
                if (!IsValidLabel(label))
                    return Err(spec, "'" + label + "' isn't a valid Clarion variable name.");
                spec.Label = label;

                // ---- Type name: up to '(' , ',' or whitespace. ----
                SkipSpaces(raw, ref pos);
                if (pos >= raw.Length)
                    return Err(spec, "Missing a type for '" + label + "' (e.g. STRING(20), LONG).");
                if (raw[pos] == '&')
                    return Err(spec, "Reference types (&) aren't supported yet — simple single-line types only.");

                string type = ReadTypeName(raw, ref pos);
                if (string.IsNullOrEmpty(type))
                    return Err(spec, "Missing a type for '" + label + "' (e.g. STRING(20), LONG).");
                spec.ClarionType = type.ToUpperInvariant();

                if (UnsupportedTypes.Contains(type))
                    return Err(spec, type.ToUpperInvariant() + " isn't supported yet — simple single-line types only "
                        + "(STRING/CSTRING/PSTRING, LONG/SHORT/BYTE/REAL/DATE/TIME, DECIMAL).");

                bool sizedString = SizedStringTypes.Contains(type);
                bool dec = DecimalTypes.Contains(type);
                bool fixedType = FixedTypes.Contains(type);
                if (!sizedString && !dec && !fixedType)
                    return Err(spec, "Unknown type '" + type + "'.");

                // ---- Optional type argument list (n) or (n,p). ----
                SkipSpaces(raw, ref pos);
                string[] typeArgs = null;
                if (pos < raw.Length && raw[pos] == '(')
                {
                    string argText;
                    if (!ReadParens(raw, ref pos, out argText))
                        return Err(spec, "Unbalanced '(' in the type for '" + label + "'.");
                    typeArgs = SplitArgs(argText);
                }

                if (sizedString)
                {
                    if (typeArgs == null || typeArgs.Length != 1)
                        return Err(spec, type.ToUpperInvariant() + " needs a single length, e.g. " + type.ToUpperInvariant() + "(20).");
                    uint chars;
                    if (!TryParseUInt(typeArgs[0], out chars) || chars == 0)
                        return Err(spec, "'" + typeArgs[0] + "' isn't a valid length for " + type.ToUpperInvariant() + ".");
                    spec.Characters = chars;
                }
                else if (dec)
                {
                    if (typeArgs == null || typeArgs.Length < 1 || typeArgs.Length > 2)
                        return Err(spec, type.ToUpperInvariant() + " needs digits, e.g. " + type.ToUpperInvariant() + "(10) or " + type.ToUpperInvariant() + "(10,2).");
                    uint digits;
                    if (!TryParseUInt(typeArgs[0], out digits) || digits == 0)
                        return Err(spec, "'" + typeArgs[0] + "' isn't a valid digit count for " + type.ToUpperInvariant() + ".");
                    spec.Characters = digits;
                    if (typeArgs.Length == 2)
                    {
                        uint places;
                        if (!TryParseUInt(typeArgs[1], out places) || places > digits)
                            return Err(spec, "'" + typeArgs[1] + "' isn't a valid number of places for " + type.ToUpperInvariant() + "(" + digits + ",...).");
                        spec.Places = (ushort)places;
                    }
                }
                else // fixed type — must NOT carry a size argument
                {
                    if (typeArgs != null)
                        return Err(spec, type.ToUpperInvariant() + " takes no size — just '" + label + "  " + type.ToUpperInvariant() + "'.");
                }

                // ---- Trailing attribute list: comma-separated. MVP recognizes DIM() and NAME(); others ignored. ----
                if (!ParseAttributes(raw, ref pos, spec, out string attrErr))
                    return Err(spec, attrErr);

                return spec;
            }
            catch (Exception ex)
            {
                return Err(spec, "Couldn't parse this line: " + ex.Message);
            }
        }

        // ---- attribute list (after the type) -----------------------------------------------------------
        // Walks ", ATTR(args)" pairs. Recognizes DIM(n) and NAME('x'); silently ignores any other attribute
        // (MVP — keeps a line with ,STATIC / ,AUTO etc. usable rather than failing it). Returns false + msg
        // only on a structurally broken attribute (unbalanced parens).
        private static bool ParseAttributes(string raw, ref int pos, ParsedFieldSpec spec, out string error)
        {
            error = null;
            while (true)
            {
                SkipSpaces(raw, ref pos);
                if (pos >= raw.Length) return true;
                if (raw[pos] != ',') { /* stray trailing text — tolerate, MVP ignores it */ return true; }
                pos++; // consume ','
                SkipSpaces(raw, ref pos);

                string attr = ReadTypeName(raw, ref pos); // same identifier rule as a type name
                if (string.IsNullOrEmpty(attr)) return true; // trailing comma — tolerate

                SkipSpaces(raw, ref pos);
                string args = null;
                if (pos < raw.Length && raw[pos] == '(')
                {
                    string argText;
                    if (!ReadParens(raw, ref pos, out argText))
                    {
                        error = "Unbalanced '(' in the " + attr.ToUpperInvariant() + " attribute.";
                        return false;
                    }
                    args = argText;
                }

                if (string.Equals(attr, "DIM", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = SplitArgs(args ?? "");
                    uint dim;
                    if (parts.Length >= 1 && TryParseUInt(parts[0], out dim) && dim > 0)
                        spec.Dim = dim; // MVP: first dimension only
                }
                else if (string.Equals(attr, "NAME", StringComparison.OrdinalIgnoreCase))
                {
                    spec.ExternalName = Unquote(args);
                }
                // any other attribute: ignored in the MVP
            }
        }

        // ---- small scanning helpers --------------------------------------------------------------------

        // Remove a trailing Clarion '!' comment, respecting single-quoted strings ('' is an escaped quote;
        // toggling on each quote nets the correct in/out state, which is all we need to find a real '!').
        private static string StripComment(string line)
        {
            if (line == null) return string.Empty;
            bool inStr = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\'') inStr = !inStr;
                else if (c == '!' && !inStr) return line.Substring(0, i);
            }
            return line;
        }

        private static void SkipSpaces(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        // First whitespace-delimited token from pos (skips leading spaces).
        private static string NextToken(string s, ref int pos)
        {
            SkipSpaces(s, ref pos);
            int start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos])) pos++;
            return s.Substring(start, pos - start);
        }

        // Read a type/attribute identifier: letters, digits, underscore — stops at '(', ',', whitespace, etc.
        private static string ReadTypeName(string s, ref int pos)
        {
            SkipSpaces(s, ref pos);
            int start = pos;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (char.IsLetterOrDigit(c) || c == '_') pos++;
                else break;
            }
            return s.Substring(start, pos - start);
        }

        // pos must be at '('. Reads the balanced parenthesized text (excluding the outer parens), respecting
        // nested parens and single-quoted strings. Leaves pos just past the closing ')'. False if unbalanced.
        private static bool ReadParens(string s, ref int pos, out string inner)
        {
            inner = null;
            if (pos >= s.Length || s[pos] != '(') return false;
            int depth = 0; bool inStr = false; int start = pos + 1;
            for (; pos < s.Length; pos++)
            {
                char c = s[pos];
                if (inStr) { if (c == '\'') inStr = false; continue; }
                if (c == '\'') inStr = true;
                else if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) { inner = s.Substring(start, pos - start); pos++; return true; }
                }
            }
            return false; // ran off the end without closing
        }

        // Split a parenthesized argument list on top-level commas (ignores commas inside nested parens/strings).
        private static string[] SplitArgs(string args)
        {
            var list = new List<string>();
            if (args == null) return list.ToArray();
            var sb = new StringBuilder();
            int depth = 0; bool inStr = false;
            foreach (char c in args)
            {
                if (inStr) { sb.Append(c); if (c == '\'') inStr = false; continue; }
                if (c == '\'') { inStr = true; sb.Append(c); }
                else if (c == '(') { depth++; sb.Append(c); }
                else if (c == ')') { depth--; sb.Append(c); }
                else if (c == ',' && depth == 0) { list.Add(sb.ToString().Trim()); sb.Length = 0; }
                else sb.Append(c);
            }
            list.Add(sb.ToString().Trim());
            // Drop a single empty entry (e.g. SplitArgs("") → one empty) so callers see length 0 for no args.
            if (list.Count == 1 && list[0].Length == 0) list.Clear();
            return list.ToArray();
        }

        // Strip surrounding single quotes from a Clarion string literal and collapse the '' escape to '.
        private static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'')
                s = s.Substring(1, s.Length - 2).Replace("''", "'");
            return s;
        }

        private static bool TryParseUInt(string s, out uint value)
        {
            return uint.TryParse((s ?? "").Trim(), out value);
        }

        // Clarion label: starts with a letter or underscore; then letters/digits/underscore, with an optional
        // single ':' prefix separator (e.g. "CUS:Name"). Conservative — rejects anything exotic.
        private static bool IsValidLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int colon = s.IndexOf(':');
            if (colon >= 0)
            {
                // exactly one prefix segment + name segment, neither empty, no further ':'
                if (s.IndexOf(':', colon + 1) >= 0) return false;
                return IsIdent(s.Substring(0, colon)) && IsIdent(s.Substring(colon + 1));
            }
            return IsIdent(s);
        }

        private static bool IsIdent(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            for (int i = 1; i < s.Length; i++)
                if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) return false;
            return true;
        }

        private static ParsedFieldSpec Err(ParsedFieldSpec spec, string message)
        {
            spec.Error = message;
            return spec;
        }
    }
}
