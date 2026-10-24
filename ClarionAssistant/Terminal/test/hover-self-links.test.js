// hover-self-links.test.js - H1-a (2026-10-23): a location link to the CA Embeditor's OWN document stays in it.
//
// Run:  node Terminal/test/hover-self-links.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL hover providers and link opener extracted from monaco-embeditor.html
// (local-first-loader.js). The language server analyses the embeditor's text under the module's path, so its
// card's "demoleg018.clw:474" link is line 474 of the EMBEDITOR text, not of demoleg018.clw on disk. Pinned:
//   * embed mode, a link to docPath (the embeditor's module): the click reveals the line in the editor
//     (revealLineFromHost) and posts NO openLocation; the label reads "line 474"
//   * the server's encoded drive colon (file:///h%3A/...) and another case are the same document
//   * a link to ANOTHER file still posts openLocation, label unchanged
//   * a local (index) card's own-document link is a disk line: marked ?ondisk, label kept, the click opens the .clw
//   * a ?ondisk link from the host (the server path's index fallback) opens the .clw too
//   * the CA Editor (file mode) and an embeditor without docPath behave as before
//   * applySource hands msg.docPath to the link code in embed mode only
//   * H1-b (2026-10-24): Back after a hover link returns to the HOVERED position (the card's anchor), not the
//     caret, in the CA Embeditor (revealed in place) and the CA Editor (same file, revealed by the host); a
//     link not on the newest card, a late card for an older hover, and a non-hover reveal start at the caret

const { load, flush, check, section, finish, html } = require('./local-first-loader');

const DOC = 'H:\\Demo\\demoleg018.clw';
const LINES = ['  CODE', '  DO PrepareProcedure', '  RETURN'];
const POS = { lineNumber: 2, column: 8 };

