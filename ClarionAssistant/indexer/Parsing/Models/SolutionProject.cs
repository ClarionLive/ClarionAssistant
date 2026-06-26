using System.Collections.Generic;

namespace ClarionCodeGraph.Parsing.Models
{
    public class SolutionProject
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Guid { get; set; }
        public string CwprojPath { get; set; }
        public string OutputType { get; set; }     // Library, Exe
        public string SlnPath { get; set; }
        public List<string> DependencyGuids { get; set; }

        public SolutionProject()
        {
            DependencyGuids = new List<string>();
        }
    }
}
