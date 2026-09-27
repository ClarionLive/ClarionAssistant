// anchored-reindent.test.js — guards the CA Embeditor's re-indent paths (Enter after END, Enter on a line,
// Ctrl+I) against the formatter's ABSOLUTE depth being wrong in a huge generated module. (16d140e9)
//
// Run:  node Terminal/test/anchored-reindent.test.js [path/to/monaco-embeditor.html]
//
// THE BUG. In the CA Embeditor the buffer is the whole generated module (86,722 lines on InventoryTable).
// ClarionFormatter.formatClarionRange computes each line's column from the structure depth counted from
// line 1, and generated code it does not model (here: OMIT blocks that hide an unclosed IF) leaves
// structures open, so every later line sits one indent further right per miscount. Pressing Enter after an
// embed's END re-formatted the IF..END block out to ~column 125.
//
// Like its neighbours this test SLICES the page's own code (cmdFormat and the format-on-Enter section) out
// of monaco-embeditor.html and runs it against a tiny line-based Monaco fake plus the real formatter.
// Point it at the pre-fix page (git show 74f7961:...) and the "generated module" checks go red.

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');
const F = require('../clarion-formatter.js');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}
const cmdFormatSrc = slice(html, '    function cmdFormat() {', '    // Add the right-click', 'cmdFormat');
const onEnterSrc = slice(html, '    // ----- Format the line on Enter', '    function autoCaseWordBefore(', 'format-on-Enter section');
if (!/function formatBlockNow/.test(onEnterSrc)) throw new Error('extracted section has no formatBlockNow');
if (!/function formatLineNow/.test(onEnterSrc)) throw new Error('extracted section has no formatLineNow');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- Monaco fakes ----------
function Range(sl, sc, el, ec) { this.startLineNumber = sl; this.startColumn = sc; this.endLineNumber = el; this.endColumn = ec; }
function makeModel(lines) {
    const m = {
        lines: lines.slice(),
        getValue() { return this.lines.join('\r\n'); },
        getEOL() { return '\r\n'; },
        getLineCount() { return this.lines.length; },
        getLineContent(n) { return this.lines[n - 1]; },
        getLineMaxColumn(n) { return this.lines[n - 1].length + 1; },
        getValueInRange(r) {
            const out = [];
            for (let n = r.startLineNumber; n <= r.endLineNumber; n++) {
                let s = this.lines[n - 1];
                if (n === r.endLineNumber) s = s.slice(0, r.endColumn - 1);
                if (n === r.startLineNumber) s = s.slice(r.startColumn - 1);
                out.push(s);
            }
            return out.join('\n');
        },
        applyEdit(e) {
            const r = e.range;
            const head = this.lines[r.startLineNumber - 1].slice(0, r.startColumn - 1);
            const tail = this.lines[r.endLineNumber - 1].slice(r.endColumn - 1);
            const repl = (head + e.text + tail).split(/\r\n|\n/);
            this.lines.splice(r.startLineNumber - 1, r.endLineNumber - r.startLineNumber + 1, ...repl);
        }
    };
    return m;
}
function makeEditor(model, sel) {
    let pos = { lineNumber: 1, column: 1 };
    let selection = sel || new Range(1, 1, 1, 1);
    return {
        getModel() { return model; },
        getPosition() { return pos; },
        setPosition(p) { pos = p; },
        getSelection() { const s = selection; s.isEmpty = () => s.startLineNumber === s.endLineNumber && s.startColumn === s.endColumn; return s; },
        setSelection(s) { selection = s; },
        executeEdits(src, edits) { edits.forEach(e => model.applyEdit(e)); }
    };
}

