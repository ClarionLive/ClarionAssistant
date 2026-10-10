// clarion-omit-dimming.test.js — zero-dependency Node harness for OMIT/COMPILE dimming (GH #264).
//
// Run:  node Terminal/test/clarion-omit-dimming.test.js
//
// The native Clarion editor greys out code that OMIT/COMPILE excludes from the build; the CA Editor
// and CA Embeditor (both monaco-embeditor.html) coloured it like live code. clarionOmittedRegions()
// in clarion-language.js decides which lines are excluded (kind 'omitted', dimmed) and which depend on
// a condition the editor can't evaluate (kind 'conditional', a margin bar), and
// installClarionOmitDimming() keeps the decorations current as the buffer changes. This guards both,
// plus the page wiring that uses them.

var fs = require('fs');
var path = require('path');
var L = require('../clarion-language.js');

var pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  ✓ ' + name); }
    else { fail++; console.log('  ✗ ' + name + (detail ? '\n      ' + detail : '')); }
}

function stubModel(lines) {
    return { getLineCount: function () { return lines.length; },
             getLineContent: function (i) { return lines[i - 1]; } };
}
function blocks(lines, kind) {
    return (L.clarionOmittedRegions ? L.clarionOmittedRegions(stubModel(lines)) : [])
        .filter(function (r) { return r.kind === kind; })
        .map(function (r) { return r.start + '-' + r.end; });
}
function same(got, want) { return JSON.stringify(got) === JSON.stringify(want); }
function regionsAre(name, lines, expected) {
    var got = blocks(lines, 'omitted');
    ok(name, same(got, expected), 'expected [' + expected + ']  got [' + got + ']');
}
function conditionalsAre(name, lines, expected) {
    var got = blocks(lines, 'conditional');
    ok(name, same(got, expected), 'expected [' + expected + ']  got [' + got + ']');
}

console.log('Unconditional directives:');
regionsAre('OMIT dims the directive line through the terminator line', [
    'x = 1',
    "  OMIT('***')",
    '  y = 2',
    '  z = 3',
    '!***',
    'w = 4',
], ['2-5']);
regionsAre('COMPILE with no condition is always compiled — nothing dimmed', [
    "  COMPILE('***')",
    '  y = 2',
    '!***',
], []);
regionsAre('equate-style terminator found anywhere on a later line', [
    "OMIT('_EndOfInclude_')",
    'MyClass  CLASS',
    '         END',
    '  _EndOfInclude_',
    'after = 1',
], ['1-4']);
regionsAre('terminator is case-sensitive, as the Clarion 12 compiler reads it', [
    "OMIT('ENDX')",
    '! endx does not end it',
    '  D = POWER(2,3)',
    '! ENDX',
], ['1-4']);
regionsAre("the directive's own terminator literal does not close it on its own line", [
    "OMIT('**')   ! ** ends below",
    'a = 1',
    '**',
], ['1-3']);
regionsAre('two separate blocks', [
    "OMIT('**')", 'a', '**', 'live', "OMIT('##')", 'b', '##',
], ['1-3', '5-7']);

console.log('\nUnterminated and false directives:');
regionsAre('unterminated OMIT dims nothing (compile error; native shades nothing) and later blocks still count', [
    "OMIT('@@')",
    'a = 1',
    "OMIT('**')",
    'b = 2',
    '**',
], ['3-5']);
regionsAre('OMIT inside a string literal is not a directive', [
    "MESSAGE('OMIT(''**'')')",
    'a = 1',
    '**',
], []);
regionsAre('OMIT inside a comment is not a directive', [
    "! OMIT('**')",
    'a = 1',
    '**',
], []);

