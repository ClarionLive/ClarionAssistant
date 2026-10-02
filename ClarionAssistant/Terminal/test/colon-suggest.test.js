// colon-suggest.test.js — typing ':' after an identifier opens completion (ticket 38158e98).
//
// Run:  node Terminal/test/colon-suggest.test.js
//
// Zero-dependency (no jsdom). Like its neighbours this test EXTRACTS the page's code rather than copying
// it: isColonQualifierTyped / installColonSuggest and the string/comment helpers they use are sliced out
// of monaco-embeditor.html at run time and evaluated against a fake editor.
//
// The bug: ':' is a triggerCharacter on the completion provider, but the Clarion wordPattern keeps ':'
// inside a word, so with the list already open while "glo" was typed Monaco continued that session and
// re-filtered it to nothing instead of asking again - no request was sent at all. Ctrl+Space worked.
// The page now triggers suggest itself (editor.action.triggerSuggest, what Ctrl+Space runs) on a ':'
// typed straight after an identifier, outside strings and comments.

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

const colonSrc = slice(html, 'function isColonQualifierTyped(', '// Normalize a just-typed assignment', 'colon suggest');
const helpersSrc = slice(html, 'function isInClarionComment(', '// ----- Local-first completion', 'string/comment helpers');
const api = new Function(helpersSrc + '\n' + colonSrc +
    '\nreturn { isColonQualifierTyped: isColonQualifierTyped, installColonSuggest: installColonSuggest };')();

let pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? '  -> ' + detail : '')); }
}

// caret = the 0-based index just after the ':' that was typed (Monaco column - 1).
function typed(line) { return api.isColonQualifierTyped(line, line.length); }

console.log('isColonQualifierTyped');
ok('glo: -> trigger', typed('    glo:'));
ok('g: (one-letter prefix) -> trigger', typed('  IF g:'));
ok('PROP: inside a property expression -> trigger', typed('  ?Btn{PROP:'));
ok('EVENT: after OF -> trigger', typed('    OF EVENT:'));
ok('nested prefix TGLO:GLO: -> trigger', typed('  x = TGLO:GLO:'));
ok('lone ":" -> no trigger', !typed('  x = :'));
ok('":" after a space -> no trigger', !typed('  label :'));
ok('"::" -> no trigger', !typed('  x::'));
ok('a time literal 12: -> no trigger', !typed('  T = 12:'));
ok('inside a string -> no trigger', !typed("  S = 'glo:"));
ok('inside a comment -> no trigger', !typed('  ! see glo:'));
ok('after a closed string -> trigger', typed("  S = 'x' & glo:"));
ok('previous char not ":" -> no trigger', !api.isColonQualifierTyped('  glo:x', 7));

console.log('installColonSuggest');
function fakeEditor(line, column) {
    const ed = {
        handlers: [], triggered: [],
        onDidType: function (h) { ed.handlers.push(h); },
        getModel: function () { return { getLineContent: function () { return line; } }; },
        getPosition: function () { return { lineNumber: 1, column: column }; },
        trigger: function (src, id, args) { ed.triggered.push(id); },
        type: function (t) { ed.handlers.forEach(function (h) { h(t); }); }
    };
    api.installColonSuggest(ed);
    return ed;
}
function tick() { return new Promise(function (r) { setTimeout(r, 5); }); }

(async function () {
    const a = fakeEditor('    glo:', 9);
    a.type(':');
    ok('nothing is triggered synchronously (Monaco closes its stale list first)', a.triggered.length === 0);
    await tick();
    ok('":" after glo triggers editor.action.triggerSuggest on the next tick',
        a.triggered.length === 1 && a.triggered[0] === 'editor.action.triggerSuggest', JSON.stringify(a.triggered));

    const b = fakeEditor('    glo', 8);
    b.type('o');
    await tick();
    ok('other characters trigger nothing', b.triggered.length === 0);

    const c = fakeEditor("  S = 'glo:", 12);
    c.type(':');
    await tick();
    ok('":" inside a string triggers nothing', c.triggered.length === 0);

    const d = fakeEditor('    glo:', 9);
    d.type('::');   // a paste or multi-char edit is not a typed colon
    await tick();
    ok('multi-character input triggers nothing', d.triggered.length === 0);

    console.log('wiring');
    ok('the main editor installs it', html.indexOf('installColonSuggest(editor);') >= 0);
    ok('the split pane installs it', html.indexOf('installColonSuggest(editor2);') >= 0);
    ok("':' is still a provider triggerCharacter", /triggerCharacters:\s*\[[^\]]*':'/.test(html));
    ok('installColonSuggest tolerates a missing editor', (function () { try { api.installColonSuggest(null); return true; } catch (e) { return false; } })());

    console.log();
    console.log(pass + ' passed, ' + fail + ' failed');
    process.exit(fail === 0 ? 0 : 1);
})();
