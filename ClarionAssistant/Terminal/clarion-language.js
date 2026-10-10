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
            var loopOpeners = {};   // stack entries (line numbers) that opened a LOOP
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
                // Directive parsing and terminator matching are shared with the omitted-code dimming
                // (parseOmitDirective / lineHasOmitTerminator, below), so both read them the way the
                // compiler does — the terminator is CASE-SENSITIVE (GH #264). What differs on purpose:
                // the fold covers every OMIT/COMPILE whatever its condition, never nests, and runs an
                // unterminated one to the end of the buffer; the dimming does none of those.
                if (omit) {
                    if (lineHasOmitTerminator(model.getLineContent(i), omit.term)) {
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

                var om = parseOmitDirective(code);
                if (om && om.term) { omit = { start: i, term: om.term }; continue; }

                if (/^END\b/.test(u) || u === '.') {            // close most-recent structure
                    if (stack.length) {
                        var open = stack.pop();
                        if (i > open) ranges.push({ start: open, end: i });
                    }
                    continue;
                }
                // UNTIL/WHILE at the start of a line terminates a LOOP just as END does (#222) — the
                // form the Language Reference's own example uses. It closes only a LOOP on top of the
                // stack: anywhere else it is not a terminator this pass understands, and popping some
                // other structure would cut that structure's fold short. (LOOP WHILE x / LOOP UNTIL x
                // on the OPENING line starts with LOOP, so it never reaches this test.) Not \b: ':' is
                // part of a Clarion name, so `While:Count += 1` is an assignment, not a terminator.
                if (/^(UNTIL|WHILE)(?![\w:])/.test(u)) {
                    if (stack.length && loopOpeners[stack[stack.length - 1]]) {
                        var loopOpen = stack.pop();
                        if (i > loopOpen) ranges.push({ start: loopOpen, end: i });
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
                var st = STRUCT.exec(u);
                if (st || TOOLBAR_OPEN.test(u)) {                // GROUP/QUEUE/LOOP/CASE/...
                    if (st && st[1] === 'LOOP') loopOpeners[i] = true;   // UNTIL/WHILE may close it (#222)
                    stack.push(i);
                    continue;
                }
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

// Optional hook a host page installs to fetch folding from the LANGUAGE SERVER instead of the
// line-oriented pass above. Signature: function(model) -> Promise<[{start,end,kind?}] | null>.
//
// The server knows the full grammar; the local pass is line-oriented. #222 was a LOOP terminated by
// UNTIL or WHILE — valid Clarion, and the form the Language Reference's own example uses — which
// the local pass used to leave open, swallowing everything after it. The local pass now closes a
// LOOP on UNTIL/WHILE too, so the fallback is right for that case, but the server stays the
// preferred answer whenever a page can reach it.
//
// Deliberately a HOOK rather than a direct call: this file is shared with monaco-diff.html, which
// has no host bridge, and folding must keep working when the LSP is absent, still starting, or slow.
// A page that installs nothing behaves exactly as before.
var clarionLspFolding = null;
function setClarionLspFolding(fn) { clarionLspFolding = fn; }

function registerClarionFolding() {
    monaco.languages.registerFoldingRangeProvider('clarion', {
        provideFoldingRanges: function (model) {
            if (!clarionLspFolding) return clarionFoldingRanges(model);
            var local = clarionFoldingRanges(model);
            try {
                return Promise.resolve(clarionLspFolding(model)).then(function (server) {
                    // Null/empty means no answer, not "no folds" — an empty gutter on a file that
                    // plainly has structures reads as a bug, so fall back rather than trust it.
                    return (server && server.length) ? server : local;
                }, function () { return local; });
            } catch (e) {
                return local;
            }
        }
    });
}

// ---------------------------------------------------------------------------------------------
// OMIT/COMPILE directive reading, shared by the fold (clarionFoldingRanges above) and the dimming
// below so the two can't drift apart on WHAT a directive and its terminator are. They still differ,
// deliberately, on what they DO with one — see each caller.

// Head of a directive: keyword + quoted terminator. The terminator keeps its case ('' unescaped).
var OMIT_HEAD = /^(OMIT|COMPILE)\s*\(\s*'((?:[^']|'')*)'/i;
// The rest of a COMPLETE directive: optional condition, then the closing ')' that ends the line. The
// condition runs to the LAST ')', so a parenthesised condition survives intact.
var OMIT_TAIL = /^\s*(?:,\s*(.*?))?\s*\)\s*$/;

// Reads an OMIT/COMPILE directive from a line's code (splitClarionLine().code — comment stripped,
// string contents intact). Returns null, or { kind: 'OMIT'|'COMPILE', term, cond, closed }:
// cond is the condition text or null; closed is false for a still-being-typed "OMIT('**'" with no
// closing ')'. The fold accepts that (it always has); the dimming, like the parser, does not.
function parseOmitDirective(code) {
    var c = code.trim();
    var h = OMIT_HEAD.exec(c);
    if (!h) return null;
    var tail = OMIT_TAIL.exec(c.slice(h[0].length));
    return {
        kind: h[1].toUpperCase(),
        term: h[2].replace(/''/g, "'"),
        cond: tail && tail[1] ? tail[1] : null,
        closed: !!tail
    };
}

// Does this RAW line (comments and strings included — the terminator usually sits in a "!***"
// comment) end a block? CASE-SENSITIVE, which is what the Clarion 12 compiler does: verified
// headless (GH #264) — after OMIT('ENDX'), a line holding "endx" does NOT end the block; "ENDX" does.
// The IDE parser agrees (CLexAnalyzer.Omit: an ordinal String.IndexOf).
function lineHasOmitTerminator(raw, term) {
    return raw.indexOf(term) >= 0;
}

// ---------------------------------------------------------------------------------------------
// Omitted-code dimming (GH #264). The native Clarion editor paints code that OMIT/COMPILE excludes
// from the build in its "OmittedCode" colour, so conditional code reads as conditional at a glance;
// the CA Editor used to colour it exactly like live code.
//
// What counts as omitted mirrors the IDE's own parser (ClarionParser.dll, CAnalyzer.omitcompile1/2
// and CLexAnalyzer.Omit), which is what drives the native shading:
//   OMIT('t')              omitted.
//   COMPILE('t')           compiled — no mark.
//   OMIT('t', expr)        omitted when expr is true (an integer > 0).
//   COMPILE('t', expr)     omitted when expr is false.
//   The block runs from the directive line THROUGH the line containing the terminator (the native
//   CustomLine spans both), the terminator found anywhere in the raw text of a later line.
//   An omitted block is skipped wholesale: directives inside it are not directives. A COMPILED
//   block's body is ordinary code, so directives inside it still count (blocks nest that way).
//   An unterminated block is a compile error (355/356) and the native editor shades nothing for it —
//   same here, which also keeps a half-typed OMIT('**') from greying out the rest of the file.
// Unlike the fold, which covers every directive, never nests and runs an unterminated one to the end.
//
// Conditional directives — the one deliberate divergence. The native parser evaluates expr with the
// project's defines in hand; an editor buffer only has the EQUATEs it declares itself. So expr is
// evaluated here only when every name in it resolves: integer literals, TRUE/FALSE, and
// "Name EQUATE(value)" declared earlier in this buffer, optionally compared with = <> ~= < > <= >=.
// Anything else (the usual _DEBUG_, _ABCDllMode_, _C55_ project defines, EQUATEs from INCLUDEs) is
// UNKNOWN: the block keeps its normal colours and gets a faint bar in the left margin instead, from
// the directive line through the terminator line, so it still reads as conditional. The native
// parser falls back to "omitted" for an unresolvable COMPILE — right for it, because it knows the
// defines; wrong here, where it would grey out code the project in fact compiles. An unknown block's
// body is scanned like compiled code (it may well be), but an EQUATE declared inside one is itself
// unknown: in the common pair COMPILE('***',_DEBUG_) TraceOn EQUATE(1) *** / OMIT('***',_DEBUG_)
// TraceOn EQUATE(0) ***, only one of the two exists, and the editor can't tell which.

var OMIT_PREFILTER = /OMIT|COMPILE|EQUATE/i;
var OMIT_DIRECTIVE_PREFILTER = /OMIT|COMPILE/i;
var EQUATE_DECL = /^([A-Za-z_][A-Za-z0-9_:]*)\s+EQUATE\s*\(\s*([^)]*?)\s*\)/i;

