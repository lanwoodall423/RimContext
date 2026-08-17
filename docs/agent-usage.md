# RimContext agent usage

RimContext is intended to narrow a coding task before an agent opens source or XML. Keep the normal loop small and deterministic:

1. Query RimContext before opening files.
2. Ask an exact query before using prefix or substring matching.
3. Inspect source only after RimContext has identified the relevant file and line.
4. Use `affected` before deciding which tests or projects need attention.

## Start with an index

```text
rimctx index --json
```

Indexing is incremental. Re-running it after no changes should do no semantic parsing work; changing one file invalidates that file's derived records.

## Exact-first queries

Use a qualified selector when it is known:

```text
rimctx definition ThingDef/MyWeapon --json
rimctx definition MyNamespace.CompWidget --json
rimctx refs ThingDef/MyWeapon --json
rimctx harmony Verse.Pawn.Tick --json
rimctx file Source/CompWidget.cs --json
```

Use `find` for discovery, preferably from the most specific selector to the least specific:

```text
rimctx find MyWeapon --json
rimctx find CompWidget --limit 10 --json
```

Use impact analysis before selecting a validation scope:

```text
rimctx affected Source/CompWidget.cs Defs/Weapons.xml --depth 2 --json
```

The `direct`, `dependent`, and `runtime_risk` tiers are bounded and deduplicated. Relationship entries include a reason or confidence when the relationship is heuristic. A `truncated: true` value means more candidates were omitted.

## Compact output

JSON is the primary agent interface. It omits null values, empty arrays, repeated identical qualified names, absolute paths, file bodies, XML nodes, and source bodies. Query responses include minimal metadata:

```json
{
  "status": "ok",
  "command": "definition",
  "meta": { "count": 1, "truncated": false },
  "results": [{ "kind": "def", "id": "...", "defType": "ThingDef", "defName": "MyWeapon", "file": "Defs/Weapons.xml", "line": 42 }]
}
```

`--limit N` caps query candidates. `--max-bytes N` applies a deterministic output budget and removes lower-priority result entries first; it always marks the response as truncated when content is omitted. `--compact` is available explicitly and is the default agent policy.

For a readable inspection of the same envelope, use `--human`. Keep `--json` for automation; `--json` and `--human` cannot be combined.

Errors are concise and machine-readable:

```json
{
  "status": "error",
  "code": "NOT_FOUND",
  "message": "ThingDef/MyWeapon not found"
}
```

Diagnostics and logs go to stderr. Stdout is JSON only for JSON-mode commands.

## Suggested agent sequence

For a change, run `index`, query the exact definition/type/member, inspect its `refs`, then run `affected` on changed files. Open only the returned files and nearby lines. If the result is truncated, narrow the selector, lower the traversal depth, or raise `--limit`/`--max-bytes` deliberately rather than requesting complete files.
