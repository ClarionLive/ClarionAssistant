// local-first-hover.test.js - 1c685f2e item 6: two-phase hover in the CA Embeditor.
//
// Run:  node Terminal/test/local-first-hover.test.js [path-to-monaco-embeditor.html]
//
// Zero-dependency. Runs the REAL hover providers extracted from monaco-embeditor.html (see
// local-first-loader.js) against a host whose replies and timers the test releases by hand. Pinned:
//   * two providers: the local card resolves while the LSP is still out
//   * both share ONE localHover request per (model, version, position)
//   * an authoritative local answer means no LSP hover request at all
//   * otherwise the LSP card shows, unless it names the same symbol as the local card (case-insensitive)
//   * a null or hung local answer lets the LSP proceed as before, after the short local timeout
//   * GH #250: a FALLBACK local card (a keyword) shows nothing itself; the server's card wins, and the local card
//     shows only when the server answers nothing or misses the 300 ms deadline counted from the hover's start

const { load, track, flush, check, section, finish } = require('./local-first-loader');

const LINES = ['  CODE', '    MyVar = 1', '    x = 1 ! MyVar'];
const POS = { lineNumber: 2, column: 6 };
const card = (sig, tail) => '```clarion\n' + sig + '\n```\n\n' + (tail || 'local');
// Hover both providers at `pos`; returns [local, lsp] tracked promises.
function hoverBoth(e, pos) {
    const [loc, lsp] = e.providers.hover;
    return [track(loc.provideHover(e.model, pos || POS)), track(lsp.provideHover(e.model, pos || POS))];
}
const value = (t) => t.value && t.value.contents && t.value.contents[0] && t.value.contents[0].value;

