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
// Shared by the embeditor AND the diff editor — also feeds Monaco's sticky-scroll scope headers.
// Register once per Monaco page (folding providers are global per language id, but each WebView2
// page hosts its own Monaco instance).
function registerClarionFolding() {
    var STRUCT = /\b(GROUP|QUEUE|RECORD|FILE|VIEW|REPORT|WINDOW|APPLICATION|CLASS|INTERFACE|MAP|MODULE|ITEMIZE|JOIN|LOOP|CASE|BEGIN|EXECUTE|ACCEPT)\b/;
    // TOOLBAR split out with its own tight lookahead ('(', ',', '!' or end-of-line right after
    // the keyword) — STRUCT's bare \b would otherwise push the ABC toolbar template's ubiquitous
    // "Toolbar ToolbarClass" variable declaration onto the fold stack as a never-closed opener,
    // silently swallowing a later real END/'.' and folding unrelated code into it. Same fix as
    // ModernEmbeditorDiagnostics.cs (C#) and Clarion-Extension's TokenPatterns.ts (PR #378).
    // MENUBAR/MENU/SHEET/TAB/OPTION share the identical ambiguity (nested inside WINDOW/REPORT
    // bodies, legitimately bare) and are split out alongside TOOLBAR for the same reason.
    var TOOLBAR_OPEN = /^\s*(?:TOOLBAR|MENUBAR|MENU|SHEET|TAB|OPTION)\b(?=\s*(?:[(,!]|$))/i;
    monaco.languages.registerFoldingRangeProvider('clarion', {
        provideFoldingRanges: function (model) {
            var ranges = [];
            var stack = [];
            var n = model.getLineCount();
            var lastProc = -1, lastRoutine = -1;
            var omit = null;    // active OMIT/COMPILE region: {start, term} (GH #133)
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
                var code = model.getLineContent(i).replace(/!.*$/, '').trim(); // strip line comment
                if (code === '') continue;
                var u = code.toUpperCase();

                var om = /^(?:OMIT|COMPILE)\s*\(\s*'([^']+)'/.exec(u);
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
                    var thenIdx = u.indexOf(' THEN');
                    var afterThen = thenIdx >= 0 ? code.substring(thenIdx + 5).trim() : '';
                    var oneLiner = afterThen.length > 0 || /\.\s*$/.test(code);
                    if (!oneLiner) stack.push(i);
                    continue;
                }
            }
            if (omit && n > omit.start) ranges.push({ start: omit.start, end: n });   // unterminated → rest of file is omitted
            if (lastRoutine !== -1 && n > lastRoutine) ranges.push({ start: lastRoutine, end: n });
            if (lastProc !== -1 && n > lastProc) ranges.push({ start: lastProc, end: n });
            return ranges;
        }
    });
}
