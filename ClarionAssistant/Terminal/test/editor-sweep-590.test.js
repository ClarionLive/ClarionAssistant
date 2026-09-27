// editor-sweep-590.test.js — three small CA Editor page fixes from the pre-5.9.0 sweep.
//
// Run:  node Terminal/test/editor-sweep-590.test.js [path/to/monaco-embeditor.html]
//
// Zero-dependency (no jsdom), built like its neighbours: the code under test is EXTRACTED from
// monaco-embeditor.html at run time and run against small fakes, so a regression in the page shows here.
//
//   GH #176  diagnostics squiggles a few lines off — refreshDiagnostics rendered whichever reply came back
//            LAST, even an older request's, so line numbers computed for another buffer version landed on
//            the current one. Pinned: a superseded reply is dropped, a reply for a buffer that has since
//            changed is dropped (and a fresh pass scheduled), the latest reply for the current buffer applies,
//            and a timed-out (null) reply still leaves the rendered markers alone (#170).
//   GH #184  the font family is a plain <select>: every preset always listed, a pick applies at once, a saved
//            font outside the list is added as an option so it round-trips, "Default" = "".

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

// ---------- scaffolding ----------
let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }
const flush = () => new Promise(r => setImmediate(r));

// =====================================================================================================
// GH #176 — diagnostics request ordering
// =====================================================================================================
const diagSrc = slice(html, '    var DIAG_TIMEOUT_MS = ', '    // registerClarionFolding()', 'diagnostics section');

// Loads the page's diagnostics code against a fake editor/model/host. Every requestFromHost call parks
// its resolver in `pending` so the test decides the order replies come back in.
function loadDiag() {
    const model = {
        version: 1,
        getValue() { return 'buffer v' + this.version; },
        getVersionId() { return this.version; },
        getLineCount() { return 10; },
    };
    const env = {
        model,
        editor: { getModel: () => env.currentModel },
        currentModel: model,
        pending: [],
        applied: [],       // one entry per setModelMarkers call: the list of start lines
        scheduled: 0,      // setTimeout(refreshDiagnostics, ...) calls
        timers: [],        // their callbacks, so a test can let the debounce fire
        cleared: 0,        // clearTimeout calls (a pending pass pushed back)
    };
    // 1c685f2e item 7: markers are stored per owner (getModelMarkers reads them back, as Monaco does); `applied`
    // keeps recording only the LSP owner's paints, so the GH #176 cases below read exactly as before.
    env.owners = {};                                       // owner -> the list currently on screen
    env.paints = [];                                       // every setModelMarkers call: {owner, list}
    const monaco = { editor: {
        setModelMarkers: (m, owner, list) => {
            env.owners[owner] = list.slice();
            env.paints.push({ owner, list: list.slice() });
            if (owner === 'clarion') env.applied.push(list.map(x => x.startLineNumber));
        },
        getModelMarkers: (filter) => (env.owners[filter.owner] || []).map(x => Object.assign({ owner: filter.owner }, x)),
    } };
    env.slot = [];         // slotDiagnostics requests, parked like `pending` (which holds only 'diagnostics')
    function requestFromHost(action, payload) {
        return new Promise(resolve => (action === 'slotDiagnostics' ? env.slot : env.pending).push({ action, payload, resolve }));
    }
    const fakeSetTimeout = (fn) => { env.scheduled++; env.timers.push(fn); return env.timers.length; };
    const fakeClearTimeout = () => { env.cleared++; };
    // 16d140e9: requests name the synced buffer version (withBuffer) instead of carrying the buffer.
    const withBuffer = (m, payload) => Object.assign({ v: m.getVersionId() }, payload);
    const bufferKey = (m) => 'm:' + m.getVersionId();
    // R11/R12 (1c685f2e): at rest (never typing-unsynced) and no span map, so the slot checks send `ranges`.
    // A test may flip env.typing / env.slotSlice.
    env.typing = false;
    env.slotSlice = null;
    env.idleWaiters = [];
    const typingUnsynced = () => env.typing;
    const slotSlicePayload = () => env.slotSlice;
    const api = new Function('editor', 'monaco', 'requestFromHost', 'liveEditableRanges', 'setTimeout', 'clearTimeout', 'withBuffer', 'bufferKey',
        'typingUnsynced', 'idleWaiters', 'slotSlicePayload',
        'var diagTimer = null;\n' + diagSrc + '\nreturn { refreshDiagnostics: refreshDiagnostics, scheduleDiagnostics: scheduleDiagnostics,' +
        ' resetDiagnosticsForNewSource: resetDiagnosticsForNewSource, setSlotChecks: function (on) { slotChecksEnabled = on; } };')(
        env.editor, monaco, requestFromHost, () => [[1, 10]], fakeSetTimeout, fakeClearTimeout, withBuffer, bufferKey,
        typingUnsynced, env.idleWaiters, slotSlicePayload);
    env.api = api;
    return env;
}
const reply = (line) => ({ markers: [{ severity: 8, message: 'm', line, column: 1, endLine: line, endColumn: 2 }] });

