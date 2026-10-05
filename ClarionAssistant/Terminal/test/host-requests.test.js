// host-requests.test.js - fc420c30: the page's half of the MCP editor tools routed to the CA Editor.
//
// Run:  node Terminal/test/host-requests.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. EXTRACTS the real host-request handlers (handleHostRequest and its helpers) and the saveResult
// branch's reply from monaco-embeditor.html and runs them against a fake Monaco model/editor that records what the
// host is sent. Pinned:
//   * getState: versionId, dirty, seq, cursor, selection (+ its text), lineCount; the buffer only when asked
//   * applyEdits: ONE executeEdits batch between undo stops (= one undo step), through the same content listener as
//     typing (setDirty + editSeq++ + fileState to the host); line breaks normalized to the model's EOL; refuses
//     "stale" when the version moved, "readOnly", and "notEditable" outside an embed slot; caretAtEnd moves the caret
//   * setSelection / reveal / undo / redo, and done:false when there is nothing to undo
//   * save: runs the existing doSave and answers only when the host's saveResult arrives
//   * unknown action and no model are refused, not thrown
// Against the pre-fix page this harness is RED (the handlers do not exist).

const fs = require('fs');
const path = require('path');

const TERMINAL = path.join(__dirname, '..');
const HTML_PATH = process.argv[2] || path.join(TERMINAL, 'monaco-embeditor.html');
const html = fs.readFileSync(HTML_PATH, 'utf8').replace(/\r\n/g, '\n');

function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a + startMarker.length);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

let HANDLERS, SAVE_BRANCH;
try {
    HANDLERS = slice(html, '    var hostSaveReqId = null;', '    // Unsaved edits stashed by the host', 'host request handlers');
    SAVE_BRANCH = slice(html, '                if (hostSaveReqId != null) {', '\n                }\n', 'saveResult reply') + '\n                }\n';
} catch (e) {
    console.log('FAIL - ' + e.message + ' (the page has no host-request handlers: pre-fc420c30)');
    process.exit(1);
}

// ---------------------------------------------------------------------------------------------- fakes
function makeWorld(text, opts) {
    opts = opts || {};
    const W = { posted: [], batches: 0, undoStops: 0, version: 1, lines: text.split('\r\n'), dirty: false, editSeq: 0,
                fileStates: 0, readOnly: !!opts.readOnly, saves: 0, undo: [], redo: [] };
    function Range(sl, sc, el, ec) {
        this.startLineNumber = sl; this.startColumn = sc; this.endLineNumber = el; this.endColumn = ec;
    }
    Range.prototype.getStartPosition = function () { return { lineNumber: this.startLineNumber, column: this.startColumn }; };
    Range.prototype.isEmpty = function () { return this.startLineNumber === this.endLineNumber && this.startColumn === this.endColumn; };
    const value = () => W.lines.join('\r\n');
    const model = {
        getVersionId: () => W.version,
        getEOL: () => '\r\n',
        getLineCount: () => W.lines.length,
        getValue: () => value(),
        validateRange(r) {
            const sl = Math.max(1, Math.min(r.startLineNumber, W.lines.length));
            const el = Math.max(1, Math.min(r.endLineNumber, W.lines.length));
            const sc = Math.max(1, Math.min(r.startColumn, W.lines[sl - 1].length + 1));
            const ec = Math.max(1, Math.min(r.endColumn, W.lines[el - 1].length + 1));
            return new Range(sl, sc, el, ec);
        },
        getOffsetAt(p) { let o = 0; for (let i = 0; i < p.lineNumber - 1; i++) o += W.lines[i].length + 2; return o + p.column - 1; },
        getPositionAt(off) {
            const v = value(); let line = 1, start = 0;
            for (let i = 0; i < off; i++) if (v[i] === '\n') { line++; start = i + 1; }
            return { lineNumber: line, column: off - start + 1 };
        },
        getValueInRange(r) { const v = value(); return v.substring(this.getOffsetAt(r.getStartPosition()), this.getOffsetAt({ lineNumber: r.endLineNumber, column: r.endColumn })); },
        canUndo: () => W.undo.length > 0,
        canRedo: () => W.redo.length > 0
    };
    W.model = model;
    W.pos = { lineNumber: 2, column: 1 };
    W.sel = new Range(2, 1, 2, 1);
    function contentListener() {   // the read-only guard's legal-edit path, as for typing
        W.dirty = true; W.editSeq++; W.fileStates++;
    }
    const editor = {
        getModel: () => (opts.noModel ? null : model),
        getPosition: () => W.pos,
        getSelection: () => W.sel,
        setPosition(p) { W.pos = p; },
        setSelection(r) { W.sel = r; },
        pushUndoStop() { W.undoStops++; },
        executeEdits(src, edits) {
            W.batches++; W.lastSource = src;
            W.undo.push(value()); W.redo = [];
            let v = value();
            const sorted = edits.slice().sort((a, b) => model.getOffsetAt(b.range.getStartPosition()) - model.getOffsetAt(a.range.getStartPosition()));
            for (const e of sorted) {
                const a = model.getOffsetAt(e.range.getStartPosition());
                const b = model.getOffsetAt({ lineNumber: e.range.endLineNumber, column: e.range.endColumn });
                v = v.substring(0, a) + e.text + v.substring(b);
            }
            W.lines = v.split('\r\n'); W.version++;
            contentListener();
        },
        trigger(src, cmd) {
            if (cmd === 'undo' && W.undo.length) { W.redo.push(value()); W.lines = W.undo.pop().split('\r\n'); W.version++; contentListener(); }
            if (cmd === 'redo' && W.redo.length) { W.undo.push(value()); W.lines = W.redo.pop().split('\r\n'); W.version++; contentListener(); }
        },
        revealRangeInCenterIfOutsideViewport() { },
        revealPositionInCenterIfOutsideViewport() { },
        getOption: () => W.readOnly
    };
    const env = {
        editor, model, W,
        monaco: { Range, editor: { EditorOption: { readOnly: 'readOnly' } } },
        activeEd: () => editor,
        postToHost: m => W.posted.push(m),
        revealLineFromHost: m => { W.pos = { lineNumber: m.line, column: m.column || 1 }; W.revealed = m.line; },
        isEditableRange: r => !opts.slots || opts.slots.some(s => r.startLineNumber >= s[0] && r.endLineNumber <= s[1]),
        doSave: () => { if (opts.throwOnSave) throw new Error('save failed'); W.saves++; },
        fileMode: opts.fileMode !== false,
        saveEnabled: opts.saveEnabled !== false
    };
    // Bind the page's free variables (isDirty / editSeq are read by hostState).
    const src = '(function(editor, monaco, activeEd, postToHost, revealLineFromHost, isEditableRange, doSave, fileMode, saveEnabled, W) {\n'
        + 'Object.defineProperty(this, "isDirty", { get: function () { return W.dirty; } });\n'
        + 'var isDirty, editSeq;\n'
        + 'function sync() { isDirty = W.dirty; editSeq = W.editSeq; }\n'
        + HANDLERS
        + '\nreturn { handle: function (m) { sync(); handleHostRequest(m); },\n'
        + '         saveResult: function (msg) { sync();\n' + SAVE_BRANCH + ' },\n'
        + '         pendingSave: function () { return hostSaveReqId; } };\n'
        + '})';
    const api = eval(src).call({}, editor, env.monaco, env.activeEd, env.postToHost, env.revealLineFromHost, env.isEditableRange,
                               env.doSave, env.fileMode, env.saveEnabled, W);
    env.api = api;
    env.last = () => W.posted[W.posted.length - 1];
    return env;
}

