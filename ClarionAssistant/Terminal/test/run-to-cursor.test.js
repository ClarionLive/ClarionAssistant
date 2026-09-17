// run-to-cursor.test.js — guards the Monaco "Run to Cursor" context-menu item (task 2484592b).
//
// Run:  node Terminal/test/run-to-cursor.test.js
//
// Zero-dependency. EXTRACTS the page section from monaco-embeditor.html and evaluates it against editor fakes
// that record addAction descriptors and context keys. The host half is pinned by source checks here and by
// Terminal/test/DebuggerBridgeCheck.cs (the reflection binding, compiled against the real bridge).
//
// What is pinned:
//   * the item exists only while the debugger is available AND paused (Monaco 0.52.2's addAction uses
//     precondition as menu visibility — see the page comment), and a host that never sends debuggerState
//     (the CA Embeditor) never shows it
//   * run() posts runToCursor WITH the caret position, and nothing when not paused
//   * state pushes reach both split panes
//   * host: the mirrored cursor is updated BEFORE the debugger is called, on the UI thread

const fs = require('fs');
const path = require('path');

const TERMINAL = path.join(__dirname, '..');
const HTML_PATH = process.argv[2] || path.join(TERMINAL, 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');
const editorCs = fs.readFileSync(path.join(TERMINAL, '..', 'MonacoClarionSourceEditor.cs'), 'utf8');
const bridgeCs = fs.readFileSync(path.join(TERMINAL, '..', 'Services', 'ClarionDebuggerBridge.cs'), 'utf8');
const csproj = fs.readFileSync(path.join(TERMINAL, '..', 'ClarionAssistant.csproj'), 'utf8');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

const rtcSrc = slice(html,
    '// ----- Debugger "Run to Cursor" (task 2484592b; CA Editor source overlay) -----',
    '    // ----- As-you-type keyword casing -----', 'run-to-cursor section');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- fakes ----------
function makeEditor(line, column) {
    const keys = {};
    const actions = [];
    let pos = { lineNumber: line, column: column };
    return {
        _keys: keys, _actions: actions,
        createContextKey(name, def) { const k = { name: name, value: def, set(v) { this.value = v; } }; keys[name] = k; return k; },
        addAction(d) { actions.push(d); return { dispose() { } }; },
        getPosition() { return pos; },
        setPosition(p) { pos = p; }
    };
}
// Monaco's context-key expression for this item: a plain conjunction of key names.
function evalWhen(expr, keys) {
    return expr.split('&&').map(s => s.trim()).every(k => !!(keys[k] && keys[k].value));
}

function makeEnv() {
    const posts = [];
    const ed1 = makeEditor(10, 3);
    const ed2 = makeEditor(20, 1);
    const env = new Function('postToHost', 'editor', 'editor2', `
        ${rtcSrc}
        return { addRunToCursorAction: addRunToCursorAction, setDebuggerState: setDebuggerState,
                 requestRunToCursor: requestRunToCursor, state: function () { return debuggerState; } };`)(
        o => posts.push(o), ed1, ed2);
    env.posts = posts; env.ed1 = ed1; env.ed2 = ed2;
    return env;
}
function visible(ed) {
    const a = ed._actions.find(x => x.id === 'ca-debugger-run-to-cursor');
    return !!a && evalWhen(a.precondition, ed._keys);
}

// ---------- registration ----------
section('action registration');
{
    const env = makeEnv();
    env.addRunToCursorAction(env.ed1);
    env.addRunToCursorAction(env.ed1);      // idempotent (split re-creation paths call it again)
    const acts = env.ed1._actions.filter(a => a.id === 'ca-debugger-run-to-cursor');
    check('one action registered (idempotent)', acts.length === 1, 'got ' + acts.length);
    const a = acts[0] || {};
    check('label is "Run to Cursor"', a.label === 'Run to Cursor');
    check('in the context menu', !!a.contextMenuGroupId);
    check('gated on both caDebuggerAvailable and caDebuggerPaused',
        /caDebuggerAvailable/.test(a.precondition || '') && /caDebuggerPaused/.test(a.precondition || ''));
    check('no keybinding (IDE key ownership unverified)', a.keybindings === undefined);
    check('hidden by default (no debuggerState ever sent — e.g. CA Embeditor)', !visible(env.ed1));
}

// ---------- gating ----------
section('gating from debuggerState');
{
    const env = makeEnv();
    env.addRunToCursorAction(env.ed1);
    env.addRunToCursorAction(env.ed2);
    env.setDebuggerState({ available: true, paused: false });
    check('available but running: hidden', !visible(env.ed1));
    env.setDebuggerState({ available: true, paused: true });
    check('paused: visible in pane 1', visible(env.ed1));
    check('paused: visible in split pane 2', visible(env.ed2));
    env.setDebuggerState({ available: false, paused: true });
    check('paused without available is treated as not paused', !visible(env.ed1) && env.state().paused === false);
    env.setDebuggerState({ available: true, paused: true });
    env.setDebuggerState({ available: false, paused: false });
    check('debugger unloaded: hidden again', !visible(env.ed1) && !visible(env.ed2));
}
{
    const env = makeEnv();
    env.setDebuggerState({ available: true, paused: true });   // state arrives before the editor exists
    env.addRunToCursorAction(env.ed1);
    check('keys created later are seeded from the current state', visible(env.ed1));
}

// ---------- run ----------
section('run');
{
    const env = makeEnv();
    env.addRunToCursorAction(env.ed1);
    env.setDebuggerState({ available: true, paused: true });
    env.ed1.setPosition({ lineNumber: 42, column: 7 });      // where Monaco put the caret on right-click
    env.ed1._actions[0].run(env.ed1);
    check('posts exactly one message', env.posts.length === 1, 'got ' + env.posts.length);
    const m = env.posts[0] || {};
    check('action is runToCursor', m.action === 'runToCursor');
    check('carries the caret line and column', m.line === 42 && m.column === 7, JSON.stringify(m));
}
{
    const env = makeEnv();
    env.addRunToCursorAction(env.ed1);
    env.setDebuggerState({ available: true, paused: false });
    env.ed1._actions[0].run(env.ed1);                         // e.g. a stale menu while the debugger resumed
    check('not paused: nothing posted', env.posts.length === 0);
}
{
    const env = makeEnv();
    env.addRunToCursorAction(env.ed2);
    env.setDebuggerState({ available: true, paused: true });
    env.ed2._actions[0].run(env.ed2);
    check('split pane posts ITS caret', env.posts.length === 1 && env.posts[0].line === 20);
}

// ---------- page wiring ----------
section('page wiring');
check('router handles debuggerState', /msg\.type === 'debuggerState'\)\s*\{\s*setDebuggerState\(msg\)/.test(html));
// Anchored at line start so a commented-out call ("//addRunToCursorAction(...)") does not count.
check('added to the main editor', /^[ \t]*addRunToCursorAction\(editor\);/m.test(html));
check('added to the split editor', /^[ \t]*addRunToCursorAction\(editor2\);/m.test(html));

// ---------- host ----------
section('host (C#)');
check('OnUnknownAction routes runToCursor', /action == "runToCursor"\)\s*\{\s*RunToCursorFromPage\(rawJson\); return; \}/.test(editorCs));
{
    const body = slice(editorCs, 'private void RunToCursorFromPage(string rawJson)', 'private void EnsureDebuggerStatePoll()', 'RunToCursorFromPage');
    const iCursor = body.indexOf('_lastCursorLine = line;');
    const iCall = body.indexOf('ClarionDebuggerBridge.RunToCursor()');
    check('mirrored cursor updated BEFORE the debugger pulls it', iCursor >= 0 && iCall > iCursor);
    check('marshals onto the UI thread when needed', /InvokeRequired\) form\.BeginInvoke\(run\)/.test(body));
    check('activates this tab if it is not the active window', /SelectWindow/.test(body));
}
check('OnReady starts the state poll / sends the current state', /EnsureDebuggerStatePoll\(\);/.test(slice(editorCs, 'void IMonacoEditorHost.OnReady(', 'public bool TryInsertReferenceAtPoint', 'OnReady')));
check('bridge binds ClarionDebugger.DebugSessionController', /"ClarionDebugger\.DebugSessionController"/.test(bridgeCs));
check('bridge requires BOTH State and parameterless RunToCursor', /GetProperty\("State"/.test(bridgeCs) && /GetMethod\("RunToCursor",[^;]*Type\.EmptyTypes/.test(bridgeCs) && /state == null \|\| run == null/.test(bridgeCs));
check('bridge compares State by name "Paused"', /ToString\(\), "Paused"/.test(bridgeCs));
check('bridge compiled into the addin', /<Compile Include="Services\\ClarionDebuggerBridge\.cs" \/>/.test(csproj));

// ---------- summary ----------
console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) {
    console.log('\nFailures:');
    failures.forEach(f => console.log('  - ' + f));
    process.exit(1);
}
