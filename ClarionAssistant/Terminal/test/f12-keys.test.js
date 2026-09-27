// f12-keys.test.js - guards the CA Editor's F12 family in the capture-phase key interceptor (77aceec5).
//
// Run:  node Terminal/test/f12-keys.test.js
//
// Zero-dependency. EXTRACTS installFindKeyInterceptor from monaco-embeditor.html and evaluates it against
// fakes, then fires keydown events at the listener it registers.
//
// The defect: the F12 branch matched keyCode 123 regardless of Shift, so Shift+F12 - "Find All References"
// in VS Code and Visual Studio - silently did GO TO DEFINITION. The editor has no references UI, so the
// right behaviour is for Shift+F12 to do NOTHING here (it falls through to Monaco, whose own references
// action is inert without a reference provider) rather than jump somewhere the user did not ask for.
//
// What is pinned:
//   * F12        -> definition request at the caret (unchanged)
//   * Ctrl+F12   -> implementation request (unchanged)
//   * Shift+F12  -> NO host request, and the event is NOT swallowed

const fs = require('fs');
const path = require('path');

const TERMINAL = path.join(__dirname, '..');
const HTML_PATH = process.argv[2] || path.join(TERMINAL, 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

const src = slice(html, '    function installFindKeyInterceptor() {',
    '    // ----- Code Snippets picker', 'installFindKeyInterceptor');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' - ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

function harness() {
    const listeners = [];
    const requests = [];
    const document = { addEventListener(type, fn, capture) { listeners.push({ type, fn, capture }); } };
    const editor = {
        getPosition() { return { lineNumber: 7, column: 3 }; },
        getModel() { return { getValue() { return 'buffer'; } }; }
    };
    const env = {
        document, editor,
        capturingKey: false, snippetPickerOpen: false, findUiMode: 'panel',
        openFindAll() { }, postCaFindOpen() { }, openFindUi() { }, gotoMatch() { },
        recordNavAt() { },
        // 16d140e9: requests name the synced buffer version instead of carrying the buffer.
        withBuffer(model, payload) { return Object.assign({ v: 1 }, payload); },
        requestFromHost(kind, payload) { requests.push({ kind, payload }); }
    };
    const names = Object.keys(env);
    // eslint-disable-next-line no-new-func
    const factory = new Function(...names, src + '\nreturn installFindKeyInterceptor;');
    const install = factory(...names.map(n => env[n]));
    install();
    return { listeners, requests };
}

function fire(h, init) {
    const ev = Object.assign({ ctrlKey: false, metaKey: false, shiftKey: false, altKey: false, keyCode: 0, key: '' }, init);
    ev.prevented = false; ev.stopped = false;
    ev.preventDefault = function () { this.prevented = true; };
    ev.stopImmediatePropagation = function () { this.stopped = true; };
    h.listeners.filter(l => l.type === 'keydown').forEach(l => l.fn(ev));
    return ev;
}

console.log('\nF12 family in the CA Editor key interceptor');
{
    const h = harness();
    check('one capture-phase keydown listener registered',
        h.listeners.length === 1 && h.listeners[0].type === 'keydown' && h.listeners[0].capture === true);

    let ev = fire(h, { keyCode: 123, key: 'F12' });
    check('F12 requests definition at the caret',
        h.requests.length === 1 && h.requests[0].kind === 'definition' && h.requests[0].payload.line === 7,
        JSON.stringify(h.requests));
    check('F12 is swallowed (Monaco does not also act on it)', ev.prevented && ev.stopped);

    h.requests.length = 0;
    ev = fire(h, { keyCode: 123, key: 'F12', ctrlKey: true });
    check('Ctrl+F12 requests implementation',
        h.requests.length === 1 && h.requests[0].kind === 'implementation', JSON.stringify(h.requests));

    h.requests.length = 0;
    ev = fire(h, { keyCode: 123, key: 'F12', shiftKey: true });
    check('Shift+F12 sends NO host request (it used to go to definition)',
        h.requests.length === 0, JSON.stringify(h.requests));
    check('Shift+F12 is not swallowed', !ev.prevented && !ev.stopped);
}

console.log('');
if (fail === 0) { console.log('PASS - ' + pass + ' checks'); process.exit(0); }
console.log('FAIL - ' + fail + ' of ' + (pass + fail) + ' checks:');
failures.forEach(f => console.log('  - ' + f));
process.exit(1);
