// clarion-folding.test.js — zero-dependency Node harness for the shared Clarion folding provider.
//
// Run:  node Terminal/test/clarion-folding.test.js
//
// Guards registerClarionFolding()'s range computation, which is shared by the CA Embeditor, the
// diff editor, and Monaco's sticky-scroll scope headers — so a regression here shows up in three
// surfaces at once.
//
// The headline cases are GH #158: a Clarion structure keyword sitting inside an ordinary string
// literal ('...Session Class instance', 'application/xml', 'View not found') must NOT be read as a
// structure opener. Before the fix the keyword scan ran on raw line text, so those pushed a phantom
// entry onto the fold stack — putting a fold triangle on a plain executable line and swallowing the
// real enclosing structure. A single ~9,000-line production .clw turned up 64 such literals.
//
// The OMIT/COMPILE cases are the matching regression guard (GH #133): the terminator has to be read
// from text whose string contents are still INTACT, so the fix for #158 mustn't blank it away.

var L = require('../clarion-language.js');

var pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  ✓ ' + name); }
    else { fail++; console.log('  ✗ ' + name + (detail ? '\n      ' + detail : '')); }
}

function folds(lines) {
    var model = { getLineCount: function () { return lines.length; },
                  getLineContent: function (i) { return lines[i - 1]; } };
    return L.clarionFoldingRanges(model).map(function (r) { return r.start + '-' + r.end; }).sort();
}
function foldsAre(name, lines, expected) {
    var got = folds(lines), want = expected.slice().sort();
    ok(name, JSON.stringify(got) === JSON.stringify(want),
       'expected [' + want + ']  got [' + got + ']');
}

// ---- splitClarionLine mechanics ----
console.log('String/comment splitting:');
(function () {
    var r = L.splitClarionLine("  MESSAGE('Session Class instance')  ! note");
    ok('comment removed from code', r.code.indexOf('! note') === -1, JSON.stringify(r.code));
    ok('string contents kept in code', r.code.indexOf('Session Class instance') >= 0, JSON.stringify(r.code));
    ok('string contents blanked in safe', r.safe.indexOf('Class') === -1, JSON.stringify(r.safe));
    var e = L.splitClarionLine("MESSAGE('it''s fine')");
    ok("'' escape consumed as one literal", e.safe.indexOf('fine') === -1, JSON.stringify(e.safe));
    var b = L.splitClarionLine("MESSAGE('Done! Really')");
    ok("'!' inside a literal does not truncate", b.code.indexOf('Really') >= 0, JSON.stringify(b.code));
    var u = L.splitClarionLine("MESSAGE('unterminated");
    ok('unterminated literal consumes to end of line', u.safe.indexOf('unterminated') === -1, JSON.stringify(u.safe));
})();

// ---- GH #158 ----
console.log('\nGH #158 — a keyword inside a string must not open a fold:');
foldsAre('CLASS inside a MESSAGE string', [
    'IF NOT Session.IsReady()',
    '   IF Session.Init(licenseKey) ~= OK',
    "      MESSAGE('Unable to initialize Session Class instance')",
    '   END',
    'END',
], ['1-5', '2-4']);

foldsAre('APPLICATION inside a MIME type', [
    'IF x = 1',
    "  HTTP.SetRequestHeader('Content-Type','application/xml')",
    'END',
], ['1-3']);

foldsAre('FILE and VIEW inside error text', [
    'IF ok',
    "  MESSAGE('Unable to open File for reading')",
    "  MESSAGE('View not found')",
    'END',
], ['1-4']);

// ---- GH #133 regression ----
console.log('\nGH #133 regression — OMIT/COMPILE terminators still resolve:');
foldsAre('OMIT folds to its terminator', [
    "OMIT('***')",
    '  GROUP',
    '  END',
    '!*** end',
    'MESSAGE(1)',
], ['1-4']);

foldsAre('COMPILE folds to its terminator', [
    "COMPILE('**X**')",
    '  CODE stuff',
    '!**X**',
], ['1-3']);

foldsAre('unterminated OMIT folds to end of buffer', [
    'MESSAGE(1)',
    "OMIT('***')",
    '  anything',
], ['2-3']);

// ---- one-liner IF (offsets must index the blanked line, not the raw one) ----
console.log('\nOne-line IF detection with strings present:');
foldsAre('one-line IF with a string is not a fold', [
    "IF x = 1 THEN MESSAGE('hi').",
    'MESSAGE(2)',
], []);

