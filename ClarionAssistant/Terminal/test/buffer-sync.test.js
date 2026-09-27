// buffer-sync.test.js - 16d140e9: the Monaco page ships its buffer to the host ONCE per content version.
//
// Run:  node Terminal/test/buffer-sync.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. EXTRACTS the real request/sync code from monaco-embeditor.html (requestFromHost and the
// buffer-sync helpers, the folding callback, the completion/hover/signature-help providers, diagnostics,
// the F12 / Ctrl+F12 key interceptor, Ctrl+click definition, the outline refresh, the structure designer
// and file mode's fileState mirror) and runs it against fakes that record every message posted to the host.
//
// The crash: Clarion.exe (32-bit, 2 GB) died with Chromium's OOM code 0xE0000008 inside
// TryGetWebMessageAsString while the CA Embeditor was on an 86,722-line / 3.2 MB generated module - every
// completion, hover, signature help, definition, implementation, diagnostics, outline and (new in 5.9.0)
// folding request carried buffer: model.getValue(). Pinned here:
//   * a request after an edit is preceded by EXACTLY ONE bufferSync, and carries `v`, never `buffer`
//   * N hovers without an edit post ZERO buffers
//   * a model swap (different model id) and setSource/bufferResync (resetBufferSync) force a resync
//   * every buffer-needing action sends `v` instead of `buffer` (dynamic + a source scan)
//   * file mode's fileState is stamped with `v` and doubles as the sync (no second copy)
//   * diagnostics: the timeout grows with the buffer (10 s + 3 s/MB, max 60 s) and only ONE request is in
//     flight - a pass asked for meanwhile is dispatched when the outstanding one settles
// It also MEASURES the bytes posted for two scenarios on a synthetic 3.2 MB buffer (printed, not asserted).
//
// Against the pre-fix page (pass its path as argv[2]) this harness is RED: that is the point.

const fs = require('fs');
const path = require('path');

const TERMINAL = path.join(__dirname, '..');
const HTML_PATH = process.argv[2] || path.join(TERMINAL, 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8').replace(/\r\n/g, '\n');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a + startMarker.length);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' - ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }
const flush = () => new Promise(r => setImmediate(r));

// ---------------------------------------------------------------------------------------------- slices
const SLICES = [
    slice(html, '    function pushFileState() {', '    // Unsaved edits stashed by the host', 'pushFileState'),
    slice(html, '    function requestFromHost(', '    function lspKindToMonaco(', 'requestFromHost + buffer sync'),
    'var __foldingCallback = ' + slice(html, 'setClarionLspFolding(', ');\n                    }\n                    registerClarionFolding();', 'folding callback')
        .slice('setClarionLspFolding('.length) + ';\n',
    slice(html, '    function cmdStructureDesigner() {', '    // ----- "New Structure" template picker', 'cmdStructureDesigner'),
    slice(html, '    function wireCtrlClickDefinition(ed) {', '    function installGlyphBookmarkClick()', 'wireCtrlClickDefinition'),
    slice(html, '    var DIAG_TIMEOUT_MS = ', '    // registerClarionFolding()', 'diagnostics'),
    slice(html, '    function registerClarionProviders() {', '    // Clarion syntax highlighting (Monarch)', 'registerClarionProviders'),
    slice(html, '    function installFindKeyInterceptor() {', '    // ----- Code Snippets picker', 'installFindKeyInterceptor'),
    slice(html, '    function refreshOutline() {', '\n    }\n', 'refreshOutline') + '\n    }\n',
];
const SRC = SLICES.join('\n');

// Names the slices DECLARE resolve to the evaluated function scope; every other free name goes through
// the env proxy (a stub when the env does not supply it). Stubs return undefined, which the page code
// reads as "not in a comment / not suppressed / nothing to do".
const DECLARED = new Set();
for (const m of SRC.matchAll(/\bfunction\s+([A-Za-z_$][\w$]*)/g)) DECLARED.add(m[1]);
for (const m of SRC.matchAll(/\bvar\s+([A-Za-z_$][\w$]*)/g)) DECLARED.add(m[1]);
const STUB = function () { return undefined; };

