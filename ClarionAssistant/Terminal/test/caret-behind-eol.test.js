// caret-behind-eol.test.js — guards Clarion's "Can move caret behind EOL" emulation and the per-key
// follow-Clarion's-editor-options resolver.
//
// Run:  node Terminal/test/caret-behind-eol.test.js
//
// Zero-dependency (no jsdom): the code under test touches Monaco, not the DOM, so the fakes here are a
// small line-based model and an editor stub.
//
// Like its neighbours this test EXTRACTS the page's code rather than copying it — the caret-behind-EOL
// section and the effIde/captureIdeOptions resolver are sliced out of monaco-embeditor.html at run time
// and evaluated. Rename a function or change the padding contract and this fails immediately.
//
// WHY THIS EXISTS. Monaco 0.52 has no virtual space: setPosition clamps through validatePosition, so the
// caret cannot occupy a column past the end of the line. The page therefore pads the line with real
// spaces and takes them away again. That trade buys the feature but introduces two ways to do real
// damage, and both are asserted here:
//   * padding must never reach the .app — if a trim is missed, trailing spaces are written into embed
//     slots, which is source corruption, not a cosmetic bug
//   * padding must never mark the buffer dirty — otherwise moving the caret lights the ● indicator and
//     prompts to save a file nobody edited
// Plus the regression floor: with the option OFF nothing in the engine may engage at all.

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');

// ---------- extract the real code ----------
function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

const engineSrc = slice(html,
    '// ===================== Caret behind EOL ("Can move caret behind EOL") =====================',
    '    var _ideOpts = null;', 'caret-behind-EOL engine');

const resolverSrc = slice(html,
    '    var _ideOpts = null;',
    '    function applyEditorSettings(s) {', 'ide option resolver');

if (!/function ensurePad/.test(engineSrc)) throw new Error('extracted engine has no ensurePad');
if (!/function trimPad/.test(engineSrc)) throw new Error('extracted engine has no trimPad');
if (!/function effIde/.test(resolverSrc)) throw new Error('extracted resolver has no effIde');

// ---------- scaffolding ----------
let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- Monaco fakes ----------
function makeMonaco() {
    function Range(sl, sc, el, ec) {
        this.startLineNumber = sl; this.startColumn = sc;
        this.endLineNumber = el; this.endColumn = ec;
    }
    return {
        Range: Range,
        KeyCode: { RightArrow: 17, LeftArrow: 15, UpArrow: 16, DownArrow: 18, End: 13, Home: 14, PageUp: 11, PageDown: 12 },
        editor: { EditorOption: { readOnly: 91 } }
    };
}

// Line-based model. Columns are 1-based, so column N sits before character N-1.
function makeModel(lines) {
    const L = lines.slice();
    return {
        _lines: L,
        getLineCount() { return L.length; },
        getLineContent(n) { return L[n - 1]; },
        getLineMaxColumn(n) { return L[n - 1].length + 1; },
        getValue() { return L.join('\n'); },
        getValueInRange(r) { return L[r.startLineNumber - 1].slice(r.startColumn - 1, r.endColumn - 1); },
        applyEdits(edits) {
            for (const e of edits) {
                const i = e.range.startLineNumber - 1;
                const s = L[i];
                L[i] = s.slice(0, e.range.startColumn - 1) + e.text + s.slice(e.range.endColumn - 1);
            }
        }
    };
}

function makeEditor(model, opts) {
    opts = opts || {};
    const handlers = { mouse: [], key: [], cursor: [], blur: [] };
    let pos = { lineNumber: 1, column: 1 };
    let selection = null;
    return {
        _handlers: handlers,
        _selection() { return selection; },
        getModel() { return model; },
        getPosition() { return pos; },
        setPosition(p) { pos = { lineNumber: p.lineNumber, column: p.column }; selection = null; },
        setSelection(s) {
            selection = s;
            pos = { lineNumber: s.positionLineNumber, column: s.positionColumn };
        },
        getSelection() {
            // Monaco always hands back a FULL Selection — collapsed at the caret when there's no selection,
            // never a bare {isEmpty}. A stub that omits selectionStart* would let a real anchor bug through.
            if (!selection) {
                return {
                    selectionStartLineNumber: pos.lineNumber, selectionStartColumn: pos.column,
                    positionLineNumber: pos.lineNumber, positionColumn: pos.column,
                    isEmpty: () => true
                };
            }
            return Object.assign({}, selection, { isEmpty: () => false });
        },
        getOption(id) { return id === 91 ? !!opts.readOnly : undefined; },
        onMouseDown(f) { handlers.mouse.push(f); },
        onKeyDown(f) { handlers.key.push(f); },
        onDidChangeCursorPosition(f) { handlers.cursor.push(f); },
        onDidBlurEditorText(f) { handlers.blur.push(f); }
    };
}