const SEED = "  MEMBER('app')\r\nP PROCEDURE\r\n  CODE\r\n  x = 1";

console.log('\ngetState');
{
    const env = makeWorld(SEED);
    env.api.handle({ type: 'hostRequest', reqId: 1, action: 'getState', args: {} });
    const r = env.last();
    check('replies hostReply with the reqId', r.action === 'hostReply' && r.reqId === 1 && r.ok === true);
    check('state carries versionId, dirty, seq, cursor, lineCount', r.data.versionId === 1 && r.data.dirty === false && r.data.seq === 0
        && r.data.cursor.line === 2 && r.data.lineCount === 4, JSON.stringify(r.data));
    check('no buffer unless asked', r.data.text === undefined);
    env.api.handle({ type: 'hostRequest', reqId: 2, action: 'getState', args: { withText: true } });
    check('withText → the whole buffer', env.last().data.text === SEED);
}

console.log('\napplyEdits');
{
    const env = makeWorld(SEED);
    env.api.handle({ type: 'hostRequest', reqId: 3, action: 'applyEdits', args: {
        expectedVersionId: 1, caretAtEnd: true,
        edits: [{ startLine: 4, startCol: 3, endLine: 4, endCol: 3, text: 'IF x =\nEND\n  ' }] } });
    const r = env.last();
    check('ok, with the new state', r.ok === true && r.data.versionId === 2, JSON.stringify(r));
    check('ONE executeEdits batch between undo stops (one undo step)', env.W.batches === 1 && env.W.undoStops === 2);
    check('through the typing path: dirty, editSeq++, fileState to the host', env.W.dirty && env.W.editSeq === 1 && env.W.fileStates === 1);
    check('line breaks normalized to the model EOL (CRLF)', env.model.getValue() === "  MEMBER('app')\r\nP PROCEDURE\r\n  CODE\r\n  IF x =\r\nEND\r\n  x = 1",
        JSON.stringify(env.model.getValue()));
    check('caretAtEnd → the caret after the inserted text', env.W.pos.lineNumber === 6 && env.W.pos.column === 3, JSON.stringify(env.W.pos));

    env.api.handle({ type: 'hostRequest', reqId: 4, action: 'applyEdits', args: { expectedVersionId: 1, edits: [{ startLine: 1, startCol: 1, endLine: 1, endCol: 1, text: 'X' }] } });
    check('a moved version → refused "stale", nothing applied', env.last().ok === false && env.last().error === 'stale' && env.W.batches === 1);
}
{
    const env = makeWorld(SEED, { readOnly: true });
    env.api.handle({ type: 'hostRequest', reqId: 5, action: 'applyEdits', args: { expectedVersionId: 1, edits: [{ startLine: 1, startCol: 1, endLine: 1, endCol: 1, text: 'X' }] } });
    check('a read-only editor → refused "readOnly"', env.last().ok === false && env.last().error === 'readOnly' && env.W.batches === 0);
}
{
    const env = makeWorld(SEED, { fileMode: false, slots: [[3, 3]] });
    env.api.handle({ type: 'hostRequest', reqId: 6, action: 'applyEdits', args: { expectedVersionId: 1, edits: [{ startLine: 1, startCol: 1, endLine: 1, endCol: 1, text: 'X' }] } });
    check('embed mode, outside a slot → refused "notEditable"', env.last().ok === false && env.last().error === 'notEditable' && env.W.batches === 0);
    env.api.handle({ type: 'hostRequest', reqId: 7, action: 'applyEdits', args: { expectedVersionId: 1, edits: [{ startLine: 3, startCol: 3, endLine: 3, endCol: 3, text: 'X' }] } });
    check('embed mode, inside a slot → applied', env.last().ok === true && env.W.batches === 1);
}

