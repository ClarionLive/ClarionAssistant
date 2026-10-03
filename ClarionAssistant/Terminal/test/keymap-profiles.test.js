// keymap-profiles.test.js — guards the CA Editor's selectable keymap profiles (GH #206).
//
// Run:  node Terminal/test/keymap-profiles.test.js
//
// Zero-dependency. Like its neighbours this test EXTRACTS the page's code rather than copying it: the key
// command section (EDITOR_COMMANDS, KEY_PROFILES and the chord resolution around them) and
// sanitizeLoadedKeyBindings are sliced out of monaco-embeditor.html and evaluated against stubs.
//
// What is pinned:
//   * Resolution order: the developer's override -> the active profile's chord -> the Clarion default.
//   * The Clarion profile is today's behaviour, byte-identical.
//   * Every shipped profile is collision-free on its own, names only real commands, uses the canonical chord
//     grammar, and never lands on a key the page claims before the dispatcher sees it.
//   * The mappings #206 asked for, including the deliberate collision moves (Ctrl+D, Ctrl+Q, Ctrl+Shift+L).
//   * Switching profile never leaves two commands on one chord, and never drops a command: an override that
//     clashes with the new profile is reset (and reported), so its command falls back to the profile's chord.
//   * Nothing moves onto a core editing key (undo, clipboard, typing, caret movement); F-keys and other modified
//     combinations stay free, and a row may stay on its own default (Cut / Clear Line on Ctrl+X).
//   * The right-click Format Selection entry names the key Format Selection is currently on.

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

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }
function finish() {
    console.log('\n' + pass + ' passed, ' + fail + ' failed');
    if (fail) { console.log('\nFailures:'); failures.forEach(f => console.log('  - ' + f)); }
    process.exit(fail ? 1 : 0);
}

// ---------- load the page's key command section into a sandbox ----------
let api = null, loadError = null;
try {
    const commandsSrc = slice(html, '    var keyBindings = {};', '    // Map a keydown to a canonical chord', 'key command section');
    const sanitizeSrc = slice(html, '    function sanitizeLoadedKeyBindings(raw) {', '    // GH #126: gray out', 'sanitizeLoadedKeyBindings');
    // The right-click Format Selection entry (its label follows the Format Selection key).
    const formatSrc = slice(html, '    function registerFormatAction(ed) {', '    // ----- Debugger "Run to Cursor"', 'addFormatAction');
    // Every page function the command table names (run: cmdX) is a stub here; only the chord logic runs.
    const runNames = new Set(['navEmbed', 'openSnippetPicker']);
    const re = /run:\s*([A-Za-z_$][\w$]*)/g;
    let m;
    while ((m = re.exec(commandsSrc))) if (m[1] !== 'function') runNames.add(m[1]);   // inline run: function () {}
    const names = Array.from(runNames);
    const document = { querySelectorAll: () => [] };   // refreshSnippetChordLabels finds no help copy
    // Everything the page may or may not define yet is exported through a typeof guard, so a missing piece
    // fails its own check instead of the whole load.
    const exported = ['EDITOR_COMMANDS', 'KEY_PROFILES', 'effectiveChord', 'chordForId', 'rebuildChordMap', 'baseChord',
        'setKeyProfile', 'normalizeKeyProfile', 'sanitizeLoadedKeyBindings', 'pageKeyLabel', 'chordFromMonacoLabel',
        'monacoKeybinding', 'loadEditorActions', 'computeEditorKeyRules', 'keyConflicts', 'editorActionKeys',
        'chordOwner', 'resolveOverrideConflicts', 'coreKeyLabel', 'coreKeyRefused', 'addFormatAction'];
    const exportsSrc = '\nreturn {' + exported.map(n => ' ' + n + ': typeof ' + n + ' !== "undefined" ? ' + n + ' : undefined,').join('') +
        ' getProfile: function () { return typeof activeKeyProfile !== "undefined" ? activeKeyProfile : undefined; },' +
        ' getActions: function () { return typeof EDITOR_ACTIONS !== "undefined" ? EDITOR_ACTIONS : undefined; },' +
        ' setBindings: function (b) { keyBindings = b; }, getBindings: function () { return keyBindings; },' +
        ' chordMap: function () { return chordToCmd; } };';
    api = new Function(...names, 'document', commandsSrc + '\n' + sanitizeSrc + '\n' + formatSrc + exportsSrc)(
        ...names.map(() => function stub() { }), document);
} catch (e) { loadError = e.message; }

section('Extraction');
check('the page\'s key command section loads', !!api, loadError);
if (!api) finish();
check('the page defines KEY_PROFILES', Array.isArray(api.KEY_PROFILES));
check('the page defines baseChord / setKeyProfile / normalizeKeyProfile',
    !!(api.baseChord && api.setKeyProfile && api.normalizeKeyProfile));
if (!Array.isArray(api.KEY_PROFILES) || !api.setKeyProfile || !api.baseChord || !api.normalizeKeyProfile) finish();

