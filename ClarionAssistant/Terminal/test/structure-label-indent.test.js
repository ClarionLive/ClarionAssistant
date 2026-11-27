// structure-label-indent.test.js — a LABEL spelled like a structure word is not a structure header. (b13bc411)
//
// Run:  node Terminal/test/structure-label-indent.test.js [path/to/clarion-language.js] [path/to/clarion-formatter.js]
//
// GROUP QUEUE RECORD FILE VIEW REPORT WINDOW APPLICATION CLASS INTERFACE MAP MODULE ITEMIZE JOIN are not
// reserved words (Clarion Language Reference, "Reserved Words"), so they may be labels: "Module  LONG",
// "Map  PROCEDURE(*CRecordSet Rs)", "Queue  &MyQueue". increaseIndentPattern accepted them followed by any
// whitespace, so Monaco indented the line after such a declaration. A real header puts '(' ',' '!' ';' '|' or
// the end of the line right after the keyword (the shape PR #316 gives ModernEmbeditorDiagnostics.cs).
// The reserved openers (IF LOOP CASE ... ELSIF) can never be labels and keep their (?![:.]) rule.
// Point it at the pre-fix clarion-language.js and the "label" group goes red.

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const LANG_PATH = process.argv[2] || path.join(__dirname, '..', 'clarion-language.js');
const FMT_PATH = path.resolve(process.argv[3] || path.join(__dirname, '..', 'clarion-formatter.js'));

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

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
const inc = rules.increaseIndentPattern;

section('1. A label spelled like a structure word does not indent the next line');
const labels = [
    'Module         LONG',
    'Map                  PROCEDURE(*CRecordSet Rs)',
    '  map &= nameMap',
    'Join   LONG',
    'Itemize  BYTE',
    'Group  LONG',
    'Queue  &MyQueue',
    'Window   &Window',
    'Class  &MyClass',
    'File  STRING(20)',
    'Report  BYTE',
    'View  LONG',
    'Record  LONG',
    'Application  LONG',
    'Interface  &IMyIface',
    'Menu  LONG',
    'Toolbar              ToolbarClass',
    // lowercase / mixed case
    'module  long',
    'map  procedure(long x)',
    'group  long',
    'queue  &MyQueue',
    'window  &window',
    'class  &MyClass',
    'file  string(20)',
    'record  long',
    'itemize  byte',
    'join  long',
    // indented uses (an assignment or a call through the label)
    '  Queue &= NEW MyQueue',
    '  Window &= SELF.Window',
    '  Group = 1',
    '  Record = Cus:Record',
    '  File{PROP:Name} = x'
];
labels.forEach(l => check('no indent after: ' + l, !inc.test(l)));

section('2. Real structure headers still indent the next line');
const headers = [
    'GROUP', '  GROUP', '  GROUP,PRE(G)', '  GROUP(MyGroupType)', '  GROUP (MyGroupType)',
    '  QUEUE,PRE(Q)', 'QUEUE(MyQ)', '  QUEUE',
    '  CLASS(WindowManager)', '  CLASS,TYPE', '  CLASS',
    '  INTERFACE', '  INTERFACE(IBase)',
    '  MAP', "  MODULE('x.clw')", "  MAP; MODULE('x.clw')",
    '  ITEMIZE,PRE(I)', '  ITEMIZE', '  ITEMIZE(1)',
    "  FILE,DRIVER('TOPSPEED'),PRE(F)", '  RECORD', '  RECORD,PRE()',
    '  VIEW(Customers)', '  JOIN(Key)', '  JOIN(Cus:KeyId, Ord:CustId)',
    "  WINDOW('Title'),AT(0,0,100,100)", '  REPORT,AT(0,0,100,100)', "  APPLICATION('App')",
    '  TOOLBAR,AT(0,0)', '  MENUBAR', "  MENU('&File')", '  SHEET,AT(0,0)', "  TAB('One')", "  OPTION('x'),USE(?o)",
    // comment and continuation forms
    '  GROUP ! c', '  GROUP! c', '  QUEUE |', "  WINDOW('Title'), |", '  MAP ! prototypes', '  CLASS;',
    // lowercase
    '  group,pre(g)', '  queue', '  map', "  module('x.clw')", '  class(WindowManager)', '  itemize,pre(i)'
];
headers.forEach(l => check('indents after: ' + l, inc.test(l)));

section('3. The reserved-word openers are unchanged');
['    IF x', '    IF x = 1 THEN', '    LOOP', '    LOOP i = 1 TO 3', '    CASE a', '    OF 1', '    OROF 2', '    ELSE',
    '    ELSIF y', '    BEGIN', '    EXECUTE n', '    ACCEPT', '    if x', '    loop', '    case a']
    .forEach(l => check('indents after: ' + l.trim(), inc.test(l)));
['    Loop:Idx = 1', '    If:Flag = 1', '    Case:Id = 2', '    Queue.Id = 3', '    Window:Opened = TRUE', '    Group:Name = x']
    .forEach(l => check('no indent after name part: ' + l.trim(), !inc.test(l)));

section('4. One-liners ending in "." still do not indent');
['    IF x THEN y = 1.', '    LOOP 3 TIMES; x += 1.', '  GROUP,PRE(G).', '  MAP.', '  QUEUE(MyQ).', '  CLASS(Base).',
    "  MODULE('x.clw').", '    CASE a; OF 1; x = 1.']
    .forEach(l => check('no indent after one-liner: ' + l.trim(), !inc.test(l)));
check('a "." inside a string is not a one-liner', inc.test("  MODULE('x.clw')"));
check('a "." in a comment is not a one-liner', inc.test('  GROUP ! ends here.'));

