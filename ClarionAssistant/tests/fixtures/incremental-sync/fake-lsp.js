// A stand-in language server for LspClient.IncrementalSync.Test.cs.
//
// It keeps each document the way the real Clarion server does: as a vscode-languageserver-textdocument TextDocument,
// updated with TextDocument.update for every didChange. That matters. update() does not rebuild the line-offset table
// after a ranged change, it PATCHES it from the change alone, so a range that splits a \r\n (a \r inserted before an
// existing \n, a \n after an existing \r) leaves the right text but the wrong lines: "a\n" + "\r" at 0:1 gives the text
// "a\r\n" with lineCount 3 and a wrong positionAt. A stand-in that rebuilt its line table from the text after each
// change could not see that, so the guards in LspTextDiff that prevent it could be deleted with every test green.
//
// After every change it therefore checks the patched document against a document freshly created from the same text:
// same lineCount, and the same offsetAt/positionAt at every offset. Any disagreement is counted as `drift`, which the
// harness asserts is zero.
//
// The TextDocument used is the real package when node can resolve it (FAKE_TEXTDOC_MODULE, set by Run-Tests.ps1 from
// the deploy's .lsp-build cache when present, or a normal require), else the faithful port below of its 1.0.14
// update/offsetAt/positionAt. findFile reports which one ran (`impl`).
//
// FAKE_SYNC (environment, inherited from the harness) is the textDocumentSync kind it advertises: 2 incremental
// (the default, as the Clarion server does) or 1 full. FAKE_SYNC_FORM=object advertises it in the options form
// ({ change: N }) instead of the bare number.
//
// LspClient has no "give me your text" request, so the state comes back through one it does send:
// clarion/findFile answers { path, text, version, ranged, full, opens, drift, firstDrift, impl } for the document whose URI is
// passed as `filename`.
'use strict';
const syncKind = Number(process.env.FAKE_SYNC || 2);
const syncForm = process.env.FAKE_SYNC_FORM || 'number';
const docs = {};          // uri -> { doc, version, ranged, full, opens, drift, firstDrift }
let buf = Buffer.alloc(0);

// ---- TextDocument: the real package if reachable, else a port of it ----
let TextDocument = null, impl = 'port';
for (const id of [process.env.FAKE_TEXTDOC_MODULE, 'vscode-languageserver-textdocument']) {
    if (!id) continue;
    try {
        TextDocument = require(id).TextDocument;
        impl = 'real ' + (() => { try { return require(require('path').join(require.resolve(id).replace(/[\\/]lib[\\/].*$/, ''), 'package.json')).version; } catch (e) { return '?'; } })();
        break;
    } catch (e) { /* not reachable: fall through to the port */ }
}

