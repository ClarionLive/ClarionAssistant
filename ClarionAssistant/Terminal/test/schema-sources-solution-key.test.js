// schema-sources-solution-key.test.js - guards against cross-solution writes from the Schema Sources /
// Source Control panel (ticket 82938fc7, pipeline run 1 P1).
//
// Run:  node Terminal/test/schema-sources-solution-key.test.js [path\to\schema-sources.html]
//
// THE DEFECT. The panel is solution-level and the IDE can switch solutions while it is in use (the header
// dropdown, or the IDE opening another solution). The Manage Sources modal and the Source Control fields
// were drawn for solution A; a Select or a pending onblur after the switch was applied to solution B:
// A's checked sources were linked into B and B's others UNLINKED, and A's repo link was saved onto B.
//
// THE FIX, page side (this test). The host stamps every setGlobalSources and setSolutionRepo with the
// solution it was drawn for (`sln`); the page echoes it on every write: applySourceSelection -> {sln, ids},
// setSolutionRepo -> {sln, accountId, repoName}. The host refuses a write whose sln is not the current
// solution (HeaderTabs.SourceScan.ps1 H9 checks that side), and on a switch re-sends both views, stamped
// with the new solution. So:
//   * a Select echoes the solution the checkboxes were drawn for
//   * a setGlobalSources for B arriving while the modal is open redraws the checkboxes AND re-keys Select to B
//   * a repo save echoes the solution the fields were drawn for, and re-keys when the host re-sends them
//   * a view the host never stamped sends sln null (which the host refuses) rather than a guess
//
// Needs jsdom (dev-only, Terminal\test\node_modules; Run-Tests.ps1 installs it). Exit 2 = could not run.

const fs = require('fs');
const path = require('path');
let JSDOM;
try { ({ JSDOM } = require('jsdom')); }
catch (e) {
    console.error('This test needs jsdom, which is a dev-only dependency and is not installed.\n' +
                  '  Install it with:  npm install   (in Terminal\\test)\n' +
                  'Skipping is NOT the same as passing - exiting non-zero so a runner cannot read this as green.');
    process.exit(2);
}

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'schema-sources.html');
const html = fs.readFileSync(HTML_PATH, 'utf8');

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

function load() {
    const posted = [];
    let hostListener = null;
    const dom = new JSDOM(html, {
        runScripts: 'dangerously',
        beforeParse(window) {
            window.chrome = { webview: {
                postMessage: s => posted.push(JSON.parse(s)),
                addEventListener: (type, fn) => { if (type === 'message') hostListener = fn; },
            } };
        },
    });
    const w = dom.window;
    return {
        w, doc: w.document, posted,
        fromHost: msg => hostListener({ data: JSON.stringify(msg) }),
        last: action => posted.filter(p => p.action === action).pop(),
    };
}
function payload(post) { try { return JSON.parse(post.data); } catch (e) { return null; } }
function checkedIds(doc) {
    return Array.prototype.filter.call(doc.querySelectorAll('#globalBody input[type=checkbox]'), c => c.checked)
        .map(c => c.getAttribute('data-id'));
}

const A = 'C:\\Apps\\School\\school.sln';
const B = 'D:\\Work\\Pos\\pos.sln';
const items = [{ id: 's1', name: 'School dict', type: 'dctx' }, { id: 's2', name: 'POS db', type: 'mssql' }];

// ---------- modal ----------
section('Manage Sources modal');
{
    const t = load();
    t.w.openManageModal();
    t.fromHost({ type: 'setGlobalSources', sln: A, items: items, linkedIds: ['s1'] });
    t.w.applySelection();
    let p = payload(t.last('applySourceSelection'));
    check('Select posts {sln, ids}', p && typeof p === 'object' && Array.isArray(p.ids), JSON.stringify(p));
    check('Select echoes the solution the modal was drawn for (A)', p && p.sln === A, p && JSON.stringify(p.sln));
    check('Select sends the checked ids', p && JSON.stringify(p.ids) === '["s1"]', p && JSON.stringify(p.ids));

    // The solution switches while the modal is open: the host redraws it for B.
    t.w.openManageModal();
    t.fromHost({ type: 'setGlobalSources', sln: A, items: items, linkedIds: ['s1'] });
    t.doc.querySelector('#globalBody input[data-id="s2"]').checked = true;      // the user's edit, for A
    t.fromHost({ type: 'setGlobalSources', sln: B, items: items, linkedIds: ['s2'] });
    check('the redraw shows B\'s links, not A\'s', JSON.stringify(checkedIds(t.doc)) === '["s2"]', JSON.stringify(checkedIds(t.doc)));
    t.w.applySelection();
    p = payload(t.last('applySourceSelection'));
    check('after the redraw, Select is keyed to B', p && p.sln === B, p && JSON.stringify(p.sln));
    check('after the redraw, Select sends B\'s checkboxes', p && JSON.stringify(p.ids) === '["s2"]', p && JSON.stringify(p.ids));
}
{
    const t = load();
    t.w.openManageModal();
    t.fromHost({ type: 'setGlobalSources', items: items, linkedIds: [] });   // an unstamped (older) host message
    t.w.applySelection();
    const p = payload(t.last('applySourceSelection'));
    check('an unstamped modal sends sln null (the host refuses it), never a guess', p && p.sln === null, p && JSON.stringify(p.sln));
}

// ---------- Source Control ----------
section('Source Control fields');
{
    const t = load();
    t.fromHost({ type: 'setRepoAccounts', accounts: [{ id: 'acc1', displayName: 'Me', username: 'me', provider: 'github' }] });
    t.fromHost({ type: 'setSolutionRepo', sln: A, accountId: 'acc1', repoName: 'school' });
    const name = t.doc.getElementById('repoName');
    name.value = 'school-v2';
    t.w.onRepoChanged();
    let p = payload(t.last('setSolutionRepo'));
    check('a repo save posts {sln, accountId, repoName}', p && p.accountId === 'acc1' && p.repoName === 'school-v2', JSON.stringify(p));
    check('a repo save echoes the solution the fields were drawn for (A)', p && p.sln === A, p && JSON.stringify(p.sln));

    // The host re-sends the fields for B on a switch; a blur that lands afterwards is keyed to B, with B's values.
    name.value = 'typed-for-A';
    t.fromHost({ type: 'setSolutionRepo', sln: B, accountId: '', repoName: '' });
    check('the re-send replaces the in-flight edit', name.value === '', JSON.stringify(name.value));
    t.w.onRepoChanged();
    p = payload(t.last('setSolutionRepo'));
    check('a save after the re-send is keyed to B', p && p.sln === B, p && JSON.stringify(p.sln));
    check('and carries B\'s values, not the discarded edit', p && p.repoName === '' && p.accountId === '', JSON.stringify(p));
}
{
    const t = load();
    t.w.onRepoChanged();
    const p = payload(t.last('setSolutionRepo'));
    check('fields the host never stamped send sln null', p && p.sln === null, p && JSON.stringify(p.sln));
}

console.log('\n' + (fail === 0 ? 'ALL PASS (' + pass + ')' : fail + ' FAILED, ' + pass + ' passed'));
process.exit(fail === 0 ? 0 : 1);
