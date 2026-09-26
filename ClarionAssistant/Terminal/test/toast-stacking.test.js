// toast-stacking.test.js — a plain toast never replaces an actionable one (task 3517fd15 item 5).
//
// Run:  node Terminal/test/toast-stacking.test.js
//
// Zero-dependency (no jsdom), built like its neighbours: it EXTRACTS showToast, hideToastAux, showActionToast
// and the Escape handler from monaco-embeditor.html and runs them against a small fake DOM with manual timers.
// The host's {type:'toast'} route is extracted too and executed as written.
//
// Why: the "file changed on disk" notice (showActionToast: Reload / Show diff / Close tab) never auto-dismisses,
// because losing it leaves a stale tab with no sign it diverged from disk. showToast used to write the same
// #toast element, so any plain toast - a Break on Entry miss from the host, an LSP notice - wiped the notice
// and its buttons. What is pinned:
//   * with an action toast up, a plain toast goes to #toastAux, raised above it; the notice, its text and its
//     SAME button nodes survive, and the buttons still work
//   * the plain toast's timer hides only #toastAux, never the notice
//   * with no action toast up, a plain toast uses #toast exactly as before and #toastAux stays hidden
//   * only the newest plain toast is visible: one in #toast hides a leftover in #toastAux
//   * the host's toast route reaches the same showToast, so a Break on Entry miss behaves the same way

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

const toastSrc = slice(html,
    '    // A plain toast never replaces an actionable one',
    '    // Shared "Show diff" toast action', 'toast section');
