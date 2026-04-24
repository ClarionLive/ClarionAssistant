using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// DocGraph: ingests, chunks, and searches third-party Clarion template documentation.
    /// Stores docs in SQLite with FTS5 full-text search.
    /// </summary>
    public class DocGraphService
    {
        private string _dbPath;

        /// <summary>
        /// Gets the default DocGraph database path alongside the Clarion installation.
        /// </summary>
        public static string GetDefaultDbPath()
        {
            // Store in the ClarionAssistant data folder
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "docgraph.db");
        }

        /// <summary>
        /// Gets the personal DocGraph database path (user's own docs, never overwritten by installer).
        /// </summary>
        public static string GetPersonalDbPath()
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "personal-docgraph.db");
        }

        public string DbPath { get { return _dbPath; } }

        public DocGraphService(string dbPath = null)
        {
            _dbPath = dbPath ?? GetDefaultDbPath();
        }

        #region Database Setup

        /// <summary>
        /// Creates the DocGraph database and tables if they don't exist.
        /// </summary>
        public void EnsureDatabase()
        {
            using (var conn = OpenConnection(readOnly: false))
            {
                CreateSchema(conn);
            }
        }

        /// <summary>
        /// Rebuilds the FTS index from doc_chunks. Call after ingestion completes.
        /// Clears and repopulates the standalone FTS5 table.
        /// </summary>
        public void RebuildFtsIndex()
        {
            using (var conn = OpenConnection(readOnly: false))
            {
                // Clear existing FTS data
                using (var cmd = new SQLiteCommand("DELETE FROM doc_fts", conn))
                    cmd.ExecuteNonQuery();

                // Repopulate from doc_chunks
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO doc_fts(chunk_id, class_name, method_name, heading, content, code_example, signature)
                    SELECT CAST(id AS TEXT), class_name, method_name, heading, content, code_example, signature
                    FROM doc_chunks", conn))
                    cmd.ExecuteNonQuery();
            }
        }

        private void CreateSchema(SQLiteConnection conn)
        {
            string[] ddl = new[]
            {
                @"CREATE TABLE IF NOT EXISTS libraries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    vendor TEXT,
                    version TEXT,
                    source_path TEXT,
                    source_format TEXT,
                    ingested_at TEXT DEFAULT (datetime('now')),
                    UNIQUE(vendor, name)
                )",

                @"CREATE TABLE IF NOT EXISTS doc_chunks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    library_id INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
                    class_name TEXT,
                    method_name TEXT,
                    topic TEXT,
                    heading TEXT,
                    content TEXT,
                    code_example TEXT,
                    signature TEXT,
                    anchor TEXT,
                    UNIQUE(library_id, class_name, method_name, topic, heading)
                )",

                // Standalone FTS5 table — stores its own data, no triggers needed.
                // Populated after ingestion via RebuildFtsIndex().
                @"CREATE VIRTUAL TABLE IF NOT EXISTS doc_fts USING fts5(
                    chunk_id,
                    class_name,
                    method_name,
                    heading,
                    content,
                    code_example,
                    signature,
                    tokenize='porter unicode61'
                )",

                @"CREATE INDEX IF NOT EXISTS idx_chunks_library ON doc_chunks(library_id)",
                @"CREATE INDEX IF NOT EXISTS idx_chunks_class ON doc_chunks(class_name)",
                @"CREATE INDEX IF NOT EXISTS idx_chunks_method ON doc_chunks(method_name)"
            };

            foreach (string sql in ddl)
            {
                using (var cmd = new SQLiteCommand(sql, conn))
                    cmd.ExecuteNonQuery();
            }

            // Migration: add tags column if missing
            try
            {
                using (var cmd = new SQLiteCommand("ALTER TABLE libraries ADD COLUMN tags TEXT", conn))
                    cmd.ExecuteNonQuery();
            }
            catch (SQLiteException) { /* column already exists */ }
        }

        #endregion

        #region Auto-Discovery

        /// <summary>
        /// Resolves the Clarion installation root from a given path.
        /// Handles: exact root ("C:\Clarion12"), subfolder ("C:\Clarion12\docs"),
        /// or null (auto-detect from common install locations).
        /// Returns null if no valid Clarion root is found.
        /// </summary>
        public static string ResolveClarionRoot(string path)
        {
            // If a path was given, try it and walk up
            if (!string.IsNullOrEmpty(path))
            {
                // Exact root — has docs/ or accessory/ subfolder
                if (IsClarionRoot(path))
                    return path;

                // Walk up parent directories (handles "C:\Clarion12\docs" etc.)
                string dir = path;
                for (int i = 0; i < 3; i++)
                {
                    string parent = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(parent) || parent == dir) break;
                    if (IsClarionRoot(parent))
                        return parent;
                    dir = parent;
                }
            }

            // Auto-detect from common install locations
            string[] drives = { "C", "D", "E" };
            string[] names = { "Clarion12", "Clarion11", "Clarion10", "Clarion10v8", "SoftVelocity\\Clarion12", "SoftVelocity\\Clarion11" };

            foreach (string drive in drives)
            {
                foreach (string name in names)
                {
                    string candidate = drive + ":\\" + name;
                    if (IsClarionRoot(candidate))
                        return candidate;
                }
            }

            return null;
        }

        public static bool IsClarionRoot(string path)
        {
            if (!Directory.Exists(path)) return false;
            return Directory.Exists(Path.Combine(path, "docs"))
                || Directory.Exists(Path.Combine(path, "accessory"));
        }

        /// <summary>
        /// Discovers documentation in a Clarion installation.
        /// Scans both the core docs/ folder and accessory/Documents/ for third-party docs.
        /// </summary>
        public List<DocSource> DiscoverDocSources(string clarionRoot)
        {
            var sources = new List<DocSource>();

            // 1. Core Clarion documentation in docs/ folder
            string coreDocsRoot = Path.Combine(clarionRoot, "docs");
            if (Directory.Exists(coreDocsRoot))
            {
                // PDFs directly in docs/
                AddDirectFiles(sources, "SoftVelocity", "Clarion", coreDocsRoot);

                // Subdirectories (e.g. In-Memory-Driver, dfd)
                foreach (string subDir in Directory.GetDirectories(coreDocsRoot))
                {
                    string subName = Path.GetFileName(subDir);
                    AddDirectFiles(sources, "SoftVelocity", subName, subDir);
                }
            }

            // 2. CHM help files in bin/ folder
            string binDir = Path.Combine(clarionRoot, "bin");
            if (Directory.Exists(binDir))
            {
                foreach (string chm in Directory.GetFiles(binDir, "*.chm"))
                {
                    sources.Add(new DocSource
                    {
                        Vendor = "SoftVelocity",
                        Library = Path.GetFileNameWithoutExtension(chm),
                        FilePath = chm,
                        Format = "chm"
                    });
                }
            }

            // 3. Third-party documentation in accessory/Documents/
            string docsRoot = Path.Combine(clarionRoot, "accessory", "Documents");
            if (Directory.Exists(docsRoot))
            {
                foreach (string vendorDir in Directory.GetDirectories(docsRoot))
                {
                    string vendor = Path.GetFileName(vendorDir);

                    // Check for docs directly in vendor folder (CHM, PDF, HTML files)
                    AddDirectFiles(sources, vendor, null, vendorDir);

                    // Check subfolders (each is a library)
                    foreach (string libDir in Directory.GetDirectories(vendorDir))
                    {
                        string library = Path.GetFileName(libDir);
                        AddDirectFiles(sources, vendor, library, libDir);
                    }
                }
            }

            return sources;
        }

        private void AddDirectFiles(List<DocSource> sources, string vendor, string library, string dir)
        {
            string[] extensions = { "*.htm", "*.html", "*.chm", "*.pdf", "*.md" };
            foreach (string ext in extensions)
            {
                foreach (string file in Directory.GetFiles(dir, ext))
                {
                    string format = Path.GetExtension(file).TrimStart('.').ToLower();
                    // Each file gets its own library name (filename without extension).
                    // This prevents collisions when multiple files are in the same folder
                    // (e.g., 23 PDFs in docs/ all sharing "Clarion").
                    string libName = library ?? Path.GetFileNameWithoutExtension(file);
                    if (format == "pdf" || library == null)
                        libName = Path.GetFileNameWithoutExtension(file);
                    sources.Add(new DocSource
                    {
                        Vendor = vendor,
                        Library = libName,
                        FilePath = file,
                        Format = format
                    });
                }
            }
        }

        #endregion

        #region Ingestion

        /// <summary>
        /// Ingest documentation from a discovered source.
        /// Returns the number of chunks created.
        /// </summary>
        public int IngestSource(DocSource source)
        {
            switch (source.Format)
            {
                case "htm":
                case "html":
                    return IngestHtml(source);
                case "chm":
                    return IngestChm(source);
                case "pdf":
                    return IngestPdf(source);
                case "md":
                    return IngestMarkdown(source);
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Ingest all discovered sources from a Clarion installation.
        /// Returns summary of what was ingested.
        /// </summary>
        public string IngestAll(string clarionRoot)
        {
            EnsureDatabase();
            var sources = DiscoverDocSources(clarionRoot);
            var sb = new StringBuilder();
            int totalChunks = 0;
            int totalLibs = 0;

            foreach (var source in sources)
            {
                try
                {
                    int chunks = IngestSource(source);
                    if (chunks > 0)
                    {
                        sb.AppendLine(string.Format("  {0}/{1}: {2} chunks ({3})", source.Vendor, source.Library, chunks, source.Format));
                        totalChunks += chunks;
                        totalLibs++;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine(string.Format("  {0}/{1}: ERROR - {2}", source.Vendor, source.Library, ex.Message));
                }
            }

            // Rebuild FTS index after all sources are ingested
            try
            {
                RebuildFtsIndex();
                sb.AppendLine("  FTS index rebuilt successfully.");
            }
            catch (Exception ex)
            {
                sb.AppendLine("  FTS index rebuild ERROR: " + ex.Message);
            }

            sb.Insert(0, string.Format("Ingested {0} chunks from {1} libraries ({2} sources discovered)\n", totalChunks, totalLibs, sources.Count));
            return sb.ToString();
        }

        /// <summary>
        /// Ingest all doc files (htm, html, chm, pdf, md) found in the given folder (and subfolders).
        /// Works with ANY folder — no Clarion root detection needed.
        /// Vendor defaults to the folder name, library defaults to the filename.
        /// </summary>
        public string IngestFolder(string folderPath, string vendor = null)
        {
            if (!Directory.Exists(folderPath))
                return "Error: Folder not found: " + folderPath;

            EnsureDatabase();

            // Default vendor to the folder name
            if (string.IsNullOrEmpty(vendor))
                vendor = Path.GetFileName(folderPath.TrimEnd('\\', '/'));

            var sources = new List<DocSource>();
            string[] extensions = { "*.htm", "*.html", "*.chm", "*.pdf", "*.md" };

            // Scan the folder and all subfolders
            foreach (string ext in extensions)
            {
                foreach (string file in Directory.GetFiles(folderPath, ext, SearchOption.AllDirectories))
                {
                    string format = Path.GetExtension(file).TrimStart('.').ToLower();
                    string libName = Path.GetFileNameWithoutExtension(file);
                    sources.Add(new DocSource
                    {
                        Vendor = vendor,
                        Library = libName,
                        FilePath = file,
                        Format = format
                    });
                }
            }

            if (sources.Count == 0)
                return "No documentation files (htm, html, chm, pdf, md) found in: " + folderPath;

            var sb = new StringBuilder();
            int totalChunks = 0;
            int totalLibs = 0;

            foreach (var source in sources)
            {
                try
                {
                    int chunks = IngestSource(source);
                    if (chunks > 0)
                    {
                        sb.AppendLine(string.Format("  {0}/{1}: {2} chunks ({3})", source.Vendor, source.Library, chunks, source.Format));
                        totalChunks += chunks;
                        totalLibs++;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine(string.Format("  {0}/{1}: ERROR - {2}", source.Vendor, source.Library, ex.Message));
                }
            }

            // Rebuild FTS index
            try
            {
                RebuildFtsIndex();
                sb.AppendLine("  FTS index rebuilt successfully.");
            }
            catch (Exception ex)
            {
                sb.AppendLine("  FTS index rebuild ERROR: " + ex.Message);
            }

            sb.Insert(0, string.Format("Ingested {0} chunks from {1} libraries ({2} files found)\n", totalChunks, totalLibs, sources.Count));
            return sb.ToString();
        }

        #endregion

        #region HTML Parser (CapeSoft-style)

        private int IngestHtml(DocSource source)
        {
            string html = File.ReadAllText(source.FilePath, Encoding.Default);
            var chunks = ParseCapesoftHtml(html, source.Library);

            if (chunks.Count == 0)
            {
                // Fallback: try generic HTML chunking
                chunks = ParseGenericHtml(html, source.Library);
            }

            if (chunks.Count == 0)
                return 0;

            using (var conn = OpenConnection(readOnly: false))
            {
                long libraryId = EnsureLibrary(conn, source);
                // Clear existing chunks for this library to allow re-ingestion
                DeleteLibraryChunks(conn, libraryId);
                InsertChunks(conn, libraryId, chunks);
            }

            return chunks.Count;
        }

        /// <summary>
        /// Parses CapeSoft-style HTML documentation.
        /// These docs use h3 for method names, .methodtitle for signatures,
        /// and .sectionheading for Description/Parameters/Return Value/See Also.
        /// </summary>
        private List<DocChunk> ParseCapesoftHtml(string html, string library)
        {
            var chunks = new List<DocChunk>();

            // Detect if this is CapeSoft-style by checking for their CSS classes
            if (!html.Contains("methodtitle") && !html.Contains("sectionheading"))
                return chunks;

            // Extract the class name from the document (usually in h1 or title)
            string className = ExtractClassName(html, library);

            // Split by h3 method sections followed by afterh3 mblock div.
            // CapeSoft uses several h3 patterns:
            //   <h3><a name="X"></a>MethodName</h3>        — anchor first
            //   <h3>MethodName<a name="X"></a></h3>        — name first
            //   <h3>Name<a name="X"></a><a name="Y"></a></h3> — multiple anchors
            // We capture the entire h3 inner content, then parse anchor + name from it.
            var methodPattern = new Regex(
                @"<h3[^>]*>(.*?)</h3>\s*" +
                @"<div\s+class=""afterh3[^""]*""[^>]*>(.*?)</div>\s*(?:<a[^>]*>[^<]*</a>)?",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match m in methodPattern.Matches(html))
            {
                string h3Inner = m.Groups[1].Value;
                string body = m.Groups[2].Value;

                // Extract anchor name(s) from h3 content
                string anchor = "";
                var anchorMatch = Regex.Match(h3Inner, @"<a\s+name=""([^""]*)""\s*/?>", RegexOptions.IgnoreCase);
                if (anchorMatch.Success)
                    anchor = anchorMatch.Groups[1].Value;

                // Extract method title by stripping all tags from h3 content
                string methodTitle = CleanHtml(h3Inner).Trim();

                if (string.IsNullOrEmpty(methodTitle))
                    continue;

                // Clean the method name (remove params from title if present)
                string methodName = methodTitle.Split('(')[0].Trim();

                // Extract signature from .methodtitle span
                string signature = ExtractByClass(body, "methodtitle");

                // Extract sections by .sectionheading
                string description = ExtractSection(body, "Description");
                string parameters = ExtractSection(body, "Parameters");
                string returnValue = ExtractSection(body, "Return Value");
                string example = ExtractSection(body, "Example");
                string seeAlso = ExtractSection(body, "See also");

                // Build content: combine description + parameters + return value
                var content = new StringBuilder();
                if (!string.IsNullOrEmpty(description))
                    content.AppendLine(description);
                if (!string.IsNullOrEmpty(parameters))
                {
                    content.AppendLine("\nParameters:");
                    content.AppendLine(parameters);
                }
                if (!string.IsNullOrEmpty(returnValue))
                {
                    content.AppendLine("\nReturn Value:");
                    content.AppendLine(returnValue);
                }
                if (!string.IsNullOrEmpty(seeAlso))
                {
                    content.AppendLine("\nSee also: " + seeAlso);
                }

                chunks.Add(new DocChunk
                {
                    ClassName = className,
                    MethodName = methodName,
                    Topic = "method",
                    Heading = methodName,
                    Content = content.ToString().Trim(),
                    CodeExample = CleanHtml(example ?? ""),
                    Signature = CleanHtml(signature ?? ""),
                    Anchor = anchor
                });
            }

            // Also extract tutorial/guide sections (h3 without afterh3 mblock div)
            var sectionPattern = new Regex(
                @"<h3[^>]*>(.*?)</h3>\s*(.*?)(?=<h[23][^>]*>|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            var methodNames = new HashSet<string>(chunks.Select(c => c.MethodName), StringComparer.OrdinalIgnoreCase);

            foreach (Match m in sectionPattern.Matches(html))
            {
                string h3Inner = m.Groups[1].Value;
                string heading = CleanHtml(h3Inner).Trim();
                string body = m.Groups[2].Value;

                // Extract anchor
                string anchor = "";
                var anchorMatch2 = Regex.Match(h3Inner, @"<a\s+name=""([^""]*)""\s*/?>", RegexOptions.IgnoreCase);
                if (anchorMatch2.Success)
                    anchor = anchorMatch2.Groups[1].Value;

                // Skip if this was already captured as a method
                string possibleMethod = heading.Split('(')[0].Trim();
                if (methodNames.Contains(possibleMethod))
                    continue;

                // Skip empty or navigation-only sections
                if (string.IsNullOrEmpty(heading) || string.IsNullOrEmpty(body) || heading.Length > 100)
                    continue;

                string content = CleanHtml(body);
                if (content.Length < 30)
                    continue;

                // Extract any code blocks
                string codeExample = ExtractCodeBlocks(body);

                chunks.Add(new DocChunk
                {
                    ClassName = className,
                    MethodName = null,
                    Topic = "guide",
                    Heading = heading,
                    Content = content,
                    CodeExample = codeExample,
                    Signature = null,
                    Anchor = anchor
                });
            }

            return chunks;
        }

        /// <summary>
        /// Generic HTML parser for non-CapeSoft docs.
        /// Chunks by h2/h3 headings with their body content.
        /// </summary>
        private List<DocChunk> ParseGenericHtml(string html, string library)
        {
            var chunks = new List<DocChunk>();

            // Split by h2 or h3 headings
            var pattern = new Regex(
                @"<h[23][^>]*>(?:<a[^>]*>)?\s*(.*?)\s*(?:</a>)?\s*</h[23]>\s*(.*?)(?=<h[23]|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            var rawChunks = new List<(string heading, string body, string content, string code)>();

            foreach (Match m in pattern.Matches(html))
            {
                string heading = CleanHtml(m.Groups[1].Value).Trim();
                string body = m.Groups[2].Value;

                if (string.IsNullOrEmpty(heading) || heading.Length > 100)
                    continue;

                string content = CleanHtml(body);
                string codeExample = ExtractCodeBlocks(body);

                rawChunks.Add((heading, body, content, codeExample));
            }

            // Merge thin chunks (< 100 chars content) into the next substantial chunk.
            // This prevents table-of-contents and navigation-only sections from
            // becoming standalone results that outrank real content.
            for (int i = 0; i < rawChunks.Count; i++)
            {
                var (heading, body, content, code) = rawChunks[i];

                // If this chunk is thin, try to merge it forward into the next chunk
                if (content.Length < 100 && i + 1 < rawChunks.Count)
                {
                    var next = rawChunks[i + 1];
                    string mergedContent = content + "\n\n" + next.content;
                    string mergedCode = string.IsNullOrEmpty(code) ? next.code :
                                        string.IsNullOrEmpty(next.code) ? code :
                                        code + "\n" + next.code;
                    // Prepend the thin heading as context for the merged chunk
                    string mergedHeading = heading + " > " + next.heading;
                    rawChunks[i + 1] = (mergedHeading, next.body, mergedContent, mergedCode);
                    continue; // skip emitting this thin chunk
                }

                if (content.Length < 30)
                    continue;

                chunks.Add(new DocChunk
                {
                    ClassName = library,
                    MethodName = null,
                    Topic = "section",
                    Heading = heading,
                    Content = content.Length > 4000 ? content.Substring(0, 4000) : content,
                    CodeExample = code,
                    Signature = null,
                    Anchor = null
                });
            }

            return chunks;
        }

        /// <summary>
        /// Parses Help &amp; Manual style HTML (used by Clarion CHM files).
        /// Uses the page title as heading and extracts body content from
        /// the idcontent div or the full body as fallback.
        /// </summary>
        private List<DocChunk> ParseHelpManualHtml(string html, string library)
        {
            var chunks = new List<DocChunk>();

            // Extract title
            var titleMatch = Regex.Match(html, @"<title[^>]*>\s*(.*?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            string heading = titleMatch.Success ? CleanHtml(titleMatch.Groups[1].Value).Trim() : null;

            if (string.IsNullOrEmpty(heading) || heading.Length > 200)
                return chunks;

            // Try to extract content from idcontent div (Help & Manual standard)
            string bodyHtml = null;
            var contentMatch = Regex.Match(html, @"<div\s+id=""idcontent""[^>]*>(.*?)</div>\s*<!--ZOOMSTOP-->",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!contentMatch.Success)
                contentMatch = Regex.Match(html, @"<div\s+id=""idcontent""[^>]*>(.*)</div>\s*<script",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (contentMatch.Success)
                bodyHtml = contentMatch.Groups[1].Value;

            // Fallback: extract from the body topic table (older Help & Manual style)
            if (string.IsNullOrEmpty(bodyHtml))
            {
                var tableMatch = Regex.Match(html, @"<!-- Placeholder for topic body[^>]*-->\s*<table[^>]*>(.*?)</table>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (tableMatch.Success)
                    bodyHtml = tableMatch.Groups[1].Value;
            }

            // Last fallback: everything inside <body>
            if (string.IsNullOrEmpty(bodyHtml))
            {
                var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (bodyMatch.Success)
                    bodyHtml = bodyMatch.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(bodyHtml))
                return chunks;

            string content = CleanHtml(bodyHtml);
            string codeExample = ExtractCodeBlocks(bodyHtml);

            // Skip pages with too little content (navigation-only, TOC pages)
            if (content.Length < 50)
                return chunks;

            chunks.Add(new DocChunk
            {
                ClassName = library,
                MethodName = null,
                Topic = "help",
                Heading = heading,
                Content = content.Length > 4000 ? content.Substring(0, 4000) : content,
                CodeExample = codeExample,
                Signature = null,
                Anchor = null
            });

            return chunks;
        }

        #endregion

        #region CHM Parser

        /// <summary>
        /// Find Git Bash (bash.exe) — needed because hh.exe -decompile silently fails
        /// when launched via .NET Process.Start, but works through Git Bash.
        /// </summary>
        private static string FindGitBash()
        {
            string[] candidates = {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe",
                @"C:\Git\bin\bash.exe"
            };
            foreach (string path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private int IngestChm(DocSource source)
        {
            // CHM files are compiled HTML - decompile to temp directory, then parse HTML files
            // Use a short path under C:\Temp to avoid hh.exe issues with long paths
            string tempBase = @"C:\Temp";
            Directory.CreateDirectory(tempBase);
            string tempDir = Path.Combine(tempBase, "dg_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                Directory.CreateDirectory(tempDir);

                // hh.exe -decompile silently fails from .NET Process.Start (returns 0 but extracts nothing).
                // It works through Git Bash, so use that as the process host.
                string gitBash = FindGitBash();
                if (gitBash == null)
                    return 0;

                string unixTemp = tempDir.Replace('\\', '/');
                if (unixTemp.Length >= 2 && unixTemp[1] == ':')
                    unixTemp = "/" + char.ToLower(unixTemp[0]) + unixTemp.Substring(2);
                string unixChm = source.FilePath.Replace('\\', '/');
                if (unixChm.Length >= 2 && unixChm[1] == ':')
                    unixChm = "/" + char.ToLower(unixChm[0]) + unixChm.Substring(2);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = gitBash,
                    Arguments = string.Format("-c \"hh.exe -decompile '{0}' '{1}'\"", unixTemp, unixChm),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    if (proc != null)
                        proc.WaitForExit(120000);
                }

                // Find and parse all HTML files in the decompiled output
                var htmlFiles = Directory.GetFiles(tempDir, "*.htm", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(tempDir, "*.html", SearchOption.AllDirectories))
                    .ToArray();

                if (htmlFiles.Length == 0)
                    return 0;

                var allChunks = new List<DocChunk>();
                foreach (string htmlFile in htmlFiles)
                {
                    string html = File.ReadAllText(htmlFile, Encoding.Default);
                    var chunks = ParseCapesoftHtml(html, source.Library);
                    if (chunks.Count == 0)
                        chunks = ParseGenericHtml(html, source.Library);
                    if (chunks.Count == 0)
                        chunks = ParseHelpManualHtml(html, source.Library);
                    allChunks.AddRange(chunks);
                }

                if (allChunks.Count > 0)
                {
                    using (var conn = OpenConnection(readOnly: false))
                    {
                        long libraryId = EnsureLibrary(conn, source);
                        DeleteLibraryChunks(conn, libraryId);
                        InsertChunks(conn, libraryId, allChunks);
                    }
                }

                return allChunks.Count;
            }
            finally
            {
                // Clean up temp directory
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion

        #region PDF Parser (pdftotext)

        private int IngestPdf(DocSource source)
        {
            string text = ExtractPdfText(source.FilePath);
            if (string.IsNullOrEmpty(text) || text.Length < 50)
                return 0;

            var chunks = ChunkPdfText(text, source.Library);
            if (chunks.Count == 0)
                return 0;

            using (var conn = OpenConnection(readOnly: false))
            {
                long libraryId = EnsureLibrary(conn, source);
                DeleteLibraryChunks(conn, libraryId);
                InsertChunks(conn, libraryId, chunks);
            }

            return chunks.Count;
        }

        /// <summary>
        /// Extract text from PDF using pdftotext (xpdf/poppler).
        /// Searches known install locations since IDE process PATH may differ from shell.
        /// </summary>
        private string ExtractPdfText(string pdfPath)
        {
            string exePath = FindPdfToText();
            if (exePath == null)
                return "";

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Format("-layout \"{0}\" -", pdfPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    // Drain stderr asynchronously to prevent deadlock when buffer fills
                    proc.BeginErrorReadLine();
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(60000);
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                        return output;
                }
            }
            catch { }

            return "";
        }

        private static string _pdfToTextPath;

        /// <summary>
        /// Finds pdftotext.exe by checking known locations and PATH.
        /// Caches the result for subsequent calls.
        /// </summary>
        private static string FindPdfToText()
        {
            if (_pdfToTextPath != null)
                return _pdfToTextPath == "" ? null : _pdfToTextPath;

            // Known install locations
            string[] candidates = new[]
            {
                @"C:\Program Files\Git\mingw64\bin\pdftotext.exe",
                @"C:\Program Files (x86)\Git\mingw64\bin\pdftotext.exe",
                @"C:\msys64\mingw64\bin\pdftotext.exe",
                @"C:\poppler\bin\pdftotext.exe",
            };

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    _pdfToTextPath = path;
                    return path;
                }
            }

            // Try PATH as fallback
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pdftotext",
                    Arguments = "-v",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    proc.WaitForExit(5000);
                    _pdfToTextPath = "pdftotext";
                    return _pdfToTextPath;
                }
            }
            catch { }

            _pdfToTextPath = "";
            return null;
        }

        /// <summary>
        /// Chunks PDF text by detecting heading patterns (ALL CAPS lines, numbered chapters,
        /// keyword definitions like "KEYWORD (description)").
        /// </summary>
        private List<DocChunk> ChunkPdfText(string text, string library)
        {
            var chunks = new List<DocChunk>();
            string[] lines = text.Split('\n');

            string currentHeading = null;
            var currentContent = new StringBuilder();
            int chunkIndex = 0;

            // Patterns for Clarion PDF headings
            var chapterPattern = new Regex(@"^\s*\d+\s*-\s+(.+)$");
            var keywordPattern = new Regex(@"^\s*([A-Z][A-Z0-9_]+)\s+\((.+)\)\s*$");
            var sectionPattern = new Regex(@"^\s*([A-Z][A-Za-z\s,/]+(?:\.{2,}|:))\s*\d*\s*$");
            var allCapsHeading = new Regex(@"^\s*([A-Z][A-Z\s]{4,})\s*$");

            foreach (string line in lines)
            {
                string trimmed = line.TrimEnd();

                // Skip page numbers and very short lines
                if (Regex.IsMatch(trimmed, @"^\s*\d+\s*$"))
                    continue;

                // Detect heading patterns
                bool isHeading = false;
                string newHeading = null;

                // Chapter heading: "3 - Variable Declarations"
                var cm = chapterPattern.Match(trimmed);
                if (cm.Success)
                {
                    isHeading = true;
                    newHeading = cm.Groups[1].Value.Trim();
                }

                // Keyword definition: "LOOP (repeat statements)"
                if (!isHeading)
                {
                    var km = keywordPattern.Match(trimmed);
                    if (km.Success)
                    {
                        isHeading = true;
                        newHeading = km.Groups[1].Value.Trim();
                    }
                }

                // Section header: "Something......:" or "Something:"
                if (!isHeading)
                {
                    var sm = sectionPattern.Match(trimmed);
                    if (sm.Success)
                    {
                        isHeading = true;
                        newHeading = sm.Groups[1].Value.Trim().TrimEnd('.', ':');
                    }
                }

                // ALL CAPS heading (5+ characters)
                if (!isHeading)
                {
                    var am = allCapsHeading.Match(trimmed);
                    if (am.Success)
                    {
                        isHeading = true;
                        newHeading = am.Groups[1].Value.Trim();
                    }
                }

                if (isHeading && currentContent.Length > 30)
                {
                    chunks.Add(new DocChunk
                    {
                        ClassName = library,
                        MethodName = null,
                        Topic = "section",
                        Heading = currentHeading ?? string.Format("Section {0}", ++chunkIndex),
                        Content = currentContent.ToString().Trim(),
                        CodeExample = null,
                        Signature = null,
                        Anchor = null
                    });
                    currentContent.Clear();
                }

                if (isHeading)
                {
                    currentHeading = newHeading;
                    currentContent.AppendLine(trimmed);
                    continue;
                }

                currentContent.AppendLine(trimmed);

                // Split on size if content gets too large
                if (currentContent.Length > 3000)
                {
                    chunks.Add(new DocChunk
                    {
                        ClassName = library,
                        MethodName = null,
                        Topic = "section",
                        Heading = currentHeading ?? string.Format("Section {0}", ++chunkIndex),
                        Content = currentContent.ToString().Trim(),
                        CodeExample = null,
                        Signature = null,
                        Anchor = null
                    });
                    currentContent.Clear();
                    // Keep the heading for continuation
                    if (currentHeading != null)
                        currentHeading = currentHeading + " (cont.)";
                }
            }

            // Final chunk
            if (currentContent.Length > 30)
            {
                chunks.Add(new DocChunk
                {
                    ClassName = library,
                    MethodName = null,
                    Topic = "section",
                    Heading = currentHeading ?? string.Format("Section {0}", ++chunkIndex),
                    Content = currentContent.ToString().Trim(),
                    CodeExample = null,
                    Signature = null,
                    Anchor = null
                });
            }

            return chunks;
        }

        /// <summary>
        /// Chunks plain text by paragraph breaks or size limits.
        /// </summary>
        private List<DocChunk> ChunkPlainText(string text, string library)
        {
            var chunks = new List<DocChunk>();

            // Split by double-newlines (paragraphs) and group into reasonable chunks
            string[] paragraphs = Regex.Split(text, @"\n\s*\n");
            var currentChunk = new StringBuilder();
            int chunkIndex = 0;

            foreach (string para in paragraphs)
            {
                string trimmed = para.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                if (currentChunk.Length + trimmed.Length > 2000 && currentChunk.Length > 0)
                {
                    chunks.Add(new DocChunk
                    {
                        ClassName = library,
                        MethodName = null,
                        Topic = "section",
                        Heading = string.Format("Section {0}", ++chunkIndex),
                        Content = currentChunk.ToString().Trim(),
                        CodeExample = null,
                        Signature = null,
                        Anchor = null
                    });
                    currentChunk.Clear();
                }

                currentChunk.AppendLine(trimmed);
            }

            if (currentChunk.Length > 30)
            {
                chunks.Add(new DocChunk
                {
                    ClassName = library,
                    MethodName = null,
                    Topic = "section",
                    Heading = string.Format("Section {0}", ++chunkIndex),
                    Content = currentChunk.ToString().Trim(),
                    CodeExample = null,
                    Signature = null,
                    Anchor = null
                });
            }

            return chunks;
        }

        #endregion

        #region Markdown Parser

        private int IngestMarkdown(DocSource source)
        {
            string text;
            try
            {
                text = File.ReadAllText(source.FilePath, Encoding.UTF8);
            }
            catch
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(text))
                return 0;

            var chunks = ChunkMarkdown(text, source.Library);
            if (chunks.Count == 0)
                return 0;

            using (var conn = OpenConnection(readOnly: false))
            {
                long libraryId = EnsureLibrary(conn, source);
                DeleteLibraryChunks(conn, libraryId);
                InsertChunks(conn, libraryId, chunks);
            }

            return chunks.Count;
        }

        /// <summary>
        /// Chunks Markdown text by ATX heading (# .. ######). Each heading starts
        /// a new chunk that runs until the next heading or end of file. The first
        /// fenced code block in each section is extracted as CodeExample; the
        /// remaining prose has markdown syntax markers stripped for cleaner FTS.
        /// </summary>
        private List<DocChunk> ChunkMarkdown(string text, string library)
        {
            var chunks = new List<DocChunk>();
            // Normalise line endings so the splitter works on any platform.
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
                    Anchor = SlugifyHeading(heading)
                });
            };

            var headingRegex = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$");

            foreach (string rawLine in lines)
            {
                string line = rawLine;

                // Track fenced code blocks so headings inside them are not treated as chunk boundaries.
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

        #endregion

        #region Web Ingestion

        /// <summary>
        /// Ingest documentation from a web URL.
        /// Fetches the start page, discovers linked HTM pages, downloads and parses them.
        /// Works great for CapeSoft online docs (NetTalk, FM3, SecWin, etc.).
        /// Returns summary of what was ingested.
        /// </summary>
        public string IngestFromWeb(string startUrl, string vendor = null, string library = null)
        {
            EnsureDatabase();

            Uri startUri;
            try
            {
                startUri = new Uri(startUrl);
            }
            catch (Exception ex)
            {
                return "Error: Invalid URL - " + ex.Message;
            }

            // Auto-detect vendor/library from URL if not provided
            if (string.IsNullOrEmpty(vendor))
                vendor = DetectVendorFromUrl(startUri);
            if (string.IsNullOrEmpty(library))
                library = DetectLibraryFromUrl(startUri);

            var sb = new StringBuilder();

            // Ensure TLS 1.2 — .NET Framework defaults to TLS 1.0 which most servers reject
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // Step 1: Fetch the start page
            string startHtml;
            try
            {
                using (var client = new System.Net.WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    startHtml = client.DownloadString(startUrl);
                }
            }
            catch (Exception ex)
            {
                return "Error fetching start URL: " + ex.Message;
            }

            // Step 2: Discover linked HTM pages in the same directory
            var linkedPages = DiscoverLinkedPages(startHtml, startUri);

            // Include the start page itself
            var allPages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            allPages[startUri.GetLeftPart(UriPartial.Path)] = startHtml;

            sb.AppendLine(string.Format("Discovered {0} linked pages from {1}", linkedPages.Count, startUrl));

            // Step 3: Fetch all linked pages
            int fetchErrors = 0;
            using (var client = new System.Net.WebClient())
            {
                client.Encoding = Encoding.UTF8;
                foreach (string pageUrl in linkedPages)
                {
                    if (allPages.ContainsKey(pageUrl))
                        continue;
                    try
                    {
                        string html = client.DownloadString(pageUrl);
                        allPages[pageUrl] = html;
                    }
                    catch
                    {
                        fetchErrors++;
                    }
                }
            }

            // Step 4: Parse all pages through existing parsers
            var allChunks = new List<DocChunk>();
            int pagesWithContent = 0;

            foreach (var kvp in allPages)
            {
                string html = kvp.Value;

                var chunks = ParseCapesoftHtml(html, library);
                if (chunks.Count == 0)
                    chunks = ParseGenericHtml(html, library);

                if (chunks.Count > 0)
                {
                    pagesWithContent++;
                    allChunks.AddRange(chunks);
                }
            }

            if (allChunks.Count == 0)
                return "No documentation content could be extracted from " + startUrl;

            // Step 5: Store in database
            var source = new DocSource
            {
                Vendor = vendor,
                Library = library,
                FilePath = startUrl,
                Format = "web"
            };

            using (var conn = OpenConnection(readOnly: false))
            {
                long libraryId = EnsureLibrary(conn, source);
                DeleteLibraryChunks(conn, libraryId);
                InsertChunks(conn, libraryId, allChunks);
            }

            // Rebuild FTS index
            try
            {
                RebuildFtsIndex();
                sb.AppendLine("FTS index rebuilt successfully.");
            }
            catch (Exception ex)
            {
                sb.AppendLine("FTS index rebuild ERROR: " + ex.Message);
            }

            sb.Insert(0, string.Format("Ingested {0} chunks from {1} pages ({2}/{3})\n",
                allChunks.Count, pagesWithContent, vendor, library));
            if (fetchErrors > 0)
                sb.AppendLine(string.Format("({0} pages could not be fetched)", fetchErrors));

            return sb.ToString();
        }

        /// <summary>
        /// Discover linked HTM/HTML pages from an index page.
        /// Only follows links to files in the same directory or subdirectories.
        /// </summary>
        private List<string> DiscoverLinkedPages(string html, Uri startUri)
        {
            var pages = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Get the base directory of the start URL
            string basePath = startUri.GetLeftPart(UriPartial.Path);
            int lastSlash = basePath.LastIndexOf('/');
            string baseDir = lastSlash >= 0 ? basePath.Substring(0, lastSlash + 1) : basePath;

            // Find all href links to .htm or .html files (both single and double quotes)
            var linkPattern = new Regex(
                @"<a\s+[^>]*href=[""']([^""'#?]+\.htm[l]?)[""']",
                RegexOptions.IgnoreCase);

            foreach (Match m in linkPattern.Matches(html))
            {
                string href = m.Groups[1].Value;

                // Skip javascript, mailto, etc.
                if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Resolve relative URL
                Uri resolved;
                try
                {
                    resolved = new Uri(startUri, href);
                }
                catch
                {
                    continue;
                }

                string resolvedUrl = resolved.GetLeftPart(UriPartial.Path);

                // Only follow links on the same host
                if (!resolved.Host.Equals(startUri.Host, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only follow links in the same directory tree
                if (!resolvedUrl.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (seen.Add(resolvedUrl))
                    pages.Add(resolvedUrl);
            }

            return pages;
        }

        private string DetectVendorFromUrl(Uri uri)
        {
            string host = uri.Host.ToLower();
            if (host.Contains("capesoft")) return "Capesoft";
            if (host.Contains("icetips")) return "Icetips";
            if (host.Contains("noyantis")) return "Noyantis";
            if (host.Contains("softvelocity")) return "SoftVelocity";
            if (host.Contains("lansrad")) return "LANSRAD";
            // Default to domain name
            string[] parts = host.Split('.');
            return parts.Length >= 2 ? parts[parts.Length - 2] : host;
        }

        private string DetectLibraryFromUrl(Uri uri)
        {
            // Extract library name from URL path
            // e.g., /docs/NetTalk14/nettalkindex.htm → NetTalk14
            string[] segments = uri.Segments;
            if (segments.Length >= 2)
            {
                string dir = segments[segments.Length - 2].Trim('/');
                if (!string.IsNullOrEmpty(dir) && dir != "docs" && dir != "accessories")
                    return dir;
            }
            return Path.GetFileNameWithoutExtension(uri.LocalPath);
        }

        #endregion

        #region Query

        /// <summary>
        /// Search documentation using FTS5 full-text search.
        /// Returns matching chunks ranked by relevance.
        /// </summary>
        public string QueryDocs(string query, string library = null, string className = null, int limit = 10)
        {
            if (!File.Exists(_dbPath))
                return "Error: DocGraph database not found. Run ingest_docs first.";

            // Sanitize query for FTS5
            string ftsQuery = SanitizeFtsQuery(query);
            if (string.IsNullOrEmpty(ftsQuery))
                return "Error: invalid search query";

            var sb = new StringBuilder();
            using (var conn = OpenConnection(readOnly: true))
            {
                // FTS5 match with composite relevance scoring:
                // 1. Exact method/heading matches get priority tier (low number = better)
                // 2. Within a tier, FTS5 BM25 rank scores by term frequency & doc length
                // 3. Bonus for content-rich chunks (penalize thin TOC/nav chunks)
                string firstWord = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? query;

                string sql = @"
                    SELECT
                        dc.id,
                        l.vendor,
                        l.name as library,
                        dc.class_name,
                        dc.method_name,
                        dc.topic,
                        dc.heading,
                        dc.signature,
                        dc.content,
                        dc.code_example,
                        CASE
                            WHEN dc.method_name = @exact THEN 0
                            WHEN dc.method_name LIKE '%' || @exact || '%' THEN 1
                            WHEN dc.heading = @exact THEN 2
                            WHEN dc.heading LIKE '%' || @exact || '%' THEN 3
                            WHEN dc.signature LIKE '%' || @exact || '%' THEN 4
                            ELSE 5
                        END as tier,
                        rank as bm25_score,
                        LENGTH(dc.content) as content_len
                    FROM doc_fts
                    JOIN doc_chunks dc ON dc.id = CAST(doc_fts.chunk_id AS INTEGER)
                    JOIN libraries l ON l.id = dc.library_id
                    WHERE doc_fts MATCH @query";

                var parameters = new List<SQLiteParameter>();
                parameters.Add(new SQLiteParameter("@query", ftsQuery));
                parameters.Add(new SQLiteParameter("@exact", firstWord));

                if (!string.IsNullOrEmpty(library))
                {
                    sql += " AND l.name = @library";
                    parameters.Add(new SQLiteParameter("@library", library));
                }
                if (!string.IsNullOrEmpty(className))
                {
                    sql += " AND dc.class_name = @class";
                    parameters.Add(new SQLiteParameter("@class", className));
                }

                // Sort by: tier first, then BM25 within tier (rank is negative, lower = better),
                // then prefer longer content as tiebreaker
                sql += " ORDER BY tier, bm25_score, content_len DESC LIMIT @limit";
                parameters.Add(new SQLiteParameter("@limit", limit));

                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    foreach (var p in parameters)
                        cmd.Parameters.Add(p);

                    using (var reader = cmd.ExecuteReader())
                    {
                        int count = 0;
                        while (reader.Read())
                        {
                            count++;
                            string vendor = reader["vendor"] as string ?? "";
                            string lib = reader["library"] as string ?? "";
                            string cls = reader["class_name"] as string ?? "";
                            string method = reader["method_name"] as string ?? "";
                            string topic = reader["topic"] as string ?? "";
                            string heading = reader["heading"] as string ?? "";
                            string sig = reader["signature"] as string ?? "";
                            string content = reader["content"] as string ?? "";
                            string code = reader["code_example"] as string ?? "";

                            sb.AppendLine(string.Format("--- Result {0} [{1}/{2}] ---", count, vendor, lib));
                            if (!string.IsNullOrEmpty(cls))
                                sb.AppendLine("Class: " + cls);
                            if (!string.IsNullOrEmpty(method))
                                sb.AppendLine("Method: " + method);
                            if (!string.IsNullOrEmpty(sig))
                                sb.AppendLine("Signature: " + sig);
                            sb.AppendLine("Topic: " + topic + " | " + heading);
                            sb.AppendLine();
                            sb.AppendLine(content);
                            if (!string.IsNullOrEmpty(code))
                            {
                                sb.AppendLine();
                                sb.AppendLine("Example:");
                                sb.AppendLine(code);
                            }
                            sb.AppendLine();
                        }

                        if (count == 0)
                            return "No documentation found for: " + query;
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Query both bundled and personal DocGraph databases using ATTACH DATABASE.
        /// Falls back to single-DB query if one database is missing.
        /// </summary>
        public string QueryDocsMulti(string personalDbPath, string query, string library = null, string className = null, int limit = 10)
        {
            bool hasBundled = File.Exists(_dbPath);
            bool hasPersonal = !string.IsNullOrEmpty(personalDbPath) && File.Exists(personalDbPath);

            if (!hasBundled && !hasPersonal)
                return "Error: No DocGraph databases found. Run ingest_docs first.";

            // If only one DB exists, use the simple query
            if (!hasPersonal) return QueryDocs(query, library, className, limit);
            if (!hasBundled)
            {
                var personalSvc = new DocGraphService(personalDbPath);
                return personalSvc.QueryDocs(query, library, className, limit);
            }

            // Both exist — query each independently and merge results
            // (FTS5 virtual tables cannot be referenced via ATTACH schema prefix)
            var bundledResults = QueryDocs(query, library, className, limit);
            var personalSvc2 = new DocGraphService(personalDbPath);
            var personalResults = personalSvc2.QueryDocs(query, library, className, limit);

            // If one returned nothing, return the other
            bool bundledEmpty = string.IsNullOrEmpty(bundledResults) || bundledResults.StartsWith("No results") || bundledResults.StartsWith("Error");
            bool personalEmpty = string.IsNullOrEmpty(personalResults) || personalResults.StartsWith("No results") || personalResults.StartsWith("Error");

            if (bundledEmpty && personalEmpty) return "No results found for: " + query;
            if (personalEmpty) return bundledResults;
            if (bundledEmpty) return personalResults;

            // Merge: show personal results first, then bundled
            var sb = new StringBuilder();
            sb.AppendLine("## Personal DocGraph Results");
            sb.AppendLine(personalResults);
            sb.AppendLine();
            sb.AppendLine("## Bundled DocGraph Results");
            sb.AppendLine(bundledResults);
            return sb.ToString();
        }

        /// <summary>
        /// List all ingested libraries.
        /// </summary>
        public string ListLibraries()
        {
            if (!File.Exists(_dbPath))
                return "No DocGraph database found. Run ingest_docs first.";

            var sb = new StringBuilder();
            using (var conn = OpenConnection(readOnly: true))
            {
                string sql = @"
                    SELECT l.vendor, l.name, l.source_format, l.ingested_at,
                           COUNT(dc.id) as chunk_count
                    FROM libraries l
                    LEFT JOIN doc_chunks dc ON dc.library_id = l.id
                    GROUP BY l.id
                    ORDER BY l.vendor, l.name";

                using (var cmd = new SQLiteCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    sb.AppendLine("Vendor\tLibrary\tFormat\tChunks\tIngested");
                    while (reader.Read())
                    {
                        sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}",
                            reader["vendor"],
                            reader["name"],
                            reader["source_format"],
                            reader["chunk_count"],
                            reader["ingested_at"]));
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Get statistics about the DocGraph database.
        /// </summary>
        public string GetStats()
        {
            if (!File.Exists(_dbPath))
                return "No DocGraph database found. Run ingest_docs first.";

            var sb = new StringBuilder();
            using (var conn = OpenConnection(readOnly: true))
            {
                // Library count
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM libraries", conn))
                    sb.AppendLine("Libraries: " + cmd.ExecuteScalar());

                // Chunk count
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM doc_chunks", conn))
                    sb.AppendLine("Total chunks: " + cmd.ExecuteScalar());

                // Chunks by topic
                using (var cmd = new SQLiteCommand("SELECT topic, COUNT(*) as cnt FROM doc_chunks GROUP BY topic ORDER BY cnt DESC", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    sb.AppendLine("\nBy topic:");
                    while (reader.Read())
                        sb.AppendLine(string.Format("  {0}: {1}", reader["topic"], reader["cnt"]));
                }

                // Top libraries by chunk count
                using (var cmd = new SQLiteCommand(
                    @"SELECT l.vendor || '/' || l.name as lib, COUNT(dc.id) as cnt
                      FROM libraries l JOIN doc_chunks dc ON dc.library_id = l.id
                      GROUP BY l.id ORDER BY cnt DESC LIMIT 10", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    sb.AppendLine("\nTop libraries:");
                    while (reader.Read())
                        sb.AppendLine(string.Format("  {0}: {1} chunks", reader["lib"], reader["cnt"]));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Delete a library and all its chunks from the database. Rebuilds FTS index.
        /// </summary>
        public void DeleteLibrary(long libraryId)
        {
            DeleteLibraryCore(libraryId);
            RebuildFtsIndex();
        }

        /// <summary>
        /// Delete multiple libraries in a batch with a single FTS rebuild at the end.
        /// </summary>
        public void DeleteLibraries(IEnumerable<long> libraryIds)
        {
            foreach (long id in libraryIds)
                DeleteLibraryCore(id);
            RebuildFtsIndex();
        }

        private void DeleteLibraryCore(long libraryId)
        {
            using (var conn = OpenConnection(readOnly: false))
            {
                DeleteLibraryChunks(conn, libraryId);
                using (var cmd = new SQLiteCommand("DELETE FROM libraries WHERE id = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", libraryId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Update the tags on a library.
        /// </summary>
        public void UpdateLibraryTags(long libraryId, string tags)
        {
            using (var conn = OpenConnection(readOnly: false))
            using (var cmd = new SQLiteCommand("UPDATE libraries SET tags = @tags WHERE id = @id", conn))
            {
                cmd.Parameters.AddWithValue("@tags", (object)tags ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", libraryId);
                cmd.ExecuteNonQuery();
            }
        }

        #endregion

        #region HTML Helpers

        private string ExtractClassName(string html, string fallback)
        {
            // Try to find class name in title or h1
            var titleMatch = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                string title = CleanHtml(titleMatch.Groups[1].Value);
                // "StringTheory Complete Documentation" → "StringTheory"
                string firstWord = title.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstWord) && firstWord.Length > 2)
                    return firstWord;
            }

            return fallback;
        }

        /// <summary>
        /// Extract text content from an element with the given CSS class.
        /// </summary>
        private string ExtractByClass(string html, string cssClass)
        {
            var pattern = new Regex(
                string.Format(@"<span\s+class=""{0}""[^>]*>(.*?)</span>", Regex.Escape(cssClass)),
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var match = pattern.Match(html);
            return match.Success ? CleanHtml(match.Groups[1].Value) : null;
        }

        /// <summary>
        /// Extract a named section (e.g., "Description", "Parameters") from a method body.
        /// Sections are delimited by .sectionheading spans.
        /// </summary>
        private string ExtractSection(string html, string sectionName)
        {
            // Find the section heading, then capture everything until the next section heading or end
            var pattern = new Regex(
                string.Format(
                    @"<span\s+class=""sectionheading""[^>]*>\s*{0}\s*</span>\s*(?:<br\s*/?>)*\s*(.*?)(?=<span\s+class=""sectionheading""|$)",
                    Regex.Escape(sectionName)),
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            var match = pattern.Match(html);
            if (!match.Success)
                return null;

            string content = match.Groups[1].Value.Trim();

            // If this section contains a table (Parameters), extract it specially
            if (content.Contains("<table"))
                return ExtractTable(content);

            return CleanHtml(content);
        }

        /// <summary>
        /// Extracts a table into a readable text format.
        /// </summary>
        private string ExtractTable(string html)
        {
            var sb = new StringBuilder();
            var rowPattern = new Regex(@"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var cellPattern = new Regex(@"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match row in rowPattern.Matches(html))
            {
                var cells = cellPattern.Matches(row.Groups[1].Value);
                var cellTexts = new List<string>();
                foreach (Match cell in cells)
                    cellTexts.Add(CleanHtml(cell.Groups[1].Value).Trim());

                if (cellTexts.Count >= 2)
                    sb.AppendLine(string.Format("  {0} — {1}", cellTexts[0], string.Join(" ", cellTexts.Skip(1))));
                else if (cellTexts.Count == 1)
                    sb.AppendLine("  " + cellTexts[0]);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Extract code blocks from HTML (pre, code, or .code-class elements).
        /// </summary>
        private string ExtractCodeBlocks(string html)
        {
            var sb = new StringBuilder();

            // Pre-formatted code blocks
            var prePattern = new Regex(@"<pre[^>]*>(.*?)</pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match m in prePattern.Matches(html))
            {
                string code = CleanHtml(m.Groups[1].Value);
                if (code.Length > 10)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine(code);
                }
            }

            // Code elements
            var codePattern = new Regex(@"<code[^>]*>(.*?)</code>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match m in codePattern.Matches(html))
            {
                string code = CleanHtml(m.Groups[1].Value);
                if (code.Length > 20)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine(code);
                }
            }

            return sb.Length > 0 ? sb.ToString().Trim() : null;
        }

        /// <summary>
        /// Strips HTML tags and decodes entities, producing clean text.
        /// </summary>
        private string CleanHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return "";

            // Remove script and style blocks
            string result = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // Replace <br> and </p> with newlines
            result = Regex.Replace(result, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"</p>", "\n", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"</li>", "\n", RegexOptions.IgnoreCase);

            // Remove all remaining tags
            result = Regex.Replace(result, @"<[^>]+>", "");

            // Decode common HTML entities
            result = result.Replace("&amp;", "&")
                           .Replace("&lt;", "<")
                           .Replace("&gt;", ">")
                           .Replace("&quot;", "\"")
                           .Replace("&nbsp;", " ")
                           .Replace("&#39;", "'")
                           .Replace("&apos;", "'")
                           .Replace("\u00A0", " "); // non-breaking space

            // Collapse multiple whitespace
            result = Regex.Replace(result, @"[ \t]+", " ");
            result = Regex.Replace(result, @"\n\s*\n\s*\n", "\n\n");

            return result.Trim();
        }

        /// <summary>
        /// Sanitize a user query for FTS5.
        /// Wraps individual words in quotes for safe matching.
        /// </summary>
        /// <summary>
        /// Common words that add noise to FTS queries without improving relevance.
        /// </summary>
        private static readonly HashSet<string> _stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "shall", "can", "need", "dare", "ought",
            "to", "of", "in", "for", "on", "with", "at", "by", "from", "as",
            "into", "through", "during", "before", "after", "above", "below",
            "between", "out", "off", "over", "under", "again", "further", "then",
            "once", "here", "there", "when", "where", "why", "how", "all", "each",
            "every", "both", "few", "more", "most", "other", "some", "such", "no",
            "not", "only", "own", "same", "so", "than", "too", "very", "just",
            "about", "up", "what", "which", "who", "whom", "this", "that", "these",
            "those", "i", "me", "my", "we", "our", "you", "your", "he", "him",
            "his", "she", "her", "it", "its", "they", "them", "their", "and",
            "but", "or", "if", "while", "because", "until", "although"
        };

        private string SanitizeFtsQuery(string query)
        {
            if (string.IsNullOrEmpty(query))
                return null;

            // Split into words, remove stop words, wrap each in quotes for safe FTS5 matching
            var words = query.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Replace("\"", "").Trim())
                .Where(w => w.Length > 0 && !_stopWords.Contains(w))
                .ToArray();

            if (words.Length == 0)
            {
                // All words were stop words — fall back to the original words
                words = query.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Replace("\"", "").Trim())
                    .Where(w => w.Length > 0)
                    .ToArray();
            }

            if (words.Length == 0)
                return null;

            // Use OR between words for broad matching; FTS5 rank() (BM25) will
            // naturally prefer chunks that match MORE of these terms.
            var quoted = words.Select(w => "\"" + w + "\"");
            return string.Join(" OR ", quoted);
        }

        #endregion

        #region Database Helpers

        // Cached absolute path to the native SQLite.Interop.dll alongside the
        // System.Data.SQLite managed assembly. Using an absolute path avoids the
        // silent LoadLibrary-search-order failures that would otherwise surface
        // as SQLITE_CORRUPT_VTAB ("database disk image is malformed") on later
        // MATCH queries against doc_fts. Resolved once per process.
        private static string _fts5InteropPath;

        // Set after a smoke probe confirms FTS5 is actually registered on a
        // freshly-opened connection — not just that LoadExtension didn't throw.
        private static bool _fts5SmokeChecked;

        private SQLiteConnection OpenConnection(bool readOnly)
        {
            string mode = readOnly ? "Read Only=True;" : "";
            string connStr = "Data Source=" + _dbPath + ";Version=3;" + mode + "Journal Mode=WAL;";
            var conn = new SQLiteConnection(connStr);
            conn.Open();

            // FTS5 is compiled into SQLite.Interop.dll as a loadable extension
            // (not a built-in module). Load it on every connection — the binding
            // keeps the extension registration per-connection, not per-process.
            string interopPath = ResolveInteropPath();
            conn.EnableExtensions(true);
            try
            {
                conn.LoadExtension(interopPath, "sqlite3_fts5_init");
            }
            catch (Exception ex)
            {
                try { conn.Dispose(); } catch { }
                throw new InvalidOperationException(
                    "DocGraph: failed to load FTS5 extension from '" + interopPath +
                    "'. Search will not work until this is fixed. Inner: " + ex.Message, ex);
            }

            // First-use smoke test. If the load "succeeded" but the fts5 module
            // didn't actually register (some SQLite.Interop builds), a later
            // MATCH query returns the misleading "database disk image is
            // malformed" — fail here with a clear error instead.
            if (!_fts5SmokeChecked)
            {
                try
                {
                    using (var probe = new SQLiteCommand(
                        "CREATE VIRTUAL TABLE temp.__ca_fts5_probe__ USING fts5(t); " +
                        "DROP TABLE temp.__ca_fts5_probe__;", conn))
                        probe.ExecuteNonQuery();
                    _fts5SmokeChecked = true;
                }
                catch (Exception ex)
                {
                    try { conn.Dispose(); } catch { }
                    throw new InvalidOperationException(
                        "DocGraph: FTS5 extension loaded but the 'fts5' virtual-table " +
                        "module did not register. MATCH queries will report " +
                        "'database disk image is malformed' — the data is fine, the " +
                        "extension is not. Interop: '" + interopPath +
                        "'. Inner: " + ex.Message, ex);
                }
            }

            return conn;
        }

        private static string ResolveInteropPath()
        {
            if (_fts5InteropPath != null) return _fts5InteropPath;

            // SQLite.Interop.dll is native; it ships next to the System.Data.SQLite
            // managed assembly or in an arch subfolder. Prefer the absolute path
            // so the load doesn't depend on the host process's CWD or LoadLibrary
            // search order (the IDE addin runs under the Clarion IDE's own path).
            string asmDir = null;
            try
            {
                asmDir = Path.GetDirectoryName(typeof(SQLiteConnection).Assembly.Location);
            }
            catch { }

            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            var candidates = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(asmDir))
            {
                candidates.Add(Path.Combine(asmDir, "SQLite.Interop.dll"));
                candidates.Add(Path.Combine(asmDir, arch, "SQLite.Interop.dll"));
            }

            foreach (string c in candidates)
            {
                if (!string.IsNullOrEmpty(c) && File.Exists(c))
                {
                    _fts5InteropPath = c;
                    return c;
                }
            }

            // Last-resort fallback: the original relative-name behavior. Works if
            // the DLL is discoverable via the standard LoadLibrary search path.
            _fts5InteropPath = "SQLite.Interop.dll";
            return _fts5InteropPath;
        }

        private long EnsureLibrary(SQLiteConnection conn, DocSource source)
        {
            // Try to find existing
            using (var cmd = new SQLiteCommand(
                "SELECT id FROM libraries WHERE vendor = @vendor AND name = @name", conn))
            {
                cmd.Parameters.AddWithValue("@vendor", source.Vendor);
                cmd.Parameters.AddWithValue("@name", source.Library);
                object result = cmd.ExecuteScalar();
                if (result != null)
                    return Convert.ToInt64(result);
            }

            // Insert new
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO libraries (name, vendor, source_path, source_format)
                  VALUES (@name, @vendor, @path, @format); SELECT last_insert_rowid();", conn))
            {
                cmd.Parameters.AddWithValue("@name", source.Library);
                cmd.Parameters.AddWithValue("@vendor", source.Vendor);
                cmd.Parameters.AddWithValue("@path", source.FilePath);
                cmd.Parameters.AddWithValue("@format", source.Format);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        private void DeleteLibraryChunks(SQLiteConnection conn, long libraryId)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM doc_chunks WHERE library_id = @id", conn))
            {
                cmd.Parameters.AddWithValue("@id", libraryId);
                cmd.ExecuteNonQuery();
            }
        }

        private void InsertChunks(SQLiteConnection conn, long libraryId, List<DocChunk> chunks)
        {
            using (var txn = conn.BeginTransaction())
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT OR REPLACE INTO doc_chunks
                        (library_id, class_name, method_name, topic, heading, content, code_example, signature, anchor)
                    VALUES
                        (@lib, @cls, @method, @topic, @heading, @content, @code, @sig, @anchor)", conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter("@lib"));
                    cmd.Parameters.Add(new SQLiteParameter("@cls"));
                    cmd.Parameters.Add(new SQLiteParameter("@method"));
                    cmd.Parameters.Add(new SQLiteParameter("@topic"));
                    cmd.Parameters.Add(new SQLiteParameter("@heading"));
                    cmd.Parameters.Add(new SQLiteParameter("@content"));
                    cmd.Parameters.Add(new SQLiteParameter("@code"));
                    cmd.Parameters.Add(new SQLiteParameter("@sig"));
                    cmd.Parameters.Add(new SQLiteParameter("@anchor"));

                    foreach (var chunk in chunks)
                    {
                        cmd.Parameters["@lib"].Value = libraryId;
                        cmd.Parameters["@cls"].Value = (object)chunk.ClassName ?? DBNull.Value;
                        cmd.Parameters["@method"].Value = (object)chunk.MethodName ?? DBNull.Value;
                        cmd.Parameters["@topic"].Value = (object)chunk.Topic ?? DBNull.Value;
                        cmd.Parameters["@heading"].Value = (object)chunk.Heading ?? DBNull.Value;
                        cmd.Parameters["@content"].Value = (object)chunk.Content ?? DBNull.Value;
                        cmd.Parameters["@code"].Value = (object)chunk.CodeExample ?? DBNull.Value;
                        cmd.Parameters["@sig"].Value = (object)chunk.Signature ?? DBNull.Value;
                        cmd.Parameters["@anchor"].Value = (object)chunk.Anchor ?? DBNull.Value;
                        cmd.ExecuteNonQuery();
                    }
                }

                txn.Commit();
            }
        }

        #endregion
    }

    #region Models

    public class DocSource
    {
        public string Vendor { get; set; }
        public string Library { get; set; }
        public string FilePath { get; set; }
        public string Format { get; set; }
    }

    public class DocChunk
    {
        public string ClassName { get; set; }
        public string MethodName { get; set; }
        public string Topic { get; set; }
        public string Heading { get; set; }
        public string Content { get; set; }
        public string CodeExample { get; set; }
        public string Signature { get; set; }
        public string Anchor { get; set; }
    }

    #endregion
}
