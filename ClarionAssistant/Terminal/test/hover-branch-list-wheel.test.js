// hover-branch-list-wheel.test.js - the CASE quick-navigation list on a hover card scrolls with the mouse wheel.
//
// Run:  node Terminal/test/hover-branch-list-wheel.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL branchListWheel / branchListOf extracted from monaco-embeditor.html against
// fake DOM nodes. Pinned:
//   * a wheel over a branch list with more rows than it shows scrolls the list and is stopped (preventDefault
//     + stopPropagation), so Monaco never reads it as an editor scroll that closes the card
//   * pixels scale in proportion, 100px = 3 rows: a notch scrolls 3 rows, a display-scaled notch (150px) a
//     little more (4.5), never a whole extra step; a touchpad's small deltas glide; a line is a row; a page is
//     the rows on show (the list less its pinned header)
//   * the CSS caps the list at header + --ca-branch-rows rows, scrolls vertically only, pins the header
//   * it is stopped at either end too (the card stays open)
//   * untouched: a list that shows all its rows, a table that is not a branch list, Ctrl+wheel (editor zoom),
//     anything outside a hover table
//   * the page registers it on document, capture phase, not passive

const { check, section, finish, html } = require('./local-first-loader');

function extract(name) {
    const start = html.indexOf('    function ' + name + '(');
    if (start < 0) throw new Error(name + ' not found in the page');
    const end = html.indexOf('\n    }\n', start);
    return html.slice(start, end + 7);
}
const src = 'var BRANCH_LIST_ROWS_PER_NOTCH = ' + /var BRANCH_LIST_ROWS_PER_NOTCH = (\d+);/.exec(html)[1] + ';\n' +
    extract('branchListOf') + extract('branchListWheel') + '\nreturn { branchListWheel: branchListWheel };';
const { branchListWheel } = new Function(src)();

const ROW = 20;
function table(opts) {
    const t = {
        isBranchList: opts.branchList !== false,
        scrollHeight: opts.rows * ROW, clientHeight: opts.shown * ROW, scrollTop: opts.scrollTop || 0,
        querySelector(sel) {
            if (sel === 'a[title="Go to branch"]') return this.isBranchList ? {} : null;
            if (sel === 'tbody tr' || sel === 'thead tr') return { getBoundingClientRect: () => ({ height: ROW }) };
            return null;
        },
    };
    return t;
}
function targetIn(t) { return { closest: (sel) => (sel === '.monaco-hover table' ? t : null) }; }
function wheel(target, deltaY, extra) {
    const ev = Object.assign({ target, deltaY, deltaMode: 0, ctrlKey: false, prevented: false, stopped: false,
        preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; } }, extra || {});
    branchListWheel(ev);
    return ev;
}

section('a list with more rows than it shows');
{
    const t = table({ rows: 20, shown: 8 });
    const ev = wheel(targetIn(t), 100);
    check('one wheel notch down scrolls 3 rows', t.scrollTop === 3 * ROW, t.scrollTop);
    check('...and the event stops here (Monaco never sees it)', ev.prevented && ev.stopped);
    wheel(targetIn(t), -100);
    check('one notch up scrolls back 3 rows', t.scrollTop === 0, t.scrollTop);
    wheel(targetIn(t), 2, { deltaMode: 1 });
    check('line mode: 2 lines = 2 rows', t.scrollTop === 2 * ROW, t.scrollTop);
    wheel(targetIn(t), 10);
    check('a touchpad delta of 10px moves 0.3 rows', Math.abs(t.scrollTop - (2 * ROW + 0.3 * ROW)) < 1e-9, t.scrollTop);
    const scaled = table({ rows: 20, shown: 8 });
    wheel(targetIn(scaled), 150);
    check('a notch under 150% display scaling (150px) moves 4.5 rows, not 6', Math.abs(scaled.scrollTop - 4.5 * ROW) < 1e-9, scaled.scrollTop);
    const paged = table({ rows: 20, shown: 8 });
    wheel(targetIn(paged), 1, { deltaMode: 2 });
    check('a page moves the 7 rows on show (8 less the pinned header)', paged.scrollTop === 7 * ROW, paged.scrollTop);
    const top = wheel(targetIn(table({ rows: 20, shown: 8, scrollTop: 0 })), -100);
    check('stopped at the top too, so the card stays open', top.prevented && top.stopped);
}

section('left alone');
{
    const fits = wheel(targetIn(table({ rows: 5, shown: 5 })), 100);
    check('a list that shows all its rows', !fits.prevented && !fits.stopped);
    const other = wheel(targetIn(table({ rows: 20, shown: 8, branchList: false })), 100);
    check('a table that is not a branch list', !other.prevented && !other.stopped);
    const t = table({ rows: 20, shown: 8 });
    const zoom = wheel(targetIn(t), 100, { ctrlKey: true });
    check('Ctrl+wheel (editor zoom)', !zoom.prevented && t.scrollTop === 0);
    const outside = wheel({ closest: () => null }, 100);
    check('a wheel outside any hover table', !outside.prevented && !outside.stopped);
}

section('the CSS');
{
    // The body of the rule whose selector list is exactly `sel` (a selector followed by `{`).
    const rule = (sel) => {
        const at = html.indexOf(sel + ' {');
        return at < 0 ? '' : html.slice(html.indexOf('{', at) + 1, html.indexOf('}', at));
    };
    const list = rule('.monaco-editor .monaco-hover table:has(a[title="Go to branch"])');
    check('the list is capped at header + --ca-branch-rows rows', /max-height:\s*calc\(\(var\(--ca-branch-rows\) \+ 1\) \* var\(--ca-branch-row-h\)\)/.test(list), list);
    check('...7 rows', /--ca-branch-rows:\s*7;/.test(list), list);
    check('...scrolls vertically only', /overflow-x:\s*hidden/.test(list) && /overflow-y:\s*auto/.test(list), list);
    const head = rule('.monaco-editor .monaco-hover table:has(a[title="Go to branch"]) thead th');
    check('the header row is pinned', /position:\s*sticky/.test(head) && /top:\s*0/.test(head), head);
    const code = rule('.monaco-editor .monaco-hover table:has(a[title="Go to branch"]) code');
    check('a long branch line ends in an ellipsis', /text-overflow:\s*ellipsis/.test(code) && /max-width:\s*min\(72ch/.test(code), code);
}

section('registration');
check('on document, capture phase, not passive',
    /document\.addEventListener\('wheel', branchListWheel, \{ capture: true, passive: false \}\)/.test(html));

finish();
