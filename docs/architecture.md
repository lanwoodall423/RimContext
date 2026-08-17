# RimContext architecture

Status: first-release architecture for a new repository. The current repository contains only
`.gitattributes`; it has no project file, source, build script, test suite, or existing CLI to
preserve. The implementation described here is therefore a proposed baseline, not a description of
already-existing code.

## Purpose and boundary

RimContext is a read-only, local index over RimWorld mod-development files. It precomputes stable
facts that an agent would otherwise rediscover: source declarations, XML definitions and patches,
assembly metadata, mod/project structure, dependency edges, and statically recognizable Harmony
targets. It answers bounded queries from a persisted local index without launching RimWorld.

### Responsibilities

Its responsibilities are:

- discover and fingerprint files under an explicit workspace root and any explicitly supplied
  assembly roots;
- parse C# syntax, XML, managed PE metadata, project files, and RimWorld `About/About.xml` metadata;
- resolve deterministic local names and relationships where the input contains enough evidence;
- persist the resulting index and compact reverse/search indexes;
- return small, stable JSON projections for the seven first-release queries;
- report unresolved or partial analysis explicitly rather than inventing semantic facts.

### Explicit non-responsibilities

RimContext explicitly does not own:

- RimWorld startup, shutdown, readiness, process identity, leases, save/screenshot/debug actions, or
  any other runtime game control;
- test execution, build orchestration, assembly deployment, mod enable/order changes, or
  `ModsConfig.xml` mutation;
- live-game observation or control, RimBridgeServer IPC, GABP, RimBridgeServer credentials, or a
  RimBridgeServer SDK dependency;
- compilation, decompilation, package restore, arbitrary MSBuild execution, or network access;
- complete source files, complete XML nodes, large source snippets, raw assembly IL, or unbounded
  dependency graphs in default query output.

DevBridge2 remains the lifecycle, test-lease, readiness, profile, and process authority. RimBridgeServer
remains the live-game observation/control authority. If another tool invokes RimContext, the boundary
is a normal local CLI process and files passed by path; RimContext has no shared runtime state, IPC
protocol, or ownership relationship with either tool.

This boundary was checked against the local DevBridge2 source and a Git checkout of RimBridgeServer
v2.1.0. DevBridge2's coordinator and Mod own lifecycle/readiness/profile behavior; RimBridgeServer's
`net472` Mod hosts the in-game GABP server and its SDK is for live companion tools. Those projects are
reference evidence only, not RimContext dependencies.

## Runtime, build, and entrypoint

The first implementation should target `net8.0` with the .NET SDK pinned in a new `global.json`.
This follows the available sibling DevBridge2 convention (`8.0.424` and deterministic `net8.0`
builds), but no such file currently exists in RimContext. RimContext must not reference RimWorld or
DevBridge2 assemblies. It should be usable on a machine with only the .NET runtime and the indexed
files.

The command is `rimctx`, built from an executable project. A Windows `rimctx.cmd` wrapper may provide
the same source-tree fallback pattern as DevBridge2, but the executable is the actual entrypoint and
must produce the same result when called directly. All machine-facing output is JSON by default;
`--json` is accepted explicitly for agent command lines.

The parser/storage dependencies should be pinned, never floating: Roslyn C# syntax APIs for robust
source spans and declarations, `System.Reflection.Metadata`/`PEReader` for non-executing assembly
inspection, and `Microsoft.Data.Sqlite` for the local store. The exact package versions belong in the
new lock file and must be restorable by the repository's offline validation path. No dependency may
load an indexed assembly into the RimContext process.

Common command options are:

```text
--root PATH       workspace root; default is the current directory
--store PATH      SQLite index path; default is <root>/.rimctx/index.sqlite
--limit N         result cap; default 20, maximum 100
--json            explicit machine-readable output request
```

Index-only options are repeatable `--assembly-root PATH` and `--force`. The root and store are
canonicalized before use. The `.rimctx` directory is always excluded from discovery.

The first-release command surface is:

```text
rimctx index [--root PATH] [--store PATH] [--assembly-root PATH]... [--force] --json
rimctx find QUERY [--root PATH] [--store PATH] [--kind KIND] [--limit N] --json
rimctx refs ENTITY [--root PATH] [--store PATH] [--direction in|out|both] [--limit N] --json
rimctx definition ENTITY_OR_NAME [--root PATH] [--store PATH] --json
rimctx affected ENTITY_OR_PATH [--root PATH] [--store PATH] [--depth N] [--limit N] --json
rimctx harmony [TARGET] [--file PATH] [--root PATH] [--store PATH] [--limit N] --json
rimctx file PATH_OR_ID [--root PATH] [--store PATH] [--limit N] --json
rimctx summary [--root PATH] [--store PATH] --json
```

