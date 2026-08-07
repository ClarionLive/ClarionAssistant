// clarion-language.js — shared Clarion language registration for Monaco.
//
// Single source of truth for the Clarion Monarch grammar + language configuration.
// Loaded by BOTH monaco-embeditor.html and monaco-diff.html (these previously each
// carried their own copy — tech debt from task 632f671a, paid down in 04dd97f9).
//
// Defines a global registerClarionLanguage() that both pages call inside their
// require(['vs/editor/editor.main'], ...) boot callback, AFTER
// monaco.languages.register({ id: 'clarion' }). 'monaco' is referenced at call time
// (inside the require callback), so it need not exist when this file is parsed.
//
// The language config here is the SUPERSET: it includes indentationRules (smart-indent
// on Enter). The diff editor is read-only, so those rules never fire there — harmless.
// Keep the grammar in THIS file only; do not re-inline copies into the HTML.

function registerClarionLanguage() {
    monaco.languages.setLanguageConfiguration('clarion', {
        comments: { lineComment: '!' },
        brackets: [['(', ')'], ['[', ']'], ['{', '}']],
        // Treat the Clarion prefix separator ':' as part of a word so double-click selects
        // the whole prefixed name (Add:AddressID), not just the field part. This is Monaco's
        // default wordPattern with ':' removed from the separator set. '.' stays a separator
        // so SELF.Method still selects just 'Method'.
        wordPattern: /(-?\d*\.\d\w*)|([^\`~!@#%^&*()\-=+\[{\]}\\|;'",.<>\/?\s]+)/g,
        // Smart auto-indent on Enter (active when autoIndent='full'): indent the body of block
        // structures, outdent on END / lone '.' / CASE & IF sub-keywords. The negative lookahead
        // skips one-line forms that self-terminate with a trailing '.' (e.g. IF x THEN y.).
        indentationRules: {
            // TOOLBAR is split out of the shared alternation with its own tight lookahead
            // (require '(', ',', '!' or end-of-line right after the keyword) — the ABC toolbar
            // template ubiquitously declares a plain variable literally named "Toolbar"
            // ("Toolbar ToolbarClass"), which the other keywords' bare \b boundary would
            // otherwise misread as opening a block and auto-indent the next line. Mirrors the
            // same fix in ModernEmbeditorDiagnostics.cs (C#) and Clarion-Extension's
            // TokenPatterns.ts (PR #378).
            // MENUBAR/MENU/SHEET/TAB/OPTION share TOOLBAR's exact ambiguity (nested inside
            // WINDOW/REPORT bodies, legitimately bare — e.g. "OPTION,USE(?opt)") and are split
            // out alongside it for the same reason (e.g. "option     LONG(0)", a plain local
            // variable named "option", was misread as opening a block).
            increaseIndentPattern: /^\s*(?:(?:IF|LOOP|CASE|BEGIN|EXECUTE|ACCEPT|GROUP|QUEUE|RECORD|FILE|VIEW|REPORT|WINDOW|APPLICATION|CLASS|INTERFACE|MAP|MODULE|ITEMIZE|JOIN|OF|OROF|ELSE|ELSIF)\b|(?:TOOLBAR|MENUBAR|MENU|SHEET|TAB|OPTION)\b(?=\s*(?:[(,!]|$)))(?![^!]*\.\s*$).*$/i,
            decreaseIndentPattern: /^\s*(END\b|\.\s*$|OF\b|OROF\b|ELSE\b|ELSIF\b|UNTIL\b|WHILE\b)/i
        }
    });
    monaco.languages.setMonarchTokensProvider('clarion', {
        ignoreCase: true,
        keywords: [
            'PROGRAM', 'MEMBER', 'MAP', 'MODULE', 'CLASS', 'INTERFACE', 'PROCEDURE', 'FUNCTION',
            'ROUTINE', 'CODE', 'DATA', 'END', 'RETURN', 'EXIT', 'IF', 'THEN', 'ELSE', 'ELSIF',
            'CASE', 'OF', 'OROF', 'LOOP', 'WHILE', 'UNTIL', 'BREAK', 'CYCLE', 'DO', 'BEGIN',
            'EXECUTE', 'GROUP', 'QUEUE', 'RECORD', 'FILE', 'VIEW', 'WINDOW', 'REPORT',
            'APPLICATION', 'DETAIL', 'HEADER',
            'FOOTER', 'BREAK', 'FORM', 'SELF', 'PARENT', 'NEW', 'DISPOSE', 'THREAD', 'NULL',
            'TRUE', 'FALSE', 'AND', 'OR', 'XOR', 'NOT', 'CHOOSE', 'OMIT', 'COMPILE', 'INCLUDE',
            'EQUATE', 'ITEMIZE', 'TYPE', 'LIKE', 'DIM', 'OVER', 'NAME', 'PRE', 'STATIC', 'THREAD'
        ],
        types: [
            'LONG', 'ULONG', 'SHORT', 'USHORT', 'BYTE', 'SIGNED', 'UNSIGNED', 'REAL', 'SREAL',
            'DECIMAL', 'PDECIMAL', 'BFLOAT4', 'BFLOAT8', 'STRING', 'CSTRING', 'PSTRING', 'ASTRING',
            'MEMO', 'BLOB', 'DATE', 'TIME', 'BOOL', 'ANY', 'LONGLONG', 'BIGINT', 'GROUP', 'QUEUE'
        ],
        tokenizer: {
            root: [
                [/!.*$/, 'comment'],
                [/'(?:[^']|'')*'/, 'string'],
                [/\b\d+(?:\.\d+)?\b/, 'number'],
                [/\b[0-9A-Fa-f]+[Hh]\b/, 'number.hex'],
                [/[A-Za-z_][A-Za-z0-9_]*:/, 'type.identifier'],
                // TOOLBAR/MENUBAR/MENU/SHEET/TAB/OPTION are handled by their own rule (ahead of
                // the generic identifier rule below) rather than the flat @keywords list — they
                // need a position-aware lookahead, not a bare \b match. See indentationRules
                // comment above for why.
                [/\b(?:TOOLBAR|MENUBAR|MENU|SHEET|TAB|OPTION)\b(?=\s*(?:[(,!]|$))/i, 'keyword'],
                [/[A-Za-z_][A-Za-z0-9_]*/, {
                    cases: {
                        '@keywords': 'keyword',
                        '@types': 'type',
                        '@default': 'identifier'
                    }
                }]
            ]
        }
    });
}

// Clarion code folding: data/control structures close with END (or a lone '.'); PROCEDURE and
// ROUTINE have no END, so they fold to the next ROUTINE/PROCEDURE boundary (or end of buffer).
// OMIT('term')/COMPILE('term') directives fold to the line containing their terminator (GH #133).
// !REGION ... !ENDREGION comment markers fold as user-defined regions — case-insensitive, nestable.
// Shared by the embeditor AND the diff editor — also feeds Monaco's sticky-scroll scope headers.
// Register once per Monaco page (folding providers are global per language id, but each WebView2
// page hosts its own Monaco instance).
// Splits one raw line into { code, safe } in a SINGLE left-to-right pass that understands
// Clarion's single-quoted strings ('' being an embedded quote):
//   code — trailing '!' comment removed, string CONTENTS intact. The OMIT/COMPILE terminator
//          must be read from this, since blanking would destroy the term (GH #133).
//   safe — the same, with every string's contents blanked to ''. EVERY structure-keyword scan
//          runs on this.
// Why (GH #158): the keyword scan used to run on raw text, so a structure keyword inside an
// ordinary string literal — 'Unable to initialize Session Class instance', 'Content-Type:
// application/xml', 'View not found' — was read as a real structure opener and pushed a phantom
// entry onto the fold stack, producing a bogus fold triangle on a plain executable line. Not an
// edge case: a single ~9,000-line production .clw turned up 64 such literals, the kind any
// codebase accumulates in error messages, log text, and MIME types.
// Doing both in one pass is also what makes the comment strip string-aware: the old
// /!.*$/ replace truncated the line at a '!' INSIDE a literal ('Done!'), which could hide a
// real one-line IF terminator and fold the rest of the procedure into it.
function splitClarionLine(line) {
    var code = '', safe = '', i = 0, n = line.length;
    while (i < n) {
        var ch = line.charAt(i);
        if (ch === '!') break;                       // line comment (we're outside a string here)
        if (ch !== "'") { code += ch; safe += ch; i++; continue; }
        var lit = "'";                               // string literal — copy it to `code` verbatim
        i++;
        while (i < n) {
            var c = line.charAt(i);
            if (c === "'") {
                if (line.charAt(i + 1) === "'") { lit += "''"; i += 2; continue; }   // '' = escaped quote
                lit += "'"; i++; break;                                              // closing quote
            }
            lit += c; i++;
        }
        code += lit;
        safe += "''";                                // placeholder keeps `safe` a valid-looking line
    }
    return { code: code, safe: safe };
}

var STRUCT = /\b(GROUP|QUEUE|RECORD|FILE|VIEW|REPORT|WINDOW|APPLICATION|CLASS|INTERFACE|MAP|MODULE|ITEMIZE|JOIN|LOOP|CASE|BEGIN|EXECUTE|ACCEPT)\b/;
    // TOOLBAR split out with its own tight lookahead ('(', ',', '!' or end-of-line right after
    // the keyword) — STRUCT's bare \b would otherwise push the ABC toolbar template's ubiquitous
    // "Toolbar ToolbarClass" variable declaration onto the fold stack as a never-closed opener,
    // silently swallowing a later real END/'.' and folding unrelated code into it. Same fix as
    // ModernEmbeditorDiagnostics.cs (C#) and Clarion-Extension's TokenPatterns.ts (PR #378).
    // MENUBAR/MENU/SHEET/TAB/OPTION share the identical ambiguity (nested inside WINDOW/REPORT
    // bodies, legitimately bare) and are split out alongside TOOLBAR for the same reason.
var TOOLBAR_OPEN = /^\s*(?:TOOLBAR|MENUBAR|MENU|SHEET|TAB|OPTION)\b(?=\s*(?:[(,!]|$))/i;

// Monaco's FoldingRangeKind.Region marks a range as a USER-defined region, which is what makes
// "Fold All Regions" / "Unfold All Regions" (Ctrl+K Ctrl+8 / Ctrl+K Ctrl+9) act on !REGION blocks
// and leave structure folds alone. Resolved lazily and defensively: this file is also loaded under
// Node by clarion-folding.test.js, where `monaco` doesn't exist, and in the browser the script can
// load before the AMD loader has defined it. An undefined kind is valid — Monaco treats the range
// as a plain fold, so the worst case is losing the Fold-All-Regions grouping, not a broken provider.
function foldKindRegion() {
    try {
        if (typeof monaco !== 'undefined' && monaco.languages && monaco.languages.FoldingRangeKind)
            return monaco.languages.FoldingRangeKind.Region;
    } catch (e) { }
    return undefined;
}

// The fold computation, split out from the provider registration so it can be exercised directly
// by Terminal/test/clarion-folding.test.js without a Monaco instance. Takes anything with
// getLineCount()/getLineContent(i) — the real model in the browser, a plain stub under Node.
function clarionFoldingRanges(model) {
            var ranges = [];
            var stack = [];
            var n = model.getLineCount();
            var lastProc = -1, lastRoutine = -1;
            var omit = null;    // active OMIT/COMPILE region: {start, term} (GH #133)
            var regions = [];   // open !REGION markers, innermost last — regions NEST, unlike OMIT
            var regionKind = foldKindRegion();
            for (var i = 1; i <= n; i++) {
                // OMIT('term') / COMPILE('term') fold to the line CONTAINING the terminator (GH #133).
                // The terminator scan uses the RAW line — it commonly sits inside a comment ("!***") or on
                // an otherwise-blank line, both of which the comment-strip below would erase. While a region
                // is open, everything else is skipped: omitted code isn't compiled, so its ENDs/PROCEDUREs
                // must not pop the structure stack or cut procedure boundaries. Directives don't nest.
                if (omit) {
                    if (model.getLineContent(i).toUpperCase().indexOf(omit.term) >= 0) {
                        if (i > omit.start) ranges.push({ start: omit.start, end: i });
                        omit = null;
                    }
                    continue;
                }
                // !REGION / !ENDREGION user-defined folds. Both markers are Clarion COMMENTS, so they
                // MUST be read from the RAW line: splitClarionLine strips the comment, leaving `code`
                // empty, and the `code === ''` guard below would drop the line before any test saw it.
                // Same reason the OMIT terminator scan above reads raw.
                //
                // Deliberately anchored at the start of the line (^\s*!) — a marker trailing real code
                // ("X = 1  !REGION foo") is not a region, it's a comment that happens to say "region".
                //
                // The \b after REGION is load-bearing. The reference implementation this mirrors
                // (Clarion-Extension, server/src/ClarionFoldingProvider.ts) tests
                // upperValue.startsWith("!REGION"), which also fires on an ordinary comment like
                // "!Regional settings" and opens a region that never closes — swallowing the rest of
                // the file into a phantom fold. \b makes "!Regional" a plain comment again.
                //
                // Regions NEST (a stack), which is the one place they differ from OMIT/COMPILE.
                var reg = /^\s*!\s*(END)?REGION\b/i.exec(model.getLineContent(i));
                if (reg) {
                    if (reg[1]) {                                   // !ENDREGION closes the innermost open region
                        var openReg = regions.pop();
                        if (openReg && i > openReg) ranges.push({ start: openReg, end: i, kind: regionKind });
                    } else {
                        regions.push(i);
                    }
                    continue;
                }
                // String-aware split: `u` (strings blanked) drives every keyword test below, so a
                // keyword inside a literal can't open a phantom fold (GH #158). The OMIT/COMPILE
                // term is read from `code`, which keeps string contents (GH #133).
                var parts = splitClarionLine(model.getLineContent(i));
                var code = parts.code.trim();
                if (code === '') continue;
                var safe = parts.safe.trim();
                var u = safe.toUpperCase();

                var om = /^(?:OMIT|COMPILE)\s*\(\s*'([^']+)'/.exec(code.toUpperCase());
                if (om) { omit = { start: i, term: om[1] }; continue; }

                if (/^END\b/.test(u) || u === '.') {            // close most-recent structure
                    if (stack.length) {
                        var open = stack.pop();
                        if (i > open) ranges.push({ start: open, end: i });
                    }
                    continue;
                }
                if (/(^|\s)PROCEDURE\b/.test(u)) {              // procedure boundary
                    if (lastRoutine !== -1) { if (i - 1 > lastRoutine) ranges.push({ start: lastRoutine, end: i - 1 }); lastRoutine = -1; }
                    if (lastProc !== -1 && i - 1 > lastProc) ranges.push({ start: lastProc, end: i - 1 });
                    lastProc = i;
                    continue;
                }
                if (/(^|\s)ROUTINE\b/.test(u)) {               // routine boundary
                    if (lastRoutine !== -1 && i - 1 > lastRoutine) ranges.push({ start: lastRoutine, end: i - 1 });
                    lastRoutine = i;
                    continue;
                }
                if (STRUCT.test(u) || TOOLBAR_OPEN.test(u)) { stack.push(i); continue; } // GROUP/QUEUE/LOOP/CASE/...
                if (/^IF\b/.test(u)) {                          // block IF only (skip one-liners)
                    // Index into `safe`, NOT `code` — blanking a literal changes the line's length,
                    // so an offset taken from `u` only lines up with `safe`. Using `code` here would
                    // slice at the wrong column on any IF whose condition contains a string.
                    var thenIdx = u.indexOf(' THEN');
                    var afterThen = thenIdx >= 0 ? safe.substring(thenIdx + 5).trim() : '';
                    var oneLiner = afterThen.length > 0 || /\.\s*$/.test(safe);
                    if (!oneLiner) stack.push(i);
                    continue;
                }
            }
            if (omit && n > omit.start) ranges.push({ start: omit.start, end: n });   // unterminated → rest of file is omitted
            // Unterminated !REGIONs are deliberately DROPPED, unlike the unterminated OMIT above. The
            // difference is semantic: an unterminated OMIT genuinely omits the rest of the file from
            // compilation, so folding it is truthful. A !REGION with no !ENDREGION is just a typo (or a
            // half-typed one), and making the remainder of the file collapsible on every keystroke while
            // the developer is still writing it would be actively annoying.
            if (lastRoutine !== -1 && n > lastRoutine) ranges.push({ start: lastRoutine, end: n });
            if (lastProc !== -1 && n > lastProc) ranges.push({ start: lastProc, end: n });
            return ranges;
}

function registerClarionFolding() {
    monaco.languages.registerFoldingRangeProvider('clarion', { provideFoldingRanges: clarionFoldingRanges });
}

// Node-visible surface for Terminal/test/clarion-folding.test.js. Guarded exactly like
// clarion-formatter.js — `module` is undefined in the WebView2 pages, so this is inert there.
if (typeof module !== 'undefined' && module.exports) {
    module.exports = { splitClarionLine: splitClarionLine, clarionFoldingRanges: clarionFoldingRanges };
}
