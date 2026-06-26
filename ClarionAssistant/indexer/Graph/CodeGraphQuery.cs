using System;
using System.Collections.Generic;
using System.Data;

namespace ClarionCodeGraph.Graph
{
    /// <summary>
    /// Structured query methods for the code graph.
    /// All queries return DataTables for easy binding to UI grids.
    /// </summary>
    public class CodeGraphQuery
    {
        private readonly CodeGraphDatabase _db;

        public CodeGraphQuery(CodeGraphDatabase db)
        {
            _db = db;
        }

        /// <summary>
        /// Find procedures/functions by name (type-ahead search).
        /// </summary>
        public DataTable FindSymbol(string searchText)
        {
            // Try exact match first; fall back to substring search
            string sqlExact = @"
                SELECT s.id, s.name, s.type, s.file_path, s.line_number,
                       p.name AS project_name, s.params, s.return_type, s.scope
                FROM symbols s
                LEFT JOIN projects p ON s.project_id = p.id
                WHERE s.name = @exact COLLATE NOCASE
                  AND s.type IN ('procedure', 'function', 'class', 'interface', 'routine')
                ORDER BY s.name";

            var result = _db.ExecuteQuery(sqlExact, new Dictionary<string, object>
            {
                { "@exact", searchText }
            });

            if (result.Rows.Count > 0)
                return result;

            string sql = @"
                SELECT s.id, s.name, s.type, s.file_path, s.line_number,
                       p.name AS project_name, s.params, s.return_type, s.scope
                FROM symbols s
                LEFT JOIN projects p ON s.project_id = p.id
                WHERE s.name LIKE @search COLLATE NOCASE
                  AND s.type IN ('procedure', 'function', 'class', 'interface', 'routine')
                ORDER BY
                  CASE WHEN s.name LIKE @prefix COLLATE NOCASE THEN 0 ELSE 1 END,
                  s.name";

            return _db.ExecuteQuery(sql, new Dictionary<string, object>
            {
                { "@search", "%" + searchText + "%" },
                { "@prefix", searchText + "%" }
            });
        }

