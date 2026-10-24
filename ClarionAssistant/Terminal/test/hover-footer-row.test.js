// hover-footer-row.test.js - the hover footer row: a card's location links and the Structure Designer action
// in a bar under the card, drawn like Monaco's own status bar under a problem hover.
//
// Run:  node Terminal/test/hover-footer-row.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL hover providers, link opener and footer block extracted from
// monaco-embeditor.html (local-first-loader.js) on a stand-in model and editor. Pinned:
//   * a line that is only location links ([File.clw:12](file:///...#L12), optionally "→" a second one or led
//     by 📍) leaves the card and becomes the footer; a link inside a code block, or in a line with other
//     text, stays in the card; a card with no such line is returned as it was, one entry
//   * a control card (Attributes/Available/With value) and a WINDOW/REPORT header card get
//     "Open Structure Designer (<key>)", the key read from the page's key bindings; the footer is trusted
//     for that one command only, and the card keeps its own trust (chips)
//   * the action is not offered where the command would refuse: a plain file without the designer
//     overlay, or a designer already open; nor on other cards
//   * a click puts the cursor where the card was hovered, closes the card and runs the command once
//   * the local card, the LSP card and the fallback path all go through it

const { load, flush, check, section, finish } = require('./local-first-loader');

const CMD = 'clarion.openStructureDesigner';
const LINES = ['Window WINDOW', "  BUTTON('OK'),AT(1,1,20,10),USE(?OK)", '  END'];
const POS = { lineNumber: 2, column: 4 };   // on BUTTON
const LINK = '[Main.clw:233](file:///d%3A/src/Main.clw#L233)';
const LINK2 = '[Main.inc:12](file:///d%3A/src/Main.inc#L12)';
const VAR_CARD = '**MyVar** — `LONG`\n\n```clarion\nMyVar LONG\n```\n' + LINK + '\n\nA counter.';
const CONTROL_CARD = '**BUTTON** — Declares a pushbutton control\n\n**Attributes:** `AT` `USE`\n\n**Available:** DEFAULT · FLAT';
const HEADER_CARD = '**MyWindow WINDOW**\n*Test*\n\n📐 AT(,,300,200)';

// Wire the editor and the page state the footer reads (outside the loader's slices) onto the env.
function rig(e, opts) {
    opts = opts || {};
    const log = { designer: 0, setPos: [], focus: 0, hide: 0 };
    e.model.isDisposed = () => false;
    Object.assign(e.editor, {
        setPosition(p) { log.setPos.push(p); },
        focus() { log.focus++; },
        getContribution(id) { return id === 'editor.contrib.contentHover' ? { _hideWidgets() { log.hide++; } } : null; },
    });
    e.fileMode = opts.fileMode === true;
    e.designerEnabled = opts.designerEnabled !== false;
    e.designerSession = opts.designerSession || null;
    e.keyEntryById = (id) => id === 'structureDesigner' ? { id, def: 'Ctrl+D' } : null;
    e.effectiveChord = () => opts.key === undefined ? 'Ctrl+D' : opts.key;
    e.cmdStructureDesigner = () => { log.designer++; };
    return log;
}

// A card through the REAL LSP hover provider; returns the content list Monaco would render.
async function lspHover(e, md) {
    let out;
    e.providers.hover[1].provideHover(e.model, POS).then(v => { out = v; });
    e.reply('localHover', { contents: null });
    await flush();
    e.reply('hover', { contents: md });
    await flush();
    return out && out.contents;
}
// A card through the REAL local hover provider.
async function localHover(e, md) {
    let out;
    e.providers.hover[0].provideHover(e.model, POS).then(v => { out = v; });
    e.reply('localHover', { contents: md });
    await flush();
    return out && out.contents;
}
const click = (e) => e.linkOpeners[0].open({ scheme: 'command', authority: '', path: CMD, query: '', fragment: '' });
const designerTrusted = (c) => !!(c && c.isTrusted && c.isTrusted.enabledCommands && c.isTrusted.enabledCommands.length === 1 && c.isTrusted.enabledCommands[0] === CMD);