const CMDS = api.EDITOR_COMMANDS;
const byId = {};
CMDS.forEach(c => { byId[c.id] = c; });
const profileIds = api.KEY_PROFILES.map(p => p.id);

function effectiveSet() {
    const out = {};
    CMDS.forEach(c => { out[c.id] = api.effectiveChord(c); });
    return out;
}
function duplicates(eff) {
    const seen = {}, dups = [];
    Object.keys(eff).forEach(id => {
        const ch = eff[id];
        if (!ch || byId[id].deferred) return;
        if (seen[ch]) dups.push(ch + ' (' + seen[ch] + ', ' + id + ')'); else seen[ch] = id;
    });
    return dups;
}
function use(profile, bindings) {
    api.setBindings(Object.assign({}, bindings || {}));
    return api.setKeyProfile(profile);
}

// ---------- profiles ----------
section('Profiles');
check('ships Clarion, VS Code, Visual Studio and Notepad++',
    ['clarion', 'vscode', 'vs', 'npp'].every(id => profileIds.indexOf(id) >= 0), profileIds.join(', '));
check('Clarion is the first (default) profile', profileIds[0] === 'clarion');
check('every profile has a label', api.KEY_PROFILES.every(p => typeof p.label === 'string' && p.label.length > 0));
check('a fresh page is on the Clarion profile', api.getProfile() === 'clarion', api.getProfile());
check('an unknown profile id normalizes to clarion', api.normalizeKeyProfile('emacs') === 'clarion'
    && api.normalizeKeyProfile(null) === 'clarion' && api.normalizeKeyProfile('vs') === 'vs');

use('clarion');
check('Clarion profile: every command is on its table default (today\'s behaviour, byte-identical)',
    CMDS.every(c => api.effectiveChord(c) === c.def),
    CMDS.filter(c => api.effectiveChord(c) !== c.def).map(c => c.id).join(', '));

// The chord grammar chordFromEvent produces: modifiers in the fixed order Ctrl, Shift, Alt, then one token.
const CANONICAL = /^(Ctrl\+)?(Shift\+)?(Alt\+)?([A-Z0-9]|F([1-9]|1[0-9]|2[0-4])|[\/\\.,;'\[\]\-=`]|Space|Enter|Tab|Delete|Insert|Home|End|Left|Right|Up|Down)$/;

// Monaco's actions and their real default keys, captured from Monaco itself by tools\monaco-keymap-probe.js.
const FIXTURE = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'monaco-keybindings.json'), 'utf8'));
const monacoById = {};
FIXTURE.actions.forEach(a => { monacoById[a.id] = a; });
check('the Monaco fixture matches the Monaco version the page loads',
    FIXTURE.monaco === (/var MONACO_VERSION = "([^"]+)"/.exec(html) || [])[1], FIXTURE.monaco);

// Keys the page claims BEFORE the rebindable dispatcher runs come from the page itself (pageKeyLabel); these
// clipboard / undo / completion staples are on top, for profiles only.
const STAPLES = new Set(['Ctrl+Z', 'Ctrl+Y', 'Ctrl+C', 'Ctrl+V', 'Ctrl+A', 'Ctrl+Space']);
check('the page defines pageKeyLabel', typeof api.pageKeyLabel === 'function');
const pageKey = ch => api.pageKeyLabel ? api.pageKeyLabel(ch) : null;
const IDE_SHORTCUTS = new Function('return ' + slice(html, 'var IDE_SHORTCUTS = [', '];', 'IDE_SHORTCUTS')
    .replace('var IDE_SHORTCUTS = ', '') + ']')();
check('every IDE shortcut is a page key', IDE_SHORTCUTS.every(s => !!pageKey(s.combo)), IDE_SHORTCUTS.map(s => s.combo).join(', '));
check('the Find interceptor\'s keys are page keys (any Ctrl+F / Ctrl+H, any F3, F12 unless Shift)',
    ['Ctrl+F', 'Ctrl+Alt+F', 'Ctrl+Shift+F', 'Ctrl+H', 'Ctrl+Shift+H', 'F3', 'Shift+F3', 'Ctrl+F3', 'F12', 'Alt+F12', 'Ctrl+F12', 'Ctrl+Shift+F12']
        .every(k => !!pageKey(k)));
check('Save, the palette and Alt+<letter> are page keys', ['Ctrl+S', 'F1', 'Ctrl+Shift+P', 'Alt+F', 'Alt+W'].every(k => !!pageKey(k)));
check('ordinary keys are not page keys', ['Shift+F12', 'Ctrl+D', 'Alt+Left', 'Ctrl+Alt+K', 'F2', 'Ctrl+K Ctrl+C'].every(k => !pageKey(k)));
check('a two-key chord starting on a page key is a page key', !!pageKey('Ctrl+F Ctrl+1'));

