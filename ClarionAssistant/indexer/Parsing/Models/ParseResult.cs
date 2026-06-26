using System.Collections.Generic;

namespace ClarionCodeGraph.Parsing.Models
{
    public class ParseResult
    {
        public string FilePath { get; set; }
        public List<ClarionSymbol> Symbols { get; set; }
        public List<ClarionRelationship> Relationships { get; set; }

        public ParseResult()
        {
            Symbols = new List<ClarionSymbol>();
            Relationships = new List<ClarionRelationship>();
        }
    }
}
