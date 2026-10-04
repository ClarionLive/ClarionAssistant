// local-first-slices.test.js - 1c685f2e R11/R11b/R12: slices instead of the synced buffer, and the full sync only
// when typing pauses.
//
// Run:  node Terminal/test/local-first-slices.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL page code (see local-first-loader.js) against a hand-driven host, fake timers
// and a model whose decorations shift like Monaco's when lines are inserted. Pinned:
//   R12  a keystroke posts NO bufferSync; exactly one goes 400 ms after the last edit. LSP-bound requests
//        (completion, hover, diagnostics) for the unsynced version wait for that sync; completion shows the
//        local items meanwhile. At rest a request still syncs at once (bufferResync recovery).
//   R11  the host's span map becomes tracked decorations; localCompletion / localHover carry PIECES (R11b): the
//        proc's DATA, the enclosing routine's DATA, a Class.Method owner's DATA - by hash while unedited, as text
//        once an edit touched them - plus a 200-line window each side of the caret clipped to the proc, all read
//        from the decorations' CURRENT ranges; needHeader / needPieces -> resend -> one retry; sliceChars logged.
//        slotDiagnostics carries the slots' text and the routine names.

const { html, load, track, flush, check, section, finish } = require('./local-first-loader');

const LINES = [
    "  MEMBER('app')",          // 1  module header
    '  MAP',                    // 2
    '  END',                    // 3
    'MyProc PROCEDURE',         // 4  proc 0: start
    'LOC:Count LONG',           // 5
    '  CODE',                   // 6  dataEnd
    '  lo',                     // 7
    '  DO MyRtn',               // 8
    'MyRtn ROUTINE',            // 9  routine (no DATA: dataEnd = start)
    '  x = 1',                  // 10
    '',                         // 11
    '',                         // 12 end
    'ThisWindow.Init PROCEDURE', // 13 proc 1 (owner 0): start
    '  CODE',                   // 14 dataEnd
    '  SELF.',                  // 15
    '',                         // 16 end
];
const MAP = [
    { name: 'MyProc', start: 4, dataEnd: 6, end: 12, owner: null, dataHash: 'D0', routines: ['MyRtn'],
      routineSpans: [{ name: 'MyRtn', start: 9, dataEnd: 9, dataHash: 'R0' }] },
    { name: 'ThisWindow.Init', start: 13, dataEnd: 14, end: 16, owner: 0, dataHash: 'D1', routines: ['MyRtn'], routineSpans: [] },
];
const joinLines = (lines, a, b) => lines.slice(a - 1, b).join('\r\n');

// A page with its first full sync done and the host's span map for it applied; posts from here on are fresh.
function setup(opts) {
    const e = load(Object.assign({ lines: LINES }, opts || {}));
    const p = e.api.withBuffer(e.model, {});
    e.api.applySpanMap({ type: 'spanMap', v: p.v, headerHash: 'H1', procs: (opts && opts.map) || MAP });
    e.posted.length = 0;
    return e;
}
// A keystroke on `line`: the new text, then the content-change event Monaco raises (pre-edit range).
function type(e, line, text) {
    e.model.setLine(line, text);
    e.api.noteBufferEdit({ changes: [{ range: { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: 1 } }] });
}
// The idle-sync timers (the local requests' own 400 ms give-up timers share the duration, so pick by callback).
const idleTimers = (e) => e.timers.filter(t => t.fn.name === 'idleSync');
function fireIdle(e) {
    let n = 0;
    for (const t of idleTimers(e)) if (!t.cleared && !t.fired) { t.fired = true; t.fn(); n++; }
    return n;
}
const ask = (e, line, column, ctx) => track(e.providers.completion[0].provideCompletionItems(e.model, { lineNumber: line, column }, ctx || {}));
const actions = (e) => e.posted.map(m => m.action);
const count = (e, action) => e.posted.filter(m => m.action === action).length;
const lastSlice = (e) => { const r = e.requests('localCompletion'); return r.length ? r[r.length - 1].slice : null; };
// Pieces as compact strings: "4#D0" (by hash) or "4:<n lines>" (with text).
const shape = (s) => s ? s.pieces.map(p => p.hash ? p.start + '#' + p.hash : p.start + ':' + p.text.split('\r\n').length).join(' ') : 'null';