const routeMatch = /msg\.type === 'toast'\)\s*\{\s*([^\n]*?;)/.exec(html);
if (!routeMatch) throw new Error('could not find the host toast route');
const routeSrc = routeMatch[1];

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- fake DOM ----------
function makeElement(tag) {
    const el = {
        tagName: tag, children: [], style: {}, className: '', offsetHeight: 0, _text: '', _listeners: {},
        get textContent() { return this._text + this.children.map(c => c.textContent).join(''); },
        set textContent(v) { this._text = String(v); this.children = []; },
        appendChild(c) { this.children.push(c); return c; },
        addEventListener(type, fn) { (this._listeners[type] = this._listeners[type] || []).push(fn); },
        click() { (this._listeners.click || []).forEach(fn => fn({ stopPropagation() { } })); }
    };
    el.classList = { contains: n => el.className.split(/\s+/).indexOf(n) >= 0 };
    return el;
}
function makeEnv() {
    const byId = { toast: makeElement('div'), toastAux: makeElement('div') };
    const docListeners = {};
    const document = {
        getElementById: id => byId[id] || null,
        createElement: tag => makeElement(tag),
        addEventListener(type, fn) { (docListeners[type] = docListeners[type] || []).push(fn); }
    };
    let now = 0, seq = 0;
    const timers = new Map();
    const setTimeout = (fn, ms) => { const id = ++seq; timers.set(id, { fn: fn, at: now + ms }); return id; };
    const clearTimeout = id => { timers.delete(id); };
    function advance(ms) {
        now += ms;
        for (const [id, t] of [...timers].sort((a, b) => a[1].at - b[1].at)) {
            if (t.at <= now && timers.has(id)) { timers.delete(id); t.fn(); }
        }
    }
    const api = new Function('document', 'setTimeout', 'clearTimeout', `
        var toastTimer = null;
        var toastAuxTimer = null;
        ${toastSrc}
        return { showToast: showToast, showActionToast: showActionToast, hideToastAux: hideToastAux,
                 route: function (msg) { ${routeSrc} } };`)(document, setTimeout, clearTimeout);
    api.toast = byId.toast; api.aux = byId.toastAux; api.advance = advance;
    api.key = k => (docListeners.keydown || []).forEach(fn => fn({ key: k }));
    return api;
}
function buttons(el) { return el.children.filter(c => c.tagName === 'button'); }
function button(el, label) { return buttons(el).find(b => b.textContent === label); }
function shown(el) { return el.classList.contains('show'); }
// The external-change notice exactly as handleExternalFileState raises it for a content change.
function raiseNotice(env, log) {
    env.showActionToast('File changed on disk outside the editor.', false, [
        { label: 'Reload', onClick: () => log.push('reload') },
        { label: 'Show diff', dismiss: false, onClick: () => log.push('diff') }
    ]);
}

// ---------- the notice survives a plain toast ----------
section('an action toast is up, then a plain toast arrives');
{
    const env = makeEnv(), log = [];
    raiseNotice(env, log);
    env.toast.offsetHeight = 40;
    const before = buttons(env.toast);
    env.showToast('Break on entry: no procedure contains line 12.', false);
    check('the notice is still shown, actionable and red', env.toast.className === 'show actionable err', env.toast.className);
    check('...with its text', env.toast.textContent.indexOf('File changed on disk outside the editor.') === 0, env.toast.textContent);
    check('...and the SAME button nodes (Reload, Show diff, dismiss)',
        buttons(env.toast).length === 3 && buttons(env.toast).every((b, i) => b === before[i]),
        buttons(env.toast).map(b => b.textContent).join(','));
    check('the plain toast is shown in #toastAux, red', env.aux.className === 'show err' && env.aux.textContent === 'Break on entry: no procedure contains line 12.',
        env.aux.className + ' / ' + env.aux.textContent);
    check('...raised just above the notice (14 + its height + 8)', env.aux.style.bottom === '62px', env.aux.style.bottom);
    const reload = button(env.toast, 'Reload');
    if (reload) reload.click();
    check('the notice\'s buttons still work', log.join() === 'reload', reload ? log.join() : 'no Reload button');
}
{
    const env = makeEnv(), log = [];
    raiseNotice(env, log);
    env.showToast('Language server is starting', false);
    env.advance(8000);
    check('the plain toast times out in #toastAux', !shown(env.aux));
    check('...and its timer does not take the notice with it', env.toast.className === 'show actionable err', env.toast.className);
    env.advance(60000);
    check('the notice never times out', shown(env.toast));
}
{
    const env = makeEnv(), log = [];
    raiseNotice(env, log);
    env.route({ type: 'toast', ok: false, message: 'Break on entry: nothing was set.' });
    check('the host\'s toast route (a Break on Entry miss) keeps the notice too',
        env.toast.className === 'show actionable err' && buttons(env.toast).length === 3);
    check('...and shows the host\'s text in #toastAux', env.aux.textContent === 'Break on entry: nothing was set.' && shown(env.aux));
    env.key('Escape');
    check('Escape still dismisses the notice', !shown(env.toast));
}

// ---------- no notice: unchanged ----------
section('no action toast: a plain toast behaves as before');
{
    const env = makeEnv();
    env.showToast('Saved', true);
    check('it uses #toast', env.toast.className === 'show' && env.toast.textContent === 'Saved', env.toast.className);
    check('#toastAux stays hidden', !shown(env.aux));
    env.showToast('Reload discards your edits', false);
    check('a newer plain toast replaces it in #toast', env.toast.textContent === 'Reload discards your edits' && env.toast.className === 'show err');
    env.advance(8000);
    check('...and times out', !shown(env.toast));
}
{
    const env = makeEnv();
    env.showToast('Saving…', true, true);
    raiseNotice(env, []);
    check('an action toast still replaces a plain one in #toast', env.toast.className === 'show actionable err');
}

// ---------- only the newest plain toast ----------
section('only the newest plain toast is visible');
{
    const env = makeEnv();
    raiseNotice(env, []);
    env.showToast('Saving…', true, true);                // persistent, so no timer would ever clear it
    check('(setup) the persistent toast went to #toastAux', shown(env.aux));
    const dismiss = button(env.toast, '✕');
    check('(setup) the notice has its dismiss button', !!dismiss);
    if (dismiss) dismiss.click();                        // the notice is dismissed by hand
    env.showToast('Saved', true);
    check('a plain toast in #toast hides the leftover in #toastAux', !shown(env.aux));
    check('...and is itself shown', env.toast.className === 'show' && env.toast.textContent === 'Saved');
}
{
    const env = makeEnv();
    raiseNotice(env, []);
    env.showToast('first', true);                        // timed: 6s
    env.advance(3000);
    env.showToast('second', true, true);                 // persistent
    check('two plain toasts over a notice: the newer one wins #toastAux', env.aux.textContent === 'second' && env.aux.className === 'show');
    env.advance(5000);
    check('...and the older one\'s timer does not hide it', shown(env.aux));
}

// ---------- summary ----------
console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) {
    console.log('\nFailures:');
    failures.forEach(f => console.log('  - ' + f));
    process.exit(1);
}
