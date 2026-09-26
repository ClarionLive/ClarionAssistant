import json, os, collections, re
dead = json.load(open('dead_final.json'))
declared = json.load(open('declared.json'))
decl_apps = collections.defaultdict(set)
for name, path, line, kind, isext, text in declared:
    decl_apps[name.lower()].add(os.path.basename(path))

def cat(r):
    if r[0].lower().endswith('::used'): return 'file-counter'
    return r[3]

byapp = collections.defaultdict(list)
for r in dead: byapp[os.path.basename(r[1])].append(r)

L = []
A = L.append
A("# Unused globals - v61POSitive solution\n")
A(f"Indexed {len(byapp)} of 27 apps' PROGRAM files. **{len(declared):,}** top-level declarations "
  f"({len([r for r in declared if not r[4]]):,} owned, {len([r for r in declared if r[4]]):,} EXTERNAL imports).\n")
A(f"**{len(dead):,} owned declarations ({len({r[0].lower() for r in dead}):,} distinct names) are never "
  f"referenced anywhere** - not in any of the 27 apps' generated source, not in the shared class libraries "
  f"(h:\dev\SourceV61\Classes, h:\dev\Source\Classes, SharedClasses, SharedLibsrc).\n")
for k, c in collections.Counter(cat(r) for r in dead).most_common():
    A(f"- **{c:,}** {k}")
A("")
A("## Method\n")
A("1. Parsed all 27 PROGRAM `.clw` files, tracking FILE/RECORD/GROUP/QUEUE/CLASS nesting, and kept only")
A("   declarations at nesting depth 0 - record fields and KEYs are *not* globals.")
A("2. Dropped every `EXTERNAL,DLL(...)` line: those are imports, the owner is elsewhere.")
A("3. Scanned all 2,806 source files the solution compiles, stripped Clarion comments, and collected every")
A("   identifier appearing in a non-label position. Text inside quotes IS counted as a use, so anything")
A("   reached by BIND/EVALUATE by name is treated as live.")
A("4. A global is dead when its name never appears in that set.\n")
A("Cross-checked against the CodeGraph index: of these declarations only 4 have index reference edges, and")
A("all 4 are index artifacts - `gb:gbThread` matched to `gbThread`, and three names that appear only in")
A("commented-out code. The index's own zero-reference list is *less* conservative than this one.\n")
A("## Caveats\n")
A("- **Other solutions.** `H:\Dev\aPOSitive` also holds POSitiveAnywhere, POSitiveDB, POSitiveAccountingLink")
A("  and others that are not in this .sln. If any of them link these DLLs, a global that is dead here may be")
A("  live there. Confirm before deleting anything exported from PRMBase/PRMMlti/PRMPbase.")
A("- **The `-Eliza` files** (PRMBase-Eliza.clw, PRM006-Eliza-2.clw, ~77 files) are not in any project and were")
A("  excluded. They contain 4,393 matches for these names - they are old copies, not live code.")
A("- **`::Used` counters** are Legacy-template plumbing: `X::Used` tracks open/close nesting for FILE `X`. A")
A("  dead counter means no app generated open/close code for that file. All 139 of those FILE labels are")
A("  still referenced elsewhere, so treat this bucket as informational.")
A("- Deleting these is an .app-side edit (global data / global embeds), not a .clw edit - generated source")
A("  regenerates.\n")
A("## By app\n")
A("| app | dead | scalars | equates | file-counters | structs |")
A("|---|---:|---:|---:|---:|---:|")
for f, rs in sorted(byapp.items(), key=lambda kv: -len(kv[1])):
    c = collections.Counter(cat(r) for r in rs)
    A(f"| {f} | {len(rs)} | {c['scalar']} | {c['equate']} | {c['file-counter']} | {c['struct']} |")
A("")
hand = [r for r in dead if cat(r) == 'scalar']
A(f"## Unused scalar globals ({len(hand)})\n")
for f, rs in sorted(byapp.items(), key=lambda kv: -len(kv[1])):
    rs = [r for r in rs if cat(r) == 'scalar']
    if not rs: continue
    A(f"### {f}  ({len(rs)})\n")
    A("```")
    for r in sorted(rs, key=lambda r: r[2]):
        n = len(decl_apps[r[0].lower()]) - 1
        A(f"{r[2]:>6}  {r[5].strip()[:92]}" + (f"   [+{n} import(s)]" if n > 0 else ""))
    A("```\n")
for label, kind in (("Unused EQUATEs", 'equate'), ("Unused structures (QUEUE/GROUP/CLASS)", 'struct'),
                    ("Unused file ::Used counters", 'file-counter')):
    rs_all = [r for r in dead if cat(r) == kind]
    A(f"## {label} ({len(rs_all)})\n")
    for f in sorted({os.path.basename(r[1]) for r in rs_all}):
        rs = sorted([r for r in rs_all if os.path.basename(r[1]) == f], key=lambda r: r[2])
        A(f"### {f}  ({len(rs)})\n")
        A("```")
        for r in rs: A(f"{r[2]:>6}  {r[5].strip()[:92]}")
        A("```\n")
out = r"H:\Dev\aPOSitive\unused-globals.md"
open(out, 'w', encoding='utf-8').write('\n'.join(L))
print("written:", out, os.path.getsize(out), "bytes")
