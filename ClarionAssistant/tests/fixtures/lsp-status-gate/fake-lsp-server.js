// Scripted Clarion language server for LspDiagnostics.StatusGateTest.ps1 (GH #216).
//
// It speaks just enough LSP over stdio (Content-Length framing) to let the real LspClient start,
// and then answers each didOpen/didChange with a FIXED sequence of publishDiagnostics and
// clarion/diagnosticsStatus notifications chosen by the document's file name. The sequences copy
// the wire shapes of the real server (1.0.5): { uri, version, state } with state complete |
// deferred | superseded, sent AFTER the publish it closes.
//
// Why a script rather than the real server here: the real server decides for itself whether an
// analysis is deferred, and for how long. The cases below (a complete for ANOTHER uri, a complete
// for a STALE version, a server that never sends the status at all) cannot be produced on demand
// from it. LspDiagnostics.SemanticPassTest.ps1 is the live-server guard; this is the contract guard.
//
// FAKE_LSP_MODE=status   (default) the server sends diagnosticsStatus
// FAKE_LSP_MODE=nostatus the server never sends it (an older server; the fallback path)

'use strict';

const MODE = process.env.FAKE_LSP_MODE || 'status';

let buffer = Buffer.alloc(0);
const texts = new Map();   // uri -> the last text accepted (the #359 guard's snapshot)

function send(msg) {
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    process.stdout.write('Content-Length: ' + body.length + '\r\n\r\n');
    process.stdout.write(body);
}

function notify(method, params) { send({ jsonrpc: '2.0', method, params }); }

// version: optional; the real server sends it from v1.0.8 (#619), 1.0.5 never did.
function publish(uri, messages, version) {
    const p = {
        uri,
        diagnostics: messages.map((m, i) => ({
            range: { start: { line: i, character: 0 }, end: { line: i, character: 5 } },
            severity: 1,
            source: 'fake-lsp',
            message: m
        }))
    };
    if (version !== undefined) p.version = version;
    notify('textDocument/publishDiagnostics', p);
}

// firstcall-server.js swaps this for the real server's spelling of the sender (LspClient's start-time probe).
let statusSender = p => notify('clarion/diagnosticsStatus', p);

function status(uri, version, state) {
    const p = { uri, state };
    if (version !== undefined) p.version = version;
    statusSender(p);
}

function later(ms, fn) { setTimeout(fn, ms); }

function siblingUri(uri, name) { return uri.replace(/[^/]+$/, name); }