`find` treats whitespace-separated terms as an AND query over normalized names, type names,
qualified names, member signatures, paths, package IDs, and assembly names. The example
`find "ThingDef MyWeapon"` therefore matches a `def` whose type term is `ThingDef` and name term is
`MyWeapon`. Matching is exact on indexed terms in the first release; callers needing a broader
search should issue separate small queries. Results are ranked by exact name, qualified name, and
path, then sorted by kind, normalized display name, path, line, and ID.

Query defaults are deliberately bounded:

| Query | Default projection |
| --- | --- |
| `find` | `kind`, `id`, name/type or path, and `file`/`line` where applicable |
| `refs` | direct inbound/outbound relation records with endpoint IDs, relation kind, path, and line |
| `definition` | one resolved `def` with identity, type/name, file/line, parent, and bounded scalar field summaries |
| `affected` | reverse dependent IDs and relation kinds; direct depth 1 unless a bounded depth is supplied |
| `harmony` | target, patch kind, owner type/member, source file, and line |
| `file` | file metadata and counts/IDs of contained entities; never file contents by default |
| `summary` | schema/version, root-relative configuration, counts by kind, index health, and unresolved counts |

An entity argument is first resolved as an exact ID, then as an exact unique name/path. Ambiguous
names return `AMBIGUOUS_ENTITY` with a bounded candidate list instead of selecting arbitrarily.

## Indexing pipeline

```mermaid
flowchart LR
    A[Root and options] --> B[Deterministic discovery]
    B --> C[Input fingerprints and cache reuse]
    C --> D[File-local parsers]
    D --> E[Cross-file resolution]
    E --> F[Search and reverse-edge indexes]
    F --> G[Atomic SQLite replacement]
    G --> H[Bounded read-only query]
    H --> I[Deterministic JSON]
```

The index command performs these phases in order:

1. Canonicalize the root, store path, and explicit assembly roots. Reject a store inside an indexed
   input unless it is the excluded `.rimctx` location. Load the indexer/schema version and fixed
   ignore rules.
2. Discover paths in ordinal, slash-normalized order. By default scan `.cs`, `.xml`, `.csproj`,
   `.sln`, `Directory.Build.*`, `packages.lock.json`, and managed `.dll`/`.exe` files. Skip `.git`,
   `.vs`, `bin`, `obj`, `artifacts`, `.rimctx`, and other generated output directories. A managed
   assembly outside the workspace is scanned only when its root was explicitly supplied.
3. Identify mods from `About/About.xml`, projects from `.csproj`/`.sln`, and source/XML ownership from
   explicit project items followed by deterministic nearest-project/path rules. Parse only literal
   project properties and item references; do not execute imports, targets, glob expressions, or
   arbitrary build code. Record an explicit partial-project diagnostic when ownership cannot be
   evaluated exactly.
4. Fingerprint each discovered input. A fast `(size, last-write time)` check may avoid hashing, but
   a changed candidate is confirmed with SHA-256. Parse only changed or cache-incompatible files.
5. Parse file-local facts:
   - C# uses Roslyn syntax trees and line maps. It records namespaces, nested/partial types,
     methods, constructors, properties, fields, events, indexers, attributes, signatures, and
     recognized static string/`typeof` arguments. It does not require a compilable project.
   - XML uses a non-validating reader with line information. It records definitions under `Defs`,
     patch-operation nodes, scalar paths, `ParentName`, and candidate def-name references without
     retaining complete XML nodes.
   - Managed PE files use metadata readers only. Record assembly identity, MVID/version, assembly
     references, type definitions, member signatures, and framework/reference names. Unmanaged or
     malformed binaries produce a bounded diagnostic and no executable load attempt.
   - `About.xml` records package ID, name, supported version metadata, load ordering, and declared
     mod dependencies.
6. Resolve cross-file facts from the complete set of file-local IR. Build def-name maps, source and
   metadata symbol maps, project/mod/assembly dependencies, reverse relations, and search terms.
   Resolution is rerun after any changed/deleted file so cached local facts cannot leave stale edges.
7. Write a new database in a temporary sibling path. Insert entities and relations in canonical
   order, run integrity checks, close it, and atomically replace the previous index. A failed index
   never replaces the last complete index. Per-file parse failures publish a partial index with the
   failed file represented and old entities removed; fatal discovery/store failures leave the old
   index untouched.

### Query pipeline

Queries never trigger a scan, build, game launch, or automatic repair. They:

