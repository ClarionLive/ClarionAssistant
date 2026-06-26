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
    }
}
