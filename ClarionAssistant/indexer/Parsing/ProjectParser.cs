using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using ClarionCodeGraph.Parsing.Models;

namespace ClarionCodeGraph.Parsing
{
    /// <summary>
    /// Parses a Clarion .cwproj file for source file references and project metadata.
    /// </summary>
    public class ProjectParser
    {
        public ProjectParseResult Parse(string cwprojPath)
        {
            if (!File.Exists(cwprojPath))
                throw new FileNotFoundException("Project file not found: " + cwprojPath);

            var result = new ProjectParseResult();
            var doc = new XmlDocument();
            doc.Load(cwprojPath);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            nsMgr.AddNamespace("ms", "http://schemas.microsoft.com/developer/msbuild/2003");

            // Extract OutputType
            var outputTypeNode = doc.SelectSingleNode("//ms:OutputType", nsMgr);
            if (outputTypeNode != null)
                result.OutputType = outputTypeNode.InnerText;

            // Extract AssemblyName
            var assemblyNameNode = doc.SelectSingleNode("//ms:AssemblyName", nsMgr);
            if (assemblyNameNode != null)
                result.AssemblyName = assemblyNameNode.InnerText;

            // Extract Compile Include entries
            var compileNodes = doc.SelectNodes("//ms:Compile", nsMgr);
            if (compileNodes != null)
            {
                foreach (XmlNode node in compileNodes)
                {
                    var includeAttr = node.Attributes["Include"];
                    if (includeAttr != null)
                    {
                        string fileName = includeAttr.Value;
                        if (fileName.EndsWith(".clw", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".inc", StringComparison.OrdinalIgnoreCase))
                        {
                            result.SourceFiles.Add(fileName);
                        }
                    }
                }
            }

            return result;
        }
    }

    public class ProjectParseResult
    {
        public string OutputType { get; set; }
        public string AssemblyName { get; set; }
        public List<string> SourceFiles { get; set; }

        public ProjectParseResult()
        {
            SourceFiles = new List<string>();
        }
    }
}
