// monaco-keymap-probe.js — runs the CA Editor's keymap code against REAL Monaco in headless Edge (GH #206).
//
// Run:  node Terminal/test/tools/monaco-keymap-probe.js                  (integration checks)
//       node Terminal/test/tools/monaco-keymap-probe.js --write-fixture  (refresh ../fixtures/monaco-keybindings.json)
//
// Not part of Run-Tests.ps1: it needs Microsoft Edge and the jsdelivr CDN, which the node harnesses never do.
// Run it after a Monaco upgrade (MONACO_VERSION in monaco-embeditor.html) — the fixture is what
// keymap-profiles.test.js checks every profile against, so a stale fixture means stale guarantees.
//
// What it does: builds a throwaway page that loads the SAME Monaco version the editor uses, registers a language
// with the providers the CA Editor registers (so rename / format / definition actions exist, as they do there),
// then evaluates the page's own key command section (sliced from monaco-embeditor.html, as the node tests do)
// against the live editor and keybinding service. Exit 0 = all checks passed, 1 = a check failed, 2 = could not run.

const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const TERMINAL = path.join(__dirname, '..', '..');
const html = fs.readFileSync(path.join(TERMINAL, 'monaco-embeditor.html'), 'utf8');
const FIXTURE = path.join(__dirname, '..', 'fixtures', 'monaco-keybindings.json');
const writeFixture = process.argv.indexOf('--write-fixture') >= 0;

function slice(text, a, b, what) {
    const i = text.indexOf(a);
    if (i < 0) throw new Error('could not find start of ' + what);
    const j = text.indexOf(b, i);
    if (j < 0) throw new Error('could not find end of ' + what);
    return text.slice(i, j);
}

const version = (/var MONACO_VERSION = "([^"]+)"/.exec(html) || [])[1];
if (!version) { console.error('COULD NOT RUN: MONACO_VERSION not found in the page'); process.exit(2); }

const edge = ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
              'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'].find(p => fs.existsSync(p));
if (!edge) { console.error('COULD NOT RUN: Microsoft Edge not found'); process.exit(2); }

const keySrc = slice(html, '    var keyBindings = {};', '    // Map a keydown to a canonical chord', 'key command section');
const runNames = new Set(['navEmbed', 'openSnippetPicker']);
let m; const re = /run:\s*([A-Za-z_$][\w$]*)/g;
while ((m = re.exec(keySrc))) if (m[1] !== 'function') runNames.add(m[1]);
const stubs = Array.from(runNames).map(n => 'function ' + n + '() {}').join('\n');

