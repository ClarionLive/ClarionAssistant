// Minimal stand-ins for the types Terminal\TerminalTab.cs references, so TabManager.Close.Test can
// compile the REAL TabManager.cs and TerminalTab.cs without WebView2 or ConPTY. Not in the csproj.
using System.Windows.Forms;

namespace ClarionAssistant.Terminal
{
    public class WebViewTerminalRenderer : Control { }

    public class ConPtyTerminal : System.IDisposable
    {
        public void Stop() { }
        public void Dispose() { }
    }
}

namespace ClarionAssistant.Services
{
    public class ClarionVersionConfig { }
}