console.log('\nselection, reveal, undo/redo');
{
    const env = makeWorld(SEED);
    env.api.handle({ type: 'hostRequest', reqId: 8, action: 'setSelection', args: { startLine: 4, startCol: 3, endLine: 4, endCol: 99 } });
    env.api.handle({ type: 'hostRequest', reqId: 9, action: 'getState', args: {} });
    check('setSelection clamps and selects; getState returns its text', env.last().data.selection.text === 'x = 1' && env.last().data.selection.endCol === 8, JSON.stringify(env.last().data.selection));
    env.api.handle({ type: 'hostRequest', reqId: 10, action: 'reveal', args: { line: 3 } });
    check('reveal moves the caret to the line', env.W.revealed === 3 && env.last().ok === true);
    env.api.handle({ type: 'hostRequest', reqId: 11, action: 'undo', args: {} });
    check('undo with nothing to undo → done:false', env.last().ok === true && env.last().data.done === false);
    env.api.handle({ type: 'hostRequest', reqId: 12, action: 'applyEdits', args: { expectedVersionId: 1, edits: [{ startLine: 1, startCol: 1, endLine: 1, endCol: 1, text: '!' }] } });
    env.api.handle({ type: 'hostRequest', reqId: 13, action: 'undo', args: {} });
    check('undo after an edit → done:true, text restored', env.last().data.done === true && env.model.getValue() === SEED);
    env.api.handle({ type: 'hostRequest', reqId: 14, action: 'redo', args: {} });
    check('redo → done:true, edit back', env.last().data.done === true && env.model.getValue().startsWith('!'));
}

console.log('\nsave');
{
    const env = makeWorld(SEED);
    env.api.handle({ type: 'hostRequest', reqId: 20, action: 'save', args: {} });
    check('save runs the existing doSave and does NOT answer yet', env.W.saves === 1 && env.W.posted.length === 0 && env.api.pendingSave() === 20);
    env.api.saveResult({ type: 'saveResult', ok: true, message: 'Saved', savedSeq: 0 });
    check('the host saveResult answers it: saved:true', env.last().reqId === 20 && env.last().ok === true && env.last().data.saved === true
        && env.api.pendingSave() === null, JSON.stringify(env.last()));
}
{
    const env = makeWorld(SEED, { saveEnabled: false });
    env.api.handle({ type: 'hostRequest', reqId: 21, action: 'save', args: {} });
    check('a tab that cannot save → refused "saveDisabled"', env.last().ok === false && env.last().error === 'saveDisabled' && env.W.saves === 0);
}

{
    // EmbedSave's review: a doSave that throws is answered by the catch, and must not leave the request pending for
    // the NEXT saveResult (the developer's own Ctrl+S) to answer a second time.
    const env = makeWorld(SEED, { throwOnSave: true });
    env.api.handle({ type: 'hostRequest', reqId: 22, action: 'save', args: {} });
    check('a doSave that throws → answered with an exception error', env.last().reqId === 22 && env.last().ok === false && /exception/.test(env.last().error));
    check('...and the request is no longer pending', env.api.pendingSave() === null);
    const before = env.W.posted.length;
    env.api.saveResult({ type: 'saveResult', ok: true, message: 'Saved', savedSeq: 0 });
    check('...so a later saveResult (the developer\'s Ctrl+S) answers nothing', env.W.posted.length === before);
}

console.log('\nrefusals');
{
    const env = makeWorld(SEED);
    env.api.handle({ type: 'hostRequest', reqId: 30, action: 'format', args: {} });
    check('an unknown action is refused', env.last().ok === false && /unknownAction/.test(env.last().error));
}
{
    const env = makeWorld(SEED, { noModel: true });
    env.api.handle({ type: 'hostRequest', reqId: 31, action: 'getState', args: {} });
    check('no model yet → refused "notReady"', env.last().ok === false && env.last().error === 'notReady');
}

console.log('\n' + (fail === 0 ? 'PASS' : 'FAIL') + ' - ' + pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);