        /// <summary>
        /// Who calls this procedure? (direct callers)
        /// </summary>
        public DataTable GetCallers(long symbolId)
        {
            string sql = @"
                SELECT DISTINCT s.id, s.name, s.type,
                       r.file_path AS call_file, r.line_number AS call_line,
                       p.name AS project_name
                FROM relationships r
                JOIN symbols s ON r.from_id = s.id
                LEFT JOIN projects p ON s.project_id = p.id
                WHERE r.to_id = @id AND r.type IN ('calls', 'do')
                ORDER BY p.name, s.name";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@id", symbolId } });
        }

        /// <summary>
        /// Who calls this procedure? Tree view with source preview.
        /// </summary>
        public DataTable GetCallersTree(long symbolId)
        {
            string sql = @"
                SELECT s.name AS caller_name, s.type AS caller_type,
                       s.file_path AS caller_file, s.line_number AS caller_line,
                       r.file_path AS call_file, r.line_number AS call_line,
                       p.name AS project_name
                FROM relationships r
                JOIN symbols s ON r.from_id = s.id
                LEFT JOIN projects p ON s.project_id = p.id
                WHERE r.to_id = @id AND r.type IN ('calls', 'do')
                ORDER BY p.name, s.name";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@id", symbolId } });
        }

        /// <summary>
        /// What does this procedure call? (direct callees only — external procedures)
        /// </summary>
        public DataTable GetCallees(long symbolId)
        {
            string sql = @"
                SELECT DISTINCT s.id, s.name, s.type, s.file_path, s.line_number,
                       p.name AS project_name, r.line_number AS call_line, r.file_path AS call_file
                FROM relationships r
                JOIN symbols s ON r.to_id = s.id
                LEFT JOIN projects p ON s.project_id = p.id
                WHERE r.from_id = @id AND r.type = 'calls'
                ORDER BY s.name";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@id", symbolId } });
        }

        /// <summary>
        /// Tree view: What does this procedure call, ordered by call site line number.
        /// Only shows external procedure calls (no local MAP procs, no routines, no method calls).
        /// </summary>
        public DataTable GetCalleesTree(long symbolId)
        {
            string sql = @"
                SELECT '' AS caller_name, 0 AS caller_line,
                       s.name AS callee_name, s.type AS callee_type,
                       r.line_number AS call_line, r.file_path AS call_file,
                       s.file_path AS callee_file, s.line_number AS callee_line,
                       p.name AS project_name
                FROM relationships r
                JOIN symbols s ON r.to_id = s.id
                LEFT JOIN projects p ON s.project_id = p.id
                WHERE r.from_id = @id AND r.type = 'calls'
                ORDER BY r.line_number";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@id", symbolId } });
        }

        /// <summary>
        /// Impact analysis: transitive callers using recursive CTE.
        /// "If I change X, what's affected?"
        /// </summary>
        public DataTable GetImpact(long symbolId, int maxDepth = 10)
        {
            string sql = @"
                WITH RECURSIVE impact(id, name, type, file_path, line_number, project_name, depth) AS (
                    SELECT s.id, s.name, s.type, s.file_path, s.line_number, p.name, 0
                    FROM symbols s
                    LEFT JOIN projects p ON s.project_id = p.id
                    WHERE s.id = @id
                    UNION
                    SELECT s.id, s.name, s.type, s.file_path, s.line_number, p.name, impact.depth + 1
                    FROM relationships r
                    JOIN symbols s ON r.from_id = s.id
                    LEFT JOIN projects p ON s.project_id = p.id
                    JOIN impact ON r.to_id = impact.id
                    WHERE impact.depth < @maxDepth
                )
                SELECT DISTINCT id, name, type, file_path, line_number, project_name, depth
                FROM impact
                WHERE depth > 0
                ORDER BY depth, name";

            return _db.ExecuteQuery(sql, new Dictionary<string, object>
            {
                { "@id", symbolId },
                { "@maxDepth", maxDepth }
            });
        }

        /// <summary>
        /// Impact analysis tree: transitive callers with call site info for source preview.
        /// </summary>
        public DataTable GetImpactTree(long symbolId, int maxDepth = 10)
        {
            string sql = @"
                WITH RECURSIVE impact(id, name, type, file_path, line_number, project_name, depth, called_id, path) AS (
                    SELECT s.id, s.name, s.type, s.file_path, s.line_number, p.name, 0, s.id, s.name
                    FROM symbols s
                    LEFT JOIN projects p ON s.project_id = p.id
                    WHERE s.id = @id
                    UNION
                    SELECT s.id, s.name, s.type, s.file_path, s.line_number, p.name, impact.depth + 1, impact.id,
                           impact.path || '>' || s.name
                    FROM relationships r
                    JOIN symbols s ON r.from_id = s.id
                    LEFT JOIN projects p ON s.project_id = p.id
                    JOIN impact ON r.to_id = impact.id
                    WHERE impact.depth < @maxDepth
                      AND ('>' || impact.path || '>') NOT LIKE ('%>' || s.name || '>%')
                )
                SELECT i.name AS caller_name, i.type AS caller_type,
                       i.file_path AS caller_file, i.line_number AS caller_line,
                       r.file_path AS call_file, r.line_number AS call_line,
                       i.project_name, i.depth,
                       called_s.name AS callee_name,
                       i.path
                FROM impact i
                JOIN symbols called_s ON i.called_id = called_s.id
                LEFT JOIN relationships r ON r.from_id = i.id AND r.to_id = i.called_id AND r.type IN ('calls', 'do')
                WHERE i.depth > 0
                ORDER BY i.path, i.depth";

            return _db.ExecuteQuery(sql, new Dictionary<string, object>
            {
                { "@id", symbolId },
                { "@maxDepth", maxDepth }
            });
        }

        /// <summary>
        /// Dead code detection: procedures/functions never called from anywhere.
        /// </summary>
        public DataTable GetDeadCode()
        {
            string sql = @"
                SELECT s.id, s.name, s.type, s.file_path, s.line_number,
                       p.name AS project_name
                FROM symbols s
                LEFT JOIN projects p ON s.project_id = p.id
                LEFT JOIN relationships r ON r.to_id = s.id AND r.type IN ('calls', 'do')
                WHERE s.type IN ('procedure', 'function')
                  AND s.scope = 'global'
                  AND r.id IS NULL
                ORDER BY p.name, s.name";

            return _db.ExecuteQuery(sql, null);
        }

        /// <summary>
        /// Dead variables: variables declared but never referenced in code.
        /// </summary>
        public DataTable GetDeadVariables()
        {
            string sql = @"
                SELECT s.id, s.name, s.params AS data_type, s.file_path, s.line_number,
                       s.parent_name AS owner, s.scope,
                       p.name AS project_name
                FROM symbols s
                LEFT JOIN projects p ON s.project_id = p.id
                LEFT JOIN relationships r ON r.to_id = s.id AND r.type = 'references'
                WHERE s.type = 'variable'
                  AND r.id IS NULL
                ORDER BY p.name, s.file_path, s.line_number";

            return _db.ExecuteQuery(sql, null);
        }

        /// <summary>
        /// Inheritance tree: classes and their base classes.
        /// </summary>
        public DataTable GetInheritanceTree(long classId)
        {
            string sql = @"
                WITH RECURSIVE tree(id, name, type, file_path, line_number, parent_name, depth) AS (
                    SELECT id, name, type, file_path, line_number, parent_name, 0
                    FROM symbols WHERE id = @id
                    UNION ALL
                    SELECT s.id, s.name, s.type, s.file_path, s.line_number, s.parent_name, tree.depth + 1
                    FROM symbols s
                    JOIN relationships r ON r.from_id = s.id AND r.type = 'inherits'
                    JOIN tree ON r.to_id = tree.id
                    WHERE tree.depth < 10
                )
                SELECT * FROM tree ORDER BY depth";

            // Return the base hierarchy
            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@id", classId } });
        }

        /// <summary>
        /// File dependencies: INCLUDE graph.
        /// </summary>
        public DataTable GetFileDependencies(string filePath)
        {
            string sql = @"
                SELECT s.name AS included_file, s.file_path, s.line_number,
                       p.name AS project_name
                FROM symbols s
                LEFT JOIN projects p ON s.project_id = p.id
                WHERE s.type = 'include' AND s.file_path = @file
                ORDER BY s.line_number";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@file", filePath } });
        }

        /// <summary>
        /// All symbols in a project (by name).
        /// </summary>
        public DataTable GetProjectSymbols(string projectName)
        {
            string sql = @"
                SELECT s.id, s.name, s.type, s.file_path, s.line_number, s.params, s.return_type, s.scope
                FROM symbols s
                JOIN projects p ON s.project_id = p.id
                WHERE p.name = @pname COLLATE NOCASE
                ORDER BY s.type, s.name";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@pname", projectName } });
        }

        /// <summary>
        /// All symbols defined in a specific file.
        /// </summary>
        public DataTable GetFileSymbols(string filePath)
        {
            string sql = @"
                SELECT s.id, s.name, s.type, s.line_number, s.params, s.return_type, s.scope,
                       p.name AS project_name
                FROM symbols s
                LEFT JOIN projects p ON s.project_id = p.id
                WHERE s.file_path = @file
                ORDER BY s.line_number";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@file", filePath } });
        }

        /// <summary>
        /// Project dependency graph.
        /// </summary>
        public DataTable GetProjectDependencies()
        {
            string sql = @"
                SELECT p1.name AS project_name, p2.name AS depends_on
                FROM project_dependencies pd
                JOIN projects p1 ON pd.project_id = p1.id
                JOIN projects p2 ON pd.depends_on_id = p2.id
                ORDER BY p1.name, p2.name";

            return _db.ExecuteQuery(sql, null);
        }

        /// <summary>
        /// Index statistics.
        /// </summary>
        public DataTable GetStats()
        {
            string sql = @"
                SELECT
                    (SELECT COUNT(*) FROM projects) AS project_count,
                    (SELECT COUNT(DISTINCT file_path) FROM symbols) AS file_count,
                    (SELECT COUNT(*) FROM symbols WHERE type IN ('procedure','function')) AS proc_count,
                    (SELECT COUNT(*) FROM symbols WHERE type = 'routine') AS routine_count,
                    (SELECT COUNT(*) FROM symbols WHERE type = 'class') AS class_count,
                    (SELECT COUNT(*) FROM symbols WHERE type = 'variable') AS variable_count,
                    (SELECT COUNT(*) FROM relationships) AS relationship_count,
                    (SELECT value FROM index_metadata WHERE key = 'last_indexed') AS last_indexed,
                    (SELECT value FROM index_metadata WHERE key = 'index_duration_ms') AS duration_ms";

            return _db.ExecuteQuery(sql, null);
        }
    }
}
