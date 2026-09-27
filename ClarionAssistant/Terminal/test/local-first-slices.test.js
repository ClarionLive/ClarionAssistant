// local-first-slices.test.js - 1c685f2e R11/R12: slices instead of the synced buffer, and the full sync only
// when typing pauses.
//
// Run:  node Terminal/test/local-first-slices.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL page code (see local-first-loader.js) against a hand-driven host, fake timers
// and a model whose decorations shift like Monaco's when lines are inserted. Pinned:
//   R12  a keystroke posts NO bufferSync; exactly one goes 400 ms after the last edit. LSP-bound requests
//        (completion, hover, diagnostics) for the unsynced version wait for that sync; completion shows the
//        local items meanwhile. At rest a request still syncs at once (bufferResync recovery).
//   R11  the host's span map becomes tracked decorations; localCompletion / localHover / slotDiagnostics carry
//        the caret's procedure (capped at 3000 lines), the owner's data for a Class.Method and the routine names,
//        read from the decorations' CURRENT ranges; needHeader -> headerSync -> one retry; sliceChars is logged.

const { html, load, makeModel, track, flush, check, section, finish } = require('./local-first-loader');

const LINES = [
    "  MEMBER('app')",          // 1  module header
    '  MAP',                    // 2
    '  END',                    // 3
    'MyProc PROCEDURE',         // 4  proc 0: start
    'LOC:Count LONG',           // 5
    '  CODE',                   // 6  dataEnd
    '  lo',                     // 7
    '  DO MyRtn',               // 8
    'MyRtn ROUTINE',            // 9
    '  x = 1',                  // 10
    '',                         // 11
    '',                         // 12 end
    'ThisWindow.Init PROCEDURE', // 13 proc 1 (owner 0): start
    '  CODE',                   // 14 dataEnd
    '  SELF.',                  // 15
    '',                         // 16 end
];
const MAP = [
    { name: 'MyProc', start: 4, dataEnd: 6, end: 12, owner: null, routines: ['MyRtn'] },
    { name: 'ThisWindow.Init', start: 13, dataEnd: 14, end: 16, owner: 0, routines: ['MyRtn'] },
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
function type(e, line, text) { e.model.setLine(line, text); e.api.noteBufferEdit(); }
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

    section('R11: the slice comes from the tracked decorations');
    {
        const e = setup();
        type(e, 11, 'NewRtn ROUTINE');
        ask(e, 7, 5);
        const s = e.requests('localCompletion')[0].slice;
        check('R11.1 span = the caret\'s procedure, from its start', s && s.span.start === 4 &&
            s.span.text === joinLines(e.model._lines, 4, 12), JSON.stringify(s && s.span).slice(0, 120));
        check('R11.1 ...the header hash, no owner data for a plain procedure', s && s.headerHash === 'H1' && s.ownerData === null);
        check('R11.1 ...routines = the map\'s plus a ROUTINE label typed since', s && s.routines.join() === 'MyRtn,NewRtn', s && s.routines.join());

        // Lines typed ABOVE the procedure: the decorations moved, the map's numbers did not.
        e.model.insertLines(2, ['  ! one', '  ! two', '  ! three']);
        e.api.noteBufferEdit();
        ask(e, 10, 5);
        const s2 = e.requests('localCompletion')[1].slice;
        check('R11.2 after 3 lines inserted above, the span starts at 7 (the decoration), not 4 (the map)',
            s2 && s2.span.start === 7 && s2.span.text === joinLines(e.model._lines, 7, 15), s2 && ('start ' + s2.span.start));

        ask(e, 18, 9);
        const s3 = e.requests('localCompletion')[2].slice;
        check('R11.3 a Class.Method carries its owner\'s data section (start..CODE)', s3 && s3.ownerData &&
            s3.ownerData.start === 7 && s3.ownerData.text === joinLines(e.model._lines, 7, 9) && s3.span.start === 16,
            JSON.stringify(s3 && s3.ownerData));

        ask(e, 5, 6);                                      // '  MAP' in the module header
        const s4 = e.requests('localCompletion')[3].slice;
        check('R11.1 outside every procedure: the gap before the next one (the module header), no routines',
            s4 && s4.span.start === 1 && s4.span.text === joinLines(e.model._lines, 1, 6) && s4.routines.length === 0,
            JSON.stringify(s4 && s4.span));
    }
    {
        // A 10,000-line procedure: the span is capped at 3000 lines around the caret.
        const big = ['  MEMBER()', 'Big PROCEDURE', '  CODE'];
        for (let i = 0; i < 10000; i++) big.push('  x = ' + i);
        const map = [{ name: 'Big', start: 2, dataEnd: 3, end: big.length, owner: null, routines: [] }];
        const e = setup({ lines: big, map });
        ask(e, 5000, 3);
        const s = e.requests('localCompletion')[0].slice;
        const n = s ? s.span.text.split('\r\n').length : 0;
        check('R11.4 a huge procedure is capped at 3000 lines around the caret', s && n === 3000 && s.span.start === 3500 &&
            s.span.start <= 5000 && 5000 < s.span.start + n, 'start ' + (s && s.span.start) + ' lines ' + n);
        ask(e, 10, 3);
        const s2 = e.requests('localCompletion')[1].slice;
        check('R11.4 ...near the start the window begins at the procedure', s2 && s2.span.start === 2 && s2.span.text.split('\r\n').length === 3000);
    }

    section('R11: no usable map -> the synced buffer; needHeader; sliceChars; slot slice');
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
        check('R11.6 ...then ONE retry with the same slice', count(h, 'localCompletion') === 2 &&
            JSON.stringify(h.requests('localCompletion')[1].slice) === JSON.stringify(h.requests('localCompletion')[0].slice));
        h.reply('localCompletion', { needHeader: true });
        await flush();
        check('R11.6 a second needHeader is not retried again', count(h, 'localCompletion') === 2 && count(h, 'headerSync') === 1);

        const l = setup();
        ask(l, 7, 5);
        const sl = l.requests('localCompletion')[0].slice;
        l.reply('localCompletion', { items: [] });
        await flush();
        const line = (l.posted.find(m => m.action === 'log' && /action=localCompletion/.test(m.line)) || {}).line || '';
        check('R11.7 [local-rt] logs sliceChars = span + owner data', new RegExp(' sliceChars=' + sl.span.text.length + '( |$)').test(line), line);

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
        check('R12 the editor arms the idle sync on every content change',
            /editor\.onDidChangeModelContent\(noteBufferEdit\);/.test(html));
    }

    finish();
}

main().catch(err => { console.error(err && err.stack || err); process.exit(1); });
