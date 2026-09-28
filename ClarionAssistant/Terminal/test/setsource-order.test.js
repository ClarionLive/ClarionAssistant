// setsource-order.test.js - 1c685f2e F1: only the NEWEST setSource may load its text.
//
// Run:  node Terminal/test/setsource-order.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Extracts the real applySource from monaco-embeditor.html and runs it with a fetch whose
// replies the test releases in any order. Before the fix, two setSources whose fetches finished out of order
// loaded the OLDER procedure's text last - under the newer one's ranges, title and mode.

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8').replace(/\r\n/g, '\n');
const a = html.indexOf('    function applySource(msg) {');
const b = html.indexOf('    // Paint the classic Clarion scheme', a);
if (a < 0 || b < 0) throw new Error('applySource not found');
const SRC = html.slice(a, b);

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}
const flush = async () => { for (let i = 0; i < 5; i++) await new Promise(r => setImmediate(r)); };

// A stand-in for everything else the function touches (DOM, helpers): any property, any call, any assignment.
const DEEP = new Proxy(function () { }, {
    get: (t, k) => (k === Symbol.toPrimitive ? () => '' : DEEP),
    apply: () => DEEP,
    set: () => true,
});

function load() {
    const fetches = [];                              // {url, resolve, reject}
    const loaded = [];                               // every model.setValue text, in order
    const calls = [];                                // named helper calls, in order
    const model = { setValue(t) { loaded.push(t); } };
    const env = {
        editor: { getModel: () => model, setValue(t) { loaded.push('editor:' + t); } },
        fetch(url) { return new Promise((resolve, reject) => fetches.push({ url, resolve: (t) => resolve({ text: () => t }), reject })); },
        resetBufferSync() { calls.push('resetBufferSync'); },
        resetDiagnosticsForNewSource() { calls.push('resetDiagnostics'); },
        resetLocalFirstState() { calls.push('resetLocalFirstState'); },
        applyEmbedDecorations() { calls.push('decorations'); },
        guarding: false, pendingRestoreSlots: null,
    };
    const declared = new Set([...SRC.matchAll(/\b(?:function|var)\s+([A-Za-z_$][\w$]*)/g)].map(m => m[1]));
    const scope = new Proxy(env, {
        has: (t, k) => typeof k === 'string' && !declared.has(k) && k !== 'Promise' && k !== 'JSON' && k !== 'String',
        get: (t, k) => (k === Symbol.unscopables ? undefined : (k in t ? t[k] : DEEP)),
        set: (t, k, v) => { t[k] = v; return true; },
    });
    // eslint-disable-next-line no-new-func
    const applySource = new Function('__scope', 'with (__scope) {\n' + SRC + '\nreturn applySource;\n}')(scope);
    return { applySource, fetches, loaded, calls, env };
}

async function main() {
    console.log('\nF1: two setSources, fetches finishing out of order');
    {
        const p = load();
        p.applySource({ sourceUrl: 'https://src/A', editableRanges: [[1, 2]] });
        p.applySource({ sourceUrl: 'https://src/B', editableRanges: [[5, 9]] });
        check('both fetches started', p.fetches.length === 2);
        p.fetches[1].resolve('TEXT B');                  // the newer one finishes first...
        await flush();
        p.fetches[0].resolve('TEXT A');                  // ...then the older one
        await flush();
        check('F1 only the newest text is loaded', JSON.stringify(p.loaded) === '["TEXT B"]', JSON.stringify(p.loaded));
        check('F1 ...and the stale one resets nothing', p.calls.filter(c => c === 'resetBufferSync').length === 1, JSON.stringify(p.calls));
        check('F1 the ranges are the newest setSource\'s', JSON.stringify(p.env.embedRanges) === '[[5,9]]');
    }
    {
        const p = load();
        p.applySource({ sourceUrl: 'https://src/A' });
        p.applySource({ sourceUrl: 'https://src/B' });
        p.fetches[0].resolve('TEXT A');                  // in order
        await flush();
        p.fetches[1].resolve('TEXT B');
        await flush();
        check('F1 in order: the older one is already superseded; only the newest loads', JSON.stringify(p.loaded) === '["TEXT B"]',
            JSON.stringify(p.loaded));
    }
    {
        const p = load();
        p.applySource({ sourceUrl: 'https://src/A' });
        p.applySource({ sourceUrl: 'https://src/B' });
        p.fetches[1].resolve('TEXT B');
        await flush();
        p.fetches[0].reject(new Error('gone'));          // the stale one fails late
        await flush();
        check('F1 a stale fetch that fails does not overwrite the newest text with an error', JSON.stringify(p.loaded) === '["TEXT B"]',
            JSON.stringify(p.loaded));
    }
    {
        const p = load();
        p.applySource({ sourceUrl: 'https://src/A' });
        p.fetches[0].resolve('TEXT A');
        await flush();
        check('a single setSource loads as before', JSON.stringify(p.loaded) === '["TEXT A"]' &&
            p.calls.join() === 'resetBufferSync,resetDiagnostics,resetLocalFirstState,decorations', p.calls.join());
    }
    console.log('\n' + pass + ' passed, ' + fail + ' failed');
    process.exit(fail ? 1 : 0);
}
main().catch(err => { console.error(err && err.stack || err); process.exit(1); });
