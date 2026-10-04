// header-tabs.test.js — guards the CA pane header's tabs (ticket 82938fc7).
//
// Run:  node Terminal/test/header-tabs.test.js [path\to\header.html]
//
// The header gained three tabs — Solution | Schema Sources (n) | Source Control — a copy button beside
// SOLUTION, a clickable RED line, and lost the LSP diagnostics toggle (◎) and a dead #tabBar. This loads
// the REAL header.html in jsdom (scripts run, the WebView2 bridge is a spy) and drives it the way the host
// and the user do. What it guards:
//   * tab switching: one active tab, the Solution pane hidden on the other two, a headerTab post per click
//   * the Schema Sources badge follows setSchemaCount (and stays hidden until a count arrives)
//   * the copy button and the RED link post INTENTS ONLY — no path ever leaves the page; the host uses its
//     own _currentSlnPath / RedFilePath (HeaderTabs.SourceScan.ps1 checks that side)
//   * RED is a link only when the host says it is openable AND it is not a warning; a warning never posts
//   * no ◎ / toggleDiagBar, no #tabBar / setTabs left behind
//
// Needs jsdom (dev-only, Terminal\test\node_modules; Run-Tests.ps1 installs it). Exit 2 = could not run.

const fs = require('fs');
const path = require('path');
let JSDOM;
try { ({ JSDOM } = require('jsdom')); }
catch (e) {
    console.error('This test needs jsdom, which is a dev-only dependency and is not installed.\n' +
                  '  Install it with:  npm install   (in Terminal\\test)\n' +
                  'Skipping is NOT the same as passing — exiting non-zero so a runner cannot read this as green.');
    process.exit(2);
}

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'header.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- load the page with a spy bridge ----------
const posted = [];
let hostListener = null;
const dom = new JSDOM(html, {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    beforeParse(window) {
        window.chrome = { webview: {
            postMessage: s => posted.push(JSON.parse(s)),
            addEventListener: (type, fn) => { if (type === 'message') hostListener = fn; },
        } };
    },
});
const { window } = dom;
const doc = window.document;
const $ = id => doc.getElementById(id);
function fromHost(msg) { hostListener({ data: JSON.stringify(msg) }); }
function lastPost() { return posted[posted.length - 1]; }
function tabBtn(name) { return doc.querySelector('#hdrTabs .hdr-tab[data-tab="' + name + '"]'); }
function activeTabs() {
    return Array.prototype.map.call(doc.querySelectorAll('#hdrTabs .hdr-tab.active'), b => b.getAttribute('data-tab'));
}

check('page registered a host message listener', typeof hostListener === 'function');

// ---------- tabs ----------
section('tabs');
const names = Array.prototype.map.call(doc.querySelectorAll('#hdrTabs .hdr-tab'), b => b.textContent.replace(/\d+/g, '').trim());
check('three tabs, in order: Solution | Schema Sources | Source Control',
      JSON.stringify(names) === JSON.stringify(['Solution', 'Schema Sources', 'Source Control']), JSON.stringify(names));
check('Solution is active at load', JSON.stringify(activeTabs()) === '["solution"]', JSON.stringify(activeTabs()));
check('Solution pane visible at load', $('paneSolution').hidden === false);

posted.length = 0;
tabBtn('schema').click();
check('Schema Sources click posts headerTab "schema"', posted.length === 1 && lastPost().action === 'headerTab' && lastPost().data === 'schema',
      JSON.stringify(posted));
check('Schema Sources is the only active tab', JSON.stringify(activeTabs()) === '["schema"]', JSON.stringify(activeTabs()));
check('Solution pane hidden on Schema Sources', $('paneSolution').hidden === true);

tabBtn('repo').click();
check('Source Control click posts headerTab "repo"', lastPost().action === 'headerTab' && lastPost().data === 'repo', JSON.stringify(lastPost()));
check('Source Control is the only active tab', JSON.stringify(activeTabs()) === '["repo"]', JSON.stringify(activeTabs()));
check('Solution pane hidden on Source Control', $('paneSolution').hidden === true);

tabBtn('solution').click();
check('Solution click posts headerTab "solution"', lastPost().action === 'headerTab' && lastPost().data === 'solution', JSON.stringify(lastPost()));
check('Solution pane shown again', $('paneSolution').hidden === false && JSON.stringify(activeTabs()) === '["solution"]');

