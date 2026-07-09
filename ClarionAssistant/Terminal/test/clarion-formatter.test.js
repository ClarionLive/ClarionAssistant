// clarion-formatter.test.js — zero-dependency Node test harness for the Clarion Smart Formatter.
//
// Run:  node Terminal/test/clarion-formatter.test.js
//
// FIXTURE-DRIVEN. The native Smart Formatter is the spec (ticket 67abc2e8). Drop John's golden
// before/after samples into fixtures/<name>.before.txt and fixtures/<name>.after.txt — the runner
// auto-discovers every pair, formats the .before with John's default settings, and diffs against
// the .after. Iterate clarion-formatter.js until all pairs are green.
//
// Until fixtures land, a few structural smoke checks below guard the settings-independent mechanics
// (EOL preservation, comment pinning, string-safe comment stripping).

var fs = require('fs');
var path = require('path');
var F = require('../clarion-formatter.js');

var pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  ✓ ' + name); }
    else { fail++; console.log('  ✗ ' + name + (detail ? '\n      ' + detail : '')); }
}

// ---- Structural smoke checks (fixture-independent) ----
console.log('Structural mechanics:');
ok('module loads with API', typeof F.formatClarion === 'function');
ok('defaults match John (col 31, mult 2)', F.DEFAULTS.preferredColumn === 31 && F.DEFAULTS.contLineMultiplier === 2);

(function () {
    var crlf = F.formatClarion('  CODE\r\n  Foo\r\n');
    ok('CRLF preserved', crlf.eol === '\r\n');
    var lf = F.formatClarion('  CODE\n  Foo\n');
    ok('LF preserved', lf.eol === '\n');
})();

(function () {
    // '!' inside a string must NOT be treated as a comment start.
    var s = F._internal.stripComment("  Msg = 'hi! there'  ! real comment");
    ok('string-safe comment strip', s.indexOf("'hi! there'") !== -1 && s.indexOf('real comment') === -1, JSON.stringify(s));
})();

(function () {
    // A col-1 comment stays in col 1 when dontIndentCol1Comments is on (it is, by default).
    var r = F.formatClarion('  CODE\r\n  IF x\r\n!pinned\r\n  END\r\n');
    var hasPinned = r.text.split(/\r\n/).some(function (l) { return l === '!pinned'; });
    ok('col-1 comment stays pinned', hasPinned);
})();

// ---- Assignment alignment (Upper Park "Format Assignment", folded into Ctrl+I) ----
// 2 spaces before/after, left operands padded to the longest so the '=' line up.
console.log('\nAssignment alignment:');
(function () {
    var src = "  System{Prop:Icon} = '~Log1.ico'\n  0{PROP:Icon} = '~<1>'\n";
    var got = F.formatClarion(src, { alignAssignments: true }).text.replace(/\r\n/g, '\n');
    var lines = got.split('\n').filter(function (l) { return l.trim() !== ''; });
    var c0 = lines[0].indexOf('='), c1 = lines[1].indexOf('=');
    ok('= operators aligned', c0 > 0 && c0 === c1, 'cols ' + c0 + ' vs ' + c1 + '\n      ' + JSON.stringify(lines));
    ok('2 spaces before on longest operand', /System\{Prop:Icon\}  =/.test(lines[0]), JSON.stringify(lines[0]));
    ok('2 spaces after operator', /=  '/.test(lines[0]) && /=  '/.test(lines[1]), JSON.stringify(lines));
})();

// ---- Keyword case normalization ("Complete": keywords upper, other names as-declared) ----
console.log('\nKeyword case:');
(function () {
    function f(src) { return F.formatClarion(src, { alignAssignments: false }).text.replace(/\r\n/g, '\n').trim(); }
    // statement keyword: 'of' -> 'OF' (John's example)
    ok("'of' -> 'OF'", /\bOF\b/.test(f("        of Button:OK")) && !/\bof\b/.test(f("        of Button:OK")));
    // type keyword in type position uppercases; field/type stays as the user named non-keywords
    ok("'long'/'like' type -> upper", /\bLONG\b/.test(f("Count   long")) && /\bLIKE\b/.test(f("Stock   like(ITC:in_stock)")));
    // control-flow words upcased anywhere
    ok("'if'/'then'/'end' -> upper", f("  if x then y.").indexOf('IF') >= 0 && f("  if x then y.").indexOf('THEN') >= 0);
    // CONTEXT-AWARE: a VARIABLE named like a keyword (right side of an assignment) is left as declared
    ok("variable 'Group' left as-declared", /=\s+Group\b/.test(f("  x = Group")) && !/=\s+GROUP\b/.test(f("  x = Group")));
    ok("variable 'Name' left as-declared", /=\s+Name\b/.test(f("  x = Name")));
    // strings are never touched
    ok("string contents untouched", f("  msg = 'if of end'").indexOf("'if of end'") >= 0);
    // 'asis' option leaves everything
    ok("keywordCase asis leaves 'of'", F.formatClarion("  of x", { keywordCase: 'asis', alignAssignments: false }).text.indexOf('of') >= 0);
})();

