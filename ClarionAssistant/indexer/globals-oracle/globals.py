import sqlite3, os, re, json, collections
from depth import depth_map, strip_comment

con = sqlite3.connect(r"file:H:\Dev\aPOSitive\v61POSitive.codegraph.db?mode=ro", uri=True)
files = [p for (p,) in con.execute(
    "SELECT DISTINCT resolved_path FROM indexed_files WHERE outcome='resolved_parsed' AND resolved_path IS NOT NULL") if p]
files = [p for p in files if p.lower().endswith('.clw') and os.path.exists(p)]

PROG = re.compile(r'^\s*\S*\s*PROGRAM\s*$', re.I)
progs = []
for p in files:
    head = open(p, encoding='latin-1').read(4000).split('\n')
    if any(PROG.match(l) for l in head[:60]):
        progs.append(p)
print("PROGRAM files:", len(progs))
for p in sorted(progs): print("   ", os.path.basename(p))

used = set(json.load(open('used_idents.json')))
EXT  = re.compile(r'\bEXTERNAL\b', re.I)
EQU  = re.compile(r'\bEQUATE\b', re.I)
STRU = re.compile(r'\b(FILE|VIEW|QUEUE|GROUP|CLASS|ITEMIZE|MODULE)\b', re.I)

declared, dead = [], []
for p in sorted(progs):
    lines = open(p, encoding='latin-1').read().split('\n')
    d = depth_map(p)
    code_ln = next((i for i, l in enumerate(lines, 1) if re.match(r'^\s+CODE\s*$', l)), len(lines))
    for i in range(1, code_ln):
        raw = lines[i-1]
        if raw[:1].strip() == '' or d[i] != 0:
            continue
        code = strip_comment(raw).rstrip()
        m = re.match(r'^(\S+)\s+(.*)$', code)
        if not m: continue
        name, rest = m.group(1), m.group(2).strip()
        if not rest or re.match(r'^(PROGRAM|MAP|CODE|MEMBER|INCLUDE|END)\b', rest, re.I):
            continue
        if not re.match(r'^[A-Za-z_]', name):
            continue
        kind = ('equate' if EQU.search(rest) else
                'struct' if STRU.search(rest) else 'scalar')
        isext = bool(EXT.search(rest))
        declared.append((name, p, i, kind, isext, code.rstrip()))
        if name.lower() not in used:
            dead.append((name, p, i, kind, isext, code.rstrip()))

owners = [r for r in declared if not r[4]]
print(f"\ntop-level declarations in PROGRAM files : {len(declared)}")
print(f"   owned (not EXTERNAL)                 : {len(owners)}")
print(f"   EXTERNAL imports                     : {len(declared)-len(owners)}")
dead_own = [r for r in dead if not r[4]]
print(f"\nnever referenced anywhere in solution   : {len(dead)} declarations")
print(f"   of those, owned declarations         : {len(dead_own)}")
print(f"   distinct names                       : {len({r[0].lower() for r in dead_own})}")
for k, c in collections.Counter(r[3] for r in dead_own).most_common():
    print(f"      {k:8}: {c}")
json.dump(dead_own, open('dead_owned.json','w'), indent=1)
json.dump(declared, open('declared.json','w'))
