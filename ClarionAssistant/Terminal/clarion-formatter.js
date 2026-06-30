// clarion-formatter.js — client-side Clarion "Smart Formatter" (replicates the native IDE formatter).
//
// SHARED, Monaco-INDEPENDENT module. Loaded as a plain <script> by the Monaco pages
// (monaco-embeditor.html now; monaco-source.html when it becomes editable) AND require()'d by the
// Node test harness. No dependency on `monaco` — pure text in/out, so it is unit-testable and
// reusable by any surface. Keep the formatting algorithm in THIS file only.
//
// Public API (global `ClarionFormatter` and module.exports):
//   formatClarion(text, options)                         -> { text, eol }
//   formatClarionRange(text, startLine, endLine, options)-> { text, eol }   (1-based inclusive)
//   DEFAULTS / keyword sets
//
// MODEL — derived from John's native whole-file output (settings: preferred column 31, tab 4;
// golden fixture Terminal/test/fixtures/clbrws002.native.clw). Clarion uses TWO column systems:
//   • DATA world (declarations, far right): label in column 1, type at a "data column".
//       - procedure local data:  data column = P + tab          (P = preferred col, snapped to a tab stop)
//       - routine / standalone:  data column = max(longestLabel+1, base) snapped to a tab stop
//       - nested struct members: data column = parent header's column + tab; END aligns to header.
//   • CODE world (statements, near left): base = 1 tab; +1 tab per nested control structure.
//       - OF/OROF/ELSE/ELSIF sit at their opener's column (native "indent from CASE" OFF).
//       - END aligns to its opener.
//   • Preferred keywords (PROGRAM/MEMBER/MAP/PRAGMA/SECTION) and PROCEDURE/ROUTINE headers -> P.
//   • Continuation lines (previous code ends with '|' or '&') -> statement column + multiplier*tab.
//   • Column-1 comments stay in column 1; whitespace-only lines are left alone.

