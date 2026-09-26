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

// ---------- run ----------
(async function main() {
    await testDiagnostics();

    console.log('\n' + '='.repeat(60));
    console.log(pass + ' passed, ' + fail + ' failed');
    if (fail) {
        console.log('\nFailures:');
        failures.forEach(f => console.log('  - ' + f));
        process.exit(1);
    }
})().catch(e => { console.error(e); process.exit(1); });
