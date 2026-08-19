import re

STRUCT = re.compile(r'^(FILE|VIEW|RECORD|GROUP|QUEUE|CLASS|ITEMIZE|MAP|MODULE|JOIN)\b', re.I)

def strip_comment(s):
    out, q = [], None
    for c in s:
        if q:
            out.append(c)
            if c == q:
                q = None
        elif c in ("'",):
            q = c; out.append(c)
        elif c == '!':
            break
        else:
            out.append(c)
    return ''.join(out)

def depth_map(path):
    """line_number -> structure nesting depth BEFORE that line is applied."""
    with open(path, 'r', encoding='latin-1') as f:
        lines = f.read().split('\n')
    depth = 0
    result = {}
    pending = ''
    pending_labelled = None
    for idx, raw in enumerate(lines, 1):
        result[idx] = depth
        s = strip_comment(raw).rstrip()
        labelled = raw[:1].strip() != ''
        if pending:
            s = pending + ' ' + s.strip()
            pending = ''
            labelled = pending_labelled
        if s.endswith('|'):
            pending = s[:-1]
            pending_labelled = labelled
            continue
        body = s.strip()
        if not body:
            continue
        # trailing '.' terminators close structures
        closes = 0
        while body.endswith('.'):
            closes += 1
            body = body[:-1].rstrip()
        if re.match(r'^END\b', body, re.I):
            closes += 1
            body = body[3:].strip()
        opens = 0
        if body:
            if not labelled:
                rest = body                      # unlabelled statement
            else:
                m = re.match(r'^(\S+)\s*(.*)$', body)
                rest = m.group(2).strip()
            if STRUCT.match(rest):
                opens = 1
        depth = max(0, depth + opens - closes)
    return result
