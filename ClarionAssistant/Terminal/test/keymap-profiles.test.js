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
    // Every page function the command table names (run: cmdX) is a stub here; only the chord logic runs.
    const runNames = new Set(['navEmbed', 'openSnippetPicker']);
    const re = /run:\s*([A-Za-z_$][\w$]*)/g;
    let m;
    while ((m = re.exec(commandsSrc))) if (m[1] !== 'function') runNames.add(m[1]);   // inline run: function () {}
    const names = Array.from(runNames);
    const document = { querySelectorAll: () => [] };   // refreshSnippetChordLabels finds no help copy
    const exportsSrc = '\nreturn {' +
        ' EDITOR_COMMANDS: EDITOR_COMMANDS,' +
        ' KEY_PROFILES: typeof KEY_PROFILES !== "undefined" ? KEY_PROFILES : undefined,' +
        ' effectiveChord: effectiveChord, chordForId: chordForId, rebuildChordMap: rebuildChordMap,' +
        ' baseChord: typeof baseChord === "function" ? baseChord : undefined,' +
        ' setKeyProfile: typeof setKeyProfile === "function" ? setKeyProfile : undefined,' +
        ' normalizeKeyProfile: typeof normalizeKeyProfile === "function" ? normalizeKeyProfile : undefined,' +
        ' sanitizeLoadedKeyBindings: sanitizeLoadedKeyBindings,' +
        ' getProfile: function () { return typeof activeKeyProfile !== "undefined" ? activeKeyProfile : undefined; },' +
        ' setBindings: function (b) { keyBindings = b; }, getBindings: function () { return keyBindings; },' +
        ' chordMap: function () { return chordToCmd; } };';
    api = new Function(...names, 'document', commandsSrc + '\n' + sanitizeSrc + exportsSrc)(
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

// Keys the page claims BEFORE the rebindable dispatcher runs (IDE_SHORTCUTS, the Find interceptor, the
// Alt+<letter> menu block), plus Monaco/clipboard staples a profile must not steal.
const IDE_SHORTCUTS = new Function('return ' + slice(html, 'var IDE_SHORTCUTS = [', '];', 'IDE_SHORTCUTS')
    .replace('var IDE_SHORTCUTS = ', '') + ']')();
const RESERVED = new Set(IDE_SHORTCUTS.map(s => s.combo).concat([
    'Ctrl+S', 'F1', 'Ctrl+Shift+P', 'Tab', 'Shift+Tab', 'Ctrl+Space',
    'Ctrl+F', 'Ctrl+H', 'Ctrl+Shift+F', 'Ctrl+Shift+H', 'F3', 'Shift+F3', 'F12', 'Ctrl+F12',
    'Ctrl+Z', 'Ctrl+Y', 'Ctrl+C', 'Ctrl+V', 'Ctrl+A']));

api.KEY_PROFILES.forEach(p => {
    const map = p.map || {};
    const ids = Object.keys(map);
    check(p.id + ': maps only real commands', ids.every(id => !!byId[id]), ids.filter(id => !byId[id]).join(', '));
    check(p.id + ': every chord is canonical', ids.every(id => CANONICAL.test(map[id])),
        ids.filter(id => !CANONICAL.test(map[id])).map(id => id + '=' + map[id]).join(', '));
    check(p.id + ': no chord is one the page claims first',
        ids.every(id => !RESERVED.has(map[id]) && !/^Alt\+[A-Z]$/.test(map[id])),
        ids.filter(id => RESERVED.has(map[id]) || /^Alt\+[A-Z]$/.test(map[id])).map(id => id + '=' + map[id]).join(', '));
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
clean = api.sanitizeLoadedKeyBindings({ nope: 'Ctrl+9', removeLine: 'Ctrl+9' });
check('unknown ids are dropped, a free override is kept', clean.nope === undefined && clean.removeLine === 'Ctrl+9', JSON.stringify(clean));
use('vs');
clean = api.sanitizeLoadedKeyBindings({ upperCase: 'Ctrl+D' });
check('on a profile, an override colliding with a PROFILE chord is dropped', clean.upperCase === undefined, JSON.stringify(clean));
use('clarion');

finish();
