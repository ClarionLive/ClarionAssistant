// mark-word.test.js — guards Mark Word (Ctrl+W, GH #229): select the word the caret is in.
//
// Run:  node Terminal/test/mark-word.test.js
//
// Zero-dependency (no jsdom): the command touches Monaco, not the DOM, so the fakes here are a small
// line-based model and an editor stub.
//
// Like its neighbours this test EXTRACTS the page's code rather than copying it — the Mark Word section,
// the EDITOR_COMMANDS table, IDE_SHORTCUTS and the keydown-to-chord mapping are sliced out of
// monaco-embeditor.html at run time and evaluated.
//
// A word is a run of [A-Za-z0-9_]. The colon is deliberately NOT a word character: the Clarion
// wordPattern keeps `LOC:Name` whole for double-click, so Mark Word is the way to take just `LOC` or
// just `Name`. That split is the reason the command exists, so it is asserted here.
//
// A SECOND Ctrl+W, while the selection is still exactly the word the first one marked, widens it to the
// whole word the editor sees -- Monaco's getWordAtPosition under the Clarion wordPattern, the same word
// double-click selects. The fake model below uses that wordPattern, lifted from clarion-language.js, so
// "the same as double-click" is checked against the real definition, not a copy of it.

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');
const langJs = fs.readFileSync(path.join(path.dirname(HTML_PATH), 'clarion-language.js'), 'utf8');

// ---------- extract the real code ----------
function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

// A table literal whose entries name page functions (run: cmdX) evaluates against a scope where every
// free name is a distinct stub, so the table can be read without dragging in the whole page.
function evalLiteral(src) {
    const stubs = {};
    const scope = new Proxy({}, {
        has: (t, k) => typeof k === 'string' && !(k in globalThis),
        get: (t, k) => stubs[k] || (stubs[k] = function stub() { }),
    });
    const value = new Function('scope', 'with (scope) { return (' + src + '); }')(scope);
    return { value, stubs };
}

let markWordSrc = null, commandsSrc = null, shortcutsSrc = null, chordSrc = null, extractError = null;
try {
    markWordSrc = slice(html, '// ===================== Mark Word', '// ===================== Split view (#15)', 'Mark Word section');
    commandsSrc = slice(html, 'var EDITOR_COMMANDS = [', '];', 'EDITOR_COMMANDS').replace('var EDITOR_COMMANDS = ', '') + ']';
    shortcutsSrc = slice(html, 'var IDE_SHORTCUTS = [', '];', 'IDE_SHORTCUTS').replace('var IDE_SHORTCUTS = ', '') + ']';
    chordSrc = slice(html, '    function tokenFromEvent(ev) {', '    function installKeyBindings() {', 'chord mapping');
} catch (e) { extractError = e.message; }

// The Clarion wordPattern, as the editor registers it.
let WORD_PATTERN = null;
{
    const m = /wordPattern:\s*(\/.+\/[gimsuy]*),\s*$/m.exec(langJs);
    if (m) WORD_PATTERN = new Function('return ' + m[1])();
    else extractError = extractError || 'could not find wordPattern in clarion-language.js';
}

// ---------- scaffolding ----------
let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- Monaco fakes ----------
// Selection(selectionStartLine, selectionStartColumn, positionLine, positionColumn), 1-based columns.
function Selection(sl, sc, pl, pc) {
    this.selectionStartLineNumber = sl; this.selectionStartColumn = sc;
    this.positionLineNumber = pl; this.positionColumn = pc;
}
const monaco = { Selection: Selection };
const caret = (line, col) => new Selection(line, col, line, col);

// Monaco's getWordAtPosition: the wordPattern match whose span holds the column, edges included.
function wordAt(text, column) {
    const re = new RegExp(WORD_PATTERN.source, 'g');
    let m;
    while ((m = re.exec(text))) {
        if (!m[0].length) { re.lastIndex++; continue; }
        const start = m.index + 1, end = m.index + m[0].length + 1;
        if (start <= column && column <= end) return { word: m[0], startColumn: start, endColumn: end };
    }
    return null;
}

function makeEditor(lines, selections) {
    const model = {
        getLineContent: n => lines[n - 1], getLineCount: () => lines.length,
        getWordAtPosition: pos => wordAt(lines[pos.lineNumber - 1], pos.column),
    };
    const ed = {
        _sels: selections, setCalls: 0,
        getModel: () => model,
        getSelections() { return this._sels; },
        setSelections(s) { this._sels = s; this.setCalls++; },
    };
    return ed;
}

function loadMarkWord(ed) {
    return new Function('monaco', 'activeEd',
        markWordSrc + '\nreturn { markWordRange: markWordRange, cmdMarkWord: cmdMarkWord };')(monaco, () => ed);
}

