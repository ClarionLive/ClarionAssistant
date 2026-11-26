// references-view.test.js - Find All References: what the page hands Monaco's references view, and where
// opening a hit goes.
//
// Run:  node Terminal/test/references-view.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL reference provider and editor opener extracted from monaco-embeditor.html
// (local-first-loader.js) on a stand-in model, with createModel/Uri stand-ins. Pinned:
//   * every hit is on a ca-ref: preview model in language 'clarion-ref', never on the editor's own model:
//     the view names a group by its model's URI, and the editor's own is Monaco's inmemory://model/1,
//     which it listed as "1" with no folder
//   * a hit in the editor's own document is named after the file (CA Editor) or the module + procedure
//     (CA Embeditor), with the editor's current text; a hit elsewhere is named after its file, with the
//     host's preview text
//   * opening a hit in the editor's own document reveals it here (no openLocation); one elsewhere posts
//     openLocation with its line; a URI that is not a preview is left to Monaco
//   * a later search disposes the previews it does not use

const { load, flush, check, section, finish } = require('./local-first-loader');

const OTHER = 'D:\\src\\Orders.inc';
const SELF_FILE = 'D:\\src\\Orders.clw';

function rig(opts) {
    opts = opts || {};
    const lines = [];
    for (let i = 1; i <= 200; i++) lines.push(i === 180 ? 'Orders.Calculate PROCEDURE' : '  x = ' + i);
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
    e.currentFilePath = opts.fileMode ? SELF_FILE : '';
    e.baseTitle = 'Calculate';
    e.api.setEmbedDocPath(opts.fileMode ? '' : SELF_FILE);
    e.created = [];
    e.monaco.Uri = { from: (c) => ({ scheme: c.scheme, path: c.path, toString() { return c.scheme + ':' + c.path; } }) };
    e.monaco.editor.createModel = (text, lang, uri) => {
        const m = { text, lang, uri, disposed: false, getValue() { return this.text; }, setValue(t) { this.text = t; },
            isDisposed() { return this.disposed; }, dispose() { this.disposed = true; } };
        e.created.push(m);
        return m;
    };
    return e;
}

async function refs(e, reply, beforeReply) {
    let out;
    e.providers.references[0].provideReferences(e.model, { lineNumber: 180, column: 12 }).then(v => { out = v; });
    await flush();
    if (beforeReply) beforeReply();
    e.reply('references', reply);
    await flush();
    return out;
}
const REPLY = {
    locations: [
        { path: OTHER, line: 12, column: 3, endLine: 12, endColumn: 17 },
        { path: null, line: 180, column: 9, endLine: 180, endColumn: 23 },
        { path: null, line: 95, column: 7, endLine: 95, endColumn: 21 },
    ],
    files: { [OTHER]: '\n'.repeat(11) + '  Calculate PROCEDURE' },
};

