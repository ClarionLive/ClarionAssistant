// hover-attribute-chips.test.js - GH #263: attribute edit links (chips) on the control hover card.
//
// Run:  node Terminal/test/hover-attribute-chips.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL hover providers, link opener and attribute-link block extracted from
// monaco-embeditor.html (local-first-loader.js) on a stand-in model and editor. The language server's
// card carries links file:///clarion-attr/edit/<base64url {e:[{l,c,el,ec,t,o}]}>. Pinned:
//   * a card shown where the line is editable keeps its links; a click applies the edit as ONE executeEdits
//     between two undo stops, then re-shows the card (Monaco's private _hideWidgets + showContentHover)
//   * a stale card (the line changed since) applies nothing, and the card is re-shown
//   * a malformed payload (bad base64, missing/short `o`, non-integer or out-of-line columns, overlapping
//     edits, a payload the card did not carry) changes nothing and throws nothing
//   * read-only, outside the embed points, or a GENERATED module opened as a plain file: plain names, no links
//   * no /clarion-attr/ link ever reaches openLocation (the host's navigation / open-file path)
//   * without _hideWidgets (a Monaco upgrade, ticket af046356) it falls back to the public showHover action

const { load, flush, check, section, finish } = require('./local-first-loader');

const DECL = '  ?L  LIST,AT(1,1,50,50),USE(?L),VSCROLL,READONLY  ! view only';
const LINES = ['Window WINDOW', DECL, '  END'];
const POS = { lineNumber: 2, column: 8 };   // on LIST
const b64url = (obj) => Buffer.from(JSON.stringify(obj), 'utf8').toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const at = (s, sub) => s.indexOf(sub);
const ro = DECL.indexOf(',READONLY') + ',READONLY'.length;
// Add HIDE after READONLY; remove READONLY; HVSCROLL for VSCROLL. Lines are relative to the hovered line.
const ADD_HIDE = b64url({ e: [{ l: 0, c: ro, el: 0, ec: ro, t: ',HIDE', o: [DECL] }] });
const DEL_RO = b64url({ e: [{ l: 0, c: at(DECL, ',READONLY'), el: 0, ec: ro, t: '', o: [DECL] }] });
const TWO = b64url({ e: [
    { l: 0, c: at(DECL, 'VSCROLL'), el: 0, ec: at(DECL, 'VSCROLL') + 7, t: 'HVSCROLL', o: [DECL] },
    { l: 0, c: ro, el: 0, ec: ro, t: ',HIDE', o: [DECL] }] });
const link = (name, payload, verb) => '[' + name + '](file:///clarion-attr/edit/' + payload + ' "' + verb + ' ' + name + ' — test")';
const cardWith = (payloads) => '```clarion\nLIST\n```\n\n**Attributes:** `AT` `USE` ' + link('READONLY', DEL_RO, 'Remove') +
    '\n**Available:** ' + link('HIDE', ADD_HIDE, 'Add') + ' ' + link('HVSCROLL', TWO, 'Add') +
    (payloads || []).map((p, i) => ' ' + link('X' + i, p, 'Add')).join('');