// The text a single-caret run of the command selects, with the caret written as `|` in the fixture.
function markAt(fixture) {
    const col = fixture.indexOf('|') + 1;
    const line = fixture.replace('|', '');
    const ed = makeEditor([line], [caret(1, col)]);
    loadMarkWord(ed).cmdMarkWord();
    const s = ed._sels[0];
    if (s.selectionStartColumn === s.positionColumn) return null;   // still a bare caret: nothing marked
    return line.slice(s.selectionStartColumn - 1, s.positionColumn - 1);
}

if (extractError) {
    check('the page has a Mark Word section, EDITOR_COMMANDS, IDE_SHORTCUTS and the chord mapping', false, extractError);
} else {
    section('The word at the caret');
    check('caret inside a word marks the whole word', markAt('  Cus|tomerName = 1') === 'CustomerName');
    check('caret at the start of a word marks it', markAt('  |CustomerName = 1') === 'CustomerName');
    check('caret just after a word marks it', markAt('  CustomerName| = 1') === 'CustomerName');
    check('caret between a word and punctuation marks the word on its left', markAt('a|+b') === 'a');
    check('caret after punctuation marks the word on its right', markAt('a+|b') === 'b');
    check('word at the very start of the line', markAt('|Start = 1') === 'Start');
    check('word at the very end of the line', markAt('x = End|') === 'End');
    check('digits and underscores are word characters', markAt('Loc_Cou|nt2 = 10') === 'Loc_Count2');
    check('a number is a word', markAt('x = 12|345') === '12345');

    section('The colon splits a prefixed name');
    check('caret in the prefix marks only the prefix', markAt('LO|C:Customer = 1') === 'LOC');
    check('caret in the name marks only the name', markAt('LOC:Cust|omer = 1') === 'Customer');
    check('caret right after the colon marks the name', markAt('LOC:|Customer = 1') === 'Customer');
    check('a dot separates words', markAt('SELF.Q|ueue.Field') === 'Queue');
    check('non-ASCII letters are not word characters', markAt('caf|é') === 'caf');

    section('Nothing to mark');
    check('caret between two spaces marks nothing', markAt('x = | 1') === null);
    check('caret between two punctuation marks marks nothing', markAt('a +|+ b') === null);
    check('an empty line marks nothing', markAt('|') === null);
    check('a blank line marks nothing', markAt('    |    ') === null);

    section('Selections and multiple cursors');
    {
        // An existing selection is replaced by the word at its caret end (the position, not the anchor).
        const ed = makeEditor(['  First = Second'], [new Selection(1, 3, 1, 12)]);   // anchor in First, caret in Second
        loadMarkWord(ed).cmdMarkWord();
        const s = ed._sels[0];
        check('an existing selection becomes the word at its caret end',
            s.selectionStartColumn === 11 && s.positionColumn === 17, JSON.stringify(s));
    }
    {
        const ed = makeEditor(['  LOC:Name = 1', '  x = ; 2', '  Total += Amount'],
            [caret(1, 8), caret(2, 7), caret(3, 15)]);
        loadMarkWord(ed).cmdMarkWord();
        const [a, b, c] = ed._sels;
        check('each cursor marks its own word', a.selectionStartColumn === 7 && a.positionColumn === 11
            && c.selectionStartColumn === 12 && c.positionColumn === 18, JSON.stringify([a, c]));
        check('a cursor touching no word keeps its caret', b.selectionStartColumn === 7 && b.positionColumn === 7, JSON.stringify(b));
        check('the result is one setSelections call, so it is one undoable cursor state', ed.setCalls === 1);
    }
    {
        const ed = makeEditor(['x = | 1'.replace('|', '')], [caret(1, 4)]);
        loadMarkWord(ed).cmdMarkWord();
        check('with no word at any cursor the selections are left untouched', ed.setCalls === 0);
    }
    {
        const ed = makeEditor(['Name'], [caret(1, 2)]);
        ed.getModel = () => null;
        let threw = null;
        try { loadMarkWord(ed).cmdMarkWord(); } catch (e) { threw = e; }
        check('no model (page still loading) is a no-op, not an exception', threw === null && ed.setCalls === 0);
    }

    section('A second press widens to the whole word (the double-click word)');
    // Runs Ctrl+W `presses` times on one caret and returns the selected text (null for a bare caret).
    function pressAt(fixture, presses) {
        const col = fixture.indexOf('|') + 1;
        const line = fixture.replace('|', '');
        const ed = makeEditor([line], [caret(1, col)]);
        const mw = loadMarkWord(ed);
        for (let i = 0; i < presses; i++) mw.cmdMarkWord();
        const s = ed._sels[0];
        const a = Math.min(s.selectionStartColumn, s.positionColumn), z = Math.max(s.selectionStartColumn, s.positionColumn);
        return a === z ? null : line.slice(a - 1, z - 1);
    }
    check('first press marks the prefix', pressAt('  LO|C:CustomerName = 1', 1) === 'LOC');
    check('second press from the prefix marks the whole name', pressAt('  LO|C:CustomerName = 1', 2) === 'LOC:CustomerName');
    check('second press from the field part marks the whole name', pressAt('  LOC:Cust|omerName = 1', 2) === 'LOC:CustomerName');
    check('a name with several colons widens to all of it', pressAt('  Relate:Cus|tomer:Open', 2) === 'Relate:Customer:Open');
    check('a field equate widens without its leading ?', pressAt('  ?LOC:Na|me:Prompt{PROP:Hide} = 1', 2) === 'LOC:Name:Prompt');
    check('the dot still separates: SELF.Queue stays Queue', pressAt('  SELF.Q|ueue.Field', 2) === 'Queue');
    check('a third press keeps the whole name', pressAt('  LO|C:CustomerName = 1', 3) === 'LOC:CustomerName');
    check('a word with no colon stays as it is on a second press', pressAt('  Cus|tomerName = 1', 2) === 'CustomerName');
    {
        const ed = makeEditor(['  CustomerName = 1'], [caret(1, 5)]);
        const mw = loadMarkWord(ed);
        mw.cmdMarkWord(); mw.cmdMarkWord();
        check('a second press with nothing wider sets no selection', ed.setCalls === 1, 'setSelections calls: ' + ed.setCalls);
    }
    {
        // A selection the first press did not make (here `OC:Cu`) is not widened; the word at its caret is marked.
        const ed = makeEditor(['  LOC:CustomerName = 1'], [new Selection(1, 4, 1, 9)]);
        loadMarkWord(ed).cmdMarkWord();
        const s = ed._sels[0];
        check('an arbitrary selection is replaced by the word at its caret, not widened',
            s.selectionStartColumn === 7 && s.positionColumn === 19, JSON.stringify(s));
    }
    {
        // Each cursor decides for itself: one already on a marked part widens, one on a bare caret marks.
        const ed = makeEditor(['  LOC:Name = 1', '  CUS:Code = 2'], [new Selection(1, 3, 1, 6), caret(2, 9)]);
        loadMarkWord(ed).cmdMarkWord();
        const [a, b] = ed._sels;
        check('with several cursors, a marked part widens and a bare caret marks its part',
            a.selectionStartColumn === 3 && a.positionColumn === 11 && b.selectionStartColumn === 7 && b.positionColumn === 11,
            JSON.stringify([a, b]));
    }
    {
        // Every widening agrees with the editor's own word, over a spread of lines and caret positions.
        const lines = ['  LOC:CustomerName = GLO:Today()', '  ?Browse:1{PROP:Selected} = Q:Rec:Id', '  x = a:b + c_d:e1 ! note:here'];
        let disagree = [];
        lines.forEach(line => {
            for (let col = 1; col <= line.length + 1; col++) {
                const ed = makeEditor([line], [caret(1, col)]);
                const mw = loadMarkWord(ed);
                mw.cmdMarkWord();
                const first = ed._sels[0];
                if (first.selectionStartColumn === first.positionColumn) continue;   // no word at this caret
                mw.cmdMarkWord();
                const s = ed._sels[0];
                const w = wordAt(line, first.selectionStartColumn);
                const got = line.slice(s.selectionStartColumn - 1, s.positionColumn - 1);
                if (!w || got !== w.word) disagree.push(line.trim() + ' @' + col + ': ' + got + ' vs ' + (w && w.word));
            }
        });
        check('the second press always selects what double-click would', disagree.length === 0, disagree.slice(0, 3).join(' | '));
    }

    section('Key binding');
    const { value: commands, stubs } = evalLiteral(commandsSrc);
    const entry = commands.find(c => c.id === 'markWord');
    check('EDITOR_COMMANDS has a markWord entry, so the gear panel can rebind it', !!entry);
    if (entry) {
        check('its label is "Mark Word"', entry.label === 'Mark Word', entry.label);
        check('its default chord is Ctrl+W', entry.def === 'Ctrl+W', entry.def);
        check('it runs cmdMarkWord', entry.run === stubs.cmdMarkWord);
    }
    const onCtrlW = commands.filter(c => c.def === 'Ctrl+W').map(c => c.id);
    check('no other command defaults to Ctrl+W', onCtrlW.length === 1, onCtrlW.join(', '));

    const shortcuts = evalLiteral(shortcutsSrc).value;
    check('Ctrl+W is not handed to the IDE (IDE_SHORTCUTS)', !shortcuts.some(s => s.combo === 'Ctrl+W'));

    const chordFromEvent = new Function(chordSrc + '\nreturn chordFromEvent;')();
    const pressed = chordFromEvent({ code: 'KeyW', key: 'w', ctrlKey: true, shiftKey: false, altKey: false, metaKey: false });
    check('a Ctrl+W keydown maps to the chord the entry is bound to', entry && pressed === entry.def, pressed);
}

// ---------- summary ----------
console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) {
    console.log('\nFailures:');
    failures.forEach(f => console.log('  - ' + f));
    process.exit(1);
}