function makeModel(id, text) {
    return {
        id, _v: 1, _text: text,
        getVersionId() { return this._v; },
        getValue() { return this._text; },
        getValueLength() { return this._text.length; },
        getLineContent() { return '    Loc:Count += 1'; },
        getLineCount() { return 10; },
        getLineMaxColumn() { return 20; },
        edit(t) { this._text = t; this._v++; },
    };
}

function load(opts) {
    opts = opts || {};
    const posted = [];            // every string posted to the host
    const providers = {};
    const listeners = [];
    const timers = [];
    const env = {
        posted, providers, listeners, timers,
        pendingRequests: {}, reqSeq: 0, FOLD_TIMEOUT_MS: 1500,
        fileMode: !!opts.fileMode, isDirty: false, editSeq: 0, trimPad: STUB,
        designerEnabled: true, designerSession: null, diagTimer: null, outlineSeq: 0,
        capturingKey: false, snippetPickerOpen: false, findUiMode: 'panel', commitKeysEnabled: true,
        snippetsList: [], CLARION_KEYWORD_SET: {},
        postToHost(obj) { posted.push(JSON.stringify(obj)); },
        setTimeout(fn, ms) { timers.push({ fn, ms }); return timers.length; },
        clearTimeout() { },
        liveEditableRanges() { return [[1, 10]]; },
        document: {
            addEventListener(type, fn, capture) { listeners.push({ type, fn, capture }); },
            getElementById() { return { classList: { contains: () => false } }; },
        },
        monaco: {
            Range: function (a, b, c, d) { this.a = a; this.b = b; this.c = c; this.d = d; },
            languages: {
                CompletionItemKind: new Proxy({}, { get: () => 1 }),
                registerCompletionItemProvider(lang, p) { (providers.completion = providers.completion || []).push(p); },
                registerHoverProvider(lang, p) { providers.hover = p; },
                registerSignatureHelpProvider(lang, p) { providers.signatureHelp = p; },
            },
            editor: {
                registerLinkOpener() { },
                setModelMarkers(m, owner, list) { (env.markersSet = env.markersSet || []).push(list.length); },
                MouseTargetType: { CONTENT_TEXT: 6 },
            },
        },
    };
    // Injectable clock for the [local-rt] timing (1c685f2e item 0): the page reads performance.now().
    env.clock = 0;
    env.performance = { now: () => env.clock };
    env.model = makeModel('$model1', opts.text || 'PROGRAM\r\n  CODE\r\n');
    env.editor = {
        getModel: () => env.model,
        getPosition: () => ({ lineNumber: 2, column: 3 }),
        onMouseDown(fn) { env.mouseDown = fn; },
    };
    env.activeEd = () => env.editor;
    env.window = { chrome: { webview: { postMessage() { } } } };
    const scope = new Proxy(env, {
        // env wins over node's globals (setTimeout!), declarations win over env.
        has(t, k) { return typeof k === 'string' && !DECLARED.has(k) && ((k in t) || !(k in globalThis)); },
        get(t, k) { if (k === Symbol.unscopables) return undefined; return (k in t) ? t[k] : STUB; },
        set(t, k, v) { t[k] = v; return true; },
    });
    const exportsList = ['requestFromHost', 'pushFileState', '__foldingCallback', 'cmdStructureDesigner', 'wireCtrlClickDefinition',
        'refreshDiagnostics', 'scheduleDiagnostics', 'registerClarionProviders', 'installFindKeyInterceptor', 'refreshOutline',
        'resetBufferSync', 'withBuffer', 'diagTimeoutFor', 'resetDiagnosticsForNewSource'];
    // Only names the page really declares (an undeclared one would resolve to a proxy stub).
    const ret = '{' + exportsList.map(n => n + ': ' + (DECLARED.has(n) || n === '__foldingCallback' ? n : 'undefined')).join(', ') + '}';
    // eslint-disable-next-line no-new-func
    const api = new Function('__scope', 'with (__scope) {\n' + SRC + '\nreturn ' + ret + ';\n}')(scope);
    env.api = api;
    api.registerClarionProviders();
    api.installFindKeyInterceptor();
    api.wireCtrlClickDefinition(env.editor);
    // Messages posted since `mark`, parsed.
    env.since = (mark) => posted.slice(mark).map(s => JSON.parse(s));
    env.reply = (reqId, data) => { const r = env.pendingRequests[reqId]; if (r) { delete env.pendingRequests[reqId]; r(data); } };
    return env;
}

