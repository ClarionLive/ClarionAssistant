// monaco-page-integrity.test.js — zero-dependency structural guard for the Monaco WebView2 pages.
//
// Run:  node Terminal/test/monaco-page-integrity.test.js
//
// WHY THIS EXISTS. monaco-embeditor.html is a ~420 KB single file carrying a ~343 KB inline script.
// Two failure modes have bitten this repo, and BOTH are invisible in a diff and survive a clean
// MSBuild — the page is a content file, so the compiler never looks at it. The first symptom is a
// blank or half-dead editor at runtime, on a developer's machine, after a deploy:
//
//   1. SYNTAX DAMAGE. A large search/replace edit that lands slightly wrong (a swallowed brace, a
//      mangled comment marker) breaks the whole script block. Everything below the damage silently
//      stops existing — including the editor bootstrap.
//   2. NUL BYTES. Certain large-file edit paths have injected literal 0x00 bytes into these pages.
//      They are invisible in most editors and in `git diff`, and they poison the script block.
//
// This test costs milliseconds and needs nothing installed, so run it after ANY edit to these pages.
// It deliberately checks the file on DISK rather than a build output — the build just copies it.

var fs   = require('fs');
var path = require('path');

var PAGES = ['monaco-embeditor.html', 'monaco-diff.html'];

var pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  ✓ ' + name); }
    else { fail++; console.log('  ✗ ' + name + (detail ? '\n      ' + detail : '')); }
}

PAGES.forEach(function (page) {
    var file = path.join(__dirname, '..', page);
    console.log('\n' + page + ':');

    if (!fs.existsSync(file)) {
        // Not a failure — monaco-diff.html may legitimately not exist in every branch. Say so loudly
        // enough that a MISSING page is never mistaken for a CLEAN page.
        console.log('  - not present, skipped');
        return;
    }

    // ---- NUL bytes (checked on raw bytes, before any string decoding can hide them) ----
    var buf = fs.readFileSync(file);
    var nul = 0, firstAt = -1;
    for (var i = 0; i < buf.length; i++) {
        if (buf[i] === 0) { nul++; if (firstAt < 0) firstAt = i; }
    }
    ok('no NUL bytes (' + buf.length + ' bytes scanned)', nul === 0,
       nul + ' NUL byte(s), first at offset ' + firstAt);

    // ---- every inline <script> block parses ----
    var html = buf.toString('utf8');
    var re = /<script\b([^>]*)>([\s\S]*?)<\/script>/gi, m, blocks = 0;
    while ((m = re.exec(html)) !== null) {
        if (/\bsrc\s*=/i.test(m[1])) continue;            // external script, nothing inline to parse
        var code = m[2];
        if (!code.trim()) continue;
        blocks++;
        var line = html.slice(0, m.index).split('\n').length;
        var err = null;
        // new Function() parses without executing — exactly the check we want, since executing the
        // page's bootstrap outside a browser would throw for reasons that are not syntax errors.
        try { new Function(code); } catch (e) { err = e.message; }
        ok('inline script block #' + blocks + ' (line ' + line + ', ' + code.length + ' chars) parses',
           err === null, err);
    }
    ok('found at least one inline script block', blocks > 0, 'found ' + blocks);
});

console.log('\n' + (fail === 0 ? 'ALL PASS (' + pass + ')' : fail + ' FAILED, ' + pass + ' passed'));
process.exit(fail === 0 ? 0 : 1);
