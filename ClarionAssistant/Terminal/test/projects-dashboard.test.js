// projects-dashboard.test.js — guards the Dashboard / COM Controls / IDE Addins split (ticket d4e941e3).
//
// Run:  node Terminal/test/projects-dashboard.test.js [path\to\home.html] [path\to\projects.html]
//
// Developers read the old "Clarion COM and Addin Projects" table on the Dashboard as "make a project for each
// Clarion app". It moved to its own tab per kind. This loads the REAL home.html and projects.html in jsdom
// (scripts run, the WebView2 bridge is a spy) and checks:
//   * home.html: no project table / modal left; the COM and Addin cards post their open actions; counts
//     render as badges and 0 hides them; a dropdown change tells the host (backendChanged)
//   * projects.html: each kind lists its own type plus legacy "Other", never the other kind; a new project
//     always carries the page's type (there is no type picker); empty kind shows the hero, not the table;
//     nothing flashes before the host's first list; the explainer says no project is needed per app
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

const HOME_PATH = process.argv[2] || path.join(__dirname, '..', 'home.html');
const PROJECTS_PATH = process.argv[3] || path.join(__dirname, '..', 'projects.html');

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

function load(file, query) {
    const html = fs.readFileSync(file, 'utf8');
    const posted = [];
    let hostListener = null;
    const dom = new JSDOM(html, {
        runScripts: 'dangerously',
        pretendToBeVisual: true,
        url: 'file:///C:/ca/Terminal/' + path.basename(file) + (query || ''),
        beforeParse(window) {
            window.chrome = { webview: {
                postMessage: s => posted.push(JSON.parse(s)),
                addEventListener: (type, fn) => { if (type === 'message') hostListener = fn; },
            } };
        },
    });
    const doc = dom.window.document;
    return {
        html, posted, doc, window: dom.window,
        $: id => doc.getElementById(id),
        fromHost: msg => hostListener({ data: JSON.stringify(msg) }),
        shown: el => !!el && el.style.display !== 'none',
    };
}

const PROJECTS = [
    { id: 'c1', name: 'ClarionChartCOM', type: 'COM Control', folder: 'C:\\P\\Chart', lastAccessed: 3 },
    { id: 'o1', name: 'POSitiveAnywhereV2', type: 'Other', folder: 'C:\\P\\Pos', lastAccessed: 2 },
    { id: 'c2', name: 'DatePickerWebviewCOM', type: 'COM Control', folder: 'C:\\P\\Date', lastAccessed: 1 },
    { id: 'a1', name: 'OpenSourceButton', type: 'Addin', folder: 'C:\\P\\Btn', lastAccessed: 4 },
];

// ---------- home.html ----------
section('home.html');
{
    const h = load(HOME_PATH);
    check('home posts homeReady', h.posted.some(p => p.action === 'homeReady'));
    check('no projects table left on the Dashboard', !h.$('projectList') && !h.doc.querySelector('.projects-table'));
    check('no project modal left on the Dashboard', !h.$('projectModal'));
    check('no "Clarion COM and Addin Projects" section', !/Clarion COM and Addin Projects/.test(h.html));
    check('separator drawn above the Dashboard title',
          h.doc.body.firstElementChild && h.doc.body.firstElementChild.classList.contains('top-divider'));

    const cards = Array.prototype.map.call(h.doc.querySelectorAll('.action-card .action-label'), l => l.textContent.trim());
    check('six cards, in order', JSON.stringify(cards) === JSON.stringify(
        ['Work With Open Solution', 'New Chat', 'Evaluate Code', 'Create New Class', 'Clarion COM Control', 'Clarion IDE Addin']),
        JSON.stringify(cards));
    const groups = Array.prototype.map.call(h.doc.querySelectorAll('.quick-actions .group-label'), g => g.textContent.trim());
    check('two labelled rows', JSON.stringify(groups) === JSON.stringify(['Your Clarion apps', 'Build something new']), JSON.stringify(groups));
    check('Work With Open Solution says no setup is needed',
          /no setup needed/.test(h.doc.querySelector('.action-card .action-hint').textContent));

    function cardFor(label) {
        return Array.prototype.find.call(h.doc.querySelectorAll('.action-card'),
            c => c.querySelector('.action-label').textContent.trim() === label);
    }
    h.posted.length = 0;
    cardFor('Clarion COM Control').click();
    check('COM card posts openComProjects', h.posted.length === 1 && h.posted[0].action === 'openComProjects', JSON.stringify(h.posted));
    h.posted.length = 0;
    cardFor('Clarion IDE Addin').click();
    check('Addin card posts openAddinProjects', h.posted.length === 1 && h.posted[0].action === 'openAddinProjects', JSON.stringify(h.posted));
    check('COM / Addin cards carry no backend tag (they open a list, not a terminal)',
          !cardFor('Clarion COM Control').querySelector('[data-backend-tag]') && !cardFor('Clarion IDE Addin').querySelector('[data-backend-tag]'));

    h.fromHost({ type: 'setProjectCounts', com: 2, addin: 1 });
    check('counts render: 2 PROJECTS / 1 PROJECT',
          h.$('comCount').textContent === '2 PROJECTS' && h.$('addinCount').textContent === '1 PROJECT',
          h.$('comCount').textContent + ' / ' + h.$('addinCount').textContent);
    h.fromHost({ type: 'setProjectCounts', com: 0, addin: 0 });
    check('a zero count empties the badge', h.$('comCount').textContent === '' && h.$('addinCount').textContent === '');
    check('an empty badge is hidden by CSS', /\.proj-count:empty\s*\{[^}]*display:\s*none/.test(h.html));

    h.fromHost({ type: 'setBackend', backend: 'Claude' });
    h.posted.length = 0;
    const sel = h.$('backendSel');
    sel.value = 'Codex';
    sel.dispatchEvent(new h.window.Event('change'));
    check('a dropdown change posts backendChanged carrying the new backend',
          h.posted.length === 1 && h.posted[0].action === 'backendChanged' && h.posted[0].backend === 'Codex', JSON.stringify(h.posted));
}

