// Scripted Clarion language server for UndeclaredFilter.CachedReadTest.ps1 (ticket 2abfbba2).
//
// Answers each didOpen/didChange with ONLY "'X' is not declared in this file." warnings, the shape the real
// server (v1.0.8) gives PRM002004.clw for GlobalRequest, a global it cannot see (upstream bug B). Then
// clarion/diagnosticsStatus complete, so the wait ends at once.
//
//   onlyglobal.clw  'GlobRes' only. GlobRes IS a global in prog.clw, so CA's filter clears the whole
//                   list. This is the case that leaked: an empty filtered answer, then the raw cache read.
//   mixed.clw       'GlobRes' plus 'NoSuchThing' (declared nowhere): the negative control. NoSuchThing
//                   must still show.

'use strict';

let buffer = Buffer.alloc(0);

function send(msg) {
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
    process.stdout.write(body);
}

function notify(method, params) { send({ jsonrpc: '2.0', method, params }); }

function undeclared(names) {
    return names.map(n => ({
        range: { start: { line: 4, character: 2 }, end: { line: 4, character: 2 + n.length } },
        severity: 2,
        source: 'clarion',
        message: "'" + n + "' is not declared in this file."
    }));
}

function analyse(uri, version) {
    const name = uri.substring(uri.lastIndexOf('/') + 1).toLowerCase();
    process.stderr.write('[fake-lsp] analyse ' + name + ' v' + version + '\n');
    const names = name === 'mixed.clw' ? ['GlobRes', 'NoSuchThing'] : name === 'onlyglobal.clw' ? ['GlobRes'] : [];
    notify('textDocument/publishDiagnostics', { uri, version, diagnostics: undeclared(names) });
    notify('clarion/diagnosticsStatus', { uri, version, state: 'complete' });
}

function handle(msg) {
    if (msg.id !== undefined && msg.method) {
        const result = msg.method === 'initialize' ? { capabilities: { textDocumentSync: 1 } } : null;
        send({ jsonrpc: '2.0', id: msg.id, result });
        return;
    }
    if (msg.method === 'textDocument/didOpen' || msg.method === 'textDocument/didChange') {
        const td = msg.params.textDocument;
        analyse(td.uri, td.version);
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
