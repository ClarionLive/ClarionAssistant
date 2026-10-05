// Scripted Clarion language server for SolutionVersion.StandaloneE2ETest.ps1 (ticket 0ce0b5e2).
//
// Planted where LspService looks for the bundled server (<exe dir>\lsp-server\out\server\src\server.js) in a
// private copy of the standalone's bin. Appends every clarion/updatePaths it receives, one JSON line each, to
// the file named by CA_FAKE_LSP_LOG, so the test can see which Clarion version the server was started with,
// and that a version change restarted it with the new one. Answers each didOpen/didChange with no diagnostics
// and diagnosticsStatus complete, so lsp_diagnostics returns at once.

'use strict';

const fs = require('fs');
const logFile = process.env.CA_FAKE_LSP_LOG || '';
let buffer = Buffer.alloc(0);

function send(msg) {
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
    process.stdout.write(body);
}

function notify(method, params) { send({ jsonrpc: '2.0', method, params }); }

function handle(msg) {
    if (msg.id !== undefined && msg.method) {
        const result = msg.method === 'initialize' ? { capabilities: { textDocumentSync: 1 } } : null;
        send({ jsonrpc: '2.0', id: msg.id, result });
        return;
    }
    if (msg.method === 'clarion/updatePaths') {
        if (logFile) fs.appendFileSync(logFile, JSON.stringify({ pid: process.pid, params: msg.params }) + '\n');
    } else if (msg.method === 'textDocument/didOpen' || msg.method === 'textDocument/didChange') {
        const td = msg.params.textDocument;
        notify('textDocument/publishDiagnostics', { uri: td.uri, version: td.version, diagnostics: [] });
        notify('clarion/diagnosticsStatus', { uri: td.uri, version: td.version, state: 'complete' });
    } else if (msg.method === 'exit') {
        process.exit(0);
    }
}

process.stdin.on('data', chunk => {
    buffer = Buffer.concat([buffer, chunk]);
    for (;;) {
        const headerEnd = buffer.indexOf('\r\n\r\n');
        if (headerEnd < 0) return;
        const m = /Content-Length:\s*(\d+)/i.exec(buffer.slice(0, headerEnd).toString('ascii'));
        if (!m) { buffer = buffer.slice(headerEnd + 4); continue; }
        const len = parseInt(m[1], 10);
        if (buffer.length < headerEnd + 4 + len) return;
        const body = buffer.slice(headerEnd + 4, headerEnd + 4 + len).toString('utf8');
        buffer = buffer.slice(headerEnd + 4 + len);
        try { handle(JSON.parse(body)); } catch (e) { process.stderr.write('[fake-lsp] bad message: ' + e.message + '\n'); }
    }
});
process.stdin.on('end', () => process.exit(0));
