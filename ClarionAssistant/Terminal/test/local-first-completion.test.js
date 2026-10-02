// local-first-completion.test.js - 1c685f2e item 5: two-phase completion in the CA Embeditor.
//
// Run:  node Terminal/test/local-first-completion.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL completion provider extracted from monaco-embeditor.html (see
// local-first-loader.js) against a host whose replies and timers the test releases by hand. Pinned:
//   * the local list is shown while the LSP is slow (the 200 ms budget fires), with incomplete:true
//   * the LSP answer is cached on (model.id, line, wordStart) - NOT versionId - and reused on the next
//     keystroke while the typed prefix extends the cached one, filtered client-side
//   * 3 consecutive race misses on a model skip the race; a fast LSP reply (raced or not) ends skip mode
//   * case-insensitive dedupe with the local item winning; overloads survive; the sortText tiers hold
//   * setSource empties the cache; nothing ever re-triggers the suggest widget

const { html, PROVIDERS_SRC, load, makeModel, track, flush, check, section, finish } = require('./local-first-loader');

const BUDGET = 200;
const item = (label, detail, extra) => Object.assign({ label, kind: 6, detail, insertText: label }, extra || {});
const labels = (res) => (res && res.suggestions || []).map(s => s.label);
const provider = (e) => e.providers.completion[0];
// Ask for completion at (line, column) on the env's current model; returns a tracked promise.
const ask = (e, line, column, ctx) => track(provider(e).provideCompletionItems(e.model, { lineNumber: line, column }, ctx || {}));
// Type `text` as the content of line `line` and ask at its end.
const typeAndAsk = (e, line, text, ctx) => { e.model.setLine(line, text); return ask(e, line, text.length + 1, ctx); };
const allTriggers = [];

// Drive one query in which the LSP MISSES its budget: local answers, the budget fires, the LSP lands late.
async function missOnce(e, line, text, lspItems) {
    const q = typeAndAsk(e, line, text);
    e.reply('localCompletion', { items: [], source: 'local', ms: 1 });
    e.fire(BUDGET);
    await flush();
    e.clock += 1000;                         // the LSP reply lands well past its budget: not a hit
    if (e.pending('completion').length) e.reply('completion', { items: lspItems || [] });
    await flush();
    return q;
}