// Strips parentheses that wrap the WHOLE expression — "(A = 1)" -> "A = 1" — and only those:
// "(A) = (B)" is left alone, where a naive /^\(.*\)$/ would cut it to "A) = (B".
function stripOuterParens(s) {
    s = s.trim();
    while (s.charAt(0) === '(' && s.charAt(s.length - 1) === ')') {
        var depth = 0, k;
        for (k = 0; k < s.length; k++) {
            var ch = s.charAt(k);
            if (ch === '(') depth++;
            else if (ch === ')' && --depth === 0) break;
        }
        if (k !== s.length - 1) break;   // the first '(' closes before the end: not a wrapper
        s = s.slice(1, -1).trim();
    }
    return s;
}

// Value of one operand, or undefined when it can't be known from this buffer.
function omitAtom(s, equates) {
    s = stripOuterParens(s);
    if (/^[+-]?\d+$/.test(s)) return parseInt(s, 10);
    var u = s.toUpperCase();
    if (u === 'TRUE') return 1;
    if (u === 'FALSE') return 0;
    if (Object.prototype.hasOwnProperty.call(equates, u)) return equates[u];
    return undefined;
}

// Truth of an OMIT/COMPILE condition: true, false, or undefined (unknown → margin bar, not dimmed).
function evalOmitCondition(expr, equates) {
    var e = stripOuterParens(expr);
    var cmp = /^(.+?)\s*(<>|~=|<=|>=|=<|=>|=|<|>)\s*(.+)$/.exec(e);
    if (cmp) {
        var a = omitAtom(cmp[1], equates), b = omitAtom(cmp[3], equates);
        if (a === undefined || b === undefined) return undefined;
        switch (cmp[2]) {
            case '=': return a === b;
            case '<>': case '~=': return a !== b;
            case '<': return a < b;
            case '>': return a > b;
            case '<=': case '=<': return a <= b;
            default: return a >= b;   // '>=' / '=>'
        }
    }
    var v = omitAtom(e, equates);
    return v === undefined ? undefined : v > 0;   // the native rule: an integer > 0 is true
}

