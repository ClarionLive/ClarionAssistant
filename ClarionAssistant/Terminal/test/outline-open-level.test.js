// outline-open-level.test.js — guards the Document structure pane's "open at level n" and
// "sort that level" settings.
//
// Run:  node Terminal/test/outline-open-level.test.js
//
// Zero-dependency (no jsdom): the Document structure section is sliced out of monaco-embeditor.html at
// run time and evaluated against a tiny fake DOM, so the real renderOutline / openOutline /
// sortSymbolsLevel are what is driven. What it guards:
//   * level n opens with the nodes at depth >= n-1 collapsed (Class > Methods > method names = level 3)
//   * level 0 ("All") collapses nothing -- the historical behaviour
//   * the level is applied ONCE per open: a later refresh keeps the user's own twisty toggles, and a
//     filtered render (force-expanded) does not consume the pending level
//   * every open starts from a clean slate (nothing is remembered between opens)
//   * "sort that level" sorts ONLY the siblings at depth n-1, within each parent; other levels keep
//     source order
//   * the settings round-trip: the panel controls exist, are listened to, read back into the payload
//     and applied, and the C# host persists and ships both keys

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html');
const CS_PATH = path.join(__dirname, '..', '..', 'Services', 'ModernEmbeditorSettings.cs');
const html = fs.readFileSync(HTML_PATH, 'utf8');
const cs = fs.readFileSync(CS_PATH, 'utf8');

let pass = 0, fail = 0;
function check(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}
function section(t) { console.log('\n' + t); }

// ---------- extract the real code ----------
function slice(text, startMarker, endMarker, what) {
    const a = text.indexOf(startMarker);
    if (a < 0) throw new Error('could not find start of ' + what + ': ' + startMarker);
    const b = text.indexOf(endMarker, a);
    if (b < 0) throw new Error('could not find end of ' + what + ': ' + endMarker);
    return text.slice(a, b);
}
let outlineSrc;
try {
    outlineSrc = slice(html, '    var outlineSeq = 0;', '    // Header result count for the Find-All column', 'Document structure section');
} catch (e) { console.log('  FAIL  ' + e.message); process.exit(1); }

// ---------- a tiny fake DOM (just what the outline code touches) ----------
function makeEl(tag) {
    const el = {
        tag, children: [], style: {}, _cls: new Set(), textContent: '', _html: '', listeners: {},
        classList: {
            add: c => el._cls.add(c), remove: c => el._cls.delete(c),
            contains: c => el._cls.has(c),
            toggle: (c, force) => { const on = force === undefined ? !el._cls.has(c) : !!force; if (on) el._cls.add(c); else el._cls.delete(c); return on; },
        },
        appendChild: c => { el.children.push(c); return c; },
        addEventListener: (t, fn) => { (el.listeners[t] = el.listeners[t] || []).push(fn); },
        set className(v) { el._cls = new Set(String(v).split(/\s+/).filter(Boolean)); },
        get className() { return Array.from(el._cls).join(' '); },
        set innerHTML(v) { el._html = v; el.children = []; },
        get innerHTML() { return el._html; },
    };
    return el;
}
const byId = { outlineList: makeEl('div'), outlineCount: makeEl('span'), outlinePanel: makeEl('div') };
byId.outlinePanel._cls.add('hidden');
const fakeDocument = { getElementById: id => byId[id] || null, createElement: makeEl };

function loadOutline() {
    byId.outlineList.children = [];
    const stubs = { requestFromHost: () => new Promise(() => { }), whenIdleSynced: () => new Promise(() => { }),
        setEditorInsets() { }, closeFindPanel() { }, recordNavAt() { }, editor: null };
    const names = Object.keys(stubs);
    // typeof-guarded so that against a page WITHOUT the feature the checks FAIL instead of the loader throwing.
    const fn = n => "(typeof " + n + " === 'function' ? " + n + " : function (x) { return x; })";
    const body = outlineSrc + ';\nreturn { api: { openOutline: ' + fn('openOutline') + ', renderOutline: ' + fn('renderOutline') +
        ', renderOutlineCurrent: ' + fn('renderOutlineCurrent') + ', sortSymbolsLevel: ' + fn('sortSymbolsLevel') + ' },' +
        ' get: function () { return { collapsed: outlineCollapsed, pending: (typeof outlinePendingInit === "undefined" ? undefined : outlinePendingInit) }; },' +
        ' set: function (k, v) { if (k === "level") outlineLevelPref = v; else if (k === "sortLevel") outlineSortLevelPref = v;' +
        ' else if (k === "symbols") outlineSymbols = v; } };';
    return new Function('document', ...names, body)(fakeDocument, ...names.map(n => stubs[n]));
}

// Class > Methods > methods > locals; plus a second class, so "within each parent" is visible.
function tree() {
    const m = (n, kids) => ({ name: n, kind: 6, line: 1, children: kids || [] });
    const local = n => ({ name: n, kind: 13, line: 1, children: [] });
    return [
        { name: 'ZClass', kind: 5, line: 1, children: [
            { name: 'Methods', kind: 6, line: 1, children: [m('Zeta', [local('loc1')]), m('Alpha', [local('loc2')]), m('Mid')] } ] },
        { name: 'AClass', kind: 5, line: 1, children: [
            { name: 'Methods', kind: 6, line: 1, children: [m('Yak'), m('Bee')] } ] },
    ];
}
// collapsed container nodes in the rendered DOM, by name
function collapsedNames(listEl) {
    const out = [];
    (function walk(nodes) {
        for (const n of nodes) {
            if (n._cls && n._cls.has('ol-node') && n._cls.has('collapsed')) {
                out.push(n.children[0].children.filter(c => c._cls.has('ol-name'))[0].textContent);
            }
            if (n.children) walk(n.children);
        }
    })(listEl.children);
    return out.sort();
}

