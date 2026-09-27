// local-first-loader.js - shared loader for the local-first completion / hover harnesses (1c685f2e items 5-6).
// Not a test itself.
//
// EXTRACTS the real code from monaco-embeditor.html at run time (requestFromHost + buffer sync + [local-rt],
// lspKindToMonaco + toMonacoCompletion, and everything from isInClarionComment through the providers,
// which holds the local-first state) and runs it against fakes:
//   * a host that parks every request until the test replies to it by action (env.reply)
//   * fake timers that fire only on command (env.fire(ms)), counted by duration
//   * a manual clock (env.clock) behind performance.now()
//   * models whose lines the test types into (bumping the version id like Monaco does)

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8').replace(/\r\n/g, '\n');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a + startMarker.length);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

const PROVIDERS_SRC = slice(html, '    function isInClarionComment(', '    // Clarion syntax highlighting (Monarch)', 'providers');
const SRC = [
    slice(html, '    function requestFromHost(', '    function lspKindToMonaco(', 'requestFromHost + buffer sync'),
    slice(html, '    function lspKindToMonaco(', '    // ----- Save -----', 'lspKindToMonaco + toMonacoCompletion'),
    PROVIDERS_SRC,
].join('\n');

const DECLARED = new Set();
for (const m of SRC.matchAll(/\bfunction\s+([A-Za-z_$][\w$]*)/g)) DECLARED.add(m[1]);
for (const m of SRC.matchAll(/\bvar\s+([A-Za-z_$][\w$]*)/g)) DECLARED.add(m[1]);
const STUB = function () { return undefined; };

function makeModel(id, lines) {
    return {
        id, _v: 1, _lines: lines.slice(),
        getVersionId() { return this._v; },
        getValue() { return this._lines.join('\r\n'); },
        getValueLength() { return this.getValue().length; },
        getLineContent(n) { return this._lines[n - 1] || ''; },
        getLineCount() { return this._lines.length; },
        getLineMaxColumn(n) { return (this._lines[n - 1] || '').length + 1; },
        // Type into line n (replace its text): a new version, like every Monaco edit.
        setLine(n, text) { this._lines[n - 1] = text; this._v++; },
    };
}

function load(opts) {
    opts = opts || {};
    const posted = [];
    const providers = { completion: [], hover: [], signatureHelp: [] };
    const timers = [];                      // {id, fn, ms, cleared, fired}
    const triggers = [];                    // editor.trigger calls (5.10)
    const env = {
        posted, providers, timers, triggers,
        pendingRequests: {}, reqSeq: 0, fileMode: false, embedRanges: [[1, 100]],
        keywordCaseMode: 'upper', commitKeysEnabled: true, snippetsList: [],
        clock: 0,
        postToHost(obj) { posted.push(JSON.parse(JSON.stringify(obj))); },
        setTimeout(fn, ms) { const t = { id: timers.length + 1, fn, ms, cleared: false, fired: false }; timers.push(t); return t.id; },
        clearTimeout(id) { const t = timers[id - 1]; if (t) t.cleared = true; },
        maybeReportLsp() { },
        isEditableRange() { return true; },
        document: { getElementById() { return null; }, addEventListener() { } },
        monaco: {
            Range: function (a, b, c, d) { this.startLineNumber = a; this.startColumn = b; this.endLineNumber = c; this.endColumn = d; },
            languages: {
                CompletionItemKind: new Proxy({}, { get: (t, k) => String(k) }),
                registerCompletionItemProvider(lang, p) { providers.completion.push(p); },
                registerHoverProvider(lang, p) { providers.hover.push(p); },
                registerSignatureHelpProvider(lang, p) { providers.signatureHelp.push(p); },
            },
            editor: { registerLinkOpener() { }, setModelMarkers() { } },
        },
    };
    env.performance = { now: () => env.clock };
    env.model = makeModel('$model1', opts.lines || ['  CODE', '    lo']);
    env.editor = {
        getModel: () => env.model,
        trigger(source, id) { triggers.push(id); },
        addCommand() { return 'cmd'; },
    };
    env.window = { chrome: { webview: { postMessage() { } } }, performance: env.performance };
    const scope = new Proxy(env, {
        has(t, k) { return typeof k === 'string' && !DECLARED.has(k) && ((k in t) || !(k in globalThis)); },
        get(t, k) { if (k === Symbol.unscopables) return undefined; return (k in t) ? t[k] : STUB; },
        set(t, k, v) { t[k] = v; return true; },
    });
    const exportsList = ['registerClarionProviders', 'resetLocalFirstState', 'resetBufferSync'];
    const ret = '{' + exportsList.map(n => n + ': ' + (DECLARED.has(n) ? n : 'undefined')).join(', ') + '}';
    // eslint-disable-next-line no-new-func
    env.api = new Function('__scope', 'with (__scope) {\n' + SRC + '\nreturn ' + ret + ';\n}')(scope);
    env.api.registerClarionProviders();

    env.requests = (action) => posted.filter(m => m.action === action);
    // Reply to the newest still-pending request of `action` (or the given message).
    env.reply = (actionOrMsg, data) => {
        const msg = typeof actionOrMsg === 'string'
            ? posted.filter(m => m.action === actionOrMsg && env.pendingRequests[m.reqId]).pop()
            : actionOrMsg;
        if (!msg) throw new Error('no pending ' + actionOrMsg);
        const r = env.pendingRequests[msg.reqId];
        delete env.pendingRequests[msg.reqId];
        r(data);
    };
    env.pending = (action) => posted.filter(m => m.action === action && env.pendingRequests[m.reqId]);
    // Fire every armed (not cleared, not fired) timer of duration `ms`.
    env.fire = (ms) => {
        let n = 0;
        for (const t of timers.slice()) if (t.ms === ms && !t.cleared && !t.fired) { t.fired = true; t.fn(); n++; }
        return n;
    };
    env.armed = (ms) => timers.filter(t => t.ms === ms).length;
    return env;
}

// Tracks a promise's settlement so a test can ask "has it resolved yet?" after a flush.
function track(p) {
    const s = { done: false, value: undefined };
    Promise.resolve(p).then(v => { s.done = true; s.value = v; });
    return s;
}
const flush = async () => { for (let i = 0; i < 5; i++) await new Promise(r => setImmediate(r)); };

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' - ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }
function finish() {
    console.log('\n' + pass + ' passed, ' + fail + ' failed');
    if (fail) { console.log('\nFailures:\n  ' + failures.join('\n  ')); process.exit(1); }
}

module.exports = { html, PROVIDERS_SRC, load, makeModel, track, flush, check, section, finish };
