// A stand-in language server for CompletionMerge.DuplicateTest.ps1 (GH #187).
//
// It answers textDocument/completion with a FIXED list read from completion-items.json beside it,
// whatever the position. That is deliberate: the thing under test is what the HOST does with a
// server answer that lists one member twice (SharedLspBridge.GetCompletion's merge), not how any
// real server arrives at that answer, so the answer is the fixture and nothing here decides it.
//
// Speaks just enough LSP over stdio for LspClient: initialize, shutdown/exit, a null result for any
// other request, and silence for notifications.
'use strict';
const fs = require('fs');
const path = require('path');

const items = JSON.parse(fs.readFileSync(path.join(__dirname, 'completion-items.json'), 'utf8'));
let buf = Buffer.alloc(0);

function send(msg) {
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
    process.stdout.write(body);
}

function handle(msg) {
    if (msg.id === undefined || msg.id === null) {
        if (msg.method === 'exit') process.exit(0);
        return;                                           // notification
    }
    let result = null;
    if (msg.method === 'initialize') result = { capabilities: { completionProvider: { triggerCharacters: ['.'] } } };
    else if (msg.method === 'textDocument/completion') result = { isIncomplete: false, items: items };
    send({ jsonrpc: '2.0', id: msg.id, result: result });
}

process.stdin.on('data', chunk => {
    buf = Buffer.concat([buf, chunk]);
    for (;;) {
        const sep = buf.indexOf('\r\n\r\n');
        if (sep < 0) return;
        const m = /Content-Length:\s*(\d+)/i.exec(buf.slice(0, sep).toString('ascii'));
        if (!m) { buf = buf.slice(sep + 4); continue; }
        const len = parseInt(m[1], 10);
        if (buf.length < sep + 4 + len) return;
        const body = buf.slice(sep + 4, sep + 4 + len).toString('utf8');
        buf = buf.slice(sep + 4 + len);
        try { handle(JSON.parse(body)); } catch (e) { process.stderr.write('fake-lsp: ' + e.message + '\n'); }
    }
});
process.stdin.on('end', () => process.exit(0));
