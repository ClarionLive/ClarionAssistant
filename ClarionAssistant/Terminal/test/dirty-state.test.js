// dirty-state.test.js - fc420c30: "unsaved changes" means the text differs from the last load or save, so an undo back
// to the saved text reads CLEAN (the ● title mark, the host's mirror via fileState, is_modified, get_open_files).
//
// Run:  node Terminal/test/dirty-state.test.js [path-to-monaco-embeditor.html]
//
// The live defect (combined-1005b, PRM002130): Ctrl+Z removed the only edit, the text equalled the disk, and the ●,
// is_modified and get_open_files all still said modified. The page set dirty on ANY edit since the save.
//
// EXTRACTS the real renderTitle/setDirty/markSavedState/dirtyFromModel from monaco-embeditor.html and drives them with
// a fake model that follows Monaco's getAlternativeVersionId() rule: an edit gets a new id, an undo/redo gives back the
// id the restored state had. Also pins (source scan) that the content listener, the load and the save use the rule.
// Against the pre-fix page this harness is RED.

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8').replace(/\r\n/g, '\n');

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

function slice(text, a, b, what) {
    const i = text.indexOf(a);
    if (i < 0) throw new Error('could not find start of ' + what + ': ' + a);
    const j = text.indexOf(b, i + a.length);
    if (j < 0) throw new Error('could not find end of ' + what + ': ' + b);
    return text.slice(i, j);
}

let BLOCK;
try {
    BLOCK = slice(html, '    function renderTitle() {', '    // File mode only: mirror the live buffer', 'dirty helpers');
    if (BLOCK.indexOf('function dirtyFromModel') < 0) throw new Error('no dirtyFromModel (pre-fc420c30 dirty rule)');
} catch (e) {
    console.log('FAIL - ' + e.message);
    process.exit(1);
}

// Monaco's alternative version id: every state has an id; an edit creates a new state with a fresh id; undo/redo move
// between states and so give back the ids they had.
function makeModel() {
    let next = 1;
    const states = [1];   // the undo stack of state ids (top = current)
    const redo = [];
    return {
        getAlternativeVersionId: () => states[states.length - 1],
        edit() { next++; states.push(next); redo.length = 0; },
        undo() { if (states.length > 1) redo.push(states.pop()); },
        redo() { if (redo.length) states.push(redo.pop()); }
    };
}

function world() {
    const model = makeModel();
    const doc = { title: { textContent: '', title: '' } };
    const src = '(function (editor, document) {\n var isDirty = false; var baseTitle = "PRM002130.clw";\n' + BLOCK
        + '\n return { setDirty: setDirty, markSavedState: markSavedState, dirtyFromModel: dirtyFromModel,'
        + ' isDirty: function () { return isDirty; } };\n})';
    const api = eval(src)({ getModel: () => model }, { getElementById: () => doc.title });
    // The page's content listener, with the fix: setDirty(dirtyFromModel()) after each legal edit.
    const afterChange = () => api.setDirty(api.dirtyFromModel());
    return { model, api, doc, afterChange };
}

console.log('\nundo back to the loaded text');
{
    const w = world();
    w.api.markSavedState();                       // applySource: setValue then markSavedState
    w.model.edit(); w.afterChange();
    check('an edit makes it dirty (and the ● shows)', w.api.isDirty() && w.doc.title.textContent.startsWith('●'));
    w.model.undo(); w.afterChange();
    check('undo back to the loaded text reads CLEAN (and the ● goes)', !w.api.isDirty() && !w.doc.title.textContent.startsWith('●'));
    w.model.redo(); w.afterChange();
    check('redo makes it dirty again', w.api.isDirty());
}

console.log('\nsaves');
{
    const w = world();
    w.api.markSavedState();
    w.model.edit(); w.afterChange();
    w.api.setDirty(false); w.api.markSavedState();   // saveResult ok with a matching seq
    check('after a save the current text is clean', !w.api.isDirty() && !w.api.dirtyFromModel());
    w.model.undo(); w.afterChange();
    check('undo PAST the save (back to the old text) is dirty: it differs from what is on disk', w.api.isDirty());
    w.model.redo(); w.afterChange();
    check('redo back to the saved text is clean again', !w.api.isDirty());
    w.model.edit(); w.model.edit(); w.model.undo(); w.afterChange();
    check('two edits, one undo: still dirty', w.api.isDirty());
}

console.log('\nwiring (source scan)');
{
    const guard = slice(html, '    function installReadOnlyGuard() {', '    function ', 'installReadOnlyGuard');
    check('the content listener derives dirty from the model at both legal-edit sites',
        (guard.match(/setDirty\(dirtyFromModel\(\)\)/g) || []).length === 2 && guard.indexOf('setDirty(true)') < 0);
    const src = slice(html, '    function applySource(msg) {', '\n    }\n', 'applySource');
    check('a load marks the loaded text as the clean state (after setValue)', /model\.setValue\(text\);\s*\n\s*markSavedState\(\);/.test(src));
    check('a successful save marks the saved text as the clean state', /setDirty\(false\);\s*markSavedState\(\);/.test(html));
}

console.log('\n' + (fail === 0 ? 'PASS' : 'FAIL') + ' - ' + pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);
