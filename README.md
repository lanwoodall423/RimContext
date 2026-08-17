# RimContext

RimContext is a local, deterministic RimWorld mod source/Defs/Harmony/dependency index for AI-agent
navigation. It does not launch RimWorld or replace DevBridge2/RimBridgeServer runtime responsibilities.

## Build and run

Install the .NET 8 SDK pinned by `global.json` (`8.0.424`), then run:

```text
dotnet build RimContext.sln --configuration Release
rimctx.cmd index --json
```

The default index is `.rimctx/index.sqlite` under the selected workspace. Use `--root PATH` for a
different workspace. The wrapper requires a prior build; the executable is at
`src/RimContext.Cli/bin/Release/net8.0/rimctx.exe` after a Release build.

## Recommended AI workflow

Use RimContext to identify WHAT to inspect; only then open source files.

```text
rimctx index
rimctx affected <changed-files> --json
rimctx definition <entity> --json
rimctx refs <entity> --json
```

Ask exact queries before fuzzy queries, use `affected` before deciding test scope, and inspect a
source/XML file only after RimContext has narrowed the location. Add `--limit N` and `--max-bytes N`
when a stricter response budget is useful; use `--human` for a person-facing indented response.

See [docs/agent-usage.md](docs/agent-usage.md) for compact-output and error details, and
[docs/architecture.md](docs/architecture.md) for the implemented boundaries and storage contract.
