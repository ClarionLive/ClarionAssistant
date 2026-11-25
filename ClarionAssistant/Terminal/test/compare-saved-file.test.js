// compare-saved-file.test.js — the CA Editor's "Compare with saved file" toolbar button and the compare-only
// diff view it opens.
//
// Run:  node Terminal/test/compare-saved-file.test.js [monaco-embeditor.html] [monaco-diff.html]
//
// Zero-dependency (no jsdom), built like its neighbours: it EXTRACTS the button's wiring from
// monaco-embeditor.html and the bottom-bar logic from monaco-diff.html and runs them against a small fake DOM.
// What is pinned:
//   * the button is in the toolbar, hidden until setSource says file mode, and hidden again for an embeditor
//   * a click in file mode posts diffWithDisk with onDemand:true and the live buffer; outside file mode it does nothing
//   * the external-change toast's "Show diff" (postDiffWithDisk with no argument) still posts onDemand:false
//   * monaco-diff.html: compareOnly hides Approve and shows Close with its own hint; without it the show_diff
//     Approve/Cancel bar is unchanged; a file compare keeps its own bar whatever compareOnly says
//   * the hosts: MonacoDiffViewContent sends compareOnly (never for a file compare) and both CA Editor disk
//     diffs set it

const fs = require('fs');
const path = require('path');

const TERMINAL = path.join(__dirname, '..');
const EMB_PATH = process.argv[2] || path.join(TERMINAL, 'monaco-embeditor.html');
const DIFF_PATH = process.argv[3] || path.join(TERMINAL, 'monaco-diff.html');
const norm = s => s.replace(/\r\n/g, '\n');
const emb = norm(fs.readFileSync(EMB_PATH, 'utf8'));
const diffPage = norm(fs.readFileSync(DIFF_PATH, 'utf8'));
const diffCs = norm(fs.readFileSync(path.join(TERMINAL, 'MonacoDiffViewContent.cs'), 'utf8'));
const editorCs = norm(fs.readFileSync(path.join(TERMINAL, '..', 'MonacoClarionSourceEditor.cs'), 'utf8'));

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }
function grab(re, what) {
    const m = re.exec(emb) || re.exec(diffPage);
    if (!m) { check('found ' + what, false, 'pattern not in the page'); return null; }
    return m[0];
}

function makeElement() {
    const el = {
        style: {}, dataset: {}, textContent: '', _listeners: {},
        addEventListener(type, fn) { (this._listeners[type] = this._listeners[type] || []).push(fn); },
        click() { (this._listeners.click || []).forEach(fn => fn({})); }
    };
    el.classList = { toggle() { } };
    return el;
}

