// clarion-omit-dimming.test.js — zero-dependency Node harness for OMIT/COMPILE dimming (GH #264).
//
// Run:  node Terminal/test/clarion-omit-dimming.test.js
//
// The native Clarion editor greys out code that OMIT/COMPILE excludes from the build; the CA Editor
// and CA Embeditor (both monaco-embeditor.html) coloured it like live code. clarionOmittedRegions()
// in clarion-language.js decides which lines are excluded, and installClarionOmitDimming() keeps the
// decorations current as the buffer changes. This guards both, plus the page wiring that uses them.

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
function regions(lines) {
    return (L.clarionOmittedRegions ? L.clarionOmittedRegions(stubModel(lines)) : [])
        .map(function (r) { return r.start + '-' + r.end; });
}
function regionsAre(name, lines, expected) {
    var got = regions(lines);
    ok(name, JSON.stringify(got) === JSON.stringify(expected),
       'expected [' + expected + ']  got [' + got + ']');
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
regionsAre('terminator matched case-insensitively (same as the fold)', [
    "OMIT('_EndOfFile_')",
    'a = 1',
    '_ENDOFFILE_',
], ['1-3']);
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
regionsAre('OMIT with an unknown condition is left undimmed', [
    "OMIT('**',_DEBUG_)", 'a', '**',
], []);
regionsAre('COMPILE with an unknown condition is left undimmed', [
    "COMPILE('**',_ABCDllMode_)", 'a', '**',
], []);
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

console.log('\nCondition evaluator:');
(function () {
    var ev = L.evalOmitCondition || function () { return 'missing'; };
    ok('integer > 0 is true', ev('2', {}) === true);
    ok('0 is false', ev('0', {}) === false);
    ok('negative is false (native rule: > 0)', ev('-1', {}) === false);
    ok('unknown name is undefined', ev('_DEBUG_', {}) === undefined);
    ok('<> compares', ev('_A_ <> 1', { _A_: 2 }) === true);
    ok('comparison with an unknown side is undefined', ev('_A_ = 1', {}) === undefined);
})();

console.log('\nLive update (installClarionOmitDimming):');
(function (done) {
    if (!L.installClarionOmitDimming) { ok('installClarionOmitDimming is exported', false); return done(); }
    global.monaco = {
        Range: function (sl, sc, el, ec) { this.startLineNumber = sl; this.startColumn = sc; this.endLineNumber = el; this.endColumn = ec; },
        editor: { TrackedRangeStickiness: { NeverGrowsWhenTypingAtEdges: 1 } }
    };
    var lines = ['a = 1', 'b = 2', 'c = 3'];
    var lang = 'clarion', applied = [], nextId = 1;
    var model = {
        getLineCount: function () { return lines.length; },
        getLineContent: function (i) { return lines[i - 1]; },
        getLineMaxColumn: function (i) { return lines[i - 1].length + 1; },
        getLanguageId: function () { return lang; },
        isDisposed: function () { return false; },
        deltaDecorations: function (old, decos) {
            applied = decos.map(function (d) { return d; });
            return decos.map(function () { return 'd' + (nextId++); });
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
        lines = ["OMIT('**')", 'b = 2', '**'];   // the user types a block in
        handlers.content();
        handlers.content();                      // a burst of keystrokes collapses into one pass
        setTimeout(function () {
            var d = applied[0];
            ok('typing an OMIT block dims it after the debounce', applied.length === 1 &&
               d.range.startLineNumber === 1 && d.range.endLineNumber === 3 && d.range.endColumn === 3,
               JSON.stringify(applied));
            ok('decoration carries the line wash and the text fade', d && d.options.isWholeLine &&
               d.options.className === 'ca-omitted-line' && d.options.inlineClassName === 'ca-omitted-text',
               JSON.stringify(d && d.options));
            lang = 'plaintext';                  // file mode can open non-Clarion files
            handlers.lang();
            setTimeout(function () {
                ok('non-Clarion language clears the dimming', applied.length === 0, JSON.stringify(applied));
                done();
            }, 20);
        }, 400);
    }, 20);
})(function () {

    console.log('\nPerformance (86k-line generated module):');
    (function () {
        if (!L.clarionOmittedRegions) { ok('clarionOmittedRegions is exported', false); return; }
        var big = [];
        for (var i = 0; i < 86000; i++) {
            if (i % 2000 === 0) big.push("  OMIT('***',_DEBUG_)");
            else if (i % 2000 === 1000) big.push("  OMIT('***')");
            else if (i % 2000 === 1010) big.push('!***');
            else big.push('    SELF.AddItem(Toolbar)   ! generated line ' + i);
        }
        var t0 = Date.now();
        var r = L.clarionOmittedRegions(stubModel(big));
        var ms = Date.now() - t0;
        console.log('    (' + ms + ' ms for ' + big.length + ' lines, ' + r.length + ' regions)');
        ok('one pass finds every block', r.length === 43, 'got ' + r.length);
        ok('one pass stays well under a typing pause', ms < 1500, ms + ' ms');
    })();

    console.log('\nPage wiring (monaco-embeditor.html — the CA Editor AND the CA Embeditor):');
    (function () {
        var page = fs.readFileSync(path.join(__dirname, '..', 'monaco-embeditor.html'), 'utf8');
        ok('the page installs the dimming on its editor', /installClarionOmitDimming\(editor\)/.test(page));
        ok('light theme styles the omitted text', /\n\s*\.ca-omitted-text\s*\{[^}]*opacity/.test(page));
        ok('dark theme styles the omitted text', /body:not\(\.light\)\s*\.ca-omitted-text\s*\{[^}]*opacity/.test(page));
        ok('dark theme styles the omitted line', /body:not\(\.light\)\s*\.ca-omitted-line\s*\{/.test(page));
    })();

    console.log('\n' + pass + ' passed, ' + fail + ' failed');
    process.exit(fail ? 1 : 0);
});
