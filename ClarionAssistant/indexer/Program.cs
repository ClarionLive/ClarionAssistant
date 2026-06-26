using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using ClarionCodeGraph.Graph;

namespace ClarionIndexer
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            string command = args[0].ToLower();

            switch (command)
            {
                case "index":
                    return RunIndex(args);
                case "query":
                    return RunQuery(args);
                case "stats":
                    return RunStats(args);
                default:
                    // Treat first arg as solution path for backwards compatibility
                    if (File.Exists(args[0]) && args[0].EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    {
                        return RunIndex(new[] { "index", args[0] });
                    }
                    Console.Error.WriteLine("Unknown command: " + command);
                    PrintUsage();
                    return 1;
            }
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine("ClarionIndexer - Build and query Clarion code graph databases");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  clarion-indexer index <solution.sln> [--db <path>] [--incremental] [--lib-paths \"path1;path2\"]");
            Console.Error.WriteLine("  clarion-indexer query <db-path> <command> [args...]");
            Console.Error.WriteLine("  clarion-indexer stats <db-path>");
            Console.Error.WriteLine("  clarion-indexer <solution.sln>              (shorthand for index)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Index Commands:");
            Console.Error.WriteLine("  index          Parse a Clarion solution and build the code graph database");
            Console.Error.WriteLine("    --db <path>      Custom database output path (default: <sln-dir>/<sln-name>.codegraph.db)");
            Console.Error.WriteLine("    --incremental    Only re-index changed projects");
            Console.Error.WriteLine("    --lib-paths      Semicolon-delimited library .inc search directories");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Query Commands:");
            Console.Error.WriteLine("  query <db> find-symbol <name>          Search symbols by name");
            Console.Error.WriteLine("  query <db> callers <symbol-id>         Find callers of a symbol");
            Console.Error.WriteLine("  query <db> callers-tree <symbol-id>    Caller tree (recursive)");
            Console.Error.WriteLine("  query <db> callees <symbol-id>         Find callees of a symbol");
            Console.Error.WriteLine("  query <db> callees-tree <symbol-id>    Callee tree (recursive)");
            Console.Error.WriteLine("  query <db> file-symbols <file-path>    Symbols in a file");
            Console.Error.WriteLine("  query <db> dead-code                   Find unreferenced procedures");
            Console.Error.WriteLine("  query <db> inheritance <class-id>      Class inheritance tree");
            Console.Error.WriteLine("  query <db> impact <symbol-id> [depth]  Impact analysis (transitive callers)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Stats Commands:");
            Console.Error.WriteLine("  stats <db>     Show index statistics");
        }

        static int RunIndex(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Error: Solution path required");
                return 1;
            }

            string slnPath = Path.GetFullPath(args[1]);
            string dbPath = null;
            bool incremental = false;
            List<string> libraryPaths = null;

            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--db" && i + 1 < args.Length)
                {
                    dbPath = Path.GetFullPath(args[++i]);
                }
                else if (args[i] == "--incremental")
                {
                    incremental = true;
                }
                else if (args[i] == "--lib-paths" && i + 1 < args.Length)
                {
                    string raw = args[++i];
                    libraryPaths = new List<string>();
                    foreach (string p in raw.Split(';'))
                    {
                        string trimmed = p.Trim();
                        if (trimmed.Length > 0)
                            libraryPaths.Add(Path.GetFullPath(trimmed));
                    }
                }
            }

            if (!File.Exists(slnPath))
            {
                Console.Error.WriteLine("Error: Solution file not found: " + slnPath);
                return 1;
            }

            if (dbPath == null)
            {
                dbPath = Path.Combine(
                    Path.GetDirectoryName(slnPath),
                    Path.GetFileNameWithoutExtension(slnPath) + ".codegraph.db");
            }

            Console.Error.WriteLine("Indexing: " + slnPath);
            Console.Error.WriteLine("Database: " + dbPath);
            Console.Error.WriteLine("Mode: " + (incremental ? "incremental" : "full"));
            if (libraryPaths != null && libraryPaths.Count > 0)
                Console.Error.WriteLine("Library paths: " + string.Join("; ", libraryPaths));
            Console.Error.WriteLine();

            try
            {
                var db = new CodeGraphDatabase();
                db.Open(dbPath);

                var indexer = new CodeGraphIndexer(db);
                indexer.OnProgress += msg => Console.Error.WriteLine("  " + msg);

                var sw = Stopwatch.StartNew();
                var result = indexer.IndexSolution(slnPath, incremental, libraryPaths);
                sw.Stop();

                // Output JSON result to stdout for LSP server to consume
                Console.WriteLine("{");
                Console.WriteLine("  \"success\": true,");
                Console.WriteLine("  \"slnPath\": " + JsonEscape(result.SlnPath) + ",");
                Console.WriteLine("  \"dbPath\": " + JsonEscape(dbPath) + ",");
                Console.WriteLine("  \"projectCount\": " + result.ProjectCount + ",");
                Console.WriteLine("  \"fileCount\": " + result.FileCount + ",");
                Console.WriteLine("  \"symbolCount\": " + result.SymbolCount + ",");
                Console.WriteLine("  \"durationMs\": " + sw.ElapsedMilliseconds);
                Console.WriteLine("}");

                Console.Error.WriteLine();
                Console.Error.WriteLine("Done: " + result.SymbolCount + " symbols, " +
                    result.FileCount + " files, " + result.ProjectCount + " projects in " +
                    sw.ElapsedMilliseconds + "ms");

                db.Close();
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("{");
                Console.WriteLine("  \"success\": false,");
                Console.WriteLine("  \"error\": " + JsonEscape(ex.Message));
                Console.WriteLine("}");
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        static int RunQuery(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Error: Database path and query command required");
                return 1;
            }

            string dbPath = Path.GetFullPath(args[1]);
            string queryCmd = args[2].ToLower();

            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine("Error: Database not found: " + dbPath);
                return 1;
            }

            try
            {
                var db = new CodeGraphDatabase();
                db.Open(dbPath);
                var query = new CodeGraphQuery(db);

                DataTable result = null;

                switch (queryCmd)
                {
                    case "find-symbol":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol name required"); return 1; }
                        result = query.FindSymbol(args[3]);
                        break;

                    case "callers":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        result = query.GetCallers(long.Parse(args[3]));
                        break;

                    case "callers-tree":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        result = query.GetCallersTree(long.Parse(args[3]));
                        break;

                    case "callees":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        result = query.GetCallees(long.Parse(args[3]));
                        break;

                    case "callees-tree":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        result = query.GetCalleesTree(long.Parse(args[3]));
                        break;

                    case "file-symbols":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: File path required"); return 1; }
                        result = query.GetFileSymbols(args[3]);
                        break;

                    case "dead-code":
                        result = query.GetDeadCode();
                        break;

                    case "inheritance":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Class ID required"); return 1; }
                        result = query.GetInheritanceTree(long.Parse(args[3]));
                        break;

                    case "impact":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        int depth = args.Length >= 5 ? int.Parse(args[4]) : 10;
                        result = query.GetImpact(long.Parse(args[3]), depth);
                        break;

                    default:
                        Console.Error.WriteLine("Unknown query command: " + queryCmd);
                        return 1;
                }

                if (result != null)
                {
                    PrintDataTableAsJson(result);
                }

                db.Close();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        static int RunStats(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Error: Database path required");
                return 1;
            }

            string dbPath = Path.GetFullPath(args[1]);
            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine("Error: Database not found: " + dbPath);
                return 1;
            }

            try
            {
                var db = new CodeGraphDatabase();
                db.Open(dbPath);
                var query = new CodeGraphQuery(db);
                var stats = query.GetStats();
                PrintDataTableAsJson(stats);
                db.Close();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        static void PrintDataTableAsJson(DataTable dt)
        {
            Console.WriteLine("[");
            for (int r = 0; r < dt.Rows.Count; r++)
            {
                Console.Write("  {");
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) Console.Write(", ");
                    string colName = dt.Columns[c].ColumnName;
                    object val = dt.Rows[r][c];

                    Console.Write(JsonEscape(colName) + ": ");
                    if (val == null || val == DBNull.Value)
                        Console.Write("null");
                    else if (val is long || val is int || val is double || val is float || val is decimal)
                        Console.Write(val.ToString());
                    else
                        Console.Write(JsonEscape(val.ToString()));
                }
                Console.WriteLine(r < dt.Rows.Count - 1 ? "}," : "}");
            }
            Console.WriteLine("]");
        }

        static string JsonEscape(string s)
        {
            if (s == null) return "null";
            return "\"" + s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }
    }
}
