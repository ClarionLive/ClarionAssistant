// hover-attribute-chips.test.js - GH #263 / #281: attribute edit links (chips) on the hover card.
//
// Run:  node Terminal/test/hover-attribute-chips.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL hover providers, link opener and attribute-link block extracted from
// monaco-embeditor.html (local-first-loader.js) on a stand-in model and editor. From language server
// v1.0.9 (GH #281, msarson/Clarion-Extension#731) a card carries
//   [NAME](command:clarion.applyAttributeEdit?<encodeURIComponent(JSON.stringify([payload]))> "title")
//   payload = { e: [{ l, c, el, ec, t, o, s? }] }
// on controls, WINDOW / APPLICATION / REPORT headers (#745) and FILE / KEY / INDEX / MEMO / BLOB
// declarations (#746); `s: true` marks `t` as a snippet (#743). Pinned:
//   * parse: the query decodes as JSON.parse(decodeURIComponent(query))[0]; anything else is no payload
//   * a card shown where the line is editable keeps its links and is handed to Monaco as trusted for the
//     edit command ONLY (Monaco drops untrusted command: links); a click applies the edit as ONE
//     executeEdits between two undo stops, then re-shows the card
//   * the guard: a stale card (the line changed since) applies nothing, and the card is re-shown
//   * a malformed payload changes nothing and throws nothing
//   * a snippet edit goes through Monaco's snippet controller (tab stop); without one, or when it does not
//     take, the plain text with the placeholder defaults (TIP('text')) as one undo step
//   * the REAL v1.0.9 cards (fixtures/lsp-109-attribute-cards.json): control, WINDOW header, FILE, KEY
//   * read-only, outside the embed points, or a GENERATED module opened as a plain file: plain names, no
//     links, not trusted
//   * no command: link ever runs as a command or reaches openLocation; other command: links are flattened;
//     the retired file:///clarion-attr/ draft form is swallowed, never applied
//   * without _hideWidgets (a Monaco upgrade, ticket af046356) it falls back to the public showHover action

const path = require('path');
const { load, flush, check, section, finish } = require('./local-first-loader');
const FX = require(path.join(__dirname, 'fixtures', 'lsp-109-attribute-cards.json'));

const CMD = 'clarion.applyAttributeEdit';
const DECL = '  ?L  LIST,AT(1,1,50,50),USE(?L),VSCROLL,READONLY  ! view only';
const LINES = ['Window WINDOW', DECL, '  END'];
const POS = { lineNumber: 2, column: 8 };   // on LIST
// The server's encoding: encodeURIComponent of a one-element array, with ( and ) escaped too.
const q = (payload) => encodeURIComponent(JSON.stringify([payload])).replace(/\(/g, '%28').replace(/\)/g, '%29');
const at = (s, sub) => s.indexOf(sub);
const ro = DECL.indexOf(',READONLY') + ',READONLY'.length;
// Add HIDE after READONLY; remove READONLY; HVSCROLL for VSCROLL. Lines are relative to the hovered line.
const ADD_HIDE = { e: [{ l: 0, c: ro, el: 0, ec: ro, t: ',HIDE', o: [DECL] }] };
const DEL_RO = { e: [{ l: 0, c: at(DECL, ',READONLY'), el: 0, ec: ro, t: '', o: [DECL] }] };
const TWO = { e: [
    { l: 0, c: at(DECL, 'VSCROLL'), el: 0, ec: at(DECL, 'VSCROLL') + 7, t: 'HVSCROLL', o: [DECL] },
    { l: 0, c: ro, el: 0, ec: ro, t: ',HIDE', o: [DECL] }] };