// For each terminator the buffer's directives use, the ascending list of lines that contain it —
// built ONCE per pass, so finding a block's end is a binary search rather than a scan. Scanning ahead
// per directive was quadratic: every unterminated OMIT re-read the buffer to its end. With few
// distinct terminators (the real case: '***', '**', '_EndOfInclude_') each line is tested with
// indexOf per term; with many, each line is cut into the substrings that could be a term (by length,
// only where a term's first character occurs) and looked up in a set, so the cost tracks the text,
// not the directive count.
var OMIT_TERM_DIRECT_MAX = 16;
function buildTerminatorIndex(lines, terms) {
    var occ = {}, i, k;
    terms.forEach(function (t) { occ[t] = []; });
    if (terms.length <= OMIT_TERM_DIRECT_MAX) {
        for (i = 1; i < lines.length; i++)
            for (k = 0; k < terms.length; k++)
                if (lineHasOmitTerminator(lines[i], terms[k])) occ[terms[k]].push(i);
        return occ;
    }
    var lens = [], firsts = {}, seenLen = {};
    terms.forEach(function (t) {
        firsts[t.charAt(0)] = true;
        if (!seenLen[t.length]) { seenLen[t.length] = true; lens.push(t.length); }
    });
    for (i = 1; i < lines.length; i++) {
        var raw = lines[i];
        for (var p = 0; p < raw.length; p++) {
            if (!firsts[raw.charAt(p)]) continue;
            for (k = 0; k < lens.length; k++) {
                if (p + lens[k] > raw.length) continue;
                var list = occ[raw.substr(p, lens[k])];
                if (list && list[list.length - 1] !== i) list.push(i);
            }
        }
    }
    return occ;
}

// First line > after in an ascending list, or -1.
function nextLineAfter(list, after) {
    var lo = 0, hi = list.length;
    while (lo < hi) { var mid = (lo + hi) >> 1; if (list[mid] > after) hi = mid; else lo = mid + 1; }
    return lo < list.length ? list[lo] : -1;
}

// The OMIT/COMPILE blocks of a buffer: [{start, end, directive, kind}], 1-based inclusive lines.
// kind 'omitted' = excluded from the build (dimmed); 'conditional' = depends on a condition the
// editor can't evaluate (margin bar). Takes anything with getLineCount()/getLineContent(i), like
// clarionFoldingRanges — the real model in the browser, a stub under Node
// (Terminal/test/clarion-omit-dimming.test.js).
// Linear: one read of the buffer, a cheap regex prefilter per line, the terminator index above, then
// one pass where a block's end is a lookup.
function clarionOmittedRegions(model) {
    var out = [];
    var n = model.getLineCount();
    var lines = new Array(n + 1), terms = [], seen = {};
    for (var r = 1; r <= n; r++) {
        lines[r] = model.getLineContent(r);
        if (!OMIT_DIRECTIVE_PREFILTER.test(lines[r])) continue;
        var d0 = parseOmitDirective(splitClarionLine(lines[r]).code);
        if (d0 && d0.term && !seen[d0.term]) { seen[d0.term] = true; terms.push(d0.term); }
    }
    if (!terms.length) return out;
    var occ = buildTerminatorIndex(lines, terms);
    var equates = {};
    var unknownUntil = 0;   // last line of the furthest-reaching open UNKNOWN block
    for (var i = 1; i <= n; i++) {
        var raw = lines[i];
        if (!OMIT_PREFILTER.test(raw)) continue;
        var code = splitClarionLine(raw).code;
        var eq = EQUATE_DECL.exec(code);   // labels sit in column 1, so this runs on the untrimmed line
        if (eq) {
            // Inside an unknown block the declaration may not exist, and redefining to a value we
            // can't read leaves the name unknown, not at its old value: either way, forget it.
            var name = eq[1].toUpperCase();
            var ev = i <= unknownUntil ? undefined : omitAtom(eq[2], equates);
            if (ev === undefined) delete equates[name]; else equates[name] = ev;
            continue;
        }
        var d = parseOmitDirective(code);
        if (!d || !d.closed || !d.term) continue;
        var state;   // 'omitted' | 'compiled' | 'conditional'
        if (d.cond === null) {
            state = d.kind === 'OMIT' ? 'omitted' : 'compiled';
        } else {
            var truth = evalOmitCondition(d.cond, equates);
            state = truth === undefined ? 'conditional'
                : ((d.kind === 'OMIT') === truth ? 'omitted' : 'compiled');
        }
        if (state === 'compiled') continue;   // its body is ordinary code, scanned as usual
        var end = nextLineAfter(occ[d.term], i);
        if (end < 0) continue;   // unterminated: compile error; the native editor shades nothing
        out.push({ start: i, end: end, directive: d.kind, kind: state });
        if (state === 'omitted') i = end;   // nothing inside an omitted block is a directive
        else if (end > unknownUntil) unknownUntil = end;
    }
    return out;
}