console.log('\nConditional directives:');
(function () {
    var omitDebug = ["OMIT('**',_DEBUG_)", 'a', '**'];
    regionsAre('OMIT with an unknown condition is not dimmed', omitDebug, []);
    conditionalsAre('...it gets the conditional margin, directive through terminator', omitDebug, ['1-3']);
    var compileDll = ["COMPILE('**',_ABCDllMode_)", 'a', '**'];
    regionsAre('COMPILE with an unknown condition is not dimmed', compileDll, []);
    conditionalsAre('...it gets the conditional margin too', compileDll, ['1-3']);
    conditionalsAre('an unterminated unknown block gets no margin', ["OMIT('**',_DEBUG_)", 'a'], []);
    conditionalsAre('known blocks get no margin', ["OMIT('**',1)", 'a', '**', "COMPILE('##')", 'b', '##'], []);
})();
regionsAre('OMIT(..., 1) dims; OMIT(..., 0) does not', [
    "OMIT('**',1)", 'a', '**', "OMIT('##',0)", 'b', '##',
], ['1-3']);
regionsAre('COMPILE(..., 0) dims; COMPILE(..., 1) does not', [
    "COMPILE('**',0)", 'a', '**', "COMPILE('##',1)", 'b', '##',
], ['1-3']);
regionsAre('TRUE / FALSE resolve', [
    "OMIT('**',TRUE)", 'a', '**', "COMPILE('##',FALSE)", 'b', '##',
], ['1-3', '4-6']);
regionsAre('EQUATEs declared earlier in the buffer resolve, including comparisons', [
    '_MyFlag_   EQUATE(1)',
    '_Other_    EQUATE(_MyFlag_)',
    "COMPILE('**',_MyFlag_=0)",
    'a',
    '**',
    "COMPILE('##',_MyFlag_=1)",
    'b',
    '##',
    "OMIT('@@',(_Other_ <> 0))",
    'c',
    '@@',
], ['3-5', '9-11']);
regionsAre('an EQUATE declared inside an omitted block does not count', [
    "OMIT('**')",
    '_X_   EQUATE(1)',
    '**',
    "OMIT('##',_X_)",
    'b',
    '##',
], ['1-3']);
(function () {
    // The debug-switch idiom: exactly one TraceOn exists, and the editor can't tell which.
    var idiom = [
        "COMPILE('***',_DEBUG_)",
        'TraceOn   EQUATE(1)',
        '***',
        "OMIT('***',_DEBUG_)",
        'TraceOn   EQUATE(0)',
        '***',
        "COMPILE('!x!',TraceOn)",
        '  Trace(here)',
        '!x!',
    ];
    regionsAre('an EQUATE declared inside an UNKNOWN block is unknown (debug-switch idiom stays live)', idiom, []);
    conditionalsAre('...and the block that tests it is marked conditional', idiom, ['1-3', '4-6', '7-9']);
})();
(function () {
    var redefined = [
        '_F_   EQUATE(0)',
        "_F_   EQUATE('text')",
        "COMPILE('**',_F_)",
        'a',
        '**',
    ];
    regionsAre('redefining an EQUATE to an unreadable value forgets the old value', redefined, []);
    conditionalsAre('...so a test on it is conditional', redefined, ['3-5']);
})();

console.log('\nNesting:');
regionsAre('OMIT inside a COMPILED block still dims (nest by different terminators)', [
    "COMPILE('**')",
    'live = 1',
    "  OMIT('##')",
    '  dead = 2',
    '  ##',
    'live = 3',
    '**',
], ['3-5']);
regionsAre('directives inside an OMITTED block are skipped wholesale', [
    "OMIT('**')",
    "  OMIT('##')",
    '  dead = 2',
    '  ##',
    'dead = 3',
    '**',
    'live = 4',
], ['1-6']);

console.log('\nTerminators and names that collide with Object.prototype:');
['constructor', '__proto__', 'toString', 'hasOwnProperty', 'valueOf'].forEach(function (t) {
    var lines = ["OMIT('" + t + "')", '  dead = 1', '! ' + t];
    regionsAre("terminator '" + t + "' is indexed like any other", lines, ['1-3']);
});
regionsAre("an EQUATE named __proto__ resolves like any other", [
    '__proto__   EQUATE(1)',
    "OMIT('**',__proto__)",
    'a',
    '**',
], ['2-4']);
conditionalsAre("an undeclared name 'constructor' is unknown, not Object's constructor", [
    "OMIT('**',constructor)", 'a', '**',
], ['1-3']);