// Evaluate the extracted page code with the globals it expects.
function makeEnv(opts) {
    opts = opts || {};
    const monaco = makeMonaco();
    const src = `
        var guarding = false;
        var _editable = true;
        var _guardWitness = [];
        function isEditableRange(r) { return _editable; }
        ${engineSrc}
        ${resolverSrc}
        return {
            setCaretBehindEol: setCaretBehindEol,
            ensurePad: ensurePad,
            trimPad: trimPad,
            installCaretBehindEol: installCaretBehindEol,
            captureIdeOptions: captureIdeOptions,
            effIde: effIde,
            padState: function () { return _pad; },
            guarding: function () { return guarding; },
            setEditable: function (v) { _editable = v; },
            isOn: function () { return caretBehindEolOn; }
        };
    `;
    return new Function('monaco', src)(monaco);
}

// ---------- pad / trim ----------
section('Padding places the caret past EOL');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);

    const ok = env.ensurePad(ed, 1, 8);
    check('ensurePad reports the column is now legal', ok === true);
    check('line padded out to the requested column', model.getLineMaxColumn(1) === 8,
        'maxColumn=' + model.getLineMaxColumn(1));
    check('padding is spaces only — original text untouched', model.getLineContent(1) === 'abc    ',
        JSON.stringify(model.getLineContent(1)));
}

section('Trim restores the line exactly');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.ensurePad(ed, 1, 8);
    env.trimPad();
    check('line is byte-identical to before padding', model.getLineContent(1) === 'abc',
        JSON.stringify(model.getLineContent(1)));
    check('no trailing whitespace survives the trim', !/\s$/.test(model.getLineContent(1)));
    check('pad tracking cleared', env.padState() === null);
}

section('Padding never marks the buffer dirty');
{
    // The page's onDidChangeModelContent handlers all bail on `guarding` before calling setDirty(true).
    // Assert the flag is actually raised across our edits — this is the whole dirty-suppression contract.
    const env = makeEnv();
    const seen = [];
    const model = makeModel(['abc']);
    const realApply = model.applyEdits.bind(model);
    model.applyEdits = function (e) { seen.push(env.guarding()); return realApply(e); };
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.ensurePad(ed, 1, 8);
    env.trimPad();
    check('every padding edit ran under `guarding`', seen.length > 0 && seen.every(Boolean),
        JSON.stringify(seen));
    check('guarding restored to false afterwards', env.guarding() === false);
}

section('Typing at the virtual column keeps the spaces (Clarion behaviour)');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.ensurePad(ed, 1, 8);
    model.applyEdits([{ range: { startLineNumber: 1, startColumn: 8, endLineNumber: 1, endColumn: 8 }, text: 'X' }]);
    env.trimPad();
    check('typed character survives', /X$/.test(model.getLineContent(1)), JSON.stringify(model.getLineContent(1)));
    check('the spaces the user typed past became real content', model.getLineContent(1) === 'abc    X',
        JSON.stringify(model.getLineContent(1)));
}

section('Off-mode is the regression floor');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    // never armed
    check('ensurePad refuses while the option is off', env.ensurePad(ed, 1, 8) === false);
    check('model untouched while the option is off', model.getLineContent(1) === 'abc');
    check('engine reports itself off', env.isOn() === false);

    env.setCaretBehindEol(true);
    env.ensurePad(ed, 1, 8);
    env.setCaretBehindEol(false);
    check('turning the option off trims any live padding', model.getLineContent(1) === 'abc',
        JSON.stringify(model.getLineContent(1)));
}

