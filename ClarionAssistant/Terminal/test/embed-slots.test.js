// embed-slots.test.js - 73bd1f03 fix (2): the page's half of the embed tools routed to the CA Embeditor.
//
// Run:  node Terminal/test/embed-slots.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. EXTRACTS the real host-request handlers (fc420c30's handleHostRequest block, which now carries
// 'getSlots') and the read-only guard's applyChangesToRanges from monaco-embeditor.html, and runs them against a
// fake Monaco model in EMBED mode. Pinned:
//   * getSlots: versionId, dirty, lineCount and the LIVE slot ranges; the buffer only when asked; copies, not the
//     page's own arrays; refused in file mode
//   * the round trip the C# side relies on: a slot write sent as applyEdits over the slot's whole lines (what
//     EmbedOverlayOps.WriteEmbedContent sends) is accepted inside the slot, and once the guard folds the change into
//     embedRanges the next getSlots reports the GROWN slot and the SHIFTED slots below it
// Against the pre-fix page this harness is RED ('getSlots' is an unknown action).

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

let HANDLERS, RANGES;
try {
    HANDLERS = slice(html, '    var hostSaveReqId = null;', '    // Unsaved edits stashed by the host', 'host request handlers');
    RANGES = slice(html, '    function applyChangesToRanges(changes) {', '\n    function installReadOnlyGuard', 'applyChangesToRanges');
} catch (e) {
    console.log('FAIL - ' + e.message);
    process.exit(1);
}

// ---------------------------------------------------------------------------------------------- fakes
function makeWorld(text, slots, opts) {
    opts = opts || {};
    const W = { posted: [], version: 1, lines: text.split('\r\n'), dirty: false, editSeq: 0, embedRanges: slots };
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
        getLineMaxColumn: l => W.lines[l - 1].length + 1,
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
        canUndo: () => false, canRedo: () => false
    };
    let applyChanges = null;
    const editor = {
        getModel: () => model,
        getPosition: () => ({ lineNumber: 1, column: 1 }),
        getSelection: () => null,
        setPosition() { }, setSelection() { },
        pushUndoStop() { },
        executeEdits(src, edits) {
            // Monaco reports changes with PRE-edit ranges and the inserted text, in descending order: what the
            // read-only guard's legal-edit path folds into embedRanges.
            const sorted = edits.slice().sort((a, b) => model.getOffsetAt(b.range.getStartPosition()) - model.getOffsetAt(a.range.getStartPosition()));
            const changes = sorted.map(e => ({ range: e.range, text: e.text.replace(/\r\n/g, '\n') }));
            let v = value();
            for (const e of sorted) {
                const a = model.getOffsetAt(e.range.getStartPosition());
                const b = model.getOffsetAt({ lineNumber: e.range.endLineNumber, column: e.range.endColumn });
                v = v.substring(0, a) + e.text + v.substring(b);
            }
            W.lines = v.split('\r\n'); W.version++;
            W.dirty = true;
            applyChanges(changes);   // the guard's legal-edit path
        },
        revealRangeInCenterIfOutsideViewport() { }, revealPositionInCenterIfOutsideViewport() { },
        getOption: () => false
    };
    const isEditableRange = r => W.embedRanges.some(s => r.startLineNumber >= s[0] && r.endLineNumber <= s[1]);
    const src = '(function(editor, monaco, activeEd, postToHost, revealLineFromHost, isEditableRange, doSave, fileMode, saveEnabled, W) {\n'
        + 'var isDirty, editSeq, embedRanges = W.embedRanges;\n'
        + 'function sync() { isDirty = W.dirty; editSeq = W.editSeq; embedRanges = W.embedRanges; }\n'
        + HANDLERS + '\n' + RANGES + '\n'
        + 'return { handle: function (m) { sync(); handleHostRequest(m); },\n'
        + '         applyChanges: function (c) { embedRanges = W.embedRanges; applyChangesToRanges(c); } };\n'
        + '})';
    const api = eval(src).call({}, editor, { Range: Range, editor: { EditorOption: { readOnly: 'readOnly' } } }, () => editor,
        m => W.posted.push(m), () => { }, isEditableRange, opts.doSave || (() => { }), !!opts.fileMode, true, W);
    applyChanges = api.applyChanges;
    return { W, model, api, last: () => W.posted[W.posted.length - 1] };
}

// An embed buffer: generated lines with two slots, [3,3] empty and [6,7] filled.
const SEED = ['P PROCEDURE', '  CODE', '', '  ! generated', '  ! more', '    x = 1', '    y = 2', '  RETURN'].join('\r\n');