console.log('\nTerminator index (Aho–Corasick) agrees with a plain substring test:');
(function () {
    if (!L.buildTerminatorIndex) { ok('buildTerminatorIndex is exported', false); return; }
    // Seeded, so a failure reproduces. Small alphabet = many overlaps, shared prefixes and suffixes.
    var seed = 264;
    function rnd(k) { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed % k; }
    function word(min, max) { var s = '', n = min + rnd(max - min + 1); while (n--) s += '*a!b'.charAt(rnd(4)); return s; }
    [['transition-table path', undefined], ['Map path (table forced off)', 0]].forEach(function (mode) {
        var bad = 0, firstBad = '';
        seed = 264;
        for (var round = 0; round < 200 && !bad; round++) {
            var terms = [], seenT = new Set();
            for (var k = 0, nt = 1 + rnd(12); k < nt; k++) { var w = word(1, 5); if (!seenT.has(w)) { seenT.add(w); terms.push(w); } }
            var lines = [undefined];
            for (var j = 0; j < 30; j++) lines.push(word(0, 25));
            var occ = L.buildTerminatorIndex(lines, terms, mode[1]);
            terms.forEach(function (t) {
                var want = [];
                for (var i = 1; i < lines.length; i++) if (lines[i].indexOf(t) >= 0) want.push(i);
                if (!bad && !same(occ.get(t), want)) { bad++; firstBad = JSON.stringify({ t: t, got: occ.get(t), want: want }); }
            });
        }
        ok(mode[0] + ': 200 random rounds, every terminator on exactly the lines indexOf finds it', !bad, firstBad);
    });
})();

console.log('\nCondition evaluator:');
(function () {
    var ev = L.evalOmitCondition || function () { return 'missing'; };
    ok('integer > 0 is true', ev('2', new Map()) === true);
    ok('0 is false', ev('0', new Map()) === false);
    ok('negative is false (native rule: > 0)', ev('-1', new Map()) === false);
    ok('unknown name is undefined', ev('_DEBUG_', new Map()) === undefined);
    ok('<> compares', ev('_A_ <> 1', new Map([['_A_', 2]])) === true);
    ok('comparison with an unknown side is undefined', ev('_A_ = 1', new Map()) === undefined);
    ok('(A) = (B): only parentheses wrapping the WHOLE expression are stripped', ev('(A) = (B)', new Map([['A', 1], ['B', 1]])) === true);
    ok('((A = 1)) still strips both wrappers', ev('((A = 1))', new Map([['A', 1]])) === true);
})();

console.log('\nShared directive parser:');
(function () {
    var p = L.parseOmitDirective || function () { return null; };
    var a = p("OMIT('it''s',_X_ = 1)") || {};
    ok("'' in the terminator unescapes to one quote", a.term === "it's", JSON.stringify(a));
    ok('terminator keeps its case', (p("omit('EndX')") || {}).term === 'EndX');
    ok('condition captured to the last )', a.cond === '_X_ = 1', JSON.stringify(a));
    var open = p("OMIT('**'") || {};
    ok('a directive still being typed parses, flagged not closed', open.term === '**' && open.closed === false, JSON.stringify(open));
})();