posted.length = 0;
window.selectHeaderTab('bogus');
check('an unknown tab name posts nothing and changes nothing', posted.length === 0 && JSON.stringify(activeTabs()) === '["solution"]');

// ---------- badge ----------
section('Schema Sources badge');
const badge = $('schemaCount');
check('badge lives inside the Schema Sources tab', badge && tabBtn('schema').contains(badge));
check('badge hidden until the host sends a count', badge.hidden === true);
fromHost({ type: 'setSchemaCount', count: 3 });
check('setSchemaCount 3 shows "3"', badge.hidden === false && badge.textContent === '3', JSON.stringify(badge.textContent));
fromHost({ type: 'setSchemaCount', count: 0 });
check('setSchemaCount 0 shows "0" (a solution with nothing linked says so)', badge.hidden === false && badge.textContent === '0');
fromHost({ type: 'setSchemaCount', count: 'x' });
check('a non-number count hides the badge', badge.hidden === true);

// ---------- copy ----------
section('copy solution path');
fromHost({ type: 'setSolution', label: 'C:\\Apps\\School\\school.sln', path: 'C:\\Apps\\School\\school.sln', open: true });
const copyBtn = $('btnCopySln');
check('copy button sits in the SOLUTION row, beside the value',
      copyBtn && copyBtn.parentElement === $('solutionValue').parentElement);
posted.length = 0;
copyBtn.click();
check('click posts copySolutionPath', posted.length === 1 && posted[0].action === 'copySolutionPath', JSON.stringify(posted));
check('copySolutionPath carries NO data (the host copies its own path)', posted.length === 1 && posted[0].data === null,
      JSON.stringify(posted[0]));