section('5. A column-1 LABELLED header still does not indent (kept: its fields must stay in column 1)');
['Rec  GROUP', 'Rec  GROUP,PRE(R)', 'Q  QUEUE,PRE(Q)', 'ThisWindow  CLASS(WindowManager)', 'Customers  FILE,DRIVER(\'TOPSPEED\')',
    "Window  WINDOW('Caption'),AT(0,0,100,100)", 'Clr  ITEMIZE,PRE(Clr)']
    .forEach(l => check('no indent after labelled header: ' + l, !inc.test(l)));

section('6. Formatter (clarion-formatter.js): a reference-typed label is a declaration, not a structure');
// labelLikeStructKeyword only admitted a type that starts with a letter ("Toolbar ToolbarClass"), so
// "Queue &MyQueue" / "Window &Window" / "Map &Foo" opened a phantom structure that never closed and pushed
// every following line (CODE included) to the data column. An indented "map &= nameMap" in CODE was read
// as the module-level MAP keyword.
(function () {
    const F = require(FMT_PATH);
    function fmt(lines) { return F.formatClarion(lines.join('\r\n'), {}).text.split('\r\n'); }
    function lead(s) { return /^ */.exec(s)[0].length; }

    let out = fmt(['  MEMBER()', 'MyClass  CLASS,TYPE', 'Module         LONG', 'Queue  &MyQueue',
        'Map                  PROCEDURE(*CRecordSet Rs)', 'Init  PROCEDURE', '  END']);
    check('CLASS member "Module LONG" stays a column-1 declaration', /^Module\s+LONG$/.test(out[2]), JSON.stringify(out[2]));
    check('CLASS member "Queue &MyQueue" stays a column-1 declaration', /^Queue\s+&MyQueue$/.test(out[3]), JSON.stringify(out[3]));
    check('CLASS member "Map PROCEDURE(...)" stays a column-1 declaration', /^Map\s+PROCEDURE\(/.test(out[4]), JSON.stringify(out[4]));
    check('"Queue &MyQueue" aligns its type with its neighbours', out[3].indexOf('&') === out[2].indexOf('LONG'), JSON.stringify(out.slice(2, 5)));

    out = fmt(['  MEMBER()', 'G  GROUP', 'Module  LONG', 'Queue  &MyQueue', 'Join  LONG', 'Itemize  BYTE', 'X  LONG', '  END']);
    check('GROUP member "Queue &MyQueue" stays in column 1', /^Queue\s+&MyQueue$/.test(out[3]), JSON.stringify(out[3]));
    check('GROUP members align their types', out[3].indexOf('&') === out[2].indexOf('LONG') && out[6].indexOf('LONG') === out[2].indexOf('LONG'),
        JSON.stringify(out.slice(2, 7)));

    out = fmt(['MyProc PROCEDURE', 'Loc  GROUP', 'Window  &Window', 'Record  LONG', 'Map  &Foo', 'Y  LONG', '  END',
        '  CODE', '  map &= nameMap', '  Queue &= NEW MyQueue', '  x = 1', '  RETURN']);
    check('"Window &Window" in a GROUP is a declaration', /^Window\s+&/i.test(out[2]), JSON.stringify(out[2]));
    check('"Map &Foo" in a GROUP is a declaration', /^Map\s+&Foo$/.test(out[4]), JSON.stringify(out[4]));
    check('CODE is back at the code column', lead(out[7]) === lead(out[11]) && lead(out[7]) < 8, JSON.stringify([out[7], out[11]]));
    check('"map &= nameMap" is a statement in the CODE body', lead(out[8]) === lead(out[10]), JSON.stringify([out[8], out[10]]));
    check('"Queue &= NEW MyQueue" is a statement in the CODE body', lead(out[9]) === lead(out[10]), JSON.stringify([out[9], out[10]]));

    // Real headers and the module-level MAP are unchanged.
    out = fmt(['  MEMBER()', 'Module  LONG', 'Queue  &MyQueue', '  MAP', '  END', 'MyProc PROCEDURE', '  CODE', '  RETURN']);
    check('module MAP still sits at the preferred column', lead(out[3]) === lead(out[0]) && /^\s+MAP$/.test(out[3]), JSON.stringify(out[3]));
    check('module MAP END aligns with MAP', lead(out[4]) === lead(out[3]), JSON.stringify(out[4]));
    check('module "Queue &MyQueue" is a declaration', /^Queue\s+&MyQueue$/.test(out[2]), JSON.stringify(out[2]));

    // Code review notes: 'Map = x' (named in the formatter's comment), tab-separated labels, and an empty "MAP.".
    out = fmt(['MyProc PROCEDURE', '  CODE', '  Map = 5', '  x = 1', '  RETURN']);
    check('"Map = 5" is a statement in the CODE body', lead(out[2]) === lead(out[3]), JSON.stringify(out.slice(2, 4)));
    out = fmt(['  MEMBER()', 'G  GROUP', 'Module\tLONG', 'Queue\t&MyQueue', 'X  LONG', '  END']);
    check('tab-separated "Module<TAB>LONG" / "Queue<TAB>&MyQueue" stay column-1 declarations',
        /^Module\s+LONG$/.test(out[2]) && /^Queue\s+&MyQueue$/.test(out[3]), JSON.stringify(out.slice(2, 4)));
    out = fmt(['  MEMBER()', '  MAP.', 'MyProc PROCEDURE', '  CODE', '  RETURN']);
    check('an empty module "MAP." stays at the preferred column and closes itself',
        /^\s+MAP\.$/.test(out[1]) && lead(out[1]) === lead(out[0]) && /^MyProc\s+PROCEDURE$/.test(out[2]), JSON.stringify(out.slice(0, 3)));
})();

console.log('\n' + pass + ' passed, ' + fail + ' failed.');
process.exit(fail ? 1 : 0);