console.log('\nLive update (installClarionOmitDimming):');
(function (done) {
    if (!L.installClarionOmitDimming) { ok('installClarionOmitDimming is exported', false); return done(); }
    global.monaco = {
        Range: function (sl, sc, el, ec) { this.startLineNumber = sl; this.startColumn = sc; this.endLineNumber = el; this.endColumn = ec; },
        editor: { TrackedRangeStickiness: { NeverGrowsWhenTypingAtEdges: 1 } }
    };
    var lines = ['a = 1', 'b = 2', 'c = 3'];
    var lang = 'clarion', applied = [], live = {}, nextId = 1, calls = 0;
    var model = {
        getLineCount: function () { return lines.length; },
        getLineContent: function (i) { return lines[i - 1]; },
        getLineMaxColumn: function (i) { return lines[i - 1].length + 1; },
        getLanguageId: function () { return lang; },
        isDisposed: function () { return false; },
        getDecorationRange: function (id) { return live[id] || null; },
        deltaDecorations: function (old, decos) {
            calls++;
            old.forEach(function (id) { delete live[id]; });
            applied = decos.slice();
            return decos.map(function (d) { var id = 'd' + (nextId++); live[id] = d.range; return id; });
        }
    };
    var handlers = {};
    var editor = {
        getModel: function () { return model; },
        onDidChangeModelContent: function (f) { handlers.content = f; },
        onDidChangeModelLanguage: function (f) { handlers.lang = f; },
        onDidChangeModel: function (f) { handlers.model = f; }
    };
    L.installClarionOmitDimming(editor);
    setTimeout(function () {
        ok('nothing dimmed before an OMIT exists', applied.length === 0, JSON.stringify(applied));
        lines = ["OMIT('**')", 'b = 2', '**', "COMPILE('##',_DEBUG_)", 'c = 3', '##'];   // the user types blocks in
        handlers.content();
        handlers.content();                      // a burst of keystrokes collapses into one pass
        setTimeout(function () {
            var d = applied[0], c = applied[1];
            ok('typing an OMIT block dims it after the debounce', applied.length === 2 &&
               d.range.startLineNumber === 1 && d.range.endLineNumber === 3 && d.range.endColumn === 3,
               JSON.stringify(applied));
            ok('omitted decoration carries the line wash and the text fade', d && d.options.isWholeLine &&
               d.options.className === 'ca-omitted-line' && d.options.inlineClassName === 'ca-omitted-text',
               JSON.stringify(d && d.options));
            ok('unknown-condition decoration carries the margin class and NOT the dim classes', c &&
               c.options.linesDecorationsClassName === 'ca-conditional-margin' &&
               !c.options.inlineClassName && !c.options.className &&
               c.range.startLineNumber === 4 && c.range.endLineNumber === 6,
               JSON.stringify(c));
            var before = calls;
            lines = lines.slice(); lines[1] = 'b = 22';   // an edit inside a line that changes no block
            handlers.content();
            setTimeout(function () {
                ok('an edit that changes no block leaves the decorations alone', calls === before,
                   (calls - before) + ' extra deltaDecorations call(s)');
                lang = 'plaintext';                  // file mode can open non-Clarion files
                handlers.lang();
                setTimeout(function () {
                    ok('non-Clarion language clears the dimming', applied.length === 0, JSON.stringify(applied));
                    done();
                }, 20);
            }, 400);
        }, 400);
    }, 20);
})(function () {

    console.log('\nPerformance (86k-line generated module):');
    function timed(label, big) {
        var t0 = Date.now();
        var r = L.clarionOmittedRegions(stubModel(big));
        var ms = Date.now() - t0;
        console.log('    (' + label + ': ' + ms + ' ms for ' + big.length + ' lines, ' + r.length + ' blocks)');
        return { r: r, ms: ms };
    }
    (function () {
        if (!L.clarionOmittedRegions) { ok('clarionOmittedRegions is exported', false); return; }
        var big = [];
        for (var i = 0; i < 86000; i++) {
            if (i % 2000 === 0) big.push("  OMIT('***',_DEBUG_)");
            else if (i % 2000 === 1000) big.push("  OMIT('***')");
            else if (i % 2000 === 1010) big.push('!***');
            else big.push('    SELF.AddItem(Toolbar)   ! generated line ' + i);
        }
        var t = timed('typical', big);
        ok('one pass finds every omitted block', t.r.filter(function (x) { return x.kind === 'omitted'; }).length === 43);
        ok('one pass stays well under a typing pause', t.ms < 500, t.ms + ' ms');

        // Stress: 2,000 UNTERMINATED OMITs. Scanning ahead per directive re-read the buffer to its end
        // for each one — quadratic, on the WebView's UI thread, after every typing pause.
        function stress(termFor) {
            var s = [];
            for (var j = 0; j < 86000; j++)
                s.push(j % 43 === 0 && j / 43 < 2000 ? "  OMIT('" + termFor(j / 43) + "')"
                    : '    SELF.AddItem(Toolbar)   ! generated line ' + j);
            return s;
        }
        // Sharing one terminator, each directive's own literal ends the one before it (the compiler's
        // raw IndexOf would find it too), so they pair up into 1,000 blocks and only the last is open.
        var same1 = timed('2,000 OMITs, one terminator', stress(function () { return '@@NEVER@@'; }));
        ok('2,000 OMITs sharing a terminator: linear, paired as the compiler pairs them',
           same1.ms < 500 && same1.r.length === 1000, same1.ms + ' ms, ' + same1.r.length + ' blocks');
        var distinct = timed('2,000 unterminated, distinct terminators', stress(function (k) { return '@@NEVER' + k + '@@'; }));
        ok('2,000 unterminated OMITs with distinct terminators: linear', distinct.ms < 500 && distinct.r.length === 0, distinct.ms + ' ms');

        // Banner stress: the matcher's cost must not grow with the number (or LENGTHS) of distinct
        // terminators. The previous substring-per-length matcher did per character x per distinct
        // length: 12.5 s at 17 '*' terminators on all-banner lines, about 9 minutes on the last shape.
        function banners(directives, bannerLen, bannerCount) {
            var s = directives.slice();
            var banner = '!' + new Array(bannerLen + 1).join('*');
            for (var j = 0; j < bannerCount; j++) s.push(banner);
            while (s.length < 86000) s.push('    SELF.AddItem(Toolbar)   ! generated line ' + s.length);
            return s;
        }
        function starTerms(count, mk) { var d = []; for (var k = 1; k <= count; k++) d.push("  OMIT('" + mk(k) + "')"); return d; }
        var stars = function (k) { return new Array(k + 1).join('*'); };
        [17, 60].forEach(function (cnt) {
            // '*'-prefixed, never present in a banner: every banner character keeps the automaton deep.
            var b = timed('every line a banner, ' + cnt + " '*'-prefixed terminators",
                banners(starTerms(cnt, function (k) { return stars(k) + '#' + k; }), 60, 86000));
            ok('every line a banner, ' + cnt + " distinct '*'-prefixed terminators: under 500 ms", b.ms < 500, b.ms + ' ms');
        });
        // 500 distinct LENGTHS, all starting '*', none present: pure matcher cost, no output.
        var lens = timed("500 terminator LENGTHS sharing '*'", banners(starTerms(500, function (k) { return stars(k) + '#'; }), 200, 40000));
        ok("500 distinct terminator lengths sharing one first character: under 500 ms", lens.ms < 500, lens.ms + ' ms');
        // The debugger's extreme shape: 1,500 '*' runs of distinct lengths, then 20,000 lines of '!' + 200 '*'.
        var extreme = timed("1,500 '*'-run terminators + 20,000 x 200-'*' banners", banners(starTerms(1500, stars), 200, 20000));
        ok("1,500 '*'-run terminators against 20,000 banners: under 1,000 ms", extreme.ms < 1000, extreme.ms + ' ms');
    })();

    console.log('\nPage wiring (monaco-embeditor.html — the CA Editor AND the CA Embeditor):');
    (function () {
        var page = fs.readFileSync(path.join(__dirname, '..', 'monaco-embeditor.html'), 'utf8');
        ok('the page installs the dimming on its editor', /installClarionOmitDimming\(editor\)/.test(page));
        ok('light theme styles the omitted text', /\n\s*\.ca-omitted-text\s*\{[^}]*opacity/.test(page));
        ok('dark theme styles the omitted text', /body:not\(\.light\)\s*\.ca-omitted-text\s*\{[^}]*opacity/.test(page));
        ok('dark theme styles the omitted line', /body:not\(\.light\)\s*\.ca-omitted-line\s*\{/.test(page));
        ok('light theme styles the conditional margin', /\n\s*\.ca-conditional-margin\s*\{[^}]*border-left/.test(page));
        ok('dark theme styles the conditional margin', /body:not\(\.light\)\s*\.ca-conditional-margin\s*\{/.test(page));
        ok('high contrast styles the conditional margin', /hc-black \.ca-conditional-margin/.test(page));
    })();

    console.log('\n' + pass + ' passed, ' + fail + ' failed');
    process.exit(fail ? 1 : 0);
});