// Paint clarionOmittedRegions() onto an editor's model and keep it current. Model-level decorations
// (ownerId 0), like the embed shading, so a split pane sharing the model shows them too. Recomputed
// on a short debounce after edits — one pass per typing pause, not per keystroke — and on language
// or model changes; cleared for any language other than Clarion (file mode can open plaintext).
// When the result matches what is already painted (the usual case: typing that touches no
// directive), the decorations are left alone. The look lives in the page's CSS (.ca-omitted-line /
// .ca-omitted-text for excluded code, .ca-conditional-margin for unknown) so it follows the theme.
var OMIT_DIM_DEBOUNCE_MS = 250;
function omitDecorationOptions(kind) {
    var sticky = monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges;
    return kind === 'omitted'
        ? { isWholeLine: true, className: 'ca-omitted-line', inlineClassName: 'ca-omitted-text', stickiness: sticky }
        : { isWholeLine: true, linesDecorationsClassName: 'ca-conditional-margin', stickiness: sticky };
}
function installClarionOmitDimming(editor) {
    var ids = [], kinds = [], idsModel = null, timer = null;
    function unchanged(model, decos) {
        if (ids.length !== decos.length) return false;
        for (var k = 0; k < ids.length; k++) {
            var have = model.getDecorationRange(ids[k]), want = decos[k].range;
            if (!have || kinds[k] !== decos[k].kind
                || have.startLineNumber !== want.startLineNumber || have.startColumn !== want.startColumn
                || have.endLineNumber !== want.endLineNumber || have.endColumn !== want.endColumn) return false;
        }
        return true;
    }
    function refresh() {
        timer = null;
        var model = editor.getModel();
        if (idsModel && idsModel !== model) {
            try { if (!idsModel.isDisposed()) idsModel.deltaDecorations(ids, []); } catch (e) { }
            ids = []; kinds = [];
        }
        idsModel = model;
        if (!model) return;
        var regions = model.getLanguageId() === 'clarion' ? clarionOmittedRegions(model) : [];
        var decos = regions.map(function (r) {
            return { kind: r.kind, range: new monaco.Range(r.start, 1, r.end, model.getLineMaxColumn(r.end)) };
        });
        if (unchanged(model, decos)) return;
        ids = model.deltaDecorations(ids, decos.map(function (d) {
            return { range: d.range, options: omitDecorationOptions(d.kind) };
        }));
        kinds = decos.map(function (d) { return d.kind; });
    }
    function schedule(delay) {
        if (timer) clearTimeout(timer);
        timer = setTimeout(refresh, delay);
    }
    editor.onDidChangeModelContent(function () { schedule(OMIT_DIM_DEBOUNCE_MS); });
    editor.onDidChangeModelLanguage(function () { schedule(0); });
    editor.onDidChangeModel(function () { schedule(0); });
    schedule(0);
}

// Node-visible surface for Terminal/test/clarion-folding.test.js. Guarded exactly like
// clarion-formatter.js — `module` is undefined in the WebView2 pages, so this is inert there.
if (typeof module !== 'undefined' && module.exports) {
    module.exports = {
        splitClarionLine: splitClarionLine,
        clarionFoldingRanges: clarionFoldingRanges,
        setClarionLspFolding: setClarionLspFolding,
        clarionOmittedRegions: clarionOmittedRegions,
        evalOmitCondition: evalOmitCondition,
        parseOmitDirective: parseOmitDirective,
        installClarionOmitDimming: installClarionOmitDimming
    };
}
