import os, re, json, collections
from depth import depth_map, strip_comment

used = json.load(open('used_zones.json'))
declared = json.load(open('declared.json'))     # (name, path, line, kind, isext, text)

def status(n):
    z = used.get(n.lower())
    if not z: return 'dead'
    if 'app' in z or 'shared' in z: return 'used'
    return 'lib-only'

owned = [r for r in declared if not r[4]]
dead  = [r for r in owned if status(r[0]) == 'dead']
libonly = [r for r in owned if status(r[0]) == 'lib-only']
print(f"owned top-level globals      : {len(owned)}  ({len({r[0].lower() for r in owned})} distinct names)")
print(f"never used in app/shared code: {len(dead)}   ({len({r[0].lower() for r in dead})} distinct names)")
print(f"only seen in Clarion libsrc  : {len(libonly)}")
for k, c in collections.Counter(r[3] for r in dead).most_common():
    print(f"    {k:8}: {c}")

def cat(r):
    n = r[0]
    if n.lower().endswith('::used'): return 'file ::Used counter'
    if re.match(r'^(Sort:(Name|Alpha)|SaveError|SaveFileError|VCRRequest|GlobalRequest|GlobalResponse)', n, re.I): return 'template boilerplate'
    return r[3]
print("\n--- dead declarations by category ---")
for k, c in collections.Counter(cat(r) for r in dead).most_common():
    print(f"{c:5d}  {k}")
print("\n--- dead declarations by app ---")
for k, c in collections.Counter(os.path.basename(r[1]) for r in dead).most_common():
    print(f"{c:5d}  {k}")
json.dump(dead, open('dead_final.json','w'), indent=1)