const TIP = { e: [{ l: 0, c: ro, el: 0, ec: ro, t: ",TIP('${1:text}')", o: [DECL], s: true }] };
const hrefOf = (payload) => 'command:' + CMD + '?' + q(payload);
const link = (name, payload, verb) => '[' + name + '](' + hrefOf(payload) + ' "' + verb + ' ' + name + ' — test")';
const cardWith = (payloads) => '```clarion\nLIST\n```\n\n**Attributes:** `AT` `USE` ' + link('READONLY', DEL_RO, 'Remove') +
    '\n**Available:** ' + link('HIDE', ADD_HIDE, 'Add') + ' ' + link('HVSCROLL', TWO, 'Add') +
    '\n**With value:** ' + link('TIP', TIP, 'Add') +
    (payloads || []).map((p, i) => ' ' + link('X' + i, p, 'Add')).join('');
const LIVE = '(command:' + CMD + '?';

// The URI Monaco's opener hands a registered link opener: URI.parse, which percent-decodes the query.
const uriOf = (s) => {
    if (/^command:/i.test(s)) {
        const m = /^command:([^?#]*)(?:\?([^#]*))?/i.exec(s);
        return { scheme: 'command', authority: '', path: decodeURIComponent(m[1]), query: m[2] ? decodeURIComponent(m[2]) : '', fragment: '' };
    }
    const m = /^file:\/\/([^/]*)(\/[^#]*)(?:#(.*))?$/.exec(s);
    return { scheme: 'file', authority: m[1], path: decodeURIComponent(m[2]), fragment: m[3] || '', fsPath: m[2].replace(/\//g, '\\') };
};
// Every edit-link href in a card, by name.
function linksOf(md) {
    const out = [];
    const rx = /\[([^\]]+)\]\((command:clarion\.applyAttributeEdit\?[^\s"]+)(?: "((?:[^"\\]|\\.)*)")?\)/g;
    let m;
    while ((m = rx.exec(md)) !== null) out.push({ name: m[1], href: m[2], title: m[3] || '' });
    return out;
}
const api = (e, name) => (typeof e.api[name] === 'function' ? e.api[name] : () => undefined);

// Wire a stand-in editor onto the loaded env: executeEdits really edits the model; undo stops, focus,
// cursor moves, selections and the content-hover controller calls are recorded. opts.snippet: a fake
// snippetController2 ('apply' inserts the plain text and bumps the version like Monaco's; 'noop' takes it
// and changes nothing, as Monaco's does when its insert fails inside its own try/catch).
function rig(e, opts) {
    opts = opts || {};
    const log = { edits: [], stops: 0, hide: 0, show: [], setPos: [], focus: 0, sel: [], snippets: [] };
    e.model.isDisposed = () => false;
    const ctrl = { showContentHover(r, mode, src, focus) { log.show.push([r.startLineNumber, r.startColumn, mode, src, focus]); } };
    if (!opts.noHideWidgets) ctrl._hideWidgets = () => { log.hide++; };
    const off = (ln, col) => e.model._lines.slice(0, ln - 1).reduce((n, s) => n + s.length + 1, 0) + col - 1;
    const apply = (ops) => {
        let out = e.model._lines.join('\n');
        ops.slice().sort((a, b) => off(b.range.startLineNumber, b.range.startColumn) - off(a.range.startLineNumber, a.range.startColumn))
            .forEach(o => { out = out.slice(0, off(o.range.startLineNumber, o.range.startColumn)) + o.text + out.slice(off(o.range.endLineNumber, o.range.endColumn)); });
        e.model._lines = out.split('\n'); e.model._v++;
    };
    let selection = null;
    const snippetCtrl = opts.snippet ? {
        insert(template, o) {
            log.snippets.push({ template, opts: o, sel: selection && [selection.startLineNumber, selection.startColumn, selection.endLineNumber, selection.endColumn], focused: log.focus });
            if (opts.snippet === 'apply') apply([{ range: selection, text: template.replace(/\$\{\d+:([^}]*)\}/g, '$1') }]);
        },
    } : null;
    Object.assign(e.editor, {
        executeEdits(source, ops) {
            log.edits.push({ source, ops: ops.map(o => ({ r: [o.range.startLineNumber, o.range.startColumn, o.range.endLineNumber, o.range.endColumn], t: o.text })) });
            apply(ops);
            return true;
        },
        pushUndoStop() { log.stops++; return true; },
        focus() { log.focus++; },
        setPosition(p) { log.setPos.push(p); },
        setSelection(r) { selection = r; log.sel.push([r.startLineNumber, r.startColumn, r.endLineNumber, r.endColumn]); },
        getContribution(id) {
            if (id === 'editor.contrib.contentHover') return ctrl;
            if (id === 'snippetController2') return snippetCtrl;
            return null;
        },
    });
    return log;
}

// Show the server's card through the REAL LSP hover provider; returns the markdown Monaco would render
// (and keeps the whole content object, isTrusted included, on e.lastContent).
async function showCard(e, md, pos) {
    const lsp = e.providers.hover[1];
    let out;
    lsp.provideHover(e.model, pos || POS).then(v => { out = v; });
    e.reply('localHover', { contents: null });
    await flush();
    e.reply('hover', { contents: md });
    await flush();
    e.lastContent = out && out.contents && out.contents[0];
    return e.lastContent && e.lastContent.value;
}
const trustedForEditOnly = (c) => !!c && !!c.isTrusted && typeof c.isTrusted === 'object' &&
    Array.isArray(c.isTrusted.enabledCommands) && c.isTrusted.enabledCommands.length === 1 && c.isTrusted.enabledCommands[0] === CMD;
const click = (e, href) => e.linkOpeners[0].open(uriOf(href));
const opened = (e) => e.requests('openLocation').length;
// A refusal proves something only when the link WAS readable: the page decodes the payload from the URI.
const decodable = (e, href) => api(e, 'attributeEditPayloadOfUri')(uriOf(href)) != null;

async function main() {
    section('parse: the query is JSON.parse(decodeURIComponent(query))[0]');
    {
        const e = load({ lines: LINES });
        const dq = api(e, 'decodeAttributeEditQuery');
        check('the server encoding (parentheses escaped) decodes to the payload', JSON.stringify(dq(q(TIP))) === JSON.stringify(TIP), JSON.stringify(dq(q(TIP))));
        check('raw parentheses in the query decode too', JSON.stringify(dq(encodeURIComponent(JSON.stringify([ADD_HIDE])))) === JSON.stringify(ADD_HIDE));
        const bad = { 'not JSON': 'abc', 'not an array': encodeURIComponent(JSON.stringify(ADD_HIDE)),
            'an array of two': encodeURIComponent(JSON.stringify([ADD_HIDE, ADD_HIDE])), 'broken percent-encoding': '%E0%A4%A',
            'an array of a string': encodeURIComponent('["x"]'), 'empty': '' };
        for (const [what, s] of Object.entries(bad)) check(what + ': no payload', dq(s) === null, JSON.stringify(dq(s)));
        const pu = api(e, 'attributeEditPayloadOfUri');
        check('the opener reads the payload from the URI Monaco hands it (query already decoded)',
            JSON.stringify(pu(uriOf(hrefOf(ADD_HIDE)))) === JSON.stringify(ADD_HIDE));
        check('...but only for clarion.applyAttributeEdit', pu(uriOf('command:other.command?' + q(ADD_HIDE))) === null);
    }

    section('apply: a matching card applies the edit as one undo step and re-shows the card');
    {
        const e = load({ lines: LINES }); const log = rig(e);
        const md = await showCard(e, cardWith());
        check('the editable card keeps its command: links, trusted for clarion.applyAttributeEdit only (Monaco drops untrusted command: links)',
            md && md.indexOf(hrefOf(ADD_HIDE)) >= 0 && trustedForEditOnly(e.lastContent), JSON.stringify(e.lastContent && e.lastContent.isTrusted));
        const handled = click(e, hrefOf(ADD_HIDE));
        check('the one link opener reports the link handled', e.linkOpeners.length === 1 && handled === true, 'openers ' + e.linkOpeners.length);
        check('HIDE is added after READONLY, the comment kept', e.model.getLineContent(2) === DECL.slice(0, ro) + ',HIDE' + DECL.slice(ro), e.model.getLineContent(2));
        check('...in ONE executeEdits from clarion-attr', log.edits.length === 1 && log.edits[0].source === 'clarion-attr' && log.edits[0].ops.length === 1);
        check('...between two undo stops (an undo step of its own)', log.stops === 2, 'stops ' + log.stops);
        check('the card is re-shown on the hovered keyword (hide, then showContentHover)', log.hide === 1 && log.show.length === 1 &&
            log.show[0][0] === 2 && log.show[0][1] === 8 && log.setPos.length === 0, JSON.stringify(log.show));
        check('nothing reached openLocation (and the edit was made)', opened(e) === 0 && log.edits.length === 1);
    }

    section('apply: a Remove link, and two edits in one payload as one executeEdits');
    {
        const e = load({ lines: LINES }); const log = rig(e);
        await showCard(e, cardWith());
        click(e, hrefOf(DEL_RO));
        check('READONLY is removed', e.model.getLineContent(2) === DECL.replace(',READONLY', ''), e.model.getLineContent(2));
        const f = load({ lines: LINES }); const flog = rig(f);
        await showCard(f, cardWith());
        click(f, hrefOf(TWO));
        const want = DECL.replace('VSCROLL,READONLY', 'HVSCROLL,READONLY,HIDE');
        check('VSCROLL -> HVSCROLL and HIDE added', f.model.getLineContent(2) === want, f.model.getLineContent(2));
        check('...one executeEdits with both ops', flog.edits.length === 1 && flog.edits[0].ops.length === 2);
        check('(the first rig made one edit)', log.edits.length === 1);
    }

    section('guard: the line changed between hover and click -> nothing applied, card re-shown');
    {
        const e = load({ lines: LINES }); const log = rig(e);
        const md = await showCard(e, cardWith());
        e.model.setLine(2, DECL.replace('READONLY', 'READONLY,BOXED'));
        const before = e.model.getLineContent(2);
        click(e, hrefOf(ADD_HIDE));
        check('no executeEdits for a readable link on a live card', log.edits.length === 0 && md && md.indexOf(LIVE) >= 0 && decodable(e, hrefOf(ADD_HIDE)));
        check('the line is untouched, and the card is shown again', e.model.getLineContent(2) === before && log.show.length === 1);
        check('the card is re-shown, freshly computed', log.show.length === 1);
        click(e, hrefOf(TIP));
        check('a stale SNIPPET link is refused too (no snippet, no edit)', log.edits.length === 0 && e.model.getLineContent(2) === before && log.show.length === 2);
    }

    section('malformed payloads: no edit, no throw, no navigation');
    {
        const bad = {
            'no edit list': { x: 1 },
            'empty edit list': { e: [] },
            'o missing': { e: [{ l: 0, c: ro, el: 0, ec: ro, t: ',HIDE' }] },
            'o shorter than l..el (an unchecked line)': { e: [{ l: 0, c: 0, el: 1, ec: 2, t: 'X', o: [DECL] }] },
            'non-integer column': { e: [{ l: 0, c: 1.5, el: 0, ec: 3, t: 'X', o: [DECL] }] },
            'string column': { e: [{ l: 0, c: '3', el: 0, ec: 3, t: 'X', o: [DECL] }] },
            'column beyond the line': { e: [{ l: 0, c: DECL.length + 5, el: 0, ec: DECL.length + 5, t: 'X', o: [DECL] }] },
            'end before start': { e: [{ l: 0, c: 9, el: 0, ec: 3, t: 'X', o: [DECL] }] },
            'text not a string': { e: [{ l: 0, c: ro, el: 0, ec: ro, t: 5, o: [DECL] }] },
            's not a boolean': { e: [{ l: 0, c: ro, el: 0, ec: ro, t: ',HIDE', o: [DECL], s: 'yes' }] },
            'a snippet edit with another edit': { e: [TIP.e[0], { l: 0, c: 0, el: 0, ec: 0, t: '', o: [DECL] }] },
            'line above the buffer': { e: [{ l: -5, c: 0, el: -5, ec: 0, t: 'X', o: [''] }] },
            'overlapping edits': { e: [{ l: 0, c: 2, el: 0, ec: 10, t: 'A', o: [DECL] }, { l: 0, c: 5, el: 0, ec: 12, t: 'B', o: [DECL] }] },
            'too many edits': { e: Array.from({ length: 40 }, () => ({ l: 0, c: ro, el: 0, ec: ro, t: '', o: [DECL] })) },
        };
        const e = load({ lines: LINES }); const log = rig(e, { snippet: 'apply' });
        const md = await showCard(e, cardWith(Object.values(bad)));
        check('(control) the card carrying them is live', md && md.indexOf(LIVE) >= 0 && trustedForEditOnly(e.lastContent));
        for (const [what, p] of Object.entries(bad)) {
            let threw = null;
            try { click(e, hrefOf(p)); } catch (x) { threw = x; }
            check(what + ': readable, refused: no edit, no throw', decodable(e, hrefOf(p)) && !threw && log.edits.length === 0 && log.snippets.length === 0 && e.model.getLineContent(2) === DECL, threw && threw.message);
        }
        const notOnCard = { e: [{ l: 0, c: ro, el: 0, ec: ro, t: ',HIDE', o: [DECL] }, { l: 0, c: 0, el: 0, ec: 0, t: '', o: [DECL] }] };
        const shownBefore = log.show.length;
        click(e, hrefOf(notOnCard));
        check('a well-formed payload the card did NOT carry does nothing',
            decodable(e, hrefOf(notOnCard)) && log.edits.length === 0 && log.show.length === shownBefore && e.model.getLineContent(2) === DECL);
        click(e, hrefOf(ADD_HIDE));
        check('(control) a payload the card DID carry still applies after all that', log.edits.length === 1, e.model.getLineContent(2));
        check('...and nothing reached openLocation', opened(e) === 0 && log.edits.length === 1);
        // Before any card at all.
        const f = load({ lines: LINES }); const flog = rig(f);
        let threw = null;
        try { click(f, hrefOf(ADD_HIDE)); } catch (x) { threw = x; }
        check('a click with no card shown is a no-op', decodable(f, hrefOf(ADD_HIDE)) && !threw && flog.edits.length === 0 && opened(f) === 0);
    }

    section('snippet (s: true): through the snippet controller, so the tab stop works');
    {
        const e = load({ lines: LINES }); const log = rig(e, { snippet: 'apply' });
        await showCard(e, cardWith());
        click(e, hrefOf(TIP));
        const sn = log.snippets[0];
        check('the snippet controller gets the template as is', log.snippets.length === 1 && sn.template === ",TIP('${1:text}')", JSON.stringify(log.snippets));
        check('...at the edit range (selection set to it first)', sn && JSON.stringify(sn.sel) === JSON.stringify([2, ro + 1, 2, ro + 1]), sn && JSON.stringify(sn.sel));
        check('...with the editor focused, as one undo step (undoStopBefore and undoStopAfter)', sn && sn.focused >= 1 &&
            sn.opts && sn.opts.undoStopBefore === true && sn.opts.undoStopAfter === true && sn.opts.overwriteBefore === 0 && sn.opts.overwriteAfter === 0, sn && JSON.stringify(sn.opts));
        check('the line reads TIP(\'text\') after READONLY', e.model.getLineContent(2) === DECL.slice(0, ro) + ",TIP('text')" + DECL.slice(ro), e.model.getLineContent(2));
        check('no plain executeEdits on top of it', log.snippets.length === 1 && log.edits.length === 0);
        check('the card is re-shown without moving the caret', log.show.length === 1 && log.setPos.length === 0);
    }
    {
        const e = load({ lines: LINES }); const log = rig(e);   // no snippet controller
        await showCard(e, cardWith());
        click(e, hrefOf(TIP));
        check('no snippet controller: the plain text with the placeholder default', e.model.getLineContent(2) === DECL.slice(0, ro) + ",TIP('text')" + DECL.slice(ro), e.model.getLineContent(2));
        check('...as one executeEdits between two undo stops', log.edits.length === 1 && log.edits[0].ops[0].t === ",TIP('text')" && log.stops === 2, JSON.stringify(log.edits));
    }
    {
        const e = load({ lines: LINES }); const log = rig(e, { snippet: 'noop' });
        await showCard(e, cardWith());
        click(e, hrefOf(TIP));
        check('a controller that does not take it (no change): the plain text instead, once', log.snippets.length === 1 && log.edits.length === 1 &&
            e.model.getLineContent(2) === DECL.slice(0, ro) + ",TIP('text')" + DECL.slice(ro), e.model.getLineContent(2));
    }
    {
        const e = load({ lines: LINES }); const log = rig(e, { snippet: 'apply', noHideWidgets: true });
        await showCard(e, cardWith());
        click(e, hrefOf(TIP));
        check('after a snippet, no public-showHover fallback (moving the caret would end the snippet)', log.snippets.length === 1 && log.setPos.length === 0 && e.triggers.indexOf('editor.action.showHover') < 0);
        const p = api(e, 'snippetToPlainText');
        const cases = [[",TIP('${1:text}')", ",TIP('text')"], [",FONT('${1:Segoe UI}',${2:9})", ",FONT('Segoe UI',9)"],
            [',ALRT(${1:F2Key})$0', ',ALRT(F2Key)'], [',X(${1})', ',X()'], [',MSG(\'\\$5\')', ",MSG('$5')"], [',C(${1|a,b|})', ',C(a)'], [',N(${1:a${2:b}c})', ',N(abc)']];
        for (const [s, want] of cases) check('plain text of ' + s + ' is ' + want, p(s) === want, JSON.stringify(p(s)));
    }

    section('the REAL v1.0.9 cards: control, WINDOW header, FILE, KEY (fixtures/lsp-109-attribute-cards.json)');
    {
        const lineOf = (name) => FX.cards[name].line;
        const posOf = (name) => ({ lineNumber: FX.cards[name].line, column: FX.cards[name].column });
        for (const name of ['button', 'window', 'file', 'key', 'memo']) {
            const e = load({ lines: FX.lines }); rig(e);
            const raw = FX.cards[name].markdown;
            const md = await showCard(e, raw, posOf(name));
            const want = linksOf(raw).length, got = md ? linksOf(md).length : -1;
            check(name + ' card: every one of its ' + want + ' edit links stays live, trusted for the edit command only',
                want > 0 && got === want && trustedForEditOnly(e.lastContent), 'got ' + got);
        }
        // name -> [link name, expected line after the click]
        const clicks = [
            ['button', 'DEFAULT', l => l + ',DEFAULT'],
            ['window', 'GRAY', l => l.replace(',GRAY', '')],
            ['window', 'CENTER', l => l + ',CENTER'],
            ['file', 'BINDABLE', l => l + ',BINDABLE'],
            ['file', 'CREATE', l => l.replace(',CREATE', '')],
            ['key', 'NOCASE', l => l.replace(',NOCASE', '')],
            ['memo', 'BINARY', l => l + ',BINARY'],
        ];
        for (const [card, chip, after] of clicks) {
            const e = load({ lines: FX.lines }); const log = rig(e);
            const md = await showCard(e, FX.cards[card].markdown, posOf(card));
            const lk = linksOf(md || '').find(x => x.name === chip);
            const ln = lineOf(card), before = e.model.getLineContent(ln);
            if (lk) click(e, lk.href);
            check(card + ': ' + (lk ? lk.title.split(' ')[0] : '?') + ' ' + chip + ' edits the declaration line', !!lk && e.model.getLineContent(ln) === after(before) && log.edits.length === 1,
                e.model.getLineContent(ln));
        }
        {
            const e = load({ lines: FX.lines }); const log = rig(e, { snippet: 'apply' });
            const md = await showCard(e, FX.cards.file.markdown, posOf('file'));
            const lk = linksOf(md || '').find(x => x.name === 'ENCRYPT');
            const ln = lineOf('file'), before = e.model.getLineContent(ln);
            if (lk) click(e, lk.href);
            check('file: the ENCRYPT snippet (brings OWNER) goes through the snippet controller', !!lk && log.snippets.length === 1 &&
                log.snippets[0].template === ",OWNER('${1:password}'),ENCRYPT" && e.model.getLineContent(ln) === before + ",OWNER('password'),ENCRYPT", e.model.getLineContent(ln));
        }
        {
            const e = load({ lines: FX.lines }); const log = rig(e, { snippet: 'apply' });
            const md = await showCard(e, FX.cards.window.markdown, posOf('window'));
            const tip = linksOf(md || '').find(x => x.name === 'TIP' || /^Add TIP/.test(x.title));
            const ln = lineOf('window'), before = e.model.getLineContent(ln);
            if (tip) click(e, tip.href);
            check('window: a value attribute (TIP) is a snippet chip and inserts with its tab stop', !!tip && log.snippets.length === 1 &&
                /TIP\('\$\{1:/.test(log.snippets[0].template) && e.model.getLineContent(ln).indexOf(before + ",TIP('") === 0, tip ? log.snippets[0] && log.snippets[0].template : 'no TIP link');
        }
    }

    section('refusal: plain names where the line cannot be edited (control, WINDOW and FILE cards)');
    {
        const cases = [
            ['read-only page', e => { e.isReadOnlyNow = () => true; }],
            ['outside the embed points', e => { e.embedRanges = [[100, 200]]; }],
            ['a GENERATED module opened as a plain file', e => { e.generatedSource = true; }],
        ];
        for (const [what, setup] of cases) {
            const e = load({ lines: LINES }); const log = rig(e, { snippet: 'apply' }); setup(e);
            const md = await showCard(e, cardWith());
            check(what + ': no command: link is left', md && md.indexOf('command:') < 0, md);
            check(what + ': names shown as code', md && /`READONLY`/.test(md) && /`HIDE`/.test(md) && /`HVSCROLL`/.test(md) && /`TIP`/.test(md));
            check(what + ': the flattened card is NOT trusted', md && md.indexOf('command:') < 0 && e.lastContent && !e.lastContent.isTrusted, JSON.stringify(e.lastContent && e.lastContent.isTrusted));
            click(e, hrefOf(ADD_HIDE)); click(e, hrefOf(TIP));
            check(what + ': a click on a stale (readable) live link does nothing', decodable(e, hrefOf(ADD_HIDE)) && log.edits.length === 0 && log.snippets.length === 0 && e.model.getLineContent(2) === DECL);
            for (const card of ['window', 'file']) {
                const f = load({ lines: FX.lines }); rig(f); setup(f);
                const fmd = await showCard(f, FX.cards[card].markdown, { lineNumber: FX.cards[card].line, column: FX.cards[card].column });
                check(what + ': the ' + card + ' card shows plain names, untrusted', fmd && fmd.indexOf('command:') < 0 && /`AT`|`PRE`/.test(fmd) && !f.lastContent.isTrusted);
            }
        }
        // Link forms the server might vary: no title, lowercase name.
        const e = load({ lines: LINES }); rig(e); e.generatedSource = true;
        const md = await showCard(e, '**Available:** [hide](' + hrefOf(ADD_HIDE) + ') [B](' + hrefOf(DEL_RO) + ' "Remove B \\"x\\"")');
        check('untitled / lowercase / escaped-quote-title links are flattened too', md === '**Available:** `hide` `B`', md);
    }

    section('the fallback card path (GH #250) prepares links the same way');
    {
        const run = async (generated) => {
            const e = load({ lines: LINES }); rig(e); e.generatedSource = generated;
            const lsp = e.providers.hover[1];
            let out;
            lsp.provideHover(e.model, POS).then(v => { out = v; });
            e.reply('localHover', { contents: '```clarion\nLIST\n```\n\nkeyword', fallback: true });
            await flush();
            e.reply('hover', { contents: cardWith() });
            await flush();
            return out && out.contents && out.contents[0];
        };
        const g = await run(true);
        check('fallback path: plain names in a generated module', g && g.value.indexOf('command:') < 0 && /`HIDE`/.test(g.value) && !g.isTrusted, JSON.stringify(g));
        const l = await run(false);
        check('fallback path: live links, trusted for the edit command only, where editable', l && l.value.indexOf(LIVE) >= 0 && trustedForEditOnly(l), JSON.stringify(l && l.isTrusted));
    }

    section('navigation and commands: no command: link runs or reaches openLocation');
    {
        const e = load({ lines: LINES }); const log = rig(e);
        const md = await showCard(e, cardWith() + '\n[Run it](command:workbench.action.terminal.new "Run") [x](command:clarion.applyAttributeEditX?%5B%5D)' +
            ' [Up](COMMAND:workbench.y) [Cased](command:Clarion.ApplyAttributeEdit?' + q(ADD_HIDE) + ')');
        check('another command: link on the card (any scheme case, a mis-cased edit command) is flattened to a plain name',
            md && md.indexOf('command:workbench') < 0 && /`Run it`/.test(md) && /`Up`/.test(md) && /`Cased`/.test(md) &&
            md.indexOf('applyAttributeEditX') < 0 && md.indexOf('Clarion.Apply') < 0 && md.indexOf(hrefOf(ADD_HIDE)) >= 0, md);
        for (const href of ['command:workbench.action.terminal.new', 'command:clarion.applyAttributeEdit', 'command:clarion.applyAttributeEdit?%5B%5D',
            'file:///clarion-attr/edit/eyJlIjpbXX0', 'file:///CLARION-ATTR/other/x', 'file://somehost/clarion-attr/edit/zz']) {
            const r = click(e, href);
            check('swallowed, nothing applied: ' + href.slice(0, 48), r === true && opened(e) === 0 && log.edits.length === 0);
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
        check('tab opened before its app: chips are live (trusted)', md && md.indexOf(LIVE) >= 0 && trustedForEditOnly(e.lastContent));
        e.api.applyGeneratedSourceFlag(true);                    // the app opened: this file is one of its modules
        check('the flag flips on', e.generatedSource === true);
        click(e, hrefOf(ADD_HIDE));
        check('a chip still on screen from before the flip does nothing', decodable(e, hrefOf(ADD_HIDE)) && log.edits.length === 0 && e.model.getLineContent(2) === DECL);
        e.model._v++;   // a new version: the shared local hover is asked again
        md = await showCard(e, cardWith());
        check('the next card shows plain names', md && md.indexOf('command:') < 0 && /`HIDE`/.test(md), md);
        e.api.applyGeneratedSourceFlag(false);                   // the app closed again
        e.model._v++;   // a new version: the shared local hover is asked again
        md = await showCard(e, cardWith());
        check('flag off again: chips are back (trusted)', e.generatedSource === false && md && md.indexOf(LIVE) >= 0 && trustedForEditOnly(e.lastContent));
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
        check('showContentHover is NOT called alone (it would leave the stale card up)', log.edits.length === 1 && log.show.length === 0);
        check('the cursor goes to the keyword and editor.action.showHover runs', log.setPos.length === 1 &&
            log.setPos[0].lineNumber === 2 && e.triggers.indexOf('editor.action.showHover') >= 0, JSON.stringify(e.triggers));
    }

    finish();
}

main().catch(err => { console.log('HARNESS ERROR: ' + (err && err.stack || err)); process.exit(1); });