// The actions a real session fires, each as a function of the loaded env.
const pos = { lineNumber: 2, column: 5 };
const ACTIONS = {
    completion: (e) => e.providers.completion[0].provideCompletionItems(e.model, pos),
    hover: (e) => e.providers.hover.provideHover(e.model, pos),
    signatureHelp: (e) => e.providers.signatureHelp.provideSignatureHelp(e.model, pos, null, {}),
    foldingRanges: (e) => e.api.__foldingCallback(e.model),
    diagnostics: (e) => e.api.refreshDiagnostics(),
    definition: (e) => fireKey(e, { keyCode: 123, key: 'F12' }),
    implementation: (e) => fireKey(e, { keyCode: 123, key: 'F12', ctrlKey: true }),
    definitionCtrlClick: (e) => e.mouseDown({ event: { ctrlKey: true }, target: { type: 6, position: { lineNumber: 2, column: 4 } } }),
    documentStructure: (e) => e.api.refreshOutline(),
    openDesigner: (e) => e.api.cmdStructureDesigner(),
};
function fireKey(e, init) {
    const ev = Object.assign({ ctrlKey: false, metaKey: false, shiftKey: false, altKey: false, keyCode: 0, key: '' }, init);
    ev.preventDefault = () => { }; ev.stopImmediatePropagation = () => { };
    e.listeners.filter(l => l.type === 'keydown').forEach(l => l.fn(ev));
}

