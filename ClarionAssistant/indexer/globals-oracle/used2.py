import sqlite3, os, re, json, time
from depth import strip_comment
con = sqlite3.connect(r"file:H:\Dev\aPOSitive\v61POSitive.codegraph.db?mode=ro", uri=True)
files = sorted({p for (p,) in con.execute(
  "SELECT DISTINCT resolved_path FROM indexed_files WHERE resolved_path IS NOT NULL") if p})
files = [p for p in files if p.lower().endswith(('.clw','.inc')) and os.path.exists(p)]
def zone(p):
    l = p.lower()
    if l.startswith('c:\clarion'): return 'lib'
    if l.startswith('h:\dev\aposi'): return 'app'
    return 'shared'
IDENT = re.compile(r'[A-Za-z_][A-Za-z0-9_]*(?::+[A-Za-z0-9_]+)*')
used = {}
t0=time.time()
for p in files:
    z = zone(p)
    try: txt = open(p, encoding='latin-1').read()
    except Exception: continue
    for raw in txt.split('\n'):
        code = strip_comment(raw)
        if not code.strip(): continue
        if code[:1].strip() != '':
            parts = code.split(None, 1)
            code = parts[1] if len(parts) > 1 else ''
        for m in IDENT.finditer(code):
            k = m.group(0).lower()
            used[k] = used.get(k, '') if z in used.get(k, '') else used.get(k, '') + z
print(f"files scanned {len(files)}  idents {len(used)}  ({time.time()-t0:.0f}s)")
json.dump(used, open('used_zones.json','w'))
