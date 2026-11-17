using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClarionAssistant.Services;

// DocGraph Markdown ingest: a section's chunk heading carries its parent headings.
// Before, "### Parameters" under "## FooInitialize Function" was stored as just "Parameters",
// its text never named the function, and a search for FooInitialize could not find it.
//
// The oracle below is the pre-fix chunker and its helpers, copied verbatim. The fix may change Heading and
// nothing else: same chunk count, same order, same Topic/Content/CodeExample/Anchor/ClassName.
// That is checked on the samples here and, when a folder is given (arg 1), on every .md under it.
//
// Run:  tests\Run-Tests.ps1
static class MarkdownChunkerTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    // ---- Oracle: the pre-fix DocGraphService.ChunkMarkdown, verbatim (helpers are unchanged) ----
    static List<DocChunk> Oracle(string text, string library)
    {
        var chunks = new List<DocChunk>();
        string normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalised.Split('\n');

        string currentHeading = null;
        int currentLevel = 0;
        var buffer = new StringBuilder();
        bool inFence = false;

        Action flush = () =>
        {
            string body = buffer.ToString().Trim();
            if (string.IsNullOrEmpty(body) && string.IsNullOrEmpty(currentHeading))
                return;

            string heading = currentHeading ?? library;
            string codeExample = OldExtractFirstFencedCodeBlock(body);
            string prose = OldStripMarkdownSyntax(body);

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
                Anchor = OldSlugifyHeading(heading)
            });
        };

        var headingRegex = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$");

        foreach (string rawLine in lines)
        {
            string line = rawLine;
            if (Regex.IsMatch(line, @"^\s{0,3}(```|~~~)"))
            {
                inFence = !inFence;
                buffer.AppendLine(line);
                continue;
            }
            if (!inFence)
            {
                var m = headingRegex.Match(line);
                if (m.Success)
                {
                    flush();
                    buffer.Length = 0;
                    currentLevel = m.Groups[1].Value.Length;
                    currentHeading = m.Groups[2].Value.Trim();
                    continue;
                }
            }
            buffer.AppendLine(line);
        }

        flush();

        if (chunks.Count == 0)
        {
            string body = normalised.Trim();
            string codeExample = OldExtractFirstFencedCodeBlock(body);
            string prose = OldStripMarkdownSyntax(body);
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
                    Anchor = OldSlugifyHeading(library)
                });
            }
        }

        return chunks;
    }

    // The pre-fix helpers, frozen here too, so a later change to MarkdownChunker's copies shows up.
    static string OldExtractFirstFencedCodeBlock(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = Regex.Match(body, @"(?:```|~~~)[^\n]*\n(.*?)\n\s{0,3}(?:```|~~~)", RegexOptions.Singleline);
        if (!m.Success) return null;
        string code = m.Groups[1].Value.TrimEnd();
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    static string OldStripMarkdownSyntax(string body)
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

    static string OldSlugifyHeading(string heading)
    {
        if (string.IsNullOrEmpty(heading)) return null;
        string s = heading.ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"\s+", "-");
        s = s.Trim('-');
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// Null when the new chunks match the oracle in everything but Heading, and each new
    /// heading still ends with the oracle's (its own text). Otherwise the first difference.
    /// </summary>
    static string Parity(List<DocChunk> oldC, List<DocChunk> newC)
    {
        if (oldC.Count != newC.Count) return "chunk count " + oldC.Count + " vs " + newC.Count;
        for (int i = 0; i < oldC.Count; i++)
        {
            DocChunk a = oldC[i], b = newC[i];
            if (a.Topic != b.Topic) return "#" + i + " topic " + a.Topic + " vs " + b.Topic;
            if (a.Content != b.Content) return "#" + i + " content differs (" + a.Heading + ")";
            if (a.CodeExample != b.CodeExample) return "#" + i + " code differs (" + a.Heading + ")";
            if (a.Anchor != b.Anchor) return "#" + i + " anchor " + a.Anchor + " vs " + b.Anchor;
            if (a.ClassName != b.ClassName || b.MethodName != null || b.Signature != null) return "#" + i + " class/method/signature changed";
            if (b.Heading != a.Heading && !b.Heading.EndsWith(" > " + a.Heading, StringComparison.Ordinal))
                return "#" + i + " heading \"" + b.Heading + "\" does not end with its own \"" + a.Heading + "\"";
        }
        return null;
    }

    static List<string> Headings(List<DocChunk> c) { return c.Select(x => x.Heading).ToList(); }
    static string Show(List<string> h) { return "[" + string.Join(" | ", h) + "]"; }
    static bool Same(List<string> a, params string[] b) { return a.SequenceEqual(b); }

    static void Case(string name, string md, params string[] expected)
    {
        var got = MarkdownChunker.Chunk(md, "lib");
        var h = Headings(got);
        Ok(name + ": headings", Same(h, expected), Show(h));
        string p = Parity(Oracle(md, "lib"), got);
        Ok(name + ": only headings differ from the old chunker", p == null, p);
    }

    static int Main(string[] args)
    {
        // 1. API reference: a lone # title, ## per function, ### sections. The fenced block holds
        //    a "#" line that must be neither a heading nor counted as a second # title.
        string api =
            "# Foo Library Reference\n\nFunctions of the Foo library.\n\n" +
            "## FooInitialize Function\n\nInitialize the library.\n\n" +
            "### Prototype\n\n```c\n# include \"foo.h\"\nBOOL FooInitialize(LPCTSTR lpszKey, LPVOID lpvReserved);\n```\n\n" +
            "### Parameters\n\n| Name | Description |\n|---|---|\n| `lpszKey` | The license key. |\n\n" +
            "### Return Value\n\nNon-zero on success.\n\n" +
            "## FooUninitialize Function\n\nRelease the library.\n\n" +
            "### Parameters\n\nNone.\n";
        Case("api reference", api,
            "Foo Library Reference",
            "FooInitialize Function",
            "FooInitialize Function > Prototype",
            "FooInitialize Function > Parameters",
            "FooInitialize Function > Return Value",
            "FooUninitialize Function",
            "FooUninitialize Function > Parameters");
        var apiChunks = MarkdownChunker.Chunk(api, "lib");
        var parms = apiChunks.FirstOrDefault(c => c.Heading == "FooInitialize Function > Parameters");
        Ok("api reference: a section's anchor is still its own heading", parms != null && parms.Anchor == "parameters", parms == null ? "no such chunk" : parms.Anchor);
        Ok("api reference: the prototype's fenced code is kept", (apiChunks[2].CodeExample ?? "").Contains("FooInitialize("), apiChunks[2].CodeExample);

        // 2. README: no # title at all, ## then ###.
        Case("readme without a title",
            "## Install\n\nSteps.\n\n### Windows\n\nRun the installer.\n\n### Linux\n\nNot supported.\n\n## Usage\n\nOpen it.\n",
            "Install", "Install > Windows", "Install > Linux", "Usage");

        // 3. Several # headings: they are sections, so they stay in the path.
        Case("several # headings",
            "# Part One\n\nIntro one.\n\n## Setup\n\nSetup one.\n\n# Part Two\n\nIntro two.\n\n## Setup\n\nSetup two.\n",
            "Part One", "Part One > Setup", "Part Two", "Part Two > Setup");

        // 4. Skipped levels, and a shallower heading closing the deeper ones.
        Case("skipped and closed levels",
            "## A\n\nText A.\n\n#### Deep\n\nDeep text.\n\n### B\n\nText B.\n\n## C\n\nText C.\n\n### D\n\nText D.\n",
            "A", "A > Deep", "A > B", "C", "C > D");

        // 5. Changelog: the same ### heading under every version. All must survive, distinct.
        string changelog =
            "# Changelog\n\n## [1.2.0]\n\n### Fixed\n\n- Crash on save.\n\n### Added\n\n- Dark theme.\n\n" +
            "## [1.1.0]\n\n### Fixed\n\n- Wrong line number.\n";
        Case("changelog", changelog,
            "[1.2.0] > Fixed", "[1.2.0] > Added", "[1.1.0] > Fixed");

        // 6. Unchanged shapes: text before the first heading, and a file with no headings.
        Case("text before the first heading", "Preamble text.\n\n## First\n\nBody.\n", "lib", "First");
        Case("no headings at all", "Just a paragraph of text.\n", "lib");
        var plain = MarkdownChunker.Chunk("Just a paragraph of text.\n", "lib");
        Ok("no headings at all: one chunk named after the library", plain.Count == 1 && plain[0].Heading == "lib", Show(Headings(plain)));

        // 6b. An empty heading under a parent stays empty, as before, not "Parent > ".
        Case("empty heading under a parent", "## Foo\n\nText.\n\n### \n\nOrphan text.\n", "Foo", "");

        // 7. CRLF input gives the same result as LF.
        var crlf = Headings(MarkdownChunker.Chunk(api.Replace("\n", "\r\n"), "lib"));
        Ok("CRLF input: same headings as LF", crlf.SequenceEqual(Headings(apiChunks)), Show(crlf));

        // 8. Corpus: every .md under the given folder keeps the old chunks except their headings.
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            int files = 0, chunks = 0, changed = 0;
            string firstBad = null;
            foreach (string f in Directory.EnumerateFiles(args[0], "*.md", SearchOption.AllDirectories))
            {
                string rel = f.Substring(args[0].Length);
                if (rel.Contains("\\.git\\") || rel.Contains("\\node_modules\\") || rel.Contains("\\obj\\") || rel.Contains("\\bin\\"))
                    continue;
                string text;
                try { text = File.ReadAllText(f); } catch { continue; }
                if (string.IsNullOrWhiteSpace(text)) continue;
                var o = Oracle(text, "lib");
                var n = MarkdownChunker.Chunk(text, "lib");
                files++; chunks += n.Count;
                for (int i = 0; i < Math.Min(o.Count, n.Count); i++) if (o[i].Heading != n[i].Heading) changed++;
                string p = Parity(o, n);
                if (p != null && firstBad == null) firstBad = rel + ": " + p;
            }
            Console.WriteLine("  corpus: " + files + " files, " + chunks + " chunks, " + changed + " headings gained a parent");
            Ok("corpus: found .md files to check", files > 0, args[0]);
            Ok("corpus: every file keeps its chunks, only headings differ", firstBad == null, firstBad);
        }

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail));
        return fail == 0 ? 0 : 1;
    }
}
