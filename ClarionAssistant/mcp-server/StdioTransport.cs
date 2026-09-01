using System;
using System.IO;
using System.Text;
using ClarionAssistant.Services;

namespace ClarionAssistant.McpServer
{
    /// <summary>
    /// MCP over stdio (ticket d051fbd1): newline-delimited JSON on stdin/stdout.
    ///
    /// FRAMING IS NEWLINE-DELIMITED JSON, NOT LSP's Content-Length headers. The two protocols
    /// look alike and this project already hosts an LSP client, so the mistake is one step away:
    /// MCP stdio says one JSON object per line, with no embedded newlines. JavaScriptSerializer
    /// emits single-line output, so every frame this class writes is well-formed by construction.
    ///
    /// WHY STDIO AND NOT THE EXISTING HTTP SERVER. The in-addin McpServer is HttpListener + SSE
    /// with bearer auth, Host/Origin validation and a 10-port scan. All of that exists because it
    /// is a network listener living inside a long-lived IDE that the user did not start for this
    /// purpose. A client-launched stdio process needs none of it: the client owns the lifetime,
    /// the pipe is private to the pair, and "who starts it / one per machine or per solution /
    /// how do clients find it" all answer themselves. Two transports over one tool set is the
    /// normal MCP shape, not a compromise.
    /// </summary>
    internal sealed class StdioTransport
    {
        private readonly McpDispatcher _dispatcher;
        private readonly TextWriter _out;
        private readonly TextReader _in;
        private readonly object _writeLock = new object();

        private StdioTransport(McpDispatcher dispatcher, TextReader input, TextWriter output)
        {
            _dispatcher = dispatcher;
            _in = input;
            _out = output;
        }

        /// <summary>
        /// Take ownership of the process's stdio and serve until stdin reaches EOF.
        ///
        /// THIS METHOD HIJACKS Console.Out ON PURPOSE. On stdio the protocol stream IS stdout, so
        /// a single stray Console.WriteLine anywhere in ~4,500 lines of tool registry (or anything
        /// it calls) injects a non-JSON line and desynchronises the client — a failure that
        /// presents as the client hanging or "disconnecting", nowhere near the code that caused
        /// it. I checked: no linked source writes to Console today. That is a fact about today,
        /// not a property anyone will preserve. So the real stdout is captured here and handed
        /// only to this class, and Console.Out is pointed at stderr, where stray output becomes
        /// harmless diagnostics instead of corruption.
        /// </summary>
        /// <param name="emitStrayConsoleWrite">
        /// TEST ONLY (--stdio-noise). Emits a Console.WriteLine after the redirect, so the
        /// stdout-hijack above can be PROVEN rather than asserted. Without it the hijack is a
        /// guard that has only ever passed by finding nothing, which is indistinguishable from a
        /// guard that does nothing: remove the Console.SetOut line and every other test still
        /// passes. Driven against the real .exe, this flag makes the difference visible — the
        /// noise must land on stderr and the stdout stream must stay pure JSON.
        /// </param>
        public static int Run(McpDispatcher dispatcher, bool emitStrayConsoleWrite = false)
        {
            // Raw streams, not Console.In/Out: the console's default encoding on Windows is the
            // OEM code page, which would mangle non-ASCII in tool output (accented identifiers,
            // file paths) on the way out and in. MCP is UTF-8. No BOM — a BOM is not valid inside
            // a JSON-RPC frame.
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            stdout.AutoFlush = false;
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

            var stderr = Console.Error;
            Console.SetOut(stderr);

            if (emitStrayConsoleWrite)
                Console.WriteLine("STRAY-CONSOLE-WRITE-THAT-MUST-NOT-REACH-STDOUT");

            var transport = new StdioTransport(dispatcher, stdin, stdout);
            try
            {
                transport.Serve();
                return 0;
            }
            catch (Exception ex)
            {
                stderr.WriteLine("clarion-mcp-server: fatal: " + ex);
                return 1;
            }
        }

        /// <summary>
        /// Serve a caller-supplied reader/writer pair instead of the process console, so the
        /// self-test can drive the REAL loop — same Serve, same HandleLine, same WriteFrame.
        /// A test that reimplemented the framing would only prove the test agrees with itself.
        ///
        /// Deliberately does NOT touch Console.Out: the stdout-hijack is a property of owning the
        /// process's streams, and the self-test asserts it separately.
        /// </summary>
        internal static void RunOn(McpDispatcher dispatcher, TextReader input, TextWriter output)
        {
            new StdioTransport(dispatcher, input, output).Serve();
        }

        private void Serve()
        {
            string line;
            while ((line = _in.ReadLine()) != null)
            {
                if (line.Length == 0) continue;          // keep-alive blank lines are not frames
                if (line.Trim().Length == 0) continue;

                HandleLine(line);
            }
            // stdin closed: the client is gone. Exiting is the correct response, not an error —
            // this is the normal shutdown path for a client-launched server.
        }

        /// <summary>
        /// One request in, at most one response out.
        ///
        /// Requests are handled SERIALLY, on this thread. The HTTP transport fans out to the
        /// thread pool because a browser-shaped client opens many connections; a stdio server has
        /// exactly one client on one pipe, and serialising removes a whole class of question about
        /// the registry's thread-safety that nobody has answered. The cost is that a long call
        /// (index_solution on a large solution) blocks later requests — which is why progress
        /// notifications matter here rather than being a nicety: the client can still see the
        /// work advancing.
        /// </summary>
        private void HandleLine(string line)
        {
            // Parsed here as well as inside the dispatcher, to learn whether this is a
            // NOTIFICATION (JSON-RPC: no id ⇒ no reply, ever). Writing a response to a
            // notification is a protocol violation that strict clients treat as a desync. The
            // second parse costs microseconds against tool calls measured in milliseconds to
            // minutes; the HTTP path guesses with a substring match on the body, which is cheaper
            // and wrong for any notification whose method doesn't start with "notifications/".
            bool isNotification;
            try
            {
                var probe = McpJsonRpc.ParseRequest(line);
                isNotification = probe != null && probe.IsNotification;
            }
            catch
            {
                // Unparseable: let the dispatcher produce the -32700 frame. A parse error
                // response carries a null id and IS sent, per JSON-RPC 2.0.
                isNotification = false;
            }

            string response = _dispatcher.ProcessJsonRpc(line, SendNotification);

            if (!isNotification && !string.IsNullOrEmpty(response))
                WriteFrame(response);
        }

        /// <summary>
        /// Sink for server→client notifications emitted DURING a tools/call (progress streaming).
        /// Passing this is what makes stdio a first-class transport rather than a buffered
        /// fallback: unlike a plain HTTP POST, a pipe is bidirectional and framed, so a long
        /// index run can report progress as it goes.
        /// </summary>
        private void SendNotification(string json)
        {
            WriteFrame(json);
        }

        /// <summary>
        /// Write one frame. Locked because progress notifications can arrive from a tool's own
        /// thread while this thread is writing the response that follows them — interleaved
        /// writes would splice two JSON objects into one unparseable line.
        /// </summary>
        private void WriteFrame(string json)
        {
            lock (_writeLock)
            {
                _out.Write(json);
                _out.Write('\n');
                _out.Flush();   // AutoFlush is off; a client blocks forever on a buffered reply
            }
        }
    }
}