async function main() {
    section('R12: no full sync while typing; one, 400 ms after the last edit');
    {
        const e = setup();
        type(e, 7, '  lo');
        const q = ask(e, 7, 5);
        check('R12.1 a keystroke posts NO bufferSync', count(e, 'bufferSync') === 0, JSON.stringify(actions(e)));
        check('R12.1 ...and no LSP completion for the unsynced version', count(e, 'completion') === 0);
        check('R12.1 ...only localCompletion, carrying a slice and no v', count(e, 'localCompletion') === 1 &&
            e.requests('localCompletion')[0].slice && !('v' in e.requests('localCompletion')[0]));
        e.reply('localCompletion', { items: [{ label: 'LOC:Count', kind: 6, insertText: 'LOC:Count' }] });
        await flush();
        check('R12.1 completion resolves local-only with incomplete:true', q.done && q.value.incomplete === true &&
            q.value.suggestions.some(s => s.label === 'LOC:Count'));
        for (const t of ['  loc', '  loc:', '  loc:c']) type(e, 7, t);
        check('R12.3 more keystrokes: still no bufferSync, and each re-arms the one idle timer', count(e, 'bufferSync') === 0 &&
            idleTimers(e).filter(t => !t.cleared).length === 1 && idleTimers(e).length === 4, 'timers ' + idleTimers(e).length);
        const fired = fireIdle(e);
        check('R12.2 400 ms after the last edit: exactly one bufferSync', fired === 1 && count(e, 'bufferSync') === 1,
            'fired ' + fired + ', syncs ' + count(e, 'bufferSync'));
        const sync = e.requests('bufferSync')[0];
        check('R12.2 ...carrying the current text', sync && sync.buffer === e.model.getValue());
        ask(e, 7, 8);
        const c = e.requests('completion')[0];
        check('R12.2 the re-query after the pause asks the LSP, naming that sync', c && c.v === sync.v && count(e, 'bufferSync') === 1);
    }

    section('R12: hover, diagnostics and superseded requests ride the idle sync');
    {
        const e = setup();
        type(e, 7, '  loc');
        const [loc, lsp] = e.providers.hover.map(p => track(p.provideHover(e.model, { lineNumber: 5, column: 3 })));
        check('R12.4 hover while typing: localHover goes at once, as a slice', count(e, 'localHover') === 1 && e.requests('localHover')[0].slice);
        e.reply('localHover', { contents: '```clarion\nLOC:Count  LONG\n```', authoritative: false });
        await flush();
        check('R12.4 ...the local card shows', loc.done && loc.value && loc.value.contents);
        check('R12.4 ...but the LSP hover waits (no hover, no bufferSync)', count(e, 'hover') === 0 && count(e, 'bufferSync') === 0);
        fireIdle(e);
        await flush();
        const s = e.requests('bufferSync')[0], h = e.requests('hover')[0];
        check('R12.4 after the idle sync the LSP hover goes, naming that sync', s && h && h.v === s.v &&
            e.posted.indexOf(s) < e.posted.indexOf(h), JSON.stringify(actions(e)));
        e.reply('hover', { contents: '```clarion\nOther\n```' });
        await flush();
        check('R12.4 ...and its card is returned', lsp.done && lsp.value && /Other/.test(lsp.value.contents[0].value));

        const f = setup();
        type(f, 7, '  lo');
        f.providers.hover.map(p => p.provideHover(f.model, { lineNumber: 5, column: 3 }));
        f.reply('localHover', { contents: null, authoritative: false });
        await flush();
        type(f, 7, '  loc');                              // the hover's version is gone before the pause
        fireIdle(f);
        await flush();
        check('R12.5 a deferred LSP request superseded by a later edit is never sent', count(f, 'hover') === 0 && count(f, 'bufferSync') === 1,
            JSON.stringify(actions(f)));

        // GH #250: a fallback (keyword) card while typing. The 300 ms deadline runs from the hover's start, so
        // it passes before the 400 ms idle sync: the keyword card shows, and the sync then posts no hover.
        const k = setup();
        type(k, 8, '  DO MyRtn ');
        const kwCard = '```clarion\nDO\n```\n\nkeyword';
        const [kloc, klsp] = k.providers.hover.map(p => track(p.provideHover(k.model, { lineNumber: 8, column: 4 })));
        k.reply('localHover', { contents: kwCard, authoritative: false, fallback: true, kind: 'keyword' });
        await flush();
        check('#250 typing: a fallback card waits for the idle sync (no hover yet), local provider empty',
            count(k, 'hover') === 0 && kloc.done && kloc.value == null && !klsp.done);
        k.fire(300);
        await flush();
        check('#250 typing: the 300 ms deadline shows the keyword card', klsp.done && klsp.value && klsp.value.contents[0].value === kwCard);
        fireIdle(k);
        await flush();
        check('#250 typing: the idle sync after the deadline posts no hover', count(k, 'hover') === 0 && count(k, 'bufferSync') === 1,
            JSON.stringify(actions(k)));

        // ...and a pause that comes first lets the server answer in time.
        const q = setup();
        type(q, 8, '  DO MyRtn ');
        const [, qlsp] = q.providers.hover.map(p => track(p.provideHover(q.model, { lineNumber: 8, column: 4 })));
        q.reply('localHover', { contents: kwCard, fallback: true });
        await flush();
        fireIdle(q);
        await flush();
        check('#250 typing: after the idle sync the server is asked', count(q, 'hover') === 1);
        q.reply('hover', { contents: '**DO** MyRtn' });
        await flush();
        check('#250 typing: ...and its card wins', qlsp.done && qlsp.value && qlsp.value.contents[0].value === '**DO** MyRtn');

        const d = setup();
        d.embedRanges = [[7, 8]];
        type(d, 7, '  loc');
        d.api.refreshDiagnostics();
        check('R12.6 diagnostics while typing: the slot checks go at once, as a slice', count(d, 'slotDiagnostics') === 1 &&
            d.requests('slotDiagnostics')[0].slots && !('v' in d.requests('slotDiagnostics')[0]));
        check('R12.6 ...the LSP pass does not force a sync', count(d, 'diagnostics') === 0 && count(d, 'bufferSync') === 0);
        fireIdle(d);
        await flush();
        const ds = d.requests('bufferSync')[0], dd = d.requests('diagnostics')[0];
        check('R12.6 ...it rides the idle sync', ds && dd && dd.v === ds.v, JSON.stringify(actions(d)));

        const r = setup();
        r.api.resetBufferSync();                           // the host lost its copy; nobody is typing
        r.model.setLine(7, '  lo');                        // a programmatic change, no keystroke
        ask(r, 7, 5);
        check('R12.7 at rest an unsynced version still syncs at once (bufferResync recovery)',
            count(r, 'bufferSync') === 1 && count(r, 'completion') === 1, JSON.stringify(actions(r)));
    }

    section('R11b: pieces - clean DATA by hash, the window as text, all from the tracked decorations');
    {
        const e = setup();
        ask(e, 7, 5);
        let s = lastSlice(e);
        check('R11b.1 caret in the proc body: [its DATA by hash, the window clipped to the proc]', shape(s) === '4#D0 4:9' &&
            s.pieces[1].text === joinLines(LINES, 4, 12), shape(s));
        check('R11b.1 ...the header hash and the map\'s routines', s && s.headerHash === 'H1' && s.routines.join() === 'MyRtn');

        ask(e, 10, 5);
        check('R11b.2 caret inside a routine: + the routine\'s DATA by hash (the last routine starting at/above the caret)',
            shape(lastSlice(e)) === '4#D0 9#R0 4:9', shape(lastSlice(e)));

        ask(e, 15, 9);
        check('R11b.3 a Class.Method: its DATA, then the owner\'s DATA, both by hash, then its window',
            shape(lastSlice(e)) === '13#D1 4#D0 13:4', shape(lastSlice(e)));

        ask(e, 2, 6);                                     // '  MAP' in the module header
        s = lastSlice(e);
        check('R11b.4 outside every procedure: just the window over the gap (the module header), no routines',
            shape(s) === '1:3' && s.routines.length === 0, shape(s));
    }
    {
        // Lines inserted ABOVE the procedure: the decorations moved, the map's numbers did not, the text did not.
        const e = setup();
        e.model.insertLines(2, ['  ! one', '  ! two', '  ! three']);
        e.api.noteBufferEdit({ changes: [{ range: { startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 1 } }] });
        ask(e, 10, 5);
        const s = lastSlice(e);
        check('R11b.5 after 3 lines inserted above: DATA still by hash, at its new start 7; window from 7',
            shape(s) === '7#D0 7:9' && s.pieces[1].text === joinLines(e.model._lines, 7, 15), shape(s));
    }
    {
        // An edit INSIDE a DATA piece: that piece travels as text until the next map; the others stay hashes.
        const e = setup();
        type(e, 5, 'LOC:Count LONG,DIM(2)');
        ask(e, 10, 5);
        const s = lastSlice(e);
        check('R11b.6 an edited DATA piece is sent with its current text', s && s.pieces[0].start === 4 && !s.pieces[0].hash &&
            s.pieces[0].text === joinLines(e.model._lines, 4, 6), shape(s));
        check('R11b.6 ...the untouched routine DATA still goes by hash', s && s.pieces[1].hash === 'R0', shape(s));
        type(e, 11, '  y = 2');                          // an edit in the body (not DATA) marks nothing new
        ask(e, 10, 5);
        check('R11b.6 an edit outside every DATA piece leaves them as they were', shape(lastSlice(e)) === '4:3 9#R0 4:9', shape(lastSlice(e)));
        fireIdle(e);
        const v = e.requests('bufferSync')[0].v;
        e.api.applySpanMap({ type: 'spanMap', v, headerHash: 'H1', procs: MAP.map(p => Object.assign({}, p, p.name === 'MyProc' ? { dataHash: 'D0b' } : {})) });
        ask(e, 10, 5);
        check('R11b.6 a new span map clears the dirty flag: the piece goes by its NEW hash', shape(lastSlice(e)) === '4#D0b 9#R0 4:9', shape(lastSlice(e)));
    }
    {
        // A 10,000-line procedure: the window is 200 lines each side of the caret, clipped to the proc.
        const big = ['  MEMBER()', 'Big PROCEDURE', '  CODE'];
        for (let i = 0; i < 10000; i++) big.push('  x = ' + i);
        const map = [{ name: 'Big', start: 2, dataEnd: 3, end: big.length, owner: null, dataHash: 'DB', routines: [], routineSpans: [] }];
        const e = setup({ lines: big, map });
        ask(e, 5000, 3);
        check('R11b.7 the window is 200 lines above and below the caret', shape(lastSlice(e)) === '2#DB 4800:401', shape(lastSlice(e)));
        ask(e, 10, 3);
        check('R11b.7 ...clipped at the proc start', shape(lastSlice(e)) === '2#DB 2:209', shape(lastSlice(e)));
        ask(e, big.length - 5, 3);
        check('R11b.7 ...clipped at the proc end', shape(lastSlice(e)) === '2#DB ' + (big.length - 205) + ':206', shape(lastSlice(e)));
        check('R11b.7 a keystroke payload stays small (by hash + ~400 lines)', JSON.stringify(e.requests('localCompletion')[0]).length < 16000,
            'bytes ' + JSON.stringify(e.requests('localCompletion')[0]).length);
    }

    section('F3: REAL typing - the keystroke arms the idle timer before Monaco asks; the LSP is still asked');
    {
        const e = setup();
        type(e, 7, '  l');
        ask(e, 7, 4);
        e.reply('localCompletion', { items: [] });
        type(e, 7, '  lo');
        ask(e, 7, 5);
        e.reply('localCompletion', { items: [] });
        await flush();
        check('F3 while typing: no completion is sent and no race timer armed (typing is not a miss)',
            count(e, 'completion') === 0 && e.armed(200) === 0, 'completion ' + count(e, 'completion') + ' races ' + e.armed(200));
        fireIdle(e);                                      // the pause
        await flush();
        const cs = e.requests('completion'), sync = e.requests('bufferSync')[0];
        check('F3 the pause sends ONE completion, for the newest word (a replaced entry is dropped), after the sync',
            cs.length === 1 && cs[0].line === 7 && cs[0].column === 5 && sync && cs[0].v === sync.v &&
            e.posted.indexOf(sync) < e.posted.indexOf(cs[0]), JSON.stringify(cs.map(c => [c.line, c.column])));
        e.reply('completion', { items: [{ label: 'LocLsp', kind: 6, insertText: 'LocLsp' }, { label: 'LongLsp', kind: 6, insertText: 'LongLsp' }] });
        await flush();
        type(e, 7, '  loc');                              // the first keystroke after the pause
        const q = ask(e, 7, 6);
        e.reply('localCompletion', { items: [{ label: 'LOC:Count', kind: 6, insertText: 'LOC:Count' }] });
        await flush();
        const labels = q.done ? q.value.suggestions.map(s => s.label) : [];
        check('F3 the next keystroke merges the LSP answer (filtered to "loc") with local, no new request',
            q.done && labels.includes('LocLsp') && !labels.includes('LongLsp') && labels.includes('LOC:Count') && count(e, 'completion') === 1,
            JSON.stringify(labels));
    }

    section('K1: slot checks that answer slowly (first open) are still painted');
    {
        const slotMarker = { severity: 8, message: 'DO NoSuchRoutine: routine not found', line: 8, column: 3, endLine: 8, endColumn: 20 };
        const painted = (e) => ((e.markers || {})['clarion-slot'] || []).map(m => m.startLineNumber + ':' + m.message);

        const e = setup();
        e.embedRanges = [[7, 8]];
        e.api.refreshDiagnostics();
        const req = e.requests('slotDiagnostics')[0];
        e.elapse(450);                                    // 450 ms pass before the host's reply lands
        e.deliver(req, { markers: [slotMarker], ms: 151 });
        await flush();
        check('K1 a reply at 450 ms for the still-current version is painted', painted(e).join() === '8:' + slotMarker.message,
            JSON.stringify(painted(e)));

        const l = setup();
        l.embedRanges = [[7, 8]];
        l.api.refreshDiagnostics();
        const lr = l.requests('slotDiagnostics')[0];
        l.elapse(lr.timeoutMs);                           // past even the slot budget: the page gave up
        await flush();
        l.deliver(lr, { markers: [slotMarker], ms: 151 });
        await flush();
        check('K1 a reply after the page\'s timeout is still painted (the version is current)', painted(l).join() === '8:' + slotMarker.message,
            JSON.stringify(painted(l)));
        const log = l.posted.filter(m => m.action === 'log' && /action=slotDiagnostics/.test(m.line)).map(m => m.line);
        check('K1 ...and [local-rt] logs the timeout, then the late arrival', log.length === 2 && / timeout=1/.test(log[0]) && / late=1/.test(log[1]),
            JSON.stringify(log));
        l.markers['clarion-slot'] = [];
        l.deliver(lr, { markers: [slotMarker] });          // a duplicate delivery for the same reqId
        await flush();
        check('K1 ...and a late reqId is delivered at most once', painted(l).length === 0, JSON.stringify(painted(l)));
        check('K1 the page\'s message handler delivers replies through deliverHostReply (so late ones reach it)',
            /msg\.type === 'response' && msg\.reqId != null\) \{\s*\n\s*deliverHostReply\(msg\.reqId, msg\.data\);/.test(html));

        const s = setup();
        s.embedRanges = [[7, 8]];
        s.api.refreshDiagnostics();
        const sr = s.requests('slotDiagnostics')[0];
        s.elapse(sr.timeoutMs);
        await flush();
        s.model.setLine(8, '  DO MyRtn');                  // the text moved on before the late reply
        s.deliver(sr, { markers: [slotMarker], ms: 151 });
        await flush();
        check('K1 a late reply for a stale version is dropped', painted(s).length === 0, JSON.stringify(painted(s)));
    }

    section('F2: an edited module header travels as text');
    {
        const e = setup();
        ask(e, 7, 5);
        check('F2 a clean header: no headerText (the host has it by hash)', lastSlice(e) && !('headerText' in lastSlice(e)));
        type(e, 2, '  MAP ! edited');
        ask(e, 7, 5);                                     // inside a procedure, before any idle sync
        const s = lastSlice(e);
        check('F2 after a header edit, the slice carries the current header text (no final newline)',
            s && s.headerText === joinLines(e.model._lines, 1, 3), JSON.stringify(s && s.headerText));
        check('F2 ...and the DATA pieces are untouched (still by hash)', s && s.pieces[0].hash === 'D0');
        fireIdle(e);
        e.api.applySpanMap({ type: 'spanMap', v: e.requests('bufferSync')[0].v, headerHash: 'H2', procs: MAP });
        ask(e, 7, 5);
        check('F2 a new span map clears it: back to the (new) hash', lastSlice(e) && !('headerText' in lastSlice(e)) &&
            lastSlice(e).headerHash === 'H2');
    }

    section('F4: a multi-change edit that changes the line count marks everything from its first line');
    {
        const e = setup();
        e.api.noteBufferEdit({ changes: [
            { range: { startLineNumber: 10, startColumn: 1, endLineNumber: 10, endColumn: 1 }, text: 'a\nb' },
            { range: { startLineNumber: 11, startColumn: 1, endLineNumber: 11, endColumn: 1 }, text: 'x' }] });
        ask(e, 15, 9);
        check('F4 a piece below the first change (proc 1 DATA) goes as text', shape(lastSlice(e)).indexOf('13:2') === 0, shape(lastSlice(e)));
        ask(e, 10, 5);
        check('F4 ...pieces above it stay hashes', shape(lastSlice(e)).indexOf('4#D0 9#R0') === 0, shape(lastSlice(e)));
        const f = setup();
        f.api.noteBufferEdit({ changes: [{ range: { startLineNumber: 10, startColumn: 1, endLineNumber: 10, endColumn: 1 }, text: 'a\nb' }] });
        ask(f, 15, 9);
        check('F4 a SINGLE change adding a line leaves the pieces below by hash', shape(lastSlice(f)).indexOf('13#D1') === 0, shape(lastSlice(f)));
    }

    section('R11: no usable map -> the synced buffer; needHeader / needPieces; sliceChars; slot slice');
    {
        const e = load({ lines: LINES });
        ask(e, 7, 5);
        const lc = e.requests('localCompletion')[0];
        check('R11.5 no span map yet: localCompletion falls back to v', lc && typeof lc.v === 'number' && !lc.slice);

        const st = load({ lines: LINES });
        const p = st.api.withBuffer(st.model, {});
        st.model.setLine(7, '  loc');                      // the model moved on before the map arrived
        st.api.applySpanMap({ type: 'spanMap', v: p.v, headerHash: 'H1', procs: MAP });
        check('R11.5 a map for a version the model has left is ignored', st.api.buildSlice(st.model, 7) === null);

        const h = setup();
        ask(h, 7, 5);
        h.reply('localCompletion', { needHeader: true });
        await flush();
        const hs = h.requests('headerSync')[0];
        check('R11.6 needHeader -> headerSync with the hash and the header text (line 1 to the first procedure)',
            hs && hs.hash === 'H1' && hs.text === joinLines(LINES, 1, 3), JSON.stringify(hs));
        check('R11.6 ...then ONE retry with the same pieces', count(h, 'localCompletion') === 2 &&
            JSON.stringify(h.requests('localCompletion')[1].slice) === JSON.stringify(h.requests('localCompletion')[0].slice));
        h.reply('localCompletion', { needHeader: true });
        await flush();
        check('R11.6 a second miss is not retried again', count(h, 'localCompletion') === 2 && count(h, 'headerSync') === 1);

        const n = setup();
        ask(n, 10, 5);
        n.reply('localCompletion', { needPieces: ['R0'] });
        await flush();
        const retry = n.requests('localCompletion')[1];
        check('R11b.8 needPieces -> ONE retry with exactly those pieces as text, the rest unchanged',
            retry && shape(retry.slice) === '4#D0 9:1 4:9' && retry.slice.pieces[1].text === LINES[8] && count(n, 'headerSync') === 0,
            retry && shape(retry.slice));
        n.reply('localCompletion', { needPieces: ['R0'] });
        await flush();
        check('R11b.8 a second needPieces is not retried again', count(n, 'localCompletion') === 2);

        const b = setup();
        ask(b, 10, 5);
        b.reply('localCompletion', { needHeader: true, needPieces: ['D0', 'R0'] });
        await flush();
        const rb = b.requests('localCompletion')[1];
        check('R11b.8 needHeader + needPieces together: the header once, both pieces as text, one retry',
            count(b, 'headerSync') === 1 && rb && shape(rb.slice) === '4:3 9:1 4:9', rb && shape(rb.slice));
        b.reply('localCompletion', { items: [{ label: 'LOC:Count', kind: 6 }] });
        await flush();

        const l = setup();
        type(l, 5, 'LOC:Count LONG,DIM(2)');
        ask(l, 10, 5);                                     // pieces: DATA as text, routine DATA by hash, window
        const sl = lastSlice(l);
        l.reply('localCompletion', { items: [] });
        await flush();
        const line = (l.posted.find(m => m.action === 'log' && /action=localCompletion/.test(m.line)) || {}).line || '';
        const withText = sl.pieces.filter(pc => pc.text).reduce((k, pc) => k + pc.text.length, 0);
        check('R11.7 [local-rt] logs sliceChars = the text the pieces carried (hash pieces count 0)',
            new RegExp(' sliceChars=' + withText + '( |$)').test(line), line + ' / expected ' + withText);

        const d = setup();
        d.embedRanges = [[7, 8], [15, 15]];
        d.api.refreshDiagnostics();
        const sd = d.requests('slotDiagnostics')[0];
        check('R11.8 slotDiagnostics carries the slots\' text and the holding procs\' routines, no v', sd && !('v' in sd) &&
            JSON.stringify(sd.slots) === JSON.stringify([{ start: 7, text: joinLines(LINES, 7, 8) }, { start: 15, text: LINES[14] }]) &&
            sd.routines.join() === 'MyRtn', JSON.stringify(sd));

        const r = setup();
        r.api.resetLocalFirstState();
        check('R11.9 setSource drops the span map and its decorations', r.api.buildSlice(r.model, 7) === null &&
            Object.keys(r.model._decs).length === 0);
        check('R11.10 the page routes the host\'s spanMap message to applySpanMap',
            /msg\.type === 'spanMap'\)\s*\{\s*\n\s*applySpanMap\(msg\);/.test(html));
        check('R12 the editor arms the idle sync on every content change (and passes the event on)',
            /editor\.onDidChangeModelContent\(noteBufferEdit\);/.test(html));
    }

    finish();
}

main().catch(err => { console.error(err && err.stack || err); process.exit(1); });