async function main() {
    section('CA Editor: every hit on a named preview, never on the editor\'s own model');
    {
        const e = rig({ fileMode: true });
        const out = await refs(e, REPLY);
        check('three hits', out && out.length === 3, JSON.stringify(out));
        check('none on the editor\'s own model', out.every(r => r.uri !== e.model.uri && r.uri.scheme === 'ca-ref'), JSON.stringify(out.map(r => r.uri)));
        check('two previews, both clarion-ref', e.created.length === 2 && e.created.every(m => m.lang === 'clarion-ref'), JSON.stringify(e.created.map(m => m.lang)));
        check('the other file is named after its path', out[0].uri.path === '/D:/src/Orders.inc', out[0].uri.path);
        check('own-document hits are named after the file', out[1].uri.path === '/D:/src/Orders.clw' && out[2].uri === out[1].uri, out[1].uri.path);
        check('...with the editor\'s current text', out[1].uri && e.created[1].text === e.model.getValue());
        check('ranges are the host\'s 1-based ones', out[1].range.startLineNumber === 180 && out[1].range.startColumn === 9 && out[1].range.endColumn === 23, JSON.stringify(out[1].range));

        const opener = e.editorOpeners[0];
        check('opening an own-document hit is handled', opener.openCodeEditor(null, out[2].uri, { startLineNumber: 95, startColumn: 7 }) === true);
        check('...revealed here (caret on 95:7), no openLocation', e.ed.pos.lineNumber === 95 && e.ed.pos.column === 7 && e.requests('openLocation').length === 0, JSON.stringify(e.ed.pos));
        check('opening a hit elsewhere is handled', opener.openCodeEditor(null, out[0].uri, { startLineNumber: 12, startColumn: 3 }) === true);
        const nav = e.requests('openLocation');
        check('...the IDE opens it (openLocation, its path and line 12)', nav.length === 1 && nav[0].path === OTHER && nav[0].line === 12, JSON.stringify(nav));
        check('a URI that is not a preview is left to Monaco', opener.openCodeEditor(null, { toString: () => 'inmemory://model/1' }, null) === false);
    }

    section('CA Embeditor: own-document hits named after the module and the procedure');
    {
        const e = rig({ fileMode: false });
        const out = await refs(e, REPLY);
        check('named Orders.clw / Calculate', out && out[1].uri.path === '/Orders.clw/Calculate', out && out[1].uri.path);
    }

    section('a later search disposes the previews it does not use');
    {
        const e = rig({ fileMode: true });
        await refs(e, REPLY);
        const [other, self] = e.created;
        await refs(e, { locations: [{ path: null, line: 3, column: 1 }], files: {} });
        check('the other file\'s preview is disposed', other.disposed === true);
        check('the own-document preview is kept and reused', self.disposed === false && e.created.length === 2);
        check('a hit in a file with no preview text is left out', (await refs(e, { locations: [{ path: 'D:\\gone.clw', line: 1, column: 1 }], files: {} })).length === 0);
    }

    section('many hits: each preview\'s text is read and built once per search, not once per hit');
    {
        // A generated module can run to 60k lines and the host sends up to 1000 hits. getValue() joins the
        // whole model into one string, so doing it per hit (plus the reuse compare) froze the page.
        const e = rig({ fileMode: true });
        let reads = 0;
        const own = e.model.getValue;
        e.model.getValue = function () { reads++; return own.apply(this, arguments); };
        const locations = [];
        for (let i = 1; i <= 200; i++) {
            locations.push({ path: null, line: i, column: 1 });
            locations.push({ path: OTHER, line: i, column: 1 });
        }
        // Counted from the host's reply on: the request itself may read the text to sync it.
        const out = await refs(e, { locations, files: { [OTHER]: 'x\n'.repeat(200) } }, () => { reads = 0; });
        check('all 400 hits listed', out && out.length === 400, out && out.length);
        check('the editor\'s text is read once, not once per hit', reads === 1, 'reads=' + reads);
        let previewReads = 0;
        e.created.forEach(m => { const g = m.getValue; m.getValue = function () { previewReads++; return g.apply(this, arguments); }; });
        await refs(e, { locations, files: { [OTHER]: 'x\n'.repeat(200) } }, () => { reads = 0; previewReads = 0; });
        check('a repeat search reads the editor once and each reused preview once', reads === 1 && previewReads === 2,
            'reads=' + reads + ' previewReads=' + previewReads);
    }

    section('Enter in the list: Monaco shows in the preview; the "goto" setting goes to the reference');
    {
        // The list's focused row, the preview editor and the key event, as the page sees them.
        function enterRig(mode) {
            const e = rig({ fileMode: true });
            e.editor2 = null;
            e.api.setRefsEnterAction(mode);
            const tree = { querySelector: (s) => s === '.monaco-list-row.focused' ? e.row : null };
            e.editor.getDomNode = () => ({ contains: (n) => n === tree });
            e.pv = { sel: null, model: null, updateOptions() { this.ro = true; }, getModel() { return this.model; }, getSelection() { return this.sel; } };
            e.api.markRefPreviewEditor(e.pv);
            e.fire(0);
            e.enter = (fileRow) => {
                e.row = { querySelector: (s) => (fileRow && s === '.reference-file') ? {} : null };
                e.api.onRefTreeEnter({ key: 'Enter', target: { closest: () => tree } });
                e.fire(50);
            };
            return e;
        }
        const closes = (e) => e.triggers.filter(t => t === 'closeReferenceSearch').length;

        const s = enterRig('show');
        await refs(s, REPLY);
        s.pv.model = s.created[0]; s.pv.sel = { startLineNumber: 12, startColumn: 3 };
        s.enter(false);
        check('Monaco (default): Enter leaves the list open and opens nothing', closes(s) === 0 && s.requests('openLocation').length === 0, JSON.stringify(s.triggers));
        check('...the preview editor is read-only', s.pv.ro === true);

        const g = enterRig('goto');
        await refs(g, REPLY);
        g.pv.model = g.created[0]; g.pv.sel = { startLineNumber: 12, startColumn: 3 };
        g.enter(false);
        const nav = g.requests('openLocation');
        check('goto: Enter on another file\'s reference closes the list once', closes(g) === 1, JSON.stringify(g.triggers));
        check('...and the IDE opens it (openLocation, its path and line 12)', nav.length === 1 && nav[0].path === OTHER && nav[0].line === 12, JSON.stringify(nav));

        const h = enterRig('goto');
        await refs(h, REPLY);
        h.pv.model = h.created[1]; h.pv.sel = { startLineNumber: 95, startColumn: 7 };
        h.enter(false);
        check('goto: Enter on a reference in this document closes the list', closes(h) === 1);
        check('...and reveals it here (caret on 95:7), no openLocation', h.ed.pos.lineNumber === 95 && h.ed.pos.column === 7 && h.requests('openLocation').length === 0, JSON.stringify(h.ed.pos));

        const f = enterRig('goto');
        await refs(f, REPLY);
        f.pv.model = f.created[0]; f.pv.sel = { startLineNumber: 12, startColumn: 3 };
        f.enter(true);
        check('goto: Enter on a file line is left to Monaco (expand / collapse)', closes(f) === 0 && f.requests('openLocation').length === 0);
    }

    finish();
}
main().catch(err => { console.error('HARNESS ERROR:', err); process.exit(1); });
