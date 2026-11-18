// Test-only stand-in for the PdfPig surface DocGraphService.ExtractPdfText touches, so the QueryDocs
// ranking test can compile the REAL Services\DocGraphService.cs without the PdfPig NuGet package.
// The ranking test never ingests a PDF; every member throws if it is ever reached.
using System;
using System.Collections.Generic;

namespace UglyToad.PdfPig
{
    public sealed class PdfDocument : IDisposable
    {
        public static PdfDocument Open(string path) { throw new NotSupportedException("PdfPig stub: no PDF in this test"); }
        public IEnumerable<Content.Page> GetPages() { throw new NotSupportedException("PdfPig stub"); }
        public void Dispose() { }
    }
}

namespace UglyToad.PdfPig.Content
{
    public sealed class Page { }
}

namespace UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor
{
    public static class ContentOrderTextExtractor
    {
        public static string GetText(UglyToad.PdfPig.Content.Page page) { throw new NotSupportedException("PdfPig stub"); }
    }
}