1. Parse and validate the command, root/store selection, selector, direction, depth, and result cap.
2. Open the selected SQLite store read-only and verify schema, root/config fingerprint, and health.
3. Resolve an entity selector by exact ID, then exact unique path/name; return a bounded ambiguity
   error when more than one entity matches.
4. Execute a prepared, indexed lookup against `entities`, `relations`, `search_terms`, and
   `def_fields`. `affected` follows reverse relations only to the requested bounded depth.
5. Project only the command's compact result fields, apply the fixed stable ordering and cap, and
   attach `truncated` or stored warning information when applicable.
6. Serialize one compact JSON envelope and return the command's stable exit code. The query observes
   the last complete immutable store even if another process is indexing a replacement.

### File-specific extraction limits

The first release intentionally supports a declared subset rather than pretending to understand all
RimWorld or C# semantics:

- A `def` is every direct child of `<Defs>` other than the container. Its type is the child local
  name, and its name is the first `<defName>` scalar, or null when absent. A missing name is still
  indexed with a location-based identity and an unresolved-name flag.
- `def_reference` records `ParentName`, constant strings passed to common `DefDatabase<T>.GetNamed*`
  calls, and XML scalar/attribute values that exactly match a known def name, excluding the defining
  `defName` field and patch XPath text. Exact-name matches carry `confidence: "name-match"`; unresolved
  or duplicate targets carry a null target ID and their observed name.
- A `patch_operation` is a node whose local name or `Class` identifies `PatchOperation...`. Store
  operation class, source location, containing XML path, extracted XPath, and a short scalar summary.
  Nested operations receive separate entities; arbitrary `<value>` subtrees are not emitted.
- A `harmony_patch` is emitted for recognizable `HarmonyPatch`/`HarmonyPrefix`/`HarmonyPostfix`,
  `HarmonyTranspiler`, and `HarmonyFinalizer` attributes and statically inspectable `Harmony.Patch`
  calls in source. The metadata scanner applies the same rule to compiled `HarmonyLib` custom
  attributes and patch-marker attributes, so a built assembly remains queryable without loading it.
  `typeof`, `nameof`, and constant string targets are recorded; dynamic expressions are kept as
  unresolved target text with a confidence flag.
- Project parsing is static and intentionally does not claim the result of running MSBuild. Explicit
  `ProjectReference`, `Reference`, `PackageReference`, target framework, and literal compile items
  are reliable; imported/globbed/generated inputs are marked as incomplete when they cannot be
  determined from the project file.

## Entity model

The first-release entity kinds are exactly the following. Kind-specific fields are stored in a
canonical payload, while common searchable fields are columns for fast lookup.

| Kind | Identity and compact fields |
| --- | --- |
| `source_file` | workspace/external path, SHA-256, line count, project/mod IDs, parse status |
| `csharp_type` | source or assembly origin, namespace, name/arity, containing type, base type text, file/line or assembly ID |
| `csharp_member` | source or assembly origin, containing type, member kind/name/signature, file/line or assembly ID |
| `xml_file` | workspace/external path, SHA-256, line count, project/mod IDs, parse status |
| `def` | mod scope, def type/name, parent name, XML file/path/line, bounded field summary, duplicate/unresolved flags |
| `def_reference` | source endpoint, observed target name/type, resolved target ID when unique, XML/C# path, file/line, confidence |
| `patch_operation` | XML file/path/line, operation class, XPath/target summary, parent operation ID |
| `harmony_patch` | owner type/member, patch kind, target type/member text, assembly/source file, line, confidence |
| `assembly` | path, simple name, version, MVID, SHA-256, managed status, reference names |
| `mod` | package ID, display name, root path, About file, load order, declared dependency IDs |
| `project` | project/solution path, name, target frameworks, root namespace, source roots, static-evaluation status |
| `dependency` | directed edge `from`/`to` or unresolved name, kind, evidence file/line, confidence |

`dependency` is a first-class edge entity so project, mod, and assembly relationships have the same
bounded `refs`/`affected` behavior. Containment and semantic references are stored as relations as
well, but are not counted as external dependencies unless their relation kind says so.

## Stable IDs and paths

IDs are deterministic and must not depend on process time, database row IDs, line numbers, or random
GUIDs. Every entity has a canonical identity string:

```text
<kind>\0<scope>\0<normalized semantic identity>
```

The ID is `<kind>:<lowercase base32url of the first 16 SHA-256 bytes>`, without padding. Normalized
paths use `/`, remove `.` segments, reject traversal above the root, and use invariant lowercase for
identity. A case-only path collision is an indexing error. Display paths preserve useful case but are
always root-relative and slash-normalized. External paths use an opaque deterministic
`external/<root-key>/...` prefix; absolute machine paths are not in normal JSON.