if (!TextDocument) {
    // Port of vscode-languageserver-textdocument 1.0.14 (lib/umd/main.js; MIT, Copyright (c) Microsoft Corporation):
    // create, update (with its INCREMENTAL line-offset patch), offsetAt, positionAt, lineCount. Kept line for line so
    // the stand-in fails exactly where the real server would; see the header for why that is the point.
    const isEOL = ch => ch === 13 || ch === 10;
    const computeLineOffsets = (text, isAtLineStart, textOffset = 0) => {
        const result = isAtLineStart ? [textOffset] : [];
        for (let i = 0; i < text.length; i++) {
            const ch = text.charCodeAt(i);
            if (isEOL(ch)) {
                if (ch === 13 && i + 1 < text.length && text.charCodeAt(i + 1) === 10) i++;
                result.push(textOffset + i + 1);
            }
        }
        return result;
    };
    const wellformed = r => (r.start.line > r.end.line || (r.start.line === r.end.line && r.start.character > r.end.character)) ? { start: r.end, end: r.start } : r;
    class FullTextDocument {
        constructor(uri, languageId, version, content) { this._content = content; this._version = version; this._lineOffsets = undefined; }
        getText() { return this._content; }
        update(changes, version) {
            for (const change of changes) {
                if (change.range !== undefined) {
                    const range = wellformed(change.range);
                    const startOffset = this.offsetAt(range.start), endOffset = this.offsetAt(range.end);
                    this._content = this._content.substring(0, startOffset) + change.text + this._content.substring(endOffset, this._content.length);
                    const startLine = Math.max(range.start.line, 0), endLine = Math.max(range.end.line, 0);
                    let lineOffsets = this._lineOffsets;
                    const added = computeLineOffsets(change.text, false, startOffset);
                    if (endLine - startLine === added.length) {
                        for (let i = 0; i < added.length; i++) lineOffsets[i + startLine + 1] = added[i];
                    } else if (added.length < 10000) {
                        lineOffsets.splice(startLine + 1, endLine - startLine, ...added);
                    } else {
                        this._lineOffsets = lineOffsets = lineOffsets.slice(0, startLine + 1).concat(added, lineOffsets.slice(endLine + 1));
                    }
                    const diff = change.text.length - (endOffset - startOffset);
                    if (diff !== 0) for (let i = startLine + 1 + added.length; i < lineOffsets.length; i++) lineOffsets[i] = lineOffsets[i] + diff;
                } else {
                    this._content = change.text;
                    this._lineOffsets = undefined;
                }
            }
            this._version = version;
        }
        getLineOffsets() {
            if (this._lineOffsets === undefined) this._lineOffsets = computeLineOffsets(this._content, true);
            return this._lineOffsets;
        }
        positionAt(offset) {
            offset = Math.max(Math.min(offset, this._content.length), 0);
            const lineOffsets = this.getLineOffsets();
            let low = 0, high = lineOffsets.length;
            if (high === 0) return { line: 0, character: offset };
            while (low < high) {
                const mid = Math.floor((low + high) / 2);
                if (lineOffsets[mid] > offset) high = mid; else low = mid + 1;
            }
            const line = low - 1;
            offset = this.ensureBeforeEOL(offset, lineOffsets[line]);
            return { line: line, character: offset - lineOffsets[line] };
        }
        offsetAt(position) {
            const lineOffsets = this.getLineOffsets();
            if (position.line >= lineOffsets.length) return this._content.length;
            else if (position.line < 0) return 0;
            const lineOffset = lineOffsets[position.line];
            if (position.character <= 0) return lineOffset;
            const nextLineOffset = (position.line + 1 < lineOffsets.length) ? lineOffsets[position.line + 1] : this._content.length;
            const offset = Math.min(lineOffset + position.character, nextLineOffset);
            return this.ensureBeforeEOL(offset, lineOffset);
        }
        ensureBeforeEOL(offset, lineOffset) {
            while (offset > lineOffset && isEOL(this._content.charCodeAt(offset - 1))) offset--;
            return offset;
        }
        get lineCount() { return this.getLineOffsets().length; }
    }
    TextDocument = {
        create: (uri, languageId, version, content) => new FullTextDocument(uri, languageId, version, content),
        update: (document, changes, version) => { document.update(changes, version); return document; }
    };
}

// The patched document against one created fresh from the same text: null when they agree, else what differs.
function driftOf(doc) {
    const text = doc.getText();
    const fresh = TextDocument.create('fresh://x', 'clarion', 0, text);
    if (doc.lineCount !== fresh.lineCount) return 'lineCount ' + doc.lineCount + ', fresh ' + fresh.lineCount;
    for (let off = 0; off <= text.length; off++) {
        const p = doc.positionAt(off), q = fresh.positionAt(off);
        if (p.line !== q.line || p.character !== q.character)
            return 'positionAt(' + off + ') ' + p.line + ':' + p.character + ', fresh ' + q.line + ':' + q.character;
        const o = doc.offsetAt(q), f = fresh.offsetAt(q);
        if (o !== f) return 'offsetAt(' + q.line + ':' + q.character + ') ' + o + ', fresh ' + f;
    }
    return null;
}

function send(msg) {
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
    process.stdout.write(body);
}

function handle(msg) {
    const p = msg.params || {};
    if (msg.method === 'textDocument/didOpen') {
        const td = p.textDocument;
        const d = docs[td.uri] || (docs[td.uri] = { doc: null, version: 0, ranged: 0, full: 0, opens: 0, drift: 0, firstDrift: null });
        d.doc = TextDocument.create(td.uri, td.languageId, td.version, td.text); d.version = td.version; d.opens++;
        return;
    }
    if (msg.method === 'textDocument/didChange') {
        const d = docs[p.textDocument.uri];
        if (!d) return;   // a change for a document this server never opened: ignored, as a real server would
        for (const c of p.contentChanges) {
            if (c.range) d.ranged++; else d.full++;
            TextDocument.update(d.doc, [c], p.textDocument.version);
            const why = driftOf(d.doc);
            if (why) { d.drift++; if (!d.firstDrift) d.firstDrift = why + ' after ' + JSON.stringify(c).slice(0, 200); }
        }
        d.version = p.textDocument.version;
        return;
    }
    if (msg.id === undefined || msg.id === null) {
        if (msg.method === 'exit') process.exit(0);
        return;
    }
    let result = null;
    if (msg.method === 'initialize')
        result = { capabilities: { textDocumentSync: syncForm === 'object' ? { openClose: true, change: syncKind } : syncKind } };
    else if (msg.method === 'clarion/findFile') {
        const d = docs[p.filename];
        result = d ? { path: p.filename, text: d.doc.getText(), version: d.version, ranged: d.ranged, full: d.full, opens: d.opens,
                       drift: d.drift, firstDrift: d.firstDrift || '', impl: impl } : { path: '' };
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