console.log('\ngetSlots');
{
    const env = makeWorld(SEED, [[3, 3], [6, 7]]);
    env.api.handle({ type: 'hostRequest', reqId: 1, action: 'getSlots', args: {} });
    const r = env.last();
    check('replies hostReply ok with the reqId', r && r.action === 'hostReply' && r.reqId === 1 && r.ok === true, JSON.stringify(r));
    if (!r || !r.data) {
        console.log('\nFAIL - the page does not answer getSlots (pre-73bd1f03 fix 2); ' + pass + ' passed, ' + (fail) + ' failed');
        process.exit(1);
    }
    check('carries versionId, dirty, lineCount', r.data.versionId === 1 && r.data.dirty === false && r.data.lineCount === 8, JSON.stringify(r.data));
    check('carries the slot ranges', JSON.stringify(r.data.ranges) === '[[3,3],[6,7]]', JSON.stringify(r.data.ranges));
    check('no buffer unless asked', r.data.text === undefined);
    check('the ranges are copies (the reply cannot alias the live array)', r.data.ranges[0] !== env.W.embedRanges[0]);
    env.api.handle({ type: 'hostRequest', reqId: 2, action: 'getSlots', args: { withText: true } });
    check('withText -> the whole buffer', env.last().data.text === SEED);
}
{
    const env = makeWorld(SEED, [[3, 3]], { fileMode: true });
    env.api.handle({ type: 'hostRequest', reqId: 3, action: 'getSlots', args: {} });
    check('file mode (CA Editor) -> refused "notEmbed"', env.last().ok === false && env.last().error === 'notEmbed', JSON.stringify(env.last()));
}

console.log('\nslot write round trip (what EmbedOverlayOps.WriteEmbedContent sends)');
{
    const env = makeWorld(SEED, [[3, 3], [6, 7]]);
    // Fill the EMPTY slot at line 3 with two lines: replace line 3 (col 1 .. its end) with the new text.
    env.api.handle({ type: 'hostRequest', reqId: 4, action: 'applyEdits', args: {
        expectedVersionId: 1, caretAtEnd: false,
        edits: [{ startLine: 3, startCol: 1, endLine: 3, endCol: 1, text: '    a = 1\n    b = 2' }] } });
    check('accepted inside the slot', env.last().ok === true, JSON.stringify(env.last()));
    env.api.handle({ type: 'hostRequest', reqId: 5, action: 'getSlots', args: { withText: true } });
    const d = env.last().data;
    check('the written slot GREW to cover its new lines', d.ranges[0][0] === 3 && d.ranges[0][1] === 4, JSON.stringify(d.ranges));
    check('the slot below SHIFTED by the line delta', d.ranges[1][0] === 7 && d.ranges[1][1] === 8, JSON.stringify(d.ranges));
    check('the text holds the code at the slot', d.text.split('\r\n')[2] === '    a = 1' && d.text.split('\r\n')[3] === '    b = 2', JSON.stringify(d.text));
    check('the version moved (the C# side guards the next write with it)', d.versionId === 2);

    // Rewrite the (now shifted) filled slot [7,8] with one line.
    env.api.handle({ type: 'hostRequest', reqId: 6, action: 'applyEdits', args: {
        expectedVersionId: 2, edits: [{ startLine: 7, startCol: 1, endLine: 8, endCol: 10, text: '    z = 9' }] } });
    env.api.handle({ type: 'hostRequest', reqId: 7, action: 'getSlots', args: { withText: true } });
    const d2 = env.last().data;
    check('a shrinking rewrite shrinks the slot', JSON.stringify(d2.ranges) === '[[3,4],[7,7]]', JSON.stringify(d2.ranges));
    check('and replaces its lines', d2.text.split('\r\n')[6] === '    z = 9' && d2.text.split('\r\n')[7] === '  RETURN', JSON.stringify(d2.text));

    env.api.handle({ type: 'hostRequest', reqId: 8, action: 'applyEdits', args: {
        expectedVersionId: 3, edits: [{ startLine: 5, startCol: 1, endLine: 5, endCol: 1, text: 'X' }] } });
    check('a write onto a generated line is refused "notEditable"', env.last().ok === false && env.last().error === 'notEditable');
    env.api.handle({ type: 'hostRequest', reqId: 9, action: 'applyEdits', args: {
        expectedVersionId: 1, edits: [{ startLine: 3, startCol: 1, endLine: 3, endCol: 1, text: 'X' }] } });
    check('a write planned against an old version is refused "stale"', env.last().ok === false && env.last().error === 'stale');
}

console.log('\nrouted save: a page that fails BEFORE reaching the host still answers (1565ef7b edge)');
{
    // EmbedSaveWait (C#) waits for EmbedSaveFinished, which the host raises only once it hears of the save. If
    // doSave throws before posting to the host (e.g. in gatherSlots), no event will ever come, so the page's
    // reply to the 'save' host request must be the error, or the routed save waits out its whole budget.
    const env = makeWorld(SEED, [[3, 3], [6, 7]], { doSave: () => { throw new Error('gatherSlots blew up'); } });
    env.api.handle({ type: 'hostRequest', reqId: 40, action: 'save', args: {} });
    const r = env.last();
    check('doSave throws -> the save request is answered at once with an error',
        r && r.reqId === 40 && r.ok === false && /^exception: gatherSlots blew up/.test(r.error), JSON.stringify(r));
}

console.log('\n' + (fail === 0 ? 'PASS' : 'FAIL') + ' - ' + pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);