(function (root) {
    'use strict';

    // ---- Keyword sets ----
    var CONTROL_OPENERS = ['IF', 'LOOP', 'CASE', 'ACCEPT', 'BEGIN', 'EXECUTE'];
    var DATA_STRUCT_OPENERS = [
        'GROUP', 'QUEUE', 'RECORD', 'FILE', 'VIEW', 'REPORT', 'WINDOW', 'APPLICATION',
        'MENUBAR', 'MENU', 'TOOLBAR', 'SHEET', 'TAB', 'OPTION',
        'CLASS', 'INTERFACE', 'MODULE', 'ITEMIZE', 'JOIN', 'OLE',
        'DETAIL', 'HEADER', 'FOOTER', 'FORM'
    ];
    var MID = ['ELSE', 'ELSIF', 'OF', 'OROF'];
    var PREFERRED_KEYWORDS = ['PROGRAM', 'MEMBER', 'MAP', 'PRAGMA', 'SECTION'];
    var PROC_HEADERS = ['PROCEDURE', 'FUNCTION', 'ROUTINE'];
    var STATEMENT_KEYWORDS = [
        'IF', 'ELSE', 'ELSIF', 'CASE', 'OF', 'OROF', 'LOOP', 'WHILE', 'UNTIL', 'END',
        'DO', 'EXIT', 'RETURN', 'CYCLE', 'BREAK', 'ACCEPT', 'BEGIN', 'EXECUTE', 'CODE',
        'DATA', 'GOTO', 'STOP', 'HALT', 'POST', 'NEW', 'DISPOSE', 'ASSERT'
    ];

    var CONTROL_SET = toSet(CONTROL_OPENERS);
    var DATASTRUCT_SET = toSet(DATA_STRUCT_OPENERS);
    var MID_SET = toSet(MID);
    var PREFERRED_SET = toSet(PREFERRED_KEYWORDS);
    var KEYWORD_SET = toSet(CONTROL_OPENERS.concat(DATA_STRUCT_OPENERS, MID, PREFERRED_KEYWORDS, PROC_HEADERS, STATEMENT_KEYWORDS));

    // NOTE: monaco-embeditor.html carries a hardcoded mirror (FORMATTER_FALLBACK_DEFAULTS) used only when
    // this script fails to load. If you add/rename/retune a key here, update that mirror too (deac3d16).
    var DEFAULTS = {
        preferredColumn: 31,
        contLineMultiplier: 2,
        indentComments: true,
        dontIndentCol1Comments: true,
        formatBlockAfterEnd: true,
        indentFromCode: false,
        indentCaseSubKeywords: false,
        colonAsLabel: false,
        // Module-level preferred keywords (PROGRAM/MEMBER/MAP/PRAGMA/SECTION): false = preferred column
        // (native default), true = one code indent (a single tab) instead.
        preferredKeywordIndent: false,
        tabSize: 4,
        insertSpaces: true,
        // Assignment alignment (Upper Park Solutions "Format Assignment" feature, folded into Ctrl+I).
        // Within a contiguous run of assignment statements, pad each left operand to the longest, then
        // place the operator with a fixed gap so the operators line up. Runs AFTER smart indentation.
        alignAssignments: true,
        spacesBeforeAssignment: 2,
        spacesAfterAssignment: 2,
        treatBlankAsContiguous: false,
        treatCommentAsContiguous: false,
        // Assignment-alignment scope: 'global' = align runs using full-buffer context (native behavior),
        // 'selection' = confine each run to the formatted window so a selection never aligns to lines
        // outside it (consumed via opts.alignWindow set by formatClarionRange).
        alignScope: 'global',
        // Keyword case ("Complete" feature): keywords/attributes/built-in types vs other names.
        // keywordCase: 'upper' | 'lower' | 'asis'   (John: upper)
        // otherNameCase: 'upper' | 'lower' | 'asis' (John: asis = As declared)
        keywordCase: 'upper',
        otherNameCase: 'asis'
    };

    // ---- helpers ----
    function toSet(a) { var s = Object.create(null); for (var i = 0; i < a.length; i++) s[a[i]] = true; return s; }
    function detectEol(t) { return /\r\n/.test(t) ? '\r\n' : '\n'; }
    function rtrim(s) { return s.replace(/\s+$/, ''); }
    function snapUpTab(c, t) { return Math.ceil(c / t) * t; }

    function stripComment(line) {
        var inStr = false;
        for (var i = 0; i < line.length; i++) {
            var ch = line.charAt(i);
            if (ch === "'") { if (inStr && line.charAt(i + 1) === "'") { i++; continue; } inStr = !inStr; }
            else if (ch === '!' && !inStr) return line.slice(0, i);
        }
        return line;
    }
    function isFullComment(t) { return t.charAt(0) === '!'; }
    function isCol1Comment(raw) { return raw.charAt(0) === '!'; }
    function startsInCol1(raw) { var c = raw.charAt(0); return c !== '' && c !== ' ' && c !== '\t'; }
    function leadingKeyword(code) { var m = /^\s*([A-Za-z_][A-Za-z0-9_]*)/.exec(code); return m ? m[1].toUpperCase() : ''; }

    // The structure/keyword driving a line (uppercased) or ''. If the line begins (column 1) with a
    // non-keyword identifier followed by whitespace, that's a LABEL and the keyword is the next token;
    // otherwise the first token itself is the keyword (handles unlabeled structures like "MENUBAR,USE").
    function structureWord(raw) {
        var code = stripComment(raw);
        var m = /^\s*([A-Za-z_][A-Za-z0-9_:.]*)/.exec(code);
        if (!m) return '';
        var firstTok = m[1].toUpperCase();
        if (startsInCol1(raw) && !KEYWORD_SET[firstTok]) {
            var lm = /^([A-Za-z_][A-Za-z0-9_:.]*)\s+(.*)$/.exec(code);
            return lm ? leadingKeyword(lm[2]) : '';   // label + keyword, or label-only
        }
        return firstTok;
    }
    function splitLabel(s) {
        var m = /^([A-Za-z_][A-Za-z0-9_:]*)(\s+)(\S.*)$/.exec(s);
        return m ? { label: m[1], rest: m[3] } : null;
    }
    // A label + type/structure declaration (column 1, non-keyword label, not an assignment/call).
    function isDataDecl(raw) {
        if (!startsInCol1(raw)) return false;
        var code = stripComment(raw), first = leadingKeyword(code);
        if (!first || KEYWORD_SET[first]) return false;
        var sp = splitLabel(rtrim(code));
        if (!sp) return false;
        var r = sp.rest.charAt(0);
        return r !== '=' && r !== '(';
    }
    function isOneLineStructure(code) { return /\.\s*$/.test(code) && /\bTHEN\b/i.test(code); }
    function endsWithContinuation(code) { return /[|&]\s*$/.test(rtrim(code)); }

    function pad(col, opts) {
        if (col <= 0) return '';
        if (opts.insertSpaces) return new Array(col + 1).join(' ');
        var tabs = Math.floor(col / opts.tabSize), rem = col % opts.tabSize;
        return new Array(tabs + 1).join('\t') + new Array(rem + 1).join(' ');
    }
    function mergeOptions(o) { var r = {}; for (var k in DEFAULTS) r[k] = DEFAULTS[k]; if (o) for (var j in o) if (o[j] !== undefined) r[j] = o[j]; return r; }

    // ---------------------------------------------------------------------------------------------
    // Keyword case normalization (the "Complete" feature: keywords/attrs/types one case, other names
    // as-declared). Context-aware so variables/labels named like keywords (Name, Date, Group) are NOT
    // touched. ALWAYS words are statement/control/operator words that can never be a variable name —
    // cased wherever they appear. POSITIONAL words (types / structures / attributes) are cased ONLY in
    // a keyword position: the first significant token of a line, or a token right after a top-level comma
    // (declaration/control attribute list). Strings and comments are never touched.
    // ---------------------------------------------------------------------------------------------
    // Statement/control/operator words that can NEVER be a variable name → safe to case anywhere,
    // including live as-you-type (the editor imports this list so both share one source of truth).
    var ALWAYS_CASE_WORDS = [
        'IF', 'THEN', 'ELSE', 'ELSIF', 'CASE', 'OF', 'OROF', 'LOOP', 'WHILE', 'UNTIL', 'EXIT', 'RETURN',
        'CYCLE', 'BREAK', 'DO', 'BEGIN', 'EXECUTE', 'END', 'ACCEPT', 'AND', 'OR', 'XOR', 'NOT', 'CHOOSE',
        'CODE', 'DATA', 'ROUTINE', 'PROCEDURE', 'FUNCTION', 'PROGRAM', 'MEMBER', 'MAP', 'MODULE',
        'CLASS', 'INTERFACE', 'NEW', 'DISPOSE', 'SELF', 'PARENT', 'NULL', 'TRUE', 'FALSE', 'TO', 'BY', 'TIMES',
        'INCLUDE', 'OMIT', 'COMPILE', 'PRAGMA', 'SECTION'   // directives: statement-position keywords, never names
    ];
    var ALWAYS_CASE = toSet(ALWAYS_CASE_WORDS);
    var POSITIONAL_CASE = toSet([
        // built-in data types
        'LONG', 'ULONG', 'SHORT', 'USHORT', 'BYTE', 'SIGNED', 'UNSIGNED', 'REAL', 'SREAL', 'DECIMAL',
        'PDECIMAL', 'BFLOAT4', 'BFLOAT8', 'STRING', 'CSTRING', 'PSTRING', 'ASTRING', 'MEMO', 'BLOB',
        'DATE', 'TIME', 'BOOL', 'ANY', 'LONGLONG', 'BIGINT',
        // structures
        'GROUP', 'QUEUE', 'RECORD', 'FILE', 'VIEW', 'WINDOW', 'REPORT', 'MENU', 'MENUBAR', 'TOOLBAR',
        'SHEET', 'TAB', 'OPTION', 'APPLICATION', 'DETAIL', 'HEADER', 'FOOTER', 'FORM', 'ITEM', 'ITEMIZE',
        'JOIN', 'OLE',
        // attributes / directives
        'PRE', 'NAME', 'DIM', 'OVER', 'STATIC', 'THREAD', 'TYPE', 'LIKE', 'DLL', 'PRIVATE', 'PROTECTED',
        'VIRTUAL', 'DERIVED', 'PROC', 'RAW', 'PASCAL', 'C', 'BINDABLE', 'EXTERNAL', 'AUTO', 'ONCE',
        'STD', 'USE', 'AT', 'MSG', 'TIP', 'ICON', 'FONT', 'COLOR', 'KEY', 'CURSOR', 'FROM', 'FORMAT',
        'REQ', 'IMM', 'RESIZE', 'CENTER', 'MAX', 'MDI', 'SYSTEM', 'STATUS', 'MODAL', 'SEPARATOR', 'EQUATE'
    ]);

    function applyCase(word, mode) { return mode === 'lower' ? word.toLowerCase() : word.toUpperCase(); }

    // Case-normalize a code string (no leading indent assumptions). Walks identifier tokens outside
    // string literals; leaves the rest untouched. kwPos = true when the FIRST token is a keyword
    // position (declaration type slot / structure line) rather than an lvalue/object (statement).
    function normalizeCode(code, opts, kwPos) {
        var out = '', i = 0, depth = 0, inStr = false, tokenIdx = -1, prevNonSpace = '';
        while (i < code.length) {
            var ch = code.charAt(i);
            if (inStr) { out += ch; if (ch === "'") { if (code.charAt(i + 1) === "'") { out += "'"; i += 2; continue; } inStr = false; } i++; continue; }
            if (ch === "'") { inStr = true; out += ch; i++; continue; }
            if (/[A-Za-z_]/.test(ch)) {
                var word = /^[A-Za-z_][A-Za-z0-9_]*/.exec(code.slice(i))[0];
                tokenIdx++;
                var upper = word.toUpperCase();
                var eligiblePos = (tokenIdx === 0 && kwPos) || (prevNonSpace === ',' && depth === 0);
                var isKeyword = ALWAYS_CASE[upper] || (POSITIONAL_CASE[upper] && eligiblePos);
                var newWord = word;
                if (isKeyword) { if (opts.keywordCase !== 'asis') newWord = applyCase(word, opts.keywordCase); }
                else { if (opts.otherNameCase !== 'asis') newWord = applyCase(word, opts.otherNameCase); }
                out += newWord; prevNonSpace = word.charAt(word.length - 1); i += word.length; continue;
            }
            if (ch === '(' || ch === '[' || ch === '{') depth++;
            else if (ch === ')' || ch === ']' || ch === '}') depth--;
            out += ch;
            if (ch !== ' ' && ch !== '\t') prevNonSpace = ch;
            i++;
        }
        return out;
    }
    // Case-normalize the code part of a content string, preserving any trailing comment.
    function normalizeContent(text, opts, kwPos) {
        if (opts.keywordCase === 'asis' && opts.otherNameCase === 'asis') return text;
        var code = stripComment(text);
        return normalizeCode(code, opts, kwPos) + text.slice(code.length);
    }

    // ---------------------------------------------------------------------------------------------
    // PASS 1 — classify. Tracks scope (module / procData / procCode / routData / routCode), a
    // structure stack (ids), continuation runs, and a per-line category. Computes columns for all
    // non-DATA categories immediately; DATA columns are resolved in pass 2 (they need group context).
    // ---------------------------------------------------------------------------------------------
    function classify(lines, opts, startBase) {
        var T = opts.tabSize;
        var P = snapUpTab(opts.preferredColumn - 1, T);   // preferred column (0-based), snapped to a tab stop
        var codeBase = startBase + T;                      // first code-indent level
        var procDataBase = P + T;                          // procedure local-data column

        var recs = [];
        var stack = [];                  // { id, kind:'code'|'data', col, bodyCol }
        var colOfStruct = {};            // structId -> column its END aligns to (code: now; data: pass 2)
        var meta = {};                   // structId -> { enclosingId }
        var nextId = 1, procInst = 0;
        var section = 'module';
        var routinePending = false;      // just saw ROUTINE; next decides routData vs routCode
        var continuing = false;          // previous logical line ended with a continuation token

        function top() { return stack.length ? stack[stack.length - 1] : null; }
        function codeBodyCol() { var t = top(); return (t && t.kind === 'code') ? t.bodyCol : codeBase; }
        function enclosingId() { return stack.length ? stack[stack.length - 1].id : 0; }

        for (var i = 0; i < lines.length; i++) {
            var raw = lines[i];
            var trimmed = raw.trim();
            var rec = { raw: raw, trimmed: trimmed, cont: continuing };

            if (trimmed === '') { continuing = false; rec.cat = 'blank'; recs.push(rec); continue; }   // a blank ends a continuation run

            var code = stripComment(raw);

            // Continuation line: emit relative to the statement it continues; do not change structure/scope.
            if (continuing) {
                rec.cat = 'cont';
                recs.push(rec);
                continuing = endsWithContinuation(code);
                continue;
            }

            if (isFullComment(trimmed)) {
                rec.cat = 'comment';
                rec.col1 = isCol1Comment(raw);
                rec.bodyCol = codeBodyCol();
                recs.push(rec);
                continue;   // comments never set the continuation flag
            }

            var first = leadingKeyword(code);
            var sw = structureWord(raw);

            // ---- scope transitions (only at top level — PROCEDURE inside a MAP is a prototype) ----
            if (stack.length === 0) {
                if (sw === 'PROCEDURE' || sw === 'FUNCTION' || first === 'PROCEDURE' || first === 'FUNCTION') {
                    procInst++; section = 'procData'; routinePending = false;
                    emitHeader(rec, code, P);
                    finish(rec, code); continue;
                }
                if (sw === 'ROUTINE' || first === 'ROUTINE') {
                    procInst++; routinePending = true; section = 'routCode';
                    emitHeader(rec, code, P);
                    finish(rec, code); continue;
                }
            }
            if (first === 'CODE' && stack.length === 0) {
                section = (section === 'routData' || section === 'routCode') ? 'routCode' : 'procCode';
                routinePending = false;
                rec.cat = 'plain'; rec.col = codeBase;
                finish(rec, code); continue;
            }
            if (first === 'DATA' && stack.length === 0 && routinePending) {
                section = 'routData'; routinePending = false;
                rec.cat = 'plain'; rec.col = codeBase;
                finish(rec, code); continue;
            }
            if (routinePending) { routinePending = false; }  // first non-DATA line after ROUTINE ⇒ code

            // ---- closers ----
            if (first === 'END' || trimmed === '.') {
                var closed = stack.pop();
                rec.cat = 'close';
                rec.closedId = closed ? closed.id : 0;
                recs.push(rec);
                finish(rec, code); continue;
            }
            // ---- mid keywords (align to enclosing code structure) ----
            if (MID_SET[first]) {
                var t = top();
                rec.cat = 'plain';
                rec.col = (t && t.kind === 'code') ? t.col : codeBodyCol();
                finish(rec, code); continue;
            }

            // ---- preferred keywords (module-level): MEMBER/PROGRAM/MAP/PRAGMA/SECTION ----
            if (PREFERRED_SET[first] && stack.length === 0) {
                var pkCol = opts.preferredKeywordIndent ? codeBase : P;   // one indent vs preferred column
                rec.cat = 'plain'; rec.col = pkCol;
                if (DATASTRUCT_SET[first] && !isOneLineStructure(code)) openStruct(rec, 'data', pkCol);
                finish(rec, code); continue;
            }

            // ---- DATA scope: declarations + unlabeled structure/control members ----
            var inData = section === 'procData' || section === 'routData' || (section === 'module' && isDataDecl(raw));
            if (inData) {
                rec.enclosingId = enclosingId();
                rec.procInst = procInst;
                rec.section = section;
                if (isDataDecl(raw)) {
                    var sp = splitLabel(rtrim(code));
                    var rawsp = splitLabel(rtrim(raw));   // rest from raw keeps any trailing comment
                    rec.cat = 'decl'; rec.label = sp.label; rec.rest = rawsp ? rawsp.rest : sp.rest; rec.labelLen = sp.label.length;
                } else {
                    rec.cat = 'dataline'; rec.labelLen = 0;   // unlabeled structure/control line (MENUBAR/ITEM/…)
                }
                if (DATASTRUCT_SET[sw] && !isOneLineStructure(code)) { rec.opensData = true; rec.structId = nextIdPeek(); openStruct(rec, 'data', null); }
                recs.push(rec);
                finish(rec, code); continue;
            }

            // ---- CODE scope: executable statements ----
            // "Treat colon-terminated word as label" (colonAsLabel, default off): a bare identifier that
            // ends with ':' is a CODE-section statement label (e.g. a GOTO target) — pin it to column 1
            // instead of indenting it as a statement. Field equates (PRE:Field) carry the colon mid-token
            // and never match, so they are unaffected.
            if (opts.colonAsLabel && /^[A-Za-z_][A-Za-z0-9_]*:$/.test(stripComment(raw).trim())) {
                rec.cat = 'stmt'; rec.col = 0;
                finish(rec, code); continue;
            }
            rec.cat = 'stmt';
            rec.col = codeBodyCol();
            if (CONTROL_SET[first] && !isOneLineStructure(code)) openStruct(rec, 'code', rec.col, first);
            finish(rec, code); continue;
        }

        return { recs: recs, colOfStruct: colOfStruct, meta: meta, consts: { T: T, P: P, codeBase: codeBase, procDataBase: procDataBase, startBase: startBase } };

        // -- helpers closed over the walk state --
        function nextIdPeek() { return nextId; }
        function openStruct(rec, kind, col, opener) {
            var id = nextId++;
            meta[id] = { enclosingId: enclosingId() };
            if (kind === 'code') {
                // "Indent from opener" toggles (deviate from native default, both OFF by default):
                //   indentFromCode      -> CASE/IF: OF/OROF/ELSE/ELSIF +1 tab, bodies +2 tabs, END stays at opener.
                //   indentCaseSubKeywords -> extend the same treatment to the non-mid structures
                //                            (LOOP/EXECUTE/ACCEPT/BEGIN) so their bodies also indent +2 tabs.
                // END always aligns to the opener column (colOfStruct[id] = col) regardless.
                var indentFrom =
                    (opts.indentFromCode && (opener === 'IF' || opener === 'CASE')) ||
                    (opts.indentCaseSubKeywords && (opener === 'LOOP' || opener === 'EXECUTE' || opener === 'ACCEPT' || opener === 'BEGIN'));
                var midCol = indentFrom ? col + T : col;       // OF/OROF/ELSE/ELSIF column
                var bodyCol = indentFrom ? col + 2 * T : col + T;
                colOfStruct[id] = col;
                stack.push({ id: id, kind: 'code', col: midCol, bodyCol: bodyCol });
            }
            else { stack.push({ id: id, kind: 'data', col: null, bodyCol: null }); }   // data col resolved in pass 2
            rec.opensId = id;
        }
        function emitHeader(rec, code, p) {
            var sp = splitLabel(rtrim(raw));
            if (sp) { rec.cat = 'header'; rec.label = sp.label; rec.rest = sp.rest; rec.col = p; }
            else { rec.cat = 'plain'; rec.col = p; }   // bare PROCEDURE (no label)
        }
        function finish(rec, code) {
            if (rec.cat !== 'comment' && rec.cat !== 'blank') { if (recsLast() !== rec) recs.push(rec); }
            continuing = endsWithContinuation(code);
        }
        function recsLast() { return recs.length ? recs[recs.length - 1] : null; }
    }

    // ---------------------------------------------------------------------------------------------
    // PASS 2 — resolve DATA columns. Group decl/dataline recs by (enclosingId, procInst); a group's
    // column = max(snapUpTab(longestLabel+1), base). base = procDataBase for top-level procedure data,
    // startBase for routine/standalone, or parentHeaderCol+tab for a nested struct. Shallow→deep so a
    // child can read its parent header column.
    // ---------------------------------------------------------------------------------------------
    function resolveDataCols(cls) {
        var recs = cls.recs, meta = cls.meta, colOfStruct = cls.colOfStruct;
        var T = cls.consts.T, procDataBase = cls.consts.procDataBase, startBase = cls.consts.startBase;
        var groups = {};
        for (var i = 0; i < recs.length; i++) {
            var r = recs[i];
            if (r.cat !== 'decl' && r.cat !== 'dataline') continue;
            var key = r.enclosingId + '#' + r.procInst;
            var g = groups[key] || (groups[key] = { max: 0, items: [], enclosingId: r.enclosingId, section: r.section });
            if (r.labelLen > g.max) g.max = r.labelLen;
            g.items.push(i);
        }
        function enclDepth(id) { var d = 0, c = id; while (c && meta[c]) { d++; c = meta[c].enclosingId; } return d; }
        var keys = Object.keys(groups).sort(function (a, b) { return enclDepth(groups[a].enclosingId) - enclDepth(groups[b].enclosingId); });
        keys.forEach(function (k) {
            var g = groups[k], base;
            if (g.enclosingId && meta[g.enclosingId]) base = (colOfStruct[g.enclosingId] != null ? colOfStruct[g.enclosingId] : startBase) + T;
            else base = (g.section === 'procData') ? procDataBase : startBase;
            var col = Math.max(snapUpTab(g.max + 1, T), base);
            g.items.forEach(function (ri) {
                recs[ri].dataCol = col;
                if (recs[ri].opensId) colOfStruct[recs[ri].opensId] = col;   // this line opens a data struct
            });
        });
    }

    // ---------------------------------------------------------------------------------------------
    // PASS 3 — emit text.
    // ---------------------------------------------------------------------------------------------
    function emit(cls, opts) {
        var recs = cls.recs, colOfStruct = cls.colOfStruct;
        var T = cls.consts.T, codeBase = cls.consts.codeBase;
        var out = [], lastCol = codeBase;
        for (var i = 0; i < recs.length; i++) {
            var r = recs[i], line;
            switch (r.cat) {
                case 'blank': out.push(''); break;
                case 'comment':
                    if (r.col1 && opts.dontIndentCol1Comments) line = r.trimmed;
                    else if (!opts.indentComments) line = r.trimmed;
                    else line = pad(r.bodyCol, opts) + r.trimmed;
                    out.push(line); break;   // comments are never case-normalized
                case 'cont':
                    out.push(pad(lastCol + opts.contLineMultiplier * T, opts) + nc(r.trimmed, true)); break;   // continued attribute list
                case 'close': {
                    var c = colOfStruct[r.closedId]; if (c == null) c = lastCol;
                    out.push(pad(c, opts) + nc(r.trimmed, false)); lastCol = c; break;
                }
                case 'header':
                    out.push(label(r.label) + gap(r.label.length, r.col) + nc(r.rest, true)); lastCol = r.col; break;
                case 'decl': {
                    var dc = r.dataCol != null ? r.dataCol : r.col || 0;
                    out.push(label(r.label) + gap(r.label.length, dc) + nc(r.rest, true)); lastCol = dc; break;
                }
                case 'dataline': {
                    var dl = r.dataCol != null ? r.dataCol : 0;
                    out.push(pad(dl, opts) + nc(r.trimmed, true)); lastCol = dl; break;
                }
                case 'plain':
                case 'stmt':
                default:
                    out.push(pad(r.col || 0, opts) + nc(r.trimmed, false)); lastCol = r.col || 0; break;
            }
        }
        return out;
        function gap(labelLen, col) { var n = col - labelLen; if (n < 1) n = 1; return new Array(n + 1).join(' '); }
        function nc(s, kwPos) { return normalizeContent(s, opts, kwPos); }
        // A label is an "other name" — cased only when otherNameCase is set (default As declared = leave).
        function label(s) { return opts.otherNameCase === 'asis' ? s : applyCase(s, opts.otherNameCase); }
    }

    function formatLines(lines, opts, startBase) {
        var cls = classify(lines, opts, startBase || 0);
        resolveDataCols(cls);
        var out = emit(cls, opts);
        if (opts.alignAssignments) out = alignAssignments(out, opts);
        return out;
    }

    // ---------------------------------------------------------------------------------------------
    // Assignment alignment (post-pass). Folds the Upper Park "Format Assignment" feature into Ctrl+I.
    // ---------------------------------------------------------------------------------------------
    // Locate the top-level assignment operator in a code string. Returns {pos, op} or null. Skips
    // operators inside strings or ()/[]/{} nesting, and rejects comparisons (==, <=, >=, !=).
    function findAssignOp(code) {
        var depth = 0, inStr = false;
        for (var i = 0; i < code.length; i++) {
            var ch = code.charAt(i);
            if (ch === "'") { if (inStr && code.charAt(i + 1) === "'") { i++; continue; } inStr = !inStr; continue; }
            if (inStr) continue;
            if (ch === '(' || ch === '[' || ch === '{') { depth++; continue; }
            if (ch === ')' || ch === ']' || ch === '}') { depth--; continue; }
            if (depth !== 0) continue;
            if ('+-*/^%&'.indexOf(ch) >= 0 && code.charAt(i + 1) === '=') return { pos: i, op: code.substr(i, 2) };
            if (ch === '=') {
                if (code.charAt(i + 1) === '=') { i++; continue; }            // == comparison
                var prev = code.charAt(i - 1);
                if (prev === '<' || prev === '>' || prev === '!') continue;   // <= >= != comparison
                return { pos: i, op: '=' };
            }
        }
        return null;
    }
    // Parse one line as an assignment statement, or null. right keeps any trailing inline comment.
    function parseAssign(line) {
        var t = line.trim();
        if (t === '' || isFullComment(t)) return null;
        var code = stripComment(line);
        if (KEYWORD_SET[leadingKeyword(code)]) return null;   // IF/CASE/RETURN/DO/… are not assignments
        var found = findAssignOp(code);
        if (!found) return null;
        var indent = /^\s*/.exec(line)[0];
        var left = code.slice(indent.length, found.pos).replace(/\s+$/, '');
        if (left === '') return null;
        var right = line.slice(found.pos + found.op.length).replace(/^\s+/, '');
        return { indent: indent, indentLen: indent.length, left: left, op: found.op, right: right };
    }
    function alignAssignments(lines, opts) {
        var res = lines.slice();
        // 'selection' scope confines alignment to the formatted window: never read or modify lines
        // outside it, so a partial selection aligns only to itself. 'global' (default) uses the whole buffer.
        var lo = 0, hi = res.length - 1;
        if (opts.alignScope === 'selection' && opts.alignWindow) {
            lo = Math.max(0, opts.alignWindow[0]);
            hi = Math.min(res.length - 1, opts.alignWindow[1]);
        }
        var i = lo;
        while (i <= hi) {
            var a = parseAssign(res[i]);
            if (!a) { i++; continue; }
            var block = [{ idx: i, a: a }];
            var j = i + 1;
            for (; j <= hi; j++) {
                var t = res[j].trim();
                if (t === '') { if (opts.treatBlankAsContiguous) continue; else break; }
                if (isFullComment(t)) { if (opts.treatCommentAsContiguous) continue; else break; }
                var aj = parseAssign(res[j]);
                if (!aj) break;
                block.push({ idx: j, a: aj });
            }
            var maxPrefix = 0;
            block.forEach(function (b) { maxPrefix = Math.max(maxPrefix, b.a.indentLen + b.a.left.length); });
            var opCol = maxPrefix + opts.spacesBeforeAssignment;
            var after = new Array(opts.spacesAfterAssignment + 1).join(' ');
            block.forEach(function (b) {
                var x = b.a, gap = opCol - (x.indentLen + x.left.length);
                if (gap < 1) gap = 1;
                res[b.idx] = x.indent + x.left + new Array(gap + 1).join(' ') + x.op + after + x.right;
            });
            i = (j > i ? j : i + 1);
        }
        return res;
    }

    // ---- public entry points ----
    function formatClarion(text, options) {
        var opts = mergeOptions(options);
        var eol = detectEol(text);
        return { text: formatLines(text.split(/\r\n|\n/), opts, 0).join(eol), eol: eol };
    }
    // Format lines [startLine, endLine] (1-based inclusive) IN FULL-BUFFER CONTEXT, then return the
    // whole text with only those lines replaced. Formatting the entire buffer (not just the window) is
    // what lets a single line look UP at its enclosing structure: an OF/ELSE aligns to its CASE/IF, an
    // END to its opener, a data member to its group — none of which are visible in an isolated window.
    function formatClarionRange(text, startLine, endLine, options) {
        var opts = mergeOptions(options);
        var eol = detectEol(text);
        var lines = text.split(/\r\n|\n/);
        // In 'selection' scope, confine assignment alignment to the formatted window (0-based, inclusive).
        if (opts.alignScope === 'selection') opts.alignWindow = [startLine - 1, endLine - 1];
        var full = formatLines(lines, opts, 0);                 // format the whole buffer with full context
        var result = lines.slice();
        for (var i = startLine - 1; i < endLine && i < full.length; i++) result[i] = full[i];
        return { text: result.join(eol), eol: eol };
    }

    var api = {
        formatClarion: formatClarion,
        formatClarionRange: formatClarionRange,
        CONTROL_OPENERS: CONTROL_OPENERS, DATA_STRUCT_OPENERS: DATA_STRUCT_OPENERS,
        MID: MID, PREFERRED_KEYWORDS: PREFERRED_KEYWORDS, DEFAULTS: DEFAULTS,
        ALWAYS_KEYWORDS: ALWAYS_CASE_WORDS,   // safe-to-case-anywhere set, for live as-you-type
        applyCase: function (word, mode) { return applyCase(word, mode); },
        _internal: { stripComment: stripComment, isDataDecl: isDataDecl, splitLabel: splitLabel, classify: classify }
    };
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    root.ClarionFormatter = api;
})(typeof self !== 'undefined' ? self : (typeof globalThis !== 'undefined' ? globalThis : this));