// A Monaco URI the way URI.parse(href) builds it: query and fragment split off, fsPath decoded with a
// lower-case drive letter and backslashes (Windows).
const uriOf = (s) => {
    const m = /^file:\/\/([^/]*)(\/[^?#]*)(?:\?([^#]*))?(?:#(.*))?$/.exec(s);
    const path = decodeURIComponent(m[2]);
    let fs = path;
    if (/^\/[A-Za-z]:/.test(fs)) fs = fs[1].toLowerCase() + fs.slice(2);
    return { scheme: 'file', authority: m[1], path, query: m[3] || '', fragment: m[4] || '', fsPath: fs.replace(/\//g, '\\') };
};

function rig(opts) {
    opts = opts || {};
    const e = load({ lines: LINES });
    e.reveals = [];
    e.revealLineFromHost = (msg) => { e.reveals.push(msg); };
    e.fileMode = !!opts.fileMode;
    // Guarded so the harness runs (and fails on behaviour, not a TypeError) against a page from before H1-a.
    if (e.api.setEmbedDocPath) e.api.setEmbedDocPath(opts.docPath === undefined ? DOC : opts.docPath);
    return e;
}
const click = (e, href) => e.linkOpeners[0].open(uriOf(href));
const opened = (e) => e.requests('openLocation');

// Every entry of a hover answer, joined: the card, then (GH #270) the footer row its location links moved to.
const cardText = (h) => h && h.contents && h.contents.length ? h.contents.map(c => c && c.value).filter(v => v != null).join('\n') : undefined;

// The server's card through the REAL LSP hover provider (no local card).
async function serverCard(e, md) {
    let out;
    e.providers.hover[1].provideHover(e.model, POS).then(v => { out = v; });
    e.reply('localHover', { contents: null });
    await flush();
    e.reply('hover', { contents: md });
    await flush();
    return cardText(out);
}
// A local (index) card through the REAL local hover provider.
async function localCard(e, md) {
    let out;
    e.providers.hover[0].provideHover(e.model, POS).then(v => { out = v; });
    e.reply('localHover', { contents: md, authoritative: true });
    await flush();
    return cardText(out);
}

// H1-b: a rig whose navigation history runs for real (load({ nav: true })): a 500-line text, the caret on
// line 10, and an editor fake that the page's revealLineFromHost / cmdNavBack move.
const HOVER_POS = { lineNumber: 65, column: 8 };
function navRig(opts) {
    opts = opts || {};
    const lines = [];
    for (let i = 1; i <= 500; i++) lines.push(i === 65 ? '  DO PrepareProcedure' : i === 474 ? 'PrepareProcedure ROUTINE' : '  x = ' + i);
    const e = load({ lines, nav: true });
    e.ready = true;
    e.ed = {
        pos: { lineNumber: 10, column: 3 },
        getPosition() { return { lineNumber: this.pos.lineNumber, column: this.pos.column }; },
        setPosition(p) { this.pos = { lineNumber: p.lineNumber, column: p.column }; },
        revealLineInCenter() { }, focus() { }, getModel: () => e.model,
    };
    e.activeEd = () => e.ed;
    e.fileMode = !!opts.fileMode;
    e.currentFilePath = opts.fileMode ? DOC : '';
    e.api.setEmbedDocPath(opts.fileMode ? '' : DOC);
    return e;
}
// Monaco asks BOTH hover providers for the position; the server answers the card.
async function hoverAt(e, pos, md) {
    let out = [];
    e.providers.hover[0].provideHover(e.model, pos).then(v => { out[0] = v; });
    e.providers.hover[1].provideHover(e.model, pos).then(v => { out[1] = v; });
    e.reply('localHover', { contents: null });
    await flush();
    e.reply('hover', { contents: md });
    await flush();
    const v = out[1] || out[0];
    return cardText(v);
}

const ROUTINE_CARD ='```clarion\nPrepareProcedure ROUTINE\n```\n\n[demoleg018.clw:474](file:///H:/Demo/demoleg018.clw#L474)';

async function main() {
    section('embed mode: the server card\'s link to the embeditor\'s own document');
    {
        const e = rig();
        const md = await serverCard(e, ROUTINE_CARD);
        check('the label reads "line 474"', md && md.indexOf('[line 474](file:///H:/Demo/demoleg018.clw#L474)') >= 0, md);
        check('...not "demoleg018.clw:474"', md && md.indexOf('demoleg018.clw:474') < 0, md);
        const handled = click(e, 'file:///H:/Demo/demoleg018.clw#L474');
        check('the opener reports the link handled', handled === true);
        check('the line is revealed in the editor, caret on it', e.reveals.length === 1 && e.reveals[0].line === 474 && e.reveals[0].column === 1, JSON.stringify(e.reveals));
        check('NO openLocation reaches the host (the .clw is not opened)', opened(e).length === 0, JSON.stringify(opened(e)));
    }

    section('the same document spelled differently');
    {
        const e = rig();
        const md = await serverCard(e, '[demoleg018.clw:12](file:///h%3A/demo/DEMOLEG018.clw#L12 "title")');
        check('encoded drive colon + other case: relabelled, title kept', md === '[line 12](file:///h%3A/demo/DEMOLEG018.clw#L12 "title")', md);
        click(e, 'file:///h%3A/demo/DEMOLEG018.clw#L12');
        check('...and revealed here, no openLocation', e.reveals.length === 1 && e.reveals[0].line === 12 && opened(e).length === 0);
    }

    section('a link to ANOTHER file still opens it');
    {
        const e = rig();
        const card = '[demoleg.inc:3](file:///H:/Demo/demoleg.inc#L3) → [demoleg019.clw:40](file:///H:/Demo/demoleg019.clw#L40)';
        const md = await serverCard(e, card);
        check('labels unchanged', md === card, md);
        click(e, 'file:///H:/Demo/demoleg.inc#L3');
        click(e, 'file:///H:/Demo/demoleg019.clw#L40');
        const nav = opened(e);
        check('both post openLocation with their lines', nav.length === 2 && nav[0].line === 3 && nav[1].line === 40, JSON.stringify(nav));
        check('...and nothing is revealed here', e.reveals.length === 0);
    }

    section('a disk line (the solution index) to the own module opens the .clw');
    {
        const e = rig();
        const md = await localCard(e, '```clarion\nOtherProc PROCEDURE\n```\n\n[demoleg018.clw:88](file:///H:/Demo/demoleg018.clw#L88)');
        check('the local card\'s own-module link is marked ?ondisk, label kept',
            md && md.indexOf('[demoleg018.clw:88](file:///H:/Demo/demoleg018.clw?ondisk#L88)') >= 0, md);
        click(e, 'file:///H:/Demo/demoleg018.clw?ondisk#L88');
        const nav = opened(e);
        check('...its click posts openLocation (line 88), nothing revealed here', nav.length === 1 && nav[0].line === 88 && e.reveals.length === 0, JSON.stringify(nav));
        const md2 = await serverCard(rig(), '[demoleg018.clw:9](file:///H:/Demo/demoleg018.clw?ondisk#L9)');
        check('a ?ondisk link from the host keeps its label', md2 === '[demoleg018.clw:9](file:///H:/Demo/demoleg018.clw?ondisk#L9)', md2);
    }

    section('unchanged: the CA Editor (file mode) and an embeditor without docPath');
    {
        for (const [what, opts] of [['file mode', { fileMode: true }], ['no docPath', { docPath: '' }]]) {
            const e = rig(opts);
            const md = await serverCard(e, ROUTINE_CARD);
            // GH #270: the link line moves to the footer row (cardText joins the card and the footer with one \n).
            check(what + ': label unchanged', md === ROUTINE_CARD.replace('\n\n[', '\n['), md);
            click(e, 'file:///H:/Demo/demoleg018.clw#L474');
            const nav = opened(e);
            check(what + ': the click opens the file (openLocation, line 474)', nav.length === 1 && nav[0].line === 474 && e.reveals.length === 0, JSON.stringify(nav));
        }
    }

    section('applySource hands docPath to the link code (embed mode only)');
    check('applySource calls setEmbedDocPath(fileMode ? \'\' : msg.docPath)',
        /function applySource\(msg\)[\s\S]{0,2500}setEmbedDocPath\(fileMode \? '' : \(msg\.docPath \|\| ''\)\)/.test(html));

    // ---- H1-b: Back from a hover link returns to the HOVERED position, not the caret ----
    section('H1-b embed mode: caret on line 10, hover line 65, click the self link to 473, Back');
    {
        const e = navRig();
        const md = await hoverAt(e, HOVER_POS, '```clarion\nPrepareProcedure ROUTINE\n```\n\n[demoleg018.clw:473](file:///H:/Demo/demoleg018.clw#L473)');
        check('the card offers the self link "line 473"', md && md.indexOf('[line 473](') >= 0, md);
        check('click: handled', click(e, 'file:///H:/Demo/demoleg018.clw#L473') === true);
        check('...the caret is on the ROUTINE line (473)', e.ed.pos.lineNumber === 473, JSON.stringify(e.ed.pos));
        e.api.cmdNavBack();
        check('Back returns to the HOVERED line 65 (col 8), not the caret line 10',
            e.ed.pos.lineNumber === 65 && e.ed.pos.column === 8, JSON.stringify(e.ed.pos));
        e.api.cmdNavForward();
        check('...and Forward to 473', e.ed.pos.lineNumber === 473, JSON.stringify(e.ed.pos));
    }

    // ---- GH #270: the card's location links sit in the footer row, its second content entry ----
    section('GH #270 + H1-a/H1-b: the self link in the FOOTER row stays in the embeditor, and Back still works');
    {
        const e = navRig();
        let out = [];
        e.providers.hover[0].provideHover(e.model, HOVER_POS).then(v => { out[0] = v; });
        e.providers.hover[1].provideHover(e.model, HOVER_POS).then(v => { out[1] = v; });
        e.reply('localHover', { contents: null });
        await flush();
        e.reply('hover', { contents: '```clarion\nPrepareProcedure ROUTINE\n```\n\n[demoleg018.clw:473](file:///H:/Demo/demoleg018.clw#L473)' });
        await flush();
        const c = out[1] && out[1].contents;
        check('two entries: the card, then the footer row', !!c && c.length === 2, JSON.stringify(c));
        check('the footer row holds the relabelled self link "line 473"', !!c && c[1] && c[1].value === '[line 473](file:///H:/Demo/demoleg018.clw#L473)', c && c[1] && c[1].value);
        check('...and the card no longer does', !!c && c[0] && c[0].value.indexOf('](file:') < 0, c && c[0] && c[0].value);
        click(e, 'file:///H:/Demo/demoleg018.clw#L473');
        check('click: revealed here (caret on 473), NO openLocation', e.ed.pos.lineNumber === 473 && opened(e).length === 0, JSON.stringify(e.ed.pos));
        e.api.cmdNavBack();
        check('Back (from a footer link) returns to the HOVERED line 65 (col 8)',
            e.ed.pos.lineNumber === 65 && e.ed.pos.column === 8, JSON.stringify(e.ed.pos));
    }
    {
        const e = rig();
        let out;
        e.providers.hover[0].provideHover(e.model, POS).then(v => { out = v; });
        e.reply('localHover', { contents: '```clarion\nOtherProc PROCEDURE\n```\n\n[demoleg018.clw:88](file:///H:/Demo/demoleg018.clw#L88)', authoritative: true });
        await flush();
        const c = out && out.contents;
        check('a local card\'s ?ondisk link moves to the footer row, label kept',
            !!c && c.length === 2 && c[1].value === '[demoleg018.clw:88](file:///H:/Demo/demoleg018.clw?ondisk#L88)', JSON.stringify(c));
        click(e, 'file:///H:/Demo/demoleg018.clw?ondisk#L88');
        const nav = opened(e);
        check('...its click opens the .clw (openLocation 88), nothing revealed here', nav.length === 1 && nav[0].line === 88 && e.reveals.length === 0, JSON.stringify(nav));
    }
    {
        const e = navRig({ fileMode: true });
        let out = [];
        e.providers.hover[0].provideHover(e.model, HOVER_POS).then(v => { out[0] = v; });
        e.providers.hover[1].provideHover(e.model, HOVER_POS).then(v => { out[1] = v; });
        e.reply('localHover', { contents: null });
        await flush();
        e.reply('hover', { contents: ROUTINE_CARD });
        await flush();
        const c = out[1] && out[1].contents;
        check('file mode: the link sits in the footer row, label unchanged', !!c && c.length === 2 && c[1].value === '[demoleg018.clw:474](file:///H:/Demo/demoleg018.clw#L474)', JSON.stringify(c));
        click(e, 'file:///H:/Demo/demoleg018.clw#L474');
        e.api.revealLineFromHost({ line: 474, column: 1 });
        e.api.cmdNavBack();
        check('file mode: Back (from a footer link) returns to the HOVERED line 65 (col 8)',
            e.ed.pos.lineNumber === 65 && e.ed.pos.column === 8, JSON.stringify(e.ed.pos));
    }

    section('H1-b unchanged: a host reveal that is not a hover link starts at the caret');
    {
        const e = navRig();
        await hoverAt(e, HOVER_POS, ROUTINE_CARD);      // a card was shown, but the jump is not its link
        e.api.revealLineFromHost({ line: 473, column: 1 });
        e.api.cmdNavBack();
        check('Back returns to the caret line 10', e.ed.pos.lineNumber === 10, JSON.stringify(e.ed.pos));
    }

    section('H1-b: a link that is not on the newest card falls back to the caret');
    {
        const e = navRig();
        await hoverAt(e, HOVER_POS, ROUTINE_CARD);
        await hoverAt(e, { lineNumber: 200, column: 5 }, '[demoleg018.clw:300](file:///H:/Demo/demoleg018.clw#L300)');
        click(e, 'file:///H:/Demo/demoleg018.clw#L474');   // the line-65 card's link: not the card on screen
        e.api.cmdNavBack();
        check('Back returns to the caret line 10', e.ed.pos.lineNumber === 10, JSON.stringify(e.ed.pos));
        const e2 = navRig();
        await hoverAt(e2, HOVER_POS, ROUTINE_CARD);
        await hoverAt(e2, { lineNumber: 200, column: 5 }, '[demoleg018.clw:300](file:///H:/Demo/demoleg018.clw#L300)');
        click(e2, 'file:///H:/Demo/demoleg018.clw#L300');
        e2.api.cmdNavBack();
        check('...while the newest card\'s link goes Back to ITS anchor (200, col 5)',
            e2.ed.pos.lineNumber === 200 && e2.ed.pos.column === 5, JSON.stringify(e2.ed.pos));
    }

    section('H1-b: a late card for an older hover does not take over the anchor');
    {
        const e = navRig();
        // Hover 65 (its server answer is slow), then 200; 200's card arrives first, 65's after it.
        e.providers.hover[0].provideHover(e.model, HOVER_POS);
        e.providers.hover[1].provideHover(e.model, HOVER_POS);
        e.reply('localHover', { contents: null });
        await flush();
        const slow = e.pending('hover')[0];
        const p200 = { lineNumber: 200, column: 5 };
        e.providers.hover[0].provideHover(e.model, p200);
        e.providers.hover[1].provideHover(e.model, p200);
        e.reply('localHover', { contents: null });
        await flush();
        e.reply('hover', { contents: '[demoleg018.clw:300](file:///H:/Demo/demoleg018.clw#L300)' });
        await flush();
        e.reply(slow, { contents: ROUTINE_CARD });              // Monaco discards it: the mouse moved on
        await flush();
        click(e, 'file:///H:/Demo/demoleg018.clw#L300');
        e.api.cmdNavBack();
        check('Back from the card on screen returns to ITS anchor (200, col 5)',
            e.ed.pos.lineNumber === 200 && e.ed.pos.column === 5, JSON.stringify(e.ed.pos));
    }

    section('H1-b the CA Editor (file mode): a link to the same file, revealed by the host');
    {
        const e = navRig({ fileMode: true });
        await hoverAt(e, HOVER_POS, ROUTINE_CARD);
        click(e, 'file:///H:/Demo/demoleg018.clw#L474');
        const nav = opened(e);
        check('the click still posts openLocation (line 474), nothing moved yet',
            nav.length === 1 && nav[0].line === 474 && e.ed.pos.lineNumber === 10, JSON.stringify(nav));
        e.api.revealLineFromHost({ line: 474, column: 1 });   // the host's answer (MonacoSourceNavigator)
        check('...the host\'s revealLine puts the caret on 474', e.ed.pos.lineNumber === 474);
        e.api.cmdNavBack();
        check('Back returns to the HOVERED line 65 (col 8), not the caret line 10',
            e.ed.pos.lineNumber === 65 && e.ed.pos.column === 8, JSON.stringify(e.ed.pos));
    }
    {
        const e = navRig({ fileMode: true });
        await hoverAt(e, HOVER_POS, ROUTINE_CARD);
        click(e, 'file:///H:/Demo/demoleg018.clw#L474');
        e.api.revealLineFromHost({ line: 90, column: 1 });     // some other reveal (not the clicked line)
        e.api.cmdNavBack();
        check('file mode: a reveal to another line starts at the caret (10)', e.ed.pos.lineNumber === 10, JSON.stringify(e.ed.pos));
        // Back left the caret on 10; a later reveal to the clicked line must not reuse the consumed origin (65).
        e.api.revealLineFromHost({ line: 474, column: 1 });
        e.api.cmdNavBack();
        check('...and the hover origin is used at most once (Back = the caret line 10)', e.ed.pos.lineNumber === 10, JSON.stringify(e.ed.pos));
    }
    {
        const e = navRig({ fileMode: true });
        await hoverAt(e, HOVER_POS, '[demoleg.inc:3](file:///H:/Demo/demoleg.inc#L3)');
        click(e, 'file:///H:/Demo/demoleg.inc#L3');             // another file: its own editor, not this one
        e.api.revealLineFromHost({ line: 3, column: 1 });
        e.api.cmdNavBack();
        check('file mode: a link to another file leaves this editor\'s reveals at the caret', e.ed.pos.lineNumber === 10, JSON.stringify(e.ed.pos));
    }

    finish();
}

main().catch(err => { console.log('HARNESS ERROR: ' + (err && err.stack || err)); process.exit(1); });