async function testDiagnostics() {
    section('GH #176 — only the newest diagnostics reply, for the buffer on screen, is rendered');
    {
        // 16d140e9 changed the shape of this race: only ONE diagnostics request is in flight, so a pass asked
        // for during request 1 (v1) is held, not dispatched. When the slow reply 1 arrives the buffer is at
        // v2: it must be dropped, and the held pass dispatched for v2 — whose reply then applies.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.model.version = 2;
        env.api.refreshDiagnostics();
        check('a second pass while one is in flight is held, not dispatched', env.pending.length === 1, 'got ' + env.pending.length);
        env.pending[0].resolve(reply(4));
        await flush();
        check('a stale (older) reply is dropped', env.applied.length === 0, JSON.stringify(env.applied));
        check('...and the held pass is scheduled', env.timers.length === 1);
        env.timers[0]();                       // debounce fires -> request 2 for v2
        check('...which dispatches for the new version', env.pending.length === 2 && env.pending[1].payload.v === 2,
            JSON.stringify(env.pending.map(p => p.payload)));
        env.pending[1].resolve(reply(7));
        await flush();
        check('the newest reply applies', env.applied.length === 1 && env.applied[0][0] === 7, JSON.stringify(env.applied));
    }
    {
        // Same buffer version for both passes (no edit between): the held pass has nothing new to ask, so
        // the outstanding reply answers it — one request, one render, nothing scheduled.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.api.refreshDiagnostics();
        env.pending[0].resolve(reply(6));
        await flush();
        check('a pass held for an unchanged buffer is answered by the outstanding reply', env.pending.length === 1 &&
            env.applied.length === 1 && env.applied[0][0] === 6 && env.timers.length === 0,
            JSON.stringify({ req: env.pending.length, applied: env.applied, timers: env.timers.length }));
    }
    {
        // The only request in flight, but the buffer changed before its reply came back (e.g. a programmatic
        // edit that scheduled no pass of its own): its line numbers belong to the old text.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.model.version = 5;
        const before = env.scheduled;
        env.pending[0].resolve(reply(3));
        await flush();
        check('a reply for a buffer that has since changed is dropped', env.applied.length === 0, JSON.stringify(env.applied));
        check('...and a fresh diagnostics pass is scheduled for the current buffer', env.scheduled === before + 1,
            'scheduled ' + (env.scheduled - before));
    }
    {
        // Typing already armed a pass: a stale reply must not re-arm it (that only pushes the pass back).
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.model.version = 2;
        env.api.scheduleDiagnostics();   // the edit's own debounce
        const sched = env.scheduled, cleared = env.cleared;
        env.pending[0].resolve(reply(3));
        await flush();
        check('a stale reply with a pass already pending leaves that pass alone', env.applied.length === 0 &&
            env.scheduled === sched && env.cleared === cleared, JSON.stringify({ s: env.scheduled - sched, c: env.cleared - cleared }));
        // Once the debounce has fired there is no pass pending, so the next stale reply does ask again.
        env.timers[env.timers.length - 1]();   // fires refreshDiagnostics -> request 2 (for v2)
        env.model.version = 3;                 // a programmatic edit, no debounce of its own
        const sched2 = env.scheduled;
        env.pending[1].resolve(reply(4));
        await flush();
        check('...and after the debounce fired, a stale reply schedules a fresh pass', env.scheduled === sched2 + 1,
            'scheduled ' + (env.scheduled - sched2));
    }
    {
        // The model was swapped (file reload) while the request was in flight.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.currentModel = { getVersionId: () => 1, getValue: () => '', getLineCount: () => 1 };
        env.pending[0].resolve(reply(3));
        await flush();
        check('a reply for a model no longer in the editor is dropped', env.applied.length === 0, JSON.stringify(env.applied));
    }
    {
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.pending[0].resolve(reply(9));
        await flush();
        check('the latest reply for an unchanged buffer applies', env.applied.length === 1 && env.applied[0][0] === 9, JSON.stringify(env.applied));
        env.api.refreshDiagnostics();
        env.pending[1].resolve({ markers: [] });
        await flush();
        check('an empty marker list still clears', env.applied.length === 2 && env.applied[1].length === 0, JSON.stringify(env.applied));
        env.api.refreshDiagnostics();
        env.pending[2].resolve(null);
        await flush();
        check('a timed-out (null) reply leaves the rendered markers alone (#170)', env.applied.length === 2, JSON.stringify(env.applied));
    }
}

