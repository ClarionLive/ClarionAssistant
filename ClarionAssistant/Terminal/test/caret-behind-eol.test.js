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
        KeyCode: { RightArrow: 17, LeftArrow: 15, UpArrow: 16, DownArrow: 18, End: 13, Home: 14, PageUp: 11, PageDown: 12,
                   Shift: 4, Ctrl: 5, Alt: 6, Meta: 57, Insert: 19, KeyC: 33, KeyF: 36, KeyS: 49 },
        editor: { EditorOption: { readOnly: 91, wrappingInfo: 150 } }
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
        getOption(id) {
            if (id === 91) return !!opts.readOnly;
            if (id === 150) return { wrappingColumn: opts.wrapColumn || -1 };   // Monaco: -1 = not wrapping
            return undefined;
        },
        // Screen top of a position. With wrapColumn set, a line breaks into rows of that many columns.
        getTopForPosition(line, column) {
            // wrappedLines: { line: breakAt } — a line word wrap breaks EARLY (at a word boundary).
            const at = (opts.wrappedLines && opts.wrappedLines[line]) || opts.wrapColumn;
            const row = opts.wrapColumn ? Math.floor((column - 1) / at) : 0;
            return line * 1000 + row * 18;
        },
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
            padGoal: function () { return _padGoal; },
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

// ---------- stale goal column + word wrap (ticket 16d140e9) ----------
// The Owner, 5.9.0: with word wrap on, a click in the empty space to the right of a wrapped row set the
// caret AND the up/down goal to that screen column (~the window width, column 125 for him), because
// mouseColumn counts from the left of the clicked ROW, not the start of the line. Nothing but four
// navigation keys ever cleared the goal, so it outlived typing, Enter, clicks and the wrap toggle: every
// later up/down padded the line it landed on out to column 125, and typing then Enter on such a blank
// line carried 124 spaces of indent onto the new line. Reopening the procedure reloaded the page, which
// is the only thing that reset it.
function keyDown(ed, keyCode, extra) {
    let prevented = false;
    const ev = Object.assign({ keyCode: keyCode, preventDefault: () => { prevented = true; }, stopPropagation() { } }, extra || {});
    ed._handlers.key.forEach(f => f(ev));
    return prevented;
}
function click(ed, line, column, mouseColumn) {
    ed._handlers.mouse.forEach(f => f({
        target: { position: { lineNumber: line, column: column }, mouseColumn: mouseColumn },
        event: { shiftKey: false, altKey: false }
    }));
}
function moveCaret(ed, line, column) {       // what Monaco does after an edit or a plain click: move, then notify
    ed.setPosition({ lineNumber: line, column: column });
    ed._handlers.cursor.forEach(f => f({ position: { lineNumber: line, column: column } }));
}
const KC_ENTER = 3, KC_KEY_X = 54, KC_DOWN = 18;