async function main() {
    section('6.1 / 6.2 / 6.3: two providers, the local card first, one shared local request');
    {
        const e = load({ lines: LINES });
        check('6.1 two hover providers are registered for clarion', e.providers.hover.length === 2, 'got ' + e.providers.hover.length);
        const [loc, lsp] = hoverBoth(e);
        check('6.3 ONE localHover for the two providers', e.requests('localHover').length === 1, 'posted ' + e.requests('localHover').length);
        const lh = e.requests('localHover')[0];
        check('localHover carries v, line and column, and gives up within 500 ms', lh && typeof lh.v === 'number' && lh.line === 2 &&
            lh.column === 6 && lh.timeoutMs <= 500 && !('buffer' in lh), JSON.stringify(lh));
        e.reply('localHover', { contents: card('MyVar  LONG'), authoritative: false, ms: 1 });
        await flush();
        check('6.2 the local card resolves while the LSP is still pending', loc.done && value(loc) === card('MyVar  LONG') &&
            !lsp.done && e.pending('hover').length === 1);
        e.reply('hover', { contents: card('OtherThing  STRING(20)', 'global') });
        await flush();
        check('6.5 not authoritative, a different symbol: the LSP card is returned', lsp.done && /OtherThing/.test(value(lsp) || ''),
            JSON.stringify(lsp.value));
        // A second hover at the same spot and version reuses the shared answer; a new version asks again.
        hoverBoth(e);
        check('...the same (model, version, position) asks no second localHover', e.requests('localHover').length === 1);
        e.model.setLine(2, '    MyVar = 2');
        hoverBoth(e);
        check('...a new version asks again', e.requests('localHover').length === 2);
    }

    section('6.4: an authoritative local answer skips the LSP request');
    {
        const e = load({ lines: LINES });
        const [loc, lsp] = hoverBoth(e);
        e.reply('localHover', { contents: card('MyVar  LONG'), authoritative: true, ms: 1 });
        await flush();
        check('6.4 the LSP provider returns null', lsp.done && lsp.value == null, JSON.stringify(lsp.value));
        check('6.4 ...and posts NO hover request', e.requests('hover').length === 0);
        check('6.4 the local card is shown', loc.done && value(loc) === card('MyVar  LONG'));
    }

    section('6.6: not authoritative, the same symbol: the LSP card is suppressed (case-insensitive)');
    {
        const e = load({ lines: LINES });
        const [, lsp] = hoverBoth(e);
        e.reply('localHover', { contents: card('myvar  LONG'), authoritative: false });
        await flush();
        e.reply('hover', { contents: card('MyVar  LONG', 'from the LSP') });
        await flush();
        check('6.6 the LSP card naming MyVar is dropped when the local card names myvar', lsp.done && lsp.value == null, JSON.stringify(lsp.value));

        const q = load({ lines: LINES });
        const [, lsp2] = hoverBoth(q);
        q.reply('localHover', { contents: card('ThisWindow.Init PROCEDURE', 'local procedure'), authoritative: false });
        await flush();
        q.reply('hover', { contents: card('ThisWindow.Init PROCEDURE(),BYTE,VIRTUAL', 'WindowManager') });
        await flush();
        check('6.6 a dotted method name compares the same way', lsp2.done && lsp2.value == null, JSON.stringify(lsp2.value));
    }

    section('6.7 / 6.8: a null or hung local answer lets the LSP proceed');
    {
        const e = load({ lines: LINES });
        const [loc, lsp] = hoverBoth(e);
        e.reply('localHover', null);
        await flush();
        check('6.7 local null: the local provider shows nothing', loc.done && loc.value == null);
        check('6.7 ...and the LSP request is posted as before', e.requests('hover').length === 1);
        e.reply('hover', { contents: card('MyVar  LONG', 'lsp') });
        await flush();
        check('6.7 ...and its card is returned', lsp.done && /MyVar/.test(value(lsp) || ''));

        const h = load({ lines: LINES });
        const [, lsph] = hoverBoth(h);
        await flush();
        check('6.8 while local is out, no LSP request yet', h.requests('hover').length === 0);
        const lh = h.requests('localHover')[0];
        const fired = h.fire(lh.timeoutMs);
        await flush();
        check('6.8 the local timeout (<= 500 ms) releases the LSP request', fired === 1 && lh.timeoutMs <= 500 && h.requests('hover').length === 1,
            'timeoutMs=' + lh.timeoutMs + ' fired=' + fired);
        h.reply('hover', { contents: card('MyVar  LONG', 'lsp') });
        await flush();
        check('6.8 ...and the LSP card is shown', lsph.done && /MyVar/.test(value(lsph) || ''));

        const n = load({ lines: LINES });
        const [locn, lspn] = hoverBoth(n);
        n.reply('localHover', { contents: null, authoritative: true });
        await flush();
        check('an "authoritative" reply with no contents does not suppress the LSP', n.requests('hover').length === 1 && locn.value == null);
        n.reply('hover', null);
        await flush();
        check('...and a null LSP reply shows no card', lspn.done && lspn.value == null);
    }

    section('GH #250: a fallback (keyword) card defers to the server, with a deadline');
    {
        const KW = ['  CODE', '    CASE EVENT()', '    OF EVENT:Timer', '    END'];
        const END = { lineNumber: 4, column: 6 };
        const kwCard = card('END', 'keyword · Control Flow');
        const srvCard = '**END** — closes CASE\n\nCASE EVENT()';

        const e = load({ lines: KW });
        const [loc, lsp] = hoverBoth(e, END);
        e.reply('localHover', { contents: kwCard, authoritative: false, fallback: true, kind: 'keyword' });
        await flush();
        check('#250 the local provider shows nothing for a fallback card', loc.done && loc.value == null, JSON.stringify(loc.value));
        check('#250 ...and the server IS asked (no authoritative skip)', e.requests('hover').length === 1);
        check('#250 ...and nothing shows until the server answers', !lsp.done);
        e.reply('hover', { contents: srvCard });
        await flush();
        check('#250 the server card replaces the keyword card (one card)', lsp.done && value(lsp) === srvCard, JSON.stringify(lsp.value));

        const n = load({ lines: KW });
        const [locn, lspn] = hoverBoth(n, END);
        n.reply('localHover', { contents: kwCard, authoritative: false, fallback: true, kind: 'keyword' });
        await flush();
        n.reply('hover', null);
        await flush();
        check('#250 server answers null: the keyword card is the fallback', lspn.done && value(lspn) === kwCard && locn.value == null, JSON.stringify(lspn.value));

        const m = load({ lines: KW });
        const [, lspm] = hoverBoth(m, END);
        m.reply('localHover', { contents: kwCard, fallback: true });
        await flush();
        m.reply('hover', { contents: '' });
        await flush();
        check('#250 server answers empty contents: the keyword card', lspm.done && value(lspm) === kwCard, JSON.stringify(lspm.value));

        const d = load({ lines: KW });
        const [, lspd] = hoverBoth(d, END);
        d.reply('localHover', { contents: kwCard, fallback: true });
        await flush();
        check('#250 the deadline timer is 300 ms', d.armed(300) === 1, 'armed(300)=' + d.armed(300));
        const fired = d.fire(300);
        await flush();
        check('#250 server past the deadline: the keyword card shows at 300 ms', fired === 1 && lspd.done && value(lspd) === kwCard, JSON.stringify(lspd.value));
        d.reply('hover', { contents: srvCard });
        await flush();
        check('#250 ...and a late server card does not replace it', value(lspd) === kwCard);

        // The deadline runs from the hover's start: a local answer slower than it skips the server altogether.
        const s = load({ lines: KW });
        const [, lsps] = hoverBoth(s, END);
        s.fire(300);
        s.reply('localHover', { contents: kwCard, fallback: true });
        await flush();
        check('#250 deadline already passed when the local card lands: the card, no hover posted',
            lsps.done && value(lsps) === kwCard && s.requests('hover').length === 0, 'hover posts=' + s.requests('hover').length);
        // (While typing, the idle-sync case is in local-first-slices.test.js, which has the span-map setup.)
    }

    section('GH #250: non-fallback cards are unchanged');
    {
        const e = load({ lines: LINES });
        const [loc, lsp] = hoverBoth(e);
        e.reply('localHover', { contents: card('GloVar  LONG', 'index'), authoritative: true, fallback: false, kind: 'index' });
        await flush();
        check('#250 an authoritative index card still skips the server', lsp.done && lsp.value == null && e.requests('hover').length === 0 &&
            loc.done && /GloVar/.test(value(loc) || ''));

        const b = load({ lines: LINES });
        const [locb, lspb] = hoverBoth(b);
        b.reply('localHover', { contents: card('MyVar  LONG'), authoritative: false, fallback: false, kind: 'member' });
        await flush();
        check('#250 a non-fallback local card still shows at once beside the LSP', locb.done && /MyVar/.test(value(locb) || '') && !lspb.done);
        b.fire(300);
        await flush();
        check('#250 ...and the 300 ms deadline does not touch it', !lspb.done);
        b.reply('hover', { contents: card('MyVar  LONG', 'lsp') });
        await flush();
        check('#250 ...and the same-symbol dedupe still drops the LSP card', lspb.done && lspb.value == null, JSON.stringify(lspb.value));
    }

    section('6.9: inside a comment neither provider posts');
    {
        const e = load({ lines: LINES });
        const [a, b] = hoverBoth(e, { lineNumber: 3, column: 17 });
        await flush();
        check('6.9 no request at all', e.posted.length === 0, JSON.stringify(e.posted.map(m => m.action)));
        check('6.9 ...and both providers return null', a.done && b.done && a.value == null && b.value == null);
    }

    finish();
}

main().catch(err => { console.error(err && err.stack || err); process.exit(1); });