const TWO_KEY = /^\S+ \S+$/;
api.KEY_PROFILES.forEach(p => {
    const map = p.map || {};
    const ids = Object.keys(map);
    check(p.id + ': maps only real commands or Monaco actions', ids.every(id => !!byId[id] || !!monacoById[id]),
        ids.filter(id => !byId[id] && !monacoById[id]).join(', '));
    check(p.id + ': every chord is canonical (two-key chords only for Monaco actions)',
        ids.every(id => !map[id] || CANONICAL.test(map[id]) || (!!monacoById[id] && TWO_KEY.test(map[id])
            && map[id].split(' ').every(x => CANONICAL.test(x)))),
        ids.filter(id => map[id] && !CANONICAL.test(map[id])).map(id => id + '=' + map[id]).join(', '));
    check(p.id + ': no chord is one the page claims first, or a clipboard / undo / completion staple',
        ids.every(id => !pageKey(map[id]) && !STAPLES.has(map[id])),
        ids.filter(id => pageKey(map[id]) || STAPLES.has(map[id])).map(id => id + '=' + map[id]).join(', '));
    use(p.id);
    const dups = duplicates(effectiveSet());
    check(p.id + ': collision-free on its own (no two commands share a chord)', dups.length === 0, dups.join('; '));
    check(p.id + ': no command that has a default is left unbound',
        CMDS.every(c => !c.def || !!api.effectiveChord(c)),
        CMDS.filter(c => c.def && !api.effectiveChord(c)).map(c => c.id).join(', '));
});

// ---------- the mappings #206 asked for ----------
section('Mappings from #206');
function expectChord(profile, id, want) {
    use(profile);
    const got = api.effectiveChord(byId[id]);
    check(profile + ': ' + id + ' -> ' + want, got === want, 'got ' + got);
}
expectChord('vscode', 'duplicateLineAbove', 'Shift+Alt+Up');
expectChord('vscode', 'removeLine', 'Ctrl+Shift+K');
expectChord('vscode', 'navigateBack', 'Ctrl+Alt+-');
expectChord('vscode', 'navigateForward', 'Ctrl+Shift+-');
expectChord('vscode', 'toggleBookmark', 'Ctrl+Alt+K');
expectChord('vscode', 'nextBookmark', 'Ctrl+Alt+L');
expectChord('vscode', 'prevBookmark', 'Ctrl+Alt+J');
expectChord('vscode', 'insertSnippet', 'Ctrl+Shift+J');      // Ctrl+Space is completion in this editor
expectChord('vscode', 'structureDesigner', 'Ctrl+Shift+D');  // Ctrl+D stays Monaco's Add Next Occurrence
expectChord('vscode', 'lowerCase', 'Ctrl+Shift+Alt+L');      // Ctrl+Shift+L stays Monaco's Select All Occurrences

expectChord('vs', 'duplicateLineAbove', 'Ctrl+D');
expectChord('vs', 'structureDesigner', 'Ctrl+Shift+D');      // moved out of duplicate's way
expectChord('vs', 'removeLine', 'Ctrl+Shift+L');
expectChord('vs', 'lowerCase', 'Ctrl+U');                    // swapped out of removeLine's way
expectChord('vs', 'upperCase', 'Ctrl+Shift+U');
expectChord('vs', 'navigateBack', 'Ctrl+-');
expectChord('vs', 'navigateForward', 'Ctrl+Shift+-');

expectChord('npp', 'duplicateLineAbove', 'Ctrl+D');
expectChord('npp', 'structureDesigner', 'Ctrl+Shift+D');
expectChord('npp', 'commentLine', 'Ctrl+Q');
expectChord('npp', 'uncommentLine', 'Ctrl+Shift+Q');
expectChord('npp', 'removeLine', 'Ctrl+L');
expectChord('npp', 'lowerCase', 'Ctrl+U');
use('npp');
check('npp: Save and Exit is moved off Ctrl+Q, not dropped',
    !!api.effectiveChord(byId.saveAndExit) && api.effectiveChord(byId.saveAndExit) !== 'Ctrl+Q',
    api.effectiveChord(byId.saveAndExit));

// Clarion-only commands keep their Clarion chord in every profile.
['invertCase', 'markWord', 'nextFilledEmbed', 'prevFilledEmbed', 'formatLine'].forEach(id => {
    check(id + ' keeps its Clarion chord in every profile',
        profileIds.every(p => { use(p); return api.effectiveChord(byId[id]) === byId[id].def; }));
});

// ---------- overrides layered on a profile ----------
section('Overrides');
use('vs', { removeLine: 'Ctrl+Shift+Y' });
check('an override wins over the profile chord', api.effectiveChord(byId.removeLine) === 'Ctrl+Shift+Y');
check('the dispatch map routes the profile chord to its command',
    api.chordMap()['Ctrl+D'] === byId.duplicateLineAbove && api.chordMap()['Ctrl+Shift+D'] === byId.structureDesigner);
check('chordForId (help copy) reports the profile chord', api.chordForId('lowerCase') === 'Ctrl+U');

