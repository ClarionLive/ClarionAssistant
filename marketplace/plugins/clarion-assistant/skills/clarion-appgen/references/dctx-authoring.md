# Dictionary Authoring (.dctx)

**Author dictionaries as `.dctx` (XML), not `.txd`.** The XML format is far easier to write than TXA and round-trips reliably.

```
ClarionCL /au /dx <dct> <file.dctx>     export — learn the shape from a real dictionary
ClarionCL /au /di <dct> <file.dctx>     create a .dct from text
```

`/di` and `/dx` select format by file **extension**.

## Why .dctx and not .txd

A `.dctx` round-trip is effectively byte-perfect. Northwind (32 tables / 261 fields / 63 keys / 14 GROUPs, MSSQL driver, BLOBs) came back with identical counts; re-export was 233,827 bytes against an original of 233,825. It preserved 14 `Over=` overlays, 13 `<Relation>` elements with all their mappings, 28 nested fields, `DriverOption="FM3IGNORE"`, and `KeyType="ORDER"` with its component.

The `.txd` path is a trap for a specific reason: **`/dx <dct> file.txd` emits a Report Writer TXD, not a dictionary-import TXD.** Two different formats share the extension. `/di` correctly refuses the file — then writes an empty 26,368-byte husk anyway and exits without a useful signal. The `/?` help text for `/di` ("either dctx or txd") is misleading; the TXD it means is not the TXD `/dx` produces.

*(Historical note for anyone reading older material: this was at one point mistakenly concluded to be a broken TXD importer. It is not — it is a format mismatch. Separately reported construct-level TXD bugs — `FM3IGNORE` truncating to `FM`, `GROUP`/`OVER` components dropped, `ORDER` components dropped — are against **hand-authored** dictionary TXDs and remain unreproduced. `.dctx` is still the right choice, just not for that reason.)*

## Structure rules

- **Every `Table`, `Field`, `Key` and `Component` needs a fresh GUID** and an `Ident` unique within the dictionary.
- **Keys bind to fields by `FieldId` = the FIELD'S GUID**, never by name.
- **`<Audit>` elements appear in exports but are not required on input.**
- **`Create="true"`** on a table makes the app create the `.tps` on first run — no data file needs to ship, which is what lets a whole app plus its schema be a few kilobytes of text.

## Overlays and groups

An overlay is attribute `Over="FieldName"` on a `<Field>` with `DataType="GROUP"`, carrying **nested `<Field>` children**.

Beware grepping for TXD syntax (`OVER(`) inside an XML file — that mistake once produced a false "no overlays present" census.

## Relations

`<Relation>` is a **sibling of `<Table>`** at Dictionary level, placed **after all tables**. Everything binds by GUID, never by name.

```xml
<Relation Guid PrimaryTable ForeignTable PrimaryKey ForeignKey>
    <ForeignMapping Guid Field="{foreign field GUID}"/>
    <PrimaryMapping Guid Field="{primary field GUID}"/>
</Relation>
```

**CRITICAL: `<ForeignMapping>` MUST come BEFORE `<PrimaryMapping>`.** Reversed, the ForeignMapping is **silently dropped** and `/di` still exits 0 — the round trip returns `PriMap=1 FrnMap=0`. XML is normally order-insensitive, which makes this a real trap when generating DCTX programmatically. Match the canonical order Clarion emits in any `/dx` export.

The element is `<Relation>`, not `<Relationship>`. `ORDER` keys use `KeyType="ORDER"`.

With correct ordering, a 3,227-byte hand-authored two-table dictionary round-tripped exactly: 2 tables / 6 fields / 3 keys / 1 relation / 1 PrimaryMapping / 1 ForeignMapping.

## Older dictionaries prompt to upgrade

Two of three dictionaries sampled from the shipped Examples tree **hung on an invisible modal** under a plain `/dx`. C6/C7-era dictionaries ask to upgrade, and that prompt is invisible to a headless agent.

**Pass `/au` on every `/dx` and `/di`.** It clears the prompt (exit 0, full export) and emits only a harmless `warning CLCE004: ... ForceUpgrade ...`.

## Verifying an import

Never infer success from the exit code. `/dx` the result back to `.dctx` and compare counts: tables, fields, keys, relations, PrimaryMapping, ForeignMapping. A resulting `.dct` of **26,368 bytes is the empty husk** — that size means nothing was imported, whatever the exit code said.
