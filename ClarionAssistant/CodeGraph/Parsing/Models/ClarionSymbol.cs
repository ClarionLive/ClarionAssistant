namespace ClarionCodeGraph.Parsing.Models
{
    public class ClarionSymbol
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }          // procedure, function, routine, class, interface, module, file
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public int ProjectId { get; set; }
        public string Params { get; set; }
        public string ReturnType { get; set; }
        public string ParentName { get; set; }     // base class if CLASS(BaseClass)
        public string MemberOf { get; set; }       // MEMBER('parent.clw') value
        public string Scope { get; set; }          // global (in MAP), local (routine), module
        public string SourcePreview { get; set; }
        // prototype (MAP or CLASS-body declaration), implementation (parsed body), or
        // external (variable carrying the EXTERNAL attribute — declared here, owned elsewhere).
        // Null on symbol kinds where the distinction is meaningless.
        public string DeclKind { get; set; }

        // NOT persisted. On a class symbol: the .clw files its CLASS line names in MODULE('x.clw') and
        // LINK('x.clw'), in order, de-duplicated. The compiler builds those files whenever the class's
        // declaration is INCLUDEd, whether or not a .cwproj lists them (GH #245); the indexer follows them.
        // Null when the CLASS line names none.
        public System.Collections.Generic.List<string> ImplementationModules { get; set; }

        // NOT persisted. On an include symbol: true when the INCLUDE sits in a PROGRAM file's global data
        // section (or in a file INCLUDEd there), so the data the included file declares is program-global
        // (GH #246). False everywhere else: a MAP, a CLASS body, a procedure's or routine's data, a MEMBER module.
        public bool InGlobalData { get; set; }
    }
}