const base = 'https://cdn.jsdelivr.net/npm/monaco-editor@' + version + '/min/vs';
const page = `<!doctype html><html><body><div id="c" style="width:800px;height:400px"></div><pre id="out">pending</pre>
<script src="${base}/loader.js"></script>
<script>
require.config({ paths: { vs: '${base}' } });
require(['vs/editor/editor.main'], function () {
  var out = { version: monaco.editor.EditorType ? '${version}' : '?' };
  function done() { document.getElementById('out').textContent = 'JSON:' + JSON.stringify(out) + ':END'; }
  try {
    monaco.languages.register({ id: 'probe' });
    var R = monaco.languages;
    R.registerRenameProvider('probe', { provideRenameEdits: function () { return { edits: [] }; } });
    R.registerDocumentFormattingEditProvider('probe', { provideDocumentFormattingEdits: function () { return []; } });
    R.registerDocumentRangeFormattingEditProvider('probe', { provideDocumentRangeFormattingEdits: function () { return []; } });
    R.registerDefinitionProvider('probe', { provideDefinition: function () { return null; } });
    R.registerReferenceProvider('probe', { provideReferences: function () { return []; } });
    R.registerDocumentSymbolProvider('probe', { provideDocumentSymbols: function () { return []; } });
    R.registerHoverProvider('probe', { provideHover: function () { return null; } });
    var editor = monaco.editor.create(document.getElementById('c'), { value: 'a b a', language: 'probe' });
    // ---- the page's own key command section ----
    var document_ = document;
    ${stubs}
    function showToast() {}
    (function () {
      ${keySrc}
      var ks = editor._standaloneKeybindingService;
      loadEditorActions(editor);
      out.actions = EDITOR_ACTIONS.map(function (a) { return { id: a.id, label: a.label, keys: a.defs }; });
      var lk = function (id) { var k = ks.lookupKeybinding(id); return k ? chordFromMonacoLabel(k.getUserSettingsLabel()) : null; };
      out.before = { selectHighlights: lk('editor.action.selectHighlights'), triggerSuggest: lk('editor.action.triggerSuggest') };
      // An override on a Monaco action moves its key in Monaco itself.
      keyBindings['editor.action.selectHighlights'] = 'Ctrl+Alt+Q';
      rebuildChordMap();
      out.afterOverride = lk('editor.action.selectHighlights');
      out.oldKeyStillBound = ks.getKeybindings().some(function (k) {
        return k.command === 'editor.action.selectHighlights' && k.resolvedKeybinding
          && chordFromMonacoLabel(k.resolvedKeybinding.getUserSettingsLabel()) === 'Ctrl+Shift+L'; });
      // A Monaco action with TWO default keys loses both when moved.
      keyBindings['editor.action.triggerSuggest'] = 'Ctrl+Alt+Space';
      rebuildChordMap();
      out.suggestKeys = ks.getKeybindings().filter(function (k) { return k.command === 'editor.action.triggerSuggest' && k.resolvedKeybinding; })
        .map(function (k) { return chordFromMonacoLabel(k.resolvedKeybinding.getUserSettingsLabel()); });
      // Clearing the overrides puts Monaco's own keys back.
      keyBindings = {};
      rebuildChordMap();
      out.afterReset = { selectHighlights: lk('editor.action.selectHighlights'), triggerSuggest: lk('editor.action.triggerSuggest') };
      // A profile that maps a Monaco action applies the same way.
      KEY_PROFILES.push({ id: 'probe', label: 'Probe', map: { 'editor.action.gotoLine': 'Ctrl+Alt+G' } });
      setKeyProfile('probe');
      out.profileMoved = lk('editor.action.gotoLine');
      setKeyProfile('clarion');
      out.profileBack = lk('editor.action.gotoLine');
    })();
  } catch (e) { out.error = (e && e.stack) || String(e); }
  done();
});
</script></body></html>`;

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ca-keymap-probe-'));
const file = path.join(tmp, 'probe.html');
fs.writeFileSync(file, page);
let dom;
try {
    dom = execFileSync(edge, ['--headless=new', '--disable-gpu', '--virtual-time-budget=20000',
        '--user-data-dir=' + path.join(tmp, 'profile'), '--dump-dom', 'file:///' + file.replace(/\\/g, '/')],
        { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore'] });
} catch (e) { console.error('COULD NOT RUN: Edge failed: ' + e.message); process.exit(2); }
finally { try { fs.rmSync(tmp, { recursive: true, force: true }); } catch (e) { } }

const mm = /JSON:([\s\S]*?):END/.exec(dom);
if (!mm) { console.error('COULD NOT RUN: no result from the page (CDN unreachable?)'); process.exit(2); }
const out = JSON.parse(mm[1].replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"'));
if (out.error) { console.error('FAILED in page: ' + out.error); process.exit(1); }

if (writeFixture) {
    const fixture = { monaco: version, note: 'Generated by tools/monaco-keymap-probe.js --write-fixture. Do not edit by hand.',
        actions: out.actions };
    fs.writeFileSync(FIXTURE, JSON.stringify(fixture, null, 1) + '\n');
    console.log('wrote ' + FIXTURE + ': ' + out.actions.length + ' actions, ' + out.actions.filter(a => a.keys.length).length + ' with keys');
}

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail !== undefined ? ' — ' + JSON.stringify(detail) : '')); }
}
console.log('Monaco ' + version + ', ' + out.actions.length + ' editor actions');
check('the page finds Monaco\'s actions with their keys', out.actions.length > 100 && out.actions.some(a => a.id === 'editor.action.selectHighlights' && a.keys[0] === 'Ctrl+Shift+L'));
check('two-key chords are read', out.actions.some(a => a.id === 'editor.action.addCommentLine' && a.keys[0] === 'Ctrl+K Ctrl+C'));
check('secondary keys are read (Trigger Suggest: Ctrl+Space and Ctrl+I)', out.actions.some(a => a.id === 'editor.action.triggerSuggest'
    && a.keys.indexOf('Ctrl+Space') >= 0 && a.keys.indexOf('Ctrl+I') >= 0), (out.actions.find(a => a.id === 'editor.action.triggerSuggest') || {}).keys);