// Build the page functions around one editor.
function load(ed) {
    const factory = new Function('monaco', 'ClarionFormatter', 'ed',
        'var fileMode = false;\n' +
        'function isEditableRange() { return true; }\n' +
        'function linesEditable() { return true; }\n' +
        'function editGuardToast() {}\n' +
        'function activeEd() { return ed; }\n' +
        'function selLineSpan() { var s = ed.getSelection(); return [s.startLineNumber, s.endLineNumber]; }\n' +
        'function applyGuardedEdits(edits) { ed.executeEdits("guarded", edits); }\n' +
        'function formatterOptions() { return { tabSize: 4, insertSpaces: true }; }\n' +
        cmdFormatSrc + '\n' + onEnterSrc + '\n' +
        'return { cmdFormat: cmdFormat, formatBlockNow: formatBlockNow, formatLineNow: formatLineNow };');
    return factory({ Range: Range }, F, ed);
}
function indentOf(s) { return /^[ \t]*/.exec(s)[0].length; }

// ---------- fixtures ----------
// A generated-looking module: MEMBER, a procedure with a QUEUE and a WINDOW (SHEET/TAB/controls), then a
// CODE section full of OMIT('***') blocks each hiding an IF with no END — the formatter doesn't model OMIT,
// so each leaves an IF open. 28 of them puts the absolute depth ~112 columns deep, like the Owner's ~125.
function generatedPrefix(nOmits) {
    const L = [];
    L.push("  MEMBER('inv.app')", '', 'InventoryTable       PROCEDURE', '');
    L.push('Q                    QUEUE', 'Id                     LONG', 'Name                   STRING(40)', '                     END');
    L.push("QuickWindow          WINDOW('Inventory'),AT(,,400,200),GRAY", '                       SHEET,AT(4,4,390,180),USE(?Sheet)',
        "                         TAB('General'),USE(?Tab1)", "                           BUTTON('OK'),AT(1,1),USE(?OK)",
        '                         END', '                       END', '                     END');
    L.push('  CODE');
    for (let k = 0; k < nOmits; k++) L.push("  OMIT('***')", '  IF Loc:X = ' + k + ' THEN', '    Loc:Y = 1', '***', '  ! generated');
    for (let k = 0; k < 150; k++) L.push('  Loc:Z = ' + k);
    return L;
}
const EMBED = [
    "            IF GlobalRequest = ChangeRecord AND ~PASSWORD('IN',103,1,0,0,0)",
    "              MESSAGE('You do not have Security rights to edit items.')",
    '              POPBIND()',
    '              RETURN',
    '',
    '            END'
];

// ---------- 1. Enter after END in a mis-nesting generated module ----------
section('Enter after END on an embed block inside a huge generated module');
{
    const prefix = generatedPrefix(28);
    const op = prefix.length + 1, end = op + EMBED.length - 1;
    // Prove the fixture CAN fail: the raw formatter really does throw the block far right here.
    const raw = F.formatClarionRange(prefix.concat(EMBED).join('\r\n'), op, end, { alignAssignments: false }).text.split('\r\n');
    check('fixture: absolute formatter puts the opener far right (> col 60)', indentOf(raw[op - 1]) > 60, 'col ' + indentOf(raw[op - 1]));

    const model = makeModel(prefix.concat(EMBED, ['']));
    const page = load(makeEditor(model));
    page.formatBlockNow(makeEditor(model), end);
    const opener = model.getLineContent(op), closer = model.getLineContent(end);
    check('opener stays where the user sees it (col 12)', indentOf(opener) === 12, 'col ' + indentOf(opener) + ': ' + JSON.stringify(opener));
    check('END aligns with its opener', indentOf(closer) === indentOf(opener), 'END col ' + indentOf(closer));
    const body = [op + 1, op + 2, op + 3].map(n => indentOf(model.getLineContent(n)));
    check('body is one indent inside the opener', body.every(c => c === 16), 'body cols ' + body.join(','));
    check('nothing in the block goes past col 40',
        model.lines.slice(op - 1, end).every(s => s.trim() === '' || indentOf(s) < 40));
    check('the generated code above is untouched', model.lines.slice(0, op - 1).join('\n') === prefix.join('\n'));
}

