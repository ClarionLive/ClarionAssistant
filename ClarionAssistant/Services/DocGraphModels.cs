namespace ClarionAssistant.Services
{
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
