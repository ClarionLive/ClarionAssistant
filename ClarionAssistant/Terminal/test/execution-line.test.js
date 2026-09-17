// execution-line.test.js — guards the debugger execution-line marker (CA-Debugger GitHub #26, ticket e6721573).
//
// Run:  node Terminal/test/execution-line.test.js
//
// Zero-dependency (no jsdom). Like its neighbours it EXTRACTS the page's code from monaco-embeditor.html at
// run time rather than copying it, and evaluates it against a small decoration-tracking model fake.
//
// What is pinned here:
//   * the marker is a lineNumberClassName decoration ONLY — never className / isWholeLine (a full-line
//     background reads as a selection; we committed to the gutter treatment on the issue)
//   * one marker: setting moves it, 0 clears it, out-of-range lines paint nothing
//   * reassert (tab re-activation) keeps a still-present marker where decoration tracking moved it
//   * a marker that arrives while setSource's async fetch is running survives the content load
//   * the host plumbing the page depends on: the frozen C# contract signature, and executionLine in BOTH
//     setSource payloads (open + reload)

const fs = require('fs');
const path = require('path');

const TERMINAL = path.join(__dirname, '..');
const HTML_PATH = process.argv[2] || path.join(TERMINAL, 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');
const editorCs = fs.readFileSync(path.join(TERMINAL, '..', 'MonacoClarionSourceEditor.cs'), 'utf8');
const navigatorCs = fs.readFileSync(path.join(TERMINAL, '..', 'Services', 'MonacoSourceNavigator.cs'), 'utf8');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

const execSrc = slice(html,
    '// ----- Debugger execution line (CA-Debugger #26; overlay/file mode) -----',
    '    function requestToggleBreakpoint(ln) {', 'execution-line section');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- fakes ----------
function makeMonaco() {
    function Range(sl, sc, el, ec) { this.startLineNumber = sl; this.startColumn = sc; this.endLineNumber = el; this.endColumn = ec; }
    return { Range: Range, editor: { OverviewRulerLane: { Left: 1, Center: 2, Right: 4, Full: 7 } } };
}

// Model with Monaco-shaped decoration bookkeeping. shiftLines() stands in for Monaco's own tracking of a
// decoration when lines are inserted above it.
function makeModel(lineCount) {
    let seq = 0;
    const decos = new Map();
    return {
        _decos: decos,
        _lineCount: lineCount,
        getLineCount() { return this._lineCount; },
        deltaDecorations(oldIds, newDecos) {
            (oldIds || []).forEach(id => decos.delete(id));
            return (newDecos || []).map(d => { const id = 'd' + (++seq); decos.set(id, { range: d.range, options: d.options }); return id; });
        },
        getDecorationRange(id) { const d = decos.get(id); return d ? d.range : null; },
        shiftLines(n) { decos.forEach(d => { d.range = { startLineNumber: d.range.startLineNumber + n, startColumn: 1, endLineNumber: d.range.endLineNumber + n, endColumn: 1 }; }); }
    };
}

function makeEnv(lineCount) {
    const monaco = makeMonaco();
    const model = makeModel(lineCount == null ? 100 : lineCount);
    const editor = { getModel() { return model; } };
    const api = new Function('monaco', 'editor', `
        ${execSrc}
        return {
            setExecutionLine: setExecutionLine,
            paintExecutionLine: paintExecutionLine,
            wanted: function () { return execLineWanted; },
            setWanted: function (v) { execLineWanted = v; },
            ids: function () { return execLineDecos; }
        };`)(monaco, editor);
    api.model = model;
    return api;
}
function painted(env) {
    return env.ids().map(id => env.model._decos.get(id)).filter(Boolean);
}

// ---------- decoration shape ----------
section('decoration shape (gutter line number only)');
{
    const env = makeEnv();
    env.setExecutionLine(12);
    const p = painted(env);
    check('one decoration painted', p.length === 1, 'got ' + p.length);
    const o = p[0] ? p[0].options : {};
    check('uses lineNumberClassName exec-line-number', o.lineNumberClassName === 'exec-line-number');
    check('no className (no full-line background)', o.className === undefined);
    check('not isWholeLine', !o.isWholeLine);
    check('no glyphMarginClassName (breakpoint column untouched)', o.glyphMarginClassName === undefined);
    check('on the requested line', p[0] && p[0].range.startLineNumber === 12);
}

section('CSS rule');
{
    const m = /\.line-numbers\.exec-line-number\s*\{([^}]*)\}/.exec(html);
    check('exec-line-number CSS rule exists', !!m);
    const body = m ? m[1] : '';
    check('green background', /background:\s*#1e7e34/i.test(body));
    check('white text', /color:\s*#ffffff/i.test(body));
    check('bold', /font-weight:\s*(700|bold)/.test(body));
}

// ---------- set / move / clear ----------
section('one global marker: move and clear');
{
    const env = makeEnv();
    env.setExecutionLine(5);
    env.setExecutionLine(9);
    const p = painted(env);
    check('moving leaves exactly one marker', p.length === 1 && env.model._decos.size === 1, 'decos=' + env.model._decos.size);
    check('moved to the new line', p[0] && p[0].range.startLineNumber === 9);
    env.setExecutionLine(0);
    check('0 clears it', env.model._decos.size === 0 && env.wanted() === 0);
    env.setExecutionLine(0);
    check('clearing twice is harmless', env.model._decos.size === 0);
    env.setExecutionLine(-3);
    check('negative line clears', env.model._decos.size === 0 && env.wanted() === 0);
}
{
    const env = makeEnv(10);
    env.setExecutionLine(50);
    check('line past end paints nothing', env.model._decos.size === 0);
    check('...but is still wanted (content may be about to load)', env.wanted() === 50);
}

// ---------- reassert ----------
section('reassert on tab re-activation');
{
    const env = makeEnv();
    env.setExecutionLine(20);
    env.model.shiftLines(3);                 // developer inserted 3 lines above the marker
    env.setExecutionLine(20, true);          // host re-asserts the original line on activation
    const p = painted(env);
    check('reassert keeps the tracked position', p.length === 1 && p[0].range.startLineNumber === 23, p[0] && p[0].range.startLineNumber);
    env.setExecutionLine(20);                // a real set from the debugger (not reassert) repaints
    check('a non-reassert set repaints at the given line', painted(env)[0].range.startLineNumber === 20);
}
{
    const env = makeEnv();
    env.setExecutionLine(20);
    env.model._decos.clear();                // marker lost while in the background
    env.setExecutionLine(20, true);
    check('reassert repairs a missing marker', painted(env).length === 1 && painted(env)[0].range.startLineNumber === 20);
}
{
    const env = makeEnv();
    env.setExecutionLine(20);
    env.setExecutionLine(30, true);
    check('reassert with a different line moves it', painted(env)[0].range.startLineNumber === 30);
}

// ---------- content load race ----------
section('content (re)load');
{
    // setSource (executionLine 0) arrives, fetch starts; a set to line 40 arrives before the fetch resolves,
    // on a model that still holds the old 1-line buffer; then the fetch lands and applySource repaints.
    const env = makeEnv(1);
    env.setWanted(0);                        // setSource recorded executionLine:0 on arrival
    env.setExecutionLine(40);                // arrives mid-fetch
    check('mid-fetch set cannot paint on the old buffer', env.model._decos.size === 0);
    env.model._lineCount = 200;              // model.setValue(text)
    env.paintExecutionLine();                // applySource's post-load repaint
    check('marker painted once the content is in', painted(env).length === 1 && painted(env)[0].range.startLineNumber === 40);
}

// ---------- page wiring ----------
section('page wiring');
check('message router handles setExecutionLine',
    /msg\.type === 'setExecutionLine'\)\s*\{\s*setExecutionLine\(msg\.line,\s*!!msg\.reassert\)/.test(html));
check('setSource records executionLine on ARRIVAL (before the ready/pending branch)',
    /msg\.type === 'setSource'\)\s*\{\s*if \(msg\.executionLine != null\) execLineWanted[^\n]*\n\s*if \(ready\) applySource\(msg\); else pending = msg;/.test(html));
{
    const applySrc = slice(html, 'fetch(msg.sourceUrl, { cache: \'no-store\' })', '.catch(function (err) {', 'applySource fetch');
    const iSet = applySrc.indexOf('model.setValue(text)');
    const iPaint = applySrc.indexOf('paintExecutionLine()');
    check('applySource repaints the marker AFTER model.setValue', iSet >= 0 && iPaint > iSet);
}

// ---------- host contract ----------
section('host contract (C#)');
check('frozen signature: public static bool SetExecutionLine(string filePath, int line)',
    /public static bool SetExecutionLine\(string filePath, int line\)/.test(navigatorCs));
check('SetExecutionLine is inside MonacoSourceNavigator in ClarionAssistant.Services',
    /namespace ClarionAssistant\.Services[\s\S]*public static class MonacoSourceNavigator[\s\S]*SetExecutionLine\(string filePath, int line\)/.test(navigatorCs));
{
    const n = (editorCs.match(/\\"executionLine\\":" \+ MonacoSourceNavigator\.GetExecutionLineFor\(_filePath\)/g) || []).length;
    check('executionLine carried in BOTH setSource payloads (OnReady + OnReload)', n === 2, 'found ' + n);
}
check('tab activation re-asserts the marker',
    /OnWorkbenchWindowSelected[\s\S]{0,1200}ApplyExecutionLine\(MonacoSourceNavigator\.GetExecutionLineFor\(_filePath\), true\)/.test(editorCs));

// ---------- summary ----------
console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) {
    console.log('\nFailures:');
    failures.forEach(f => console.log('  - ' + f));
    process.exit(1);
}
