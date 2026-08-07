// fold-state.test.js — guards the fold persistence fingerprint + line resolution.
//
// Run:  node Terminal/test/fold-state.test.js
//
// Zero-dependency: the logic under test is pure string/line matching, no DOM and no Monaco.
// Like its neighbours it EXTRACTS the page's real code by marker rather than copying it, so the tests
// cannot drift from monaco-embeditor.html.
//
// WHY THIS MATTERS MORE THAN IT LOOKS. Restoring folds by line number alone is unsafe in this editor:
// the embeditor's buffer is GENERATED source, so line numbers move when templates regenerate or an
// embed slot grows. A bookmark landing on the wrong line is cosmetic. A FOLD landing on the wrong line
// HIDES CODE THE DEVELOPER DID NOT ASK TO HIDE — they can scroll past a collapsed region without ever
// realising something is under it.
//
// So the contract these tests pin down is deliberately conservative:
//   * restore a fold only where the start line's fingerprint still matches
//   * if the buffer shifted, find it within a small window
//   * otherwise return 0 — restore NOTHING. Skipping is always better than folding the wrong region.

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

const src = slice(html,
    '// ===================== Fold state persistence (contract/expand survives reopen) =====================',
    '    // Monaco computes folding ranges ASYNCHRONOUSLY', 'fold persistence block');

if (!/function foldFingerprint/.test(src)) throw new Error('extracted block has no foldFingerprint');
if (!/function resolveFoldLine/.test(src)) throw new Error('extracted block has no resolveFoldLine');

// The block references postToHost/activeEd, which don't exist under Node. They're only referenced
// inside function bodies we never call here, so declaring the functions is safe.
const env = new Function(`
    ${src}
    return { foldFingerprint: foldFingerprint, resolveFoldLine: resolveFoldLine };
`)();

