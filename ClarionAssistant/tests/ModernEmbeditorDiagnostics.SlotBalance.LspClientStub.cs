using System.Collections.Generic;

// The two LspClient DTOs ModernEmbeditorDiagnostics.cs reads, for the SlotBalance harness only. Harnesses
// that also compile the real Services\LspClient.cs (LocalLayer.Handlers.Test) leave this file out.
namespace ClarionAssistant.Services
{
    public class LspClient
    {
        public class DiagnosticEntry
        {
            public int Severity;
            public int Line;
            public int Character;
            public int EndLine;
            public int EndCharacter;
            public string Message;
            public string Source;
        }

        public class DiagnosticWaitResult
        {
            public List<DiagnosticEntry> Entries;
            public bool Pending;
        }
    }
}