let dropped = use('clarion', { removeLine: 'Ctrl+Shift+D' });
check('on Clarion, an override on a free chord is kept', dropped.length === 0 && api.getBindings().removeLine === 'Ctrl+Shift+D');
api.setBindings({ removeLine: 'Ctrl+Shift+D' });
dropped = api.setKeyProfile('vs');
check('switching to a profile that wants that chord resets the override, and reports it',
    dropped.length === 1 && dropped[0] === 'removeLine' && api.getBindings().removeLine === undefined, JSON.stringify(dropped));
check('... and the command falls back to the profile chord, not to nothing',
    api.effectiveChord(byId.removeLine) === 'Ctrl+Shift+L' && api.effectiveChord(byId.structureDesigner) === 'Ctrl+Shift+D');
check('switching profile returns [] when nothing clashes', use('vscode').length === 0);
check('setKeyProfile records the active profile', api.getProfile() === 'vscode');
use('nonsense');
check('setKeyProfile with an unknown id lands on clarion', api.getProfile() === 'clarion');

// Exhaustive-ish: any set of overrides, under any profile, resolves to a collision-free, fully bound set.
section('Conflict resolution (fuzz)');
let seed = 206;
function rnd(n) { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed % n; }
const POOL = [];
CMDS.forEach(c => { if (c.def) POOL.push(c.def); });
api.KEY_PROFILES.forEach(p => Object.keys(p.map || {}).forEach(id => POOL.push(p.map[id])));
POOL.push('Ctrl+Shift+D', 'Ctrl+U', 'Ctrl+L', 'Ctrl+Shift+K', 'F9');
let fuzzBad = null;
for (let i = 0; i < 400 && !fuzzBad; i++) {
    const b = {};
    const n = 1 + rnd(6);
    for (let k = 0; k < n; k++) b[CMDS[rnd(CMDS.length)].id] = POOL[rnd(POOL.length)];
    const p = profileIds[rnd(profileIds.length)];
    use(p, b);
    const eff = effectiveSet();
    const dups = duplicates(eff);
    const unbound = CMDS.filter(c => c.def && !eff[c.id]).map(c => c.id);
    if (dups.length || unbound.length)
        fuzzBad = p + ' ' + JSON.stringify(b) + ' -> dups [' + dups.join('; ') + '] unbound [' + unbound.join(', ') + ']';
}
check('400 random override sets across all profiles: never a shared chord, never an unbound command', !fuzzBad, fuzzBad);

// ---------- loading persisted overrides ----------
section('sanitizeLoadedKeyBindings');
use('clarion');
let clean = api.sanitizeLoadedKeyBindings({ cutClarion: 'Ctrl+D' });
check('an override that takes a LATER command\'s default is dropped (it used to shadow that command)',
    clean.cutClarion === undefined, JSON.stringify(clean));
clean = api.sanitizeLoadedKeyBindings({ '<nope>': 'Ctrl+9', removeLine: 'Ctrl+9' });
check('malformed ids are dropped, a free override is kept', clean['<nope>'] === undefined && clean.removeLine === 'Ctrl+9', JSON.stringify(clean));
// (A well-formed unknown id may be a Monaco action: it is kept until the editor's actions are known — see below.)
use('vs');
clean = api.sanitizeLoadedKeyBindings({ upperCase: 'Ctrl+D' });
check('on a profile, an override colliding with a PROFILE chord is dropped', clean.upperCase === undefined, JSON.stringify(clean));
use('clarion');
clean = api.sanitizeLoadedKeyBindings({ 'editor.action.selectHighlights': 'Ctrl+Alt+Q', '<bad id>': 'Ctrl+9' });
check('before the editor exists, a Monaco action override is kept on trust; a malformed id is not',
    clean['editor.action.selectHighlights'] === 'Ctrl+Alt+Q' && clean['<bad id>'] === undefined, JSON.stringify(clean));

// ---------- Monaco key labels <-> the table's chords ----------
section('Monaco key labels');
const L = api.chordFromMonacoLabel;
check('the page defines chordFromMonacoLabel / monacoKeybinding', typeof L === 'function' && typeof api.monacoKeybinding === 'function');
[['ctrl+shift+l', 'Ctrl+Shift+L'], ['ctrl+k ctrl+c', 'Ctrl+K Ctrl+C'], ['shift+alt+up', 'Shift+Alt+Up'],
 ['ctrl+alt+=', 'Ctrl+Alt+='], ['ctrl+shift+\\', 'Ctrl+Shift+\\'], ['ctrl+[', 'Ctrl+['], ['f8', 'F8'],
 ['shift+f10', 'Shift+F10'], ['ctrl+alt+backspace', 'Ctrl+Alt+Backspace'], ['ctrl+k ctrl+shift+l', 'Ctrl+K Ctrl+Shift+L'],
 ['alt+shift+a', 'Shift+Alt+A'], ['meta+k', ''], ['ctrl+numpad_add', ''], ['', '']]
    .forEach(([monaco, want]) => check('"' + monaco + '" -> "' + want + '"', L(monaco) === want, 'got "' + L(monaco) + '"'));

