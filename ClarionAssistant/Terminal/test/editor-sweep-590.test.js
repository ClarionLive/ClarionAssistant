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
    const monaco = { editor: { setModelMarkers: (m, owner, list) => env.applied.push(list.map(x => x.startLineNumber)) } };
    function requestFromHost(action, payload) {
        return new Promise(resolve => env.pending.push({ action, payload, resolve }));
    }
    const fakeSetTimeout = (fn) => { env.scheduled++; env.timers.push(fn); return env.timers.length; };
    const fakeClearTimeout = () => { env.cleared++; };
    // 16d140e9: requests name the synced buffer version (withBuffer) instead of carrying the buffer.
    const withBuffer = (m, payload) => Object.assign({ v: m.getVersionId() }, payload);
    const api = new Function('editor', 'monaco', 'requestFromHost', 'liveEditableRanges', 'setTimeout', 'clearTimeout', 'withBuffer',
        'var diagTimer = null;\n' + diagSrc + '\nreturn { refreshDiagnostics: refreshDiagnostics, scheduleDiagnostics: scheduleDiagnostics };')(
        env.editor, monaco, requestFromHost, () => [[1, 10]], fakeSetTimeout, fakeClearTimeout, withBuffer);
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
