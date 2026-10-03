// A stand-in language server for LspClient.IncrementalSync.Test.cs.
//
// It keeps each document's text the way a real server does, applying every didChange by the LSP's rules (a change
// with a range replaces that range: lines end at \r\n, \r or \n, characters are UTF-16 units; a change without a range
// replaces the whole text), and counts how each change arrived. The harness reads that state back to check the server
// ends up holding exactly the editor's text.
//
// FAKE_SYNC (environment, inherited from the harness) is the textDocumentSync kind it advertises: 2 incremental
// (the default, as the Clarion server does) or 1 full.
//
// LspClient has no "give me your text" request, so the state comes back through one it does send:
// clarion/findFile answers { path, text, ranged, full, opens } for the document whose URI is passed as `filename`.
'use strict';
const syncKind = Number(process.env.FAKE_SYNC || 2);
const docs = {};          // uri -> { text, ranged, full, opens }
let buf = Buffer.alloc(0);

function send(msg) {
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
    process.stdout.write(body);
}

function lineStarts(t) {
    const s = [0];
    for (let i = 0; i < t.length; i++) {
        if (t[i] === '\r') { if (t[i + 1] === '\n') i++; s.push(i + 1); }
        else if (t[i] === '\n') s.push(i + 1);
    }
    return s;
}

function offsetAt(t, starts, pos) {
    if (pos.line >= starts.length) return t.length;
    return Math.min(starts[pos.line] + pos.character, pos.line + 1 < starts.length ? starts[pos.line + 1] : t.length);
}

function apply(doc, change) {
    if (!change.range) { doc.text = change.text; doc.full++; return; }
    const starts = lineStarts(doc.text);
    const s = offsetAt(doc.text, starts, change.range.start), e = offsetAt(doc.text, starts, change.range.end);
    doc.text = doc.text.slice(0, s) + change.text + doc.text.slice(e);
    doc.ranged++;
}

function handle(msg) {
    const p = msg.params || {};
    if (msg.method === 'textDocument/didOpen') {
        const d = docs[p.textDocument.uri] || (docs[p.textDocument.uri] = { text: '', ranged: 0, full: 0, opens: 0 });
        d.text = p.textDocument.text; d.opens++;
        return;
    }
    if (msg.method === 'textDocument/didChange') {
        const d = docs[p.textDocument.uri];
        if (!d) return;   // a change for a document this server never opened: ignored, as a real server would
        for (const c of p.contentChanges) apply(d, c);
        return;
    }
    if (msg.id === undefined || msg.id === null) {
        if (msg.method === 'exit') process.exit(0);
        return;
    }
    let result = null;
    if (msg.method === 'initialize') result = { capabilities: { textDocumentSync: syncKind } };
    else if (msg.method === 'clarion/findFile') {
        const d = docs[p.filename];
        result = d ? { path: p.filename, text: d.text, ranged: d.ranged, full: d.full, opens: d.opens } : { path: '' };
    }
    send({ jsonrpc: '2.0', id: msg.id, result: result });
}

process.stdin.on('data', chunk => {
    buf = Buffer.concat([buf, chunk]);
    for (;;) {
        const sep = buf.indexOf('\r\n\r\n');
        if (sep < 0) return;
        const m = /Content-Length:\s*(\d+)/i.exec(buf.slice(0, sep).toString('ascii'));
        if (!m) { buf = buf.slice(sep + 4); continue; }
        const len = Number(m[1]);
        if (buf.length < sep + 4 + len) return;
        const body = buf.slice(sep + 4, sep + 4 + len).toString('utf8');
        buf = buf.slice(sep + 4 + len);
        try { handle(JSON.parse(body)); } catch (e) { /* a broken message must not kill the stand-in */ }
    }
});