// A fake monaco namespace: distinct numbers per key code, and the real modifier bit layout.
const KeyCode = new Proxy({}, { get: (t, k) => (typeof k === 'string' && /^(Key[A-Z]|Digit[0-9]|F([1-9]|1[0-9]|2[0-4])|UpArrow|DownArrow|LeftArrow|RightArrow|Enter|Space|Tab|Delete|Insert|Home|End|Backspace|Escape|PageUp|PageDown|Slash|Backslash|Period|Comma|Semicolon|Quote|BracketLeft|BracketRight|Minus|Equal|Backquote)$/.test(k))
    ? (t[k] || (t[k] = Object.keys(t).length + 1)) : undefined });
const M = { KeyCode, KeyMod: { CtrlCmd: 2048, Shift: 1024, Alt: 512, chord: (a, b) => (a | (b << 16)) >>> 0 } };
const kb = ch => api.monacoKeybinding(ch, M);
check('Ctrl+Shift+L -> KeyL | Ctrl | Shift', kb('Ctrl+Shift+L') === (KeyCode.KeyL | 2048 | 1024));
check('Shift+Alt+Up -> UpArrow | Shift | Alt', kb('Shift+Alt+Up') === (KeyCode.UpArrow | 1024 | 512));
check('Ctrl+K Ctrl+C -> a chord of the two', kb('Ctrl+K Ctrl+C') === M.KeyMod.chord(KeyCode.KeyK | 2048, KeyCode.KeyC | 2048));
check('Ctrl+- and Ctrl+\\ map to Minus / Backslash', kb('Ctrl+-') === (KeyCode.Minus | 2048) && kb('Ctrl+\\') === (KeyCode.Backslash | 2048));
check('an unknown key, a Meta modifier or three parts -> 0', kb('Ctrl+NumpadAdd') === 0 && kb('Meta+K') === 0 && kb('Ctrl+K Ctrl+C Ctrl+D') === 0);
check('every key Monaco reports round-trips to a keybinding',
    FIXTURE.actions.every(a => a.keys.every(k => kb(k) !== 0)),
    FIXTURE.actions.filter(a => a.keys.some(k => kb(k) === 0)).map(a => a.id + ' ' + a.keys.join('/')).join('; '));

// ---------- Monaco's actions in the table ----------
section('Monaco actions');
const fakeKs = {
    getKeybindings: () => [].concat(...FIXTURE.actions.map(a => a.keys.map(k => ({
        command: a.id, resolvedKeybinding: { getUserSettingsLabel: () => k.toLowerCase() } })))),
    lookupKeybinding: id => monacoById[id] && monacoById[id].keys.length
        ? { getUserSettingsLabel: () => monacoById[id].keys[0].toLowerCase() } : null
};
const fakeEditor = { _standaloneKeybindingService: fakeKs, getSupportedActions: () => FIXTURE.actions.map(a => ({ id: a.id, label: a.label })) };
use('clarion', { 'editor.action.selectHighlights': 'Ctrl+Alt+Q', 'editor.action.nope': 'Ctrl+Alt+W' });
check('loadEditorActions reads Monaco\'s actions once', api.loadEditorActions(fakeEditor) === true && api.loadEditorActions(fakeEditor) === false);
const ACTS = api.getActions();
check('... all of them, with their keys', ACTS.length === FIXTURE.actions.length
    && ACTS.find(a => a.id === 'editor.action.triggerSuggest').defs.join('/') === 'Ctrl+Space/Ctrl+I');
check('... sorted by label', ACTS.every((a, i) => i === 0 || ACTS[i - 1].label.toLowerCase() <= a.label.toLowerCase()));
check('... and an override trusted earlier is checked now: a real action kept, an unknown one pruned',
    api.getBindings()['editor.action.selectHighlights'] === 'Ctrl+Alt+Q' && api.getBindings()['editor.action.nope'] === undefined,
    JSON.stringify(api.getBindings()));
const act = id => ACTS.find(a => a.id === id);

section('Monaco keybinding rules');
let rules = api.computeEditorKeyRules(M);
check('a moved action: its default key removed, its new key added',
    JSON.stringify(rules) === JSON.stringify([{ keybinding: kb('Ctrl+Shift+L'), command: '-editor.action.selectHighlights' },
                                              { keybinding: kb('Ctrl+Alt+Q'), command: 'editor.action.selectHighlights' }]), JSON.stringify(rules));
use('clarion', { 'editor.action.triggerSuggest': 'Ctrl+Alt+Space' });
rules = api.computeEditorKeyRules(M);
check('an action with two default keys loses both', rules.filter(r => r.command === '-editor.action.triggerSuggest').length === 2
    && rules.some(r => r.command === 'editor.action.triggerSuggest' && r.keybinding === kb('Ctrl+Alt+Space')), JSON.stringify(rules));
