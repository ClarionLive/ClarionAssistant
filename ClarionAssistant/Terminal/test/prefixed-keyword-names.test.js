// prefixed-keyword-names.test.js — a Clarion keyword used as a NAME part (a prefix like While:Count,
// Loop:Idx, End:Date, or a field after ':' / '.' like Cus:End, Q.Loop, SELF.Case) is not a keyword. (46506010)
//
// Run:  node Terminal/test/prefixed-keyword-names.test.js [path/to/monaco-embeditor.html]
//
// Reported by ClarionClone (which ported CA's rules): typing 'While:Count += 1' outdented the line. Three
// paths read the leading word only up to the ':' and so saw WHILE/LOOP/CASE/END:
//   1. clarion-language.js indentationRules (Monaco smart indent on Enter): outdent on While:/End:/Of:...,
//      indent after Loop:/Case:/Group:...
//   2. clarion-formatter.js (Ctrl+I, format-line-on-Enter): leadingKeyword() -> mis-indented every following
//      line, dropped the line from assignment alignment, and the casing pass uppercased the prefix
//      (WHILE:Count) and keyword-named fields (Cus:END, Q.LOOP, SELF.CASE).
//   3. monaco-embeditor.html autoCaseWordBefore (live keyword casing): 'Cus:End ' -> 'Cus:END '.
// Point it at the pre-fix files and every group goes red.

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const LANG_PATH = process.argv[3] || path.join(__dirname, '..', 'clarion-language.js');
const F = require('../clarion-formatter.js');

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- 1. indentationRules ----------
section('1. Smart indent rules (clarion-language.js)');
let rules = null;
(function () {
    const sandbox = {
        monaco: { languages: {
            setLanguageConfiguration(id, cfg) { if (id === 'clarion') rules = cfg.indentationRules; },
            setMonarchTokensProvider() { }, registerFoldingRangeProvider() { }
        } },
        console
    };
    vm.createContext(sandbox);
    vm.runInContext(fs.readFileSync(LANG_PATH, 'utf8') + '\n;registerClarionLanguage();', sandbox);
})();
check('indentationRules captured', !!(rules && rules.increaseIndentPattern && rules.decreaseIndentPattern));
const inc = rules.increaseIndentPattern, dec = rules.decreaseIndentPattern;

const notOutdent = ['    While:Count += 1', '    End:Date = TODAY()', '    Of:Value = 2', '    Else:Flag = 1',
    '    Elsif:X = 1', '    Until:Done = TRUE', '    OROF:Code = 3'];
notOutdent.forEach(l => check('no outdent: ' + l.trim(), !dec.test(l)));
const notIndent = ['    Loop:Idx = 1', '    Case:Id = 2', '    Group:Name = x', '    If:Flag = 1', '    Of:Value = 2',
    '    Begin:Date = TODAY()', '    Queue:Browse:1.ResetSort(1)', '    Window:Opened = TRUE'];
notIndent.forEach(l => check('no indent after: ' + l.trim(), !inc.test(l)));
// GROUP/QUEUE/WINDOW/CLASS... may be labels (only the reserved words may not), so a structure label used as a
// qualifier is a name too (pipeline Run 1, Codex).
['    Queue.Id = 3', '    Group.Name = 2', '    Window.Opened = TRUE', '    Class.Init()', '    File.Record = 1']
    .forEach(l => check('no indent after dotted label: ' + l.trim(), !inc.test(l)));
// The real keywords still work.
['    END', '    WHILE x < 5', '    OF 1', '    ELSE', '    UNTIL done', '    .'].forEach(l => check('still outdents: ' + l.trim(), dec.test(l)));
['    LOOP', '    IF x = 1', '    CASE a', '    OF 1', '    GROUP', '    LOOP i = 1 TO 3'].forEach(l => check('still indents after: ' + l.trim(), inc.test(l)));