// ---------- projects.html ----------
function names(p) {
    return Array.prototype.map.call(p.doc.querySelectorAll('#projectList .project-name'), n => n.textContent).sort();
}

section('projects.html — COM Controls');
{
    const p = load(PROJECTS_PATH, '?kind=com');
    check('title names the kind', p.$('pageTitle').textContent === 'Clarion COM Controls', p.$('pageTitle').textContent);
    check('explainer says no project is needed per app', /do not need a project here for each Clarion app/.test(p.$('explainText').textContent));
    check('nothing (table or hero) shows before the host sends the list',
          !p.shown(p.$('listWrap')) && !p.shown(p.$('emptyHero')));
    check('no type picker in the modal', !p.$('modalType') && !p.doc.querySelector('#projectModal select:not(#modalGitHubAccount)'));

    p.fromHost({ type: 'setProjects', items: PROJECTS });
    check('lists COM controls plus Other, never addins',
          JSON.stringify(names(p)) === JSON.stringify(['ClarionChartCOM', 'DatePickerWebviewCOM', 'POSitiveAnywhereV2']), JSON.stringify(names(p)));
    check('Other keeps its OTHER badge', !!p.doc.querySelector('.type-badge.other'));
    check('table shown, hero hidden', p.shown(p.$('listWrap')) && !p.shown(p.$('emptyHero')));

    p.$('newBtn').click();
    check('New opens the modal titled for the kind', p.$('projectModal').classList.contains('visible') &&
          p.$('modalTitle').textContent === 'New COM Control', p.$('modalTitle').textContent);
    p.$('modalName').value = 'GridCOM';
    p.$('modalFolder').value = 'C:\\P\\Grid';
    p.posted.length = 0;
    p.$('modalSaveBtn').click();
    const add = p.posted[0];
    check('a new project posts addProject with type "COM Control"',
          add && add.action === 'addProject' && JSON.parse(add.data).type === 'COM Control', JSON.stringify(add));

    p.window.editProject('o1');
    check('editing an Other project opens it as an edit', p.$('modalTitle').textContent === 'Edit Project' && p.$('modalName').value === 'POSitiveAnywhereV2');
    p.posted.length = 0;
    p.$('modalSaveBtn').click();
    check('saving an edit posts editProject with the id', p.posted[0] && p.posted[0].action === 'editProject' &&
          JSON.parse(p.posted[0].data).id === 'o1', JSON.stringify(p.posted[0]));

    p.$('projSearchInput').value = 'zzz';
    p.$('projSearchInput').dispatchEvent(new p.window.Event('input'));
    check('a search with no hits says so and keeps the table (not the empty hero)',
          p.shown(p.$('noMatch')) && p.shown(p.$('listWrap')) && !p.shown(p.$('emptyHero')));
}

section('projects.html — IDE Addins');
{
    const p = load(PROJECTS_PATH, '?kind=addin&theme=light');
    check('light theme from the query string', p.doc.body.classList.contains('light'));
    p.fromHost({ type: 'setProjects', items: PROJECTS });
    check('lists addins plus Other, never COM controls',
          JSON.stringify(names(p)) === JSON.stringify(['OpenSourceButton', 'POSitiveAnywhereV2']), JSON.stringify(names(p)));
    p.$('newBtn').click();
    p.$('modalName').value = 'MyPad';
    p.$('modalFolder').value = 'C:\\P\\Pad';
    p.posted.length = 0;
    p.$('modalSaveBtn').click();
    check('a new project posts addProject with type "Addin"',
          p.posted[0] && JSON.parse(p.posted[0].data).type === 'Addin', JSON.stringify(p.posted[0]));

    p.fromHost({ type: 'setProjects', items: PROJECTS.filter(x => x.type === 'COM Control') });
    check('no addins (and no Other): hero shown, table hidden, header button hidden',
          p.shown(p.$('emptyHero')) && !p.shown(p.$('listWrap')) && p.$('newBtn').style.visibility === 'hidden');
    check('hero button names the kind', p.$('emptyBtn').textContent === 'Create your first IDE addin', p.$('emptyBtn').textContent);
    p.$('emptyBtn').click();
    check('hero button opens the New IDE Addin modal', p.$('projectModal').classList.contains('visible') &&
          p.$('modalTitle').textContent === 'New IDE Addin');
}

section('projects.html — markup safety');
{
    const p = load(PROJECTS_PATH, '?kind=com');
    p.fromHost({ type: 'setProjects', items: [{ id: 'x', name: '<img src=x onerror=alert(1)>', type: 'COM Control', folder: 'C:\\x', lastAccessed: 1 }] });
    check('a project name is text, never markup', !p.doc.querySelector('#projectList img') &&
          p.doc.querySelector('#projectList .project-name').textContent === '<img src=x onerror=alert(1)>');
}

console.log('\n' + (fail === 0 ? 'ALL PASS (' + pass + ')' : fail + ' FAILED, ' + pass + ' passed'));
process.exit(fail === 0 ? 0 : 1);