check('page never calls navigator.clipboard', !/navigator\.clipboard\s*\.\s*\w+\s*\(/.test(html));
fromHost({ type: 'copyResult', ok: true });
check('copyResult ok shows ✓', copyBtn.classList.contains('copied') && copyBtn.textContent === '\u2713', copyBtn.textContent);
fromHost({ type: 'copyResult', ok: false });
check('copyResult failure shows ✗', copyBtn.classList.contains('copy-failed') && !copyBtn.classList.contains('copied') &&
      copyBtn.textContent === '\u2717', copyBtn.textContent);

// ---------- SOLUTION is read-only (d4e941e3) ----------
section('SOLUTION read-only');
const sv = $('solutionValue');
const SLN = 'C:\\Apps\\School\\school.sln';
check('no solution dropdown left', !$('solutionSelect') && !doc.querySelector('#paneSolution select'));
check('no browse ("...") button left', !/send\('browse'\)/.test(html));
check('the page never posts solutionChanged', !/solutionChanged/.test(html));
fromHost({ type: 'setSolution', label: SLN, path: SLN, open: true });
check('open in the IDE: shows the path, no note', sv.textContent === SLN && !sv.classList.contains('not-open'), JSON.stringify(sv.textContent));
check('the title carries the full path', sv.title.indexOf(SLN) === 0, sv.title);
fromHost({ type: 'setSolution', label: SLN, path: SLN, open: false });
check('not open in the IDE: path kept, note added, marked not-open',
      sv.querySelector('.sv-path').textContent === SLN && /not open in the IDE/.test(sv.querySelector('.sv-note').textContent) &&
      sv.classList.contains('not-open'), sv.textContent);
check('the note is its own non-shrinking span (a narrow pane cuts the path, not the note)',
      /#solutionValue \.sv-note\s*\{[^}]*flex-shrink:\s*0/.test(html));
fromHost({ type: 'setSolution', label: SLN, path: SLN, open: true });
check('back to open: the note goes away', !sv.querySelector('.sv-note') && !sv.classList.contains('not-open'));
fromHost({ type: 'setSolution', label: '', path: '', open: false });
check('no solution at all: says so, not marked not-open', sv.textContent === '(no solution open)' && !sv.classList.contains('not-open'),
      sv.textContent);
fromHost({ type: 'setSolution', label: '<b>x</b>.sln', path: '<b>x</b>.sln', open: false });
check('the path is text, never markup', !sv.querySelector('b') && sv.querySelector('.sv-path').textContent === '<b>x</b>.sln');
posted.length = 0;
sv.click();
check('clicking the field posts nothing', posted.length === 0, JSON.stringify(posted));

// ---------- label column (d4e941e3) ----------
section('label column');
const labels = Array.prototype.map.call(doc.querySelectorAll('#paneSolution .config-label'), l => l.textContent.trim());
check('all four labels share .config-label', JSON.stringify(labels) === JSON.stringify(['Solution', 'Version', 'RED', 'CodeGraph']),
      JSON.stringify(labels));
check('labels are left-aligned at a fixed width', /\.config-label\s*\{[^}]*width:\s*\d+px[^}]*text-align:\s*left/.test(html));
const outside = Array.prototype.map.call(doc.querySelectorAll('#paneSolution .outside-row .config-label'), l => l.textContent.trim());
check('RED and CodeGraph rows are inset to line up with the boxed rows', JSON.stringify(outside) === JSON.stringify(['RED', 'CodeGraph']),
      JSON.stringify(outside));
check('RED and CodeGraph label + value align on the text baseline',
      doc.querySelectorAll('#paneSolution .baseline-row').length === 2 && /\.baseline-row\s*\{[^}]*align-items:\s*baseline/.test(html));

// ---------- RED ----------
section('RED link');
const red = $('redFile');
const RED = 'C:\\Clarion12\\bin\\Clarion120.red';
fromHost({ type: 'setRedFile', text: RED, css: '', openable: true });
check('resolved RED shows the path', red.textContent === RED);
check('resolved RED is a link (red-link)', red.classList.contains('red-link'));
check('resolved RED is monospace + underlined by .red-link CSS',
      /\.red-value\.red-link\s*\{[^}]*text-decoration:\s*underline/.test(html) && /\.red-value\s*\{[^}]*font-family:\s*Consolas/.test(html));
posted.length = 0;
red.click();
check('click posts openRedFile', posted.length === 1 && posted[0].action === 'openRedFile', JSON.stringify(posted));
check('openRedFile carries NO path', posted.length === 1 && posted[0].data === null, JSON.stringify(posted[0]));

fromHost({ type: 'setRedFile', text: 'C:\\Clarion12\\bin\\Clarion120.red  (NOT FOUND)', css: 'warning', openable: true });
check('warning RED is not a link, even if flagged openable', !red.classList.contains('red-link') && red.classList.contains('warning'));
posted.length = 0;
red.click();
check('clicking a warning RED posts nothing', posted.length === 0, JSON.stringify(posted));

fromHost({ type: 'setRedFile', text: RED, css: '', openable: false });
posted.length = 0;
red.click();
check('RED the host did not flag openable posts nothing', posted.length === 0 && !red.classList.contains('red-link'), JSON.stringify(posted));

fromHost({ type: 'setRedFile', text: RED, css: '' });
check('openable defaults to false when the host omits it', !red.classList.contains('red-link'));

// ---------- removed ----------
section('removed');
check('no ◎ diagnostics toggle in the title icons', !/toggleDiagBar/.test(html) && !/&#9678;|\u25ce/.test(html));
check('no #tabBar element', $('tabBar') === null && !/id="tabBar"/.test(html));
check('no dead tab-bar JS (renderTabBar / setTabs / setActiveTab)',
      !/renderTabBar|highlightActiveTab|'setTabs'|'setActiveTab'/.test(html));

// ---------- nothing the page posts carries a path ----------
section('no path ever leaves the page');
// Replay every user-reachable post from the new controls, then inspect everything the page sent.
posted.length = 0;
['schema', 'repo', 'solution'].forEach(t => tabBtn(t).click());
copyBtn.click();
fromHost({ type: 'setRedFile', text: RED, css: '', openable: true });
red.click();
const leaky = posted.filter(p => typeof p.data === 'string' && /[\\/:]/.test(p.data));
check('posts were captured', posted.length === 5, String(posted.length));
check('no post from tabs / copy / RED has path-like data', leaky.length === 0, JSON.stringify(leaky));

console.log('\n' + (fail === 0 ? 'ALL PASS (' + pass + ')' : fail + ' FAILED, ' + pass + ' passed'));
process.exit(fail === 0 ? 0 : 1);
