// run-to-cursor.test.js — guards the Monaco "Run to Cursor" context-menu item (task 2484592b).
//
// Run:  node Terminal/test/run-to-cursor.test.js
//
// Zero-dependency. EXTRACTS the page section from monaco-embeditor.html and evaluates it against editor fakes
// that record addAction descriptors and context keys. The host half is pinned by source checks here and by two
// C# harnesses run from Terminal/test/run-debugger-host-checks.ps1 — DebuggerBridgeCheck.cs (the reflection
// binding, in four real assembly scenarios) and DebuggerHookGuardsCheck.cs (the line and marker guards).
// A source check here says the host CALLS the right guard; the harnesses say the guard is RIGHT.
//
// What is pinned:
//   * the item exists only while the debugger is available AND paused (Monaco 0.52.2's addAction uses
//     precondition as menu visibility — see the page comment), and a host that never sends debuggerState
//     (the CA Embeditor) never shows it
//   * run() posts runToCursor WITH the caret position, and nothing when not paused
//   * right-click inside a multi-line selection sends the CARET, which is a decision, not an oversight
//     (f022fb4e item 6 — reviewed and kept; the comment above requestRunToCursor is the record of it)
//   * state pushes reach both split panes
//   * host: the mirrored cursor is updated BEFORE the debugger is called, on the UI thread, and only for a
//     line that exists in the document (f022fb4e item 2)
//   * host: the bridge binds by assembly identity, and refuses two candidates (f022fb4e item 1)

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
    let sel = null;
    return {
        _keys: keys, _actions: actions,
        createContextKey(name, def) { const k = { name: name, value: def, set(v) { this.value = v; } }; keys[name] = k; return k; },
        addAction(d) { actions.push(d); return { dispose() { } }; },
        getPosition() { return pos; },
        setPosition(p) { pos = p; },
        // Present so a run() that reached for the selection instead of the caret would find something to
        // reach for — the multi-line-selection case below would otherwise pass by accident.
        getSelection() { return sel || { startLineNumber: pos.lineNumber, endLineNumber: pos.lineNumber }; },
        setSelection(s) { sel = s; }
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
{
    // f022fb4e item 6, reviewed and KEPT: right-clicking inside a multi-line selection is the one case where
    // Monaco leaves the caret alone, so the run goes to the caret (the selection's active end) rather than to
    // the line under the pointer. Moving the caret ourselves would destroy the selection as a side effect of
    // a menu click. This pins the decision — anything that reads the selection's start instead fails here.
    const env = makeEnv();
    env.addRunToCursorAction(env.ed1);
    env.setDebuggerState({ available: true, paused: true });
    env.ed1.setPosition({ lineNumber: 30, column: 1 });                       // caret = the selection's end
    env.ed1.setSelection({ startLineNumber: 25, endLineNumber: 30 });         // dragged 25..30, right-clicked inside
    env.ed1._actions[0].run(env.ed1);
    check('right-click inside a selection runs to the caret, not the selection start',
        env.posts.length === 1 && env.posts[0].line === 30, JSON.stringify(env.posts));
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

    // Not-active path (pipeline run 1): if SelectWindow did not make this tab active, the debugger would pull
    // ANOTHER tab's cursor. The host must re-read the active window AFTER SelectWindow and bail out — before
    // touching the mirrored cursor and before calling the bridge.
    const iSelect = body.indexOf('GetMethod("SelectWindow"');
    const guard = /if \(myWin == null \|\| !ReferenceEquals\(myWin, ReflectProp\(wb, "ActiveWorkbenchWindow"\)\)\)\s*\{[^}]*return;\s*\}/.exec(body);
    const iGuard = guard ? guard.index : -1;
    check('re-checks the active window AFTER SelectWindow', iSelect >= 0 && iGuard > iSelect);
    check('not active (or window unknown): returns without running', !!guard);
    check('not active: logs why', !!guard && /MonacoSpikeLog\.Write\("runToCursor: NOT sent/.test(guard[0]));
    check('mirrored cursor is set only AFTER the active check', iGuard >= 0 && iCursor > iGuard);
    check('bridge is called only AFTER the active check', iGuard >= 0 && iCall > iGuard);
    check('reuses the hooked _wbWindow before reflection (like OnFocusEditor)', /object myWin = _wbWindow;\s*if \(myWin == null\)/.test(body));

    // f022fb4e item 2: the page supplies the line, so a line that cannot exist must not reach the mirrored
    // cursor (which the debugger reads back, and which is persisted as this file's saved cursor position).
    // DocumentLineGuard.Contains is executed by Terminal/test/DebuggerHookGuardsCheck.cs; what is pinned
    // here is that the host consults it, and does so before anything is written or sent.
    const iRange = body.indexOf('DocumentLineGuard.Contains(line,');
    check('refuses a line that is not in the document', iRange >= 0);
    check('...asking the page\'s live mirror first, the native document second',
        /DocumentLineGuard\.Contains\(line, live, nativeLines\)/.test(body)
        && /string live = _overlayLiveText;/.test(body) && /int nativeLines = NativeLineCount\(\);/.test(body));
    check('...and says so in the log', /runToCursor: NOT sent - line " \+ line \+ " is past the end of/.test(body));
    check('...before the mirrored cursor is written', iRange >= 0 && iCursor > iRange);
    check('...before the bridge is called', iRange >= 0 && iCall > iRange);
    check('the low end is still rejected too', /if \(line < 1 \|\| string\.IsNullOrEmpty\(_filePath\)\) return;/.test(body));
}
check('OnReady starts the state poll / sends the current state', /EnsureDebuggerStatePoll\(\);/.test(slice(editorCs, 'void IMonacoEditorHost.OnReady(', 'public bool TryInsertReferenceAtPoint', 'OnReady')));
check('bridge binds ClarionDebugger.DebugSessionController', /"ClarionDebugger\.DebugSessionController"/.test(bridgeCs));
check('bridge requires BOTH State and parameterless RunToCursor', /GetProperty\("State"/.test(bridgeCs) && /GetMethod\("RunToCursor",[^;]*Type\.EmptyTypes/.test(bridgeCs) && /state == null \|\| run == null/.test(bridgeCs));
check('bridge compares State by name "Paused"', /ToString\(\), "Paused"/.test(bridgeCs));
check('bridge compiled into the addin', /<Compile Include="Services\\ClarionDebuggerBridge\.cs" \/>/.test(csproj));
// f022fb4e item 1. The four behavioural scenarios (installed / absent / foreign assembly / two candidates)
// are in DebuggerBridgeCheck.cs, which compiles this same source into real, differently-named assemblies.
check('bridge takes the assembly name as part of the contract', /ControllerAssemblyName = "ClarionDebugger"/.test(bridgeCs));
check('bridge filters candidates by assembly name before looking for the type',
    /asm\.GetName\(\)\.Name[\s\S]{0,200}ControllerAssemblyName[\s\S]{0,200}asm\.GetType\(ControllerTypeName/.test(bridgeCs));
check('bridge refuses anything other than exactly one candidate', /if \(candidates != 1\) return false;/.test(bridgeCs));
check('bridge logs an ambiguity rather than binding silently', /refusing to guess/.test(bridgeCs));
// f022fb4e item 3. The behavioural half — a debugger assembly loading mid-session binds within the rescan
// interval, and a duplicate loading after that does NOT un-bind — is the "late" scenario in
// DebuggerBridgeCheck.cs. What is pinned here is the policy the scenario cannot see: that the poll behind
// the hook backs off and is capped, and that the handler takes no decision of its own.
{
    const bind = slice(bridgeCs, 'private static bool Bind()', '/// <summary>One scan.', 'Bind');
    check('a rescan is forced by the AssemblyLoad hook, ahead of the cooldown',
        /bool forced = _rescanNow;[\s\S]{0,200}else if \(DateTime\.UtcNow < _nextScanUtc\) return false;/.test(bind));
    check('a forced rescan also resets the backoff', /if \(forced\) \{ _rescanNow = false; _rescanMs = InitialRescanMs; \}/.test(bind));
    check('a fruitless scan waits longer next time, up to a cap',
        /_rescanMs = _rescanMs < MaxRescanMs \/ 2 \? _rescanMs \* 2 : MaxRescanMs;/.test(bind));
    check('the backoff is bounded (cap is a finite constant)', /private const int MaxRescanMs = \d+;/.test(bridgeCs));
    check('once bound, Bind never scans again', /if \(_bound\) return true;/.test(bind));
}
{
    const hook = slice(bridgeCs, 'private static void OnAssemblyLoad(', 'private static bool TryBind()', 'OnAssemblyLoad');
    check('the handler only fires for an assembly named ClarionDebugger',
        /if \(!string\.Equals\(name, ControllerAssemblyName, StringComparison\.OrdinalIgnoreCase\)\) return;/.test(hook));
    check('the handler does no reflection into the type and binds nothing itself',
        !/GetType\(|GetProperty\(|GetMethod\(|Invoke\(/.test(hook) && /_rescanNow = true;/.test(hook));
    check('a duplicate arriving after the bind does not un-bind, and is logged once',
        /if \(_bound\)[\s\S]{0,600}_lateDuplicateLogged = true;[\s\S]{0,400}return;/.test(hook)
        && !/_bound = false/.test(bridgeCs));
    check('cross-thread flags are volatile', /private static volatile bool _bound;/.test(bridgeCs)
        && /private static volatile bool _rescanNow;/.test(bridgeCs));
}
{
    const bind = slice(bridgeCs, 'private static bool TryBind()', '/// <summary>Is the CA Debugger loaded', 'TryBind');
    const gates = bind.match(/if \(\w+ == null \|\| \w+ == null\) return false;/g) || [];
    check('exactly ONE required-member gate, over State and RunToCursor — nothing else became mandatory',
        gates.length === 1 && /var state = controller\.GetProperty\("State"/.test(bind)
        && /var run = controller\.GetMethod\("RunToCursor"/.test(bind), gates.join(' | '));
    check('an optional-member seam is left after it (e61e4f92 Break on entry)', /SEAM for optional members/.test(bind));
}

// ---------- summary ----------
console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) {
    console.log('\nFailures:');
    failures.forEach(f => console.log('  - ' + f));
    process.exit(1);
}