// ---------- 2. Enter on an ordinary line in the same module ----------
section('Enter on a single line (formatLineNow) inside the generated module');
{
    const prefix = generatedPrefix(28);
    const lines = prefix.concat(['            IF Loc:A = 1', '      Loc:B = 2', '']);
    const model = makeModel(lines);
    const ed = makeEditor(model);
    const page = load(ed);
    const L = prefix.length + 2;
    page.formatLineNow(ed, L);
    check('body line lands one indent inside the line above (col 16)', indentOf(model.getLineContent(L)) === 16,
        'col ' + indentOf(model.getLineContent(L)));
}

// ---------- 3. Ctrl+I on a selection inside the generated module ----------
section('Ctrl+I (cmdFormat) on a selection inside the generated module');
{
    const prefix = generatedPrefix(28);
    const blk = ['            IF Loc:A = 1', '        Loc:B = 2', '         Loc:C = 3', '   END'];
    const model = makeModel(prefix.concat(blk, ['']));
    const a = prefix.length + 1, b = a + blk.length - 1;
    const ed = makeEditor(model, new Range(a + 1, 1, b, 1));        // select the body + END, not the IF
    const page = load(ed);
    page.cmdFormat();
    const cols = [a, a + 1, a + 2, a + 3].map(n => indentOf(model.getLineContent(n)));
    check('selection re-indents relative to the IF above it', cols.join(',') === '12,16,16,12', 'cols ' + cols.join(','));
}

// ---------- 4. Well-formed file: anchoring changes nothing ----------
section('Well-formed source: result identical to the absolute formatter');
{
    // The lines the fix anchors on (CODE, the IF opener, the LOOP) sit exactly where the formatter puts
    // them; everything below is mis-indented so the format has real work to do.
    const src = [
        "  MEMBER('inv.app')", '',
        'MyProc               PROCEDURE', '',
        'Loc:A                LONG', '',
        '    CODE',
        '    IF Loc:A = 1',
        '        LOOP 3 TIMES',
        '        Loc:A += 1',
        ' IF Loc:A > 2',
        'BREAK',
        '     END',
        '    END',
        '  END'
    ];
    const end = src.length, op = 8;
    const expected = F.formatClarionRange(src.join('\r\n'), op, end, { alignAssignments: false, tabSize: 4, insertSpaces: true }).text.split('\r\n');
    check('fixture: the absolute format actually changes the block', expected.slice(op - 1, end).join('\n') !== src.slice(op - 1, end).join('\n'));
    const model = makeModel(src.concat(['']));
    const ed = makeEditor(model);
    load(ed).formatBlockNow(ed, end);
    check('Enter-after-END result == absolute formatter result', model.lines.slice(0, end).join('\n') === expected.slice(0, end).join('\n'),
        '\n' + model.lines.slice(op - 1, end).join('\n'));

    const model2 = makeModel(src.concat(['']));
    const ed2 = makeEditor(model2);
    load(ed2).formatLineNow(ed2, 10);
    check('Enter-on-line result == absolute formatter result', model2.getLineContent(10) === expected[9],
        JSON.stringify(model2.getLineContent(10)) + ' vs ' + JSON.stringify(expected[9]));

    const model3 = makeModel(src.concat(['']));
    const ed3 = makeEditor(model3, new Range(9, 1, 14, 1));
    load(ed3).cmdFormat();
    const exp3 = F.formatClarionRange(src.join('\r\n'), 9, 14, { tabSize: 4, insertSpaces: true }).text.split('\r\n');
    check('Ctrl+I result == absolute formatter result', model3.lines.slice(8, 14).join('\n') === exp3.slice(8, 14).join('\n'),
        '\n' + model3.lines.slice(8, 14).join('\n'));
}