// ---------- toolbar markup ----------
section('Toolbar markup');
const tbMatch = /<div id="toolbar">[\s\S]*?\n    <\/div>/.exec(emb);
const btnTag = /<button id="diffBtn"[^>]*>/.exec(emb);
check('the button is in the toolbar', !!(tbMatch && tbMatch[0].indexOf('id="diffBtn"') >= 0));
check('it starts hidden (display:none until setSource)', !!(btnTag && /style="display:none"/.test(btnTag[0])));
check('it has a tooltip', !!(btnTag && /title="[^"]+"/.test(btnTag[0])));

// ---------- setSource shows it in file mode only ----------
section('setSource: file mode shows it, the embeditor hides it');
const showSrc = grab(/var dfb = document\.getElementById\('diffBtn'\);\n\s*if \(dfb\) dfb\.style\.display = [^\n]*/, 'setSource toggle');
if (showSrc) {
    for (const fileMode of [true, false]) {
        const btn = makeElement();
        btn.style.display = 'none';
        new Function('document', 'fileMode', showSrc)({ getElementById: id => id === 'diffBtn' ? btn : null }, fileMode);
        check((fileMode ? 'file mode: visible' : 'embeditor: hidden'), btn.style.display === (fileMode ? '' : 'none'),
            'display=' + JSON.stringify(btn.style.display));
    }
}

// ---------- the post ----------
section('postDiffWithDisk');
const postSrc = grab(/function postDiffWithDisk\(onDemand\) \{[\s\S]*?\n    \}/, 'postDiffWithDisk(onDemand)');
let posted = [];
let postDiff = null;
if (postSrc) {
    const model = { getValue: () => 'LIVE BUFFER' };
    postDiff = new Function('activeEd', 'postToHost', postSrc + '\nreturn postDiffWithDisk;')(
        () => ({ getModel: () => model }), m => posted.push(m));
    posted = []; postDiff(true);
    check('onDemand call posts diffWithDisk + onDemand:true + the live buffer',
        posted.length === 1 && posted[0].action === 'diffWithDisk' && posted[0].onDemand === true && posted[0].text === 'LIVE BUFFER',
        JSON.stringify(posted));
    posted = []; postDiff();
    check('the toast\'s "Show diff" (no argument) still posts onDemand:false',
        posted.length === 1 && posted[0].action === 'diffWithDisk' && posted[0].onDemand === false, JSON.stringify(posted));
}

// ---------- the click ----------
section('Button click');
const clickSrc = grab(/var diffBtn = document\.getElementById\('diffBtn'\);\n\s*if \(diffBtn\) diffBtn\.addEventListener\('click',[^\n]*/, 'click wiring');
if (clickSrc && postDiff) {
    for (const fileMode of [true, false]) {
        const btn = makeElement();
        posted = [];
        new Function('document', 'fileMode', 'postDiffWithDisk', clickSrc)(
            { getElementById: id => id === 'diffBtn' ? btn : null }, fileMode, postDiff);
        btn.click();
        if (fileMode) check('file mode: a click posts one on-demand compare', posted.length === 1 && posted[0].onDemand === true, JSON.stringify(posted));
        else check('outside file mode: a click posts nothing', posted.length === 0, JSON.stringify(posted));
    }
}

// ---------- monaco-diff.html bottom bar ----------
section('monaco-diff.html: compare-only bottom bar');
const barMatch = /var hint = document\.getElementById\('bottom-hint'\);[\s\S]*?(?=\n\s*\/\/ FULL paths)/.exec(diffPage);
if (!barMatch) check('found the bottom-bar block in applyFileCompareMode', false);
else {
    function newBar() {
        const els = { 'bottom-hint': makeElement(), filebar: makeElement(), 'btn-approve': makeElement(), 'btn-cancel': makeElement() };
        els['bottom-hint'].textContent = 'ORIGINAL HINT';   // stands in for the markup's show_diff hint
        return els;
    }
    function runBar(msg, els) {
        els = els || newBar();
        new Function('document', 'msg', 'fileCompareMode', barMatch[0])(
            { getElementById: id => els[id] || null }, msg, msg.fileCompare === true);
        return { approveShown: els['btn-approve'].style.display !== 'none', cancel: els['btn-cancel'].textContent,
                 hint: els['bottom-hint'].textContent, hintShown: els['bottom-hint'].style.display !== 'none' };
    }
    const co = runBar({ fileCompare: false, compareOnly: true });
    check('compareOnly: Approve hidden', !co.approveShown);
    check('compareOnly: the button reads Close', co.cancel === 'Close', co.cancel);
    check('compareOnly: hint says it is a read-only comparison, not "Approve to apply"',
        co.hintShown && /comparison/i.test(co.hint) && !/Approve/.test(co.hint), co.hint);
    check('compareOnly: hint names no left/right side (the Inline layout has none)', !/\b(left|right)\b/i.test(co.hint), co.hint);
    const sd = runBar({ fileCompare: false });
    check('show_diff (no compareOnly): Approve shown', sd.approveShown);
    check('show_diff: the button still reads Cancel', sd.cancel === 'Cancel', sd.cancel);
    check('show_diff: hint is the markup\'s own text, untouched', sd.hint === 'ORIGINAL HINT', sd.hint);
    const reused = newBar();
    runBar({ fileCompare: false, compareOnly: true }, reused);
    const back = runBar({ fileCompare: false }, reused);
    check('a normal diff after a compare-only one gets the markup hint back', back.hint === 'ORIGINAL HINT' && back.approveShown, back.hint);
    const markupHint = /<span class="hint" id="bottom-hint">([^<]*)<\/span>/.exec(diffPage);
    check('the markup hint is show_diff\'s Approve/Cancel text (the one copy)', !!(markupHint && /Approve/.test(markupHint[1])));
    const fc = runBar({ fileCompare: true, compareOnly: true });
    check('file compare: Approve hidden, Close, hint hidden (its own bar)', !fc.approveShown && fc.cancel === 'Close' && !fc.hintShown);
}

// ---------- hosts ----------
section('Hosts');
check('MonacoDiffViewContent has a CompareOnly property', /public bool CompareOnly \{ get; set; \}/.test(diffCs));
check('setDiff carries compareOnly, never for a file compare', /"\\"compareOnly\\":" \+ \(CompareOnly && _fileContext == null/.test(diffCs));
const showDisk = /private void ShowDiskDiff\(string rawJson\)[\s\S]*?\n        \}\n/.exec(editorCs);
const sd = showDisk ? showDisk[0] : '';
const newAt = sd.indexOf('new MonacoDiffViewContent(');
check('ShowDiskDiff marks its diff CompareOnly', /diff\.CompareOnly = true;/.test(sd));
check('ShowDiskDiff parses onDemand as a real bool (one parse, no string sniffing)',
    /data\["onDemand"\] is bool/.test(sd) && !/Contains\("\\"onDemand/.test(sd));
const closeAt = sd.indexOf('CloseDiffView(_onDemandDiff)');
check('on demand: the previous compare is closed before a new one is made', closeAt >= 0 && newAt > closeAt);
const sameAt = sd.search(/No unsaved changes/);
check('on demand: an unchanged file gives a toast and returns before any diff is made',
    sameAt >= 0 && newAt > sameAt && /No unsaved changes[^\n]*\n\s*return;/.test(sd));
check('on demand: the tab is titled "Saved vs. editor:"', /"Saved vs\. editor: "/.test(sd));
check('on demand: the new tab is remembered for the next click', /if \(onDemand\) _onDemandDiff = diff;/.test(sd));
const closeFn = /private static void CloseDiffView\(MonacoDiffViewContent diff\)[\s\S]*?\n        \}\n/.exec(editorCs);
check('CloseDiffView skips a tab the developer already closed', !!(closeFn && /diff\.IsDisposed\) return;/.test(closeFn[0])));
const disposeFn = /public override void Dispose\(\)\s*\{[^\n]*\n[^\n]*/.exec(diffCs);
check('MonacoDiffViewContent.Dispose sets IsDisposed first', !!(disposeFn && /IsDisposed = true;/.test(disposeFn[0])));

console.log('\n' + pass + ' passed, ' + fail + ' failed');
if (fail) { console.log('\nFailures:\n  ' + failures.join('\n  ')); process.exit(1); }
