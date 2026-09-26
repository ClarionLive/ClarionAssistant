import json, re, os, sqlite3, collections
from depth import strip_comment

cat = json.load(open('unused_globals.json'))
rowsall = [(k, r) for k in ('scalar','struct','equate') for r in cat[k]]
names = sorted({r[1] for _, r in rowsall})
pat = {n: re.compile(r'(^|[^A-Za-z0-9_:])' + re.escape(n) + r'([^A-Za-z0-9_:]|$)', re.I) for n in names}
kind_of = {}
for k, r in rowsall:
    kind_of.setdefault(r[1], k)

con = sqlite3.connect(r"file:H:\Dev\aPOSitive\v61POSitive.codegraph.db?mode=ro", uri=True)
indexed = {os.path.normcase(p) for (p,) in
           con.execute("SELECT resolved_path FROM indexed_files WHERE outcome IN ('resolved_parsed','resolved_no_symbols') AND resolved_path IS NOT NULL")}
declset = {(os.path.normcase(r[2]), r[3]) for _, r in rowsall}

uses = collections.defaultdict(list)
for raw in open('hits2.txt', encoding='latin-1', errors='replace'):
    m = re.match(r'^(.*?):(\d+):(.*)$', raw.rstrip('\n'))
    if not m: continue
    f, ln, txt = m.group(1), int(m.group(2)), m.group(3)
    full = os.path.normcase(os.path.join(r'H:\Dev\aPOSitive', f))
    if full not in indexed:  continue
    if (full, ln) in declset: continue
    code = strip_comment(txt)
    if not code.strip(): continue
    head = code.split(None, 1)
    for n in names:
        if not pat[n].search(code): continue
        if code[:1].strip() != '' and head and head[0].lower() == n.lower():
            continue                      # a declaration line in another app (EXTERNAL import)
        uses[n].append((f, ln, code.strip()[:110]))

confirmed = [n for n in names if not uses[n]]
missed    = [n for n in names if uses[n]]

def bucket(n):
    if n.lower().endswith('::used'): return 'file ::Used counter'
    return kind_of[n]

print(f"candidate names           : {len(names)}")
print(f"CONFIRMED dead (grep=0)   : {len(confirmed)}")
print(f"index missed real uses    : {len(missed)}")
print()
print("--- confirmed dead, by kind ---")
for k, c in collections.Counter(bucket(n) for n in confirmed).most_common():
    print(f"{c:5d}  {k}")
print("\n--- index misses, by kind ---")
for k, c in collections.Counter(bucket(n) for n in missed).most_common():
    print(f"{c:5d}  {k}")

json.dump({"confirmed": confirmed, "missed": {n: uses[n][:4] for n in missed}},
          open('verified2.json','w'), indent=1)