// =====================================================================================================
// 1c685f2e item 7 — slot checks painted apart ('clarion-slot'), deduped against the LSP set both ways
// =====================================================================================================
const mk = (line, message) => ({ severity: 8, message, line, column: 1, endLine: line, endColumn: 5 });
const onScreen = (env, owner) => (env.owners[owner] || []).map(m => m.startLineNumber + ':' + m.message).sort();

async function testSlotDiagnostics() {
    section('1c685f2e item 7 — slotDiagnostics: every pass, own owner, deduped against the LSP set');
    {
        // 7.7: no in-flight limit — the LSP request is out, yet each pass still asks the slot checks.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        check('7.7 a pass posts slotDiagnostics beside diagnostics', env.slot.length === 1 && env.pending.length === 1,
            'slot ' + env.slot.length + ' lsp ' + env.pending.length);
        check('...with the live editable ranges and the synced v', env.slot[0] && env.slot[0].payload.v === 1 &&
            JSON.stringify(env.slot[0].payload.ranges) === '[[1,10]]');
        env.model.version = 2;
        env.api.refreshDiagnostics();
        check('7.7 with the LSP request still in flight, a second pass still posts slotDiagnostics (2), not diagnostics (1)',
            env.slot.length === 2 && env.pending.length === 1, 'slot ' + env.slot.length + ' lsp ' + env.pending.length);
    }
    {
        // 7.8 / 7.9: slot first, then an LSP reply carrying one duplicate and two near-misses.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.slot[0].resolve({ markers: [mk(5, 'LOOP is not terminated')] });
        await flush();
        check('7.8 slot markers are painted under clarion-slot', JSON.stringify(onScreen(env, 'clarion-slot')) === '["5:LOOP is not terminated"]' &&
            !env.owners.clarion, JSON.stringify(env.owners));
        env.pending[0].resolve({ markers: [mk(5, 'LOOP is not terminated'), mk(5, 'other'), mk(9, 'LOOP is not terminated')] });
        await flush();
        check('7.9 the LSP set drops only the exact (line, message) duplicate', JSON.stringify(onScreen(env, 'clarion')) ===
            '["5:other","9:LOOP is not terminated"]', JSON.stringify(onScreen(env, 'clarion')));
        check('7.8 ...and the later LSP reply leaves clarion-slot intact', JSON.stringify(onScreen(env, 'clarion-slot')) === '["5:LOOP is not terminated"]');
    }
    {
        // 7.10: the LSP set lands first; a slot reply with the same (line, message) removes the LSP copy.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.pending[0].resolve({ markers: [mk(5, 'LOOP is not terminated'), mk(7, 'lsp only')] });
        await flush();
        env.slot[0].resolve({ markers: [mk(5, 'LOOP is not terminated')] });
        await flush();
        check('7.10 slot after LSP: the duplicate LSP marker is removed', JSON.stringify(onScreen(env, 'clarion')) === '["7:lsp only"]',
            JSON.stringify(onScreen(env, 'clarion')));
        check('7.10 ...and the slot marker shows once', JSON.stringify(onScreen(env, 'clarion-slot')) === '["5:LOOP is not terminated"]');
    }
    {
        // 7.11 / 7.12: an older version's slot reply is dropped; a null keeps what is on screen.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.slot[0].resolve({ markers: [mk(3, 'first')] });
        await flush();
        env.model.version = 2;
        env.api.refreshDiagnostics();                  // the LSP pass is held, the slot one goes out
        env.model.version = 3;
        env.slot[1].resolve({ markers: [mk(4, 'stale')] });
        await flush();
        check('7.11 a slot reply for an older version is dropped', JSON.stringify(onScreen(env, 'clarion-slot')) === '["3:first"]',
            JSON.stringify(onScreen(env, 'clarion-slot')));
        env.api.refreshDiagnostics();
        env.slot[2].resolve(null);
        await flush();
        check('7.12 a null slot reply keeps the slot markers on screen', JSON.stringify(onScreen(env, 'clarion-slot')) === '["3:first"]');
        env.api.refreshDiagnostics();
        check('...and the next pass for that version asks again', env.slot.length === 4, 'slot ' + env.slot.length);
        env.api.refreshDiagnostics();
        check('...but a pass for an already-asked version does not re-ask (a held LSP pass re-running)', env.slot.length === 4,
            'slot ' + env.slot.length);
        env.slot[3].resolve({ markers: [] });
        await flush();
        check('...while a real empty reply clears them', onScreen(env, 'clarion-slot').length === 0);
    }
    {
        // 7.13: setSource clears the slot owner, and the old source's late slot reply is dropped.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.slot[0].resolve({ markers: [mk(2, 'old procedure')] });
        await flush();
        env.model.version = 2;
        env.api.refreshDiagnostics();                  // an old-source slot request still out
        env.api.resetDiagnosticsForNewSource();
        check('7.13 setSource clears clarion-slot', env.owners['clarion-slot'] && env.owners['clarion-slot'].length === 0,
            JSON.stringify(env.owners['clarion-slot']));
        env.slot[1].resolve({ markers: [mk(2, 'old procedure')] });
        await flush();
        check('7.13 ...and the old source\'s late slot reply is dropped', onScreen(env, 'clarion-slot').length === 0,
            JSON.stringify(onScreen(env, 'clarion-slot')));
        check('setSource calls resetDiagnosticsForNewSource (which clears the slot owner)',
            /resetBufferSync\(\);[^\n]*\n\s*resetDiagnosticsForNewSource\(\);/.test(html));
    }
    {
        // The Embeditor's file-mode tab (setSource slotChecks:false) asks nothing; the overlay (flag absent) asks.
        const env = loadDiag();
        env.api.setSlotChecks(false);
        env.api.refreshDiagnostics();
        check('slot checks off: no slotDiagnostics, the LSP pass still runs', env.slot.length === 0 && env.pending.length === 1);
        check('setSource reads slotChecks: absent = on, only an explicit false turns it off',
            /slotChecksEnabled = msg\.slotChecks !== false;/.test(html));
    }
}