foldsAre('block IF with a string in the condition is a fold', [
    "IF Name = 'Class'",
    '  MESSAGE(1)',
    'END',
], ['1-3']);

// ---- real structures still work ----
console.log('\nReal structures still fold:');
foldsAre('GROUP closes on END', ['Rec GROUP', 'F1  LONG', 'END'], ['1-3']);
foldsAre('bare TOOLBAR opens, "Toolbar ToolbarClass" does not', [
    'Toolbar ToolbarClass',
    'WINDOW',
    '  TOOLBAR,AT(0,0)',
    '  END',
    'END',
], ['2-5', '3-4']);

// ---- !REGION / !ENDREGION ----
// User-defined folds requested 2026-08-06. Markers are Clarion COMMENTS, so the whole feature
// depends on being read from the raw line before the comment strip — if that ever regresses, every
// case in this section goes to zero folds at once.
console.log('\n!REGION / !ENDREGION:');
foldsAre('simple region folds', [
    '!REGION Setup',
    '  A = 1',
    '!ENDREGION',
], ['1-3']);

foldsAre('case-insensitive — lower', ['!region', '  A = 1', '!endregion'], ['1-3']);
foldsAre('case-insensitive — mixed', ['!Region', '  A = 1', '!EndRegion'], ['1-3']);
foldsAre('space after the bang is tolerated', ['! REGION', '  A = 1', '! ENDREGION'], ['1-3']);

foldsAre('regions nest', [
    '!REGION outer',
    '  !REGION inner',
    '    A = 1',
    '  !ENDREGION',
    '!ENDREGION',
], ['1-5', '2-4']);

foldsAre('two sibling regions', [
    '!REGION one',
    '  A = 1',
    '!ENDREGION',
    '!REGION two',
    '  B = 2',
    '!ENDREGION',
], ['1-3', '4-6']);

// The bug in the reference implementation (startsWith("!REGION")) — a perfectly ordinary comment
// would open a region that never closes and swallow the rest of the file.
foldsAre('"!Regional settings" is a comment, not a region', [
    '!Regional settings',
    'Rec GROUP',
    'F1  LONG',
    'END',
], ['2-4']);
foldsAre('"!ENDREGIONAL" is not a terminator', [
    '!REGION open',
    '  A = 1',
    '!ENDREGIONAL',
], []);

foldsAre('unterminated region does NOT fold to EOF', [
    '!REGION open',
    '  A = 1',
    '  B = 2',
], []);
foldsAre('stray !ENDREGION with nothing open is ignored', [
    '  A = 1',
    '!ENDREGION',
], []);

foldsAre('a marker trailing real code is just a comment', [
    'Rec GROUP  !REGION not-a-region',
    'F1  LONG',
    'END',
], ['1-3']);

// Regions are comment-based, so they must be independent of the structure/PROCEDURE machinery.
foldsAre('region spanning a PROCEDURE boundary does not corrupt it', [
    '!REGION both procs',
    'Foo PROCEDURE',
    '  A = 1',
    'Bar PROCEDURE',
    '  B = 2',
    '!ENDREGION',
], ['1-6', '2-3', '4-6']);

foldsAre('region wrapping a structure keeps both folds', [
    '!REGION data',
    'Rec GROUP',
    'F1  LONG',
    'END',
    '!ENDREGION',
], ['1-5', '2-4']);

foldsAre('a region marker inside an OMIT block stays omitted', [
    "  OMIT('***')",
    '!REGION swallowed',
    '  A = 1',
    '!ENDREGION',
    '  ***',
], ['1-5']);

// kind: Monaco marks user regions so Fold/Unfold All Regions can target them. Under Node `monaco`
// is undefined, so the provider must degrade to an undefined kind rather than throw.
(function () {
    var lines = ['!REGION r', '  A = 1', '!ENDREGION'];
    var model = { getLineCount: function () { return lines.length; },
                  getLineContent: function (i) { return lines[i - 1]; } };
    var rs = L.clarionFoldingRanges(model);
    ok('provider does not throw without a global monaco', rs.length === 1, JSON.stringify(rs));
    ok('region range carries a kind property', rs.length === 1 && 'kind' in rs[0], JSON.stringify(rs[0]));
})();

console.log('\n' + pass + ' passed, ' + fail + ' failed.');
process.exit(fail ? 1 : 0);
