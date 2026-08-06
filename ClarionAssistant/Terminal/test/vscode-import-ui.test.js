// vscode-import-ui.test.js — guards the gear panel's "Import from VS Code…" UI.
//
// Run:  npm install jsdom                     (ONE TIME — see the dependency note below)
//       node Terminal/test/vscode-import-ui.test.js
//
// Unlike its neighbours here this test is NOT zero-dependency: it needs jsdom. The page code under
// test manipulates a real DOM (innerHTML-built tables, classList, click handlers), and hand-rolling
// a shim for that would mean hand-rolling an HTML parser. jsdom is dev-only — nothing ships with it.
//
// WHAT MAKES THIS WORTH THE DEPENDENCY: it does not copy the page's logic, it EXTRACTS it. The
// gearPaneEditor markup and the "Import from VS Code" JS section are sliced straight out of
// monaco-embeditor.html at run time and evaluated. So the test cannot drift from the page — if
// someone renames a control id or changes the response contract, this fails immediately. Monaco is
// never booted; requestFromHost / onSettingChanged / showToast are spied stubs.
//
// WHAT IT GUARDS, beyond the obvious rendering:
//   * the single-write-path contract — Import must call onSettingChanged() EXACTLY once and must NOT
//     dispatch per-control 'change' events (that would post one saveSettings per imported key)
//   * "cancelled" is never rendered as "not found", and a null (timed-out) reply is never rendered
//     as either — those three states look alike and mean very different things
//   * settings.json content is attacker-influenceable and must stay escaped in the preview
//   * the follow-IDE caveat is a full-width colspan row, NOT a span inside the label cell: as a cell
//     child its max-width became the label column's width demand and shoved the whole gear panel
//     from 377px to 489px
//
// Sibling: monaco-page-integrity.test.js catches syntax damage to the page as a whole.

const fs = require('fs');
let JSDOM;
try { ({ JSDOM } = require('jsdom')); }
catch (e) {
    console.error('This test needs jsdom, which is a dev-only dependency and is not installed.\n' +
                  '  Install it with:  npm install jsdom\n' +
                  '  (from this folder, or anywhere on the NODE_PATH)\n' +
                  'Skipping is NOT the same as passing — exiting non-zero so a runner cannot read this as green.');
    process.exit(2);
}

const HTML_PATH = process.argv[2] ||
    require('path').join(__dirname, '..', 'monaco-embeditor.html');   // default: the page next door
const html = fs.readFileSync(HTML_PATH, 'utf8');

// ---------- extract the real pieces ----------
function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b + endMarker.length);
}

const gearMarkup = slice(html,
    '<div class="gear-pane" id="gearPaneEditor">', '</div><!-- /gearPaneEditor -->', 'gear pane markup');

const vscJsFull = slice(html,
    '// ===================== Import from VS Code (gear panel > Editor) =====================',
    '    // ----- Keyboard rebinding table (gear panel) -----', 'VS Code import section');

const escHtmlJs = slice(html, '    function escHtml(s) {', '    }', 'escHtml');

if (!/vscImportBtn/.test(gearMarkup)) throw new Error('extracted markup has no import button');
if (!/function vscApply/.test(vscJsFull)) throw new Error('extracted JS has no vscApply');

