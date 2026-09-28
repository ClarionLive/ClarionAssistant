// schema-sources-postgres.test.js — zero-dependency guard for the two PostgreSQL ingest defects in GH #201.
//
// Run:  node Terminal/test/schema-sources-postgres.test.js [path\to\schema-sources.html]
//
//   1. THE INDEX STATUS CELL. schema-sources.html's indexStatus handler used to write the literal
//      string 'error' and throw st.error away, so every ingest failure was undiagnosable from the UI.
//      The handler is EXTRACTED FROM THE PAGE at run time and driven against a stub document, so this
//      tests the shipped code, not a copy of it.
//   2. THE PROCEDURE-STAGE QUERY. pg_get_functiondef() raises 42809 on aggregates (prokind 'a'), and
//      one such row aborted the whole ingest. It does NOT raise on window functions ('w'), which it
//      renders with the WINDOW keyword (ruleutils.c), so those must keep being indexed. The query must
//      exclude exactly 'a'. Checked on the C# source text, since running it needs a PostgreSQL server.

var fs   = require('fs');
var path = require('path');

var pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  ✓ ' + name); }
    else { fail++; console.log('  ✗ ' + name + (detail ? '\n      ' + detail : '')); }
}

// ---- 1. indexStatus handler ----
console.log('\nschema-sources.html indexStatus handler:');
var page = fs.readFileSync(process.argv[2] || path.join(__dirname, '..', 'schema-sources.html'), 'utf8');
var startMark = "if (msg.type === 'indexStatus')";
var endMark   = "if (msg.type === 'testConnectionResult')";
var a = page.indexOf(startMark), b = page.indexOf(endMark);
ok('handler found in page', a >= 0 && b > a, 'start=' + a + ' end=' + b);

function run(msg) {
    var els = {};
    var document = { getElementById: function (id) {
        return els[id] || (els[id] = { id: id, className: '', textContent: '', title: '', disabled: true });
    } };
    var sources = [];
    function formatDate(x) { return x; }
    var logged = [];
    var console = { error: function () { logged.push(Array.prototype.join.call(arguments, ' ')); } };
    eval(page.substring(a, b));
    return { status: els['status-' + msg.sourceId], btn: els['idx-' + msg.sourceId], logged: logged };
}

if (a >= 0 && b > a) {
    var short = 'Error during PostgreSQL ingestion: 42809: "avg" is an aggregate function';
    var r = run({ type: 'indexStatus', sourceId: 's1', status: { error: short } });
    ok('error text is shown in the cell', r.status.textContent === short, 'got ' + JSON.stringify(r.status.textContent));
    ok('cell styled as error', r.status.className === 'status-text error', r.status.className);
    ok('full text in tooltip', r.status.title === short, JSON.stringify(r.status.title));
    ok('error logged to console', r.logged.length === 1, 'logged ' + r.logged.length);
    ok('Index button re-enabled', r.btn.disabled === false && r.btn.textContent === 'Index');

    var long = short + ' x'.repeat(100);
    r = run({ type: 'indexStatus', sourceId: 's2', status: { error: long } });
    ok('long error truncated in the cell', r.status.textContent.length === 121 && r.status.textContent.slice(-1) === '…',
       'length ' + r.status.textContent.length);
    ok('long error kept whole in the tooltip', r.status.title === long);

    r = run({ type: 'indexStatus', sourceId: 's3', status: { tableCount: 5, lastIndexed: 'd' } });
    ok('success path unchanged', r.status.textContent === '5 tables' && r.status.className === 'status-text indexed',
       JSON.stringify(r.status.textContent));
}

// ---- 2. procedure-stage query ----
console.log('\nSchemaGraphService.cs PostgreSQL procedure query:');
var cs = fs.readFileSync(path.join(__dirname, '..', '..', 'Services', 'SchemaGraphService.cs'), 'utf8');
var q = cs.indexOf('pg_get_functiondef(p.oid)');
ok('pg_get_functiondef query found', q >= 0);
if (q >= 0) {
    var orderBy = cs.indexOf('ORDER BY n.nspname, p.proname', q);
    var where = cs.substring(q, orderBy);
    ok('query excludes aggregates (prokind <> \'a\')', /AND\s+p\.prokind\s*<>\s*'a'/.test(where),
       'aggregates would reach pg_get_functiondef()');
    ok('query does not exclude window functions', !/prokind\s+(NOT\s+)?IN\s*\(/i.test(where) && !/'w'/.test(where),
       'window functions are valid pg_get_functiondef() input and must stay indexed');
}

// ---- 3. the page is the CA header's panes (82938fc7) ----
// Schema Sources / Source Control moved out of a collapsed bar in each chat tab into
// the CA header's tabs. The page keeps no collapse bar or tab strip of its own; the host's setMode picks
// the pane. setMode is EXTRACTED from the page and run against a stub document.
console.log('\nschema-sources.html as the header\'s panes:');
ok('no collapse bar / "Solution Settings" title', !/collapse-bar|Solution Settings|toggleCollapse/.test(page));
ok('no inner tab strip (switchTab / tab-btn)', !/switchTab|tab-btn/.test(page));
ok('no setCollapsed handler left', !/setCollapsed/.test(page));
ok('host setMode message is handled', /msg\.type === 'setMode'\)\s*\{\s*setMode\(msg\.mode\)/.test(page));
var fnMatch = /function setMode\(mode\) \{[\s\S]*?\r?\n\}/.exec(page);   // up to the first column-0 brace
ok('setMode found in page', !!fnMatch);
if (fnMatch) {
    var fnSrc = fnMatch[0];
    function runMode(calls) {
        var els = { paneSchema: { style: { display: '' } }, paneRepo: { style: { display: 'none' } } };
        var document = { getElementById: function (id) { return els[id]; } };
        var currentMode = 'schema';
        eval(fnSrc);
        calls.forEach(function (m) { setMode(m); });
        return { schema: els.paneSchema.style.display, repo: els.paneRepo.style.display };
    }
    var r2 = runMode(['repo']);
    ok('setMode("repo") shows only Source Control', r2.repo === '' && r2.schema === 'none', JSON.stringify(r2));
    r2 = runMode(['repo', 'schema']);
    ok('setMode("schema") shows only Schema Sources', r2.schema === '' && r2.repo === 'none', JSON.stringify(r2));
    r2 = runMode(['repo', 'bogus']);
    ok('an unknown mode changes nothing', r2.repo === '' && r2.schema === 'none', JSON.stringify(r2));
}
ok('the pane scrolls rather than growing (.content is 100vh, overflow-y auto)',
   /\.content\s*\{[^}]*height:\s*100vh[^}]*overflow-y:\s*auto/.test(page));
ok('Manage Sources modal still tells the host to grow / restore', /send\('modalOpened'\)/.test(page) && /send\('modalClosed'\)/.test(page));

console.log('\n' + (fail === 0 ? 'ALL PASS (' + pass + ')' : fail + ' FAILED, ' + pass + ' passed'));
process.exit(fail === 0 ? 0 : 1);