Identity rules are:

- files: scope plus normalized path;
- projects/mods/assemblies: scope plus normalized path and intrinsic name/package ID;
- source and metadata types: origin ID plus fully qualified type name and arity;
- members: containing type ID plus member kind, name, and normalized signature;
- defs: mod ID plus def type and def name; unnamed or duplicate defs add a deterministic XML path and
  occurrence suffix;
- XML operations/references and Harmony records: owning file/declaration plus canonical XML path or
  declaration locator, target text, kind, and deterministic occurrence number;
- dependencies: endpoint IDs (or normalized unresolved target), dependency kind, and evidence locator.

Line numbers and byte offsets are attributes, never identity components. Moving a declaration within a
file therefore preserves its ID; renaming or changing its semantic signature intentionally changes it.

## Persistence and cache invalidation

The default store is `.rimctx/index.sqlite`, a single SQLite database opened read-only by query
commands. It contains:

- `meta`: schema/indexer versions, canonical root/config fingerprints, and health status;
- `files`: file IDs, path/display metadata, hashes, size/mtime, parser status, and bounded diagnostics;
- `entities`: explicit ID, kind, searchable columns, origin/file/line, and canonical compact payload;
- `relations`: explicit relation ID, endpoint IDs, kind, evidence file/line/path, and unresolved target;
- `search_terms`: normalized term-to-entity rows and deterministic rank weight;
- `def_fields`: bounded scalar field summaries used by `definition` without storing XML trees.

Primary keys and all exposed ordering are explicit; SQLite row order is never observable. No raw source,
full XML, IL, credentials, or volatile timestamps are persisted. The write path uses a lock, a temporary
database, one transaction, integrity checks, and atomic replacement. Readers open the previous immutable
database while a new one is being built.

Cache reuse is valid only when all of these match: normalized path, content SHA-256, parser/indexer
version, schema version, root/assembly-root configuration fingerprint, and relevant ownership/config
inputs. Deletions and renames are handled by discovery absence/path identity, not by trusting a stale
cache. A project, mod, assembly, or definition change invalidates global resolution; cached file-local
IR may still be reused, but symbol maps, def maps, search terms, and relations are rebuilt. `--force`
skips reuse and creates the same canonical result as a clean index.

## Query and JSON contract

Every invocation writes exactly one UTF-8 JSON object followed by a newline to stdout and no human text
to stdout. Property names, arrays, and result fields are emitted in a fixed order; arrays are sorted
by the command-specific stable sort. The default serializer is compact, invariant, and omits optional
fields when they are absent. `--json` does not change semantics.

Successful output has this envelope:

```json
{
  "schemaVersion": "rimctx/v1",
  "status": "ok",
  "command": "find",
  "results": []
}
```

`status` is `ok`, `partial`, or `error`. `partial` is used when the stored index contains bounded
per-file diagnostics; it is still queryable. `warnings` appears only for partial output. Query result
objects always include `kind` and `id`, followed only by the fields needed for that query. The `find`
projection for a definition is:

```json
{
  "kind": "def",
  "id": "def:...",
  "name": "MyWeapon",
  "type": "ThingDef",
  "file": "Defs/Weapons.xml",
  "line": 42
}
```

`definition` scalar summaries are capped at 32 fields and 160 Unicode characters per value; the
response includes `truncated: true` when a cap is reached. `refs`, `affected`, and `harmony` apply the
same result limit and report `truncated: true` rather than expanding the cap. `file` returns metadata
and bounded counts/IDs, never contents. No query has a flag in the first release that returns an
unbounded file, XML node, source snippet, or graph.

Error output has the same envelope and this shape:

```json
{
  "schemaVersion": "rimctx/v1",
  "status": "error",
  "command": "definition",
  "error": {
    "code": "INDEX_NOT_FOUND",
    "message": "No RimContext index exists for the selected root."
  }
}
```

Error codes and native exit codes are stable:

| Exit | Codes | Meaning |
| ---: | --- | --- |
| 0 | none | successful or partial result, including no matches |
| 2 | `INVALID_ARGUMENT`, `LIMIT_EXCEEDED`, `AMBIGUOUS_ENTITY` | caller input is invalid or underspecified |
| 3 | `INDEX_NOT_FOUND`, `INDEX_INCOMPATIBLE`, `ROOT_MISMATCH` | query cannot use the selected store |
| 4 | `PATH_NOT_FOUND`, `INPUT_READ_FAILED`, `STORE_LOCKED`, `STORE_FAILED` | filesystem/store failure |
| 5 | `INDEX_FAILED` | index could not be safely published |
| 6 | `not_implemented` | recognized command is reserved for a later analyzer stage |
| 10 | `INTERNAL` | unexpected bug; message is bounded and contains no stack trace |