check('defaults before any override', out.before.selectHighlights === 'Ctrl+Shift+L', out.before);
check('an override moves the action in Monaco', out.afterOverride === 'Ctrl+Alt+Q', out.afterOverride);
check('... and frees its old key', out.oldKeyStillBound === false);
check('moving an action with two keys removes both', JSON.stringify(out.suggestKeys) === '["Ctrl+Alt+Space"]', out.suggestKeys);
check('clearing overrides restores Monaco\'s own keys', out.afterReset.selectHighlights === 'Ctrl+Shift+L' && out.afterReset.triggerSuggest === 'Ctrl+Space', out.afterReset);
check('a profile can move a Monaco action', out.profileMoved === 'Ctrl+Alt+G', out.profileMoved);
check('... and switching back restores it', out.profileBack === 'Ctrl+G', out.profileBack);
// ---------- the REAL page: boot monaco-embeditor.html itself and look at its Keyboard table ----------
// The page boots Monaco on load with no host, so a copy (plus the two scripts it loads) runs as-is. An injected
// error catcher goes first; a poller at the end waits for the editor, then opens the table's "All" view.
function runEdge(dir, file) {
    try {
        return execFileSync(edge, ['--headless=new', '--disable-gpu', '--virtual-time-budget=25000',
            '--user-data-dir=' + path.join(dir, 'profile'), '--dump-dom', 'file:///' + file.replace(/\\/g, '/')],
            { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore'] });
    } catch (e) { return null; }
}
const realDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ca-keymap-page-'));
let real = null;
try {
    ['clarion-language.js', 'clarion-formatter.js'].forEach(f => fs.copyFileSync(path.join(TERMINAL, f), path.join(realDir, f)));
    const catcher = '<script>window.__errs = []; window.addEventListener("error", function (e) { window.__errs.push(String(e.message)); });</script>';
    const poller = `<script>
(function () {
  var tries = 0;
  (function poll() {
    tries++;
    if ((typeof editorActionsLoaded === 'undefined' || !editorActionsLoaded) && tries < 200) return setTimeout(poll, 100);
    var r = { loaded: typeof editorActionsLoaded !== 'undefined' && editorActionsLoaded, errors: window.__errs };
    try {
      r.actions = EDITOR_ACTIONS.length;
      r.clarionRows = document.querySelectorAll('#keybindRows tr').length;
      document.querySelector('#kbFilters .kb-chip[data-filter="all"]').click();
      r.allRows = document.querySelectorAll('#keybindRows tr').length;
      var sh = document.querySelector('#keybindRows .kb-input[data-cmd="editor.action.selectHighlights"]');
      r.selectHighlightsNote = sh ? (sh.closest('tr').querySelector('.kb-note') || {}).textContent || '' : null;
      r.profiles = document.getElementById('kbProfile').options.length;
      r.pageActions = EDITOR_ACTIONS.filter(function (a) { return /^clarion|ca\\./i.test(a.id); }).map(function (a) { return a.id; });
    } catch (e) { r.error = String(e && e.stack || e); }
    // Visual Studio's two-key comment chords, typed into the live editor: keydowns dispatched at the editor's
    // textarea run through the page's own dispatcher AND Monaco's keybinding service, as real keys do.
    try {
      fileMode = true;                      // the whole buffer is editable, as in a plain source file
      setKeyProfile('vs');
      var ta = editor.getDomNode().querySelector('textarea');
      var key = function (code, kc, mods) {
        var e = new KeyboardEvent('keydown', { code: code, key: code.replace(/^(Key|Digit)/, '').toLowerCase(),
          ctrlKey: !mods || mods.ctrl !== false, shiftKey: !!(mods && mods.shift), bubbles: true, cancelable: true });
        Object.defineProperty(e, 'keyCode', { get: function () { return kc; } });
        Object.defineProperty(e, 'which', { get: function () { return kc; } });
        ta.dispatchEvent(e);
      };
      var NL = String.fromCharCode(10);
      var reset = function (text, ln) { editor.setValue(text); editor.setPosition({ lineNumber: ln, column: 1 }); editor.focus(); };
      reset(['one', 'two', 'three'].join(NL), 2);
      key('KeyK', 75); key('KeyC', 67);
      r.vsComment = editor.getValue().split(NL)[1];
      key('KeyA', 65);                      // Monaco must have left its chord mode: Ctrl+A selects all
      var s = editor.getSelection();
      r.selectAllAfter = [s.startLineNumber, s.startColumn, s.endLineNumber, s.endColumn].join(',');
      editor.setPosition({ lineNumber: 2, column: 1 });
      key('KeyK', 75); key('KeyU', 85);
      r.vsUncomment = editor.getValue().split(NL)[1];
      reset(['one', 'TWO', 'three'].join(NL), 2);
      editor.setSelection(new monaco.Selection(2, 1, 2, 4));
      key('KeyU', 85);                      // Ctrl+U alone: Lowercase
      r.vsLower = editor.getValue().split(NL)[1];
      reset(['one', 'two   ', 'three'].join(NL), 2);
      key('KeyK', 75); key('KeyX', 88);     // Ctrl+K Ctrl+X is Monaco's Trim Trailing Whitespace, not Clarion's Ctrl+X
      r.trimAfterK = JSON.stringify(editor.getValue().split(NL)[1]);
      setKeyProfile('clarion');
    } catch (e) { r.chordError = String(e && e.stack || e); }
    var pre = document.createElement('pre'); pre.id = 'probeOut'; pre.textContent = 'JSON:' + JSON.stringify(r) + ':END';
    document.body.appendChild(pre);
  })();
})();
</script>`;
    const pageHtml = html.replace('<head>', '<head>' + catcher).replace(/<\/body>(?![\s\S]*<\/body>)/, poller + '</body>');
    const realFile = path.join(realDir, 'monaco-embeditor.html');
    fs.writeFileSync(realFile, pageHtml);
    const dom2 = runEdge(realDir, realFile);
    const m2 = dom2 && /JSON:([\s\S]*?):END/.exec(dom2.slice(dom2.indexOf('id="probeOut"')));
    if (m2) real = JSON.parse(m2[1].replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"'));
} finally { try { fs.rmSync(realDir, { recursive: true, force: true }); } catch (e) { } }

console.log('\nThe real page');
check('monaco-embeditor.html boots and lists Monaco\'s actions', real && real.loaded && real.actions > 100, real);
if (real) {
    check('no script errors while booting', !real.error && (real.errors || []).length === 0, real.error || real.errors);
    check('the Keyboard table opens on the Clarion commands', real.clarionRows > 0 && real.clarionRows < 40, real.clarionRows);
    check('"All" adds the Monaco actions', real.allRows === real.clarionRows + real.actions, [real.clarionRows, real.actions, real.allRows]);
    check('Select All Occurrences shows that Lowercase takes its key', /Lowercase/.test(real.selectHighlightsNote || ''), real.selectHighlightsNote);
    check('the profile picker lists the profiles', real.profiles === 4, real.profiles);
    console.log('  (page actions found: ' + JSON.stringify(real.pageActions) + ')');
    console.log('\nVisual Studio two-key chords, typed into the real page');
    check('the chord run raised no error', !real.chordError, real.chordError);
    check('Ctrl+K Ctrl+C comments the line ONCE with the Clarion command (Monaco\'s own Add Line Comment removed)',
        real.vsComment === '!two', real.vsComment);
    check('... and Monaco left its chord mode: the next key (Ctrl+A) still selects all', real.selectAllAfter === '1,1,3,6', real.selectAllAfter);
    check('Ctrl+K Ctrl+U uncomments it', real.vsUncomment === 'two', real.vsUncomment);
    check('Ctrl+U alone is still Lowercase', real.vsLower === 'two', real.vsLower);
    check('Ctrl+K Ctrl+X reaches Monaco (Trim Trailing Whitespace), not Clarion\'s Ctrl+X', real.trimAfterK === '"two"', real.trimAfterK);
}

console.log('\n' + pass + ' passed, ' + fail + ' failed');
process.exit(fail ? 1 : 0);