const uriOf = (s) => {
    const m = /^file:\/\/([^/]*)(\/[^#]*)(?:#(.*))?$/.exec(s);
    return { scheme: 'file', authority: m[1], path: decodeURIComponent(m[2]), fragment: m[3] || '', fsPath: m[2].replace(/\//g, '\\') };
};

// Wire a stand-in editor onto the loaded env: executeEdits really edits the model; undo stops, focus,
// cursor moves and the content-hover controller calls are recorded.
function rig(e, opts) {
    opts = opts || {};
    const log = { edits: [], stops: 0, hide: 0, show: [], setPos: [], focus: 0 };
    e.model.isDisposed = () => false;
    const ctrl = { showContentHover(r, mode, src, focus) { log.show.push([r.startLineNumber, r.startColumn, mode, src, focus]); } };
    if (!opts.noHideWidgets) ctrl._hideWidgets = () => { log.hide++; };
    Object.assign(e.editor, {
        executeEdits(source, ops) {
            log.edits.push({ source, ops: ops.map(o => ({ r: [o.range.startLineNumber, o.range.startColumn, o.range.endLineNumber, o.range.endColumn], t: o.text })) });
            // Apply from the last op back, on the joined text.
            const text = e.model._lines.join('\n');
            const off = (ln, col) => e.model._lines.slice(0, ln - 1).reduce((n, s) => n + s.length + 1, 0) + col - 1;
            let out = text;
            ops.slice().sort((a, b) => off(b.range.startLineNumber, b.range.startColumn) - off(a.range.startLineNumber, a.range.startColumn))
                .forEach(o => { out = out.slice(0, off(o.range.startLineNumber, o.range.startColumn)) + o.text + out.slice(off(o.range.endLineNumber, o.range.endColumn)); });
            e.model._lines = out.split('\n'); e.model._v++;
            return true;
        },
        pushUndoStop() { log.stops++; return true; },
        focus() { log.focus++; },
        setPosition(p) { log.setPos.push(p); },
        getContribution(id) { return id === 'editor.contrib.contentHover' ? ctrl : null; },
    });
    return log;
}

// Show the server's card through the REAL LSP hover provider; returns the markdown Monaco would render.
async function showCard(e, md) {
    const lsp = e.providers.hover[1];
    let out;
    lsp.provideHover(e.model, POS).then(v => { out = v; });
    e.reply('localHover', { contents: null });
    await flush();
    e.reply('hover', { contents: md });
    await flush();
    return out && out.contents && out.contents[0] && out.contents[0].value;
}
const click = (e, href) => e.linkOpeners[0].open(uriOf(href));
const hrefOf = (payload) => 'file:///clarion-attr/edit/' + payload;
const opened = (e) => e.requests('openLocation').length;

async function main() {
    section('apply: a matching card applies the edit as one undo step and re-shows the card');
    {
        const e = load({ lines: LINES }); const log = rig(e);
        check('one link opener is registered', e.linkOpeners.length === 1, 'got ' + e.linkOpeners.length);
        const md = await showCard(e, cardWith());
        check('the editable card keeps its links', md && md.indexOf('file:///clarion-attr/edit/' + ADD_HIDE) >= 0, md);
        const handled = click(e, hrefOf(ADD_HIDE));
        check('the opener reports the link handled', handled === true);
        check('HIDE is added after READONLY, the comment kept', e.model.getLineContent(2) === DECL.slice(0, ro) + ',HIDE' + DECL.slice(ro), e.model.getLineContent(2));
        check('...in ONE executeEdits from clarion-attr', log.edits.length === 1 && log.edits[0].source === 'clarion-attr' && log.edits[0].ops.length === 1);
        check('...between two undo stops (an undo step of its own)', log.stops === 2, 'stops ' + log.stops);
        check('the card is re-shown on the hovered keyword (hide, then showContentHover)', log.hide === 1 && log.show.length === 1 &&
            log.show[0][0] === 2 && log.show[0][1] === 8 && log.setPos.length === 0, JSON.stringify(log.show));
        check('nothing reached openLocation', opened(e) === 0);
    }

    section('apply: two edits in one payload land as one executeEdits');
    {
        const e = load({ lines: LINES }); const log = rig(e);
        await showCard(e, cardWith());
        click(e, hrefOf(TWO));
        const want = DECL.replace('VSCROLL,READONLY', 'HVSCROLL,READONLY,HIDE');
        check('VSCROLL -> HVSCROLL and HIDE added', e.model.getLineContent(2) === want, e.model.getLineContent(2));
        check('...one executeEdits with both ops', log.edits.length === 1 && log.edits[0].ops.length === 2);
    }

    section('stale: the line changed between hover and click -> nothing applied, card re-shown');
    {
        const e = load({ lines: LINES }); const log = rig(e);
        await showCard(e, cardWith());
        e.model.setLine(2, DECL.replace('READONLY', 'READONLY,BOXED'));
        const before = e.model.getLineContent(2);
        click(e, hrefOf(ADD_HIDE));
        check('no executeEdits', log.edits.length === 0);
        check('the line is untouched', e.model.getLineContent(2) === before);
        check('the card is re-shown, freshly computed', log.show.length === 1);
        check('a second click on the same stale card is still a no-op', (click(e, hrefOf(DEL_RO)), log.edits.length === 0));
    }

    section('malformed payloads: no edit, no throw, no navigation');
    {
        const bad = {
            'not base64 JSON': 'bm90IGpzb24',
            'garbage characters': 'Zm9v!!',
            'no edit list': b64url({ x: 1 }),
            'empty edit list': b64url({ e: [] }),
            'o missing': b64url({ e: [{ l: 0, c: ro, el: 0, ec: ro, t: ',HIDE' }] }),
            'o shorter than l..el (an unchecked line)': b64url({ e: [{ l: 0, c: 0, el: 1, ec: 2, t: 'X', o: [DECL] }] }),
            'non-integer column': b64url({ e: [{ l: 0, c: 1.5, el: 0, ec: 3, t: 'X', o: [DECL] }] }),
            'string column': b64url({ e: [{ l: 0, c: '3', el: 0, ec: 3, t: 'X', o: [DECL] }] }),
            'column beyond the line': b64url({ e: [{ l: 0, c: DECL.length + 5, el: 0, ec: DECL.length + 5, t: 'X', o: [DECL] }] }),
            'end before start': b64url({ e: [{ l: 0, c: 9, el: 0, ec: 3, t: 'X', o: [DECL] }] }),
            'text not a string': b64url({ e: [{ l: 0, c: ro, el: 0, ec: ro, t: 5, o: [DECL] }] }),
            'line above the buffer': b64url({ e: [{ l: -5, c: 0, el: -5, ec: 0, t: 'X', o: [''] }] }),
            'overlapping edits': b64url({ e: [{ l: 0, c: 2, el: 0, ec: 10, t: 'A', o: [DECL] }, { l: 0, c: 5, el: 0, ec: 12, t: 'B', o: [DECL] }] }),
            'too many edits': b64url({ e: Array.from({ length: 40 }, () => ({ l: 0, c: ro, el: 0, ec: ro, t: '', o: [DECL] })) }),
        };
        const e = load({ lines: LINES }); const log = rig(e);
        await showCard(e, cardWith(Object.values(bad)));
        for (const [what, p] of Object.entries(bad)) {
            let threw = null;
            try { click(e, hrefOf(p)); } catch (x) { threw = x; }
            check(what + ': no edit, no throw', !threw && log.edits.length === 0 && e.model.getLineContent(2) === DECL, threw && threw.message);
        }
        const notOnCard = b64url({ e: [{ l: 0, c: ro, el: 0, ec: ro, t: ',HIDE', o: [DECL] }, { l: 0, c: 0, el: 0, ec: 0, t: '', o: [DECL] }] });
        const shownBefore = log.show.length;
        click(e, hrefOf(notOnCard));
        check('a well-formed payload the card did NOT carry (e.g. ctrl+click on buffer text) does nothing',
            log.edits.length === 0 && log.show.length === shownBefore && e.model.getLineContent(2) === DECL);
        check('...and nothing reached openLocation', opened(e) === 0);
        // Before any card at all.
        const f = load({ lines: LINES }); const flog = rig(f);
        let threw = null;
        try { click(f, hrefOf(ADD_HIDE)); } catch (x) { threw = x; }
        check('a click with no card shown is a no-op', !threw && flog.edits.length === 0 && opened(f) === 0);
    }

    section('plain names where the line cannot be edited');
    {
        const cases = [
            ['read-only page', e => { e.isReadOnlyNow = () => true; }],
            ['outside the embed points', e => { e.embedRanges = [[10, 20]]; }],
            ['a GENERATED module opened as a plain file', e => { e.generatedSource = true; }],
        ];
        for (const [what, setup] of cases) {
            const e = load({ lines: LINES }); const log = rig(e); setup(e);
            const md = await showCard(e, cardWith());
            check(what + ': no clarion-attr link is left', md && md.indexOf('clarion-attr') < 0, md);
            check(what + ': names shown as code', md && /`READONLY`/.test(md) && /`HIDE`/.test(md) && /`HVSCROLL`/.test(md));
            click(e, hrefOf(ADD_HIDE));
            check(what + ': a click on a stale live link does nothing', log.edits.length === 0 && e.model.getLineContent(2) === DECL);
        }
        // Link forms the server might vary: no title, lowercase name.
        const e = load({ lines: LINES }); rig(e); e.generatedSource = true;
        const md = await showCard(e, '**Available:** [hide](file:///clarion-attr/edit/' + ADD_HIDE + ') [B](file:///clarion-attr/other)');
        check('untitled / lowercase / non-edit links are flattened too', md === '**Available:** `hide` `B`', md);
    }

    section('the fallback card path (GH #250) prepares links the same way');
    {
        const e = load({ lines: LINES }); rig(e); e.generatedSource = true;
        const lsp = e.providers.hover[1];
        let out;
        lsp.provideHover(e.model, POS).then(v => { out = v; });
        e.reply('localHover', { contents: '```clarion\nLIST\n```\n\nkeyword', fallback: true });
        await flush();
        e.reply('hover', { contents: cardWith() });
        await flush();
        const md = out && out.contents && out.contents[0] && out.contents[0].value;
        check('fallback path: plain names in a generated module', md && md.indexOf('clarion-attr') < 0 && /`HIDE`/.test(md), JSON.stringify(out));
    }

    section('navigation: no /clarion-attr/ link reaches openLocation; ordinary file links still do');
    {
        const e = load({ lines: LINES }); rig(e);
        for (const href of ['file:///clarion-attr/other/x', 'file:///CLARION-ATTR/edit/' + ADD_HIDE, 'file://somehost/clarion-attr/edit/zz', 'file:///clarion-attr/']) {
            const r = click(e, href);
            check('swallowed: ' + href.slice(0, 40), r === true && opened(e) === 0);
        }
        click(e, 'file:///C:/src/Foo.inc#L12');
        const nav = e.requests('openLocation');
        check('control: a real file link still navigates', nav.length === 1 && nav[0].line === 12, JSON.stringify(nav));
    }

    section('the host re-sends generatedSource after the tab loaded (an app opened or closed later)');
    {
        const { html } = require('./local-first-loader');
        check('the page routes a {type:"generatedSource"} message to applyGeneratedSourceFlag',
            /msg\.type === 'generatedSource'[\s\S]{0,400}applyGeneratedSourceFlag\(msg\.value\)/.test(html));
        check('...and a setSource still pending takes the newer answer with it',
            /msg\.type === 'generatedSource'[\s\S]{0,400}pending\.generatedSource = msg\.value === true/.test(html));

        const e = load({ lines: LINES }); const log = rig(e); e.fileMode = true; e.embedRanges = [[1, 3]];
        let md = await showCard(e, cardWith());
        check('tab opened before its app: chips are live', md && md.indexOf('file:///clarion-attr/edit/') >= 0);
        e.api.applyGeneratedSourceFlag(true);                    // the app opened: this file is one of its modules
        check('the flag flips on', e.generatedSource === true);
        click(e, hrefOf(ADD_HIDE));
        check('a chip still on screen from before the flip does nothing', log.edits.length === 0 && e.model.getLineContent(2) === DECL);
        e.model._v++;   // a new version: the shared local hover is asked again
        md = await showCard(e, cardWith());
        check('the next card shows plain names', md && md.indexOf('clarion-attr') < 0 && /`HIDE`/.test(md), md);
        e.api.applyGeneratedSourceFlag(false);                   // the app closed again
        e.model._v++;   // a new version: the shared local hover is asked again
        md = await showCard(e, cardWith());
        check('flag off again: chips are back', e.generatedSource === false && md && md.indexOf('file:///clarion-attr/edit/') >= 0);
        e.fileMode = false;
        e.api.applyGeneratedSourceFlag(true);
        check('outside file mode the flag stays off (the CA Embeditor guards its module by its slots)', e.generatedSource === false);
    }

    section('Monaco upgrade guard: without _hideWidgets, fall back to the public showHover action');
    {
        const e = load({ lines: LINES }); const log = rig(e, { noHideWidgets: true });
        await showCard(e, cardWith());
        click(e, hrefOf(ADD_HIDE));
        check('the edit still applies', log.edits.length === 1);
        check('showContentHover is NOT called alone (it would leave the stale card up)', log.show.length === 0);
        check('the cursor goes to the keyword and editor.action.showHover runs', log.setPos.length === 1 &&
            log.setPos[0].lineNumber === 2 && e.triggers.indexOf('editor.action.showHover') >= 0, JSON.stringify(e.triggers));
    }

    finish();
}

main().catch(err => { console.log('HARNESS ERROR: ' + (err && err.stack || err)); process.exit(1); });