Messages are concise and deterministic. `path`, `line`, and a bounded `details` object may be added
when useful; stack traces, absolute paths, environment variables, raw command lines, and secrets are
never emitted in normal output. Diagnostics belong on stderr only when a future explicit debug mode is
added, not in the default agent contract.

## Performance goals

These are acceptance targets for a typical development workspace, not promises about pathological
inputs:

- a warm query, including process startup, completes at p95 under 250 ms for an index with 100,000
  entities and the default 20-result cap;
- a read-only query performs bounded indexed reads and emits no more than 100 result records;
- an incremental index hashes only changed candidates and parses only changed/cache-incompatible
  inputs; an unchanged 10,000-file workspace should complete under 2 seconds on a normal developer PC;
- a cold index should scale approximately with bytes read and changed syntax/XML/metadata, not with
  the number of possible query terms;
- default JSON should remain compact, normally below 16 KiB for a 20-result query and never grow with
  complete file or graph size.

The implementation should measure these targets with checked-in fixtures and report counts/durations
only in test output, not in normal query JSON.

## Test strategy

The repository should follow DevBridge2's existing offline-test convention: a `RimContext.Tests`
`net8.0` executable with deterministic assertions, invoked by a validation script, rather than
requiring a running RimWorld or a live RimBridgeServer. Tests use temporary roots and checked-in
fixtures and never mutate a user's real mod tree.

Required test layers are:

- parser fixtures for C# nesting/partial types/member signatures, XML defs/patch operations/line
  mapping, About metadata, static def references, Harmony forms, malformed input, and managed PE
  metadata with missing references;
- identity/golden tests proving repeated clean indexes produce equivalent IDs, sorted rows, hashes,
  and JSON bytes; path moves, duplicates, deletions, and case collisions follow the stated rules;
- cache tests for unchanged reuse, content changes, parser/schema changes, project ownership changes,
  assembly-root changes, deletion, and `--force` equivalence;
- SQLite tests for atomic replacement, interrupted writes, read-only snapshots, lock behavior, schema
  mismatch, bounded field summaries, and no raw-file persistence;
- query contract tests for every command, empty results, ambiguity, limits, direct/transitive affected
  depth, partial indexes, error codes, exit codes, and exact compact JSON projections;
- process-level CLI tests that invoke the published executable against temporary fixtures. They must
  verify that no RimWorld process, DevBridge2 command, RimBridgeServer connection, build, or network
  activity is required.

## Proposed directory structure

```text
RimContext/
  global.json
  RimContext.sln
  rimctx.cmd
  Directory.Build.props
  Directory.Packages.props
  src/
    RimContext.Core/
      RimContext.Core.csproj
      Discovery/
      Parsing/
      Model/
      Resolution/
      Storage/
      Queries/
      Output/
    RimContext.Cli/
      RimContext.Cli.csproj
      Program.cs
  tests/
    RimContext.Tests/
      RimContext.Tests.csproj
      Fixtures/
      OfflineTests.cs
  scripts/
    validate.ps1
  docs/
    architecture.md
  .rimctx/                         # generated local store; ignored by git
```

`Core` owns no process or UI code. `Cli` owns argument parsing, exit-code mapping, and stdout/stderr
policy. Tests own fixture generation and assertions. The first implementation should add the proposed
files only as needed; optional web APIs, background watchers, decompilers, semantic compilation,
remote stores, and UI are out of scope until a concrete first-release requirement exists.

## Implementation risks to preserve explicitly

- Roslyn and SQLite package availability must be locked so an offline developer build is reproducible.
- Static project parsing cannot know arbitrary imported/generated MSBuild inputs; the index must expose
  partial ownership instead of claiming completeness.
- Def-name matching and Harmony target extraction are necessarily conservative without a compiled game
  reference graph; unresolved/confidence fields are part of the contract.
- Multiple mods can declare the same package ID or def key. The index must retain deterministic
  duplicate records and return ambiguity rather than silently choosing load order.
- RimWorld assemblies can be large and may have unavailable references. Metadata-only scanning and
  bounded type/member projections are required to keep memory and query output predictable.
- SQLite atomic replacement and Windows path/case behavior need process-level tests before the tool is
  used by multiple agents concurrently.
