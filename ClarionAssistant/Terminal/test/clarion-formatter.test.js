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

console.log('\n' + pass + ' passed, ' + fail + ' failed.');
process.exit(fail ? 1 : 0);
