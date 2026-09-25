// break-on-entry.test.js — guards the Monaco "Break on Entry" context-menu item (task e61e4f92).
//
// Run:  node Terminal/test/break-on-entry.test.js
//
// Zero-dependency, built like run-to-cursor.test.js: it EXTRACTS the debugger section from monaco-embeditor.html
// and evaluates it against editor fakes that record addAction descriptors and context keys. The host half is
// pinned by source checks here; the reflection binding itself is executed by DebuggerBridgeCheck.cs (the
// "bound", "boevoid" and "boenone" scenarios of Terminal/test/run-debugger-host-checks.ps1).
//
// What is pinned:
//   * the item is shown whenever the host says the loaded debugger HAS BreakOnProcEntry, in ANY debugger state
//     (owner decision 5, 2026-09-24) - not the paused-only gate Run to Cursor uses - and never otherwise
//   * run() posts breakOnProcEntry with the caret position, and nothing when the item should not be there
//   * Run to Cursor's gating is unchanged by the new state member
//   * host: the same line guard and active-tab verification as Run to Cursor, BEFORE the debugger is called;
//     a miss is toasted in this tab and a hit is not; the toast carries the debugger's text LAST

const fs = require('fs');
const path = require('path');

const TERMINAL = path.join(__dirname, '..');
const HTML_PATH = process.argv[2] || path.join(TERMINAL, 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');
const editorCs = fs.readFileSync(path.join(TERMINAL, '..', 'MonacoClarionSourceEditor.cs'), 'utf8');
const bridgeCs = fs.readFileSync(path.join(TERMINAL, '..', 'Services', 'ClarionDebuggerBridge.cs'), 'utf8');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

// The whole debugger section: Run to Cursor's state and setDebuggerState live there, and Break on Entry
// shares both.
const dbgSrc = slice(html,
    '// ----- Debugger "Run to Cursor" (task 2484592b; CA Editor source overlay) -----',
    '    // ----- As-you-type keyword casing -----', 'debugger section');

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
// Monaco's context-key expression for these items: a plain conjunction of key names.
function evalWhen(expr, keys) {
    return expr.split('&&').map(s => s.trim()).every(k => !!(keys[k] && keys[k].value));
}
function makeEnv() {
    const posts = [];
    const ed1 = makeEditor(10, 3);
    const ed2 = makeEditor(20, 1);
    const env = new Function('postToHost', 'editor', 'editor2', `
        ${dbgSrc}
        return { addRunToCursorAction: addRunToCursorAction, addBreakOnEntryAction: addBreakOnEntryAction,
                 setDebuggerState: setDebuggerState, state: function () { return debuggerState; } };`)(
        o => posts.push(o), ed1, ed2);
    env.posts = posts; env.ed1 = ed1; env.ed2 = ed2;
    return env;
}
function action(ed, id) { return ed._actions.find(x => x.id === id); }
function visible(ed, id) { const a = action(ed, id); return !!a && evalWhen(a.precondition, ed._keys); }
const BOE = 'ca-debugger-break-on-entry';
const RTC = 'ca-debugger-run-to-cursor';

// ---------- registration ----------
section('action registration');
{
    const env = makeEnv();
    env.addBreakOnEntryAction(env.ed1);
    env.addBreakOnEntryAction(env.ed1);      // idempotent (split re-creation paths call it again)
    const acts = env.ed1._actions.filter(a => a.id === BOE);
    check('one action registered (idempotent)', acts.length === 1, 'got ' + acts.length);
    const a = acts[0] || {};
    check('label is "Break on Entry"', a.label === 'Break on Entry');
    check('in the context menu', !!a.contextMenuGroupId);
    check('gated on caDebuggerBreakOnEntry ALONE - no paused key', a.precondition === 'caDebuggerBreakOnEntry', a.precondition);
    check('no keybinding (IDE key ownership unverified)', a.keybindings === undefined);
    check('hidden by default (no debuggerState ever sent — e.g. CA Embeditor)', !visible(env.ed1, BOE));
}

// ---------- gating ----------
section('gating from debuggerState: every state, but only a debugger that has the member');
{
    const env = makeEnv();
    env.addBreakOnEntryAction(env.ed1); env.addBreakOnEntryAction(env.ed2);
    env.addRunToCursorAction(env.ed1);
    env.setDebuggerState({ available: true, paused: false, breakOnEntry: true });
    check('debugger loaded, NOT paused (idle or running): visible', visible(env.ed1, BOE));
    check('...in the split pane too', visible(env.ed2, BOE));
    check('...while Run to Cursor stays hidden, as before', !visible(env.ed1, RTC));
    env.setDebuggerState({ available: true, paused: true, breakOnEntry: true });
    check('paused: visible, beside Run to Cursor', visible(env.ed1, BOE) && visible(env.ed1, RTC));
    env.setDebuggerState({ available: true, paused: true, breakOnEntry: false });
    check('a debugger WITHOUT the member (older build): hidden', !visible(env.ed1, BOE) && !visible(env.ed2, BOE));
    check('...and Run to Cursor is unaffected by that', visible(env.ed1, RTC));
    env.setDebuggerState({ available: true, paused: true });
    check('a state message with no breakOnEntry member (older host) reads as hidden', !visible(env.ed1, BOE));
    env.setDebuggerState({ available: false, paused: false, breakOnEntry: true });
    check('breakOnEntry without available is treated as off', !visible(env.ed1, BOE) && env.state().breakOnEntry === false);
}
{
    const env = makeEnv();
    env.setDebuggerState({ available: true, paused: false, breakOnEntry: true });   // state before the editor exists
    env.addBreakOnEntryAction(env.ed1);
    check('a key created later is seeded from the current state', visible(env.ed1, BOE));
}

// ---------- run ----------
section('run');
{
    const env = makeEnv();
    env.addBreakOnEntryAction(env.ed1);
    env.setDebuggerState({ available: true, paused: false, breakOnEntry: true });
    env.ed1.setPosition({ lineNumber: 42, column: 7 });
    action(env.ed1, BOE).run(env.ed1);
    check('posts exactly one message', env.posts.length === 1, 'got ' + env.posts.length);
    const m = env.posts[0] || {};
    check('action is breakOnProcEntry, with the caret line and column',
        m.action === 'breakOnProcEntry' && m.line === 42 && m.column === 7, JSON.stringify(m));
}
{
    const env = makeEnv();
    env.addBreakOnEntryAction(env.ed2);
    env.setDebuggerState({ available: true, paused: false, breakOnEntry: true });
    action(env.ed2, BOE).run(env.ed2);
    check('split pane posts ITS caret', env.posts.length === 1 && env.posts[0].line === 20, JSON.stringify(env.posts));
}
{
    const env = makeEnv();
    env.addBreakOnEntryAction(env.ed1);
    env.setDebuggerState({ available: true, paused: true, breakOnEntry: false });
    action(env.ed1, BOE).run(env.ed1);                          // a stale menu after the state changed
    check('a debugger without the member: nothing posted', env.posts.length === 0);
}

// ---------- page wiring ----------
section('page wiring');
check('added to the main editor', /^[ \t]*addBreakOnEntryAction\(editor\);/m.test(html));
check('added to the split editor', /^[ \t]*addBreakOnEntryAction\(editor2\);/m.test(html));
check('router shows a host toast through showToast, red unless ok',
    /msg\.type === 'toast'\)\s*\{\s*showToast\(String\(msg\.message \|\| ''\), msg\.ok !== false\);/.test(html));
check('...and showToast writes TEXT, not markup', /function showToast\(message, ok, persist\) \{[\s\S]{0,120}t\.textContent = message;/.test(html));

// ---------- host ----------
section('host (C#)');
check('OnUnknownAction routes breakOnProcEntry',
    /action == "breakOnProcEntry"\)\s*\{\s*BreakOnProcEntryFromPage\(rawJson\); return; \}/.test(editorCs));
check('the state push carries breakOnEntry, and only with available',
    /",\\"breakOnEntry\\":" \+ \(available && breakOnEntry \? "true" : "false"\)/.test(editorCs));
check('the poll re-sends when breakOnEntry alone changes',
    /breakOnEntry == _debugBreakOnEntry\) return;/.test(editorCs));
{
    const body = slice(editorCs, 'private void BreakOnProcEntryFromPage(string rawJson)', '/// <summary>Start the shared debugger-state poll', 'BreakOnProcEntryFromPage');
    const iRange = body.indexOf('DocumentLineGuard.Contains(line, live, nativeLines)');
    const iSelect = body.indexOf('GetMethod("SelectWindow"');
    const guard = /if \(myWin == null \|\| !ReferenceEquals\(myWin, ReflectProp\(wb, "ActiveWorkbenchWindow"\)\)\)\s*\{[^}]*return;\s*\}/.exec(body);
    const iGuard = guard ? guard.index : -1;
    const iCursor = body.indexOf('_lastCursorLine = line;');
    const iCall = body.indexOf('ClarionDebuggerBridge.BreakOnProcEntry(filePath, line, out message)');
    check('refuses a line that is not in the document, before anything else', iRange >= 0 && iSelect > iRange && iCall > iRange);
    check('...and toasts that refusal', iRange >= 0 && /is past the end of this file - nothing was set\.", false\);/.test(body));
    check('re-checks the active window AFTER SelectWindow', iSelect >= 0 && iGuard > iSelect);
    check('not active: logs, toasts and returns without calling the debugger',
        !!guard && /MonacoSpikeLog\.Write\("breakOnProcEntry: NOT sent/.test(guard[0]) && /ToastInPage\(/.test(guard[0]));
    check('the caret is set only AFTER the active check', iGuard >= 0 && iCursor > iGuard);
    check('the debugger is called only AFTER the active check, with this tab\'s file and the line', iGuard >= 0 && iCall > iGuard);
    check('a miss toasts the debugger\'s message, or CA\'s own when it gave none',
        /if \(!ok\)\s*ToastInPage\(string\.IsNullOrEmpty\(message\) \? Services\.ClarionDebuggerBridge\.BreakOnEntryNoAnswer : message, false\);/.test(body));
    check('a hit toasts nothing (the pad reports it): after the call, only the miss and the catch toast',
        (body.slice(iCall).match(/ToastInPage\(/g) || []).length === 2 /* the miss, and the catch */, (body.slice(iCall).match(/ToastInPage\(/g) || []).length + ' after the call');
    check('marshals onto the UI thread when needed', /InvokeRequired\) form\.BeginInvoke\(run\)/.test(body));
}
{
    const toast = slice(editorCs, 'private void ToastInPage(string message, bool ok)', 'private void BreakOnProcEntryFromPage', 'ToastInPage');
    const iType = toast.indexOf('d["type"] = "toast";'), iOk = toast.indexOf('d["ok"] = ok;'), iMsg = toast.indexOf('d["message"] = message ?? "";');
    check('ToastInPage serializes the debugger\'s text LAST, after type and ok', iType >= 0 && iOk > iType && iMsg > iOk);
    check('...through the serializer, not by concatenation', /new JavaScriptSerializer\(\)\.Serialize\(d\)/.test(toast));
}
{
    const bind = slice(bridgeCs, 'private static bool TryBind()', '/// <summary>Is the CA Debugger loaded', 'TryBind');
    check('the bridge binds BreakOnProcEntry by exact signature (string, int, out string)',
        /GetMethod\("BreakOnProcEntry", BindingFlags\.Public \| BindingFlags\.Static, null,\s*new\[\] \{ typeof\(string\), typeof\(int\), typeof\(string\)\.MakeByRefType\(\) \}, null\)/.test(bind));
    check('...and only with a bool return', /if \(boe != null && boe\.ReturnType != typeof\(bool\)\) boe = null;/.test(bind));
    const gates = bind.match(/if \(\w+ == null \|\| \w+ == null\) return false;/g) || [];
    check('...OPTIONALLY: still exactly one required-member gate, over State and RunToCursor', gates.length === 1, gates.join(' | '));
}

// ---------- summary ----------
console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) {
    console.log('\nFailures:');
    failures.forEach(f => console.log('  - ' + f));
    process.exit(1);
}
