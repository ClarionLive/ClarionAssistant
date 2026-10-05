// Scripted Clarion language server for LspDiagnostics.LiveTextTest.ps1 (ticket 44a1b10c).
//
// Records every text the client sends (didOpen, and each didChange that is applied) to FAKE_LOG as one JSON line,
// so the test can prove WHICH text reached the server: the open editor's buffer, never the disk. Behaves like the
// real server where it matters:
//   * full text sync (textDocumentSync 1), so each change carries the whole text;
//   * #359 ContentChangeGuard: a change identical to the last accepted text is skipped (logged as "skip"), with
//     no analysis, publish or status;
//   * each analysis publishes one error per line containing "BAD" (message "BAD at <0-based line>"), with the
//     version, then clarion/diagnosticsStatus complete.

'use strict';

const fs = require('fs');
const LOG = process.env.FAKE_LOG;
const texts = new Map();
let buffer = Buffer.alloc(0);

function log(entry) { if (LOG) fs.appendFileSync(LOG, JSON.stringify(entry) + '\n'); }

function send(msg) {
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
    process.stdout.write(body);
}

function notify(method, params) { send({ jsonrpc: '2.0', method, params }); }

function analyse(uri, version, text) {
    const diagnostics = [];
    text.split('\n').forEach((line, i) => {
        if (line.indexOf('BAD') >= 0)
            diagnostics.push({
                range: { start: { line: i, character: 2 }, end: { line: i, character: 5 } },
                severity: 1, source: 'clarion', message: 'BAD at ' + i
            });
    });
    notify('textDocument/publishDiagnostics', { uri, version, diagnostics });
    notify('clarion/diagnosticsStatus', { uri, version, state: 'complete' });
}

function handle(msg) {
    if (msg.id !== undefined && msg.method) {
        const result = msg.method === 'initialize' ? { capabilities: { textDocumentSync: 1 } } : null;
        send({ jsonrpc: '2.0', id: msg.id, result });
        return;
    }
    if (msg.method === 'textDocument/didOpen') {
        const td = msg.params.textDocument;
        texts.set(td.uri, td.text);
        log({ event: 'open', uri: td.uri, version: td.version, text: td.text });
        analyse(td.uri, td.version, td.text);
    } else if (msg.method === 'textDocument/didChange') {
        const td = msg.params.textDocument;
        const changes = msg.params.contentChanges || [];
        const last = changes.length ? changes[changes.length - 1] : null;
        if (!last || last.range || typeof last.text !== 'string') { log({ event: 'unexpected-ranged-change', uri: td.uri }); return; }
        if (texts.get(td.uri) === last.text) {
            log({ event: 'skip', uri: td.uri, version: td.version });   // #359: identical content, nothing happens
            return;
        }
        texts.set(td.uri, last.text);
        log({ event: 'change', uri: td.uri, version: td.version, text: last.text });
        analyse(td.uri, td.version, last.text);
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