// ---------- test scaffolding ----------
let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; failures.push(name + (detail ? ' — ' + detail : '')); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- build the page ----------
function makeEnv() {
    const dom = new JSDOM(
        '<!doctype html><html><body><div id="settingsPanel">' + gearMarkup + '</div><div id="toast"></div></body></html>',
        { runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;

    const spy = {
        requests: [],          // {action, payload, timeoutMs, resolve}
        settingChanged: 0,     // times onSettingChanged() ran  <-- the single-write-path assertion
        controlChangeEvents: 0,// 'change' events fired on gear controls (must stay 0 during import)
        toasts: []
    };

    // Count any 'change' event on the importable controls: vscWriteControl must NOT dispatch them.
    ['setTabSize', 'setInsertSpaces', 'setAutoIndent', 'setWordWrap', 'setMinimap', 'setCompleteOnInsertKey',
     'setFontSize', 'setFontFamily', 'setOccurrenceHighlight', 'setHorizontalScrollbar'].forEach(id => {
        const e = w.document.getElementById(id);
        if (e) e.addEventListener('change', () => { spy.controlChangeEvents++; });
    });

    const scope = {
        document: w.document,
        window: w,
        setTimeout: w.setTimeout.bind(w),
        Promise: Promise,
        onSettingChanged: function () { spy.settingChanged++; },
        showToast: function (m, ok) { spy.toasts.push({ m, ok }); },
        requestFromHost: function (action, payload, timeoutMs) {
            return new Promise(resolve => spy.requests.push({ action, payload, timeoutMs, resolve }));
        }
    };

    const src = escHtmlJs + '\n' + vscJsFull + '\n' +
        'return { vscRequest, vscRenderResult, vscApply, vscHide, vscBuildDiff, vscDisplay, ' +
        'VSC_IMPORT_MAP, VSC_READ_TIMEOUT_MS, VSC_BROWSE_TIMEOUT_MS };';
    const api = new Function(...Object.keys(scope), src)(...Object.values(scope));

    // Helpers over the live panel
    const $ = id => w.document.getElementById(id);
    const result = () => $('vscImportResult');
    const resultText = () => result().textContent;
    const buttons = () => Array.from(result().querySelectorAll('.vsc-actions button'));
    const clickBtn = label => {
        const b = buttons().find(x => x.textContent === label);
        if (!b) throw new Error('no button labelled "' + label + '" (have: ' + buttons().map(x => x.textContent).join(', ') + ')');
        b.click();
    };
    const seedPanel = () => {   // simulate a populated gear panel (post host-settings)
        $('setFollowIdeIndent').checked = false;
        $('setTabSize').value = '2';
        $('setInsertSpaces').checked = true;
        $('setAutoIndent').checked = true;
        $('setWordWrap').checked = false;
        $('setMinimap').checked = false;
        $('setCompleteOnInsertKey').checked = true;
        $('setFontSize').value = '13';
        $('setFontFamily').value = '';
        $('setOccurrenceHighlight').checked = true;
        $('setHorizontalScrollbar').value = 'auto';
    };
    // Resolve the outstanding host request and let the .then microtask run.
    const reply = async data => {
        const r = spy.requests[spy.requests.length - 1];
        if (!r) throw new Error('no pending host request to reply to');
        r.resolve(data);
        await new Promise(res => setImmediate(res));
    };

    return { w, api, spy, $, result, resultText, buttons, clickBtn, seedPanel, reply };
}

// ============================ tests ============================
(async function () {

section('Wiring / contract');
{
    const e = makeEnv();
    check('import button exists in the real gearPaneEditor markup', !!e.$('vscImportBtn'));
    check('result container exists and starts hidden',
        !!e.$('vscImportResult') && e.$('vscImportResult').classList.contains('hidden'));
    check('note states Smart Formatter / split orientation are not imported',
        /Smart Formatter/.test(e.$('vscImportBtn').parentNode.textContent) &&
        /split orientation/.test(e.$('vscImportBtn').parentNode.textContent));
    check('map covers exactly the 10 documented keys', e.api.VSC_IMPORT_MAP.length === 10,
        'got ' + e.api.VSC_IMPORT_MAP.length);
    const ids = e.api.VSC_IMPORT_MAP.map(m => m.id);
    check('every mapped control id exists in the panel',
        ids.every(id => !!e.$(id)), ids.filter(id => !e.$(id)).join(','));

    e.api.vscRequest(false);
    const r = e.spy.requests[0];
    check('plain read posts action readVsCodeSettings', r && r.action === 'readVsCodeSettings');
    check('plain read sends no browse flag', r && r.payload === null);
    check('plain read uses the short timeout', r && r.timeoutMs === e.api.VSC_READ_TIMEOUT_MS && r.timeoutMs === 8000);
    check('button disabled while the request is in flight', e.$('vscImportBtn').disabled === true);
}

section('State (a) — found, with changes');
{
    const e = makeEnv();
    e.seedPanel();
    e.api.vscRequest(false);
    await e.reply({
        found: true, path: 'C:\\Users\\dev\\AppData\\Roaming\\Code\\User\\settings.json',
        source: 'Code', error: '',
        values: { tabSize: 4, insertSpaces: true, fontSize: 15, fontFamily: 'Cascadia Code',
                  minimap: true, horizontalScrollbar: 'hidden' },
        skipped: [], cancelled: false
    });
    const txt = e.resultText();
    check('button re-enabled after reply', e.$('vscImportBtn').disabled === false);
    check('preview is visible', !e.result().classList.contains('hidden'));
    const rows = e.result().querySelectorAll('.vsc-rows tr');
    // insertSpaces already true in the panel → identical → excluded. 5 of the 6 differ.
    check('only differing settings are listed (5 of 6)', rows.length === 5, 'got ' + rows.length);
    check('identical value is not offered as a change', !/Insert spaces/.test(txt));
    check('header states the change count', /5 settings would change/.test(txt), txt.slice(0, 90));
    check('path is shown', /Code\\User\\settings\.json/.test(txt));
    check('numeric row reads current → imported', /13/.test(txt) && /15/.test(txt));
    check('empty font family renders as (default)', /\(default\)/.test(txt));
    check('select shows the dropdown wording, not the raw enum',
        /Show never/.test(txt) && !/hidden</.test(e.result().innerHTML.replace(/class="[^"]*"/g, '')));
    // Read the cells, not textContent: adjacent <td>s concatenate ("Minimap"+"off") and destroy \b boundaries.
    const minimapRow = Array.from(e.result().querySelectorAll('.vsc-rows tr'))
        .find(tr => /^Minimap/.test(tr.querySelector('.vl').textContent));
    check('boolean renders on/off',
        minimapRow && minimapRow.querySelector('.vfrom').textContent === 'off'
                   && minimapRow.querySelector('.vto').textContent === 'on',
        minimapRow ? JSON.stringify([minimapRow.querySelector('.vfrom').textContent,
                                     minimapRow.querySelector('.vto').textContent]) : 'no Minimap row');
    check('Import and Cancel are offered', e.buttons().map(b => b.textContent).join(',') === 'Import,Cancel');
    check('nothing written before Import is clicked',
        e.spy.settingChanged === 0 && e.$('setTabSize').value === '2');

    // ---- apply ----
    e.clickBtn('Import');
    check('APPLY: tabSize written', e.$('setTabSize').value === '4');
    check('APPLY: fontSize written', e.$('setFontSize').value === '15');
    check('APPLY: fontFamily written', e.$('setFontFamily').value === 'Cascadia Code');
    check('APPLY: checkbox written', e.$('setMinimap').checked === true);
    check('APPLY: select written', e.$('setHorizontalScrollbar').value === 'hidden');
    check('APPLY: onSettingChanged called EXACTLY once (single write path)',
        e.spy.settingChanged === 1, 'got ' + e.spy.settingChanged);
    check('APPLY: no per-control change events dispatched',
        e.spy.controlChangeEvents === 0, 'got ' + e.spy.controlChangeEvents);
    check('APPLY: no second saveSettings request posted directly',
        e.spy.requests.filter(r => r.action !== 'readVsCodeSettings').length === 0);
    check('APPLY: toast confirms the count', e.spy.toasts.length === 1 && /Imported 5 settings/.test(e.spy.toasts[0].m),
        JSON.stringify(e.spy.toasts));
    check('APPLY: preview closes', e.result().classList.contains('hidden'));

    // ---- cancel leaves everything alone ----
    const e2 = makeEnv();
    e2.seedPanel();
    e2.api.vscRequest(false);
    await e2.reply({ found: true, path: 'p', error: '', values: { tabSize: 8 }, skipped: [], cancelled: false });
    e2.clickBtn('Cancel');
    check('CANCEL: control untouched', e2.$('setTabSize').value === '2');
    check('CANCEL: no write', e2.spy.settingChanged === 0);
    check('CANCEL: preview closes', e2.result().classList.contains('hidden'));
}

section('State (b) — found, nothing to change');
{
    // b1: John's actual case — settings.json with zero editor.* keys
    const e = makeEnv();
    e.seedPanel();
    e.api.vscRequest(false);
    await e.reply({ found: true, path: 'C:\\x\\settings.json', error: '', values: {}, skipped: [], cancelled: false });
    check('empty values → "doesn\'t set any of the editor options"',
        /doesn't set any of the editor options/.test(e.resultText()), e.resultText().slice(0, 140));
    check('empty values → says nothing to change', /nothing to change/.test(e.resultText()));
    check('empty values → still shows which file was read', /settings\.json/.test(e.resultText()));
    check('empty values → only a Close button', e.buttons().map(b => b.textContent).join(',') === 'Close');
    e.clickBtn('Close');
    check('empty values → Close hides and writes nothing',
        e.result().classList.contains('hidden') && e.spy.settingChanged === 0);

    // b2: values present but identical to the panel
    const e2 = makeEnv();
    e2.seedPanel();
    e2.api.vscRequest(false);
    await e2.reply({ found: true, path: 'p', error: '',
        values: { tabSize: 2, insertSpaces: true, fontSize: 13, horizontalScrollbar: 'auto' },
        skipped: [], cancelled: false });
    check('identical values → "already matches your CA settings"',
        /already matches your CA settings/.test(e2.resultText()), e2.resultText().slice(0, 140));
    check('identical values → distinct wording from the empty case',
        !/doesn't set any of the editor options/.test(e2.resultText()));
    check('identical values → no diff table', e2.result().querySelectorAll('.vsc-rows tr').length === 0);
}

section('State (c) — not found → Browse…');
{
    const e = makeEnv();
    e.api.vscRequest(false);
    await e.reply({ found: false, path: '', source: '', error: '', values: {}, skipped: [], cancelled: false });
    check('not-found message names the three probed installs',
        /Code, Code - Insiders, VSCodium/.test(e.resultText()), e.resultText().slice(0, 160));
    check('not-found mentions portable / WSL', /portable or WSL/.test(e.resultText()));
    check('not-found offers Browse… and Cancel',
        e.buttons().map(b => b.textContent).join(',') === 'Browse…,Cancel');

    e.clickBtn('Browse…');
    const r = e.spy.requests[e.spy.requests.length - 1];
    check('Browse… re-requests with browse:true', r.action === 'readVsCodeSettings' && r.payload && r.payload.browse === true);
    check('Browse… uses the LONG timeout (modal dialog may sit for minutes)',
        r.timeoutMs === e.api.VSC_BROWSE_TIMEOUT_MS && r.timeoutMs === 300000, 'got ' + r.timeoutMs);
    check('Browse… shows a waiting state', /Waiting for you to pick/.test(e.resultText()));

    // Browse that succeeds feeds straight back into the preview
    e.seedPanel();
    await e.reply({ found: true, path: 'D:\\portable\\data\\user-data\\User\\settings.json', error: '',
                    values: { fontSize: 20 }, skipped: [], cancelled: false });
    check('Browse… result renders the normal preview',
        /1 setting would change/.test(e.resultText()) && /portable/.test(e.resultText()), e.resultText().slice(0, 120));
}

section('State (d) — Browse… cancelled → silent no-op');
{
    const e = makeEnv();
    e.seedPanel();
    e.api.vscRequest(false);
    await e.reply({ found: false, path: '', error: '', values: {}, skipped: [], cancelled: false });
    e.clickBtn('Browse…');
    await e.reply({ found: false, path: '', source: '', error: '', values: {}, skipped: [], cancelled: true });
    check('cancelled → preview closed', e.result().classList.contains('hidden'));
    check('cancelled → nothing rendered at all', e.resultText() === '');
    check('cancelled → no toast (silent)', e.spy.toasts.length === 0);
    check('cancelled → nothing written', e.spy.settingChanged === 0 && e.$('setTabSize').value === '2');
    check('cancelled → NOT reported as not-found', !/Couldn't find/.test(e.resultText()));
    check('cancelled → button re-enabled', e.$('vscImportBtn').disabled === false);
}

section('Error and timeout paths');
{
    const e = makeEnv();
    e.api.vscRequest(false);
    await e.reply({ found: true, path: 'C:\\bad\\settings.json',
                    error: 'Could not parse settings.json: bad token', values: {}, skipped: [], cancelled: false });
    check('parse error is surfaced verbatim', /Could not parse settings\.json: bad token/.test(e.resultText()));
    check('parse error styled as an error', !!e.result().querySelector('.vsc-msg.err'));
    check('parse error offers Browse… + Close',
        e.buttons().map(b => b.textContent).join(',') === 'Browse…,Close');

    const e2 = makeEnv();
    e2.api.vscRequest(false);
    await e2.reply(null);                       // requestFromHost timeout resolves null
    check('null reply → explicit timeout message', /timed out/.test(e2.resultText()), e2.resultText());
    check('null reply → not misreported as "not found"', !/Couldn't find/.test(e2.resultText()));
    check('null reply → button re-enabled so it can be retried', e2.$('vscImportBtn').disabled === false);
}

section('Skipped keys');
{
    const e = makeEnv();
    e.seedPanel();
    e.api.vscRequest(false);
    await e.reply({ found: true, path: 'p', error: '', values: { fontSize: 20 },
        skipped: [{ key: 'editor.wordWrap', reason: 'unrecognised value "smart"' },
                  { key: 'editor.tabSize', reason: 'not a number' }], cancelled: false });
    check('skipped block is labelled', /Not imported:/.test(e.resultText()));
    check('skipped key names shown', /editor\.wordWrap/.test(e.resultText()) && /editor\.tabSize/.test(e.resultText()));
    check('skipped reasons shown', /unrecognised value/.test(e.resultText()) && /not a number/.test(e.resultText()));

    const e2 = makeEnv();
    e2.seedPanel();
    e2.api.vscRequest(false);
    await e2.reply({ found: true, path: 'p', error: '', values: {},
                     skipped: [{ key: 'editor.fontSize', reason: 'out of range' }], cancelled: false });
    check('skipped also surfaces in the nothing-to-change state', /Not imported:/.test(e2.resultText()));
}

section('Follow-IDE caveat (GH #126)');
{
    const e = makeEnv();
    e.seedPanel();
    e.$('setFollowIdeIndent').checked = true;
    e.api.vscRequest(false);
    await e.reply({ found: true, path: 'p', error: '',
                    values: { tabSize: 4, insertSpaces: false, fontSize: 20 }, skipped: [], cancelled: false });
    const caveats = e.result().querySelectorAll('.vcaveat');
    check('follow-IDE on → caveat on both indentation rows', caveats.length === 2, 'got ' + caveats.length);
    check('caveat explains the IDE value wins', /the IDE’s value wins/.test(e.resultText()));
    // Each caveat is now its OWN full-width row (colspan=4) directly after the setting it annotates,
    // rather than a span inside the label cell — that layout shoved the panel from 377px to 489px.
    check('caveat spans the full table width', Array.from(caveats).every(c =>
        c.tagName === 'TD' && c.getAttribute('colspan') === '4'));
    check('caveat row directly follows the row it annotates', Array.from(caveats).every(c => {
        const prev = c.parentNode.previousElementSibling;
        return prev && /^(Tab size|Insert spaces)/.test(prev.querySelector('.vl').textContent);
    }));
    check('caveat is NOT attached to unrelated rows', Array.from(caveats).every(c =>
        !/Font size/.test(c.parentNode.previousElementSibling.querySelector('.vl').textContent)));
    check('follow-IDE rows are still importable (stored prefs) — 3 settings + 2 caveat rows',
        e.result().querySelectorAll('.vsc-rows tr').length === 5 &&
        e.result().querySelectorAll('.vsc-rows tr .vl').length === 3);
    e.clickBtn('Import');
    check('follow-IDE: disabled control still receives the imported value',
        e.$('setTabSize').value === '4' && e.$('setInsertSpaces').checked === false);

    const e2 = makeEnv();
    e2.seedPanel();                              // follow-IDE off
    e2.api.vscRequest(false);
    await e2.reply({ found: true, path: 'p', error: '', values: { tabSize: 4 }, skipped: [], cancelled: false });
    check('follow-IDE off → no caveat', e2.result().querySelectorAll('.vcaveat').length === 0);
}

section('Escaping (settings.json content is attacker-influenceable)');
{
    const e = makeEnv();
    e.seedPanel();
    e.api.vscRequest(false);
    await e.reply({ found: true, path: 'C:\\<img src=x onerror=alert(1)>\\settings.json',
        error: '', values: { fontFamily: '"><script>alert(2)<\/script>' },
        skipped: [{ key: '<b>k</b>', reason: '<i>r</i>' }], cancelled: false });
    const inner = e.result().innerHTML;
    check('malicious path is escaped, not injected',
        e.result().querySelectorAll('img').length === 0 && /&lt;img/.test(inner));
    check('malicious value is escaped', e.result().querySelectorAll('script').length === 0);
    check('malicious skipped entry is escaped',
        e.result().querySelectorAll('.vsc-skipped b').length === 1 &&      // only our own <b>Not imported:</b>
        /&lt;b&gt;k&lt;\/b&gt;/.test(inner));
    check('escaped text still reads correctly to a human', /alert\(1\)/.test(e.resultText()));
}

section('Stale-preview hygiene');
{
    const e = makeEnv();
    e.seedPanel();
    e.api.vscRequest(false);
    await e.reply({ found: true, path: 'p', error: '', values: { tabSize: 9 }, skipped: [], cancelled: false });
    check('preview open before a new request', !e.result().classList.contains('hidden'));
    e.api.vscRequest(false);                     // second click while a preview is up
    check('new request replaces the old preview with a reading state',
        /Reading VS Code settings/.test(e.resultText()) && e.result().querySelectorAll('.vsc-rows tr').length === 0);
    await e.reply({ found: true, path: 'p', error: '', values: {}, skipped: [], cancelled: false });
    check('stale rows cannot be imported after being replaced', e.spy.settingChanged === 0);
}

console.log('\n' + '='.repeat(60));
console.log(pass + ' passed, ' + fail + ' failed');
if (fail) { console.log('\nFailures:'); failures.forEach(f => console.log('  - ' + f)); }
process.exit(fail ? 1 : 0);

})().catch(e => { console.error('HARNESS ERROR: ' + e.stack); process.exit(2); });