// ---- Built-in FUNCTION casing (GitHub #61: upper/clip/getini were not cased by Ctrl+I) ----
console.log('\nBuilt-in function case:');
(function () {
    function f(src, o) {
        var opts = { alignAssignments: false };
        if (o) for (var k in o) opts[k] = o[k];
        return F.formatClarion(src, opts).text.replace(/\r\n/g, '\n');
    }
    // BoxSoft's exact line from the issue
    var box = f("    IF upper(clip(GetIni('startup','debug','',clip(GLS:INIFileName)))) = 'ON'");
    ok('upper( -> UPPER(', box.indexOf('UPPER(') >= 0, box);
    ok('clip( -> CLIP( (both)', (box.match(/CLIP\(/g) || []).length === 2, box);
    ok('GetIni( -> GETINI(', box.indexOf('GETINI(') >= 0, box);
    ok('GLS:INIFileName untouched', box.indexOf('GLS:INIFileName') >= 0, box);
    ok("string 'ON' untouched", box.indexOf("'ON'") >= 0, box);
    // call-position gate: same names NOT followed by '(' are variables -> as declared
    ok("variable 'Len' left as-declared", /=\s+Len\b/.test(f('  x = Len')) && !/=\s+LEN\b/.test(f('  x = Len')));
    ok("variable 'Date' left as-declared", /=\s+Date\s*\+/.test(f('  x = Date + 1')), f('  x = Date + 1'));
    // method calls / prefixed fields keep the user's casing
    ok('SELF.Open( untouched', f('  SELF.Open(win)').indexOf('SELF.Open(') >= 0, f('  SELF.Open(win)'));
    ok('ThisWindow.Update( untouched', f('  ThisWindow.Update()').indexOf('.Update(') >= 0);
    ok('Cus:Name( untouched', f('  x = Cus:Name(1)').indexOf('Cus:Name(') >= 0);
    // space between name and paren still counts as a call
    ok('upper (x) with space -> UPPER (x)', f('  y = upper (x)').indexOf('UPPER (x)') >= 0, f('  y = upper (x)'));
    // keywordCase 'lower' flows through to functions
    var lo = f("    IF UPPER(CLIP(x)) = 'ON'", { keywordCase: 'lower' });
    ok("lower mode: UPPER( -> upper(", lo.indexOf('upper(') >= 0 && lo.indexOf('clip(') >= 0, lo);
    // 'asis' leaves function names alone
    var asis = f('  y = upper(x)', { keywordCase: 'asis' });
    ok("asis mode leaves 'upper('", asis.indexOf('upper(') >= 0, asis);
})();

// ---- Single-line range format must look UP to the enclosing structure ----
// formatClarionRange(text, L, L) must produce, for line L, exactly what a full-buffer format produces
// for line L — so a lone OF aligns to its CASE, an END to its opener, a body to its depth.
console.log('\nSingle-line range format (full-buffer context):');
(function () {
    var o = { alignAssignments: false, keywordCase: 'asis', otherNameCase: 'asis' };
    var src = ['  CASE x', '  OF 1', '    DoA', '  OF 2', '    DoB', '  END'].join('\n');
    var full = F.formatClarion(src, o).text.split('\n');
    [2, 3, 4, 6].forEach(function (L) {
        var got = F.formatClarionRange(src, L, L, o).text.split('\n')[L - 1];
        ok('range(L' + L + ') == full(L' + L + ')', got === full[L - 1],
            'line ' + L + '\n      range: ' + JSON.stringify(got) + '\n      full:  ' + JSON.stringify(full[L - 1]));
    });
    // The reported bug: formatting just the OF line must NOT indent it deeper than its CASE.
    var ofLine = F.formatClarionRange(src, 2, 2, o).text.split('\n')[1];
    ok('lone OF not over-indented vs CASE', ofLine.search(/\S/) === full[0].search(/\S/),
        'OF col ' + ofLine.search(/\S/) + ' vs CASE col ' + full[0].search(/\S/));
})();

// ---- Golden fixtures (auto-discovered) ----
var fixDir = path.join(__dirname, 'fixtures');
console.log('\nGolden fixtures:');
if (!fs.existsSync(fixDir)) {
    console.log('  (none yet — add fixtures/<name>.before.txt + .after.txt from native Smart Formatter output)');
} else {
    var befores = fs.readdirSync(fixDir).filter(function (f) { return /\.before\.txt$/.test(f); });
    if (!befores.length) console.log('  (fixtures/ exists but is empty — drop in <name>.before.txt + .after.txt pairs)');
    befores.forEach(function (bf) {
        var name = bf.replace(/\.before\.txt$/, '');
        var afterPath = path.join(fixDir, name + '.after.txt');
        if (!fs.existsSync(afterPath)) { ok(name + ' (missing .after)', false); return; }
        var before = fs.readFileSync(path.join(fixDir, bf), 'utf8');
        var after = fs.readFileSync(afterPath, 'utf8');
        // Native-fidelity fixtures test INDENTATION only; alignment + keyword-casing are extras beyond native.
        var got = F.formatClarion(before, { alignAssignments: false, keywordCase: 'asis', otherNameCase: 'asis' }).text;
        // Code snippets carry an external base column (their real nesting inside a procedure that the
        // snippet omits), so compare base-normalized: strip the common leading indent from each side.
        // Trailing inline-comment column is a separate refinement, so neutralize the gap before '!'.
        var e = normalize(after), g = normalize(got);
        if (g === e) { ok(name, true); }
        else { ok(name, false, firstDiff(e, g)); }
    });
}

// Strip the minimum common leading whitespace (over non-blank lines) and collapse the run of spaces
// before a trailing (non-line-start) '!' comment to a single space.
function normalize(text) {
    var lines = text.replace(/\r\n/g, '\n').split('\n');
    var min = Infinity;
    lines.forEach(function (l) {
        if (l.trim() === '') return;
        var m = /^ */.exec(l)[0].length;
        if (m < min) min = m;
    });
    if (!isFinite(min)) min = 0;
    // Comments shift rigidly with their code line (gap preserved), so after base-normalization the
    // comment column matches natively too — compare the FULL line, comment included.
    return lines.map(function (l) {
        if (l.trim() === '') return '';
        return l.slice(min);
    }).join('\n').replace(/\n+$/, '');
}

// Whole-file idempotency: formatting already-native-formatted code must return it unchanged.
// Compare line-by-line with trailing whitespace ignored (native leaves some trailing spaces).
console.log('\nWhole-file idempotency (format(native) == native):');
(function () {
    var nativePath = path.join(fixDir, 'clbrws002.native.clw');
    if (!fs.existsSync(nativePath)) { console.log('  (clbrws002.native.clw not present)'); return; }
    var nat = fs.readFileSync(nativePath, 'utf8');
    var got = F.formatClarion(nat, { alignAssignments: false, keywordCase: 'asis', otherNameCase: 'asis' }).text;   // indentation idempotency (alignment + casing are extras)
    var e = nat.replace(/\r\n/g, '\n').split('\n').map(rtrimLine);
    var g = got.replace(/\r\n/g, '\n').split('\n').map(rtrimLine);
    while (e.length && e[e.length - 1] === '') e.pop();
    while (g.length && g[g.length - 1] === '') g.pop();
    var diffs = [];
    var n = Math.max(e.length, g.length);
    for (var i = 0; i < n; i++) if (e[i] !== g[i]) diffs.push(i + 1);
    if (!diffs.length) ok('clbrws002 idempotent', true);
    else ok('clbrws002 idempotent', false, diffs.length + ' line(s) differ; first ' + diffs.slice(0, 8).join(',') +
        '\n      L' + diffs[0] + ' expected: ' + JSON.stringify(e[diffs[0] - 1]) +
        '\n      L' + diffs[0] + ' got:      ' + JSON.stringify(g[diffs[0] - 1]));
})();
function rtrimLine(s) { return s.replace(/\s+$/, ''); }

function firstDiff(expected, got) {
    var e = expected.split(/\r\n|\n/), g = got.split(/\r\n|\n/);
    var n = Math.max(e.length, g.length);
    for (var i = 0; i < n; i++) {
        if (e[i] !== g[i]) {
            return 'first diff @ line ' + (i + 1) +
                '\n      expected: ' + JSON.stringify(e[i]) +
                '\n      got:      ' + JSON.stringify(g[i]);
        }
    }
    return 'differs (length ' + e.length + ' vs ' + g.length + ')';
}

// ---- New configurable options (ticket deac3d16) ----
console.log('\npreferredKeywordIndent (PROGRAM/MEMBER/MAP/PRAGMA placement):');
(function () {
    function lead(src, opt) {
        var t = F.formatClarion(src, opt).text.replace(/\r\n/g, '\n').split('\n')[0];
        return /^ */.exec(t)[0].length;
    }
    var src = "  MAP\n  END\n";
    var o = { alignAssignments: false, keywordCase: 'asis', otherNameCase: 'asis' };
    // default (false): MAP sits at the preferred column (col 31 -> 0-based 32, tab-snapped).
    var dflt = lead(src, Object.assign({}, o));
    ok('default -> preferred column (>1 tab)', dflt > 4, 'MAP leading spaces = ' + dflt);
    // true: MAP sits at a single code indent (one tab = 4 with default tabSize).
    var indented = lead(src, Object.assign({ preferredKeywordIndent: true }, o));
    ok('preferredKeywordIndent -> one tab', indented === 4, 'MAP leading spaces = ' + indented);
})();

console.log('\nalignScope (selection vs global assignment alignment):');
(function () {
    var src = ['  AAAAAA = 1', '  B = 2', '  CC = 3'].join('\n');
    function eqCol(text, lineNo) { return text.replace(/\r\n/g, '\n').split('\n')[lineNo - 1].indexOf('='); }
    var base = { alignAssignments: true, keywordCase: 'asis', otherNameCase: 'asis' };
    // Format only the window L2..L3.
    var sel = F.formatClarionRange(src, 2, 3, Object.assign({ alignScope: 'selection' }, base)).text;
    var glob = F.formatClarionRange(src, 2, 3, Object.assign({ alignScope: 'global' }, base)).text;
    // selection: L2 & L3 align to each other (longest = 'CC'), not to the out-of-window 'AAAAAA'.
    ok('selection: window lines align to each other', eqCol(sel, 2) === eqCol(sel, 3), 'L2=' + eqCol(sel, 2) + ' L3=' + eqCol(sel, 3));
    ok('selection narrower than global', eqCol(sel, 2) < eqCol(glob, 2), 'sel=' + eqCol(sel, 2) + ' global=' + eqCol(glob, 2));
    // global: window lines align out to the longest operand in the full buffer ('AAAAAA').
    ok('global: window aligns to full-buffer longest', eqCol(glob, 2) === eqCol(glob, 3) && eqCol(glob, 2) > eqCol(sel, 2));
    // L1 is outside the window in both cases -> never modified.
    ok('out-of-window line untouched', sel.replace(/\r\n/g, '\n').split('\n')[0] === '  AAAAAA = 1');
})();

// ---- Code Statements: indent-from-opener toggles (ticket 24aa9557) ----
// Both deviate from native default (OFF). indentFromCode: CASE/IF mid-keywords (OF/OROF/ELSE/ELSIF)
// +1 tab, statement bodies +2 tabs, END stays at opener. indentCaseSubKeywords: same body indent
// extended to the non-mid structures (LOOP/EXECUTE/ACCEPT/BEGIN).
function leadOf(text, i) { var l = text.replace(/\r\n/g, '\n').split('\n')[i]; return /^ */.exec(l)[0].length; }
console.log('\nindentFromCode (Indent OF/ELSE from CASE/IF):');
(function () {
    var o = { alignAssignments: false, keywordCase: 'asis', otherNameCase: 'asis' };
    var src = ['  CASE x', '  OF 1', '  DoA', '  OF 2', '  DoB', '  END'].join('\n');
    var off = F.formatClarion(src, o).text;
    ok('default: OF aligns to CASE', leadOf(off, 1) === leadOf(off, 0), 'CASE=' + leadOf(off, 0) + ' OF=' + leadOf(off, 1));
    ok('default: body +1 tab', leadOf(off, 2) === leadOf(off, 0) + 4, 'body=' + leadOf(off, 2));
    ok('default: END at CASE', leadOf(off, 5) === leadOf(off, 0));
    var on = F.formatClarion(src, Object.assign({ indentFromCode: true }, o)).text;
    ok('ON: OF +1 tab from CASE', leadOf(on, 1) === leadOf(on, 0) + 4, 'CASE=' + leadOf(on, 0) + ' OF=' + leadOf(on, 1));
    ok('ON: body +2 tabs from CASE', leadOf(on, 2) === leadOf(on, 0) + 8, 'body=' + leadOf(on, 2));
    ok('ON: END stays at CASE', leadOf(on, 5) === leadOf(on, 0), 'CASE=' + leadOf(on, 0) + ' END=' + leadOf(on, 5));
    // IF/ELSE family
    var ifsrc = ['  IF x', '  DoA', '  ELSE', '  DoB', '  END'].join('\n');
    var ion = F.formatClarion(ifsrc, Object.assign({ indentFromCode: true }, o)).text;
    ok('ON: ELSE +1 tab from IF', leadOf(ion, 2) === leadOf(ion, 0) + 4, 'IF=' + leadOf(ion, 0) + ' ELSE=' + leadOf(ion, 2));
    ok('ON: IF body +2 tabs', leadOf(ion, 1) === leadOf(ion, 0) + 8, 'body=' + leadOf(ion, 1));
    ok('ON: IF END at IF', leadOf(ion, 4) === leadOf(ion, 0));
})();

console.log('\nindentCaseSubKeywords (extend indent to LOOP/EXECUTE):');
(function () {
    var o = { alignAssignments: false, keywordCase: 'asis', otherNameCase: 'asis' };
    var src = ['  LOOP 5 TIMES', '  DoA', '  END'].join('\n');
    // indentFromCode alone must NOT touch LOOP (it only governs CASE/IF).
    var fc = F.formatClarion(src, Object.assign({ indentFromCode: true }, o)).text;
    ok('indentFromCode leaves LOOP body at +1 tab', leadOf(fc, 1) === leadOf(fc, 0) + 4, 'body=' + leadOf(fc, 1));
    // indentCaseSubKeywords pushes LOOP body to +2 tabs, END stays at opener.
    var sk = F.formatClarion(src, Object.assign({ indentCaseSubKeywords: true }, o)).text;
    ok('indentCaseSubKeywords: LOOP body +2 tabs', leadOf(sk, 1) === leadOf(sk, 0) + 8, 'body=' + leadOf(sk, 1));
    ok('indentCaseSubKeywords: LOOP END at opener', leadOf(sk, 2) === leadOf(sk, 0));
    // EXECUTE too.
    var esrc = ['  EXECUTE n', '  DoA', '  END'].join('\n');
    var esk = F.formatClarion(esrc, Object.assign({ indentCaseSubKeywords: true }, o)).text;
    ok('indentCaseSubKeywords: EXECUTE body +2 tabs', leadOf(esk, 1) === leadOf(esk, 0) + 8, 'body=' + leadOf(esk, 1));
})();

console.log('\ncolonAsLabel (colon-terminated word = CODE-section label):');
(function () {
    var o = { alignAssignments: false, keywordCase: 'asis', otherNameCase: 'asis' };
    var src = ['  CODE', '  IF x', '  Retry:', '  DoA', '  END'].join('\n');
    var off = F.formatClarion(src, o).text;                                  // default OFF
    var on = F.formatClarion(src, Object.assign({ colonAsLabel: true }, o)).text;
    ok('OFF: colon-label indented as a statement', leadOf(off, 2) > 0, 'lead=' + leadOf(off, 2));
    ok('ON: colon-label pinned to column 1', leadOf(on, 2) === 0, 'lead=' + leadOf(on, 2));
    ok('ON: surrounding body still indented', leadOf(on, 3) > 0, 'DoA lead=' + leadOf(on, 3));
    // A field equate (colon mid-token, line does NOT end with ':') must never be treated as a label.
    var src2 = ['  CODE', '  x = ITC:in_stock'].join('\n');
    var on2 = F.formatClarion(src2, Object.assign({ colonAsLabel: true }, o)).text;
    ok('ON: field-equate line not pinned', leadOf(on2, 1) > 0, 'lead=' + leadOf(on2, 1));
})();

// ---- C# <-> JS formatter-key drift guard (ticket deac3d16) ----
// The host (ModernEmbeditorSettings.FormatterKeys) must round-trip EXACTLY the keys the gear panel
// persists/broadcasts: the HTML's FORMATTER_SETTING_KEYS plus formatLineOnEnter (an editor-side aid the
// C# list also carries). A silent drift = a new formatter option that never persists or broadcasts. The
// two lists live in different languages bound only by a comment, so assert their parity mechanically.
console.log('\nC#/JS formatter-key list parity:');
(function () {
    function keysFromBlock(text, startRe) {
        var m = startRe.exec(text);
        if (!m) return null;
        var tail = text.slice(m.index + m[0].length);   // text just after the opening [ or {
        var end = tail.search(/[\]}]/);                  // keys are quoted words — first ]/} closes the literal
        var body = end >= 0 ? tail.slice(0, end) : tail;
        return (body.match(/['"]([A-Za-z0-9_]+)['"]/g) || []).map(function (t) { return t.replace(/['"]/g, ''); });
    }
    var csPath = path.join(__dirname, '..', '..', 'Services', 'ModernEmbeditorSettings.cs');
    var htmlPath = path.join(__dirname, '..', 'monaco-embeditor.html');
    if (!fs.existsSync(csPath) || !fs.existsSync(htmlPath)) {
        ok('formatter-key source files present', false, 'missing ' + (!fs.existsSync(csPath) ? csPath : htmlPath));
    } else {
        var cs = keysFromBlock(fs.readFileSync(csPath, 'utf8'), /string\[\]\s+FormatterKeys\s*=\s*\{/);
        var js = keysFromBlock(fs.readFileSync(htmlPath, 'utf8'), /FORMATTER_SETTING_KEYS\s*=\s*\[/);
        ok('C# FormatterKeys parsed', cs && cs.length > 0, JSON.stringify(cs));
        ok('JS FORMATTER_SETTING_KEYS parsed', js && js.length > 0, JSON.stringify(js));
        if (cs && js) {
            var expected = js.concat(['formatLineOnEnter']).sort();
            var actual = cs.slice().sort();
            var same = expected.length === actual.length && expected.every(function (k, i) { return k === actual[i]; });
            ok('C# FormatterKeys == JS FORMATTER_SETTING_KEYS + formatLineOnEnter', same,
                'C#:  ' + JSON.stringify(actual) + '\n      JS+: ' + JSON.stringify(expected));
        }
    }
})();

