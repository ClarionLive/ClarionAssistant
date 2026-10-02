// keymap-profiles-ui.test.js — guards the gear panel's keymap profile picker (GH #206).
//
// Run:  npm install jsdom    (ONE TIME — dev-only, as for vscode-import-ui.test.js)
//       node Terminal/test/keymap-profiles-ui.test.js
//
// Sibling of keymap-profiles.test.js, which pins the chord logic without a DOM. This one needs jsdom because
// the code under test builds the Keyboard table and the picker with real DOM calls. Nothing is copied: the
// gearPaneEditor markup, the key command section and the Keyboard rebinding table section are sliced out of
// monaco-embeditor.html and evaluated. persistSettings / showToast are spies.
//
// What is pinned:
//   * the picker lists every profile and shows the active one
//   * the "Default" column shows the ACTIVE profile's chord (what a reset returns to), headed with its name
//   * the developer's own key still shows on top of the profile, marked custom
//   * choosing a profile persists once and toasts; a reset override is reported as a warning, not success

const fs = require('fs');
let JSDOM;
try { ({ JSDOM } = require('jsdom')); }
catch (e) {
    console.error('This test needs jsdom, which is a dev-only dependency and is not installed.\n' +
                  '  Install it with:  npm install jsdom\n' +
                  'Skipping is NOT the same as passing — exiting non-zero so a runner cannot read this as green.');
    process.exit(2);
}

const HTML_PATH = process.argv[2] || require('path').join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');

function slice(text, startMarker, endMarker, what, keepEnd) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, keepEnd ? b + endMarker.length : b);
}

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

const gearMarkup = slice(html, '<div class="gear-pane" id="gearPaneEditor">', '</div><!-- /gearPaneEditor -->', 'gear pane markup', true);
const keySrc = slice(html, '    var keyBindings = {};', '    // Map a keydown to a canonical chord', 'key command section');
const tableSrc = slice(html, '    // ----- Keyboard rebinding table (gear panel) -----', '    function startKeyCapture', 'Keyboard table section');
const escHtmlSrc = slice(html, '    function escHtml(s) {', '    }', 'escHtml', true);

function makeEnv() {
    const dom = new JSDOM('<!doctype html><html><body><div id="settingsPanel">' + gearMarkup + '</div></body></html>',
        { runScripts: 'outside-only' });
    const spy = { persisted: 0, toasts: [] };
    const runNames = new Set(['navEmbed', 'openSnippetPicker']);
    const re = /run:\s*([A-Za-z_$][\w$]*)/g;
    let m;
    while ((m = re.exec(keySrc))) if (m[1] !== 'function') runNames.add(m[1]);
    const names = Array.from(runNames);
    const scope = {
        document: dom.window.document,
        persistSettings: () => { spy.persisted++; },
        showToast: (msg, ok) => { spy.toasts.push({ msg, ok }); },
        startKeyCapture: () => { }, cancelKeyCapture: () => { }
    };
    const params = names.concat(Object.keys(scope));
    const args = names.map(() => function stub() { }).concat(Object.values(scope));
    const page = new Function(...params, escHtmlSrc + keySrc + tableSrc +
        '\nreturn { buildKeybindTable: buildKeybindTable, onKeyProfileChosen: onKeyProfileChosen,' +
        ' setKeyProfile: setKeyProfile, KEY_PROFILES: KEY_PROFILES,' +
        ' setBindings: function (b) { keyBindings = b; }, getBindings: function () { return keyBindings; } };')(...args);
    return { page, spy, doc: dom.window.document };
}

function row(doc, cmdId) {
    const input = doc.querySelector('#keybindRows .kb-input[data-cmd="' + cmdId + '"]');
    if (!input) return null;
    const tr = input.closest('tr');
    return { def: tr.querySelector('.kb-def').textContent, key: input.value, custom: input.getAttribute('data-custom') === '1' };
}

let env;
try { env = makeEnv(); } catch (e) { check('the gear panel and key sections load', false, e.message); }
if (env) {
    const { page, spy, doc } = env;
    page.buildKeybindTable();

    section('Picker');
    const sel = doc.getElementById('kbProfile');
    check('the gear panel has a keymap profile picker', !!sel);
    if (sel) {
        check('it lists every profile', sel.options.length === page.KEY_PROFILES.length
            && page.KEY_PROFILES.every((p, i) => sel.options[i].value === p.id && sel.options[i].textContent === p.label));
        check('it shows Clarion on a fresh page', sel.value === 'clarion', sel.value);
    }
    check('the column is headed "Default" on Clarion', doc.getElementById('kbDefHead').textContent === 'Default');
    check('Clarion: Remove Line shows its Clarion chord', row(doc, 'removeLine').def === 'Ctrl+Shift+Y', JSON.stringify(row(doc, 'removeLine')));

    section('Choosing Visual Studio');
    page.onKeyProfileChosen('vs');
    check('the picker now shows Visual Studio', sel && sel.value === 'vs');
    check('the column is headed with the profile name', doc.getElementById('kbDefHead').textContent === 'Visual Studio');
    check('Remove Line\'s default column shows the profile chord', row(doc, 'removeLine').def === 'Ctrl+Shift+L');
    check('Duplicate Line is on the profile chord', row(doc, 'duplicateLineAbove').key === 'Ctrl+D');
    check('Structure Designer moved to Ctrl+Shift+D', row(doc, 'structureDesigner').key === 'Ctrl+Shift+D');
    check('Invert Case keeps its Clarion chord', row(doc, 'invertCase').key === 'Ctrl+\\');
    check('the choice is persisted once', spy.persisted === 1, String(spy.persisted));
    check('a clean switch toasts success, naming the profile',
        spy.toasts.length === 1 && spy.toasts[0].ok === true && /Visual Studio/.test(spy.toasts[0].msg), JSON.stringify(spy.toasts));

    section('Your keys on top of a profile');
    page.setBindings({ removeLine: 'Ctrl+9' });
    page.buildKeybindTable();
    const r = row(doc, 'removeLine');
    check('your key shows in the Key column, marked custom', r.key === 'Ctrl+9' && r.custom, JSON.stringify(r));
    check('the Default column still shows the profile chord a reset returns to', r.def === 'Ctrl+Shift+L', r.def);

    section('A switch that resets one of your keys');
    page.setBindings({ upperCase: 'Ctrl+L' });   // free on VS; Notepad++ puts Remove Line there
    page.onKeyProfileChosen('npp');
    const last = spy.toasts[spy.toasts.length - 1];
    check('the clashing override is reset', page.getBindings().upperCase === undefined, JSON.stringify(page.getBindings()));
    check('the toast says so, as a warning', last && last.ok === false && /1 of your keys reset/.test(last.msg), JSON.stringify(last));
    check('Remove Line gets the Notepad++ chord', row(doc, 'removeLine').key === 'Ctrl+L');
    check('Uppercase falls back to its profile chord, not to nothing', row(doc, 'upperCase').key === 'Ctrl+Shift+U');
}

console.log('\n' + pass + ' passed, ' + fail + ' failed');
if (fail) { console.log('\nFailures:'); failures.forEach(f => console.log('  - ' + f)); }
process.exit(fail ? 1 : 0);