section('Generated / read-only code is never padded');
{
    const env = makeEnv();
    const model = makeModel(['generated']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.setEditable(false);                      // embed mode: outside an editable slot
    check('refuses to pad outside an editable slot', env.ensurePad(ed, 1, 20) === false);
    check('generated line untouched', model.getLineContent(1) === 'generated');

    const ro = makeEditor(makeModel(['abc']), { readOnly: true });
    env.setEditable(true);
    check('refuses to pad a read-only editor', env.ensurePad(ro, 1, 20) === false);
    check('read-only line untouched', ro.getModel().getLineContent(1) === 'abc');
}

section('Only one line is ever padded');
{
    const env = makeEnv();
    const model = makeModel(['abc', 'de']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.ensurePad(ed, 1, 8);
    env.ensurePad(ed, 2, 6);                     // moving to another line
    check('previous line was trimmed when padding moved', model.getLineContent(1) === 'abc',
        JSON.stringify(model.getLineContent(1)));
    check('new line is the padded one', model.getLineMaxColumn(2) === 6);
    check('exactly one padded line tracked', env.padState() !== null && env.padState().line === 2);
}

section('Shrinking the pad (left-arrow inside the padding)');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.ensurePad(ed, 1, 10);
    env.ensurePad(ed, 1, 6);
    check('pad shrinks to the caret', model.getLineMaxColumn(1) === 6, 'maxColumn=' + model.getLineMaxColumn(1));
    check('still only spaces past the real text', model.getLineContent(1) === 'abc  ',
        JSON.stringify(model.getLineContent(1)));
    env.ensurePad(ed, 1, 4);                     // back to the real end of the line
    check('collapsing to the real EOL stops tracking', env.padState() === null);
    check('line back to original', model.getLineContent(1) === 'abc');
}

section('Blur trims (a tab switch must not freeze padding into the buffer)');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    env.ensurePad(ed, 1, 8);
    ed._handlers.blur.forEach(f => f());
    check('blur handler trimmed the padding', model.getLineContent(1) === 'abc',
        JSON.stringify(model.getLineContent(1)));
}