use('clarion');
check('no overrides and no Monaco ids in the profile -> no rules', api.computeEditorKeyRules(M).length === 0);
check('every shipped profile produces only rules Monaco can take',
    profileIds.every(p => { use(p); return api.computeEditorKeyRules(M).every(r => r.keybinding); }));

section('Clashes between Clarion and Monaco keys');
use('clarion');
let notes = api.keyConflicts();
check('Clarion profile: Lowercase is shown taking Ctrl+Shift+L from Select All Occurrences',
    (notes.lowerCase || []).some(n => /Ctrl\+Shift\+L/.test(n) && /Select All Occurrences/.test(n)), JSON.stringify(notes.lowerCase));
check('... and the Monaco row says who took it',
    (notes['editor.action.selectHighlights'] || []).some(n => /Lowercase/.test(n)), JSON.stringify(notes['editor.action.selectHighlights']));
check('a Monaco key the page handles is noted (Find on Ctrl+F)',
    (notes['actions.find'] || []).some(n => /Ctrl\+F/.test(n) && /CA Editor/.test(n)), JSON.stringify(notes['actions.find']));
use('vscode');
notes = api.keyConflicts();
['editor.action.selectHighlights', 'editor.action.addSelectionToNextFindMatch', 'editor.action.rename'].forEach(id =>
    check('VS Code profile: ' + id + ' keeps its Monaco key', !notes[id] || !notes[id].some(n => /is taken by/.test(n)), JSON.stringify(notes[id])));

// The VS Code profile may take a Monaco key ONLY where a Clarion command does that same job.
const VSCODE_REPLACES = {
    'Ctrl+/': 'editor.action.commentLine',            // Comment Line (Clarion "!" comment, slot-guarded)
    'Shift+Alt+Up': 'editor.action.copyLinesUpAction', // Duplicate Line (up)
    'Ctrl+Shift+K': 'editor.action.deleteLines',       // Remove Line
    'Ctrl+I': 'editor.action.triggerSuggest'           // Format Selection keeps Ctrl+I; Suggest keeps Ctrl+Space
};
const taken = [];
CMDS.forEach(c => {
    const ch = api.effectiveChord(c);
    FIXTURE.actions.forEach(a => { if (ch && a.keys.indexOf(ch) >= 0 && VSCODE_REPLACES[ch] !== a.id) taken.push(c.id + ' takes ' + ch + ' from ' + a.id); });
});
check('VS Code profile: no Clarion command takes a Monaco key except the deliberate replacements', taken.length === 0, taken.join('; '));
use('clarion');

section('Overrides on Monaco actions');
let d = use('clarion', { 'editor.action.gotoLine': 'Ctrl+Alt+G' });
check('a Monaco action on a free key is kept', d.length === 0 && api.getBindings()['editor.action.gotoLine'] === 'Ctrl+Alt+G');
check('... and answers to it alone', JSON.stringify(api.editorActionKeys(act('editor.action.gotoLine'))) === '["Ctrl+Alt+G"]');
d = use('clarion', { 'editor.action.gotoLine': 'Ctrl+D' });
check('a Monaco action on a Clarion key is reset (Monaco cannot win it)', d.indexOf('editor.action.gotoLine') >= 0);
d = use('clarion', { 'editor.action.gotoLine': 'Ctrl+K Ctrl+C' });
check('a Monaco action on another action\'s key is reset', d.indexOf('editor.action.gotoLine') >= 0);
d = use('clarion', { 'editor.action.gotoLine': 'Ctrl+F' });
check('a Monaco action on a page key is reset', d.indexOf('editor.action.gotoLine') >= 0);
d = use('clarion', { upperCase: 'Ctrl+G' });
check('a Clarion command MAY take a Monaco key', d.length === 0 && api.effectiveChord(byId.upperCase) === 'Ctrl+G');
check('... and the table says so', (api.keyConflicts().upperCase || []).some(n => /Go to Line/.test(n)));
d = use('clarion', { upperCase: 'Ctrl+S' });
check('a Clarion command on a page key is reset', d.indexOf('upperCase') >= 0);
use('clarion', { upperCase: 'Ctrl+G' });
const owner = api.chordOwner('Ctrl+G', 'removeLine');
check('chordOwner finds the Clarion command first', owner && owner.id === 'upperCase' && owner.kind === 'clarion', JSON.stringify(owner));
use('clarion');
const owner2 = api.chordOwner('Ctrl+G', 'removeLine');
check('chordOwner finds a Monaco action', owner2 && owner2.id === 'editor.action.gotoLine' && owner2.kind === 'editor', JSON.stringify(owner2));

section('Conflict resolution with Monaco actions (fuzz)');
const IDS = CMDS.map(c => c.id).concat(['editor.action.gotoLine', 'editor.action.selectHighlights', 'editor.action.triggerSuggest',
    'editor.action.addSelectionToNextFindMatch', 'editor.action.deleteLines', 'expandLineSelection']);