// ---- MAP scope: procedure prototypes pin their label to column 1 (task 8933fa05) ----
console.log('\nMAP prototype indentation:');
(function () {
    function f(src) { return F.formatClarion(src, { alignAssignments: false, insertSpaces: true, tabSize: 2, preferredColumn: 3 }).text.replace(/\r\n/g, '\n'); }
    function lines(src) { return f(src).split('\n'); }

    // A col-1 prototype keeps its label in column 1; PROCEDURE moves to the data column.
    var a = lines('  MEMBER()\n\n  MAP\nMyProc PROCEDURE(LONG)\n  END\n');
    ok('MAP: col-1 prototype label stays in col 1', a.indexOf('MyProc  PROCEDURE(LONG)') !== -1, JSON.stringify(a));

    // An INDENTED prototype snaps its label back to column 1.
    var b = lines('  MEMBER()\n\n  MAP\n      MyProc PROCEDURE(LONG)\n        Another PROCEDURE\n  END\n');
    ok('MAP: indented prototype label snaps to col 1', b.indexOf('MyProc  PROCEDURE(LONG)') !== -1 && b.indexOf('Another PROCEDURE') !== -1, JSON.stringify(b));

    // The MAP's END aligns to MAP's own column (preferred col), not the members' data column.
    ok('MAP: END aligns to MAP column', b.indexOf('  END') !== -1 && b.indexOf('        END') === -1, JSON.stringify(b));

    // A directive inside the MAP (INCLUDE) is NOT treated as a prototype (label not pinned to col 1).
    var c = lines('  MAP\n    INCLUDE("x.inc"),ONCE\n   BoxProc PROCEDURE(STRING pName),STRING\n  END\n');
    ok('MAP: INCLUDE directive not pinned to col 1', c.some(function (l) { return /^\s+INCLUDE\(/.test(l); }), JSON.stringify(c));
    ok('MAP: prototype after directive pins to col 1', c.indexOf('BoxProc PROCEDURE(STRING pName),STRING') !== -1, JSON.stringify(c));

    // mapDepth resets after the MAP closes: a real procedure implementation below formats normally.
    var d = lines('  MEMBER()\n  MAP\nFoo PROCEDURE\n  END\n\nFoo PROCEDURE\nLoc  LONG\n  CODE\n  Loc = 1\n');
    ok('MAP: real proc header after MAP stays col 1', d.indexOf('Foo PROCEDURE') !== -1, JSON.stringify(d));
    ok('MAP: proc-local data still indents after MAP block', d.some(function (l) { return /^Loc\s+LONG/.test(l); }), JSON.stringify(d));
})();

console.log('\n' + pass + ' passed, ' + fail + ' failed.');
process.exit(fail ? 1 : 0);
