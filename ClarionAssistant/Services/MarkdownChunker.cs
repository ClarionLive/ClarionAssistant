using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Splits a Markdown document into DocGraph chunks, one per ATX heading (# .. ######).
    /// Kept apart from DocGraphService (SQLite, PdfPig) so tests can compile it on its own.
    /// </summary>
    internal static class MarkdownChunker
    {
        private static readonly Regex HeadingRegex = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
        private static readonly Regex FenceRegex = new Regex(@"^\s{0,3}(```|~~~)");

        /// <summary>
        /// Chunks Markdown text by ATX heading (# .. ######). Each heading starts
        /// a new chunk that runs until the next heading or end of file. The first
        /// fenced code block in each section is extracted as CodeExample; the
        /// remaining prose has markdown syntax markers stripped for cleaner FTS.
        ///
        /// A chunk's Heading carries its parent headings, joined with " > ": in an API
        /// reference shaped "## FooInitialize Function" / "### Parameters", the
        /// Parameters section is otherwise stored under the bare heading "Parameters",
        /// its text never names the function, and a search for FooInitialize cannot
        /// find it (only the one-line summary under the ## heading). Changelogs are the
        /// same shape: every "### Fixed" would be indistinguishable from the others.
        /// A single # heading is the document's title and is left out of the path
        /// (it would otherwise prefix every chunk in the file); a document with several
        /// # headings uses them as sections, so they stay in. Anchor stays the slug of
        /// the chunk's own heading text, which is what a Markdown renderer links to.
        /// </summary>
        public static List<DocChunk> Chunk(string text, string library)
        {
            var chunks = new List<DocChunk>();
            // Normalise line endings so the splitter works on any platform.
            string normalised = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalised.Split('\n');

            bool h1IsTitle = CountTopLevelHeadings(lines) == 1;

            // headingStack[n] = the open heading at level n (1..6), or null.
            var headingStack = new string[7];
            string currentHeading = null;      // the chunk's own heading text
            string currentPath = null;         // ancestors + own heading, " > "-joined
            int currentLevel = 0;
            var buffer = new StringBuilder();
            bool inFence = false;

            Action flush = () =>
            {
                string body = buffer.ToString().Trim();
                if (string.IsNullOrEmpty(body) && string.IsNullOrEmpty(currentHeading))
                    return;

                string heading = currentPath ?? library;
                string codeExample = ExtractFirstFencedCodeBlock(body);
                string prose = StripMarkdownSyntax(body);

                if (string.IsNullOrWhiteSpace(prose) && string.IsNullOrWhiteSpace(codeExample))
                    return;

                chunks.Add(new DocChunk
                {
                    ClassName = library,
                    MethodName = null,
                    Topic = currentLevel > 0 ? ("h" + currentLevel) : "section",
                    Heading = heading,
                    Content = prose,
                    CodeExample = codeExample,
                    Signature = null,
                    Anchor = SlugifyHeading(currentHeading ?? library)
                });
            };

            foreach (string rawLine in lines)
            {
                string line = rawLine;

                // Track fenced code blocks so headings inside them are not treated as chunk boundaries.
                if (FenceRegex.IsMatch(line))
                {
                    inFence = !inFence;
                    buffer.AppendLine(line);
                    continue;
                }

                if (!inFence)
                {
                    var m = HeadingRegex.Match(line);
                    if (m.Success)
                    {
                        flush();
                        buffer.Length = 0;
                        currentLevel = m.Groups[1].Value.Length;
                        currentHeading = m.Groups[2].Value.Trim();

                        headingStack[currentLevel] = currentHeading;
                        for (int n = currentLevel + 1; n < headingStack.Length; n++)
                            headingStack[n] = null;
                        currentPath = BuildPath(headingStack, currentLevel, h1IsTitle);
                        continue;
                    }
                }

                buffer.AppendLine(line);
            }

            flush();

            // Fallback: if the file had no headings at all, store the whole file as a single chunk.
            if (chunks.Count == 0)
            {
                string body = normalised.Trim();
                string codeExample = ExtractFirstFencedCodeBlock(body);
                string prose = StripMarkdownSyntax(body);
                if (!string.IsNullOrWhiteSpace(prose) || !string.IsNullOrWhiteSpace(codeExample))
                {
                    chunks.Add(new DocChunk
                    {
                        ClassName = library,
                        MethodName = null,
                        Topic = "document",
                        Heading = library,
                        Content = prose,
                        CodeExample = codeExample,
                        Signature = null,
                        Anchor = SlugifyHeading(library)
                    });
                }
            }

            return chunks;
        }

        /// <summary>
        /// The chunk heading for a heading at <paramref name="level"/>: its open ancestors
        /// (skipped levels simply have none) followed by its own text. The document title
        /// (a lone # heading) is never part of a deeper heading's path.
        /// </summary>
        private static string BuildPath(string[] headingStack, int level, bool h1IsTitle)
        {
            // An empty heading ("### ") stays empty, as before, rather than becoming "Parent > ".
            if (string.IsNullOrEmpty(headingStack[level])) return headingStack[level];

            var parts = new List<string>();
            for (int n = 1; n < level; n++)
            {
                if (n == 1 && h1IsTitle) continue;
                if (!string.IsNullOrEmpty(headingStack[n])) parts.Add(headingStack[n]);
            }
            parts.Add(headingStack[level]);
            return string.Join(" > ", parts);
        }

        /// <summary># headings outside fenced code blocks.</summary>
        private static int CountTopLevelHeadings(string[] lines)
        {
            int count = 0;
            bool inFence = false;
            foreach (string line in lines)
            {
                if (FenceRegex.IsMatch(line)) { inFence = !inFence; continue; }
                if (inFence) continue;
                var m = HeadingRegex.Match(line);
                if (m.Success && m.Groups[1].Value.Length == 1) count++;
            }
            return count;
        }

        private static string ExtractFirstFencedCodeBlock(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = Regex.Match(body, @"(?:```|~~~)[^\n]*\n(.*?)\n\s{0,3}(?:```|~~~)", RegexOptions.Singleline);
            if (!m.Success) return null;
            string code = m.Groups[1].Value.TrimEnd();
            return string.IsNullOrWhiteSpace(code) ? null : code;
        }

        private static string StripMarkdownSyntax(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;

            // Remove fenced code blocks entirely (already captured separately).
            body = Regex.Replace(body, @"(?:```|~~~)[^\n]*\n.*?\n\s{0,3}(?:```|~~~)", "", RegexOptions.Singleline);
            // HTML comments.
            body = Regex.Replace(body, @"<!--.*?-->", "", RegexOptions.Singleline);
            // Images ![alt](url) → alt
            body = Regex.Replace(body, @"!\[([^\]]*)\]\([^\)]*\)", "$1");
            // Links [text](url) → text
            body = Regex.Replace(body, @"\[([^\]]+)\]\([^\)]*\)", "$1");
            // Reference-style link definitions: [label]: url → drop the line
            body = Regex.Replace(body, @"^\s*\[[^\]]+\]:\s*\S+.*$", "", RegexOptions.Multiline);
            // Bold / italic markers (keep the text).
            body = Regex.Replace(body, @"\*\*([^*]+)\*\*", "$1");
            body = Regex.Replace(body, @"__([^_]+)__", "$1");
            body = Regex.Replace(body, @"(?<![*\w])\*([^*\n]+)\*(?!\w)", "$1");
            body = Regex.Replace(body, @"(?<![_\w])_([^_\n]+)_(?!\w)", "$1");
            // Inline code `text` → text
            body = Regex.Replace(body, @"`([^`]+)`", "$1");
            // Blockquote markers at start of line.
            body = Regex.Replace(body, @"^\s{0,3}>\s?", "", RegexOptions.Multiline);
            // List bullets / ordered list numbers at start of line.
            body = Regex.Replace(body, @"^\s*[-*+]\s+", "", RegexOptions.Multiline);
            body = Regex.Replace(body, @"^\s*\d+\.\s+", "", RegexOptions.Multiline);
            // Horizontal rules.
            body = Regex.Replace(body, @"^\s*([-*_])\1{2,}\s*$", "", RegexOptions.Multiline);
            // Collapse runs of blank lines.
            body = Regex.Replace(body, @"\n{3,}", "\n\n");

            return body.Trim();
        }

        private static string SlugifyHeading(string heading)
        {
            if (string.IsNullOrEmpty(heading)) return null;
            string s = heading.ToLowerInvariant();
            s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
            s = Regex.Replace(s, @"\s+", "-");
            s = s.Trim('-');
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