function analyse(uri, version) {
    const name = uri.substring(uri.lastIndexOf('/') + 1).toLowerCase();
    process.stderr.write('[fake-lsp] analyse ' + name + ' v' + version + ' mode=' + MODE + '\n');

    if (MODE === 'nostatus') {
        if (name === 'twopass.clw') {
            // An older server's two-phase analysis: sync pass, boundary marker, semantic pass.
            publish(uri, []);
            notify('clarion/symbolsRefreshed', { uri });
            // Inside the 400ms settle window: today's fallback catches it; longer, and it never did.
            later(200, () => publish(uri, ['NOSTATUS-SEMANTIC']));
        } else {
            // A clean file: one publish, then silence. The settle window is the only way to answer.
            publish(uri, []);
        }
        return;
    }

    switch (name) {
        case 'deferred.clw':
            // The shape from GH #216: partial publish + deferred, then after a drain (longer than the
            // 400ms settle window) the real publish and complete.
            publish(uri, []);
            status(uri, version, 'deferred');
            notify('clarion/symbolsRefreshed', { uri });
            later(1200, () => {
                publish(uri, ['DEFERRED-REAL']);
                status(uri, version, 'complete');
            });
            break;

        case 'wronguri.clw': {
            // Deferred, and the only complete that ever arrives is for a DIFFERENT document.
            const other = siblingUri(uri, 'bystander.clw');
            publish(uri, []);
            status(uri, version, 'deferred');
            later(300, () => {
                publish(other, ['BYSTANDER']);
                status(other, 1, 'complete');
            });
            break;
        }

        case 'stalever.clw':
            // Deferred, and the only complete that arrives is for an OLDER version of this document.
            publish(uri, []);
            status(uri, version, 'deferred');
            later(300, () => status(uri, version - 1, 'complete'));
            break;

        case 'superseded.clw':
            // Our version is superseded; the NEWER version then completes. Must be accepted.
            publish(uri, []);
            status(uri, version, 'superseded');
            later(500, () => {
                publish(uri, ['SUPERSEDED-NEWER']);
                status(uri, version + 1, 'complete');
            });
            break;

        case 'firstcall.clw':
            // c7878eba, the shape of a 62k-line module: the sync-pass publish at once with NO status, the
            // complete answer and its status 1500ms later (beyond the 400ms settle window).
            publish(uri, ['SYNC-PARTIAL']);
            later(1500, () => {
                publish(uri, ['SYNC-PARTIAL', 'SEMANTIC-FULL']);
                status(uri, version, 'complete');
            });
            break;

        case 'partial.clw':
            // 92d06c29: the v1.0.8 shape on a huge module. A versioned sync-pass publish at once, the complete
            // answer long after a 3 s budget. The budget expires on a publish for the current text: partial.
            publish(uri, ['PARTIAL-SO-FAR'], version);
            later(6000, () => {
                publish(uri, ['PARTIAL-SO-FAR', 'PARTIAL-REST'], version);
                status(uri, version, 'complete');
            });
            break;

        case 'slow.clw':
            // 92d06c29: completes at 5 s. Pending at the 3 s default; complete with timeout_ms 10000.
            publish(uri, ['SLOW-SYNC'], version);
            later(5000, () => {
                publish(uri, ['SLOW-SYNC', 'SLOW-FULL'], version);
                status(uri, version, 'complete');
            });
            break;

        case 'emptypartial.clw':
            // 92d06c29 (b): a current-version publish with NOTHING in it, the complete much later. Pending,
            // and NOT partial: partial is only ever true with entries.
            publish(uri, [], version);
            later(6000, () => {
                publish(uri, ['EMPTYPARTIAL-LATE'], version);
                status(uri, version, 'complete');
            });
            break;

        case 'settled.clw':
            // 92d06c29: analysed and complete at once. Asked about again with the disk text unchanged, the
            // second call must answer from that complete; a re-sent identical version is skipped (#359).
            publish(uri, ['SETTLED-ONE'], version);
            status(uri, version, 'complete');
            break;

        case 'stalepub.clw':
            // 92d06c29: the only publish describes an OLDER text. Never served, not even as partial.
            publish(uri, ['OLD-TEXT'], version - 1);
            break;

        default:
            // Plain clean file: one publish, complete straight away (the libsrc shape).
            publish(uri, []);
            status(uri, version, 'complete');
            break;
    }
}

function handle(msg) {
    if (msg.id !== undefined && msg.method) {
        // Request. initialize gets capabilities; everything else a null result so nothing hangs.
        if (msg.method === 'initialize') {
            send({ jsonrpc: '2.0', id: msg.id, result: { capabilities: { textDocumentSync: 1 }, serverInfo: { name: 'fake-lsp', version: '0.0.0' } } });
        } else if (msg.method === 'shutdown') {
            send({ jsonrpc: '2.0', id: msg.id, result: null });
        } else {
            send({ jsonrpc: '2.0', id: msg.id, result: null });
        }
        return;
    }
    if (msg.method === 'textDocument/didOpen') {
        const td = msg.params.textDocument;
        texts.set(td.uri, td.text);
        analyse(td.uri, td.version);
    } else if (msg.method === 'textDocument/didChange') {
        const td = msg.params.textDocument;
        // The real server's #359 ContentChangeGuard: a change whose text is identical to the last accepted text
        // is skipped. No analysis, no publish, no status, ever, for that version (92d06c29). This server
        // advertises full sync, so the last change carries the whole text.
        const changes = msg.params.contentChanges || [];
        const last = changes.length ? changes[changes.length - 1] : null;
        if (last && !last.range && typeof last.text === 'string') {
            if (texts.get(td.uri) === last.text) {
                process.stderr.write('[fake-lsp] #359 skipping identical-content change v' + td.version + '\n');
                return;
            }
            texts.set(td.uri, last.text);
        }
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
        const header = buffer.slice(0, headerEnd).toString('ascii');
        const m = /Content-Length:\s*(\d+)/i.exec(header);
        if (!m) { buffer = buffer.slice(headerEnd + 4); continue; }
        const len = parseInt(m[1], 10);
        if (buffer.length < headerEnd + 4 + len) return;
        const body = buffer.slice(headerEnd + 4, headerEnd + 4 + len).toString('utf8');
        buffer = buffer.slice(headerEnd + 4 + len);
        try { handle(JSON.parse(body)); } catch (e) { process.stderr.write('[fake-lsp] bad message: ' + e.message + '\n'); }
    }
});
process.stdin.on('end', () => process.exit(0));

module.exports = { notify, setStatusSender: fn => { statusSender = fn; } };
