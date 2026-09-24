// schema-sources-postgres.test.js — zero-dependency guard for the two PostgreSQL ingest defects in GH #201.
//
// Run:  node Terminal/test/schema-sources-postgres.test.js
//
//   1. THE INDEX STATUS CELL. schema-sources.html's indexStatus handler used to write the literal
//      string 'error' and throw st.error away, so every ingest failure was undiagnosable from the UI.
//      The handler is EXTRACTED FROM THE PAGE at run time and driven against a stub document, so this
//      tests the shipped code, not a copy of it.
//   2. THE PROCEDURE-STAGE QUERY. pg_get_functiondef() raises 42809 on aggregates (prokind 'a') and
//      window functions ('w'), and one such row aborted the whole ingest. The query must keep its
//      prokind filter. Checked on the C# source text, since running it needs a PostgreSQL server.

var fs   = require('fs');
var path = require('path');

var pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  ✓ ' + name); }
    else { fail++; console.log('  ✗ ' + name + (detail ? '\n      ' + detail : '')); }
}

// ---- 1. indexStatus handler ----
console.log('\nschema-sources.html indexStatus handler:');
var page = fs.readFileSync(path.join(__dirname, '..', 'schema-sources.html'), 'utf8');
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
    ok('query restricts pg_proc to prokind IN (\'f\',\'p\')', /AND\s+p\.prokind\s+IN\s*\(\s*'f'\s*,\s*'p'\s*\)/.test(where),
       'aggregates/window functions would reach pg_get_functiondef()');
}

console.log('\n' + (fail === 0 ? 'ALL PASS (' + pass + ')' : fail + ' FAILED, ' + pass + ' passed'));
process.exit(fail === 0 ? 0 : 1);