let pass = 0, fail = 0;
const failures = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  ✓ ' + name); }
    else { fail++; failures.push(name); console.log('  ✗ ' + name + (detail ? '\n      ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

function makeModel(lines) {
    return {
        getLineCount: function () { return lines.length; },
        getLineContent: function (i) { return lines[i - 1]; }
    };
}
function startsOf(/* ...lineNumbers */) {
    const m = {};
    for (const n of arguments) m[n] = true;
    return m;
}

// ---------- fingerprint ----------
section('Fingerprint normalization:');
{
    const m = makeModel(['  If  Foo = 1   ', 'x']);
    ok('trims, collapses inner whitespace, uppercases',
        env.foldFingerprint(m, 1) === 'IF FOO = 1', JSON.stringify(env.foldFingerprint(m, 1)));

    // The point of normalizing: live keyword casing (GH #138) and the Smart Formatter rewrite the line's
    // casing and spacing as you type. A raw-text fingerprint would go stale on its own.
    const a = makeModel(['if foo = 1']);
    const b = makeModel(['IF   FOO  =  1']);
    ok('case + spacing differences produce the SAME fingerprint',
        env.foldFingerprint(a, 1) === env.foldFingerprint(b, 1),
        JSON.stringify(env.foldFingerprint(a, 1)) + ' vs ' + JSON.stringify(env.foldFingerprint(b, 1)));

    ok('out-of-range line yields empty, not a throw', env.foldFingerprint(m, 99) === '');
    ok('line 0 yields empty', env.foldFingerprint(m, 0) === '');
    ok('null model yields empty', env.foldFingerprint(null, 1) === '');

    const long = makeModel(['A'.repeat(400)]);
    ok('fingerprint is length-capped at 120 (matches the host cap)',
        env.foldFingerprint(long, 1).length === 120, String(env.foldFingerprint(long, 1).length));
}

// ---------- resolution ----------
section('Resolving a saved fold to a line:');
{
    const model = makeModel(['Rec GROUP', '  F1 LONG', 'END', 'Foo PROCEDURE', '  CODE']);

    ok('exact line, matching fingerprint, is a fold start → restored',
        env.resolveFoldLine(model, startsOf(1), { line: 1, text: 'REC GROUP' }) === 1);

    ok('line is NOT a fold start → skipped even though the text matches',
        env.resolveFoldLine(model, startsOf(4), { line: 1, text: 'REC GROUP' }) === 0);

    ok('fingerprint mismatch at the saved line → skipped',
        env.resolveFoldLine(model, startsOf(1), { line: 1, text: 'SOMETHING ELSE' }) === 0);

    ok('casing/spacing drift still matches (fingerprint is normalized)',
        env.resolveFoldLine(model, startsOf(1), { line: 1, text: 'rec   group'.toUpperCase() }) === 1);
}

section('Buffer drift — the case this whole design exists for:');
{
    // Two lines were inserted above the GROUP since the fold was saved at line 1.
    const drifted = makeModel(['! new header', '! another', 'Rec GROUP', '  F1 LONG', 'END']);

    ok('finds the region at its new line via the search window',
        env.resolveFoldLine(drifted, startsOf(3), { line: 1, text: 'REC GROUP' }) === 3);

    ok('searches upward too (content moved earlier)',
        env.resolveFoldLine(makeModel(['Rec GROUP', 'END']), startsOf(1), { line: 2, text: 'REC GROUP' }) === 1);

    // Beyond the window we refuse rather than hunt the whole buffer — a match 200 lines away is far more
    // likely to be a DIFFERENT region that happens to look the same (Clarion generated code repeats).
    const far = [];
    for (let i = 0; i < 200; i++) far.push('  filler');
    far.push('Rec GROUP');
    ok('a match beyond the search window is refused, not guessed',
        env.resolveFoldLine(makeModel(far), startsOf(201), { line: 1, text: 'REC GROUP' }) === 0);

    ok('no match anywhere → 0 (restore nothing)',
        env.resolveFoldLine(drifted, startsOf(3), { line: 1, text: 'GONE ENTIRELY' }) === 0);
}

section('Ambiguity — the case that matters for STRUCTURE folds, not regions:');
{
    // "!REGION Setup cards" is effectively unique. "Rec GROUP" is not — generated Clarion repeats
    // structure headers constantly, so a drifted lookup could land on a different-but-identical region.
    // Two identical GROUP headers, and the saved line no longer starts a region:
    const dup = makeModel([
        '! header',        // 1  (saved fold was here)
        'Rec GROUP',       // 2
        '  F1 LONG',       // 3
        'END',             // 4
        'Rec GROUP',       // 5  <- identical header
        '  F2 LONG',       // 6
        'END',             // 7
    ]);
    ok('two identical candidates in the window → restore NOTHING',
        env.resolveFoldLine(dup, startsOf(2, 5), { line: 1, text: 'REC GROUP' }) === 0,
        'got ' + env.resolveFoldLine(dup, startsOf(2, 5), { line: 1, text: 'REC GROUP' }));

    // But an exact hit at the saved line is not a guess — duplicates elsewhere must not suppress it.
    ok('exact line match still wins despite duplicates elsewhere',
        env.resolveFoldLine(dup, startsOf(2, 5), { line: 2, text: 'REC GROUP' }) === 2);

    // And a single candidate still resolves — the guard must not make drift-matching useless.
    const single = makeModel(['! header', 'Rec GROUP', '  F1 LONG', 'END', 'Other GROUP', 'END']);
    ok('one candidate in the window still restores',
        env.resolveFoldLine(single, startsOf(2, 5), { line: 1, text: 'REC GROUP' }) === 2);

    // Nearest wins when only one matches.
    const near = makeModel(['Rec GROUP', 'END', '! filler', 'Other GROUP', 'END']);
    ok('the single match is returned regardless of direction',
        env.resolveFoldLine(near, startsOf(1, 4), { line: 3, text: 'REC GROUP' }) === 1);
}

section('Records written by an older build (no fingerprint):');
{
    const model = makeModel(['Rec GROUP', 'END']);
    ok('falls back to the plain line when it IS a fold start',
        env.resolveFoldLine(model, startsOf(1), { line: 1, text: '' }) === 1);
    ok('still skipped when the line starts no region',
        env.resolveFoldLine(model, startsOf(2), { line: 1, text: '' }) === 0);
    ok('missing text property behaves like an empty one',
        env.resolveFoldLine(model, startsOf(1), { line: 1 }) === 1);
}

console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) { console.log('\nFailures:'); failures.forEach(f => console.log('  - ' + f)); process.exit(1); }