// ---------- sortSymbolsLevel ----------
section('sort that level');
{
    const o = loadOutline();
    const names = ns => ns.map(n => n.name);
    const t = tree();
    const s = o.api.sortSymbolsLevel(t, 0, 2);   // level 3 -> target depth 2 (the method names)
    check('roots keep source order', names(s).join() === 'ZClass,AClass');
    check('Methods group stays in place', s[0].children[0].name === 'Methods');
    check('method names sorted within each parent',
        names(s[0].children[0].children).join() === 'Alpha,Mid,Zeta' && names(s[1].children[0].children).join() === 'Bee,Yak');
    check('does not sort across parents (ZClass methods stay under ZClass)', names(s[0].children[0].children).indexOf('Yak') < 0);
    check('input tree is not mutated', names(t[0].children[0].children).join() === 'Zeta,Alpha,Mid');
    const r = o.api.sortSymbolsLevel(tree(), 0, 0);   // level 1 -> the roots
    check('level 1 sorts the roots only', names(r).join() === 'AClass,ZClass' && names(r[1].children[0].children).join() === 'Zeta,Alpha,Mid');
}

// ---------- open at level n ----------
section('open at level n');
function collapsedAfterOpen(level, extra) {
    const o = loadOutline();
    o.set('level', level);
    o.api.openOutline();                       // resets state, arms the level (its refresh never answers here)
    const t = tree();
    o.set('symbols', t);
    o.api.renderOutlineCurrent();
    return { o, names: collapsedNames(byId.outlineList) };
}
{
    const a = collapsedAfterOpen(0);
    check('level 0 (All): nothing collapsed', a.names.length === 0, a.names.join());
    const b = collapsedAfterOpen(1);
    check('level 1: roots collapsed (and everything below, which is hidden anyway)',
        b.names.indexOf('ZClass') >= 0 && b.names.indexOf('AClass') >= 0, b.names.join());
    const c = collapsedAfterOpen(2);
    check('level 2: roots open, Methods collapsed (method nodes with locals too, so expanding Methods shows names only)',
        c.names.join() === 'Alpha,Methods,Methods,Zeta' && c.names.indexOf('ZClass') < 0, c.names.join());
    const d = collapsedAfterOpen(3);
    check('level 3: classes + Methods open, method names (with locals) collapsed', d.names.join() === 'Alpha,Zeta', d.names.join());
    const e = collapsedAfterOpen(4);
    check('level 4: everything with children open down to the locals (leaves)', e.names.length === 0, e.names.join());
}

// ---------- applied once, nothing remembered ----------
section('applied once per open');
{
    const o = loadOutline();
    o.set('level', 2);
    o.api.openOutline();
    check('pending armed on open', o.get().pending === true);
    o.set('symbols', tree());
    o.api.renderOutline(tree(), true);                 // a filtered (force-expanded) render must not consume it
    check('a force-expanded render keeps the level pending', o.get().pending === true);
    o.api.renderOutline([], false);                    // an empty tree must not consume it either
    check('an empty render keeps the level pending', o.get().pending === true);
    o.api.renderOutlineCurrent();
    check('first real render applies the level and clears pending', o.get().pending === false && Object.keys(o.get().collapsed).length === 4);   // 2 x Methods + the 2 method nodes that have locals
    // user expands one Methods node, then a refresh renders again
    const key = Object.keys(o.get().collapsed)[0];
    delete o.get().collapsed[key];
    o.api.renderOutlineCurrent();
    check('a later refresh keeps the user\'s own toggle (level not re-applied)', !(key in o.get().collapsed) && Object.keys(o.get().collapsed).length === 3);
    // reopening starts clean
    o.api.openOutline();
    check('reopen resets to the configured level', o.get().pending === true && Object.keys(o.get().collapsed).length === 0);
}

// ---------- settings round trip ----------
section('settings plumbing');
{
    check('level select present with All..4', /<select id="setOutlineLevel">[\s\S]*?value="0"[\s\S]*?value="4"[\s\S]*?<\/select>/.test(html));
    check('sort checkbox present', /id="setOutlineSortLevel"/.test(html));
    check('both controls are change-listened', /'setOutlineLevel', 'setOutlineSortLevel'/.test(html));
    check('panel -> payload', /outlineLevel: Math\.max\(0, Math\.min\(4, parseInt\(sel\('setOutlineLevel'/.test(html) && /outlineSortLevel: chk\('setOutlineSortLevel'\)/.test(html));
    check('payload -> page', /outlineLevelPref = Math\.max\(0, Math\.min\(4, s\.outlineLevel \| 0\)\)/.test(html) && /outlineSortLevelPref = s\.outlineSortLevel/.test(html));
    check('host -> panel', /getElementById\('setOutlineLevel'\)\)\) el\.value = String\(/.test(html) && /getElementById\('setOutlineSortLevel'\)\)\) el\.checked/.test(html));
    check('C# declares both fields (defaults All / off)', /public int OutlineLevel = 0;/.test(cs) && /public bool OutlineSortLevel = false;/.test(cs));
    check('C# loads, saves, reads from the payload and ships both keys',
        /"OutlineLevel"/.test(cs) && /"OutlineSortLevel"/.test(cs) && /"outlineLevel"/.test(cs) && /"outlineSortLevel"/.test(cs) &&
        (cs.match(/OutlineLevel/g) || []).length >= 6);
}

console.log('\n' + pass + ' passed, ' + fail + ' failed');
process.exit(fail ? 1 : 0);
