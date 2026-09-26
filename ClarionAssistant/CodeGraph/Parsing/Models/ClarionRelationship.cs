namespace ClarionCodeGraph.Parsing.Models
{
    public class ClarionRelationship
    {
        public long Id { get; set; }
        public long FromId { get; set; }
        public long ToId { get; set; }
        public string Type { get; set; }           // calls, do, inherits, implements, includes, contains, member_of, depends_on
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        // True when call resolution had more than one equal-rank candidate after scope ordering
        // and arity tie-breaking, and picked one deterministically (lowest id) anyway.
        public bool Ambiguous { get; set; }
    }
}