section('Caret leaving the line trims (the cursor reconciler)');
{
    const env = makeEnv();
    const model = makeModel(['abc', 'defgh']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    env.ensurePad(ed, 1, 8);
    ed._handlers.cursor.forEach(f => f({ position: { lineNumber: 2, column: 1 } }));
    check('padding removed when the caret moved to another line', model.getLineContent(1) === 'abc',
        JSON.stringify(model.getLineContent(1)));
    check('tracking cleared', env.padState() === null);
}

section('Right-arrow at EOL moves right instead of wrapping');
{
    const env = makeEnv();
    const model = makeModel(['abc', 'next']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed.setPosition({ lineNumber: 1, column: 4 });    // at EOL of "abc"
    let prevented = false;
    ed._handlers.key.forEach(f => f({ keyCode: 17, preventDefault: () => { prevented = true; }, stopPropagation() { } }));
    check('the key was intercepted', prevented === true);
    check('caret stayed on the same line', ed.getPosition().lineNumber === 1);
    check('caret moved one column right', ed.getPosition().column === 5, 'column=' + ed.getPosition().column);
}

section('Click past EOL uses the UNCLAMPED mouseColumn');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    // target.position is clamped by Monaco to the line end; mouseColumn is where the pointer really was.
    ed._handlers.mouse.forEach(f => f({
        target: { position: { lineNumber: 1, column: 4 }, mouseColumn: 12 },
        event: { shiftKey: false, altKey: false }
    }));
    check('caret landed at the clicked column, not the clamped one', ed.getPosition().column === 12,
        'column=' + ed.getPosition().column);
    check('line padded to reach it', model.getLineMaxColumn(1) === 12);
}

section('Shift+right at EOL extends the selection past the end of the line');
{
    const env = makeEnv();
    const model = makeModel(['abc', 'next']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed.setPosition({ lineNumber: 1, column: 4 });
    let prevented = false;
    ed._handlers.key.forEach(f => f({
        keyCode: 17, shiftKey: true,
        preventDefault: () => { prevented = true; }, stopPropagation() { }
    }));
    check('shift+right was intercepted', prevented === true);
    const s = ed._selection();
    check('a selection was created', !!s);
    check('anchor stayed at the real EOL', s && s.selectionStartColumn === 4, s && 'anchor=' + s.selectionStartColumn);
    check('active end moved into virtual space', s && s.positionColumn === 5, s && 'active=' + s.positionColumn);
    check('selection stayed on one line', s && s.positionLineNumber === 1);
}

section('Plain right-arrow must not swallow the FIRST shift+right');
{
    // Regression guard: on the first shift+right the selection is still empty, so a !hasSel-only guard on
    // the plain right-arrow branch would fire and collapse the selection the user was starting.
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed.setPosition({ lineNumber: 1, column: 4 });
    ed._handlers.key.forEach(f => f({ keyCode: 17, shiftKey: true, preventDefault() { }, stopPropagation() { } }));
    check('shift+right produced a selection, not a bare caret move', !!ed._selection());
}

section('Shift+click past EOL extends the selection to the clicked column');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed.setSelection({ selectionStartLineNumber: 1, selectionStartColumn: 1, positionLineNumber: 1, positionColumn: 2 });
    ed._handlers.mouse.forEach(f => f({
        target: { position: { lineNumber: 1, column: 4 }, mouseColumn: 10 },
        event: { shiftKey: true, altKey: false }
    }));
    const s = ed._selection();
    check('original anchor preserved', s && s.selectionStartColumn === 1, s && 'anchor=' + s.selectionStartColumn);
    check('selection extended to the clicked virtual column', s && s.positionColumn === 10,
        s && 'active=' + s.positionColumn);
}

section('Alt+click is left to Monaco (column select / multi-cursor)');
{
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed._handlers.mouse.forEach(f => f({
        target: { position: { lineNumber: 1, column: 4 }, mouseColumn: 10 },
        event: { shiftKey: false, altKey: true }
    }));
    check('no padding applied on alt+click', model.getLineContent(1) === 'abc',
        JSON.stringify(model.getLineContent(1)));
}

section('Up/down carry our own goal column');
{
    const env = makeEnv();
    const model = makeModel(['a-long-line-here', 'ab']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed.setPosition({ lineNumber: 1, column: 12 });          // real text reaches col 12 on line 1
    ed._handlers.key.forEach(f => f({ keyCode: 18, preventDefault() { }, stopPropagation() { } }));  // Down
    check('caret moved to the short line', ed.getPosition().lineNumber === 2);
    check('goal column preserved past that line\'s real end', ed.getPosition().column === 12,
        'column=' + ed.getPosition().column);
    check('short line padded to reach the goal column', model.getLineMaxColumn(2) === 12);
    // and coming back off it leaves nothing behind
    ed._handlers.cursor.forEach(f => f({ position: { lineNumber: 1, column: 1 } }));
    check('padding removed on leaving', model.getLineContent(2) === 'ab',
        JSON.stringify(model.getLineContent(2)));
}

// ---------- follow-mode resolver ----------
section('Per-key follow-Clarion resolver');
{
    const env = makeEnv();
    env.captureIdeOptions({ ideTabSize: 2, ideInsertSpaces: true, ideLineNumbers: 'off', ideFolding: false });
    check('follow ON → the IDE value wins', env.effIde(true, 'lineNumbers', 'on') === 'off');
    check('follow OFF → the stored/default value wins', env.effIde(false, 'lineNumbers', 'on') === 'on');
    check('follow ON, key absent from the IDE bundle → stored wins',
        env.effIde(true, 'renderLineHighlight', 'line') === 'line');
    check('booleans survive the resolver', env.effIde(true, 'folding', true) === false);
}

section('Font keys omitted by the host fall back to the stored pref');
{
    const env = makeEnv();
    env.captureIdeOptions({ ideTabSize: 2 });        // descriptor did not parse → no ideFontFamily/ideFontSize
    check('fontFamily falls back even in follow mode', env.effIde(true, 'fontFamily', 'Consolas') === 'Consolas');
    check('fontSize falls back even in follow mode', env.effIde(true, 'fontSize', 13) === 13);

    const env2 = makeEnv();
    env2.captureIdeOptions({ ideTabSize: 2, ideFontFamily: 'Courier New', ideFontSize: 15 });
    check('a parsed font IS followed', env2.effIde(true, 'fontFamily', 'Consolas') === 'Courier New');
    check('a parsed size IS followed', env2.effIde(true, 'fontSize', 13) === 15);
}

section('A locally-built payload must not drop follow mode');
{
    // onSettingChanged builds a payload from the gear controls; it carries NO ide* keys. If that cleared
    // the cache, toggling any unrelated control would silently stop following Clarion until the next load.
    const env = makeEnv();
    env.captureIdeOptions({ ideTabSize: 2, ideLineNumbers: 'off' });
    env.captureIdeOptions({ tabSize: 4, wordWrap: true });     // no ideTabSize sentinel
    check('cached IDE bundle survives a local payload', env.effIde(true, 'lineNumbers', 'on') === 'off');
}

// ---------- summary ----------
console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) {
    console.log('\nFailures:');
    failures.forEach(f => console.log('  - ' + f));
    process.exit(1);
}
