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
    };
    const monaco = { editor: { setModelMarkers: (m, owner, list) => env.applied.push(list.map(x => x.startLineNumber)) } };
    function requestFromHost(action, payload) {
        return new Promise(resolve => env.pending.push({ action, payload, resolve }));
    }
    const fakeSetTimeout = () => { env.scheduled++; return 1; };
    const fakeClearTimeout = () => { };
    const api = new Function('editor', 'monaco', 'requestFromHost', 'liveEditableRanges', 'setTimeout', 'clearTimeout',
        'var diagTimer = null;\n' + diagSrc + '\nreturn { refreshDiagnostics: refreshDiagnostics, scheduleDiagnostics: scheduleDiagnostics };')(
        env.editor, monaco, requestFromHost, () => [[1, 10]], fakeSetTimeout, fakeClearTimeout);
    env.api = api;
    return env;
}
const reply = (line) => ({ markers: [{ severity: 8, message: 'm', line, column: 1, endLine: line, endColumn: 2 }] });

async function testDiagnostics() {
    section('GH #176 — only the newest diagnostics reply, for the buffer on screen, is rendered');
    {
        // Two round-trips in flight: request 1 for v1, an edit, request 2 for v2. Reply 2 arrives first,
        // then the slow reply 1 — it must not overwrite the newer markers.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.model.version = 2;
        env.api.refreshDiagnostics();
        check('two requests dispatched', env.pending.length === 2, 'got ' + env.pending.length);
        env.pending[1].resolve(reply(7));
        await flush();
        check('the newest reply applies', env.applied.length === 1 && env.applied[0][0] === 7, JSON.stringify(env.applied));
        env.pending[0].resolve(reply(4));
        await flush();
        check('a stale (older) reply arriving late is dropped', env.applied.length === 1 && env.applied[env.applied.length - 1][0] === 7,
            JSON.stringify(env.applied));
    }
    {
        // Same buffer version for both requests (two passes without an edit between), so ONLY the request
        // sequence can tell them apart: the superseded reply still must not render after the newer one.
        const env = loadDiag();
        env.api.refreshDiagnostics();
        env.api.refreshDiagnostics();
        env.pending[1].resolve(reply(6));
        await flush();
        env.pending[0].resolve(reply(2));
        await flush();
        check('a superseded reply for the same buffer version is dropped', env.applied.length === 1 && env.applied[0][0] === 6,
            JSON.stringify(env.applied));
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
// GH #184 — the font family list shows every preset while the box has focus
// =====================================================================================================
function testFontPicker() {
    section('GH #184 — focusing the font family box shows the full list; leaving it restores the font');
    let src = null;
    try {
        src = slice(html, '    // ===================== Font family picker (GH #184)', '    // ===================== end Font family picker', 'font picker');
    } catch (e) {
        check('font picker section present in the page', false, e.message);
        return;
    }
    const { JSDOM } = require('jsdom');
    const inputMarkup = /<input[^>]*id="setFontFamily"[^>]*>/.exec(html);
    const listMarkup = /<datalist id="fontFamilyList">[\s\S]*?<\/datalist>/.exec(html);
    if (!inputMarkup || !listMarkup) { check('font family input + datalist found in the page', false); return; }

    // One fresh page per scenario: the real markup, the real section, and a stand-in for the generic
    // change -> onSettingChanged listener registered AFTER the picker, exactly as wireSettingsPanel orders them.
    function load(initial) {
        const dom = new JSDOM('<!DOCTYPE html><body><button id="other">x</button>' + inputMarkup[0] + listMarkup[0] + '</body>');
        const win = dom.window, doc = win.document;
        const api = new Function('document', src + '\nreturn { wireFontFamilyPicker: wireFontFamilyPicker, fontFamilyFieldValue: fontFamilyFieldValue };')(doc);
        const el = doc.getElementById('setFontFamily');
        el.value = initial;
        api.wireFontFamilyPicker();
        const saved = [];
        el.addEventListener('change', () => saved.push(api.fontFamilyFieldValue(el)));
        return { win, doc, el, api, saved, fire: (t) => el.dispatchEvent(new win.Event(t, { bubbles: true })) };
    }

    {
        const p = load('Consolas');
        const origPlaceholder = p.el.placeholder;
        p.el.focus();
        check('focus clears the box so the datalist is not filtered', p.el.value === '', JSON.stringify(p.el.value));
        check('...the current font stays visible as the placeholder', p.el.placeholder === 'Consolas', p.el.placeholder);
        check('...and settings read while blanked still see the current font', p.api.fontFamilyFieldValue(p.el) === 'Consolas');
        p.el.blur();
        check('leaving without a pick restores the font', p.el.value === 'Consolas', JSON.stringify(p.el.value));
        check('...and the original placeholder', p.el.placeholder === origPlaceholder, p.el.placeholder);
    }
    {
        const p = load('Consolas');
        p.el.focus();
        p.fire('change');   // a change that fires on the way out while the box is still blank
        check('a change while still blank saves the current font, not "default"', p.saved.length === 1 && p.saved[0] === 'Consolas',
            JSON.stringify(p.saved));
    }
    {
        const p = load('Consolas');
        p.el.focus();
        p.el.value = 'Fira Code'; p.fire('input'); p.fire('change');   // picked from the list
        p.el.blur();
        check('a font picked from the list is kept and saved', p.el.value === 'Fira Code' && p.saved[p.saved.length - 1] === 'Fira Code',
            JSON.stringify({ v: p.el.value, saved: p.saved }));
        p.el.focus();
        check('re-focusing after a pick clears again (full list)', p.el.value === '', JSON.stringify(p.el.value));
        p.el.blur();
        check('...and leaving restores the picked font', p.el.value === 'Fira Code', JSON.stringify(p.el.value));
    }
    {
        const p = load('Consolas');
        p.el.focus();
        p.el.value = 'Fira Code'; p.fire('input'); p.fire('change');   // pick, focus stays in the box
        p.fire('pointerdown');                                         // click it again to reopen the list
        check('clicking the focused box again clears it to reopen the full list', p.el.value === '', JSON.stringify(p.el.value));
        p.el.blur();
        check('...and leaving restores the picked font', p.el.value === 'Fira Code', JSON.stringify(p.el.value));
    }
    {
        const p = load('Consolas');
        p.el.focus();
        p.el.value = 'Cour'; p.fire('input');
        check('typing filters as before (the typed text is kept)', p.el.value === 'Cour');
        p.el.value = ''; p.fire('input'); p.fire('change');   // then erased it on purpose
        p.el.blur();
        check('erasing the box on purpose still selects the default (blank)', p.el.value === '' && p.saved[p.saved.length - 1] === '',
            JSON.stringify({ v: p.el.value, saved: p.saved }));
    }
    {
        const p = load('');
        p.el.focus(); p.el.blur();
        check('an already-blank box stays blank', p.el.value === '');
    }
    check('the settings payload reads the box through fontFamilyFieldValue',
        /fontFamily: storedPref\('fontFamily', \(function \(\) \{ var e = document\.getElementById\('setFontFamily'\); return e \? fontFamilyFieldValue\(e\)/.test(html));
    const wireAt = html.indexOf('wireFontFamilyPicker();'), idsAt = html.indexOf("var ids = ['setCursorBehindEol'");
    check('the picker is wired before the generic change listeners', wireAt > 0 && idsAt > 0 && wireAt < idsAt);
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