// ---------- 5. Column-1 label lines are never anchors (review of 130dbac) ----------
// A ROUTINE / PROCEDURE header or a data declaration puts its label in column 1 and its keyword at the
// preferred/data column — the gap reflects label length, not structure depth. Anchoring on it shifted the
// Procedure Routines embed's CODE and statements into COLUMN 1, where Clarion reads them as labels.
function absFmt(src, a, b, align) {
    return F.formatClarionRange(src.join('\r\n'), a, b, { alignAssignments: !!align, tabSize: 4, insertSpaces: true }).text.split('\r\n');
}
section('ROUTINE header above: CODE and statements keep their depth indent');
{
    const src = [
        "  MEMBER('inv.app')", '',
        'MyProc               PROCEDURE', '',
        '    CODE',
        '    DO MyRoutine', '',
        'MyRoutine ROUTINE',
        '  CODE',
        '  x = 1',
        '  y = 2'
    ];
    const expected = absFmt(src, 9, 9);
    const m1 = makeModel(src.concat([''])), e1 = makeEditor(m1);
    load(e1).formatLineNow(e1, 9);
    check('Enter on CODE under a ROUTINE header == absolute format (never column 1)',
        m1.getLineContent(9) === expected[8] && indentOf(m1.getLineContent(9)) > 0,
        JSON.stringify(m1.getLineContent(9)) + ' vs ' + JSON.stringify(expected[8]));
    const exp2 = absFmt(src, 9, 10, true);
    const m2 = makeModel(src.concat([''])), e2 = makeEditor(m2, new Range(9, 1, 10, 1));
    load(e2).cmdFormat();
    check('Ctrl+I on CODE + x = 1 under a ROUTINE header == absolute format',
        m2.lines.slice(8, 10).join('\n') === exp2.slice(8, 10).join('\n') && indentOf(m2.getLineContent(10)) > 0,
        '\n' + m2.lines.slice(8, 10).join('\n'));
    const m3 = makeModel(src.concat([''])), e3 = makeEditor(m3);
    load(e3).formatLineNow(e3, 11);
    check('statement under the ROUTINE body never lands in column 1', indentOf(m3.getLineContent(11)) > 0,
        JSON.stringify(m3.getLineContent(11)));
}

section('Labelled QUEUE / GROUP: Enter after END keeps the data-column layout');
{
    const src = [
        "  MEMBER('inv.app')", '',
        'MyProc               PROCEDURE', '',
        'Q  QUEUE',
        'VeryLongFieldLabelName LONG',
        'B      LONG',
        'G GROUP',
        'C LONG',
        ' END',
        '  END',
        '    CODE'
    ];
    const expected = absFmt(src, 5, 11);
    const m = makeModel(src.concat([''])), e = makeEditor(m);
    load(e).formatBlockNow(e, 11);
    check('labelled QUEUE block == absolute format', m.lines.slice(4, 11).join('\n') === expected.slice(4, 11).join('\n'),
        '\n' + m.lines.slice(4, 11).join('\n') + '\n--- expected ---\n' + expected.slice(4, 11).join('\n'));
    const exp2 = absFmt(src, 8, 10);
    const m2 = makeModel(src.concat([''])), e2 = makeEditor(m2);
    load(e2).formatBlockNow(e2, 10);
    check('nested labelled GROUP block == absolute format', m2.lines.slice(7, 10).join('\n') === exp2.slice(7, 10).join('\n'),
        '\n' + m2.lines.slice(7, 10).join('\n'));
}

