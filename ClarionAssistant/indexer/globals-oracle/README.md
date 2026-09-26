# globals-oracle

Independent, index-free detector for unused Clarion globals. Written while auditing
v61POSitive; it exists because the CodeGraph index could not answer the question
(see the ticket "CodeGraph indexer: record fields indexed as globals").

Use it as the ORACLE when fixing the indexer: the indexer's answer should converge
on this one.

## Run order

    python globals.py     # parse the 27 PROGRAM .clw -> declared.json  (needs used_zones.json first)
    python used2.py       # scan every compiled source file -> used_zones.json
    python globals2.py    # diff the two -> dead_final.json + console summary
    python cross.py       # compare against the CodeGraph index, print disagreements
    python report2.py     # write the markdown report

`depth.py` is the piece worth porting to C#: Clarion structure-nesting depth, with the
four traps that each silently corrupt the count (inline `.` terminators, continuation
lines whose label is on the FIRST physical line, BLOB not being a structure, and
double-colon identifiers).

Both DB paths are hardcoded to H:\Dev\aPOSitive\v61POSitive.codegraph.db.

## Baseline (v61POSitive, indexed 2026-08-17 16:36)

    owned top-level globals   6,314   (3,652 distinct names)
    dead declarations         1,708   (734 distinct names)
      equates                   969
      scalars                   563
      file ::Used counters      139
      structures                 37

`cross.py` reports 4 declarations where the index disagrees. All 4 were checked by hand
and all 4 are index artifacts, not real uses.
