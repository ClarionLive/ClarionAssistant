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

// The server's card through the REAL LSP hover provider (no local card).
async function serverCard(e, md) {
    let out;
    e.providers.hover[1].provideHover(e.model, POS).then(v => { out = v; });
    e.reply('localHover', { contents: null });
    await flush();
    e.reply('hover', { contents: md });
    await flush();
    return out && out.contents && out.contents[0] && out.contents[0].value;
}
// A local (index) card through the REAL local hover provider.
async function localCard(e, md) {
    let out;
    e.providers.hover[0].provideHover(e.model, POS).then(v => { out = v; });
    e.reply('localHover', { contents: md, authoritative: true });
    await flush();
    return out && out.contents && out.contents[0] && out.contents[0].value;
}

const ROUTINE_CARD = '```clarion\nPrepareProcedure ROUTINE\n```\n\n[demoleg018.clw:474](file:///H:/Demo/demoleg018.clw#L474)';

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
            check(what + ': label unchanged', md === ROUTINE_CARD, md);
            click(e, 'file:///H:/Demo/demoleg018.clw#L474');
            const nav = opened(e);
            check(what + ': the click opens the file (openLocation, line 474)', nav.length === 1 && nav[0].line === 474 && e.reveals.length === 0, JSON.stringify(nav));
        }
    }

    section('applySource hands docPath to the link code (embed mode only)');
    check('applySource calls setEmbedDocPath(fileMode ? \'\' : msg.docPath)',
        /function applySource\(msg\)[\s\S]{0,2500}setEmbedDocPath\(fileMode \? '' : \(msg\.docPath \|\| ''\)\)/.test(html));

    finish();
}

main().catch(err => { console.log('HARNESS ERROR: ' + (err && err.stack || err)); process.exit(1); });