async function main() {
    section('Every buffer-needing request sends `v`, not `buffer`');
    for (const [name, fire] of Object.entries(ACTIONS)) {
        const e = load();
        const mark = e.posted.length;
        fire(e);
        const msgs = e.since(mark);
        const action = name === 'definitionCtrlClick' ? 'definition' : name;
        const req = msgs.find(m => m.action === action);
        const syncs = msgs.filter(m => m.action === 'bufferSync');
        check(name + ': request posted with v and no buffer', !!req && typeof req.v === 'number' && !('buffer' in req),
            JSON.stringify(msgs.map(m => ({ action: m.action, keys: Object.keys(m) }))));
        check(name + ': preceded by exactly one bufferSync carrying that v',
            syncs.length === 1 && req && msgs.indexOf(syncs[0]) < msgs.indexOf(req) && syncs[0].v === req.v &&
            syncs[0].buffer === e.model.getValue(), 'syncs=' + syncs.length);
    }

    section('No edit, no buffer: N hovers post zero buffers after the first sync');
    {
        const e = load();
        ACTIONS.hover(e);                      // first request syncs
        const mark = e.posted.length;
        for (let i = 0; i < 10; i++) { ACTIONS.hover(e); ACTIONS.completion(e); ACTIONS.foldingRanges(e); }
        const msgs = e.since(mark);
        check('30 requests without an edit post no bufferSync', msgs.filter(m => m.action === 'bufferSync').length === 0);
        check('...and none of them carries the buffer', msgs.every(m => !('buffer' in m) && !('text' in m)));
        check('...and all name the same v', new Set(msgs.map(m => m.v)).size === 1);
    }

    section('An edit: the next request is preceded by exactly one bufferSync, later ones by none');
    {
        const e = load();
        ACTIONS.hover(e);
        const v1 = e.since(0).find(m => m.action === 'hover').v;
        e.model.edit('PROGRAM\r\n  CODE\r\n  x = 1\r\n');
        const mark = e.posted.length;
        ACTIONS.completion(e); ACTIONS.hover(e); ACTIONS.foldingRanges(e);
        const msgs = e.since(mark);
        const syncs = msgs.filter(m => m.action === 'bufferSync');
        check('one bufferSync for the edited version', syncs.length === 1 && msgs[0].action === 'bufferSync', 'syncs=' + syncs.length);
        check('...carrying the edited text', syncs[0] && syncs[0].buffer === e.model.getValue());
        check('...with a new v that every following request names', syncs[0] && syncs[0].v !== v1 &&
            msgs.filter(m => m.action !== 'bufferSync').every(m => m.v === syncs[0].v));
    }

    section('Model swap / setSource / host resync force a resend');
    {
        const e = load();
        ACTIONS.hover(e);
        // A different model (split/diff or a replaced model) — version ids restart per model.
        e.model = makeModel('$model2', 'OTHER\r\n');
        let mark = e.posted.length;
        ACTIONS.hover(e);
        let msgs = e.since(mark);
        check('a different model id forces a bufferSync even at the same version id',
            msgs.filter(m => m.action === 'bufferSync').length === 1 && msgs[0].buffer === 'OTHER\r\n');
        mark = e.posted.length;
        if (e.api.resetBufferSync) e.api.resetBufferSync();   // setSource's model.setValue, or the host's 'bufferResync'
        ACTIONS.hover(e);
        msgs = e.since(mark);
        check('resetBufferSync (setSource / bufferResync) forces a resend', msgs.filter(m => m.action === 'bufferSync').length === 1);
        check('setSource resets the sync after model.setValue',
            /model\.setValue\(text\);\s*\n\s*resetBufferSync\(\);/.test(html));
        check("the page handles the host's 'bufferResync' by resetting",
            /msg\.type === 'bufferResync'\)\s*\{\s*\n\s*resetBufferSync\(\);/.test(html));
    }

    section('File mode: fileState is stamped with v and doubles as the sync');
    {
        const e = load({ fileMode: true });
        e.model.edit('FILE MODE TEXT\r\n');
        let mark = e.posted.length;
        e.api.pushFileState();
        let msgs = e.since(mark);
        const fsMsg = msgs.find(m => m.action === 'fileState');
        check('fileState carries v and the text (close-safety mirror unchanged)', fsMsg && typeof fsMsg.v === 'number' && fsMsg.text === 'FILE MODE TEXT\r\n');
        check('...with text as the LAST key (the host reads the small fields without scanning it)',
            fsMsg && Object.keys(fsMsg)[Object.keys(fsMsg).length - 1] === 'text');
        mark = e.posted.length;
        ACTIONS.completion(e); ACTIONS.hover(e);
        msgs = e.since(mark);
        check('a request for the same version sends NO separate bufferSync', msgs.filter(m => m.action === 'bufferSync').length === 0);
        check('...and names the fileState v', fsMsg && msgs.every(m => m.v === fsMsg.v));
    }

    section('bufferSync puts the buffer LAST (host parses action + v without scanning 3 MB)');
    {
        const e = load();
        ACTIONS.hover(e);
        const raw = e.posted.find(s => s.indexOf('"bufferSync"') >= 0) || '';
        check('{"action":"bufferSync","v":N,"buffer":"..."}', /^\{"action":"bufferSync","v":\d+,"buffer":"/.test(raw), raw.slice(0, 60));
    }

    section('Source scan: no request payload carries buffer: any more');
    {
        const bad = [];
        const re = /requestFromHost\(\s*'([A-Za-z]+)'\s*,\s*\{[^}]*\bbuffer\s*:/g;
        let m;
        while ((m = re.exec(html))) bad.push(m[1]);
        check('no requestFromHost(..., { buffer: ... })', bad.length === 0, bad.join(', '));
        for (const a of ['foldingRanges', 'diagnostics', 'hover', 'completion', 'signatureHelp', 'definition', 'implementation',
                         'documentStructure', 'openDesigner', 'openDesignerCreate']) {
            let n = (html.match(new RegExp("requestFromHost\\('" + a + "',\\s*withBuffer\\(", 'g')) || []).length;
            // diagnostics builds its payload first (so a throw cannot wedge the in-flight slot)
            if (a === 'diagnostics' && /payload = withBuffer\(model, \{ ranges: liveEditableRanges\(\) \}\)/.test(html) &&
                /requestFromHost\('diagnostics', payload,/.test(html)) n++;
            check(a + ' goes through withBuffer', n >= 1, 'found ' + n);
        }
    }

    section('Diagnostics: timeout scales with the buffer; one request in flight');
    {
        const e = load();
        const f = e.api.diagTimeoutFor;
        check('diagTimeoutFor exists', typeof f === 'function');
        if (typeof f === 'function') {
            check('small buffer keeps the 10 s floor', f(1000) === 10000 + 3, 'got ' + f(1000));
            check('0 chars -> 10 s', f(0) === 10000);
            check('3.2 MB -> 10 s + ~3 s/MB (19,152 ms)', f(3198532) === 10000 + Math.ceil(3198532 / 1048576 * 3000), 'got ' + f(3198532));
            check('100 MB caps at 60 s', f(100 * 1048576) === 60000);
        }
        const big = load({ text: 'x'.repeat(3198532) });
        big.api.refreshDiagnostics();
        const diag = big.since(0).find(m => m.action === 'diagnostics');
        check('the request carries the scaled timeout', diag && diag.timeoutMs === (f ? f(3198532) : -1), diag && String(diag.timeoutMs));
        const t = big.timers.find(x => x.ms === (f ? f(3198532) : -1));
        check('...and requestFromHost arms its give-up timer with it', !!t);

        // One in flight: a second pass while the first is out does not dispatch.
        const e2 = load();
        e2.api.refreshDiagnostics();
        e2.model.edit('edited\r\n');
        e2.api.refreshDiagnostics();
        let reqs = e2.since(0).filter(m => m.action === 'diagnostics');
        check('a pass asked for while one is in flight is NOT dispatched', reqs.length === 1, 'dispatched ' + reqs.length);
        const timersBefore = e2.timers.length;
        e2.reply(reqs[0].reqId, { markers: [{ severity: 8, message: 'm', line: 1, column: 1, endLine: 1, endColumn: 2 }] });
        await flush();
        check('the stale reply (buffer moved on) is dropped', !e2.markersSet);
        check('...and the remembered pass is scheduled', e2.timers.length === timersBefore + 1);
        e2.timers[e2.timers.length - 1].fn();   // the debounce fires
        reqs = e2.since(0).filter(m => m.action === 'diagnostics');
        check('...which dispatches the newest version', reqs.length === 2 && reqs[1].v !== reqs[0].v);

        // A timed-out (null) reply frees the slot too.
        const e3 = load();
        e3.api.refreshDiagnostics();
        const r3 = e3.since(0).find(m => m.action === 'diagnostics');
        e3.reply(r3.reqId, null);
        await flush();
        e3.api.refreshDiagnostics();
        check('after a null (timeout) reply the next pass dispatches', e3.since(0).filter(m => m.action === 'diagnostics').length === 2);

        // Pipeline MINOR: a pass held behind a request that then TIMES OUT (null) must still run.
        const e4 = load();
        e4.api.refreshDiagnostics();
        e4.api.refreshDiagnostics();           // held (same version)
        const r4 = e4.since(0).find(m => m.action === 'diagnostics');
        const t4 = e4.timers.length;
        e4.reply(r4.reqId, null);
        await flush();
        check('a held pass is scheduled when the outstanding reply is a timeout (null)', e4.timers.length === t4 + 1,
            'scheduled ' + (e4.timers.length - t4));

        // Pipeline MINOR: setSource (new procedure/file) must not queue the new source's pass behind the old one.
        const e5 = load();
        e5.api.refreshDiagnostics();           // old source's request, still out
        const hasReset = typeof e5.api.resetDiagnosticsForNewSource === 'function';
        check('the page has resetDiagnosticsForNewSource', hasReset);
        check('setSource calls it after loading the new text', /resetBufferSync\(\);[^\n]*\n\s*resetDiagnosticsForNewSource\(\);/.test(html));
        if (hasReset) e5.api.resetDiagnosticsForNewSource();
        e5.model = makeModel('$model1', 'NEW PROCEDURE\r\n');
        e5.model._v = 7;
        e5.api.refreshDiagnostics();
        let d5 = e5.since(0).filter(m => m.action === 'diagnostics');
        check('the new source\'s first pass dispatches at once', d5.length === 2, 'dispatched ' + d5.length);
        const t5 = e5.timers.length;
        e5.reply(d5[0].reqId, { markers: [{ severity: 8, message: 'old', line: 1, column: 1, endLine: 1, endColumn: 2 }] });
        await flush();
        check('the old source\'s late reply schedules nothing and paints nothing', e5.timers.length === t5 && !e5.markersSet);
        e5.api.refreshDiagnostics();
        d5 = e5.since(0).filter(m => m.action === 'diagnostics');
        check('...and does not free the NEW request\'s in-flight slot', d5.length === 2, 'dispatched ' + d5.length);

        // Pipeline NIT: a throw while building the payload must not leave diagnostics stuck in flight.
        const e6 = load();
        const realGet = e6.model.getValue;
        e6.model.getValue = () => { throw new Error('boom'); };
        try { e6.api.refreshDiagnostics(); } catch (err) { /* the pre-fix page throws out of here */ }
        e6.model.getValue = realGet;
        e6.model.edit('after the throw\r\n');
        e6.api.refreshDiagnostics();
        check('a throw building the payload does not wedge diagnostics', e6.since(0).filter(m => m.action === 'diagnostics').length === 1);
    }

    section('Requests are stamped with sentAt + timeoutMs (host logs replies that arrive after the page gave up)');
    {
        const e = load();
        ACTIONS.hover(e);
        const h = e.since(0).find(m => m.action === 'hover');
        check('hover carries sentAt and timeoutMs=4000', h && typeof h.sentAt === 'number' && h.timeoutMs === 4000);
    }

    // ------------------------------------------------------------------ [local-rt] timing (1c685f2e item 0)
    section('[local-rt]: one timing line per timed reply, on the page clock, sync attributed once');
    {
        const RT_RE = /^\[local-rt\] action=(\w+) v=(\S+) rtMs=(\d+) syncBytes=(\d+) syncMs=(\d+)( syncAgeMs=(\d+))?( timeout=1| null=1)?$/;
        const logs = (e, mark) => e.since(mark || 0).filter(m => m.action === 'log');
        const parse = (m) => { const x = RT_RE.exec(m.line); return x && { action: x[1], v: x[2], rtMs: +x[3], syncBytes: +x[4], syncMs: +x[5],
            syncAgeMs: x[7] == null ? null : +x[7], flag: (x[8] || '').trim() }; };
        const lastReq = (e, action) => e.since(0).filter(m => m.action === action).pop();

        // 0.1 / 0.2 / 0.3 / 0.8: sync at t=900 (costing 25 ms inside getValue), request at 1000, reply at 1137.
        const text = 'x'.repeat(3198532);
        const e = load({ text });
        const realGet = e.model.getValue.bind(e.model);
        e.model.getValue = () => { e.clock += 25; return realGet(); };   // the sync's own cost, on the page clock
        e.clock = 900;
        const p = e.api.withBuffer(e.model, { line: 2, column: 5 });   // posts the bufferSync (900 -> 925)
        e.clock = 1000;
        e.api.requestFromHost('completion', p);
        check('0.1 nothing is logged on send', logs(e).length === 0);
        e.clock = 1137;
        e.reply(lastReq(e, 'completion').reqId, { items: [] });
        await flush();
        let L = logs(e);
        const r1 = L.length === 1 ? parse(L[0]) : null;
        check('0.1 exactly one well-formed [local-rt] line per reply', !!r1, JSON.stringify(L));
        check('0.2 rtMs is request-sent -> reply-received (137)', r1 && r1.rtMs === 137, r1 && ('rtMs=' + r1.rtMs));
        const syncMsg = e.since(0).find(m => m.action === 'bufferSync');
        check('0.3 syncBytes = the posted buffer\'s JS length', r1 && syncMsg && r1.syncBytes === syncMsg.buffer.length && r1.syncBytes === 3198532,
            r1 && ('syncBytes=' + r1.syncBytes));
        check('0.8 syncMs = the page-side cost of the sync (getValue + postMessage) = 25', r1 && r1.syncMs === 25, r1 && ('syncMs=' + r1.syncMs));
        check('0.2 syncAgeMs = request send minus sync post (1000 - 925)', r1 && r1.syncAgeMs === 75, r1 && ('syncAgeMs=' + r1.syncAgeMs));
        check('...and the line names the action and the v', r1 && r1.action === 'completion' && r1.v === String(syncMsg.v));

        // 0.4: a second request with no edit carries no sync.
        let mark = e.posted.length;
        e.clock = 2000;
        ACTIONS.hover(e);
        e.clock = 2010;
        e.reply(lastReq(e, 'hover').reqId, { contents: 'x' });
        await flush();
        L = logs(e, mark);
        const r2 = L.length === 1 ? parse(L[0]) : null;
        check('0.4 no edit: syncBytes=0 syncMs=0 and no syncAgeMs', r2 && r2.syncBytes === 0 && r2.syncMs === 0 && r2.syncAgeMs === null && r2.rtMs === 10,
            JSON.stringify(L));

        // 0.5: two requests after one edit share one sync; it is attributed to exactly ONE of them.
        e.model.edit(text.slice(0, -1) + 'Y');
        mark = e.posted.length;
        ACTIONS.foldingRanges(e);
        ACTIONS.completion(e);
        const f = lastReq(e, 'foldingRanges'), c = lastReq(e, 'completion');
        e.reply(c.reqId, { items: [] }); e.reply(f.reqId, { ranges: [] });
        await flush();
        L = logs(e, mark).map(parse);
        check('0.5 two lines, the sync bytes on exactly one (the first request of that version)',
            L.length === 2 && L.filter(x => x && x.syncBytes > 0).length === 1 &&
            L.find(x => x.syncBytes > 0).action === 'foldingRanges', JSON.stringify(L));

        // 0.6: a page timeout is logged (timeout=1), a host null as null=1.
        const t = load();
        t.clock = 50;
        ACTIONS.hover(t);
        const giveUp = t.timers.filter(x => x.ms === 4000).pop();
        t.clock = 4050;
        giveUp.fn();
        await flush();
        L = logs(t).map(parse);
        check('0.6 a timed-out request logs rtMs=<timeout> timeout=1', L.length === 1 && L[0] && L[0].flag === 'timeout=1' && L[0].rtMs === 4000,
            JSON.stringify(logs(t)));
        const n = load();
        ACTIONS.hover(n);
        n.reply(lastReq(n, 'hover').reqId, null);
        await flush();
        L = logs(n).map(parse);
        check('0.6 a host null reply logs null=1', L.length === 1 && L[0] && L[0].flag === 'null=1', JSON.stringify(logs(n)));

        // Untimed actions log nothing (signature help, definition...): the log stays at the measured set.
        const u = load();
        ACTIONS.signatureHelp(u);
        u.reply(lastReq(u, 'signatureHelp').reqId, null);
        await flush();
        check('an untimed action (signatureHelp) logs no line', logs(u).length === 0);

        // 0.7: no log post is ever over 1 KB (it never carries the buffer).
        check('0.7 every log post is under 1 KB', e.posted.filter(s => s.indexOf('"action":"log"') >= 0).every(s => s.length < 1024));

        // File mode: the fileState mirror is the sync, so it is attributed the same way.
        const fm = load({ fileMode: true });
        fm.model.edit('FILE MODE TEXT\r\n');
        fm.api.pushFileState();
        ACTIONS.completion(fm);
        fm.reply(lastReq(fm, 'completion').reqId, { items: [] });
        await flush();
        L = logs(fm).map(parse);
        check('file mode: the fileState text is the attributed sync', L.length === 1 && L[0] && L[0].syncBytes === 'FILE MODE TEXT\r\n'.length,
            JSON.stringify(logs(fm)));
        check('the page posts the line through the generic {action:\'log\'} channel', /postToHost\(\{ action: 'log', line: line \}\)/.test(html));
    }

    // ------------------------------------------------------------------------------------ measurement
    section('Measurement: bytes posted to the host on a synthetic 3.2 MB buffer');
    {
        const line = '    IF LOC:Count > 0 THEN DO SomeRoutine. ! "quoted" \\ back\r\n';
        let text = '';
        while (text.length < 3198532) text += line;
        text = text.slice(0, 3198532);
        const bytes = (e, mark) => e.posted.slice(mark).reduce((n, s) => n + Buffer.byteLength(s, 'utf8'), 0);

        const a = load({ text });
        let m0 = a.posted.length;
        for (let i = 0; i < 20; i++) ACTIONS.hover(a);
        const bytesA = bytes(a, m0);

        const b = load({ text });
        m0 = b.posted.length;
        for (let i = 0; i < 10; i++) {
            b.model.edit(b.model.getValue().slice(0, -1) + String.fromCharCode(65 + i));
            ACTIONS.hover(b); ACTIONS.completion(b); ACTIONS.foldingRanges(b); ACTIONS.diagnostics(b);
            // the host answers diagnostics before the next edit (so the one-in-flight gate never holds one back)
            const d = b.since(0).filter(m => m.action === 'diagnostics').pop();
            if (d) b.reply(d.reqId, { markers: [] });
            await flush();
        }
        const bytesB = bytes(b, m0);
        const reqB = b.since(m0).filter(m => m.action !== 'bufferSync').length;
        const mb = n => (n / 1048576).toFixed(1) + ' MB';
        console.log('    buffer: ' + text.length + ' chars; one JSON-escaped copy = ' + Buffer.byteLength(JSON.stringify(text)) + ' bytes');
        console.log('    (a) 20 hovers, no edits:                        ' + bytesA + ' bytes (' + mb(bytesA) + ')');
        console.log('    (b) 10 edits x hover+completion+folding+diag:   ' + bytesB + ' bytes (' + mb(bytesB) + '), ' + reqB + ' requests');
        console.log('    (the IDE holds each posted string as UTF-16: x2 in Clarion.exe)');
        const one = Buffer.byteLength(JSON.stringify(text));
        check('(a) posts the buffer at most once', bytesA < one * 1.01 + 20 * 400, 'bytes ' + bytesA);
        check('(b) posts the buffer once per edit (10 copies), not once per request (40)', bytesB < one * 10.1 + 40 * 400, 'bytes ' + bytesB);
    }

    console.log('\n' + pass + ' passed, ' + fail + ' failed');
    if (fail) { console.log('\nFailures:\n  ' + failures.join('\n  ')); process.exit(1); }
}

main().catch(err => { console.error(err && err.stack || err); process.exit(1); });