section('Enter ends the goal column (16d140e9)');
{
    const env = makeEnv();
    const model = makeModel(['abc', '  x = 1', '', 'de']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    click(ed, 1, 4, 40);                                     // past EOL: goal 40, legitimately
    check('precondition: a click past EOL sets the goal', env.padGoal() === 40, 'goal=' + env.padGoal());
    moveCaret(ed, 2, 8);                                     // caret goes elsewhere (not by a key we see)
    keyDown(ed, KC_ENTER);                                   // Enter
    moveCaret(ed, 3, 3);                                     // Monaco puts the caret on the new indented line
    check('Enter cleared the goal', env.padGoal() === null, 'goal=' + env.padGoal());
    keyDown(ed, KC_DOWN);
    check('the next Down does not pad out to the stale goal', model.getLineContent(4) === 'de',
        JSON.stringify(model.getLineContent(4)));
}

section('Typing ends the goal column (16d140e9)');
{
    const env = makeEnv();
    const model = makeModel(['abc', 'de']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    click(ed, 1, 4, 30);
    keyDown(ed, KC_KEY_X);
    check('a typed character cleared the goal', env.padGoal() === null, 'goal=' + env.padGoal());
}

section('A click inside the text ends the goal column (16d140e9)');
{
    const env = makeEnv();
    const model = makeModel(['abc', 'a-longer-line', 'de']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    click(ed, 1, 4, 30);                                     // goal 30
    click(ed, 2, 3, 3);                                      // an ordinary click inside the text
    moveCaret(ed, 2, 3);
    check('the ordinary click cleared the goal', env.padGoal() === null, 'goal=' + env.padGoal());
    keyDown(ed, KC_DOWN);
    check('Down from there does not jump to column 30', model.getLineContent(3) === 'de',
        JSON.stringify(model.getLineContent(3)));
}

section('Word wrap: a click right of a wrapped row is NOT past EOL (16d140e9)');
{
    const env = makeEnv();
    const long = '  Loc:Something = ' + 'x'.repeat(180);    // wraps at 80 → three screen rows
    const model = makeModel([long, '  a = 1', '']);
    const ed = makeEditor(model, { wrapColumn: 80 });
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    // Monaco clamps position to the end of the clicked ROW (col 81); mouseColumn is the screen column (125).
    click(ed, 1, 81, 125);
    check('caret NOT thrown to model column 125', ed.getPosition().column !== 125, 'column=' + ed.getPosition().column);
    check('no goal column taken from a screen column', env.padGoal() === null, 'goal=' + env.padGoal());
    check('the wrapped line was not touched', model.getLineContent(1) === long);
    moveCaret(ed, 1, 81);
    keyDown(ed, KC_DOWN);
    check('Down on the next line did not pad it out to column 125', model.getLineContent(2) === '  a = 1',
        JSON.stringify(model.getLineContent(2)));
}

section('Word wrap: up/down on a wrapped line is left to Monaco (16d140e9)');
{
    const env = makeEnv();
    const model = makeModel(['L'.repeat(200), 'ab']);
    const ed = makeEditor(model, { wrapColumn: 80 });
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed.setPosition({ lineNumber: 1, column: 150 });          // on the wrapped line's second row
    const prevented = keyDown(ed, KC_DOWN);
    check('Down was not intercepted (Monaco moves to the next ROW)', prevented === false);
    check('the short line below was not padded', model.getLineContent(2) === 'ab', JSON.stringify(model.getLineContent(2)));
}

section('Word wrap: a one-row line still takes a click past EOL');
{
    // Regression floor for the fix above: wrap mode only disqualifies WRAPPED lines.
    const env = makeEnv();
    const model = makeModel(['abc']);
    const ed = makeEditor(model, { wrapColumn: 80 });
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    click(ed, 1, 4, 12);
    check('caret landed at the clicked column', ed.getPosition().column === 12, 'column=' + ed.getPosition().column);
    check('line padded to reach it', model.getLineMaxColumn(1) === 12);
    check('goal column taken', env.padGoal() === 12, 'goal=' + env.padGoal());
}

section('Word wrap: handing up/down to Monaco ends our goal (16d140e9 review)');
{
    const env = makeEnv();
    const model = makeModel(['abc', 'L'.repeat(200), '  ab', 'x']);
    const ed = makeEditor(model, { wrapColumn: 80 });
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    click(ed, 1, 4, 30);                                     // goal 30 on a one-row line
    moveCaret(ed, 2, 30);                                    // caret now on the wrapped line
    keyDown(ed, KC_DOWN);                                    // handed to Monaco
    check('the goal ended at the hand-off', env.padGoal() === null, 'goal=' + env.padGoal());
    moveCaret(ed, 3, 3);                                     // Monaco lands back on a short line
    keyDown(ed, KC_DOWN);
    check('the old goal does not re-apply on the short line below', model.getLineContent(4).length < 29,
        JSON.stringify(model.getLineContent(4)));
}

section('Word wrap: a wrapped TARGET line is not padded (16d140e9 review)');
{
    // Word wrap breaks at word boundaries, so a line can wrap well before the wrap column.
    const env = makeEnv();
    const model = makeModel(['abcdefghijklmnopqrst', 'word word word']);
    const ed = makeEditor(model, { wrapColumn: 80, wrappedLines: { 2: 10 } });
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed.setPosition({ lineNumber: 1, column: 21 });
    const prevented = keyDown(ed, KC_DOWN);
    check('Down onto the wrapped line was left to Monaco', prevented === false);
    check('the wrapped target line was not padded', model.getLineContent(2) === 'word word word',
        JSON.stringify(model.getLineContent(2)));
}

section('Word wrap: padding never pushes a line past the wrap column (16d140e9 review)');
{
    const env = makeEnv();
    const model = makeModel(['x'.repeat(80), 'next']);
    const ed = makeEditor(model, { wrapColumn: 80 });
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    ed.setPosition({ lineNumber: 1, column: 81 });           // at EOL, exactly one full row
    const prevented = keyDown(ed, 17);                       // Right
    check('Right at a full row is left to Monaco', prevented === false);
    check('the line was not padded onto a second row', model.getLineContent(1).length === 80,
        'len=' + model.getLineContent(1).length);
    check('no goal left behind', env.padGoal() === null, 'goal=' + env.padGoal());

    const model2 = makeModel(['abc']);
    const ed2 = makeEditor(model2, { wrapColumn: 80 });
    env.installCaretBehindEol(ed2);
    click(ed2, 1, 4, 90);                                    // pointer beyond the wrap column
    check('a click beyond the wrap column does not pad', model2.getLineContent(1) === 'abc',
        JSON.stringify(model2.getLineContent(1)));
    click(ed2, 1, 4, 81);                                    // right at it: still one row
    check('a click up to the wrap column still pads', model2.getLineMaxColumn(1) === 81,
        'max=' + model2.getLineMaxColumn(1));
}

section('Ctrl chords that neither move nor edit keep the goal (16d140e9 review)');
{
    const env = makeEnv();
    const model = makeModel(['abc', 'de']);
    const ed = makeEditor(model);
    env.setCaretBehindEol(true);
    env.installCaretBehindEol(ed);
    click(ed, 1, 4, 30);
    keyDown(ed, 33, { ctrlKey: true });                      // Ctrl+C
    check('Ctrl+C keeps the goal', env.padGoal() === 30, 'goal=' + env.padGoal());
    keyDown(ed, 49, { ctrlKey: true });                      // Ctrl+S
    check('Ctrl+S keeps the goal', env.padGoal() === 30, 'goal=' + env.padGoal());
    keyDown(ed, 52, { ctrlKey: true });                      // Ctrl+V pastes — an edit
    check('Ctrl+V ends the goal', env.padGoal() === null, 'goal=' + env.padGoal());
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
