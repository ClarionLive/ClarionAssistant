// Minimal stand-ins for the types Terminal\TerminalTab.cs references, so TabManager.Close.Test can
// compile the REAL TabManager.cs and TerminalTab.cs without WebView2 or ConPTY. Not in the csproj.
using System.Windows.Forms;

namespace ClarionAssistant.Terminal
{
    public class WebViewTerminalRenderer : Control { }

    public class ConPtyTerminal : System.IDisposable
    {
        // Counted so the test can prove a closed tab's terminal is stopped and disposed.
        public int StopCalls, DisposeCalls;
        public void Stop() { StopCalls++; }
        public void Dispose() { DisposeCalls++; }
    }
}

namespace ClarionAssistant.Services
{
    public class ClarionVersionConfig { }
}
