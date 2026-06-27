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
            increaseIndentPattern: /^\s*(IF|LOOP|CASE|BEGIN|EXECUTE|ACCEPT|GROUP|QUEUE|RECORD|FILE|VIEW|REPORT|WINDOW|APPLICATION|MENUBAR|MENU|TOOLBAR|SHEET|TAB|OPTION|CLASS|INTERFACE|MAP|MODULE|ITEMIZE|JOIN|OF|OROF|ELSE|ELSIF)\b(?![^!]*\.\s*$).*$/i,
            decreaseIndentPattern: /^\s*(END\b|\.\s*$|OF\b|OROF\b|ELSE\b|ELSIF\b|UNTIL\b|WHILE\b)/i
        }
    });
    monaco.languages.setMonarchTokensProvider('clarion', {
        ignoreCase: true,
        keywords: [
            'PROGRAM', 'MEMBER', 'MAP', 'MODULE', 'CLASS', 'INTERFACE', 'PROCEDURE', 'FUNCTION',
            'ROUTINE', 'CODE', 'DATA', 'END', 'RETURN', 'EXIT', 'IF', 'THEN', 'ELSE', 'ELSIF',
            'CASE', 'OF', 'OROF', 'LOOP', 'WHILE', 'UNTIL', 'BREAK', 'CYCLE', 'DO', 'BEGIN',
            'EXECUTE', 'GROUP', 'QUEUE', 'RECORD', 'FILE', 'VIEW', 'WINDOW', 'REPORT', 'MENU',
            'MENUBAR', 'TOOLBAR', 'SHEET', 'TAB', 'OPTION', 'APPLICATION', 'DETAIL', 'HEADER',
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
// Shared by the embeditor AND the diff editor — also feeds Monaco's sticky-scroll scope headers.
// Register once per Monaco page (folding providers are global per language id, but each WebView2
// page hosts its own Monaco instance).
function registerClarionFolding() {
    var STRUCT = /\b(GROUP|QUEUE|RECORD|FILE|VIEW|REPORT|WINDOW|APPLICATION|MENUBAR|MENU|TOOLBAR|SHEET|TAB|OPTION|CLASS|INTERFACE|MAP|MODULE|ITEMIZE|JOIN|LOOP|CASE|BEGIN|EXECUTE|ACCEPT)\b/;
    monaco.languages.registerFoldingRangeProvider('clarion', {
        provideFoldingRanges: function (model) {
            var ranges = [];
            var stack = [];
            var n = model.getLineCount();
            var lastProc = -1, lastRoutine = -1;
            for (var i = 1; i <= n; i++) {
                var code = model.getLineContent(i).replace(/!.*$/, '').trim(); // strip line comment
                if (code === '') continue;
                var u = code.toUpperCase();

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
                if (STRUCT.test(u)) { stack.push(i); continue; } // GROUP/QUEUE/LOOP/CASE/...
                if (/^IF\b/.test(u)) {                          // block IF only (skip one-liners)
                    var thenIdx = u.indexOf(' THEN');
                    var afterThen = thenIdx >= 0 ? code.substring(thenIdx + 5).trim() : '';
                    var oneLiner = afterThen.length > 0 || /\.\s*$/.test(code);
                    if (!oneLiner) stack.push(i);
                    continue;
                }
            }
            if (lastRoutine !== -1 && n > lastRoutine) ranges.push({ start: lastRoutine, end: n });
            if (lastProc !== -1 && n > lastProc) ranges.push({ start: lastProc, end: n });
            return ranges;
        }
    });
}
