using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClarionCodeGraph.Graph
{
    /// <summary>
    /// The wire format between a host (the addin in Clarion.exe, the standalone MCP server) and the
    /// clarion-indexer.exe child it runs the index in (ticket 583abdec: the in-process index ran the
    /// 32-bit IDE out of memory on large solutions).
    ///
    /// SHARED SOURCE: compiled into the addin, clarion-indexer.exe and clarion-mcp-server.exe, so both
    /// ends of the pipe read and write with the same code. It is IDE-free and dependency-free (no JSON
    /// library: the indexer references none).
    ///
    /// stderr, one line each, when the child runs with --protocol:
    ///   @@ca-indexer/1 {"t":"msg","text":"..."}          an OnProgress message (the header log, the run log)
    ///   @@ca-indexer/1 {"t":"ev","phase":...,...}        an OnProgressEvent (the progress window, MCP progress)
    ///   @@ca-indexer/1 {"t":"mode","incremental":true}   what will actually run (an incremental request on an
    ///                                                    older database runs full) - sent before anything is cleared
    /// Any other stderr line is console text; the host keeps the last few for an error report.
    ///
    /// stdout: one JSON object at the end of the run (<see cref="ResultToJson"/>), success or not.
    /// stdin: the line "cancel" asks for a cooperative cancel; end of input (the host died) cancels too.
    /// Exit codes: 0 indexed, 1 failed, 2 cancelled.
    /// </summary>
    public static class IndexerProtocol
    {
        public const string Prefix = "@@ca-indexer/1 ";
        public const string CancelCommand = "cancel";

        public const int ExitOk = 0;
        public const int ExitFailed = 1;
        public const int ExitCancelled = 2;

        public const string KindMessage = "msg";
        public const string KindEvent = "ev";
        public const string KindMode = "mode";

        // ------------------------------------------------------------------ progress lines

        public static string EncodeMessage(string text)
        {
            var o = new Dictionary<string, object> { { "t", KindMessage }, { "text", text } };
            return Prefix + Json.Write(o);
        }

        public static string EncodeEvent(IndexProgressEvent ev)
        {
            var o = new Dictionary<string, object>
            {
                { "t", KindEvent },
                { "phase", ev.Phase },
                { "project", ev.ProjectName },
                { "file", ev.CurrentFile },
                { "message", ev.Message },
                { "done", ev.FilesDone },
                { "total", ev.FilesTotal },
                { "symbols", ev.SymbolCount },
                { "rels", ev.RelationshipCount }
            };
            return Prefix + Json.Write(o);
        }

        public static string EncodeMode(bool incremental)
        {
            var o = new Dictionary<string, object> { { "t", KindMode }, { "incremental", incremental } };
            return Prefix + Json.Write(o);
        }

        /// <summary>One decoded protocol line. Exactly one of Message / Event / Incremental is meaningful, by Kind.</summary>
        public sealed class Line
        {
            public string Kind;
            public string Message;
            public IndexProgressEvent Event;
            public bool Incremental;
        }

        /// <summary>Decode a stderr line. False for console text and for a protocol line that does not parse
        /// (a host shows those as text rather than dropping them).</summary>
        public static bool TryDecode(string line, out Line decoded)
        {
            decoded = null;
            if (line == null || !line.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            Dictionary<string, object> o;
            try { o = Json.Parse(line.Substring(Prefix.Length)) as Dictionary<string, object>; }
            catch (FormatException) { return false; }
            if (o == null) return false;
            string kind = Json.Str(o, "t");
            switch (kind)
            {
                case KindMessage:
                    decoded = new Line { Kind = kind, Message = Json.Str(o, "text") ?? "" };
                    return true;
                case KindEvent:
                    decoded = new Line
                    {
                        Kind = kind,
                        Event = new IndexProgressEvent
                        {
                            Phase = Json.Str(o, "phase"),
                            ProjectName = Json.Str(o, "project"),
                            CurrentFile = Json.Str(o, "file"),
                            Message = Json.Str(o, "message"),
                            FilesDone = (int)Json.Num(o, "done"),
                            FilesTotal = (int)Json.Num(o, "total"),
                            SymbolCount = (int)Json.Num(o, "symbols"),
                            RelationshipCount = (int)Json.Num(o, "rels")
                        }
                    };
                    return true;
                case KindMode:
                    decoded = new Line { Kind = kind, Incremental = Json.Bool(o, "incremental") };
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ the result

        /// <summary>The stdout JSON of a finished run. Carries every IndexResult field a host formats, so
        /// <see cref="ResultFromJson"/> rebuilds an IndexResult whose Headline, ModeText and CoverageText are
        /// the in-process run's, character for character. Key names before 583abdec are unchanged.</summary>
        public static string ResultToJson(IndexResult r, string dbPath, long durationMs)
        {
            var o = new Dictionary<string, object>
            {
                { "success", true },
                { "slnPath", r.SlnPath },
                { "dbPath", dbPath },
                { "projectCount", r.ProjectCount },
                { "fileCount", r.FileCount },
                { "symbolCount", r.SymbolCount },
                { "relationshipCount", r.RelationshipCount },
                // #244 item 3: "success" means the run finished; "status" says how much it covered.
                { "status", StatusOf(r) },
                { "indexerVersion", r.IndexerVersion },
                { "featureLevel", r.FeatureLevel },
                // ef2fd2a9: what ran ("incremental" / "full"), what was asked, and -1 unless upgraded.
                { "mode", r.Incremental ? "incremental" : "full" },
                { "requestedMode", r.RequestedIncremental ? "incremental" : "full" },
                { "upgradedFromFeatureLevel", r.UpgradedFromFeatureLevel },
                { "upToDate", r.UpToDate },
                { "modeText", r.ModeText },
                { "projectsResolved", r.ProjectsResolved },
                { "projectsUnresolved", r.ProjectsUnresolved },
                { "unresolvedProjectNames", new List<object>(r.UnresolvedProjectNames) },
                { "cwprojFilesListed", r.CwprojFilesListed },
                { "cwprojFilesUnresolved", r.CwprojFilesUnresolved },
                { "filesParsed", r.FilesParsed },
                { "filesUnresolved", r.FilesUnresolved },
                { "filesSkipped", r.FilesSkipped },
                { "moduleFilesIndexed", r.ModuleFilesIndexed },
                { "moduleTargetsUnresolved", r.ModuleTargetsUnresolved },
                { "moduleTargetsSkipped", r.ModuleTargetsSkipped },
                { "includeTargetsUnresolved", r.IncludeTargetsUnresolved },
                { "unresolvedIncludeNames", new List<object>(r.UnresolvedIncludeNames) },
                { "includeTargetsConditional", r.IncludeTargetsConditional },
                { "globalDataFilesRead", r.GlobalDataFilesRead },
                { "warnings", new List<object>(r.Warnings) },
                // The indexer's own duration (IndexResult.DurationMs, what every host prints) and the
                // console's wall clock around the call ("durationMs", as before 583abdec).
                { "resultDurationMs", r.DurationMs },
                { "durationMs", durationMs }
            };
            return Json.Write(o, pretty: true);
        }

        private static string StatusOf(IndexResult r)
        {
            r.Decide();
            return r.Status;
        }

        /// <summary>The stdout JSON of a run that did not finish.</summary>
        public static string FailureToJson(Exception ex, bool cancelled, bool? effectiveIncremental)
        {
            var o = new Dictionary<string, object>
            {
                { "success", false },
                { "cancelled", cancelled },
                { "error", ex != null ? ex.Message : null },
                { "errorType", ex != null ? ex.GetType().FullName : null }
            };
            if (effectiveIncremental.HasValue)
                o["mode"] = effectiveIncremental.Value ? "incremental" : "full";
            return Json.Write(o, pretty: true);
        }

        /// <summary>The child's stdout, parsed: the result of a successful run, or what went wrong.</summary>
        public sealed class Outcome
        {
            public bool Success;
            public bool Cancelled;
            public string Error;
            public string ErrorType;
            /// <summary>null when the output did not say (an older indexer, or a run that failed before the mode was known).</summary>
            public bool? EffectiveIncremental;
            public IndexResult Result;
            public string DbPath;
        }

        /// <summary>
        /// Parse the child's stdout. Null when it holds no complete JSON object (the child died before writing one).
        /// 583abdec review D4a: when stdout holds MORE than one object (a library that printed one, a result followed
        /// by another line), the LAST complete object is the outcome; text and partial objects around it are skipped.
        /// </summary>
        public static Outcome ParseOutcome(string stdout)
        {
            Dictionary<string, object> o = LastObject(stdout);
            if (o == null) return null;

            var outcome = new Outcome
            {
                Success = Json.Bool(o, "success"),
                Cancelled = Json.Bool(o, "cancelled"),
                Error = Json.Str(o, "error"),
                ErrorType = Json.Str(o, "errorType"),
                DbPath = Json.Str(o, "dbPath")
            };
            string mode = Json.Str(o, "mode");
            if (mode == "incremental") outcome.EffectiveIncremental = true;
            else if (mode == "full") outcome.EffectiveIncremental = false;
            if (outcome.Success) outcome.Result = ResultFromDictionary(o);
            return outcome;
        }

        /// <summary>The last complete top-level JSON object in <paramref name="text"/>, or null. Scans forward: at each
        /// '{' it tries to read one object; a success skips past it (its inner braces are not candidates), a failure
        /// moves on by one character, so stray braces in console text cannot hide a later object.</summary>
        private static Dictionary<string, object> LastObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Dictionary<string, object> last = null;
            int i = text.IndexOf('{');
            while (i >= 0 && i < text.Length)
            {
                object v;
                int end;
                if (Json.TryParseAt(text, i, out v, out end) && v is Dictionary<string, object>)
                {
                    last = (Dictionary<string, object>)v;
                    i = end;
                }
                else
                {
                    i++;
                }
                i = i < text.Length ? text.IndexOf('{', i) : -1;
            }
            return last;
        }

        /// <summary>Rebuild an IndexResult from <see cref="ResultToJson"/>'s output.</summary>
        public static IndexResult ResultFromJson(string json)
        {
            var o = Json.Parse(json) as Dictionary<string, object>;
            if (o == null) throw new FormatException("indexer result is not a JSON object");
            return ResultFromDictionary(o);
        }

        private static IndexResult ResultFromDictionary(Dictionary<string, object> o)
        {
            var r = new IndexResult
            {
                SlnPath = Json.Str(o, "slnPath"),
                ProjectCount = (int)Json.Num(o, "projectCount"),
                FileCount = (int)Json.Num(o, "fileCount"),
                SymbolCount = (int)Json.Num(o, "symbolCount"),
                RelationshipCount = (int)Json.Num(o, "relationshipCount"),
                DurationMs = o.ContainsKey("resultDurationMs") ? Json.Num(o, "resultDurationMs") : Json.Num(o, "durationMs"),
                ProjectsResolved = (int)Json.Num(o, "projectsResolved"),
                ProjectsUnresolved = (int)Json.Num(o, "projectsUnresolved"),
                CwprojFilesListed = (int)Json.Num(o, "cwprojFilesListed"),
                CwprojFilesUnresolved = (int)Json.Num(o, "cwprojFilesUnresolved"),
                FilesParsed = (int)Json.Num(o, "filesParsed"),
                FilesUnresolved = (int)Json.Num(o, "filesUnresolved"),
                FilesSkipped = (int)Json.Num(o, "filesSkipped"),
                ModuleFilesIndexed = (int)Json.Num(o, "moduleFilesIndexed"),
                ModuleTargetsUnresolved = (int)Json.Num(o, "moduleTargetsUnresolved"),
                ModuleTargetsSkipped = (int)Json.Num(o, "moduleTargetsSkipped"),
                IncludeTargetsUnresolved = (int)Json.Num(o, "includeTargetsUnresolved"),
                IncludeTargetsConditional = (int)Json.Num(o, "includeTargetsConditional"),
                GlobalDataFilesRead = (int)Json.Num(o, "globalDataFilesRead"),
                IndexerVersion = Json.Str(o, "indexerVersion"),
                FeatureLevel = (int)Json.Num(o, "featureLevel"),
                RequestedIncremental = Json.Str(o, "requestedMode") == "incremental",
                Incremental = Json.Str(o, "mode") == "incremental",
                UpgradedFromFeatureLevel = o.ContainsKey("upgradedFromFeatureLevel") ? (int)Json.Num(o, "upgradedFromFeatureLevel") : -1,
                UpToDate = Json.Bool(o, "upToDate")
            };
            object names;
            if (o.TryGetValue("unresolvedProjectNames", out names) && names is List<object>)
                foreach (object n in (List<object>)names)
                    if (n is string) r.UnresolvedProjectNames.Add((string)n);
            if (o.TryGetValue("unresolvedIncludeNames", out names) && names is List<object>)
                foreach (object n in (List<object>)names)
                    if (n is string) r.UnresolvedIncludeNames.Add((string)n);
            // Status and Warnings are DERIVED from the counts (Decide), exactly as the in-process result's are.
            r.Decide();
            return r;
        }

        // ------------------------------------------------------------------ minimal JSON

        /// <summary>
        /// Just enough JSON for this protocol: objects, arrays, strings, integers, booleans, null. Strings are
        /// escaped completely (control characters as \uXXXX) so a file name with a tab or a quote cannot break
        /// a line, and non-ASCII is written as itself (the pipe is UTF-8 without a BOM at both ends).
        /// </summary>
        public static class Json
        {
            public static string Write(object value, bool pretty = false)
            {
                var sb = new StringBuilder();
                WriteValue(sb, value, pretty, 0);
                return sb.ToString();
            }

            private static void WriteValue(StringBuilder sb, object v, bool pretty, int depth)
            {
                if (v == null) { sb.Append("null"); return; }
                if (v is string) { WriteString(sb, (string)v); return; }
                if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
                if (v is int || v is long || v is short || v is byte)
                {
                    sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    return;
                }
                var dict = v as Dictionary<string, object>;
                if (dict != null)
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (pretty) { sb.Append('\n').Append(' ', (depth + 1) * 2); }
                        WriteString(sb, kv.Key);
                        sb.Append(pretty ? ": " : ":");
                        WriteValue(sb, kv.Value, pretty, depth + 1);
                    }
                    if (pretty && dict.Count > 0) sb.Append('\n').Append(' ', depth * 2);
                    sb.Append('}');
                    return;
                }
                var list = v as System.Collections.IEnumerable;
                if (list != null)
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first) sb.Append(pretty ? ", " : ",");
                        first = false;
                        WriteValue(sb, item, false, depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
                throw new ArgumentException("unsupported JSON value type: " + v.GetType().FullName);
            }

            private static void WriteString(StringBuilder sb, string s)
            {
                sb.Append('"');
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            // Control characters, and the two JS line separators, never travel raw.
                            if (c < 0x20 || c == '\u2028' || c == '\u2029')
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
            }

            /// <summary>Parse one JSON value. Throws FormatException on anything malformed or trailing.</summary>
            public static object Parse(string s)
            {
                if (s == null) throw new FormatException("no JSON");
                int i = 0;
                object v = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i != s.Length) throw new FormatException("trailing characters after JSON at " + i);
                return v;
            }

            /// <summary>Read ONE JSON value starting at <paramref name="start"/>; <paramref name="end"/> is the index just
            /// past it. False (never a throw) when no complete value starts there.</summary>
            public static bool TryParseAt(string s, int start, out object value, out int end)
            {
                value = null;
                end = start;
                if (s == null || start < 0 || start >= s.Length) return false;
                int i = start;
                try { value = ParseValue(s, ref i); }
                catch (FormatException) { return false; }
                catch (OverflowException) { return false; }
                end = i;
                return true;
            }

            private static void SkipWs(string s, ref int i)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            }

            private static object ParseValue(string s, ref int i)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end of JSON");
                char c = s[i];
                if (c == '{') return ParseObject(s, ref i);
                if (c == '[') return ParseArray(s, ref i);
                if (c == '"') return ParseString(s, ref i);
                if (c == 't') { Expect(s, ref i, "true"); return true; }
                if (c == 'f') { Expect(s, ref i, "false"); return false; }
                if (c == 'n') { Expect(s, ref i, "null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
                throw new FormatException("unexpected character '" + c + "' at " + i);
            }

            private static void Expect(string s, ref int i, string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                    throw new FormatException("expected " + word + " at " + i);
                i += word.Length;
            }

            private static Dictionary<string, object> ParseObject(string s, ref int i)
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; // {
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("expected a key at " + i);
                    string key = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                    i++;
                    o[key] = ParseValue(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("unterminated object");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("expected ',' or '}' at " + i);
                }
            }

            private static List<object> ParseArray(string s, ref int i)
            {
                var a = new List<object>();
                i++; // [
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(ParseValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("unterminated array");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("expected ',' or ']' at " + i);
                }
            }

            private static string ParseString(string s, ref int i)
            {
                var sb = new StringBuilder();
                i++; // opening quote
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("short \\u escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException("bad escape \\" + e);
                    }
                }
                throw new FormatException("unterminated string");
            }

            private static object ParseNumber(string s, ref int i)
            {
                int start = i;
                if (s[i] == '-') i++;
                while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
                string text = s.Substring(start, i - start);
                long l;
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw new FormatException("bad number '" + text + "'");
            }

            public static string Str(Dictionary<string, object> o, string key)
            {
                object v;
                return o.TryGetValue(key, out v) ? v as string : null;
            }

            public static long Num(Dictionary<string, object> o, string key)
            {
                object v;
                if (!o.TryGetValue(key, out v) || v == null) return 0;
                if (v is long) return (long)v;
                if (v is double) return (long)(double)v;
                return 0;
            }

            public static bool Bool(Dictionary<string, object> o, string key)
            {
                object v;
                return o.TryGetValue(key, out v) && v is bool && (bool)v;
            }
        }
    }
}