async function main() {
    section('5.1 / 5.2: the local list shows while the LSP is slow; a fast LSP is merged into it');
    {
        const e = load();
        const q = typeAndAsk(e, 2, '    lo');
        check('both requests are posted at once', e.requests('localCompletion').length === 1 && e.requests('completion').length === 1);
        e.reply('localCompletion', { items: [item('LocalVar', 'LONG (local)')], source: 'local', ms: 2 });
        await flush();
        check('5.1 the provider waits for the budget while the LSP is out', !q.done);
        e.fire(BUDGET);
        await flush();
        check('5.1 budget fired: resolves with the LOCAL items while completion is still pending',
            q.done && labels(q.value).filter(l => l !== 'LOOP').join() === 'LocalVar' && e.pending('completion').length === 1,
            JSON.stringify(q.value && labels(q.value)));   // (LOOP: the injected keyword for "lo")
        check('5.1 ...and incomplete:true, so the next keystroke re-queries', q.done && q.value.incomplete === true);
        allTriggers.push(...e.triggers);

        const e2 = load();
        const q2 = typeAndAsk(e2, 2, '    lo');
        e2.clock = 50;
        e2.reply('completion', { items: [item('LongLspThing', 'lsp')] });
        e2.reply('localCompletion', { items: [item('LocalVar', 'local')], source: 'local', ms: 2 });
        await flush();
        check('5.2 the LSP inside the budget: one merged list, no budget wait', q2.done &&
            labels(q2.value).includes('LocalVar') && labels(q2.value).includes('LongLspThing'), JSON.stringify(q2.value && labels(q2.value)));
        allTriggers.push(...e2.triggers);
    }

    section('5.3 / 5.4 / 5.5: dedupe, overloads, sort tiers on the merged list');
    {
        const e = load();
        const q = typeAndAsk(e, 2, '    lo');
        e.reply('localCompletion', { items: [item('LOC:Count', 'LONG (local)')] });
        e.reply('completion', { items: [item('loc:count', 'LSP detail'), item('Open(LONG)', 'lsp', { insertText: 'Open' }),
            item('Open(STRING)', 'lsp', { insertText: 'Open' })] });
        await flush();
        const rows = (q.value && q.value.suggestions || []).filter(s => /^loc:count$/i.test(s.label));
        check('5.3 local LOC:Count + LSP loc:count -> ONE row, with the LOCAL detail', rows.length === 1 && rows[0].detail === 'LONG (local)',
            JSON.stringify(rows.map(r => [r.label, r.detail])));
        check('5.4 overloads with the same insertText both survive (GH #187)',
            labels(q.value).includes('Open(LONG)') && labels(q.value).includes('Open(STRING)'), JSON.stringify(labels(q.value)));

        const t = load();
        const qt = typeAndAsk(t, 2, '    pro');
        t.reply('localCompletion', { items: [item('PRO', 'local exact'), item('ProcName', 'local')] });
        t.reply('completion', { items: [item('PROCEDURE', 'keyword', { kind: 14 }), item('ProgressBar', 'lsp')] });
        await flush();
        const by = {};
        (qt.value && qt.value.suggestions || []).forEach(s => { by[s.label] = s; });
        check('5.5 exact local identifier -> 00_ and preselected', by.PRO && by.PRO.sortText === '00_PRO' && by.PRO.preselect === true,
            by.PRO && by.PRO.sortText);
        check('5.5 keyword from the LSP -> 0kw_', by.PROCEDURE && by.PROCEDURE.sortText === '0kw_PROCEDURE', by.PROCEDURE && by.PROCEDURE.sortText);
        check('5.5 other items (local and LSP) -> 1_', by.ProcName && by.ProcName.sortText === '1_ProcName' &&
            by.ProgressBar && by.ProgressBar.sortText === '1_ProgressBar');
        check('5.5 injected keywords are still added (PROGRAM)', !!by.PROGRAM && by.PROGRAM.sortText === '0kw_PROGRAM');
    }

    section('5.6 / 5.7 / 5.8: the LSP cache is keyed on (model.id, line, wordStart) and served while the prefix extends');
    {
        const e = load();
        await missOnce(e, 2, '    lo', [item('LocalVar', 'lsp dup'), item('LocCached', 'lsp only'), item('LongLspOnly', 'lsp only')]);
        const v1 = e.model.getVersionId();
        const nComp = e.requests('completion').length;
        const q = typeAndAsk(e, 2, '    loc');
        check('5.6 the model version moved on (the cache must not key on it)', e.model.getVersionId() !== v1);
        check('5.6 the next keystroke posts NO new completion', e.requests('completion').length === nComp,
            'completion posts ' + nComp + ' -> ' + e.requests('completion').length);
        e.reply('localCompletion', { items: [item('LocalVar', 'local')] });
        await flush();
        check('5.6 ...and resolves at once (no budget to wait for)', q.done);
        check('5.6 the LSP-only item appears, filtered to "loc"', q.done && labels(q.value).includes('LocCached') && !labels(q.value).includes('LongLspOnly'),
            JSON.stringify(q.value && labels(q.value)));
        const lv = (q.value && q.value.suggestions || []).filter(s => s.label === 'LocalVar');
        check('5.6 ...deduped against local (local detail wins)', lv.length === 1 && lv[0].detail === 'local');
        const rangeOk = (q.value && q.value.suggestions || []).every(s => !s.range || (s.range.startColumn === 5 && s.range.endColumn === 8));
        check('5.6 cached items take the CURRENT replace range', rangeOk);

        // 5.7: a prefix that does not extend the cached one asks again and does not show its items.
        let n = e.requests('completion').length;
        const qx = typeAndAsk(e, 2, '    lx');
        check('5.7 "lx" (does not extend "lo") posts a new completion', e.requests('completion').length === n + 1);
        e.reply('localCompletion', { items: [] });
        e.fire(BUDGET);
        await flush();
        check('5.7 ...and shows none of the "lo" items', qx.done && !labels(qx.value).includes('LocCached'), JSON.stringify(qx.value && labels(qx.value)));
        e.clock += 1000;
        e.reply('completion', { items: [item('LxThing', 'lsp')] });
        await flush();
        n = e.requests('completion').length;
        const ql = typeAndAsk(e, 2, '    l');
        check('5.7 "l" (shorter than the cached "lx") posts a new completion', e.requests('completion').length === n + 1);
        e.reply('localCompletion', { items: [] }); e.fire(BUDGET); await flush();
        check('5.7 ...and does not show the "lx" items', ql.done && !labels(ql.value).includes('LxThing'));

        // 5.8: another line, another wordStart, another model.
        const f = load({ lines: ['  CODE', '    lo', '    lo', '      lo'] });
        await missOnce(f, 2, '    lo', [item('LocCached', 'lsp')]);
        n = f.requests('completion').length;
        const qLine = ask(f, 3, 7);
        check('5.8 another line posts a new completion', f.requests('completion').length === n + 1);
        f.reply('localCompletion', { items: [] }); f.fire(BUDGET); await flush();
        check('5.8 ...and does not show line 2\'s items', qLine.done && !labels(qLine.value).includes('LocCached'));
        f.clock += 1000; f.reply('completion', { items: [] }); await flush();
        await missOnce(f, 2, '    lo', [item('LocCached', 'lsp')]);
        n = f.requests('completion').length;
        f.model.setLine(2, '      lo');                 // same line, the word now starts two columns later
        ask(f, 2, 9);
        check('5.8 another wordStart on the same line posts a new completion', f.requests('completion').length === n + 1);
        f.reply('localCompletion', { items: [] }); f.fire(BUDGET); await flush();
        f.clock += 1000; f.reply('completion', { items: [] }); await flush();
        await missOnce(f, 2, '    lo', [item('LocCached', 'lsp')]);
        n = f.requests('completion').length;
        f.model = makeModel('$model2', ['  CODE', '    lo']);
        f.model._v = f.model._v;               // version ids restart per model
        ask(f, 2, 7);
        check('5.8 another model posts a new completion', f.requests('completion').length === n + 1);
        allTriggers.push(...e.triggers, ...f.triggers);
    }

    section('5.9: setSource empties the cache (the model id survives a setValue)');
    {
        const e = load();
        await missOnce(e, 2, '    lo', [item('OldProcLocal', 'lsp')]);
        check('setSource calls resetLocalFirstState after the new text is in',
            /model\.setValue\(text\);[\s\S]{0,400}resetLocalFirstState\(\);/.test(html));
        e.api.resetLocalFirstState();
        e.model.setLine(2, '    lo');
        const n = e.requests('completion').length;
        const q = typeAndAsk(e, 2, '    loc');
        check('5.9 after setSource the next query posts a new completion', e.requests('completion').length === n + 1);
        e.reply('localCompletion', { items: [] }); e.fire(BUDGET); await flush();
        check('5.9 ...and the old procedure\'s items do not leak in', q.done && !labels(q.value).includes('OldProcLocal'),
            JSON.stringify(q.value && labels(q.value)));
    }

    section('5.11 - 5.15: adaptive race - skip after 3 misses on a model, per model, a hit resets, skip mode ends');
    {
        const e = load({ lines: ['  CODE', '    aa'] });
        await missOnce(e, 2, '    aa'); await missOnce(e, 2, '    bb'); await missOnce(e, 2, '    cc');
        const armed = e.armed(BUDGET), n = e.requests('completion').length;
        const q4 = typeAndAsk(e, 2, '    dd');
        check('5.11 the 4th query arms NO budget timer', e.armed(BUDGET) === armed, 'armed ' + (e.armed(BUDGET) - armed));
        check('5.11 ...but still posts completion, so the cache fills', e.requests('completion').length === n + 1);
        e.reply('localCompletion', { items: [item('DdLocal', 'local')] });
        await flush();
        check('5.11 ...and resolves with local as soon as local answers', q4.done && labels(q4.value).includes('DdLocal'));
        // 5.15: the late LSP answer fills the cache in skip mode; the next keystroke shows it.
        e.clock += 1000;
        e.reply('completion', { items: [item('DdxLspOnly', 'lsp')] });
        await flush();
        const n2 = e.requests('completion').length;
        const q5 = typeAndAsk(e, 2, '    ddx');
        e.reply('localCompletion', { items: [] });
        await flush();
        check('5.15 the late skip-mode answer is shown on the next keystroke, without a new request',
            q5.done && labels(q5.value).includes('DdxLspOnly') && e.requests('completion').length === n2, JSON.stringify(q5.value && labels(q5.value)));

        // 5.12: another model still races.
        const mM = e.model;
        e.model = makeModel('$modelN', ['  CODE', '    zz']);
        const a2 = e.armed(BUDGET);
        typeAndAsk(e, 2, '    zz');
        check('5.12 3 misses on M; a query on model N still races', e.armed(BUDGET) === a2 + 1);
        e.reply('localCompletion', { items: [] }); e.fire(BUDGET); await flush();
        e.clock += 1000; e.reply('completion', { items: [] }); await flush();

        // 5.14: in skip mode a reply within the budget of its OWN request is a hit -> racing resumes.
        e.model = mM;
        const q6 = typeAndAsk(e, 2, '    ee');     // skip mode: unraced
        const sent = e.clock;
        e.reply('localCompletion', { items: [] });
        await flush();
        check('5.14 (still in skip mode for M)', q6.done);
        e.clock = sent + 50;
        e.reply('completion', { items: [item('EeFast', 'lsp')] });
        await flush();
        const a3 = e.armed(BUDGET);
        typeAndAsk(e, 2, '    ff');
        check('5.14 a fast reply in skip mode is a hit: the next query races again', e.armed(BUDGET) === a3 + 1,
            'armed ' + (e.armed(BUDGET) - a3));
        e.reply('localCompletion', { items: [] }); e.fire(BUDGET); await flush();
        e.clock += 1000; e.reply('completion', { items: [] }); await flush();

        // 5.13: a hit after 2 misses resets the count - 3 more misses are needed.
        const h = load({ lines: ['  CODE', '    aa'] });
        await missOnce(h, 2, '    aa'); await missOnce(h, 2, '    bb');
        const qh = typeAndAsk(h, 2, '    cc');
        h.clock += 20;
        h.reply('completion', { items: [] });        // inside the budget: a hit
        h.reply('localCompletion', { items: [] });
        await flush();
        check('5.13 (the hit query resolved without the budget)', qh.done);
        await missOnce(h, 2, '    dd'); await missOnce(h, 2, '    ee');
        const a4 = h.armed(BUDGET);
        typeAndAsk(h, 2, '    gg');
        check('5.13 a hit after 2 misses resets: 2 more misses do NOT enter skip mode', h.armed(BUDGET) === a4 + 1);
        allTriggers.push(...e.triggers, ...h.triggers);
    }

    section('5.16 / 5.17 / 5.18 / 5.19: local null, suppressed contexts, the local timeout, the colon range');
    {
        const e = load();
        const q = typeAndAsk(e, 2, '    lo');
        e.reply('localCompletion', null);            // timed out / the host has no local layer
        e.fire(BUDGET);
        await flush();
        check('5.16 a null local answer does not return an empty list: still waiting for the LSP', !q.done);
        e.clock += 1000;
        e.reply('completion', { items: [item('LspOnly', 'lsp')] });
        await flush();
        check('5.16 ...the LSP result is shown when it arrives', q.done && labels(q.value).includes('LspOnly'), JSON.stringify(q.value && labels(q.value)));

        const s = load({ lines: ['  CODE', 'lo', "    x = 'lo", '    x = 1 ! lo'] });
        ask(s, 2, 3);                  // column-1 typing
        ask(s, 3, 12);                 // inside a string
        ask(s, 4, 15);                 // inside a comment
        check('5.17 col-1 typing, strings and comments post nothing', s.posted.length === 0, JSON.stringify(s.posted.map(m => m.action)));

        const lc = e.requests('localCompletion')[0];
        check('5.18 localCompletion\'s timeoutMs is at most 500', lc && lc.timeoutMs <= 500, lc && String(lc.timeoutMs));

        const c = load({ lines: ['  CODE', '    LOC:C'] });
        const qc = ask(c, 2, 10, { triggerCharacter: undefined });
        c.reply('localCompletion', { items: [item('LOC:Count', 'local', { insertText: 'LOC:Count' })] });
        c.fire(BUDGET);
        await flush();
        const lcItem = (qc.value && qc.value.suggestions || []).find(x => x.label === 'LOC:Count');
        check('5.19 local LOC:Count after typing LOC:C replaces from the L', lcItem && lcItem.range.startColumn === 5 &&
            lcItem.insertText === 'LOC:Count', lcItem && JSON.stringify(lcItem.range));

        const t = load({ lines: ['  CODE', '    SELF.'] });
        ask(t, 2, 10, { triggerCharacter: '.' });
        const tl = t.requests('localCompletion')[0];
        check('the trigger character is passed to localCompletion', tl && tl.triggerCharacter === '.' && tl.line === 2 && tl.column === 10);
        allTriggers.push(...e.triggers, ...c.triggers);
    }

    section('5.20: after a dot, a typed LSP field label and the bare local field are one row');
    {
        // The server labels a GROUP field "Address STRING(40)" and inserts "Address"; the local layer
        // lists the same field bare. A label-only dedupe showed both.
        const e = load();
        const q = typeAndAsk(e, 2, '    Settings.', { triggerCharacter: '.' });
        e.reply('localCompletion', { items: [item('Address', 'STRING(40)  (field)', { kind: 5 }), item('Port', 'LONG  (field)', { kind: 5 }),
            item('Send', 'PROCEDURE', { kind: 2 })] });
        e.reply('completion', { items: [item('Address STRING(40)', 'STRING(40)', { kind: 5, insertText: 'Address' }),
            item('Port LONG', 'LONG', { kind: 5, insertText: 'Port' }), item('Timeout LONG', 'LONG', { kind: 5, insertText: 'Timeout' }),
            item('Send(STRING pText)', '', { kind: 2, insertText: 'Send' }), item('Send(LONG pCode)', '', { kind: 2, insertText: 'Send' })] });
        await flush();
        const L = labels(q.value);
        check('5.20 each field the local layer knows is listed once, as the local item',
            L.filter(l => /^address\b/i.test(l)).join() === 'Address' && L.filter(l => /^port\b/i.test(l)).join() === 'Port', JSON.stringify(L));
        check('5.20 a field only the LSP knows still appears', L.includes('Timeout LONG'), JSON.stringify(L));
        check('5.20 method overloads sharing an insertText with a local method all survive',
            L.includes('Send(STRING pText)') && L.includes('Send(LONG pCode)'), JSON.stringify(L));

        // Not after a dot: the insertText match stays off (a PRE-qualified item may insert only the remainder).
        const p = load();
        const qp = typeAndAsk(p, 2, '    Cus:');
        p.reply('localCompletion', { items: [item('Cus:Name', 'STRING(20)  (field)', { kind: 5, insertText: 'Name' })] });
        p.reply('completion', { items: [item('Name STRING(20)', 'STRING(20)', { kind: 5, insertText: 'Name' })] });
        await flush();
        check('5.20 without a dot, only a matching label dedupes', labels(qp.value).includes('Name STRING(20)'), JSON.stringify(labels(qp.value)));
        allTriggers.push(...e.triggers, ...p.triggers);
    }

    section('5.10: a showing list is never re-triggered');
    {
        check('5.10 editor.trigger was never called in any scenario above', allTriggers.length === 0, JSON.stringify(allTriggers));
        // 38158e98: the only triggerSuggest in the local-first code is refreshWhenLspLands (5.21).
        const calls = PROVIDERS_SRC.match(/triggerSuggest/g) || [];
        const fn = /function refreshWhenLspLands\([\s\S]*?\n    \}/.exec(PROVIDERS_SRC);
        check('5.10 triggerSuggest appears once, inside refreshWhenLspLands',
            calls.length === 1 && !!fn && /triggerSuggest/.test(fn[0]), 'calls=' + calls.length);
    }

    section('5.21: an EMPTY list is re-opened once when the late LSP lands at the same caret (38158e98)');
    {
        // "glo:" - the local layer has nothing, the LSP misses the race, Monaco shows no list, and the user,
        // seeing none, types nothing: the late answer must re-open suggest (served from the cache).
        const atCaret = (e, line, column) => { e.editor.getPosition = () => ({ lineNumber: line, column }); };

        const a = load({ lines: ['  CODE', '    glo:'] });
        atCaret(a, 2, 9);
        const qa = ask(a, 2, 9, { triggerCharacter: ':' });
        a.reply('localCompletion', { items: [] }); a.fire(BUDGET); await flush();
        check('5.21 the empty local answer is shown at once', qa.done && labels(qa.value).length === 0, JSON.stringify(qa.value && labels(qa.value)));
        check('5.21 ...and nothing is triggered yet', a.triggers.length === 0);
        a.clock += 1000; a.reply('completion', { items: [item('Glo:Svc', 'LONG')] }); await flush();
        check('5.21 the late LSP answer with items re-opens suggest once', a.triggers.length === 1 && a.triggers[0] === 'editor.action.triggerSuggest',
            JSON.stringify(a.triggers));
        const qa2 = ask(a, 2, 9, {});
        a.reply('localCompletion', { items: [] }); await flush();
        check('5.21 ...and the re-query is served the cached items', qa2.done && labels(qa2.value).includes('Glo:Svc'), JSON.stringify(qa2.value && labels(qa2.value)));
        check('5.21 ...without triggering again', a.triggers.length === 1);

        const b = load({ lines: ['  CODE', '    glo:'] });
        atCaret(b, 2, 9);
        ask(b, 2, 9, {});
        b.reply('localCompletion', { items: [] }); b.fire(BUDGET); await flush();
        b.model.setLine(2, '    glo:x'); atCaret(b, 2, 10);          // the user typed on meanwhile
        b.clock += 1000; b.reply('completion', { items: [item('Glo:Svc', 'LONG')] }); await flush();
        check('5.21 no re-open when the user has typed on (the next keystroke merges it anyway)', b.triggers.length === 0);

        const c = load({ lines: ['  CODE', '    glo:'] });
        atCaret(c, 2, 9);
        ask(c, 2, 9, {});
        c.reply('localCompletion', { items: [] }); c.fire(BUDGET); await flush();
        c.clock += 1000; c.reply('completion', { items: [] }); await flush();
        check('5.21 no re-open when the late answer is empty too', c.triggers.length === 0);

        const d = load({ lines: ['  CODE', '    glo:'] });
        atCaret(d, 2, 9);
        ask(d, 2, 9, {});
        d.reply('localCompletion', { items: [item('Glo:Local', 'local')] }); d.fire(BUDGET); await flush();
        d.clock += 1000; d.reply('completion', { items: [item('Glo:Svc', 'LONG')] }); await flush();
        check('5.21 no re-open when a list was showing (never re-trigger a showing list)', d.triggers.length === 0);

        // Pipeline run 2: a ghost list long after the user stopped, a hidden editor, and two askers.
        const g = load({ lines: ['  CODE', '    glo:'] });
        atCaret(g, 2, 9);
        ask(g, 2, 9, {});
        g.reply('localCompletion', { items: [] }); g.fire(BUDGET); await flush();
        g.clock += 4000; g.reply('completion', { items: [item('Glo:Svc', 'LONG')] }); await flush();
        check('5.21 no re-open for an answer that lands after 3 s (huge module: no ghost list)', g.triggers.length === 0);

        const h = load({ lines: ['  CODE', '    glo:'] });
        atCaret(h, 2, 9); h.editor.hasTextFocus = () => false;
        ask(h, 2, 9, {});
        h.reply('localCompletion', { items: [] }); h.fire(BUDGET); await flush();
        h.clock += 1000; h.reply('completion', { items: [item('Glo:Svc', 'LONG')] }); await flush();
        check('5.21 no re-open when the editor lost focus', h.triggers.length === 0);

        const k = load({ lines: ['  CODE', '    glo:'] });
        atCaret(k, 2, 9);
        ask(k, 2, 9, { triggerCharacter: ':' });          // Monaco's own trigger ...
        ask(k, 2, 9, {});                                  // ... and the colon handler's explicit one
        k.reply('localCompletion', { items: [] }); k.reply('localCompletion', { items: [] }); k.fire(BUDGET); await flush();
        k.clock += 1000; k.reply('completion', { items: [item('Glo:Svc', 'LONG')] }); await flush();
        check('5.21 two askers on one request re-open only once', k.triggers.length === 1, JSON.stringify(k.triggers));
    }

    finish();
}

main().catch(err => { console.error(err && err.stack || err); process.exit(1); });