section('Well-formed PROCEDURE file with a labelled statement: identical to the absolute format');
{
    const src = [
        "  MEMBER('inv.app')", '',
        'MyProc               PROCEDURE', '',
        'Loc:A                LONG', '',
        '    CODE',
        'Scan    LOOP 3 TIMES',
        '  Loc:A += 1',
        '       IF Loc:A > 2',
        '  BREAK',
        '   END',
        '  END',
        '    RETURN'
    ];
    const exp = absFmt(src, 8, 13);
    const m = makeModel(src.concat([''])), e = makeEditor(m);
    load(e).formatBlockNow(e, 13);
    check('Enter after END of "Scan LOOP" == absolute format', m.lines.slice(7, 13).join('\n') === exp.slice(7, 13).join('\n'),
        '\n' + m.lines.slice(7, 13).join('\n') + '\n--- expected ---\n' + exp.slice(7, 13).join('\n'));
    const exp9 = absFmt(src, 9, 9);
    const m2 = makeModel(src.concat([''])), e2 = makeEditor(m2);
    load(e2).formatLineNow(e2, 9);
    check('Enter on the line under "Scan LOOP" == absolute format', m2.getLineContent(9) === exp9[8],
        JSON.stringify(m2.getLineContent(9)) + ' vs ' + JSON.stringify(exp9[8]));
    // An already-formatted file: knock ONE line out of place at a time and press Enter on it. Its anchor
    // above is always where the formatter puts it (including the column-1 header, declaration and "Scan"
    // label lines), so the result must be exactly the absolute format.
    const good = absFmt(src, 1, src.length).slice(0, src.length);
    let bad = [];
    for (let L = 5; L <= good.length; L++) {
        if (!/^[ \t]+\S/.test(good[L - 1])) continue;         // only indented code lines can be knocked out
        const knocked = good.slice(); knocked[L - 1] = '  ' + good[L - 1].trim();
        const mm = makeModel(knocked.concat([''])), ee = makeEditor(mm);
        load(ee).formatLineNow(ee, L);
        if (mm.getLineContent(L) !== good[L - 1]) bad.push(L + ': ' + JSON.stringify(mm.getLineContent(L)) + ' vs ' + JSON.stringify(good[L - 1]));
    }
    check('Enter on each knocked-out line of a formatted file restores the absolute format', bad.length === 0, bad.join('; '));
}

section('Labelled statement INSIDE a drifted embed block moves with the block, label stays in column 1');
{
    const prefix = generatedPrefix(28);
    const blk = ['            IF Loc:A = 1', 'Scan LOOP 3 TIMES', '    BREAK', '  END', '            END'];
    const op = prefix.length + 1, end = op + blk.length - 1;
    const m = makeModel(prefix.concat(blk, [''])), e = makeEditor(m);
    load(e).formatBlockNow(e, end);
    const got = m.lines.slice(op - 1, end);
    check('block re-indents relative to the IF; "Scan" keeps column 1 with LOOP one indent in',
        got.join('\n') === ['            IF Loc:A = 1', 'Scan            LOOP 3 TIMES', '                    BREAK', '                END', '            END'].join('\n'),
        '\n' + got.join('\n'));
}

// ---------- 6. An indented line is never shifted into column 1 ----------
section('Shift clamps at column 2, never column 1');
{
    // Drifted module; the user's IF sits at column 2 and the line after it closes a structure the
    // formatter thinks was opened far above, so its relative shift goes below zero.
    const prefix = generatedPrefix(28);
    const lines = prefix.concat(['  IF Loc:A = 1', '    Loc:B = 2', '  END', '  END', '']);
    const m = makeModel(lines), e = makeEditor(m, new Range(prefix.length + 2, 1, prefix.length + 4, 1));
    load(e).cmdFormat();
    const got = [prefix.length + 2, prefix.length + 3, prefix.length + 4].map(n => m.getLineContent(n));
    check('no formatted code line lands in column 1', got.every(s => /^[ \t]/.test(s)), JSON.stringify(got));
}

// ---------- 7. No anchor in reach: never throw a line far right ----------
section('No anchor within the look-back: a wild absolute column is not applied');
{
    const prefix = generatedPrefix(28);
    const blanks = []; for (let k = 0; k < 250; k++) blanks.push('');
    const lines = prefix.concat(blanks, ['            Loc:Q = 1', '']);
    const L = prefix.length + blanks.length + 1;
    const m = makeModel(lines), e = makeEditor(m);
    load(e).formatLineNow(e, L);
    check('line with no anchor within 200 lines stays put (< col 40)', indentOf(m.getLineContent(L)) < 40,
        'col ' + indentOf(m.getLineContent(L)));
}

console.log('\n' + pass + ' passed, ' + fail + ' failed');
if (fail) { console.log('\nFailures:\n  ' + failures.join('\n  ')); process.exit(1); }