// ---------- 2. formatter ----------
section('2. Formatter (clarion-formatter.js)');
function fmt(lines) { return F.formatClarion(lines.join('\r\n'), {}).text.split('\r\n'); }
function lead(s) { return /^ */.exec(s)[0].length; }
(function () {
    const out = fmt(['MyProc PROCEDURE', '  CODE', '  LOOP 3 TIMES', '  While:Count += 1', '  End:Date = TODAY()',
        '  Loop:Idx = 1', '  Case:Id = 2', '  x = 1', '  END', '  RETURN']);
    const body = lead(out[3]);
    check('LOOP body is one tab in from LOOP', body === lead(out[2]) + 4, JSON.stringify(out[3]));
    check('End:Date stays in the LOOP body', lead(out[4]) === body, JSON.stringify(out[4]));
    check('Loop:Idx stays in the LOOP body', lead(out[5]) === body, JSON.stringify(out[5]));
    check('Case:Id does not open a level', lead(out[6]) === body, JSON.stringify(out[6]));
    check('the LOOP END aligns with LOOP', lead(out[8]) === lead(out[2]), JSON.stringify(out[8]));
    check('RETURN back at the code column', lead(out[9]) === lead(out[2]), JSON.stringify(out[9]));
    check('prefix keeps its case (While:Count)', /\bWhile:Count\b/.test(out[3]), JSON.stringify(out[3]));
    check('prefix keeps its case (End:Date)', /\bEnd:Date\b/.test(out[4]), JSON.stringify(out[4]));
})();
(function () {
    const out = fmt(['MyProc PROCEDURE', '  CODE', '  x = Cus:End + Q.Loop + SELF.Case', '  RETURN']);
    check('field after ":" keeps its case (Cus:End)', /Cus:End\b/.test(out[2]), JSON.stringify(out[2]));
    check('field after "." keeps its case (Q.Loop, SELF.Case)', /Q\.Loop\b/.test(out[2]) && /SELF\.Case\b/.test(out[2]), JSON.stringify(out[2]));
})();
(function () {
    // Assignment alignment: a prefixed keyword-named lvalue is an assignment like any other.
    const out = fmt(['MyProc PROCEDURE', '  CODE', '  Loop:Idx = 1', '  abc = 2', '  RETURN']);
    const eqA = out[2].indexOf('='), eqB = out[3].indexOf('=');
    check('Loop:Idx aligns with the next assignment', eqA > 0 && eqA === eqB, JSON.stringify(out.slice(2, 4)));
})();
(function () {
    // Real keywords are unchanged: a labelled LOOP with a prefix-style label, and plain keyword casing.
    const out = fmt(['MyProc PROCEDURE', '  CODE', 'Scan:Top LOOP i = 1 TO 3', '  if x then break.', '  end', '  RETURN']);
    check('prefix-style label + LOOP still opens a level', lead(out[3]) > lead(out[2].replace(/^Scan:Top\s*/, '')) || /^\s+IF/.test(out[3]), JSON.stringify(out.slice(2, 5)));
    check('keywords still cased (IF/BREAK/END)', /\bIF\b/.test(out[3]) && /\bBREAK\b/.test(out[3]) && /\bEND\b/.test(out[4]), JSON.stringify(out.slice(3, 5)));
})();

(function () {
    // A prefixed label + control keyword at module scope is a labelled statement, not a data declaration
    // (pipeline Run 1, debugger): it must be left as it was before the fix, not laid out at the data column.
    const src = ['  MEMBER()', 'Loop:Top LOOP'];
    const out = fmt(src);
    check('module-scope "Loop:Top LOOP" is not laid out as a declaration', out[1] === 'Loop:Top LOOP', JSON.stringify(out[1]));
    const decl = fmt(['  MEMBER()', 'Loop:Idx LONG']);
    check('module-scope "Loop:Idx LONG" is still a declaration', /^Loop:Idx\s{2,}LONG$/.test(decl[1]), JSON.stringify(decl[1]));
})();

// ---------- 3. live keyword casing ----------
section('3. Live keyword casing (monaco-embeditor.html autoCaseWordBefore)');
(function () {
    const html = fs.readFileSync(HTML_PATH, 'utf8');
    const a = html.indexOf('    function autoCaseWordBefore(');
    const b = html.indexOf('\n    function ', a + 10);
    if (a < 0 || b < 0) { check('autoCaseWordBefore found in the page', false); return; }
    const src = html.slice(a, b);
    function Range(sl, sc, el, ec) { this.startLineNumber = sl; this.startColumn = sc; this.endLineNumber = el; this.endColumn = ec; }
    const always = Object.create(null); (F.ALWAYS_KEYWORDS || []).forEach(w => always[w] = true);
    const builtins = Object.create(null); (F.BUILTIN_FUNCTIONS || []).forEach(w => builtins[w] = true);
    const sandbox = { keywordCaseMode: 'upper', fileMode: true, isEditableRange: () => true, monaco: { Range },
        alwaysKwSet: () => always, builtinFnSet: () => builtins, console };
    vm.createContext(sandbox);
    vm.runInContext(src, sandbox);
    // Types `typed` (ending with its boundary char) and returns the line after live casing.
    function type(typed) {
        let line = typed;
        const ed = {
            getModel: () => ({ getLineContent: () => line }),
            getPosition: () => ({ lineNumber: 1, column: line.length + 1 }),
            executeEdits: (_src, edits) => { const e = edits[0]; line = line.slice(0, e.range.startColumn - 1) + e.text + line.slice(e.range.endColumn - 1); }
        };
        sandbox.autoCaseWordBefore(ed, typed.charAt(typed.length - 1));
        return line;
    }
    check('always-keywords found', Object.keys(always).length > 10 && !!always.END && !!always.LOOP);
    check('"Cus:End " keeps End', type('    x = Cus:End ') === '    x = Cus:End ', type('    x = Cus:End '));
    check('"Q.Loop " keeps Loop', type('    x = Q.Loop ') === '    x = Q.Loop ', type('    x = Q.Loop '));
    check('"SELF.Case(" keeps Case', type('    SELF.Case(') === '    SELF.Case(', type('    SELF.Case('));
    check('"?If " (control equate) keeps If', type('    SELECT(?If ') === '    SELECT(?If ', type('    SELECT(?If '));
    check('plain "end " is still cased to END', type('    end ') === '    END ', type('    end '));
    check('plain "loop " is still cased to LOOP', type('    loop ') === '    LOOP ', type('    loop '));
})();

console.log('\n' + pass + ' passed, ' + fail + ' failed.');
process.exit(fail ? 1 : 0);
