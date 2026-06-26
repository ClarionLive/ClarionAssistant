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
    }
}
