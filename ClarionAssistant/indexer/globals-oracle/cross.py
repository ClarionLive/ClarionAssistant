import json, sqlite3, collections
dead = json.load(open('dead_final.json'))
con = sqlite3.connect(r"file:H:\Dev\aPOSitive\v61POSitive.codegraph.db?mode=ro", uri=True)
disagree = []
for name, path, line, kind, isext, text in dead:
    r = con.execute("SELECT id,(SELECT COUNT(*) FROM relationships x WHERE x.to_id=s.id) FROM symbols s WHERE s.name=? AND s.file_path=? AND s.line_number=?",(name,path,line)).fetchone()
    if r and r[1] > 0:
        disagree.append((name, path, line, r[0], r[1], text))
print(f"dead decls where index shows refs: {len(disagree)} / {len(dead)}")
for d in disagree[:10]:
    print(f"  {d[0]:30} refs={d[4]:3}  {d[5][:55]}")
    for fp, ln, tp in con.execute("SELECT file_path,line_number,type FROM relationships WHERE to_id=? LIMIT 3", (d[3],)):
        print(f"        <- {fp.split(chr(92))[-1]}:{ln} ({tp})")