async function main() {
    section('location links move to the footer');
    {
        const e = load({ lines: LINES }); rig(e);
        const c = await lspHover(e, VAR_CARD);
        check('two entries: the card, then the footer', c && c.length === 2, JSON.stringify(c));
        check('the footer is the link', c[1].value === LINK, c[1].value);
        check('the card no longer holds it, the rest kept in order', c[0].value === '**MyVar** — `LONG`\n\n```clarion\nMyVar LONG\n```\n\nA counter.', JSON.stringify(c[0].value));
        check('a footer of links only needs no trust', !c[1].isTrusted);

        const both = await lspHover(load({ lines: LINES }), '**Foo** 🌍 Global Procedure\n\n```clarion\nFoo PROCEDURE\n```\n' + LINK2 + ' → ' + LINK);
        check('a declaration → implementation line moves whole', both && both.length === 2 && both[1].value === LINK2 + ' → ' + LINK, both && both[1] && both[1].value);

        const pin = await lspHover(load({ lines: LINES }), '**Routine:** `Init`\n\n📍 ' + LINK + '\n\n```clarion\nInit ROUTINE\n```');
        check('a 📍 line moves, without the pin', pin && pin.length === 2 && pin[1].value === LINK, pin && pin[1] && pin[1].value);
        check('...and the card closes the gap it left', pin && pin[0].value === '**Routine:** `Init`\n\n```clarion\nInit ROUTINE\n```', JSON.stringify(pin && pin[0].value));

        const two = await lspHover(load({ lines: LINES }), '**X**\n\n' + LINK2 + '\n' + LINK);
        check('two link lines share one footer, in order', two && two.length === 2 && two[1].value === LINK2 + ' ' + LINK, two && two[1] && two[1].value);
    }

    section('links that stay in the card');
    {
        const fenced = '**X**\n\n```clarion\n' + LINK + '\n```';
        let c = await lspHover(load({ lines: LINES }), fenced);
        check('a link inside a code block stays; nothing to show, one entry', c && c.length === 1 && c[0].value === fenced, JSON.stringify(c));
        const inline = '**X**\n\nDeclared in ' + LINK + ' as a global.';
        c = await lspHover(load({ lines: LINES }), inline);
        check('a link with other text on its line stays', c && c.length === 1 && c[0].value === inline, JSON.stringify(c));
        const plain = '**END** — closes LOOP';
        c = await lspHover(load({ lines: LINES }), plain);
        check('a card with no links is returned as it was', c && c.length === 1 && c[0].value === plain, JSON.stringify(c));
        const warn = '**LOOP** — closed by END on line 9\n\n' + LINK + '\n\n⚠️ This END is lined up with the IF on line 2.';
        c = await lspHover(load({ lines: LINES }), warn);
        check('a warning after the link stays in the card, the link goes', c && c.length === 2 && c[0].value === '**LOOP** — closed by END on line 9\n\n⚠️ This END is lined up with the IF on line 2.' && c[1].value === LINK, JSON.stringify(c));
    }

    section('the Structure Designer action');
    {
        const e = load({ lines: LINES }); rig(e);
        let c = await lspHover(e, CONTROL_CARD);
        check('a control card gets the action, with its key', c && c.length === 2 && /^\[Open Structure Designer \(Ctrl\+D\)\]\(command:clarion\.openStructureDesigner "/.test(c[1].value), JSON.stringify(c));
        check('...the footer trusted for that command only', designerTrusted(c[1]), JSON.stringify(c[1].isTrusted));
        check('...the card itself unchanged', c[0].value === CONTROL_CARD);

        c = await lspHover(load({ lines: LINES }), HEADER_CARD).then(x => x);
        // (no rig: the stubs make designerSession look open, so nothing is offered)
        check('without the page state the action is not offered', c && c.length === 1, JSON.stringify(c));

        const h = load({ lines: LINES }); rig(h);
        c = await lspHover(h, HEADER_CARD);
        check('a WINDOW header card gets the action', c && c.length === 2 && /Open Structure Designer/.test(c[1].value), JSON.stringify(c));
        const r = load({ lines: LINES }); rig(r);
        c = await lspHover(r, '**MyReport REPORT**\n\n📐 6000×9000 @ (1000,1000)');
        check('a REPORT header card gets the action', c && c.length === 2 && /Open Structure Designer/.test(c[1].value), JSON.stringify(c));
        const a = load({ lines: LINES }); rig(a);
        c = await lspHover(a, '**Main APPLICATION**\n\nframe');
        check('an APPLICATION header does not (the designer lookup takes WINDOW/REPORT)', c && c.length === 1, JSON.stringify(c));
        const v = load({ lines: LINES }); rig(v);
        c = await lspHover(v, VAR_CARD);
        check('a variable card gets its link only', c && c.length === 2 && c[1].value === LINK && !c[1].isTrusted, JSON.stringify(c));

        const k = load({ lines: LINES }); rig(k, { key: 'Ctrl+Shift+D' });
        c = await lspHover(k, CONTROL_CARD);
        check('the label follows the bound key', c && /\(Ctrl\+Shift\+D\)\]/.test(c[1].value), c && c[1] && c[1].value);
        const u = load({ lines: LINES }); rig(u, { key: '' });
        c = await lspHover(u, CONTROL_CARD);
        check('an unbound command shows no key', c && /^\[Open Structure Designer\]\(/.test(c[1].value), c && c[1] && c[1].value);
    }

    section('not offered where the command would refuse');
    {
        const cases = [
            ['a plain file without the designer overlay', { fileMode: true, designerEnabled: false }],
            ['a designer already open', { designerSession: { decoId: 'd1' } }],
        ];
        for (const [what, opts] of cases) {
            const e = load({ lines: LINES }); rig(e, opts);
            const c = await lspHover(e, CONTROL_CARD);
            check(what + ': no footer', c && c.length === 1 && c[0].value === CONTROL_CARD, JSON.stringify(c));
        }
        const f = load({ lines: LINES }); rig(f, { fileMode: true, designerEnabled: true });
        const c = await lspHover(f, CONTROL_CARD);
        check('a plain file WITH the designer overlay: offered', c && c.length === 2, JSON.stringify(c));
    }

    section('click: cursor to the hovered place, card closed, command run once');
    {
        const e = load({ lines: LINES }); const log = rig(e);
        await lspHover(e, CONTROL_CARD);
        const handled = click(e);
        check('the opener reports the link handled', handled === true);
        check('the cursor goes where the card was hovered', log.setPos.length === 1 && log.setPos[0].lineNumber === 2 && log.setPos[0].column === 4, JSON.stringify(log.setPos));
        check('the card closes and the editor has focus', log.hide === 1 && log.focus === 1);
        check('the Structure Designer command runs once', log.designer === 1);

        const d = load({ lines: LINES }); const dlog = rig(d);
        await lspHover(d, CONTROL_CARD);
        d.model.isDisposed = () => true;
        let threw = null;
        try { click(d); } catch (x) { threw = x; }
        check('a click after the model is gone does nothing and throws nothing', !threw && dlog.designer === 0);

        const n = load({ lines: LINES }); const nlog = rig(n);
        let threw2 = null;
        try { click(n); } catch (x) { threw2 = x; }
        check('a click with no card shown does nothing', !threw2 && nlog.designer === 0 && n.requests('openLocation').length === 0);
    }

    section('chips and the footer together');
    {
        const e = load({ lines: LINES }); rig(e);
        const args = encodeURIComponent(JSON.stringify([{ e: [{ l: 0, c: 36, el: 0, ec: 36, t: ',HIDE', o: [LINES[1]] }] }])).replace(/\(/g, '%28').replace(/\)/g, '%29');
        const card = CONTROL_CARD.replace('FLAT', '[HIDE](command:clarion.applyAttributeEdit?' + args + ' "Add HIDE — hidden")');
        const c = await lspHover(e, card);
        check('the card keeps its chip trust', c && c[0].isTrusted && c[0].isTrusted.enabledCommands[0] === 'clarion.applyAttributeEdit', JSON.stringify(c && c[0].isTrusted));
        check('the footer has the designer trust only', designerTrusted(c && c[1]), JSON.stringify(c && c[1]));
    }

    section('the local card and the fallback path use the footer too');
    {
        const e = load({ lines: LINES }); rig(e);
        const c = await localHover(e, VAR_CARD);
        check('local card: link in the footer', c && c.length === 2 && c[1].value === LINK, JSON.stringify(c));

        const f = load({ lines: LINES }); rig(f);
        let out;
        f.providers.hover[1].provideHover(f.model, POS).then(v => { out = v; });
        f.reply('localHover', { contents: '**BUTTON**\n\nkeyword', fallback: true });
        await flush();
        f.reply('hover', { contents: CONTROL_CARD });
        await flush();
        check('fallback path: the server card gets the footer', out && out.contents.length === 2 && /Open Structure Designer/.test(out.contents[1].value), JSON.stringify(out));
    }

    // ---- integration on top of the patch (GH #270) ----
    section('cards that list attributes but are not designed: FILE, KEY, MEMO, APPLICATION');
    {
        const chip = '[PRE](command:clarion.applyAttributeEdit?%5B%5D "Remove PRE")';
        for (const [what, md] of [
            ['a FILE card', '**Customer FILE** — Declares a data file\n\n**Attributes:** `DRIVER` ' + chip],
            ['a KEY card', '**KeyId KEY** — Declares a key\n\n**Attributes:** [NOCASE](command:clarion.applyAttributeEdit?%5B%5D "Remove NOCASE")'],
            ['a MEMO card', '**Notes MEMO** — Declares a variable-length text field\n\n**Available:** [BINARY](command:clarion.applyAttributeEdit?%5B%5D "Add BINARY")'],
            ['an APPLICATION header card with chips', '**Main APPLICATION**\n\n**Attributes:** [MDI](command:clarion.applyAttributeEdit?%5B%5D "Remove MDI")'],
            ['a keyword card that names a control but lists no attributes', '**BUTTON** — Declares a pushbutton control'],
        ]) {
            const e = load({ lines: LINES }); rig(e);
            const c = await lspHover(e, md);
            check(what + ': no designer action', c && c.length === 1, JSON.stringify(c));
        }
    }

    section('link shapes the footer takes, and the one it leaves');
    {
        const titled = '[Main.clw:233](file:///d%3A/src/Main.clw#L233 "Go to the declaration")';
        let c = await lspHover(load({ lines: LINES }), '**MyVar** — `LONG`\n\n' + titled);
        check('a link with a "title" moves', c && c.length === 2 && c[1].value === titled, JSON.stringify(c));
        const esc = '[My\\_Mod.clw:7](file:///d%3A/src/My_Mod.clw#L7)';
        c = await lspHover(load({ lines: LINES }), '**X**\n\n' + esc);
        check('a label with an escaped character moves', c && c.length === 2 && c[1].value === esc, JSON.stringify(c));
        c = await lspHover(load({ lines: LINES }), LINK);
        check('a card that is nothing but its link keeps it (one entry: Monaco would skip an empty card)', c && c.length === 1 && c[0].value === LINK, JSON.stringify(c));
    }

    section('the footer row is sticky: a tall card scrolls behind it (geircodes, #270 follow-up)');
    {
        const html = require('fs').readFileSync(process.argv[2] || require('path').join(__dirname, '..', 'monaco-embeditor.html'), 'utf8');
        const m = /\.monaco-editor \.monaco-hover \.hover-row-contents > \.markdown-hover:not\(:first-child\):last-child \{([^}]*)\}/.exec(html);
        const rule = m ? m[1] : '';
        check('the footer rule draws the status-bar background under a rule',
            /background-color:\s*var\(--vscode-editorHoverWidget-statusBarBackground\)/.test(rule) && /border-top:\s*1px solid var\(--vscode-editorHoverWidget-border\)/.test(rule), rule);
        check('...and is position: sticky; bottom: 0, above the card (z-index)',
            /position:\s*sticky/.test(rule) && /bottom:\s*0/.test(rule) && /z-index:\s*[1-9]/.test(rule), rule);
    }

    section('the label and the click follow the REAL key table (EDITOR_COMMANDS + KEY_PROFILES)');
    {
        const km = loadKeymap();
        check('the key section loads', !!km.api, km.error);
        if (km.api) {
            const run = (e) => { e.keyEntryById = km.api.keyEntryById; e.effectiveChord = km.api.effectiveChord; };
            for (const [profile, key] of [['clarion', 'Ctrl+D'], ['vscode', 'Ctrl+Shift+D'], ['vs', 'Ctrl+Shift+D'], ['npp', 'Ctrl+Shift+D']]) {
                km.api.setKeyProfile(profile);
                const e = load({ lines: LINES }); rig(e); run(e);
                const c = await lspHover(e, CONTROL_CARD);
                check(profile + ' profile: "Open Structure Designer (' + key + ')"', c && c.length === 2 && c[1].value.indexOf('[Open Structure Designer (' + key + ')](') === 0, c && c[1] && c[1].value);
                check(profile + ' profile: that key dispatches the same command entry', km.api.chordMap()[key] === km.api.keyEntryById('structureDesigner'));
            }
            km.api.setKeyProfile('vscode');
            const e = load({ lines: LINES }); const log = rig(e); run(e);
            await lspHover(e, CONTROL_CARD);
            const before = km.calls.structureDesigner;
            click(e);
            check('the click runs the key table entry\'s own run (what the key runs), once', km.calls.structureDesigner === before + 1 && log.designer === 0,
                'entry runs ' + (km.calls.structureDesigner - before) + ', direct cmdStructureDesigner ' + log.designer);
            km.api.setKeyProfile('clarion');
        }
    }

    finish();
}

// The page's key command section (EDITOR_COMMANDS, KEY_PROFILES, chord resolution), sliced out as
// keymap-profiles.test.js does; every run: function is a stub that counts its calls.
function loadKeymap() {
    const fs = require('fs'), path = require('path');
    const html = fs.readFileSync(process.argv[2] || path.join(__dirname, '..', 'monaco-embeditor.html'), 'utf8');
    const cut = (a, b) => { const i = html.indexOf(a); const j = html.indexOf(b, i); if (i < 0 || j < 0) throw new Error('slice ' + a); return html.slice(i, j); };
    const calls = { structureDesigner: 0 };
    try {
        const commandsSrc = cut('    var keyBindings = {};', '    // Map a keydown to a canonical chord');
        const formatSrc = cut('    function registerFormatAction(ed) {', '    // ----- Debugger "Run to Cursor"');
        const names = new Set(['navEmbed', 'openSnippetPicker']);
        const re = /run:\s*([A-Za-z_$][\w$]*)/g; let m;
        while ((m = re.exec(commandsSrc))) if (m[1] !== 'function') names.add(m[1]);
        const list = Array.from(names);
        const stubs = list.map(n => n === 'cmdStructureDesigner' ? function () { calls.structureDesigner++; } : function () { });
        const api = new Function(...list, 'document', commandsSrc + '\n' + formatSrc +
            '\nreturn { keyEntryById: keyEntryById, effectiveChord: effectiveChord, setKeyProfile: setKeyProfile, chordMap: function () { return chordToCmd; } };')(
            ...stubs, { querySelectorAll: () => [] });
        api.setKeyProfile('clarion');
        return { api, calls };
    } catch (err) { return { api: null, error: String(err && err.stack || err), calls }; }
}

main().catch(err => { console.log('HARNESS ERROR: ' + (err && err.stack || err)); process.exit(1); });