const POOL2 = POOL.concat(['Ctrl+G', 'Ctrl+Space', 'Ctrl+K Ctrl+C', 'Ctrl+Alt+G', 'Ctrl+F', 'Ctrl+S', 'Ctrl+Alt+Q']);
let fuzz2 = null;
for (let i = 0; i < 400 && !fuzz2; i++) {
    const b = {};
    const n = 1 + rnd(7);
    for (let k = 0; k < n; k++) b[IDS[rnd(IDS.length)]] = POOL2[rnd(POOL2.length)];
    const p = profileIds[rnd(profileIds.length)];
    use(p, b);
    const eff = effectiveSet();
    const dups = duplicates(eff);
    const unbound = CMDS.filter(c => c.def && !eff[c.id]).map(c => c.id);
    const clarion = {};
    CMDS.forEach(c => { if (eff[c.id]) clarion[eff[c.id]] = c.id; });
    const bad = [];
    ACTS.forEach(a => {
        const o = api.getBindings()[a.id];
        if (!o) return;
        if (clarion[o] || pageKey(o)) bad.push(a.id + '=' + o);
        ACTS.forEach(x => { if (x !== a && api.editorActionKeys(x).indexOf(o) >= 0) bad.push(a.id + '=' + o + ' shares with ' + x.id); });
    });
    const onPage = CMDS.filter(c => pageKey(eff[c.id])).map(c => c.id);
    if (dups.length || unbound.length || bad.length || onPage.length)
        fuzz2 = p + ' ' + JSON.stringify(b) + ' -> dups [' + dups.join('; ') + '] unbound [' + unbound.join(', ') +
            '] monaco [' + bad.join('; ') + '] on page keys [' + onPage.join(', ') + ']';
}
check('400 random Clarion + Monaco override sets: consistent every time', !fuzz2, fuzz2);
use('clarion');

// ---------- core editing keys (review of #249) ----------
// Undo, the clipboard, typing and caret movement are Monaco core commands, not table rows — so nothing may be
// MOVED onto them: an action on Ctrl+Z, or a command on a bare letter, would break editing with no clash shown.
section('Core editing keys');
const core = ch => api.coreKeyLabel ? api.coreKeyLabel(ch) : null;
check('the page defines coreKeyLabel / coreKeyRefused', typeof api.coreKeyLabel === 'function' && typeof api.coreKeyRefused === 'function');
const CORE = ['Ctrl+Z', 'Ctrl+Y', 'Ctrl+Shift+Z', 'Ctrl+C', 'Ctrl+V', 'Ctrl+X', 'Ctrl+A', 'Ctrl+Insert', 'Shift+Insert', 'Shift+Delete',
    'A', 'Q', '7', '/', 'Space', 'Enter', 'Tab', 'Escape', 'Up', 'Down', 'Left', 'Right', 'Home', 'End', 'PageUp', 'PageDown',
    'Insert', 'Backspace', 'Delete', 'Shift+A', 'Shift+7', 'Shift+Tab', 'Shift+Enter', 'Shift+Up', 'Shift+Home', 'Shift+PageDown',
    'Ctrl+Left', 'Ctrl+Right', 'Ctrl+Shift+Left', 'Ctrl+Home', 'Ctrl+Shift+End', 'Ctrl+Backspace', 'Ctrl+Delete', 'Ctrl+Z Ctrl+1'];
check('undo, the clipboard, typing and caret keys are core keys', CORE.every(k => !!core(k)), CORE.filter(k => !core(k)).join(', '));
const FREE = ['F2', 'F9', 'Shift+F2', 'Ctrl+F2', 'Shift+Alt+F5', 'Ctrl+D', 'Ctrl+Shift+Y', 'Alt+Left', 'Shift+Alt+Up', 'Ctrl+Alt+Z',
    'Ctrl+Shift+A', 'Ctrl+2', 'Ctrl+K Ctrl+C', 'Ctrl+Up', 'Alt+Enter'];
check('F-keys and other modified combinations are free', FREE.every(k => !core(k)), FREE.filter(k => core(k)).join(', '));
check('Ctrl+Z is labelled Undo (the refusal names what the key does)', core('Ctrl+Z') === 'Undo' && core('Ctrl+V') === 'Paste');

api.KEY_PROFILES.forEach(p => {
    const map = p.map || {};
    check(p.id + ': no profile chord is a core editing key', Object.keys(map).every(id => !core(map[id])),
        Object.keys(map).filter(id => core(map[id])).map(id => id + '=' + map[id]).join(', '));
});
const coreDefs = CMDS.filter(c => core(c.def)).map(c => c.id + '=' + c.def);
check('the only Clarion default on a core key is Cut / Clear Line on Ctrl+X (that IS the cut key)',
    coreDefs.join(',') === 'cutClarion=Ctrl+X', coreDefs.join(', '));