// =====================================================================================================
// GH #184 — the font family is a non-editable <select> with every preset always listed
// =====================================================================================================
function testFontPicker() {
    section('GH #184 — font family select: full list, a pick applies at once, unknown saved fonts round-trip');
    const { JSDOM } = require('jsdom');
    const labelMarkup = /<label[^>]*>Font family[\s\S]*?<\/label>/.exec(html);
    let src = null;
    try { src = slice(html, '    // ===================== Font family select (GH #184)', '    // ===================== end Font family select', 'font family select'); }
    catch (e) { src = null; }
    check('font family select section present in the page', !!src);
    check('font family control markup found', !!labelMarkup);
    if (!labelMarkup) return;

    // One fresh page per scenario: the real markup, the real setter, and a stand-in for the generic
    // change -> onSettingChanged listener that reads the control's value the way the settings payload does.
    function load() {
        const dom = new JSDOM('<!DOCTYPE html><body>' + labelMarkup[0] + '</body>');
        const doc = dom.window.document;
        const el = doc.getElementById('setFontFamily');
        const setter = src ? new Function('document', src + '\nreturn setFontFamilySelect;')(doc) : null;
        const saved = [];
        if (el) el.addEventListener('change', () => saved.push(el.value.trim().slice(0, 64)));
        const set = (v) => { if (setter) setter(el, v); else el.value = v; };
        const values = () => (el && el.options) ? Array.from(el.options).map(o => o.value) : [];
        return { dom, doc, el, set, saved, values };
    }
    const PRESETS = ['Consolas', 'Cascadia Code', 'Cascadia Mono', 'Courier New', 'Fira Code', 'JetBrains Mono', 'Lucida Console', 'Source Code Pro'];

    {
        const p = load();
        check('the font family control is a non-editable select', !!p.el && p.el.tagName === 'SELECT', p.el ? p.el.tagName : 'missing');
        check('no datalist remains', !/<datalist id="fontFamilyList"/.test(html));
        const v = p.values();
        check('every preset is always listed', PRESETS.every(f => v.includes(f)), JSON.stringify(v));
        const first = p.el && p.el.options && p.el.options[0];
        check('the first option is "Default" with value "" (the editor default)',
            !!first && first.value === '' && /^Default/.test(first.textContent), first ? JSON.stringify([first.value, first.textContent]) : '-');
    }
    {
        const p = load();
        p.set('Courier New');
        check('with a font set, the full list is still there', PRESETS.every(f => p.values().includes(f)), JSON.stringify(p.values()));
        p.el.value = 'Fira Code';
        p.el.dispatchEvent(new p.dom.window.Event('change', { bubbles: true }));
        check('choosing an option applies it at once through change', p.saved.length === 1 && p.saved[0] === 'Fira Code', JSON.stringify(p.saved));
        p.el.value = '';
        p.el.dispatchEvent(new p.dom.window.Event('change', { bubbles: true }));
        check('choosing Default saves ""', p.saved[p.saved.length - 1] === '', JSON.stringify(p.saved));
    }
    {
        const p = load();
        const legacy = "Consolas, 'Courier New'";
        p.set(legacy);
        check('an unknown saved font is added as an option and selected', p.el.tagName === 'SELECT' && p.el.value === legacy &&
            p.values().filter(v => v === legacy).length === 1, JSON.stringify({ v: p.el.value, opts: p.values() }));
        check('...and round-trips unchanged into the settings payload', p.el.value.trim().slice(0, 64) === legacy);
        p.set(legacy);
        check('setting it again does not duplicate the option', p.values().filter(v => v === legacy).length === 1, JSON.stringify(p.values()));
        p.set('JetBrains Mono');
        check('a later preset selects the preset and drops the stale extra', p.el.value === 'JetBrains Mono' && !p.values().includes(legacy),
            JSON.stringify(p.values()));
        p.set('');
        check('setting "" selects Default', p.el.selectedIndex === 0 && p.el.value === '', String(p.el.selectedIndex));
    }
    check('no blank-on-focus machinery is left', !/_ffStash|ffRestore|fontFamilyFieldValue|setFontFamilyBox|wireFontFamilyPicker/.test(html));
    check('every page write to the font family goes through setFontFamilySelect',
        /setFontFamilySelect\(el, followingFontFamily/.test(html) &&
        /else if \(e\.id === 'setFontFamily'\) setFontFamilySelect\(e, String\(v\)\)/.test(html) &&
        /\(el = document\.getElementById\('setFontFamily'\)\)\) setFontFamilySelect\(el, \(typeof s\.fontFamily/.test(html));
}

// =====================================================================================================
// GH #195 — no white backdrop before Monaco's theme applies (high contrast)
// =====================================================================================================
const isWhiteish = (c) => /^(white|#fff|#ffffff|#fffffe|#eff1f5|rgb\(255, 255, 255\))$/i.test(String(c).trim());

function testBackdrop() {
    section('GH #195 — the pre-Monaco backdrop is never a light flash under high contrast');

    // Static CSS: the page's initial background, and the Windows High Contrast override.
    const rootBg = /:root\s*\{[^}]*--bg:\s*([^;]+);/.exec(html);
    const bodyRule = /html, body \{[^}]*background: var\(--bg\)/.test(html);
    check('the initial page background (html/body before any theme class) is not white',
        !!rootBg && bodyRule && !isWhiteish(rootBg[1]), rootBg ? rootBg[1] : 'no :root --bg');
    check('a forced-colors (Windows High Contrast) rule paints html/body with the system Canvas colour',
        /@media \(forced-colors: active\)\s*\{\s*html, body \{[^}]*background: Canvas !important/.test(html));

    // The boot script that paints the backdrop before Monaco loads, run against fakes.
    let src = null;
    try { src = slice(html, '    var themePrefDark = false;', '    function updateThemeBtn()', 'early theme script'); }
    catch (e) { check('early theme script present', false, e.message); return; }
    function boot(opts) {
        const store = { 'modernEmbeditor.themeDark': opts.dark ? '1' : '0' };
        if (opts.hc) store['modernEmbeditor.themeHC'] = '1';
        const docEl = { style: {} };
        const document = { documentElement: docEl, body: { classList: { toggle() { } } }, addEventListener() { } };
        const window = { matchMedia: (q) => ({ matches: !!opts.forced && q.indexOf('forced-colors: active') >= 0 }) };
        const localStorage = { getItem: (k) => (k in store ? store[k] : null), setItem() { } };
        new Function('window', 'document', 'localStorage', 'postToHost', src)(window, document, localStorage, () => { });
        return docEl.style.background;
    }
    check('Windows High Contrast: the early backdrop is the system Canvas colour (light pref)',
        boot({ dark: false, forced: true }) === 'Canvas', boot({ dark: false, forced: true }));
    check('Windows High Contrast: the early backdrop is the system Canvas colour (dark pref)',
        boot({ dark: true, forced: true }) === 'Canvas', boot({ dark: true, forced: true }));
    check("Monaco's HC toggle from dark (hc-black): the early backdrop is black, not the dark pref's grey",
        boot({ dark: true, hc: true }) === '#000000', boot({ dark: true, hc: true }));
    check('no high contrast: dark pref keeps its backdrop', boot({ dark: true }) === '#1e1e2e', boot({ dark: true }));
    check('no high contrast: light pref keeps its backdrop', boot({ dark: false }) === '#eff1f5', boot({ dark: false }));

    // Host side: every surface painted before the page (WebView2 DefaultBackgroundColor, the control
    // backdrop, the covers over the native editor) goes through one high-contrast-aware helper.
    const caDir = path.join(path.dirname(HTML_PATH), '..');
    const readCs = (rel) => { try { return fs.readFileSync(path.join(caDir, rel), 'utf8'); } catch (e) { return null; } };
    const mec = readCs('Terminal/MonacoEditorControl.cs');
    const mev = readCs('Terminal/ModernEmbeditorViewContent.cs');
    const mcs = readCs('MonacoClarionSourceEditor.cs');
    check('host sources found', !!(mec && mev && mcs), caDir);
    if (!(mec && mev && mcs)) return;
    const helper = /static Color PrePaintBackdrop\(bool isDark\)\s*\{[\s\S]*?SystemInformation\.HighContrast[\s\S]*?SystemColors\.Window[\s\S]*?\n        \}/.exec(mec);
    check('PrePaintBackdrop uses the system window colour under Windows High Contrast', !!helper);
    check('the Monaco control backdrop (and so WebView2 DefaultBackgroundColor) comes from PrePaintBackdrop',
        /BackColor = PrePaintBackdrop\(isDark\);\s*\r?\n[\s\S]{0,400}?new WebView2 \{[^}]*DefaultBackgroundColor = BackColor/.test(mec));
    check('the embeditor covers come from PrePaintBackdrop (no hardcoded white)',
        (mev.match(/BackColor = MonacoEditorControl\.PrePaintBackdrop\(/g) || []).length === 2 && !/Color\.White \}/.test(mev));
    check('the CA Editor cover comes from PrePaintBackdrop',
        /CoverColor \{ get \{ return MonacoEditorControl\.PrePaintBackdrop\(/.test(mcs));
}

// ---------- run ----------
(async function main() {
    await testDiagnostics();
    await testSlotDiagnostics();
    testFontPicker();
    testBackdrop();

    console.log('\n' + '='.repeat(60));
    console.log(pass + ' passed, ' + fail + ' failed');
    if (fail) {
        console.log('\nFailures:');
        failures.forEach(f => console.log('  - ' + f));
        process.exit(1);
    }
})().catch(e => { console.error(e); process.exit(1); });
