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
const tableSrc = slice(html, '    // ----- Keyboard rebinding table (gear panel) -----', '    // ===================== Code Snippets manager', 'Keyboard table section');
const chordSrc = slice(html, '    // Map a keydown to a canonical chord', '    function installKeyBindings() {', 'chord mapping');
const FIXTURE = JSON.parse(fs.readFileSync(require('path').join(__dirname, 'fixtures', 'monaco-keybindings.json'), 'utf8'));
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
        showToast: (msg, ok) => { spy.toasts.push({ msg, ok }); }
    };
    const params = names.concat(Object.keys(scope));
    const args = names.map(() => function stub() { }).concat(Object.values(scope));
    const page = new Function(...params, escHtmlSrc + keySrc + chordSrc + tableSrc +
        '\nreturn { buildKeybindTable: buildKeybindTable, onKeyProfileChosen: onKeyProfileChosen,' +
        ' setKeyProfile: setKeyProfile, KEY_PROFILES: KEY_PROFILES, loadEditorActions: loadEditorActions,' +
        ' wireKeybindFilters: wireKeybindFilters, startKeyCapture: startKeyCapture, handleKeyCapture: handleKeyCapture,' +
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

    // ---------- the editor's own commands ----------
    section('The editor\'s own commands');
    page.onKeyProfileChosen('clarion');
    page.setBindings({});
    page.wireKeybindFilters();
    const chip = f => doc.querySelector('#kbFilters .kb-chip[data-filter="' + f + '"]');
    const click = el => el.dispatchEvent(new doc.defaultView.Event('click'));
    const typeSearch = t => { const s = doc.getElementById('kbSearch'); s.value = t; s.dispatchEvent(new doc.defaultView.Event('input')); };
    const rowCount = () => doc.querySelectorAll('#keybindRows tr').length;
    const fakeKs = {
        getKeybindings: () => [].concat(...FIXTURE.actions.map(a => a.keys.map(k => ({
            command: a.id, resolvedKeybinding: { getUserSettingsLabel: () => k.toLowerCase() } })))),
        lookupKeybinding: id => { const a = FIXTURE.actions.find(x => x.id === id); return a && a.keys.length ? { getUserSettingsLabel: () => a.keys[0].toLowerCase() } : null; }
    };
    page.loadEditorActions({ _standaloneKeybindingService: fakeKs, getSupportedActions: () => FIXTURE.actions.map(a => ({ id: a.id, label: a.label })) });
    page.buildKeybindTable();
    const clarionRows = rowCount();
    check('the table opens on the Clarion commands alone', chip('clarion').classList.contains('on')
        && !row(doc, 'editor.action.selectHighlights') && !!row(doc, 'removeLine'));
    check('Clarion rows are tagged Clarion', /Clarion/.test(doc.querySelector('#keybindRows tr').textContent));
    click(chip('all'));
    check('"All" adds every Monaco action', rowCount() === clarionRows + FIXTURE.actions.length, rowCount() + ' rows');
    check('a Monaco row shows Monaco\'s key', row(doc, 'editor.action.selectHighlights').def === 'Ctrl+Shift+L');
    check('a Monaco action with two keys shows both', row(doc, 'editor.action.triggerSuggest').def === 'Ctrl+Space, Ctrl+I');
    click(chip('editor'));
    check('"Editor" shows only Monaco actions', rowCount() === FIXTURE.actions.length && !row(doc, 'removeLine'));

    click(chip('clarion'));
    typeSearch('occurrences');
    check('searching the Clarion view for a Monaco action finds nothing, and says to try All',
        rowCount() === 0 && /try All/.test(doc.getElementById('kbEmpty').textContent)
        && !doc.getElementById('kbEmpty').classList.contains('hidden'));
    click(chip('all'));
    check('... while All finds it', !!row(doc, 'editor.action.selectHighlights'));
    typeSearch('ctrl+shift+l');
    check('search matches keys too', !!row(doc, 'editor.action.selectHighlights') && !!row(doc, 'lowerCase'));
    typeSearch('');

    click(chip('clashes'));
    const noteOf = id => { const i = doc.querySelector('#keybindRows .kb-input[data-cmd="' + id + '"]'); const n = i && i.closest('tr').querySelector('.kb-note'); return n ? n.textContent : ''; };
    check('"Clashes" lists Lowercase, noting the Monaco key it takes', /takes Ctrl\+Shift\+L from Select All Occurrences/.test(noteOf('lowerCase')), noteOf('lowerCase'));
    check('... and Select All Occurrences, noting who took it', /taken by Lowercase/.test(noteOf('editor.action.selectHighlights')), noteOf('editor.action.selectHighlights'));
    check('... and not a command with no clash', !row(doc, 'invertCase'));
    page.onKeyProfileChosen('vscode');
    check('VS Code profile: Select All Occurrences no longer clashes', !row(doc, 'editor.action.selectHighlights'));
    page.onKeyProfileChosen('clarion');

    section('Setting keys');
    click(chip('all'));
    const press = (code, mods) => page.handleKeyCapture(Object.assign({ key: 'x', code: code,
        ctrlKey: false, shiftKey: false, altKey: false, metaKey: false,
        preventDefault() { }, stopImmediatePropagation() { } }, mods));
    const capture = id => page.startKeyCapture(doc.querySelector('#keybindRows .kb-input[data-cmd="' + id + '"]'));
    const persistedBefore = spy.persisted;
    capture('editor.action.gotoLine');
    press('KeyQ', { ctrlKey: true, altKey: true });
    check('a Monaco action takes a free key', page.getBindings()['editor.action.gotoLine'] === 'Ctrl+Alt+Q'
        && spy.persisted === persistedBefore + 1, JSON.stringify(page.getBindings()));
    capture('editor.action.gotoLine');
    press('KeyD', { ctrlKey: true });
    let t = spy.toasts[spy.toasts.length - 1];
    check('a Monaco action cannot take a Clarion command\'s key', page.getBindings()['editor.action.gotoLine'] === 'Ctrl+Alt+Q'
        && t.ok === false && /Structure Designer/.test(t.msg), JSON.stringify(t));
    press('KeyF', { ctrlKey: true });
    t = spy.toasts[spy.toasts.length - 1];
    check('nothing can take a key the page handles', /used by the CA Editor \(Find\)/.test(t.msg), JSON.stringify(t));
    page.handleKeyCapture({ key: 'Escape', code: 'Escape', preventDefault() { }, stopImmediatePropagation() { } });
    capture('upperCase');
    press('KeyL', { ctrlKey: true });   // Monaco's Expand Line Selection (Go to Line was moved above)
    t = spy.toasts[spy.toasts.length - 1];
    check('a Clarion command may take a Monaco key, and the toast says what it took',
        page.getBindings().upperCase === 'Ctrl+L' && t.ok === true && /Expand Line Selection/.test(t.msg), JSON.stringify(t));
}

console.log('\n' + pass + ' passed, ' + fail + ' failed');
if (fail) { console.log('\nFailures:'); failures.forEach(f => console.log('  - ' + f)); }
process.exit(fail ? 1 : 0);