check('... and every row may stay on its own base key in every profile', profileIds.every(p => {
    use(p); return CMDS.every(c => !c.def || !api.coreKeyRefused(c.id, api.baseChord(c)))
        && ACTS.every(a => a.defs.every(k => !api.coreKeyRefused(a.id, k)));
}));
use('clarion');

[['upperCase', 'Ctrl+Z'], ['removeLine', 'Ctrl+V'], ['removeLine', 'A'], ['markWord', 'Enter'], ['invertCase', 'Shift+Up'],
 ['upperCase', 'Tab'], ['upperCase', 'Ctrl+Left'], ['upperCase', 'Ctrl+A']].forEach(([id, ch]) => {
    const got = api.sanitizeLoadedKeyBindings({ [id]: ch });
    check('a loaded Clarion override on ' + ch + ' (' + id + ') is dropped', got[id] === undefined, JSON.stringify(got));
});
let kept = api.sanitizeLoadedKeyBindings({ cutClarion: 'Ctrl+X', removeLine: 'F9', upperCase: 'Ctrl+Alt+Z' });
check('a row on its own base core key, an F-key and a modified combo are kept',
    kept.cutClarion === 'Ctrl+X' && kept.removeLine === 'F9' && kept.upperCase === 'Ctrl+Alt+Z', JSON.stringify(kept));
[['editor.action.gotoLine', 'Ctrl+Z'], ['editor.action.gotoLine', 'Ctrl+A'], ['editor.action.selectHighlights', 'Q'],
 ['editor.action.selectHighlights', 'Shift+Home'], ['editor.action.gotoLine', 'Ctrl+End']].forEach(([id, ch]) => {
    const got = api.sanitizeLoadedKeyBindings({ [id]: ch });
    check('a loaded Monaco override on ' + ch + ' (' + id + ') is dropped', got[id] === undefined, JSON.stringify(got));
});
kept = api.sanitizeLoadedKeyBindings({ 'editor.action.nextMatchFindAction': 'Enter', 'editor.action.gotoLine': 'Ctrl+Alt+G' });
check('a Monaco action may stay on one of its own Monaco keys (Find Next on Enter)',
    kept['editor.action.nextMatchFindAction'] === 'Enter' && kept['editor.action.gotoLine'] === 'Ctrl+Alt+G', JSON.stringify(kept));
d = use('vscode', { upperCase: 'Ctrl+C', 'editor.action.gotoLine': 'Backspace' });
check('a profile switch drops core-key overrides too, and reports them',
    d.indexOf('upperCase') >= 0 && d.indexOf('editor.action.gotoLine') >= 0, JSON.stringify(d));

const POOL3 = POOL2.concat(['Ctrl+Z', 'Ctrl+Y', 'Ctrl+C', 'Ctrl+V', 'Ctrl+X', 'Ctrl+A', 'A', 'Enter', 'Tab', 'Up', 'Shift+Left',
    'Home', 'Delete', 'Space', 'Ctrl+Backspace', 'F9', 'Shift+F9']);
let fuzz3 = null;
for (let i = 0; i < 400 && !fuzz3; i++) {
    const b = {};
    const n = 1 + rnd(7);
    for (let k = 0; k < n; k++) b[IDS[rnd(IDS.length)]] = POOL3[rnd(POOL3.length)];
    use(profileIds[rnd(profileIds.length)], b);
    const bad = Object.keys(api.getBindings()).filter(id => api.coreKeyRefused(id, api.getBindings()[id]));
    if (bad.length) fuzz3 = JSON.stringify(b) + ' -> left on core keys: ' + bad.map(id => id + '=' + api.getBindings()[id]).join(', ');
}
check('400 random override sets with core keys in the pool: no override survives on a core key', !fuzz3, fuzz3);
use('clarion');

// ---------- the right-click Format Selection entry names its live key ----------
section('Format Selection context-menu label');
function fakeEd() {
    const ed = { added: [], live: null, onDispose: null };
    ed.addAction = function (desc) { const h = { label: desc.label, disposed: false, dispose() { this.disposed = true; } }; ed.added.push(h); ed.live = h; return h; };
    ed.onDidDispose = function (cb) { ed.onDispose = cb; };
    return ed;
}
const fed = fakeEd();
api.addFormatAction(fed);
check('the entry shows the default key', fed.live && fed.live.label === 'Format Selection (Ctrl+I)', fed.live && fed.live.label);
use('clarion', { formatLine: 'Ctrl+Alt+9' });
check('rebinding Format Selection re-adds the entry with the new key, disposing the old one',
    fed.live.label === 'Format Selection (Ctrl+Alt+9)' && fed.added.length === 2 && fed.added[0].disposed, fed.live.label);
use('clarion', { formatLine: 'Ctrl+Alt+9', upperCase: 'Ctrl+9' });
check('an unrelated rebind leaves the entry alone', fed.added.length === 2);
fed.onDispose();
use('clarion');
check('a disposed editor is dropped (its entry is not re-added)', fed.added.length === 2);

finish();
